using Game.GameTime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Environment
{
    /// <summary>
    /// 昼夜后期处理：把"时间"翻译成 色调映射 / 曝光 / 对比 / 饱和 / 泛光 / 暗角 的变化。
    ///
    /// 为什么单独一个脚本：后期处理是可选的、耗性能的，不该和光照逻辑绑死。
    /// 不想要后期处理 → 直接删掉这个组件，光照照样工作。
    ///
    /// 关于 VolumeProfile：
    ///   运行时 new 出来的 Profile 不会存盘，重启就没了。
    ///   所以推荐用菜单 Game → Environment → Create Day-Night Environment 生成一个 .asset；
    ///   原资产只作模板，编辑器预览和运行时均使用独立副本，禁用时释放。
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [DefaultExecutionOrder(110)]
    public class DayNightPostProcessing : MonoBehaviour
    {
        [Header("时间来源")]
        [SerializeField] private TimeOfDaySystem _timeSystem;
        [Tooltip("留空则自动查找场景里的 DayNightLightingController。")]
        [SerializeField] private DayNightLightingController _lighting;

        [Header("Volume")]
        [Tooltip("留空则自动创建 / 查找 Global Volume。")]
        [SerializeField] private Volume _volume;
        [Tooltip("留空则使用运行时临时 Profile（不存盘）。")]
        [SerializeField] private VolumeProfile _profile;

        [Header("色调映射")]
        [SerializeField] private bool _useTonemapping = true;
        [SerializeField] private TonemappingMode _tonemappingMode = TonemappingMode.Neutral;

        [Header("颜色分级（暗部/亮部随时间变冷变暖）")]
        [SerializeField] private bool _useColorAdjustments = true;
        [Tooltip("黄昏时的暖色滤镜。")]
        [SerializeField] private Color _warmFilter = new Color(1f, 0.94f, 0.86f);
        [Tooltip("夜晚时的冷色滤镜。")]
        [SerializeField] private Color _coldFilter = new Color(0.86f, 0.92f, 1f);
        [Tooltip("黄昏时提高的对比度。")]
        [SerializeField, Range(-100f, 100f)] private float _duskContrast = 6f;
        [Tooltip("夜晚降低的亮度。")]
        [SerializeField, Range(-5f, 5f)] private float _nightPostExposure = -0.4f;

        [Header("泛光（Bloom，营造月光/火光辉光）")]
        [SerializeField] private bool _useBloom = true;
        [SerializeField, Range(0f, 3f)] private float _dayBloomIntensity = 0.06f;
        [SerializeField, Range(0f, 3f)] private float _nightBloomIntensity = 0.18f;
        [SerializeField, Range(0f, 4f)] private float _bloomThreshold = 1.2f;
        [SerializeField] private Color _bloomTint = Color.white;

        [Header("暗角（Vignette）")]
        [SerializeField] private bool _useVignette = true;
        [SerializeField, Range(0f, 1f)] private float _dayVignette = 0.08f;
        [SerializeField, Range(0f, 1f)] private float _nightVignette = 0.20f;
        [SerializeField] private Color _vignetteColor = Color.black;

        [Header("相机")]
        [Tooltip("自动打开主相机的 Post Processing 开关。")]
        [SerializeField] private bool _enableCameraPostProcessing = true;

        private Tonemapping _tonemapping;
        private ColorAdjustments _colorAdjustments;
        private Bloom _bloom;
        private Vignette _vignette;
        private bool _initialized;
        private VolumeProfile _instanceProfile;
        private VolumeProfile _previousInstance;
        private Volume _instanceVolume;

        public Volume Volume => _volume;
        public VolumeProfile Profile => _profile;

        private void Awake()
        {
            Initialize();
        }

        private void OnEnable()
        {
            Initialize();
        }

        private void Update()
        {
            if (!_initialized) Initialize();
            if (!_initialized) return;

            TimeOfDaySystem time = ResolveTimeSystem();
            if (time == null) return;

            DayNightCycle.State cycle = DayNightCycle.Evaluate(time.Hour);
            bool useLighting = _lighting != null && _lighting.isActiveAndEnabled;
            float night = useLighting ? _lighting.NightAmount : cycle.NightAmount;
            float dusk = useLighting ? _lighting.TwilightAmount : cycle.TwilightAmount;

            ApplyValues(night, dusk);
        }

        private void ApplyValues(float night, float dusk)
        {
            if (_tonemapping != null)
            {
                _tonemapping.active = _useTonemapping;
                _tonemapping.mode.value = _tonemappingMode;
            }

            if (_colorAdjustments != null)
            {
                _colorAdjustments.active = _useColorAdjustments;
                Color filter = Color.Lerp(Color.white, _warmFilter, dusk * 0.7f);
                filter = Color.Lerp(filter, _coldFilter, night * 0.6f);

                _colorAdjustments.colorFilter.value = filter;
                _colorAdjustments.contrast.value = _duskContrast * dusk;
                _colorAdjustments.postExposure.value = _nightPostExposure * night;
                _colorAdjustments.saturation.value = -6f * night;
            }

            if (_bloom != null)
            {
                _bloom.active = _useBloom;
                _bloom.intensity.value = Mathf.Lerp(_dayBloomIntensity, _nightBloomIntensity, night);
                _bloom.threshold.value = _bloomThreshold;
                _bloom.tint.value = _bloomTint;
            }

            if (_vignette != null)
            {
                _vignette.active = _useVignette;
                _vignette.intensity.value = Mathf.Lerp(_dayVignette, _nightVignette, night);
                _vignette.color.value = _vignetteColor;
            }
        }

        private void Initialize()
        {
            if (_initialized) return;

            if (_lighting == null)
            {
                Debug.LogWarning(
                    $"[{nameof(DayNightPostProcessing)}] 没有指定 _lighting（光照控制器）。" +
                    "后期处理将使用时间系统推算昼夜强度。", this);
            }

            if (_timeSystem == null)
            {
                Debug.LogWarning(
                    $"[{nameof(DayNightPostProcessing)}] 没有指定 _timeSystem（时间系统）。" +
                    "请执行菜单「游戏 → 环境 → 给当前场景接入昼夜」完成接线。", this);
            }

            EnsureVolume();
            EnsureComponents();
            EnableCameraPostProcessing();

            _initialized = _volume != null && _instanceProfile != null;
        }

        /// <summary>时间系统只认显式注入的引用，不做运行时全场景查找。</summary>
        private TimeOfDaySystem ResolveTimeSystem()
        {
            return _timeSystem;
        }

        private void EnsureVolume()
        {
            if (_volume == null)
            {
                // 只认自己管理的那一个：先看自己，再看名为"全局Volume"的子物体，都没有就新建一个。
                // 刻意不退回"场景里任意 Volume"：那会误改到别的模块的 Volume（例如模板自带的 SampleSceneProfile）。
                _volume = GetComponent<Volume>();

                if (_volume == null)
                {
                    Transform child = transform.Find("全局Volume");
                    if (child != null) _volume = child.GetComponent<Volume>();
                }

                if (_volume == null)
                {
                    GameObject go = new GameObject("全局Volume");
                    go.transform.SetParent(transform, false);
                    _volume = go.AddComponent<Volume>();
                }
            }

            _volume.isGlobal = true;
            _volume.priority = 1f;

            if (_instanceProfile != null) return;
            VolumeProfile source = _profile != null ? _profile : _volume.sharedProfile;
            _previousInstance = _volume.HasInstantiatedProfile() ? _volume.profile : null;
            _instanceVolume = _volume;
            _instanceProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            _instanceProfile.name = "DayNightProfile_Instance";
            _instanceProfile.hideFlags = HideFlags.HideAndDontSave;
            if (source != null)
            {
                foreach (VolumeComponent component in source.components)
                {
                    if (component == null) continue;
                    VolumeComponent copy = Instantiate(component);
                    copy.hideFlags = HideFlags.HideAndDontSave;
                    _instanceProfile.components.Add(copy);
                }
            }
            _volume.profile = _instanceProfile;
        }

        private void OnDisable() => ReleaseProfile();
        private void OnDestroy() => ReleaseProfile();

        private void ReleaseProfile()
        {
            if (_instanceProfile != null)
            {
                if (_instanceVolume != null && _instanceVolume.HasInstantiatedProfile() &&
                    _instanceVolume.profile == _instanceProfile)
                    _instanceVolume.profile = _previousInstance;
                foreach (VolumeComponent component in _instanceProfile.components)
                    CoreUtils.Destroy(component);
                CoreUtils.Destroy(_instanceProfile);
            }
            _instanceProfile = null;
            _instanceVolume = null;
            _previousInstance = null;
            _tonemapping = null;
            _colorAdjustments = null;
            _bloom = null;
            _vignette = null;
            _initialized = false;
        }
        private void EnsureComponents()
        {
            if (_instanceProfile == null) return;

            if (_tonemapping == null)
            {
                if (!_instanceProfile.TryGet(out _tonemapping))
                {
                    _tonemapping = _instanceProfile.Add<Tonemapping>(true);
                }
            }

            if (_colorAdjustments == null)
            {
                if (!_instanceProfile.TryGet(out _colorAdjustments))
                {
                    _colorAdjustments = _instanceProfile.Add<ColorAdjustments>(true);
                }
            }

            if (_bloom == null)
            {
                if (!_instanceProfile.TryGet(out _bloom))
                {
                    _bloom = _instanceProfile.Add<Bloom>(true);
                }
            }

            if (_vignette == null)
            {
                if (!_instanceProfile.TryGet(out _vignette))
                {
                    _vignette = _instanceProfile.Add<Vignette>(true);
                }
            }

            // 模板可能关闭过 override；昼夜模块明确接管的参数必须实际参与混合。
            _tonemapping.mode.overrideState = true;
            _colorAdjustments.colorFilter.overrideState = true;
            _colorAdjustments.contrast.overrideState = true;
            _colorAdjustments.postExposure.overrideState = true;
            _colorAdjustments.saturation.overrideState = true;
            _bloom.intensity.overrideState = true;
            _bloom.threshold.overrideState = true;
            _bloom.tint.overrideState = true;
            _vignette.intensity.overrideState = true;
            _vignette.color.overrideState = true;
            _tonemapping.active = _useTonemapping;
            _colorAdjustments.active = _useColorAdjustments;
            _bloom.active = _useBloom;
            _vignette.active = _useVignette;
        }

        private void EnableCameraPostProcessing()
        {
            if (!_enableCameraPostProcessing) return;

            Camera camera = Camera.main;
            if (camera == null) return;

            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                data.renderPostProcessing = true;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器工具用：把生成好的 Profile / Volume 接上（运行时不需要调用）。
        /// </summary>
        public void Setup(VolumeProfile profile, Volume volume)
        {
            ReleaseProfile();
            _profile = profile;
            _volume = volume;
            _initialized = false;
        }
#endif
    }
}
