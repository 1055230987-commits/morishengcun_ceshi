using System.Collections;
using System.Collections.Generic;
using Game.Items;
using Game.Player;
using UnityEngine;
using Game.Rendering;

namespace Game.World
{
    /// <summary>
    /// 可采集的树木：按 E 砍，每次产出木材；砍够次数后随机倒向一侧，然后渐隐消失。
    ///
    /// 数据结构化的体现：产出什么物品完全由 Inspector 上的 ItemDefinition 决定，
    /// 代码里没有任何 "wood" 硬编码字符串。
    ///
    /// 倒下流程：
    ///   1. 最后一击 → 立刻关掉碰撞（避免撞在正在消失的树上）
    ///   2. 可选延迟（给打击感/音效留时间）
    ///   3. 倒下：随机方向，动画插值 或 刚体物理（_fallMode 切换）
    ///   4. 渐隐：把材质慢慢透明到 0（可选同时下沉一点）
    ///   5. 隐藏（默认）或销毁；提供 ResetTree() 以便以后做树木再生
    ///
    /// 刻意【不做】逐次缩小：灰盒阶段砍树只需要"能砍、有产出、倒掉"，逐次缩小反而让
    /// 最后一次的倒下看起来像"变小"而不是"倒下"。
    /// </summary>
    public class InteractableTree : InteractableBase
    {
        [Header("产出")]
        [Tooltip("砍一次产出的物品。留空则不能采集（会打警告）。")]
        [SerializeField] private ItemDefinition _yieldItem;

        [Tooltip("每次产出数量。")]
        [SerializeField, Min(1)] private int _yieldPerChop = 1;

        [Header("耐久")]
        [Tooltip("需要砍几次才倒下。")]
        [SerializeField, Min(1)] private int _chopsRequired = 3;

        [Header("倒下")]
        [Tooltip("倒下方式：\n" +
                 "动画 = 旋转插值。稳定、可复现，适合以后做存档与树木再生。\n" +
                 "物理 = 刚体 + 铰链 + 扭矩。有真实的加速与落地感，但会永久改写 Transform。")]
        [SerializeField] private FallMode _fallMode = FallMode.Animated;

        [Tooltip("砍倒后停留多久才开始倒下。给打击感/音效留时间。")]
        [SerializeField, Min(0f)] private float _fallDelay = 0.15f;

        [Tooltip("倒下动画时长（秒）。动画模式使用；物理模式只是最长等待时间的参考。")]
        [SerializeField, Min(0.05f)] private float _fallDuration = 1.0f;

        [Tooltip("最多倾倒多少度。90 = 完全躺平，45 = 斜靠。")]
        [SerializeField, Range(0f, 90f)] private float _fallTiltDegrees = 90f;

        [Tooltip("倒向哪一侧：勾上 = 每次随机，取消 = 固定用 _fixedFallYaw。")]
        [SerializeField] private bool _randomFallDirection = true;

        [Tooltip("固定倒向的方位角（度）。_randomFallDirection 勾上时忽略。")]
        [SerializeField, Range(0f, 360f)] private float _fixedFallYaw = 0f;

        [Header("物理模式参数（_fallMode = 物理 时生效）")]
        [Tooltip("施加的倾倒扭矩大小。太小倒不下去，太大像被甩飞。")]
        [SerializeField, Min(0f)] private float _physicsTorque = 260f;

        [SerializeField, Min(0.1f)] private float _physicsMass = 120f;

        [Tooltip("最多等多久认定为「已停稳」。超时后停止跟踪，避免物理一直跑着耗性能。")]
        [SerializeField, Min(0.5f)] private float _physicsSettleTimeout = 5f;

        [Header("渐隐消失")]
        [Tooltip("倒下后渐隐消失的时长（秒）。0 = 不渐隐，直接消失。")]
        [SerializeField, Min(0f)] private float _fadeDuration = 0.9f;

        [Tooltip("渐隐时向下沉降的距离（米）。让「倒下的树沉入地面」更自然。0 = 不沉降。")]
        [SerializeField, Min(0f)] private float _sinkDepth = 0.35f;

        [Tooltip("消失方式：勾上 = 隐藏对象（保留在场景里，以后做树木再生不用重建）；取消 = 彻底销毁。")]
        [SerializeField] private bool _deactivateInsteadOfDestroy = true;

