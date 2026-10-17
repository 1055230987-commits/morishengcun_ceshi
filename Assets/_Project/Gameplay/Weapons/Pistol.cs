using Game.Gameplay.Combat;
using UnityEngine;

namespace Game.Gameplay.Weapons
{
    /// <summary>
    /// 手枪：射线命中（hitscan）的远程武器。
    ///
    /// 【为什么用射线而不是子弹实体】
    /// 手枪子弹快，实体子弹需要预判/穿透/弹道下坠，而视觉上玩家看不出区别。
    /// 射线是瞬时的、没有物理开销、判定精确。以后要做火箭筒/投掷物再另写一类武器，
    /// 不要把两种机制塞进同一个类。
    ///
    /// 【瞄准方式：鼠标指向哪、就打哪】
    /// 本工程是俯视斜视角（相机 pitch 62°），所以"屏幕中心"不等于"角色前方"。
    /// 用鼠标指针反投影到世界：
    ///   1. 先看指针下方有没有可击中目标（木桩）—— 打中就朝那个点打；
    ///   2. 没有目标就朝"指针在地面上的落点"打。
    /// 这样点到木桩身上就是瞄准木桩，点地面就是瞄准地面，符合俯视角的直觉。
    ///
    /// 【输入】复用 PlayerInputSource 的 Player/Attack 动作（鼠标左键），不新增输入资产。
    /// </summary>
    [DisallowMultipleComponent]
    public class Pistol : MonoBehaviour
    {
        [Header("输入")]
        [Tooltip("留空则自动在同物体/父物体上找 PlayerInputSource。")]
        [SerializeField] private Game.Player.PlayerInputSource _input;

        [Header("伤害")]
        [Tooltip("单发伤害。")]
        [SerializeField, Min(1f)] private float _damage = 25f;

        [Tooltip("射速（发/秒）。0.15 秒一发 ≈ 400 RPM。")]
        [SerializeField, Min(0.05f)] private float _fireInterval = 0.18f;

        [Tooltip("是否按住就连发。手枪应为 false（单发）。")]
        [SerializeField] private bool _automatic;

        [Header("射程与散布")]
        [Tooltip("最大射程（米）。超出这个距离的目标打不到。")]
        [SerializeField, Min(1f)] private float _range = 60f;

        [Tooltip("散布角度（度）。0 = 完全精准。手枪给一点点，显得不那么死板。")]
        [SerializeField, Range(0f, 15f)] private float _spreadDegrees = 0.6f;

        [Header("命中图层")]
        [Tooltip("射线会命中哪些层。默认排除玩家自己所在的层由代码处理。")]
        [SerializeField] private LayerMask _hitMask = ~0;

        [Header("表现")]
        [Tooltip("曳光弹显示时长（秒）。")]
        [SerializeField, Range(0.01f, 1f)] private float _tracerDuration = 0.045f;

        [Tooltip("曳光弹颜色。")]
        [SerializeField] private Color _tracerColor = new Color(1f, 0.92f, 0.55f, 1f);

        [Tooltip("枪口相对武器的位置（曳光弹从这里出发，看起来才像从枪口射出）。")]
        [SerializeField] private Vector3 _muzzleOffset = new Vector3(0f, 1.25f, 0.45f);

        [Header("调试")]
        [Tooltip("打开后打印每次开火的命中结果。")]
        [SerializeField] private bool _logShots;

        private Transform _muzzle;
        private LineRenderer _tracer;
        private float _nextFireTime;

        /// <summary>最近一次开火是否命中（供准星/音效以后接入）。</summary>
        public bool LastShotHit { get; private set; }

        /// <summary>最近一次命中的对象（可能是 null）。</summary>
        public GameObject LastHitObject { get; private set; }

        /// <summary>射速（发/秒），只读。</summary>
        public float FireRate => 1f / Mathf.Max(0.01f, _fireInterval);

