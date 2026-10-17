using System.Collections.Generic;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 建筑网格模型：把一个 ASCII 平面图解析成 1×1 的格子数据。
    ///
    /// 【为什么用 ASCII 网格】这是"不许重叠"这一要求最简单的实现方式：
    /// 一个字符只占一个格子，**结构上就不可能重叠**（数组的一个下标只有一个值）。
    /// 相比"给每个部件加碰撞检测来判断重叠"，这种表示法把约束提到了数据层 ——
    /// 写错的图纸在解析阶段就会被发现，而不是等生成出来再检查。
    ///
    /// 字符约定（和 BuildingPart.FromChar 对应）：
    ///   '#' 墙      'o' 地板      'D' 门
    ///   'R' 屋顶    'S' 楼梯      '.' 空地
    ///
    /// 坐标约定：行索引 = Z 轴，列索引 = X 轴，第一行是 z=0。
    /// 因为 ASCII 从上往下读，所以把第一行放在 z 最小的一端。
    /// </summary>
    public struct GridHouseModel
    {
        private readonly char[,] _cells;   // [x, z]
        private readonly int _width;       // X 方向格子数
        private readonly int _depth;       // Z 方向格子数
        private readonly string _name;

        public int Width => _width;
        public int Depth => _depth;
        public string Name => _name;

        public GridHouseModel(string name, string[] layout)
        {
            _name = name;

            _depth = layout != null ? layout.Length : 0;
            _width = 0;

            for (int z = 0; z < _depth; z++)
            {
                if (layout[z].Length > _width) _width = layout[z].Length;
            }

            _cells = new char[Mathf.Max(1, _width), Mathf.Max(1, _depth)];

            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    // 行长度不齐时按空地补齐，避免越界。
                    _cells[x, z] = x < layout[z].Length ? layout[z][x] : BuildingPart.EmptyCell;
                }
            }
        }

        public char GetCell(int x, int z)
        {
            if (x < 0 || x >= _width || z < 0 || z >= _depth) return BuildingPart.EmptyCell;
            return _cells[x, z];
        }

        /// <summary>该格是否有"地面"（地板或楼梯可以站的部分）。</summary>
        public bool IsWalkable(int x, int z)
        {
            return IsWalkableChar(GetCell(x, z));
        }

        /// <summary>
        /// 某个字符是否算"可行走"。
        ///
        /// 【为什么单独抽成一个静态方法】GridConnectivity 需要"字符 → 可行走"这个判断，
        /// 而它是纯逻辑（不依赖 UnityEngine）。把判断放在这里、以静态方法暴露，
        /// 就不会在两边各写一份（两份判断迟早会改歪一个）。
        /// </summary>
        public static bool IsWalkableChar(char c)
        {
            return c == 'o' || c == 'D' || c == 'S';
        }

        /// <summary>该格是否属于建筑（墙、地板、门、楼梯都算；屋顶单独判断）。</summary>
        public bool IsPartOfBuilding(int x, int z)
        {
            return BuildingPart.FromChar(GetCell(x, z)) != null;
        }

        /// <summary>
        /// 解析校验：确认图纸没有明显错误。
        ///
        /// 【判据的修复过程 —— 值得记下来】
        /// 早先这里检查的是"每个地板格是否至少邻接一个边界（墙/门/空地/图纸边缘）"。
        /// 那个判据是错的，而且是【两个方向都错】：
        ///   1. 大房间的中间地板四周全是 'o'，会【误报】"和轮廓断开了" —— 其实它好得很；
        ///   2. 真正孤立的地板如果恰好落在图纸边缘那一圈，
        ///      又因为 `x == 0 || x == _width-1` 恒为真而【漏报】。
        ///
        /// 根本原因是判据选错了：想表达的是【连通性】，却写成了【邻接边界】。
        /// 现在改成真正的连通性检查（洪水填充），顺带补上"门是否通到室内"。
        ///
        /// 检查项：
        ///   1. 布局非空
        ///   2. 至少一个地板格、至少一个门
        ///   3. 所有可行走格（地板/门/楼梯）必须【互相连通】—— 不能有飘在外面的孤岛
        ///   4. 每个门必须至少邻接一个可行走格 —— 否则门开在墙上也没人走得到
        ///
        /// 返回问题列表，空列表表示没问题。
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();

            if (_width <= 0 || _depth <= 0)
            {
                problems.Add($"「{_name}」布局为空。");
                return problems;
            }

            int floorCount = 0;
            int doorCount = 0;

            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    char c = _cells[x, z];
                    if (c == 'o') floorCount++;
                    if (c == 'D') doorCount++;
                }
            }

            if (floorCount == 0) problems.Add($"「{_name}」没有任何地板格（'o'）。");
            if (doorCount == 0) problems.Add($"「{_name}」没有门（'D'），玩家进不去。");

            if (floorCount == 0) return problems;   // 没有地板就谈不上连通性

            // ---- 连通性：交给 GridConnectivity（纯逻辑，可被独立断言覆盖）----
            //
            // 【两个编译约束，都是本工程实际撞到的】
            //   1. 不能传方法组（`GetCell`）—— Unity 的 C# 编译器在这里不接受隐式转换，
            //      必须写成显式 lambda。
            //   2. 本类型是 struct，而【struct 里的 lambda 不能捕获 this】
            //      （报 CS1673）。所以要把字段先复制到局部变量，lambda 只捕获这些局部变量。
            int width = _width;
            int depth = _depth;
            char[,] cells = _cells;
            string label = _name;

            char ReadCell(int x, int z)
            {
                // 越界返回空地 —— 和 GetCell 的行为一致，但这里不通过 this 访问。
                if (cells == null || x < 0 || x >= width || z < 0 || z >= depth)
                {
                    return BuildingPart.EmptyCell;
                }
                return cells[x, z];
            }

            string connectivityProblem = GridConnectivity.CheckWalkableConnected(
                width, depth,
                ReadCell,
                IsWalkableChar,
                label);

            if (connectivityProblem != null) problems.Add(connectivityProblem);

            // ---- 每个门必须邻接至少一个可行走格 ----
            foreach ((int X, int Z) door in EnumerateDoors())
            {
                bool adjacentToWalkable =
                    IsWalkable(door.X - 1, door.Z) || IsWalkable(door.X + 1, door.Z) ||
                    IsWalkable(door.X, door.Z - 1) || IsWalkable(door.X, door.Z + 1);

                if (!adjacentToWalkable)
                {
                    problems.Add($"「{_name}」门 ({door.X}, {door.Z}) 四周没有可行走格，这道门通不到室内。");
                }
            }

            return problems;
        }

        /// <summary>
        /// 枚举所有【需要立墙的格子边】。
        ///
        /// 规则：一个格子是"可站立"（地板/门/楼梯），而它某个方向的邻居不是"可站立"
        /// （空地、墙、或者图纸外），那这条边就要有墙。
        ///
        /// 这样墙是【从地板形状反推出来的】—— 图纸只需要画地板和门，
        /// 墙自动包在周围，不会出现"墙和地板对不上"或"两段墙重叠"。
        /// </summary>
        public IEnumerable<(int X, int Z, int DirX, int DirZ)> EnumerateWallEdges()
        {
            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (!IsWalkable(x, z)) continue;

                    if (!IsWalkable(x, z - 1)) yield return (x, z, 0, -1);
                    if (!IsWalkable(x, z + 1)) yield return (x, z, 0, 1);
                    if (!IsWalkable(x - 1, z)) yield return (x, z, -1, 0);
                    if (!IsWalkable(x + 1, z)) yield return (x, z, 1, 0);
                }
            }
        }

        /// <summary>枚举所有门格。</summary>
        public IEnumerable<(int X, int Z)> EnumerateDoors()
        {
            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (_cells[x, z] == 'D') yield return (x, z);
                }
            }
        }

        /// <summary>枚举所有楼梯格。</summary>
        public IEnumerable<(int X, int Z)> EnumerateStairs()
        {
            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (_cells[x, z] == 'S') yield return (x, z);
                }
            }
        }

        public void GetBounds(out int minX, out int maxX, out int minZ, out int maxZ)
        {
            minX = int.MaxValue; maxX = int.MinValue;
            minZ = int.MaxValue; maxZ = int.MinValue;

            for (int z = 0; z < _depth; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (!IsPartOfBuilding(x, z)) continue;

                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
            }

            if (minX > maxX) { minX = 0; maxX = 0; minZ = 0; maxZ = 0; }
        }
    }
}
