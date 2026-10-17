using Game.Environment;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 路灯：入夜自动点亮、天亮自动熄灭，并且可以用 E 手动开关。
    ///
    /// 【为什么继承 InteractableBase】
    /// 交互探测走的是 Physics.OverlapSphere + GetComponentInParent&lt;InteractableBase&gt;()，
    /// 所以继承它就能直接接入现有的"走近按 E"体系 —— 提示文案、按键、探测全都不用新写。
    ///
    /// 【三种状态】
    ///   Auto     —— 跟随昼夜（默认）：入夜亮、天亮灭
    ///   ForcedOn —— 玩家手动开的（白天也亮）
    ///   ForcedOff—— 玩家手动关的（夜里也灭）
    /// 手动状态优先于自动；这样玩家关掉的灯不会在下一帧被"自动逻辑"重新点亮。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public class StreetLamp : InteractableBase
    {
        public enum LampState
        {
            /// <summary>跟随昼夜自动开关。</summary>
            Auto,

            /// <summary>玩家强制打开。</summary>
            ForcedOn,

            /// <summary>玩家强制关闭。</summary>
            ForcedOff
        }

        [Header("来源")]
        [Tooltip("昼夜光照控制器。留空则自动在场景里找。")]
        [SerializeField] private DayNightLightingController _lighting;

        [Header("自动开关")]
        [Tooltip("环境暗到这个程度开始点亮（1 = 全黑）。")]
        [SerializeField, Range(0f, 1f)] private float _onThreshold = 0.62f;

        [Tooltip("阈值两侧的过渡宽度。避免黄昏时灯一闪一闪。")]
        [SerializeField, Range(0.01f, 0.5f)] private float _fadeWidth = 0.12f;

        [Header("亮度")]
        [Tooltip("点亮时的强度。")]
        [SerializeField, Min(0f)] private float _maxIntensity = 3.2f;

        [Tooltip("开关过渡速度（强度/秒）。0 = 瞬间切换。")]
        [SerializeField, Min(0f)] private float _fadeSpeed = 4f;

        [Header("夜间闪烁")]
        [Tooltip("老式路灯的轻微闪烁幅度（0 = 不闪）。")]
        [SerializeField, Range(0f, 0.5f)] private float _flickerAmount = 0.06f;

        [Tooltip("闪烁速度。")]
        [SerializeField, Min(0f)] private float _flickerSpeed = 9f;

        private Light _light;
        private LampState _state = LampState.Auto;
        private float _currentIntensity;
        private float _flickerSeed;

        /// <summary>当前状态（自动 / 强制开 / 强制关）。</summary>
        public LampState State => _state;

        /// <summary>灯此刻是否亮着（按实际强度判断，不是按状态）。</summary>
        public bool IsLit => _light != null && _light.enabled && _currentIntensity > 0.05f;

        /// <summary>是否处于"跟随昼夜"的自动状态。</summary>
        public bool IsAuto => _state == LampState.Auto;

        public override string InteractionPrompt
        {
            get
            {
                if (_state == LampState.Auto) return IsLit ? "关掉路灯" : "打开路灯";
                return _state == LampState.ForcedOn ? "恢复自动（当前强制开）" : "恢复自动（当前强制关）";
            }
        }

        private void Awake()
        {
            _light = GetComponent<Light>();
            _currentIntensity = _light != null ? _light.intensity : 0f;

            // 用位置做种子，让每盏灯的闪烁不同步（否则整条街一起闪，很假）。
            _flickerSeed = (transform.position.x * 7.31f + transform.position.z * 3.17f) % 100f;
        }

        protected override InteractionResult OnInteract(GameObject actor)
        {
            // 循环：自动 → 强制开 → 强制关 → 自动
            switch (_state)
            {
                case LampState.Auto:
                    _state = LampState.ForcedOn;
                    return InteractionResult.Success("路灯：强制打开");

                case LampState.ForcedOn:
                    _state = LampState.ForcedOff;
                    return InteractionResult.Success("路灯：强制关闭");

                default:
                    _state = LampState.Auto;
                    return InteractionResult.Success("路灯：恢复自动");
            }
        }

        private void Update()
        {
            if (_light == null) return;

            float target = ResolveTargetIntensity();

            // 平滑过渡（0 速度则瞬间到位）
            _currentIntensity = _fadeSpeed <= 0.001f
                ? target
                : Mathf.MoveTowards(_currentIntensity, target, _fadeSpeed * Time.deltaTime);

            // 闪烁：只在有一定亮度时叠加，且用正弦噪声避免规律感。
            float flicker = 0f;
            if (_flickerAmount > 0.0001f && _currentIntensity > 0.05f)
            {
                float t = Time.time * _flickerSpeed + _flickerSeed;
                flicker = (Mathf.PerlinNoise(t, 0.37f) - 0.5f) * 2f * _flickerAmount;
            }

            _light.intensity = Mathf.Max(0f, _currentIntensity * (1f + flicker));
            _light.enabled = _light.intensity > 0.01f;
        }

        /// <summary>算出这一帧应该达到的强度（未经过渡）。</summary>
        private float ResolveTargetIntensity()
        {
            switch (_state)
            {
                case LampState.ForcedOn:
                    return _maxIntensity;

                case LampState.ForcedOff:
                    return 0f;

                default:
                    // 自动：按"环境有多暗"决定。
                    // 用一条从 _onThreshold - _fadeWidth 到 _onThreshold + _fadeWidth 的
                    // 平滑过渡，避免黄昏时灯在阈值附近来回闪。
                    if (_lighting == null)
                    {
                        _lighting = FindFirstObjectByType<DayNightLightingController>();
                        if (_lighting == null) return 0f;
                    }

                    float darkness = Mathf.Clamp01(_lighting.NightAmount);
                    float from = _onThreshold - _fadeWidth;
                    float span = Mathf.Max(0.01f, _fadeWidth * 2f);

                    return _maxIntensity * Mathf.Clamp01((darkness - from) / span);
            }
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureLamp(DayNightLightingController lighting, float maxIntensity, float range,
            float onThreshold = 0.62f)
        {
            _lighting = lighting;
            _maxIntensity = maxIntensity;
            _onThreshold = onThreshold;

            _light = GetComponent<Light>();
            _light.type = LightType.Point;
            _light.range = range;
            _light.color = new Color(1f, 0.88f, 0.68f);   // 暖黄，和老式路灯接近
            _light.shadows = LightShadows.Soft;
            _light.shadowStrength = 0.75f;
        }
#endif
    }
}
