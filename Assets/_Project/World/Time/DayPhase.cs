namespace Game.GameTime
{
    /// <summary>
    /// 一天中的阶段。玩法用它做判断（例如夜晚刷怪、白天才能采集）。
    /// </summary>
    public enum DayPhase
    {
        Night = 0,
        Dawn = 1,
        Day = 2,
        Dusk = 3
    }
}
