const fs = require('fs');
function replace(s,a,b){if(!s.includes(a))throw Error('Missing '+a.slice(0,80));return s.replace(a,b);}
function section(s,a,b,value){const i=s.indexOf(a),j=s.indexOf(b,i);if(i<0||j<0)throw Error(a);return s.slice(0,i)+value+s.slice(j);}
const file='Assets/_Project/World/Environment/DayNightLightingController.cs';
let s=fs.readFileSync(file,'utf8').replace(/\r\n/g,'\n');
s=section(s,'        /// <summary>太阳高度的形状','        [Header("月亮")]',
 '        [Tooltip("正午太阳最高角度。轨道以 05:00 日出、12:00 正午、19:00 日落为基准。")]\n        [SerializeField, Range(5f, 85f)] private float _maximumSunElevation = 65f;\n\n');
s=replace(s,'        private bool _loggedState;',[
 '        private bool _loggedState;',
 '        private Material _skyInstance;',
 '        private Material _skySource;',
 '        private Material _previousSkybox;',
 '        private static readonly int NightAmountProperty = Shader.PropertyToID("_NightAmount");',
 '        public float TwilightAmount { get; private set; }',
 '        public Vector3 SunDirection { get; private set; }',
 '        public Vector3 MoonDirection => -SunDirection;'
].join('\n'));
s=section(s,'        private void OnEnable()','        private void Update()',[
 '        private void OnEnable()',
 '        {',
 '            ResolveReferences();',
 '            EnsureSkyMaterial();',
 '            EnsureStars();',
 '            EnsureMoonDisc();',
 '#if UNITY_EDITOR',
 '            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= BeforeSceneSave;',
 '            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= AfterSceneSave;',
 '            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += BeforeSceneSave;',
 '            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += AfterSceneSave;',
 '#endif',
 '        }',
 '',
 '        private void OnDisable()',
 '        {',
 '#if UNITY_EDITOR',
 '            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= BeforeSceneSave;',
 '            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= AfterSceneSave;',
 '#endif',
 '            ReleaseSkyMaterial();',
 '        }',
 '',
 '        private void OnDestroy() => ReleaseSkyMaterial();',
 '',
 '#if UNITY_EDITOR',
 '        private void BeforeSceneSave(UnityEngine.SceneManagement.Scene scene, string path)',
 '        {',
 '            if (scene == gameObject.scene && RenderSettings.skybox == _skyInstance)',
 '                RenderSettings.skybox = _previousSkybox;',
 '        }',
 '',
 '        private void AfterSceneSave(UnityEngine.SceneManagement.Scene scene)',
 '        {',
 '            if (scene == gameObject.scene && isActiveAndEnabled && _skyInstance != null)',
 '                RenderSettings.skybox = _skyInstance;',
 '        }',
 '#endif',
 '',
].join('\n'));
s=replace(s,'            TimeOfDaySystem time = ResolveTimeSystem();','            EnsureSkyMaterial();\n            TimeOfDaySystem time = ResolveTimeSystem();');
s=replace(s,'            if (time == null)\n            {','            if (time == null)\n            {\n                time = GetComponent<TimeOfDaySystem>();\n                if (time != null) _timeSystem = time;\n            }\n            if (time == null)\n            {\n                if (!Application.isPlaying) return;');
s=replace(s,'            float sunElevation = EvaluateSunElevation(time.Hour);\n            SunElevation = sunElevation;\n            NightAmount = EvaluateNightAmount(sunElevation);',[
 '            DayNightCycle.State cycle = DayNightCycle.Evaluate(time.Hour, _maximumSunElevation);',
 '            float sunElevation = cycle.Elevation;',
 '            SunElevation = sunElevation;',
 '            NightAmount = cycle.NightAmount;',
 '            TwilightAmount = cycle.TwilightAmount;',
 '            SunDirection = Quaternion.Euler(0f, _sunYaw, 0f) * new Vector3(cycle.X, cycle.Y, cycle.Z);'
].join('\n'));
s=section(s,'        /// <summary>日出 / 日落时间。','        private void UpdateSun(', '');
s=replace(s,'            if (_sunLight == null) return;\n\n            _sunLight.transform.rotation = Quaternion.Euler(sunElevation, _sunYaw, 0f);',
 '            SunColor = EvaluateColorKeys(_sunColorKeys, t, Color.white);\n            if (_sunLight == null) return;\n\n            _sunLight.transform.rotation = Quaternion.LookRotation(-SunDirection, Vector3.up);');
s=replace(s,'            _moonLight.transform.rotation = EvaluateMoonRotation(t);','            _moonLight.transform.rotation = Quaternion.LookRotation(-MoonDirection, Vector3.up);');
s=replace(s,'            _moonLight.intensity = _moonMaxIntensity * NightAmount;',
 '            _moonLight.intensity = _moonMaxIntensity * NightAmount * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f, 0.1f, MoonDirection.y));');
