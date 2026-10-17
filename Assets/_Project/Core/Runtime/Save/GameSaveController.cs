// 本文件开启可空引用类型分析（和 Health.cs / GridConnectivity.cs 一致）。
#nullable enable

using System.Collections.Generic;
using Game.Gameplay.Building;
using Game.Items;
using Game.Player;
using Game.World;
using UnityEngine;

namespace Game.Save
{
    /// <summary>
    /// 存档的"采集与还原"：把场景里的状态收进 <see cref="GameSaveData"/>，再从它摆回去。
    ///
    /// 【这一层的职责边界】
    ///   - 它负责：读场景对象的状态、按数据重新生成建造物、把玩家摆回原位
    ///   - 它不负责：读写文件（在 SaveSerializer）、占用规则（在 BuildAreaData）
    ///
    /// 【为什么加载时要"先清空再重建"】
    /// 场景里可能已经有玩家这次会话里建的东西。如果只往上叠加，
    /// 加载后会变成"旧存档 + 本次会话"的混合体 —— 那不是加载，是合并。
    /// 所以加载前先把玩家建造物全部清掉，再按存档重建。
    ///
    /// 【按键】F5 保存、F9 读取。用键盘直接读（本工程禁用了旧 Input）。
    /// </summary>
    [DisallowMultipleComponent]
    public class GameSaveController : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("留空则在场景里自动找。")]
        [SerializeField] private PlayerInventory? _inventory;

        [SerializeField] private BuildController? _builder;
        [SerializeField] private PlayerHealth? _health;

        [Header("按键")]
        [SerializeField] private bool _enableHotkeys = true;

        [Tooltip("保存快捷键。")]
        [SerializeField] private KeyCode _saveKey = KeyCode.F5;

        [Tooltip("读取快捷键。")]
        [SerializeField] private KeyCode _loadKey = KeyCode.F9;

        [Tooltip("删除存档快捷键。")]
        [SerializeField] private KeyCode _deleteKey = KeyCode.F10;

        [Header("行为")]
        [Tooltip("加载时把玩家摆回存档里的位置。")]
        [SerializeField] private bool _restorePlayerPose = true;

        [Tooltip("加载时恢复背包内容。")]
        [SerializeField] private bool _restoreInventory = true;

        [Tooltip("在屏幕上显示存档提示。")]
        [SerializeField] private bool _showHud = true;

        [Tooltip("版本不一致时是否强行读取。默认关闭 —— 结构可能已经变了，" +
                 "硬读会写进错误的状态。只在明确知道改了什么时打开。")]
        [SerializeField] private bool _allowVersionMismatch;

        private string _statusMessage = string.Empty;
        private float _statusUntil;
        private GUIStyle? _hudStyle;
        private CharacterController? _controller;

        // KeyCode 是旧输入枚举，但这里只用它当"键位配置"的数据类型，
        // 真正的按键检测走 Input System 的 Keyboard（见 IsPressedThisFrame）。
        private void Awake()
        {
            EnsureReferences();
        }

        /// <summary>
        /// 解析引用（自己身上找不到就去场景里找）。
        ///
        /// 【为什么不能只在 Awake 里做 —— 这是一个真实的缺陷修复】
        /// 编辑器工具、诊断菜单会直接调用 Capture()/Load()，
        /// 而【编辑模式下 Awake 根本不会跑】。于是 _inventory 是 null，
        /// CaptureInventory(null) 返回空列表 —— 采集出来的存档里【一件物品都没有】，
        /// 而且没有任何报错（null 被静默当成"空容器"）。
        ///
        /// 自动化测试第一次跑就撞上了这个：测试往背包放了 17 木材，
        /// 采集结果却是 0 种物品。
        ///
        /// 现在改成：每次需要时都确保引用可用（幂等，开销只是几次 null 判断）。
        /// </summary>
        private void EnsureReferences()
        {
            if (_inventory == null) _inventory = GetComponent<PlayerInventory>();
            if (_builder == null) _builder = GetComponent<BuildController>();
            if (_health == null) _health = GetComponent<PlayerHealth>();
            if (_controller == null) _controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            if (!_enableHotkeys) return;
            if (!Game.Player.InputModeState.AllowsMovement) return;   // 界面打开时不响应

            if (IsPressedThisFrame(_saveKey))
            {
                Save(out string message);
                SetStatus(message);
                return;
            }

            if (IsPressedThisFrame(_loadKey))
            {
                Load(out string message);
                SetStatus(message);
                return;
            }

            if (IsPressedThisFrame(_deleteKey))
            {
                SaveSerializer.TryDelete(out string message);
                SetStatus(message);
            }
        }

        /// <summary>
        /// 本帧是否按了某个键。
        ///
        /// 【为什么不用 UnityEngine.Input.GetKeyDown】本工程的 activeInputHandler 是
        /// Input System 包，用旧的 UnityEngine.Input 会直接抛 InvalidOperationException
        /// （本工程踩过）。所以这里把 KeyCode 映射到 Input System 的键再查。
        /// </summary>
        private static bool IsPressedThisFrame(KeyCode key)
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return false;

            switch (key)
            {
                case KeyCode.F5: return keyboard.f5Key.wasPressedThisFrame;
                case KeyCode.F9: return keyboard.f9Key.wasPressedThisFrame;
                case KeyCode.F10: return keyboard.f10Key.wasPressedThisFrame;
                case KeyCode.F1: return keyboard.f1Key.wasPressedThisFrame;
                case KeyCode.F2: return keyboard.f2Key.wasPressedThisFrame;
                case KeyCode.F3: return keyboard.f3Key.wasPressedThisFrame;
                case KeyCode.F4: return keyboard.f4Key.wasPressedThisFrame;
                default: return false;
            }
        }

        // ---------------------------------------------------------------- 保存

        /// <summary>
        /// 把当前状态写进存档文件。
        ///
        /// 【报出来的"建造物 N 个"是什么口径】
        /// 是"落在当前场景里存在的建造区里的记录条数" —— 和 Load 报的
        /// "恢复 N 个"使用同一口径，所以【保存和加载的数字可以直接对比】。
        ///
        /// 早先这里报的是原始记录总条数，而 Load 报的是实际恢复数，
        /// 于是同一个存档出现"保存 18 → 恢复 17"，看起来像丢了一个（排查时误导过）。
        /// 两个数字口径不同是没法对比的。
        /// </summary>
        public bool Save(out string message)
        {
            GameSaveData data = Capture();
            bool ok = SaveSerializer.TryWrite(data, out string writeMessage);
            message = writeMessage;

            if (ok)
            {
                int relevantParts = CountPartsInExistingAreas(data.PlacedParts);

                message = $"已保存：背包 {data.Backpack.Count} 种、" +
                          $"建造物 {relevantParts} 个、" +
                          $"位置 ({data.Player.X:F1}, {data.Player.Z:F1})";
                Debug.Log($"[存档] {message}\n  {writeMessage}");

                // 【保险】正常情况下两者相等（采集时就是按场景里的建造区遍历的）。
                // 不相等说明有记录指向了场景里不存在的建造区 —— 那会导致
                // "保存 18、恢复 17"这种看起来像丢东西的日志。
                // 与其让数字自己变得可疑，不如直接说清楚。
                if (data.PlacedParts.Count != relevantParts)
                {
                    Debug.LogWarning(
                        $"[存档] 有 {data.PlacedParts.Count - relevantParts} 条建造物记录" +
                        "指向了当前场景里【不存在】的建造区，加载时会被跳过。" +
                        "通常是改过建造区的 AreaId，或者从一个场景存、在另一个场景读。");
                }
            }
            else
            {
                Debug.LogError($"[存档] {writeMessage}");
            }

            return ok;
        }

        /// <summary>
        /// 数一数有多少条建造物记录落在"当前场景里存在的建造区"上。
        ///
        /// 【和 Load 口径一致】Load 只恢复它能找到的建造区里的东西，
        /// 所以保存时也该只数这些 —— 否则两个数字没法对比。
        /// </summary>
        private static int CountPartsInExistingAreas(List<PlacedPartSaveData>? parts)
        {
            if (parts == null || parts.Count == 0) return 0;

            BuildArea[] areas = Object.FindObjectsByType<BuildArea>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var areaIds = new HashSet<string>();
            foreach (BuildArea area in areas)
            {
                if (area != null) areaIds.Add(area.AreaId);
            }

            int count = 0;
            foreach (PlacedPartSaveData part in parts)
            {
                if (part != null && areaIds.Contains(part.AreaId)) count++;
            }

            return count;
        }

        /// <summary>把当前场景状态采集进一个存档数据对象。测试也用它。</summary>
        public GameSaveData Capture()
        {
            EnsureReferences();

            var data = new GameSaveData
            {
                MapId = ResolveMapId(),
                Player = CapturePlayer(),
                Backpack = CaptureInventory(_inventory != null ? _inventory.Backpack : null),
                Pocket = CaptureInventory(_inventory != null ? _inventory.Pocket : null),
                PlacedParts = CapturePlacedParts(),
            };

            return data;
        }

        private PlayerSaveData CapturePlayer()
        {
            var player = new PlayerSaveData
            {
                X = transform.position.x,
                Y = transform.position.y,
                Z = transform.position.z,
                Yaw = transform.eulerAngles.y,
                Health = _health != null ? _health.Current : 0f,
            };

            return player;
        }

        private static string ResolveMapId()
        {
            AreaBounds? bounds = Object.FindFirstObjectByType<AreaBounds>();
            return bounds != null ? bounds.MapId : string.Empty;
        }

        /// <summary>
        /// 采集一个容器的内容。
        ///
        /// 【为什么存 ItemId 而不是资产引用】资产路径/引用会随工程结构变化，
        /// 而 ItemId 是刻意设计的稳定标识（ItemDefinition 里写着"存档只认这个"）。
        /// </summary>
        private static List<ItemSaveData> CaptureInventory(Inventory? inventory)
        {
            var result = new List<ItemSaveData>();
            if (inventory == null) return result;

            IReadOnlyList<ItemStack> slots = inventory.SlotsView;

            for (int i = 0; i < slots.Count; i++)
            {
                ItemStack slot = slots[i];
                if (slot.IsEmpty || slot.Item == null) continue;

                result.Add(new ItemSaveData(slot.Item.ItemId, slot.Amount));
            }

            return result;
        }

        /// <summary>
        /// 采集所有建造物。
        ///
        /// 【为什么从 BuildAreaData 导出，而不是遍历场景物体】
        /// BuildAreaData 才是"哪个格/哪条边被占了"的唯一权威。
        /// 遍历场景物体的话，一旦出现"数据里有、场景里没有"的不同步，
        /// 存档就会保存一个和实际占用不符的状态。
        /// （诊断工具 <c>游戏 → 工程 → 诊断建造区状态</c> 就是用来查这种不同步的。）
        ///
        /// 【为什么采集前要先同步预置部件】
        /// BuildAreaData 是【不被 Unity 序列化】的普通 C# 类，
        /// 而"把场景里的 PreplacedPart 登记回数据"这一步写在 BuildController.Awake() 里 ——
        /// **编辑模式下 Awake 不跑**。于是编辑器工具采集到的数据层是空的，
        /// 存出来的档里一个建造物都没有（测试实测：期望 1 个起步地板，实际 0）。
        ///
        /// 这里先同步一次，让数据层反映场景里真实存在的东西，再导出。
        /// </summary>
        private List<PlacedPartSaveData> CapturePlacedParts()
        {
            var result = new List<PlacedPartSaveData>();

            BuildArea[] areas = Object.FindObjectsByType<BuildArea>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            foreach (BuildArea area in areas)
            {
                // 先把场景里的预置部件补登记进数据层（幂等）
                BuildController? builder = FindBuilderForArea(area);
                builder?.SyncPreplacedParts();

                foreach (PlacedPartRecord record in area.Data.Export())
                {
                    var part = new PlacedPartSaveData
                    {
                        AreaId = area.AreaId,
                        PartId = record.PartId,
                        IsWall = record.IsWall,
                        Floor = record.Floor,
                        Yaw = record.IsWall
                            ? (record.Edge.Axis == EdgeAxis.AlongX ? 0f : 90f)
                            : 0f,
                    };

                    if (record.IsWall)
                    {
                        part.EdgeMidX2 = record.Edge.MidX2;
                        part.EdgeMidZ2 = record.Edge.MidZ2;
                    }
                    else
                    {
                        part.CellX = record.CellX;
                        part.CellZ = record.CellZ;
                    }

                    result.Add(part);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------- 加载

        /// <summary>
        /// 从存档文件恢复到场景。
        ///
        /// <paramref name="fileName"/> 默认是正式存档槽。自动化测试会传自己的文件名 ——
        /// 【这个参数是必须的】：早先它写死读默认档，于是测试里"把坏存档写到
        /// test.json 再 Load"实际上 load 的是 save_slot0.json（不存在），
        /// 报"没有找到存档文件"，而测试却以为验证了"预检拒绝"。
        /// 那是一种很隐蔽的假验证 —— 断言通过了，但验证的根本不是它声称的东西。
        /// </summary>
        public bool Load(out string message, string? fileName = null)
        {
            EnsureReferences();

            string effectiveName = fileName ?? SaveSerializer.DefaultFileName;
            GameSaveData? data = SaveSerializer.TryRead(out string readMessage, effectiveName);

            if (data == null)
            {
                message = readMessage;
                Debug.LogWarning($"[存档] {readMessage}");
                return false;
            }

            // 文件层没问题了，但"能不能安全应用"要由预检说了算。
            // Apply 内部会先预检，失败时返回 -1 且【什么都不改】。
            int restored = Apply(data, allowVersionMismatch: _allowVersionMismatch);

            if (restored < 0)
            {
                // 【关键】不能报成功 —— 早先这里无论如何都返回 true，
                // 于是"拒绝加载、状态未变"会被显示成"读取成功"，玩家以为读到了。
                message = "读档失败：存档内容和当前场景对不上（详见 Console）。当前状态未被改动。";
                Debug.LogError($"[存档] {message}");
                return false;
            }

            message = $"已读取：恢复 {restored} 个建造物" +
                      (string.IsNullOrEmpty(readMessage) || readMessage == "读取成功" ? "" : $"（{readMessage}）");

            Debug.Log($"[存档] {message}");
            return true;
        }

        /// <summary>
        /// 把存档数据应用到场景。返回成功恢复的建造物数量；**返回 -1 表示预检失败、什么都没动**。
        ///
        /// 【顺序是刻意设计的：先全部验证，再动任何东西】
        ///
        /// 早先的顺序是"先清空背包与建造物，再逐个恢复，找不到就跳过"。
        /// 那意味着存档里只要有一个引用了不存在的东西（物品改名、建造区没了、
        /// 部件配置改了），**玩家会丢掉当前的背包和建筑，而且还能看到"读取成功"**。
        /// 数据没了、界面还说成功 —— 这是最糟的一类 bug。
        ///
        /// 现在的约定：
        ///   1. 先把所有记录和依赖都查一遍（<see cref="SaveLoadPrecheck"/>）
        ///   2. 有任何【阻断】问题 → 立刻返回，**完全不动当前状态**
        ///   3. 只有全部通过，才替换状态
        ///
        /// 拆成公开方法是为了让测试/工具能直接喂数据进来，不必经过文件。
        /// </summary>
        public int Apply(GameSaveData data, bool allowVersionMismatch = false)
        {
            if (data == null) return -1;

            // ---- 第一步：预检（不改任何状态）----
            LoadPrecheck precheck = RunPrecheck(data, allowVersionMismatch);

            if (!precheck.CanProceed)
            {
                Debug.LogError(
                    "[存档] 预检未通过，**已放弃加载，当前状态未被改动**。原因：\n  " +
                    precheck.Describe());

                return -1;
            }

            if (precheck.HasAnyIssue)
            {
                // 非阻断的警告（地图不一致等）：继续，但要让人看到
                Debug.LogWarning($"[存档] 加载前的提醒：\n  {precheck.Describe()}");
            }

            // ---- 第二步：确认能过，才动状态 ----
            if (_restorePlayerPose) ApplyPlayer(data.Player);
            if (_restoreInventory) ApplyInventory(data);

            int restored = ApplyPlacedParts(data.PlacedParts);

            ApplyHealth(data.Player);

            return restored;
        }

        /// <summary>跑一次预检。把"当前场景有什么"翻译成预检器需要的委托。</summary>
        public LoadPrecheck RunPrecheck(GameSaveData data, bool allowVersionMismatch = false)
        {
            EnsureReferences();

            // 先把场景里现有的建造区收集起来（预检要用）
            BuildArea[] areas = Object.FindObjectsByType<BuildArea>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var areaById = new Dictionary<string, BuildArea>();
            var duplicateAreas = new List<string>();

            foreach (BuildArea area in areas)
            {
                if (areaById.ContainsKey(area.AreaId)) duplicateAreas.Add(area.AreaId);
                else areaById[area.AreaId] = area;
            }

            LoadPrecheck precheck = SaveLoadPrecheck.Run(
                data,
                currentMapId: ResolveMapId(),

                hasItem: itemId => FindItemDefinition(itemId) != null,

                hasArea: areaId => areaById.ContainsKey(areaId),

                hasPart: (areaId, partId) =>
                {
                    if (!areaById.TryGetValue(areaId, out BuildArea? area)) return false;

                    BuildController? builder = FindBuilderForArea(area);
                    return builder != null && builder.FindPart(partId) != null;
                },

                platformSupportsItemLookup: () => IsItemLookupSupported(),

                allowVersionMismatch: allowVersionMismatch);

            foreach (string duplicate in duplicateAreas)
            {
                precheck.Add(LoadIssueKind.DuplicateArea,
                    $"场景里有两个建造区的 AreaId 都是「{duplicate}」—— 加载时归属会错乱",
                    blocking: true);
            }

            return precheck;
        }

        /// <summary>
        /// 恢复玩家血量。
        ///
        /// 【为什么现在能做了】早先 PlayerHealth 没有"直接设定血量"的接口，
        /// 而走 TakeDamage/Heal 去凑会有副作用（触发无敌时间、死亡事件）。
        /// 现在 PlayerHealth 提供了 RestoreHealth，专门给存档用。
        /// </summary>
        private void ApplyHealth(PlayerSaveData? player)
        {
            if (_health == null || player == null) return;

            // ==0 表示这份存档没存血量（旧格式或玩家当时就是 0），按满血处理
            float target = player.Health > 0f ? player.Health : _health.Max;
            _health.RestoreHealth(target);
        }

        private void ApplyPlayer(PlayerSaveData? player)
        {
            if (player == null) return;

            var position = new Vector3(player.X, player.Y, player.Z);

            // CharacterController 会覆盖 transform.position，直接设会被它拉回去。
            // 官方做法是先禁用再设一次。
            if (_controller != null)
            {
                _controller.enabled = false;
                transform.position = position;
                transform.rotation = Quaternion.Euler(0f, player.Yaw, 0f);
                _controller.enabled = true;
            }
            else
            {
                transform.position = position;
                transform.rotation = Quaternion.Euler(0f, player.Yaw, 0f);
            }
        }

        private void ApplyInventory(GameSaveData data)
        {
            if (_inventory == null) return;

            _inventory.Backpack.Clear();
            _inventory.Pocket.Clear();

            RestoreInto(_inventory.Backpack, data.Backpack);
            RestoreInto(_inventory.Pocket, data.Pocket);
        }

        /// <summary>把记录的物品放回容器，并报告放不下的情况。</summary>
        private static void RestoreInto(Inventory container, List<ItemSaveData>? items)
        {
            if (items == null) return;

            foreach (ItemSaveData item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.ItemId) || item.Amount <= 0) continue;

                ItemDefinition? definition = FindItemDefinition(item.ItemId);

                if (definition == null)
                {
                    Debug.LogWarning($"[存档] 找不到物品「{item.ItemId}」（{item.Amount} 个），已跳过。" +
                                     "通常是物品资产被改名或删除了。");
                    continue;
                }

                int added = container.Add(definition, item.Amount);

                if (added < item.Amount)
                {
                    Debug.LogWarning($"[存档] 「{definition.DisplayName}」只放回了 {added}/{item.Amount} 个" +
                                     "（容器装不下）。超出的部分丢失。");
                }
            }
        }

        /// <summary>
        /// 按 ItemId 找物品定义。
        ///
        /// 【两条路，编辑器优先】
        ///   编辑器：直接查 AssetDatabase —— 不依赖注册表，
        ///           所以"忘了重建注册表"不会影响编辑器里的测试。
        ///   打包后：查 <see cref="ItemRegistry"/>（Resources 里的资产）。
        ///
        /// 【为什么以前做不到】早先打包分支直接返回 null，于是打包后读档会
        /// 把背包清空且一件都放不回来。现在有了注册表，打包后能正常恢复。
        /// </summary>
        private static ItemDefinition? FindItemDefinition(string itemId)
        {
#if UNITY_EDITOR
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:ItemDefinition");

            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                ItemDefinition? candidate = UnityEditor.AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);

                if (candidate != null && candidate.ItemId == itemId) return candidate;
            }

            return null;
