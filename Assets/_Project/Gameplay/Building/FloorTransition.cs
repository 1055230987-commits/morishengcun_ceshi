using Game.Player;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 按键式楼层切换点（可选组件，目前场景生成器没有使用）。
    ///
    /// 【和 StairFloorTrigger 的分工】
    ///   楼梯用 StairFloorTrigger：楼梯是真能走上去的，踩进上下口的触发区就自动切楼层，不需要按键。
    ///   本组件用于【不能走】的切换方式：梯子、传送门、电梯这类"按 E 才切"的场合。
    ///
    /// 复用现有的 IInteractable 体系：玩家走到切换点旁按 E 就能上下楼，不需要另做一套输入。
    ///
    /// 【关于落点】_landingPoint 是"切到目标楼层后把玩家放在哪里"。
    /// 留空时会退化成"保持 X/Z 不变、Y 对齐目标楼层" —— 能用，但可能卡在墙里，
    /// 所以正式搭场景时请显式指定落点。
    /// </summary>
    [DisallowMultipleComponent]
    public class FloorTransition : InteractableBase
    {
        [Header("切换目标")]
        [Tooltip("所属建筑。留空则从父物体上找 BuildingController。")]
        [SerializeField] private BuildingController _building;

        [Tooltip("按 E 之后切到哪一层。0 = 一楼、1 = 二楼、-1 = 地下室。\n" +
                 "楼梯两侧各放一个组件的话，两边都填对方楼层即可。")]
        [SerializeField] private int _destinationFloor;

        [Header("落点")]
        [Tooltip("切到目标楼层后玩家的位置。建议每层楼梯口各放一个空物体。\n" +
                 "留空 = 保持 X/Z 不变、Y 对齐目标楼层高度。")]
        [SerializeField] private Transform _landingPoint;

        [Tooltip("落点为空时的 Y 偏移（避免生成在楼板里面）。")]
        [SerializeField] private float _fallbackYOffset = 0.1f;

        [Header("提示文案")]
        [Tooltip("提示前缀。实际显示为「前缀 + 目标楼层名」，例如「上二楼」。")]
        [SerializeField] private string _promptPrefix = "前往";

        public int DestinationFloor => _destinationFloor;

        public override string InteractionPrompt
        {
            get
            {
                BuildingFloor floor = ResolveBuilding() != null ? ResolveBuilding().GetFloor(_destinationFloor) : null;
                string floorName = floor != null ? floor.DisplayName : $"楼层 {_destinationFloor}";
                return $"{_promptPrefix} {floorName}";
            }
        }

        private void Awake()
        {
            ResolveBuilding();
        }

        private BuildingController ResolveBuilding()
        {
            if (_building == null) _building = GetComponentInParent<BuildingController>();
            return _building;
        }

        public override bool CanInteract(GameObject actor)
        {
            if (!base.CanInteract(actor)) return false;

            BuildingController building = ResolveBuilding();
            if (building == null) return false;

            // 目标楼层必须真实存在，否则按了也没反应，不如直接不给交互。
            return building.GetFloor(_destinationFloor) != null;
        }

        protected override InteractionResult OnInteract(GameObject actor)
        {
            BuildingController building = ResolveBuilding();
            if (building == null)
            {
                return InteractionResult.Failed("这个楼梯没有关联建筑");
            }

            BuildingFloor targetFloor = building.GetFloor(_destinationFloor);
            if (targetFloor == null)
            {
                return InteractionResult.Failed($"建筑里没有楼层 {_destinationFloor}");
            }

            if (actor == null) return InteractionResult.Failed("没有找到玩家");

            // 先让建筑切状态，再把玩家挪过去。
            // 顺序很重要：先切楼层可以让隐藏楼层的碰撞体先关掉，
            // 否则玩家可能被"即将隐藏的那一层的墙"挡住，传送后卡在里面。
            if (!building.MoveToFloor(_destinationFloor))
            {
                return InteractionResult.Failed("楼层切换失败");
            }

            TeleportActor(actor, targetFloor);

            return InteractionResult.Success($"已到{targetFloor.DisplayName}");
        }

        private void TeleportActor(GameObject actor, BuildingFloor targetFloor)
        {
            PlayerController controller = actor.GetComponent<PlayerController>();

            Vector3 position;
            Quaternion rotation;

            if (_landingPoint != null)
            {
                position = _landingPoint.position;
                rotation = _landingPoint.rotation;
            }
            else
            {
                // 退路：只调整 Y。会保留玩家当前的 X/Z，通常就是楼梯口，够用。
                position = actor.transform.position;
                position.y = targetFloor.transform.position.y + _fallbackYOffset;
                rotation = actor.transform.rotation;
            }

            if (controller != null)
            {
                controller.Teleport(position, rotation);
            }
            else
            {
                // 没有 PlayerController（例如以后给 AI 用）时退化成直接改 Transform。
                actor.transform.SetPositionAndRotation(position, rotation);
            }
        }

        protected override void OnDrawGizmosSelected()
        {
            base.OnDrawGizmosSelected();

            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
            if (_landingPoint != null)
            {
                Gizmos.DrawWireSphere(_landingPoint.position, 0.3f);
                Gizmos.DrawLine(WorldPosition, _landingPoint.position);
            }
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器用来配置楼梯。</summary>
        public void ConfigureTransition(BuildingController building, int destinationFloor, Transform landingPoint, string promptPrefix = "前往")
        {
            _building = building;
            _destinationFloor = destinationFloor;
            _landingPoint = landingPoint;
            _promptPrefix = promptPrefix;
        }
#endif
    }
}
