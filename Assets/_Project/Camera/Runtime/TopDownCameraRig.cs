using UnityEngine;

namespace Game.CameraSystem
{
    /// <summary>
    /// 斜俯视跟随相机（类似《地球末日：生存》的视角）。
    ///
    /// 职责边界：**只跟随 Target，不读输入、不控制角色**。
    /// 角色想按屏幕方向移动时，向它索取朝向即可（见 PlayerInputSource.SetMovementReference）。
    ///
    /// 层级（由本组件自动建立/校验）：
    ///   Main Camera            <- 只挂 Camera，位置由本组件驱动
    ///     └ CameraBoom         <- 本组件所在物体：跟随 Target，并整体看向「视线点」
    ///         └ CameraLookAt   <- 视线点锚点（只用于可视化/调试，可挂 CameraRigDebugInfo）
    ///   相机本体被放在 Boom 的 +Z 延长线上，距离 = 吊臂长度，旋转 = Boom 旋转，
    ///   因此 Boom 的正前方始终是「视线点 - 相机」的方向。
    ///
    /// 参数全部暴露在 Inspector：高度、后撤距离、视线前瞻、视点高度、FOV、平滑速度、俯仰角限制。
    /// </summary>
    [DisallowMultipleComponent]
    public class TopDownCameraRig : MonoBehaviour
    {
        [Header("跟随目标")]
        [SerializeField] private Transform _target;
        [Tooltip("找不到 Target 时，自动按此组件类型查找。")]
        [SerializeField] private Player.PlayerController _autoTargetSource;

        [Header("镜头摆放（核心手感参数）")]
        [Tooltip("视线点相对角色高度，一般取角色胸口高度。")]
        [SerializeField] private float _lookHeight = 1.4f;

        [Tooltip("角色在画面里的高低位置。0.5 = 正中央；越大角色越靠画面下方（看得见更多前方）；越小越靠上。\n" +
                 "默认 0.5 就是正中央 —— 如果发现『角色不在画面里』，先把它设成 0.5 确认相机是否真的对着角色。\n" +
                 "推荐 0.55~0.65：角色略偏下，接近《地球末日》的构图。\n" +
                 "这个值是按吊臂长度的比例算的，所以改俯仰角不会影响角色的屏幕位置。")]
        [SerializeField, Range(0.5f, 0.9f)] private float _targetScreenAnchor = 0.5f;

        [Tooltip("相机在水平面上从角色向后退的距离。越小越接近正俯视。\n" +
                 "注意：俯视角接近 80° 以上时，地平线会进画面（露天空），大约 75° 是『地形铺满画面』的临界点。\n" +
                 "当前配置：后撤 8 / 高度 16.5 → 约 62°（《地球末日》那种斜俯视）。")]
        [SerializeField] private float _backDistance = 8f;

        [Tooltip("相机离地高度。与 _backDistance 一起决定俯视角度：\n" +
                 "俯视角 = atan2(后撤距离, 高度 - 视线高度)。\n" +
                 "16.5 / 8 → 约 62°。数值越大越接近正俯视，越小越贴地平线。")]
        [SerializeField] private float _height = 16.5f;

        [Tooltip("镜头方位角（绕 Y 轴，度）。0 = 相机在角色正南方（世界 -Z 方向）。\n" +
                 "45 会让整张地图在画面里斜过来，形成等距（isometric）构图 ——\n" +
                 "《地球末日》就是这种斜 45° 的视角。想要正对世界轴向就设 0。")]
        [SerializeField] private float _yawDegrees = 45f;

        [Tooltip("俯仰角限制：相机能多接近水平/正俯视。")]
        [SerializeField] private Vector2 _pitchLimits = new Vector2(20f, 85f);

        [Tooltip("视角方位是否跟随角色朝向。关闭 = 固定方位（更接近《地球末日》）；开启 = 角色转身视角跟着转。")]
        [SerializeField] private bool _followTargetYaw = false;

        [Header("取景")]
        [SerializeField] private float _fieldOfView = 50f;
        [SerializeField] private float _nearClipPlane = 0.3f;
        [SerializeField] private float _farClipPlane = 300f;

        [Header("平滑")]
        [Tooltip("跟随平滑速度（越大越硬，0 表示瞬间跟随）。")]
        [SerializeField] private float _followSharpness = 14f;
        [SerializeField] private float _rotationSharpness = 18f;

        [Header("层级引用（留空自动查找）")]
        [SerializeField] private Transform _boom;
        [SerializeField] private Transform _lookTarget;
        [SerializeField] private Transform _cameraTransform;
        [SerializeField] private UnityEngine.Camera _camera;

