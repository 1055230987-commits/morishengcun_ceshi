#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>
    /// 用【真实生成器 + 真实 CharacterController】验证楼梯能不能走上去。
    ///
    /// 【为什么需要它】楼梯问题排查了很多轮，全部靠"改几何 → 让用户手动 Play 试"。
    /// 而真正的原因是二楼的室内楼板少开了楼梯井洞口，形成一块【渲染被关掉、碰撞体还在】
    /// 的隐形板。这类"看不见但撞得到"的问题，几何断言抓不到（断言只验证它建模的那部分），
    /// 必须让真实物理跑一遍才能发现。
    ///
    /// 本测试自己建独立场景、自己搭楼梯、自己驱动一个 CharacterController 往前走，
    /// 【不打开也不保存用户的场景】。
    ///
    /// 【怎么用】菜单「游戏 → 工程 → 验证楼梯真实碰撞」，结果写到
    /// OptimizationReview/stair-physics-results.txt，同时打到 Console。
    ///
    /// 【踩过的坑，改这个文件前先读】
    ///   1. `SceneManager.CreateScene` 在编辑模式会抛
    ///      "This can only be used during play mode" —— 必须用
    ///      `EditorSceneManager.NewScene(..., NewSceneMode.Single)`。
    ///      但那样会关掉当前场景，所以测试前要先问用户保存，测完再恢复。
    ///   2. `Physics.Simulate` 只在 `Physics.simulationMode == SimulationMode.Script`
    ///      时才有意义、也才允许手动调用；默认是 FixedUpdate 模式。
    ///      测试期间要临时切到 Script，结束后恢复。
    /// </summary>
    public static class StairPhysicsRegression
    {
        private static string FilePath(string name) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath), "OptimizationReview", name);

        private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [InitializeOnLoadMethod]
        private static void Schedule()
        {
            if (File.Exists(FilePath("stair-physics.request"))) EditorApplication.delayCall += RunRequested;
        }

        private static void RunRequested()
        {
            if (!File.Exists(FilePath("stair-physics.request"))) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.delayCall += RunRequested;
                return;
            }
            File.Delete(FilePath("stair-physics.request"));
            Run();
        }

        [MenuItem("游戏/工程/验证楼梯真实碰撞（独立测试场景）")]
        public static void Run()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[楼梯验证] 请退出播放模式后运行。");
                return;
            }

            // 本测试需要新建并关闭场景，会丢失未保存的改动 —— 先问一次。
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.LogWarning("[楼梯验证] 已取消（当前场景有未保存改动）。");
                return;
            }

            // 记住要恢复的场景路径
            string previousScenePath = SceneManager.GetActiveScene().path;

            var report = new StringBuilder("Native stair regression: " + DateTime.Now.ToString("O") + "\n");
            int failed = 0;

            // Physics.Simulate 需要 Script 模式；测试期间临时切换。
            SimulationMode previousSimulationMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;

            try
            {
                foreach (int hz in new[] { 30, 60, 144 })
                {
                    try { RunCase(hz, report); }
                    catch (Exception error) { failed++; report.AppendLine("FAIL " + error); }
                }
            }
            finally
            {
                Physics.simulationMode = previousSimulationMode;

                // 恢复用户原来的场景
                if (!string.IsNullOrEmpty(previousScenePath) &&
                    File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), previousScenePath)))
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
            }

            report.AppendLine(failed == 0 ? "PASS: 3 native physics cases" : $"FAIL: {failed} cases");

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath("stair-physics-results.txt")));
            File.WriteAllText(FilePath("stair-physics-results.txt"), report.ToString());
            Debug.Log(report.ToString());
        }

        private static float Constant(string name) =>
            (float)typeof(GreyboxLevelBuilder).GetField(name, PrivateStatic).GetRawConstantValue();

        private static void Invoke(string name, params object[] args) =>
            typeof(GreyboxLevelBuilder).GetMethod(name, PrivateStatic).Invoke(null, args);

        /// <summary>
        /// 在独立空场景里跑一遍：搭出两层房的一楼/二楼 + 楼梯，放一个 CharacterController
        /// 从坡底往前走，检查它能不能到达二楼高度。
        /// </summary>
        private static void RunCase(int hz, StringBuilder report)
        {
            // 【关键】编辑器里不能用 SceneManager.CreateScene，必须用 EditorSceneManager。
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("StairFixture");
            try
            {
                // 用生产生成器搭出一楼（含天花板）+ 二楼（含室内楼板）+ 楼梯。
                // 这两层里"天花板"和"室内楼板"必须在楼梯井处共用洞口 ——
                // 这正是本次修复的要点，也是本测试要守住的不变量。
                Invoke("BuildFloorLevel", root.transform, 0, "一楼", null, null, true, true, false);
                Invoke("BuildFloorLevel", root.transform, 1, "二楼", null, null, false, false, true);

                var stairs = new GameObject("楼梯");
                stairs.transform.SetParent(root.transform, false);
                Invoke("BuildWalkableStairs", stairs.transform, null);

                float startZ = Constant("StairStartZ") - 0.6f;
                float targetZ = Constant("StairEndZ") + 0.65f;

                var actor = new GameObject("TestCharacter");
                actor.transform.SetParent(root.transform, false);
                actor.transform.position = new Vector3(
                    Constant("StairCenterX"),
                    Constant("TwoStoryFloorTop") + 0.05f,
                    startZ);

                CharacterController controller = actor.AddComponent<CharacterController>();
                controller.height = 1.8f;
                controller.radius = 0.35f;
                controller.center = new Vector3(0f, 0.9f, 0f);
                controller.stepOffset = 0.4f;
                controller.skinWidth = 0.03f;
                controller.slopeLimit = 50f;
                controller.minMoveDistance = 0f;

                Physics.SyncTransforms();

                PhysicsScene physics = scene.GetPhysicsScene();
                physics.Simulate(1f / hz);

                float vertical = 0f;
                float dt = 1f / hz;
                CollisionFlags flags = CollisionFlags.None;
                int frames = 0;

                for (; frames < hz * 8 && actor.transform.position.z < targetZ; frames++)
                {
                    vertical = controller.isGrounded ? -2f : vertical - 20f * dt;
                    flags = controller.Move(new Vector3(0f, vertical * dt, 4.5f * dt));
                    physics.Simulate(dt);
                }

                Vector3 top = actor.transform.position;
                float upperTop = Constant("UpperFloorTop");
                bool reached = top.z >= targetZ && Mathf.Abs(top.y - upperTop) < 0.15f;

                report.AppendLine(
                    $"  {hz,3}Hz: reached={reached}, pos={top:F4}, frames={frames}, " +
                    $"grounded={controller.isGrounded}, flags={flags}");

                Require(reached,
                    $"{hz}Hz 没走到二楼：期望 z>={targetZ:F2} 且 y≈{upperTop:F2}，实际 {top:F4}。" +
                    "最可能的原因：某个楼层的楼板/天花板在楼梯井处没开洞，" +
                    "形成了一块【渲染被关、碰撞还在】的隐形板。请检查 " +
                    "GreyboxLevelBuilder.BuildUpperFloorSlab 是否同时被天花板和室内楼板调用。");

                // 反向：从二楼走回一楼，验证下楼梯也正常（并且不会掉穿楼板）。
                for (frames = 0; frames < hz * 8 && actor.transform.position.z > startZ; frames++)
                {
                    vertical = controller.isGrounded ? -2f : vertical - 20f * dt;
                    controller.Move(new Vector3(0f, vertical * dt, -4.5f * dt));
                    physics.Simulate(dt);
                }

                for (int i = 0; i < hz; i++) controller.Move(Vector3.down * 2f * dt);

                Vector3 bottom = actor.transform.position;
                float floorTop = Constant("TwoStoryFloorTop");
                Require(bottom.z <= startZ && Mathf.Abs(bottom.y - floorTop) < 0.15f,
                    $"{hz}Hz 下楼梯失败或掉穿楼板：期望 z<={startZ:F2} 且 y≈{floorTop:F2}，实际 {bottom:F4}");

                report.AppendLine($"       下楼梯通过: {bottom:F4}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
