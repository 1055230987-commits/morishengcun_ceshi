using Game.Environment;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 窗户玻璃的"透光"控制：把昼夜亮度写进全局 shader 属性，
    /// 场景里【所有】窗户一起变化。
    ///
    /// 【为什么用全局属性而不是逐材质改】
    ///  1. 一盏窗一个材质实例 → 内存和 draw call 都变差；
    ///  2. 逐扇窗 SetColor 要遍历，而全局属性是 O(1)；
    ///  3. 新增窗户时自动生效，不用注册。
    ///
    /// 【为什么需要它】WindowGlass.shader 里 _WindowGlowAmount 刻意【没有】写进
    /// Properties 块 —— 因为 `Shader.SetGlobalFloat` 只对没暴露在属性块里的属性生效。
    /// 一旦写进属性块，材质上的值会覆盖全局值，昼夜控制就失效了。
    ///
    /// 【挂在哪】场景里放一个即可（和昼夜光照控制器放一起最自然）。
    /// 多加几个也不会出错 —— 它们写的是同一个值。
    /// </summary>
    [DisallowMultipleComponent]
    public class WindowGlassDriver : MonoBehaviour
    {
        /// <summary>shader 里的全局属性名。改名要同步改 WindowGlass.shader。</summary>
        public static readonly int WindowGlowId = Shader.PropertyToID("_WindowGlowAmount");

        [Header("来源")]
        [Tooltip("昼夜光照控制器。留空则自动在场景里找。")]
        [SerializeField] private DayNightLightingController _lighting;

        [Header("透光曲线")]
        [Tooltip("环境亮度低于这个值时窗户完全不发光（夜里窗户不该自己亮）。")]
        [SerializeField, Range(0f, 0.5f)] private float _minDaylight = 0.06f;

        [Tooltip("环境亮度高于这个值时窗户达到最亮。\n" +
                 "两者之间平滑过渡，避免日出瞬间窗户「啪」地亮起来。")]
        [SerializeField, Range(0.05f, 1f)] private float _fullDaylight = 0.45f;

        [Tooltip("在编辑器里也实时更新（方便不用 Play 就调曲线）。")]
        [SerializeField] private bool _updateInEditMode = true;

        private float _lastApplied = -1f;

        private void Awake() => ResolveLighting();

        private void Update()
        {
            if (!Application.isPlaying && !_updateInEditMode) return;
            Apply();
        }

        private void ResolveLighting()
        {
            if (_lighting == null) _lighting = FindFirstObjectByType<DayNightLightingController>();
        }

        /// <summary>把当前昼夜状态写进全局属性。</summary>
        private void Apply()
        {
            if (_lighting == null)
            {
                ResolveLighting();
                if (_lighting == null)
                {
                    // 没有昼夜系统时按"白天"处理，至少窗户不会是黑的。
                    SetGlow(1f);
                    return;
                }
            }

            SetGlow(DaylightAmount);
        }

        private void SetGlow(float amount)
        {
            // 只在变化超过阈值时写入 —— Shader.SetGlobalFloat 每帧调用虽然不贵，
            // 但没必要在时间暂停时反复写同一个值。
            if (Mathf.Abs(amount - _lastApplied) < 0.002f) return;

            _lastApplied = amount;
            Shader.SetGlobalFloat(WindowGlowId, amount);
        }

        /// <summary>当前"环境有多亮"，0 = 全黑（夜里），1 = 全亮（正午）。</summary>
        public float DaylightAmount
        {
            get
            {
                if (_lighting == null) return 1f;

                // NightAmount：1 = 深夜、0 = 白天。取反得到"白天程度"。
                float daylight = 1f - Mathf.Clamp01(_lighting.NightAmount);

                // 在 [_minDaylight, _fullDaylight] 之间平滑映射到 0~1。
                float span = Mathf.Max(0.01f, _fullDaylight - _minDaylight);
                return Mathf.Clamp01((daylight - _minDaylight) / span);
            }
        }
    }
}
