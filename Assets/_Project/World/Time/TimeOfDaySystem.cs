using System;
using UnityEngine;

namespace Game.GameTime
{
    /// <summary>
    /// 游戏时间系统（昼夜循环的核心）。
    ///
    /// 命名空间叫 Game.GameTime 而不是 Game.Time：
    ///   叫 Game.Time 会在本命名空间及其子命名空间里**遮蔽 UnityEngine.Time**，
    ///   于是任何一行 Time.deltaTime 都会被解析成 Game.Time.deltaTime 而编译失败。
    ///   这种坑一旦留在工程里，以后每加一个文件都可能再踩一次，所以从根上避开。
    ///
    /// 职责边界：**只管时间，不管画面**。
    /// 光照、雾、天空由 DayNightLightingController 读它；玩法（夜晚刷怪、白天采集）读它的事件和 IsNight。
    /// 以后要存档，只需要存 DayNumber + 当天的 NormalizedTime 两个数。
    ///
    /// 时间约定：
    ///   0 点 = 午夜，12 点 = 正午；光照轨道目前设为 5 点日出、19 点日落。
    ///   NormalizedTime 0~1 映射到 0~24 点。
    /// </summary>
    [DisallowMultipleComponent]
    public class TimeOfDaySystem : MonoBehaviour
    {
        [Header("时间模式")]
        [Tooltip("游戏时间：时间会自己流逝。固定时间：时间钉在下面的小时上不动，用来反复看某个时刻的光照。")]
        [SerializeField] private TimeControlMode _mode = TimeControlMode.GameTime;

        [Tooltip("暂停时间流逝（不影响手动跳时间）。开背包/对话/暂停菜单时可以置 true。")]
        [SerializeField] private bool _paused = false;

        [Header("时间流速")]
        [Tooltip("一整天（24 游戏小时）对应多少现实秒。480 秒 = 8 分钟一天。")]
        [SerializeField, Min(10f)] private float _dayLengthSeconds = 480f;

        [Tooltip("夜间时间流速倍率。1 = 和白天一样快；2 = 夜晚过得快一倍（想让玩家少熬夜晚就调大）。只影响流速，不影响画面。")]
        [SerializeField, Min(1f)] private float _nightTimeScale = 1f;

        [Header("起始时间")]
        [Tooltip("每次开始游戏时的起始小时。默认 9 点：太阳约 45° 高，明暗对比正常，适合开局。")]
        [SerializeField, Range(0f, 24f)] private float _startHour = 9f;
        [SerializeField, Min(1)] private int _startDay = 1;

        [Tooltip("勾上（推荐）：每次 Play 都从上面的「起始时间」开始。\n" +
                 "为什么需要它：_hour 是序列化字段，运行时的时刻会被【存进场景】。\n" +
                 "如果你某次在凌晨 5 点保存过场景，之后每次打开都会从 5 点开始 ——\n" +
                 "那时太阳还没升起来，画面就是黑的（阳光强度为 0）。\n" +
                 "关掉它才会保留场景里存的时刻（做存档/调试时才需要）。")]
        [SerializeField] private bool _useStartHourOnPlay = true;

        [Header("时间控制（0~24 小时，可直接调）")]
        [Tooltip("当前小时。拖动它会立刻改时间；游戏模式下它每帧被时间流逝覆盖，固定模式下就是它在说了算。")]
        [SerializeField, Range(0f, 24f)] private float _hourControl = 9f;

        [Header("运行时只读")]
        [SerializeField] private float _hour;
        [SerializeField] private int _displayDayNumber = 1;
        [SerializeField] private DayPhase _phase = DayPhase.Day;
        [SerializeField] private bool _isNight;

        private float _normalizedTime;
        private int _dayNumber;
        private int _lastHour = -1;
        private DayPhase _lastPhase = (DayPhase)(-1);

        /// <summary>当前小时（0~24 的小数，例如 13.5 = 下午 1 点半）。</summary>
        public float Hour => Application.isPlaying ? _hour : Mathf.Repeat(_hourControl, 24f);

        /// <summary>当前小时小数部分（0~1），等价 Hour 去掉整数。</summary>
        public float HourFraction => _hour - Mathf.Floor(_hour);

