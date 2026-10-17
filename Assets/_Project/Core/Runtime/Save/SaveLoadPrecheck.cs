// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System.Collections.Generic;

namespace Game.Save
{
    /// <summary>预检发现的一类问题。</summary>
    public enum LoadIssueKind
    {
        /// <summary>存档版本和当前不一致。</summary>
        VersionMismatch = 0,

        /// <summary>存档来自别的地图。</summary>
        MapMismatch = 1,

        /// <summary>找不到某个物品的定义。</summary>
        UnknownItem = 2,

        /// <summary>找不到某个建造区的定义。</summary>
        UnknownArea = 3,

        /// <summary>找不到某个建造部件的定义。</summary>
        UnknownPart = 4,

        /// <summary>当前平台不支持按 ID 查物品（打包后没有注册表）。</summary>
        ItemLookupUnsupported = 5,

        /// <summary>建造区标识重复。</summary>
        DuplicateArea = 6,

        /// <summary>存档数据本身是空的或损坏的。</summary>
        Corrupt = 7,
    }

    /// <summary>预检发现的一个具体问题。</summary>
    public readonly struct LoadIssue
    {
        public readonly LoadIssueKind Kind;
        public readonly string Detail;

        /// <summary>是否足以拒绝这次加载。</summary>
        public readonly bool IsBlocking;

        public LoadIssue(LoadIssueKind kind, string detail, bool isBlocking)
        {
            Kind = kind;
            Detail = detail;
            IsBlocking = isBlocking;
        }

        public override string ToString() => $"{(IsBlocking ? "【阻断】" : "【警告】")}{Detail}";
    }

    /// <summary>预检结果。</summary>
    public sealed class LoadPrecheck
    {
        public readonly List<LoadIssue> Issues = new List<LoadIssue>();

        /// <summary>能不能安全地应用这份存档。</summary>
        public bool CanProceed
        {
            get
            {
                foreach (LoadIssue issue in Issues)
                {
                    if (issue.IsBlocking) return false;
                }
                return true;
            }
        }

        /// <summary>有没有问题（含非阻断的警告）。</summary>
        public bool HasAnyIssue => Issues.Count > 0;

        public void Add(LoadIssueKind kind, string detail, bool blocking)
            => Issues.Add(new LoadIssue(kind, detail, blocking));

