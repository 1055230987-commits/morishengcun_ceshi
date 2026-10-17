// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System;

namespace Game.AI
{
    /// <summary>敌人的行为状态。</summary>
    public enum EnemyState
    {
        /// <summary>原地待机。计时结束后转巡逻。</summary>
        Idle = 0,

        /// <summary>沿路点巡逻。</summary>
        Patrol = 1,

        /// <summary>发现玩家，正在接近。</summary>
        Chase = 2,

        /// <summary>已经贴近，正在攻击。</summary>
        Attack = 3,

        /// <summary>已倒下。终态，不会再变。</summary>
        Dead = 4,
    }

    /// <summary>
    /// 一次决策需要的外部信息（由 MonoBehaviour 采集后喂进来）。
    ///
    /// 【为什么打包成一个结构而不是一堆参数】
    /// 决策函数的参数会越加越多（血量、距离、视线、路点……），
    /// 一堆位置参数很容易传错顺序。用一个结构，字段有名字，加字段也不破坏调用点。
    /// </summary>
    public readonly struct EnemySenses
    {
        /// <summary>敌人到玩家的距离（米）。没有玩家时给正无穷。</summary>
        public readonly float DistanceToTarget;

        /// <summary>玩家是否在【视野锥】内（距离 + 角度都满足）。</summary>
        public readonly bool TargetInSight;

        /// <summary>是否已经贴近到可以攻击的距离。</summary>
        public readonly float AttackRange;

        /// <summary>是否还有巡逻路点可走。</summary>
        public readonly bool HasPatrolPoints;

        /// <summary>自己是否已经死亡。</summary>
        public readonly bool IsDead;

        /// <summary>是否已经到达当前巡逻点（用于推进到下一个路点）。</summary>
        public readonly bool ReachedPatrolPoint;

        public EnemySenses(
            float distanceToTarget,
            bool targetInSight,
            float attackRange,
            bool hasPatrolPoints,
            bool isDead,
            bool reachedPatrolPoint)
        {
            DistanceToTarget = distanceToTarget;
            TargetInSight = targetInSight;
            AttackRange = attackRange;
            HasPatrolPoints = hasPatrolPoints;
            IsDead = isDead;
            ReachedPatrolPoint = reachedPatrolPoint;
        }
    }

    /// <summary>
    /// 敌人行为的状态机（纯逻辑，不依赖 UnityEngine）。
    ///
    /// 【为什么单独抽出来】和 StairSnapLogic / Health 同样的理由：
    /// 状态转换是"什么时候切、切成什么"的规则，可以完全脱离引擎验证；
    /// 而移动、转向、感知检测那些必须在 Unity 里跑的部分留在 MonoBehaviour。
    ///
    /// 【状态图】
    ///   Idle   --待机超时--> Patrol
    ///   Patrol --看见玩家--> Chase
    ///   Patrol --走到路点--> Patrol（换下一个路点）
    ///   Chase  --进入攻击距离--> Attack
    ///   Chase  --丢失目标且超时--> Idle
    ///   Attack --离开攻击距离--> Chase
    ///   任何状态 --死亡--> Dead（优先级最高）
    ///
    /// 【本类不持有任何"计时"状态】计时由调用方按 deltaTime 推进后传进来
    /// （通过 Advance 方法）。这样它保持无副作用、可重复调用、易断言。
    /// </summary>
    public sealed class EnemyBrainLogic
    {
        /// <summary>当前状态。</summary>
        public EnemyState State { get; private set; } = EnemyState.Idle;

        /// <summary>上一次状态。</summary>
        public EnemyState PreviousState { get; private set; } = EnemyState.Idle;

        /// <summary>在当前状态里待了多久（秒）。</summary>
        public float TimeInState { get; private set; }

        /// <summary>距离最后一次"看见"玩家过了多久（秒）。</summary>
        public float TimeSinceLastSeen { get; private set; } = float.MaxValue;

        /// <summary>待机多长时间后开始巡逻。</summary>
        public float IdleDuration { get; }

        /// <summary>失去目标后追多久才放弃。</summary>
        public float LoseTargetGrace { get; }

