using Game.Items;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Game.Player
{
    /// <summary>背包绘制与指针控制。输入只在 Update 处理一次，不让 IMGUI 控件争抢拖动事件。</summary>
    [DisallowMultipleComponent]
    public class InventoryScreen : MonoBehaviour
    {
        public enum ContainerKind { Backpack, Pocket, Equipment }
        [Header("开关")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.Tab;
        [SerializeField] private bool _alsoUseInputAction;
        [SerializeField] private bool _startOpen;
        [SerializeField] private bool _unlockCursorWhenOpen = true;
        [Header("布局")]
        [SerializeField, Range(0.5f, 4f)] private float _uiScale = 2.2f;
        [SerializeField] private int _slotSize = 38;
        [SerializeField] private int _slotSpacing = 4;
        [SerializeField] private int _previewWidth = 300;
        [SerializeField] private int _previewHeight = 300;
        [Header("配色")]
        [SerializeField] private Color _panelColor = new Color(0.04f,0.045f,0.055f,0.97f);
        [SerializeField] private Color _slotColor = new Color(1,1,1,0.11f);
        [SerializeField] private Color _slotSelectedColor = new Color(1,0.85f,0.35f,0.3f);
        [SerializeField] private Color _borderColor = new Color(1,1,1,0.25f);
        [SerializeField] private Color _textColor = new Color(0.94f,0.94f,0.92f);
        [SerializeField] private Color _hintColor = new Color(0.65f,0.65f,0.63f);
        [SerializeField] private Color _popupColor = new Color(0.06f,0.06f,0.08f,0.98f);
        [Header("字号")]
        [SerializeField] private int _titleSize = 13;
        [SerializeField] private int _slotTextSize = 10;
        [SerializeField] private int _detailSize = 11;
        [SerializeField] private bool _dragLog;
        private PlayerInventory _playerInventory;
        private PlayerInputSource _input;
        private CharacterPreviewRig _preview;
        private bool _isOpen, _cursorStateCaptured, _previousCursorVisible;
        private CursorLockMode _previousLockMode;
        private InputMode _modeBeforeInventory;
        private bool _draggingPreview, _dragPending, _dragActive;
        private ContainerKind _dragContainer, _hoverContainer, _selectedContainer;
        private int _dragIndex = -1, _hoverIndex = -1, _selectedIndex = -1;
        private Vector2 _pointer, _lastPointer, _pressPosition, _grabOffset;
        private string _statusMessage;
        private float _statusUntil, _styleScale;
        private GUIStyle _titleStyle, _bodyStyle, _centerStyle, _hintStyle, _countStyle;
        public bool IsOpen => _isOpen;
        private int EquipmentRows => Mathf.Max(1, Mathf.CeilToInt(EquipmentCount / 4f));
        private int EquipmentCount => _playerInventory != null ? _playerInventory.Equipment.Capacity : 4;
        private float BaseGrid => 4 * _slotSize + 3 * _slotSpacing;
        private float BaseWidth => 32f + BaseGrid + 26f + _previewWidth;
        private float BaseHeight => 32f + Mathf.Max(24f + BaseGrid + 20f + 24f + _slotSize,
            24f + _previewHeight + 20f + 24f + EquipmentRows * (_slotSize + 18f + _slotSpacing));
        private float Scale => Mathf.Max(0.1f, Mathf.Min(_uiScale,
            (Screen.width - 24f) / BaseWidth, (Screen.height - 24f) / BaseHeight));
        private float SlotSize => _slotSize * Scale;
        private float Gap => _slotSpacing * Scale;
        private Rect Panel => new Rect((Screen.width - BaseWidth * Scale) * 0.5f,
            (Screen.height - BaseHeight * Scale) * 0.5f, BaseWidth * Scale, BaseHeight * Scale);
        public Rect PreviewRect
        {
            get
            {
                Rect p = Panel;
                return new Rect(p.x + (16 + BaseGrid + 26) * Scale, p.y + 40 * Scale,
                    _previewWidth * Scale, _previewHeight * Scale);
            }
        }

        public Rect GetSlotRect(ContainerKind kind, int index)
        {
            Rect p = Panel;
            float x = p.x + 16 * Scale, y = p.y + 40 * Scale;
            if (kind == ContainerKind.Pocket) y += (BaseGrid + 44) * Scale;
            if (kind == ContainerKind.Equipment)
            {
                Rect preview = PreviewRect;
                x = preview.x + (preview.width - BaseGrid * Scale) * 0.5f;
                y = preview.yMax + 44 * Scale;
            }
            int row = index / 4;
            return new Rect(x + index % 4 * (SlotSize + Gap),
                y + row * (SlotSize + Gap + (kind == ContainerKind.Equipment ? 18 * Scale : 0)),
                SlotSize, SlotSize);
        }

        private Inventory Container(ContainerKind kind) => kind == ContainerKind.Backpack
            ? _playerInventory.Backpack : kind == ContainerKind.Pocket ? _playerInventory.Pocket : _playerInventory.Equipment;

        private void Awake()
        {
            _playerInventory = GetComponent<PlayerInventory>();
            if (_playerInventory == null) _playerInventory = FindFirstObjectByType<PlayerInventory>();
            _input = GetComponent<PlayerInputSource>();
            if (_input == null) _input = FindFirstObjectByType<PlayerInputSource>();
            _preview = FindFirstObjectByType<CharacterPreviewRig>();
            SetOpen(_startOpen);
        }

        private void Update()
        {
            if (InputModeState.IsPlayerDead) { CloseForDeath(); return; }
            Keyboard keyboard = Keyboard.current;
            if (_isOpen && keyboard != null && keyboard.bKey.wasPressedThisFrame)
            {
                var builder = GetComponent<Game.Gameplay.Building.BuildController>();
                if (builder == null) builder = FindFirstObjectByType<Game.Gameplay.Building.BuildController>();
                builder?.ForceExitBuilding();
                _modeBeforeInventory = InputMode.Normal;
                InputModeState.Current = InputMode.Inventory;
            }
            if (TogglePressed()) SetOpen(!_isOpen);
            if (!_isOpen || _playerInventory == null) return;
            if (_preview == null) _preview = FindFirstObjectByType<CharacterPreviewRig>();
            Mouse mouse = Mouse.current;
            if (mouse == null) { CancelPointer(); return; }
            Vector2 raw = mouse.position.ReadValue();
            HandlePointer(new Vector2(raw.x, Screen.height - raw.y),
                mouse.leftButton.wasPressedThisFrame, mouse.leftButton.isPressed,
                mouse.leftButton.wasReleasedThisFrame);
        }

        private bool TogglePressed()
        {
            Keyboard keyboard = Keyboard.current;
            KeyControl key = null;
            if (keyboard != null)
                switch (_toggleKey)
                {
                    case KeyCode.Tab: key = keyboard.tabKey; break;
                    case KeyCode.I: key = keyboard.iKey; break;
                    case KeyCode.B: key = keyboard.bKey; break;
                    case KeyCode.Escape: key = keyboard.escapeKey; break;
                    case KeyCode.BackQuote: key = keyboard.backquoteKey; break;
                    case KeyCode.F1: key = keyboard.f1Key; break;
                    case KeyCode.F2: key = keyboard.f2Key; break;
                }
            bool direct = key != null && key.wasPressedThisFrame;
            return direct || (_alsoUseInputAction && _input != null && _input.InventoryTogglePressed);
        }

        public void SetOpen(bool open)
        {
            if (open && InputModeState.IsPlayerDead) return;
            CancelPointer(); _selectedIndex = -1;
            if (open && !_isOpen)
            {
                _modeBeforeInventory = InputModeState.Current;
                InputModeState.Current = InputMode.Inventory;
            }
            else if (!open && _isOpen && InputModeState.Current == InputMode.Inventory)
                InputModeState.Current = _modeBeforeInventory;
            _isOpen = open;
            ApplyCursorState();
        }

        private void HandlePointer(Vector2 position, bool pressed, bool held, bool released)
        {
            _pointer = position;
            HitTest(position, out _hoverContainer, out _hoverIndex);
            if (pressed)
            {
                CancelPointer();
                _pressPosition = _lastPointer = position;
                if (_hoverIndex >= 0)
                {
                    _selectedContainer = _hoverContainer; _selectedIndex = _hoverIndex;
                    if (!Container(_hoverContainer).GetSlot(_hoverIndex).IsEmpty)
                    {
                        _dragContainer = _hoverContainer; _dragIndex = _hoverIndex; _dragPending = true;
                        _grabOffset = position - GetSlotRect(_dragContainer, _dragIndex).position;
                    }
                }
                else if (PreviewRect.Contains(position) && _preview != null && _preview.IsReady)
                {
                    _draggingPreview = true; _preview.SetDragging(true);
                }
                else _selectedIndex = -1;
            }
            if (_draggingPreview && held)
            {
                float degrees = -(position.x - _lastPointer.x) * 180f /
                    Mathf.Max(1f, PreviewRect.width) * _preview.DragSensitivity;
                _preview.AddYaw(degrees);
            }
            if (_dragPending && (held || released) && (position - _pressPosition).sqrMagnitude >= 36f)
                _dragActive = true;
            if (released || (!held && !pressed))
            {
                if (_dragPending && _dragActive && _hoverIndex >= 0 &&
                    !(_dragContainer == _hoverContainer && _dragIndex == _hoverIndex))
                {
                    bool moved = Inventory.MoveBetween(Container(_dragContainer), _dragIndex,
                        Container(_hoverContainer), _hoverIndex);
                    if (!moved) SetStatus("无法放入：请检查装备类型、目标格和堆叠上限");
                    else { _selectedContainer = _hoverContainer; _selectedIndex = _hoverIndex; }
                    if (_dragLog) Debug.Log("[背包] 拖放 " + (moved ? "成功" : "被规则拒绝"), this);
                }
                CancelPointer();
            }
            _lastPointer = position;
        }

        private void HitTest(Vector2 point, out ContainerKind kind, out int index)
        {
            for (int kindIndex = 0; kindIndex < 3; kindIndex++)
            {
                ContainerKind candidate = (ContainerKind)kindIndex;
                for (int i = 0; i < Container(candidate).Capacity; i++)
                    if (GetSlotRect(candidate, i).Contains(point)) { kind = candidate; index = i; return; }
            }
            kind = ContainerKind.Backpack; index = -1;
        }

        private void CancelPointer()
        {
            _dragPending = _dragActive = _draggingPreview = false; _dragIndex = -1;
            if (_preview != null) _preview.SetDragging(false);
        }
        public void CloseForDeath()
        {
            SetOpen(false); _modeBeforeInventory = InputMode.Normal;
        }
        private void OnApplicationFocus(bool focused) { if (!focused) CancelPointer(); }
        private void OnDisable() => SetOpen(false);

        private void ApplyCursorState()
        {
            if (!_unlockCursorWhenOpen) return;
            if (_isOpen)
            {
                if (!_cursorStateCaptured)
                {
                    _previousLockMode = Cursor.lockState; _previousCursorVisible = Cursor.visible;
                    _cursorStateCaptured = true;
                }
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }
            else if (_cursorStateCaptured)
            {
                Cursor.lockState = _previousLockMode; Cursor.visible = _previousCursorVisible;
                _cursorStateCaptured = false;
            }
        }

        private void SetStatus(string text) { _statusMessage = text; _statusUntil = Time.unscaledTime + 3f; }
        private void OnGUI()
        {
            if (!_isOpen || _playerInventory == null || InputModeState.IsPlayerDead) return;
            EnsureStyles();
            Fill(new Rect(0,0,Screen.width,Screen.height), new Color(0,0,0,0.4f));
            Fill(Panel, _panelColor); Border(Panel, _borderColor);
            DrawContainer("背包", ContainerKind.Backpack);
            DrawContainer("口袋", ContainerKind.Pocket);
            Rect preview = PreviewRect;
            Label(new Rect(preview.x, preview.y - 24 * Scale, preview.width, 24 * Scale), "主角 · 按住左右拖动旋转", _titleStyle);
            Fill(preview, new Color(0.10f,0.10f,0.12f,1)); Border(preview, _borderColor);
            if (_preview != null && _preview.IsReady && _preview.Texture != null)
                GUI.DrawTexture(preview, _preview.Texture, ScaleMode.ScaleToFit, false);
            else Label(preview, "模型预览未就绪", _hintStyle);
            DrawContainer("装备", ContainerKind.Equipment);
            if (Time.unscaledTime < _statusUntil)
                Label(new Rect(Panel.x + 8 * Scale, Panel.yMax - 20 * Scale, Panel.width - 16 * Scale, 20 * Scale),
                    _statusMessage, _hintStyle);
            if (_dragActive)
            {
                var ghost = new Rect(_pointer - _grabOffset, new Vector2(SlotSize, SlotSize));
                Fill(ghost, new Color(0.18f,0.20f,0.24f,0.95f));
                DrawStack(ghost, Container(_dragContainer).GetSlot(_dragIndex));
                Border(ghost, _textColor);
            }
            else if (!_draggingPreview) DrawDetails();
        }

        private void DrawContainer(string title, ContainerKind kind)
        {
            Inventory inventory = Container(kind);
            Rect first = GetSlotRect(kind, 0);
            Label(new Rect(first.x, first.y - 24 * Scale, BaseGrid * Scale, 24 * Scale),
                title + (kind == ContainerKind.Equipment ? "" : "  " + inventory.SlotCount + "/" + inventory.Capacity), _titleStyle);
            for (int i = 0; i < inventory.Capacity; i++)
            {
                Rect rect = GetSlotRect(kind, i);
                bool hover = _hoverIndex == i && _hoverContainer == kind;
                bool selected = _selectedIndex == i && _selectedContainer == kind;
                Color border = _borderColor;
                if (hover && _dragActive)
                    border = Inventory.CanMoveBetween(Container(_dragContainer), _dragIndex, inventory, i)
                        ? new Color(0.3f,1f,0.5f) : new Color(1f,0.25f,0.2f);
                Fill(rect, selected ? _slotSelectedColor : _slotColor);
                Border(rect, border);
                if (!(_dragActive && _dragContainer == kind && _dragIndex == i))
                    DrawStack(rect, inventory.GetSlot(i));
                if (kind == ContainerKind.Equipment)
                    Label(new Rect(rect.x - 4 * Scale, rect.yMax, rect.width + 8 * Scale, 18 * Scale),
                        _playerInventory.Equipment.Definitions[i].DisplayName, _hintStyle);
            }
        }

        private void DrawStack(Rect rect, ItemStack stack)
        {
            if (stack.IsEmpty) return;
            Rect icon = new Rect(rect.x + 4 * Scale, rect.y + 3 * Scale,
                rect.width - 8 * Scale, rect.height - 12 * Scale);
            if (stack.Item.Icon != null)
            {
                Sprite sprite = stack.Item.Icon;
                Rect uv = sprite.textureRect;
                uv = new Rect(uv.x / sprite.texture.width, uv.y / sprite.texture.height,
                    uv.width / sprite.texture.width, uv.height / sprite.texture.height);
                GUI.DrawTextureWithTexCoords(icon, sprite.texture, uv, true);
            }
            else Label(icon, string.IsNullOrEmpty(stack.Item.DisplayName) ? "?" :
                stack.Item.DisplayName.Substring(0,1), _centerStyle);
            if (stack.Amount > 1)
                Label(new Rect(rect.x, rect.yMax - 14 * Scale, rect.width - 3 * Scale, 14 * Scale),
                    stack.Amount.ToString(), _countStyle);
        }

        private void DrawDetails()
        {
            if (_hoverIndex < 0) return;
            ItemStack item = Container(_hoverContainer).GetSlot(_hoverIndex);
            if (item.IsEmpty) return;
            string text = item.Item.DisplayName + "\n" + item.Item.Description;
            if (item.Item.EquipmentType != null) text += "\n装备类型：" + item.Item.EquipmentType.DisplayName;
            float width = Mathf.Min(240 * Scale, Screen.width - 24f);
            float height = _bodyStyle.CalcHeight(new GUIContent(text), width - 16 * Scale) + 16 * Scale;
            var rect = new Rect(Mathf.Clamp(_pointer.x + 16, 8, Screen.width - width - 8),
                Mathf.Clamp(_pointer.y + 20, 8, Screen.height - height - 8), width, height);
            Fill(rect, _popupColor); Border(rect, _borderColor);
            Label(new Rect(rect.x + 8 * Scale, rect.y + 8 * Scale,
                rect.width - 16 * Scale, rect.height - 16 * Scale), text, _bodyStyle);
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null && Mathf.Approximately(_styleScale, Scale)) return;
            _styleScale = Scale;
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(10, Mathf.RoundToInt(_titleSize * Scale)) };
            _bodyStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = Mathf.Max(10, Mathf.RoundToInt(_detailSize * Scale)) };
            _centerStyle = new GUIStyle(_titleStyle) { alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.Max(12, Mathf.RoundToInt((_slotTextSize + 8) * Scale)) };
            _hintStyle = new GUIStyle(_bodyStyle) { alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.Max(9, Mathf.RoundToInt(9 * Scale)) };
            _hintStyle.normal.textColor = _hintColor;
            _countStyle = new GUIStyle(_bodyStyle) { alignment = TextAnchor.LowerRight };
        }
        private void Label(Rect rect, string text, GUIStyle style)
        { Color before = GUI.color; GUI.color = _textColor; GUI.Label(rect, text, style); GUI.color = before; }
        private static void Fill(Rect rect, Color color)
        { Color before = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = before; }
        private static void Border(Rect rect, Color color)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1), color); Fill(new Rect(rect.x, rect.yMax-1, rect.width, 1), color);
            Fill(new Rect(rect.x, rect.y, 1, rect.height), color); Fill(new Rect(rect.xMax-1, rect.y, 1, rect.height), color);
        }
    }
}
