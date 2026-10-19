using Game.Gameplay.AI;
using Game.Gameplay.Building;
using UnityEngine;

namespace Game.Player
{
    /// <summary>原型死亡界面：黑屏、输入锁定和出生点复活；沿用项目现有 IMGUI。</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerHealth))]
    public sealed class PlayerDeathScreen : MonoBehaviour
    {
        [Tooltip("留空时使用本次进入 Play 的初始位置。")]
        [SerializeField] private Transform _respawnPoint;
        [SerializeField, Min(1f)] private float _respawnInvulnerability = 3f;
        private PlayerHealth _health;
        private PlayerController _movement;
        private PlayerInputSource _input;
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;
        private bool _visible;
        private bool _movementWasEnabled, _inputWasEnabled, _cursorWasVisible;
        private CursorLockMode _cursorLock;
        private GUIStyle _titleStyle, _buttonStyle, _hintStyle;
        public bool IsShowing => _visible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSessionInput() => InputModeState.SetPlayerDead(false);

        private void Awake()
        {
            _health = GetComponent<PlayerHealth>();
            _movement = GetComponent<PlayerController>();
            _input = GetComponent<PlayerInputSource>();
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
        }

        private void OnEnable()
        {
            _health.Died += Show;
            if (_health.IsDead) Show();
        }

        private void OnDisable()
        {
            if (_health != null) _health.Died -= Show;
            Hide();
        }

        private void Update()
        {
            // 正血量读档或其他正式复活入口同样能解除死亡画面。
            if (_visible && !_health.IsDead) Hide();
        }

        private void Show()
        {
            if (_visible) return;
            InputModeState.SetPlayerDead(true);
            GetComponent<InventoryScreen>()?.CloseForDeath();
            GetComponent<BuildController>()?.ForceExitBuilding();
            _cursorLock = Cursor.lockState;
            _cursorWasVisible = Cursor.visible;
            _movementWasEnabled = _movement != null && _movement.enabled;
            _inputWasEnabled = _input != null && _input.InputEnabled;
            if (_movement != null) _movement.enabled = false;
            if (_input != null) _input.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _visible = true;
            EnemyController.ForgetPlayer(transform);
        }

        private void Hide()
        {
            if (!_visible) return;
            _visible = false;
            if (_movement != null) _movement.enabled = _movementWasEnabled;
            if (_input != null) _input.InputEnabled = _inputWasEnabled;
            InputModeState.SetPlayerDead(false);
            Cursor.lockState = _cursorLock;
            Cursor.visible = _cursorWasVisible;
        }

        public bool RevivePlayer()
        {
            if (!_health.IsDead) return false;
            Vector3 position = _respawnPoint != null ? _respawnPoint.position : _spawnPosition;
            Quaternion rotation = _respawnPoint != null ? _respawnPoint.rotation : _spawnRotation;
            if (_movement != null) _movement.Teleport(position, rotation);
            else
            {
                CharacterController cc = GetComponent<CharacterController>();
                bool wasEnabled = cc != null && cc.enabled;
                if (wasEnabled) cc.enabled = false;
                transform.SetPositionAndRotation(position, rotation);
                if (wasEnabled) cc.enabled = true;
            }
            EnemyController.ForgetPlayer(transform);
            _health.Revive(_respawnInvulnerability);
            Hide();
            return true;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            int depth = GUI.depth;
            Color color = GUI.color, background = GUI.backgroundColor;
            Matrix4x4 matrix = GUI.matrix;
            bool enabled = GUI.enabled;
            try
            {
                GUI.depth = -10000;
                GUI.matrix = Matrix4x4.identity;
                GUI.enabled = true;
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.backgroundColor = Color.white;
                if (_titleStyle == null)
                {
                    _titleStyle = new GUIStyle(GUI.skin.label)
                        { alignment = TextAnchor.MiddleCenter, fontSize = 38 };
                    _titleStyle.normal.textColor = Color.white;
                    _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 24 };
                    _hintStyle = new GUIStyle(GUI.skin.label)
                        { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
                    _hintStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
                }
                float width = Mathf.Min(420f, Screen.width - 24f);
                float x = (Screen.width - width) * 0.5f, y = Screen.height * 0.5f;
                GUI.Label(new Rect(x, y - 100f, width, 60f), "你已死亡", _titleStyle);
                if (GUI.Button(new Rect(x + width * 0.15f, y, width * 0.7f, 58f), "复活", _buttonStyle))
                    RevivePlayer();
                GUI.Label(new Rect(x, y + 72f, width, 32f), "返回出生点 · 恢复全部生命值", _hintStyle);
            }
            finally
            {
                GUI.depth = depth; GUI.color = color; GUI.backgroundColor = background;
                GUI.matrix = matrix; GUI.enabled = enabled;
            }
        }
    }
}