        private void Awake()
        {
            if (_input == null) _input = GetComponentInParent<Game.Player.PlayerInputSource>();

            // 枪口用一个独立子物体，方便以后挂枪口火焰、抛壳。
            Transform muzzle = new GameObject("枪口").transform;
            muzzle.SetParent(transform, false);
            muzzle.localPosition = _muzzleOffset;
            _muzzle = muzzle;

            BuildTracer();
        }

        /// <summary>
        /// 曳光弹用 LineRenderer，而不是每次开火 Instantiate 一个预制体。
        /// 理由：手枪射速不低，反复实例化/销毁会产生 GC 抖动；
        /// 一条常驻的线改改端点就够了。
        /// </summary>
        private void BuildTracer()
        {
            GameObject go = new GameObject("曳光弹");
            go.transform.SetParent(transform, false);

            _tracer = go.AddComponent<LineRenderer>();
            _tracer.useWorldSpace = true;
            _tracer.positionCount = 2;
            _tracer.startWidth = 0.035f;
            _tracer.endWidth = 0.012f;
            _tracer.numCapVertices = 2;
            _tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _tracer.receiveShadows = false;
            _tracer.enabled = false;

            // 材质：用内置的 Unlit 颜色材质，避免依赖具体渲染管线里的 shader。
            // Sprites/Default 在所有管线下都能画出纯色线。
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");

            if (shader != null)
            {
                _tracer.material = new Material(shader) { name = "M_Tracer_Runtime" };
                _tracer.startColor = _tracerColor;
                _tracer.endColor = new Color(_tracerColor.r, _tracerColor.g, _tracerColor.b, 0.15f);
            }
            else
            {
                Debug.LogWarning("[手枪] 找不到可用的曳光弹 shader，命中判定仍然有效，但看不到弹道线。", this);
            }
        }

        private void Update()
        {
            if (_input == null) return;

            // 背包打开时不响应开火 —— 否则在界面里点鼠标会走火。
            if (IsBlockedByUi()) return;

            bool wantsFire = _automatic ? _input.AttackHeld : _input.AttackPressed;
            if (!wantsFire) return;

            if (Time.time < _nextFireTime) return;

            _nextFireTime = Time.time + _fireInterval;
            Fire();
        }

        /// <summary>
        /// 现在能不能开火。
        ///
        /// 【为什么走 InputModeState 而不是各自判断】
        /// 早先这里只检查"背包是否打开"，于是加了建造模式之后，
        /// 在建造界面里点鼠标会同时放置方块和开枪。
        /// 现在统一问 InputModeState —— 只有正常游玩模式允许开火。
        /// </summary>
        private bool IsBlockedByUi()
        {
            return !Game.Player.InputModeState.AllowsCombat;
        }

        /// <summary>开一枪。</summary>
        public void Fire()
        {
            UnityEngine.Camera camera = UnityEngine.Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[手枪] 场景里没有 MainCamera，无法瞄准。", this);
                return;
            }

            Vector3 origin = _muzzle != null ? _muzzle.position : transform.position;
            Vector3 target = ResolveAimPoint(camera);
            Vector3 direction = (target - origin).normalized;

            // 散布：在方向上叠一点随机偏转
            if (_spreadDegrees > 0.001f)
            {
                direction = ApplySpread(direction, _spreadDegrees);
            }

            float distance = _range;
            LastShotHit = false;
            LastHitObject = null;
            Vector3 endPoint = origin + direction * _range;

            // 射线检测。从枪口出发，避免子弹"穿过"贴脸的墙。
            if (Physics.Raycast(origin, direction, out RaycastHit hit, _range, _hitMask,
                    QueryTriggerInteraction.Ignore))
            {
                // 别打到自己身上
                if (!hit.collider.transform.IsChildOf(transform.root))
                {
                    distance = hit.distance;
                    endPoint = hit.point;
                    LastHitObject = hit.collider.gameObject;

                    IDamageable damageable = hit.collider.GetComponentInParent<IDamageable>();
                    if (damageable != null && damageable.IsAlive)
                    {
                        float applied = damageable.ApplyDamage(_damage);
                        LastShotHit = applied > 0f;

                        if (_logShots)
                        {
                            Debug.Log($"[手枪] 命中「{hit.collider.name}」，造成 {applied:F1} 点伤害" +
                                      $"（剩余 {(damageable.IsAlive ? "存活" : "已倒下")}）");
                        }
                    }
                    else if (_logShots)
                    {
                        Debug.Log($"[手枪] 命中「{hit.collider.name}」，但它不可受伤");
                    }
                }
            }
            else if (_logShots)
            {
                Debug.Log("[手枪] 未命中任何东西");
            }

