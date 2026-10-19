#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Game.AI;
using Game.Gameplay.AI;
using Game.Gameplay.Building;
using Game.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Editor
{
    public static class AggroDeathRegression
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static void Invoke(object target, string method) =>
            target.GetType().GetMethod(method, Private).Invoke(target, null);
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Private).SetValue(target, value);

        [MenuItem("游戏/工程/验证圆形仇恨与死亡复活（独立测试场景）", false, 113)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Scene previous = SceneManager.GetActiveScene();
            string folder = "Assets/__AggroDeathRegression_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(previous.path))
            {
                if (!Application.isBatchMode)
                { Debug.LogWarning("[验证] 请先保存未命名场景，再运行独立验证。"); return; }
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
                EditorSceneManager.SaveScene(previous, folder + "/BatchBaseline.unity");
            }
            Scene test = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var report = new StringBuilder("Aggro/death native regression " + DateTime.Now.ToString("O") + "\n");
            int passed = 0, failed = 0;
            InputMode modeBefore = InputModeState.Current;
            CursorLockMode lockBefore = Cursor.lockState;
            bool cursorBefore = Cursor.visible;
            void Check(bool ok, string label)
            { report.AppendLine((ok ? "PASS " : "FAIL ") + label); if (ok) passed++; else failed++; }
            try
            {
                SceneManager.SetActiveScene(test);
                InputModeState.SetPlayerDead(false);
                var player = new GameObject("Player fixture");
                Vector3 spawn = new Vector3(10000f, 0f, 10000f);
                player.transform.position = spawn;
                var cc = player.AddComponent<CharacterController>();
                var input = player.AddComponent<PlayerInputSource>();
                var movement = player.AddComponent<PlayerController>();
                Set(movement, "_controller", cc);
                var health = player.AddComponent<PlayerHealth>();
                Invoke(health, "Awake");
                var screen = player.GetComponent<PlayerDeathScreen>();
                Check(screen != null && player.GetComponents<PlayerDeathScreen>().Length == 1,
                    "旧玩家自动接入且只有一份死亡界面");
                Invoke(screen, "Awake");
                Invoke(screen, "OnEnable");
                var inventory = player.AddComponent<InventoryScreen>();
                var builder = player.AddComponent<BuildController>();
                Set(inventory, "_isOpen", true);
                Set(inventory, "_dragPending", true);
                Set(inventory, "_dragActive", true);
                builder.EnterBuilding();

                var enemyObject = new GameObject("Enemy fixture");
                enemyObject.transform.position = spawn + Vector3.forward * 13f;
                var enemy = enemyObject.AddComponent<EnemyController>();
                enemy.ConfigureAvoidanceDefaults();
                Invoke(enemy, "Awake");
                Invoke(enemy, "OnEnable");
                Set(enemy, "_target", player.transform);
                Set(enemy, "_targetDamageable", health);
                var senses = (EnemySenses)typeof(EnemyController).GetMethod("GatherSenses", Private).Invoke(enemy, null);
                Check(senses.TargetInSight, "敌人背后的玩家在圆内也被发现");
                enemyObject.transform.position = spawn + Vector3.forward * 14.1f;
                senses = (EnemySenses)typeof(EnemyController).GetMethod("GatherSenses", Private).Invoke(enemy, null);
                Check(!senses.TargetInSight, "超过仇恨半径不触发发现");

                Invoke(enemy, "UpdateAggroCircle");
                var circle = enemyObject.GetComponentInChildren<LineRenderer>();
                Check(circle != null && circle.enabled && circle.loop && circle.positionCount == 96,
                    "运行时范围圈已生成，不只依赖 Gizmo");
                Vector3 point = circle.GetPosition(0) - enemyObject.transform.position;
                point.y = 0f;
                Check(Mathf.Abs(point.magnitude - enemy.AggroRadius) < 0.01f,
                    "可视圈与真实仇恨半径一致");
                Material material = Resources.Load<Material>("EnemyAggroRange");
                Check(material != null && material.shader != null &&
                    material.shader.name == "Game/Debug/EnemyAggroRange", "范围材质与专用 Shader 引用有效");
                bool shaderOk = true;
                foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                    if (message.severity.ToString() == "Error") shaderOk = false;
                Check(shaderOk, "范围 Shader 导入没有编译错误");

                var brain = (EnemyBrainLogic)typeof(EnemyController).GetField("_brain", Private).GetValue(enemy);
                brain.ForceState(EnemyState.Chase);
                health.RestoreHealth(0f);
                Check(health.IsDead && screen.IsShowing, "死亡事件自动显示死亡界面");
                Check(InputModeState.Current == InputMode.Dead && !InputModeState.AllowsMovement &&
                    !InputModeState.AllowsCombat && !InputModeState.AllowsInteraction && !InputModeState.AllowsBuilding,
                    "死亡屏蔽移动、战斗、交互和建造");
                Check(!input.InputEnabled && !movement.enabled, "死亡立即停用角色操作");
                Check(!inventory.IsOpen && !builder.IsBuilding, "死亡关闭背包拖拽与建造模式");
                Check(brain.State == EnemyState.Idle, "死亡清除敌人的旧仇恨");
                InputModeState.Current = InputMode.Inventory;
                InputModeState.Reset();
                builder.EnterBuilding();
                Check(InputModeState.Current == InputMode.Dead && !builder.IsBuilding,
                    "背包、建造和普通 Reset 不能覆盖死亡锁");
                Check(Cursor.visible && Cursor.lockState == CursorLockMode.None, "死亡界面释放鼠标供点击");

                movement.Teleport(spawn + new Vector3(5f, 0f, 5f));
                Check(screen.RevivePlayer(), "复活按钮所调用的入口成功执行");
                Check(!health.IsDead && Mathf.Approximately(health.Current, health.Max) && !screen.IsShowing,
                    "复活满血并关闭死亡界面");
                Check(Vector3.Distance(player.transform.position, spawn) < 0.01f,
                    "复活返回本次开局出生点");
                Check(health.IsInvulnerable && health.TakeDamage(50f) == 0f, "复活保护期间伤害无效");
                Check(input.InputEnabled && movement.enabled && InputModeState.AllowsMovement &&
                    InputModeState.AllowsCombat, "复活恢复正常操作");
                Check(!screen.RevivePlayer(), "活着时重复点击复活不会再次结算");

                input.InputEnabled = false;
                movement.enabled = false;
                health.RestoreHealth(0f);
                health.RestoreHealth(25f);
                Invoke(screen, "Update");
                Check(!screen.IsShowing && !input.InputEnabled && !movement.enabled &&
                    !InputModeState.IsPlayerDead, "正血量读档解除黑屏，保留原本禁用的组件状态");

                health.RestoreHealth(0f);
                Set(enemy, "_target", player.transform);
                Set(enemy, "_targetDamageable", health);
                Invoke(enemy, "ResolveTarget");
                Check(!ReferenceEquals(typeof(EnemyController).GetField("_target", Private).GetValue(enemy), player.transform),
                    "敌人不把死亡玩家重新选为目标");
            }
            catch (Exception error) { failed++; report.AppendLine("FAIL " + error); }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (test.IsValid() && test.isLoaded) EditorSceneManager.CloseScene(test, true);
                InputModeState.SetPlayerDead(modeBefore == InputMode.Dead);
                InputModeState.Current = modeBefore;
                Cursor.lockState = lockBefore;
                Cursor.visible = cursorBefore;
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
            report.AppendLine($"RESULT passed={passed} failed={failed}");
            string output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "OptimizationReview", "aggro-death-results.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, report.ToString());
            if (failed > 0) Debug.LogError(report.ToString()); else Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(failed == 0 ? 0 : 1);
        }
    }
}
#endif