        private Vector3 _followPosition;
        private Quaternion _boomRotation;
        private float _pitchDegrees;
        private float _rawPitchDegrees;
        private float _boomDistance;
        private bool _initialized;
        private bool _missingTargetWarned;
        private bool _loggedReady;

        [Header("运行时只读（排查用，不能在这里改）")]
        [SerializeField] private bool _hasTarget;
        [SerializeField] private bool _hasCamera;
        [SerializeField] private float _resolvedCameraDistance;
        [SerializeField] private Vector3 _cameraWorldPosition;
        [SerializeField] private Vector3 _targetWorldPosition;

        public Transform Target
        {
            get => _target;
            set
            {
                _target = value;
                _initialized = false;
            }
        }

        /// <summary>供角色/输入层读取的相机朝向。</summary>
        public Transform AlignmentTransform => _cameraTransform != null ? _cameraTransform : transform;

        public UnityEngine.Camera Camera => _camera;

        public float FieldOfView
        {
            get => _fieldOfView;
            set
            {
                _fieldOfView = Mathf.Clamp(value, 10f, 110f);
                ApplyCameraSettings();
            }
        }

        public float Height
        {
            get => _height;
            set
            {
                _height = Mathf.Max(0.1f, value);
                RecalculateBoom();
            }
        }

        public float BackDistance
        {
            get => _backDistance;
            set
            {
                _backDistance = Mathf.Max(0f, value);
                RecalculateBoom();
            }
        }

        /// <summary>角色在画面里的高低位置（0.5 = 正中央，越大越靠画面下方）。</summary>
        public float TargetScreenAnchor
        {
            get => _targetScreenAnchor;
            set => _targetScreenAnchor = Mathf.Clamp(value, 0.5f, 0.9f);
        }

        /// <summary>当前实际俯仰角（度，0 = 水平，90 = 正俯视）。</summary>
        public float PitchDegrees => _pitchDegrees;

        /// <summary>视角方位是否跟随角色朝向。</summary>
        public bool FollowTargetYaw
        {
            get => _followTargetYaw;
            set => _followTargetYaw = value;
        }

        /// <summary>
        /// 当前使用的视角方位角（绕 Y 轴，度）。
        ///
        /// 平时用 _yawDegrees 这个固定值 —— 45° 会让整张地图在画面里斜过来，
        /// 形成《地球末日》那种等距（isometric）构图；设 0 就是正对世界轴向。
        /// 开启 _followTargetYaw 时改为跟随角色朝向，方位角由角色决定。
        /// </summary>
        public float CurrentYaw => _followTargetYaw && _target != null ? _target.eulerAngles.y : _yawDegrees;

        private void Awake()
        {
            EnsureRig();
            ApplyCameraSettings();
            RecalculateBoom();
        }

        private void OnEnable()
        {
            _initialized = false;
        }

        private void LateUpdate()
        {
            EnsureRig();

            if (_target == null)
            {
                ResolveTarget();
                if (_target == null)
                {
                    if (!_missingTargetWarned)
                    {
                        _missingTargetWarned = true;
                        Debug.LogWarning(
                            $"[{nameof(TopDownCameraRig)}] 场景里找不到跟随目标，相机不会移动。" +
                            "请先执行菜单「游戏 → 灰盒 → 重建到当前场景」生成玩家，" +
                            "或在 Inspector 里把 _target 指到玩家身上。", this);
                    }

                    return;
                }
            }

            // 视线点 = 角色位置 + 胸口高度 + 【世界坐标】偏移。
            // 视线点 = 角色位置 + 屏幕构图偏移。
            // 偏移量按【吊臂长度的比例】算，所以俯仰角怎么调，角色在画面里的位置都保持不变。
            // （早先用的是固定距离的 _worldLookOffset，它和俯仰角耦合：一改角度构图就崩，所以废弃了。）
            Vector3 desiredFollow = _target.position + Vector3.up * _lookHeight + ResolveLookOffset();

            if (!_initialized)
            {
                _followPosition = desiredFollow;
                _boomRotation = Quaternion.Euler(_pitchDegrees, CurrentYaw, 0f);
                _initialized = true;
            }
            else
            {
                _followPosition = SmoothTowards(_followPosition, desiredFollow, _followSharpness, Time.deltaTime);

                Quaternion desiredRotation = Quaternion.Euler(_pitchDegrees, CurrentYaw, 0f);
                _boomRotation = _rotationSharpness <= 0f
                    ? desiredRotation
                    : Quaternion.Slerp(_boomRotation, desiredRotation, 1f - Mathf.Exp(-_rotationSharpness * Time.deltaTime));
            }

            _boom.position = _followPosition;
            _boom.rotation = _boomRotation;
            SyncCameraTransform();
            UpdateDiagnostics();
        }

