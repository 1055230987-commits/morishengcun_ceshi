namespace Game.Player
{
    /// <summary>
    /// 当前输入该由谁处理。
    ///
    /// 【为什么需要它 —— 这是本工程实际存在的一个缺口】
    /// 在这之前，各个系统各自判断"现在能不能响应输入"：
    ///   - Pistol 只检查 InventoryScreen.IsOpen
    ///   - InteractionProbe 完全没有屏蔽
    ///   - InventoryScreen 直接读键盘
    /// 结果是每加一个模式就要去改一遍所有响应输入的地方，漏一个就出 bug
    /// （例如"背包开着还能开枪"）。
    ///
    /// 所以抽出一个【唯一的模式开关】：谁想知道"现在能不能做这件事"，
    /// 就查这里，不要再各自判断。
    ///
    /// 【为什么不用事件驱动的状态机】
    /// 那会引入状态转换合法性、进入/退出钩子、重入等一堆问题，
    /// 而当前只有三个模式、切换点也很少。一个枚举 + 几个查询属性够用，
    /// 以后真需要了再升级（升级时只改这一个文件）。
    /// </summary>
    public enum InputMode
    {
        /// <summary>正常游玩：能移动、能开枪、能交互。</summary>
        Normal = 0,

        /// <summary>建造模式：能移动，不能开枪/交互，鼠标用于放置。</summary>
        Building = 1,

        /// <summary>界面打开（背包等）：不能移动、不能开枪、不能交互。</summary>
        Inventory = 2,
        /// <summary>死亡画面；只有复活流程可以解除。</summary>
        Dead = 3,
    }

    /// <summary>
    /// 输入模式的唯一权威。
    ///
    /// 【用静态类而不是单例 MonoBehaviour】理由：
    ///   1. 查询点很多（每个响应输入的组件都会问），静态调用最省事；
    ///   2. 它不持有任何场景对象，只是"当前是哪个模式"，没有生命周期问题；
    ///   3. 场景切换/重载时由 BuildController / InventoryScreen 显式重置，
    ///      不会留着一个卡住的模式。
    ///
    /// 【约定：退出键必须永远有效】
    /// 任何模式都必须能退出。所以"按 B 退出建造"、"按 Tab 关背包"
    /// 【不能】走这套开关去屏蔽自己 —— 否则玩家会被锁在某个模式里出不来。
    /// 这条约定在 BuildController 和 InventoryScreen 里都要遵守。
    /// </summary>
    public static class InputModeState
    {
        private static InputMode _current = InputMode.Normal;
        private static bool _playerDead;
        public static bool IsPlayerDead => _playerDead;

        /// <summary>当前模式。设置时如果值没变，不做任何事。</summary>
        public static InputMode Current
        {
            get => _playerDead ? InputMode.Dead : _current;
            set { if (!_playerDead) _current = value; }
        }

        /// <summary>角色能否移动。界面打开时不能。</summary>
        public static bool AllowsMovement => Current == InputMode.Normal || Current == InputMode.Building;

        /// <summary>能否开枪。只有正常游玩时能。</summary>
        public static bool AllowsCombat => Current == InputMode.Normal;

        /// <summary>能否按 E 交互。只有正常游玩时能。</summary>
        public static bool AllowsInteraction => Current == InputMode.Normal;

        /// <summary>能否放置/拆除建造物。只有建造模式能。</summary>
        public static bool AllowsBuilding => Current == InputMode.Building;

        /// <summary>
        /// 鼠标指针是否被建造系统占用。
        /// 建造模式下鼠标用于选格子，不该同时被相机拖拽之类的逻辑吃掉。
        /// </summary>
        public static bool PointerUsedByBuilding => Current == InputMode.Building;

        public static void SetPlayerDead(bool dead)
        {
            _playerDead = dead;
            if (!dead) _current = InputMode.Normal;
        }

        /// <summary>回到正常模式。场景重建、退出游戏时调。</summary>
        public static void Reset() => _current = InputMode.Normal;

        /// <summary>当前模式的名字（调试用）。静态类不能重写 ToString，所以用这个方法。</summary>
        public static string Describe() => Current.ToString();
    }
}
