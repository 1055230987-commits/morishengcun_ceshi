using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 房门：平滑旋转开合，不依赖物理。
    ///
    /// 【为什么不用刚体】
    /// 试过刚体 + 铰链，在"门框和门板几乎等宽"的几何下必然抖动：
    /// 门板两侧和门框只有几厘米间隙，刚体一被推动就会左右碰擦，
    /// 弹簧再把它拉回来，形成持续的碰撞抖动。这是刚体门的典型难题，
    /// 不是把参数调一调就能解决的（除非把门框做宽、或给门加专门的物理层与碰撞忽略）。
    ///
    /// 【现在的做法】角度完全由代码驱动：
    ///   玩家进入 _openDistance 范围内 → 平滑转向"开"
    ///   玩家离开 _openDistance 持续 _closeDelay 秒 → 平滑转向"关"
    /// 观感可预测、绝不抖动，而且随时可以换成真正的物理门（换组件即可，不影响建筑）。
    ///
    /// 【铰链位置】本组件挂在【铰链那个点】上，门板是它的子物体并向一侧偏移。
    /// 绕自身 Y 轴旋转就等于绕铰链转，这也是真人门的转法。
    /// </summary>
    [DisallowMultipleComponent]
    public class HingedDoor : MonoBehaviour
    {
        [Header("开合")]
        [Tooltip("开门方向：1 = 绕 Y 轴正方向开，-1 = 反方向。门开反了改这个。")]
        [SerializeField] private float _openDirection = 1f;

        [Tooltip("完全打开时的角度（度）。")]
        [SerializeField, Range(20f, 170f)] private float _openAngle = 88f;

        [Tooltip("开关门速度（度/秒）。越大越利落。")]
        [SerializeField, Min(10f)] private float _rotateSpeed = 260f;

        [Header("触发条件")]
        [Tooltip("玩家进入这个距离（米）内，门开始打开。")]
        [SerializeField, Min(0.3f)] private float _openDistance = 2.2f;

        [Tooltip("玩家离开 _openDistance 后，再等多久开始关门（秒）。")]
        [SerializeField, Min(0f)] private float _closeDelay = 1.2f;

        [Tooltip("用来判断距离的玩家。留空则按 Tag 自动查找。")]
        [SerializeField] private Transform _player;

        [Tooltip("玩家 Tag。用于自动查找 _player。")]
        [SerializeField] private string _playerTag = "Player";

        [Tooltip("只考虑水平距离（忽略高度差）。玩家在二楼时不该把一楼的门打开，所以一般保持勾选。")]
        [SerializeField] private bool _ignoreHeight = true;

        [Header("初始状态")]
        [SerializeField] private bool _startOpen = false;

        [Header("调试")]
        [Tooltip("在 Console 打印门的开合状态变化。")]
        [SerializeField] private bool _logState = false;

        private float _closedYaw;
        private float _currentYaw;
        private float _outOfRangeTime;
        private bool _wasOpen;

        /// <summary>当前是否处于"开着"的状态（角度超过一半）。</summary>
        public bool IsOpen => Mathf.Abs(Mathf.DeltaAngle(_currentYaw, _closedYaw)) > _openAngle * 0.5f;

        /// <summary>当前相对关闭状态的角度（度，带符号）。</summary>
        public float OpenAngle => Mathf.DeltaAngle(_closedYaw, _currentYaw);

        private void Awake()
        {
            _closedYaw = transform.localEulerAngles.y;
            _currentYaw = _closedYaw;

            if (_startOpen)
            {
                _currentYaw = _closedYaw + _openAngle * Mathf.Sign(_openDirection);
                ApplyYaw();
            }
        }

        private void Update()
        {
            float target = ResolveTargetYaw();

            // 用 MoveTowards 而不是 Lerp：速度恒定，不受帧率影响，也不会"永远差一点"。
            _currentYaw = Mathf.MoveTowardsAngle(_currentYaw, target, _rotateSpeed * Time.deltaTime);
            ApplyYaw();

            UpdateLog();
        }

        /// <summary>算出这一帧应该朝向的目标角度。</summary>
        private float ResolveTargetYaw()
        {
            Transform player = ResolvePlayer();
            bool inRange = false;

            if (player != null)
            {
                Vector3 delta = player.position - transform.position;
                if (_ignoreHeight) delta.y = 0f;

                inRange = delta.sqrMagnitude <= _openDistance * _openDistance;
            }

            if (inRange)
            {
                _outOfRangeTime = 0f;
                return _closedYaw + _openAngle * Mathf.Sign(_openDirection);
            }

            // 离开范围后先等 _closeDelay 秒再关，避免玩家在门口来回走时门一直开合。
            _outOfRangeTime += Time.deltaTime;
            if (_outOfRangeTime < _closeDelay)
            {
                return _closedYaw + _openAngle * Mathf.Sign(_openDirection);
            }

            return _closedYaw;
        }

        private Transform ResolvePlayer()
        {
            if (_player != null) return _player;
            if (string.IsNullOrEmpty(_playerTag)) return null;

            // 按 Tag 找一次并缓存。找不到就返回 null（门保持关闭，不会报错）。
            GameObject found = GameObject.FindGameObjectWithTag(_playerTag);
            if (found != null) _player = found.transform;

            return _player;
        }

        private void ApplyYaw()
        {
            Vector3 euler = transform.localEulerAngles;
            euler.y = _currentYaw;
            transform.localEulerAngles = euler;
        }

        /// <summary>立刻把门置为关闭状态。</summary>
        public void Close()
        {
            _currentYaw = _closedYaw;
            _outOfRangeTime = 0f;
            ApplyYaw();
        }

        /// <summary>立刻把门置为打开状态。</summary>
        public void Open()
        {
            _currentYaw = _closedYaw + _openAngle * Mathf.Sign(_openDirection);
            _outOfRangeTime = 0f;
            ApplyYaw();
        }

        /// <summary>以后做"上锁/需要钥匙"时在这里扩展。目前门永远可以推开。</summary>
        public bool IsLocked { get; set; }

        private void UpdateLog()
        {
            if (!_logState) return;

            bool open = IsOpen;
            if (open == _wasOpen) return;

            _wasOpen = open;
            Debug.Log($"[房门]「{name}」{(open ? "已打开" : "已关上")}（角度 {OpenAngle:F1}°）", this);
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器用来配置门。</summary>
        public void ConfigureDoor(float openDirection, float openAngle, float openDistance, float closeDelay)
        {
            _openDirection = openDirection;
            _openAngle = openAngle;
            _openDistance = openDistance;
            _closeDelay = closeDelay;
        }
#endif

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 0.12f);
            Gizmos.DrawRay(transform.position, transform.right * 1.2f);

            if (_openDistance > 0.001f)
            {
                Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.35f);
                Gizmos.DrawWireSphere(transform.position, _openDistance);
            }
        }
    }
}