        /// <summary>
        /// 把关键状态同步到 Inspector，并在第一次成功接管时打一条 Log。
        /// 目的是让"看不到玩家"这类问题一眼能定位：看不到日志 = 相机根本没驱动。
        /// </summary>
        private void UpdateDiagnostics()
        {
            _hasTarget = _target != null;
            _hasCamera = _camera != null && _cameraTransform != null;
            _cameraWorldPosition = _cameraTransform != null ? _cameraTransform.position : Vector3.zero;
            _targetWorldPosition = _target != null ? _target.position : Vector3.zero;

            if (_loggedReady || !Application.isPlaying) return;
            if (!_hasTarget || !_hasCamera) return;

            _loggedReady = true;
            Debug.Log(
                $"[斜俯视相机] 已接管相机：跟随目标「{_target.name}」{_targetWorldPosition}，" +
                $"相机位置 {_cameraWorldPosition}，距离 {_resolvedCameraDistance:F1} 米，俯仰角 {_pitchDegrees:F1}°。\n" +
                $"  参数实况：原始角度 {_rawPitchDegrees:F1}°（钳位范围 {_pitchLimits.x}~{_pitchLimits.y}）｜" +
                $"_height {_height:F2}｜_backDistance {_backDistance:F2}｜_lookHeight {_lookHeight:F2}｜" +
                $"_targetScreenAnchor {_targetScreenAnchor:F2}｜_followTargetYaw {_followTargetYaw}｜" +
                $"相机朝向 {_cameraTransform.eulerAngles}（X 为负 = 向下看）", this);
        }

        /// <summary>
        /// 由"角色在画面里的高低"反算防线点的偏移量。
        ///
        /// 数学关系（与俯仰角无关的部分已消掉）：
        ///   视线点在屏幕上偏离中心的比例 = 偏移量 / 吊臂长度
        /// 所以偏移量 = (1 - 期望屏幕位置) × 吊臂长度。
        /// _targetScreenAnchor = 0.5 表示角色在正中央；大于 0.5 表示角色更靠画面下方。
        /// </summary>
        private Vector3 ResolveLookOffset()
        {
            float t = Mathf.Clamp01(_targetScreenAnchor);
            if (Mathf.Approximately(t, 0.5f)) return Vector3.zero;

            float distance = _boomDistance * (t - 0.5f);
            if (Mathf.Abs(distance) < 0.0001f) return Vector3.zero;

            // 沿吊臂前方向后退：数学上等价于"把视线点抬高并往前挪"，
            // 具体表现为角色在屏幕上往下移（t > 0.5）。
            Vector3 downDirection = Quaternion.Euler(_pitchDegrees, CurrentYaw, 0f) * Vector3.forward;
            return -downDirection.normalized * distance;
        }

        private void SyncCameraTransform()
        {
            if (_cameraTransform == null || _boom == null) return;

            Vector3 anchor = _boom.position;

            // Boom 的前方向是"从相机指向视线点"的方向（俯视时朝斜下方）。
            // 相机必须挂在它的**反方向**上：也就是视线点的斜上方，然后往下看。
            // 早先写成 +forward，结果相机被放到地面以下，画面变成"从下往上看"——就是这个符号错的。
            Vector3 fromAnchorToCamera = -(_boom.rotation * Vector3.forward);

            // 兜底：万一 Boom 的旋转被别人改坏，退回按俯仰角直接算方向（同样取反）。
            if (fromAnchorToCamera.sqrMagnitude < 0.0001f)
            {
                fromAnchorToCamera = Quaternion.Euler(-_pitchDegrees, 0f, 0f) * Vector3.forward;
            }

            fromAnchorToCamera.Normalize();
            _cameraTransform.position = anchor + fromAnchorToCamera * _boomDistance;

            // 相机不是"继承 Boom 的旋转"，而是**明确看向视线点**。
            // 这样即使 Boom 的旋转是错的，相机也一定对着玩家，不会出现"看不到玩家"。
            Vector3 lookDirection = anchor - _cameraTransform.position;
            if (lookDirection.sqrMagnitude > 0.0001f)
            {
                _cameraTransform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
            }
            else
            {
                _cameraTransform.rotation = _boom.rotation;
            }

            _resolvedCameraDistance = Vector3.Distance(_cameraTransform.position, anchor);
        }

