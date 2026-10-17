using Game.Player;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 楼梯口的楼层切换触发器。
    ///
    /// 楼梯是**真的能走上去**的（台阶 + 斜坡碰撞体），所以不需要按 E 传送 ——
    /// 玩家走上去、踩进这个触发区，楼层显示就自动切过去。
    ///
    /// 为什么用触发器而不是"每帧比较玩家 Y 高度"：
    ///   楼梯空间里 Y 是连续变化的，用高度判断会在台阶中途反复抖动切换。
    ///   触发器只在"跨过某个平面"时触发一次，稳定得多。
    ///
    /// 用法：在楼梯上口和下口各放一个触发器，分别指定目标楼层。
    /// 例如上口一个 _targetFloor = 1，下口一个 _targetFloor = 0。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public class StairFloorTrigger : MonoBehaviour
    {
        [Tooltip("所属建筑。留空则从父物体上找 BuildingController。")]
        [SerializeField] private BuildingController _building;

        [Tooltip("玩家进入这个区域后，当前楼层切换成它。")]
        [SerializeField] private int _targetFloor;

        [Tooltip("玩家的 Tag。用于识别进入的是不是玩家。")]
        [SerializeField] private string _playerTag = "Player";

        [Tooltip("进入时在 Console 打印一次，用来确认触发器位置对不对。")]
        [SerializeField] private bool _logOnEnter = true;

        private Collider _trigger;

        public int TargetFloor => _targetFloor;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null) _trigger.isTrigger = true;

            if (_building == null) _building = GetComponentInParent<BuildingController>();

            if (_building == null)
            {
                Debug.LogError(
                    $"[{nameof(StairFloorTrigger)}]「{name}」找不到所属的 BuildingController，楼层切换不会生效。", this);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_building == null || !IsPlayer(other)) return;

            // 诊断：把玩家踩到触发区时的实际位置打出来。
            //
            // 为什么需要它："上不去楼梯"可能是卡在坡道上、卡在天花板洞口、或者根本没走到坡道 ——
            // 光看现象无法区分。把 XYZ 和当前楼层打出来，一次就能定位到卡在哪。
            if (_logOnEnter)
            {
                Vector3 p = other.transform.position;
                Debug.Log(
                    $"[楼梯] 进入「{name}」→ 切换到楼层 {_targetFloor}\n" +
                    $"       玩家位置 ({p.x:F2}, {p.y:F2}, {p.z:F2})，当前楼层 {_building.CurrentFloorIndex}",
                    this);
            }

            // 先确保"玩家在建筑内"这个状态是对的：
            // 有的布局里楼梯口可能紧贴内部触发区的边界，进来时外面那个区还没触发。
            if (!_building.IsPlayerInside) _building.OnPlayerEnter(other.gameObject);

            if (_building.CurrentFloorIndex == _targetFloor) return;

            _building.MoveToFloor(_targetFloor);
        }

        private bool IsPlayer(Collider other)
        {
            if (other == null) return false;

            // 优先按组件识别（比 Tag 可靠）。
            if (other.GetComponentInParent<PlayerController>() != null) return true;

            return !string.IsNullOrEmpty(_playerTag) && other.CompareTag(_playerTag);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0.2f, 0.35f);

            BoxCollider box = GetComponent<BoxCollider>();
            if (box != null)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawWireCube(box.center, box.size);
            }
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器用来配置楼梯触发器。</summary>
        public void ConfigureTrigger(BuildingController building, int targetFloor)
        {
            _building = building;
            _targetFloor = targetFloor;
        }
#endif
    }
}
