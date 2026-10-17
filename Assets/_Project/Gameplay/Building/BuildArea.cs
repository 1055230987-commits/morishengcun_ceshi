using Game.World;
using UnityEngine;

namespace Game.Gameplay.Building
{
    /// <summary>
    /// 一块可建造的平地。挂在场景里，Inspector 调位置和大小。
    ///
    /// 【它负责什么】把世界坐标和"格子坐标"对上；把 <see cref="BuildAreaData"/> 这个纯逻辑
    /// 和场景里的实际位置连起来；以及在游戏里画出可见的建造区（不能只用 Gizmos ——
    /// Gizmos 只在编辑器里出现，运行时玩家看不到区域在哪）。
    ///
    /// 【它不负责什么】占用数据、材料、预览 —— 那些在 BuildController。
    /// 本组件只回答"这块地在哪、有多大、某个世界坐标属于哪一格"。
    ///
    /// 【为什么用 MonoBehaviour 而不是 ScriptableObject】
    /// 建造区是【场景里的一个具体位置】（玩家实际站在那里建），属于场景数据。
    /// 用组件就能在 Scene 视图里直接拖动调整，也能看到 Gizmos。
    /// 数据部分（格子、占用）仍然抽在 BuildAreaData 里，测试不需要 Unity。
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildArea : MonoBehaviour
    {
        public enum PreviewMode
        {
            /// <summary>只在建造模式下显示区域。推荐 —— 平时不挡视线。</summary>
            OnlyWhileBuilding,

            /// <summary>始终显示。</summary>
            Always,

            /// <summary>从不显示（只靠 Gizmos 在编辑器里看）。</summary>
            Never,
        }

        [Header("尺寸（格数）")]
        [Tooltip("建造区标识。存档按它把建造物归属回这块地，改名会让旧存档找不到。")]
        [SerializeField] private string _areaId = "home";

        [Tooltip("X 方向的格子数。")]
        [SerializeField, Min(1)] private int _width = 4;

        [Tooltip("Z 方向的格子数。默认和 _width 相同（正方形区域）。")]
        [SerializeField, Min(1)] private int _depth = 4;

        [Tooltip("每格边长（米）。1 = 标准 1×1 部件。")]
        [SerializeField, Min(0.25f)] private float _cellSize = 1f;

        [Header("楼层")]
        [Tooltip("建造区所属楼层。-1 = 地下室，0 = 一楼。")]
        [SerializeField] private int _floor;

        [Tooltip("地面高度（米）。格子的逻辑高度，用于放置部件和射线求交。")]
        [SerializeField] private float _groundHeight;

        [Header("游戏内显示")]
        [Tooltip("什么时候显示建造区。默认只在建造模式下显示。")]
        [SerializeField] private PreviewMode _showArea = PreviewMode.OnlyWhileBuilding;

        [Tooltip("区域底色（半透明）。")]
        [SerializeField] private Color _areaColor = new Color(0.35f, 0.75f, 1f, 0.10f);

        [Tooltip("网格线颜色。")]
        [SerializeField] private Color _gridColor = new Color(0.55f, 0.85f, 1f, 0.35f);

        [Tooltip("网格线的粗细（米）。太细在俯视角下会看不清。")]
        [SerializeField, Range(0.005f, 0.1f)] private float _lineThickness = 0.03f;

        [Tooltip("区域显示离地高度。稍微抬起一点避免和地面 z-fighting（本工程踩过地面闪烁）。")]
        [SerializeField] private float _displayLift = 0.02f;

        private BuildAreaData _data;
        private GameObject _areaVisual;
        private Transform _gridLines;
        private Material _areaMaterial;
        private Material _lineMaterial;

        /// <summary>
        /// 纯逻辑的占用数据。第一次访问时创建，之后【尺寸和楼层就固定了】。
        ///
        /// 【为什么改成"固定"而不是"发现不一致就重建"】
        /// 早先的写法是"如果 Inspector 的宽深和当前数据不一致，就丢掉数据重建"。
        /// 那会留下一个很难查的状态：占用数据被清空、但场景里已生成的物体还在 ——
        /// 于是玩家能建在已有方块上（重叠），或者拆不掉看得见的东西。
        ///
        /// 现在改成：数据一旦建立就不再改。运行中改了 Inspector 的话，
        /// 由 <see cref="ValidateConfiguration"/> 报错，而不是静默地破坏状态。
        /// </summary>
        public BuildAreaData Data
        {
            get
            {
                if (_data == null)
                {
                    _data = new BuildAreaData(_width, _depth, _floor, _cellSize);
                }

                return _data;
            }
        }

        /// <summary>
        /// 检查"场景里的配置"和"已经建立的占用数据"是否还一致。
        /// 不一致时返回 false 并打印明确的错误 —— 由 BuildController 在启动时调用。
        ///
        /// 【为什么不自动迁移】迁移要把数据、场景物体、显示三者一起改，
        /// 而这个阶段还没有存档与重建机制，做半个迁移只会制造更隐蔽的不一致。
        /// 明确报错、让玩家重建场景，比"看起来没事但状态已经歪了"好。
        /// </summary>
        public bool ValidateConfiguration()
        {
            BuildAreaData data = Data;

            bool ok = true;

            if (data.Width != _width || data.Depth != _depth)
            {
                Debug.LogError(
                    $"[建造区]「{name}」的尺寸和已建立的占用数据不一致：" +
                    $"场景配置 {_width}×{_depth}，数据 {data.Width}×{data.Depth}。\n" +
                    "运行中改尺寸会让数据与已生成的方块分离。请重建场景" +
                    "（菜单「游戏 → 灰盒 → 重建到当前场景」）。", this);
                ok = false;
            }

            if (data.Floor != _floor)
            {
                Debug.LogError(
                    $"[建造区]「{name}」的楼层和占用数据不一致：" +
                    $"场景配置 {_floor}，数据 {data.Floor}。请重建场景。", this);
                ok = false;
            }

            if (!Mathf.Approximately(data.CellSize, _cellSize))
            {
                Debug.LogError(
                    $"[建造区]「{name}」的格子尺寸和占用数据不一致：" +
                    $"场景配置 {_cellSize:F3}，数据 {data.CellSize:F3}。" +
                    "格子尺寸变了，之前算出的世界坐标就全偏了。请重建场景。", this);
                ok = false;
            }

            return ok;
        }

        public int Width => _width;
        public int Depth => _depth;
        public float CellSize => _cellSize;
        public int Floor => _floor;
        public float GroundHeight => _groundHeight;

        /// <summary>建造区标识。存档按它归属。重名会让加载时归属错乱。</summary>
        public string AreaId => _areaId;

        /// <summary>区域最小角（格子 (0,0) 的外侧角）在世界坐标里的位置。</summary>
        public Vector3 OriginWorld => transform.position;

        // ---------------------------------------------------------------- 坐标换算

        /// <summary>格子中心的世界坐标（贴地）。</summary>
        public Vector3 CellCenterWorld(int x, int z)
        {
            Vector3 origin = OriginWorld;
            return new Vector3(
                origin.x + (x + 0.5f) * _cellSize,
                origin.y + _groundHeight,
                origin.z + (z + 0.5f) * _cellSize);
        }

        /// <summary>边中点的世界坐标（贴地）。墙就立在这里。</summary>
        public Vector3 EdgeCenterWorld(BuildEdge edge)
        {
            Vector3 origin = OriginWorld;
            return new Vector3(
                origin.x + edge.MidX2 * 0.5f * _cellSize,
                origin.y + _groundHeight,
                origin.z + edge.MidZ2 * 0.5f * _cellSize);
        }

        /// <summary>把世界坐标转成建造区局部坐标（只取水平面）。</summary>
        public void WorldToLocal(float worldX, float worldZ, out float localX, out float localZ)
        {
            Vector3 origin = OriginWorld;
            localX = worldX - origin.x;
            localZ = worldZ - origin.z;
        }

        /// <summary>世界坐标落在哪个格子。区域外返回 false。</summary>
        public bool WorldToCell(Vector3 world, out int x, out int z)
        {
            WorldToLocal(world.x, world.z, out float localX, out float localZ);
            return Data.LocalToCell(localX, localZ, out x, out z);
        }

        /// <summary>世界坐标最近的一条边。区域外返回 false。</summary>
        public bool WorldToNearestEdge(Vector3 world, out BuildEdge edge)
        {
            WorldToLocal(world.x, world.z, out float localX, out float localZ);
            return Data.LocalToNearestEdge(localX, localZ, out edge);
        }

        /// <summary>
        /// 建造区的水平面。鼠标射线和它求交，得到"指针指向建造区的哪个位置"。
        ///
        /// 【为什么不用 Physics.Raycast 打地面】地面可能被湖挖了洞、也可能有坡，
        /// 而且地形碰撞体很大，射线容易先打到别的东西。
        /// 用一个纯数学平面求交，"指针在地面哪里"这个问题就有了确定答案。
        ///
        /// 【注意】平面求交成功 ≠ 这里能放 —— 那只解决"指向哪一格"，
        /// 合法性（有没有被树挡住、脚下有没有支撑）必须另外检查。
        /// </summary>
        public Plane GroundPlane => new Plane(Vector3.up, OriginWorld + Vector3.up * _groundHeight);

        // ---------------------------------------------------------------- 显示

        private void Awake()
        {
            BuildVisual();
        }

        private void OnValidate()
        {
            _width = Mathf.Max(1, _width);
            _depth = Mathf.Max(1, _depth);
            _cellSize = Mathf.Max(0.25f, _cellSize);

            // Inspector 改了尺寸：把显示重建一遍，这样在 Scene 视图里能立刻看到新范围。
            //
            // 【为什么要在 OnValidate 里做】否则改了 Inspector 的宽高，
            // 编辑器里看到的还是旧的网格 —— 而 Gizmos 是每帧重画的，
            // 于是 Gizmos（新）和实际显示物体（旧）会对不上，很难判断哪个是真的。
            if (Application.isPlaying) return;
            if (_areaVisual != null) BuildVisual();
        }

        /// <summary>
        /// 生成游戏内可见的区域表示：一块半透明底色 + 网格线。
        ///
        /// 【为什么必须做这个】Gizmos 只在编辑器里画，运行时玩家完全看不到建造区在哪。
        /// 而"玩家知不知道自己在哪块地上建"是能不能用的问题，不是表现问题。
        /// </summary>
        private void BuildVisual()
        {
            if (_areaVisual != null)
            {
                DestroyImmediate(_areaVisual);
                _areaVisual = null;
                _gridLines = null;
            }

            _areaVisual = new GameObject("建造区显示");
            _areaVisual.transform.SetParent(transform, false);
            _areaVisual.transform.localPosition = Vector3.zero;

            // ---- 半透明底色 ----
            GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plane.name = "区域底色";
            plane.transform.SetParent(_areaVisual.transform, false);

            float sizeX = _width * _cellSize;
            float sizeZ = _depth * _cellSize;

            plane.transform.localPosition = new Vector3(
                sizeX * 0.5f,
                _groundHeight + _displayLift,
                sizeZ * 0.5f);
            plane.transform.localScale = new Vector3(sizeX, 0.01f, sizeZ);

            // 【必须删掉碰撞体】这块板只是显示用的。
            // 留着它会挡住鼠标射线、也会让玩家站在上面 —— 本工程在预览方块上踩过同类问题。
            Collider planeCollider = plane.GetComponent<Collider>();
            if (planeCollider != null) DestroyImmediate(planeCollider);

            _areaMaterial = CreateTransparentMaterial("M_BuildArea_Display", _areaColor);
            Renderer planeRenderer = plane.GetComponent<Renderer>();
            planeRenderer.sharedMaterial = _areaMaterial;
            planeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            planeRenderer.receiveShadows = false;

            // ---- 网格线 ----
            _gridLines = new GameObject("网格线").transform;
            _gridLines.SetParent(_areaVisual.transform, false);

            _lineMaterial = CreateTransparentMaterial("M_BuildArea_Lines", _gridColor);

            // 沿 X 方向的线（横线），共 _depth + 1 条
            for (int z = 0; z <= _depth; z++)
            {
                CreateLine($"横线_{z}",
                    new Vector3(sizeX * 0.5f, 0f, z * _cellSize),
                    new Vector3(sizeX, 0f, _lineThickness));
            }

            // 沿 Z 方向的线（竖线），共 _width + 1 条
            for (int x = 0; x <= _width; x++)
            {
                CreateLine($"竖线_{x}",
                    new Vector3(x * _cellSize, 0f, sizeZ * 0.5f),
                    new Vector3(_lineThickness, 0f, sizeZ));
            }

            _areaVisual.SetActive(ShouldShowArea());
        }

        private void CreateLine(string name, Vector3 localCenter, Vector3 localScale)
        {
            GameObject line = GameObject.CreatePrimitive(PrimitiveType.Cube);
            line.name = name;
            line.transform.SetParent(_gridLines, false);
            line.transform.localPosition = new Vector3(
                localCenter.x,
                _groundHeight + _displayLift + 0.005f,
                localCenter.z);
            line.transform.localScale = new Vector3(localScale.x, 0.01f, localScale.z);

            Collider collider = line.GetComponent<Collider>();
            if (collider != null) DestroyImmediate(collider);

            Renderer renderer = line.GetComponent<Renderer>();
            renderer.sharedMaterial = _lineMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// 创建一个运行时的半透明材质。
        ///
        /// 优先用 URP 的 Unlit；拿不到就退回 Sprites/Default（它在所有管线下都能画半透明）。
        /// 两个都拿不到时返回 null —— 调用方会退化成不透明，但不会崩。
        /// </summary>
        private static Material CreateTransparentMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            if (shader == null)
            {
                Debug.LogWarning("[建造区] 找不到可用的半透明 shader，区域显示会不透明。");
                return null;
            }

            Material material = new Material(shader) { name = name };

            // URP/Unlit 要显式开透明 + 设混合模式，否则 alpha 不起作用
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);   // 1 = Transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);       // 0 = Alpha
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return material;
        }

