using System;
using Game.GameTime;
using UnityEngine;

namespace Game.Environment
{
    /// <summary>
    /// 一个时间点对应一种颜色（比 Gradient 更直观、更好在 Inspector 里调）。
    /// </summary>
    [Serializable]
    public struct ColorKey
    {
        [Range(0f, 1f)] public float Time;
        public Color Color;

        public ColorKey(float time, Color color)
        {
            Time = time;
            Color = color;
        }
    }

    /// <summary>
    /// 昼夜光照与环境控制器（写实向的"动态光照模块"主脚本）。
    ///
    /// 它做这些事，全部由 TimeOfDaySystem 的时间驱动：
    ///   1. 太阳方向/颜色/强度随时间变化（东升西落，黎明橙、正午白、黄昏红）
    ///   2. 月亮方向/颜色/强度（夜间补光，不是主光源）
    ///   3. 天空盒：颜色、曝光、大气厚度随时间变化
    ///   4. 星空：夜间淡入，跟随相机
    ///   5. 月亮圆盘：夜间淡入，挂在天空上
    ///   6. 雾：颜色与浓度随时间变化（末日灰霾感，同时藏住地图边界）
    ///   7. 环境光（Ambient）：天空色带一点点余晖
    ///
    /// 设计取舍（有意为之）：
    ///   - 优先使用 Game/ProceduralSkyCelestial，未配置时回退到 Skybox/Procedural。
    ///     写实度够用、性能好、参数可调，而且不会因为 shader 写错整个场景变粉。
    ///   - 不做体积云/体积雾/光追阴影：中低端设备直接跪，俯视角也看不出好处。
    ///   - 参数全部在这里的 Inspector 上（渐变/曲线），不需要改代码。
    ///
    /// 玩法接入（不在本脚本里，避免耦合）：
    ///   var t = GetComponent<TimeOfDaySystem>();
    ///   t.OnPhaseChanged += phase => Debug.Log($"阶段变化：{phase}");
    ///   if (t.IsNight) { /* 刷怪、降温度、开手电 */ }
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [DefaultExecutionOrder(100)]
    public class DayNightLightingController : MonoBehaviour
    {
        [Header("时间来源")]
        [Tooltip("留空则自动查找场景里的 TimeOfDaySystem。")]
        [SerializeField] private TimeOfDaySystem _timeSystem;

        [Header("光源引用（留空则自动查找或创建）")]
        [SerializeField] private Light _sunLight;
        [SerializeField] private Light _moonLight;

        [Header("太阳")]
        [Tooltip("整条日月轨道绕世界 Y 轴的方位角。改变它会同时旋转日出、正午与日落方向。")]
        [SerializeField, Range(0f, 360f)] private float _sunYaw = 210f;
        [SerializeField] private ColorKey[] _sunColorKeys = DefaultSunColors();
        [SerializeField] private AnimationCurve _sunIntensityCurve = DefaultSunIntensity();
        [Tooltip("正午太阳最高角度。轨道以 05:00 日出、12:00 正午、19:00 日落为基准。")]
        [SerializeField, Range(5f, 85f)] private float _maximumSunElevation = 65f;

        [Header("月亮")]
        [SerializeField] private Color _moonColor = new Color(0.64f, 0.72f, 0.95f);
        [SerializeField, Range(0f, 1f)] private float _moonMaxIntensity = 0.045f;

        [Header("天空材质（仅作模板，预览和运行使用副本）")]
        [Tooltip("留空则自动查找/创建 Skybox/Procedural 材质。")]
        [SerializeField] private Material _skyboxMaterial;
        [SerializeField] private ColorKey[] _skyTintKeys = DefaultSkyTints();
        [SerializeField] private ColorKey[] _equatorTintKeys = DefaultEquatorTints();
        [SerializeField] private Color _groundTint = new Color(0.16f, 0.15f, 0.14f);
        [SerializeField, Range(0.2f, 6f)] private float _dayAtmosphereThickness = 1.0f;
        [SerializeField, Range(0.2f, 6f)] private float _nightAtmosphereThickness = 0.45f;
        [SerializeField, Range(0.2f, 5f)] private float _dayExposure = 1.35f;
        [SerializeField, Range(0.2f, 5f)] private float _nightExposure = 0.22f;

