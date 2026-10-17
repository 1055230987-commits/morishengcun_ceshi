using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player
{
    /// <summary>
    /// 本地玩家输入源：把 Input System 的动作读成 MovementIntent。
    /// 这是整个工程里**唯一**直接接触键盘/手柄的地方。
    ///
    /// 关于动作绑定（两条路，任选其一，不需要改代码）：
    ///   1) 留空 = 自动使用 Project Settings > Input System > Default Actions Asset
    ///      （本工程即 Assets/InputSystem_Actions.inputactions 里的 Player/Move、Player/Look、Player/Interact）。
    ///   2) 在 Inspector 里手动拖入 InputActionReference 覆盖，适合做按键重映射或分区输入。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerInputSource : MonoBehaviour, ICharacterInputSource
    {
        [Header("可选覆盖（留空则用 Project-wide 默认动作）")]
        [SerializeField] private InputActionReference _moveAction;
        [SerializeField] private InputActionReference _lookAction;
        [SerializeField] private InputActionReference _interactAction;
        [SerializeField] private InputActionReference _inventoryAction;
        [SerializeField] private InputActionReference _attackAction;

        [Header("参数")]
        [Tooltip("是否上报鼠标指针位置，供以后瞄准/鼠标交互使用。")]
        [SerializeField] private bool _reportPointerPosition = true;

        private MovementIntent _intent;
        private InputAction _resolvedMove;
        private InputAction _resolvedLook;
        private InputAction _resolvedInteract;
        private InputAction _resolvedInventory;
        private InputAction _resolvedAttack;
        private bool _actionsResolved;
        private bool _inputEnabled = true;

        public MovementIntent CurrentIntent => _intent;

        /// <summary>
        /// 本帧是否按下了"打开/关闭背包"。
        /// 单独暴露而不是塞进 MovementIntent：它不是"移动意图"，而是 UI 开关，
        /// 混进移动结构会让以后 AI 驱动的角色莫名其妙地带着一个 UI 标志。
        /// </summary>
        public bool InventoryTogglePressed { get; private set; }

        /// <summary>
        /// 本帧是否按下了"开火/攻击"，以及是否正按着（用于自动武器）。
        ///
        /// 【为什么也暴露 Held】手枪是单发（只看 Pressed），但以后做自动步枪时
        /// 需要"按住就连发"。两个都提供，武器自己决定用哪个，不用改输入层。
        ///
        /// 【为什么和 InventoryTogglePressed 一样单独暴露、不塞进 MovementIntent】
        /// 它不是"移动意图"。混进去会让以后 AI 驱动的角色莫名带着一个开火标志。
        /// </summary>
        public bool AttackPressed { get; private set; }

        /// <summary>本帧是否持续按着开火键。</summary>
        public bool AttackHeld { get; private set; }

        public bool InputEnabled
        {
            get => _inputEnabled;
            set
            {
                _inputEnabled = value;
                if (!value) _intent = MovementIntent.None;
            }
        }

        private void OnEnable()
        {
            ResolveActions();
            _intent = MovementIntent.None;
            InventoryTogglePressed = false;
            AttackPressed = false;
            AttackHeld = false;
        }

        private void OnDisable()
        {
            _intent = MovementIntent.None;
            InventoryTogglePressed = false;
            AttackPressed = false;
            AttackHeld = false;
        }

        private void Update()
        {
            if (!_inputEnabled)
            {
                InventoryTogglePressed = false;
                AttackPressed = false;
                AttackHeld = false;
                return;
            }

            ResolveActions();

            Vector2 move2D = _resolvedMove != null ? _resolvedMove.ReadValue<Vector2>() : Vector2.zero;

            // 屏幕方向 -> 世界方向。以后接不同视角的摄像机，只改这里的参考系即可。
            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;
            if (_movementReference != null && _movementReference)
            {
                forward = Vector3.ProjectOnPlane(_movementReference.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
                right = Vector3.Cross(Vector3.up, forward).normalized;
            }

            Vector3 direction = forward * move2D.y + right * move2D.x;
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            MovementIntent intent = default;
            intent.Direction = direction;
            // 摇杆推杆量既是方向也是强度；键盘必然是 1，所以这里不会误伤键盘手感。
            intent.SpeedMultiplier = Mathf.Clamp01(move2D.magnitude);
            intent.InteractPressed = _resolvedInteract != null && _resolvedInteract.WasPressedThisFrame();
            InventoryTogglePressed = _resolvedInventory != null && _resolvedInventory.WasPressedThisFrame();

            AttackPressed = _resolvedAttack != null && _resolvedAttack.WasPressedThisFrame();
            AttackHeld = _resolvedAttack != null && _resolvedAttack.IsPressed();

            if (_reportPointerPosition && Mouse.current != null)
            {
                intent.HasAimScreenPosition = true;
                intent.AimScreenPosition = Mouse.current.position.ReadValue();
            }

            _intent = intent;
        }

        /// <summary>移动方向的参考系。由 CameraRig 在运行时注入，实现「按屏幕方向移动」。</summary>
        private Transform _movementReference;

        public void SetMovementReference(Transform reference)
        {
            _movementReference = reference;
        }

        /// <summary>供相机装置注入：本帧右摇杆 / 鼠标位移。</summary>
        public Vector2 ReadLookDelta()
        {
            ResolveActions();
            if (_resolvedLook == null) return Vector2.zero;
            return _resolvedLook.ReadValue<Vector2>();
        }

        private void ResolveActions()
        {
            if (_actionsResolved) return;

            _resolvedMove = Resolve(_moveAction, "Player/Move");
            _resolvedLook = Resolve(_lookAction, "Player/Look");
            _resolvedInteract = Resolve(_interactAction, "Player/Interact");
            _resolvedInventory = Resolve(_inventoryAction, "Player/Inventory");
            _resolvedAttack = Resolve(_attackAction, "Player/Attack");

            _actionsResolved = _resolvedMove != null;
        }

        private InputAction Resolve(InputActionReference reference, string fallbackPath)
        {
            if (reference != null && reference.action != null)
            {
                return reference.action;
            }

            InputAction fallback = FindDefaultAction(fallbackPath);
            if (fallback == null)
            {
                Debug.LogWarning(
                    $"[{nameof(PlayerInputSource)}] 找不到动作 '{fallbackPath}'。" +
                    "请在 Project Settings > Input System 指定 Default Actions Asset，" +
                    "或在本组件 Inspector 里手动指定 InputActionReference。", this);
            }

            return fallback;
        }

        /// <summary>
        /// 只使用 Project-wide 动作；不自己造动作、不改动共享动作的 Map，
        /// 避免与别的系统互相干扰（也避免运行时 new 出来的动作被错误 Dispose）。
        /// </summary>
        private static InputAction FindDefaultAction(string actionPath)
        {
            InputActionAsset actions = InputSystem.actions;
            if (actions == null) return null;

            try
            {
                return actions.FindAction(actionPath, false);
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