        /// <summary>
        /// 构造。
        ///
        /// 【LoseTargetGrace 的作用】玩家短暂绕到墙后或跑出视野时不该立刻放弃 ——
        /// 那会让敌人贴着墙角来回切状态（看起来很傻）。
        /// 给一个"宽限期"：这段时间内即使看不到也继续追。
        /// </summary>
        public EnemyBrainLogic(float idleDuration = 2f, float loseTargetGrace = 3f)
        {
            IdleDuration = MathF.Max(0f, idleDuration);
            LoseTargetGrace = MathF.Max(0f, loseTargetGrace);
        }

        /// <summary>状态是否发生了变化（调用方据此更新表现）。</summary>
        public bool StateChanged { get; private set; }

        /// <summary>本帧是否应该发起一次攻击（由调用方消费）。</summary>
        public bool WantsToAttack { get; private set; }

        // ---------------------------------------------------------------- 推进

        /// <summary>
        /// 推进一帧。返回当前状态。
        ///
        /// 【为什么把 deltaTime 作为参数而不是读 Time.deltaTime】
        /// 读全局时间会让这个类无法在测试里精确控制时间推进。
        /// 传进来之后，断言可以"推 0.1 秒 × 100 次"来验证超时行为。
        /// </summary>
        public EnemyState Advance(float deltaTime, EnemySenses senses)
        {
            StateChanged = false;
            WantsToAttack = false;

            if (deltaTime < 0f) deltaTime = 0f;

            TimeInState += deltaTime;

            // 【死亡优先级最高】任何情况下血量归零都直接进 Dead，且不再离开。
            if (senses.IsDead)
            {
                if (State != EnemyState.Dead) TransitionTo(EnemyState.Dead);
                return State;
            }

            // 已经在 Dead 里就不该被复活（除非外部显式 Reset）
            if (State == EnemyState.Dead) return State;

            // 记录"多久没看见"
            if (senses.TargetInSight) TimeSinceLastSeen = 0f;
            else TimeSinceLastSeen += deltaTime;

            switch (State)
            {
                case EnemyState.Idle:
                    UpdateIdle(senses);
                    break;

                case EnemyState.Patrol:
                    UpdatePatrol(senses);
                    break;

                case EnemyState.Chase:
                    UpdateChase(senses);
                    break;

                case EnemyState.Attack:
                    UpdateAttack(senses);
                    break;
            }

            return State;
        }

        private void UpdateIdle(EnemySenses senses)
        {
            // 待机时也会发现玩家（否则站着不动就永远看不见人）
            if (senses.TargetInSight)
            {
                TransitionTo(EnemyState.Chase);
                return;
            }

            if (TimeInState >= IdleDuration)
            {
                TransitionTo(senses.HasPatrolPoints ? EnemyState.Patrol : EnemyState.Idle);
            }
        }

        private void UpdatePatrol(EnemySenses senses)
        {
            if (senses.TargetInSight)
            {
                TransitionTo(EnemyState.Chase);
                return;
            }

            // 没有路点可走就退回避机，避免卡在 Patrol 里空转
            if (!senses.HasPatrolPoints)
            {
                TransitionTo(EnemyState.Idle);
                return;
            }

            // 走到路点了：调用方负责推进到下一个路点。
            // 这里【不切状态】—— 巡逻是持续状态，只是换了目标点。
            _ = senses.ReachedPatrolPoint;
        }

        private void UpdateChase(EnemySenses senses)
        {
            // 够近就开打
            if (senses.TargetInSight && senses.DistanceToTarget <= senses.AttackRange)
            {
                TransitionTo(EnemyState.Attack);
                return;
            }

            // 追丢了：宽限期过后放弃
            if (!senses.TargetInSight && TimeSinceLastSeen >= LoseTargetGrace)
            {
                TransitionTo(senses.HasPatrolPoints ? EnemyState.Patrol : EnemyState.Idle);
            }
        }

        private void UpdateAttack(EnemySenses senses)
        {
            // 目标跑远了就继续追
            if (!senses.TargetInSight || senses.DistanceToTarget > senses.AttackRange)
            {
                TransitionTo(EnemyState.Chase);
                return;
            }

            // 贴住了：请求攻击（冷却由调用方管 —— 它才知道攻速配置）
            WantsToAttack = true;
        }

        // ---------------------------------------------------------------- 转换

        private void TransitionTo(EnemyState next)
        {
            if (State == next) return;

            PreviousState = State;
            State = next;
            TimeInState = 0f;
            StateChanged = true;
        }