        [Header("粒子星空（默认关闭，见下方说明）")]
        [Tooltip("默认【关闭】。理由：自定义天空盒 Game/ProceduralSkyCelestial 已经画了程序化星星，" +
                 "两套星星同时存在是重复的，而且粒子这套依赖 Shader.Find，在编辑器里不稳定。\n" +
                 "如果你用的是普通 Skybox/Procedural（没有星星），把这个打开即可补上星空。")]
        [SerializeField] private bool _createStars = false;
        [SerializeField] private int _starCount = 900;
        [SerializeField, Min(1f)] private float _starRadius = 220f;
        [SerializeField] private Color _starColor = new Color(1f, 0.97f, 0.9f);
        [SerializeField, Range(0f, 4f)] private float _starBrightness = 1.6f;
        [Tooltip("自定义天空盒（Game/ProceduralSkyCelestial）里程序化星星的亮度。\n" +
                 "用粒子星空时这个值不起作用；两者可以同时存在，但一般只留一个。")]
        [SerializeField, Range(0f, 4f)] private float _shaderStarBrightness = 1.1f;

        [Header("月亮圆盘（网格版，默认关闭，见下方说明）")]
        [Tooltip("默认【关闭】。理由同粒子星空：自定义天空盒已经画了月亮圆盘，" +
                 "网格球体是重复的，而且它的亮度靠改材质颜色实现，观感更差。\n" +
                 "如果你用的是普通 Skybox/Procedural（没有月亮），把这个打开即可补上。")]
        [SerializeField] private bool _createMoonDisc = false;
        [SerializeField, Min(1f)] private float _moonDiscDistance = 180f;
        [SerializeField, Min(0.1f)] private float _moonDiscSize = 14f;

        [Header("雾（末日灰霾 + 藏住地图边界）")]
        [SerializeField] private bool _controlFog = true;
        [SerializeField] private ColorKey[] _fogColorKeys = DefaultFogColors();
        [SerializeField, Range(0f, 0.2f)] private float _dayFogDensity = 0.006f;
        [SerializeField, Range(0f, 0.2f)] private float _nightFogDensity = 0.012f;

        [Header("环境光（Ambient）")]
        [Tooltip("夜间半球环境光强度。直接缩放天空、水平线、地面三个方向的补光颜色，保留暗部可读性。")]
        [SerializeField, Range(0f, 1f)] private float _minAmbientIntensity = 0.025f;

        [Tooltip("白天半球环境光强度。顶部接收较亮冷色天光，侧面和底部补光较弱，避免把物体照平。")]
        [SerializeField, Range(0f, 1f)] private float _dayAmbientIntensity = 0.18f;

        [Tooltip("是否覆盖场景的 Ambient 设置。关闭则完全用 Lighting 窗口里的值。")]
        [SerializeField] private bool _overrideAmbient = true;

        [Header("天空跟随目标（星空/月亮圆盘挂在这个物体上，一般就是相机）")]
        [SerializeField] private Transform _skyFollowTarget;

        private ParticleSystem _stars;
        private Transform _starsTransform;
        private Transform _moonDisc;
        private Renderer _moonDiscRenderer;
        private MaterialPropertyBlock _propertyBlock;
        private bool _warnedNoSkyShader;
        private bool _loggedState;
        private Material _skyInstance;
        private Material _skySource;
        private Material _previousSkybox;
        private static readonly int NightAmountProperty = Shader.PropertyToID("_NightAmount");
        public float TwilightAmount { get; private set; }
        public Vector3 SunDirection { get; private set; }
        public Vector3 MoonDirection => -SunDirection;

        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int BaseColorProperty = Shader.PropertyToID("_BaseColor");
        private static readonly int SkyTintProperty = Shader.PropertyToID("_SkyTint");
        private static readonly int EquatorTintProperty = Shader.PropertyToID("_EquatorColor");
        private static readonly int GroundColorProperty = Shader.PropertyToID("_GroundColor");
        private static readonly int AtmosphereProperty = Shader.PropertyToID("_AtmosphereThickness");
        private static readonly int ExposureProperty = Shader.PropertyToID("_Exposure");

        // 自定义天空盒（Game/ProceduralSkyCelestial）专用参数。
        private static readonly int SunDirectionProperty = Shader.PropertyToID("_SunDirection");
        private static readonly int SunColorProperty = Shader.PropertyToID("_SunColor");
        private static readonly int MoonDirectionProperty = Shader.PropertyToID("_MoonDirection");
        private static readonly int MoonVisibilityProperty = Shader.PropertyToID("_MoonVisibility");
        private static readonly int DaySkyColorProperty = Shader.PropertyToID("_DaySkyColor");
        private static readonly int NightSkyColorProperty = Shader.PropertyToID("_NightSkyColor");
        private static readonly int DayHorizonColorProperty = Shader.PropertyToID("_DayHorizonColor");
        private static readonly int NightHorizonColorProperty = Shader.PropertyToID("_NightHorizonColor");
        private static readonly int SunsetHorizonColorProperty = Shader.PropertyToID("_SunsetHorizonColor");
        private static readonly int StarBrightnessProperty = Shader.PropertyToID("_StarBrightness");

