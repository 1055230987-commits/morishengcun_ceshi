using Game.Items;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 极简 HUD：显示"当前能交互什么"和"背包里有什么"。
    ///
    /// 为什么用 OnGUI 而不是 Canvas：
    ///   原型阶段只需要"看得见数值"来验证系统是否工作。
    ///   OnGUI 零设置、零资源、零 prefab —— 等真正做 UI 时整个删掉换成 Canvas 即可，
    ///   不会留下任何需要清理的东西。
    /// </summary>
    [DisallowMultipleComponent]
    public class InteractionHud : MonoBehaviour
    {
        [Header("开关")]
        [SerializeField] private bool _showHud = true;
        [Tooltip("在编辑器里也可以关掉，避免挡住画面。")]
        [SerializeField] private bool _showPrompt = true;

        [Header("外观")]
        [SerializeField] private int _fontSize = 20;
        [SerializeField] private Color _promptColor = new Color(1f, 0.95f, 0.6f);
        [SerializeField] private Color _messageColor = new Color(0.6f, 1f, 0.7f);

        [Header("血条")]
        [Tooltip("血条宽度（像素）。")]
        [SerializeField] private float _healthBarWidth = 260f;

        [Tooltip("血条高度（像素）。")]
        [SerializeField] private float _healthBarHeight = 22f;

        [Tooltip("血条离屏幕左边和底边的距离。")]
        [SerializeField] private Vector2 _healthBarMargin = new Vector2(20f, 20f);

        [Tooltip("高血量时的颜色。")]
        [SerializeField] private Color _healthHighColor = new Color(0.35f, 0.8f, 0.35f);

        [Tooltip("低血量时的颜色。血量低于 LowThreshold 时在这两个颜色之间插值。")]
        [SerializeField] private Color _healthLowColor = new Color(0.85f, 0.25f, 0.2f);

        [Tooltip("低于这个比例开始变红。")]
        [SerializeField, Range(0.05f, 0.9f)] private float _healthLowThreshold = 0.35f;

        [Tooltip("血条背景色。")]
        [SerializeField] private Color _healthBackColor = new Color(0.08f, 0.08f, 0.08f, 0.85f);

        [Tooltip("血条描边色。")]
        [SerializeField] private Color _healthBorderColor = new Color(0f, 0f, 0f, 0.9f);

        private InteractionProbe _probe;
        private Inventory _inventory;
        private Inventory _pocket;
        private PlayerHealth _health;
        private GUIStyle _promptStyle;
        private GUIStyle _messageStyle;
        private GUIStyle _healthTextStyle;
        private float _messageHideTime;
        private uint _messageVersion;
        private string _inventoryText;
        private bool _inventoryDirty = true;

        private void Awake()
        {
            _probe = GetComponent<InteractionProbe>();
            _inventory = PlayerInventory.GetInventory(gameObject);
            _pocket = PlayerInventory.GetPocket(gameObject);
            _health = GetComponent<PlayerHealth>();

            if (_inventory != null)
            {
                _inventory.Changed += OnInventoryChanged;
            }

            if (_pocket != null)
            {
                _pocket.Changed += OnInventoryChanged;
            }

            if (_inventory == null || _pocket == null)
            {
                Debug.LogWarning(
                    $"[{nameof(InteractionHud)}] 容器接入不完整：背包={(_inventory != null ? "正常" : "【null】")}、" +
                    $"口袋={(_pocket != null ? "正常" : "【null】")}。" +
                    "通常是玩家物体上没有 PlayerInventory 组件，或它还没初始化。", this);
            }
        }

        private void OnDestroy()
        {
            if (_inventory != null) _inventory.Changed -= OnInventoryChanged;
            if (_pocket != null) _pocket.Changed -= OnInventoryChanged;
        }

        private void OnInventoryChanged()
        {
            _inventoryDirty = true;
        }

        private void Update()
        {
            // 交互消息显示 2 秒后淡出。
            if (_probe != null && _probe.MessageVersion != _messageVersion)
            {
                _messageVersion = _probe.MessageVersion;
                _messageHideTime = Time.time + 2f;
            }
        }

        private void OnGUI()
        {
            if (!_showHud || Event.current.type != EventType.Repaint) return;

            EnsureStyles();

            if (_showPrompt && _probe != null && _probe.CurrentTarget != null)
            {
                string prompt = $"[E] {_probe.CurrentTarget.InteractionPrompt}";
                Vector2 size = _promptStyle.CalcSize(new GUIContent(prompt));
                Rect rect = new Rect((Screen.width - size.x) * 0.5f, Screen.height - 90f, size.x, size.y);

                GUI.color = _promptColor;
                GUI.Label(rect, prompt, _promptStyle);
                GUI.color = Color.white;
            }

            if (_probe != null && Time.time < _messageHideTime)
            {
                string message = _probe.LastMessage;
                if (!string.IsNullOrEmpty(message))
                {
                    Vector2 size = _messageStyle.CalcSize(new GUIContent(message));
                    Rect rect = new Rect((Screen.width - size.x) * 0.5f, Screen.height - 60f, size.x, size.y);

                    GUI.color = _messageColor;
                    GUI.Label(rect, message, _messageStyle);
                    GUI.color = Color.white;
                }
            }

            if (_inventory != null || _pocket != null)
            {
                // 左上角一行汇总：背包（4×4）和口袋（4格）分开显示，方便判断"东西进对了地方没"。
                // 取字段前全部做 null 判断并给出明确提示 —— 这样万一某个容器没准备好，
                // 画面上会显示"（未接入）"而不是抛 NullReferenceException。
                if (_inventoryDirty)
                {
                    string backpackText = _inventory != null
                        ? $"{_inventory.SlotCount}/{_inventory.Capacity}　{Describe(_inventory)}"
                        : "（未接入）";
                    string pocketText = _pocket != null
                        ? $"{_pocket.SlotCount}/{_pocket.Capacity}　{Describe(_pocket)}"
                        : "（未接入）";
                    _inventoryText = $"背包：{backpackText}\n口袋：{pocketText}";
                    _inventoryDirty = false;
                }

                GUI.color = Color.white;
                GUI.Label(new Rect(16f, 12f, Screen.width - 32f, 60f), _inventoryText, _messageStyle);
            }

            DrawHealthBar();
        }

        /// <summary>
        /// 画玩家血条（屏幕左下角）。
        ///
        /// 【为什么不用 uGUI】搜过现成的开源方案，它们都依赖 uGUI 的 Image，
        /// 而本工程目前完全用 OnGUI（零 prefab、零 Canvas）。
        /// 为显示一个条形引入整套 RectTransform/Canvas 体系不划算，
        /// 而且 OnGUI 用 Texture2D.whiteTexture + GUI.color 就能画矩形，够用。
        /// 以后正式做 UI 时这一段整个删掉。
        /// </summary>
        private void DrawHealthBar()
        {
            if (_health == null || _health.Health == null) return;

            float width = Mathf.Max(40f, _healthBarWidth);
            float height = Mathf.Max(6f, _healthBarHeight);

            var rect = new Rect(
                _healthBarMargin.x,
                Screen.height - _healthBarMargin.y - height,
                width, height);

            float ratio = Mathf.Clamp01(_health.Normalized);

            // 背景
            Fill(rect, _healthBackColor);

            // 前景：低血量变红。
            // 用 saturate 把 ratio 推进阈值区间，映射成 0~1 的插值系数。
            Color fillColor = Color.Lerp(
                _healthLowColor, _healthHighColor,
                Mathf.Clamp01(ratio / Mathf.Max(0.01f, _healthLowThreshold)));

            if (ratio > 0f)
            {
                Fill(new Rect(rect.x, rect.y, rect.width * ratio, rect.height), fillColor);
            }

            // 描边（四条 1 像素线，避免依赖 GUI.skin.box 的贴图）
            Border(rect, _healthBorderColor);

            // 数值文字：居中叠在血条上
            string text = $"{Mathf.CeilToInt(_health.Current)} / {Mathf.CeilToInt(_health.Max)}";
            GUI.color = Color.white;
            GUI.Label(rect, text, _healthTextStyle);
        }

        /// <summary>用白贴图 + GUI.color 画一个纯色矩形。</summary>
        private static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            // 用 Unity 内置的 whiteTexture，而不是自己 new Texture2D：
            // 自建贴图如果创建/上传失败，GUI.DrawTexture 会【静默什么都不画】（本工程踩过）。
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        /// <summary>四条 1 像素边。</summary>
        private static void Border(Rect rect, Color color)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, 1f), color);
            Fill(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            Fill(new Rect(rect.x, rect.y, 1f, rect.height), color);
            Fill(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        /// <summary>容器内容的文字描述。单独包一层是为了在它出错时不至于把整个 HUD 打挂。</summary>
        private static string Describe(Inventory inventory)
        {
            if (inventory == null) return "—";

            string described = inventory.Describe();
            return string.IsNullOrEmpty(described) ? "—" : described;
        }

        private void EnsureStyles()
        {
            if (_promptStyle != null) return;

            _promptStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = _fontSize + 6,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };

            _messageStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = _fontSize,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false
            };

            _healthTextStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = _fontSize - 3,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
        }
    }
}