#else
            ItemRegistry? registry = ItemRegistry.LoadOrNull();
            return registry != null ? registry.Find(itemId) : null;
#endif
        }

        /// <summary>
        /// 当前平台是否支持"按 ID 查物品定义"。
        ///
        /// 【为什么要单独问这一句】预检要区分两种"查不到"：
        ///   - "有查的能力，但没这个物品" → 存档里的物品被改名/删了，报错合理
        ///   - "根本没有查的能力" → 所有物品都查不到，那会把背包清空且放不回来
        /// 后者必须直接阻断加载。
        ///
        /// 编辑器永远支持；打包后取决于注册表在不在。
        /// </summary>
        private static bool IsItemLookupSupported()
        {
#if UNITY_EDITOR
            return true;
#else
            return ItemRegistry.LoadOrNull() != null;
#endif
        }

        /// <summary>
        /// 检查存档里记的墙朝向和边编码推出来的朝向是否一致。
        ///
        /// 【为什么只警告不改】墙的朝向完全由边的轴向决定（沿 X 或沿 Z），
        /// 边编码是【实际几何】，Yaw 只是一个冗余的记录值。
        /// 两者矛盾时以边为准，但矛盾本身说明存档被人改过或写坏了，要报出来。
        ///
        /// 【为什么不干脆删掉 Yaw 字段】它的用途是"以后的可旋转部件"（家具、门）。
        /// 现在留着并校验，是为了让字段有明确定义，而不是一个没人读的摆设。
        /// </summary>
        private static void CheckWallYawConsistency(PlacedPartSaveData part, BuildEdge edge)
        {
            // 只有"存过朝向"的记录才检查（Yaw 为 0 时无法区分"沿 X"和"没存"）
            if (Mathf.Approximately(part.Yaw, 0f)) return;

            float expected = edge.Axis == EdgeAxis.AlongX ? 0f : 90f;

            if (!Mathf.Approximately(Mathf.Repeat(part.Yaw, 360f), expected))
            {
                Debug.LogWarning(
                    $"[存档] {part} 记的朝向 {part.Yaw:F0}° 和边编码推出的 {expected:F0}° 不一致。" +
                    "已按边编码为准（它是实际几何）。存档可能被改过。");
            }
        }

        /// <summary>
        /// 【只读】检查一份存档能不能安全加载，**不改任何状态**。
        ///
        /// 这是给自动化测试和手工排查用的：真正的 Load() 会先跑同一套预检，
        /// 所以"这里说能过"和"Load 会成功"是一致的。
        /// 区别在于 Load 还会读文件、并在通过后真正应用。
        /// </summary>
        public LoadPrecheck VerifyLoadable(GameSaveData data, bool allowVersionMismatch = false)
        {
            EnsureReferences();

            if (data == null)
            {
                var empty = new LoadPrecheck();
                empty.Add(LoadIssueKind.Corrupt, "存档数据为空", blocking: true);
                return empty;
            }

            return RunPrecheck(data, allowVersionMismatch);
        }

        /// <summary>C# 属性形式，方便测试里读。</summary>
        public bool RestoresPlayerPose => _restorePlayerPose;

        /// <summary>让自动化测试能临时关掉"恢复玩家位置"，避免把玩家挪走。</summary>
        public void SetRestorePlayerPoseForTesting(bool value) => _restorePlayerPose = value;

        /// <summary>
        /// 按存档重建建造物。先清空现有的，再逐个生成。
        /// </summary>
        private int ApplyPlacedParts(List<PlacedPartSaveData>? parts)
        {
            // 收集场景里的建造区（按 AreaId 索引）
            BuildArea[] areas = Object.FindObjectsByType<BuildArea>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var areaById = new Dictionary<string, BuildArea>();
            foreach (BuildArea area in areas)
            {
                if (areaById.ContainsKey(area.AreaId))
                {
                    Debug.LogError($"[存档] 有两个建造区的 AreaId 都是「{area.AreaId}」——" +
                                   "加载时归属会错乱。请改掉其中一个。");
                    continue;
                }

                areaById[area.AreaId] = area;
            }

            // 【先清空】否则加载后会变成"旧存档 + 本次会话"的混合体。
            // ClearAllPlacedParts 会【保留】预置部件（起步地板），只销毁玩家放置的。
            ClearAllPlacedParts(areas);

            if (parts == null) return 0;

            // 【计数口径：把"保留下来的预置部件"也算作已恢复】
            //
            // 【为什么要这样算】早先只统计"新生成成功"的数量，于是日志出现
            // "保存 18 个 → 恢复 17 个"，看起来像丢了一个。
            // 实际差的那 1 个是起步地板 —— 它被保留（没销毁、也没重新生成），
            // 所以没进计数。但这个数字会让人以为恢复不完整（排查时误导过一次）。
            //
            // 正确口径："加载完成后，场景里存在的、且在这份存档里记录过的部件数量"。
            int restored = CountPreservedPreplacedParts(areas);

            foreach (PlacedPartSaveData part in parts)
            {
                if (part == null) continue;

                if (!areaById.TryGetValue(part.AreaId, out BuildArea? area))
                {
                    Debug.LogWarning($"[存档] 找不到建造区「{part.AreaId}」，跳过 {part}。");
                    continue;
                }

                BuildController? builder = FindBuilderForArea(area);

                if (builder == null)
                {
                    Debug.LogWarning($"[存档] 建造区「{part.AreaId}」没有对应的 BuildController，跳过 {part}。");
                    continue;
                }

                BuildController.BuildablePart? definition = builder.FindPart(part.PartId);

                if (definition == null)
                {
                    Debug.LogWarning($"[存档] 建造部件「{part.PartId}」不在 BuildController 的配置里，跳过 {part}。");
                    continue;
                }

                GameObject? created;

                if (part.IsWall)
                {
                    var edge = new BuildEdge(part.EdgeMidX2, part.EdgeMidZ2, part.Floor);
                    created = builder.SpawnPartFromData(
                        definition, isWall: true, edge, 0, 0, part.Floor);

                    // 【朝向一致性】墙的朝向是从边的轴向推出来的（沿 X 或沿 Z），
                    // 存档里的 Yaw 只是一个记录值。两者矛盾说明存档被人改过或写坏了，
                    // 这时以【边】为准（它是实际几何），但必须报出来。
                    CheckWallYawConsistency(part, edge);
                }
                else
                {
                    created = builder.SpawnPartFromData(definition, isWall: false, default,
                        part.CellX, part.CellZ, part.Floor);
                }

                if (created == null)
                {
                    Debug.LogWarning($"[存档] {part} 生成失败（可能越界或位置已被占），跳过。");
                    continue;
                }

                restored++;
            }

            if (restored > 0)
            {
                Debug.Log($"[存档] 恢复 {restored} 个建造物" +
                          "（含保留下来的预置部件，例如起步地板）。");
            }

            return restored;
        }

        /// <summary>
        /// 数一数场景里保留了几个"挂 PreplacedPart 的部件"。
        ///
        /// 【它解决什么】这些部件在加载时被【保留】而不是重新生成，
        /// 早先不计入"恢复 N 个"，于是日志里出现"保存 18 → 恢复 17"，
        /// 看起来像丢了一个（排查时真的误导过一次）。
        ///
        /// 口径："加载完成后，场景里确实存在的预置部件数量"。
        /// 它们同时也被登记在数据层（RegisterPreplacedParts），
        /// 所以算作"已恢复"是符合事实的。
        /// </summary>
        private static int CountPreservedPreplacedParts(BuildArea[] areas)
        {
            PreplacedPart[] preplaced = Object.FindObjectsByType<PreplacedPart>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (preplaced == null || preplaced.Length == 0) return 0;

            // 只数落在这些建造区楼层里的（跨楼层的预置部件不该被本区算进来）
            var areaFloors = new HashSet<int>();
            foreach (BuildArea area in areas)
            {
                if (area != null) areaFloors.Add(area.Floor);
            }

            int count = 0;
            foreach (PreplacedPart part in preplaced)
            {
                if (part != null && areaFloors.Contains(part.Floor)) count++;
            }

            return count;
        }

        /// <summary>把某个建造区里已放置的物体全部销毁，并清空它的占用数据。</summary>
        private int ClearAllPlacedParts(BuildArea[] areas)
        {
            int cleared = 0;

            foreach (BuildArea area in areas)
            {
                BuildController? builder = FindBuilderForArea(area);
                cleared += builder != null ? builder.ClearAllParts() : 0;
            }

            return cleared;
        }

        /// <summary>找到某个建造区对应的控制器。一个场景里通常只有一个。</summary>
        private BuildController? FindBuilderForArea(BuildArea area)
        {
            if (_builder != null && _builder.Area == area) return _builder;

            BuildController[] builders = Object.FindObjectsByType<BuildController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            foreach (BuildController builder in builders)
            {
                if (builder.Area == area) return builder;
            }

            return null;
        }

        // ---------------------------------------------------------------- 提示

        private void SetStatus(string message)
        {
            _statusMessage = message;
            _statusUntil = Time.time + 4f;
        }

        private void OnGUI()
        {
            if (!_showHud) return;
            if (Time.time >= _statusUntil || string.IsNullOrEmpty(_statusMessage)) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            _hudStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                alignment = TextAnchor.UpperLeft,
            };
            _hudStyle.normal.textColor = Color.white;

            GUI.Label(new Rect(16f, 96f, Screen.width - 32f, 30f), _statusMessage, _hudStyle);
        }
    }
}
