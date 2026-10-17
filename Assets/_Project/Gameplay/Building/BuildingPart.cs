using System.Collections.Generic;
using UnityEngine;

namespace Game.World
{
    /// <summary>建筑部件的种类。以后从 Unity 导入的模型也按这个分类挂接。</summary>
    public enum BuildingPartType
    {
        /// <summary>地板（可站立的平面）。</summary>
        Floor = 0,

        /// <summary>墙（阻挡）。</summary>
        Wall = 1,

        /// <summary>门（墙上留出的通道）。</summary>
        Door = 2,

        /// <summary>屋顶（遮挡，玩家进入后隐藏）。</summary>
        Roof = 3,

        /// <summary>楼梯（楼层切换）。</summary>
        Stair = 4
    }

    /// <summary>
    /// 单个建筑部件。挂在每个 1×1 标准部件上。
    ///
    /// 【设计意图】以后从 Unity 导入的可建造房屋模型，只要在上面挂一个本组件、
    /// 填好格子坐标和类型，就能被建筑系统接管（显示切换、重叠校验、存档）。
    /// 代码生成的标准部件也挂它，所以两条路走的是同一套数据结构 ——
    /// 这也是"以后换成美术模型时不用改逻辑"的前提。
    ///
    /// 【为什么用格子坐标而不是世界坐标】格子坐标是逻辑位置（X, Z, Floor），
    /// 和实际 Y 高度解耦。这样以后做地下室、错层、多地图都不会出问题。
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingPart : MonoBehaviour
    {
        [Header("格子坐标（逻辑位置，不是世界坐标）")]
        [Tooltip("占用格子的 X 序号。")]
        [SerializeField] private int _gridX;

        [Tooltip("占用格子的 Z 序号。")]
        [SerializeField] private int _gridZ;

        [Tooltip("所在楼层。-1 = 地下室，0 = 一楼，1 = 二楼……")]
        [SerializeField] private int _floorIndex;

        [Header("类型")]
        [SerializeField] private BuildingPartType _partType = BuildingPartType.Wall;

        /// <summary>本部件所属的建筑 ID（存档/多建筑用）。</summary>
        [SerializeField] private string _buildingId;

        public int GridX => _gridX;
        public int GridZ => _gridZ;
        public int FloorIndex => _floorIndex;
        public BuildingPartType PartType => _partType;
        public string BuildingId => _buildingId;

        /// <summary>逻辑格坐标（X, Z, Floor）。以后建造系统/存档都用它，不用世界坐标。</summary>
        public Vector3Int GridPosition => new Vector3Int(_gridX, _gridZ, _floorIndex);

        /// <summary>
        /// 是否是"地面"部件（地板或屋顶）。地面部件占据整格，
        /// 墙/门只占格子的边界 —— 这个区别在重叠校验时会用到。
        /// </summary>
        public bool IsSurfacePart => _partType == BuildingPartType.Floor || _partType == BuildingPartType.Roof;

        /// <summary>设置格坐标与类型。生成器/建造系统用。</summary>
        public void Configure(int gridX, int gridZ, int floorIndex, BuildingPartType partType, string buildingId = null)
        {
            _gridX = gridX;
            _gridZ = gridZ;
            _floorIndex = floorIndex;
            _partType = partType;
            if (!string.IsNullOrEmpty(buildingId)) _buildingId = buildingId;
        }

        /// <summary>
        /// 是否是"空"格子。生成器用 '.' 表示空，
        /// 这样重叠校验可以直接问"这个格子已经被占了吗"。
        /// </summary>
        public const char EmptyCell = '.';

        /// <summary>把 ASCII 里的字符翻译成部件类型。返回 null 表示"空格子"。</summary>
        public static BuildingPartType? FromChar(char c)
        {
            switch (c)
            {
                case '#': return BuildingPartType.Wall;
                case 'o': return BuildingPartType.Floor;
                case 'D': return BuildingPartType.Door;
                case 'R': return BuildingPartType.Roof;
                case 'S': return BuildingPartType.Stair;
                default: return null;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = _partType switch
            {
                BuildingPartType.Wall => new Color(1f, 0.4f, 0.2f, 0.6f),
                BuildingPartType.Floor => new Color(0.3f, 0.8f, 1f, 0.6f),
                BuildingPartType.Roof => new Color(1f, 0.9f, 0.3f, 0.6f),
                BuildingPartType.Door => new Color(0.4f, 1f, 0.4f, 0.6f),
                BuildingPartType.Stair => new Color(0.8f, 0.5f, 1f, 0.6f),
                _ => Color.gray
            };

            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.5f, Vector3.one);
        }
    }
}
