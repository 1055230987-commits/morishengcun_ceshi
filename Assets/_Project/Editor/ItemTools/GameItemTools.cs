#if UNITY_EDITOR
using Game.Items;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 物品资产的编辑器工具。
    ///
    /// 目前只有"创建木材"。以后每加一种物品，都在这里加一个菜单项 ——
    /// 而不是在代码里写死物品数据（那正是我们要避免的）。
    ///
    /// 本文件位于 Editor/ 文件夹，属于 Game.Editor 程序集（见同目录 Game.Editor.asmdef）。
    /// </summary>
    public static class GameItemTools
    {
        public const string WoodItemPath = "Assets/_Project/Data/Items/DA_Item_Wood.asset";
        public const string StoneItemPath = "Assets/_Project/Data/Items/DA_Item_Stone.asset";

        [MenuItem("游戏/物品/创建木材物品", priority = 40)]
        public static void CreateWoodItemMenu()
        {
            ItemDefinition wood = GetOrCreateWoodItem();
            Selection.activeObject = wood;
            EditorGUIUtility.PingObject(wood);
            Debug.Log($"[物品] 木材物品已就绪：{WoodItemPath}");
        }

        [MenuItem("游戏/物品/创建全部原型物品", priority = 41)]
        public static void CreateAllPrototypeItems()
        {
            ItemDefinition wood = GetOrCreateWoodItem();
            ItemDefinition stone = GetOrCreateStoneItem();

            Selection.activeObject = wood;
            EditorGUIUtility.PingObject(wood);
            Debug.Log($"[物品] 原型物品已就绪：{wood.DisplayName}、{stone.DisplayName}");
        }

        /// <summary>获取或创建木材物品资产。场景生成器也调用它，保证树和箱子的产出有引可用。</summary>
        public static ItemDefinition GetOrCreateWoodItem()
        {
            return GetOrCreateItem(
                WoodItemPath,
                "wood", "木材", "最基础的建材。砍树获得，用于搭建与生火。", 99);
        }

        /// <summary>
        /// 获取或创建石头物品资产。
        ///
        /// 【为什么建造系统需要它】手感上"地板用木材、墙用石头"比"什么都用木材"
        /// 更像生存游戏，而石材在物品定义里本来就有（描述里写着"用于砌墙"）——
        /// 这里只是把它真正接进建造配方，不是新加一种资源。
        /// </summary>
        public static ItemDefinition GetOrCreateStoneItem()
        {
            return GetOrCreateItem(
                StoneItemPath,
                "stone", "石头", "基础建材。用于砌墙与工具，以后由采石获得。", 99);
        }

        public static ItemDefinition GetOrCreateItem(string assetPath, string itemId, string displayName, string description, int maxStack)
        {
            ItemDefinition existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(assetPath);
            if (existing != null) return existing;

            // 目录必须"已存在且已登记"，否则 CreateAsset 会报 "Creating asset at path ... failed"。
            if (!EditorFileUtility.EnsureAssetFolderForFile(assetPath))
            {
                Debug.LogError($"[物品] 无法创建目录：{assetPath}，放弃创建物品。");
                return null;
            }

            ItemDefinition item = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(item, assetPath);

            // 用 SerializedObject 写私有字段，保持字段封装。
            SerializedObject serialized = new SerializedObject(item);
            SetString(serialized, "_itemId", itemId);
            SetString(serialized, "_displayName", displayName);
            SetString(serialized, "_description", description);
            SetInt(serialized, "_maxStackSize", maxStack);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(item);
            AssetDatabase.SaveAssets();

            return item;
        }

        private static void SetString(SerializedObject serialized, string field, string value)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property != null) property.stringValue = value;
        }

        private static void SetInt(SerializedObject serialized, string field, int value)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property != null) property.intValue = value;
        }

        // ------------------------------------------------------------------ 物品注册表

        /// <summary>注册表资产的固定位置。必须在 Resources 下，打包后才加载得到。</summary>
        public const string RegistryPath = "Assets/_Project/Resources/ItemRegistry.asset";

        /// <summary>
        /// 扫描所有 ItemDefinition 资产，重建物品注册表。
        ///
        /// 【为什么需要手动跑】本工程没有引入 Addressables，打包后只能靠 Resources 加载。
        /// 所以"有哪些物品"必须【提前写进一个资产】。
        ///
        /// 【什么时候要重跑】
        ///   - 新建了物品资产
        ///   - 改了某个物品的 itemId
        ///   - 删除了物品资产
        ///
        /// 【忘了重跑会怎样】编辑器里【没有影响】（那边直接查 AssetDatabase）；
        /// 但打包后读档时那个物品会"找不到定义"，被预检拦下。
        /// 所以这个菜单在打包前必须跑一次。
        /// </summary>
        [MenuItem("游戏/物品/重建物品注册表", priority = 42)]
        public static void RebuildRegistry()
        {
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition");

            var found = new System.Collections.Generic.List<ItemDefinition>();
            var seenIds = new System.Collections.Generic.Dictionary<string, string>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                ItemDefinition item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);

                if (item == null) continue;

                // 跳过 OptimizationReview 里的备份副本 —— 它们不在 Assets/_Project 下，
                // 但为了稳妥还是显式排除，避免把备份里的物品也注册进来。
                if (!path.StartsWith("Assets/_Project/")) continue;

                string id = item.ItemId;

                if (string.IsNullOrEmpty(id))
                {
                    Debug.LogWarning($"[物品注册表] 「{path}」没有 itemId，已跳过。");
                    continue;
                }

                if (seenIds.TryGetValue(id, out string otherPath))
                {
                    Debug.LogError(
                        $"[物品注册表] 物品 ID「{id}」被两个资产使用：\n  {otherPath}\n  {path}\n" +
                        "请改掉其中一个的 ID —— 否则存档读出来会是错的那个物品。");
                    continue;
                }

                seenIds[id] = path;
                found.Add(item);
            }

            found.Sort((a, b) => string.CompareOrdinal(a.ItemId, b.ItemId));

            ItemRegistry registry = AssetDatabase.LoadAssetAtPath<ItemRegistry>(RegistryPath);

            bool created = false;
            if (registry == null)
            {
                if (!EditorFileUtility.EnsureAssetFolderForFile(RegistryPath))
                {
                    Debug.LogError($"[物品注册表] 无法创建目录：{RegistryPath}");
                    return;
                }

                registry = ScriptableObject.CreateInstance<ItemRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
                created = true;
            }

            // 用 SerializedObject 写私有列表字段，保持封装（和上面的物品创建一致）
            SerializedObject serialized = new SerializedObject(registry);
            SerializedProperty list = serialized.FindProperty("_items");

            if (list == null)
            {
                Debug.LogError("[物品注册表] 找不到 _items 字段 —— 字段名改过了？请同步更新这个工具。");
                return;
            }

            list.arraySize = found.Count;
            for (int i = 0; i < found.Count; i++)
            {
                list.GetArrayElementAtIndex(i).objectReferenceValue = found[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();

            // 【必须清缓存】否则本进程里后续的查找还会用旧索引
            ItemRegistry.ClearCache();

            Selection.activeObject = registry;
            EditorGUIUtility.PingObject(registry);

            var summary = new System.Text.StringBuilder();
            foreach (ItemDefinition item in found)
            {
                if (summary.Length > 0) summary.Append("、");
                summary.Append($"{item.ItemId}({item.DisplayName})");
            }

            Debug.Log($"[物品注册表] {(created ? "已创建" : "已更新")}：{RegistryPath}\n" +
                      $"  共 {found.Count} 个物品：{(summary.Length > 0 ? summary.ToString() : "（没有物品）")}");
        }
    }

    /// <summary>
    /// 打包前自动重建物品注册表。
    ///
    /// 【为什么需要这一步】注册表是"有哪些物品"的【快照】。
    /// 忘了重建就会导致打包后某些物品读不出存档 ——
    /// 而这类问题只在打包后出现，编辑器里怎么测都正常（编辑器直接查 AssetDatabase）。
    /// 那种"测不出来"的缺陷最适合用自动化挡掉。
    ///
    /// 用 IPreprocessBuildWithReport：它在构建开始【之前】执行，
    /// 而且抛出异常会中止构建 —— 正好符合"注册表坏了就别打包"的意图。
    /// </summary>
    public class ItemRegistryBuildStep : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            Debug.Log("[物品注册表] 打包前自动重建（保证打包后能按 ID 恢复物品）");
            GameItemTools.RebuildRegistry();
        }
    }
}
#endif
