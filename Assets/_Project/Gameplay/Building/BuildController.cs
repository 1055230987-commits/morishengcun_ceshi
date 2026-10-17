using System.Collections.Generic;
using Game.Items;
using Game.Player;
using Game.Save;
using Game.World;
using UnityEngine;

namespace Game.Gameplay.Building
{
    /// <summary>
    /// 家园建造：切换建造模式、鼠标吸附、幽灵预览、放置与拆除、消耗材料。
    ///
    /// 【它负责什么】
    ///   模式切换（B 键）、把鼠标射线转成格子/边、显示预览、调用 BuildAreaData 判定、
    ///   扣材料、生成实际的方块、拆除与返还、以及游戏内的操作提示。
    ///
    /// 【它不负责什么】
    ///   占用规则（在 BuildAreaData）、坐标换算（在 BuildArea）、
    ///   方块长什么样（在 GreyboxPrimitives）。
    ///
    /// 【放置的顺序 —— 这是最容易出错的地方，刻意写死成这个顺序】
    ///   1. 算出要放的位置（格子或边）
    ///   2. 【全部】合法性检查：数据层占用、场景阻挡、支撑、材料
    ///   3. 扣材料
    ///   4. 生成物体
    ///   5. 生成失败 → 【把材料还回去】，并放弃
    ///   6. 登记到 BuildAreaData
    /// 任何一步失败都不允许留下"材料扣了但东西没生成"或"东西生成了但没登记"的状态。
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildController : MonoBehaviour
    {
        /// <summary>一种可建造的部件。</summary>
        [System.Serializable]
        public class BuildablePart
        {
            [Tooltip("部件标识，会写进存档。改这个会让旧存档认不出来。")]
            public string PartId = "floor";

            [Tooltip("显示名（游戏内提示用）。")]
            public string DisplayName = "地板";

            [Tooltip("是否占【边】（墙）而不是占【格】（地板）。")]
            public bool IsEdgePart;

            [Tooltip("消耗的物品。")]
            public ItemDefinition Material;

            [Tooltip("消耗数量。")]
            [Min(1)] public int MaterialCost = 1;

            [Tooltip("部件高度（米）。地板是很薄的板，墙是整面。")]
            public float Height = 0.15f;

            [Tooltip("部件厚度（米）。只对占边的墙有意义。")]
            public float Thickness = 0.25f;
        }

        [Header("引用")]
        [Tooltip("要在这块地上建造。")]
        [SerializeField] private BuildArea _area;

        [Tooltip("玩家输入源（读 B 键和鼠标位置）。留空则自动在父物体找。")]
        [SerializeField] private PlayerInputSource _input;

        [Tooltip("玩家的背包（扣材料用）。留空则自动在父物体找。")]
        [SerializeField] private PlayerInventory _inventoryOwner;

        [Header("可建造部件")]
        [Tooltip("按数字键 1、2…… 切换。第一个是默认选中的。")]
        [SerializeField] private List<BuildablePart> _parts = new List<BuildablePart>();

        [Header("外观")]
        [SerializeField] private Color _validColor = new Color(0.35f, 1f, 0.45f, 0.45f);
        [SerializeField] private Color _invalidColor = new Color(1f, 0.35f, 0.3f, 0.45f);

        [Header("提示")]
        [Tooltip("在屏幕上显示操作提示。")]
        [SerializeField] private bool _showHud = true;

        [Header("调试")]
        [Tooltip("打开后打印每次放置的判定结果。")]
        [SerializeField] private bool _logPlacement;

        // ---- 运行时 ----
        private bool _building;
        private int _selectedIndex;
        private GameObject _ghost;
        private Material _ghostMaterial;
        private readonly Dictionary<BuildAreaData.BuildCell, GameObject> _floorObjects
            = new Dictionary<BuildAreaData.BuildCell, GameObject>();
        private readonly Dictionary<BuildEdge, GameObject> _wallObjects
            = new Dictionary<BuildEdge, GameObject>();

        private string _statusMessage = string.Empty;
        private float _statusUntil;
        private GUIStyle _hudStyle;
        private GUIStyle _warnStyle;
        private Transform _spawnRoot;
        private Material _partMaterial;

        /// <summary>玩家建造物的存放节点名。诊断工具也按这个名字找，改这里要同步改。</summary>
        public const string SpawnRootName = "玩家建造物";

        public bool IsBuilding => _building;
        public BuildArea Area => _area;

        /// <summary>存放玩家建造物的世界根节点。存档加载时也用它当父节点。</summary>
        public Transform SpawnRoot => _spawnRoot;

        /// <summary>
        /// 清空本建造区里所有【玩家放置】的部件：销毁物体 + 清空占用数据。
        /// 返回清掉的部件数量。加载存档前要用它（否则会变成"旧存档 + 本次会话"的混合体）。
        ///
        /// 【预置部件必须保留 —— 这里修过一个"注释和行为不一致"的缺陷】
        /// 起步地板是靠 PreplacedPart 恢复的；清掉它会让玩家失去唯一的落脚点
        /// （地板需要支撑，就没法再铺了）。
        ///
        /// 早先的注释写着"只清玩家放置的，预置的保留"，但代码是
        /// **无条件销毁 _floorObjects / _wallObjects 里的所有物体** ——
        /// 而 RegisterPreplacedParts 恰好把起步地板也放进了这两个字典。
        /// 于是"清空"会把起步地板一起销毁，而数据层随后又把它登记回去：
        /// **数据里有一块地板，场景里没有对应物体**。
        /// 表现是：Play 里按 F9 后，起步地板消失、且再也放不了新地板。
        ///
        /// 现在先按组件把预置部件挑出来，销毁其余，再把预置的映射恢复回去。
        /// </summary>
        public int ClearAllParts()
        {
            // ---- 1. 先找出场景里所有预置部件（按组件查，不依赖任何缓存）----
            //
            // 【为什么用全局查找而不是查 _floorObjects】那两个字典可能还没被
            // RegisterPreplacedParts 填过（例如编辑模式下 Awake 没跑）。
            // 按 PreplacedPart 组件查是最直接的"场景里到底有什么"。
            var preplacedFloorCells = new HashSet<BuildAreaData.BuildCell>();
            var preplacedWalls = new HashSet<BuildEdge>();
            var preplacedFloorObjects = new Dictionary<BuildAreaData.BuildCell, GameObject>();
            var preplacedWallObjects = new Dictionary<BuildEdge, GameObject>();

            PreplacedPart[] preplaced = FindObjectsByType<PreplacedPart>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (PreplacedPart part in preplaced)
            {
                if (part == null) continue;

                if (part.IsWall)
                {
                    preplacedWalls.Add(part.Edge);
                    preplacedWallObjects[part.Edge] = part.gameObject;
                }
                else
                {
                    var cell = new BuildAreaData.BuildCell(part.CellX, part.CellZ, part.Floor);
                    preplacedFloorCells.Add(cell);
                    preplacedFloorObjects[cell] = part.gameObject;
                }
            }

            // ---- 2. 销毁玩家放置的（跳过预置的）----
            int cleared = 0;

            foreach (KeyValuePair<BuildAreaData.BuildCell, GameObject> pair in _floorObjects)
            {
                if (preplacedFloorCells.Contains(pair.Key)) continue;   // 预置的留下
                if (pair.Value != null) DestroyImmediate(pair.Value);
                cleared++;
            }

            foreach (KeyValuePair<BuildEdge, GameObject> pair in _wallObjects)
            {
                if (preplacedWalls.Contains(pair.Key)) continue;
                if (pair.Value != null) DestroyImmediate(pair.Value);
                cleared++;
            }

            // ---- 3. 映射只留预置的 ----
            _floorObjects.Clear();
            _wallObjects.Clear();

            foreach (KeyValuePair<BuildAreaData.BuildCell, GameObject> pair in preplacedFloorObjects)
            {
                if (pair.Value != null) _floorObjects[pair.Key] = pair.Value;
            }

            foreach (KeyValuePair<BuildEdge, GameObject> pair in preplacedWallObjects)
            {
                if (pair.Value != null) _wallObjects[pair.Key] = pair.Value;
            }

            // ---- 4. 清数据层，再只把预置部件登记回去 ----
            _area.Data.Clear();
            RegisterPreplacedParts();

            return cleared;
        }

        /// <summary>按部件标识找一个可建造部件（存档加载时用）。</summary>
        public BuildablePart FindPart(string partId) => FindPartById(partId);

        /// <summary>
        /// 按【格子/边】生成一个部件并登记，但【不检查材料】。
        ///
        /// 【为什么加载存档要用它而不是 TryPlace】
        /// TryPlace 面向玩家操作：它要检查材料、要看鼠标指向哪、要扣物品。
        /// 加载存档时这些都不适用 —— 材料在存档里，位置也是已知的。
        /// 所以单独提供一个"直接按数据生成"的入口，两条路共用同一个
        /// CreatePartObject + Register 流程，避免生成出来的东西不一样。
        ///
        /// 【楼层由本方法守卫，不靠调用方自觉】
        /// BuildAreaData 内部的键用的是【建造区自己的楼层】，
        /// 所以传入一个别的楼层的坐标会被静默地当成"本层"的位置 ——
        /// 那会把二楼的东西放到一楼（数据上也记成一楼）。
        /// 早先 GameSaveController 就是这么做的（它没把存档里的 Floor 传进来）。
        /// 现在在这里显式拒绝，任何调用方都绕不过去。
        ///
        /// 返回生成出来的物体（失败返回 null）。
        /// </summary>
        public GameObject SpawnPartFromData(BuildablePart part, bool isWall, BuildEdge edge,
            int cellX, int cellZ, int floor)
        {
            if (part == null || _area == null) return null;

            if (floor != _area.Floor)
            {
                Debug.LogError(
                    $"[建造] 拒绝在建造区「{_area.AreaId}」（楼层 {_area.Floor}）里生成楼层 {floor} 的部件。" +
                    "跨楼层的记录不该被应用到本区。", this);
                return null;
            }

            Vector3 groundPosition = isWall
                ? _area.EdgeCenterWorld(edge)
                : _area.CellCenterWorld(cellX, cellZ);

            var cell = new BuildAreaData.BuildCell(cellX, cellZ, _area.Floor);

            GameObject created = CreatePartObject(part, groundPosition, isWall, edge, cell);
            if (created == null) return null;

            bool registered = isWall
                ? _area.Data.RegisterWall(edge, part.PartId)
                : _area.Data.RegisterFloor(cellX, cellZ, part.PartId);

            if (!registered)
            {
                DestroyImmediate(created);
                return null;
            }

            if (isWall) _wallObjects[edge] = created;
            else _floorObjects[cell] = created;

            return created;
        }
        public BuildablePart SelectedPart =>
            _parts != null && _selectedIndex >= 0 && _selectedIndex < _parts.Count
                ? _parts[_selectedIndex]
                : null;

        // ---------------------------------------------------------------- 生命周期

        private void Awake()
        {
            if (_input == null) _input = GetComponentInParent<PlayerInputSource>();
            if (_inventoryOwner == null) _inventoryOwner = GetComponentInParent<PlayerInventory>();
            if (_area == null) _area = FindFirstObjectByType<BuildArea>();

            if (_area == null)
            {
                Debug.LogError(
                    "[建造] 场景里没有 BuildArea，建造系统无法工作。" +
                    "请执行菜单「游戏 → 灰盒 → 重建到当前场景」，或在场景里放一个带 BuildArea 的物体。", this);
                enabled = false;
                return;
            }

            // 生成出来的方块挂在这个【世界根节点】下面 —— 不能挂玩家身上，否则会跟着走
            _spawnRoot = ResolveSpawnRoot();

            // 配置一致性：尺寸/楼层/格子边长变了就明确报错，而不是静默地破坏占用数据
            _area.ValidateConfiguration();

            // 把场景里【预置的部件】（例如生成器放的起步地板）补登进占用数据。
            // 不做这一步的话：起步地板看得见、但数据层认为那格是空的（能重叠建、也拆不掉）。
            RegisterPreplacedParts();

            // 建造用的材质引用（不用 EnsurePalette —— 那会改所有材质资产并存盘）
            _partMaterial = Game.World.GreyboxPrimitives.GetOrCreateMaterial(
                Game.World.GreyboxPrimitives.StructureMaterialPath,
                new Color(0.52f, 0.52f, 0.55f));
        }

        /// <summary>
        /// 把场景里所有 <see cref="PreplacedPart"/> 读出来补登进占用数据。
        ///
        /// 【为什么只在本建造区范围内认领】一个场景可能有多个建造区；
        /// 用格子坐标是否落在本区内来判定归属，避免把别人的部件算到自己头上。
        /// </summary>
        /// <summary>
        /// 把场景里挂着 <see cref="PreplacedPart"/> 的部件登记进占用数据。
        ///
        /// 【为什么要公开】它早先是私有的、只在 Awake() 里调用 ——
        /// 而【编辑模式下 Awake 不跑】，于是存档工具在编辑模式下采集到的
        /// BuildAreaData 是空的（起步地板看不见），存出来的档里一个建造物都没有。
        ///
        /// 现在存档采集前会显式调一次 <see cref="SyncPreplacedParts"/>，
        /// 保证"数据层反映了场景里实际有的东西"。
        ///
        /// 幂等：重复调用不会产生重复记录（BuildAreaData 用 HashSet）。
        /// </summary>
        public void SyncPreplacedParts() => RegisterPreplacedParts();

        private void RegisterPreplacedParts()
        {
            PreplacedPart[] preplaced = FindObjectsByType<PreplacedPart>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            int registered = 0;
            int skipped = 0;

            foreach (PreplacedPart part in preplaced)
            {
                if (part == null) continue;

                bool ok = part.IsWall
                    ? _area.Data.RegisterWall(part.Edge, part.PartId)
                    : _area.Data.RegisterFloor(part.CellX, part.CellZ, part.PartId);

                if (ok)
                {
                    registered++;

                    // 建物体映射：这样预置部件也能被右键拆除（早先拆不了）
                    if (part.IsWall)
                    {
                        _wallObjects[part.Edge] = part.gameObject;
                    }
                    else
                    {
                        _floorObjects[new BuildAreaData.BuildCell(part.CellX, part.CellZ, part.Floor)]
                            = part.gameObject;
                    }
                }
                else
                {
                    skipped++;
                }
            }

            if (registered > 0 || skipped > 0)
            {
                Debug.Log($"[建造] 预置部件补登：成功 {registered} 个，跳过 {skipped} 个" +
                          "（跳过通常是重复登记或不在本区范围内）。");
            }
        }

        private void OnDisable()
        {
            // 【必须】对象被禁用/销毁时退回正常模式，
            // 否则会留下一个卡在建造模式的全局状态，玩家连枪都开不了。
            if (_building) ExitBuilding();
        }

        /// <summary>
        /// 存放玩家建造物的节点。
        ///
        /// 【必须是世界根节点下的独立物体 —— 这是一个真实的缺陷修复】
        /// 早先这里挂到 `transform.root`（玩家根节点）下面，有两个后果：
        ///   1. 玩家走路/转身会【带着整座房子一起动】；
        ///   2. CreateBlock 用的是 localPosition，而调用方传的是【世界坐标】——
        ///      玩家不在原点时，生成位置会再叠加一次玩家的位移，房子直接偏出去。
        ///
        /// 现在固定放在场景根节点下、位置归零，于是"局部坐标 == 世界坐标"，
        /// 第 2 个问题也一并消失。
        /// </summary>
        private Transform ResolveSpawnRoot()
        {
            if (_spawnRoot != null) return _spawnRoot;

            GameObject existing = GameObject.Find(SpawnRootName);
            if (existing != null)
            {
                _spawnRoot = existing.transform;
            }
            else
            {
                GameObject root = new GameObject(SpawnRootName);
                // 挂在场景根节点下、位置与旋转都归零 —— 这是"世界坐标 == 局部坐标"的前提
                root.transform.SetParent(null, false);
                root.transform.position = Vector3.zero;
                root.transform.rotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;
                _spawnRoot = root.transform;
            }

            return _spawnRoot;
        }

        // ---------------------------------------------------------------- 模式切换

        private void Update()
        {
            if (_area == null) return;

            HandleModeToggle();

            if (!_building) return;

            HandlePartSelection();

            // 【放置是否真的生效】由全局模式决定，而不是只看 _building。
            //
            // 这是修掉"B 进建造 → Tab 开背包 → 关闭后预览还在、放置失灵"的关键：
            // 背包打开时模式是 Inventory，此时不该显示预览、也不该响应点击。
            // 用"上一帧 vs 这一帧"比较来只在变化时处理一次，避免每帧重复设提示。
            bool canPlaceNow = InputModeState.AllowsBuilding;

            if (canPlaceNow != _placementActive)
            {
                _placementActive = canPlaceNow;

                if (canPlaceNow)
                {
                    SetStatus("继续建造");
                }
                else
                {
                    // 被别的界面接管（通常是打开了背包）：把预览收起来，
                    // 否则会留下一个跟着鼠标跑、但点了没反应的幽灵方块。
                    HideGhost();
                    SetStatus("建造已暂停（请关闭界面继续）");
                }
            }

            if (!canPlaceNow) return;

            UpdateGhost();
            HandlePlacementInput();
        }

        /// <summary>放置目前是否生效（受全局输入模式控制）。</summary>
        private bool _placementActive;

        /// <summary>
        /// B 键切换建造模式。
        ///
        /// 【约定：退出键必须永远有效】这里【不】检查 InputModeState ——
        /// 否则一旦别的系统把模式设成 Inventory，玩家就退不出建造了。
        /// 只有"背包开着的时候"不允许进入（避免两个界面抢鼠标）。
        /// </summary>
        private void HandleModeToggle()
        {
            if (_input == null) return;

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;
            if (!keyboard.bKey.wasPressedThisFrame) return;

            if (_building)
            {
                ExitBuilding();
                return;
            }

            if (InputModeState.Current == InputMode.Inventory)
            {
                SetStatus("请先关闭背包");
                return;
            }

            EnterBuilding();
        }

        public void EnterBuilding()
        {
            _building = true;
            _placementActive = false;   // 交给 Update 里的状态比较去首次显示预览
            InputModeState.Current = InputMode.Building;
            SetStatus("建造模式：左键放置，右键拆除，1/2 切换部件，B 退出。墙要先有地板才能立");
        }

        public void ExitBuilding()
        {
            _building = false;
            _placementActive = false;
            InputModeState.Reset();
            DestroyGhost();
            SetStatus("已退出建造模式");
        }

        /// <summary>
        /// 被外部强制中断建造（例如在建造模式里打开了背包，又在背包里按了 B 退出）。
        ///
        /// 【为什么需要这个】没有它就会出现"反向分家"：
        /// 玩家在背包里按 B → 建造控制器把模式设成 Normal（退出建造），
        /// 但背包关闭时会恢复"打开背包之前的模式"（= Building）→
        /// 结果是模式说在建造、控制器说没在建造，界面卡住。
        ///
        /// 所以由 <see cref="Game.Player.InventoryScreen"/> 在"背包打开时按了 B"的情况下调用它，
        /// 让控制器把内部状态也一起收干净。
        /// </summary>
        public void ForceExitBuilding()
        {
            if (!_building) return;

            _building = false;
            _placementActive = false;
            DestroyGhost();
        }

        private void HandlePartSelection()
        {
            if (_parts == null || _parts.Count == 0) return;

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            // 数字键 1..9 选择部件
            for (int i = 0; i < Mathf.Min(9, _parts.Count); i++)
            {
                var key = i switch
                {
                    0 => keyboard.digit1Key,
                    1 => keyboard.digit2Key,
                    2 => keyboard.digit3Key,
                    3 => keyboard.digit4Key,
                    4 => keyboard.digit5Key,
                    5 => keyboard.digit6Key,
                    6 => keyboard.digit7Key,
                    7 => keyboard.digit8Key,
                    _ => keyboard.digit9Key,
                };

                if (key.wasPressedThisFrame)
                {
                    _selectedIndex = i;
                    DestroyGhost();
                    SetStatus($"已选择：{_parts[i].DisplayName}");
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- 预览

        /// <summary>
        /// 算出鼠标当前指向哪个位置，并把它统一成"一个落点 + 是不是边"。
        ///
        /// 【为什么统一成这一种返回形式】放置、拆除、预览三个地方都需要这个结果。
        /// 早先它们各写了一遍解析逻辑，改一处忘一处就会不一致
        /// （预览说能放、点击说不能放）。现在只有这一个入口。
        ///
        /// 返回 false 表示指针不在建造区上、或建造区/相机不可用。
        /// </summary>
        private bool TryResolveTarget(out Vector3 groundPosition, out bool isEdge,
            out BuildEdge edge, out BuildAreaData.BuildCell cell)
        {
            groundPosition = Vector3.zero;
            isEdge = false;
            edge = default;
            cell = default;

            Camera camera = Camera.main;
            if (camera == null || _area == null || _input == null) return false;
            if (!_input.CurrentIntent.HasAimScreenPosition) return false;

            BuildablePart part = SelectedPart;
            if (part == null) return false;

            Ray ray = camera.ScreenPointToRay(_input.CurrentIntent.AimScreenPosition);

            // 与建造区的水平面求交。不用 Physics.Raycast 打地面 —— 见 BuildArea.GroundPlane 的说明：
            // 地面可能被湖挖了洞、也可能有坡，用纯数学平面求交答案才确定。
            Plane plane = _area.GroundPlane;
            if (!plane.Raycast(ray, out float enter)) return false;

            Vector3 world = ray.GetPoint(enter);

            if (part.IsEdgePart)
            {
                if (!_area.WorldToNearestEdge(world, out edge)) return false;

                isEdge = true;
                groundPosition = _area.EdgeCenterWorld(edge);
                return true;
            }

            if (!_area.WorldToCell(world, out int x, out int z)) return false;

            cell = new BuildAreaData.BuildCell(x, z, _area.Floor);
            groundPosition = _area.CellCenterWorld(x, z);
            return true;
        }

        private void UpdateGhost()
        {
            BuildablePart part = SelectedPart;
            if (part == null || _area == null) { DestroyGhost(); return; }

            if (!TryResolveTarget(out Vector3 position, out bool isEdge, out BuildEdge edge,
                    out BuildAreaData.BuildCell cell))
            {
                HideGhost();
                return;
            }

            EnsureGhost(part, isEdge);

            if (_ghost == null) return;

            _ghost.SetActive(true);

            // 尺寸：地板是薄板铺满一格；墙是薄板立起来
            if (isEdge)
            {
                bool alongX = edge.Axis == EdgeAxis.AlongX;
                float length = _area.CellSize;
                _ghost.transform.localScale = alongX
                    ? new Vector3(length, part.Height, part.Thickness)
                    : new Vector3(part.Thickness, part.Height, length);
            }
            else
            {
                _ghost.transform.localScale = new Vector3(
                    _area.CellSize * 0.96f, Mathf.Max(0.05f, part.Height), _area.CellSize * 0.96f);
            }

            // 预览方块的中心在"半个高度"处 —— 和实际生成的部件保持一致
            _ghost.transform.position = position + Vector3.up * (_ghost.transform.localScale.y * 0.5f);

            // 颜色表示合法性
            bool ok = isEdge ? CheckPlacement(edge).Ok : CheckPlacement(cell).Ok;

            if (_ghostMaterial != null)
            {
                Color color = ok ? _validColor : _invalidColor;
                if (_ghostMaterial.HasProperty("_BaseColor")) _ghostMaterial.SetColor("_BaseColor", color);
                if (_ghostMaterial.HasProperty("_Color")) _ghostMaterial.SetColor("_Color", color);
            }
        }

        private void EnsureGhost(BuildablePart part, bool isEdge)
        {
            if (_ghost != null && _ghostIsEdge == isEdge) return;

            DestroyGhost();

            _ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _ghost.name = "建造预览";
            _ghost.transform.SetParent(null, false);

            // 【必须删掉碰撞体】否则预览方块会挡住鼠标射线、也会让玩家站在上面。
            // 本工程在"预览装置"上踩过同类问题（多余碰撞体变成隐形墙）。
            Collider collider = _ghost.GetComponent<Collider>();
            if (collider != null) DestroyImmediate(collider);

            // 【必须不带 WorldObjectId】它不是真的世界物体，
            // 带了会被场景校验当成重复 ID、也会污染存档。
            WorldObjectId id = _ghost.GetComponent<WorldObjectId>();
            if (id != null) DestroyImmediate(id);

            _ghostMaterial = CreateGhostMaterial();
            Renderer renderer = _ghost.GetComponent<Renderer>();
            if (renderer != null && _ghostMaterial != null) renderer.sharedMaterial = _ghostMaterial;

            _ghostIsEdge = isEdge;
        }

        private bool _ghostIsEdge;

        private Material CreateGhostMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            if (shader == null)
            {
                Debug.LogWarning("[建造] 找不到可用的预览 shader，预览会是不透明的。", this);
                return null;
            }

            Material material = new Material(shader) { name = "M_BuildGhost_Runtime" };
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", _validColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", _validColor);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            return material;
        }

        private void HideGhost()
        {
            if (_ghost != null) _ghost.SetActive(false);
        }

        private void DestroyGhost()
        {
            if (_ghost != null)
            {
                DestroyImmediate(_ghost);
                _ghost = null;
            }

            if (_ghostMaterial != null)
            {
                DestroyImmediate(_ghostMaterial);
                _ghostMaterial = null;
            }
        }

        // ---------------------------------------------------------------- 判定

        /// <summary>
        /// 地板能否放在这个格子。包含【数据层】和【场景层】两部分检查。
        /// </summary>
        private PlaceCheck CheckPlacement(BuildAreaData.BuildCell cell)
        {
            BuildablePart part = SelectedPart;
            if (part == null) return PlaceCheck.Reject(PlaceRejectReason.NotConfigured, "没有选中部件");

            BuildAreaData data = _area.Data;

            // 1. 数据层：范围 + 是否已被占
            PlaceCheck basic = data.CanPlaceFloor(cell.X, cell.Z);
            if (!basic.Ok) return basic;

            // 2. 场景层：有没有被树/石头/房屋挡住
            PlaceCheck blocked = CheckWorldBlocked(_area.CellCenterWorld(cell.X, cell.Z),
                new Vector3(_area.CellSize * 0.9f, 1.2f, _area.CellSize * 0.9f));
            if (!blocked.Ok) return blocked;

            // 3. 支撑：地板下面必须有东西，不能悬空
            PlaceCheck support = CheckSupport(cell);
            if (!support.Ok) return support;

            // 4. 材料
            return CheckMaterials(part);
        }

        /// <summary>墙能否放在这条边上。</summary>
        private PlaceCheck CheckPlacement(BuildEdge edge)
        {
            BuildablePart part = SelectedPart;
            if (part == null) return PlaceCheck.Reject(PlaceRejectReason.NotConfigured, "没有选中部件");

            PlaceCheck basic = _area.Data.CanPlaceWall(edge);
            if (!basic.Ok) return basic;

            // 墙的碰撞盒：沿墙方向一整格长，垂直于墙方向只占厚度
            bool alongX = edge.Axis == EdgeAxis.AlongX;
            Vector3 size = alongX
                ? new Vector3(_area.CellSize * 0.9f, 2.4f, 0.2f)
                : new Vector3(0.2f, 2.4f, _area.CellSize * 0.9f);

            PlaceCheck blocked = CheckWorldBlocked(
                _area.EdgeCenterWorld(edge) + Vector3.up * 1.2f, size);
            if (!blocked.Ok) return blocked;

            return CheckMaterials(part);
        }

        /// <summary>
        /// 场景里有没有别的东西占着这块空间。
        ///
        /// 【为什么必须做这一步】数据层只知道"这个格子在我自己的记录里空不空"，
        /// 它看不见树、石头、编辑器生成的房屋。玩家完全可以把墙建到树里面去。
        ///
        /// 【做法】用 OverlapBox 查一个略小于格子的盒子：
        ///   略小是为了不误判"贴着隔壁的方块"（相邻部件本来就会挨着）。
        /// </summary>
        private PlaceCheck CheckWorldBlocked(Vector3 center, Vector3 size)
        {
            Collider[] hits = Physics.OverlapBox(center + Vector3.up * 0.4f, size * 0.5f,
                Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;

                // 玩家自己不算阻挡（否则站在格子上就建不了）
                if (hit.transform.IsChildOf(transform.root)) continue;

                // 已经放置的玩家建造物不算阻挡 —— 相邻部件本来就贴着
                if (_spawnRoot != null && hit.transform.IsChildOf(_spawnRoot)) continue;

                // 地形不算阻挡（地面本来就在下面）
                if (hit.GetComponent<MeshCollider>() != null &&
                    hit.gameObject.name.StartsWith("地面"))
                {
                    continue;
                }

                if (_logPlacement)
                {
                    Debug.Log($"[建造] 被「{hit.gameObject.name}」挡住（层 {LayerMask.LayerToName(hit.gameObject.layer)}）");
                }

                return PlaceCheck.Reject(PlaceRejectReason.BlockedByWorld, $"被「{hit.gameObject.name}」挡住");
            }

            return PlaceCheck.Allowed;
        }

        /// <summary>
        /// 地板下面是否有支撑。
        ///
        /// 【规则】向下打一条射线：
        ///   - 打到地形 → 有支撑
        ///   - 打到玩家已建的地板 → 有支撑
        ///   - 打到别的东西 → 也算有支撑（例如踩在集装箱上）
        ///   - 什么都没打到 → 悬空，拒绝
        ///
        /// 【为什么不能省掉这条】不检查的话玩家可以在半空中铺地板，
        /// 铺出一座"空中楼阁"—— 那不是这个阶段要验证的东西。
        /// </summary>
        private PlaceCheck CheckSupport(BuildAreaData.BuildCell cell)
        {
            Vector3 center = _area.CellCenterWorld(cell.X, cell.Z);

            // 从格子上方一点往下打，避免起点就在地形内部
            Vector3 origin = center + Vector3.up * 0.5f;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1.5f, ~0,
                    QueryTriggerInteraction.Ignore))
            {
                _ = hit;
                return PlaceCheck.Allowed;
            }

            return PlaceCheck.Reject(PlaceRejectReason.NoSupport, "地板悬空，下面没有支撑");
        }

