// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System.Collections.Generic;
using Game.AI;
using Game.Gameplay.Combat;
using Game.Player;
using Game.World;
using UnityEngine;

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
    ///   - 不寻路（地图是空地，直线够用）
    ///   - 不做视野遮挡（先距离 + 角度）
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

        [Header("感知")]
        [Tooltip("能发现玩家的最远距离（米）。")]
        [SerializeField, Min(0.5f)] private float _viewDistance = 14f;

        [Tooltip("视野总角度（度）。180 = 左右各 90°，360 = 全向。")]
        [SerializeField, Range(10f, 360f)] private float _viewAngle = 140f;

        [Tooltip("失去目标后还追多久才放弃（秒）。给宽限期可以避免贴着墙角来回切状态。")]
        [SerializeField, Min(0f)] private float _loseTargetGrace = 3f;

        [Header("移动")]
        [SerializeField, Min(0.1f)] private float _patrolSpeed = 1.6f;
        [SerializeField, Min(0.1f)] private float _chaseSpeed = 4.2f;

        [Tooltip("转身速度（度/秒）。设很大就是瞬间转身。")]
        [SerializeField, Min(0f)] private float _turnSpeed = 540f;

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

        [Tooltip("视线射线起点往前推的距离，避免从自己身体内部出发。")]
        [SerializeField] private float _lineOfSightStartOffset = 0.4f;

        [Tooltip("视线遮挡检测的层。留空（Nothing）表示检测所有层 —— " +
                 "最安全的默认值，因为漏检会导致隔墙攻击。")]
        [SerializeField] private LayerMask _lineOfSightMask = ~0;

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
        private float _stateTimer;
        private int _patrolIndex;
        private float _verticalVelocity;
        private bool _fallen;
        private GameObject? _visualInstance;

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
            _controller = GetComponent<CharacterController>();

            // 【必须显式构造】Health 是普通 C# 类，Unity 不跑它的构造函数（本工程踩过多次）
            _health = new Health(_maxHealth);
            _health.Died += HandleDied;

            _brain = new EnemyBrainLogic(idleDuration: 2f, loseTargetGrace: _loseTargetGrace);

            _block = new MaterialPropertyBlock();
            _uprightRotation = transform.rotation;

            ApplyVisualPrefab();
            CacheRenderers();
            ApplyTint(_calmColor);
        }

        /// <summary>
        /// 如果配了外观模型，就用它替换程序化生成的方块外观。
        ///
        /// 【为什么需要这个方法 —— 这是一个"注释说谎"的修复】
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

            if (player == null)
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

            // 视野判定：距离 + 角度（纯逻辑在 EnemyPerception 里）
            float angle = EnemyPerception.AngleBetween(
                transform.forward.x, transform.forward.z,
                toTarget.x, toTarget.z);

            bool inSight = EnemyPerception.CanSee(distance, angle, _viewDistance, _viewAngle);

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
                    ComputeAttackIntent(ref intent, ref speed);
                    break;

                case EnemyState.Dead:
                    // 死亡时控制器被禁用，上面那句已经返回了；这里是双保险。
                    return;
            }

            ApplyMovement(intent, speed, stopDistance);
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
            intent.y = 0f;
            float distance = intent.magnitude;

            if (distance > 1e-4f && speed > 0f)
            {
                Vector3 direction = intent / distance;
                FaceDirection(direction);

                // 靠近到 stopDistance 以内就只转向、不前进（追击时避免贴脸挤进玩家）
                if (!(stopDistance > 0f && distance <= stopDistance))
                {
                    motion.x = direction.x * speed * Time.deltaTime;
                    motion.z = direction.z * speed * Time.deltaTime;
                }
            }

            _controller.Move(motion);
        }

        private bool ReachedPatrolPoint()
        {
            if (_patrolPoints == null || _patrolPoints.Length == 0) return false;

            Transform point = _patrolPoints[_patrolIndex];
            if (point == null) return true;

            Vector3 delta = point.position - transform.position;
            delta.y = 0f;
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
            toPoint.y = 0f;

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
            toTarget.y = 0f;

            // 追到攻击距离就停下（避免挤进玩家身体里）
            stopDistance = Mathf.Max(0.1f, _attackRange * 0.85f);

            intent = toTarget;
            speed = _chaseSpeed;
        }

        /// <summary>攻击的移动意图（停下、面向玩家、按冷却出手）。</summary>
        private void ComputeAttackIntent(ref Vector3 intent, ref float speed)
        {
            _ = intent;
            _ = speed;

            // 攻击时停下并面向玩家 —— 朝向由 ApplyMovement 里的 FaceDirection 处理，
            // 这里给一个朝向意图但不给速度（speed 保持 0）。
            if (_target != null)
            {
                Vector3 toTarget = _target.position - transform.position;
                toTarget.y = 0f;
                intent = toTarget;
            }

            if (!_brain.WantsToAttack) return;
            if (Time.time < _nextAttackTime) return;

            // 【攻击前必须再确认一次"打得到"】
            // 状态机是靠"水平距离 + 视野锥"判断进入攻击的，那个判断看不见墙和楼层。
            // 这里做最后一道闸：确认高度差和遮挡都允许，才真的扣血。
            // 状态机保持纯逻辑（它拿不到物理世界），闸门放在这一层。
            if (!CanReachTargetNow())
            {
                if (_logStateChanges && Time.time >= _nextReachLogTime)
                {
                    _nextReachLogTime = Time.time + 1f;
                    Debug.Log($"[敌人]「{name}」想攻击但打不到（被挡住或不在同一层），本次不出手");
                }

                return;
            }

            _nextAttackTime = Time.time + _attackInterval;
            DealDamage();
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
                // 【为什么要减一个余量】目标点通常在自己或目标的碰撞体内部，
                // 射线从眼睛出发时可能立刻打到自己。从稍微前面一点开始更好。
                Vector3 origin = eye + direction / distance * _lineOfSightStartOffset;
                float remaining = Mathf.Max(0f, distance - _lineOfSightStartOffset);

                if (remaining > 0.01f &&
                    Physics.Raycast(origin, direction / distance, out RaycastHit hit,
                        remaining, _lineOfSightMask, QueryTriggerInteraction.Ignore))
                {
                    // 打到的如果是目标自己，那不算被挡（它的碰撞体就在射线终点附近）
                    if (hit.collider != null &&
                        !hit.collider.transform.IsChildOf(_target) &&
                        hit.collider.transform != _target)
                    {
                        return false;
                    }
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
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureEnemy(float maxHealth, Transform[] patrolPoints,
            float viewDistance = 14f, float attackDamage = 10f)
        {
            _maxHealth = Mathf.Max(1f, maxHealth);
            _patrolPoints = patrolPoints ?? new Transform[0];
            _viewDistance = Mathf.Max(0.5f, viewDistance);
            _attackDamage = Mathf.Max(0f, attackDamage);
        }

        /// <summary>编辑器专用：换外观模型。</summary>
        public void SetVisualPrefab(GameObject prefab) => _visualPrefab = prefab;
#endif

        private void OnValidate()
        {
            _maxHealth = Mathf.Max(1f, _maxHealth);
            _viewDistance = Mathf.Max(0.5f, _viewDistance);
            _loseTargetGrace = Mathf.Max(0f, _loseTargetGrace);
            _attackRange = Mathf.Max(0.5f, _attackRange);
            _attackInterval = Mathf.Max(0.05f, _attackInterval);
        }
    }
}