            ShowTracer(origin, endPoint);

            _ = distance;
        }

        /// <summary>
        /// 求出"玩家想打哪里"。
        ///
        /// 优先命中指针下的可受伤目标 —— 这样点到木桩身上就是瞄准木桩，
        /// 而不需要玩家精确地把它和地面连线对齐（俯视角下两者差别很明显）。
        /// 没瞄准目标时，退化成"指针在地面上的落点"。
        /// </summary>
        private Vector3 ResolveAimPoint(UnityEngine.Camera camera)
        {
            Vector3 fallback = transform.position + transform.forward * _range;

            if (_input == null || !_input.CurrentIntent.HasAimScreenPosition) return fallback;

            Vector2 screen = _input.CurrentIntent.AimScreenPosition;
            Ray pointerRay = camera.ScreenPointToRay(screen);

            // 1) 指针下有没有可受伤目标？
            if (Physics.Raycast(pointerRay, out RaycastHit targetHit, 500f, _hitMask,
                    QueryTriggerInteraction.Ignore))
            {
                if (targetHit.collider.GetComponentInParent<IDamageable>() != null)
                {
                    return targetHit.point;
                }
            }

            // 2) 没有目标：取指针与"木桩高度那一层水平面"的交点。
            //    用地面（y=0）会让瞄准点在脚下，玩家得把鼠标压到木桩底部才打得中。
            //    取胸口高度更接近直觉。
            const float aimPlaneHeight = 1.0f;
            Plane plane = new Plane(Vector3.up, new Vector3(0f, aimPlaneHeight, 0f));

            if (plane.Raycast(pointerRay, out float enter)) return pointerRay.GetPoint(enter);

            return fallback;
        }

        /// <summary>在方向上叠加随机散布（角度制）。</summary>
        private static Vector3 ApplySpread(Vector3 direction, float degrees)
        {
            // 取一个与方向垂直的基，在这个平面内随机偏一个角度
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f
                ? Vector3.right
                : Vector3.up;

            Vector3 right = Vector3.Cross(up, direction).normalized;
            Vector3 realUp = Vector3.Cross(direction, right).normalized;

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float radius = Mathf.Tan(degrees * Mathf.Deg2Rad) * Random.Range(0f, 1f);

            return (direction + (right * Mathf.Cos(angle) + realUp * Mathf.Sin(angle)) * radius).normalized;
        }

        /// <summary>显示一次曳光弹。</summary>
        private void ShowTracer(Vector3 from, Vector3 to)
        {
            if (_tracer == null) return;

            _tracer.SetPosition(0, from);
            _tracer.SetPosition(1, to);
            _tracer.enabled = true;

            // 用协程而不是 Invoke，避免字符串查找；对象被禁用时协程自然停止。
            StopAllCoroutines();
            StartCoroutine(HideTracerAfterDelay());
        }

        private System.Collections.IEnumerator HideTracerAfterDelay()
        {
            yield return new WaitForSeconds(_tracerDuration);

            if (_tracer != null) _tracer.enabled = false;
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigurePistol(Game.Player.PlayerInputSource input, float damage,
            float fireInterval, float spreadDegrees = 0.6f)
        {
            _input = input;
            _damage = damage;
            _fireInterval = fireInterval;
            _spreadDegrees = spreadDegrees;
        }
#endif

        private void OnValidate()
        {
            _fireInterval = Mathf.Max(0.05f, _fireInterval);
            _range = Mathf.Max(1f, _range);
            _damage = Mathf.Max(1f, _damage);
        }
    }
}