        // 树干尺寸：会被事件参数带出去，供特效挂接脚本使用（烟雾沿树干分布之类）。
        [Header("树干尺寸（供特效挂接脚本读取）")]
        [Tooltip("树干高度。生成器会按实际树干填入。")]
        [SerializeField, Min(0.5f)] private float _trunkHeight = 3.4f;

        [Tooltip("树干横向半径。")]
        [SerializeField, Min(0.02f)] private float _trunkRadius = 0.4f;

        /// <summary>
        /// 开始倒下时触发（参数：树干根部的世界坐标）。
        /// 特效一般挂这个事件：此时位置还没被旋转改变，取到的正是树根位置。
        /// </summary>
        public event System.Action<Vector3> OnFellStarted;

        /// <summary>彻底消失前触发（参数：树干根部的世界坐标）。适合"最后扬尘"之类。</summary>
        public event System.Action<Vector3> OnFellFinished;

        /// <summary>树干高度（特效挂接脚本用）。</summary>
        public float TrunkHeight => _trunkHeight;

        /// <summary>树干半径（特效挂接脚本用）。</summary>
        public float TrunkRadius => _trunkRadius;

        /// <summary>树的倒下方式。</summary>
        public enum FallMode
        {
            /// <summary>旋转插值。Transform 变化完全可控。</summary>
            Animated = 0,

            /// <summary>刚体 + 铰链 + 扭矩。观感真实，但会永久改写 Transform。</summary>
            Physics = 1
        }

        private int _chopsDone;
        private int _remainingChopYield;
        private Vector3 _initialPosition;
        private Vector3 _initialScale;
        private Quaternion _initialRotation;
        private Collider[] _colliders;
        private Renderer[] _renderers;
        private RendererFadeMaterials _fadeMaterials;
        private Coroutine _fallRoutine;
        private Rigidbody _rigidbody;
        private bool _renderersHidden;

        public bool IsFelled => _chopsDone >= _chopsRequired;

        public int ChopsRemaining => Mathf.Max(0, _chopsRequired - _chopsDone);

        public override string InteractionPrompt
        {
            get
            {
                if (_yieldItem == null) return $"{name}（未配置产出）";
                if (IsFelled) return $"{_yieldItem.DisplayName}已采完";

                return $"砍伐 {name}（剩 {ChopsRemaining} 次）";
            }
        }

        protected virtual void Awake()
        {
            _initialPosition = transform.localPosition;
            _initialScale = transform.localScale;
            _initialRotation = transform.localRotation;
            _colliders = GetComponentsInChildren<Collider>();
            _renderers = GetComponentsInChildren<Renderer>();

            if (_yieldItem == null)
            {
                Debug.LogWarning(
                    $"[{nameof(InteractableTree)}]「{name}」没有配置产出物品，砍了不会给东西。" +
                    "请把 ItemDefinition 拖到 _yieldItem 上（或用菜单「游戏 → 物品 → 创建木材物品」）。", this);
            }
        }

        public override bool CanInteract(GameObject actor)
        {
            if (!base.CanInteract(actor)) return false;
            if (IsFelled) return false;
            if (_yieldItem == null) return false;

            return true;
        }

        protected override InteractionResult OnInteract(GameObject actor)
        {
            Inventory inventory = PlayerInventory.GetInventory(actor);
            if (inventory == null)
            {
                return InteractionResult.Failed("这个角色没有背包");
            }

            if (_remainingChopYield <= 0) _remainingChopYield = _yieldPerChop;
            int added = inventory.Add(_yieldItem, _remainingChopYield);
            if (added <= 0)
            {
                return InteractionResult.Failed("背包满了");
            }

            _remainingChopYield -= added;
            if (_remainingChopYield > 0)
                return InteractionResult.Success($"+{_yieldItem.DisplayName} ×{added}（还有 {_remainingChopYield} 未领取）");

            _chopsDone++;

            if (IsFelled)
            {
                BeginFell();
                return InteractionResult.Success($"{name} 已砍倒，获得 {_yieldItem.DisplayName} ×{added}");
            }

            return InteractionResult.Success($"+{_yieldItem.DisplayName} ×{added}（还需 {ChopsRemaining} 次）");
        }

        // ------------------------------------------------------------------ 倒下流程

        private void BeginFell()
        {
            // 先关碰撞，避免玩家撞在正在消失的树上。
            SetCollidersEnabled(false);
            Debug.Log($"[采集] {name} 已被砍倒。");

            if (_fallRoutine != null) StopCoroutine(_fallRoutine);
            _fallRoutine = StartCoroutine(FellRoutine());
        }

