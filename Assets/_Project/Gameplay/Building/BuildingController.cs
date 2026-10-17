using Game.Player;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 一栋建筑。
    ///
    /// 职责：
    ///   1. 注册所有楼层（BuildingFloor）
    ///   2. 根据"玩家是不是在这栋楼里、在哪一层"决定每层怎么显示
    ///   3. 负责切换当前楼层（由楼梯调用）
    ///
    /// 【设计要点】判断"该显示哪层"的规则只写在本类的 ApplyVisibility 里。
    /// 楼层自己（BuildingFloor）只负责"按指示显示"，不知道别人。
    /// 以后加地下室/三楼，只需要改 ApplyVisibility 的规则，不用改任何其它脚本。
    ///
    /// 【为什么不用 transform.position.y 判断楼层】
    /// Y 只是几何位置，楼层是逻辑概念（以后会有层高不一、错层、楼梯井）。
    /// 所以楼层号由 BuildingFloor.floorIndex 显式声明。
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingController : MonoBehaviour
    {
        [Header("标识")]
        [Tooltip("建筑 ID。存档/联机用，也用于区分多栋建筑。不要用 GameObject 名字代替它。")]
        [SerializeField] private string _buildingId = "building";

        [Header("楼层")]
        [Tooltip("本建筑的所有楼层。由场景生成器注册，也可以在 Inspector 手动拖动。")]
        [SerializeField] private BuildingFloor[] _floors;

        [Tooltip("地面层（一楼）的楼层号。玩家从室外看到的那一层就是它。")]
        [SerializeField] private int _groundFloorIndex = 0;

        [Header("屋顶与外壳")]
        [Tooltip("屋顶。玩家在建筑内部时要隐藏它，否则它会挡住俯视视角看到室内。")]
        [SerializeField] private Transform _roof;

        [Tooltip("建筑外壳（从室外看是完整的外墙）。玩家在内部时弱化它，避免遮挡室内视线。\n" +
                 "【按层拆分】下标对应楼层：_exteriorShells[0] = 一楼外墙、[1] = 二楼外墙……\n" +
                 "拆开的原因：玩家在一楼时，二楼外墙应该比一楼更透明，\n" +
                 "否则从俯视角看，二楼那圈墙会把一楼的视野压住半截。\n" +
                 "只有一个元素时（单层建筑）行为与旧版一致。")]
        [SerializeField] private Transform[] _exteriorShells;

        [Tooltip("各层的【天花板】（它同时是上一层的可站立地板）。\n" +
                 "数组下标与楼层一一对应：_floorSlabs[0] = 一楼的天花板 = 二楼的地板。\n" +
                 "用途：玩家站在某层时，头顶那块天花板会挡住俯视视线，所以要把它隐藏掉；\n" +
                 "但它【必须保留碰撞】，否则玩家会从二楼掉到一楼。")]
        [SerializeField] private Transform[] _floorSlabs;

        [Tooltip("弱化屋顶/外壳时使用的透明度。")]
        [SerializeField, Range(0.02f, 1f)] private float _shellGhostAlpha = 0.16f;

        [Tooltip("【高楼层外壳】的透明度。必须小于 _shellGhostAlpha 才会更透明。\n" +
                 "玩家站在某层时，比它更高的楼层外壳用这个值 ——\n" +
                 "这样一楼的视野不会被二楼那圈墙压住。\n" +
                 "取 0.06 约等于「几乎看不见，但还留着一点轮廓提示」。")]
        [SerializeField, Range(0.01f, 1f)] private float _upperShellGhostAlpha = 0.06f;

        [Tooltip("玩家在室内时，屋顶的显示方式。\n" +
                 "Hidden = 完全隐藏（推荐）；Ghosted = 半透明但保留轮廓。\n" +
                 "注意：无论哪种，屋顶的碰撞都会保留 —— 否则玩家会从二楼穿出房子。")]
        [SerializeField] private FloorVisibility _roofWhenInside = FloorVisibility.Hidden;

        [Header("室外时各楼层怎么显示")]
        [Tooltip("玩家在室外时，「一楼」显示方式。\n" +
                 "默认 Hidden：室外时整栋建筑的内部都看不到，只有外壳和屋顶（完整的房子外观）。\n" +
                 "改成 Visible 就能从外面直接看到一楼里面（遮挡只剩屋顶和墙）。")]
        [SerializeField] private FloorVisibility _outsideGroundFloor = FloorVisibility.Hidden;

        [Tooltip("玩家在室外时，比一楼更高的楼层怎么显示。Hidden = 完全不显示，避免室内被人一眼看光。")]
        [SerializeField] private FloorVisibility _outsideUpperFloors = FloorVisibility.Hidden;

        [Tooltip("玩家在室外时，比一楼更低的楼层（地下室）怎么显示。")]
        [SerializeField] private FloorVisibility _outsideLowerFloors = FloorVisibility.Hidden;

        [Header("玩家状态（运行时只读）")]
        [SerializeField] private int _currentFloorIndex;
        [SerializeField] private bool _isPlayerInside;

        private int _insideDepth;
        private bool _warnedAboutShell;

        /// <summary>本建筑 ID。</summary>
        public string BuildingId => _buildingId;

        /// <summary>当前楼层号（玩家在室内时有效）。</summary>
        public int CurrentFloorIndex => _currentFloorIndex;

        /// <summary>玩家是否在这栋建筑的内部。</summary>
        public bool IsPlayerInside => _isPlayerInside;

        /// <summary>最近一次玩家所在的建筑。没有则为 null。</summary>
        public static BuildingController ActiveBuilding { get; private set; }

        private void Awake()
        {
            ValidateFloors();
            RefreshSlots();

            // 初始：按"玩家在室外"的规则摆好，避免开局时里面全是亮的。
            ApplyVisibility();
            ApplyShellVisibility();
        }

        /// <summary>
        /// 临时的位置记录：玩家在建筑内时每 0.5 秒打一次坐标。
        ///
        /// 这是为了定位"上不去楼梯"到底卡在哪一步 ——
        /// 是根本没走上坡道、还是卡在坡道中段、还是卡在天花板洞口。
        /// 坐标序列一看就知道。问题解决后可以把这个方法删掉。
        /// </summary>
        private void Update()
        {
            if (!_logPlayerTrace || !_isPlayerInside) return;

            if (Time.unscaledTime < _nextTraceTime) return;
            _nextTraceTime = Time.unscaledTime + 0.5f;

            Transform player = ResolveTracePlayer();
            if (player == null) return;

            Vector3 p = player.position;
            Debug.Log($"[建筑-位置] 楼层 {_currentFloorIndex}，(x {p.x:F2}, y {p.y:F2}, z {p.z:F2})", this);
        }

        private float _nextTraceTime;

        [Header("调试")]
        [Tooltip("打开后，玩家在建筑内时每 0.5 秒把坐标打到 Console。用来定位卡在楼梯哪一段。")]
        [SerializeField] private bool _logPlayerTrace = false;

        private Transform _tracePlayer;

        private Transform ResolveTracePlayer()
        {
            if (_tracePlayer != null) return _tracePlayer;

            GameObject found = GameObject.FindGameObjectWithTag("Player");
            if (found != null) _tracePlayer = found.transform;

            return _tracePlayer;
        }

        /// <summary>检查楼层配置是否合法。重复的楼层号会导致切换行为不可预测，所以直接报错。</summary>
        private void ValidateFloors()
        {
            if (_floors == null || _floors.Length == 0)
            {
                Debug.LogWarning($"[{nameof(BuildingController)}]「{name}」没有注册任何楼层。", this);
                return;
            }

            for (int i = 0; i < _floors.Length; i++)
            {
                if (_floors[i] == null)
                {
                    Debug.LogError($"[{nameof(BuildingController)}]「{name}」的第 {i} 个楼层槽位是空的。", this);
                    continue;
                }

                for (int j = i + 1; j < _floors.Length; j++)
                {
                    if (_floors[j] != null && _floors[j].FloorIndex == _floors[i].FloorIndex)
                    {
                        Debug.LogError(
                            $"[{nameof(BuildingController)}]「{name}」有重复的楼层号 {_floors[i].FloorIndex}：" +
                            "同一栋建筑里楼层号必须唯一，否则切换行为不可预测。", this);
                    }
                }
            }
        }

        /// <summary>按楼层号取楼层。找不到返回 null。</summary>
        public BuildingFloor GetFloor(int floorIndex)
        {
            if (_floors == null) return null;

            for (int i = 0; i < _floors.Length; i++)
            {
                if (_floors[i] != null && _floors[i].FloorIndex == floorIndex) return _floors[i];
            }

            return null;
        }

        /// <summary>本建筑存在的楼层号（升序），供 UI / 调试使用。</summary>
        public int[] GetFloorIndices()
        {
            if (_floors == null) return new int[0];

            System.Collections.Generic.List<int> indices = new System.Collections.Generic.List<int>();
            for (int i = 0; i < _floors.Length; i++)
            {
                if (_floors[i] != null) indices.Add(_floors[i].FloorIndex);
            }

            indices.Sort();
            return indices.ToArray();
        }

        // ------------------------------------------------------------------ 玩家进出

        /// <summary>由 BuildingInteriorTrigger 调用：玩家进入建筑内部。</summary>
        public void OnPlayerEnter(GameObject player)
        {
            _insideDepth++;
            _isPlayerInside = true;
            ActiveBuilding = this;

            // 进入时以玩家实际所在的楼层为准。
            // 触发器不负责算楼层号，这里用"脚下最近的下方楼层"来推断，
            // 这样即使玩家是从二楼窗户之类的地方进来，也能落到正确楼层。
            _currentFloorIndex = ResolveFloorFromPosition(player != null ? player.transform.position : transform.position);
            RefreshSlots();

            ApplyVisibility();
            ApplyShellVisibility();
        }

        /// <summary>由 BuildingInteriorTrigger 调用：玩家离开建筑内部。</summary>
        public void OnPlayerExit(GameObject player)
        {
            _insideDepth = Mathf.Max(0, _insideDepth - 1);

            // 用计数而不是布尔：玩家可能同时处在多个触发区里（门口、楼梯井），
            // 只要还有一个区没离开，就仍然算"在室内"。
            if (_insideDepth > 0) return;

            _isPlayerInside = false;
            if (ActiveBuilding == this) ActiveBuilding = null;

            // 回到地面层的显示规则（玩家已经在室外，楼层号不再有意义）。
            _currentFloorIndex = _groundFloorIndex;
            RefreshSlots();

            ApplyVisibility();
            ApplyShellVisibility();
        }

        /// <summary>
        /// 根据世界坐标推断应该归属哪一层：取"不高于该位置"的最高楼层。
        ///
        /// 这是全工程**唯一**使用 Y 高度判断楼层的地方，而且只在"进入建筑"这一瞬间用一次。
        /// 之后的楼层切换一律由楼梯显式指定，不再依赖高度 —— 这样以后做错层、
        /// 层高不一、楼梯井都不会出问题。
        /// </summary>
        public int ResolveFloorFromPosition(Vector3 worldPosition)
        {
            if (_floors == null || _floors.Length == 0) return _groundFloorIndex;

            int best = int.MinValue;

            for (int i = 0; i < _floors.Length; i++)
            {
                BuildingFloor floor = _floors[i];
                if (floor == null) continue;

                if (floor.transform.position.y <= worldPosition.y + 0.5f && floor.FloorIndex > best)
                {
                    best = floor.FloorIndex;
                }
            }

            return best == int.MinValue ? _groundFloorIndex : best;
        }

        // ------------------------------------------------------------------ 楼层切换

        /// <summary>
        /// 切到指定楼层。由楼梯（FloorTransition）调用。
        /// 返回是否切换成功。
        /// </summary>
        public bool MoveToFloor(int floorIndex)
        {
            BuildingFloor floor = GetFloor(floorIndex);
            if (floor == null)
            {
                Debug.LogWarning(
                    $"[{nameof(BuildingController)}]「{name}」没有楼层 {floorIndex}，忽略这次切换。", this);
                return false;
            }

            _currentFloorIndex = floorIndex;
            RefreshSlots();

            // 玩家可能在触发区外按了楼梯（理论上不会，这里只是保险）。
            if (!_isPlayerInside)
            {
                _isPlayerInside = true;
                ActiveBuilding = this;
            }

            ApplyVisibility();
            ApplyShellVisibility();

            Debug.Log($"[建筑] 「{name}」当前楼层切换到 {floorIndex}（{floor.DisplayName}）", this);

            return true;
        }

        // ------------------------------------------------------------------ 显示规则

        /// <summary>
        /// 【核心规则】决定每一层怎么显示。
        ///
        /// 所有与楼层显示有关的判断都集中在这里。要加地下室/三楼、
        /// 或者改成"只显示当前层和相邻层"，都只改这个方法。
        /// </summary>
        public void ApplyVisibility() => ApplyVisibility(_isPlayerInside, _currentFloorIndex);

        /// <summary>
        /// 带参数版本。除了运行时状态，编辑器工具也用这个方法来"按指定状态摆一次"。
        /// </summary>
        public void ApplyVisibility(bool isInside, int currentFloorIndex)
        {
            if (_floors == null) return;

            for (int i = 0; i < _floors.Length; i++)
            {
                BuildingFloor floor = _floors[i];
                if (floor == null) continue;

                floor.SetGhostAlpha(_shellGhostAlpha);

                // 屋顶/外壳这类部件由 ApplyShellVisibility 直接控制，不参与楼层规则。
                if (floor.IgnoreFloorVisibilityRules) continue;

                // 只有"玩家当前所在层"的交互物才启用。
                // 否则从室外看着一楼时，能隔着墙按 E 操作屋里的东西。
                floor.IsCurrentFloorInteractable = isInside && floor.FloorIndex == currentFloorIndex;

                floor.SetVisibility(ResolveFloorVisibility(floor.FloorIndex, isInside, currentFloorIndex));
            }

            ApplyGhostAlphaToShell(_roof, _shellGhostAlpha);

            // 外壳按层给透明度：与玩家同层或更低层用 _shellGhostAlpha，
            // 更高层用 _upperShellGhostAlpha（更透明），这样一楼的视野不会被二楼那圈墙压住。
            if (_exteriorShells != null)
            {
                for (int i = 0; i < _exteriorShells.Length; i++)
                {
                    ApplyGhostAlphaToShell(_exteriorShells[i], i > currentFloorIndex
                        ? _upperShellGhostAlpha
                        : _shellGhostAlpha);
                }
            }
        }

        /// <summary>
        /// 一次性诊断：报告屋顶/外壳/天花板各自被设成了什么状态，以及实际有多少渲染器是开着的。
        ///
        /// 为什么需要它："还有东西挡视线"这句话无法区分是屋顶、外壳还是天花板 ——
        /// 这三者的隐藏逻辑不同。把它们的实际渲染器数量打出来，一次就能定位到具体是哪一块。
        /// 等布局稳定后可以删掉这个方法。
        /// </summary>
        private void LogShellState(string reason)
        {
            if (_loggedShellState) return;
            _loggedShellState = true;

            Debug.Log(
                $"[建筑显示] {reason}\n" +
                $"  玩家在室内 = {_isPlayerInside}，当前层 = {_currentFloorIndex}\n" +
                $"  屋顶 = {DescribeShell(_roof)}\n" +
                $"  外壳（按层）= {DescribeShells()}\n" +
                $"  天花板数量 = {(_floorSlabs != null ? _floorSlabs.Length : 0)}：" +
                DescribeSlabs(),
                this);
        }

        private string DescribeShells()
        {
            if (_exteriorShells == null || _exteriorShells.Length == 0) return "（未注册）";

            var builder = new System.Text.StringBuilder();

            for (int i = 0; i < _exteriorShells.Length; i++)
            {
                if (i > 0) builder.Append("｜");
                builder.Append($"[{i}] {DescribeShell(_exteriorShells[i])}");
            }

            return builder.ToString();
        }

        private string DescribeSlabs()
        {
            if (_floorSlabs == null || _floorSlabs.Length == 0) return "（未注册）";

            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < _floorSlabs.Length; i++)
            {
                if (i > 0) builder.Append("；");
                builder.Append($"[{i}] {DescribeShell(_floorSlabs[i])}");
            }

            return builder.ToString();
        }

        private static string DescribeShell(Transform target)
        {
            if (target == null) return "（未指定）";

            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            int visible = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled) visible++;
            }

            return $"{target.name} → 渲染器 {visible}/{renderers.Length} 开着";
        }

        private bool _loggedShellState;

        private void ApplyGhostAlphaToShell(Transform target, float alpha)
        {
            if (target == null) return;

            BuildingFloor proxy = target.GetComponent<BuildingFloor>();
            if (proxy != null) proxy.SetGhostAlpha(alpha);
        }

        /// <summary>单层规则。独立成方法，方便以后被编辑器工具或测试单独调用。</summary>
        public FloorVisibility ResolveFloorVisibility(int floorIndex, bool isInside, int currentFloorIndex)
        {
            if (isInside)
            {
                // 室内：只完整显示当前层，其它层全部隐藏。
                // 隐藏而不是半透明 —— 半透明的下层墙体会在斜俯视下叠在当前层上，很难看清。
                return floorIndex == currentFloorIndex ? FloorVisibility.Visible : FloorVisibility.Hidden;
            }

            // 室外：一楼按配置显示（默认完整，靠屋顶+外墙遮挡室内）；
            // 更高的楼层默认隐藏（否则室内被人一眼看光），更低的楼层（地下室）也默认隐藏。
            if (floorIndex == _groundFloorIndex) return _outsideGroundFloor;
            if (floorIndex > _groundFloorIndex) return _outsideUpperFloors;
            return _outsideLowerFloors;
        }

        // ------------------------------------------------------------------ 屋顶与外壳

        private void ApplyShellVisibility()
        {
            // 屋顶：玩家在室内时隐藏渲染，但【保留碰撞】。
            //   隐藏渲染：斜俯视下屋顶会把整个室内压住。
            //   保留碰撞：否则玩家会从二楼直接穿出房子。
            SetShellState(_roof, _isPlayerInside ? _roofWhenInside : FloorVisibility.Visible);

            // 外壳：玩家在室内时半透明（能看到外面的地图），在室外时完整不透明（挡住室内）。
            // 两种状态都保留碰撞 —— 外墙没有了玩家会走出房子。
            // 【按层处理】更高层的外壳用更低的透明度，否则一楼的视野会被二楼那圈墙压住。
            if (_exteriorShells != null)
            {
                for (int i = 0; i < _exteriorShells.Length; i++)
                {
                    SetShellState(_exteriorShells[i],
                        _isPlayerInside ? FloorVisibility.Ghosted : FloorVisibility.Visible);
                }
            }

            ApplyFloorSlabVisibility();

            // 一次性诊断：把屋顶/外壳/天花板的实际状态打出来，方便定位"还有东西挡视线"。
            LogShellState(_isPlayerInside ? "玩家在室内" : "玩家在室外");
        }

        /// <summary>
        /// 天花板（各层头顶那一块）：只隐藏"玩家头顶的"，其余显示。
        ///
        /// 为什么必须按玩家所在层隐藏：
        ///   一楼天花板就是二楼的地板。玩家在一楼时如果它显示着，
        ///   斜俯视下会看到一块实心板压在头顶，完全看不到二楼。
        ///
        /// 为什么必须保留碰撞：
        ///   它是玩家站在二楼时唯一能踩的东西。隐藏时只关渲染，绝不碰碰撞体，
        ///   否则玩家一上楼就会掉到一楼（本工程踩过这个坑）。
        /// </summary>
        private void ApplyFloorSlabVisibility()
        {
            if (_floorSlabs == null) return;

            for (int i = 0; i < _floorSlabs.Length; i++)
            {
                Transform slab = _floorSlabs[i];
                if (slab == null) continue;

                // 玩家在室内：隐藏"玩家所在层"的天花板（头顶那块）。
                // 玩家在室外：全部显示（从外面看是一栋完整的房子）。
                bool hideIt = _isPlayerInside && i == _currentFloorSlot;

                SetShellState(slab, hideIt ? FloorVisibility.Hidden : FloorVisibility.Visible);
            }
        }

        /// <summary>把楼层号转换成 _floorSlabs 数组里的下标。找不到返回 -1。</summary>
        private int ResolveFloorSlot(int floorIndex)
        {
            if (_floors == null) return -1;

            for (int i = 0; i < _floors.Length; i++)
            {
                if (_floors[i] != null && _floors[i].FloorIndex == floorIndex) return i;
            }

            return -1;
        }

        private int _groundFloorSlot;
        private int _currentFloorSlot;

        /// <summary>
        /// 刷新"当前层 / 地面层"对应的楼板下标。
        /// 在进入建筑、离开建筑、切换楼层时各调一次即可，不必每帧算。
        /// </summary>
        private void RefreshSlots()
        {
            _groundFloorSlot = ResolveFloorSlot(_groundFloorIndex);
            _currentFloorSlot = _isPlayerInside ? ResolveFloorSlot(_currentFloorIndex) : _groundFloorSlot;
        }

        private void SetShellState(Transform target, FloorVisibility visibility)
        {
            if (target == null) return;

            BuildingFloor proxy = target.GetComponent<BuildingFloor>();
            bool visible = visibility != FloorVisibility.Hidden;

            if (proxy != null)
            {
                // 有 BuildingFloor：能开渲染，也能做半透明。
                // 透明度用调用方（ApplyVisibility）按层算好的值，这里不再覆盖，
                // 否则高层外壳的"更透明"会被这里重置回 _shellGhostAlpha。
                proxy.SetRenderersVisibleOnly(visible);

                if (visibility == FloorVisibility.Ghosted) proxy.BuildingFloorGhost();
                return;
            }

            // 没挂 BuildingFloor 的普通节点：只能整体开关渲染（不做半透明）。
            // 这不影响功能，只是室内时看不到建筑外壳而已（视线反而更好）。
            // 用一次性警告，避免每帧刷屏。
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = visible;
            }

            if (visibility != FloorVisibility.Ghosted || _warnedAboutShell) return;

            _warnedAboutShell = true;
            Debug.LogWarning(
                $"[{nameof(BuildingController)}]「{target.name}」没有 BuildingFloor 组件，" +
                "玩家在室内时它只能整体隐藏、无法半透明。想要淡出效果就给它加一个。", this);
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器用来注册楼层、屋顶/外壳、各层楼板。</summary>
        public void ConfigureBuilding(string buildingId, BuildingFloor[] floors, Transform roof,
            Transform[] exteriorShells, Transform[] floorSlabs)
        {
            _buildingId = buildingId;
            _floors = floors;
            _roof = roof;
            _exteriorShells = exteriorShells;
            _floorSlabs = floorSlabs;

            // 立刻刷新一次，保证 Awake 之前被配置时状态也是对的。
            RefreshSlots();
        }
#endif
    }
}
