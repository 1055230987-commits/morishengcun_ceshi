// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System;
using System.Collections.Generic;

namespace Game.World
{
    /// <summary>放置被拒绝的原因。用枚举而不是字符串，是为了让它可被断言、且界面能自己翻译。</summary>
    public enum PlaceRejectReason
    {
        /// <summary>可以放置。</summary>
        None = 0,

        /// <summary>超出建造区范围。</summary>
        OutOfBounds,

        /// <summary>这个格子已经有地板了。</summary>
        CellOccupied,

        /// <summary>这条边已经有墙了。</summary>
        EdgeOccupied,

        /// <summary>这条边两侧都没有地板 —— 墙必须贴着已建好的地板。</summary>
        NoAdjacentFloor,

        /// <summary>地板下面没有支撑（悬空）。</summary>
        NoSupport,

        /// <summary>材料不足。</summary>
        NotEnoughMaterial,

        /// <summary>被场景里的其它东西挡住（树、石头、房屋……）。</summary>
        BlockedByWorld,

        /// <summary>没有选中部件（配置问题，不是玩家的错）。</summary>
        NotConfigured,
    }

    /// <summary>一个放置判定的结果。</summary>
    public readonly struct PlaceCheck
    {
        public readonly PlaceRejectReason Reason;

        /// <summary>失败时的补充说明（例如"还差 2 个木材"）。成功时为 null。</summary>
        public readonly string? Detail;

        public bool Ok => Reason == PlaceRejectReason.None;

        private PlaceCheck(PlaceRejectReason reason, string? detail)
        {
            Reason = reason;
            Detail = detail;
        }

        public static readonly PlaceCheck Allowed = new PlaceCheck(PlaceRejectReason.None, null);

        public static PlaceCheck Reject(PlaceRejectReason reason, string? detail = null)
            => new PlaceCheck(reason, detail);

        public override string ToString() => Ok ? "可放置" : $"不可放置：{Reason}{(Detail != null ? "（" + Detail + "）" : "")}";
    }

    /// <summary>
    /// 建造区的占用数据（纯逻辑，不依赖 UnityEngine —— 所以能被独立断言直接覆盖）。
    ///
    /// 【核心设计：地板占"格"，墙占"边"】
    ///   - 地板是 1×1 的面，占满一个格子 → 用 HashSet&lt;BuildCell&gt;
    ///   - 墙贴在格子边界上，占一条边 → 用 HashSet&lt;BuildEdge&gt;
    ///
    /// 【为什么墙不能也按格子记】
    /// 一面物理墙，从它两侧的格子看会得到两个不同的 (X, Z)。
    /// 若按格子记，占用校验会以为"两格各有一面墙、不冲突"，
    /// 而几何上它们是同一面墙、完全重叠。
    /// 用 BuildEdge 做键之后，两侧算出的是同一个键，重复放置必然被拒绝。
    ///
    /// 【这个类型保证什么、不保证什么】
    /// 保证：
    ///   - 同一个格子不会被放两次地板
    ///   - 同一条边不会被放两次墙（无论从哪一侧放）
    ///   - 地板和墙互不干扰（一个格子的四条边都能立墙，不影响格子本身）
    ///   - 不同楼层互不干扰
    /// 不保证（这些必须在别处检查，见 PlaceCheck 的额外检查）：
    ///   - 几何上不穿进树/石头/已有房屋 → 需要 Physics 查询，纯逻辑做不到
    ///   - 地板下面有支撑 → 需要查地形
    ///   - 相邻格子的模型不互相穿插 → 取决于模型尺寸，不是网格数据能表达的
    /// </summary>
    public sealed class BuildAreaData
    {
        /// <summary>一个格子。地板占它。</summary>
        public readonly struct BuildCell : IEquatable<BuildCell>
        {
            public readonly int X;
            public readonly int Z;
            public readonly int Floor;

            public BuildCell(int x, int z, int floor)
            {
                X = x;
                Z = z;
                Floor = floor;
            }

            public bool Equals(BuildCell other) => X == other.X && Z == other.Z && Floor == other.Floor;
            public override bool Equals(object? obj) => obj is BuildCell other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = X;
                    hash = (hash * 397) ^ Z;
                    hash = (hash * 397) ^ Floor;
                    return hash;
                }
            }

