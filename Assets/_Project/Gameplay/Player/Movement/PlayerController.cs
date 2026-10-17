using Game.World;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 角色移动执行者。
    /// 它**不读输入**：只消费 ICharacterInputSource 给出的 MovementIntent，
    /// 并把结果交给 AreaBounds 做边界裁剪。AI / 联机将来复用同一个组件即可。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class PlayerController : MonoBehaviour
    {
        [Header("引用（留空则自动查找）")]
        [SerializeField] private MonoBehaviour _intentSourceBehaviour;
        [SerializeField] private AreaBounds _area;

        [Header("移动")]
        [SerializeField] private float _moveSpeed = 4.5f;
        [SerializeField] private float _acceleration = 30f;
        [SerializeField] private float _rotationSpeed = 720f;
        [Tooltip("启用后按屏幕方向移动（相机旋转后 W 仍朝屏幕上方）；关闭则按世界轴向移动。")]
        [SerializeField] private bool _useCameraRelativeMovement = true;

        [Header("重力")]
        [SerializeField] private float _gravity = -20f;
        [Tooltip("没有地面时贴地的下落速度（避免角色在斜坡上抖动）。")]
        [SerializeField] private float _groundedStickSpeed = -2f;

        [Header("台阶吸附")]
        [Tooltip("允许向上吸附（上台阶）的最大高度，单位米。\n" +
                 "必须大于楼梯单级高度，否则爬不上去。本工程楼梯单级 0.279 米，所以取 0.35。\n" +
                 "做法参考 OpenKCC 的 verticalSnapUp：不依赖 CharacterController 的黑盒抬腿逻辑，\n" +
                 "而是自己射线探地面后直接把 Y 抬上去 —— 阈值显式、可测、失败原因可见。")]
        [SerializeField] private float _maxStepUp = 0.35f;

        [Tooltip("允许向下吸附（下台阶/贴住下坡）的最大深度，单位米。\n" +
                 "太小会让角色走下台阶时一格格地弹跳。本工程台阶进深约 0.54 米，取 0.45。\n" +
                 "对应 OpenKCC 的 verticalSnapDown。")]
        [SerializeField] private float _maxSnapDown = 0.45f;

        [Tooltip("离地后仍允许向下吸附的时间窗（秒）。\n" +
                 "没有它的话，走下台阶的那一瞬间就会进入自由落体、产生抖动。\n" +
                 "对应 OpenKCC 的 snapBufferTime。")]
        [SerializeField] private float _snapBufferTime = 0.06f;

        [Tooltip("打开后打印迈步/吸附的日志。排查台阶问题时用，平时关掉避免刷屏。")]
        [SerializeField] private bool _logMovement;

        [Tooltip("死区（米）。地面距离在这个范围内时不吸附 —— 没有它的话，角色静止贴地时\n" +
                 "会为了几毫米的差值每帧上下微调，表现就是原地抖动（本工程踩过这个坑）。")]
        [SerializeField] private float _deadZone = 0.02f;

        [Tooltip("迈步探测高度（米）。水平推进被挡住时，从这个高度往下探台阶顶面。\n" +
                 "取略大于台阶单级高度（本工程 0.258）即可。")]
        [SerializeField] private float _stepProbeHeight = 0.28f;

        [Tooltip("抬腿后的额外余量（米）。抬到台阶顶面之上一点点，避免脚正好卡在棱上。")]
        [SerializeField] private float _stepClearance = 0.02f;

        private CharacterController _controller;
        private ICharacterInputSource _intentSource;
        private Vector3 _horizontalVelocity;
        private float _verticalVelocity;
        private Vector3 _desiredMove;

        /// <summary>最近一次离地时刻（Time.unscaledTime）。用于 snapBufferTime。</summary>
        private float _leftGroundTime;

        /// <summary>本帧的吸附决策与探测结果，供排查和测试读取。</summary>
        private StairSnapLogic.SnapKind _lastSnap = StairSnapLogic.SnapKind.None;
        private float _lastSnapDelta;
        private float _lastProbeDistance;

        public Vector3 HorizontalVelocity => _horizontalVelocity;
        public float CurrentSpeed => _horizontalVelocity.magnitude;

        /// <summary>最近一次吸附的方向（None / Up / Down）。排查楼梯用。</summary>
        public StairSnapLogic.SnapKind LastSnap => _lastSnap;

        /// <summary>最近一次吸附把 Y 移动了多少米（正 = 向上）。</summary>
        public float LastSnapDelta => _lastSnapDelta;

        /// <summary>最近一次探测到的地面距离，单位米。float.NaN 表示没探到。</summary>
        public float LastProbeDistance => _lastProbeDistance;

        /// <summary>当前朝向（供以后动画/瞄准使用）。</summary>
        public Vector3 FacingDirection => transform.forward;

        public AreaBounds Area
        {
            get => _area;
            set => _area = value;
        }

        /// <summary>
        /// 把角色瞬移到指定位置（楼层切换、传送点用）。
        ///
        /// 为什么不直接写 transform.position：
        /// CharacterController 会用它内部的缓存位置做碰撞解算，直接改 transform 会让它
        /// "把角色拉回原处"或者卡进墙里。所以必须先禁用再改位置再启用。
        /// </summary>
        public void Teleport(Vector3 position, Quaternion? rotation = null)
        {
            bool wasEnabled = _controller != null && _controller.enabled;

            if (wasEnabled) _controller.enabled = false;

            transform.position = position;
            if (rotation.HasValue) transform.rotation = rotation.Value;

            if (wasEnabled) _controller.enabled = true;

            // 清掉速度，避免传送后带着旧惯性冲出去。
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = 0f;
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();

            _intentSource = _intentSourceBehaviour as ICharacterInputSource;
            if (_intentSource == null)
            {
                _intentSource = GetComponent<ICharacterInputSource>();
            }

            if (_intentSource == null)
            {
                Debug.LogWarning(
                    $"[{nameof(PlayerController)}] 没有找到 ICharacterInputSource，" +
                    "角色将不会响应任何输入。请添加 PlayerInputSource 或自定义输入源。", this);
            }

            // 边界由外部显式注入（Inspector / 场景生成器）。
            // 这里不再 FindFirstObjectByType 兜底：隐式查找会让"忘了接线"变成难查的运行期 bug，
            // 而不是一眼可见的 Inspector 空引用。
            if (_area == null)
            {
                Debug.LogWarning(
                    $"[{nameof(PlayerController)}] 没有指定地图边界（_area），角色将不受地图范围限制。", this);
            }

            // 相机若在场景中，注入移动参考系，实现「屏幕方向移动」而不是写死世界轴向。
            if (_useCameraRelativeMovement && _intentSource is PlayerInputSource playerInput)
            {
                UnityEngine.Camera cam = UnityEngine.Camera.main;
                if (cam != null)
                {
                    playerInput.SetMovementReference(cam.transform);
                }
            }
        }

        private void Update()
        {
            MovementIntent intent = _intentSource != null ? _intentSource.CurrentIntent : MovementIntent.None;

            // 【输入模式屏蔽移动】背包界面打开时不该还能走路。
            // 统一走 InputModeState，不要在这里再写一遍"界面是否打开"的判断 ——
            // 那样以后每加一个模式都要回来改这里。
            if (!InputModeState.AllowsMovement)
            {
                intent.Direction = Vector3.zero;
                intent.SpeedMultiplier = 0f;
            }

            if (intent.SpeedMultiplier <= 0.0001f)
            {
                intent.SpeedMultiplier = intent.Direction.sqrMagnitude > 0.0001f ? 1f : 0f;
            }

            Vector3 desiredVelocity = intent.Direction * (_moveSpeed * Mathf.Max(0f, intent.SpeedMultiplier));
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, desiredVelocity, _acceleration * Time.deltaTime);

            ApplyGravity();

            _desiredMove = _horizontalVelocity * Time.deltaTime;
            _desiredMove.y = _verticalVelocity * Time.deltaTime;

            // 边界裁剪只处理水平位移，垂直方向完全交给 CharacterController / 物理。
            if (_area != null)
            {
                Vector3 horizontal = new Vector3(_desiredMove.x, 0f, _desiredMove.z);
                Vector3 clamped = _area.ClampMove(transform.position, horizontal, _controller.radius);
                _desiredMove.x = clamped.x;
                _desiredMove.z = clamped.z;

                if (clamped.sqrMagnitude < horizontal.sqrMagnitude)
                {
                    // 撞到地图边界：消掉朝外的速度分量，避免贴边时抖动或持续蓄力。
                    Vector3 outward = Vector3.Project(_horizontalVelocity, horizontal.normalized);
                    _horizontalVelocity -= outward;
                }
            }

            Vector3 positionBeforeMove = transform.position;
            Vector3 horizontalBeforeMove = new Vector3(_desiredMove.x, 0f, _desiredMove.z);

            _controller.Move(_desiredMove);

            // ---- 显式迈步 ----
            // 【必须在吸附之前】顺序很重要：
            //   先迈步（把角色抬到台阶顶面上）→ 再吸附（贴住地面）
            // 反过来做的话，吸附先把角色压到台阶【侧面】下方的地面上，
            // 然后再迈步就会把脚抬到已经站着的台阶上，白抬一次。
            TryStepUp(positionBeforeMove, horizontalBeforeMove);

            // 吸附在迈步之后：把角色贴合到它脚下真正的地面。
            ApplyStairSnap();

            ApplyRotation(intent);
        }

        /// <summary>
        /// 显式迈步：水平移动被台阶挡住时，主动把角色抬到台阶顶面上。
        ///
        /// 【它是干什么的】`CharacterController.stepOffset` 的自动抬腿是黑盒，失败时静默。
        /// 这个方法作为**补强**：水平推进明显受阻 + 前方确实有一级可站的台阶 → 自己抬上去。
        /// 探两步（低 0.28 → 高 stepOffset 0.4）：
        ///   低的那步命中 = 这确实是一级矮台阶；
        ///   都不命中 = 前方是墙（顶面比 stepOffset 还高），不抬腿，避免贴墙走时往上飘。
        ///
        /// 【它【不是】楼梯问题的解药 —— 这点很重要】
        /// 当初写它是基于一个**错误的假设**（以为"水平推不上去"是 stepOffset 失效）。
        /// 后来证实楼梯卡住的真正原因是：二楼室内楼板没有和天花板共用洞口，
        /// 形成了一块【渲染被关掉、碰撞体还在】的隐形板，玩家撞在它上面。
        /// 那时这个方法根本没被触发过（因为角色不是"被台阶挡住"，而是"被隐形板挡住"）。
        ///
        /// 所以：它现在保留作为通用补强（13 条单元测试覆盖其判定逻辑），
        /// 但凡遇到"卡住"，**先查那个位置有什么碰撞体**，不要假定是台阶问题。
        /// </summary>
        private void TryStepUp(Vector3 positionBeforeMove, Vector3 intendedHorizontal)
        {
            // 本帧想要走的水平距离
            float intended = intendedHorizontal.magnitude;

            // 实际走了多少（Move 之后的位置差，去掉垂直分量）
            Vector3 actualDelta = transform.position - positionBeforeMove;
            float actual = new Vector2(actualDelta.x, actualDelta.z).magnitude;

            // 每帧都探测前方（不受 isGrounded / intended 影响），
            // 这样"卡住"时也能打出真实数据。
            bool foundLow = TryProbeStepTop(_stepProbeHeight, out float probeLow);
            float probeHigh = 0f;
            bool foundHigh = foundLow || TryProbeStepTop(_controller.stepOffset, out probeHigh);
            bool found = foundLow || foundHigh;
            float probeFound = foundLow ? probeLow : probeHigh;

            // 探测到的"脚下地面"（贴地时用）。这是判断"是否站在踏面边缘"的关键。
            bool hasGround = TryProbeGroundDown(_maxSnapDown, out float groundHit);
            float groundBelow = hasGround ? groundHit : float.NaN;

            // 记录"卡住"的情形：贴地 + 有移动意图 + 水平几乎没走 + 也没抬起来
            bool stuck = _controller.isGrounded
                         && intended > 0.005f
                         && actual < intended * 0.5f
                         && actualDelta.y < 0.005f;

            if (stuck && _logMovement)
            {
                Debug.Log(
                    $"[移动诊断-迈步] 卡住：意图 {intended:F4} m / 实际水平 {actual:F4} m / 竖直 {actualDelta.y:+0.0000;-0.0000} m\n" +
                    $"   贴地={_controller.isGrounded}  位置 y={transform.position.y:F4}\n" +
                    $"   前方探测: 低({_stepProbeHeight:F2})={(foundLow ? $"+{probeLow:F3}" : "未命中")}  " +
                    $"高({_controller.stepOffset:F2})={(foundHigh ? $"+{probeHigh:F3}" : "未命中")}  " +
                    $"→ 采用 {(found ? $"+{probeFound:F3} m" : "无（视为墙）")}\n" +
                    $"   脚下地面: {(hasGround ? $"{groundBelow:F4} m" : "未命中")}  " +
                    $"（_maxStepUp={_maxStepUp:F2} clearance={_stepClearance:F3}）",
                    this);
            }

            if (!_controller.isGrounded) return;
            if (intended <= 0.0001f) return;

            if (!StairSnapLogic.ShouldStepUp(intended, actual, found, probeFound, _maxStepUp))
            {
                return;
            }

            float lift = Mathf.Min(probeFound, _controller.stepOffset) + _stepClearance;

            float yBefore = transform.position.y;
            _controller.Move(new Vector3(0f, lift, 0f));
            float lifted = transform.position.y - yBefore;

            if (_logMovement)
            {
                Debug.Log(
                    $"[迈步] 想要 {intended:F4} m / 实际 {actual:F4} m → " +
                    $"抬腿 {lift:F3} m（实际 {lifted:F3} m），" +
                    $"台阶顶面 +{probeFound:F3}，位置 y={transform.position.y:F3}", this);
            }
        }

        /// <summary>
        /// 临时诊断：打印"为什么走不动"所需的信息。
        ///
        /// 【重要】这个方法是排查"上不去楼梯"时写的，当时的假设是"头顶被挡"和
        /// "前方有台阶没迈上去"—— 后来证明【两条都不对】：
        /// 真正原因是二楼的室内楼板没有和天花板共用洞口，形成了一块隐形碰撞板
        /// （渲染被楼层规则关掉了，但碰撞体还在，所以看不见却撞得到）。
        ///
        /// 方法保留，因为它的触发条件是"贴地 + 有移动意图 + 水平几乎没动" ——
        /// 这个条件不依赖任何假设，能如实反映"角色被什么挡在原地"。
        /// 真出问题时打开 _logMovement 即可。
        ///
        /// 一次打出四件事，覆盖四类失败模式：
        ///   ① 贴地/速度  —— 角色是不是根本在动
        ///   ② 向下探测   —— 有没有探到地面
        ///   ③ 前方障碍   —— 面前有什么、顶面在脚上方多高
        ///   ④ 头顶障碍   —— 上方有什么
        /// </summary>
        private void LogStepDiagnostics(string reason)
        {
            if (!_logMovement) return;

            Vector3 feet = transform.position;
            float maxForward = _controller.radius + 0.5f;

            // ③ 前方障碍：站在"脚底 + 一点点"的高度往前打。
            var forwardHits = new RaycastHit[8];
            int n = Physics.RaycastNonAlloc(
                feet + Vector3.up * 0.06f, transform.forward, forwardHits, maxForward,
                ~0, QueryTriggerInteraction.Ignore);

            bool hitForward = false;
            float bestDist = float.MaxValue;
            string obstacleName = "";
            float obstacleTopAboveFeet = 0f;

            for (int i = 0; i < n; i++)
            {
                if (forwardHits[i].collider == null) continue;
                if (forwardHits[i].collider.transform.IsChildOf(transform)) continue;
                if (forwardHits[i].distance >= bestDist) continue;

                bestDist = forwardHits[i].distance;
                hitForward = true;
                obstacleName = forwardHits[i].collider.name;
            }

            if (hitForward)
            {
                // 再垂直打一条，量出这个障碍的顶面高度
                Vector3 probe = feet + transform.forward * (bestDist + 0.05f);

                if (Physics.Raycast(probe + Vector3.up * (_controller.height + _maxStepUp), Vector3.down,
                        out RaycastHit topHit, _controller.height + _maxStepUp * 2f,
                        ~0, QueryTriggerInteraction.Ignore)
                    && topHit.collider != null && !topHit.collider.transform.IsChildOf(transform))
                {
                    obstacleTopAboveFeet = topHit.point.y - feet.y;
                }
            }

            // ④ 头顶障碍
            bool ceilingAbove = Physics.Raycast(
                feet + Vector3.up * 0.1f, Vector3.up,
                out RaycastHit ceilHit, _controller.height + _maxStepUp,
                ~0, QueryTriggerInteraction.Ignore);

            Debug.Log(
                $"[移动诊断] {reason}\n" +
                $"   ① 贴地={_controller.isGrounded}  竖直速度={_verticalVelocity:F2}  " +
                $"位置=(x {feet.x:F2}, y {feet.y:F3}, z {feet.z:F2})\n" +
                $"   ② 向下探测={(_lastProbeDistance is float.NaN ? "未命中" : $"{_lastProbeDistance:+0.000;-0.000} m")}" +
                $"（maxStepUp={_maxStepUp:F2} maxSnapDown={_maxSnapDown:F2} deadZone={_deadZone:F3}）\n" +
                $"   ③ 前方 {maxForward:F2} m 内障碍=" +
                $"{(hitForward ? $"「{obstacleName}」距离 {bestDist:F3} m，顶面相对脚高 {obstacleTopAboveFeet:F3} m" : "无")}\n" +
                $"   ④ 头顶障碍={(ceilingAbove ? $"「{(ceilHit.collider != null ? ceilHit.collider.name : "?")}」在上方 {ceilHit.distance:F3} m" : "无")}" +
                $"（stepOffset={_controller.stepOffset:F2}）",
                this);
        }

        /// <summary>
        /// 向前下方探测台阶顶面高度。
        ///
        /// 做法：从"前方 + 高出脚底 reachHeight"处向下打射线。
        /// 这样既能探到面前的台阶顶面（它比脚高一点点），
        /// 也不会误判脚下这一级（那一级在身后，不在前方）。
        /// </summary>
        private bool TryProbeStepTop(float reachHeight, out float topAboveFeet)
        {
            topAboveFeet = 0f;
            if (reachHeight <= 0f) return false;

            // 探测点放在前方一个半径处（贴着胶囊前缘），再往外一点点。
            float forward = _controller.radius + 0.05f;
            Vector3 origin = transform.position
                             + transform.forward * forward
                             + Vector3.up * reachHeight;

            float castLength = reachHeight + 0.1f;

            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, castLength,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            if (hit.collider == null) return false;
            if (hit.collider.transform.IsChildOf(transform)) return false;

            topAboveFeet = hit.point.y - transform.position.y;
            return true;
        }

        /// <summary>
        /// 显式台阶吸附。
        ///
        /// 【为什么要自己写这个】CharacterController 自带的 stepOffset 是内部黑盒：
        /// 它能否抬腿上台阶取决于"头顶是否有空间"，而这个失败是**静默的** ——
        /// 角色就是不动，不给任何提示。本工程楼梯卡住的根因正是这类静默失败。
        ///
        /// 这里改成显式探测 + 显式修改 Y，好处：
        ///   1. 阈值可调、可读（_maxStepUp / _maxSnapDown / _snapBufferTime）
        ///   2. 决策逻辑抽到 StairSnapLogic，可以脱离 Unity 做单元测试
        ///   3. 失败时可以看 LastProbeDistance 判断"到底探到地面没有"
        ///
        /// 思路与 OpenKCC 的 verticalSnapUp / verticalSnapDown / snapBufferTime 一致。
        /// </summary>
        private void ApplyStairSnap()
        {
            _lastSnap = StairSnapLogic.SnapKind.None;
            _lastSnapDelta = 0f;
            _lastProbeDistance = float.NaN;

            bool grounded = _controller.isGrounded;

            // 离地时间窗：刚离地的一小段时间内仍然允许向下吸附，
            // 否则走下台阶的瞬间会进入自由落体，看起来一顿一顿的。
            if (grounded)
            {
                _leftGroundTime = Time.unscaledTime;
            }

            bool canSnapDown = Time.unscaledTime - _leftGroundTime <= _snapBufferTime;

            // 贴地时：只向下探 maxSnapDown（脚下有没有更深的地面）。
            // 离地时：同样向下探，但要探得更远一点（在下落，地面在下面）。
            //
            // 【为什么不再向"脚上方"探】上一版为了找台阶顶面，把射线起点抬到脚上方再往下打，
            // 结果球的底端落在了脚下方、整个球陷进地板里 —— 命中距离恒为 0，
            // 探测结果恒等于"地面在脚上方 upReach"，于是每帧都把角色往上抬 → 原地抖动。
            // 现在一律从脚底向下打射线，配合 CharacterController 自带的 stepOffset 处理上台阶，
            // 吸附只负责"贴合地面"这一件事。职责单一，也就不会互相干扰。
            float probeDistance = _maxSnapDown;

            bool hit = TryProbeGroundDown(probeDistance, out float castDistance);

            float groundDistance = hit
                ? StairSnapLogic.DistanceBelowFeet(castDistance)
                : float.NaN;

            _lastProbeDistance = groundDistance;

            StairSnapLogic.SnapDecision decision = StairSnapLogic.Decide(
                groundDistance: grounded ? groundDistance : 0f,
                downDistance: grounded ? 0f : -groundDistance,
                isGrounded: grounded,
                canSnapDown: canSnapDown,
                maxStepUp: _maxStepUp,
                maxSnapDown: _maxSnapDown,
                deadZone: _deadZone);

            if (decision.Kind == StairSnapLogic.SnapKind.None)
            {
                // 贴地但没能吸附 —— 这正是"上不去台阶"的情形，把物理层的真实数据打出来。
                if (grounded)
                {
                    LogStepDiagnostics(
                        $"判定为不吸附（探测={(!hit ? "未命中" : castDistance.ToString("F3") + " m")}" +
                        $"，有符号距离 {groundDistance:+0.000;-0.000}）");
                }
                return;
            }

            // 直接把位置沿 Y 挪到地面高度。
            // 用 Move 而不是直接改 transform：Move 会考虑碰撞，
            // 万一头顶真被挡住，它会拒绝这次上升而不是把角色塞进墙里。
            float yBefore = transform.position.y;
            _controller.Move(new Vector3(0f, decision.DeltaY, 0f));
            float actualDelta = transform.position.y - yBefore;

            _lastSnap = decision.Kind;
            _lastSnapDelta = actualDelta;

            // 【关键诊断】请求了吸附，但位置几乎没动 —— 说明 Move 被碰撞挡下了。
            // 这是最隐蔽的失败模式：吸附"成功"了，但角色没动，所以之前一条日志都不会打。
            bool blocked = decision.Kind == StairSnapLogic.SnapKind.Up
                           && decision.DeltaY > 0.02f
                           && actualDelta < decision.DeltaY * 0.5f;

            if (blocked)
            {
                if (_logMovement)
                {
                    LogStepDiagnostics(
                        $"★ 吸附被挡住：请求抬 {decision.DeltaY:+0.000;-0.000} 米，" +
                        $"实际只动了 {actualDelta:+0.000;-0.000} 米");
                }

                // 被挡住时不要清垂直速度，保持真实物理状态。
                return;
            }

            // 吸附成功 = 重新站到地面上了，清掉垂直速度（否则会带着下落惯性抖）。
            if (_verticalVelocity < 0f) _verticalVelocity = _groundedStickSpeed;

            if (_logMovement)
            {
                Debug.Log(
                    $"[台阶吸附] {decision.Kind}  请求 {decision.DeltaY:+0.000;-0.000}，" +
                    $"实际 {actualDelta:+0.000;-0.000} 米；探测 {groundDistance:+0.000;-0.000}，" +
                    $"位置 y={transform.position.y:F3}", this);
            }
        }


        /// <summary>
        /// 从脚底向下打一条射线探测地面，返回命中距离（米，非负）。
        ///
        /// 【为什么用射线而不是球形投射】球形投射的命中距离受"球的哪一部分先碰到"影响，
        /// 起点稍有不慎就会让球陷进地面、命中距离恒为 0 —— 本工程踩过这个坑
        /// （表现是原地抖动）。射线只有一个起点，语义明确、可预测。
        ///
        /// 起点取脚底【往上一点点】，避免射线起点正好落在碰撞体表面内部导致漏检。
        /// </summary>
        private bool TryProbeGroundDown(float maxDistance, out float castDistance)
        {
            castDistance = 0f;
            if (maxDistance <= 0f) return false;

            const float startLift = 0.05f;
            Vector3 origin = transform.position + Vector3.up * startLift;
            float length = maxDistance + startLift;

            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, length,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            if (hit.collider == null) return false;

            // 忽略自己身上的碰撞体（CharacterController 本身也是 Collider）。
            if (hit.collider.transform.IsChildOf(transform)) return false;

            // 命中点在脚底【上方 startLift - 距离】处，换算成"从脚底往下的距离"。
            castDistance = Mathf.Max(0f, hit.distance - startLift);
            return true;
        }

        private void ApplyGravity()
        {
            if (_controller.isGrounded && _verticalVelocity <= 0f)
            {
                _verticalVelocity = _groundedStickSpeed;
            }
            else
            {
                _verticalVelocity += _gravity * Time.deltaTime;
            }
        }

        private void ApplyRotation(MovementIntent intent)
        {
            Vector3 look = intent.HasFacingDirection ? intent.FacingDirection : _horizontalVelocity;
            look.y = 0f;
            if (look.sqrMagnitude < 0.0001f) return;

            Quaternion target = Quaternion.LookRotation(look.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, _rotationSpeed * Time.deltaTime);
        }
    }
}
