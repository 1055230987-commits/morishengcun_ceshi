// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System;

namespace Game.AI
{
    /// <summary>
    /// 敌人的障碍规避与分离（纯逻辑，不依赖 UnityEngine）。
    ///
    /// 【为什么需要它 —— 这不是优化，是修复"敌人撞墙卡住"】
    /// 早先敌人是直线朝目标走：撞到房子外墙、玩家建的围墙、树、石头就会原地顶着，
    /// 表现是"它傻了"。而且三个敌人之间没有任何避让，会挤成一团。
    ///
    /// 【为什么不用 NavMesh —— 更正：这不是"不成立"，是"这一版没做"】
    ///
    /// 早先这里写着"NavMesh 对本游戏不成立，因为玩家能实时建墙"。**这个推论是错的。**
    /// Unity 的 NavMeshObstacle 支持 Carving：它会在已有导航面上动态挖洞，
    /// 让路径规划绕开新出现的障碍，不需要重烘焙整张地图。
    /// 所以"动态建墙"并不是排除 NavMesh 的理由。
    ///
    /// 本版仍然用局部探距，理由是【取舍】而不是【不可能】：
    ///   - 局部探距天生跟着当前碰撞体走，没有烘焙步骤、没有额外资产
    ///   - 与现有 CharacterController 移动、状态机、存档都不冲突
    ///   - 3 个敌人不需要路线规划的性能与内存开销
    ///
    /// 但必须承认它的【能力上限】，这些是 NavMesh 有而它没有的：
    ///   - **没有路线记忆**：只看得见 2.6 米内，绕不过需要"先远离目标"的 L/U 形障碍
    ///   - **没有到达保证**：目标在封闭区域时它会一直贴着墙绕
    ///   - **可能局部振荡**：两个方向的通畅度接近时会来回选
    /// 如果以后敌人要在建筑内部追击、或目标常常隔着墙，
    /// 正确做法是接 NavMesh + NavMeshObstacle(Carving)，而不是继续加固这套探距。
    ///
    /// 【为什么纯逻辑】和 EnemyBrainLogic 同样的理由：
    /// "哪个方向能走"是规则，"用射线量距离"是引擎的事。
    /// 分开之后规则可以被穷举断言，而不用等进 Unity 才知道绕得对不对。
    /// </summary>
    public static class EnemySteering
    {
        /// <summary>
        /// 候选方向相对"期望方向"的偏移角度（度）。
        ///
        /// 【顺序是有意义的】按 |偏移| 升序排列 ——
        /// 选方向的规则是"能直行就直行，否则找偏离最小的通畅方向"，
        /// 所以数组顺序直接表达了这个优先级。
        ///
        /// 【-30 在 +30 之前】纯粹为了确定性：左右同等通畅时固定先试左边，
        /// 这样断言可以写死期望值。玩家看不出差别，但测试需要它确定。
        /// </summary>
        public static readonly float[] CandidateOffsetsDegrees =
        {
            0f,
            -30f, 30f,
            -60f, 60f,
            -90f, 90f,
        };

        /// <summary>候选方向的数量。</summary>
        public const int CandidateCount = 7;

        // ---------------------------------------------------------------- 选方向

        /// <summary>
        /// 从候选里挑一个要走的方向下标。返回 -1 表示没有可用候选（调用方应原地不动）。
        ///
        /// <paramref name="clearances"/> 是各候选方向的通畅距离（米），
        /// 用 <see cref="float.PositiveInfinity"/> 表示完全通畅。
        /// <paramref name="requiredClearance"/> 是"算通畅"的门槛。
        ///
        /// 三级规则：
        ///   1. 直行（下标 0）够通畅 → 直行。绝大多数时候走这条，不会乱扭。
        ///   2. 否则按偏离从小到大，找第一个够通畅的 → 用小角度绕。
        ///   3. 全都不够通畅 → 挑【最通畅】的那个，至少试着往外挤。
        ///
        /// 【为什么第 3 级不返回 -1】原地不动是最糟糕的结果（敌人永远卡住）。
        /// 往最空的方向挪一点，配合卡死检测（<see cref="EnemyStuckDetector"/>）
        /// 总比站着不动更容易脱困。
        /// </summary>
        public static int ChooseCandidate(
            float[] clearances, int candidateCount, float requiredClearance)
        {
            if (clearances == null || candidateCount <= 0) return -1;
            if (candidateCount > clearances.Length) candidateCount = clearances.Length;

            // 1) 直行够通畅 → 直行
            if (IsClear(clearances[0], requiredClearance)) return 0;

            // 2) 找偏离最小的通畅方向
            for (int i = 1; i < candidateCount; i++)
            {
                if (IsClear(clearances[i], requiredClearance)) return i;
            }

            // 3) 全堵 → 挑最通畅的（NaN 安全：比较会失败，保持初始值）
            int best = 0;
            for (int i = 1; i < candidateCount; i++)
            {
                if (clearances[i] > clearances[best]) best = i;
            }

            return best;
        }

