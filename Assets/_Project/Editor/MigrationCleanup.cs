#if UNITY_EDITOR
using Game.Environment;
using Game.Items;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 迁移收尾工具。
    ///
    /// 处理第一次迁移因"路径常量与文件名不匹配"而漏掉的三件事：
    ///   1. 旧天空材质 M_NightSky.mat 还在 Assets/Game/Environment/ → 搬到 _Project 并改名
    ///   2. 物品定义目录还是空的 → 重新生成木材（和石材）物品资产
    ///   3. 顺带把天空材质切到自定义天体天空盒（Game/ProceduralSkyCelestial）并注册进 Always Included Shaders
    ///
    /// 完成后可以连同 ProjectLayoutMigration.cs 一起删掉。
    /// </summary>
    public static class MigrationCleanup
    {
        private const string LegacySkyMaterialPath = "Assets/Game/Environment/M_NightSky.mat";
        private const string SkyMaterialFolder = "Assets/_Project/Rendering/Sky";
        private const string SkyMaterialName = "M_Sky_Celestial";
        private const string SkyMaterialPath = SkyMaterialFolder + "/" + SkyMaterialName + ".mat";

        [MenuItem("游戏/工程/迁移收尾（天空材质 + 物品资产）", priority = 102)]
        public static void Cleanup()
        {
            MoveLeftoverScene();
            FixSkyMaterial();
            FixItemAssets();
            RemoveStrayLeftoverSources();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[收尾] 完成。请检查 Project 窗口结构，然后 Play 验证。");
        }

        // ------------------------------------------------------------------ 遗留场景

        /// <summary>
        /// 把旧场景从 Assets/Scenes 搬到 _Project/Scenes/Prototype。
        ///
        /// 说明：AssetDatabase.MoveAsset 对这个场景反复失败
        ///（"Trying to move asset as a sub directory of a directory that does not exist"，
        ///  即使目标目录确实存在、也 Refresh 过）。
        /// 所以这里做成"先试 MoveAsset，失败就明确告诉用户手动拖"，
        /// 不再让用户反复跑同一个会失败的菜单。
        /// </summary>
        private static void MoveLeftoverScene()
        {
            const string legacyScene = "Assets/Scenes/Prototype_Greybox.unity";
            const string targetFolder = "Assets/_Project/Scenes/Prototype";
            const string targetScene = targetFolder + "/SC_Prototype_Greybox.unity";

            if (AssetDatabase.LoadAssetAtPath<Object>(targetScene) != null)
            {
                // 已经在位，无需处理（可能是复制的，也可能是新的）
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<Object>(legacyScene) == null)
            {
                return;
            }

            EnsureDirectory(targetFolder);
            AssetDatabase.Refresh();

            string error = AssetDatabase.MoveAsset(legacyScene, targetScene);
            if (string.IsNullOrEmpty(error))
            {
                Debug.Log($"[收尾] 场景已迁移：{legacyScene} → {targetScene}");
                return;
            }

            // 自动移动失败：给出确定可用的手动方案，不再反复尝试。
            Debug.LogWarning(
                $"[收尾] 场景自动移动失败（{error}）。\n" +
                "请手动完成这一步（10 秒）：\n" +
                $"  1. 在 Project 窗口选中 {legacyScene}\n" +
                $"  2. 拖到 {targetFolder} 目录里\n" +
                "  3. 按 F2 重命名为 SC_Prototype_Greybox\n" +
                "场景内部引用靠 GUID，改名/移动都不会断。" +
                "（如果场景当前是打开状态，先切到别的场景再操作。）");
        }

        // ------------------------------------------------------------------ 天空材质

        private static void FixSkyMaterial()
        {
            Material sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);

            if (sky == null)
            {
                // 还没搬：先搬到 _Project 下（保持原名），再改名。
                Material legacy = AssetDatabase.LoadAssetAtPath<Material>(LegacySkyMaterialPath);
                if (legacy != null)
                {
                    EnsureDirectory(SkyMaterialFolder);

                    string error = AssetDatabase.MoveAsset(LegacySkyMaterialPath, SkyMaterialFolder + "/M_NightSky.mat");
                    if (string.IsNullOrEmpty(error))
                    {
                        error = AssetDatabase.RenameAsset(SkyMaterialFolder + "/M_NightSky.mat", SkyMaterialName);
                    }

                    if (!string.IsNullOrEmpty(error))
                    {
                        Debug.LogError($"[收尾] 天空材质移动/改名失败：{error}");
                        return;
                    }

                    sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);
                    Debug.Log($"[收尾] 天空材质已迁移并改名为 {SkyMaterialPath}");
                }
            }

            if (sky == null)
            {
                // 没有旧材质：直接生成一份新的（DayNightSceneTools 会复用同路径）。
                sky = DayNightSceneTools.GetOrCreateSkyMaterial(true);
            }

            if (sky == null)
            {
                Debug.LogWarning("[收尾] 无法取得天空材质，跳过。");
                return;
            }

            // 切到自定义天体天空盒（太阳/月亮圆盘 + 程序化星空）。
            Shader celestial = Shader.Find(DayNightSceneTools.SkyShaderName);
            if (celestial != null && sky.shader != celestial)
            {
                sky.shader = celestial;
                EditorUtility.SetDirty(sky);
                Debug.Log($"[收尾] 天空材质已切换到「{DayNightSceneTools.SkyShaderName}」（现在能看到太阳/月亮/星星了）。");
            }

            // 打包后 Shader.Find 才不会失败。
            DayNightSceneTools.RegisterAlwaysIncludedShader();

            // 把场景里 DayNightLightingController 的引用指向这份材质（靠 GUID，通常已经是它）。
            DayNightLightingController[] controllers = Object.FindObjectsByType<DayNightLightingController>(FindObjectsSortMode.None);
            foreach (DayNightLightingController controller in controllers)
            {
                SerializedObject serialized = new SerializedObject(controller);
                SerializedProperty property = serialized.FindProperty("_skyboxMaterial");
                if (property == null) continue;

                property.objectReferenceValue = sky;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(controller);
            }
        }

        // ------------------------------------------------------------------ 物品资产

        private static void FixItemAssets()
        {
            ItemDefinition wood = GameItemTools.GetOrCreateWoodItem();
            ItemDefinition stone = GameItemTools.GetOrCreateItem(
                "Assets/_Project/Data/Items/DA_Item_Stone.asset",
                "stone", "石头", "基础建材。用于砌墙与工具，以后由采石获得。", 99);

            Debug.Log($"[收尾] 物品资产已就绪：{wood.DisplayName}（{AssetDatabase.GetAssetPath(wood)}）、{stone.DisplayName}（{AssetDatabase.GetAssetPath(stone)}）");
        }

        // ------------------------------------------------------------------ 清理残留源码

        /// <summary>
        /// 迁移后 Assets/Game 下会留下三个"只有注释"的空壳 .cs（内容早已搬到 _Project）。
        /// 它们无害，但会让 Project 窗口看起来不干净。这里删掉它们（含 .meta）。
        /// </summary>
        private static void RemoveStrayLeftoverSources()
        {
            string[] strays =
            {
                "Assets/Game/GreyboxLevelBuilder.cs",
                "Assets/Game/Environment/DayNightSceneTools.cs",
                "Assets/Game/Items/GameItemTools.cs"
            };

            foreach (string path in strays)
            {
                if (!AssetDatabase.LoadAssetAtPath<Object>(path)) continue;

                if (AssetDatabase.DeleteAsset(path))
                {
                    Debug.Log($"[收尾] 删除残留空壳：{path}");
                }
            }
        }

        private static void EnsureDirectory(string directory)
        {
            EditorFileUtility.EnsureAssetFolder(directory);
        }
    }
}
#endif