        /// <summary>夜间强度 0~1（0 = 全亮白天，1 = 全黑夜晚）。后期处理模块也读它。</summary>
        public float NightAmount { get; private set; }

        /// <summary>太阳当前颜色（后期处理/调试用）。</summary>
        public Color SunColor { get; private set; } = Color.white;

        /// <summary>太阳当前高度角（度，负值 = 在地平线下）。</summary>
        public float SunElevation { get; private set; }

        private void Awake()
        {
            ResolveReferences();
            EnsureSkyMaterial();
            EnsureStars();
            EnsureMoonDisc();
        }

        private void OnEnable()
        {
            ResolveReferences();
            EnsureSkyMaterial();
            EnsureStars();
            EnsureMoonDisc();
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= BeforeSceneSave;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= AfterSceneSave;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += BeforeSceneSave;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += AfterSceneSave;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= BeforeSceneSave;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= AfterSceneSave;
#endif
            ReleaseSkyMaterial();
        }

        private void OnDestroy() => ReleaseSkyMaterial();

#if UNITY_EDITOR
        private void BeforeSceneSave(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (scene == gameObject.scene && RenderSettings.skybox == _skyInstance)
                RenderSettings.skybox = _previousSkybox;
        }

        private void AfterSceneSave(UnityEngine.SceneManagement.Scene scene)
        {
            if (scene == gameObject.scene && isActiveAndEnabled && _skyInstance != null)
                RenderSettings.skybox = _skyInstance;
        }
#endif
        private void Update()
        {
            EnsureSkyMaterial();
            TimeOfDaySystem time = ResolveTimeSystem();
            if (time == null)
            {
                time = GetComponent<TimeOfDaySystem>();
                if (time != null) _timeSystem = time;
            }
            if (time == null)
            {
                if (!Application.isPlaying) return;
                // 兜底：场景里没有时间系统就自己建一个。
                // 否则光照会完全停止更新，画面就停在"上一次保存下来的时刻"（如果上次存的是夜里，白天也是黑的）。
                time = gameObject.AddComponent<TimeOfDaySystem>();
                _timeSystem = time;
                Debug.LogWarning(
                    $"[昼夜光照] 场景里原本没有 TimeOfDaySystem，已自动补上一个（默认 9:00 开始）。" +
                    "如果需要存档/结算等功能，请执行菜单「游戏 → 环境 → 给当前场景接入昼夜」正式接入。", this);
            }

            ApplyLighting(time);
            LogStateOnce(time);
        }

        /// <summary>启动时打印一次光照实况，用于快速定位"白天也是黑的"这类问题。</summary>
        private void LogStateOnce(TimeOfDaySystem time)
        {
            if (_loggedState || !Application.isPlaying) return;

            _loggedState = true;

            string sunState = _sunLight == null
                ? "**没有引用到太阳灯**"
                : $"{(_sunLight.enabled ? "启用" : "**已禁用**")}｜强度 {_sunLight.intensity:F2}｜" +
                  $"光照方向 {_sunLight.transform.forward}";

            string alignment = "（无法判断：没有主相机或太阳）";
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam != null && _sunLight != null)
            {
                float dot = Vector3.Dot(cam.transform.forward, _sunLight.transform.forward);
                alignment = dot < -0.5f
                    ? $"相机与太阳方向重合度 {dot:F2} —— **逆光/正面无受光**，画面会显得没有光照。" +
                      "把 _sunYaw 转到 90°~270° 之间（例如 210°），让太阳从侧面打过来。"
                    : $"相机与太阳方向重合度 {dot:F2}（正常）";
            }

            Debug.Log(
                $"[昼夜光照] 已接管光照：第 {time.DayNumber} 天 {time.Hour:F1} 点（{time.Phase}）\n" +
                $"  太阳：高度角 {SunElevation:F1}°｜yaw {_sunYaw}°｜{sunState}\n" +
                $"  颜色 {SunColor}｜夜晚程度 {NightAmount:F2}\n" +
                $"  环境光 {RenderSettings.ambientMode}｜天空 {RenderSettings.ambientSkyColor}｜地面 {RenderSettings.ambientGroundColor}\n" +
                $"  雾：开关 {RenderSettings.fog}｜浓度 {RenderSettings.fogDensity:F4}｜颜色 {RenderSettings.fogColor}\n" +
                $"  方向诊断：{alignment}", this);
        }