        /// <summary>这个距离算不算"通畅"。正无穷天然算通。</summary>
        public static bool IsClear(float clearance, float requiredClearance)
        {
            // NaN 一律当不通畅 —— 否则 NaN 比较全为 false 会让它意外通过/失败，
            // 那种 bug 极难查。这里显式拒绝。
            if (float.IsNaN(clearance)) return false;

            return clearance >= requiredClearance;
        }

        /// <summary>
        /// 把方向 (dirX, dirZ) 绕 Y 轴旋转若干度。
        ///
        /// 用 Unity 的偏航约定（+90° 把 +Z 转成 +X）——
        /// 这样候选偏移的符号和引擎里的 yaw 是同一个方向，不会左右搞反。
        /// </summary>
        public static void RotateDirection(
            float dirX, float dirZ, float degrees, out float outX, out float outZ)
        {
            if (MathF.Abs(degrees) < 1e-4f)
            {
                outX = dirX;
                outZ = dirZ;
                return;
            }

            float radians = degrees * (MathF.PI / 180f);
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);

            outX = dirX * cos + dirZ * sin;
            outZ = -dirX * sin + dirZ * cos;
        }

        /// <summary>
        /// 单位化。长度接近 0 时返回 false，且输出零向量。
        ///
        /// 【为什么不用引擎的 normalize】纯逻辑层要能独立断言，
        /// 而且这里需要"零向量要显式报告"这个语义，而不是静默给个 (0,0)。
        /// </summary>
        public static bool TryNormalize(float x, float z, out float normalizedX, out float normalizedZ)
        {
            float lengthSquared = x * x + z * z;

            if (lengthSquared < 1e-8f || float.IsNaN(lengthSquared))
            {
                normalizedX = 0f;
                normalizedZ = 0f;
                return false;
            }

            float inverse = 1f / MathF.Sqrt(lengthSquared);
            normalizedX = x * inverse;
            normalizedZ = z * inverse;
            return true;
        }

        // ---------------------------------------------------------------- 分离

