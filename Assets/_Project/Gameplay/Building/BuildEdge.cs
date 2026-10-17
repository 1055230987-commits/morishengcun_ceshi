// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System;

namespace Game.World
{
    /// <summary>一条边沿哪个轴延伸。</summary>
    public enum EdgeAxis
    {
        /// <summary>边沿 X 轴延伸，墙把"Z 方向相邻的两格"隔开（南北向的墙）。</summary>
        AlongX = 0,

        /// <summary>边沿 Z 轴延伸，墙把"X 方向相邻的两格"隔开（东西向的墙）。</summary>
        AlongZ = 1,
    }

    /// <summary>
    /// 一个格子的方向（四邻）。
    ///
    /// 【为什么用枚举而不是 (dx, dz) 两个 int】方向只有四个有效值，
    /// 用两个 int 表达就允许 (-1,-1) 这种斜向的非法组合，还得在每处判断。
    /// 枚举把非法状态排除掉了。
    /// </summary>
    public enum CellDirection
    {
        /// <summary>X 减一（西）。</summary>
        NegX = 0,

        /// <summary>X 加一（东）。</summary>
        PosX = 1,

        /// <summary>Z 减一（南）。</summary>
        NegZ = 2,

        /// <summary>Z 加一（北）。</summary>
        PosZ = 3,
    }