        // ------------------------------------------------------------------ 主流程

        private void ApplyLighting(TimeOfDaySystem time)
        {
            float t = time.NormalizedTime;
            DayNightCycle.State cycle = DayNightCycle.Evaluate(time.Hour, _maximumSunElevation);
            float sunElevation = cycle.Elevation;
            SunElevation = sunElevation;
            NightAmount = cycle.NightAmount;
            TwilightAmount = cycle.TwilightAmount;
            SunDirection = Quaternion.Euler(0f, _sunYaw, 0f) * new Vector3(cycle.X, cycle.Y, cycle.Z);

            UpdateSun(sunElevation, t);
            UpdateMoon();
            UpdateSky(t);
            UpdateFog(t);
            UpdateAmbient(t);
            UpdateSkyFollowers();
        }

        private void UpdateSun(float sunElevation, float t)
        {
            SunColor = EvaluateColorKeys(_sunColorKeys, t, Color.white);
            if (_sunLight == null) return;

            _sunLight.transform.rotation = Quaternion.LookRotation(-SunDirection, Vector3.up);

            // 地平线附近压暗，避免日出日落时突然一束强光怼脸。
            float horizonFade = Mathf.Clamp01(Mathf.InverseLerp(-2f, 6f, sunElevation));
            float daylight = 1f - NightAmount;

            // 注意：所有颜色/强度曲线统一使用【纯归一化时间】（0 = 00:00，0.5 = 12:00）。
            // 早先这里写成 Repeat(t + 0.5f, 1f)，和强度曲线用的是两套坐标，
            // 结果颜色和亮度整体错位半天（正午的白色被排到了午夜）。
            float intensity = EvaluateCurve(_sunIntensityCurve, t, 1f);
            _sunLight.intensity = Mathf.Max(0f, intensity) * horizonFade * daylight;

            Color color = EvaluateColorKeys(_sunColorKeys, t, Color.white);
            SunColor = color;
            _sunLight.color = color;
        }

