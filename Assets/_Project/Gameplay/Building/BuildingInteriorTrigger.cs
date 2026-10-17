using Game.Player;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 建筑内部的触发区。
    ///
    /// 作用：判断玩家是不是"真的进到建筑里了"。
    /// 用一个覆盖建筑内部的立方体触发器，而不是去判断 Y 高度 ——
    /// 触发器能自动处理"从门进、从窗户进、从楼梯井掉下来"这些情况，
    /// 而且不用每帧算坐标，也更符合"进入一个空间"这个语义。
    ///
    /// 用法：把本组件挂在带 BoxCollider（Is Trigger = true）的物体上，
    /// 触发器的范围要覆盖建筑内部的可站人区域（含所有楼层）。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public class BuildingInteriorTrigger : MonoBehaviour
    {
        [Tooltip("所属建筑。留空则从父物体上找 BuildingController。")]
        [SerializeField] private BuildingController _building;

        [Tooltip("玩家的 Tag。用它来识别进入的是不是玩家。")]
        [SerializeField] private string _playerTag = "Player";

        private Collider _trigger;

        /// <summary>所属建筑。</summary>
        public BuildingController Building => _building;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null) _trigger.isTrigger = true;

            if (_building == null) _building = GetComponentInParent<BuildingController>();

            if (_building == null)
            {
                Debug.LogError(
                    $"[{nameof(BuildingInteriorTrigger)}]「{name}」找不到所属的 BuildingController，" +
                    "进入检测不会生效。请把它挂到建筑节点下，或在 Inspector 里手动指定。", this);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_building == null || !IsPlayer(other)) return;

            _building.OnPlayerEnter(other.gameObject);
        }

        private void OnTriggerExit(Collider other)
        {
            if (_building == null || !IsPlayer(other)) return;

            _building.OnPlayerExit(other.gameObject);
        }

        private bool IsPlayer(Collider other)
        {
            if (other == null) return false;

            // 优先按组件识别（比 Tag 可靠：Tag 可以被误设，也可能多处重名）。
            if (other.GetComponentInParent<PlayerController>() != null) return true;

            // 退路：按 Tag。
            return !string.IsNullOrEmpty(_playerTag) && other.CompareTag(_playerTag);
        }
    }
}
