// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System.Collections.Generic;
using Game.AI;
using Game.Gameplay.Combat;
using Game.Player;
using Game.World;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Gameplay.AI
{
    /// <summary>
    /// 会巡逻、会发现玩家、会追、会打的敌人。
    ///
    /// 【它复用了什么 —— 这是本工程的核心设计】
    ///   - 血量：<see cref="Game.Player.Health"/> —— 和玩家、木桩【同一份代码】
    ///   - 受伤接口：<see cref="IDamageable"/> —— 所以手枪不用改一行就能打它
    ///   - 移动：CharacterController —— 和玩家同一套（以后还能复用台阶吸附）
    ///   - 状态决策：<see cref="EnemyBrainLogic"/> —— 纯逻辑，可独立断言
    ///
    /// 【它不做什么】
    ///   - 不接管地图烘焙；复用已烘焙的 NavMesh
    ///   - 感知按距离和角度，攻击另外检查遮挡与高度
    ///   - 不受 InputModeState 影响（它不该管玩家界面开没开）
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class EnemyController : MonoBehaviour, IDamageable, IHealthTarget
    {
        [Header("血量")]
        [SerializeField, Min(1f)] private float _maxHealth = 60f;

        [Tooltip("血条挂在头顶多高。")]
        [SerializeField] private float _healthBarHeight = 2.3f;

        [Header("巡逻")]
        [Tooltip("巡逻路点。留空则只待机不巡逻。用空物体摆位置即可。")]
        [SerializeField] private Transform[] _patrolPoints = new Transform[0];

        [Tooltip("走到路点多近算到达（米）。")]
        [SerializeField, Min(0.1f)] private float _patrolReachDistance = 0.8f;

        [Tooltip("在路点上停留多久再走向下一个（秒）。")]
        [SerializeField, Min(0f)] private float _patrolWaitTime = 1.5f;

        [Header("圆形仇恨范围")]
        [Tooltip("以敌人为中心的水平圆形半径（米），360° 检测，不受朝向影响。")]
        [FormerlySerializedAs("_viewDistance")]
        [SerializeField, Min(0.5f)] private float _aggroRadius = 14f;

        [Tooltip("在游戏中显示仇恨范围圈；Scene 视图也显示同尺寸的圆。")]
        [SerializeField] private bool _showAggroRange = true;

        [Tooltip("失去目标后还追多久才放弃（秒）。给宽限期可以避免贴着墙角来回切状态。")]
        [SerializeField, Min(0f)] private float _loseTargetGrace = 3f;

        [Header("移动")]
        [SerializeField, Min(0.1f)] private float _patrolSpeed = 1.6f;
        [SerializeField, Min(0.1f)] private float _chaseSpeed = 4.2f;

        [Tooltip("转身速度（度/秒）。设很大就是瞬间转身。")]
        [SerializeField, Min(0f)] private float _turnSpeed = 540f;

        [Header("障碍规避")]
        [Tooltip("【主方案】用 Unity 内置 NavMesh 算路径跟着走。需要先跑「游戏 → 工程 → 烘焙敌人导航网格」。" +
                 "没有烘焙过 NavMesh 时自动回退到下一条的局部探距。")]
        [SerializeField] private bool _useNavMesh = true;

        [Tooltip("【回退方案】NavMesh 不可用时，用局部探距绕开眼前的墙。" +
                 "它没有路线记忆，绕不过需要先远离目标的凹形障碍。")]
        [SerializeField] private bool _avoidObstacles = true;

        [Tooltip("探距射线的最远距离（米）。越远越早开始绕，但更容易在空地上无谓地拐。")]
        [SerializeField, Min(1f)] private float _avoidLookAheadDistance = 2.6f;

        [Tooltip("额外反应时间（秒）：需要通畅 = 速度 × 这个时间。让高速追击时提前绕开。")]
        [SerializeField, Min(0f)] private float _avoidReactionSeconds = 0.35f;

        [Tooltip("探距射线的高度（相对脚底）。取身体中部，避开地面与头顶。")]
        [SerializeField] private float _avoidProbeHeight = 0.9f;

        [Tooltip("敌人之间互相排斥的半径（米）。0 = 关闭分离力。")]
        [SerializeField, Min(0f)] private float _separationRadius = 1.8f;

        [Tooltip("分离力强度。太大敌人会被弹得乱转。")]
        [SerializeField, Min(0f)] private float _separationStrength = 0.9f;

        [Header("攻击")]
        [Tooltip("进入这个距离就开始攻击（米）。")]
        [SerializeField, Min(0.5f)] private float _attackRange = 1.6f;

        [Tooltip("每次攻击的伤害。")]
        [SerializeField, Min(0f)] private float _attackDamage = 10f;

        [Tooltip("攻击间隔（秒）。")]
        [SerializeField, Min(0.05f)] private float _attackInterval = 1.2f;

        [Tooltip("超过这个高度差（米）就认为不在同一层，打不到。" +
                 "用来防止敌人在楼下打到二楼的人。")]
        [SerializeField, Min(0.1f)] private float _maxAttackHeightDifference = 1.6f;

        [Tooltip("视线检测的起点高度（眼睛位置，相对脚底）。")]
        [SerializeField] private float _eyeHeight = 1.55f;

        [Tooltip("旧配置兼容字段。遮挡检测现在从眼睛起点检测并过滤自身，不再向前偏移，以免跳过薄墙。")]
        [SerializeField] private float _lineOfSightStartOffset = 0.4f;

        [Tooltip("视线遮挡检测的层。留空（Nothing）表示检测所有层 —— " +
                 "最安全的默认值，因为漏检会导致隔墙攻击。")]
        [SerializeField] private LayerMask _lineOfSightMask = ~0;

        // 【关于障碍探距复用 _lineOfSightMask】
        // 探距没有单独的层掩码，共用 _lineOfSightMask。理由：
        // 在这个工程里"挡视线的东西"和"走不过去的东西"是同一批（墙、房子、树、石头），
        // 分成两个字段会让使用者要配两遍，而配错一边就出 bug。
        // 以后若真需要"能看穿但走不过去"（玻璃门之类），再拆成两个字段。

        [Header("表现")]
        [Tooltip("巡逻/待机时的颜色。")]
        [SerializeField] private Color _calmColor = new Color(0.62f, 0.22f, 0.20f);

        [Tooltip("追击/攻击时的颜色（用来一眼看出它在追你）。")]
        [SerializeField] private Color _alertColor = new Color(1f, 0.30f, 0.15f);

        [Tooltip("倒下时是否侧倒。")]
        [SerializeField] private bool _fallOnDeath = true;

        [Tooltip("【换真模型用】把模型预制体拖到这里，会替换掉程序化生成的方块外观。留空则用方块。")]
        [SerializeField] private GameObject? _visualPrefab;

        [Tooltip("模型相对敌人根节点的位置修正。")]
        [SerializeField] private Vector3 _visualPrefabOffset = Vector3.zero;

        [Tooltip("模型绕 Y 的朝向修正（度）。Kenney 的 glTF 角色默认朝 -Z，这里填 180 可修正。")]
        [SerializeField] private float _visualPrefabYaw;

        [Tooltip("模型整体缩放修正。")]
        [SerializeField, Min(0.01f)] private float _visualPrefabScale = 1f;

        [Header("调试")]
        [Tooltip("打印状态转换。")]
        [SerializeField] private bool _logStateChanges;

        [Tooltip("打印绕障诊断：每次探距后输出各方向的通畅距离、门槛、被选中的方向（限流 0.5 秒）。" +
                 "排查「敌人不绕墙」时打开它，就能看清是探距问题还是选向问题。")]
        [SerializeField] private bool _debugSteering;

        // ---------------------------------------------------------------- 配置版本与迁移

        /// <summary>旧场景配置的显式迁移版本；合法的手调值不按零值一概重置。</summary>
        private const int CurrentConfigVersion = 4;

        [HideInInspector]
        [SerializeField] private int _configVersion;

        // ---- 运行时 ----
        private Health _health = null!;
        private EnemyBrainLogic _brain = null!;
        private CharacterController _controller = null!;
        private Renderer[] _renderers = new Renderer[0];
        private MaterialPropertyBlock _block = null!;
        private Color[] _baseColors = new Color[0];
        private Quaternion _uprightRotation;
        private Transform? _target;
        private IDamageable? _targetDamageable;
        private float _nextAttackTime;
        private float _nextReachLogTime;
        private float _nextSteeringLogTime;
        private float _stateTimer;
        private int _patrolIndex;
        private float _verticalVelocity;
        private bool _fallen;
        private GameObject? _visualInstance;

        // ---- 障碍规避（运行时状态）----
        private readonly float[] _clearances = new float[EnemySteering.CandidateCount];
        private readonly float[] _neighborX = new float[16];
        private readonly float[] _neighborZ = new float[16];

        /// <summary>探距用的命中缓冲。复用以避免每帧分配（3 敌人 × 7 方向 = 21 次/帧）。</summary>
        private readonly RaycastHit[] _probeHits = new RaycastHit[16];

        private bool _loggedProbeSaturation;

        /// <summary>
        /// "两者完全重合时往哪推"的角度。每个敌人不同，否则一对重合的敌人会一起往同方向走、永远分不开。
        ///
        /// 用黄金角分布：连续生成的敌人方向尽量散开，不会挤在同一侧。
        /// </summary>
        private float _separationTieAngle;
        private static int _spawnSequence;

        private EnemyStuckDetector _stuck = null!;

        /// <summary>NavMesh 路径跟随（主方案）。见 <see cref="EnemyNavigation"/>。</summary>
        private EnemyNavigation _navigation = null!;
        private bool _loggedNoNavMesh;
        private bool _usingNavigation;
        private float _nextNavigationRecoveryTime;
        private LineRenderer? _aggroCircle;
        private readonly Vector3[] _aggroPoints = new Vector3[96];
        private float _nextAggroCircleUpdate;
        private Vector3 _aggroCircleCenter;
        private float _aggroCircleRadius;
        public float AggroRadius => Mathf.Max(0.5f, _aggroRadius);
        private Vector3 _lastPosition;
        private float _detourTimer;
        private float _detourSign;

        /// <summary>当前状态（供 HUD / 调试查看）。</summary>
        public EnemyState State => _brain != null ? _brain.State : EnemyState.Idle;

        public bool IsAlive => _health != null && !_health.IsDead;
        public float Current => _health != null ? _health.Current : 0f;
        public float Max => _health != null ? _health.Max : _maxHealth;

        public Vector3 DamagePoint => transform.position + Vector3.up * 1.0f;

        // ---- IHealthTarget（血条用）----
        public float CurrentHealth => Current;
        public float MaxHealth => Max;
        public float NormalizedHealth => Max > 0f ? Mathf.Clamp01(Current / Max) : 0f;
        public Vector3 HealthBarAnchor => transform.position + Vector3.up * _healthBarHeight;

        /// <summary>当前用的是模型外观还是程序化方块（调试用）。</summary>
        public bool UsesVisualPrefab => _visualInstance != null;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            // 在使用导航配置前执行版本迁移。
            MigrateConfigIfNeeded();

            _controller = GetComponent<CharacterController>();

            // 【必须显式构造】Health 是普通 C# 类，Unity 不跑它的构造函数（本工程踩过多次）
            _health = new Health(_maxHealth);
            _health.Died += HandleDied;

            _brain = new EnemyBrainLogic(idleDuration: 2f, loseTargetGrace: _loseTargetGrace);
            _stuck = new EnemyStuckDetector();
            _navigation = new EnemyNavigation();

            // 每个敌人一个不同的"重合时推开方向"，避免一对重合的敌人往同方向走
            const float goldenAngle = 2.39996323f;
            _separationTieAngle = _spawnSequence * goldenAngle;
            _spawnSequence++;

            _lastPosition = transform.position;

            _block = new MaterialPropertyBlock();
            _uprightRotation = transform.rotation;

            ApplyVisualPrefab();
            CacheRenderers();
            ApplyTint(_calmColor);
            UpdateAggroCircle();
        }

        /// <summary>
        /// 登记进敌人注册表（分离力要用）。放在 OnEnable 而不是 Awake：
        /// 禁用后再启用也能正确回到表里。
        /// </summary>
        private void OnEnable()
        {
            if (!_registry.Contains(this)) _registry.Add(this);
        }

        private void OnDisable()
        {
            _registry.Remove(this);
            if (_aggroCircle != null) _aggroCircle.enabled = false;
        }

        /// <summary>
        /// 把旧场景里"未配置过"的字段补成正确初值。
        ///
        /// 只在 `_configVersion` 落后时执行一次，之后不再覆盖（尊重 Inspector 里的手工值）。
        /// 具体原因见 <see cref="CurrentConfigVersion"/> 上方的说明。
        /// </summary>
        private void MigrateConfigIfNeeded()
        {
            if (_configVersion >= CurrentConfigVersion) return;

            _showAggroRange = true;
            if (_configVersion >= 3)
            {
                _configVersion = CurrentConfigVersion;
                return;
            }

            // v3 只迁移主方案开关，保留已经调整过的 v2 参数。
            if (_configVersion >= 2)
            {
                _useNavMesh = true;
                _configVersion = CurrentConfigVersion;
                return;
            }
            _useNavMesh = true;

            // 探距
            _avoidObstacles = true;
            _avoidLookAheadDistance = 2.6f;
            _avoidReactionSeconds = 0.35f;
            _avoidProbeHeight = 0.9f;

            // 分离
            _separationRadius = 1.8f;
            _separationStrength = 0.9f;

            // 视线 / 探距的层掩码：Nothing(0) 会让所有物理查询直接返回空，
            // 表现是"敌人隔着墙也能打"以及"探距什么都探不到"。
            if (_lineOfSightMask.value == 0) _lineOfSightMask = ~0;

            // 攻击可达性（这几个字段在旧场景里可能已经在、也可能不在）
            if (_maxAttackHeightDifference <= 0f) _maxAttackHeightDifference = 1.6f;
            if (_eyeHeight <= 0f) _eyeHeight = 1.55f;
            if (_lineOfSightStartOffset < 0f) _lineOfSightStartOffset = 0.4f;

            _configVersion = CurrentConfigVersion;

            Debug.LogWarning(
                $"[敌人]「{name}」的配置是旧版本（场景保存于这些字段存在之前），" +
                "已自动补上避障默认值：避障=开、探距2.6m、反应0.35s、探距高度0.9m、" +
                "分离半径1.8m、层掩码=全部。\n" +
                "如需把迁移结果保存到场景，可运行" +
                "「游戏 → 工程 → 给场景敌人写入默认配置」（会重置避障参数）。" +
                "无需重建场景。", this);
        }

        /// <summary>
        /// 如果配了外观模型，就用它替换程序化生成的方块外观。
        ///
        /// 【为什么需要它 —— 这是一个"注释说谎"的修复】
        /// _visualPrefab 字段早先只是声明和赋值，没有任何代码读它。
        /// 于是"以后填个模型就能换外观"这个说法是不成立的 ——
        /// 填了也不会发生任何事。现在它的行为与注释一致。
        ///
        /// 【做法】把程序化生成的子物体隐藏（不是删除），
        /// 然后实例化模型作为子物体。隐藏而不删除的理由：
        ///   1. 隐藏的方块仍然提供 Renderer 缓存之外的"占位"作用 ── 不，实际上
        ///      隐藏后 CacheRenderers 会跳过它们（见下面的筛选），所以染色只作用于模型。
        ///   2. 想切回方块外观时不用重新生成。
        ///
        /// 【为什么不做成 Prefab 覆盖】敌人是运行时生成的（生成器建场景），
        /// 直接替换整个 GameObject 会和 CharacterController、血条、状态机打架。
        /// 只替换"外观子物体"是侵入最小的做法。
        /// </summary>
        private void ApplyVisualPrefab()
        {
            if (_visualPrefab == null) return;

            // 1) 隐藏程序化外观（子孙里的所有 Renderer，不包括之后实例化的模型）
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == null) continue;

                // 只关掉渲染，保留碰撞 —— 程序化外观本来也没加碰撞体
                // （碰撞全部由 CharacterController 提供）
                foreach (Renderer renderer in child.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = false;
                }
            }

            // 2) 实例化模型
            GameObject instance = Instantiate(_visualPrefab, transform);

            // 【朝向修正】Kenney 的 glTF 角色默认朝 -Z，而 Unity 约定朝 +Z。
            // 模型方向不对时这个字段可以修，不用改代码重新导入。
            instance.transform.localPosition = _visualPrefabOffset;
            instance.transform.localRotation = Quaternion.Euler(0f, _visualPrefabYaw, 0f);
            instance.transform.localScale = Vector3.one * _visualPrefabScale;

            _visualInstance = instance;
        }

        private void OnDestroy()
        {
            if (_health != null) _health.Died -= HandleDied;
        }

        /// <summary>
        /// 缓存要染色的渲染器。
        /// 【为什么要排除子物体里"不是身体"的部分】如果以后挂了武器/特效，
        /// 那些不该跟着身体一起变色。现在只取启用的渲染器（见方法内注释）。
        /// </summary>
        /// <summary>
        /// 缓存要染色的渲染器。
        ///
        /// 【只取"启用的"渲染器】换了外观模型之后，程序化生成的方块会被
        /// renderer.enabled = false 关掉。这里必须排除它们 ——
        /// 否则染色会同时作用在"看不见的方块"和"看得见的模型"上，
        /// 而且换模型后颜色可能不对（方块的材质和模型的不一样）。
        /// </summary>
        private void CacheRenderers()
        {
            Renderer[] all = GetComponentsInChildren<Renderer>(true);

            var enabledOnes = new List<Renderer>(all.Length);
            foreach (Renderer renderer in all)
            {
                if (renderer != null && renderer.enabled) enabledOnes.Add(renderer);
            }

            _renderers = enabledOnes.ToArray();
            _baseColors = new Color[_renderers.Length];

            for (int i = 0; i < _renderers.Length; i++)
            {
                Material shared = _renderers[i].sharedMaterial;
                _baseColors[i] = shared != null && shared.HasProperty("_BaseColor")
                    ? shared.GetColor("_BaseColor")
                    : Color.white;
            }
        }

        // ---------------------------------------------------------------- 感知

        /// <summary>
        /// 找到玩家。
        ///
        /// 【为什么按"实现了 IDamageable 的玩家"找】直接找 PlayerController 会
        /// 把 AI 和玩家脚本绑死。按能力找的话，以后"引诱用的假人"也能当目标。
        /// 这里先用最简单的：找带 PlayerHealth 的对象。
        /// </summary>
        private void ResolveTarget()
        {
            if (_target != null && _targetDamageable != null && _targetDamageable.IsAlive) return;

            Game.Player.PlayerHealth? player = FindFirstObjectByType<Game.Player.PlayerHealth>();

            if (player == null || !player.IsAlive)
            {
                _target = null;
                _targetDamageable = null;
                return;
            }

            _target = player.transform;
            _targetDamageable = player;   // PlayerHealth 实现了 IDamageable
        }

        /// <summary>采集这一帧的感知信息，喂给纯逻辑状态机。</summary>
        private EnemySenses GatherSenses()
        {
            bool dead = _health.IsDead;

            if (_target == null || _targetDamageable == null || !_targetDamageable.IsAlive)
            {
                return new EnemySenses(
                    distanceToTarget: float.PositiveInfinity,
                    targetInSight: false,
                    attackRange: _attackRange,
                    hasPatrolPoints: _patrolPoints != null && _patrolPoints.Length > 0,
                    isDead: dead,
                    reachedPatrolPoint: ReachedPatrolPoint());
            }

            Vector3 toTarget = _target.position - transform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;

            bool inSight = EnemyPerception.IsInAggroRange(toTarget.x, toTarget.z, AggroRadius);

            return new EnemySenses(
                distanceToTarget: distance,
                targetInSight: inSight,
                attackRange: _attackRange,
                hasPatrolPoints: _patrolPoints != null && _patrolPoints.Length > 0,
                isDead: dead,
                reachedPatrolPoint: ReachedPatrolPoint());
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            ResolveTarget();
            if (_brain == null) return;

            EnemySenses senses = GatherSenses();

            // 推进状态机（当前状态在推进【之前】记下来，用来判断是否发生了转换）
            EnemyState before = _brain.State;
            _brain.Advance(Time.deltaTime, senses);

            if (_brain.StateChanged || before != _brain.State)
            {
                OnStateEntered(_brain.State);
            }

            // 【控制器可用性检查必须放在行为之前】
            // 早先它放在 switch 之后 —— 那时 MoveTowards 已经调过 Move 了，
            // 挡不住"外部把控制器禁用掉"的情况（只会白报一次错）。
            if (_controller == null || !_controller.enabled) return;

            // 本帧的水平位移意图（由下面的状态行为填），最后和重力合并成一次 Move。
            //
            // 【为什么改成"先算意图、再合并移动"】
            // 早先 MoveTowards 里自己调了一次 Move（含竖直分量），
            // 之后 ApplyGravity 又调一次 Move —— 每帧竖直位移被施加两遍，
            // 下落速度和贴地判定都会偏。现在统一成一次。
            Vector3 intent = Vector3.zero;
            float speed = 0f;
            float stopDistance = 0f;

            switch (_brain.State)
            {
                case EnemyState.Idle:
                    break;   // intent 保持零，只受重力

                case EnemyState.Patrol:
                    ComputePatrolIntent(ref intent, ref speed);
                    break;

                case EnemyState.Chase:
                    ComputeChaseIntent(ref intent, ref speed, ref stopDistance);
                    break;

                case EnemyState.Attack:
                    ComputeAttackIntent(ref intent, ref speed, ref stopDistance);
                    break;

                case EnemyState.Dead:
                    // 死亡时控制器被禁用，上面那句已经返回了；这里是双保险。
                    return;
            }

            ApplyMovement(intent, speed, stopDistance);
        }

        /// <summary>
        /// 决定这一步往哪个方向走。
        ///
        /// 【顺序：先 NavMesh 路径，再退回局部探距】
        /// 1. <b>NavMesh</b>（主方案）：有烘焙好的导航网格时，沿真实路径拐点走。
        ///    它有【路线记忆】，能绕开凹形障碍、能处理"需要先远离目标"的路线 ——
        ///    这正是手写局部避障做不到的（实测 C 形障碍 40 秒绕不出来）。
        /// 2. <b>局部探距</b>（回退）：没烘焙过 NavMesh 时，至少能避开眼前的墙。
        ///
        /// 【为什么保留回退而不是直接删掉旧代码】
        /// 烘焙是编辑器里的手工步骤（菜单「烘焙敌人导航网格」）。
        /// 如果场景里还没有导航网格，直接删掉回退会让敌人立刻变成"直线撞墙"，
        /// 比现在更差。回退只是兜底，不参与主流程。
        /// </summary>
        private Vector3 ResolveMoveDirection(Vector3 desiredUnit, Vector3 targetPoint, float speed)
        {
            _usingNavigation = false;
            if (_useNavMesh && _navigation != null)
            {
                EnemyNavigationResult result = _navigation.GetDirection(
                    transform.position, targetPoint, Time.time, out Vector3 navDirection);
                if (result != EnemyNavigationResult.NoNavMesh)
                {
                    _usingNavigation = true;
                    _loggedNoNavMesh = false;
                    if (result != EnemyNavigationResult.Moving) return Vector3.zero;
                    Vector3 separated = ApplySeparation(navDirection);
                    float distance = Mathf.Max(0.1f, speed * Time.deltaTime);
                    if (_navigation.CanMove(transform.position, separated, distance)) return separated;
                    // 未叠加分离的方向已由完整路径给出；边界上 Raycast 的数值误差
                    // 不应让角色永久停下，实际墙体碰撞仍由 CharacterController 约束。
                    return navDirection;
                }
                if (!_loggedNoNavMesh)
                {
                    _loggedNoNavMesh = true;
                    Debug.LogWarning($"[敌人]「{name}」附近没有同层 NavMesh，回退到局部探距。" +
                        "请检查出生点并运行「游戏 → 工程 → 烘焙敌人导航网格」。", this);
                }
            }
            return SteerAroundObstacles(desiredUnit, speed);
        }
        /// <summary>
        /// 把水平意图和重力合并成【一次】CharacterController.Move。
        ///
        /// 【为什么要合并】CharacterController 每次 Move 都会做一次完整的碰撞扫描。
        /// 一帧调两次不仅浪费，还会让"先水平移动再竖直移动"和"同时移动"产生不同的结果
        /// （贴地判定、台阶吸附都会受影响）。合成一次才是正确的做法。
        /// </summary>
        private void ApplyMovement(Vector3 intent, float speed, float stopDistance)
        {
            // ---- 重力 ----
            if (_controller.isGrounded && _verticalVelocity < 0f)
            {
                // 贴地时给一个很小的向下速度，保证 isGrounded 稳定
                _verticalVelocity = -2f;
            }
            else
            {
                _verticalVelocity += Physics.gravity.y * Time.deltaTime;
            }

            Vector3 motion = new Vector3(0f, _verticalVelocity * Time.deltaTime, 0f);

            // ---- 水平 ----
            Vector3 targetPoint = transform.position + intent;
            bool sameLevel = Mathf.Abs(intent.y) <= EnemyNavigation.MaxSampleHeightDifference;
            bool hasTargetOffset = intent.sqrMagnitude > 1e-8f;
            intent.y = 0f;
            float distance = intent.magnitude;

            bool wantsToMove = hasTargetOffset && speed > 0f;
            bool withinStopDistance = sameLevel && stopDistance > 0f && distance <= stopDistance;
            _usingNavigation = false;

            if (wantsToMove && !withinStopDistance)
            {
                Vector3 direction = distance > 1e-4f ? intent / distance : transform.forward;

                // 【方向的唯一接入点】巡逻和追击都从这里出去，
                // 所以只改这一处就覆盖了全部移动。
                direction = ResolveMoveDirection(direction, targetPoint, speed);

                FaceDirection(direction);

                float travel = speed * Time.deltaTime;
                if (_usingNavigation) travel = Mathf.Min(travel, _navigation.DistanceToCorner);
                motion.x = direction.x * travel;
                motion.z = direction.z * travel;
            }
            else if (wantsToMove && withinStopDistance)
            {
                // 到位了：只转向、不前进
                FaceDirection(intent / distance);
            }

            _controller.Move(motion);

            UpdateStuckState(wantsToMove && !withinStopDistance &&
                (!_usingNavigation || _navigation.Result == EnemyNavigationResult.Moving));
        }

        /// <summary>两条移动路线共用的同层邻居分离计算。</summary>
        private Vector3 ApplySeparation(Vector3 desired)
        {
            float dirX = desired.x, dirZ = desired.z;
            if (_separationRadius > 0f)
            {
                int count = CollectNeighborPositions();
                if (count > 0)
                    EnemySteering.AccumulateSeparation(
                        transform.position.x, transform.position.z, _neighborX, _neighborZ, count,
                        _separationRadius, _separationStrength, _separationTieAngle,
                        ref dirX, ref dirZ);
            }
            return EnemySteering.TryNormalize(dirX, dirZ, out dirX, out dirZ)
                ? new Vector3(dirX, 0f, dirZ) : desired;
        }
        /// <summary>无导航网格时的局部探距回退；绕行方向先探距再采用。</summary>
        private Vector3 SteerAroundObstacles(Vector3 desired, float speed)
        {
            float dirX = desired.x;
            float dirZ = desired.z;

            if (!EnemySteering.TryNormalize(dirX, dirZ, out dirX, out dirZ))
            {
                return desired;   // 零向量：没有方向可掰
            }

            Vector3 separated = ApplySeparation(desired);
            dirX = separated.x;
            dirZ = separated.z;

            // ---- 2. 卡死兜底：先偏转基准方向 ----
            //
            // 【为什么只有在开启避障时才允许绕行】绕行是"盲转 90°"，
            // 只有后面紧接着的探距选向能保证转完还在可走的方向上。
            // 关掉避障时没有这个保证，就不该盲转。
            if (_avoidObstacles && _detourTimer > 0f)
            {
                _detourTimer -= Time.deltaTime;

                EnemySteering.RotateDirection(dirX, dirZ, 90f * _detourSign, out dirX, out dirZ);
            }

            // ---- 3/4. 探距 + 选方向 ----
            if (_avoidObstacles)
            {
                float required = RequiredClearance(speed);

                for (int i = 0; i < EnemySteering.CandidateCount; i++)
                {
                    EnemySteering.RotateDirection(
                        dirX, dirZ, EnemySteering.CandidateOffsetsDegrees[i],
                        out float probeX, out float probeZ);

                    _clearances[i] = ProbeClearance(probeX, probeZ);
                }

                int chosen = EnemySteering.ChooseCandidate(
                    _clearances, EnemySteering.CandidateCount, required);

                // 诊断输出：只在 Inspector 打开 _debugSteering 时打，且限流。
                // 用来直接看清"每个方向到底探到多远"，避免再靠猜。
                if (_debugSteering && Time.time >= _nextSteeringLogTime)
                {
                    _nextSteeringLogTime = Time.time + 0.5f;

                    var text = new System.Text.StringBuilder();
                    for (int i = 0; i < EnemySteering.CandidateCount; i++)
                    {
                        if (i > 0) text.Append("  ");
                        float c = _clearances[i];
                        string shown = float.IsPositiveInfinity(c) ? "∞" : c.ToString("F1");
                        // 标记被选中的那个，和"够不够通畅"的门槛对比
                        text.Append(EnemySteering.IsClear(c, required) ? "+" : "-");
                        text.Append(EnemySteering.CandidateOffsetsDegrees[i].ToString("F0"));
                        text.Append("°=");
                        text.Append(shown);
                    }

                    Debug.Log($"[敌人]「{name}」状态={EnemyBrainLogic.Describe(_brain.State)} " +
                              $"速度={speed:F1} 门槛={required:F2}m 选中={chosen} " +
                              $"(+通畅/-受阻) {text}");
                }

                if (chosen >= 0)
                {
                    EnemySteering.RotateDirection(
                        dirX, dirZ, EnemySteering.CandidateOffsetsDegrees[chosen],
                        out dirX, out dirZ);
                }
            }

            return new Vector3(dirX, 0f, dirZ);
        }

        /// <summary>
        /// "算通畅"所需的距离门槛。
        ///
        /// 【为什么要跟速度挂钩】走得快就需要更早开始绕：
        /// 巡逻 1.6 m/s 时 1.3 米够用，追击 4.2 m/s 时就不够了，
        /// 会在贴着墙才反应，然后顶着墙磨。
        /// </summary>
        private float RequiredClearance(float speed)
        {
            float byReaction = speed * _avoidReactionSeconds;
            float minimum = _controller != null ? _controller.radius * 2.5f : 0.9f;
            return Mathf.Max(byReaction, minimum);
        }

        /// <summary>
        /// 朝某个方向探"还有多远是空的"。
        ///
        /// 【为什么用 SphereCast 而不是 Raycast】射线没有宽度，
        /// 会认为"刚好擦着肩膀过去"是通畅的，而敌人身体过不去。
        /// 用略小于身体半径的球体探距，结果和实际能否通过更接近。
        ///
        /// 【为什么起点要往前挪】球体如果一开始就和自己的 CharacterController 重叠，
        /// SphereCast 可能不返回它，也可能从墙内部起始而漏检。
        /// 从身体边缘外侧起步，并把这段距离加回结果，语义就干净了。
        ///
        /// 【为什么用 NonAlloc 并过滤目标】目标（玩家）不该算障碍 ——
        /// 否则敌人会在快要进入攻击距离时"绕开玩家"而不是逼近。
        /// 但过滤不能靠层（工程没有专用层），只能拿到命中列表后逐个判断是谁。
        /// </summary>
        private float ProbeClearance(float dirX, float dirZ)
        {
            // 【兜底：字段可能是旧场景遗留的 0】
            // Awake 里的迁移只在 Play 模式跑；编辑模式（Scene 视图画 gizmo）不会跑。
            // 所以这里再用一次兜底，保证任何情况下探距都有意义。
            float lookAhead = _avoidLookAheadDistance > 0.1f ? _avoidLookAheadDistance : 2.6f;
            float probeHeight = _avoidProbeHeight > 0.05f ? _avoidProbeHeight : 0.9f;

            float radius = _controller != null ? _controller.radius : 0.35f;
            float probeRadius = radius * 0.9f;
            float startOffset = radius + 0.05f;

            Vector3 direction = new Vector3(dirX, 0f, dirZ);
            Vector3 origin = transform.position
                             + Vector3.up * probeHeight
                             + direction * startOffset;

            float remaining = lookAhead - startOffset;
            if (remaining <= 0.01f) return float.PositiveInfinity;

            int hitCount = Physics.SphereCastNonAlloc(
                origin, probeRadius, direction, _probeHits, remaining,
                _lineOfSightMask, QueryTriggerInteraction.Ignore);

            // 【缓冲饱和必须显式处理】
            // Unity 文档明确说 SphereCastNonAlloc 的结果【顺序不确定】，
            // 缓冲填满时不保证留下的是最近的命中。
            // 所以"饱和时按已有的算最近"是不可信的 —— 可能漏掉真正的墙。
            //
            // 处理方式：饱和时保守地当作"紧贴障碍"（返回 startOffset），
            // 让选向器倾向于避开这个方向。宁可多绕一点，也不要朝墙里走。
            // 并打一次警告 —— 出现饱和说明缓冲偏小或场景比预期拥挤。
            if (hitCount >= _probeHits.Length)
            {
                if (!_loggedProbeSaturation)
                {
                    _loggedProbeSaturation = true;
                    Debug.LogWarning(
                        $"[敌人]「{name}」的探距缓冲（{_probeHits.Length}）被打满，" +
                        "已保守地把该方向当作受阻。若频繁出现，请加大缓冲或减少近身碰撞体。");
                }

                return startOffset;
            }

            float nearest = float.PositiveInfinity;

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = _probeHits[i].collider;
                if (collider == null) continue;

                // ---- 必须排除【自己】----
                //
                // 【这是"避障看起来完全没生效"的根因】
                // 探距起点在身体外侧 0.4 米，但敌人身上不止一个碰撞体：
                // 灰盒生成器用 CreatePrimitive 造的身体/头/双臂方块，
                // 每个都自带一个 BoxCollider。探距球半径 0.315，
                // 在多数方向上会和自己的身体方块重叠/相切 → 立刻命中自己。
                //
                // 后果：七个候选方向的通畅度【全都一样差】，
                // ChooseCandidate 在"全都不够通畅"时固定返回下标 0（直行）——
                // 于是避障等于没生效，只剩卡死兜底每 0.7 秒盲转一次。
                // 玩家看到的就是"只是偶尔突然绕一下"。
                //
                // 生成器那边现在会删掉这些方块碰撞体（EnemyController 只应保留
                // CharacterController），但场景没重建时旧碰撞体还在 ——
                // 所以这里必须自己排除，不能依赖场景已经重建过。
                if (collider.transform == transform || collider.transform.IsChildOf(transform))
                {
                    continue;
                }

                // 目标是"要接近的东西"，不是障碍
                if (_target != null &&
                    (collider.transform == _target || collider.transform.IsChildOf(_target)))
                {
                    continue;
                }

                if (_probeHits[i].distance < nearest) nearest = _probeHits[i].distance;
            }

            return float.IsPositiveInfinity(nearest) ? float.PositiveInfinity : startOffset + nearest;
        }

        /// <summary>
        /// 收集附近其它敌人的水平位置，写进复用的数组（避免每帧分配）。
        /// 返回写入的数量。
        ///
        /// 【为什么不用 FindObjectsByType】那是全场景扫描，
        /// 每个敌人每帧调一次 = 每帧扫 3 遍整个场景（场景里有 400+ 带 ID 的物体）。
        /// 改用一个静态注册表：敌人在 OnEnable 登记、OnDisable 注销，遍历是 O(敌人数)。
        ///
        /// 【为什么遍历时要跳过 null】如果某个敌人被销毁时没走到 OnDisable
        /// （例如整个场景被卸载），注册表里会留下已销毁的引用。
        /// 遍历时顺手剔除，比依赖"一定能注销"更稳。
        /// </summary>
        private int CollectNeighborPositions()
        {
            int count = 0;
            Vector3 self = transform.position;
            float radiusSquared = _separationRadius * _separationRadius;

            for (int i = _registry.Count - 1; i >= 0; i--)
            {
                EnemyController other = _registry[i];

                // 已销毁的引用（Unity 的 == null 对已销毁对象成立）→ 顺手剔除
                if (other == null)
                {
                    _registry.RemoveAt(i);
                    continue;
                }

                if (other == this) continue;
                if (Mathf.Abs(other.transform.position.y - self.y) >
                    EnemyNavigation.MaxSampleHeightDifference) continue;

                // 已倒下的敌人不再当障碍：它的 CharacterController 已被禁用（不是实体），
                // 但 GameObject 还活着、仍在注册表里。若不过滤，活着的敌人会绕开一具尸体。
                if (!other.IsAlive) continue;

                if (count >= _neighborX.Length) break;

                Vector3 delta = other.transform.position - self;
                delta.y = 0f;

                if (delta.sqrMagnitude > radiusSquared) continue;

                _neighborX[count] = other.transform.position.x;
                _neighborZ[count] = other.transform.position.z;
                count++;
            }

            return count;
        }

        /// <summary>
        /// 所有活着的敌人（用于分离力）。
        ///
        /// 【为什么是静态的】分离力是"敌人之间"的关系，不属于某一个实例。
        /// 静态表让查询是 O(n) 而不是每帧全场景扫描。
        /// </summary>
        private static readonly List<EnemyController> _registry = new List<EnemyController>();

        /// <summary>
        /// 每帧更新"卡住"判定；一旦判定卡住就开一段绕行。
        ///
        /// 【为什么绕行有持续时间而不是立刻结束】只在一帧里朝侧面走毫无意义，
        /// 位移小到看不见。给 0.4 秒才能真的挪开一点。
        ///
        /// 【为什么传位置而不是每帧位移】判据比的是"窗口起点到终点的净位移"。
        /// 每帧位移的累加无法区分"走了 1 米"和"原地来回走了 2 米"——
        /// 后者恰恰是贴墙抖的典型表现，旧判据会漏掉它。
        /// </summary>
        private void UpdateStuckState(bool wantsToMove)
        {
            Vector3 current = transform.position;

            _stuck.Update(Time.deltaTime, wantsToMove, current.x, current.z);
            _lastPosition = current;

            if (_usingNavigation)
            {
                _detourTimer = 0f;
                if (_stuck.IsStuck && Time.time >= _nextNavigationRecoveryTime)
                {
                    _nextNavigationRecoveryTime = Time.time + 0.5f;
                    _navigation.RequestRepath();
                }
                return;
            }

            if (!_stuck.IsStuck || _detourTimer > 0f) return;

            _detourSign = _stuck.NextDetourSign();
            _detourTimer = 0.4f;

            if (_logStateChanges)
            {
                Debug.Log($"[敌人]「{name}」判定卡住（连续 {_stuck.ConsecutiveStuckWindows} 个窗口，" +
                          $"窗口净位移 {_stuck.LastNetTravel:F3} 米），" +
                          $"朝{(_detourSign > 0f ? "左" : "右")}绕行");
            }
        }

        private bool ReachedPatrolPoint()
        {
            if (_patrolPoints == null || _patrolPoints.Length == 0) return false;

            Transform point = _patrolPoints[_patrolIndex];
            if (point == null) return true;

            Vector3 delta = point.position - transform.position;
            return delta.magnitude <= _patrolReachDistance;
        }

        /// <summary>
        /// 巡逻的移动意图（走向当前路点；到了就在原地等一会儿）。
        /// </summary>
        private void ComputePatrolIntent(ref Vector3 intent, ref float speed)
        {
            if (_patrolPoints == null || _patrolPoints.Length == 0) return;

            Transform point = _patrolPoints[_patrolIndex];

            if (point == null)
            {
                AdvancePatrolIndex();
                return;
            }

            Vector3 toPoint = point.position - transform.position;

            // 到了：停一会儿再走向下一个
            if (toPoint.magnitude <= _patrolReachDistance)
            {
                _stateTimer += Time.deltaTime;

                if (_stateTimer >= _patrolWaitTime)
                {
                    _stateTimer = 0f;
                    AdvancePatrolIndex();
                }

                return;   // intent 保持零 = 原地等
            }

            intent = toPoint;
            speed = _patrolSpeed;
        }

        private void AdvancePatrolIndex()
        {
            if (_patrolPoints == null || _patrolPoints.Length == 0) return;
            _patrolIndex = (_patrolIndex + 1) % _patrolPoints.Length;
        }

        /// <summary>追击的移动意图（追到攻击距离就停）。</summary>
        private void ComputeChaseIntent(ref Vector3 intent, ref float speed, ref float stopDistance)
        {
            if (_target == null) return;

            Vector3 toTarget = _target.position - transform.position;

            // 追到攻击距离就停下（避免挤进玩家身体里）
            stopDistance = CanReachTargetNow() ? Mathf.Max(0.1f, _attackRange * 0.85f) : 0f;

            intent = toTarget;
            speed = _chaseSpeed;
        }

        /// <summary>
        /// 攻击的移动意图。
        ///
        /// 【这里修过一个会让敌人永远打不到人的停滞缺陷】
        /// 早先无论能不能打到，speed 都是 0 —— 于是：
        ///   - 玩家和敌人隔着薄墙、水平距离 < 攻击距离 → 状态机进 Attack
        ///   - 攻击被墙挡住 → 不出手
        ///   - speed = 0 → 不移动 → 不调用绕障 → 卡死检测也按"不想动"重置
        /// 结果敌人既打不到、也不绕，就站在墙那边干等。这是最糟的一种"卡住"：
        /// 玩家看得见它、它看得见玩家，但永远不会绕过来。
        ///
        /// 现在的规则：
        ///   够得着（视线通、同层）→ 站定出手
        ///   够不着             → 按追击速度继续靠近，让绕障去找路
        /// "够不够得着"由 <see cref="CanReachTargetNow"/> 判断（它需要物理世界），
        /// 所以这条规则留在控制器层，状态机保持纯逻辑。
        /// </summary>
        private void ComputeAttackIntent(ref Vector3 intent, ref float speed, ref float stopDistance)
        {
            if (_target == null) return;

            Vector3 toTarget = _target.position - transform.position;
            intent = toTarget;

            // ---- 够得着：站定出手 ----
            if (CanReachTargetNow())
            {
                // speed 保持 0 —— 攻击时不该继续挪动
                if (!_brain.WantsToAttack) return;
                if (Time.time < _nextAttackTime) return;

                _nextAttackTime = Time.time + _attackInterval;
                DealDamage();
                return;
            }

            // ---- 够不着：继续靠近，而不是站着 ----
            //
            // 【为什么用追击速度而不是原地绕】目标是"能打到它"，
            // 所以移动意图仍然指向目标；绕不绕开墙由 SteerAroundObstacles 决定。
            speed = _chaseSpeed;
            stopDistance = 0f;

            if (_logStateChanges && Time.time >= _nextReachLogTime)
            {
                _nextReachLogTime = Time.time + 1f;
                Debug.Log($"[敌人]「{name}」进入攻击距离却打不到（被挡住或不在同一层），继续靠近");
            }
        }

        /// <summary>
        /// 现在这一击能不能真的打到目标。
        ///
        /// 【为什么必须有这个 —— 这是一个真实的缺陷修复】
        /// 感知只算【水平】距离和角度，把高度差清零了。于是：
        ///   - 玩家站在二楼、敌人在楼下同一 X/Z 附近 → 会被隔着一层楼板扣血
        ///   - 玩家在墙的另一侧、水平距离够近 → 会被隔着墙扣血
        /// 工程里已经有房屋和玩家建造的墙，"地图基本是空地"这个前提不成立了。
        ///
        /// 两道检查：
        ///   1. 高度差超过阈值 → 不在同一层，打不到
        ///   2. 从敌人眼睛到目标胸口之间被挡住 → 打不到
        /// </summary>
        private bool CanReachTargetNow()
        {
            if (_target == null) return false;

            Vector3 eye = transform.position + Vector3.up * _eyeHeight;
            Vector3 targetPoint = _targetDamageable != null
                ? _targetDamageable.DamagePoint
                : _target.position + Vector3.up * _eyeHeight;

            // ---- 1. 高度差 ----
            //
            // 用"眼睛到目标点"的竖直差，而不是 transform.position 的差 ——
            // 后者在目标有高度偏移时会算错。
            float heightDifference = Mathf.Abs(eye.y - targetPoint.y);

            if (heightDifference > _maxAttackHeightDifference) return false;

            // ---- 2. 遮挡 ----
            Vector3 direction = targetPoint - eye;
            float distance = direction.magnitude;

            if (distance > 1e-4f)
            {
                // 不把起点前推：贴着薄墙时会直接跳过遮挡。过滤自身和目标碰撞体。
                int count = Physics.RaycastNonAlloc(eye, direction / distance, _probeHits,
                    distance, _lineOfSightMask, QueryTriggerInteraction.Ignore);
                if (count >= _probeHits.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    Transform hit = _probeHits[i].collider.transform;
                    if (hit == transform || hit.IsChildOf(transform) ||
                        hit == _target || hit.IsChildOf(_target)) continue;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 造成一次攻击伤害。
        ///
        /// 【为什么走 IDamageable】这样玩家的无敌时间、以后的护甲、
        /// 各种减伤都自动生效 —— 不用在这里重写一遍战斗规则。
        /// </summary>
        private void DealDamage()
        {
            if (_targetDamageable == null || !_targetDamageable.IsAlive) return;

            float applied = _targetDamageable.ApplyDamage(_attackDamage);

            if (_logStateChanges)
            {
                Debug.Log($"[敌人] 攻击玩家，造成 {applied:F1} 点伤害" +
                          (applied <= 0f ? "（被无敌时间或其它规则挡下）" : ""));
            }
        }

        // ---------------------------------------------------------------- 移动

        /// <summary>平滑转向某个方向（只绕 Y 轴）。</summary>
        private void FaceDirection(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f) return;

            Quaternion target = Quaternion.LookRotation(direction.normalized, Vector3.up);

            transform.rotation = _turnSpeed <= 0.01f
                ? target
                : Quaternion.RotateTowards(transform.rotation, target, _turnSpeed * Time.deltaTime);
        }

        // ---------------------------------------------------------------- 状态表现

        private void OnStateEntered(EnemyState state)
        {
            if (_logStateChanges)
            {
                Debug.Log($"[敌人]「{name}」{EnemyBrainLogic.Describe(_brain.PreviousState)}" +
                          $" → {EnemyBrainLogic.Describe(state)}");
            }

            // 用颜色表达状态：待机/巡逻是暗红，追击/攻击是亮红。
            // 没有动画的阶段，这是玩家唯一能一眼看出"它发现我了"的线索。
            bool alert = state == EnemyState.Chase || state == EnemyState.Attack;
            ApplyTint(alert ? _alertColor : _calmColor);
        }

        /// <summary>
        /// 染色。
        ///
        /// 【为什么用 MaterialPropertyBlock】直接改 sharedMaterial 会改到材质【资产】，
        /// 场景里所有用同一材质的物体一起变色（本工程在木桩上踩过同类问题）。
        /// </summary>
        private void ApplyTint(Color tint)
        {
            if (_renderers == null) return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer == null) continue;

                Color baseColor = i < _baseColors.Length ? _baseColors[i] : Color.white;
                Color result = new Color(
                    baseColor.r * tint.r,
                    baseColor.g * tint.g,
                    baseColor.b * tint.b,
                    baseColor.a);

                renderer.GetPropertyBlock(_block);
                _block.SetColor("_BaseColor", result);
                _block.SetColor("_Color", result);
                renderer.SetPropertyBlock(_block);
            }
        }

        private void HandleDied()
        {
            if (_logStateChanges) Debug.Log($"[敌人]「{name}」已倒下");

            // 立刻推进一次状态机，让 State 变 Dead（不必等下一帧）
            _brain.ForceState(EnemyState.Dead);
            ApplyTint(new Color(0.45f, 0.42f, 0.40f));

            if (_fallOnDeath && !_fallen)
            {
                _fallen = true;
                // 侧倒。不是物理体，直接设旋转。
                transform.rotation = _uprightRotation * Quaternion.Euler(80f, 0f, 0f);
            }

            // 关掉控制器：死了就不该再被推动/撞人。
            // 【注意】不能 Destroy 自己 —— 存档/复活可能还要用这个对象。
            if (_controller != null) _controller.enabled = false;

            // 【同时关掉所有子碰撞体】
            //
            // 灰盒生成器用 CreatePrimitive 造的身体/头/手臂方块各带一个 BoxCollider。
            // 生成器现在会删掉它们，但【场景没重建时旧碰撞体还在】。
            // 不在这里关掉的话：尸体仍然是一个实体障碍，
            // 活着的敌人探距会探到它并绕开 —— 与"尸体不挡路"的设计不符。
            Collider[] childColliders = GetComponentsInChildren<Collider>(true);

            foreach (Collider collider in childColliders)
            {
                if (collider != null) collider.enabled = false;
            }
        }

        // ---------------------------------------------------------------- 诊断可视化

        /// <summary>玩家死亡或复活时清除其旧仇恨，避免继续追击尸体。</summary>
        public static void ForgetPlayer(Transform player)
        {
            foreach (EnemyController enemy in _registry)
            {
                if (enemy == null || !enemy.IsAlive || enemy._target != player) continue;
                enemy._target = null;
                enemy._targetDamageable = null;
                enemy._brain.Reset();
                enemy._navigation.Reset();
                enemy._stateTimer = enemy._detourTimer = 0f;
                enemy.OnStateEntered(EnemyState.Idle);
            }
        }

        private void LateUpdate() => UpdateAggroCircle();

        private void UpdateAggroCircle()
        {
            bool visible = _showAggroRange && isActiveAndEnabled && IsAlive;
            if (_aggroCircle == null && visible)
            {
                Material material = Resources.Load<Material>("EnemyAggroRange");
                if (material == null) return;
                var circle = new GameObject("仇恨范围（调试）");
                circle.hideFlags = HideFlags.DontSave;
                circle.transform.SetParent(transform, false);
                _aggroCircle = circle.AddComponent<LineRenderer>();
                _aggroCircle.sharedMaterial = material;
                _aggroCircle.useWorldSpace = true;
                _aggroCircle.loop = true;
                _aggroCircle.positionCount = _aggroPoints.Length;
                _aggroCircle.widthMultiplier = 0.065f;
                _aggroCircle.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _aggroCircle.receiveShadows = false;
                _nextAggroCircleUpdate = 0f;
            }
            if (_aggroCircle == null) return;
            _aggroCircle.enabled = visible;
            if (!visible) return;
            Color color = State == EnemyState.Chase || State == EnemyState.Attack
                ? new Color(1f, 0.18f, 0.12f, 0.85f) : new Color(1f, 0.65f, 0.12f, 0.75f);
            _aggroCircle.startColor = _aggroCircle.endColor = color;
            Vector3 center = transform.position;
            if (Time.unscaledTime < _nextAggroCircleUpdate &&
                Mathf.Approximately(_aggroCircleRadius, AggroRadius))
            {
                // 中心每帧跟随；只把较贵的贴地采样限流，避免范围圈拖在敌人身后。
                Vector3 offset = center - _aggroCircleCenter;
                for (int i = 0; i < _aggroPoints.Length; i++) _aggroPoints[i] += offset;
                _aggroCircleCenter = center;
                _aggroCircle.SetPositions(_aggroPoints);
                return;
            }
            _nextAggroCircleUpdate = Time.unscaledTime + 0.1f;
            _aggroCircleCenter = center;
            _aggroCircleRadius = AggroRadius;
            for (int i = 0; i < _aggroPoints.Length; i++)
            {
                float angle = i * Mathf.PI * 2f / _aggroPoints.Length;
                Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * AggroRadius;
                int count = Physics.RaycastNonAlloc(point + Vector3.up * 0.75f, Vector3.down,
                    _probeHits, 2f, ~0, QueryTriggerInteraction.Ignore);
                float nearest = float.PositiveInfinity;
                for (int h = 0; h < count; h++)
                {
                    RaycastHit hit = _probeHits[h];
                    if (hit.normal.y < 0.6f || hit.distance >= nearest ||
                        hit.collider.GetComponentInParent<EnemyController>() != null ||
                        hit.collider.GetComponentInParent<PlayerHealth>() != null) continue;
                    point.y = hit.point.y;
                    nearest = hit.distance;
                }
                _aggroPoints[i] = point + Vector3.up * 0.06f;
            }
            _aggroCircle.SetPositions(_aggroPoints);
        }

        private void OnDrawGizmos()
        {
            if (!_showAggroRange || (Application.isPlaying && !IsAlive)) return;
            Gizmos.color = new Color(1f, 0.65f, 0.12f, 0.85f);
            Vector3 center = transform.position + Vector3.up * 0.06f;
            Vector3 previous = center + Vector3.right * AggroRadius;
            for (int i = 1; i <= 96; i++)
            {
                float angle = i * Mathf.PI * 2f / 96;
                Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * AggroRadius;
                Gizmos.DrawLine(previous, point);
                previous = point;
            }
        }

        /// <summary>
        /// 在 Scene 视图里画出七个探距方向。
        ///
        /// 【为什么需要它】"避障像没生效"这类问题最省时的查法不是猜参数，
        /// 而是【看见】敌人到底探到了什么：
        ///   - 线全是绿的 → 探距根本没打到东西（掩码错、高度错、几何没碰撞体）
        ///   - 线全是红且很短 → 打到了自己身上（曾经的真实缺陷）
        ///   - 线长度全是 0 或不显示 → 探距长度字段是 0（**这就是本次的根因**）
        ///   - 有红有绿但选的方向不对 → 是选向/权重问题
        ///
        /// 【必须用兜底值】编辑模式下 Awake 不跑，旧场景的字段还是类型默认值 0。
        /// 直接读字段的话线长会算成 0 —— 那正是"选中敌人却看不见线"的原因。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!_debugSteering) return;
            float lookAhead = _avoidLookAheadDistance > 0.1f ? _avoidLookAheadDistance : 2.6f;
            float probeHeight = _avoidProbeHeight > 0.05f ? _avoidProbeHeight : 0.9f;

            float radius = _controller != null ? _controller.radius : 0.35f;
            float required = RequiredClearance(Application.isPlaying ? _chaseSpeed : 4.2f);

            // 方向基准：优先朝目标；没有目标就朝 forward
            float baseX = transform.forward.x;
            float baseZ = transform.forward.z;

            if (_target != null)
            {
                baseX = _target.position.x - transform.position.x;
                baseZ = _target.position.z - transform.position.z;
            }

            if (!EnemySteering.TryNormalize(baseX, baseZ, out baseX, out baseZ)) return;

            Vector3 eye = transform.position + Vector3.up * probeHeight;

            for (int i = 0; i < EnemySteering.CandidateCount; i++)
            {
                EnemySteering.RotateDirection(baseX, baseZ,
                    EnemySteering.CandidateOffsetsDegrees[i], out float px, out float pz);

                float clearance = ProbeClearance(px, pz);
                bool clear = EnemySteering.IsClear(clearance, required);

                float drawn = float.IsPositiveInfinity(clearance)
                    ? lookAhead
                    : Mathf.Min(clearance, lookAhead);

                Gizmos.color = clear ? new Color(0.2f, 0.95f, 0.3f, 0.9f) : new Color(1f, 0.25f, 0.2f, 0.9f);
                Gizmos.DrawLine(eye, eye + new Vector3(px, 0f, pz) * Mathf.Max(0.05f, drawn));
            }

            // 门槛圈：半径 = 门槛距离，画在脚下便于比对
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.8f);
            Gizmos.DrawWireSphere(new Vector3(transform.position.x, 0.05f, transform.position.z), required);

            // 实际身体半径（便于确认探距球比身体小）
            Gizmos.color = new Color(0.4f, 0.6f, 1f, 0.6f);
            Gizmos.DrawWireSphere(new Vector3(transform.position.x, 0.05f, transform.position.z), radius);
        }

        // ---------------------------------------------------------------- IDamageable

        public float ApplyDamage(float amount)
        {
            if (_health == null) return 0f;

            float applied = _health.TakeDamage(amount);
            if (applied <= 0f) return 0f;

            // 【挨打就反击】即使玩家在视野外，被打到也该知道有人。
            // 这条规则放在外层（这里知道"被打了"这件事），
            // 状态机只管"状态怎么切"。
            if (IsAlive && _brain != null && _brain.State != EnemyState.Chase && _brain.State != EnemyState.Attack)
            {
                _brain.ForceState(EnemyState.Chase);
                OnStateEntered(EnemyState.Chase);
            }

            return applied;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器专用：把避障相关字段写成当前默认值，并把配置版本标到最新。
        ///
        /// 【为什么需要它】原因见 <see cref="CurrentConfigVersion"/>：
        /// 旧场景里这些字段是类型默认值（0 / false），运行时自动迁移能救回来，
        /// 但会每次都打一条警告、而且 Inspector 里看到的还是 0。
        /// 这个方法是"把正确的值真正写进场景"，这样 Inspector 能看到、警告也消失。
        ///
        /// 场景生成器和菜单「游戏 → 工程 → 给场景敌人写入默认配置」都调用它。
        /// </summary>
        public void ConfigureAvoidanceDefaults()
        {
            _showAggroRange = true;
            _useNavMesh = true;
            _avoidObstacles = true;
            _avoidLookAheadDistance = 2.6f;
            _avoidReactionSeconds = 0.35f;
            _avoidProbeHeight = 0.9f;

            _separationRadius = 1.8f;
            _separationStrength = 0.9f;

            if (_lineOfSightMask.value == 0) _lineOfSightMask = ~0;

            if (_maxAttackHeightDifference <= 0f) _maxAttackHeightDifference = 1.6f;
            if (_eyeHeight <= 0f) _eyeHeight = 1.55f;
            if (_lineOfSightStartOffset < 0f) _lineOfSightStartOffset = 0.4f;

            _configVersion = CurrentConfigVersion;
        }

        /// <summary>编辑器专用：当前配置版本（诊断用）。</summary>
        public int ConfigVersionForEditor => _configVersion;

        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureEnemy(float maxHealth, Transform[] patrolPoints,
            float viewDistance = 14f, float attackDamage = 10f)
        {
            _maxHealth = Mathf.Max(1f, maxHealth);
            _patrolPoints = patrolPoints ?? new Transform[0];
            _aggroRadius = Mathf.Max(0.5f, viewDistance);
            _attackDamage = Mathf.Max(0f, attackDamage);
        }

        /// <summary>编辑器专用：换外观模型。</summary>
        public void SetVisualPrefab(GameObject prefab) => _visualPrefab = prefab;
#endif

        private void OnValidate()
        {
            _maxHealth = Mathf.Max(1f, _maxHealth);
            _aggroRadius = Mathf.Max(0.5f, _aggroRadius);
            _loseTargetGrace = Mathf.Max(0f, _loseTargetGrace);
            _attackRange = Mathf.Max(0.5f, _attackRange);
            _attackInterval = Mathf.Max(0.05f, _attackInterval);
        }
    }
}