        /// <summary>
        /// 把"离其它敌人太近"的排斥力叠加到期望方向上。
        ///
        /// 【为什么要做】三个敌人同时追玩家时会挤成一坨：互相穿不过去、
        /// 又都朝同一点走，结果互相顶着原地打转。
        ///
        /// 【为什么用线性衰减而不是 1/d】1/d 在距离趋近 0 时会爆炸，
        /// 导致轻微重叠就被弹飞。线性衰减有界，行为更可预测，也好断言。
        ///
        /// <paramref name="tieAngleRadians"/> 是"两者完全重合时往哪推"的角度。
        ///
        /// 【为什么需要它 —— 这里修过一个会让分离彻底失效的缺陷】
        /// 早先用"邻居数组下标奇偶"决定推开方向。但两个完全重合的敌人，
        /// 各自看到的邻居只有一个、下标都是 0 —— 于是**两边都往同一方向推**，
        /// 永远分不开。现在改成由调用方传入一个【每个敌人不同】的角度，
        /// 两个重合的敌人就会往不同方向散开。
        ///
        /// 【已知限制：只做水平面】上下楼但 X/Z 接近的敌人仍会互相排斥。
        /// 做得更准需要传入高度差，本版没做（楼层内追击目前是主要场景）。
        /// </summary>
        public static void AccumulateSeparation(
            float selfX, float selfZ,
            float[] otherX, float[] otherZ, int otherCount,
            float radius, float strength,
            float tieAngleRadians,
            ref float desiredX, ref float desiredZ)
        {
            if (otherX == null || otherZ == null) return;
            if (radius <= 0f || strength <= 0f) return;

            if (otherCount > otherX.Length) otherCount = otherX.Length;
            if (otherCount > otherZ.Length) otherCount = otherZ.Length;

            float radiusSquared = radius * radius;

            // 完全重合时的推开方向（每个敌人不同，所以一对重合的敌人会散开）
            float tieX = MathF.Cos(tieAngleRadians);
            float tieZ = MathF.Sin(tieAngleRadians);

            for (int i = 0; i < otherCount; i++)
            {
                if (float.IsNaN(otherX[i]) || float.IsNaN(otherZ[i])) continue;

                // 从别人指向自己 —— 这就是"推开"的方向
                float dx = selfX - otherX[i];
                float dz = selfZ - otherZ[i];
                float distanceSquared = dx * dx + dz * dz;

                if (distanceSquared >= radiusSquared) continue;   // 太远，不影响

                // 贴在一起（距离≈0）：方向未定义，用调用方给的角度兜底
                if (distanceSquared < 1e-4f)
                {
                    desiredX += tieX * strength;
                    desiredZ += tieZ * strength;
                    continue;
                }

                float distance = MathF.Sqrt(distanceSquared);
                float falloff = 1f - distance / radius;   // 0（在边缘）→ 1（贴住）
                float push = falloff * strength;

                desiredX += dx / distance * push;
                desiredZ += dz / distance * push;
            }
        }
    }

    /// <summary>
    /// "卡住了吗"的检测 + 绕行方向建议（纯逻辑）。
    ///
    /// 【为什么需要它】局部避障能解决大部分情况，但仍有死角：
    /// 敌人可能被推进一个夹角（房子外墙 + 玩家围墙），
    /// 或者两个敌人互相顶住 —— 这时"选一个最通畅的方向"也还是走不动。
    /// 没有这层兜底，表现就是敌人贴着墙永远抖。
    ///
    /// 【判据：窗口内【净位移】，不是累计路程 —— 这里修过一个会让它失效的缺陷】
    /// 早先统计的是"每帧位移的累加"。那有个致命问题：
    /// **原地来回抖动时累计路程很大，净进展却是 0。**
    /// 实测（每帧来回移动 1 厘米、持续 2 秒）旧判据给出"没卡住"。
    /// 而那恰恰是"贴着墙抖"最典型的表现，等于这个兜底在最需要它的时候不工作。
    ///
    /// 现在改成比较【窗口起点与终点的直线距离】：
    ///   - 正常行走 → 净位移大 → 没卡住
    ///   - 顶着墙 → 净位移 ≈ 0 → 卡住
    ///   - 原地抖动 → 净位移 ≈ 0 → 卡住（旧版漏掉的正是这个）
    ///
    /// 【代价】合法的"绕着小圈走"也会被判成卡住。这是有意的取舍：
    /// 在小圈里打转本身就是没进展，触发一次绕行反而更可能脱困。
    /// </summary>
    public sealed class EnemyStuckDetector
    {
        /// <summary>观察窗口（秒）。太短会误判，太长会卡很久才反应。</summary>
        public float Window { get; }

        /// <summary>窗口起点到终点的净位移小于这个值（米）就算卡住。</summary>
        public float MinTravel { get; }

        private float _windowTimer;
        private float _windowStartX;
        private float _windowStartZ;
        private bool _hasWindowStart;
        private int _detourAlt;

        /// <summary>当前是否判定为卡住。</summary>
        public bool IsStuck { get; private set; }

        /// <summary>已经连续卡住了几个窗口（0 = 没卡）。可用于"卡太久就放弃追击"。</summary>
        public int ConsecutiveStuckWindows { get; private set; }

        /// <summary>上一个窗口的净位移（米）。调试与断言用。</summary>
        public float LastNetTravel { get; private set; }

        public EnemyStuckDetector(float window = 0.7f, float minTravel = 0.15f)
        {
            Window = window <= 0.05f ? 0.05f : window;
            MinTravel = minTravel < 0f ? 0f : minTravel;
        }

        /// <summary>
        /// 推进一帧。
        /// <paramref name="wantsToMove"/> 本帧是否想动。
        /// <paramref name="x"/>/<paramref name="z"/> 当前水平位置（世界坐标）。
        ///
        /// 【为什么收位置而不是"本帧位移"】判据需要"窗口起点到终点"的距离，
        /// 而"每帧位移"的信息不足以还原它 —— 只有位置能算净位移。
        /// </summary>
        public void Update(float deltaTime, bool wantsToMove, float x, float z)
        {
            if (deltaTime < 0f) deltaTime = 0f;

            // 不想动的时候不算卡住 —— 站着待机不是卡住
            if (!wantsToMove)
            {
                ResetWindow();
                IsStuck = false;
                ConsecutiveStuckWindows = 0;
                _hasWindowStart = false;
                return;
            }

            if (!_hasWindowStart)
            {
                _windowStartX = x;
                _windowStartZ = z;
                _hasWindowStart = true;
            }

            _windowTimer += deltaTime;
            if (_windowTimer < Window) return;

            float dx = x - _windowStartX;
            float dz = z - _windowStartZ;

            // 位置是 NaN 时无法判断 —— 当作"没动过"，宁可触发一次绕行
            float netTravel;
            if (float.IsNaN(dx) || float.IsNaN(dz))
            {
                netTravel = 0f;
            }
            else
            {
                netTravel = MathF.Sqrt(dx * dx + dz * dz);
            }

            LastNetTravel = netTravel;

            bool stuckNow = netTravel < MinTravel;

            if (stuckNow)
            {
                if (!IsStuck) ConsecutiveStuckWindows = 0;
                ConsecutiveStuckWindows++;
            }
            else
            {
                ConsecutiveStuckWindows = 0;
            }

            IsStuck = stuckNow;
            ResetWindow();
            _windowStartX = x;
            _windowStartZ = z;
            _hasWindowStart = true;
        }

        /// <summary>
        /// 下一次绕行往哪边转：+1 / -1，交替。
        ///
        /// 【为什么交替】总往同一边绕会在凹角里反复撞同一面墙。
        /// 交替能让第二次尝试换个方向，脱困概率明显更高。
        /// </summary>
        public float NextDetourSign()
        {
            float sign = (_detourAlt & 1) == 0 ? 1f : -1f;
            _detourAlt++;
            return sign;
        }

        /// <summary>复位（复活 / 对象池复用 / 场景重载）。</summary>
        public void Reset()
        {
            ResetWindow();
            IsStuck = false;
            ConsecutiveStuckWindows = 0;
            LastNetTravel = 0f;
            _hasWindowStart = false;
            _detourAlt = 0;
        }

        private void ResetWindow()
        {
            _windowTimer = 0f;
        }
    }
}
