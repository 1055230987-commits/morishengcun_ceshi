#if UNITY_EDITOR
using System.Collections.Generic;
using Game.Gameplay.AI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>显式重置当前场景的敌人避障参数；日常检查优先使用只读诊断。</summary>
    public static class EnemyConfigTools
    {
        [MenuItem("游戏/工程/给场景敌人写入默认配置", false, 108)]
        public static void ApplyAvoidanceDefaults()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { Debug.LogWarning("[敌人配置] 请先退出 Play，再写入场景配置。"); return; }
            EnemyController[] enemies = CurrentEnemies();

            if (enemies.Length == 0)
            {
                Debug.LogWarning("[敌人配置] 当前场景里没有 EnemyController。请放置一个已有敌人实例或预制体。");
                return;
            }

            int changed = 0;

            foreach (EnemyController enemy in enemies)
            {
                int before = enemy.ConfigVersionForEditor;

                Undo.RecordObject(enemy, "写入敌人避障默认配置");
                enemy.ConfigureAvoidanceDefaults();
                EditorUtility.SetDirty(enemy);

                if (enemy.ConfigVersionForEditor != before) changed++;
            }

            // 标记场景已修改，这样 Unity 会提示保存 / Ctrl+S 能存下去
            EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            Debug.Log(
                $"[敌人配置] 已给 {enemies.Length} 个敌人写入避障默认配置" +
                $"（其中 {changed} 个是旧版本）。\n" +
                "  现在 Inspector 里应能看到：\n" +
                "    NavMesh 主方案与局部回退 = 开启\n" +
                "    探距最远距离 = 2.6\n" +
                "    反应时间 = 0.35\n" +
                "    探距高度 = 0.9\n" +
                "    分离半径 = 1.8\n" +
                "    分离强度 = 0.9\n" +
                "  记得 Ctrl+S 保存场景。");
        }

        /// <summary>
        /// 只读诊断：列出当前场景里所有敌人的配置版本与关键字段值。
        ///
        /// 用来确认"Inspector 里看到的到底是不是运行时真正用的值"。
        /// </summary>
        [MenuItem("游戏/工程/诊断敌人配置（只读）", false, 109)]
        public static void Diagnose()
        {
            EnemyController[] enemies = CurrentEnemies();

            var report = new System.Text.StringBuilder();
            report.AppendLine($"=== 敌人配置诊断（{enemies.Length} 个）===");

            if (enemies.Length == 0)
            {
                report.AppendLine("场景里没有 EnemyController。");
                Debug.Log(report.ToString());
                return;
            }

            foreach (EnemyController enemy in enemies)
            {
                var serialized = new SerializedObject(enemy);

                report.AppendLine();
                report.AppendLine($"「{enemy.name}」配置版本 = {enemy.ConfigVersionForEditor}");

                string[] fields =
                {
                    "_aggroRadius", "_showAggroRange", "_useNavMesh", "_avoidObstacles", "_avoidLookAheadDistance", "_avoidReactionSeconds",
                    "_avoidProbeHeight", "_separationRadius", "_separationStrength",
                    "_lineOfSightMask", "_maxAttackHeightDifference", "_eyeHeight",
                    "_configVersion",
                };

                foreach (string field in fields)
                {
                    SerializedProperty property = serialized.FindProperty(field);

                    if (property == null)
                    {
                        report.AppendLine($"  {field,-30} 【找不到这个属性字段】");
                        continue;
                    }

                    string value = property.propertyType switch
                    {
                        SerializedPropertyType.Boolean => property.boolValue.ToString(),
                        SerializedPropertyType.Float => property.floatValue.ToString("F3"),
                        SerializedPropertyType.Integer => property.intValue.ToString(),
                        SerializedPropertyType.LayerMask => property.intValue.ToString(),
                        _ => property.propertyType.ToString(),
                    };

                    report.AppendLine($"  {field,-30} {value}");
                }
            }

            Debug.Log(report.ToString());
        }

        private static EnemyController[] CurrentEnemies()
        {
            var result = new List<EnemyController>();
            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<EnemyController>(true));
            return result.ToArray();
        }
    }
}
#endif