        private IEnumerator FellRoutine()
        {
            if (_fallDelay > 0f)
            {
                yield return new WaitForSeconds(_fallDelay);
            }

            // 随机方向：绕"随机方位角"的轴倾倒，所以能倒向任意一侧。
            float fallYaw = _randomFallDirection ? Random.Range(0f, 360f) : _fixedFallYaw;

            // 事件参数必须用【倒下之前】的世界位置：树一旦开始倾倒，
            // 根节点的旋转会改变 transform.position，那时取到的位置会偏离地面。
            Vector3 eventPosition = transform.position;
            OnFellStarted?.Invoke(eventPosition);

            if (_fallMode == FallMode.Physics && TryStartPhysicsFall(fallYaw))
            {
                yield return PhysicsFallRoutine();
            }
            else
            {
                yield return AnimatedFallRoutine(fallYaw);
            }

            if (_fadeDuration > 0f)
            {
                yield return FadeOutRoutine();
            }

            OnFellFinished?.Invoke(eventPosition);

            _fallRoutine = null;
            FinishFell();
        }

        /// <summary>动画模式：绕随机轴倾倒，用 t² 缓入（开始慢、后面突然倒下去）。</summary>
        private IEnumerator AnimatedFallRoutine(float fallYaw)
        {
            float elapsed = 0f;
            while (elapsed < _fallDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / _fallDuration);

                if (_fallTiltDegrees > 0f)
                {
                    transform.localRotation =
                        Quaternion.AngleAxis(fallYaw, Vector3.up) *
                        Quaternion.AngleAxis(_fallTiltDegrees * t * t, Vector3.right) *
                        _initialRotation;
                }

                yield return null;
            }

            if (_fallTiltDegrees > 0f)
            {
                transform.localRotation =
                    Quaternion.AngleAxis(fallYaw, Vector3.up) *
                    Quaternion.AngleAxis(_fallTiltDegrees, Vector3.right) *
                    _initialRotation;
            }
        }

        /// <summary>把树变成刚体并施加倾倒扭矩。失败时返回 false，由调用方退回动画模式。</summary>
        private bool TryStartPhysicsFall(float fallYaw)
        {
            _rigidbody = GetComponent<Rigidbody>();
            if (_rigidbody == null) _rigidbody = gameObject.AddComponent<Rigidbody>();

            _rigidbody.isKinematic = false;
            _rigidbody.mass = Mathf.Max(0.1f, _physicsMass);
            _rigidbody.useGravity = true;
            _rigidbody.linearDamping = 0.35f;
            _rigidbody.angularDamping = 0.5f;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

            // 让树绕【根部】转，而不是绕树干中点转 —— 否则树会像平移一样"飘"倒。
            _rigidbody.centerOfMass = new Vector3(0f, 0.25f, 0f);

            // 铰链在树根，把运动限制成"原地倒下"，物理结果更可控。
            HingeJoint hinge = GetComponent<HingeJoint>();
            if (hinge == null) hinge = gameObject.AddComponent<HingeJoint>();

            hinge.axis = Quaternion.AngleAxis(fallYaw, Vector3.up) * Vector3.right;
            hinge.autoConfigureConnectedAnchor = true;
            hinge.useLimits = true;
            hinge.limits = new JointLimits { min = 0f, max = Mathf.Clamp(_fallTiltDegrees + 2f, 0f, 92f) };

            Vector3 torqueAxis = Quaternion.AngleAxis(fallYaw, Vector3.up) * Vector3.forward;
            _rigidbody.AddTorque(torqueAxis * Mathf.Max(0f, _physicsTorque), ForceMode.Impulse);

            Debug.Log($"[采集] {name} 使用物理倒下（方向 {fallYaw:F0}°）。");
            return true;
        }

        /// <summary>等物理停稳，然后冻结它（避免倒下后还被推来推去、一直耗物理）。</summary>
        private IEnumerator PhysicsFallRoutine()
        {
            if (_rigidbody == null) yield break;

            float timeout = Mathf.Max(0.5f, _physicsSettleTimeout);
            float elapsed = 0f;
            float stillTime = 0f;

            while (elapsed < timeout)
            {
                elapsed += Time.deltaTime;

                // 连续 0.4 秒都很慢就认为停稳了。
                if (_rigidbody.linearVelocity.sqrMagnitude < 0.04f &&
                    _rigidbody.angularVelocity.sqrMagnitude < 0.04f)
                {
                    stillTime += Time.deltaTime;
                    if (stillTime >= 0.4f) break;
                }
                else
                {
                    stillTime = 0f;
                }

                yield return null;
            }

            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
            _rigidbody.isKinematic = true;
        }

