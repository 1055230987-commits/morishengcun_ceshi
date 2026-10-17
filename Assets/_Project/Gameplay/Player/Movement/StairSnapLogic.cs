using System;

namespace Game.Player
{
    /// <summary>
    /// 台阶吸附的【纯决策逻辑】。
    ///
    /// 【为什么刻意不 using UnityEngine】
    /// 真实的吸附要打物理射线，那是 Unity 运行时行为，在 Unity 外部无法验证。
    /// 但"探到的地面距离该不该吸附、吸附多少"是纯数学判断 ——
    /// 只要这个文件不引用 UnityEngine，就能像 DayNightCycle.cs 那样
    /// 被一个独立的 .NET 控制台程序直接编译进去、逐点断言，完全不需要开 Unity。
    ///
    /// 本工程楼梯问题的教训：之前三轮都靠"我按几何算应该能上"来判断，而计算和实际行为
    /// 不符。现在把可计算的判断变成**可执行的测试**。
    /// </summary>
    public static class StairSnapLogic
    {
        /// <summary>吸附方向。</summary>
        public enum SnapKind
        {
            /// <summary>不吸附。</summary>
            None,

            /// <summary>向上吸附（上台阶、上坡）。</summary>
            Up,

            /// <summary>向下吸附（下落时贴近地面）。</summary>
            Down
        }

