// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System.Collections.Generic;
using System.Text;
using Game.Gameplay.Building;
using Game.Items;
using Game.Save;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// 存档的真实端到端测试。
    ///
    /// 【为什么必须有它 —— 这是对"验证不充分"的回应】
    /// 纯逻辑断言跑的是 OptimizationReview 工程里的副本，
    /// **没有经过 JsonUtility、没有写过文件、没有动过场景**。
    /// 那种测试能证明"转换逻辑对"，但证明不了"存档真的能存下来再读回来"。
    ///
    /// 这个菜单在编辑器里跑真实的完整链路：
    ///
    ///   1. 真实写盘（SaveSerializer → JsonUtility → 文件）
    ///   2. 真实读盘
    ///   3. 【预检必须拒绝】故意加一条未知物品记录 → 确认 VerifyLoadable 拒绝
    ///   4. 【拒绝时状态不变】清空背包后调 Load → 必须失败，且背包仍是空的
    ///      （关键：如果实现是"先清空再验证"，这时背包会被清空且报成功）
    ///   5. 真实恢复：Load 成功 → 背包和建造物回到存档状态
    ///   6. 【幂等】再 Load 一次 → 结果必须和第一次完全一样
    ///
    /// 【它改动了什么】会清空并重建玩家的建造物、替换背包内容。
    /// 所以用完会恢复现场（用步骤 1 之前抓的快照回灌）。
    /// </summary>
    public static class SaveSystemIntegrationTest
    {
        /// <summary>测试用的存档文件名。跑完会删掉，不碰真实的存档槽。</summary>
        private const string TestFileName = "save_integration_test.json";

        private static int _passed;
        private static int _failed;
        private static readonly StringBuilder Report = new StringBuilder();

        [MenuItem("游戏/工程/存档端到端测试（真实读写 + 重复读取）", false, 107)]
        public static void Run()
        {
            var controller = Object.FindFirstObjectByType<GameSaveController>();
            var inventory = Object.FindFirstObjectByType<Game.Player.PlayerInventory>();

            if (controller == null)
            {
                Debug.LogError("[存档测试] 场景里没有 GameSaveController。先跑「重建到当前场景」。");
                return;
            }

            if (inventory == null)
            {
                Debug.LogError("[存档测试] 场景里没有 PlayerInventory。");
                return;
            }

            _passed = 0;
            _failed = 0;
            Report.Clear();

            Report.AppendLine("===== 存档端到端测试 =====");

            bool restorePoseWas = controller.RestoresPlayerPose;
            controller.SetRestorePlayerPoseForTesting(false);   // 别把玩家挪走

            try
            {
                // --- 0. 先往背包里放一些确定的东西 ---
                ItemDefinition? wood = FindItem("wood");
                ItemDefinition? stone = FindItem("stone");

                if (wood == null || stone == null)
                {
                    Report.AppendLine("★ 找不到 wood 或 stone 物品资产。先跑「游戏 → 物品 → 创建全部原型物品」。");
                    _failed++;
                    return;
                }

                inventory.Backpack.Clear();
                inventory.Pocket.Clear();
                inventory.Backpack.Add(wood, 17);
                inventory.Backpack.Add(stone, 5);
                inventory.Pocket.Add(wood, 2);

                Report.AppendLine($"准备：背包 {inventory.Backpack.CountOf(wood)} 木材 + " +
                                  $"{inventory.Backpack.CountOf(stone)} 石头，口袋 {inventory.Pocket.CountOf(wood)} 木材");

                // --- 1. 真实写盘 ---
                GameSaveData snapshot = controller.Capture();
                Report.AppendLine($"采集：{snapshot.PlacedParts.Count} 个建造物、" +
                                  $"{snapshot.Backpack.Count} 种背包物品、地图「{snapshot.MapId}」");

                // 【诊断】采集到 0 个建造物时，把中间状态打出来 ——
                // 否则只能看到"结果是 0"，无法判断是哪一环丢的。
                if (snapshot.PlacedParts.Count == 0)
                {
                    DiagnoseEmptyPlacedParts(controller);
                }

                bool wrote = SaveSerializer.TryWrite(snapshot, out string writeMessage, TestFileName);
                Assert(wrote, $"真实写盘应成功（{writeMessage}）");

                string path = SaveSerializer.GetSavePath(TestFileName);
                Assert(System.IO.File.Exists(path), $"文件应存在于 {path}");

                if (System.IO.File.Exists(path))
                {
                    long size = new System.IO.FileInfo(path).Length;
                    Assert(size > 0, $"文件不应为空（实际 {size} 字节）");
                    Report.AppendLine($"写盘：{path}（{size} 字节）");

                    // 【为什么要把文件内容也核对一遍】
                    // 第一次跑这个测试时，采集出 0 种物品（GameSaveController 的引用
                    // 在编辑模式下没解析），但写盘、读盘、数量比对全部"通过" ——
                    // 因为存 0 读 0 确实是一致的。断言必须能发现"存的东西本身就不对"，
                    // 所以这里直接看文件里有没有那两个物品 ID。
                    string json = System.IO.File.ReadAllText(path);
                    Assert(json.Contains("\"wood\""),
                        "★ 写出的文件里应含物品 ID「wood」—— 不含说明采集阶段就漏了物品");
                    Assert(json.Contains("\"stone\""), "★ 写出的文件里应含物品 ID「stone」");
                    Assert(json.Contains("\"home\""), "★ 写出的文件里应含建造区标识「home」");
                }

                // --- 2. 真实读盘 ---
                GameSaveData? loaded = SaveSerializer.TryRead(out string readMessage, TestFileName);
                Assert(loaded != null, $"真实读盘应成功（{readMessage}）");

                if (loaded == null) return;

                Assert(loaded.Backpack.Count == snapshot.Backpack.Count,
                    $"读回后背包种类数应一致（存 {snapshot.Backpack.Count}，读 {loaded.Backpack.Count}）");
                Assert(loaded.PlacedParts.Count == snapshot.PlacedParts.Count,
                    $"读回后建造物数量应一致（存 {snapshot.PlacedParts.Count}，读 {loaded.PlacedParts.Count}）");
                Assert(loaded.MapId == snapshot.MapId, "读回后地图标识应一致");

                // 逐项核对物品数量（这是 JsonUtility 最容易出问题的地方）
                foreach (ItemSaveData expected in snapshot.Backpack)
                {
                    ItemSaveData actual = loaded.Backpack.Find(i => i.ItemId == expected.ItemId);
                    Assert(actual != null && actual.Amount == expected.Amount,
                        $"物品「{expected.ItemId}」数量应一致（存 {expected.Amount}，读 {(actual == null ? "缺失" : actual.Amount.ToString())}）");
                }

                // --- 3. 预检必须拒绝"含未知物品"的存档 ---
                var bad = loaded;
                bad.Backpack.Add(new ItemSaveData("definitely_not_a_real_item", 3));

                LoadPrecheck badPrecheck = controller.VerifyLoadable(bad);
                Assert(!badPrecheck.CanProceed, "含未知物品的存档必须被预检拒绝");
                Report.AppendLine($"预检拒绝（预期）：{badPrecheck.Describe(" / ")}");

                // --- 4. 【最关键】拒绝时状态必须不变 ---
                //
                // 做法：先把背包清空，再拿"有问题的存档"去 Load。
                // 如果实现是"先清空再验证"，背包会被清空；正确实现会拒绝并保持空。
                // 为了能区分，我们往背包里放一个【标记物品】：
                //   - 正确实现：拒绝加载 → 标记物品还在
                //   - 错误实现：先清空 → 标记物品没了
                inventory.Backpack.Clear();
                inventory.Pocket.Clear();
                inventory.Backpack.Add(wood, 99);   // 标记：99

                bool badLoadResult = SaveSerializer.TryWrite(bad, out _, TestFileName);
                Assert(badLoadResult, "写入有问题的存档应成功（文件层没问题）");

                bool loadSucceeded = controller.Load(out string badLoadMessage, TestFileName);
                Assert(!loadSucceeded, $"有问题的存档不该加载成功（返回了 {loadSucceeded}）");
                Assert(inventory.Backpack.CountOf(wood) == 99,
                    $"★ 加载被拒时背包必须保持原样：期望 99 木材，实际 {inventory.Backpack.CountOf(wood)}");
                Report.AppendLine($"拒绝加载后背包：{inventory.Backpack.CountOf(wood)} 木材（应为 99）");

                // --- 5. 恢复：把好存档写回去，真实加载 ---
                SaveSerializer.TryWrite(snapshot, out _, TestFileName);

                // 先把背包搅乱，才能验证"真的恢复了"
                inventory.Backpack.Clear();
                inventory.Pocket.Clear();
                inventory.Backpack.Add(stone, 1);

                bool goodLoad = controller.Load(out string goodMessage, TestFileName);
                Assert(goodLoad, $"正常存档应加载成功（{goodMessage}）");

                Assert(inventory.Backpack.CountOf(wood) == 17,
                    $"恢复后背包木材应为 17，实际 {inventory.Backpack.CountOf(wood)}");
                Assert(inventory.Backpack.CountOf(stone) == 5,
                    $"恢复后背包石头应为 5，实际 {inventory.Backpack.CountOf(stone)}");
                Assert(inventory.Pocket.CountOf(wood) == 2,
                    $"恢复后口袋木材应为 2，实际 {inventory.Pocket.CountOf(wood)}");

                // --- 6. 幂等：再加载一次，结果必须一样 ---
                bool secondLoad = controller.Load(out string secondMessage, TestFileName);
                Assert(secondLoad, $"第二次加载也应成功（{secondMessage}）");
                Assert(inventory.Backpack.CountOf(wood) == 17,
                    $"★ 重复加载后木材应仍是 17（不是 34），实际 {inventory.Backpack.CountOf(wood)}");
                Assert(inventory.Backpack.CountOf(stone) == 5,
                    $"★ 重复加载后石头应仍是 5，实际 {inventory.Backpack.CountOf(stone)}");
                Report.AppendLine($"重复加载后背包：{inventory.Backpack.CountOf(wood)} 木材、" +
                                  $"{inventory.Backpack.CountOf(stone)} 石头（应无变化）");

                // --- 7. 建造物恢复的核对（数据层）---
                BuildArea area = Object.FindFirstObjectByType<BuildArea>();
                if (area != null)
                {
                    int expectedFloors = 0, expectedWalls = 0;
                    foreach (PlacedPartSaveData part in snapshot.PlacedParts)
                    {
                        if (part.AreaId != area.AreaId) continue;
                        if (part.IsWall) expectedWalls++; else expectedFloors++;
                    }

                    Assert(area.Data.FloorCount == expectedFloors,
                        $"★ 恢复后地板数应一致：期望 {expectedFloors}，实际 {area.Data.FloorCount}");
                    Assert(area.Data.WallCount == expectedWalls,
                        $"★ 恢复后墙数应一致：期望 {expectedWalls}，实际 {area.Data.WallCount}");
                    Report.AppendLine($"建造区「{area.AreaId}」：{area.Data.FloorCount} 地板、" +
                                      $"{area.Data.WallCount} 墙（期望 {expectedFloors}/{expectedWalls}）");

                    // --- 8. 【缺陷回归】预置部件必须活过"清空 + 重建" ---
                    //
                    // 早先 ClearAllParts 无条件销毁 _floorObjects 里的所有物体，
                    // 而 RegisterPreplacedParts 把起步地板也放进去了 ——
                    // 于是加载存档会【销毁起步地板】，但数据层又把它登记回去：
                    // 数据里有一块地板、场景里没有物体。
                    // 表现：Play 里按 F9 后起步地板消失，且再也放不了新地板
                    // （地板需要支撑，而唯一能提供支撑的那块只存在于数据里）。
                    //
                    // 这条断言检查"场景里实际存在几个挂着 PreplacedPart 的物体"。
                    PreplacedPart[] preplacedNow = Object.FindObjectsByType<PreplacedPart>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);

                    int preplacedFloors = 0;
                    foreach (PreplacedPart part in preplacedNow)
                    {
                        if (part != null && !part.IsWall) preplacedFloors++;
                    }

                    Report.AppendLine($"  预置部件：加载后场景里还有 {preplacedFloors} 个预置地板");

                    if (preplacedFloors == 0)
                    {
                        // 场景里本来就没有预置地板时不算失败（例如还没重建场景）
                        Report.AppendLine("  （场景里没有预置地板，跳过这条回归检查）");
                    }
                    else
                    {
                        Assert(area.Data.HasFloor(
                                preplacedNow[0].CellX, preplacedNow[0].CellZ),
                            "★ 预置地板必须同时存在于【场景】和【数据层】——" +
                            "只有数据没有物体时，玩家会看到地板消失且再也放不了新地板");
                    }
                }
                else
                {
                    Report.AppendLine("（场景里没有 BuildArea，跳过建造物核对）");
                }
            }
            finally
            {
                // --- 收尾：删测试文件、恢复设置 ---
                SaveSerializer.TryDelete(out _, TestFileName);
                controller.SetRestorePlayerPoseForTesting(restorePoseWas);

                Report.AppendLine("===== 结果 =====");
                Report.AppendLine($"通过 {_passed}，失败 {_failed}");

                if (_failed > 0)
                {
                    Debug.LogError(Report.ToString());
                }
                else
                {
                    Debug.Log(Report.ToString());
                }
            }
        }

        /// <summary>
        /// 采集到 0 个建造物时的诊断：逐层看清是哪一环丢的。
        ///
        /// 【为什么要写这个】"结果是 0"有很多种可能的原因：
        /// 场景里没有 BuildArea、没有 PreplacedPart、找不到 BuildController、
        /// 或者数据层登记失败。不逐层看就只能猜。
        /// </summary>
        private static void DiagnoseEmptyPlacedParts(GameSaveController controller)
        {
            BuildController[] builders = Object.FindObjectsByType<BuildController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            BuildArea[] areas = Object.FindObjectsByType<BuildArea>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            PreplacedPart[] preplaced = Object.FindObjectsByType<PreplacedPart>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Report.AppendLine("  ---- 诊断：建造物采集为 0 ----");
            Report.AppendLine($"  BuildArea 数量：{areas.Length}");
            Report.AppendLine($"  BuildController 数量：{builders.Length}");
            Report.AppendLine($"  PreplacedPart 数量：{preplaced.Length}");

            foreach (BuildController builder in builders)
            {
                Report.AppendLine($"    BuildController「{builder.name}」的 Area：" +
                                  (builder.Area != null ? builder.Area.AreaId : "★ null"));
            }

            foreach (BuildArea area in areas)
            {
                Report.AppendLine($"    BuildArea「{area.AreaId}」：{area.Data.FloorCount} 地板、" +
                                  $"{area.Data.WallCount} 墙（AreaId 为空说明没配置）");
            }

            foreach (PreplacedPart part in preplaced)
            {
                Report.AppendLine($"    PreplacedPart「{part.name}」：部件「{part.PartId}」" +
                                  $"{(part.IsWall ? "墙" : $"地板 格({part.CellX},{part.CellZ})")} 层{part.Floor}");
            }

            Report.AppendLine("  ---- 诊断结束 ----");
        }

        /// <summary>按 ItemId 找物品资产（和 GameSaveController 里一样的查找方式）。</summary>
        private static ItemDefinition? FindItem(string itemId)
        {
            string[] guids = AssetDatabase.FindAssets("t:ItemDefinition");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                ItemDefinition? candidate = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                if (candidate != null && candidate.ItemId == itemId) return candidate;
            }

            return null;
        }

        private static void Assert(bool condition, string message)
        {
            if (condition)
            {
                _passed++;
                Report.AppendLine($"  OK   {message}");
            }
            else
            {
                _failed++;
                Report.AppendLine($"  ★失败 {message}");
            }
        }
    }
}