        /// <summary>
        /// 外部强制切状态（例如被玩家打了一下但没死 → 立刻进入 Chase 反击）。
        ///
        /// 【为什么需要】"挨打就反击"是一条独立规则：即使玩家在视野外，
        /// 被打了也该知道有人在打它。这条规则由调用方在外层判断（它知道谁打的），
        /// 然后调这里切换。
        ///
        /// 【为什么强制进 Chase 时要重置"多久没看见"】
        /// 这是断言抓出来的一个真 bug：不重置的话，TimeSinceLastSeen 还是初始的
        /// 极大值，于是 UpdateChase 第一帧就判定"追丢了"、立刻退回巡逻 ——
        /// 反击完全失效（刚切进追击就退出）。
        ///
        /// 语义上也该重置：既然强制进入追击，就等于"刚刚知道目标在哪"，
        /// 宽限期应当从这一刻重新开始算。
        /// </summary>
        public void ForceState(EnemyState next)
        {
            if (State == EnemyState.Dead) return;   // 死了不能被强制切走

            TransitionTo(next);

            if (next == EnemyState.Chase) TimeSinceLastSeen = 0f;
        }

        /// <summary>重置（复活 / 对象池复用时）。</summary>
        public void Reset()
        {
            PreviousState = EnemyState.Idle;
            State = EnemyState.Idle;
            TimeInState = 0f;
            TimeSinceLastSeen = float.MaxValue;
            StateChanged = false;
            WantsToAttack = false;
        }

        /// <summary>状态的中文名（界面/日志用）。</summary>
        public static string Describe(EnemyState state)
        {
            switch (state)
            {
                case EnemyState.Idle: return "待机";
                case EnemyState.Patrol: return "巡逻";
                case EnemyState.Chase: return "追击";
                case EnemyState.Attack: return "攻击";
                case EnemyState.Dead: return "已倒下";
                default: return state.ToString();
            }
        }
    }

    /// <summary>
    /// 视野判定（纯逻辑）。
    ///
    /// 【为什么不做遮挡检测】"玩家在墙后"需要射线检测，而射线要处理
    /// "打到的是墙还是敌人自己"、"多高才算视线"等一堆边界。
    /// 当前地图基本是空地，先做"距离 + 角度"，够验证追击行为。
    /// 遮挡留到有明确需求时再加（那时这里多一个 bool 参数即可）。
    /// </summary>
    public static class EnemyPerception
    {
        /// <summary>
        /// 玩家是否在视野内。
        ///
        /// <paramref name="viewAngleDegrees"/> 是【总】视野角（例如 120 表示左右各 60°）。
        /// 用总角度而不是半角，是因为 Inspector 里填"120 度视野"比"60 度半视野"直观。
        /// </summary>
        public static bool CanSee(
            float distance, float angleToTargetDegrees, float viewDistance, float viewAngleDegrees)
        {
            if (distance > viewDistance) return false;

            // 角度归一化到 0~180（正前方是 0）
            float angle = MathF.Abs(NormalizeAngle(angleToTargetDegrees));
            float half = MathF.Max(0f, viewAngleDegrees) * 0.5f;

            return angle <= half;
        }

        /// <summary>把角度归一化到 (-180, 180]。</summary>
        public static float NormalizeAngle(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            else if (degrees <= -180f) degrees += 360f;
            return degrees;
        }

        /// <summary>
        /// 两个朝向之间的夹角（度，0~180）。
        /// 用于把"敌人的 forward"和"指向玩家的方向"换算成夹角。
        /// </summary>
        public static float AngleBetween(float forwardX, float forwardZ, float toTargetX, float toTargetZ)
        {
            float forwardLength = MathF.Sqrt(forwardX * forwardX + forwardZ * forwardZ);
            float targetLength = MathF.Sqrt(toTargetX * toTargetX + toTargetZ * toTargetZ);

            if (forwardLength < 1e-6f || targetLength < 1e-6f) return 180f;   // 退化情形：当作看不见

            // 点积求夹角（只用 X/Z 平面）
            float dot = (forwardX * toTargetX + forwardZ * toTargetZ) / (forwardLength * targetLength);
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;

            return MathF.Acos(dot) * (180f / MathF.PI);
        }
    }
}
