using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 地图区域定义（数据）。
    ///
    /// 为什么现在就用 ScriptableObject 而不是在场景里写死一个 Box：
    ///   - 以后家园 / 野外 / 聚居地是不同地图，各自的尺寸和边界不该散落在场景对象上；
    ///   - 边界属于「数据」，存档时只需存 MapId，不需要保存场景对象本身。
    ///
    /// 坐标约定：X/Z 为水平面，Y 为高度；地板层数用 FloorCount 预留（当前恒为 1 层，即 Floor 0）。
    /// 注意：Floor 与 Y 是两件事 —— 同样的 X/Z 在不同 Floor 上是不同位置，
    /// 所以以后做多层建筑时，位置应使用 (X, Z, Floor) 三元组，Y 只表示该层内的局部高度。
    /// </summary>
    [CreateAssetMenu(fileName = "Area_", menuName = "Game/World/Map Area Definition", order = 0)]
    public class MapAreaDefinition : ScriptableObject
    {
        [Header("标识")]
        [Tooltip("稳定 ID，存档只认这个，不要随意改名。")]
        [SerializeField] private string _mapId = "home";
        [SerializeField] private string _displayName = "家园";

        [Header("范围（水平面，单位：米，以世界原点为中心）")]
        [SerializeField] private Vector2 _sizeXZ = new Vector2(60f, 60f);
        [Tooltip("可行走区域比地图范围向内收缩的距离，一般等于围墙厚度。")]
        [SerializeField] private float _walkableInset = 0.6f;

        [Tooltip("【地形视觉外扩】地面网格向外多生成这么多米，做出「边界之外还有一大片空地」的观感。\n" +
                 "只影响地面的渲染与碰撞，【不影响可行走范围】——\n" +
                 "玩家依然被 WalkableHalfExtent 挡住（空气墙），所以外圈是看得到、走不到的远景地。\n" +
                 "设成 0 就回到「地面刚好铺满可行走区域」的旧行为。")]
        [SerializeField, Min(0f)] private float _terrainPadding = 45f;

        [Header("层数（预留，当前只有 1 层）")]
        [Tooltip("包含地下室时为 1 + 地上层数。当前原型请保持为 1。")]
        [SerializeField] private int _floorCount = 1;
        [Tooltip("地下首层的编号，例如 -1 表示 B1。当前原型无实际用途。")]
        [SerializeField] private int _lowestFloorIndex = 0;

        [Header("出生点")]
        [SerializeField] private Vector3 _defaultSpawnPosition = new Vector3(0f, 0.1f, -4f);
        [SerializeField] private float _defaultSpawnYaw = 0f;

        public string MapId => _mapId;
        public string DisplayName => _displayName;
        public Vector2 SizeXZ => _sizeXZ;
        public int FloorCount => Mathf.Max(1, _floorCount);
        public int LowestFloorIndex => Mathf.Min(_lowestFloorIndex, 0);
        public Vector3 DefaultSpawnPosition => _defaultSpawnPosition;
        public float DefaultSpawnYaw => _defaultSpawnYaw;

        /// <summary>可行走半径（X/Z 相同）。</summary>
        public float WalkableHalfExtent => Mathf.Max(0f, Mathf.Min(_sizeXZ.x, _sizeXZ.y) * 0.5f - _walkableInset);

        /// <summary>
        /// 地形【视觉】半径 = 可行走半径 + 外扩量。
        ///
        /// 【为什么要把两个半径分开】可行走范围决定「玩家能走多远」（空气墙的位置，
        /// 也是存档真正关心的边界）；地形半径决定「画面里能看多远」。
        /// 原先两者是同一个值，所以地图边界看起来就是地面的边缘。
        /// 分开之后，边界外面可以铺一大片走不到的空地，观感上地图更大。
        /// </summary>
        public float TerrainHalfExtent => WalkableHalfExtent + Mathf.Max(0f, _terrainPadding);

        public Vector3 Min => new Vector3(-_sizeXZ.x * 0.5f, 0f, -_sizeXZ.y * 0.5f);
        public Vector3 Max => new Vector3(_sizeXZ.x * 0.5f, 0f, _sizeXZ.y * 0.5f);

        public Bounds WorldBounds => new Bounds(Vector3.zero, new Vector3(_sizeXZ.x, 100f, _sizeXZ.y));

        public bool Contains(Vector3 worldPosition)
        {
            return ContainsXZ(worldPosition, 0f);
        }

        public bool ContainsXZ(Vector3 worldPosition, float margin = 0f)
        {
            float half = WalkableHalfExtent - margin;
            if (half <= 0f) return false;
            return Mathf.Abs(worldPosition.x) <= half && Mathf.Abs(worldPosition.z) <= half;
        }

        /// <summary>把位置夹回区域内部（Y 不变）。</summary>
        public Vector3 ClampPosition(Vector3 worldPosition, float margin = 0f)
        {
            float half = Mathf.Max(0f, WalkableHalfExtent - margin);
            worldPosition.x = Mathf.Clamp(worldPosition.x, -half, half);
            worldPosition.z = Mathf.Clamp(worldPosition.z, -half, half);
            return worldPosition;
        }

        /// <summary>
        /// 把一次位移夹回区域内部，返回允许的位移量。
        /// 用「位移」而不是「位置」做接口，是为了以后和 CharacterController / 物理推挤共存。
        /// </summary>
        public Vector3 ClampMove(Vector3 currentPosition, Vector3 delta, float margin = 0f)
        {
            float half = Mathf.Max(0f, WalkableHalfExtent - margin);
            Vector3 target = currentPosition + delta;

            if (target.x > half) delta.x = half - currentPosition.x;
            else if (target.x < -half) delta.x = -half - currentPosition.x;

            if (target.z > half) delta.z = half - currentPosition.z;
            else if (target.z < -half) delta.z = -half - currentPosition.z;

            return delta;
        }

        private void OnValidate()
        {
            _sizeXZ.x = Mathf.Max(4f, _sizeXZ.x);
            _sizeXZ.y = Mathf.Max(4f, _sizeXZ.y);
            _walkableInset = Mathf.Max(0f, _walkableInset);
            _terrainPadding = Mathf.Max(0f, _terrainPadding);
            _floorCount = Mathf.Max(1, _floorCount);
            _lowestFloorIndex = Mathf.Min(_lowestFloorIndex, 0);
        }
    }
}