        private bool ShouldShowArea()
        {
            switch (_showArea)
            {
                case PreviewMode.Always: return true;
                case PreviewMode.Never: return false;
                default: return Game.Player.InputModeState.AllowsBuilding;
            }
        }

        private void Update()
        {
            if (_areaVisual == null) return;

            bool shouldShow = ShouldShowArea();
            if (_areaVisual.activeSelf != shouldShow) _areaVisual.SetActive(shouldShow);
        }

        // ---------------------------------------------------------------- 编辑器辅助

        private void OnDrawGizmos()
        {
            Vector3 origin = OriginWorld;
            float sizeX = _width * _cellSize;
            float sizeZ = _depth * _cellSize;

            // 外框
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.9f);
            Vector3 center = origin + new Vector3(sizeX * 0.5f, _groundHeight, sizeZ * 0.5f);

            Gizmos.DrawWireCube(center + Vector3.up * 0.005f, new Vector3(sizeX, 0.01f, sizeZ));

            // 网格线（只画内部的，外框已经画了）
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.35f);

            for (int x = 1; x < _width; x++)
            {
                float wx = origin.x + x * _cellSize;
                Gizmos.DrawLine(
                    new Vector3(wx, origin.y + _groundHeight, origin.z),
                    new Vector3(wx, origin.y + _groundHeight, origin.z + sizeZ));
            }

            for (int z = 1; z < _depth; z++)
            {
                float wz = origin.z + z * _cellSize;
                Gizmos.DrawLine(
                    new Vector3(origin.x, origin.y + _groundHeight, wz),
                    new Vector3(origin.x + sizeX, origin.y + _groundHeight, wz));
            }
        }

        private void OnDestroy()
        {
            if (_areaMaterial != null) Destroy(_areaMaterial);
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureArea(int width, int depth, float cellSize, int floor, float groundHeight,
            string areaId = null)
        {
            _width = Mathf.Max(1, width);
            _depth = Mathf.Max(1, depth);
            _cellSize = Mathf.Max(0.25f, cellSize);
            _floor = floor;
            _groundHeight = groundHeight;
            if (!string.IsNullOrEmpty(areaId)) _areaId = areaId;
        }
#endif
    }
}