        // ------------------------------------------------------------------ 渐隐

        /// <summary>
        /// 渐隐：把材质透明到 0，可选同时向下沉降。
        ///
        /// 使用显式拥有的材质副本做透明，复位时恢复原材质，销毁时释放副本，
        /// 改它不会污染项目里的共享材质资产（灰盒的 M_Grey_Prop 是共享的，直接改会全场景变透明）。
        /// </summary>
        private IEnumerator FadeOutRoutine()
        {
            CacheFadeMaterials();
            if (_fadeMaterials == null)
            {
                yield break;
            }

            Vector3 startPosition = transform.position;
            Vector3 endPosition = startPosition + Vector3.down * Mathf.Max(0f, _sinkDepth);

            float elapsed = 0f;
            while (elapsed < _fadeDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / _fadeDuration);

                ApplyAlpha(1f - t);
                if (_sinkDepth > 0f) transform.position = Vector3.Lerp(startPosition, endPosition, t);

                yield return null;
            }

            ApplyAlpha(0f);
            HideRenderers();
        }

        private void CacheFadeMaterials()
        {
            if (_fadeMaterials == null && _renderers != null)
                _fadeMaterials = new RendererFadeMaterials(_renderers);
        }

        private void ApplyAlpha(float alpha) => _fadeMaterials?.Apply(alpha);

        private void OnDestroy() => _fadeMaterials?.Dispose();
        private void HideRenderers()
        {
            if (_renderersHidden || _renderers == null) return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] != null) _renderers[i].enabled = false;
            }

            _renderersHidden = true;
        }

        // ------------------------------------------------------------------ 结束与复位

        private void FinishFell()
        {
            if (_deactivateInsteadOfDestroy)
            {
                // 默认只隐藏：对象留在场景里，以后做"树木再生"时不用重建，也能被存档引用。
                gameObject.SetActive(false);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 复位成一棵完好的树（供以后的"树木再生"使用）。
        /// 之所以现在就写，是因为倒下的树被物理/动画改过 Transform，
        /// 没有这个方法的话，以后做再生时会很难查出"为什么树是歪的"。
        /// </summary>
        public void ResetTree()
        {
            if (_fallRoutine != null)
            {
                StopCoroutine(_fallRoutine);
                _fallRoutine = null;
            }

            gameObject.SetActive(true);

            transform.localScale = _initialScale;
            transform.localRotation = _initialRotation;
            _chopsDone = 0;
            _remainingChopYield = 0;
            transform.localPosition = _initialPosition;

            if (_rigidbody != null)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
                _rigidbody.isKinematic = true;
            }

            _fadeMaterials?.Restore();

            if (_renderers != null)
            {
                for (int i = 0; i < _renderers.Length; i++)
                {
                    if (_renderers[i] != null) _renderers[i].enabled = true;
                }
            }

            _renderersHidden = false;
            SetCollidersEnabled(true);

            Debug.Log($"[采集] {name} 已复位（可再次采集）。");
        }

        private void SetCollidersEnabled(bool enabled)
        {
            if (_colliders == null) return;

            for (int i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null) _colliders[i].enabled = enabled;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器专用：给场景生成器配置产出（保持字段私有，不对外暴露 setter）。
        /// 运行时不需要这个方法，所以用 #if UNITY_EDITOR 排除掉。
        /// </summary>
        public void ConfigureYield(ItemDefinition item, int amountPerChop, int chopsRequired)
        {
            _yieldItem = item;
            _yieldPerChop = Mathf.Max(1, amountPerChop);
            _chopsRequired = Mathf.Max(1, chopsRequired);
        }

        /// <summary>编辑器专用：由场景生成器填入树干尺寸（供特效挂接脚本读取）。</summary>
        public void ConfigureTrunkSize(float trunkHeight, float trunkRadius)
        {
            _trunkHeight = Mathf.Max(0.5f, trunkHeight);
            _trunkRadius = Mathf.Max(0.02f, trunkRadius);
        }
#endif

        protected override void OnDrawGizmosSelected()
        {
            base.OnDrawGizmosSelected();
            Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.6f);
            Gizmos.DrawWireCube(WorldPosition + Vector3.up * 1.5f, new Vector3(1.5f, 3f, 1.5f));
        }
    }
}
