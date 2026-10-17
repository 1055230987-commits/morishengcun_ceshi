namespace Game.Player
{
    /// <summary>
    /// 移动意图来源。
    /// 本地键盘、手柄、移动端虚拟摇杆、AI 行为树、网络回放……都实现这个接口即可，
    /// PlayerController 只认这个接口，不认具体设备。
    /// </summary>
    public interface ICharacterInputSource
    {
        /// <summary>本帧的移动意图。</summary>
        MovementIntent CurrentIntent { get; }

        /// <summary>输入是否被允许（打开 UI / 过场 / 死亡 / 存档加载时置 false 即可冻结角色）。</summary>
        bool InputEnabled { get; set; }
    }
}
