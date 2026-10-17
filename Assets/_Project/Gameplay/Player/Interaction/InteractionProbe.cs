using System.Collections.Generic;
using Game.World;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// 交互探测器：找出角色附近最合适的一个 IInteractable，并在收到交互输入时执行它。
    ///
    /// 职责边界：
    ///   - 它【不读键盘】，交互意图来自 ICharacterInputSource（MovementIntent.InteractPressed），
    ///     所以 AI / 联机复用同一套逻辑。
    ///   - 它【不弹 UI】，只对外暴露 CurrentTarget 和 LastMessage，由 InteractionHud 显示。
    ///
    /// 查找方式（重要）：
    ///   用 Physics.OverlapSphereNonAlloc 做【空间查询】，再从命中的 Collider 上取 IInteractable。
    ///   早先的实现是每帧 FindObjectsByType&lt;InteractableBase&gt;() 全场景扫描 ——
    ///   世界里 10 个箱子和 10000 个箱子，全扫的代价是天差地别的，而空间查询几乎不变。
    ///
    /// 选择规则（按优先级）：
    ///   1. 在 _radius 内（物理查询已经保证）
    ///   2. 在角色正前方 _maxAngle 度扇形内（避免"隔着背也能交互"）
    ///   3. 其中最近的
    /// </summary>
    [DisallowMultipleComponent]
    public class InteractionProbe : MonoBehaviour
    {
        [Header("范围")]
        [Tooltip("能交互的最大距离（米）。")]
        [SerializeField, Min(0.5f)] private float _radius = 3.0f;

        [Tooltip("交互扇形总张角（度）。180 = 只要求在前半球；360 = 不限方向。")]
        [SerializeField, Range(30f, 360f)] private float _maxAngle = 160f;

        [Tooltip("原型生成器配置为 Interactable 层；手动添加时默认全部，避免漏掉尚未分层的目标。")]
        [SerializeField] private LayerMask _interactableMask = ~0;

        [Header("引用")]
        [Tooltip("交互意图来源。留空则在本物体上找 ICharacterInputSource。")]
        [SerializeField] private MonoBehaviour _intentSourceBehaviour;

        [Tooltip("是否把 Trigger 碰撞体也算作可交互目标。可交互物用普通碰撞体时无所谓；用 Trigger 做交互范围时必须勾上。")]
        [SerializeField] private bool _useQueryTriggerInteraction = true;

        private Collider[] _hitBuffer = new Collider[64];
        private readonly HashSet<InteractableBase> _candidateSet = new HashSet<InteractableBase>();
        private readonly List<InteractableBase> _candidates = new List<InteractableBase>();

        private ICharacterInputSource _intentSource;
        private IInteractable _currentTarget;
        private string _lastMessage;
        public uint MessageVersion { get; private set; }
        public int BufferCapacity => _hitBuffer.Length;
        public LayerMask InteractableMask { get => _interactableMask; set => _interactableMask = value; }

        /// <summary>当前可交互目标，没有则为 null。</summary>
        public IInteractable CurrentTarget => _currentTarget;

        /// <summary>最近一次交互的结果文本（成功/失败都会写），供 HUD 显示。</summary>
        public string LastMessage => _lastMessage;

        /// <summary>上一帧找到的候选数量，用于排查"为什么找不到目标"。</summary>
        public int LastCandidateCount => _candidates.Count;

        private void Awake()
        {
            _intentSource = _intentSourceBehaviour as ICharacterInputSource;
            if (_intentSource == null) _intentSource = GetComponent<ICharacterInputSource>();
        }

        private void Update()
        {
            // 【输入模式屏蔽】建造模式/背包打开时不该触发 E 交互
            // （否则在建造模式里按 E 会去砍树，或者在背包里按 E 会去开门）。
            //
            // 早先这里【完全没有屏蔽】，而 Pistol 只查了背包是否打开 ——
            // 每加一个模式就要去改一遍所有响应输入的地方，漏一个就出 bug。
            // 现在统一问 InputModeState。
            if (!Game.Player.InputModeState.AllowsInteraction)
            {
                // 仍然更新"当前能交互什么"用于显示提示，但不响应按键。
                _currentTarget = FindBestTarget();
                return;
            }

            if (_intentSource != null && _intentSource.CurrentIntent.InteractPressed)
                TryInteract();
            else
                _currentTarget = FindBestTarget();
        }

        /// <summary>执行一次交互（也可以从 UI 按钮 / AI 直接调用）。</summary>
        public bool TryInteract()
        {
            MessageVersion++;
            _currentTarget = FindBestTarget();
            if (_currentTarget == null)
            {
                _lastMessage = null;
                return false;
            }

            if (!_currentTarget.CanInteract(gameObject))
            {
                _lastMessage = "现在不能交互";
                return false;
            }

            InteractionResult result = _currentTarget.Interact(gameObject);
            _lastMessage = result.Message;

            return result.Succeeded;
        }

        private IInteractable FindBestTarget()
        {
            CollectCandidates();
            if (_candidates.Count == 0) return null;

            Vector3 origin = transform.position;
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            float halfAngle = _maxAngle * 0.5f;
            float minimumDot = Mathf.Cos(halfAngle * Mathf.Deg2Rad);
            bool angleUnlimited = _maxAngle >= 359.5f;

            IInteractable best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < _candidates.Count; i++)
            {
                InteractableBase candidate = _candidates[i];
                if (candidate == null || !candidate.CanInteract(gameObject)) continue;

                Vector3 toTarget = candidate.WorldPosition - origin;
                float distance = toTarget.sqrMagnitude;
                if (distance >= bestDistance) continue;

                if (!angleUnlimited && distance > 0.0001f)
                {
                    Vector3 flat = Vector3.ProjectOnPlane(toTarget, Vector3.up);
                    if (flat.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f)
                    {
                        if (Vector3.Dot(forward, flat.normalized) < minimumDot) continue;
                    }
                }

                best = candidate;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>
        /// 用物理空间查询收集候选：只扫到附近这些 Collider，和世界总物体数无关。
        /// 缓冲饱和时扩容；容量足够后复用缓冲，不再分配查询数组。
        /// </summary>
        private void CollectCandidates()
        {
            _candidates.Clear();
            _candidateSet.Clear();

            QueryTriggerInteraction triggerMode = _useQueryTriggerInteraction
                ? QueryTriggerInteraction.Collide
                : QueryTriggerInteraction.Ignore;

            // 饱和返回值不代表完整候选集；扩容并重查，扩容后的缓冲继续复用。
            int count;
            while (true)
            {
                count = Physics.OverlapSphereNonAlloc(
                    transform.position, _radius, _hitBuffer, _interactableMask, triggerMode);
                if (count < _hitBuffer.Length) break;
                System.Array.Resize(ref _hitBuffer, _hitBuffer.Length * 2);
            }

            for (int i = 0; i < count; i++)
            {
                Collider hit = _hitBuffer[i];
                if (hit == null) continue;

                // 交互组件可能挂在父物体上（例如树：碰撞体在父物体、子物体只是外观）。
                InteractableBase interactable = hit.GetComponentInParent<InteractableBase>();
                if (interactable == null) continue;
                if (!interactable.isActiveAndEnabled) continue;

                if (_candidateSet.Add(interactable))
                {
                    _candidates.Add(interactable);
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 1f, 0.6f, 1f);
            Gizmos.DrawWireSphere(transform.position, _radius);

            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            float halfAngle = _maxAngle * 0.5f;
            Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.8f);
            Gizmos.DrawRay(transform.position, Quaternion.Euler(0f, -halfAngle, 0f) * forward * _radius);
            Gizmos.DrawRay(transform.position, Quaternion.Euler(0f, halfAngle, 0f) * forward * _radius);

            if (_currentTarget != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position, _currentTarget.WorldPosition);
            }
        }
    }
}
