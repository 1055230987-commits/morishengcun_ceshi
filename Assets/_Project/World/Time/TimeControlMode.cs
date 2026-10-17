namespace Game.GameTime
{
    /// <summary>
    /// 时间模式。
    ///
    /// GameTime（游戏时间）：时间会自己流逝，玩家正常游玩时用这个。
    /// FixedTime（固定时间）：时间被钉在你指定的那一格，用来反复看某个时刻的光照效果。
    ///   调效果时切到这个模式，拖 0~24 的滑块，日出/正午/黄昏/夜晚随便对比，不用等 8 分钟。
    /// </summary>
    public enum TimeControlMode
    {
        GameTime = 0,
        FixedTime = 1
    }
}
