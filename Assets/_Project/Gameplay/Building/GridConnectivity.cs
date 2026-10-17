// 本文件开启可空引用类型分析：返回值可能为 null（表示"没问题"），用 string? 表达。
// Game.Runtime 程序集默认没开可空上下文，所以这里局部开启，否则会报 CS8632。
#nullable enable

using System.Collections.Generic;

namespace Game.World
{
    /// <summary>
    /// 网格连通性检查（纯逻辑，不依赖 UnityEngine）。
    ///
    /// 【为什么单独抽出来】
    /// 它原本是 GridHouseModel.Validate 里的一段检查，而那段检查的判据是错的
    /// （用"是否邻接边界"代替"是否连通"，两个方向都错）。修完之后必须能被验证 ——
    /// 但 GridHouseModel 用了 UnityEngine（Mathf），进不了 OptimizationReview
    /// 那个独立 .NET 断言工程，等于修了也没法证明修对了。
    ///
    /// 所以把这段逻辑抽成不引用 UnityEngine 的静态方法：
    /// 它能被独立断言直接覆盖，而 GridHouseModel 只负责提供格子查询。
    ///
    /// 【为什么用委托而不是传 char[,]】
    /// 调用方（GridHouseModel）的格子数组是 [x, z] 二维数组、越界返回空地。
    /// 用委托把"取格子"抽象出来，这里就不需要知道数组怎么存、边界怎么处理，
    /// 也方便断言里用各种构造出来的图去测（包括故意不规则的图）。
    /// </summary>
    public static class GridConnectivity
    {
        /// <summary>取某个格子的字符。越界由调用方决定返回什么（通常是"空地"）。</summary>
        public delegate char CellQuery(int x, int z);

        /// <summary>某个字符是否算"可行走"（地板 / 门 / 楼梯）。</summary>
        public delegate bool WalkableQuery(char cell);

        /// <summary>
        /// 检查所有可行走格是否【全部互相连通】。
        ///
        /// 返回问题描述（null 表示没问题）。
        ///
        /// 【判据说明】从第一个可行走格开始四方向洪水填充，
        /// 填不到的可行走格就是"走不过去的孤岛"。
        ///
        /// 为什么是四方向而不是八方向：斜向相邻但中间被墙隔开时，玩家走不过去，
        /// 八方向会把这种情形误判成连通。
        ///
        /// 为什么用显式栈而不是递归：图纸尺寸不可控，递归可能爆栈。
        /// </summary>
        public static string? CheckWalkableConnected(
            int width, int depth, CellQuery getCell, WalkableQuery isWalkable, string label)
        {
            if (width <= 0 || depth <= 0) return null;

            // 找第一个可行走格作为填充起点
            int startX = -1, startZ = -1;
            int walkableTotal = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!isWalkable(getCell(x, z))) continue;
                    walkableTotal++;
                    if (startX < 0) { startX = x; startZ = z; }
                }
            }

            if (startX < 0) return null;          // 没有可行走格，谈不上连通性
            if (walkableTotal == 1) return null;  // 只有一个格，必然是连通的

            // 洪水填充
            var visited = new bool[width * depth];
            var stack = new Stack<(int X, int Z)>();

            stack.Push((startX, startZ));
            visited[startZ * width + startX] = true;
            int reached = 1;

            // 四方向：西、东、南、北
            int[] dx = { -1, 1, 0, 0 };
            int[] dz = { 0, 0, -1, 1 };

            while (stack.Count > 0)
            {
                (int cx, int cz) = stack.Pop();

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int nz = cz + dz[i];

                    if (nx < 0 || nx >= width || nz < 0 || nz >= depth) continue;

                    int index = nz * width + nx;
                    if (visited[index]) continue;
                    if (!isWalkable(getCell(nx, nz))) continue;

                    visited[index] = true;
                    reached++;
                    stack.Push((nx, nz));
                }
            }

            if (reached >= walkableTotal) return null;

            // 把走不到的格子逐个列出来 —— 只说"不连通"帮不上排查
            var disconnected = new List<string>();
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!isWalkable(getCell(x, z))) continue;
                    if (visited[z * width + x]) continue;
                    disconnected.Add($"({x}, {z})");
                }
            }

            return $"「{label}」有 {disconnected.Count} 个可行走格与主体不连通，玩家走不到：" +
                   string.Join("、", disconnected);
        }
    }
}