        /// <summary>把问题拼成一段可读文本。</summary>
        public string Describe(string separator = "\n  ")
        {
            if (Issues.Count == 0) return "没有问题";

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < Issues.Count; i++)
            {
                if (i > 0) builder.Append(separator);
                builder.Append(Issues[i]);
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// 读档前的【纯逻辑】预检。
    ///
    /// 【为什么必须要有这个 —— 这是一个真实的缺陷修复】
    /// 早先 Apply() 的顺序是"先清空背包与建造物，再逐个恢复，找不到就跳过"。
    /// 那意味着：**存档一旦有任何一个引用了不存在的东西，
    /// 玩家会丢掉当前的背包和建筑，而且 Load() 还返回成功。**
    /// 这是最糟的一类 bug —— 数据丢了，界面上还说"读取成功"。
    ///
    /// 现在的约定：**任何阻断性问题 → 完全不动当前状态**。
    /// 宁可"读不了"，也不能"读了之后比不读更糟"。
    ///
    /// 【为什么做成纯逻辑】这样"哪些情况该拒绝"可以被断言覆盖，
    /// 而不是等玩家真的丢了存档才发现。
    /// </summary>
    public static class SaveLoadPrecheck
    {
        /// <summary>查物品定义。找不到返回 null。打包后可能整体不可用。</summary>
        public delegate bool ItemResolver(string itemId);

        /// <summary>查建造区是否存在。返回该区的楼层（不存在返回 null）。</summary>
        public delegate bool AreaResolver(string areaId);

        /// <summary>查某个建造区里是否有这个部件定义。</summary>
        public delegate bool PartResolver(string areaId, string partId);

        /// <summary>本平台是否支持按 ID 查物品。</summary>
        public delegate bool PlatformSupportsItemLookup();

        /// <summary>
        /// 跑一次预检。
        ///
        /// <paramref name="allowVersionMismatch"/> 为 false 时（默认），
        /// 版本不一致直接阻断 —— 结构可能已经变了，硬读会写进错误的状态。
        /// </summary>
        public static LoadPrecheck Run(
            GameSaveData? data,
            string? currentMapId,
            ItemResolver hasItem,
            AreaResolver hasArea,
            PartResolver hasPart,
            PlatformSupportsItemLookup platformSupportsItemLookup,
            bool allowVersionMismatch = false)
        {
            var result = new LoadPrecheck();

            if (data == null)
            {
                result.Add(LoadIssueKind.Corrupt, "存档数据为空", blocking: true);
                return result;
            }

            // ---- 版本 ----
            if (data.Version != GameSaveData.CurrentVersion)
            {
                result.Add(
                    LoadIssueKind.VersionMismatch,
                    $"存档版本 {data.Version}，当前 {GameSaveData.CurrentVersion}" +
                    (allowVersionMismatch ? "（已允许强行读取，可能出错）" : "，已拒绝"),
                    blocking: !allowVersionMismatch);
            }

            // ---- 地图 ----
            //
            // 【为什么地图不匹配只警告不阻断】开发时经常换场景测试，
            // 硬拒绝会让"在同一份存档上试不同地图"变得很麻烦。
            // 位置可能不对，但建造区是按 AreaId 归属的，不会串。
            if (!string.IsNullOrEmpty(data.MapId) &&
                !string.IsNullOrEmpty(currentMapId) &&
                data.MapId != currentMapId)
            {
                result.Add(LoadIssueKind.MapMismatch,
                    $"存档来自地图「{data.MapId}」，当前是「{currentMapId}」，位置可能不对",
                    blocking: false);
            }

            // ---- 物品 ----
            //
            // 【打包后的情况单独处理】打包后不能查 AssetDatabase，
            // 如果不查就继续，会把背包清空然后一个都放不回来。
            // 那比"拒绝加载"糟得多，所以这里直接阻断。
            bool anyItems = (data.Backpack != null && data.Backpack.Count > 0)
                         || (data.Pocket != null && data.Pocket.Count > 0);

            if (anyItems && !platformSupportsItemLookup())
            {
                result.Add(LoadIssueKind.ItemLookupUnsupported,
                    "存档里有物品，但当前平台不支持按 ID 查物品定义（打包后需要物品注册表）。" +
                    "继续会清空背包且放不回任何东西，因此拒绝加载",
                    blocking: true);
            }
            else if (anyItems)
            {
                var reported = new HashSet<string>();
                CheckItems(data.Backpack, "背包", hasItem, result, reported);
                CheckItems(data.Pocket, "口袋", hasItem, result, reported);
            }

            // ---- 建造区 ----
            var seenAreas = new HashSet<string>();

            if (data.PlacedParts != null)
            {
                foreach (PlacedPartSaveData part in data.PlacedParts)
                {
                    if (part == null) continue;

                    if (!seenAreas.Contains(part.AreaId))
                    {
                        seenAreas.Add(part.AreaId);

                        if (!hasArea(part.AreaId))
                        {
                            result.Add(LoadIssueKind.UnknownArea,
                                $"找不到建造区「{part.AreaId}」，它下面的建造物无法恢复",
                                blocking: true);
                        }
                    }

                    if (string.IsNullOrEmpty(part.PartId))
                    {
                        result.Add(LoadIssueKind.UnknownPart,
                            $"有一条建造物记录没写部件标识（{part}）", blocking: true);
                        continue;
                    }

                    if (!hasPart(part.AreaId, part.PartId))
                    {
                        result.Add(LoadIssueKind.UnknownPart,
                            $"建造区「{part.AreaId}」里没有部件「{part.PartId}」，无法生成",
                            blocking: true);
                    }
                }
            }

            // ---- 楼层一致性（只警告）----
            //
            // 建造物记录的楼层和它所属建造区的楼层不一致时，
            // 它会被 Import 的楼层校验拦下（静默跳过）。那不算致命，
            // 但必须让玩家知道"有些东西没恢复"。
            if (data.PlacedParts != null && data.PlacedParts.Count > 0)
            {
                int mismatched = 0;
                foreach (PlacedPartSaveData part in data.PlacedParts)
                {
                    if (part == null) continue;
                    if (!hasArea(part.AreaId)) continue;

                    // 这里不知道区的楼层数，只能用"负数或过大"做粗筛；
                    // 精确的楼层校验在 BuildAreaData.Import 里（它会跳过不符的）。
                    if (part.Floor < -8 || part.Floor > 8) mismatched++;
                }

                if (mismatched > 0)
                {
                    result.Add(LoadIssueKind.Corrupt,
                        $"有 {mismatched} 条建造物记录的楼层看起来不合理", blocking: false);
                }
            }

            return result;
        }

        private static void CheckItems(
            List<ItemSaveData>? items, string containerName,
            ItemResolver hasItem, LoadPrecheck result, HashSet<string> reported)
        {
            if (items == null) return;

            foreach (ItemSaveData item in items)
            {
                if (item == null) continue;

                if (string.IsNullOrEmpty(item.ItemId))
                {
                    result.Add(LoadIssueKind.UnknownItem,
                        $"{containerName}里有一条记录没写物品 ID", blocking: true);
                    continue;
                }

                if (reported.Contains(item.ItemId)) continue;

                if (!hasItem(item.ItemId))
                {
                    reported.Add(item.ItemId);
                    result.Add(LoadIssueKind.UnknownItem,
                        $"{containerName}里的物品「{item.ItemId}」找不到定义（物品资产被改名或删除了？）",
                        blocking: true);
                }
            }
        }
    }
}
