using Game.Items;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Game.Player
{
    /// <summary>
    /// 背包界面。左侧 4×4 背包 + 4 格口袋，右侧主角模型展示，点击物品时弹出介绍小窗。
    ///
    /// 为什么还是用 OnGUI：
    ///   原型阶段需要"能看见格子、能点、能看到介绍"来验证背包逻辑，
    ///   而 OnGUI 零资源、零 prefab、零 Canvas 配置。等做正式 UI 时整个删掉换成
    ///   Canvas/UI Toolkit 即可，不会留下任何需要清理的东西。
    ///
    /// 这个类的职责边界：
    ///   - 只画界面 + 记录"当前选中哪一格" + 处理鼠标交互
    ///   - 不持有数据：数据在 PlayerInventory（背包/口袋两个 Inventory）
    ///   - 不写业务规则：装不装得下由 Inventory 自己决定
    /// 所以以后换成正式 UI，只需要重写这一个文件，其它逻辑都不用动。
    ///
    /// 【布局经验】整块界面必须【居中】，并且总宽高设上限。
    /// 早期版本把面板贴屏幕右边缘（x = Screen.width - 边距），结果在 Game 视图
    /// 分辨率设置（例如 4K UHD）远大于实际显示窗口时，面板被画到了看不见的地方。
    /// </summary>
    [DisallowMultipleComponent]
    public class InventoryScreen : MonoBehaviour
    {
        /// <summary>容器种类。选中状态需要知道"选的是背包还是口袋"。</summary>
        public enum ContainerKind
        {
            Backpack,
            Pocket
        }

        [Header("开关")]
        [Tooltip("打开/关闭背包的按键（手柄无法通过这个用，手柄走 Player/Inventory 动作）。")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.Tab;

        [Tooltip("同时监听输入动作 Player/Inventory（默认也绑了 Tab）。\n" +
                 "如果手改过 .inputactions 但 Unity 没有重新导入，这条链路可能失效 —— \n" +
                 "所以默认主要靠上面那个直接读键盘的按键。两条都开会重复触发（一开一关等于没动）。")]
        [SerializeField] private bool _alsoUseInputAction = false;

        [SerializeField] private bool _startOpen = false;

        [Tooltip("打开时解锁鼠标指针（方便点击格子）。关闭时恢复原来的锁定状态。")]
        [SerializeField] private bool _unlockCursorWhenOpen = true;

        [Header("布局")]
        [Tooltip("整体缩放系数。界面变大变小调这一个值就够了（它同时作用于格子、间距、字号、预览区）。\n" +
                 "1920×1080 下 2.0~2.5 比较合适。")]
        [SerializeField, Range(0.5f, 4f)] private float _uiScale = 2.2f;

        [Tooltip("缩放后的基准格子边长（最终边长 = 这个值 × 整体缩放）。")]
        [SerializeField] private int _slotSize = 38;

        [SerializeField] private int _slotSpacing = 4;

        [Tooltip("缩放后的基准预览区宽度。")]
        [SerializeField] private int _previewWidth = 300;

        [Tooltip("缩放后的基准预览区高度。正方形构图比较好看。")]
        [SerializeField] private int _previewHeight = 300;

        [Header("配色")]
        [SerializeField] private Color _panelColor = new Color(0f, 0f, 0f, 0.72f);
        [SerializeField] private Color _slotColor = new Color(1f, 1f, 1f, 0.11f);
        [SerializeField] private Color _slotSelectedColor = new Color(1f, 0.85f, 0.35f, 0.30f);
        [SerializeField] private Color _borderColor = new Color(1f, 1f, 1f, 0.25f);
        [SerializeField] private Color _textColor = new Color(0.94f, 0.94f, 0.92f);
        [SerializeField] private Color _hintColor = new Color(0.65f, 0.65f, 0.63f);
        [SerializeField] private Color _popupColor = new Color(0.06f, 0.06f, 0.08f, 0.96f);

        [Header("字号")]
        [SerializeField] private int _titleSize = 13;
        [SerializeField] private int _slotTextSize = 10;
        [SerializeField] private int _detailSize = 11;

        private ContainerKind _selectedContainer = ContainerKind.Backpack;
        private int _selectedIndex = -1;

        private PlayerInventory _playerInventory;
        private PlayerInputSource _input;
        private CharacterPreviewRig _preview;
        private bool _isOpen;
        private bool _cursorStateCaptured;
        private CursorLockMode _previousLockMode;
        private bool _previousCursorVisible;
        private bool _draggingPreview;

        // ---- 物品拖拽 ----
        //
        // 三态：
        //   _dragPending  = 在某个格子上按下了左键（还可能只是在"按住看简介"）
        //   _dragActive   = 鼠标已经离开源格 → 真的在拖
        //   松手          = 结算（落回原格=看简介；落在别格=移动）
        private bool _dragPending;
        private bool _dragActive;
        private ContainerKind _dragContainer;
        private int _dragIndex = -1;

        // 本帧鼠标停在哪一格（每帧重算）
        private ContainerKind _hoverContainer;
        private int _hoverIndex = -1;

        [Header("调试")]
        [Tooltip("打开后打印预览拖动的每条 IMGUI 事件，用来排查「拖动没反应」。")]
        [SerializeField] private bool _dragLog;

        private GUIStyle _titleStyle;
        private GUIStyle _centerStyle;
        private GUIStyle _countStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _hintStyle;

        /// <summary>背包是否打开（供其他系统查询）。</summary>
        public bool IsOpen => _isOpen;

        private void Awake()
        {
            _playerInventory = GetComponent<PlayerInventory>();

            if (_playerInventory == null)
            {
                // UI 需要拿到角色的容器。原型里场景只有一个玩家，按类型找是安全且明确的。
                // 以后接入正式的 UI 框架时，这里换成注入即可。
                _playerInventory = FindFirstObjectByType<PlayerInventory>();
            }

            if (_playerInventory == null)
            {
                Debug.LogWarning(
                    $"[{nameof(InventoryScreen)}] 没有找到 PlayerInventory，背包界面不会显示。", this);
            }

            _input = GetComponent<PlayerInputSource>();
            if (_input == null) _input = FindFirstObjectByType<PlayerInputSource>();

            _preview = FindFirstObjectByType<CharacterPreviewRig>();

            _isOpen = _startOpen;
            ApplyCursorState();
            SyncInputMode();
        }

        private void Update()
        {
            // 【背包打开时按 B】= 玩家想退出建造模式。
            // 必须先处理这个：否则建造控制器会退出建造、但背包关闭时又把模式恢复成
            // Building，造成"模式说在建造、控制器说没在建造"的卡死。
            if (_isOpen && BuildTogglePressed())
            {
                HandleBuildToggleWhileOpen();
                SetStatusMessage("已退出建造模式");
                return;
            }

            if (!TogglePressed())
            {
                // 关掉界面时也要把拖动状态清掉，否则鼠标状态会留着。
                if (_draggingPreview) _draggingPreview = false;
                return;
            }

            // 【约定：退出键必须永远有效】这里刻意不检查 InputModeState ——
            // 否则一旦别的系统把模式设成 Building，玩家就关不掉背包了。
            _isOpen = !_isOpen;
            _draggingPreview = false;
            ApplyCursorState();
            SyncInputMode();
        }

        /// <summary>本帧是否按了 B（退出建造）。和 TogglePressed 一样直接读键盘。</summary>
        private static bool BuildTogglePressed()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.bKey.wasPressedThisFrame;
        }

        /// <summary>
        /// 背包【打开期间】按了 B：说明玩家想退出建造模式。
        ///
        /// 【为什么必须处理这种情况】否则会出现"反向分家"：
        /// 建造控制器把模式设成 Normal（它退出了），但背包关闭时会恢复
        /// "打开背包之前的模式"（= Building）→ 模式说在建造、控制器说没在建造，界面卡住。
        ///
        /// 所以这里通知建造控制器把内部状态一起收干净，
        /// 并把"要恢复的模式"改成 Normal。
        /// </summary>
        private void HandleBuildToggleWhileOpen()
        {
            Game.Gameplay.Building.BuildController builder =
                FindFirstObjectByType<Game.Gameplay.Building.BuildController>();

            if (builder == null || !builder.IsBuilding) return;

            builder.ForceExitBuilding();
            _modeBeforeInventory = InputMode.Normal;
        }

        /// <summary>
        /// 把"界面是否打开"同步到全局输入模式。
        ///
        /// 【为什么要记住打开之前的模式 —— 这是一个真实的缺陷修复】
        /// 可复现顺序：`B 进建造 → Tab 开背包 → Tab 关背包`
        /// 早先关闭时无条件设成 Normal，于是建造控制器还以为自己在建造模式
        /// （它的 _building 仍是 true）→ 预览继续显示、放置失灵、
        /// 左键又开始开枪 —— 全局模式和控制器的内部状态分家了。
        ///
        /// 现在关闭时【恢复打开之前的模式】，分家就不会发生。
        /// 同理，打开时如果本来在建造模式，也不该把模式丢掉。
        /// </summary>
        private void SyncInputMode()
        {
            if (_isOpen)
            {
                // 记住"打开之前是什么模式"，关闭时恢复。
                // 只在从"非 Inventory"进入时记录，避免连续调用把记录覆盖成 Inventory 自己。
                if (InputModeState.Current != InputMode.Inventory)
                {
                    _modeBeforeInventory = InputModeState.Current;
                }

                InputModeState.Current = InputMode.Inventory;
            }
            else
            {
                InputModeState.Current = _modeBeforeInventory;
            }
        }

        /// <summary>打开背包之前处于哪个模式。默认正常游玩。</summary>
        private InputMode _modeBeforeInventory = InputMode.Normal;

        // ------------------------------------------------------------------ 布局计算

        // 布局用的固定间距（会乘以整体缩放）。
        // 提成常量让 GetPanelRect / OnGUI / 各个绘制方法共用同一套数值 ——
        // 分散写死会导致"算出来的矩形"和"画出来的位置"对不上，点击区域跟着错位。
        private const float BasePanelPadding = 16f;
        private const float BaseTitleHeight = 24f;
        private const float BaseBlockGap = 20f;
        private const float BaseColumnGap = 26f;

        /// <summary>
        /// 拖动鼠标 1 像素对应的模型旋转角度（装置缺失时的兜底值）。
        ///
        /// 【为什么放在这里而不是硬编码】灵敏度是"预览装置怎么转"的属性，
        /// 所以由 CharacterPreviewRig 提供（见它的 _dragSensitivity），
        /// 界面对应的只读属性在下面。装置缺失时退回这个默认值。
        /// </summary>
        private const float DefaultPreviewDragSensitivity = 2.0f;

        private float PreviewDragSensitivity =>
            _preview != null ? _preview.DragSensitivity : DefaultPreviewDragSensitivity;

        // ---- 缩放后的实际数值 ----
        private float SlotSize => _slotSize * _uiScale;
        private float SlotSpacing => _slotSpacing * _uiScale;
        private float PreviewWidth => _previewWidth * _uiScale;
        private float PreviewHeight => _previewHeight * _uiScale;

        private float PanelPadding => BasePanelPadding * _uiScale;
        private float TitleHeight => BaseTitleHeight * _uiScale;
        private float BlockGap => BaseBlockGap * _uiScale;
        private float ColumnGap => BaseColumnGap * _uiScale;

        private int ScaledFont(int baseSize) => Mathf.Max(6, Mathf.RoundToInt(baseSize * _uiScale));

        /// <summary>
        /// 显示一条短消息（例如"已退出建造模式"）。
        ///
        /// 【为什么需要】在背包里按 B 是对背包【之外】的系统产生影响的操作，
        /// 没有任何视觉反馈的话，玩家不知道自己刚才那一下做了什么。
        /// </summary>
        private void SetStatusMessage(string message)
        {
            _statusMessage = message;
            _statusUntil = Time.time + 2f;
        }

        private string _statusMessage = string.Empty;
        private float _statusUntil;

        /// <summary>界面的整体矩形（居中）。OnGUI 和输入处理共用，保证点击区域和绘制区域一致。</summary>
        private Rect GetPanelRect()
        {
            float gridWidth = 4 * SlotSize + 3 * SlotSpacing;
            float gridHeight = 4 * SlotSize + 3 * SlotSpacing;

            float leftColumnWidth = gridWidth;
            float leftColumnHeight = TitleHeight + gridHeight + BlockGap + TitleHeight + SlotSize;
            float rightColumnHeight = TitleHeight + PreviewHeight;

            float contentWidth = leftColumnWidth + ColumnGap + PreviewWidth;
            float contentHeight = Mathf.Max(leftColumnHeight, rightColumnHeight);

            float panelWidth = contentWidth + PanelPadding * 2f;
            float panelHeight = contentHeight + PanelPadding * 2f;

            float x = Mathf.Max(8f, (Screen.width - panelWidth) * 0.5f);
            float y = Mathf.Max(8f, (Screen.height - panelHeight) * 0.5f);

            return new Rect(x, y, panelWidth, panelHeight);
        }

        private void OnGUI()
        {
            if (!_isOpen || _playerInventory == null)
            {
                _draggingPreview = false;
                return;
            }

            EnsureStyles();

            // 落点每帧重算：下面画格子时会重新填。
            // 不清的话，鼠标移出所有格子后 _hoverIndex 会停在最后一个格子上，
            // 松手就会误判成"放到了那一格"。
            _hoverIndex = -1;

            Rect panel = GetPanelRect();

            // 全屏遮罩先画（IMGUI 先画的在下层）。
            Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.35f));
            Fill(panel, _panelColor);
            Border(panel, _borderColor);

            // ---- 顶部短消息（例如"已退出建造模式"）----
            if (Time.time < _statusUntil && !string.IsNullOrEmpty(_statusMessage))
            {
                GUI.color = new Color(1f, 0.85f, 0.5f);
                GUI.Label(new Rect(panel.x, panel.y - 26f * _uiScale, panel.width, 24f * _uiScale),
                    _statusMessage, _titleStyle);
                GUI.color = Color.white;
            }

            float gridWidth = 4 * SlotSize + 3 * SlotSpacing;
            float gridHeight = 4 * SlotSize + 3 * SlotSpacing;

            float leftX = panel.x + PanelPadding;
            float topY = panel.y + PanelPadding;

            // ---- 左列：背包 + 口袋 ----
            Rect backpackRect = new Rect(leftX, topY + TitleHeight, gridWidth, gridHeight);
            DrawGrid("背包", backpackRect, _playerInventory.Backpack, ContainerKind.Backpack);

            float pocketY = backpackRect.yMax + BlockGap + TitleHeight;
            Rect pocketRect = new Rect(leftX, pocketY, gridWidth, SlotSize);
            DrawGrid("口袋", pocketRect, _playerInventory.Pocket, ContainerKind.Pocket);

            // ---- 右列：主角模型 ----
            float previewX = leftX + gridWidth + ColumnGap;
            Rect previewRect = new Rect(previewX, topY + TitleHeight, PreviewWidth, PreviewHeight);
            DrawCharacterPreview(previewRect);

            // ---- 弹出介绍小窗（画在最上层，按住鼠标时显示）----
            DrawItemPopup();

            // ---- 物品拖拽（必须在画完所有格子之后：它靠本帧记录下的落点工作）----
            HandleItemDrag();

            // ---- 兜底：MouseUp 没收到时别让物品一直粘在鼠标上 ----
            RecoverStuckDrag();
        }

        // ------------------------------------------------------------------ 主角模型

        private void DrawCharacterPreview(Rect rect)
        {
            GUI.Label(new Rect(rect.x, rect.y - TitleHeight + 2f, rect.width, 20f), "主角", _titleStyle);

            Fill(rect, new Color(0.10f, 0.10f, 0.12f, 1f));
            Border(rect, _borderColor);

            if (_preview != null && _preview.IsReady && _preview.Texture != null)
            {
                GUI.DrawTexture(rect, _preview.Texture, ScaleMode.ScaleToFit, false);
                HandlePreviewDrag(rect);

                GUI.color = _hintColor;
                GUI.Label(new Rect(rect.x + 6f, rect.yMax - 20f * _uiScale, rect.width - 12f, 18f * _uiScale),
                    "按住拖动可旋转", _hintStyle);
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = _hintColor;
                GUI.Label(rect, "（模型预览未就绪）", _hintStyle);
                GUI.color = Color.white;
            }
        }

        /// <summary>
        /// 处理模型旋转：在预览区域内按住左键拖动。
        ///
        /// 【IMGUI 事件流】一次拖动会依次收到：
        ///   MouseDown（按下）→ MouseDrag（每次移动，可能很多次）→ MouseUp（松开）
        /// 所以用 _draggingPreview 记住"当前是否按住"，只在按住期间响应 MouseDrag。
        /// </summary>
        private void HandlePreviewDrag(Rect rect)
        {
            Event current = Event.current;

            // 右键/中键不参与，避免和以后可能加入的右键操作冲突。
            if (current == null || current.button != 0) return;

            bool inside = rect.Contains(current.mousePosition);

            switch (current.type)
            {
                case EventType.MouseDown when inside:
                    _draggingPreview = true;
                    current.Use();
                    break;

                case EventType.MouseUp:
                    _draggingPreview = false;
                    break;

                case EventType.MouseDrag when _draggingPreview:
                {
                    // 用 IMGUI 的横向位移直接转。
                    // 这里不读鼠标轴：本工程禁用了旧输入，而且 IMGUI 的 delta 更精确。
                    // 【只取 x】纵向位移被忽略 —— 这样就"只能左右旋转"。
                    float degrees = -current.delta.x * PreviewDragSensitivity;

                    if (_dragLog)
                    {
                        Debug.Log(
                            $"[预览拖动] MouseDrag delta=({current.delta.x:F2}, {current.delta.y:F2}) " +
                            $"→ 转 {degrees:F2}°，灵敏度={PreviewDragSensitivity:F2}，" +
                            $"装置={(_preview != null ? _preview.name : "【null】")}，" +
                            $"当前偏航={(_preview != null ? _preview.Yaw : 0f):F1}°");
                    }

                    if (_preview != null) _preview.AddYaw(degrees);
                    current.Use();
                    break;
                }

                case EventType.MouseDown:
                    // 按在预览区外面：不开始拖动（但也不要卡住旧状态）
                    if (_dragLog) Debug.Log($"[预览拖动] MouseDown 但在区域外（鼠标={current.mousePosition}，区域={rect}）");
                    break;
            }
        }

        // ------------------------------------------------------------------ 格子区

        private void DrawGrid(string title, Rect area, Inventory inventory, ContainerKind kind)
        {
            GUI.Label(new Rect(area.x, area.y - TitleHeight + 2f, 200f, TitleHeight), title, _titleStyle);

            // 格子数由容器容量决定（4×4 = 16、口袋 = 4），所以换容量不需要改这里。
            for (int i = 0; i < inventory.Capacity; i++)
            {
                int column = i % 4;
                int row = i / 4;

                Rect rect = new Rect(
                    area.x + column * (SlotSize + SlotSpacing),
                    area.y + row * (SlotSize + SlotSpacing),
                    SlotSize,
                    SlotSize);

                DrawSlot(rect, inventory.GetSlot(i), kind, i);
            }
        }

        private void DrawSlot(Rect rect, ItemStack stack, ContainerKind kind, int index)
        {
            bool selected = _selectedContainer == kind && _selectedIndex == index;
            bool empty = stack.IsEmpty;
            bool isDragSource = _dragActive && _dragContainer == kind && _dragIndex == index;

            Fill(rect, selected ? _slotSelectedColor : _slotColor);
            Border(rect, empty ? _borderColor : new Color(1f, 1f, 1f, 0.45f));

            // 记录鼠标是否停在这一格 —— 松手时要靠它判断"放到哪了"。
            //
            // 【为什么用 Rect.Contains 而不是 GUI 事件】格子的点击已经被下面的
            // RepeatButton 占用（"按住看简介"要用它）。再注册一套事件处理器会互相打架。
            // 用"当前绘制到哪个 Rect、鼠标在不在里面"来判定落点更直接，也不影响原交互。
            if (rect.Contains(Event.current.mousePosition))
            {
                _hoverContainer = kind;
                _hoverIndex = index;
            }

            // 拖动中的源格子画暗一点，表示"它被拿起来了"。
            if (isDragSource) Fill(rect, new Color(0f, 0f, 0f, 0.35f));

            // 用 RepeatButton：按住不放时每帧都返回 true。
            // 这样"按住 → 显示简介；松开 → 简介消失"的交互才能成立
            //（普通 Button 只在松开的瞬间触发一次点击，无法表达"按住"这个状态）。
            //
            // 拖动复用的是同一个按下动作：按住格子 = 同时进入"看简介"和"准备拖动"。
            // 松手时看落点：还在原格 → 只是看简介；落在别的格 → 执行移动。
            if (GUI.RepeatButton(rect, GUIContent.none, GUIStyle.none) && !empty)
            {
                _selectedContainer = kind;
                _selectedIndex = index;

                // 记录拖动起点。不在这里判定"是否在拖" ——
                // 按住不动也算按下，那时不该显示拖拽表现。
                _dragContainer = kind;
                _dragIndex = index;
                _dragPending = true;
            }

            if (empty) return;

            ItemDefinition item = stack.Item;

            Rect iconRect = new Rect(rect.x + 5f, rect.y + 4f, rect.width - 10f, rect.height - 17f);
            if (item.Icon != null && item.Icon.texture != null)
            {
                GUI.DrawTexture(iconRect, item.Icon.texture, ScaleMode.ScaleToFit, true);
            }
            else
            {
                Fill(iconRect, new Color(0.72f, 0.55f, 0.33f, 0.55f));
                GUI.color = _textColor;
                GUI.Label(iconRect, FirstChar(item.DisplayName), _centerStyle);
                GUI.color = Color.white;
            }

            Rect countRect = new Rect(rect.x, rect.yMax - 16f, rect.width - 3f, 15f);
            GUI.color = _textColor;
            GUI.Label(countRect, stack.Amount.ToString(), _countStyle);
            GUI.color = Color.white;
        }

        /// <summary>
        /// 处理物品拖拽。在每帧的 OnGUI 末尾调用一次。
        ///
        /// 【交互设计：一个按下动作，两种用途】
        ///   按住不动、松开在原格 → 只是看简介（原有行为，不能破坏）
        ///   按住后移到别的格、松开 → 把物品移过去
        /// 判定放在【松手】那一刻，用落点决定是哪种。
        ///
        /// 【为什么要等鼠标真正移动过】按住不动时鼠标也会停在格子里，
        /// 那时若就把物品"拎起来"，会造成视觉上的闪烁。
        /// 所以只有鼠标离开源格之后才进入 _dragActive。
        ///
        /// 【踩过的两个坑，都跟 Event.Use() 有关】
        ///   1. 在 Layout 事件上调用 Use() 会直接报
        ///      "Event.Use() should not be called for events of type Layout"。
        ///      IMGUI 每个事件类型都会走一遍 OnGUI，Layout 阶段不能消费事件。
        ///   2. Use() 会把事件标记为已处理，连带影响后续事件的分发 ——
        ///      表现是【松手后 MouseUp 收不到】，物品一直悬在鼠标上、放不下去。
        ///
        /// 所以这里【完全不调用 Use()】：落点判定是自己用 Rect.Contains 算的，
        /// 不依赖事件分发顺序，也就不需要抢事件。
        /// </summary>
        private void HandleItemDrag()
        {
            Event current = Event.current;
            if (current == null) return;

            // Layout 阶段什么都别做 —— 它只是让控件报名自己的位置，不处理输入。
            if (current.type == EventType.Layout) return;

            // ---- 松手：结算 ----
            if (current.type == EventType.MouseUp && current.button == 0)
            {
                if (_dragActive && _dragPending)
                {
                    CommitDrop();
                }

                ClearDragState();
                return;
            }

            // ---- 按住期间：判断是否真的开始拖 ----
            if (!_dragPending) return;

            // 鼠标已经离开源格 → 算作拖动
            if (!_dragActive)
            {
                bool leftSource = _hoverContainer != _dragContainer || _hoverIndex != _dragIndex;
                if (leftSource) _dragActive = true;
            }

            if (_dragActive)
            {
                DrawDragGhost();
            }
        }

        /// <summary>
        /// 兜底：鼠标已经松开、但拖拽状态还留着（说明 MouseUp 没收到）。
        ///
        /// 【为什么需要它】IMGUI 的事件分发在某些情况下会吞掉 MouseUp
        /// （鼠标移出窗口、失焦、别的控件抢了事件）。
        /// 没有兜底的话，物品会一直"粘"在鼠标上放不下去 —— 玩家看到的就是"卡住"。
        ///
        /// 每帧直接问"左键现在还按着吗"，比信任事件流可靠。
        /// </summary>
        private void RecoverStuckDrag()
        {
            if (!_dragPending) return;
            if (IsPointerHeld()) return;

            // 已经松开了却没收到 MouseUp —— 补一次结算
            if (_dragActive) CommitDrop();
            ClearDragState();
        }

        /// <summary>左键当前是否按住（用 Input System，本工程禁用了旧 Input）。</summary>
        private static bool IsPointerHeld()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && mouse.leftButton.isPressed;
        }

        /// <summary>在鼠标位置画一个"跟着手走"的物品图标。</summary>
        private void DrawDragGhost()
        {
            if (_dragIndex < 0) return;

            Inventory source = _dragContainer == ContainerKind.Backpack
                ? _playerInventory.Backpack
                : _playerInventory.Pocket;

            if (source == null) return;

            ItemStack stack = source.GetSlot(_dragIndex);
            if (stack.IsEmpty) return;

            Vector2 mouse = Event.current.mousePosition;
            var rect = new Rect(mouse.x - SlotSize * 0.5f, mouse.y - SlotSize * 0.5f, SlotSize, SlotSize);

            Fill(rect, new Color(_slotColor.r, _slotColor.g, _slotColor.b, 0.85f));
            Border(rect, new Color(1f, 1f, 1f, 0.8f));

            Rect iconRect = new Rect(rect.x + 5f, rect.y + 4f, rect.width - 10f, rect.height - 17f);
            ItemDefinition item = stack.Item;

            if (item != null && item.Icon != null && item.Icon.texture != null)
            {
                GUI.DrawTexture(iconRect, item.Icon.texture, ScaleMode.ScaleToFit, true);
            }
            else if (item != null)
            {
                Fill(iconRect, new Color(0.72f, 0.55f, 0.33f, 0.55f));
                GUI.color = _textColor;
                GUI.Label(iconRect, FirstChar(item.DisplayName), _centerStyle);
                GUI.color = Color.white;
            }

            GUI.color = _textColor;
            GUI.Label(new Rect(rect.x, rect.yMax - 16f, rect.width - 3f, 15f),
                stack.Amount.ToString(), _countStyle);
            GUI.color = Color.white;
        }

        /// <summary>松手时把物品放到落点。</summary>
        private void CommitDrop()
        {
            if (_dragIndex < 0) return;

            // 落点不在任何格子上 → 取消（以后可以扩展成"丢到世界里"）
            if (_hoverIndex < 0) return;

            Inventory source = _dragContainer == ContainerKind.Backpack ? _playerInventory.Backpack : _playerInventory.Pocket;
            Inventory target = _hoverContainer == ContainerKind.Backpack ? _playerInventory.Backpack : _playerInventory.Pocket;

            if (source == null || target == null) return;

            // 落回原格 → 什么都不做（这是"只想看简介"的情况）
            if (source == target && _dragIndex == _hoverIndex) return;

            bool moved = Inventory.MoveBetween(source, _dragIndex, target, _hoverIndex);

            if (!moved && _dragLog)
            {
                Debug.Log($"[背包拖动] 移动失败：{_dragContainer}[{_dragIndex}] → " +
                          $"{_hoverContainer}[{_hoverIndex}]（目标放不下，或没有可移动的内容）");
            }
        }

        private void ClearDragState()
        {
            _dragPending = false;
            _dragActive = false;
            _dragIndex = -1;
        }

        /// <summary>本帧鼠标是否停在某个格子上（没停在任何格子上时为 false）。</summary>
        private bool HasHoverSlot => _hoverIndex >= 0;

        private static string FirstChar(string text)
        {
            if (string.IsNullOrEmpty(text)) return "?";
            return text.Substring(0, 1);
        }

        // ------------------------------------------------------------------ 介绍小窗

        /// <summary>
        /// 介绍小窗：**按住鼠标时显示，松开就消失**。
        ///
        /// 实现要点：格子用的是 GUI.RepeatButton（按住期间每帧都返回 true），
        /// 所以"按住"这个状态能表达出来。松开时清掉选中，小窗自然消失。
        /// 用点击切换（Toggle）就无法做到这一点。
        ///
        /// 【读鼠标必须用 Input System】本工程 activeInputHandler = Input System 包，
        /// 用旧的 UnityEngine.Input 类会直接抛 InvalidOperationException（本工程踩过）。
        /// </summary>
        private void DrawItemPopup()
        {
            // 拖动中不显示简介小窗 —— 否则它会跟着鼠标挡住要放的目标格，
            // 玩家看不到自己正把东西放到哪。
            if (_dragActive) return;

            // 松开左键 → 取消选中 → 小窗消失。
            if (IsPointerReleasedThisFrame())
            {
                _selectedIndex = -1;
                return;
            }

            if (_selectedIndex < 0) return;

            Inventory source = _selectedContainer == ContainerKind.Backpack
                ? _playerInventory.Backpack
                : _playerInventory.Pocket;

            ItemStack selected = source.GetSlot(_selectedIndex);
            if (selected.IsEmpty)
            {
                // 物品被移走了：自动取消选中，不要留一个空窗。
                _selectedIndex = -1;
                return;
            }

            ItemDefinition item = selected.Item;

            float width = 250f * _uiScale;
            float padding = 10f * _uiScale;

            string description = string.IsNullOrEmpty(item.Description) ? "（暂无描述）" : item.Description;

            float textWidth = width - padding * 2f;
            float descriptionHeight = _bodyStyle.CalcHeight(new GUIContent(description), textWidth);
            float lineHeight = 20f * _uiScale;
            float height = padding + lineHeight + lineHeight * 0.9f + lineHeight + lineHeight * 0.4f
                           + descriptionHeight + padding;

            Vector2 mouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;

            float x = mouse.x + 18f * _uiScale;
            float y = mouse.y + 12f * _uiScale;

            // 贴边避让：右边放不下就放到鼠标左边；下面放不下就往上挪。
            if (x + width > Screen.width - 4f) x = mouse.x - width - 18f * _uiScale;
            if (y + height > Screen.height - 4f) y = Screen.height - height - 4f;

            x = Mathf.Max(4f, x);
            y = Mathf.Max(4f, y);

            Rect popup = new Rect(x, y, width, height);

            Fill(popup, _popupColor);
            Border(popup, new Color(1f, 0.85f, 0.35f, 0.7f));

            float innerX = popup.x + padding;
            float cursorY = popup.y + padding;

            GUI.color = _textColor;
            GUI.Label(new Rect(innerX, cursorY, textWidth, lineHeight), item.DisplayName, _titleStyle);
            cursorY += lineHeight;

            GUI.color = _hintColor;
            GUI.Label(new Rect(innerX, cursorY, textWidth, lineHeight), $"ID：{item.ItemId}", _hintStyle);
            cursorY += lineHeight * 0.9f;

            GUI.color = _textColor;
            GUI.Label(new Rect(innerX, cursorY, textWidth, lineHeight),
                $"数量：{selected.Amount} / {item.MaxStackSize}", _bodyStyle);
            cursorY += lineHeight;

            Fill(new Rect(innerX, cursorY, textWidth, 1f), _borderColor);
            cursorY += lineHeight * 0.4f;

            GUI.Label(new Rect(innerX, cursorY, textWidth, descriptionHeight), description, _bodyStyle);
            GUI.color = Color.white;
        }

        // ------------------------------------------------------------------ 开关与鼠标

        /// <summary>
        /// 本帧是否按下了开关键。
        ///
        /// 主要走 Keyboard.current 直接读设备，而不是走 .inputactions 里的动作：
        /// 动作资产是手工改过 JSON 的，Unity 不一定会重新导入，那条链路会静默失效
        /// （表现就是"按了没反应"，而且没有任何报错）。
        /// 直接读键盘没有这层风险，也不影响以后要做按键重映射时切回动作。
        /// </summary>
        private bool TogglePressed()
        {
            if (Keyboard.current != null && _toggleKey != KeyCode.None)
            {
                KeyControl control = Keyboard.current[ConvertToKey(_toggleKey)];
                if (control != null && control.wasPressedThisFrame) return true;
            }

            return _alsoUseInputAction && _input != null && _input.InventoryTogglePressed;
        }

        /// <summary>KeyCode → Input System 的 Key。只覆盖原型用得到的按键，其它返回 None（即忽略）。</summary>
        private static Key ConvertToKey(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.I: return Key.I;
                case KeyCode.B: return Key.B;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.F1: return Key.F1;
                case KeyCode.F2: return Key.F2;
                default: return Key.None;
            }
        }

        /// <summary>
        /// 本帧左键是否刚松开。
        ///
        /// 用 Input System 的鼠标设备，不用 UnityEngine.Input（本工程禁用了旧输入，会抛异常）。
        /// 另外保留 IMGUI 事件的判断作为兜底 —— 两种途径任一生效即可，
        /// 避免因为 OnGUI 只在特定事件类型下被调用而漏掉"松开"这一刻。
        /// </summary>
        private bool IsPointerReleasedThisFrame()
        {
            if (Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame) return true;

            Event current = Event.current;
            return current != null && current.type == EventType.MouseUp && current.button == 0;
        }

        private void ApplyCursorState()
        {
            if (!_unlockCursorWhenOpen) return;

            if (_isOpen)
            {
                // 记下打开之前的状态，关闭时原样恢复 —— 不要假设"关闭就是锁定"，
                // 否则以后接上鼠标视角后会把别人的设置改坏。
                if (!_cursorStateCaptured)
                {
                    _previousLockMode = Cursor.lockState;
                    _previousCursorVisible = Cursor.visible;
                    _cursorStateCaptured = true;
                }

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            if (!_cursorStateCaptured) return;

            Cursor.lockState = _previousLockMode;
            Cursor.visible = _previousCursorVisible;
            _cursorStateCaptured = false;
        }

        private void OnDisable()
        {
            // 【必须】界面被禁用/销毁时退回正常模式，
            // 否则会留下一个卡在 Inventory 的全局状态 —— 玩家连路都走不了。
            if (InputModeState.Current == InputMode.Inventory) InputModeState.Current = InputMode.Normal;

            // 组件被禁用/销毁时不要把鼠标锁死。
            if (!_cursorStateCaptured) return;

            Cursor.lockState = _previousLockMode;
            Cursor.visible = _previousCursorVisible;
            _cursorStateCaptured = false;
        }

        // ------------------------------------------------------------------ 绘制辅助

        private void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;

            // 用 Unity 内置的 whiteTexture，而不是自己 new Texture2D：
            // 自建贴图如果创建/上传失败，GUI.DrawTexture 会【静默什么都不画】，
            // 表现就是"代码跑完了但屏幕没变化"，非常难查（本工程实际踩到过）。
            // 内置贴图一定可用。
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            GUI.color = previous;
        }

        private void Border(Rect rect, Color color)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1f), color);
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            Fill(new Rect(rect.x, rect.y, 1f, rect.height), color);
            Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = ScaledFont(_titleSize),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };

            _centerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = ScaledFont(_slotTextSize + 7),
                alignment = TextAnchor.MiddleCenter
            };

            _countStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = ScaledFont(_slotTextSize),
                alignment = TextAnchor.LowerRight
            };

            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = ScaledFont(_detailSize),
                alignment = TextAnchor.UpperLeft,
                wordWrap = true
            };

            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = ScaledFont(_detailSize),
                alignment = TextAnchor.UpperLeft,
                wordWrap = true
            };
        }
    }
}