        private void UpdateMoon()
        {
            if (_moonLight == null) return;

            _moonLight.transform.rotation = Quaternion.LookRotation(-MoonDirection, Vector3.up);
            _moonLight.color = _moonColor;
            _moonLight.intensity = _moonMaxIntensity * NightAmount * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f, 0.1f, MoonDirection.y));
        }

        private void UpdateSky(float t)
        {
            if (_skyInstance == null) return;

            float night = NightAmount;

            if (_skyInstance.HasProperty(SkyTintProperty))
            {
                _skyInstance.SetColor(SkyTintProperty, EvaluateColorKeys(_skyTintKeys, t, Color.white));
            }

            if (_skyInstance.HasProperty(EquatorTintProperty))
            {
                _skyInstance.SetColor(EquatorTintProperty, EvaluateColorKeys(_equatorTintKeys, t, Color.white));
            }

            if (_skyInstance.HasProperty(GroundColorProperty))
            {
                _skyInstance.SetColor(GroundColorProperty, _groundTint);
            }

            if (_skyInstance.HasProperty(AtmosphereProperty))
            {
                _skyInstance.SetFloat(AtmosphereProperty, Mathf.Lerp(_dayAtmosphereThickness, _nightAtmosphereThickness, night));
            }

            if (_skyInstance.HasProperty(ExposureProperty))
            {
                _skyInstance.SetFloat(ExposureProperty, Mathf.Lerp(_dayExposure, _nightExposure, night));
            }

            UpdateCelestialBodies(t, night);
        }

        /// <summary>预览和运行共用临时材质，天体方向来自同一轨道。</summary>
        private void UpdateCelestialBodies(float t, float night)
        {
            if (_skyInstance.HasProperty(NightAmountProperty)) _skyInstance.SetFloat(NightAmountProperty, night);

            if (_skyInstance.HasProperty(SunDirectionProperty))
            {
                Vector3 sunForward = SunDirection;
                _skyInstance.SetVector(SunDirectionProperty, new Vector4(sunForward.x, sunForward.y, sunForward.z, 0f));
            }

            if (_skyInstance.HasProperty(MoonDirectionProperty))
            {
                Vector3 moonForward = MoonDirection;
                _skyInstance.SetVector(MoonDirectionProperty, new Vector4(moonForward.x, moonForward.y, moonForward.z, 0f));
            }

            // 月亮只在夜里可见，白天彻底淡出。
            if (_skyInstance.HasProperty(MoonVisibilityProperty))
            {
                _skyInstance.SetFloat(MoonVisibilityProperty, Mathf.Clamp01(night));
            }

            if (_skyInstance.HasProperty(SunColorProperty))
            {
                _skyInstance.SetColor(SunColorProperty, SunColor);
            }

            if (_skyInstance.HasProperty(DaySkyColorProperty))
            {
                _skyInstance.SetColor(DaySkyColorProperty, EvaluateColorKeys(_skyTintKeys, t, Color.white));
            }

            if (_skyInstance.HasProperty(NightSkyColorProperty))
            {
                _skyInstance.SetColor(NightSkyColorProperty, EvaluateColorKeys(_skyTintKeys, 0f, new Color(0.05f, 0.06f, 0.11f)));
            }

            if (_skyInstance.HasProperty(DayHorizonColorProperty))
            {
                _skyInstance.SetColor(DayHorizonColorProperty, EvaluateColorKeys(_equatorTintKeys, t, Color.white));
            }

            if (_skyInstance.HasProperty(NightHorizonColorProperty))
            {
                _skyInstance.SetColor(NightHorizonColorProperty, EvaluateColorKeys(_equatorTintKeys, 0f, new Color(0.04f, 0.05f, 0.09f)));
            }

            if (_skyInstance.HasProperty(SunsetHorizonColorProperty))
            {
                _skyInstance.SetColor(SunsetHorizonColorProperty, EvaluateColorKeys(_sunColorKeys, t, new Color(0.95f, 0.45f, 0.22f)));
            }

            if (_skyInstance.HasProperty(StarBrightnessProperty))
            {
                // 淡入只在 Shader 内乘一次，避免夜晚权重被平方。
                float starBrightness = _shaderStarBrightness;
                _skyInstance.SetFloat(StarBrightnessProperty, starBrightness);
            }
        }

        private void UpdateFog(float t)
        {
            if (!_controlFog) return;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = EvaluateColorKeys(_fogColorKeys, t, new Color(0.45f, 0.5f, 0.55f));
            RenderSettings.fogDensity = Mathf.Lerp(_dayFogDensity, _nightFogDensity, NightAmount);
        }

        private void UpdateAmbient(float t)
        {
            if (!_overrideAmbient) return;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            // ambientIntensity 是天空盒环境光的倍率；Gradient 模式直接缩放颜色。
            // 色调只部分跟随天空，避免晚霞染红所有材质，或午夜把环境光重复压暗。
            float strength = Mathf.Lerp(_dayAmbientIntensity, _minAmbientIntensity, NightAmount);
            Color sky = EvaluateColorKeys(_skyTintKeys, t, Color.gray);
            Color equator = EvaluateColorKeys(_equatorTintKeys, t, Color.gray);
            RenderSettings.ambientSkyColor = Color.Lerp(Color.white, sky, 0.45f) * strength;
            RenderSettings.ambientEquatorColor = Color.Lerp(Color.white, equator, 0.4f) * (strength * 0.65f);
            RenderSettings.ambientGroundColor = Color.Lerp(Color.white, _groundTint, 0.65f) * (strength * 0.35f);
        }

        private void UpdateSkyFollowers()
        {
            Transform follow = ResolveSkyFollowTarget();

            if (_starsTransform != null)
            {
                _starsTransform.position = follow != null ? follow.position : Vector3.zero;
                if (_stars != null)
                {
                    ParticleSystem.MainModule main = _stars.main;
                    main.startColor = new Color(_starColor.r, _starColor.g, _starColor.b, Mathf.Clamp01(NightAmount) * _starBrightness);
                }
            }

            if (_moonDisc != null)
            {
                Vector3 direction = MoonDirection;
                Vector3 anchor = follow != null ? follow.position : Vector3.zero;
                _moonDisc.position = anchor + direction * _moonDiscDistance;
                _moonDisc.rotation = Quaternion.LookRotation(direction);

                float scale = _moonDiscSize;
                _moonDisc.localScale = new Vector3(scale, scale, scale);

                if (_moonDiscRenderer != null)
                {
                    _propertyBlock ??= new MaterialPropertyBlock();
                    _moonDiscRenderer.GetPropertyBlock(_propertyBlock);
                    // 用自发光色控制亮度：夜间变亮，白天透明。
                    float brightness = Mathf.Clamp01(NightAmount) * 1.4f;
                    _propertyBlock.SetColor(BaseColorProperty, new Color(brightness, brightness, brightness * 1.05f, 1f));
                    _moonDiscRenderer.SetPropertyBlock(_propertyBlock);
                }
            }
        }

        // ------------------------------------------------------------------ 引用与构建

        /// <summary>
        /// 时间系统只认显式注入的引用（由「游戏 → 环境 → 给当前场景接入昼夜」完成接线）。
        /// 刻意不做运行时全场景查找：忘接线时应该报警告，而不是静默跟随某个碰巧找到的对象。
        /// </summary>
        private TimeOfDaySystem ResolveTimeSystem()
        {
            return _timeSystem;
        }

        private void ResolveReferences()
        {
            ResolveTimeSystem();

            if (_sunLight == null) _sunLight = FindLight("太阳");
            if (_moonLight == null) _moonLight = FindLight("月亮");

            if (_skyFollowTarget == null && Camera.main != null)
            {
                _skyFollowTarget = Camera.main.transform;
            }
        }

        private Light FindLight(string name)
        {
            Transform child = transform.Find(name);
            if (child != null)
            {
                Light light = child.GetComponent<Light>();
                if (light != null) return light;
            }

            return null;
        }

        private Transform ResolveSkyFollowTarget()
        {
            if (_skyFollowTarget != null) return _skyFollowTarget;

            Camera main = Camera.main;
            if (main != null)
            {
                _skyFollowTarget = main.transform;
                return _skyFollowTarget;
            }

            return null;
        }

        private bool UsesCelestialSky => _skyInstance != null && _skyInstance.HasProperty(NightAmountProperty);

        private void EnsureSkyMaterial()
        {
            if (_skyInstance != null && _skySource == _skyboxMaterial) return;
            ReleaseSkyMaterial();
            Shader shader = _skyboxMaterial != null ? _skyboxMaterial.shader : Shader.Find("Skybox/Procedural");
            if (shader == null)
            {
                if (!_warnedNoSkyShader) Debug.LogWarning("[昼夜光照] 缺少天空 Shader。", this);
                _warnedNoSkyShader = true;
                return;
            }
            _previousSkybox = RenderSettings.skybox;
            _skySource = _skyboxMaterial;
            _skyInstance = _skyboxMaterial != null ? new Material(_skyboxMaterial) : new Material(shader);
            _skyInstance.name = "Sky_Preview_Instance";
            _skyInstance.hideFlags = HideFlags.HideAndDontSave;
            RenderSettings.skybox = _skyInstance;
        }

        private void ReleaseSkyMaterial()
        {
            if (_skyInstance == null) return;
            if (RenderSettings.skybox == _skyInstance) RenderSettings.skybox = _previousSkybox;
            UnityEngine.Rendering.CoreUtils.Destroy(_skyInstance);
            _skyInstance = null;
            _skySource = null;
            _previousSkybox = null;
        }
        private void EnsureStars()
        {
            if (!_createStars || UsesCelestialSky)
            {
                HideLegacyObject("星空");
                return;
            }

            Transform existing = transform.Find("星空");
            if (existing != null)
            {
                _starsTransform = existing;
                _stars = existing.GetComponent<ParticleSystem>();
                return;
            }

            GameObject go = new GameObject("星空");
            go.transform.SetParent(transform, false);

            ParticleSystem system = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = system.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = 99999f;
            main.startSpeed = 0f;
            main.startSize = 1.6f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(1, _starCount);

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(_starCount, 1, 32000)) });

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = _starRadius;

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = -100;

            // 用 sharedMaterial 而不是 material：
            // material 会"实例化"材质，在编辑器里（AddComponent 触发的 Awake）容易出问题。
            // 材质为 null 时直接跳过，不要赋 null（赋值 null 会报空引用）。
            Material starMaterial = CreateStarMaterial();
            if (starMaterial != null)
            {
                renderer.sharedMaterial = starMaterial;
            }

            system.Simulate(0f, true, true);
            system.Play();

            _stars = system;
            _starsTransform = go.transform;
        }

        private Material CreateStarMaterial()
        {
            // Shader.Find 在编辑器里并不可靠：像 Sprites/Default 这种内置 shader，
            // 如果当前没有任何材质引用它，就可能返回 null（本工程就踩到了），
            // 而 new Material(null) 会得到一个无效材质，赋给 renderer.material 时报空引用。
            // 所以这里按顺序试几个候选，全都找不到就返回 null，由调用方跳过。
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            if (shader == null)
            {
                Debug.LogWarning(
                    $"[{nameof(DayNightLightingController)}] 找不到可用的粒子 shader，粒子星空将被跳过。" +
                    "（天空盒自带的程序化星星不受影响。）", this);
                return null;
            }

            Material material = new Material(shader) { name = "M_Stars_Runtime" };

            Texture2D glow = LoadBuiltinTexture("UI/Skins/Default/ps_glow");
            if (glow != null)
            {
                material.mainTexture = glow;
            }

            material.SetColor(ColorProperty, Color.white);
            return material;
        }

        private static Texture2D LoadBuiltinTexture(string resourceName)
        {
#if UNITY_EDITOR
            Texture2D texture = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Texture2D>(resourceName + ".png");
            if (texture != null) return texture;
#endif
            return Resources.GetBuiltinResource<Texture2D>("Default-Particle.psd");
        }

        private void EnsureMoonDisc()
        {
            if (!_createMoonDisc || UsesCelestialSky)
            {
                HideLegacyObject("月亮圆盘");
                return;
            }

            Transform existing = transform.Find("月亮圆盘");
            if (existing != null)
            {
                _moonDisc = existing;
                _moonDiscRenderer = existing.GetComponent<Renderer>();
                return;
            }

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "月亮圆盘";
            go.transform.SetParent(transform, false);

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) Destroy(collider);
                else DestroyImmediate(collider);
            }

            _moonDiscRenderer = go.GetComponent<Renderer>();
            if (_moonDiscRenderer != null)
            {
                _moonDiscRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _moonDiscRenderer.receiveShadows = false;
            }

            _moonDisc = go.transform;
        }

        /// <summary>
        /// 隐藏早期版本留下的对象（星空 / 月亮圆盘）。
        /// 用 SetActive(false) 而不是销毁：不破坏场景、不动用户的改动，
        /// 但能保证"默认关闭的重复效果"不会出现在画面里（也不会因坏材质刷报错）。
        /// </summary>
        private void HideLegacyObject(string childName)
        {
            Transform legacy = transform.Find(childName);
            if (legacy == null || !legacy.gameObject.activeSelf) return;

            legacy.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ 求值工具

        private static float EvaluateCurve(AnimationCurve curve, float time, float fallback)
        {
            if (curve == null || curve.length == 0) return fallback;
            return curve.Evaluate(Mathf.Clamp01(time));
        }

        /// <summary>
        /// 按时间取色。key 不要求排序、不要求覆盖 0 和 1，会在首尾自动延伸。
        /// </summary>
        public static Color EvaluateColorKeys(ColorKey[] keys, float time, Color fallback)
        {
            if (keys == null || keys.Length == 0) return fallback;
            if (keys.Length == 1) return keys[0].Color;

            time = Mathf.Repeat(time, 1f);

            int previous = -1;
            int next = -1;
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i].Time <= time) previous = i;
                if (keys[i].Time >= time && next < 0) next = i;
            }

            if (previous < 0) return keys[next].Color;
            if (next < 0) return keys[previous].Color;
            if (previous == next) return keys[previous].Color;

            float span = keys[next].Time - keys[previous].Time;
            float blend = span <= 0.0001f ? 0f : (time - keys[previous].Time) / span;
            return Color.Lerp(keys[previous].Color, keys[next].Color, blend);
        }

        // ------------------------------------------------------------------ 默认值

        private static ColorKey[] DefaultSunColors()
        {
            // 横轴 = 纯归一化时间：小时 / 24。0 = 00:00，0.25 = 06:00，0.5 = 12:00，0.75 = 18:00。
            // 与 SunriseHour(5:00=0.208) / SunsetHour(19:00=0.792) 对齐：
            // 太阳在地平线附近时偏橙红（穿过更厚的大气），正午偏白。
            return new[]
            {
                new ColorKey(0.000f, new Color(0.55f, 0.68f, 0.95f)),   // 00:00 夜（月光色）
                new ColorKey(0.190f, new Color(0.55f, 0.68f, 0.95f)),   // 04:34 日出前
                new ColorKey(0.235f, new Color(1.00f, 0.62f, 0.35f)),   // 05:38 朝霞
                new ColorKey(0.300f, new Color(1.00f, 0.92f, 0.78f)),   // 07:12 早晨
                new ColorKey(0.500f, new Color(1.00f, 0.98f, 0.94f)),   // 12:00 正午
                new ColorKey(0.700f, new Color(1.00f, 0.92f, 0.78f)),   // 16:48 午后
                new ColorKey(0.765f, new Color(1.00f, 0.55f, 0.28f)),   // 18:22 晚霞
                new ColorKey(0.810f, new Color(0.55f, 0.68f, 0.95f)),   // 19:26 日落后
                new ColorKey(1.000f, new Color(0.55f, 0.68f, 0.95f))    // 24:00 夜
            };
        }

        private static AnimationCurve DefaultSunIntensity()
        {
            // 横轴 = 归一化时间（0 = 00:00，0.5 = 12:00，1 = 24:00）。
            // 必须和 SunriseHour/SunsetHour（5 点 / 19 点）对齐，否则清晨会有一段"太阳升起但强度为 0"的黑窗：
            // 归一化时间 = 小时 / 24，所以 5 点 = 0.2083，19 点 = 0.7917。
            return new AnimationCurve(
                new Keyframe(0.0000f, 0f),      // 00:00 午夜
                new Keyframe(0.2083f, 0f),      // 05:00 日出（与高度角同步归零）
                new Keyframe(0.2500f, 0.45f),   // 06:00 天光初现
                new Keyframe(0.2917f, 1.20f),   // 07:00 早晨
                new Keyframe(0.3750f, 2.00f),   // 09:00 上午
                new Keyframe(0.5000f, 2.20f),   // 12:00 正午最高
                new Keyframe(0.6250f, 2.00f),   // 15:00 下午
                new Keyframe(0.7083f, 1.20f),   // 17:00 傍晚
                new Keyframe(0.7500f, 0.45f),   // 18:00 日落前
                new Keyframe(0.7917f, 0f),      // 19:00 日落
                new Keyframe(1.0000f, 0f));     // 24:00
        }

        private static ColorKey[] DefaultSkyTints()
        {
            // 横轴 = 纯归一化时间（小时 / 24）。
            // 白天这几格刻意调亮：SkyTint 是【乘算】到大气散射上的，
            // 值偏低会让整个天空连带环境光一起发暗（"白天像傍晚"多半是这个原因）。
            return new[]
            {
                new ColorKey(0.000f, new Color(0.070f, 0.085f, 0.140f)),  // 00:00 夜
                new ColorKey(0.190f, new Color(0.100f, 0.110f, 0.160f)),  // 04:34 拂晓前
                new ColorKey(0.235f, new Color(0.520f, 0.300f, 0.240f)),  // 05:38 朝霞
                new ColorKey(0.330f, new Color(0.560f, 0.700f, 0.930f)),  // 07:55 晨间蓝天
                new ColorKey(0.500f, new Color(0.620f, 0.760f, 1.000f)),  // 12:00 正午
                new ColorKey(0.680f, new Color(0.560f, 0.680f, 0.920f)),  // 16:19 午后
                new ColorKey(0.765f, new Color(0.640f, 0.320f, 0.220f)),  // 18:22 晚霞
                new ColorKey(0.810f, new Color(0.120f, 0.130f, 0.190f)),  // 19:26 暮色
                new ColorKey(1.000f, new Color(0.070f, 0.085f, 0.140f))   // 24:00 夜
            };
        }

        private static ColorKey[] DefaultEquatorTints()
        {
            // 水平线附近的颜色（地平线雾带的色温）。横轴 = 纯归一化时间（小时 / 24）。
            return new[]
            {
                new ColorKey(0.000f, new Color(0.050f, 0.055f, 0.085f)),  // 00:00 夜
                new ColorKey(0.225f, new Color(0.180f, 0.130f, 0.120f)),  // 05:24 破晓
                new ColorKey(0.340f, new Color(0.420f, 0.440f, 0.480f)),  // 08:10 晨间
                new ColorKey(0.500f, new Color(0.500f, 0.540f, 0.580f)),  // 12:00 正午
                new ColorKey(0.770f, new Color(0.300f, 0.200f, 0.170f)),  // 18:29 暮色
                new ColorKey(1.000f, new Color(0.050f, 0.055f, 0.085f))   // 24:00 夜
            };
        }

        private static ColorKey[] DefaultFogColors()
        {
            // 横轴 = 纯归一化时间（小时 / 24）。白天偏灰蓝（灰霾），日出日落偏暖。
            return new[]
            {
                new ColorKey(0.000f, new Color(0.040f, 0.050f, 0.080f)),  // 00:00 夜
                new ColorKey(0.225f, new Color(0.320f, 0.260f, 0.250f)),  // 05:24 破晓
                new ColorKey(0.400f, new Color(0.610f, 0.640f, 0.680f)),  // 09:36 白天灰霾
                new ColorKey(0.600f, new Color(0.610f, 0.640f, 0.680f)),  // 14:24 白天灰霾
                new ColorKey(0.780f, new Color(0.360f, 0.250f, 0.230f)),  // 18:43 暮色
                new ColorKey(1.000f, new Color(0.040f, 0.050f, 0.080f))   // 24:00 夜
            };
        }
    }
}
