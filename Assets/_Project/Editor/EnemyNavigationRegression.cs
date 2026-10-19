#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.Gameplay.AI;
using Game.Gameplay.Building;
using Game.World;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Editor
{
    /// <summary>真实 NavMesh/CharacterController 回归；只创建并关闭独立附加场景。</summary>
    public static class EnemyNavigationRegression
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static string Output(string name) => Path.Combine(
            Path.GetDirectoryName(Application.dataPath), "OptimizationReview", name);

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            if (File.Exists(Output("navigation.request"))) EditorApplication.delayCall += Requested;
        }

        private static void Requested()
        {
            if (!File.Exists(Output("navigation.request"))) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorApplication.delayCall += Requested; return; }
            File.Delete(Output("navigation.request"));
            Run();
        }

        [MenuItem("游戏/工程/验证敌人导航（独立测试场景）", false, 112)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { Debug.LogWarning("[导航验证] 请先退出 Play。"); return; }
            Scene previous = SceneManager.GetActiveScene();
            string folder = "Assets/__NavigationRegression_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode)
                {
                    Debug.LogWarning("[导航验证] 当前是未命名场景，请先自行保存场景再运行验证。");
                    return;
                }
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
                EditorSceneManager.SaveScene(previous, folder + "/BatchBaseline.unity");
            }
            Scene test = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var report = new StringBuilder("Native navigation regression " + DateTime.Now.ToString("O") + "\n");
            int passed = 0, failed = 0;
            void Check(bool ok, string label)
            {
                report.AppendLine((ok ? "PASS " : "FAIL ") + label);
                if (ok) passed++; else failed++;
            }
            NavMeshSurface surface = null;
            try
            {
                SceneManager.SetActiveScene(test);
                var root = new GameObject("Navigation regression");
                root.transform.position = new Vector3(10000f, 0f, 10000f);
                surface = root.AddComponent<NavMeshSurface>();
                surface.collectObjects = CollectObjects.Children;
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                surface.agentTypeID = 0;
                surface.overrideVoxelSize = true;
                surface.voxelSize = 0.1f;
                Block(root.transform, new Vector3(0,-0.1f,0), new Vector3(30,0.2f,30));
                Block(root.transform, new Vector3(0,1.5f,0), new Vector3(0.25f,3,10));
                Block(root.transform, new Vector3(24,-0.1f,0), new Vector3(6,0.2f,6));
                Physics.SyncTransforms();
                surface.BuildNavMesh();
                Check(surface.navMeshData != null, "真实碰撞体烘焙出数据");
                Vector3 start = root.transform.position + new Vector3(-4,0,0);
                Vector3 target = root.transform.position + new Vector3(4,0,0);
                var nav = new EnemyNavigation { AgentTypeId = 0 };
                var state = nav.GetDirection(start, target, 0f, out Vector3 initial);
                Check(state == EnemyNavigationResult.Moving && nav.CornerCount > 2,
                    "长墙两侧存在拐点路径");

                var body = new GameObject("Follower");
                body.transform.position = start;
                var cc = body.AddComponent<CharacterController>();
                cc.radius = 0.35f; cc.height = 1.8f; cc.center = Vector3.up * 0.9f;
                cc.stepOffset = 0.4f; cc.skinWidth = 0.03f; cc.minMoveDistance = 0f;
                float travelled = 0f;
                const float dt = 1f / 60f;
                bool crossed = false;
                for (int frame = 0; frame < 1200; frame++)
                {
                    Vector3 before = body.transform.position;
                    state = nav.GetDirection(before, target, frame * dt, out Vector3 direction);
                    float step = Mathf.Min(4.2f * dt, nav.DistanceToCorner);
                    if (state != EnemyNavigationResult.Moving)
                        step = 0f;
                    cc.Move(direction * step + Vector3.down * 2f * dt);
                    Physics.SyncTransforms();
                    Vector3 after = body.transform.position;
                    travelled += Vector3.Distance(before, after);
                    if (after.x > root.transform.position.x - 0.25f &&
                        after.x < root.transform.position.x + 0.25f &&
                        Mathf.Abs(after.z - root.transform.position.z) < 4.8f) crossed = true;
                    if (Vector3.Distance(after, target) < 0.3f) break;
                }
                Check(Vector3.Distance(body.transform.position, target) < 0.3f && !crossed,
                    "CharacterController 实际绕墙抵达，未穿墙（路程 " + travelled.ToString("F2") +
                    "，终点 " + body.transform.position + "，状态 " + nav.Result + "）");
                if (Vector3.Distance(body.transform.position, target) >= 0.3f)
                {
                    nav.TrySamplePosition(body.transform.position, out NavMeshHit sampled);
                    var debugPath = new NavMeshPath();
                    NavMesh.CalculatePath(sampled.position, target, nav.Filter, debugPath);
                    foreach (Vector3 corner in debugPath.corners)
                    {
                        bool blocked = NavMesh.Raycast(sampled.position, corner, out NavMeshHit edge, nav.Filter);
                        report.AppendLine($"  corner {corner.ToString("F4")} blocked={blocked} edge={edge.position.ToString("F4")} distance={edge.distance}");
                    }
                }

                nav.Reset();
                Vector3 absent = root.transform.position + Vector3.one * 500f;
                for (int i = 0; i < 10; i++) nav.GetDirection(absent, target, i * 0.01f, out _);
                Check(nav.RecalculationCount == 1 && nav.Result == EnemyNavigationResult.NoNavMesh,
                    "没有网格时重试受节流");
                nav.GetDirection(absent, target, 0.4f, out _);
                Check(nav.RecalculationCount == 2, "间隔到期后允许重试");
                nav.Reset();
                for (int i = 0; i < 10; i++)
                    nav.GetDirection(start, target + Vector3.up * 8f, i * 0.01f, out _);
                Check(nav.RecalculationCount == 1 && nav.Result == EnemyNavigationResult.NoPath,
                    "目标不在网格时不回退撞墙、不吸附到楼下");
                nav.Reset();
                Check(nav.GetDirection(target, target, 0f, out _) == EnemyNavigationResult.Arrived,
                    "已抵达与无路径区分");
                nav.Reset();
                nav.GetDirection(start, root.transform.position + Vector3.right * 24f, 0f, out _);
                Check(nav.LastStatus == NavMeshPathStatus.PathPartial, "断开的地面返回 Partial");

                // 攻击受阻后的真实移动意图，防止又被停止距离截断。
                var actorObject = new GameObject("Actor");
                actorObject.transform.position = root.transform.position + Vector3.left * 0.6f;
                var actorCc = actorObject.AddComponent<CharacterController>();
                actorCc.center = Vector3.up * 0.9f; actorCc.radius = 0.35f; actorCc.height = 1.8f;
                actorCc.stepOffset = 0.4f; actorCc.slopeLimit = 50f;
                var actor = actorObject.AddComponent<EnemyController>();
                actor.ConfigureAvoidanceDefaults();
                typeof(EnemyController).GetMethod("Awake", Private).Invoke(actor, null);
                var targetObject = new GameObject("Target");
                targetObject.transform.position = root.transform.position + Vector3.right * 0.6f;
                typeof(EnemyController).GetField("_target", Private).SetValue(actor, targetObject.transform);
                Physics.SyncTransforms();
                object[] intent = { Vector3.zero, 0f, 0f };
                typeof(EnemyController).GetMethod("ComputeAttackIntent", Private).Invoke(actor, intent);
                Check((float)intent[1] > 0f && (float)intent[2] == 0f,
                    "隔墙 1.2 米仍请求移动且停止距离为零");
                actorObject.transform.position = root.transform.position + Vector3.left * 0.48f;
                targetObject.transform.position = root.transform.position + Vector3.right * 0.48f;
                Physics.SyncTransforms();
                Check(!(bool)typeof(EnemyController).GetMethod("CanReachTargetNow", Private).Invoke(actor, null),
                    "贴近薄墙时射线不因起点偏移而漏检");
                targetObject.transform.position += Vector3.up * 3f;
                intent = new object[] { Vector3.zero, 0f, 0f };
                typeof(EnemyController).GetMethod("ComputeChaseIntent", Private).Invoke(actor, intent);
                Check(((Vector3)intent[0]).y > 2.9f, "追击目标保留楼层高度");

                actorObject.transform.position = root.transform.position + new Vector3(-4,0,-8);
                var neighborObject = new GameObject("Neighbor");
                neighborObject.transform.position = actorObject.transform.position + Vector3.back * 0.6f;
                var neighbor = neighborObject.AddComponent<EnemyController>();
                neighbor.ConfigureAvoidanceDefaults();
                typeof(EnemyController).GetMethod("Awake", Private).Invoke(neighbor, null);
                typeof(EnemyController).GetMethod("OnEnable", Private).Invoke(neighbor, null);
                typeof(EnemyController).GetField("_navigation", Private).SetValue(actor,
                    new EnemyNavigation { AgentTypeId = 0 });
                Vector3 openTarget = actorObject.transform.position + Vector3.right * 8f;
                var resolve = typeof(EnemyController).GetMethod("ResolveMoveDirection", Private);
                Vector3 separated = (Vector3)resolve.Invoke(actor,
                    new object[] { Vector3.right, openTarget, 4.2f });
                Check(separated.z > 0.01f, "NavMesh 主流程确实叠加邻居分离");
                neighborObject.transform.position += Vector3.up * 3f;
                separated = (Vector3)resolve.Invoke(actor,
                    new object[] { Vector3.right, openTarget, 4.2f });
                Check(Mathf.Abs(separated.z) < 0.01f, "不同楼层的敌人不互相推开");
                Object.DestroyImmediate(neighborObject);
                Vector3 unreachable = (Vector3)resolve.Invoke(actor,
                    new object[] { Vector3.right, openTarget + Vector3.up * 8f, 4.2f });
                Check(unreachable.sqrMagnitude < 1e-6f &&
                    (bool)typeof(EnemyController).GetField("_usingNavigation", Private).GetValue(actor),
                    "有网格但目标不可达时不退回局部撞墙");

                var construction = new GameObject("Construction test");
                BuildArea area = construction.AddComponent<BuildArea>();
                BuildController builder = construction.AddComponent<BuildController>();
                typeof(BuildController).GetField("_area", Private).SetValue(builder, area);
                typeof(BuildController).GetField("_spawnRoot", Private).SetValue(builder, root.transform);
                var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
                typeof(BuildController).GetField("_partMaterial", Private).SetValue(builder, material);
                var part = new BuildController.BuildablePart
                    { DisplayName = "测试墙", Height = 3f, Thickness = 0.25f, IsEdgePart = true };
                GameObject built = (GameObject)typeof(BuildController).GetMethod("CreatePartObject", Private)
                    .Invoke(builder, new object[] { part, Vector3.zero, true, default(BuildEdge),
                        default(BuildAreaData.BuildCell) });
                Physics.SyncTransforms();
                NavMeshObstacle obstacle = built.GetComponent<NavMeshObstacle>();
                Vector3 obstacleWorld = Vector3.Scale(obstacle.size, built.transform.lossyScale);
                Check(Vector3.Distance(obstacleWorld, built.GetComponent<BoxCollider>().bounds.size) < 0.001f,
                    "真实建墙入口的导航障碍与墙碰撞体尺寸一致");
                Object.DestroyImmediate(built);
                Object.DestroyImmediate(construction);
                Object.DestroyImmediate(material);

                var actorConfig = new SerializedObject(actor);
                actorConfig.FindProperty("_configVersion").intValue = 2;
                actorConfig.FindProperty("_separationRadius").floatValue = 0.75f;
                actorConfig.FindProperty("_useNavMesh").boolValue = false;
                actorConfig.ApplyModifiedPropertiesWithoutUndo();
                typeof(EnemyController).GetMethod("MigrateConfigIfNeeded", Private).Invoke(actor, null);
                actorConfig.Update();
                Check(actorConfig.FindProperty("_useNavMesh").boolValue &&
                    Mathf.Abs(actorConfig.FindProperty("_separationRadius").floatValue - 0.75f) < 0.001f,
                    "v2 升级主方案开关时保留手调参数");

                if (!AssetDatabase.IsValidFolder(folder))
                    AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
                string scenePath = folder + "/Test.unity";
                EditorSceneManager.SaveScene(test, scenePath);
                if (Application.isBatchMode)
                {
                    // 仅隔离批处理工程验证会写专用 Agent 类型的编辑器烘焙入口。
                    var modifier = actorObject.AddComponent<NavMeshModifier>();
                    modifier.ignoreFromBuild = false;
                    modifier.applyToChildren = false;
                    EnemyNavMeshTools.Bake();
                    Check(!modifier.ignoreFromBuild && !modifier.applyToChildren,
                        "完整烘焙入口恢复已有 Modifier 配置");
                    NavMeshBuildSettings settings = surface.GetBuildSettings();
                    Check(NavMesh.GetSettingsNameFromID(surface.agentTypeID) == EnemyNavigation.AgentTypeName &&
                        Mathf.Abs(settings.agentRadius - actorCc.radius) < 0.001f &&
                        settings.agentClimb <= actorCc.stepOffset + 0.001f,
                        "专用 Agent 类型与角色尺寸、台阶能力匹配");
                    nav.AgentTypeId = surface.agentTypeID;
                }
                else EnemyNavMeshTools.SaveDataAsset(surface, scenePath);
                string dataPath = AssetDatabase.GetAssetPath(surface.navMeshData);
                EditorSceneManager.SaveScene(test);
                Check(!string.IsNullOrEmpty(dataPath) && File.Exists(dataPath),
                    "导航数据确实落盘为资产");
                surface.RemoveData();
                Object.DestroyImmediate(surface.gameObject);
                // 重新打开刚保存的测试场景，校验引用与导航数据注册。
                EditorSceneManager.CloseScene(test, true);
                test = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                SceneManager.SetActiveScene(test);
                surface = null;
                foreach (GameObject loadedRoot in test.GetRootGameObjects())
                {
                    surface = loadedRoot.GetComponent<NavMeshSurface>();
                    if (surface != null) break;
                }
                nav.Reset();
                Check(surface != null && surface.navMeshData != null &&
                    nav.GetDirection(start, target, 1f, out _) == EnemyNavigationResult.Moving,
                    "关闭重开场景后数据引用与寻路仍有效");
            }
            catch (Exception error)
            {
                failed++;
                report.AppendLine("FAIL exception: " + error);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (test.IsValid() && test.isLoaded) EditorSceneManager.CloseScene(test, true);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
            report.AppendLine($"RESULT passed={passed} failed={failed}");
            Directory.CreateDirectory(Path.GetDirectoryName(Output("navigation-results.txt")));
            File.WriteAllText(Output("navigation-results.txt"), report.ToString());
            if (failed > 0) Debug.LogError(report.ToString()); else Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        private static void Block(Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject("Collider");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            go.AddComponent<BoxCollider>();
        }
    }
}
#endif