s=section(s,'        /// <summary>\n        /// 月亮朝向：','        private void UpdateSky(', '');
let begin=s.indexOf('        private void UpdateSky('),end=s.indexOf('        private void UpdateFog(',begin);
let sky=s.slice(begin,end).replaceAll('_skyboxMaterial','_skyInstance');
sky=replace(sky,'float night = Mathf.Clamp01(Mathf.InverseLerp(8f, -4f, sunElevation));','float night = NightAmount;');
sky=section(sky,'        /// <summary>\n        /// 驱动自定义天空盒','        private void UpdateCelestialBodies(',
 '        /// <summary>预览和运行共用临时材质，天体方向来自同一轨道。</summary>\n');
sky=replace(sky,'            if (!Application.isPlaying) return;\n','            if (_skyInstance.HasProperty(NightAmountProperty)) _skyInstance.SetFloat(NightAmountProperty, night);\n');
sky=sky.replace('if (_sunLight != null && _skyInstance.HasProperty(SunDirectionProperty))','if (_skyInstance.HasProperty(SunDirectionProperty))')
 .replace('Vector3 sunForward = _sunLight.transform.forward;','Vector3 sunForward = SunDirection;')
 .replace('if (_moonLight != null && _skyInstance.HasProperty(MoonDirectionProperty))','if (_skyInstance.HasProperty(MoonDirectionProperty))')
 .replace('Vector3 moonForward = _moonLight.transform.forward;','Vector3 moonForward = MoonDirection;')
 .replace('EvaluateColorKeys(_skyTintKeys, t, new Color(0.05f, 0.06f, 0.11f)) * 0.35f','EvaluateColorKeys(_skyTintKeys, 0f, new Color(0.05f, 0.06f, 0.11f))')
 .replace('EvaluateColorKeys(_equatorTintKeys, t, new Color(0.04f, 0.05f, 0.09f)) * 0.3f','EvaluateColorKeys(_equatorTintKeys, 0f, new Color(0.04f, 0.05f, 0.09f))')
 .replace('// 星星只在夜里亮；白天给 0，省掉那 27 次哈希采样。\n                float starBrightness = night > 0.02f ? _shaderStarBrightness * night : 0f;','// 淡入只在 Shader 内乘一次，避免夜晚权重被平方。\n                float starBrightness = _shaderStarBrightness;');
s=s.slice(0,begin)+sky+s.slice(end);
s=s.replace('Vector3 direction = EvaluateMoonRotation(normalizedTime) * Vector3.forward;','Vector3 direction = MoonDirection;');
s=section(s,'        /// <summary>没有天空盒材质时','        private void EnsureStars()',[
 '        private bool UsesCelestialSky => _skyInstance != null && _skyInstance.HasProperty(NightAmountProperty);',
 '',
 '        private void EnsureSkyMaterial()',
 '        {',
 '            if (_skyInstance != null && _skySource == _skyboxMaterial) return;',
 '            ReleaseSkyMaterial();',
 '            Shader shader = _skyboxMaterial != null ? _skyboxMaterial.shader : Shader.Find("Skybox/Procedural");',
 '            if (shader == null)',
 '            {',
 '                if (!_warnedNoSkyShader) Debug.LogWarning("[昼夜光照] 缺少天空 Shader。", this);',
 '                _warnedNoSkyShader = true;',
 '                return;',
 '            }',
 '            _previousSkybox = RenderSettings.skybox;',
 '            _skySource = _skyboxMaterial;',
 '            _skyInstance = _skyboxMaterial != null ? new Material(_skyboxMaterial) : new Material(shader);',
 '            _skyInstance.name = "Sky_Preview_Instance";',
 '            _skyInstance.hideFlags = HideFlags.HideAndDontSave;',
 '            RenderSettings.skybox = _skyInstance;',
 '        }',
 '',
 '        private void ReleaseSkyMaterial()',
 '        {',
 '            if (_skyInstance == null) return;',
 '            if (RenderSettings.skybox == _skyInstance) RenderSettings.skybox = _previousSkybox;',
 '            UnityEngine.Rendering.CoreUtils.Destroy(_skyInstance);',
 '            _skyInstance = null;',
 '            _skySource = null;',
 '            _previousSkybox = null;',
 '        }',
 '',
].join('\n'));
s=s.replace('if (!_createStars)','if (!_createStars || UsesCelestialSky)').replace('if (!_createMoonDisc)','if (!_createMoonDisc || UsesCelestialSky)');
s=section(s,'        private static AnimationCurve DefaultSunElevation()','        private static ColorKey[] DefaultSkyTints()', '');
s=s.replace('不写自定义天空盒 shader，用 Unity 内置的 Skybox/Procedural。','优先使用 Game/ProceduralSkyCelestial，未配置时回退到 Skybox/Procedural。')
 .replace('[Header("天空（内置 Procedural 天空盒）")]','[Header("天空材质（仅作模板，预览和运行使用副本）")]');
fs.writeFileSync(file,s.replace(/\n/g,'\r\n'));
const scene='Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity';
let yaml=fs.readFileSync(scene,'utf8').replace(/\r\n/g,'\n');
yaml=yaml.replace(/  _sunElevationCurve:\n[\s\S]*?(?=  _moonColor:)/,'  _maximumSunElevation: 65\n')
 .replace('  _createStars: 1','  _createStars: 0').replace('  _createMoonDisc: 1','  _createMoonDisc: 0');
fs.writeFileSync(scene,yaml.replace(/\n/g,'\r\n'));
console.log('Sky controller and scene updated.');
