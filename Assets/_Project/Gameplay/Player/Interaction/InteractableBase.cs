using Game.Save;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 所有可交互物的基类。
    ///
    /// 为什么需要它（而不是让每个类直接实现 IInteractable）：
    ///   交互探测器用 Physics.OverlapSphere 拿到附近 Collider 后，需要判断"这个物体是不是可交互物"。
    ///   有了基类，一次 GetComponentInParent&lt;InteractableBase&gt;() 就能判定，既快又不会漏。
    ///   如果只有接口，就得逐个试探接口实现，慢且容易出错。
    ///
    /// 子类通常只需要覆写 OnInteract。
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class InteractableBase : MonoBehaviour, IInteractable
    {
        [Header("交互")]
        [Tooltip("交互判定点相对物体的偏移。树应该用树根附近，箱子用箱子中心。")]
        [SerializeField] private Vector3 _interactionPointOffset = new Vector3(0f, 0.5f, 0f);

        [Tooltip("交互提示里显示的名字。留空则用物体名字。")]
        [SerializeField] private string _displayName = "";

        private WorldObjectId _objectId;

        /// <summary>稳定 ID。优先用 WorldObjectId 组件（存档/联机依赖它），没有就退回物体名。</summary>
        public string ObjectId
        {
            get
            {
                if (_objectId == null) _objectId = GetComponent<WorldObjectId>();
                if (_objectId != null && _objectId.HasId) return _objectId.ObjectId;

                return name;
            }
        }

        public Vector3 WorldPosition => transform.position + _interactionPointOffset;

        /// <summary>提示文本。默认是「名字」，子类一般覆写成「砍伐」「打开」这类动词 + 名字。</summary>
        public virtual string InteractionPrompt
        {
            get
            {
                string label = string.IsNullOrEmpty(_displayName) ? name : _displayName;
                return label;
            }
        }

        /// <summary>默认任何 actor 都能交互。子类按需覆写（例如"需要钥匙"）。</summary>
        public virtual bool CanInteract(GameObject actor)
        {
            return isActiveAndEnabled;
        }

        /// <summary>执行交互。默认成功；子类覆写 OnInteract 实现具体行为。</summary>
        public InteractionResult Interact(GameObject actor)
        {
            if (!CanInteract(actor))
            {
                return InteractionResult.Failed("现在不能交互");
            }

            return OnInteract(actor);
        }

        /// <summary>子类在这里实现真正的交互逻辑。</summary>
        protected abstract InteractionResult OnInteract(GameObject actor);

        protected virtual void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 1f);
            Gizmos.DrawWireSphere(WorldPosition, 0.2f);
        }
    }
}