        /// <summary>材料够不够。</summary>
        private PlaceCheck CheckMaterials(BuildablePart part)
        {
            Inventory inventory = ResolveInventory();

            if (inventory == null)
                return PlaceCheck.Reject(PlaceRejectReason.NotConfigured, "没有背包");

            if (part.Material == null)
                return PlaceCheck.Reject(PlaceRejectReason.NotConfigured, $"「{part.DisplayName}」没有配置材料");

            int have = inventory.CountOf(part.Material);
            if (have < part.MaterialCost)
            {
                return PlaceCheck.Reject(PlaceRejectReason.NotEnoughMaterial,
                    $"还差 {part.MaterialCost - have} 个{part.Material.DisplayName}");
            }

            return PlaceCheck.Allowed;
        }

        private Inventory ResolveInventory()
        {
            if (_inventoryOwner == null) return null;
            return _inventoryOwner.Backpack;
        }

        // ---------------------------------------------------------------- 放置 / 拆除

        private void HandlePlacementInput()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;

            if (mouse.leftButton.wasPressedThisFrame) TryPlace();
            else if (mouse.rightButton.wasPressedThisFrame) TryRemove();
        }

        /// <summary>放置当前选中的部件。</summary>
        public bool TryPlace()
        {
            BuildablePart part = SelectedPart;
            if (part == null) return false;

            if (!TryResolveTarget(out Vector3 position, out bool isEdge, out BuildEdge edge,
                    out BuildAreaData.BuildCell cell))
            {
                SetStatus("指针不在建造区内");
                return false;
            }

            // 1. 全部合法性检查
            PlaceCheck check = isEdge ? CheckPlacement(edge) : CheckPlacement(cell);
            if (!check.Ok)
            {
                SetStatus($"不能放置：{Describe(check)}");
                if (_logPlacement) Debug.Log($"[建造] 拒绝：{check}");
                return false;
            }

            // 2. 扣材料
            Inventory inventory = ResolveInventory();
            if (inventory == null || !inventory.Remove(part.Material, part.MaterialCost))
            {
                SetStatus("扣材料失败");
                return false;
            }

            // 3. 生成物体
            GameObject created = CreatePartObject(part, position, isEdge, edge, cell);
            if (created == null)
            {
                // 【回滚】不能留下"材料扣了但东西没生成"的状态
                int refunded = inventory.Add(part.Material, part.MaterialCost);
                SetStatus(refunded == part.MaterialCost
                    ? "生成失败，材料已退回"
                    : $"生成失败，材料退回不完整（退回 {refunded}/{part.MaterialCost}）");
                Debug.LogError($"[建造] 「{part.DisplayName}」生成失败，已尝试退回材料。", this);
                return false;
            }

            // 4. 登记
            bool registered = isEdge
                ? _area.Data.RegisterWall(edge, part.PartId)
                : _area.Data.RegisterFloor(cell.X, cell.Z, part.PartId);

            if (!registered)
            {
                // 数据层拒绝（理论上不会走到，因为前面查过）—— 一样要回滚，避免不一致
                DestroyImmediate(created);
                inventory.Add(part.Material, part.MaterialCost);
                SetStatus("登记失败，已回滚");
                Debug.LogError("[建造] 生成成功但登记失败，已回滚。这是数据层与检查逻辑不一致的信号。", this);
                return false;
            }

            // 5. 记录物体引用（拆除时要用）
            if (isEdge) _wallObjects[edge] = created;
            else _floorObjects[cell] = created;

            SetStatus($"已放置 {part.DisplayName}");
            return true;
        }

        /// <summary>拆除鼠标指向的部件。</summary>
        public bool TryRemove()
        {
            if (!TryResolveTarget(out _, out bool isEdge, out BuildEdge edge,
                    out BuildAreaData.BuildCell cell))
            {
                SetStatus("指针不在建造区内");
                return false;
            }

            return isEdge ? RemoveWall(edge) : RemoveFloor(cell);
        }

        private bool RemoveFloor(BuildAreaData.BuildCell cell)
        {
            if (!_area.Data.HasFloor(cell.X, cell.Z))
            {
                SetStatus("这里没有可拆的地板");
                return false;
            }

            // 【业务规则 1】地板上挂着墙就不让拆 —— 否则会留下悬空的墙。
            List<BuildEdge> walls = _area.Data.GetWallsAroundCell(cell.X, cell.Z);
            if (walls.Count > 0)
            {
                SetStatus($"请先拆掉这格上的 {walls.Count} 面墙");
                return false;
            }

            // 【业务规则 2】拆掉后会不会让【隔壁】的墙失去地板支撑？
            //
            // 这是"墙必须贴着地板"这条规则带出来的新情况：
            // 一格地板可能同时给好几面墙当支撑，其中有些墙挂着【相邻的格子】上，
            // 上面那条规则查不到它们。如果直接拆，那些墙就变成悬空墙 ——
            // 而按新规则它们本来是不该存在的。
            List<BuildEdge> orphaned = FindWallsThatWouldBeOrphaned(cell.X, cell.Z);
            if (orphaned.Count > 0)
            {
                SetStatus($"拆掉这块地板会让 {orphaned.Count} 面墙悬空，请先拆那些墙");
                return false;
            }

            BuildablePart part = FindPartById(_area.Data.GetFloorId(cell.X, cell.Z)) ?? SelectedPart;

            // 先试返还，背包满则拒绝拆除（不丢东西）
            if (!TryRefund(part)) return false;

            _area.Data.RemoveFloor(cell.X, cell.Z);

            if (_floorObjects.TryGetValue(cell, out GameObject go))
            {
                _floorObjects.Remove(cell);
                if (go != null) DestroyImmediate(go);
            }

            SetStatus("已拆除地板");
            return true;
        }

        /// <summary>
        /// 假设拆掉 (cellX, cellZ) 这块地板，找出会因此【失去所有地板支撑】的墙。
        ///
        /// 【做法】不修改数据，而是"装作它已经没了"再逐条边判断：
        /// 对每一面已放置的墙，检查它两侧的格子（排除即将被拆的这一格）
        /// 还有没有地板。都没有 → 这面墙会悬空。
        ///
        /// 【为什么不真的先拆再查再回滚】那样中途会有一段"数据不一致"的窗口，
        /// 万一中间抛异常就留下脏状态。纯查询更安全。
        /// </summary>
        private List<BuildEdge> FindWallsThatWouldBeOrphaned(int cellX, int cellZ)
        {
            var orphaned = new List<BuildEdge>();

            // 只有这一格【四周】的墙可能受影响（更远的墙不挨着它）
            var directions = new[]
            {
                CellDirection.NegX, CellDirection.PosX, CellDirection.NegZ, CellDirection.PosZ
            };

            foreach (CellDirection direction in directions)
            {
                BuildEdge edge = BuildEdge.FromCell(cellX, cellZ, _area.Floor, direction);
                if (!_area.Data.HasWall(edge)) continue;

                if (WouldEdgeLoseAllFloors(edge, cellX, cellZ)) orphaned.Add(edge);
            }

            return orphaned;
        }

        /// <summary>假设 (removedX, removedZ) 不再是地板，这条边两侧还有没有地板。</summary>
        private bool WouldEdgeLoseAllFloors(BuildEdge edge, int removedX, int removedZ)
        {
            edge.GetLowSideCell(out int lowX, out int lowZ);
            edge.GetHighSideCell(out int highX, out int highZ);

            bool lowHasFloor = !(lowX == removedX && lowZ == removedZ) && _area.Data.HasFloor(lowX, lowZ);
            bool highHasFloor = !(highX == removedX && highZ == removedZ) && _area.Data.HasFloor(highX, highZ);

            return !lowHasFloor && !highHasFloor;
        }

        private bool RemoveWall(BuildEdge edge)
        {
            if (!_area.Data.HasWall(edge))
            {
                SetStatus("这里没有可拆的墙");
                return false;
            }

            BuildablePart part = FindPartById(_area.Data.GetWallId(edge)) ?? SelectedPart;

            if (!TryRefund(part)) return false;

            _area.Data.RemoveWall(edge);

            if (_wallObjects.TryGetValue(edge, out GameObject go))
            {
                _wallObjects.Remove(edge);
                if (go != null) DestroyImmediate(go);
            }

            SetStatus("已拆除墙");
            return true;
        }

        /// <summary>
        /// 把材料退回背包。背包放不下时【拒绝】这次拆除并返回 false，
        /// 而不是把材料凭空丢掉（那样玩家会觉得"东西莫名少了"）。
        /// </summary>
        private bool TryRefund(BuildablePart part)
        {
            if (part == null || part.Material == null || part.MaterialCost <= 0) return true;

            Inventory inventory = ResolveInventory();
            if (inventory == null) return true;

            int added = inventory.Add(part.Material, part.MaterialCost);
            if (added < part.MaterialCost)
            {
                // 放不下的部分退回去（Add 已经放进去的先扣回来，保持原样）
                if (added > 0) inventory.Remove(part.Material, added);

                SetStatus("背包放不下返还的材料，无法拆除");
                return false;
            }

            return true;
        }

        private BuildablePart FindPartById(string partId)
        {
            if (string.IsNullOrEmpty(partId) || _parts == null) return null;

            for (int i = 0; i < _parts.Count; i++)
            {
                if (_parts[i] != null && _parts[i].PartId == partId) return _parts[i];
            }

            return null;
        }

        /// <summary>
        /// 生成实际的部件方块。
        ///
        /// 【和预览的区别】真的部件有碰撞体、有 WorldObjectId。
        /// </summary>
        private GameObject CreatePartObject(BuildablePart part, Vector3 groundPosition,
            bool isEdge, BuildEdge edge, BuildAreaData.BuildCell cell)
        {
            try
            {
                Vector3 center;
                Vector3 size;
                string objectId;

                if (isEdge)
                {
                    bool alongX = edge.Axis == EdgeAxis.AlongX;
                    size = alongX
                        ? new Vector3(_area.CellSize, part.Height, part.Thickness)
                        : new Vector3(part.Thickness, part.Height, _area.CellSize);
                    center = groundPosition + Vector3.up * (part.Height * 0.5f);

                    // 墙的 ID 用边的编码 —— 它本身就是唯一的
                    objectId = $"player/{_area.Floor}/wall/{edge.MidX2}_{edge.MidZ2}";
                }
                else
                {
                    size = new Vector3(_area.CellSize, Mathf.Max(0.05f, part.Height), _area.CellSize);
                    center = groundPosition + Vector3.up * (size.y * 0.5f);

                    // 【地板的 ID 必须用【格子】坐标，不能用 edge】
                    // 早先这里误用了 edge（地板分支里 edge 是 default），
                    // 结果是所有地板都拿到同一个 ID —— 场景校验会因为"重复 WorldObjectId"报错。
                    objectId = $"player/{_area.Floor}/floor/{cell.X}_{cell.Z}";
                }

                // 【材质必须用已配置好的资产，不能调 EnsurePalette()】
                //
                // 早先这里调用 EnsurePalette()，它会：
                //   1. 重新写入【所有】环境材质的颜色（地面、树叶、建筑……）
                //   2. 调用 AssetDatabase.SaveAssets()
                // 后果是：每放一个方块就写一次盘（卡顿），
                // 而且会把手工调过的材质配色永久改回默认值。
                // 打包后那条路径还返回 null，编辑器和游戏表现不一致。
                //
                // 现在只在 Awake 取一次引用，之后复用。
                Material material = _partMaterial != null
                    ? _partMaterial
                    : Game.World.GreyboxPrimitives.GetOrCreateMaterial(
                        Game.World.GreyboxPrimitives.StructureMaterialPath,
                        new Color(0.52f, 0.52f, 0.55f));

                GameObject created = Game.World.GreyboxPrimitives.CreateBlock(
                    $"玩家{part.DisplayName}", _spawnRoot, center, size,
                    material, "player_build", objectId);

                return created;
            }
            catch (System.Exception exception)
            {
                Debug.LogError($"[建造] 生成「{part.DisplayName}」时异常：{exception.Message}", this);
                return null;
            }
        }

        // ---------------------------------------------------------------- 提示

        private void SetStatus(string message)
        {
            _statusMessage = message;
            _statusUntil = Time.time + 2.5f;
        }

        private static string Describe(PlaceCheck check)
        {
            switch (check.Reason)
            {
                case PlaceRejectReason.OutOfBounds: return "超出建造区";
                case PlaceRejectReason.CellOccupied: return "这格已经有地板";
                case PlaceRejectReason.EdgeOccupied: return "这条边已经有墙";
                case PlaceRejectReason.NoAdjacentFloor: return check.Detail ?? "两侧都没有地板，先铺地板";
                case PlaceRejectReason.NoSupport: return check.Detail ?? "下面是空的";
                case PlaceRejectReason.NotEnoughMaterial: return check.Detail ?? "材料不足";
                case PlaceRejectReason.BlockedByWorld: return check.Detail ?? "被挡住了";
                case PlaceRejectReason.NotConfigured: return check.Detail ?? "部件未配置";
                default: return check.Detail ?? "不可放置";
            }
        }

        private void OnGUI()
        {
            if (!_showHud || !_building) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            EnsureStyles();

            var rect = new Rect(16f, Screen.height - 150f, 560f, 130f);
            GUI.color = Color.white;

            BuildablePart part = SelectedPart;
            Inventory inventory = ResolveInventory();

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("【建造模式】");

            if (part != null)
            {
                int have = inventory != null && part.Material != null ? inventory.CountOf(part.Material) : 0;
                string materialName = part.Material != null ? part.Material.DisplayName : "（未配置）";
                builder.AppendLine($"当前：{part.DisplayName}　消耗 {part.MaterialCost} {materialName}（持有 {have}）");
            }

            // 列出所有部件和对应数字键
            if (_parts != null && _parts.Count > 1)
            {
                var line = new System.Text.StringBuilder("切换：");
                for (int i = 0; i < Mathf.Min(9, _parts.Count); i++)
                {
                    if (i > 0) line.Append("　");
                    line.Append($"[{i + 1}] {_parts[i].DisplayName}");
                    if (i == _selectedIndex) line.Append("←");
                }
                builder.AppendLine(line.ToString());
            }

            builder.AppendLine("左键放置　右键拆除　B 退出　（墙必须贴着已铺好的地板）");

            if (Time.time < _statusUntil && !string.IsNullOrEmpty(_statusMessage))
            {
                builder.Append(_statusMessage);
            }

            GUI.Label(rect, builder.ToString(), _hudStyle);
        }

        private void EnsureStyles()
        {
            if (_hudStyle != null) return;

            _hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                alignment = TextAnchor.LowerLeft,
                wordWrap = false,
            };
            _hudStyle.normal.textColor = Color.white;

            _warnStyle = new GUIStyle(_hudStyle);
            _warnStyle.normal.textColor = new Color(1f, 0.7f, 0.4f);
        }

#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器配置。</summary>
        public void ConfigureController(BuildArea area, PlayerInputSource input,
            PlayerInventory inventory, List<BuildablePart> parts)
        {
            _area = area;
            _input = input;
            _inventoryOwner = inventory;
            _parts = parts;
        }
#endif
    }
}