        /// <summary>一次吸附的完整结果。</summary>
        public readonly struct SnapDecision
        {
            /// <summary>吸附方向。</summary>
            public readonly SnapKind Kind;

            /// <summary>应该把角色沿 Y 移动多少米（正 = 向上）。</summary>
            public readonly float DeltaY;

            public SnapDecision(SnapKind kind, float deltaY)
            {
                Kind = kind;
                DeltaY = deltaY;
            }

            public static readonly SnapDecision None = new SnapDecision(SnapKind.None, 0f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (!(value > min)) return value < min ? min : value;   // NaN 也走这里
            return value > max ? max : value;
        }

        /// <summary>
        /// 把"射线起点到命中点的距离"换算成"命中点相对脚底的有符号垂直距离"。
        ///
        /// 约定：**正值 = 地面在脚上方**（脚陷进去了），负值 = 地面在脚下方（脚悬空）。
        ///
        /// 推导：射线从脚底出发向下打，命中点离脚底 castDistance，
        /// 所以命中点在脚底【下方】castDistance 处 → 有符号距离 = -castDistance。
        ///
        /// 单独抽出来是为了让测试能验证这个换算式 —— 它是整个吸附里最容易写错的一步
        /// （正负号搞错，表现就是"角色被吸到地里"或"永远吸不上台阶"）。
        /// </summary>
        public static float DistanceBelowFeet(float castDistance)
        {
            return -castDistance;
        }

        /// <summary>
        /// 判断该不该【主动迈上台阶】。
        ///
        /// 【为什么要单独做这个】`CharacterController.stepOffset` 的自动抬腿是个黑盒：
        /// 它能否生效取决于内部碰撞解算，失败时完全静默。
        /// 本工程的实际表现是"玩家能走上几级台阶，然后卡住"，
        /// 而日志显示吸附一直在正常把角色贴到台阶面上（探测值只有几厘米）——
        /// 说明【垂直方向没问题，是水平方向推不上去】。
        ///
        /// 所以这里改成显式判定：水平移动被挡住 + 前方有可站的台阶顶面 → 主动抬腿。
        /// 不依赖 stepOffset，失败原因也可见。
        /// </summary>
        /// <param name="intendedDistance">本帧【想要】走的水平距离（米，非负）。</param>
        /// <param name="actualDistance">本帧【实际】走了的水平距离（米，非负）。</param>
        /// <param name="probeFound">向前下方探测是否命中地面。</param>
        /// <param name="stepTopAboveFeet">探测到的地面相对脚底的高度（米，正 = 在脚上方）。</param>
        /// <param name="maxStepUp">允许迈上的最大高度（米，非负）。</param>
        /// <param name="minProgressRatio">实际/想要 低于这个比例就认为"被挡住了"。</param>
        public static bool ShouldStepUp(
            float intendedDistance,
            float actualDistance,
            bool probeFound,
            float stepTopAboveFeet,
            float maxStepUp,
            float minProgressRatio = 0.5f)
        {
            // 本来就没想动 → 不迈步（避免站着不动时乱抬腿）
            if (!IsFinite(intendedDistance) || intendedDistance <= 0.01f) return false;

            // 实际走到了想要的量 → 没被挡住
            if (!IsFinite(actualDistance) || actualDistance >= intendedDistance * minProgressRatio)
                return false;

            // 前方没探到可站的地面 → 是墙，不是台阶
            if (!probeFound) return false;

            if (!IsFinite(stepTopAboveFeet)) return false;

            // 台阶太矮（几毫米）不算一步；太高则迈不上去
            if (stepTopAboveFeet <= 0.005f) return false;
            if (stepTopAboveFeet > Clamp(maxStepUp, 0f, float.MaxValue)) return false;

            return true;
        }

        /// <summary>
        /// 判断该不该吸附，以及吸附多少。
        ///
        /// 【两种状态用不同的距离参数 —— 这是关键】
        ///   贴地（isGrounded）：CharacterController 已经把角色放在地面上，地面距离通常
        ///     接近 0 或略负（脚陷在碰撞体的 skinWidth 里）。此时需要的是"能不能抬上去"
        ///     —— 向上抬、向下压都需要，所以用**有符号距离**。
        ///
        ///   离地（下落中）：角色在往下跌，地面在脚【下方】。此时只需要"吸住地面"，
        ///     用**无符号的距离**（downDistance，非负）。
        ///
        /// 【为什么要有死区 deadZone】否则角色静止贴地时，每帧都会为了几毫米的差值
        /// 反复上下微调 —— 表现就是原地抖动（本工程实际出现过）。
        /// </summary>
        /// <param name="groundDistance">贴地状态下的有符号地面距离（正 = 在脚上方）。</param>
        /// <param name="downDistance">离地状态下的无符号下落距离（非负，脚下多少米有地面）。</param>
        /// <param name="isGrounded">CharacterController 报告的贴地状态。</param>
        /// <param name="canSnapDown">是否还允许向下吸附（离地后有一个时间窗）。</param>
        /// <param name="maxStepUp">允许向上吸附的最大高度（米，非负）。</param>
        /// <param name="maxSnapDown">允许向下吸附的最大深度（米，非负）。</param>
        /// <param name="deadZone">死区（米）。地面距离在这个范围内时不吸附，避免抖动。</param>
        public static SnapDecision Decide(
            float groundDistance,
            float downDistance,
            bool isGrounded,
            bool canSnapDown,
            float maxStepUp,
            float maxSnapDown,
            float deadZone)
        {
            maxStepUp = Clamp(maxStepUp, 0f, float.MaxValue);
            maxSnapDown = Clamp(maxSnapDown, 0f, float.MaxValue);
            deadZone = Clamp(deadZone, 0f, float.MaxValue);

            if (isGrounded)
            {
                // 非有限输入一律不吸附 —— 否则会把角色送到 NaN 位置。
                if (!IsFinite(groundDistance)) return SnapDecision.None;

                // 死区：差得很少就不动，避免静止时抖动。
                if (Abs(groundDistance) <= deadZone) return SnapDecision.None;

                if (groundDistance > 0f)
                {
                    // 地面在脚上方（脚陷进去了，或前面有台阶顶面）→ 抬上去。
                    return groundDistance <= maxStepUp
                        ? new SnapDecision(SnapKind.Up, groundDistance)
                        : SnapDecision.None;
                }

                // 地面在脚下方 → 压下去贴住（走下小台阶、小下坡）。
                return -groundDistance <= maxSnapDown
                    ? new SnapDecision(SnapKind.Down, groundDistance)
                    : SnapDecision.None;
            }

            // 下落中：用无符号下落距离。
            if (!canSnapDown) return SnapDecision.None;
            if (!IsFinite(downDistance) || downDistance < 0f) return SnapDecision.None;

            // 死区同上（下落时地面正好在脚下时不要抖）。
            if (downDistance <= deadZone) return SnapDecision.None;

            return downDistance <= maxSnapDown
                ? new SnapDecision(SnapKind.Down, -downDistance)
                : SnapDecision.None;
        }

        private static float Abs(float v) => v < 0f ? -v : v;

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
