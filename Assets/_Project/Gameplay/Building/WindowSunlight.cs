using Game.Environment;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 窗户内侧的"假阳光"：一盏只在白天开、朝室内打的光。
    ///
    /// 【为什么需要它】玻璃本身发光只能让窗户"看起来亮"，但阳光透过窗户
    /// 在室内地面上留下的那道光斑，才是"这是窗户"的关键视觉线索。
    ///
    /// 【为什么叫"假"】真实的光透射（caustics）需要烘焙 GI 或光追，URP 实时光下做不到。
    /// 所以这里用一盏受限的光去模拟光斑 —— 这是游戏里普遍做法。
    ///
    /// 【跟随太阳方向】朝向会随太阳方位偏转（复用昼夜控制器算好的太阳方向），
    /// 所以早晨和下午的光斑方向不同，观感更像真的。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public class WindowSunlight : MonoBehaviour
    {
        [Header("来源")]
        [Tooltip("昼夜光照控制器。留空则自动在场景里找（用来拿太阳方向与昼夜程度）。")]
        [SerializeField] private DayNightLightingController _lighting;

        [Header("强度")]
        [Tooltip("白天最大强度。")]
        [SerializeField, Min(0f)] private float _maxIntensity = 2.2f;

        [Tooltip("低于这个环境亮度就熄灭（夜里没有阳光透进来）。")]
        [SerializeField, Range(0f, 0.5f)] private float _minDaylight = 0.08f;

        [Tooltip("高于这个环境亮度达到最大强度。")]
        [SerializeField, Range(0.05f, 1f)] private float _fullDaylight = 0.5f;

        [Header("跟随太阳")]
        [Tooltip("让光斑方向随太阳方位偏转（早晨与下午方向不同，更自然）。")]
        [SerializeField] private bool _followSunAzimuth = true;

        [Tooltip("完全跟随太阳时为 1；保持固定朝向时为 0。中间值做混合，避免光斑扫得太夸张。")]
        [SerializeField, Range(0f, 1f)] private float _sunFollowWeight = 0.45f;

        [Tooltip("朝室内的基准方向（世界坐标）。生成器会按窗户所在墙面设置。")]
        [SerializeField] private Vector3 _inwardDirection = Vector3.forward;

        private Light _light;
        private float _baseRange;

        private void Awake()
        {
            _light = GetComponent<Light>();
            _baseRange = _light.range;

            // 窗户光只负责"光斑"，不参与阴影贴图 —— 阴影贴图有分辨率上限，
            // 每扇窗都投影会很快把预算吃光，而光斑本身不需要阴影就够看。
            _light.shadows = LightShadows.None;
            _light.type = LightType.Spot;
        }

        private void Update()
        {
            if (_light == null) return;

            if (_lighting == null)
            {
                _lighting = FindFirstObjectByType<DayNightLightingController>();
                if (_lighting == null) return;
            }

            float daylight = 1f - Mathf.Clamp01(_lighting.NightAmount);
            float span = Mathf.Max(0.01f, _fullDaylight - _minDaylight);
            float t = Mathf.Clamp01((daylight - _minDaylight) / span);

            _light.intensity = _maxIntensity * t;
            _light.enabled = t > 0.01f;

            if (_followSunAzimuth && _sunFollowWeight > 0.001f)
            {
                ApplySunFollow();
            }
        }

        /// <summary>
        /// 把"朝室内"的方向和"太阳方向"混合，得到最终照射方向。
        ///
        /// 取太阳方向的水平分量（忽略高度），这样光斑只会左右偏，
        /// 不会因为太阳高度变化而让光斑一下子消失。
        /// </summary>
        private void ApplySunFollow()
        {
            Vector3 sun = _lighting.SunDirection;
            sun.y = 0f;

            if (sun.sqrMagnitude < 0.0001f) return;
            sun.Normalize();

            // 太阳方向是"从观察者指向太阳"，光要往里照，所以取反。
            Vector3 mixed = Vector3.Lerp(_inwardDirection.normalized, -sun, _sunFollowWeight);

            if (mixed.sqrMagnitude < 0.0001f) return;

            transform.rotation = Quaternion.LookRotation(mixed.normalized, Vector3.up);
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureSunlight(DayNightLightingController lighting, Vector3 inwardDirection,
            float maxIntensity, float range, float spotAngle)
        {
            _lighting = lighting;
            _inwardDirection = inwardDirection;
            _maxIntensity = maxIntensity;

            _light = GetComponent<Light>();
            _light.type = LightType.Spot;
            _light.range = range;
            _light.spotAngle = spotAngle;
            _light.shadows = LightShadows.None;
            _baseRange = range;
        }
#endif
    }
}