            public override string ToString() => $"({X}, {Z}, 层{Floor})";
        }

        private readonly HashSet<BuildCell> _floors = new HashSet<BuildCell>();
        private readonly HashSet<BuildEdge> _walls = new HashSet<BuildEdge>();

        /// <summary>
        /// 格子 → 已放置部件的标识（存档用）。
        ///
        /// 【为什么要这张表】存档不能只存 GameObject —— 那会把场景结构写进存档，
        /// 换版本就废。存"格子 + 部件标识"才能在加载时重新生成。
        /// </summary>
        private readonly Dictionary<BuildCell, string> _floorIds = new Dictionary<BuildCell, string>();
        private readonly Dictionary<BuildEdge, string> _wallIds = new Dictionary<BuildEdge, string>();

        /// <summary>建造区的格子数与原点（用于范围判断）。</summary>
        public int Width { get; }

        public int Depth { get; }

        /// <summary>本建造区所属楼层。</summary>
        public int Floor { get; }

        /// <summary>
        /// 格子边长（米）。只用于把格子坐标换算成"局部平面坐标"。
        ///
        /// 【为什么这也放在数据层】坐标映射是纯算术，不放这里就得在 MonoBehaviour 里
        /// 再写一遍，两处迟早会改歪一个。这里只算【局部平面坐标】，
        /// 不涉及 UnityEngine.Vector3、也不涉及世界原点的高度 —— 那些留在组件层。
        /// </summary>
        public float CellSize { get; }

        public BuildAreaData(int width, int depth, int floor, float cellSize = 1f)
        {
            Width = Math.Max(1, width);
            Depth = Math.Max(1, depth);
            Floor = floor;
            CellSize = cellSize > 0.001f ? cellSize : 1f;
        }

        // ---------------------------------------------------------------- 坐标映射（纯算术）

        /// <summary>
        /// 格子的中心在【建造区局部坐标】里的 X。局部原点 = 区域最小角。
        /// </summary>
        public float CellCenterLocalX(int x) => (x + 0.5f) * CellSize;

        /// <summary>格子的中心在建造区局部坐标里的 Z。</summary>
        public float CellCenterLocalZ(int z) => (z + 0.5f) * CellSize;

        /// <summary>
        /// 由【局部坐标】反推格子。落在区域外时返回 false。
        ///
        /// 用 FloorToInt 而不是 Round：格子 [0,1) 属于 x=0，
        /// [1,2) 属于 x=1 —— 和"边界归左边那一格"的直觉一致。
        /// </summary>
        public bool LocalToCell(float localX, float localZ, out int x, out int z)
        {
            x = (int)Math.Floor(localX / CellSize);
            z = (int)Math.Floor(localZ / CellSize);

            return ContainsCell(x, z);
        }

        /// <summary>
        /// 边的中点在【建造区局部坐标】里的位置。
        /// 由 MidX2/MidZ2 除以 2 再乘格子边长得到。
        /// </summary>
        public void EdgeCenterLocal(BuildEdge edge, out float localX, out float localZ)
        {
            localX = edge.MidX2 * 0.5f * CellSize;
            localZ = edge.MidZ2 * 0.5f * CellSize;
        }

        /// <summary>
        /// 某条边沿哪个轴延伸（转发到 BuildEdge，方便调用方少导一个类型）。
        /// </summary>
        public static EdgeAxis AxisOf(BuildEdge edge) => edge.Axis;

        /// <summary>
        /// 由局部坐标找出【最近的一条边】。
        ///
        /// 【吸附规则：取离得最近的那条网格线，而不是"先判断在哪一格再取四条边"】
        /// 用"把局部坐标除以格子边长，看它离整数（网格线）有多近"来判定，
        /// 横向与纵向哪个更近就用哪个方向。
        /// 这样拖动鼠标时预览不会跳来跳去。
        ///
        /// 【越界必须【整个拒绝】，不能把格索引夹回范围 —— 这是一个真实的缺陷修复】
        /// 早先的写法是把越界的格索引强行 clamp 到 [0, Depth-1]，结果：
        /// 鼠标远在区域之外（例如局部坐标 (1, -100)）时，仍然会"吸"到区域南端那条墙边，
        /// 预览跑到玩家完全没指的地方。
        ///
        /// 现在的规则是：
        ///   - 指针本身在区域外（含外扩一个格子的容差）→ 直接返回 false，不吸附
        ///   - 指针在区域内 → 正常吸附到最近的网格线
        /// 容差的意义：站在区域边缘时鼠标常常刚好越过一个像素，
        /// 严格判定会让边缘那一圈墙很难放。
        /// </summary>
        public bool LocalToNearestEdge(float localX, float localZ, out BuildEdge edge)
        {
            edge = default;

            // ---- 先做范围拒绝 ----
            //
            // 容差取"一个格子"：允许指针略微越界（贴边放外墙时很常见），
            // 但绝不能允许"远在天边也吸回来"。
            float tolerance = CellSize;
            float sizeX = Width * CellSize;
            float sizeZ = Depth * CellSize;

            if (localX < -tolerance || localX > sizeX + tolerance) return false;
            if (localZ < -tolerance || localZ > sizeZ + tolerance) return false;

            // 换算成"第几条网格线"的小数形式
            float gx = localX / CellSize;
            float gz = localZ / CellSize;

            float nearestGridX = (float)Math.Round(gx);
            float nearestGridZ = (float)Math.Round(gz);

            float distToGridX = Math.Abs(gx - nearestGridX);
            float distToGridZ = Math.Abs(gz - nearestGridZ);

            // 离哪条网格线更近，就立哪个方向的墙：
            //   离 X 网格线近 → 墙沿 Z 延伸（东西向的墙，法线沿 X）
            //   离 Z 网格线近 → 墙沿 X 延伸（南北向的墙，法线沿 Z）
            if (distToGridX <= distToGridZ)
            {
                int lineX = (int)nearestGridX;

                // 网格线必须在 [0, Width] 之间（外墙就在 0 和 Width 上）
                if (lineX < 0 || lineX > Width) return false;

                // 【这里仍然要 clamp，但含义不同了】指针可能落在相邻格（容差内），
                // 此时把"沿边方向的格索引"收进区域，得到的是【区域边缘那条边】——
                // 这是想要的行为。指针【远】在区域外的情况已经被上面的范围检查挡掉了。
                int cellZ = (int)Math.Floor(gz);
                if (cellZ < 0) cellZ = 0;
                if (cellZ >= Depth) cellZ = Depth - 1;

                edge = new BuildEdge(2 * lineX, 2 * cellZ + 1, Floor);
                return IsEdgeInBounds(edge);
            }
            else
            {
                int lineZ = (int)nearestGridZ;

                if (lineZ < 0 || lineZ > Depth) return false;

                int cellX = (int)Math.Floor(gx);
                if (cellX < 0) cellX = 0;
                if (cellX >= Width) cellX = Width - 1;

                edge = new BuildEdge(2 * cellX + 1, 2 * lineZ, Floor);
                return IsEdgeInBounds(edge);
            }
        }

        public int FloorCount => _floors.Count;
        public int WallCount => _walls.Count;

        // ---------------------------------------------------------------- 范围

        /// <summary>格子是否在建造区内。格子坐标是【从 0 开始】的局部坐标。</summary>
        public bool ContainsCell(int x, int z)
            => x >= 0 && x < Width && z >= 0 && z < Depth;

        // ---------------------------------------------------------------- 查询

        public bool HasFloor(int x, int z)
            => _floors.Contains(new BuildCell(x, z, Floor));

        public bool HasWall(BuildEdge edge)
            => edge.Floor == Floor && _walls.Contains(edge);

        public bool HasWall(int cellX, int cellZ, CellDirection direction)
            => HasWall(BuildEdge.FromCell(cellX, cellZ, Floor, direction));

        /// <summary>取某个格子已放置部件的标识（没有则返回 null）。</summary>
        public string? GetFloorId(int x, int z)
            => _floorIds.TryGetValue(new BuildCell(x, z, Floor), out string? id) ? id : null;

        /// <summary>取某条边已放置部件的标识（没有则返回 null）。</summary>
        public string? GetWallId(BuildEdge edge)
            => _wallIds.TryGetValue(edge, out string? id) ? id : null;

        // ---------------------------------------------------------------- 合法性

        /// <summary>
        /// 检查能否在某个格子放地板。
        ///
        /// 只做【数据层】能判断的部分：范围、是否已有地板。
        /// 支撑、场景阻挡、材料这些要由调用方补充（见 BuildController）。
        /// </summary>
        public PlaceCheck CanPlaceFloor(int x, int z)
        {
            if (!ContainsCell(x, z))
                return PlaceCheck.Reject(PlaceRejectReason.OutOfBounds, $"({x}, {z}) 不在建造区内");

            if (HasFloor(x, z))
                return PlaceCheck.Reject(PlaceRejectReason.CellOccupied, $"({x}, {z}) 已经有地板了");

            return PlaceCheck.Allowed;
        }

        /// <summary>
        /// 检查能否放一面墙。
        ///
        /// 【规则：墙必须贴着至少一块地板】
        /// 一条边只有两个相邻格（见 <see cref="BuildEdge.GetLowSideCell"/> /
        /// <see cref="BuildEdge.GetHighSideCell"/>），"贴着地板"就是【任一侧有地板】。
        ///
        /// 为什么这条规则是合理的：
        ///   - 围墙：一侧是室内地板、另一侧是空地 → 通过（这正是围墙该在的位置）
        ///   - 隔断墙：两侧都是地板 → 通过
        ///   - 荒野里凭空立一面孤墙：两侧都没地板 → 拒绝
        /// 所以它既不妨碍正常的建造，又挡住了"到处乱插墙"。
        ///
        /// 【和建房子时的先后顺序】这条规则自然要求"先铺地板、再立墙"，
        /// 也隐含了"第一块地板必须靠地形支撑"（地板那条规则在 BuildController 里查）。
        /// </summary>
        public PlaceCheck CanPlaceWall(BuildEdge edge)
        {
            if (edge.Floor != Floor)
                return PlaceCheck.Reject(PlaceRejectReason.OutOfBounds, "楼层不匹配");

            if (!IsEdgeInBounds(edge))
                return PlaceCheck.Reject(PlaceRejectReason.OutOfBounds, $"{edge} 不在建造区边界上");

            if (_walls.Contains(edge))
                return PlaceCheck.Reject(PlaceRejectReason.EdgeOccupied, $"{edge} 已经有墙了");

            if (!HasFloorOnEitherSide(edge))
            {
                return PlaceCheck.Reject(PlaceRejectReason.NoAdjacentFloor,
                    "这里两侧都没有地板 —— 墙要先有地板才能立");
            }

            return PlaceCheck.Allowed;
        }

        /// <summary>
        /// 这条边的两侧（两个相邻格）里，是否至少有一侧有地板。
        ///
        /// 【为什么要单独抽一个方法】它同时被合法性检查和界面提示用；
        /// 分开写两份的话，以后改规则（例如改成"两侧都要有地板"）容易改漏一处。
        /// </summary>
        public bool HasFloorOnEitherSide(BuildEdge edge)
        {
            if (edge.Floor != Floor) return false;

            edge.GetLowSideCell(out int lowX, out int lowZ);
            edge.GetHighSideCell(out int highX, out int highZ);

            return HasFloor(lowX, lowZ) || HasFloor(highX, highZ);
        }

        /// <summary>
        /// 这条边是否是【合法且落在建造区内】的边。
        ///
        /// 两个条件都要满足：
        ///   1. 编码本身有效（一个坐标奇数、一个偶数 —— 中点在网格线上而不是格子中心）
        ///   2. 中点在 [0, 2*Width] × [0, 2*Depth] 之间。
        ///      墙在网格线上，所以边界线上的墙（例如 x=0 那条西外墙）是合法的 ——
        ///      那正是围墙该在的位置。
        ///
        /// 【第 1 条是后补的】早先只查范围，于是 (0,0) 这种"两个坐标都是偶数"的
        /// 非法编码也能被登记成一条边。手写存档、外部工具都可能带进这种值。
        /// </summary>
        public bool IsEdgeInBounds(BuildEdge edge)
        {
            if (!edge.IsValid) return false;
            if (edge.Floor != Floor) return false;
            if (edge.MidX2 < 0 || edge.MidX2 > 2 * Width) return false;
            if (edge.MidZ2 < 0 || edge.MidZ2 > 2 * Depth) return false;
            return true;
        }

        // ---------------------------------------------------------------- 放置 / 拆除

        /// <summary>
        /// 登记一个地板。调用方【必须先】调 CanPlaceFloor 并通过。
        /// 返回 false 表示数据层拒绝（重复或越界）—— 这是防御性的，正常流程不该走到。
        /// </summary>
        public bool RegisterFloor(int x, int z, string partId)
        {
            if (!CanPlaceFloor(x, z).Ok) return false;

            var cell = new BuildCell(x, z, Floor);
            _floors.Add(cell);
            _floorIds[cell] = partId;
            return true;
        }

        /// <summary>登记一面墙。调用方必须先调 CanPlaceWall 并通过。</summary>
        public bool RegisterWall(BuildEdge edge, string partId)
        {
            if (!CanPlaceWall(edge).Ok) return false;

            _walls.Add(edge);
            _wallIds[edge] = partId;
            return true;
        }

        /// <summary>
        /// 拆掉一个地板。返回被拆部件的标识（没有则返回 null）。
        ///
        /// 【不做的事】不检查"这个地板上还有没有墙" —— 那是业务规则，
        /// 由 BuildController 决定（本工程的选择：先拆墙再拆地板，避免留下悬空墙）。
        /// </summary>
        public string? RemoveFloor(int x, int z)
        {
            var cell = new BuildCell(x, z, Floor);
            if (!_floors.Remove(cell)) return null;

            _floorIds.TryGetValue(cell, out string? id);
            _floorIds.Remove(cell);
            return id ?? string.Empty;
        }

        /// <summary>拆掉一面墙。返回被拆部件的标识（没有则返回 null）。</summary>
        public string? RemoveWall(BuildEdge edge)
        {
            if (!_walls.Remove(edge)) return null;

            _wallIds.TryGetValue(edge, out string? id);
            _wallIds.Remove(edge);
            return id ?? string.Empty;
        }

        /// <summary>
        /// 某个格子上是否【挂着墙】（四条边里任意一条有墙）。
        /// 用于"拆地板前先检查有没有墙"这类业务规则。
        /// </summary>
        public bool CellHasAnyWall(int x, int z)
        {
            return HasWall(x, z, CellDirection.NegX)
                || HasWall(x, z, CellDirection.PosX)
                || HasWall(x, z, CellDirection.NegZ)
                || HasWall(x, z, CellDirection.PosZ);
        }

        /// <summary>列出某个格子上挂着的所有墙（用于拆除时一并清理或给出提示）。</summary>
        public List<BuildEdge> GetWallsAroundCell(int x, int z)
        {
            var result = new List<BuildEdge>();
            var directions = new[]
            {
                CellDirection.NegX, CellDirection.PosX, CellDirection.NegZ, CellDirection.PosZ
            };

            foreach (CellDirection dir in directions)
            {
                BuildEdge edge = BuildEdge.FromCell(x, z, Floor, dir);
                if (_walls.Contains(edge)) result.Add(edge);
            }

            return result;
        }

        /// <summary>
        /// 导出全部已放置部件（存档用）。
        ///
        /// 【为什么只导出"格子/边 + 标识"】存档不该写场景结构。
        /// 加载时按这些坐标重新生成，才能跨版本、跨地图偏移使用。
        /// </summary>
        public List<PlacedPartRecord> Export()
        {
            var records = new List<PlacedPartRecord>(_floors.Count + _walls.Count);

            foreach (KeyValuePair<BuildCell, string> pair in _floorIds)
            {
                records.Add(new PlacedPartRecord(
                    isWall: false,
                    cellX: pair.Key.X, cellZ: pair.Key.Z, floor: pair.Key.Floor,
                    edge: default,
                    partId: pair.Value));
            }

            foreach (KeyValuePair<BuildEdge, string> pair in _wallIds)
            {
                records.Add(new PlacedPartRecord(
                    isWall: true,
                    cellX: 0, cellZ: 0, floor: pair.Key.Floor,
                    edge: pair.Key,
                    partId: pair.Value));
            }

            return records;
        }

        /// <summary>从记录恢复（加载存档用）。返回成功导入的数量。</summary>
        public int Import(IEnumerable<PlacedPartRecord> records)
        {
            int imported = 0;

            foreach (PlacedPartRecord record in records)
            {
                // 【必须校验记录的楼层】
                //
                // 早先这里直接调 RegisterFloor，而 RegisterFloor 用的是【本区自己的】
                // Floor 字段构造 BuildCell —— 于是"楼层 99 的地板记录"会被静默地
                // 当成"本区楼层的地板"导入进来。存档一旦有脏数据或跨区串档，就会错位。
                //
                // 现在按记录里声明的楼层筛选：不属于本区的直接跳过。
                if (record.Floor != Floor) continue;

                if (record.IsWall)
                {
                    if (!record.Edge.IsValid) continue;
                    if (RegisterWall(record.Edge, record.PartId)) imported++;
                }
                else
                {
                    if (RegisterFloor(record.CellX, record.CellZ, record.PartId)) imported++;
                }
            }

            return imported;
        }

        /// <summary>清空（重置用）。</summary>
        public void Clear()
        {
            _floors.Clear();
            _walls.Clear();
            _floorIds.Clear();
            _wallIds.Clear();
        }
    }

    /// <summary>
    /// 一个已放置部件的记录（存档单元）。
    ///
    /// 【为什么地板和墙共用一个结构】它们的区别只是"占格还是占边"，
    /// 用一个带 IsWall 标志的结构可以让存档格式统一，加载时一个循环处理完。
    /// 用两个列表反而要维护两套序列化逻辑。
    /// </summary>
    [Serializable]
    public readonly struct PlacedPartRecord
    {
        /// <summary>true = 墙（占边），false = 地板（占格）。</summary>
        public readonly bool IsWall;

        /// <summary>地板用：格子的 X。墙忽略此字段。</summary>
        public readonly int CellX;

        /// <summary>地板用：格子的 Z。墙忽略此字段。</summary>
        public readonly int CellZ;

        /// <summary>楼层。</summary>
        public readonly int Floor;

        /// <summary>墙用：所在的边。地板忽略此字段。</summary>
        public readonly BuildEdge Edge;

        /// <summary>部件标识（例如 "floor" / "wall"），加载时按它决定生成什么。</summary>
        public readonly string PartId;

        public PlacedPartRecord(bool isWall, int cellX, int cellZ, int floor, BuildEdge edge, string partId)
        {
            IsWall = isWall;
            CellX = cellX;
            CellZ = cellZ;
            Floor = floor;
            Edge = edge;
            PartId = partId;
        }

        public override string ToString()
            => IsWall ? $"墙 {Edge} ← {PartId}" : $"地板 ({CellX}, {CellZ}, 层{Floor}) ← {PartId}";
    }
}
