#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using Game.Gameplay.Building;
using Game.Player;
using Game.Save;
using Game.World;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 建造区诊断：对照【数据层】和【场景里的实际物体】是否一致。
    ///
    /// 【为什么必须有这个工具】
    /// 本工程楼梯卡了五轮，最后根因是一块"数据上存在、但场景里位置对不上"的楼板
    /// —— 它变成了一堵看不见的墙。建造系统比楼梯更容易出现这类不一致，因为：
    ///   - 物体是玩家在运行时【动态生成】的，不像场景生成器那样一次性建完
    ///   - 数据层（BuildAreaData）和场景物体（GameObject）是两份状态，靠代码保持同步
    ///   - 放置失败、拆除失败、回滚失败都会让两者分家
    ///
    /// 所以这里把两边都打印出来，并且【主动比对】：
    ///   - 数据里有、场景里没有 → 幽灵记录（拆不掉、但挡着）
    ///   - 场景里有、数据里没有 → 野物体（能被建上去、但状态不对）
    ///   - 位置对不上 → 数据坐标换算错了
    ///
    /// 用法：菜单「游戏 → 工程 → 诊断建造区状态」。运行中（Play）和编辑期都能跑，
    /// 但只有 Play 模式下才有实际意义（编辑期还没有放置的数据）。
    /// </summary>
    public static class BuildAreaDiagnostics
    {
        [MenuItem("游戏/工程/诊断建造区状态", priority = 106)]
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine("===== 建造区诊断 =====");
            report.AppendLine($"运行模式：{(Application.isPlaying ? "Play" : "编辑期（没有运行时数据，仅供参考）")}");
            report.AppendLine();

            BuildArea[] areas = Object.FindObjectsByType<BuildArea>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (areas.Length == 0)
            {
                report.AppendLine("【没有找到任何 BuildArea】");
                report.AppendLine("  请执行菜单「游戏 → 灰盒 → 重建到当前场景」来生成家园建造区。");
                Debug.Log(report.ToString());
                return;
            }

            report.AppendLine($"找到 {areas.Length} 个建造区。");
            report.AppendLine();

            int problems = 0;

            for (int i = 0; i < areas.Length; i++)
            {
                problems += DescribeArea(areas[i], i, report);
            }

            report.AppendLine();
            report.AppendLine("===== 结论 =====");

            if (problems == 0)
            {
                report.AppendLine("没有发现数据层与场景不一致的问题。");
            }
            else
            {
                report.AppendLine($"★ 发现 {problems} 处不一致 —— 上面标注了 ★ 的行就是问题所在。");
                report.AppendLine();
                report.AppendLine("常见原因：");
                report.AppendLine("  - 放置时生成失败但登记成功（或反过来）→ 检查 BuildController 的回滚分支");
                report.AppendLine("  - 重建场景后旧物体没清干净 → 手动删掉「玩家建造物」节点再重建");
                report.AppendLine("  - Inspector 改了建造区尺寸 → 已放置的数据会被丢弃（会有警告）");
            }

            Debug.Log(report.ToString());
        }

        /// <summary>描述一个建造区，返回发现的问题数量。</summary>
        private static int DescribeArea(BuildArea area, int index, StringBuilder report)
        {
            int problems = 0;

            report.AppendLine($"---------- 建造区 {index}：「{area.name}」 ----------");

            Vector3 origin = area.OriginWorld;
            float sizeX = area.Width * area.CellSize;
            float sizeZ = area.Depth * area.CellSize;

            report.AppendLine($"  尺寸：{area.Width} × {area.Depth} 格，每格 {area.CellSize:F2} 米" +
                              $"（世界占地 {sizeX:F2} × {sizeZ:F2} 米）");
            report.AppendLine($"  世界原点（最小角）：({origin.x:F2}, {origin.y:F2}, {origin.z:F2})");
            report.AppendLine($"  世界范围：X ∈ [{origin.x:F2}, {origin.x + sizeX:F2}]，" +
                              $"Z ∈ [{origin.z:F2}, {origin.z + sizeZ:F2}]");
            report.AppendLine($"  楼层：{area.Floor}　地面高度：{area.GroundHeight:F2}");
            report.AppendLine();

            BuildAreaData data = area.Data;
            report.AppendLine($"  【数据层】地板 {data.FloorCount} 块，墙 {data.WallCount} 面");
            report.AppendLine();

            // ---- 找出存放玩家建造物的节点 ----
            //
            // 它应该在【场景根节点】下，名字取自 BuildController.SpawnRootName
            // （两处都从这里取，避免名字写死两次不一致）。
            Transform spawnRoot = null;

            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                spawnRoot = FindDeep(root.transform, BuildController.SpawnRootName);
                if (spawnRoot != null) break;
            }

            var sceneObjects = new Dictionary<string, GameObject>();

            if (spawnRoot != null)
            {
                foreach (Transform child in spawnRoot)
                {
                    WorldObjectId id = child.GetComponent<WorldObjectId>();
                    if (id != null) sceneObjects[id.ObjectId] = child.gameObject;
                }
            }

            report.AppendLine($"  【场景】「{BuildController.SpawnRootName}」节点下找到 {sceneObjects.Count} 个带 ID 的物体");

            if (spawnRoot == null)
            {
                report.AppendLine("    （没有找到「玩家建造物」节点 —— 说明还没放过任何东西）");
            }
            else if (spawnRoot.parent != null)
            {
                report.AppendLine($"    ★ 「{BuildController.SpawnRootName}」的父节点是「{spawnRoot.parent.name}」——");
                report.AppendLine("      它应该在【场景根节点】下。挂在别处会让建造物跟着那个物体移动/旋转。");
                problems++;
            }

            report.AppendLine();

            // ---- 逐个核对数据层的每一条记录 ----
            List<PlacedPartRecord> records = data.Export();
            int matched = 0;

            foreach (PlacedPartRecord record in records)
            {
                string expectedId = record.IsWall
                    ? $"player/{record.Floor}/wall/{record.Edge.MidX2}_{record.Edge.MidZ2}"
                    : $"player/{record.Floor}/floor/{record.CellX}_{record.CellZ}";

                // 期望的世界位置
                Vector3 expectedPosition = record.IsWall
                    ? area.EdgeCenterWorld(record.Edge)
                    : area.CellCenterWorld(record.CellX, record.CellZ);

                if (!sceneObjects.TryGetValue(expectedId, out GameObject found))
                {
                    report.AppendLine($"  ★ 数据里有、场景里没有：{record}（期望 ID 「{expectedId}」）");
                    report.AppendLine($"      期望位置 ({expectedPosition.x:F2}, {expectedPosition.z:F2})" +
                                      " —— 这条记录拆不掉也看不见，但占着位置");
                    problems++;
                    continue;
                }

                matched++;

                // 位置核对：水平距离应该几乎为 0（Y 上允许有半个高度的差）
                float dx = found.transform.position.x - expectedPosition.x;
                float dz = found.transform.position.z - expectedPosition.z;
                float horizontalError = Mathf.Sqrt(dx * dx + dz * dz);

                if (horizontalError > 0.05f)
                {
                    report.AppendLine($"  ★ 位置对不上：{record}");
                    report.AppendLine($"      数据算出 ({expectedPosition.x:F2}, {expectedPosition.z:F2})，" +
                                      $"实际物体 ({found.transform.position.x:F2}, {found.transform.position.z:F2})" +
                                      $"　偏差 {horizontalError:F3} 米");
                    problems++;
                }

                // 再确认一次这个格子/边在数据层里真的被标记为占用
                bool dataSaysOccupied = record.IsWall
                    ? data.HasWall(record.Edge)
                    : data.HasFloor(record.CellX, record.CellZ);

                if (!dataSaysOccupied)
                {
                    report.AppendLine($"  ★ 数据自相矛盾：记录里有 {record}，但查询说没被占用");
                    problems++;
                }
            }

            // ---- 反向核对：场景里有、数据里没有的野物体 ----
            var expectedIds = new HashSet<string>();
            foreach (PlacedPartRecord record in records)
            {
                expectedIds.Add(record.IsWall
                    ? $"player/{record.Floor}/wall/{record.Edge.MidX2}_{record.Edge.MidZ2}"
                    : $"player/{record.Floor}/floor/{record.CellX}_{record.CellZ}");
            }

            foreach (KeyValuePair<string, GameObject> pair in sceneObjects)
            {
                if (expectedIds.Contains(pair.Key)) continue;

                // 起步地板（种子）是生成器直接建的，它的占用状态靠 PreplacedPart 表达，
                // 而不是靠玩家建造记录 —— 所以要走另一条核对路径，不能当野物体报。
                if (pair.Key == "home/seed_floor")
                {
                    PreplacedPart preplaced = pair.Value.GetComponent<PreplacedPart>();
                    if (preplaced == null)
                    {
                        report.AppendLine($"  ★ 起步地板「{pair.Key}」没有挂 PreplacedPart ——");
                        report.AppendLine("      进 Play 后它的占用状态会丢失（能重叠建、也拆不掉）。");
                        problems++;
                    }
                    else
                    {
                        bool occupied = preplaced.IsWall
                            ? data.HasWall(preplaced.Edge)
                            : data.HasFloor(preplaced.CellX, preplaced.CellZ);

                        report.AppendLine($"  【预置】起步地板 {preplaced}，数据层" +
                                          $"{(occupied ? "已登记" : "★ 未登记")}");

                        if (!occupied)
                        {
                            report.AppendLine("      ★ 预置部件没有被登记进占用数据 ——" +
                                              "BuildController.RegisterPreplacedParts 没跑到，或者它不在本区范围内。");
                            problems++;
                        }
                    }

                    continue;
                }

                report.AppendLine($"  ★ 场景里有、数据里没有：{pair.Key}");
                report.AppendLine($"      位置 ({pair.Value.transform.position.x:F2}, " +
                                  $"{pair.Value.transform.position.z:F2})" +
                                  " —— 能被建上去，但拆除时找不到记录");
                problems++;
            }

            report.AppendLine();
            report.AppendLine($"  汇总：匹配 {matched}/{records.Count} 条记录，" +
                              $"场景物体 {sceneObjects.Count} 个，问题 {problems} 处");

            // ---- 顺便报告控制器状态 ----
            BuildController controller = Object.FindFirstObjectByType<BuildController>();
            if (controller != null)
            {
                report.AppendLine();
                report.AppendLine($"  【控制器】「{controller.name}」" +
                                  $"建造模式={(controller.IsBuilding ? "开" : "关")}，" +
                                  $"当前部件={(controller.SelectedPart != null ? controller.SelectedPart.DisplayName : "（无）")}");
            }
            else
            {
                report.AppendLine();
                report.AppendLine("  ★ 没有找到 BuildController —— 按 B 不会有任何反应");
                problems++;
            }

            report.AppendLine();
            return problems;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            if (parent.name == name) return parent;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindDeep(parent.GetChild(i), name);
                if (found != null) return found;
            }

            return null;
        }
    }
}
#endif