        private static Vector3 SmoothTowards(Vector3 current, Vector3 target, float sharpness, float deltaTime)
        {
            if (sharpness <= 0f) return target;
            return Vector3.Lerp(current, target, 1f - Mathf.Exp(-sharpness * deltaTime));
        }

        /// <summary>
        /// 由 高度 / 后撤距离 / 视线点高度 反算俯仰角与吊臂长度。
        ///
        /// 俯仰角定义：相机视线与【水平面】的夹角（0° = 平视，90° = 正俯视）。
        /// 所以对边是 (高度 - 视线高度)、邻边是 后撤距离：
        ///     pitch = atan2(vertical, horizontal)
        /// 直觉校验：相机越高 → 越接近正俯视 → 角度越大。分子必须是 vertical。
        /// （早先写成了 atan2(horizontal, vertical)，把角度算成了它的余角，
        ///   例如 20.06/5 被算成 15° 而不是 75°，导致视角一直贴地平线。）
        /// </summary>
        private void RecalculateBoom()
        {
            float vertical = Mathf.Max(0.05f, _height - _lookHeight);
            float horizontal = Mathf.Max(0f, _backDistance);

            _rawPitchDegrees = Mathf.Atan2(vertical, horizontal) * Mathf.Rad2Deg;
            _pitchDegrees = Mathf.Clamp(_rawPitchDegrees, _pitchLimits.x, _pitchLimits.y);

            _boomDistance = Mathf.Sqrt(vertical * vertical + horizontal * horizontal);
        }

        private void EnsureRig()
        {
            if (_boom == null) _boom = transform;
            if (_lookTarget == null) _lookTarget = transform;
            if (_camera == null)
            {
                // 先找自己的子物体，再往父级找（Boom 既可能是相机的子物体，也可能是相机的父物体），
                // 最后退回 Camera.main，保证不论谁先建立都能接上。
                _camera = GetComponentInChildren<UnityEngine.Camera>(true);
                if (_camera == null) _camera = GetComponentInParent<UnityEngine.Camera>(true);
                if (_camera == null) _camera = UnityEngine.Camera.main;
            }

            if (_cameraTransform == null)
            {
                if (_camera != null)
                {
                    _cameraTransform = _camera.transform;
                }
                else
                {
                    Debug.LogError(
                        $"[{nameof(TopDownCameraRig)}] 没有找到任何 Camera。" +
                        "请把本组件挂在相机物体上（或相机是其子物体），或在 Inspector 里指定 _camera / _cameraTransform。", this);
                    enabled = false;
                    return;
                }
            }

            if (_target == null)
            {
                ResolveTarget();
            }
        }

        /// <summary>
        /// 解析跟随目标。只依赖显式注入的引用，不做运行时全场景查找 ——
        /// 隐式查找会在"忘了接线"时静默跟随错误对象，比 Inspector 空引用难查得多。
        /// </summary>
        private void ResolveTarget()
        {
            if (_target != null) return;

            if (_autoTargetSource != null)
            {
                _target = _autoTargetSource.transform;
            }
        }

        private void ApplyCameraSettings()
        {
            if (_camera == null) return;
            _camera.fieldOfView = _fieldOfView;
            _camera.nearClipPlane = _nearClipPlane;
            _camera.farClipPlane = _farClipPlane;
        }

        private void OnValidate()
        {
            _fieldOfView = Mathf.Clamp(_fieldOfView, 10f, 110f);
            _height = Mathf.Max(0.1f, _height);
            _backDistance = Mathf.Max(0f, _backDistance);
            _pitchLimits.x = Mathf.Clamp(_pitchLimits.x, 5f, 89f);
            _pitchLimits.y = Mathf.Clamp(_pitchLimits.y, _pitchLimits.x, 89f);

            if (isActiveAndEnabled)
            {
                RecalculateBoom();
                ApplyCameraSettings();
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (_target == null) return;

            Gizmos.color = new Color(0.2f, 1f, 0.4f, 1f);
            Vector3 lookPoint = _target.position + Vector3.up * _lookHeight + ResolveLookOffset();
            Gizmos.DrawWireSphere(lookPoint, 0.25f);
            Gizmos.DrawLine(_target.position, lookPoint);

            if (_cameraTransform != null)
            {
                Gizmos.color = new Color(1f, 0.6f, 0.1f, 1f);
                Gizmos.DrawLine(lookPoint, _cameraTransform.position);
            }
        }
    }
}
