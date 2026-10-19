#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Gameplay.AI;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Editor
{
    /// <summary>复用 AI Navigation 的 Surface/Modifier；烘焙后显式保存数据资产。</summary>
    public static class EnemyNavMeshTools
    {
        private const string SurfaceObjectName = "敌人导航网格";

        [MenuItem("游戏/工程/烘焙敌人导航网格", false, 110)]
        public static void Bake()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[导航网格] 请先退出 Play。");
                return;
            }
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("[导航网格] 请先保存当前场景，再烘焙导航。");
                return;
            }

            NavMeshSurface surface = FindSurface(scene);
            if (surface == null)
            {
                var host = new GameObject(SurfaceObjectName);
                SceneManager.MoveGameObjectToScene(host, scene);
                Undo.RegisterCreatedObjectUndo(host, "创建敌人导航网格");
                surface = Undo.AddComponent<NavMeshSurface>(host);
            }
            Undo.RecordObject(surface, "配置敌人导航网格");
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~0;
            surface.agentTypeID = EnsureAgentSettings(scene);
            surface.defaultArea = 0;
            surface.ignoreNavMeshAgent = true;
            surface.ignoreNavMeshObstacle = true;
            surface.overrideVoxelSize = true;
            surface.voxelSize = 0.1f;

            var exclusions = new List<ModifierState>();
            try
            {
                // All 会收集已加载场景。排除其他场景，避免把相邻编辑场景烘进来。
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene loaded = SceneManager.GetSceneAt(i);
                    if (!loaded.isLoaded) continue;
                    foreach (GameObject root in loaded.GetRootGameObjects())
                    {
                        if (loaded != scene) Exclude(root, exclusions);
                        else ExcludeActors(root.transform, exclusions);
                    }
                }
                surface.BuildNavMesh();
                if (surface.navMeshData == null)
                    throw new InvalidOperationException("没有生成导航数据，请检查地面碰撞体。");
                SaveDataAsset(surface, scene.path);
            }
            finally
            {
                for (int i = exclusions.Count - 1; i >= 0; i--) exclusions[i].Restore();
            }
            EditorUtility.SetDirty(surface);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[导航网格] 已烘焙并保存数据资产：" +
                AssetDatabase.GetAssetPath(surface.navMeshData) +
                "\n请 Ctrl+S 保存场景，再运行「诊断导航网格（只读）」。无需重建灰盒。", surface);
        }

        internal static NavMeshSurface FindSurface(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (NavMeshSurface surface in root.GetComponentsInChildren<NavMeshSurface>(true))
                    if (surface.name == SurfaceObjectName) return surface;
            // 允许美术手工配置一个 Surface；多个时不擅自选择。
            NavMeshSurface candidate = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (NavMeshSurface surface in root.GetComponentsInChildren<NavMeshSurface>(true))
                {
                    if (candidate != null)
                        throw new InvalidOperationException("当前场景存在多个 Surface，请命名需要烘焙的一个为「" +
                            SurfaceObjectName + "」，或分别使用各 Surface 的 Bake。");
                    candidate = surface;
                }
            return candidate;
        }

        /// <summary>某个场景对应的导航数据目录（与场景同名 + _Navigation）。</summary>
        internal static string NavigationFolderFor(string scenePath) =>
            Path.ChangeExtension(scenePath, null).Replace('\\', '/') + "_Navigation";

        /// <summary>把物体名转成合法资产名（Unity 资产名不能含路径非法字符）。</summary>
        internal static string SanitizeAssetName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "NavMesh";

            foreach (char bad in Path.GetInvalidFileNameChars()) raw = raw.Replace(bad, '_');

            return raw;
        }

        /// <summary>找到或创建导航数据目录。</summary>
        internal static string EnsureNavigationFolder(string scenePath)
        {
            string folder = NavigationFolderFor(scenePath);

            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder(
                    Path.GetDirectoryName(scenePath).Replace('\\', '/'),
                    Path.GetFileName(folder));
            }

            return folder;
        }

        internal static void SaveDataAsset(NavMeshSurface surface, string scenePath)
        {
            string folder = EnsureNavigationFolder(scenePath);
            string desired = folder + "/" + SanitizeAssetName(surface.name) + ".asset";

            // 【为什么先建到临时路径，再替换】（本轮加的清理，解决资产不断累积）
            //
            // 旧实现用的是 AssetDatabase.GenerateUniqueAssetPath —— 每次烘焙都新建一个
            // 名字不同的资产，旧的留在磁盘上没人引用。反复烘焙几次就会堆出一串孤儿资产，
            // 而且场景里只指向最新的那个，很难判断哪些能删。
            //
            // 现在改成【固定路径】：同一个场景 + 同一个 Surface 名 → 永远同一个文件。
            //
            // 但"直接覆盖"也有风险：如果先删旧资产、再建新资产时失败，就什么都不剩了。
            // 所以顺序是"先建成功 → 删旧的 → 把新的挪到目标路径"。最坏情况只是没替换上，
            // 旧资产还在，场景引用也还指向有效对象。
            string temp = AssetDatabase.GenerateUniqueAssetPath(folder + "/__baking.asset");
            AssetDatabase.CreateAsset(surface.navMeshData, temp);

            if (AssetDatabase.LoadAssetAtPath<NavMeshData>(desired) != null)
            {
                AssetDatabase.DeleteAsset(desired);
            }

            string error = AssetDatabase.MoveAsset(temp, desired);

            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError(
                    $"[导航网格] 无法把烘焙结果保存到 {desired}：{error}\n" +
                    $"  数据仍在临时资产 {temp} 里，场景引用仍然有效，" +
                    "但请手动把它改名/移动，否则下次烘焙会再产生一个临时文件。", surface);
            }

            EditorUtility.SetDirty(surface);
            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- 清理

        /// <summary>
        /// 收集"仍被引用"的导航数据资产路径。
        ///
        /// 【两条来源，缺一不可】
        ///   1. 当前已加载的场景：直接看 NavMeshSurface.navMeshData。
        ///      必须要这一条 —— 刚烘焙完还没保存的场景，磁盘版本里还没有这个引用。
        ///   2. 磁盘上的场景与预制体：用 AssetDatabase.GetDependencies 查，
        ///      不加载场景也能知道谁引用了它。
        /// </summary>
        private static HashSet<string> CollectReferencedNavMeshAssets()
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loaded = SceneManager.GetSceneAt(i);
                if (!loaded.isLoaded) continue;

                foreach (GameObject root in loaded.GetRootGameObjects())
                {
                    foreach (NavMeshSurface surface in root.GetComponentsInChildren<NavMeshSurface>(true))
                    {
                        if (surface.navMeshData == null) continue;

                        string path = AssetDatabase.GetAssetPath(surface.navMeshData);
                        if (!string.IsNullOrEmpty(path)) referenced.Add(path);
                    }
                }
            }

            foreach (string assetPath in AssetDatabase.GetAllAssetPaths())
            {
                if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;

                bool isScene = assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                bool isPrefab = assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
                if (!isScene && !isPrefab) continue;

                foreach (string dependency in AssetDatabase.GetDependencies(assetPath, true))
                {
                    if (dependency.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        referenced.Add(dependency);
                    }
                }
            }

            return referenced;
        }

        /// <summary>
        /// 找出所有 *_Navigation 目录里、已经没人引用的 NavMeshData 资产。
        /// </summary>
        private static void FindOrphanNavMeshAssets(
            out List<string> allAssets, out List<string> orphans)
        {
            allAssets = new List<string>();
            orphans = new List<string>();

            HashSet<string> referenced = CollectReferencedNavMeshAssets();

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) continue;

                // 只扫 *_Navigation 目录，避免碰到别的资产（材质、物品定义……）
                string folder = Path.GetDirectoryName(path).Replace('\\', '/');
                if (!Path.GetFileName(folder).EndsWith("_Navigation", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (AssetDatabase.LoadAssetAtPath<NavMeshData>(path) == null) continue;

                allAssets.Add(path);

                if (!referenced.Contains(path)) orphans.Add(path);
            }

            allAssets.Sort(StringComparer.OrdinalIgnoreCase);
            orphans.Sort(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 只读诊断：列出所有导航数据资产，标出哪些是孤儿。
        /// </summary>
        [MenuItem("游戏/工程/诊断导航数据资产（只读）", false, 117)]
        public static void DiagnoseNavMeshAssets()
        {
            FindOrphanNavMeshAssets(out List<string> all, out List<string> orphans);

            var report = new StringBuilder("=== 导航数据资产（只读）===\n");

            if (all.Count == 0)
            {
                report.AppendLine("没有找到任何 *_Navigation 目录下的 NavMeshData 资产。");
                report.AppendLine("（当前场景的导航可能还没烘焙，或数据内嵌在场景里。）");
                Debug.Log(report.ToString());
                return;
            }

            report.AppendLine($"共 {all.Count} 个，其中 {orphans.Count} 个没人引用：\n");

            foreach (string path in all)
            {
                bool orphan = orphans.Contains(path);
                report.AppendLine($"  {(orphan ? "【孤儿】" : "  引用 ") } {path}");
            }

            if (orphans.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("删除请运行「游戏 → 工程 → 清理无用的导航数据资产」（会先弹窗确认）。");
            }

            Debug.Log(report.ToString());
        }

        /// <summary>
        /// 删除没有任何场景/预制体引用的导航数据资产。会先弹窗列出清单。
        ///
        /// 【为什么需要确认弹窗】这是不可撤销的删除。虽然判定条件是"没人引用"，
        /// 但如果判定逻辑有偏差（例如某个场景还没保存），误删的代价很高。
        /// 所以把清单摊开让人看一眼，比直接删更合适。
        /// </summary>
        [MenuItem("游戏/工程/清理无用的导航数据资产", false, 118)]
        public static void CleanupOrphanNavMeshAssets()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[导航清理] 请先退出 Play。");
                return;
            }

            FindOrphanNavMeshAssets(out List<string> all, out List<string> orphans);

            if (all.Count == 0)
            {
                Debug.Log("[导航清理] 没有找到 *_Navigation 目录下的导航数据资产，无需清理。");
                return;
            }

            if (orphans.Count == 0)
            {
                Debug.Log($"[导航清理] 共 {all.Count} 个导航数据资产，全部仍被引用，无需清理。");
                return;
            }

            // 弹窗最多列出前 12 条，避免太长看不完
            var preview = new StringBuilder();

            for (int i = 0; i < orphans.Count && i < 12; i++)
            {
                preview.AppendLine("  " + orphans[i]);
            }

            if (orphans.Count > 12) preview.AppendLine($"  …以及另外 {orphans.Count - 12} 个");

            bool confirmed = EditorUtility.DisplayDialog(
                "清理无用的导航数据资产",
                $"将删除 {orphans.Count} 个没有任何场景/预制体引用的导航数据资产：\n\n" +
                preview +
                "\n说明：\n" +
                "• 已加载场景里 Surface 正在使用的、以及磁盘上场景/预制体引用到的都不会删\n" +
                "• 删除后可用「烘焙敌人导航网格」重新生成\n" +
                "• 这是不可撤销操作",
                "删除",
                "取消");

            if (!confirmed)
            {
                Debug.Log("[导航清理] 已取消。");
                return;
            }

            int deleted = 0;

            foreach (string path in orphans)
            {
                if (AssetDatabase.DeleteAsset(path)) deleted++;
                else Debug.LogWarning($"[导航清理] 删除失败：{path}");
            }

            AssetDatabase.Refresh();

            Debug.Log($"[导航清理] 已删除 {deleted}/{orphans.Count} 个无用的导航数据资产。" +
                      $"（保留 {all.Count - deleted} 个仍在使用的）");
        }

        private static int EnsureAgentSettings(Scene scene)
        {
            float radius = 0.35f, height = 1.8f, climb = 0.4f, slope = 50f;
            bool found = false;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (EnemyController enemy in root.GetComponentsInChildren<EnemyController>(true))
                {
                    CharacterController cc = enemy.GetComponent<CharacterController>();
                    if (cc == null) continue;
                    if (!found)
                    {
                        radius = cc.radius; height = cc.height;
                        climb = cc.stepOffset; slope = cc.slopeLimit;
                        found = true;
                    }
                    else
                    {
                        radius = Mathf.Max(radius, cc.radius);
                        height = Mathf.Max(height, cc.height);
                        climb = Mathf.Min(climb, cc.stepOffset);
                        slope = Mathf.Min(slope, cc.slopeLimit);
                    }
                }
            // 与包内 NavigationWindow 同一入口；新建专用类型，不覆盖 Humanoid。
            int id = -1;
            for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                int candidate = NavMesh.GetSettingsByIndex(i).agentTypeID;
                if (NavMesh.GetSettingsNameFromID(candidate) == EnemyNavigation.AgentTypeName) id = candidate;
            }
            if (id < 0) id = NavMesh.CreateSettings().agentTypeID;
            var settings = new SerializedObject(
                Unsupported.GetSerializedAssetInterfaceSingleton("NavMeshProjectSettings"));
            SerializedProperty types = settings.FindProperty("m_Settings");
            SerializedProperty names = settings.FindProperty("m_SettingNames");
            for (int i = 0; i < types.arraySize; i++)
            {
                SerializedProperty type = types.GetArrayElementAtIndex(i);
                if (type.FindPropertyRelative("agentTypeID").intValue != id) continue;
                type.FindPropertyRelative("agentRadius").floatValue = radius;
                type.FindPropertyRelative("agentHeight").floatValue = height;
                type.FindPropertyRelative("agentClimb").floatValue = climb;
                type.FindPropertyRelative("agentSlope").floatValue = slope;
                // 当前 CharacterController 不实现跳跃/落差 Link 穿越。
                type.FindPropertyRelative("ledgeDropHeight").floatValue = 0f;
                type.FindPropertyRelative("maxJumpAcrossDistance").floatValue = 0f;
                names.GetArrayElementAtIndex(i).stringValue = EnemyNavigation.AgentTypeName;
            }
            settings.ApplyModifiedProperties();
            return id;
        }

        private struct ModifierState
        {
            public NavMeshModifier Modifier;
            public bool Added, Ignore, Children;
            public void Restore()
            {
                if (Modifier == null) return;
                if (Added) Object.DestroyImmediate(Modifier);
                else { Modifier.ignoreFromBuild = Ignore; Modifier.applyToChildren = Children; }
            }
        }

        private static void Exclude(GameObject go, List<ModifierState> states)
        {
            NavMeshModifier modifier = go.GetComponent<NavMeshModifier>();
            bool added = modifier == null;
            if (added) modifier = go.AddComponent<NavMeshModifier>();
            states.Add(new ModifierState { Modifier = modifier, Added = added,
                Ignore = modifier.ignoreFromBuild, Children = modifier.applyToChildren });
            modifier.ignoreFromBuild = true;
            modifier.applyToChildren = true;
        }

        private static void ExcludeActors(Transform node, List<ModifierState> states)
        {
            if (node.GetComponent<CharacterController>() != null ||
                node.GetComponent<EnemyController>() != null)
            {
                Exclude(node.gameObject, states);
                return;
            }
            for (int i = 0; i < node.childCount; i++) ExcludeActors(node.GetChild(i), states);
        }

        [MenuItem("游戏/工程/诊断导航网格（只读）", false, 111)]
        public static void Diagnose()
        {
            Scene scene = SceneManager.GetActiveScene();
            var report = new StringBuilder("=== 当前场景导航诊断（只读）===\n");
            NavMeshSurface surface = FindSurface(scene);
            if (surface == null) { Debug.LogWarning(report + "没有 Surface，请先烘焙。"); return; }
            string path = surface.navMeshData != null ? AssetDatabase.GetAssetPath(surface.navMeshData) : "";
            report.AppendLine("导航资产：" + (string.IsNullOrEmpty(path) ? "【未持久化】" : path));
            report.AppendLine("Agent：" + NavMesh.GetSettingsNameFromID(surface.agentTypeID));
            NavMeshBuildSettings bs = surface.GetBuildSettings();
            report.AppendLine($"半径={bs.agentRadius:F2} 高度={bs.agentHeight:F2} 台阶={bs.agentClimb:F2} 坡度={bs.agentSlope:F1}");
            var nav = new EnemyNavigation { AgentTypeId = surface.agentTypeID };
            Game.Player.PlayerHealth player = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                player = root.GetComponentInChildren<Game.Player.PlayerHealth>(true);
                if (player != null) break;
            }
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (EnemyController enemy in root.GetComponentsInChildren<EnemyController>(true))
                {
                    Vector3 position = enemy.transform.position;
                    bool sampled = nav.TrySamplePosition(position, out NavMeshHit hit);
                    bool placed = sampled && Vector3.Distance(position, hit.position) <= 0.45f;
                    var config = new SerializedObject(enemy);
                    report.AppendLine($"「{enemy.name}」主方案={config.FindProperty("_useNavMesh").boolValue} " +
                        (placed ? "出生点通过" : "【出生点偏离/不在同层导航上】"));
                    CharacterController cc = enemy.GetComponent<CharacterController>();
                    if (cc != null && (bs.agentRadius < cc.radius || bs.agentHeight < cc.height ||
                        bs.agentClimb > cc.stepOffset + 0.001f || bs.agentSlope > cc.slopeLimit + 0.001f))
                        report.AppendLine("  【烘焙参数超出该角色能力，请重新烘焙】");
                    if (!sampled) continue;
                    if (player != null) ReportPath(nav, hit.position, player.transform.position, "玩家", report);
                    SerializedProperty patrol = config.FindProperty("_patrolPoints");
                    for (int i = 0; i < patrol.arraySize; i++)
                        if (patrol.GetArrayElementAtIndex(i).objectReferenceValue is Transform point)
                            ReportPath(nav, hit.position, point.position, "路点 " + point.name, report);
                }
            report.AppendLine("完整路径仍需 Play 检查碰撞、楼梯、拥堵和动态墙；采样成功不等于已运行通过。");
            Debug.Log(report.ToString());
        }

        private static void ReportPath(EnemyNavigation nav, Vector3 start, Vector3 target,
            string label, StringBuilder report)
        {
            if (!nav.TrySamplePosition(target, out NavMeshHit end))
            { report.AppendLine("  → " + label + "：【目标不在同层网格】"); return; }
            var path = new NavMeshPath();
            bool ok = NavMesh.CalculatePath(start, end.position, nav.Filter, path);
            report.AppendLine("  → " + label + "：" + (ok ? path.status.ToString() : "【无路】"));
        }
    }
}
#endif