    /// <summary>
    /// 建造网格上的"一条边"（墙所在的位置）。
    ///
    /// 【为什么需要这个类型 —— 这是个真实的数据模型缺口】
    /// 墙的几何位置在【格子边界】上，不在格子中心。而 BuildingPart 只记录了
    /// (X, Z, Floor) + 类型，没有记录"在哪条边上"。
    ///
    /// 这会导致一个具体问题：一面物理上的墙，从它两侧的格子看会得到两个不同的
    /// (X, Z)。例如格 (0,0) 的北边墙和格 (0,1) 的南边墙是【同一面墙】，
    /// 但按格子坐标记下来是 (0,0) 和 (0,1) 两条不同记录 ——
    /// 占用校验会以为它们不冲突，而几何上它们完全重叠。
    ///
    /// 本类型用【端点中点】唯一标识一条边，从哪一侧算都得到同一个键。
    ///
    /// 【编码方式：坐标 ×2 + 奇偶编码轴向】
    /// 一条边由"位于哪条网格线"和"沿哪个轴延伸"唯一确定。设格子 (x, z) 的中心
    /// 在整数坐标上，则网格线在整数上、格子中心在 .5 上。把坐标乘 2 之后：
    ///   格子 (x, z) 的中心       → (2x+1, 2z+1)   两个都是奇数
    ///   边的中点                  → 一个奇数、一个偶数
    /// 于是"哪个是奇数"就编码了轴向，不需要额外的字段去校验一致性：
    ///   沿 X 延伸的边（南北墙）→ (2x+1, 2z)      X 奇、Z 偶
    ///   沿 Z 延伸的边（东西墙）→ (2x,   2z+1)    X 偶、Z 奇
    /// 这样 Axis 属性是从坐标【推导】出来的，不可能和坐标矛盾。
    /// </summary>
    [Serializable]
    public readonly struct BuildEdge : IEquatable<BuildEdge>
    {
        /// <summary>边中点的 X 坐标 ×2。</summary>
        public readonly int MidX2;

        /// <summary>边中点的 Z 坐标 ×2。</summary>
        public readonly int MidZ2;

        /// <summary>所在楼层。-1 = 地下室，0 = 一楼，1 = 二楼……</summary>
        public readonly int Floor;

        public BuildEdge(int midX2, int midZ2, int floor)
        {
            MidX2 = midX2;
            MidZ2 = midZ2;
            Floor = floor;
        }

        /// <summary>边沿哪个轴延伸。由坐标奇偶推导，不会和坐标矛盾。</summary>
        public EdgeAxis Axis => (MidX2 & 1) == 1 ? EdgeAxis.AlongX : EdgeAxis.AlongZ;

        /// <summary>
        /// 这个编码是否描述了一条【真实存在的边】。
        ///
        /// 【为什么需要这个检查】编码约定是"一个坐标奇数、另一个偶数"：
        ///   沿 X 的边 → X 奇、Z 偶
        ///   沿 Z 的边 → X 偶、Z 奇
        /// 而 (0, 0) 这种"两个都是偶数"的坐标，中点落在【格子中心】而不是网格线上 ——
        /// 它不对应任何一条边。
        ///
        /// 早先 IsEdgeInBounds 只检查范围，于是 (0,0) 能被当成合法边登记进去
        /// （会被记成一条沿 Z 的边，中点在 X=0 —— 那条线其实是 x=0.5，错位的）。
        /// 手写的存档、外部工具生成的记录都可能带进这种值，所以必须校验。
        /// </summary>
        public bool IsValid => ((MidX2 ^ MidZ2) & 1) == 1;

        // ---------------------------------------------------------------- 构造

        /// <summary>
        /// 由"格子 + 方向"得到边。这是最常用的入口。
        ///
        /// 例：格 (3, 5) 的 PosZ 方向（北边）→ 沿 X 的边，中点 (3.5, 6) → 键 (7, 12)
        /// </summary>
        public static BuildEdge FromCell(int cellX, int cellZ, int floor, CellDirection direction)
        {
            switch (direction)
            {
                // 西边：格子中心 (x+.5, z+.5) 往 -X 走半格 → 中点 (x, z+.5)
                case CellDirection.NegX:
                    return new BuildEdge(2 * cellX, 2 * cellZ + 1, floor);

                // 东边：中点 (x+1, z+.5)
                case CellDirection.PosX:
                    return new BuildEdge(2 * cellX + 2, 2 * cellZ + 1, floor);

                // 南边：中点 (x+.5, z)
                case CellDirection.NegZ:
                    return new BuildEdge(2 * cellX + 1, 2 * cellZ, floor);

                // 北边：中点 (x+.5, z+1)
                default:
                    return new BuildEdge(2 * cellX + 1, 2 * cellZ + 2, floor);
            }
        }

        // ---------------------------------------------------------------- 反查

        /// <summary>
        /// 这条边的【低坐标一侧】的格子（沿边的法线方向取坐标较小的那一侧）。
        ///
        /// 【为什么要"低坐标一侧"】一条边有两个相邻格，用哪个当代表会让键不唯一。
        /// 固定取低坐标那一侧，就得到规范表示 —— 任何一侧算出来都一致。
        /// </summary>
        public void GetLowSideCell(out int cellX, out int cellZ)
        {
            if (Axis == EdgeAxis.AlongX)
            {
                // 中点 (x+.5, z)，法线沿 Z：低侧是 z-1
                cellX = (MidX2 - 1) / 2;
                cellZ = MidZ2 / 2 - 1;
            }
            else
            {
                // 中点 (x, z+.5)，法线沿 X：低侧是 x-1
                cellX = MidX2 / 2 - 1;
                cellZ = (MidZ2 - 1) / 2;
            }
        }

        /// <summary>这条边的【高坐标一侧】的格子。</summary>
        public void GetHighSideCell(out int cellX, out int cellZ)
        {
            GetLowSideCell(out int lowX, out int lowZ);

            if (Axis == EdgeAxis.AlongX) { cellX = lowX; cellZ = lowZ + 1; }
            else { cellX = lowX + 1; cellZ = lowZ; }
        }

        /// <summary>
        /// 某个格子是否【紧邻】这条边（在它的四个方向之一上）。
        /// 用于判断"这面墙是不是贴着我这格"。
        /// </summary>
        public bool IsAdjacentToCell(int cellX, int cellZ, int floor)
        {
            if (Floor != floor) return false;

            GetLowSideCell(out int lowX, out int lowZ);
            GetHighSideCell(out int highX, out int highZ);

            return (cellX == lowX && cellZ == lowZ) || (cellX == highX && cellZ == highZ);
        }

        /// <summary>
        /// 从某个格子看这条边，它在哪个方向。不相邻时返回 null。
        /// </summary>
        public CellDirection? DirectionFromCell(int cellX, int cellZ, int floor)
        {
            if (Floor != floor) return null;

            GetLowSideCell(out int lowX, out int lowZ);
            GetHighSideCell(out int highX, out int highZ);

            if (cellX == lowX && cellZ == lowZ)
            {
                // 我在低侧，边在高侧方向
                return Axis == EdgeAxis.AlongX ? CellDirection.PosZ : CellDirection.PosX;
            }

            if (cellX == highX && cellZ == highZ)
            {
                return Axis == EdgeAxis.AlongX ? CellDirection.NegZ : CellDirection.NegX;
            }

            return null;
        }

        // ---------------------------------------------------------------- 相等性

        public bool Equals(BuildEdge other)
            => MidX2 == other.MidX2 && MidZ2 == other.MidZ2 && Floor == other.Floor;

        public override bool Equals(object? obj) => obj is BuildEdge other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MidX2;
                hash = (hash * 397) ^ MidZ2;
                hash = (hash * 397) ^ Floor;
                return hash;
            }
        }

        public override string ToString()
            => $"[边 {Axis} 中点({MidX2 / 2f:F1}, {MidZ2 / 2f:F1}) 层{Floor}]";

        /// <summary>把方向转成 (dx, dz)。斜向不存在，所以返回的必是四个正交方向之一。</summary>
        public static void ToDelta(CellDirection direction, out int dx, out int dz)
        {
            switch (direction)
            {
                case CellDirection.NegX: dx = -1; dz = 0; break;
                case CellDirection.PosX: dx = 1; dz = 0; break;
                case CellDirection.NegZ: dx = 0; dz = -1; break;
                default: dx = 0; dz = 1; break;
            }
        }

        /// <summary>相反方向。</summary>
        public static CellDirection Opposite(CellDirection direction)
        {
            switch (direction)
            {
                case CellDirection.NegX: return CellDirection.PosX;
                case CellDirection.PosX: return CellDirection.NegX;
                case CellDirection.NegZ: return CellDirection.PosZ;
                default: return CellDirection.NegZ;
            }
        }
    }
}