        /// <summary>当天进度 0~1（0 = 午夜 0 点，0.5 = 正午 12 点）。</summary>
        public float NormalizedTime => Hour / 24f;

        /// <summary>第几天，从 1 开始。存档时存这个数。</summary>
        public int DayNumber => _dayNumber;

        /// <summary>当前是否夜晚（22:00 ~ 05:00）。玩法判断用这个，不要自己算小时。</summary>
        public bool IsNight => _phase == DayPhase.Night;

        /// <summary>当前阶段。</summary>
        public DayPhase Phase => _phase;

        /// <summary>一整天多少现实秒。</summary>
        public float DayLengthSeconds => _dayLengthSeconds;

        /// <summary>当前时间模式。</summary>
        public TimeControlMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        /// <summary>时间是否暂停流逝。</summary>
        public bool IsPaused
        {
            get => _paused;
            set => _paused = value;
        }

        /// <summary>时间是否在自动流逝（游戏模式且未暂停）。</summary>
        public bool IsAdvancing => _mode == TimeControlMode.GameTime && !_paused;

        /// <summary>每次时间被改变/推进后触发（参数：当前小时）。光照模块用它立即刷新，不用等下一帧。</summary>
        public event Action<float> OnTimeChanged;

        /// <summary>整点变化时触发（参数：新的天数、新的小时）。适合"每天结算""每小时扣饥饿"。</summary>
        public event Action<int, int> OnHourChanged;

        /// <summary>阶段变化时触发（黎明/白天/黄昏/夜晚）。适合"入夜刷僵尸""天亮停止刷怪"。</summary>
        public event Action<DayPhase> OnPhaseChanged;

        /// <summary>新的一天开始时触发。</summary>
        public event Action<int> OnNewDay;

        private void Awake()
        {
            _dayNumber = Mathf.Max(1, _useStartHourOnPlay ? _startDay : _displayDayNumber);

            float beginHour;
            if (_mode == TimeControlMode.FixedTime)
            {
                // 固定模式：用玩家调好的那一格。
                beginHour = _hourControl;
            }
            else if (_useStartHourOnPlay)
            {
                // 关键：忽略场景里存下来的 _hour，强制从"起始时间"开始。
                // 否则某次在凌晨保存过场景，之后每次开局都会是黑天（太阳没升起来，阳光强度为 0）。
                beginHour = _startHour;
            }
            else
            {
                // 保留场景里存的时刻（存档/调试用）。
                beginHour = _hour;
            }

            SetNormalizedTime(Mathf.Repeat(beginHour / 24f, 1f));
            _hourControl = _hour;
            _lastHour = -1;
            _lastPhase = (DayPhase)(-1);
        }

        private void Update()
        {
            // 固定时间模式：时间钉在 _hourControl 上，只字不动。
            // 这让"想看黄昏"变成拖一下滑块，而不是等 8 分钟。
            if (_mode == TimeControlMode.FixedTime)
            {
                if (!Mathf.Approximately(_hour, _hourControl))
                {
                    SetNormalizedTime(Mathf.Repeat(_hourControl / 24f, 1f));
                    FireHourEventIfChanged();
                }

                return;
            }

            if (_paused) return;

            float speed = 1f / Mathf.Max(1f, _dayLengthSeconds);
            if (_phase == DayPhase.Night)
            {
                speed *= _nightTimeScale;
            }

            float next = _normalizedTime + Time.deltaTime * speed;

            bool wrapped = next >= 1f;
            if (wrapped)
            {
                next -= 1f;
                _dayNumber++;
                OnNewDay?.Invoke(_dayNumber);
            }

            SetNormalizedTime(next);
            FireHourEventIfChanged();
        }

        /// <summary>直接跳到某个时间（调试/读档用）。normalizedTime 会自动取小数部分。</summary>
        public void SetNormalizedTime(float normalizedTime)
        {
            _normalizedTime = Mathf.Repeat(normalizedTime, 1f);
            _hour = _normalizedTime * 24f;
            _hourControl = _hour;
            _displayDayNumber = _dayNumber;
            _phase = EvaluatePhase(_hour);
            _isNight = _phase == DayPhase.Night;
            RefreshPhaseEvent();
            OnTimeChanged?.Invoke(_hour);
        }

        /// <summary>跳到指定小时（调试/剧情/睡觉用）。跨过 24 点会自动进到第二天。</summary>
        public void SetHour(float hour)
        {
            if (hour >= 24f) _dayNumber++;
            else if (hour < 0f) _dayNumber = Mathf.Max(1, _dayNumber - 1);

            SetNormalizedTime(Mathf.Repeat(hour / 24f, 1f));
            FireHourEventIfChanged();
        }

        /// <summary>把时间往前推进若干小时（睡觉/传送用）。</summary>
        public void AdvanceHours(float hours)
        {
            SetHour(_hour + hours);
        }

        /// <summary>暂停/恢复时间流逝。</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
        }

        /// <summary>切换"游戏时间 / 固定时间"。切到固定时间会钉在当前这一格。</summary>
        public void SetMode(TimeControlMode mode)
        {
            _mode = mode;
            if (mode == TimeControlMode.FixedTime)
            {
                _hourControl = _hour;
            }
        }

        /// <summary>读档用：直接设置到第几天几点。</summary>
        public void Restore(int dayNumber, float normalizedTime)
        {
            _dayNumber = Mathf.Max(1, dayNumber);
            SetNormalizedTime(normalizedTime);
            _lastHour = Mathf.FloorToInt(_hour);
        }

        private void FireHourEventIfChanged()
        {
            int hour = Mathf.FloorToInt(_hour);
            if (hour == _lastHour) return;

            _lastHour = hour;
            OnHourChanged?.Invoke(_dayNumber, hour);
        }

        // ------------------------------------------------------------------ 编辑器快捷操作
        // 这些按钮让你不用写代码就能跳到关键时间点看效果（右键组件标题即可看到）。

        [ContextMenu("跳到 00:00 午夜")]
        private void JumpMidnight() => JumpTo(0f);

        [ContextMenu("跳到 05:30 拂晓")]
        private void JumpDawn() => JumpTo(5.5f);

        [ContextMenu("跳到 09:00 上午")]
        private void JumpMorning() => JumpTo(9f);

        [ContextMenu("跳到 12:00 正午")]
        private void JumpNoon() => JumpTo(12f);

        [ContextMenu("跳到 18:30 黄昏")]
        private void JumpDusk() => JumpTo(18.5f);

        [ContextMenu("跳到 22:00 入夜")]
        private void JumpNight() => JumpTo(22f);

        /// <summary>跳时间并顺便切到固定模式（这样跳完不会马上被时间流逝冲走）。</summary>
        private void JumpTo(float hour)
        {
            _mode = TimeControlMode.FixedTime;
            SetHour(hour);
        }

        private void RefreshPhaseEvent()
        {
            if (_phase == _lastPhase) return;

            _lastPhase = _phase;
            OnPhaseChanged?.Invoke(_phase);
        }

        /// <summary>
        /// 阶段划分（可按需改，改完全局判断都跟着变）：
        ///   05:00~07:00 黎明 / 07:00~17:00 白天 / 17:00~22:00 黄昏 / 22:00~05:00 夜晚
        /// </summary>
        public static DayPhase EvaluatePhase(float hour)
        {
            if (hour >= 5f && hour < 7f) return DayPhase.Dawn;
            if (hour >= 7f && hour < 17f) return DayPhase.Day;
            if (hour >= 17f && hour < 22f) return DayPhase.Dusk;
            return DayPhase.Night;
        }

        private void OnValidate()
        {
            _dayLengthSeconds = Mathf.Max(10f, _dayLengthSeconds);
            _nightTimeScale = Mathf.Max(1f, _nightTimeScale);

            if (Application.isPlaying)
            {
                _startDay = Mathf.Max(1, _startDay);

                // Play 状态下拖 0~24 滑块 = 立刻改时间。
                if (!Mathf.Approximately(_hourControl, _hour))
                {
                    SetHour(_hourControl);
                }

                return;
            }

            // 预览小时与开局小时分开；重载/验证不应把用户选的午夜又改回 9 点。
            SetNormalizedTime(Mathf.Repeat(_hourControl / 24f, 1f));
        }
    }
}
