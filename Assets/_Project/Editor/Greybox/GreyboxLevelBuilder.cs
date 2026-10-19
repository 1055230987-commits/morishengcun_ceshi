#if UNITY_EDITOR
using System.Collections.Generic;
using Game.Player;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 一键生成灰盒原型关卡。
    ///
    /// 生成内容（全部是可手动修改的普通场景对象，不是运行时临时物）：
    ///   世界/地面            地面（含碰撞）
    ///   世界/围墙            四面围墙，同时是视觉边界和物理边界
    ///   世界/建筑            2 个灰盒建筑（有门窗洞、有屋顶）
    ///   世界/结构            L 形围墙 / 集装箱
    ///   道具/树木 石头 箱子   树和箱子是【可交互物】
    ///   玩家                 胶囊占位 + CharacterController + 输入源 + 背包 + 交互探测 + HUD
    ///   相机吊臂             斜俯视跟随相机
    ///
    /// 可重复执行：只会清理自己生成的 世界 / 道具 子节点，以及复用已有的 玩家 / 相机吊臂。
    /// </summary>
    public static class GreyboxLevelBuilder
    {
        public const string AreaDefinitionAssetPath = "Assets/_Project/Data/Areas/DA_Area_Prototype.asset";
        public const string ScenePath = "Assets/_Project/Scenes/Prototype/SC_Prototype_Greybox.unity";

        private const string GroundObjectType = "ground";
        private const string WallObjectType = "wall";
        private const string BuildingObjectType = "building";
        private const string PropObjectType = "prop";
        private const string TreeObjectType = "tree";

        [MenuItem("游戏/灰盒/创建原型场景", priority = 0)]
        public static void CreatePrototypeScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Build();
            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = FindPlayer();
            Debug.Log($"[灰盒] 原型场景已创建：{ScenePath}");
        }

        [MenuItem("游戏/灰盒/重建到当前场景", priority = 1)]
        public static void RebuildIntoCurrentScene()
        {
            Build();
            DayNightSceneTools.ApplyLightingPresetToCurrentScene();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        [MenuItem("游戏/灰盒/把玩家放到场景视图中心", priority = 20)]
        public static void PlacePlayerAtSceneView()
        {
            GameObject player = FindPlayer();
            if (player == null)
            {
                Debug.LogWarning("[灰盒] 场景里没有玩家（缺 PlayerController 组件）。");
                return;
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) return;

            Vector3 position = view.pivot;
            position.y = 0.05f;
            Undo.RecordObject(player.transform, "Place Player At Scene View");
            player.transform.position = position;
            Debug.Log($"[灰盒] 玩家已移动到 {position}。");
        }

        /// <summary>生成/刷新整个世界与玩家。可在任意场景中调用。</summary>
        public static void Build()
        {
            Game.World.MapAreaDefinition area = GetOrCreateAreaDefinition();
            if (area == null)
            {
                Debug.LogError("[灰盒] 地图数据资产创建失败，已中止场景生成（避免生成出一堆没有边界的物体）。");
                return;
            }

            // 根节点查找用【组件类型】，不能用名字（名字只是给人看的）。
            // 但要注意：FindOrCreateRoot<T> 必须保证返回的对象确实带 T，
            // 否则后续 GetComponent 拿到 null，或者被当成别的容器用。
            GameObject worldRoot = FindOrCreateRoot<Game.World.AreaBounds>("世界");
            EnsureComponent<Game.World.AreaBounds>(worldRoot);

            // 道具只是个纯容器，没有专属组件，所以按名字找。
            // 早先这里写成 FindOrCreateRoot<Transform>("道具") —— 那是个灾难：
            // FindFirstObjectByType<Transform>() 会返回场景里任意一个 Transform（通常是主相机），
            // 于是道具容器变成了主相机，重复重建时还会出现"对象已销毁仍被访问"的 MissingReferenceException。
            GameObject propsRoot = FindOrCreateNamedRoot("道具");

            ClearChildren(worldRoot.transform);
            ClearChildren(propsRoot.transform);

            // 地图边界挂在 世界 根节点上：以后换地图时，这里就是「当前场景是哪张图」的唯一入口。
            //
            // 注意：这里可能拿到"改了程序集之后没被重新解析"的旧组件（场景里存的是
            // m_EditorClassIdentifier: Assembly-CSharp::Game.World.AreaBounds，而类已搬到 Game.Runtime）。
            // 那种情况下 GetComponent 会返回 null，所以必须补一次 AddComponent 兜底，
            // 否则会在这里抛 NullReferenceException 并中断整个场景生成。
            Game.World.AreaBounds bounds = worldRoot.GetComponent<Game.World.AreaBounds>();
            bounds.Definition = area;

            // 一整套灰盒材质（颜色每次都会重新写入，所以改了配色重建场景就能看到）。
            Game.World.GreyboxPrimitives.GreyboxPalette palette = Game.World.GreyboxPrimitives.EnsurePalette();

            // 采集产出用的物品资产：没有就自动创建一份，保证树/箱子一建出来就是可用的。
            // 创建失败时给 null，InteractableTree/Crate 会在 Awake 打警告并拒绝交互（不会崩）。
            Items.ItemDefinition woodItem = GameItemTools.GetOrCreateWoodItem();
            if (woodItem == null)
            {
                Debug.LogWarning("[灰盒] 木材物品资产创建失败，树和箱子将无法采集。可手动执行菜单「游戏 → 物品 → 创建木材物品」重试。");
            }

            BuildGround(worldRoot.transform, area, palette);
            BuildPerimeterWalls(worldRoot.transform, area, palette.Structure);
            BuildBuildings(worldRoot.transform, palette);
            BuildLakes(worldRoot.transform, palette, area);            BuildStructures(worldRoot.transform, palette);
            BuildProps(propsRoot.transform, palette, woodItem, area);

            GameObject player = BuildPlayer(area, bounds);
            BuildCameraRig(player);

            EnsureWindowGlassDriver();
            BuildHomeBuildSystem(worldRoot.transform, player, palette);

            PrototypeSceneValidation.FinalizeGeneratedScene(SceneManager.GetActiveScene());
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        // ================================================================== 家园建造

        /// <summary>
        /// 建造区中心在世界里的位置。
        ///
        /// 【位置是算出来的，不是随手填的】建造区 4×4 占 X ∈ [-20,-16]、Z ∈ [6,10]：
        ///   - 北边到两层房（Z ≥ 14）留 4 米，不会压到房子
        ///   - 南边到训练木桩（Z ≈ 12~13.5）留 2 米，不会压到木桩
        /// 改这个值之前先确认这两处避让还成立。
        /// </summary>
        private static readonly Vector2 HomeBuildAreaCenter = new Vector2(-20f, 6f);

        /// <summary>建造区边长（格）。需求就是 4×4。</summary>
        private const int HomeBuildAreaSize = 4;

        /// <summary>
        /// 家园建造区的标识。存档按它把建造物归属回这块地 ——
        /// 改名会让旧存档的建造物找不到归属（加载时会警告并跳过）。
        /// </summary>
        private const string HomeBuildAreaId = "home";

        /// <summary>
        /// 接入家园建造系统：一块 4×4 的可建造平地 + 建造控制器 + 起步材料。
        ///
        /// 【为什么放在这里而不是让玩家自己找地方】
        /// 这个阶段要验证的是"能不能建"，不是"去哪建"。
        /// 固定一块平地（Inspector 里还能调位置和大小）能让验证集中在建造交互本身。
        ///
        /// 【为什么要有"初始种子地板"】
        /// 建造规则要求地板下面有支撑（不能悬空）。如果一块地板都没有，
        /// 玩家第一块地板也无处可放 —— 死锁。
        /// 所以先在区域里铺一块地板作为起点，玩家从它往外扩。
        /// </summary>
        private static void BuildHomeBuildSystem(Transform parent, GameObject player,
            Game.World.GreyboxPrimitives.GreyboxPalette palette)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("家园建造", parent);

            // ---- 建造区对象 ----
            Transform areaRoot = Game.World.GreyboxPrimitives.CreateGroup("建造区", group);
            areaRoot.position = new Vector3(HomeBuildAreaCenter.x, 0f, HomeBuildAreaCenter.y);

            Game.Gameplay.Building.BuildArea buildArea = areaRoot.gameObject.AddComponent<Game.Gameplay.Building.BuildArea>();
            buildArea.ConfigureArea(
                width: HomeBuildAreaSize,
                depth: HomeBuildAreaSize,
                cellSize: 1f,
                floor: 0,
                groundHeight: 0f,
                areaId: HomeBuildAreaId);

            // ---- 起步地板（种子）----
            //
            // 【为什么必须有】建造规则要求地板下面有支撑（不能悬空）。
            // 如果一块地板都没有，玩家第一块地板也无处可放 —— 死锁。
            // 所以先在区域里铺一块地板作为起点，玩家从它往外扩。
            //
            // 【为什么必须挂 PreplacedPart】占用数据（BuildAreaData）是普通 C# 类，
            // Unity【不序列化】它 —— 所以"进 Play 后起步地板还在占用数据里"这件事
            // 不能靠编辑器期登记（那条记录进 Play 就没了）。
            // 挂上 PreplacedPart 之后，BuildController 在 Awake 时会把场景里的预置部件
            // 重新补登进占用数据，编辑器期与运行期才一致。
            const int seedX = 0;
            const int seedZ = 0;

            GameObject seedFloor = Game.World.GreyboxPrimitives.CreateBlock("起步地板", group,
                new Vector3(
                    HomeBuildAreaCenter.x + (seedX + 0.5f) * buildArea.CellSize,
                    0.075f,
                    HomeBuildAreaCenter.y + (seedZ + 0.5f) * buildArea.CellSize),
                new Vector3(buildArea.CellSize * 0.98f, 0.15f, buildArea.CellSize * 0.98f),
                palette.Structure, "player_build", "home/seed_floor");

            Game.Gameplay.Building.PreplacedPart seedPart =
                seedFloor.AddComponent<Game.Gameplay.Building.PreplacedPart>();
            seedPart.ConfigureFloor(seedX, seedZ, buildArea.Floor, "floor");

            // 编辑器期也登记一次：这样在编辑器里（没进 Play 时）跑诊断工具也能看到一致的占用。
            // 运行时由 BuildController 从 PreplacedPart 重新补登 —— 两条路都要有。
            buildArea.Data.RegisterFloor(seedX, seedZ, "floor");

            // ---- 可建造部件 ----
            Items.ItemDefinition wood = GameItemTools.GetOrCreateWoodItem();
            Items.ItemDefinition stone = GameItemTools.GetOrCreateStoneItem();

            var parts = new List<Game.Gameplay.Building.BuildController.BuildablePart>
            {
                new Game.Gameplay.Building.BuildController.BuildablePart
                {
                    PartId = "floor",
                    DisplayName = "木地板",
                    IsEdgePart = false,
                    Material = wood,
                    MaterialCost = 1,
                    Height = 0.15f,
                    Thickness = 0.15f,
                },
                new Game.Gameplay.Building.BuildController.BuildablePart
                {
                    PartId = "wall",
                    DisplayName = "石墙",
                    IsEdgePart = true,
                    Material = stone,
                    MaterialCost = 2,
                    Height = 3.0f,
                    Thickness = 0.25f,
                },
            };

            if (wood == null || stone == null)
            {
                Debug.LogWarning(
                    "[灰盒] 建造材料资产创建失败（木材或石头为 null），建造系统会无法扣材料。" +
                    "可手动执行菜单「游戏 → 物品 → 创建全部原型物品」重试。");
            }

            // ---- 控制器：挂在玩家身上（它需要玩家的输入与背包）----
            Game.Gameplay.Building.BuildController controller =
                player.GetComponent<Game.Gameplay.Building.BuildController>();
            if (controller == null) controller = player.AddComponent<Game.Gameplay.Building.BuildController>();

            controller.ConfigureController(
                buildArea,
                player.GetComponent<Game.Player.PlayerInputSource>(),
                player.GetComponent<Game.Player.PlayerInventory>(),
                parts);

            // ---- 存档 ----
            // 挂在玩家身上（它要读玩家的位置、背包、生命值，还要找建造区）。
            if (player.GetComponent<Game.Save.GameSaveController>() == null)
            {
                player.AddComponent<Game.Save.GameSaveController>();
            }

            // ---- 起步材料 ----
            // 不给一点材料的话，第一次建造会因为"材料不足"而失败 —— 那就没法验证建造本身。
            Game.Player.PlayerInventory inventory = player.GetComponent<Game.Player.PlayerInventory>();
            if (inventory != null)
            {
                if (wood != null) inventory.Backpack.Add(wood, 20);
                if (stone != null) inventory.Backpack.Add(stone, 20);
            }
            else
            {
                Debug.LogWarning("[灰盒] 玩家上没有 PlayerInventory，无法发起步材料。");
            }
        }

        /// <summary>
        /// 保证场景里有一个 <see cref="Game.World.WindowGlassDriver"/>。
        ///
        /// 【为什么需要它】窗户玻璃的"随昼夜透光"靠一个【全局 shader 属性】驱动，
        /// 而写这个属性需要一个组件。没有它的话窗户就完全不发光（一直是暗玻璃）。
        ///
        /// 【放在哪个物体下】优先挂到昼夜光照控制器所在物体（语义最贴近）；
        /// 没有昼夜系统时挂到场景根节点新建的一个小物体上 —— 这样即使还没接入昼夜，
        /// 窗户也会按"白天"处理（见 WindowGlassDriver 的兜底逻辑），不会是黑的。
        /// </summary>
        private static void EnsureWindowGlassDriver()
        {
            if (Object.FindFirstObjectByType<Game.World.WindowGlassDriver>() != null) return;

            Game.Environment.DayNightLightingController lighting = GetLighting();

            if (lighting != null)
            {
                if (lighting.gameObject.GetComponent<Game.World.WindowGlassDriver>() == null)
                {
                    lighting.gameObject.AddComponent<Game.World.WindowGlassDriver>();
                }
                return;
            }

            Transform holder = Game.World.GreyboxPrimitives.CreateGroup("窗户透光驱动", null);
            holder.gameObject.AddComponent<Game.World.WindowGlassDriver>();
        }

        // ---------------------------------------------------------------- 查找与创建

        /// <summary>按组件类型找玩家，而不是按 GameObject 名字。改名不会破坏代码。</summary>
        private static GameObject FindPlayer()
        {
            PlayerController controller = Object.FindFirstObjectByType<PlayerController>();
            return controller != null ? controller.gameObject : null;
        }

        /// <summary>
        /// 找已有对象（按组件类型），没有就新建一个【并确保带上该组件】。
        /// 刻意不用名字做接口：名字是给人看的，改名不该让代码坏掉。
        /// </summary>
        private static GameObject FindOrCreateRoot<T>(string fallbackName) where T : Component
        {
            T existing = Object.FindFirstObjectByType<T>();
            if (existing != null) return existing.gameObject;

            GameObject created = new GameObject(fallbackName);
            created.AddComponent<T>();
            return created;
        }

        /// <summary>
        /// 按名字找纯容器对象（没有专属组件的那些，例如"道具"）。
        ///
        /// 这里用名字是刻意的例外：容器没有组件可以按类型识别，
        /// 而它是场景生成器的内部结构，命名由本项目独占，不存在"改名导致代码坏掉"的风险。
        /// **不要**把这个方法用于有组件可识别的对象。
        /// </summary>
        private static GameObject FindOrCreateNamedRoot(string name)
        {
            GameObject existing = GameObject.Find(name);
            if (existing != null) return existing;

            return new GameObject(name);
        }

        /// <summary>确保对象上带有 T 组件（没有就补一个）。</summary>
        private static T EnsureComponent<T>(GameObject target) where T : Component
        {
            T component = target.GetComponent<T>();
            if (component == null) component = target.AddComponent<T>();
            return component;
        }

        private static Game.World.MapAreaDefinition GetOrCreateAreaDefinition()
        {
            Game.World.MapAreaDefinition area = AssetDatabase.LoadAssetAtPath<Game.World.MapAreaDefinition>(AreaDefinitionAssetPath);
            if (area != null) return area;

            // 目录必须已登记，否则 CreateAsset 会报 "Creating asset at path ... failed"。
            if (!EditorFileUtility.EnsureAssetFolderForFile(AreaDefinitionAssetPath))
            {
                Debug.LogError($"[灰盒] 无法创建地图数据目录：{AreaDefinitionAssetPath}");
                return null;
            }

            area = ScriptableObject.CreateInstance<Game.World.MapAreaDefinition>();
            AssetDatabase.CreateAsset(area, AreaDefinitionAssetPath);
            AssetDatabase.SaveAssets();
            return area;
        }

        // ---------------------------------------------------------------- 场景内容

        private static void BuildGround(Transform parent, Game.World.MapAreaDefinition area, Game.World.GreyboxPrimitives.GreyboxPalette palette)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("地面", parent);

            // ---- 草地 / 泥地的颜色与分布参数 ----
            // 这两组颜色和噪声参数决定了地面的外观，也决定了草长在哪里（下面共用同一套参数）。
            Color mudColor = new Color(0.33f, 0.27f, 0.20f);
            Color grassColor = new Color(0.27f, 0.40f, 0.18f);

            const int terrainSeed = 20240;
            const float terrainNoiseScale = 0.055f;
            const float grassThreshold = 0.42f;

            // 地面：顶点色的网格。用网格而不是 Plane，才能给每个顶点写颜色做出区域差异。
            //
            // 【在湖泊处开洞】必须这样做：湖盆是挖在地面【之下】的，
            // 如果地面是一整块平板，整个湖泊会被盖住、从上面完全看不见（本工程踩过这个坑）。
            // 开洞后地形在湖面上没有三角面，湖盆和水面就露出来了。
            //
            // 【比可行走区域大一圈】用 TerrainHalfExtent 而不是 WalkableHalfExtent：
            // 边界之外要能看到一大片空地（走不到，但看得到）。
            // 玩家依然被 PlayerController 的 ClampMove + 隐形空气墙挡住。
            GameObject ground = new GameObject("地面_网格");
            ground.transform.SetParent(group, false);

            float terrainExtent = area.TerrainHalfExtent * 2f;

            // 【格子大小随地形尺寸放大】
            //
            // 地面做平面着色（见 RecalculateFlatNormals），每个三角面拆成 3 个独立顶点，
            // 所以顶点数 = 三角面数 × 3。固定 1 米格子在 60×60 时是 21,600 顶点（安全），
            // 但扩到 148.8 米就变成 133,206 顶点 —— 超出 16 位索引上限，地面会只渲染一部分
            // （本工程实际踩过：地面"消失了一半"）。
            //
            // 虽然已经在 RecalculateFlatNormals 里显式声明了 UInt32 索引（那才是真正的修复），
            // 这里再让格子随尺寸放大，是为了同时控制面数。90 格的实测值：
            //   148.8 米 / 90 = 1.65 米格子 → 16,200 面 → 48,600 顶点 = 上限的 74%
            // 留出约 25% 余量，以后稍微扩图也不会再撞上限。
            // （1.65 米的格子在俯视角下和 1 米几乎看不出差别。）
            const float minCellSize = 1.0f;
            const float targetCellsPerSide = 90f;
            float cellSize = Mathf.Max(minCellSize, terrainExtent / targetCellsPerSide);

            Mesh terrainMesh = Game.World.GreyboxPrimitives.BuildTerrainMesh(
                terrainExtent, terrainExtent, cellSize,
                mudColor, grassColor, terrainSeed, terrainNoiseScale, grassThreshold,
                holes: BuildTerrainHoles());

            MeshFilter terrainFilter = ground.AddComponent<MeshFilter>();
            terrainFilter.sharedMesh = terrainMesh;

            MeshRenderer terrainRenderer = ground.AddComponent<MeshRenderer>();
            terrainRenderer.sharedMaterial = palette.Ground;

            // 碰撞体：没有它玩家会直接穿过地面掉进虚空。
            // 自带 Plane 基本体是有碰撞体的，换成自己生成的网格后必须手动补上。
            Game.World.GreyboxPrimitives.AddTerrainCollider(ground, terrainMesh);

            Game.World.GreyboxPrimitives.EnsureObjectId(ground, $"{area.MapId}/地面/000", GroundObjectType);

            // ---- 草地面片：已按需求移除 ----
            //
            // 需要恢复时：把下面那段注释打开，并在 GreyboxPrimitives 里保留的
            // BuildGrassField / GrassSettings 就是完整实现（没有删，只是不再调用）。
            //
            // Transform grassGroup = Game.World.GreyboxPrimitives.CreateGroup("草", group);
            // Game.World.GreyboxPrimitives.GrassSettings grassSettings = new ... { ... };
            // Game.World.GreyboxPrimitives.BuildGrassField(grassGroup, area.SizeXZ.x, area.SizeXZ.y, palette.Grass, grassSettings);
        }

        /// <summary>
        /// 地图边界：不生成任何外观，只放一道【隐形空气墙】。
        ///
        /// 【为什么还要实体碰撞体，而不是只靠 PlayerController.ClampMove】
        /// ClampMove 已经在做边界裁剪（而且会消掉朝外的速度分量，避免贴边抖动），
        /// 它自己就够挡住玩家。但那是一条"硬夹住"的逻辑路径 —— 它只对走 PlayerController
        /// 的对象生效，而且贴着边界时玩家是"推不动"，没有任何物理反馈。
        ///
        /// 加一道实体碰撞体之后：
        ///   1. 手感变成"撞到东西了"，而不是"走到这里就动不了"；
        ///   2. 以后扔出去的箱子、滚动的物体也会被边界挡住（它们不走 ClampMove）；
        ///   3. 两者叠加，任意一条失效另一条仍然拦得住（本工程楼梯卡过多轮，
        ///      根因就是只依赖了其中一条约束）。
        ///
        /// 【位置】用 WalkableHalfExtent（不是 TerrainHalfExtent）——
        /// 地面在墙外还延伸一大片，那是"看得到走不到"的远景地。
        /// </summary>
        private static void BuildPerimeterWalls(Transform parent, Game.World.MapAreaDefinition area, Material material)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("空气墙", parent);

            const float thickness = 1.0f;
            const float height = 6.0f;

            // 墙心放在可行走边界之外半个厚度处，这样墙的【内表面】正好贴在可行走边界上。
            float half = area.WalkableHalfExtent + thickness * 0.5f;

            CreateAirWall("空气墙_北", group, new Vector3(-half, 0f, half), new Vector3(half, 0f, half), thickness, height);
            CreateAirWall("空气墙_南", group, new Vector3(-half, 0f, -half), new Vector3(half, 0f, -half), thickness, height);
            CreateAirWall("空气墙_东", group, new Vector3(half, 0f, -half), new Vector3(half, 0f, half), thickness, height);
            CreateAirWall("空气墙_西", group, new Vector3(-half, 0f, -half), new Vector3(-half, 0f, half), thickness, height);

            _ = material;
        }

        /// <summary>
        /// 生成一道没有外观、只有碰撞的墙。
        ///
        /// 做法：先按普通墙生成（拿到 BoxCollider），再把 Renderer 删掉。
        /// 【为什么不直接 new GameObject + BoxCollider】因为要复用 CreateWall 的
        /// 尺寸/居中计算 —— 手写一遍容易和别的墙不一致（而且那些计算已经踩过坑）。
        /// </summary>
        private static void CreateAirWall(string name, Transform parent,
            Vector3 start, Vector3 end, float thickness, float height)
        {
            GameObject wall = Game.World.GreyboxPrimitives.CreateWall(
                name, parent, start, end, thickness, height,
                material: null, objectType: WallObjectType, objectId: $"world/airwall/{name}");

            Renderer renderer = wall.GetComponent<Renderer>();
            if (renderer != null) Object.DestroyImmediate(renderer);

            // 空气墙不该被子弹命中、也不该被交互探测到 —— 它是边界，不是物体。
            // 放到一个不参与这些查询的层：直接用 Default（0），而 Pistol 的 hitMask 虽然包含
            // Default，但空气墙在射程外，实际打不到。这里显式说明而不是留着不管。
            wall.layer = 0;
        }

        private static void BuildBuildings(Transform parent, Game.World.GreyboxPrimitives.GreyboxPalette palette)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("建筑", parent);

            // 两层房屋 —— 多楼层显示与切换的测试场（矩形，带楼梯 + 窗户）
            BuildTwoStoryHouse(group, palette, new Vector2(-20f, 20f));

            // ---- 标准格子房屋：1×1 部件 + ASCII 平面图 ----
            foreach (var entry in GridHouseLayouts)
            {
                BuildGridHouse(group, palette, entry.Name, entry.Position, entry.Layout);
            }

            // ---- 路灯 ----
            // 只放在两层房周围（当前阶段就是验证窗户与路灯，先集中在看得见的地方）。
            BuildStreetLamps(group, palette, new Vector2(-20f, 20f));

            // ---- 训练木桩 ----
            // 放在两层房门前的空地上，正对门口，方便一出门就能试枪。
            BuildTrainingDummies(group, palette, new Vector2(-20f, 20f));

            // ---- 敌人 ----
            // 放在两层房【北侧】的空地：离建造区（南侧）远，不会干扰建造验证。
            BuildEnemies(group, palette);
        }

        // ================================================================== 敌人

        /// <summary>
        /// 敌人的初始位置。
        ///
        /// 【位置是算出来的，不是随手填的 —— 这里踩过一次】
        /// 第一版放在 Z = 30~32，**超出了可行走范围 ±29.4**（空气墙在 29.9），
        /// 结果敌人被挡在地图外、还报 CharacterController 的错。
        ///
        /// 现在放在两层房【南侧】空地的东半边，逐项避让：
        ///   可行走边界  ±29.4（空气墙内表面）      → Z = 22~23 安全
        ///   两层房      X[-25,-15] Z[14,26]       → 取 X ∈ [-9, 9]，不碰
        ///   训练木桩    X[-24.5,-16] Z[12,13.5]   → X 不重叠
        ///   家园建造区  X[-20,-16] Z[6,10]        → 相距 12 米以上
        ///   水塘_西     X[-28,-22] Z[-4.5,0.5]    → 很远
        /// </summary>
        private static readonly Vector3[] EnemyPositions =
        {
            new Vector3(0f, 0f, 22f),
            new Vector3(-6f, 0f, 23f),
            new Vector3(6f, 0f, 22f),
        };

        /// <summary>
        /// 每个敌人的巡逻路点相对初始位置的偏移。
        ///
        /// 【同样要避让】路点也会被空气墙挡住 —— 巡逻目标落在边界外的话，
        /// 敌人会一直朝那个方向走、贴着墙原地打转。
        /// 这里的偏移最大 7 米，配合上面的初始位置，最远只到 X = ±9、Z ∈ [19, 24]。
        /// </summary>
        private static readonly Vector2[][] EnemyPatrolOffsets =
        {
            new[] { new Vector2(-5f, 0f), new Vector2(4f, -2f) },
            new[] { new Vector2(-3f, 1f), new Vector2(3f, -5f) },
            new[] { new Vector2(-3f, 2f), new Vector2(3f, -2f) },
        };

        /// <summary>
        /// 去掉某个外观方块自带的碰撞体。
        ///
        /// 用 GameObject.CreatePrimitive 造出来的方块一定带 BoxCollider，
        /// 而敌人的碰撞应该只由 CharacterController 提供。
        /// </summary>
        private static void DestroyBodyColliders(GameObject target)
        {
            if (target == null) return;

            Collider collider = target.GetComponent<Collider>();

            if (collider != null) Object.DestroyImmediate(collider);
        }

        /// <summary>
        /// 生成会巡逻、会发现玩家、会追、会打的敌人。
        ///
        /// 【外观】程序化生成的方块（暗红身体 + 头 + 双臂），和木桩同风格。
        /// 现在不引入 glTF 模型 —— 那需要装第三方导入包，
        /// 而且这一阶段要验证的是"行为对不对"，不是"模型好不好看"。
        /// 想换真模型时：给 EnemyController 的 _visualPrefab 字段填一个预制体即可，
        /// 不用改 AI 代码。
        /// </summary>
        private static void BuildEnemies(Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("敌人", parent);

            for (int i = 0; i < EnemyPositions.Length; i++)
            {
                Vector3 position = EnemyPositions[i];
                Transform enemy = Game.World.GreyboxPrimitives.CreateGroup($"敌人_{i}", group);
                enemy.position = position;

                // ---- 外观：身体 + 头 + 两条手臂 ----
                // 尺寸参考木桩（高约 1.8 米），但更"人形"一点，方便一眼区分。
                GameObject body = Game.World.GreyboxPrimitives.CreateBlock($"{i}_身体", enemy,
                    new Vector3(0f, 0.95f, 0f),
                    new Vector3(0.62f, 1.25f, 0.40f),
                    palette.Trunk, BuildingObjectType, $"enemy/{i}/body");

                GameObject head = Game.World.GreyboxPrimitives.CreateBlock($"{i}_头", enemy,
                    new Vector3(0f, 1.78f, 0f),
                    new Vector3(0.42f, 0.42f, 0.42f),
                    palette.Rock, BuildingObjectType, $"enemy/{i}/head");

                GameObject armLeft = Game.World.GreyboxPrimitives.CreateBlock($"{i}_左臂", enemy,
                    new Vector3(-0.42f, 1.15f, 0.16f),
                    new Vector3(0.20f, 0.85f, 0.22f),
                    palette.Trunk, BuildingObjectType, $"enemy/{i}/arm_l");

                GameObject armRight = Game.World.GreyboxPrimitives.CreateBlock($"{i}_右臂", enemy,
                    new Vector3(0.42f, 1.15f, 0.16f),
                    new Vector3(0.20f, 0.85f, 0.22f),
                    palette.Trunk, BuildingObjectType, $"enemy/{i}/arm_r");

                // ---- 【必须】去掉外观方块自带的碰撞体 ----
                //
                // 【为什么必须做 —— 这是一个真实的缺陷】
                // GreyboxPrimitives.CreateBlock 用 GameObject.CreatePrimitive 创建方块，
                // 那会【自动附加一个 BoxCollider】。于是每个敌人实际有
                // 4 个 BoxCollider + 1 个 CharacterController，而敌人真正的碰撞体
                // 只应该是后者。多出来的方块碰撞体带来两个问题：
                //   1. 敌人的身体方块会和自己/墙/其它敌人发生物理碰撞，行为不可预测
                //   2. 敌人死亡时只禁用 CharacterController，那 4 个 BoxCollider 还在，
                //      于是活着的敌人探距时会探测到"一具尸体的碰撞体"并绕开它
                //
                // 去掉之后：敌人只有一个 CharacterController 作为碰撞体，
                // 死亡禁用它 → 尸体不挡路、也不被探测。手枪仍然能打中（CharacterController 是 Collider）。
                DestroyBodyColliders(body);
                DestroyBodyColliders(head);
                DestroyBodyColliders(armLeft);
                DestroyBodyColliders(armRight);

                // ---- 碰撞体 ----
                // CharacterController 自己就是碰撞体，不需要额外加。
                // 【注意】半径要略小于视觉宽度，否则会卡在门框/墙缝里。
                CharacterController controller = enemy.gameObject.AddComponent<CharacterController>();
                controller.height = 1.8f;
                controller.radius = 0.35f;
                controller.center = new Vector3(0f, 0.9f, 0f);
                controller.slopeLimit = 50f;
                controller.stepOffset = 0.4f;
                controller.skinWidth = 0.03f;
                controller.minMoveDistance = 0f;

                // ---- 巡逻路点 ----
                // 用独立的空物体，方便你在 Scene 视图里直接拖。
                Vector2[] offsets = EnemyPatrolOffsets[i % EnemyPatrolOffsets.Length];
                Transform[] points = new Transform[offsets.Length];

                for (int p = 0; p < offsets.Length; p++)
                {
                    points[p] = Game.World.GreyboxPrimitives.CreateGroup($"{i}_路点{p}", group);
                    points[p].position = position + new Vector3(offsets[p].x, 0f, offsets[p].y);
                }

                // ---- AI ----
                Game.Gameplay.AI.EnemyController component =
                    enemy.gameObject.AddComponent<Game.Gameplay.AI.EnemyController>();

                component.ConfigureEnemy(
                    maxHealth: 60f,
                    patrolPoints: points,
                    viewDistance: 14f,
                    attackDamage: 10f);

                // 【必须显式写入避障配置】
                // 不能依赖 EnemyController 里字段的初始值：生成器加完组件后，
                // 只有【序列化到场景里的值】在下次加载时才会被还原。
                // 不在这里写一遍，Inspector 里会看到 0/false，很难排查。
                component.ConfigureAvoidanceDefaults();
            }
        }

        /// <summary>
        /// 训练木桩的位置（世界坐标）。三根排成一条弧线，方便对比不同距离与散布。
        /// </summary>
        private static readonly Vector3[] DummyPositions =
        {
            new Vector3(-20.0f, 0f, 13.5f),   // 正对房门
            new Vector3(-16.0f, 0f, 12.0f),
            new Vector3(-24.5f, 0f, 12.5f),
        };

        /// <summary>
        /// 生成训练木桩（不会动的靶子），用来验证武器系统。
        ///
        /// 结构：底座 + 立柱 + 头部方块（做成"稻草人"的样子，也更容易看清朝向）。
        /// 碰撞体放在最外层的交互物体上，且【可被射线命中】——
        /// 所以不能用 Trigger（Pistol 的射线用 QueryTriggerInteraction.Ignore）。
        /// </summary>
        private static void BuildTrainingDummies(Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette, Vector2 houseCenter)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("训练木桩", parent);

            for (int i = 0; i < DummyPositions.Length; i++)
            {
                Vector3 position = DummyPositions[i];
                Transform dummy = Game.World.GreyboxPrimitives.CreateGroup($"木桩_{i}", group);
                dummy.position = position;

                // 立柱
                Game.World.GreyboxPrimitives.CreateBlock($"{i}_柱", dummy,
                    new Vector3(0f, 0.9f, 0f),
                    new Vector3(0.45f, 1.8f, 0.45f),
                    palette.Trunk, BuildingObjectType, $"dummy/{i}/post");

                // 头部（横向的木块，像稻草人的臂/头，也方便看清它有没有侧倒）
                Game.World.GreyboxPrimitives.CreateBlock($"{i}_头", dummy,
                    new Vector3(0f, 1.95f, 0f),
                    new Vector3(1.25f, 0.55f, 0.5f),
                    palette.YellowCanopy, BuildingObjectType, $"dummy/{i}/head");

                // 底座
                Game.World.GreyboxPrimitives.CreateBlock($"{i}_底座", dummy,
                    new Vector3(0f, 0.08f, 0f),
                    new Vector3(1.1f, 0.16f, 1.1f),
                    palette.Structure, BuildingObjectType, $"dummy/{i}/base");

                // 命中体：一个覆盖整根木桩的实心碰撞盒。
                // 【为什么用独立的 BoxCollider 而不是靠上面的方块】上面三个都是 Primitive Cube，
                // 自带 BoxCollider，但它们的层/父级不一致；统一放一个专用碰撞盒更可控，
                // 而且能一次覆盖到头顶，玩家瞄哪儿都能打中。
                BoxCollider hitBox = dummy.gameObject.AddComponent<BoxCollider>();
                hitBox.center = new Vector3(0f, 1.1f, 0f);
                hitBox.size = new Vector3(1.3f, 2.3f, 0.6f);
                hitBox.isTrigger = false;

                Game.Gameplay.Combat.TrainingDummy component = dummy.gameObject.AddComponent<Game.Gameplay.Combat.TrainingDummy>();
                component.ConfigureDummy(200f);
            }

            _ = houseCenter;
        }

        /// <summary>两层房周围的路灯位置（相对于房屋中心的偏移）。</summary>
        private static readonly Vector2[] LampOffsets =
        {
            new Vector2(-2.0f, -9.5f),   // 门前主路
            new Vector2(9.5f, -2.0f),    // 东侧（窗户那一面，方便对照光斑）
            new Vector2(-9.5f, 2.0f),    // 西侧（另一扇窗）
        };

        /// <summary>
        /// 在两层房周围放几盏路灯。
        ///
        /// 灯杆/灯头是程序化生成的方块（灰盒风格）；光源挂 StreetLamp 组件
        /// → 入夜自动亮、天亮自动灭，按 E 可手动开关。
        ///
        /// 【层级安排】交互碰撞体、StreetLamp、Light 三者放在【同一个物体】上，
        /// 理由是 PrototypeSceneValidation.ConfigureInteractionLayers 会把
        /// "InteractableBase 所在层级的 Collider"统一设到 Interactable 层 ——
        /// 放在一起就自动生效，不用在这里手动写层号。
        /// </summary>
        private static void BuildStreetLamps(Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette, Vector2 houseCenter)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("路灯", parent);
            Game.Environment.DayNightLightingController lighting = GetLighting();

            const float poleHeight = 4.2f;
            const float poleThickness = 0.16f;
            const float headSize = 0.44f;

            for (int i = 0; i < LampOffsets.Length; i++)
            {
                Vector2 position = houseCenter + LampOffsets[i];
                Transform lamp = Game.World.GreyboxPrimitives.CreateGroup($"路灯_{i}", group);
                lamp.position = new Vector3(position.x, 0f, position.y);

                // 灯杆（细长方块）
                Game.World.GreyboxPrimitives.CreateBlock($"{i}_灯杆", lamp,
                    new Vector3(0f, poleHeight * 0.5f, 0f),
                    new Vector3(poleThickness, poleHeight, poleThickness),
                    palette.Structure,
                    BuildingObjectType, $"lamp/{i}/pole");

                // 灯头（稍大的方块，像个灯罩）
                Game.World.GreyboxPrimitives.CreateBlock($"{i}_灯头", lamp,
                    new Vector3(0f, poleHeight - headSize * 0.4f, 0f),
                    new Vector3(headSize, headSize * 0.75f, headSize),
                    palette.Structure,
                    BuildingObjectType, $"lamp/{i}/head");

                // 交互体：光源 + 触发器 + StreetLamp 全在这一个物体上
                Transform interact = Game.World.GreyboxPrimitives.CreateGroup($"{i}_交互", lamp);
                interact.localPosition = new Vector3(0f, poleHeight - headSize, 0f);

                GameObject interactGo = interact.gameObject;

                Light light = interactGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 11f;
                light.color = new Color(1f, 0.88f, 0.68f);
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.75f;
                light.intensity = 0f;

                Game.World.StreetLamp streetLamp = interactGo.AddComponent<Game.World.StreetLamp>();
                streetLamp.ConfigureLamp(lighting, 3.4f, 11f);

                // 交互碰撞体：没有它，OverlapSphere 探测不到这盏灯，按 E 就没反应。
                // 半径给大一点，让玩家不用贴着灯杆才能按到。
                SphereCollider trigger = interactGo.AddComponent<SphereCollider>();
                trigger.radius = 1.2f;
                trigger.isTrigger = true;
            }
        }

        // ================================================================== 标准格子房屋
        //
        // 【设计】房屋完全由 ASCII 平面图描述，每个字符 = 一个 1×1 标准格子：
        //     'o' 地板（可站立的室内格子）
        //     '#' 墙（显式指定，但通常不需要写 —— 墙会从地板形状自动反推）
        //     'D' 门（玩家从这里进出）
        //     '.' 空地（图纸上的间距，也可以挖出缺口做出 L 形 / 7 形）
        //
        // 【为什么必须这样写】需求是"1×1 标准部件、不可以重叠"。
        // ASCII 网格从数据层就保证了这一点：数组的一个下标只有一个字符，
        // **结构上不可能重叠**。而且图纸写错的格子会在 Validate() 阶段被报出来，
        // 不用等生成出来再逐个检查碰撞。
        //
        // 【墙是反推的，不是画的】一个格子是地板、而它某个方向的邻居不是地板，
        // 那条边就自动生成一道墙。这样：
        //   - 不会出现"墙和地板对不上"
        //   - 不会出现两段墙重叠
        //   - 改形状只需要改地板字符
        //
        // 【以后导入 Unity 模型】每个生成的部件都挂了 BuildingPart（格子坐标 + 类型）。
        // 你自己做的 1×1 模型只要挂上同一个组件、填好坐标，就能被建筑系统接管，
        // 不需要改这里的生成逻辑。

        /// <summary>
        /// 四栋标准格子房屋的平面图。
        ///
        /// 行索引从上往下，映射到 z 轴（第一行 = z 最小）。
        /// 每行长度应当一致（不一致时按空地补齐，但补齐处不会有墙 —— 容易漏风，所以尽量对齐）。
        /// </summary>
        private static readonly (string Name, Vector2 Position, string[] Layout)[] GridHouseLayouts =
        {
            // ---- 标准矩形 7×5 ----
            ("格子房_矩形", new Vector2(-10f, 10f), new[]
            {
                "#######",
                "#ooooo#",
                "#ooooo#",
                "#ooDoo#",
                "#######"
            }),

            // ---- L 形：右上角挖掉 3×2 ----
            // 位置注意：占地 X 范围约 [-29, -23.5]，而西南湖东边界在 x = -14 ——
            // 原来放 (-24,-22) 会压到湖上，所以往西挪开。
            ("格子房_L形", new Vector2(-25f, -12f), new[]
            {
                "######...",
                "#ooooo...",
                "#ooooo###",
                "#ooooooo#",
                "#ooooooo#",
                "####D####"
            }),

            // ---- '7' 形：右上角大缺口（用空地字符留出间距）----
            ("格子房_7形", new Vector2(-8f, 27f), new[]
            {
                "...######",
                "...######",
                "...######",
                "...######",
                "#########",
                "#########",
                "####D####"
            }),

            // ---- T 形：顶部窄、底部宽 ----
            ("格子房_T形", new Vector2(10f, 27f), new[]
            {
                "...####...",
                "...####...",
                ".########.",
                ".########.",
                "....D....."
            })
        };

        private const float GridCellSize = 1.0f;
        private const float GridHouseWallHeight = 3.0f;
        private const float GridHouseWallThickness = 0.25f;
        private const float GridHouseFloorThickness = 0.3f;
        private const float GridHouseRoofThickness = 0.25f;

        /// <summary>
        /// 楼板顶面相对地形的高度（米）。
        ///
        /// 【为什么必须抬高】地形在 y = 0 是一个平面。如果楼板顶面也放在 y = 0，
        /// 两者**完全共面** → 逐像素深度冲突（z-fighting）→ 地面不断闪烁
        /// （本工程实际出现过）。
        ///
        /// 抬高 0.3 米后：楼板占据 y ∈ [0, 0.3]，与地形不再重合。
        /// 这个值必须小于 CharacterController.stepOffset（0.4 米），
        /// 否则玩家从屋外迈不进屋里 —— 门槛变成了"墙"。
        /// </summary>
        private const float GridHouseFloorTop = 0.3f;

        /// <summary>
        /// 按 ASCII 布局生成一栋房屋。
        ///
        /// 生成顺序：楼板 → 墙（含门洞）→ 屋顶 → 建筑控制器 + 室内触发区。
        /// </summary>
        private static void BuildGridHouse(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            string name,
            Vector2 position,
            string[] layout)
        {
            Game.World.GridHouseModel model = new Game.World.GridHouseModel(name, layout);

            // ---- 校验图纸 ----
            List<string> problems = model.Validate();
            if (problems.Count > 0)
            {
                foreach (string problem in problems) Debug.LogWarning($"[格子房] {problem}");
                Debug.LogWarning($"[格子房]「{name}」图纸有问题，仍会生成，但形状可能不完整。");
            }

            Transform root = Game.World.GreyboxPrimitives.CreateGroup(name, parent,
                new Vector3(position.x, 0f, position.y));

            Game.World.BuildingController building = root.gameObject.AddComponent<Game.World.BuildingController>();

            Transform floorRoot = Game.World.GreyboxPrimitives.CreateGroup("楼层_0", root);
            Transform interiorRoot = Game.World.GreyboxPrimitives.CreateGroup("室内", floorRoot);
            Transform interactionRoot = Game.World.GreyboxPrimitives.CreateGroup("交互", floorRoot);

            // 外壳节点：装外墙。它由 ApplyShellVisibility 控制 ——
            // 屋外完整显示（挡住室内），屋内半透明（能看到外面）。
            // 【为什么墙不能放楼层节点下】那样玩家一进屋，墙就跟着楼层一起被隐藏，
            // 只剩屋顶 —— 就是"从外面看只有一个天花板"的现象。
            Transform shellRoot = Game.World.GreyboxPrimitives.CreateGroup("外壳", root);

            // 网格中心：把格子坐标 (0..width-1) 平移到以 root 为原点
            float offsetX = (model.Width - 1) * GridCellSize * 0.5f;
            float offsetZ = (model.Depth - 1) * GridCellSize * 0.5f;

            int floorCount = 0;
            int wallCount = 0;

            // ---- 楼板 + 屋顶：按格子 ----
            // 屋顶放在独立节点下，进入房屋时整组隐藏（这就是"室内看不到头顶那层"）。
            Transform roofGroup = Game.World.GreyboxPrimitives.CreateGroup("屋顶", root);

            for (int z = 0; z < model.Depth; z++)
            {
                for (int x = 0; x < model.Width; x++)
                {
                    if (!model.IsWalkable(x, z)) continue;

                    Vector3 cellCenter = GridToLocal(x, z, offsetX, offsetZ);

                    // 楼板：顶面在 GridHouseFloorTop（抬到地形之上，避免 z-fighting）
                    GameObject floor = Game.World.GreyboxPrimitives.CreateBlock($"楼板_{x}_{z}", floorRoot,
                        cellCenter + new Vector3(0f, GridHouseFloorTop - GridHouseFloorThickness * 0.5f, 0f),
                        new Vector3(GridCellSize, GridHouseFloorThickness, GridCellSize),
                        palette.Structure, BuildingObjectType, $"gridhouse/floor/{x}_{z}");

                    AttachPart(floor, x, z, 0, Game.World.BuildingPartType.Floor, name);
                    floorCount++;

                    // 屋顶：底面在墙顶上方（墙从楼板顶面起算）
                    GameObject roof = Game.World.GreyboxPrimitives.CreateBlock($"屋顶_{x}_{z}", roofGroup,
                        cellCenter + new Vector3(0f,
                            GridHouseFloorTop + GridHouseWallHeight + GridHouseRoofThickness * 0.5f, 0f),
                        new Vector3(GridCellSize, GridHouseRoofThickness, GridCellSize),
                        palette.Structure, BuildingObjectType, $"gridhouse/roof/{x}_{z}");

                    AttachPart(roof, x, z, 0, Game.World.BuildingPartType.Roof, name);
                }
            }

            // ---- 墙：从地板形状反推 ----
            //
            // 【墙必须挂在"外壳"节点下，不能挂在楼层下】
            // 这是"玩家在屋外时看不到墙、只剩一个屋顶"的原因：
            // 墙如果和楼板一起放进 floorRoot（楼层 0），就会跟着楼层显示规则走 ——
            // 玩家进屋后楼层 0 变成"非当前层"被整组隐藏，墙就没了，只剩屋顶。
            //
            // 正确分工（和两层房的外壳一致）：
            //   外壳节点 → ApplyShellVisibility 控制：屋外完整、屋内半透明，始终可见
            //   楼层节点 → ApplyVisibility 控制：只显示玩家所在层
            // 楼板仍然放楼层节点（屋内不需要看到自己脚下的板？不 —— 楼板要留着踩，
            // 但它本来就看不见，所以放哪都行；放楼层下更符合"这一层的结构"语义）。
            foreach ((int x, int z, int dirX, int dirZ) in model.EnumerateWallEdges())
            {
                // 门格朝某方向的边要留门洞（不立墙）
                bool isDoorEdge = model.GetCell(x, z) == 'D' && !model.IsWalkable(x + dirX, z + dirZ);

                Vector3 cellCenter = GridToLocal(x, z, offsetX, offsetZ);

                if (isDoorEdge)
                {
                    // 门洞：两侧补墙，中间留空，上面加门楣
                    DoorwayInfo doorway = BuildDoorway(shellRoot, palette, cellCenter, dirX, dirZ,
                        $"{name}/door/{x}_{z}");
                    wallCount++;

                    // 挂上门板：门洞朝向由 dirX/dirZ 决定，"门朝屋里开"。
                    // 铰链放在门洞沿墙方向的一端，门板从铰链延伸覆盖整个洞口。
                    //
                    // 【铰链朝向怎么定】墙若沿 X 延伸（南北墙），铰链局部 +X 要沿墙 →
                    // 用 180° 让局部 +X 指向 -X，门板从东端往西盖住洞口；
                    // 墙若沿 Z 延伸（东西墙），铰链局部 +X 已经是沿墙 → 用 0°。
                    float hingeYaw = doorway.AlongX ? 180f : 0f;

                    Vector3 wallAxis = doorway.AlongX ? Vector3.right : Vector3.forward;
                    Vector3 hingePos = doorway.EdgeCenter - wallAxis * (doorway.Width * 0.5f);

                    BuildPhysicalDoor(shellRoot, palette, hingePos, hingeYaw,
                        doorway.Width, doorway.Height,
                        $"门板_{x}_{z}", $"gridhouse/door_panel/{x}_{z}",
                        openDirection: -dirX != 0 ? -dirX : -dirZ);

                    continue;
                }

                // 墙块中心：在格子中心 + 半格（贴到那条边上）。
                // Y 从楼板顶面起算 —— 楼板抬高了，墙也要跟着抬，否则墙脚会陷进楼板里。
                Vector3 wallCenter = cellCenter + new Vector3(dirX, 0f, dirZ) * (GridCellSize * 0.5f);
                wallCenter.y = GridHouseFloorTop + GridHouseWallHeight * 0.5f;

                Vector3 wallSize = dirX != 0
                    ? new Vector3(GridHouseWallThickness, GridHouseWallHeight, GridCellSize)
                    : new Vector3(GridCellSize, GridHouseWallHeight, GridHouseWallThickness);

                GameObject wall = Game.World.GreyboxPrimitives.CreateBlock($"墙_{x}_{z}_{dirX}_{dirZ}", shellRoot,
                    wallCenter, wallSize, palette.Structure, BuildingObjectType, $"gridhouse/wall/{x}_{z}_{dirX}_{dirZ}");

                AttachPart(wall, x, z, 0, Game.World.BuildingPartType.Wall, name);
                wallCount++;
            }

            // ---- 建筑控制器 + 室内触发区 ----
            float sizeX = model.Width * GridCellSize;
            float sizeZ = model.Depth * GridCellSize;

            GameObject triggerGo = CreateTriggerBox(root, "内部触发区",
                new Vector3(0f, GridHouseWallHeight * 0.5f, 0f),
                new Vector3(sizeX - GridHouseWallThickness * 2f,
                            GridHouseWallHeight - 0.2f,
                            sizeZ - GridHouseWallThickness * 2f));

            Game.World.BuildingInteriorTrigger trigger = triggerGo.AddComponent<Game.World.BuildingInteriorTrigger>();

            Game.World.BuildingFloor floorPart = floorRoot.gameObject.AddComponent<Game.World.BuildingFloor>();
            floorPart.ConfigureFloor(0, name, interiorRoot, floorRoot, interactionRoot, floorRoot);

            // 屋顶楼层：楼层号给一个不会和真实楼层冲突的值，且不参与楼层显示规则
            // （它的显隐完全由 ApplyShellVisibility 控制）。
            Game.World.BuildingFloor roofFloor = roofGroup.gameObject.AddComponent<Game.World.BuildingFloor>();
            roofFloor.ConfigureFloor(int.MaxValue - 1, $"{name}_屋顶", roofGroup, roofGroup, roofGroup);
            roofFloor.IgnoreFloorVisibilityRules = true;

            // 外壳也要有一个 BuildingFloor 代理，ApplyGhostAlphaToShell 才能给它设置半透明透明度。
            // 没有它的话，玩家在屋内时外壳没有幽灵材质，会看起来"消失"。
            Game.World.BuildingFloor shellFloor = shellRoot.gameObject.AddComponent<Game.World.BuildingFloor>();
            shellFloor.ConfigureFloor(int.MaxValue - 2, $"{name}_外壳", shellRoot, shellRoot, shellRoot);
            shellFloor.IgnoreFloorVisibilityRules = true;

            // exteriorShell 传 shellRoot —— 这是"屋外能看到墙"的关键。
            building.ConfigureBuilding($"house/{name}", new[] { floorPart }, roofGroup,
                new[] { shellRoot }, null);

            SerializedObject triggerSerialized = new SerializedObject(trigger);
            SetReference(triggerSerialized, "_building", building);
            triggerSerialized.ApplyModifiedPropertiesWithoutUndo();

            building.ApplyVisibility(false, 0);

            Debug.Log($"[格子房]「{name}」生成完成：{model.Width}×{model.Depth} 格，" +
                      $"地板 {floorCount} 块，墙 {wallCount} 段。");
        }

        /// <summary>格子坐标 → root 下的局部坐标（格子中心）。</summary>
        private static Vector3 GridToLocal(int x, int z, float offsetX, float offsetZ)
        {
            return new Vector3(x * GridCellSize - offsetX, 0f, z * GridCellSize - offsetZ);
        }

        /// <summary>给生成出来的部件挂 BuildingPart 标记（格子坐标 + 类型）。</summary>
        private static void AttachPart(
            GameObject target, int x, int z, int floorIndex, Game.World.BuildingPartType type, string buildingId)
        {
            if (target == null) return;

            Game.World.BuildingPart part = target.GetComponent<Game.World.BuildingPart>();
            if (part == null) part = target.AddComponent<Game.World.BuildingPart>();

            part.Configure(x, z, floorIndex, type, buildingId);
        }

        /// <summary>一个门洞的位置信息，供之后挂门板用。</summary>
        private struct DoorwayInfo
        {
            /// <summary>门洞在地面上的中心点（局部坐标，y 已对齐楼板顶面）。</summary>
            public Vector3 EdgeCenter;

            /// <summary>门洞沿哪个世界轴延伸。true = 沿 X，false = 沿 Z。</summary>
            public bool AlongX;

            /// <summary>门洞宽度（米）。</summary>
            public float Width;

            /// <summary>门洞高度（米）。</summary>
            public float Height;
        }

        /// <summary>
        /// 生成一个门洞：门洞两侧各补一小段墙，上方加门楣，中间留空给玩家通过。
        ///
        /// 返回门洞信息，调用方据此挂上门板（见 BuildPhysicalDoor）。
        /// 「墙上有洞」和「有扇门」是两件事 —— 洞是结构，门是物件，
        /// 所以这里只负责开洞，门板由调用方决定要不要加。
        /// </summary>
        private static DoorwayInfo BuildDoorway(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Vector3 cellCenter,
            int dirX,
            int dirZ,
            string objectIdPrefix)
        {
            const float doorWidth = 0.9f;
            const float doorHeight = 2.1f;

            Vector3 edgeCenter = cellCenter + new Vector3(dirX, 0f, dirZ) * (GridCellSize * 0.5f);
            edgeCenter.y = GridHouseFloorTop;

            bool alongX = dirX != 0;   // 墙块沿哪个轴延伸

            // 门洞两侧
            float sideLength = (GridCellSize - doorWidth) * 0.5f;
            if (sideLength > 0.02f)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 offset = alongX
                        ? new Vector3(0f, 0f, side * (doorWidth * 0.5f + sideLength * 0.5f))
                        : new Vector3(side * (doorWidth * 0.5f + sideLength * 0.5f), 0f, 0f);

                    Vector3 size = alongX
                        ? new Vector3(GridHouseWallThickness, GridHouseWallHeight, sideLength)
                        : new Vector3(sideLength, GridHouseWallHeight, GridHouseWallThickness);

                    Vector3 center = edgeCenter + offset;
                    // 从楼板顶面起算，砌满整个墙高（不能只砌到门楣下方，否则门洞下方会漏空）。
                    center.y = GridHouseFloorTop + GridHouseWallHeight * 0.5f;

                    Game.World.GreyboxPrimitives.CreateBlock($"{objectIdPrefix}_侧{side}", parent,
                        center, size, palette.Structure, BuildingObjectType, $"{objectIdPrefix}/side{side}");
                }
            }

            // 门楣（门洞上方到墙顶）
            float lintelHeight = GridHouseWallHeight - doorHeight;
            if (lintelHeight > 0.05f)
            {
                Vector3 lintelSize = alongX
                    ? new Vector3(GridHouseWallThickness, lintelHeight, doorWidth)
                    : new Vector3(doorWidth, lintelHeight, GridHouseWallThickness);

                Vector3 lintelCenter = edgeCenter;
                lintelCenter.y = GridHouseFloorTop + doorHeight + lintelHeight * 0.5f;

                Game.World.GreyboxPrimitives.CreateBlock($"{objectIdPrefix}_门楣", parent,
                    lintelCenter, lintelSize, palette.Structure, BuildingObjectType, $"{objectIdPrefix}/lintel");
            }

            return new DoorwayInfo
            {
                EdgeCenter = edgeCenter,
                AlongX = alongX,
                Width = doorWidth,
                Height = doorHeight
            };
        }

        // ================================================================== 两层测试房屋
        //
        // 【坐标系与关键高度】全部以房屋根节点为原点（root 放在地形上）。
        //
        //   y = 0.30  一楼楼板顶面 = 玩家在一楼的脚高（= _floorTop）
        //   y = 3.65  二楼楼板顶面 = 玩家在二楼的脚高（= _floorTop + TwoStoryFloorHeight）
        //   y = 3.30  一楼天花板底面 = 二楼楼板底面（= 二楼楼板顶面 - 楼板厚）
        //
        //   俯视图（X 向右，Z 向上 = 北）：
        //
        //        z = +6  ┌──────────────────────────────┐
        //                │        二楼（可站立）          │
        //        z = +3  ├──────────────────┬───────────┤  落脚平台边界
        //                │                  │  楼梯      │  x 属于 [2.6, 5.0]
        //                │   一楼空地        │  z: -4→+3  │
        //        z = -4  ├──────────────────┤            │  楼梯起点
        //                │        门          │            │
        //        z = -6  └──────────────────┴───────────┘
        //              x=-5   门在 x=-1.5    x=+2.6   x=+5
        //
        // 【为什么楼板要抬到 0.3】地形是一个 y = 0 的平面。如果楼板顶面也在 y = 0，
        // 两者完全共面 → 逐像素深度冲突（z-fighting）→ 一楼地面不停闪烁（本工程实际出现过）。
        // 抬高 0.3 米后楼板占 [0, 0.3]，与地形不再重合；且 0.3 < stepOffset(0.4)，
        // 玩家还能从屋外迈进来。
        //
        // 【为什么房间要加深到 12 米】楼梯要爬 3.35 米。坡度越陡，玩家越早接近二楼楼板，
        // 而二楼楼板底面（3.30）会在玩家脚高超过 1.5 米时开始挡头。算式：
        //     头部碰到楼板底面的条件：脚高 + 1.8 > 3.30 → 脚高 > 1.5
        //     坡面 ramp(z) = 3.35 * (z - (-4)) / 7
        //     脚高 1.5 出现在 z = -0.87；玩家前缘最多再往前 0.35 → z = -0.52
        //     而楼板（落脚平台）从 z = +3.0 才开始 → 净空充足
        // 7 米跑道对应坡度 3.35/7 ≈ 25.6°，单级抬高 3.35/13 ≈ 0.258 米。

        private const float TwoStoryHalfX = 5.0f;
        private const float TwoStoryHalfZ = 6.0f;
        private const float TwoStoryWallThickness = 0.3f;
        private const float TwoStoryFloorHeight = 3.35f;
        private const float TwoStoryDoorWidth = 2.2f;
        private const float TwoStoryDoorHeight = 2.3f;

        /// <summary>一楼楼板顶面相对地形的高度。必须 &gt; 0（否则和地形共面闪烁），且 &lt; stepOffset（否则玩家进不去）。</summary>
        private const float TwoStoryFloorTop = 0.3f;

        /// <summary>楼板厚度。一楼楼板占 [0, 0.3]；二楼楼板占 [3.35, 3.65]。</summary>
        private const float TwoStorySlabThickness = 0.3f;

        // ---- 楼梯参数 ----
        //
        // 【为什么贴着东墙、西侧留 2 米】上一版楼梯宽 2.4、中心在 x=3.7，
        // 于是西侧只剩 x ∈ [-5, 2.5] 的空地，而东墙侧还留了 1.0 米死缝 ——
        // 玩家反馈"楼梯够不到、和墙壁间距太小"。
        // 现在让楼梯直接贴东墙（x ∈ [2.6, 5.0]），把西侧的 2.0 米留成完整通道，
        // 玩家从西侧正面走上楼梯，没有死角。
        private const float StairWidth = 2.4f;
        private const float StairCenterX = TwoStoryHalfX - StairWidth * 0.5f;   // 3.8
        private const float StairStartZ = -4.0f;
        private const float StairEndZ = 3.0f;
        private const float StairTopY = TwoStoryFloorHeight;

        /// <summary>楼梯总级数。单级抬高 = StairTopY / StairStepCount。</summary>
        private const int StairStepCount = 13;

        // 天花板 / 二楼楼板的开洞范围。x 与楼梯同宽（西边界 = 楼梯西侧），
        // 这样楼梯正好填满洞口，两侧不会留出会掉下去的缝。
        private const float HoleMinX = StairCenterX - StairWidth * 0.5f;   // 2.6
        private const float HoleMaxX = TwoStoryHalfX;                      // 5.0
        private const float HoleMinZ = StairStartZ - 0.2f;
        private const float HoleMaxZ = StairEndZ;

        /// <summary>二楼落脚平台：楼梯终点往北到北墙。它的南边界必须正好等于楼梯终点，
        /// 这样玩家上到最后一级时脚下高度（3.65）与平台表面齐平，不会掉一下。</summary>
        private const float UpperSlabMinZ = StairEndZ;
        private const float UpperSlabMaxZ = TwoStoryHalfZ;

        /// <summary>二楼的室内楼板（可站立）顶面高度。</summary>
        private const float UpperFloorTop = TwoStoryFloorTop + TwoStoryFloorHeight;

        // ---- 梯子（传送式上楼的保底方案，保留但不再是主要途径）----
        private const float LadderX = -3.4f;
        private const float LadderBaseZ = -TwoStoryHalfZ + TwoStoryWallThickness;   // 贴南墙内侧

        private const float LadderHoleMinX = -4.4f;
        private const float LadderHoleMaxX = -2.4f;
        private const float LadderHoleMinZ = -TwoStoryHalfZ + TwoStoryWallThickness - 0.05f;
        private const float LadderHoleMaxZ = LadderHoleMinZ + 1.6f;

        // ---- 窗户 ----
        /// <summary>窗台离本层楼板顶面的高度。</summary>
        private const float WindowSillHeight = 0.95f;

        /// <summary>窗洞高度。窗楣高度 = 墙高 3.35 - 窗台 0.95 - 窗高 1.5 = 0.9 米。</summary>
        private const float WindowHeight = 1.5f;

        /// <summary>窗洞宽度。东西墙长 12 米，够放。</summary>
        private const float WindowWidth = 1.6f;

        // 窗户在各自墙面上的位置。
        // 东墙往南偏、西墙往北偏，避免两扇窗正对（正对会显得很假，也方便对比两侧光斑）。
        private const float WindowEastCenterZ = -1.6f;
        private const float WindowWestCenterZ = 1.8f;
        private const float WindowSouthCenterX = 1.4f;
        private const float WindowNorthCenterX = -1.6f;

        /// <summary>窗户玻璃材质（每次重建只取一次，避免重复 AssetDatabase 查询）。</summary>
        private static Material _cachedWindowGlass;

        /// <summary>昼夜光照控制器缓存（窗户透光和路灯都要用）。</summary>
        private static Game.Environment.DayNightLightingController _cachedLighting;

        private static Material GetWindowGlassMaterial()
        {
            if (_cachedWindowGlass == null)
            {
                _cachedWindowGlass = Game.World.GreyboxPrimitives.GetOrCreateWindowGlassMaterial();
            }
            return _cachedWindowGlass;
        }

        /// <summary>取场景里的昼夜光照控制器（没有就返回 null，各处会退回"按白天"处理）。</summary>
        private static Game.Environment.DayNightLightingController GetLighting()
        {
            if (_cachedLighting == null)
            {
#if UNITY_EDITOR
                _cachedLighting = Object.FindFirstObjectByType<Game.Environment.DayNightLightingController>();
#endif
            }
            return _cachedLighting;
        }

        private static void BuildTwoStoryHouse(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Vector2 center)
        {
            Transform root = Game.World.GreyboxPrimitives.CreateGroup("两层测试房屋", parent,
                new Vector3(center.x, 0f, center.y));

            Game.World.BuildingController building = root.gameObject.AddComponent<Game.World.BuildingController>();

            Material structure = palette.Structure;
            Material furniture = palette.Crate;

            var level0 = BuildFloorLevel(root, 0, "一楼", structure, furniture,
                withDoor: true, withCeiling: true, withLadderHole: false);
            var level1 = BuildFloorLevel(root, 1, "二楼", structure, furniture,
                withDoor: false, withCeiling: false, withLadderHole: true);

            // ---- 两层高的外壳（按层拆分，便于给更高层更低的透明度）----
            Transform shellRoot = Game.World.GreyboxPrimitives.CreateGroup("外壳", root);
            Transform[] shells = BuildShellWalls(shellRoot, structure);

            // ---- 屋顶 ----
            Transform roof = Game.World.GreyboxPrimitives.CreateGroup("屋顶", root);
            Game.World.GreyboxPrimitives.CreateBlock("屋顶_板", roof,
                new Vector3(0f, UpperFloorTop + TwoStoryFloorHeight + 0.15f, 0f),
                new Vector3(TwoStoryHalfX * 2f + 0.6f, 0.3f, TwoStoryHalfZ * 2f + 0.6f),
                structure, BuildingObjectType, "house/roof");

            Game.World.BuildingFloor roofFloor = roof.gameObject.AddComponent<Game.World.BuildingFloor>();
            roofFloor.ConfigureFloor(int.MaxValue - 1, "屋顶", roof, roof, roof);
            roofFloor.IgnoreFloorVisibilityRules = true;

            // ---- 楼梯 ----
            Transform stairs = Game.World.GreyboxPrimitives.CreateGroup("楼梯", root);
            BuildWalkableStairs(stairs, structure);

            // ---- 楼梯口触发器 ----
            // 上口放在落脚平台上（玩家走完楼梯踏上去就切楼层）；
            // 下口放在楼梯起点前面一点（玩家往回走到底就切回一楼）。
            GameObject upperTriggerGo = CreateTriggerBox(stairs, "楼梯触发器_上口",
                new Vector3(StairCenterX, UpperFloorTop + 1.2f, UpperSlabMinZ + 0.6f),
                new Vector3(StairWidth + 0.6f, 2.4f, 1.6f));
            Game.World.StairFloorTrigger upperTrigger = upperTriggerGo.AddComponent<Game.World.StairFloorTrigger>();
            upperTrigger.ConfigureTrigger(building, 1);

            GameObject lowerTriggerGo = CreateTriggerBox(stairs, "楼梯触发器_下口",
                new Vector3(StairCenterX, TwoStoryFloorTop + 1.2f, StairStartZ + 1.0f),
                new Vector3(StairWidth + 0.6f, 2.4f, 1.6f));
            Game.World.StairFloorTrigger lowerTrigger = lowerTriggerGo.AddComponent<Game.World.StairFloorTrigger>();
            lowerTrigger.ConfigureTrigger(building, 0);

            // ---- 房门 ----
            BuildTwoStoryDoor(root, palette, -1.5f);

            // ---- 内部触发区 ----
            GameObject interiorGo = CreateTriggerBox(root, "内部触发区",
                new Vector3(0f, TwoStoryFloorTop + TwoStoryFloorHeight, 0f),
                new Vector3(TwoStoryHalfX * 2f - TwoStoryWallThickness * 2f,
                            TwoStoryFloorHeight * 2f - 0.2f,
                            TwoStoryHalfZ * 2f - TwoStoryWallThickness * 2f));
            Game.World.BuildingInteriorTrigger interiorTrigger = interiorGo.AddComponent<Game.World.BuildingInteriorTrigger>();

            // ---- 梯子（保底：按 E 直接切楼层 + 传送，不依赖物理）----
            BuildLadder(root, palette, building);

            // ---- 组装 ----
            building.ConfigureBuilding("house/two_story", new[] { level0.Floor, level1.Floor }, roof, shells,
                new[] { level0.Ceiling });

            SerializedObject triggerSerialized = new SerializedObject(interiorTrigger);
            SetReference(triggerSerialized, "_building", building);
            triggerSerialized.ApplyModifiedPropertiesWithoutUndo();

            building.ApplyVisibility(false, 0);
        }

        private struct FloorLevelResult
        {
            public Game.World.BuildingFloor Floor;

            /// <summary>该层的天花板（可能为 null）。它同时是上一层的可站立地板。</summary>
            public Transform Ceiling;
        }

        private static FloorLevelResult BuildFloorLevel(
            Transform root,
            int floorIndex,
            string displayName,
            Material structure,
            Material furniture,
            bool withDoor,
            bool withCeiling,
            bool withLadderHole)
        {
            // 楼层根节点抬到该层楼板顶面所在高度。
            float baseY = TwoStoryFloorTop + floorIndex * TwoStoryFloorHeight;

            Transform floorRoot = Game.World.GreyboxPrimitives.CreateGroup(
                $"楼层_{floorIndex}", root, new Vector3(0f, baseY, 0f));

            Transform interiorRoot = Game.World.GreyboxPrimitives.CreateGroup("室内", floorRoot);
            Transform interactionRoot = Game.World.GreyboxPrimitives.CreateGroup("交互", floorRoot);

            // 室内楼板（结构件，挂在楼层根上，不参与楼层隐藏）。
            // 顶面对齐楼层根（y = 0 局部），所以厚度全部在楼层根下方。
            if (withLadderHole)
            {
                // 同一高度的天花板、室内楼板必须共用两个洞口，否则隐藏的楼板仍会挡住楼梯。
                BuildUpperFloorSlab($"室内楼板_{floorIndex}", floorRoot, 0f, structure,
                    $"house/interior_slab_{floorIndex}");
            }
            else
            {
                Game.World.GreyboxPrimitives.CreateBlock($"室内楼板_{floorIndex}", floorRoot,
                    new Vector3(0f, -TwoStorySlabThickness * 0.5f, 0f),
                    new Vector3(TwoStoryHalfX * 2f, TwoStorySlabThickness, TwoStoryHalfZ * 2f),
                    structure, BuildingObjectType, $"house/interior_slab_{floorIndex}");
            }

            BuildInteriorWalls(floorRoot, structure, withDoor);

            // 室内陈设（会随楼层切换隐藏）。位置避开楼梯所在的东侧（x >= 2.4）。
            Game.World.GreyboxPrimitives.CreateBlock($"床_{floorIndex}", interiorRoot,
                new Vector3(-TwoStoryHalfX + 1.6f, 0.3f, -TwoStoryHalfZ + 1.6f),
                new Vector3(2.0f, 0.6f, 1.0f), furniture, PropObjectType, $"house/bed_{floorIndex}");

            Game.World.GreyboxPrimitives.CreateBlock($"柜子_{floorIndex}", interiorRoot,
                new Vector3(-TwoStoryHalfX + 0.6f, 0.8f, TwoStoryHalfZ - 1.4f),
                new Vector3(1.0f, 1.6f, 0.7f), furniture, PropObjectType, $"house/cabinet_{floorIndex}");

            Game.World.GreyboxPrimitives.CreateBlock($"桌子_{floorIndex}", interiorRoot,
                new Vector3(-1.6f, 0.4f, 0.5f),
                new Vector3(1.6f, 0.8f, 0.9f), furniture, PropObjectType, $"house/table_{floorIndex}");

            Game.World.BuildingFloor floor = floorRoot.gameObject.AddComponent<Game.World.BuildingFloor>();
            floor.ConfigureFloor(floorIndex, displayName, interiorRoot, floorRoot, interactionRoot, floorRoot);

            Transform ceiling = withCeiling ? BuildCeiling(floorRoot, floorIndex, structure) : null;

            return new FloorLevelResult { Floor = floor, Ceiling = ceiling };
        }

        /// <summary>
        /// 一楼的天花板（同时是二楼的可站立地板）。楼梯井处开洞。
        ///
        /// 相对楼层根（y = 0 局部 = 世界 0.3）算：
        ///   天花板顶面 = TwoStoryFloorHeight（世界 3.65 = 二楼楼板顶面）
        ///   天花板底面 = TwoStoryFloorHeight - 厚度（世界 3.35）
        /// </summary>
        internal static void BuildUpperFloorSlab(string name, Transform parent, float topY,
            Material material, string idPrefix)
        {
            Rect stairHole = Rect.MinMaxRect(HoleMinX, HoleMinZ, HoleMaxX, HoleMaxZ);
            Rect ladderHole = Rect.MinMaxRect(LadderHoleMinX, LadderHoleMinZ, LadderHoleMaxX, LadderHoleMaxZ);
            var xs = new List<float>(new SortedSet<float>
                { -TwoStoryHalfX, TwoStoryHalfX, stairHole.xMin, stairHole.xMax, ladderHole.xMin, ladderHole.xMax });
            var zs = new List<float>(new SortedSet<float>
                { -TwoStoryHalfZ, TwoStoryHalfZ, stairHole.yMin, stairHole.yMax, ladderHole.yMin, ladderHole.yMax });
            for (int x = 0; x < xs.Count - 1; x++)
            for (int z = 0; z < zs.Count - 1; z++)
            {
                Vector2 center = new Vector2((xs[x] + xs[x + 1]) * 0.5f, (zs[z] + zs[z + 1]) * 0.5f);
                if (stairHole.Contains(center) || ladderHole.Contains(center)) continue;
                Game.World.GreyboxPrimitives.CreateBlock($"{name}_{x}_{z}", parent,
                    new Vector3(center.x, topY - TwoStorySlabThickness * 0.5f, center.y),
                    new Vector3(xs[x + 1] - xs[x], TwoStorySlabThickness, zs[z + 1] - zs[z]),
                    material, BuildingObjectType, $"{idPrefix}/{x}_{z}");
            }
        }

        private static Transform BuildCeiling(Transform floorRoot, int floorIndex, Material structure)
        {
            Transform slab = Game.World.GreyboxPrimitives.CreateGroup($"天花板_{floorIndex}", floorRoot);

            float topY = TwoStoryFloorHeight;
            // 分块本身包含北侧落脚平台，不再重复叠加一块平台碰撞体。
            BuildUpperFloorSlab($"天花板_板_{floorIndex}", slab, topY, structure,
                $"house/ceiling_{floorIndex}");

            Game.World.BuildingFloor proxy = slab.gameObject.AddComponent<Game.World.BuildingFloor>();
            proxy.ConfigureFloor(int.MaxValue - 100 - floorIndex, $"天花板_{floorIndex}", slab, slab, slab);
            proxy.IgnoreFloorVisibilityRules = true;

            return slab;
        }

        /// <summary>一层的隔墙（结构件）。门洞只开在指定楼层。</summary>
        private static void BuildInteriorWalls(Transform parent, Material material, bool withDoor)
        {
            const float thickness = TwoStoryWallThickness;
            float halfX = TwoStoryHalfX;
            float halfZ = TwoStoryHalfZ;
            float wallHeight = TwoStoryFloorHeight;
            float centerY = wallHeight * 0.5f;

            if (withDoor)
            {
                const float doorHeight = TwoStoryDoorHeight;
                const float doorCenterX = -1.5f;
                const float half = TwoStoryDoorWidth * 0.5f;

                float leftMax = doorCenterX - half;
                float rightMin = doorCenterX + half;

                BuildStripWall("南墙_左", parent, -halfX, leftMax, -halfZ, thickness, wallHeight, centerY,
                    material, true, "house/south_l");

                BuildStripWall("南墙_右", parent, rightMin, halfX, -halfZ, thickness, wallHeight, centerY,
                    material, true, "house/south_r");

                Game.World.GreyboxPrimitives.CreateBlock("南墙_门楣", parent,
                    new Vector3(doorCenterX, (doorHeight + wallHeight) * 0.5f, -halfZ),
                    new Vector3(TwoStoryDoorWidth, wallHeight - doorHeight, thickness),
                    material, BuildingObjectType, "house/south_top");
            }
            else
            {
                Game.World.GreyboxPrimitives.CreateBlock("南墙", parent,
                    new Vector3(0f, centerY, -halfZ),
                    new Vector3(halfX * 2f, wallHeight, thickness), material, BuildingObjectType, "house/south");
            }

            Game.World.GreyboxPrimitives.CreateBlock("北墙", parent,
                new Vector3(0f, centerY, halfZ),
                new Vector3(halfX * 2f, wallHeight, thickness), material, BuildingObjectType, "house/north");

            Game.World.GreyboxPrimitives.CreateBlock("东墙", parent,
                new Vector3(halfX, centerY, 0f),
                new Vector3(thickness, wallHeight, halfZ * 2f), material, BuildingObjectType, "house/east");

            // 西墙带窗
            const float sillHeight = 0.9f;
            const float windowHeight = 1.2f;

            Game.World.GreyboxPrimitives.CreateBlock("西墙_墙裙", parent,
                new Vector3(-halfX, sillHeight * 0.5f, 0f),
                new Vector3(thickness, sillHeight, halfZ * 2f), material, BuildingObjectType, "house/west_sill");

            Game.World.GreyboxPrimitives.CreateBlock("西墙_窗楣", parent,
                new Vector3(-halfX, sillHeight + windowHeight + (wallHeight - sillHeight - windowHeight) * 0.5f, 0f),
                new Vector3(thickness, wallHeight - sillHeight - windowHeight, halfZ * 2f),
                material, BuildingObjectType, "house/west_top");
        }

        /// <summary>
        /// 给玩家装一把手枪。
        ///
        /// 【为什么挂在子物体上】武器的瞄准原点、曳光弹起点、以后的枪口火焰
        /// 都需要一个"枪在哪"的参考点。挂在玩家根节点上会让枪口就是脚底。
        /// </summary>
        private static void BuildPistol(GameObject player)
        {
            Game.Player.PlayerInputSource input = player.GetComponent<Game.Player.PlayerInputSource>();
            if (input == null)
            {
                Debug.LogWarning("[灰盒] 玩家上没有 PlayerInputSource，手枪无法接入输入，跳过。");
                return;
            }

            // 复用已有武器物体（重建场景时不要越堆越多）
            Transform existing = player.transform.Find("手枪");
            GameObject go;

            if (existing != null)
            {
                go = existing.gameObject;
            }
            else
            {
                go = new GameObject("手枪");
                go.transform.SetParent(player.transform, false);
                go.transform.localPosition = new Vector3(0.22f, 1.15f, 0.3f);
            }

            Game.Gameplay.Weapons.Pistol pistol = go.GetComponent<Game.Gameplay.Weapons.Pistol>();
            if (pistol == null) pistol = go.AddComponent<Game.Gameplay.Weapons.Pistol>();

            pistol.ConfigurePistol(input, damage: 25f, fireInterval: 0.18f, spreadDegrees: 0.6f);
        }

        /// <summary>
        /// 在 X 方向的一段范围内生成墙块（用于带门洞的墙）。
        /// </summary>
        private static void BuildStripWall(
            string name, Transform parent, float minX, float maxX, float z,
            float thickness, float height, float centerY,
            Material material, bool alongX, string objectId)
        {
            float length = maxX - minX;
            if (length < 0.02f) return;

            Game.World.GreyboxPrimitives.CreateBlock(name, parent,
                new Vector3((minX + maxX) * 0.5f, centerY, z),
                new Vector3(length, height, thickness),
                material, BuildingObjectType, objectId);

            _ = alongX;
        }

        // ------------------------------------------------------------------ 窗户

        /// <summary>窗户开口在墙面上的位置（世界坐标）。</summary>
        private readonly struct WindowOpening
        {
            public readonly float AlongMin;
            public readonly float AlongMax;
            public readonly float SillY;
            public readonly float HeaderY;

            public WindowOpening(float alongMin, float alongMax, float sillY, float headerY)
            {
                AlongMin = alongMin;
                AlongMax = alongMax;
                SillY = sillY;
                HeaderY = headerY;
            }

            public float Width => AlongMax - AlongMin;
            public float Height => HeaderY - SillY;
            public float AlongCenter => (AlongMin + AlongMax) * 0.5f;
            public float CenterY => (SillY + HeaderY) * 0.5f;
        }

        /// <summary>
        /// 在【南北墙】（墙沿 X 延伸、厚度在 Z）上开一个窗。
        ///
        /// 把窗洞两侧的墙补回去（窗台下 + 窗楣上 + 左右两侧），再放玻璃和室内光。
        /// 这样"开窗"不需要把整面墙重排 —— 原来的墙块照旧生成，本函数只补洞口。
        /// </summary>
        /// <summary>
        /// 生成一段墙，并【跳过】若干个开口（窗洞）。
        ///
        /// 【为什么必须"跳过"而不是"先铺满再补洞"】
        /// 如果先铺一整面墙、再在洞口位置补墙块，那些补块会和整面墙【重叠】——
        /// 表现是 z-fighting 闪烁、几何翻倍、光照计算重复。
        /// 所以洞口必须由一面【被切成几段】的墙来让出：
        ///   沿墙方向在每个洞口处断开，每个洞口的上下各补一段
        ///   （窗台下的墙 + 窗楣上的墙）。
        ///
        /// <paramref name="openings"/> 必须按 AlongMin 升序排列
        /// （调用方都是硬编码常量，顺序在代码里可见）。
        /// </summary>
        private static void BuildWallWithOpenings(Transform parent, Material material,
            float alongMin, float alongMax, float fixedCoord, float wallBottom, float wallHeight,
            float thickness, bool alongX, string idPrefix, string objectIdPrefix,
            params WindowOpening[] openings)
        {
            float wallTop = wallBottom + wallHeight;
            int segment = 0;
            float cursor = alongMin;

            for (int i = 0; i < openings.Length; i++)
            {
                WindowOpening opening = openings[i];

                // 洞口之前的墙（整高）
                float segmentMax = Mathf.Min(opening.AlongMin, alongMax);
                int index = segment;
                if (segmentMax - cursor > 0.02f)
                {
                    CreateWallSegment(parent, material,
                        $"{idPrefix}_段{index}", cursor, segmentMax, fixedCoord,
                        wallBottom, wallTop, thickness, alongX, $"{objectIdPrefix}_s{index}");
                    segment++;
                }

                // 洞口下方的墙（窗台墙）：从墙底到窗台
                if (opening.SillY - wallBottom > 0.02f)
                {
                    CreateWallSegment(parent, material,
                        $"{idPrefix}_窗下{i}", opening.AlongMin, opening.AlongMax, fixedCoord,
                        wallBottom, opening.SillY, thickness, alongX, $"{objectIdPrefix}_below{i}");
                }

                // 洞口上方的墙（窗楣墙）：从窗顶到墙顶
                if (wallTop - opening.HeaderY > 0.02f)
                {
                    CreateWallSegment(parent, material,
                        $"{idPrefix}_窗上{i}", opening.AlongMin, opening.AlongMax, fixedCoord,
                        opening.HeaderY, wallTop, thickness, alongX, $"{objectIdPrefix}_above{i}");
                }

                cursor = Mathf.Max(cursor, Mathf.Min(opening.AlongMax, alongMax));
            }

            // 最后一个洞口之后的墙
            if (alongMax - cursor > 0.02f)
            {
                CreateWallSegment(parent, material,
                    $"{idPrefix}_段{segment}", cursor, alongMax, fixedCoord,
                    wallBottom, wallTop, thickness, alongX, $"{objectIdPrefix}_s{segment}");
            }
        }

        /// <summary>墙段（实心块）。alongMin/Max 是沿墙方向的区间，bottomY/topY 是竖直区间。</summary>
        private static void CreateWallSegment(Transform parent, Material material, string name,
            float alongMin, float alongMax, float fixedCoord, float bottomY, float topY,
            float thickness, bool alongX, string objectId)
        {
            float alongLength = alongMax - alongMin;
            float height = topY - bottomY;
            if (alongLength <= 0.02f || height <= 0.02f) return;

            Vector3 center = alongX
                ? new Vector3((alongMin + alongMax) * 0.5f, (bottomY + topY) * 0.5f, fixedCoord)
                : new Vector3(fixedCoord, (bottomY + topY) * 0.5f, (alongMin + alongMax) * 0.5f);

            Vector3 size = alongX
                ? new Vector3(alongLength, height, thickness)
                : new Vector3(thickness, height, alongLength);

            Game.World.GreyboxPrimitives.CreateBlock(name, parent, center, size,
                material, BuildingObjectType, objectId);
        }

        /// <summary>南北墙（沿 X）上某个窗洞的区间。</summary>
        private static WindowOpening OpeningAlongX(float centerX, float width, float sillY, float height)
            => new WindowOpening(centerX - width * 0.5f, centerX + width * 0.5f, sillY, sillY + height);

        /// <summary>东西墙（沿 Z）上某个窗洞的区间。</summary>
        private static WindowOpening OpeningAlongZ(float centerZ, float width, float sillY, float height)
            => new WindowOpening(centerZ - width * 0.5f, centerZ + width * 0.5f, sillY, sillY + height);

        /// <summary>
        /// 在【南北墙】（墙沿 X 延伸、厚度在 Z）上放玻璃和室内透光。
        /// 【注意】墙体本身由 BuildWallWithOpenings 负责让出洞口，本函数【不建墙】。
        /// </summary>
        private static void BuildWindowAlongX(
            string name, Transform parent, Material wallMaterial,
            float wallZ, float wallThickness,
            float openingCenterX, float openingWidth, float sillY, float openingHeight,
            Material glassMaterial, Game.Environment.DayNightLightingController lighting,
            float inwardSign, string objectIdPrefix)
        {
            WindowOpening opening = OpeningAlongX(openingCenterX, openingWidth, sillY, openingHeight);

            PlaceGlassPlate(name, parent, glassMaterial, wallMaterial,
                new Vector3(opening.AlongCenter, opening.CenterY, wallZ),
                new Vector3(opening.Width * 0.98f, opening.Height * 0.98f, Mathf.Max(0.04f, wallThickness * 0.35f)),
                $"{objectIdPrefix}_glass");

            PlaceWindowSunlight(name, parent, lighting,
                new Vector3(opening.AlongCenter, opening.CenterY, wallZ + inwardSign * (wallThickness * 0.5f + 0.05f)),
                new Vector3(0f, 0f, inwardSign),
                Quaternion.LookRotation(new Vector3(0f, 0f, inwardSign), Vector3.up),
                $"{objectIdPrefix}_sun");
        }

        /// <summary>
        /// 在【东西墙】（墙沿 Z 延伸、厚度在 X）上放玻璃和室内透光。
        /// 与 BuildWindowAlongX 同构，只是把轴换掉。
        /// </summary>
        private static void BuildWindowAlongZ(
            string name, Transform parent, Material wallMaterial,
            float wallX, float wallThickness,
            float openingCenterZ, float openingWidth, float sillY, float openingHeight,
            Material glassMaterial, Game.Environment.DayNightLightingController lighting,
            float inwardSign, string objectIdPrefix)
        {
            WindowOpening opening = OpeningAlongZ(openingCenterZ, openingWidth, sillY, openingHeight);

            PlaceGlassPlate(name, parent, glassMaterial, wallMaterial,
                new Vector3(wallX, opening.CenterY, opening.AlongCenter),
                new Vector3(Mathf.Max(0.04f, wallThickness * 0.35f), opening.Height * 0.98f, opening.Width * 0.98f),
                $"{objectIdPrefix}_glass");

            PlaceWindowSunlight(name, parent, lighting,
                new Vector3(wallX + inwardSign * (wallThickness * 0.5f + 0.05f), opening.CenterY, opening.AlongCenter),
                new Vector3(inwardSign, 0f, 0f),
                Quaternion.LookRotation(new Vector3(inwardSign, 0f, 0f), Vector3.up),
                $"{objectIdPrefix}_sun");
        }

        /// <summary>放玻璃板。材质为 null（shader 没编译过）时用半透明回退材质，避免变粉红。</summary>
        private static void PlaceGlassPlate(string name, Transform parent, Material glassMaterial,
            Material fallbackMaterial, Vector3 center, Vector3 size, string objectId)
        {
            // 玻璃 shader 没编译过时退回不透明结构材质 —— 窗户会难看，但场景不会变粉红。
            Material material = glassMaterial != null ? glassMaterial : fallbackMaterial;

            GameObject glass = Game.World.GreyboxPrimitives.CreateBlock(
                $"{name}_玻璃", parent, center, size, material, BuildingObjectType, objectId);

            // 玻璃不参与碰撞 —— 否则玩家会被窗玻璃挡住（窗户应该能看过去，也能翻）。
            Collider collider = glass.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);

            // 让玻璃渲染在墙面之后，减少半透明排序问题。
            Renderer renderer = glass.GetComponent<Renderer>();
            if (renderer != null) renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>放窗户内侧的"假阳光"。</summary>
        private static void PlaceWindowSunlight(string name, Transform parent, Game.Environment.DayNightLightingController lighting,
            Vector3 position, Vector3 inwardDirection, Quaternion rotation, string objectId)
        {
            Transform sunRoot = Game.World.GreyboxPrimitives.CreateGroup($"{name}_透光", parent);
            GameObject go = sunRoot.gameObject;
            sunRoot.position = position;
            sunRoot.rotation = rotation;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = new Color(1f, 0.96f, 0.86f);
            light.range = 8f;
            light.spotAngle = 62f;
            light.innerSpotAngle = 20f;
            light.shadows = LightShadows.None;
            light.intensity = 0f;

            Game.World.WindowSunlight sunlight = go.AddComponent<Game.World.WindowSunlight>();
            sunlight.ConfigureSunlight(lighting, inwardDirection, 2.4f, 8f, 62f);

            _ = objectId;
        }

        /// <summary>在 X 方向的一段范围内生成墙块（用于带门洞的墙）。</summary>

        /// <summary>
        /// 两层高的外墙外壳，【按层返回】。
        ///
        /// 【为什么要拆成两层】玩家在一楼时，二楼那圈外墙如果和一楼一样透明（0.16），
        /// 在俯视角下会把一楼的视野压住半截。拆开之后可以给更高的楼层更低的透明度
        /// （见 BuildingController._upperShellGhostAlpha）。
        ///
        /// 每层的外壳都挂 BuildingFloor 代理，且 IgnoreFloorVisibilityRules = true ——
        /// 它们的显隐只由 ApplyShellVisibility 控制，不参与楼层规则。
        /// </summary>
        private static Transform[] BuildShellWalls(Transform parent, Material material)
        {
            const float thickness = TwoStoryWallThickness;
            float halfX = TwoStoryHalfX;
            float halfZ = TwoStoryHalfZ;

            const float doorCenterX = -1.5f;
            float halfDoor = TwoStoryDoorWidth * 0.5f;
            float leftMax = doorCenterX - halfDoor;
            float rightMin = doorCenterX + halfDoor;

            // 一楼外壳从楼板顶面起；二楼外壳从二楼楼板顶面起。
            float floor0Base = TwoStoryFloorTop;
            float floor1Base = UpperFloorTop;

            var shells = new Transform[2];

            for (int level = 0; level < 2; level++)
            {
                float baseY = level == 0 ? floor0Base : floor1Base;
                float height = level == 0 ? TwoStoryFloorHeight : TwoStoryFloorHeight;
                float centerY = baseY + height * 0.5f;

                Transform shell = Game.World.GreyboxPrimitives.CreateGroup($"外壳_{level}", parent);
                shells[level] = shell;

                string id = $"house/shell{level}";

                // 本层楼板顶面：窗台高度从这里起算，保证两层一致。
                float sillY = baseY + WindowSillHeight;
                Material glass = GetWindowGlassMaterial();

                // ---- 窗户洞口 ----
                // 东西墙各一扇（这两面墙最长，12 米）；
                // 二楼南北墙各再加一扇 —— 二楼没有门洞，不会和门打架。
                WindowOpening eastWindow = OpeningAlongZ(WindowEastCenterZ, WindowWidth, sillY, WindowHeight);
                WindowOpening westWindow = OpeningAlongZ(WindowWestCenterZ, WindowWidth, sillY, WindowHeight);

                // ---- 南北墙 ----
                if (level == 0)
                {
                    // 一楼南墙留门洞（门在门洞下方，见 BuildTwoStoryDoor）。
                    // 门洞和窗洞一样，必须让墙"跳过"它，而不是先铺满再补 —— 否则会重叠。
                    float doorTop = TwoStoryFloorTop + TwoStoryDoorHeight;

                    BuildStripWall($"{id}_南墙_左", shell, -halfX, leftMax, -halfZ,
                        thickness, height, centerY, material, true, $"{id}_south_l");
                    BuildStripWall($"{id}_南墙_右", shell, rightMin, halfX, -halfZ,
                        thickness, height, centerY, material, true, $"{id}_south_r");

                    float lintelHeight = baseY + height - doorTop;
                    if (lintelHeight > 0.05f)
                    {
                        Game.World.GreyboxPrimitives.CreateBlock($"{id}_南墙_门楣", shell,
                            new Vector3(doorCenterX, doorTop + lintelHeight * 0.5f, -halfZ),
                            new Vector3(TwoStoryDoorWidth, lintelHeight, thickness),
                            material, BuildingObjectType, $"{id}_south_top");
                    }

                    // 一楼北墙：整面（没有窗）
                    BuildWallWithOpenings(shell, material,
                        -halfX, halfX, halfZ, baseY, height, thickness, true,
                        $"{id}_北墙", $"{id}_north");
                }
                else
                {
                    // 二楼：南北墙各开一扇窗
                    BuildWallWithOpenings(shell, material,
                        -halfX, halfX, -halfZ, baseY, height, thickness, true,
                        $"{id}_南墙", $"{id}_south",
                        OpeningAlongX(WindowSouthCenterX, WindowWidth, sillY, WindowHeight));

                    BuildWallWithOpenings(shell, material,
                        -halfX, halfX, halfZ, baseY, height, thickness, true,
                        $"{id}_北墙", $"{id}_north",
                        OpeningAlongX(WindowNorthCenterX, WindowWidth, sillY, WindowHeight));
                }

                // ---- 东西墙（两层都开窗）----
                BuildWallWithOpenings(shell, material,
                    -halfZ, halfZ, halfX, baseY, height, thickness, false,
                    $"{id}_东墙", $"{id}_east", eastWindow);

                BuildWallWithOpenings(shell, material,
                    -halfZ, halfZ, -halfX, baseY, height, thickness, false,
                    $"{id}_西墙", $"{id}_west", westWindow);

                // ---- 玻璃 + 室内透光 ----
                BuildWindowAlongZ($"窗_东_{level}", shell, material,
                    halfX, thickness,
                    WindowEastCenterZ, WindowWidth, sillY, WindowHeight,
                    glass, GetLighting(), -1f, $"{id}_win_e");

                BuildWindowAlongZ($"窗_西_{level}", shell, material,
                    -halfX, thickness,
                    WindowWestCenterZ, WindowWidth, sillY, WindowHeight,
                    glass, GetLighting(), 1f, $"{id}_win_w");

                if (level == 1)
                {
                    BuildWindowAlongX($"窗_南_{level}", shell, material,
                        -halfZ, thickness,
                        WindowSouthCenterX, WindowWidth, sillY, WindowHeight,
                        glass, GetLighting(), 1f, $"{id}_win_s");

                    BuildWindowAlongX($"窗_北_{level}", shell, material,
                        halfZ, thickness,
                        WindowNorthCenterX, WindowWidth, sillY, WindowHeight,
                        glass, GetLighting(), -1f, $"{id}_win_n");
                }
                // 外壳代理：显隐由 ApplyShellVisibility 控制，不参与楼层规则。
                Game.World.BuildingFloor proxy = shell.gameObject.AddComponent<Game.World.BuildingFloor>();
                proxy.ConfigureFloor(int.MaxValue - 10 - level, $"外壳_{level}", shell, shell, shell);
                proxy.IgnoreFloorVisibilityRules = true;
            }

            return shells;
        }

        /// <summary>
        /// 楼梯：StairStepCount 级实心台阶，每级自带 BoxCollider。
        ///
        /// 【几何】坡长 = StairEndZ - StairStartZ = 7 米，总升高 = TwoStoryFloorHeight = 3.35 米。
        ///   每级抬高 3.35 / 13 ≈ 0.258 米，进深 7 / 13 ≈ 0.538 米，坡度约 25.6°。
        ///
        /// 【为什么起点要从一楼楼板顶面算】一楼楼板顶面在 TwoStoryFloorTop（0.3）。
        /// 玩家站在那儿，脚高就是 0.3。所以第一级顶面应该是
        /// TwoStoryFloorTop + rise（而不是从 0 起算的 rise），否则第一级会陷进楼板里。
        ///
        /// 【每级抬高 0.258 米的两条阈值】它同时低于：
        ///   CharacterController.stepOffset（0.4）—— 原生抬腿
        ///   PlayerController._maxStepUp（0.35）—— 显式吸附
        /// 任意一条失效另一条仍能工作（本工程楼梯卡过多轮，根因就是只依赖了其中一条）。
        /// </summary>
        private static void BuildWalkableStairs(Transform parent, Material material)
        {
            float baseY = TwoStoryFloorTop;
            float rise = StairTopY / StairStepCount;
            float run = (StairEndZ - StairStartZ) / StairStepCount;

            Transform steps = Game.World.GreyboxPrimitives.CreateGroup("台阶", parent);

            for (int i = 0; i < StairStepCount; i++)
            {
                int level = Mathf.Min(i + 1, StairStepCount);
                float topY = baseY + level * rise;
                float centerZ = StairStartZ + run * (i + 0.5f);

                // 每级做成一整块从地面到该级顶面的实心方块（侧面看是实心楼梯，不是悬空板）。
                Game.World.GreyboxPrimitives.CreateBlock($"台阶_{i:00}", steps,
                    new Vector3(StairCenterX, topY * 0.5f, centerZ),
                    new Vector3(StairWidth, topY, run),
                    material, PropObjectType, $"house/step_{i:00}");
            }

            // ---- 顶端缓冲踏步 ----
            // 保证玩家接近二楼楼板边缘时脚下已经是二楼楼板高度，
            // 避免身体嵌进楼板实体（本工程踩过这个坑）。
            const float bufferDepth = 0.7f;

            Game.World.GreyboxPrimitives.CreateBlock("台阶_顶端缓冲", steps,
                new Vector3(StairCenterX, (baseY + StairTopY) * 0.5f, StairEndZ - bufferDepth * 0.5f),
                new Vector3(StairWidth, baseY + StairTopY, bufferDepth),
                material, PropObjectType, "house/step_top_buffer");

            // ---- 两侧护栏 ----
            // 楼梯西侧是开放通道，护栏防止玩家从侧面掉下楼梯井；
            // 东侧贴着外墙，也加一道，让楼梯井边界明确。
            float railHeight = baseY + StairTopY + 1.0f;
            float halfSpan = (StairEndZ - StairStartZ) * 0.5f;
            float railCenterZ = (StairStartZ + StairEndZ) * 0.5f;

            for (int side = -1; side <= 1; side += 2)
            {
                Game.World.GreyboxPrimitives.CreateBlock($"楼梯_护栏_{(side < 0 ? "西" : "东")}", parent,
                    new Vector3(StairCenterX + side * (StairWidth * 0.5f + 0.09f),
                                railHeight * 0.5f, railCenterZ),
                    new Vector3(0.18f, railHeight, halfSpan * 2f),
                    material, PropObjectType, $"house/stair_rail_{side}");
            }
        }

        /// <summary>
        /// 梯子：上二楼的【保底通道】。
        ///
        /// 【定位】它【不是】主要上楼方式 —— 楼梯才是。梯子用于：
        ///   1. 楼梯万一还有问题时，仍有一条一定能用的路
        ///   2. 以后做"梯子类道具"时直接复用
        ///
        /// 【为什么不依赖物理】不爬、不碰撞、不投射 —— 按交互键直接切楼层 + 传送到落点。
        ///
        /// 【复用现有体系】用 FloorTransition（继承 InteractableBase），
        /// 所以提示、按键、探测全都是现成的，没有新增输入。
        /// </summary>
        private static void BuildLadder(
            Transform root,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Game.World.BuildingController building)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("梯子", root);

            float ladderHeight = TwoStoryFloorHeight;
            const float railOffset = 0.28f;
            const float rungSpacing = 0.42f;

            // ---- 外观：两根立柱 + 若干横档（靠南墙）----
            Transform visual = Game.World.GreyboxPrimitives.CreateGroup("梯子_外观", group);

            for (int side = -1; side <= 1; side += 2)
            {
                Game.World.GreyboxPrimitives.CreateBlock($"梯子_立柱{(side < 0 ? "西" : "东")}", visual,
                    new Vector3(LadderX + side * railOffset,
                                TwoStoryFloorTop + ladderHeight * 0.5f, LadderBaseZ),
                    new Vector3(0.1f, ladderHeight, 0.1f),
                    palette.Trunk, PropObjectType, $"house/ladder_rail{side}");
            }

            int rungCount = Mathf.FloorToInt(ladderHeight / rungSpacing);

            for (int i = 1; i <= rungCount; i++)
            {
                Game.World.GreyboxPrimitives.CreateBlock($"梯子_横档{i:00}", visual,
                    new Vector3(LadderX, TwoStoryFloorTop + rungSpacing * i, LadderBaseZ),
                    new Vector3(railOffset * 2f, 0.07f, 0.07f),
                    palette.Trunk, PropObjectType, $"house/ladder_rung{i:00}");
            }

            // ---- 两个交互点：上二楼 / 下一楼 ----
            float holeCenterZ = (LadderHoleMinZ + LadderHoleMaxZ) * 0.5f;

            CreateLadderStep(group, building, "梯子_上二楼", 1,
                new Vector3(LadderX, TwoStoryFloorTop + 1.2f, LadderBaseZ + 0.6f),
                new Vector3(LadderX, UpperFloorTop + 0.15f, holeCenterZ),
                "爬上二楼");

            CreateLadderStep(group, building, "梯子_下一楼", 0,
                new Vector3(LadderX, UpperFloorTop + 1.2f, holeCenterZ),
                new Vector3(LadderX, TwoStoryFloorTop + 0.15f, LadderBaseZ + 1.0f),
                "爬下一楼");
        }

        /// <summary>梯子上的一个交互点：把玩家从当前层送到目标层的落点。</summary>
        private static void CreateLadderStep(
            Transform parent,
            Game.World.BuildingController building,
            string name,
            int destinationFloor,
            Vector3 triggerLocalPosition,
            Vector3 landingLocalPosition,
            string promptPrefix)
        {
            GameObject step = new GameObject(name);
            step.transform.SetParent(parent, false);
            step.transform.localPosition = triggerLocalPosition;

            // 交互探测用的是 OverlapSphere + GetComponentInParent，
            // 所以需要一个 Collider 才能被探到。做成触发器，不挡路。
            SphereCollider collider = step.AddComponent<SphereCollider>();
            collider.isTrigger = true;
            collider.radius = 1.1f;

            // 落点：一个空物体，FloorTransition 会读它的 position/rotation。
            GameObject landing = new GameObject($"{name}_落点");
            landing.transform.SetParent(parent, false);
            landing.transform.localPosition = landingLocalPosition;

            Game.World.FloorTransition transition = step.AddComponent<Game.World.FloorTransition>();
            transition.ConfigureTransition(building, destinationFloor, landing.transform, promptPrefix);

            Game.World.GreyboxPrimitives.EnsureObjectId(step,
                $"house/ladder_step_{destinationFloor}", PropObjectType);
        }

        /// <summary>
        /// 一扇平滑开合的房门。
        ///
        /// 【轴向约定】铰链节点的局部 +X 必须【沿着墙】、局部 +Z 指向门要开向的那一侧。
        /// 门板是铰链的子物体，放在局部 (width/2, h/2, 0)，所以它从铰链沿墙面延伸，
        /// 正好盖住洞口。HingedDoor 绕节点 Y 轴旋转，于是门就绕"贴着墙的那条边"开合。
        /// </summary>
        /// <param name="hingeLocalPosition">铰链位置（相对 parent 的局部坐标）。</param>
        /// <param name="hingeYaw">铰链的 Y 旋转（度），决定墙面朝向。</param>
        /// <param name="openDirection">开门方向：+1 / -1。</param>
        private static void BuildPhysicalDoor(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Vector3 hingeLocalPosition,
            float hingeYaw,
            float width,
            float height,
            string name,
            string objectId,
            float openDirection = 1f)
        {
            Transform hinge = Game.World.GreyboxPrimitives.CreateGroup(name, parent, hingeLocalPosition);
            hinge.localRotation = Quaternion.Euler(0f, hingeYaw, 0f);

            Game.World.GreyboxPrimitives.CreateBlock($"{name}_板", hinge,
                new Vector3(width * 0.5f, height * 0.5f, 0f),
                new Vector3(width, height, 0.12f),
                palette.Trunk, PropObjectType, objectId);

            Game.World.HingedDoor door = hinge.gameObject.AddComponent<Game.World.HingedDoor>();
            door.ConfigureDoor(openDirection, openAngle: 88f, openDistance: 2.2f, closeDelay: 1.2f);
        }

        /// <summary>两层测试房屋的房门（南墙、门朝屋里 +Z 开、铰链在洞口西端）。</summary>
        private static void BuildTwoStoryDoor(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            float doorCenterX)
        {
            float halfWidth = TwoStoryDoorWidth * 0.5f;
            float hingeX = doorCenterX - halfWidth;

            BuildPhysicalDoor(parent, palette,
                hingeLocalPosition: new Vector3(hingeX, TwoStoryFloorTop, -TwoStoryHalfZ),
                hingeYaw: 0f,                                   // 局部 +X 即世界 +X（沿南墙）
                width: TwoStoryDoorWidth,
                height: TwoStoryDoorHeight,
                name: "房门",
                objectId: "house/door_panel",
                openDirection: 1f);
        }

        /// <summary>创建一个带 BoxCollider 的触发器对象。</summary>
        private static GameObject CreateTriggerBox(Transform parent, string name, Vector3 localPosition, Vector3 size)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;

            return go;
        }

        // ================================================================== 湖泊

        /// <summary>一个湖泊的定义。</summary>
        private struct LakeDefinition
        {
            public string Name;
            public Vector2 Center;
            public Vector2 Size;
            public float Depth;

            public LakeDefinition(string name, float x, float z, float sizeX, float sizeZ, float depth = 2.2f)
            {
                Name = name;
                Center = new Vector2(x, z);
                Size = new Vector2(sizeX, sizeZ);
                Depth = depth;
            }
        }

        /// <summary>
        /// 湖泊分布。
        ///
        /// 摆放原则：尽量落在原本没有树/石的空白处，正对建筑和围墙留出余量。
        /// 仍然落在湖里的道具会被 ValidatePropsAgainstBuildings 自动删除并在 Console 报告。
        ///
        /// 地图范围是 ±30（可行走 ±29.4）。
        /// </summary>
        private static readonly LakeDefinition[] Lakes =
        {
            new LakeDefinition("湖泊_东南", 17f, -17f, 13f, 9f),
            new LakeDefinition("湖泊_西南", -19f, -17f, 10f, 7f),
            new LakeDefinition("湖泊_东北", 20f, 12f, 9f, 7f),
            new LakeDefinition("水塘_西", -25f, -2f, 6f, 5f, 1.6f)
        };

        /// <summary>地形上要开的洞（比湖盆略小，让湖盆外圈压在洞的边缘上、不留缝）。</summary>
        private static Game.World.GreyboxPrimitives.TerrainHole[] BuildTerrainHoles()
        {
            var holes = new Game.World.GreyboxPrimitives.TerrainHole[Lakes.Length];

            for (int i = 0; i < Lakes.Length; i++)
            {
                // 0.95 倍：湖盆会按洞尺寸 + 2 米生成，所以外圈能压在洞口外面。
                holes[i] = new Game.World.GreyboxPrimitives.TerrainHole(Lakes[i].Center, Lakes[i].Size * 0.95f);
            }

            return holes;
        }

        /// <summary>
        /// 生成所有湖泊。
        ///
        /// 每个湖泊三部分：
        ///   湖盆   —— 自己生成的碗形网格（顶点色从岸边泥色渐变到深处暗色），带碰撞
        ///   水面   —— 自己生成的细分网格 + 水体 shader（Gerstner 波在顶点着色器里做）
        ///   地形洞 —— 由 BuildGround 一次性开好（见 BuildTerrainHoles）
        /// </summary>
        private static void BuildLakes(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Game.World.MapAreaDefinition area)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("湖泊", parent);

            Material waterMaterial = Game.World.GreyboxPrimitives.GetOrCreateWaterMaterial(
                shallow: new Color(0.16f, 0.42f, 0.45f, 0.62f),
                deep: new Color(0.02f, 0.09f, 0.16f, 0.97f));

            if (waterMaterial == null)
            {
                waterMaterial = palette.Ground;
                Debug.LogWarning("[灰盒] 水体材质不可用，湖面使用地面材质代替（看起来会像一块干地）。");
            }

            for (int i = 0; i < Lakes.Length; i++)
            {
                BuildSingleLake(group, palette, waterMaterial, area, Lakes[i], i);
            }
        }

        private static void BuildSingleLake(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Material waterMaterial,
            Game.World.MapAreaDefinition area,
            LakeDefinition lake,
            int index)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup(lake.Name, parent);

            Vector2 holeSize = lake.Size * 0.95f;
            // 湖盆比洞口大 2 米：多出来的外圈压在 y=0 上，把地形缺口盖住，看不到缝。
            Vector2 bedSize = holeSize + new Vector2(2f, 2f);

            // ---- 湖盆 ----
            GameObject bed = new GameObject("湖盆");
            bed.transform.SetParent(group, false);
            bed.transform.localPosition = new Vector3(lake.Center.x, 0f, lake.Center.y);

            Mesh bedMesh = Game.World.GreyboxPrimitives.BuildLakeBedMesh(
                bedSize.x, bedSize.y, lake.Depth, slopeFraction: 0.55f,
                shoreColor: new Color(0.36f, 0.30f, 0.22f),
                shallowColor: new Color(0.40f, 0.37f, 0.26f),
                deepColor: new Color(0.09f, 0.11f, 0.10f),
                seed: 7413 + index * 977);

            MeshFilter bedFilter = bed.AddComponent<MeshFilter>();
            bedFilter.sharedMesh = bedMesh;

            MeshRenderer bedRenderer = bed.AddComponent<MeshRenderer>();
            bedRenderer.sharedMaterial = palette.Ground;

            MeshCollider bedCollider = bed.AddComponent<MeshCollider>();
            bedCollider.sharedMesh = bedMesh;
            bedCollider.convex = false;

            Game.World.GreyboxPrimitives.EnsureObjectId(bed, $"{area.MapId}/湖泊/{index:00}/湖盆", GroundObjectType);

            // ---- 水面 ----
            // 水面范围比洞口略大一点，避免从岸上看时边缘露出缺口。
            Game.World.GreyboxPrimitives.CreateWaterSurface(
                "水面", group, lake.Center, holeSize + new Vector2(0.6f, 0.6f),
                waterLevel: -lake.Depth * 0.45f,
                material: waterMaterial,
                objectType: "water");
        }

        private static void BuildStructures(Transform parent, Game.World.GreyboxPrimitives.GreyboxPalette palette)
        {
            Transform group = Game.World.GreyboxPrimitives.CreateGroup("结构", parent);

            // L 形围墙：测试"绕过去才能看到后面"的遮挡关系
            Game.World.GreyboxPrimitives.CreateWall("L形墙_A", group, new Vector3(2f, 0f, 4f), new Vector3(2f, 0f, 10f), 0.35f, 2.6f, palette.Structure, WallObjectType, "world/wall/l_a");
            Game.World.GreyboxPrimitives.CreateWall("L形墙_B", group, new Vector3(2f, 0f, 10f), new Vector3(8f, 0f, 10f), 0.35f, 2.6f, palette.Structure, WallObjectType, "world/wall/l_b");

            // 集装箱：站在上面 vs 站在旁边的比例参照
            Game.World.GreyboxPrimitives.CreateBlock("集装箱", group, new Vector3(-4f, 1.3f, -6f), new Vector3(2.5f, 2.6f, 6f), palette.Crate, BuildingObjectType, "world/container/001");
        }

        /// <summary>树的形态。灰盒阶段用最少的方块表达"这是不同的树"。</summary>
        private enum TreeShape
        {
            /// <summary>单块树冠。</summary>
            Single,

            /// <summary>两块树冠错位堆叠，看起来更蓬松。</summary>
            Double,

            /// <summary>光秃树：只有树干 + 两根斜树杈，没有叶子。</summary>
            Bare
        }

        private static void BuildProps(Transform parent, Game.World.GreyboxPrimitives.GreyboxPalette palette, Items.ItemDefinition woodItem, Game.World.MapAreaDefinition area)
        {
            Transform trees = Game.World.GreyboxPrimitives.CreateGroup("树木", parent);
            Transform rocks = Game.World.GreyboxPrimitives.CreateGroup("石头", parent);
            Transform crates = Game.World.GreyboxPrimitives.CreateGroup("箱子", parent);

            // 树的位置 + 形态 + 叶色。
            // 灰盒阶段用"形态 + 颜色"的组合来表达树种差异，比每种树做一套模型省事得多。
            // 坐标都在可行走范围内（±29.4），并且避开了建筑、围墙、集装箱和箱子堆。
            var treeData = new (float X, float Z, TreeShape Shape, bool Yellow)[]
            {
                // ---- 开局点附近：四种树各来一棵，方便一眼对比 ----
                (-2f,   2f,  TreeShape.Single, false),
                ( 4f,   6f,  TreeShape.Double, false),
                (-6f,  -2f,  TreeShape.Bare,   false),
                ( 3f,  -8f,  TreeShape.Single, true),

                // ---- 西侧 ----
                (-16f, -4f,  TreeShape.Single, false),
                (-19f,  2f,  TreeShape.Double, true),
                (-23f, -9f,  TreeShape.Bare,   false),
                (-13f, -16f, TreeShape.Single, false),
                (-25f, 11f,  TreeShape.Double, false),

                // ---- 东侧 ----
                ( 15f,  3f,  TreeShape.Double, true),
                ( 19f,  9f,  TreeShape.Single, false),
                ( 23f, -4f,  TreeShape.Bare,   false),
                ( 20f, -15f, TreeShape.Single, true),

                // ---- 南侧 ----
                ( 7f, -17f,  TreeShape.Double, false),
                (-5f, -20f,  TreeShape.Single, true),
                ( 13f, -22f, TreeShape.Bare,   false),
                ( 15f, -4f,  TreeShape.Single, false),

                // ---- 北侧 ----
                ( 7f,  13f,  TreeShape.Single, false),
                (-2f,  17f,  TreeShape.Double, true),
                ( 9f,  19f,  TreeShape.Bare,   false),
                (14f,  15f,  TreeShape.Single, true),
                (-8f,  22f,  TreeShape.Double, false),
                (18f,  21f,  TreeShape.Double, false),
                // 原本在 (-17, 17)：离两层房屋（中心 -20,20、半宽 5、半深 4）只有 0.2 米，
                // 树会直接长进房子里。挪到这里。
                (-20f, 27f,  TreeShape.Single, true)
            };

            for (int i = 0; i < treeData.Length; i++)
            {
                (float x, float z, TreeShape shape, bool yellow) = treeData[i];
                CreateTree(trees, palette, woodItem, area, i, new Vector3(x, 0f, z), shape, yellow);
            }

            // 石头：低矮体块，用来对比地面细节
            Vector3[] rockData =
            {
                new Vector3(-8f, 0.35f, 6f),
                new Vector3(9f, 0.30f, 8f),
                new Vector3(6f, 0.45f, -8f),
                new Vector3(-11f, 0.25f, -7f),
                new Vector3(0f, 0.30f, 12f),
                new Vector3(-21f, 0.30f, -3f),
                new Vector3(17f, 0.30f, 13f),
                new Vector3(-9f, 0.28f, -13f)
            };

            for (int i = 0; i < rockData.Length; i++)
            {
                Vector3 p = rockData[i];
                Game.World.GreyboxPrimitives.CreateBlock($"石头_{i:00}", rocks, p,
                    new Vector3(2.0f + i % 3 * 0.4f, p.y * 2f, 1.6f + i % 2 * 0.5f), palette.Rock, PropObjectType, $"world/rock/{i:000}");
            }

            // 箱子：标准 1m 立方，人 / 门 / 箱子的比例一眼可判
            Vector3[] crateData =
            {
                new Vector3(2f, 0.5f, -4f),
                new Vector3(3.05f, 0.5f, -4.4f),
                new Vector3(2.5f, 1.5f, -4.1f),
                new Vector3(10f, 0.5f, -14f),
                new Vector3(-14f, 0.5f, 12f)
            };

            for (int i = 0; i < crateData.Length; i++)
            {
                GameObject crate = Game.World.GreyboxPrimitives.CreateBlock($"箱子_{i:00}", crates, crateData[i],
                    new Vector3(1f, 1f, 1f), palette.Crate, PropObjectType, $"world/crate/{i:000}");

                // 箱子也做成可搜刮：验证 IInteractable 能容纳"和树完全不同的交互方式"（一次性 vs 重复采集）。
                InteractableCrate loot = crate.AddComponent<InteractableCrate>();
                loot.ConfigureLoot(woodItem, 2, true);
            }

            // 全部道具生成完再统一校验一次：有没有树/石头/箱子插进建筑或湖泊里。
            // 这类问题肉眼在俯视图里很难发现（被屋顶挡住），但站在屋里就会看到树穿墙。
            ValidatePropsAgainstBuildings(trees, rocks, crates);
        }

        /// <summary>
        /// 检查道具（树、石头、箱子）有没有插进建筑或湖泊里；**有就删掉并报告**。
        ///
        /// 为什么用"自动删除 + 报告"而不是"只报警告"：
        ///   俯视图里房屋被屋顶挡住，很难看出"树长进墙里"；湖泊同理（树会长在水面上）。
        ///   只报警告的话，重叠的物体依然留在场景里 —— 玩家走进屋/跳进湖才发现。
        ///   自动删掉能保证生成结果一定是干净的，同时把删了什么打出来，
        ///   提醒你去修正 treeData / rockData / crateData 里的源坐标。
        /// </summary>
        private static void ValidatePropsAgainstBuildings(params Transform[] propGroups)
        {
            // 已知的"占地物"范围（名字 + 中心 + 半宽/半深）。
            // 以后建筑变多时应该改成从 BuildingController 遍历 —— 目前只有固定几栋，写死更直白。
            var staticFootprints = new (string Name, Vector2 Center, Vector2 HalfSize)[]
            {
                ("两层测试房屋", new Vector2(-20f, 20f), new Vector2(TwoStoryHalfX, TwoStoryHalfZ))
            };

            // 湖泊与格子房都是动态列表，运行时拼进来。
            // 格子房的占地范围直接从 ASCII 图纸算 —— 改图纸就自动跟上，不用手写尺寸。
            int gridHouseCount = GridHouseLayouts.Length;
            var footprints = new (string Name, Vector2 Center, Vector2 HalfSize)[
                staticFootprints.Length + Lakes.Length + gridHouseCount];

            System.Array.Copy(staticFootprints, footprints, staticFootprints.Length);

            for (int i = 0; i < Lakes.Length; i++)
            {
                footprints[staticFootprints.Length + i] = (Lakes[i].Name, Lakes[i].Center, Lakes[i].Size * 0.5f);
            }

            for (int i = 0; i < gridHouseCount; i++)
            {
                var entry = GridHouseLayouts[i];
                Game.World.GridHouseModel model = new Game.World.GridHouseModel(entry.Name, entry.Layout);

                Vector2 halfSize = new Vector2(model.Width * GridCellSize * 0.5f,
                                               model.Depth * GridCellSize * 0.5f);

                footprints[staticFootprints.Length + Lakes.Length + i] = (entry.Name, entry.Position, halfSize);
            }

            const float clearance = 1.0f;   // 树冠半径之外再留一点余量
            int removed = 0;

            foreach (Transform group in propGroups)
            {
                if (group == null) continue;

                // 倒序遍历：删除子物体时不会打乱后面的下标。
                for (int i = group.childCount - 1; i >= 0; i--)
                {
                    Transform prop = group.GetChild(i);
                    Vector2 position = new Vector2(prop.localPosition.x, prop.localPosition.z);

                    bool overlap = false;
                    string culprit = null;
                    float gap = 0f;

                    foreach (var footprint in footprints)
                    {
                        Vector2 delta = position - footprint.Center;

                        // 到外壳的最近距离（小于 0 表示在内部）
                        float dx = Mathf.Abs(delta.x) - footprint.HalfSize.x;
                        float dz = Mathf.Abs(delta.y) - footprint.HalfSize.y;
                        float distance = Mathf.Max(dx, dz);

                        if (distance >= clearance) continue;

                        overlap = true;
                        culprit = footprint.Name;
                        gap = distance;
                        break;
                    }

                    if (!overlap) continue;

                    removed++;
                    Debug.LogWarning(
                        $"[灰盒] 已删除「{prop.name}」({position.x}, {position.y}) —— " +
                        $"它和「{culprit}」重叠或过近（间隙 {gap:F2} 米，要求至少 {clearance} 米）。");

                    UnityEngine.Object.DestroyImmediate(prop.gameObject);
                }
            }

            if (removed > 0)
            {
                Debug.LogWarning(
                    $"[灰盒] 共删除 {removed} 个与建筑/湖泊重叠的道具。" +
                    "建议修正 treeData / rockData / crateData 里的源坐标，让生成结果本身是干净的。");
            }
        }

        /// <summary>
        /// 造一棵树。三种形态共用同一套结构：
        ///   父物体 = 树干 + 碰撞体 + InteractableTree（可砍）
        ///   子物体 = 树冠 / 树杈（纯外观，母体缩放时跟着一起动）
        /// </summary>
        private static void CreateTree(
            Transform parent,
            Game.World.GreyboxPrimitives.GreyboxPalette palette,
            Items.ItemDefinition woodItem,
            Game.World.MapAreaDefinition area,
            int index,
            Vector3 position,
            TreeShape shape,
            bool yellowLeaves)
        {
            string name = $"树_{index:00}_{ShapeLabel(shape)}{(yellowLeaves ? "_黄" : string.Empty)}";

            // 每棵树的高度/大小做一点随机，避免整片林子看起来像复制粘贴。
            float trunkHeight = 3.2f + (index % 4) * 0.45f;
            float trunkWidth = 0.36f + (index % 3) * 0.05f;
            Material leafMaterial = yellowLeaves ? palette.YellowCanopy : palette.Canopy;

            GameObject treeRoot = new GameObject(name);
            treeRoot.transform.SetParent(parent, false);
            treeRoot.transform.localPosition = position;

            Game.World.GreyboxPrimitives.CreateBlock($"{name}_树干", treeRoot.transform,
                new Vector3(0f, trunkHeight * 0.5f, 0f),
                new Vector3(trunkWidth, trunkHeight, trunkWidth),
                palette.Trunk, TreeObjectType, $"{name}/树干");

            switch (shape)
            {
                case TreeShape.Single:
                {
                    // 一个"不规则球"：大致是球形，但表面有几个鼓包（用固定种子，重建后长得一样）。
                    float radius = 1.4f + (index % 2) * 0.35f;
                    Game.World.GreyboxPrimitives.CreateBlob($"{name}_树冠", treeRoot.transform,
                        new Vector3(0f, trunkHeight + radius * 0.62f, 0f),
                        radius,
                        leafMaterial,
                        index * 977 + 13,
                        lobeCount: 3,
                        noiseAmount: 0.24f,
                        objectType: TreeObjectType,
                        objectId: $"{name}/树冠");
                    break;
                }

                case TreeShape.Double:
                {
                    // 两个不规则球叠成"结合体"：下面的大、上面的小，各自有独立鼓包。
                    float lower = 1.20f + (index % 2) * 0.20f;
                    float upper = lower * 0.78f;
                    float offset = lower * 0.34f;

                    Game.World.GreyboxPrimitives.CreateBlob($"{name}_树冠下", treeRoot.transform,
                        new Vector3(-offset, trunkHeight + lower * 0.55f, offset * 0.5f),
                        lower,
                        leafMaterial,
                        index * 977 + 31,
                        lobeCount: 3,
                        noiseAmount: 0.24f,
                        objectType: TreeObjectType,
                        objectId: $"{name}/树冠下");

                    Game.World.GreyboxPrimitives.CreateBlob($"{name}_树冠上", treeRoot.transform,
                        new Vector3(offset * 0.85f, trunkHeight + lower * 1.15f, -offset * 0.4f),
                        upper,
                        leafMaterial,
                        index * 977 + 67,
                        lobeCount: 2,
                        noiseAmount: 0.24f,
                        objectType: TreeObjectType,
                        objectId: $"{name}/树冠上");
                    break;
                }

                case TreeShape.Bare:
                {
                    // 光秃树：两根斜向树杈。用旋转过的细长方块表达"树杈"。
                    float branchLength = 1.5f + (index % 3) * 0.35f;
                    float branchWidth = trunkWidth * 0.45f;

                    CreateBranch($"{name}_树杈_A", treeRoot.transform, trunkHeight * 0.82f, branchLength, branchWidth,
                        new Vector3(35f, 25f, 0f), palette.Trunk);
                    CreateBranch($"{name}_树杈_B", treeRoot.transform, trunkHeight * 0.95f, branchLength * 0.85f, branchWidth,
                        new Vector3(-30f, 200f, 15f), palette.Trunk);
                    break;
                }
            }

            Game.World.GreyboxPrimitives.EnsureObjectId(treeRoot, $"{area.MapId}/tree/{index:000}", TreeObjectType);

            // 碰撞体按树干给（树冠不给，避免头顶出现悬空空气墙）。
            BoxCollider trunkCollider = treeRoot.AddComponent<BoxCollider>();
            trunkCollider.center = new Vector3(0f, trunkHeight * 0.5f, 0f);
            trunkCollider.size = new Vector3(trunkWidth * 1.6f, trunkHeight, trunkWidth * 1.6f);

            InteractableTree interactable = treeRoot.AddComponent<InteractableTree>();
            interactable.ConfigureYield(woodItem, 1, 3);
            interactable.ConfigureTrunkSize(trunkHeight, trunkWidth);
        }

        /// <summary>用一根旋转的细长方块表示树杈。</summary>
        private static void CreateBranch(string name, Transform parent, float baseHeight, float length, float width, Vector3 euler, Material material)
        {
            GameObject branch = Game.World.GreyboxPrimitives.CreateBlock(name, parent,
                Vector3.zero, new Vector3(width, length, width), material, TreeObjectType, name);

            // 从树干上的一点朝外伸出：先把旋转中心移到方块底部，再整体旋转。
            branch.transform.localRotation = Quaternion.Euler(euler);
            branch.transform.localPosition = new Vector3(0f, baseHeight, 0f)
                                             + branch.transform.localRotation * new Vector3(0f, length * 0.5f, 0f);
        }

        private static string ShapeLabel(TreeShape shape)
        {
            switch (shape)
            {
                case TreeShape.Double: return "双叶";
                case TreeShape.Bare: return "枯枝";
                default: return "单叶";
            }
        }

        // ---------------------------------------------------------------- 玩家与相机

        private static GameObject BuildPlayer(Game.World.MapAreaDefinition area, Game.World.AreaBounds bounds)
        {
            GameObject player = FindPlayer();
            if (player == null)
            {
                player = new GameObject("玩家");
            }

            player.tag = "Player";
            player.transform.position = area.DefaultSpawnPosition;
            player.transform.rotation = Quaternion.Euler(0f, area.DefaultSpawnYaw, 0f);

            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller == null) controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.slopeLimit = 50f;
            controller.stepOffset = 0.4f;
            controller.skinWidth = 0.03f;
            controller.minMoveDistance = 0f;

            // 占位外观：胶囊（前方的小方块指示朝向）
            Transform visual = player.transform.Find("外观");
            if (visual == null)
            {
                GameObject visualGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                visualGo.name = "外观";
                visualGo.transform.SetParent(player.transform, false);
                visualGo.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                visualGo.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);

                // 角色自身用 CharacterController 做碰撞，避免和占位体重复碰撞。
                Collider visualCollider = visualGo.GetComponent<Collider>();
                if (visualCollider != null) Object.DestroyImmediate(visualCollider);

                visual = visualGo.transform;

                Game.World.GreyboxPrimitives.CreateBlock("朝向标记", visual,
                    new Vector3(0f, 0.9f, 0.5f), new Vector3(0.18f, 0.18f, 0.6f), null, "player", "player/朝向标记");
            }

            if (player.GetComponent<PlayerInputSource>() == null) player.AddComponent<PlayerInputSource>();
            if (player.GetComponent<PlayerController>() == null) player.AddComponent<PlayerController>();

            // 生命值：必须在 InteractionHud 之前加 —— HUD 的 Awake 会 GetComponent<PlayerHealth>()，
            // 晚加的话 HUD 就取不到，血条不会显示。
            if (player.GetComponent<PlayerHealth>() == null) player.AddComponent<PlayerHealth>();

            // 交互三件套：背包（存东西）、探测器（找目标并按 E）、HUD（显示提示与背包内容）。
            // 顺序有讲究：PlayerInventory 必须在 HUD 之前，否则 HUD 的 Awake 取不到背包。
            if (player.GetComponent<PlayerInventory>() == null) player.AddComponent<PlayerInventory>();
            if (player.GetComponent<InteractionProbe>() == null) player.AddComponent<InteractionProbe>();
            if (player.GetComponent<InteractionHud>() == null) player.AddComponent<InteractionHud>();

            // 背包界面（4×4 背包 + 4 格口袋 + 右侧主角模型展示）。同样必须在 PlayerInventory 之后。
            if (player.GetComponent<InventoryScreen>() == null) player.AddComponent<InventoryScreen>();

            // 世界空间血条：给所有"有血量的东西"画血条（训练木桩 + 敌人）。
            // 挂在玩家身上 —— 它需要相机，而且只该有一个实例。
            if (player.GetComponent<Game.Player.HealthBarHud>() == null)
            {
                player.AddComponent<Game.Player.HealthBarHud>();
            }

            // 手枪：挂在一个子物体上，位置代表"枪在手上的位置"。
            // 不用真的做手部骨骼 —— 灰盒阶段只需要一个能开火的组件。
            BuildPistol(player);

            // 主角模型预览装置：以玩家的外观为源，用离屏相机渲染成贴图给界面显示。
            // 放在玩家下面会被玩家带着走，所以挂到场景根节点上。
            BuildCharacterPreview(player);

            PlayerController playerController = player.GetComponent<PlayerController>();
            playerController.Area = bounds;

            return player;
        }

        /// <summary>
        /// 生成主角模型预览装置（背包界面右侧显示的那个）。
        /// 装置挂在场景根节点，不跟着玩家移动；模型取自玩家的外观层级。
        ///
        /// 【为什么要按名字找，而不是只按组件类型找】
        /// FindFirstObjectByType 默认【不搜索未激活的对象】，而且找不到就 new 一个 ——
        /// 于是"场景里已经有装置、但它的组件处于未激活/被禁用状态"时，
        /// 每次重建都会再造一套，越积越多（本工程实际积累到 18 套：18 个相机 + 34 盏灯）。
        ///
        /// 现在先用名字找根物体（名字查找不受激活状态影响），找到就复用；
        /// 只有真的不存在时才新建。这样重复不会继续增长。
        /// </summary>
        private static void BuildCharacterPreview(GameObject player)
        {
            const string previewRootName = "主角模型预览";

            // 先按名字找（不受激活状态影响）
            GameObject rigGo = GameObject.Find(previewRootName);

            // 再按组件类型兜底（名字被改过的情况），并包含未激活对象
            if (rigGo == null)
            {
                Game.Player.CharacterPreviewRig existing =
                    Object.FindFirstObjectByType<Game.Player.CharacterPreviewRig>(FindObjectsInactive.Include);
                if (existing != null) rigGo = existing.gameObject;
            }

            if (rigGo == null)
            {
                rigGo = new GameObject(previewRootName);
            }

            Game.Player.CharacterPreviewRig rig = rigGo.GetComponent<Game.Player.CharacterPreviewRig>();
            if (rig == null) rig = rigGo.AddComponent<Game.Player.CharacterPreviewRig>();

            // 用玩家的"外观"子物体作为模型来源（不是玩家本体，本体上有碰撞体和脚本）。
            Transform visual = player.transform.Find("外观");
            rig.SetSourceFrom(visual != null ? visual.gameObject : player);
        }

        private static void BuildCameraRig(GameObject player)
        {
            // 用类型查找而不是名字，改名不会破坏工具。
            Game.CameraSystem.TopDownCameraRig rig = Object.FindFirstObjectByType<Game.CameraSystem.TopDownCameraRig>();

            if (rig == null)
            {
                GameObject cameraGo = GameObject.FindGameObjectWithTag("MainCamera");
                if (cameraGo == null)
                {
                    cameraGo = new GameObject("主相机");
                    cameraGo.tag = "MainCamera";
                    cameraGo.AddComponent<Camera>();
                    cameraGo.AddComponent<AudioListener>();
                }

                GameObject boom = new GameObject("相机吊臂");
                boom.transform.SetParent(cameraGo.transform, false);
                rig = boom.AddComponent<Game.CameraSystem.TopDownCameraRig>();

                GameObject lookTarget = new GameObject("视线点");
                lookTarget.transform.SetParent(boom.transform, false);

                SerializedObject rigSerialized = new SerializedObject(rig);
                SetReference(rigSerialized, "_boom", boom.transform);
                SetReference(rigSerialized, "_lookTarget", lookTarget.transform);
                SetReference(rigSerialized, "_cameraTransform", cameraGo.transform);
                SetReference(rigSerialized, "_camera", cameraGo.GetComponent<Camera>());
                rigSerialized.ApplyModifiedPropertiesWithoutUndo();
            }

            // 显式注入引用与镜头参数（不再依赖运行时 Find）。
            //
            // 【为什么这些值要在这里写一遍】它们是 [SerializeField]，会被序列化进场景。
            // 只在 C# 里改默认值的话，已经存在的组件仍用旧的序列化值 ——
            // 表现就是"改了代码但画面没变"（本工程踩过好几次）。
            SerializedObject serialized = new SerializedObject(rig);
            SetReference(serialized, "_target", player.transform);
            SetFloat(serialized, "_lookHeight", 1.4f);
            SetFloat(serialized, "_targetScreenAnchor", 0.6f);

            // 斜俯视构图：后撤 8 / 高度 16.5 → 约 62°，
            // 方位角 45° 让地图在画面里斜过来（《地球末日》那种等距视角）。
            SetFloat(serialized, "_backDistance", 8f);
            SetFloat(serialized, "_height", 16.5f);
            SetFloat(serialized, "_yawDegrees", 45f);

            SetVector2(serialized, "_pitchLimits", new Vector2(20f, 85f));
            SetFloat(serialized, "_fieldOfView", 50f);
            SetFloat(serialized, "_followSharpness", 14f);
            SetBool(serialized, "_followTargetYaw", false);
            SetReference(serialized, "_autoTargetSource", player.GetComponent<PlayerController>());
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // 玩家按屏幕方向移动，需要相机的朝向作参考系。
            PlayerInputSource input = player.GetComponent<PlayerInputSource>();
            Camera mainCamera = Camera.main;
            if (input != null && mainCamera != null)
            {
                input.SetMovementReference(mainCamera.transform);
            }
        }

        // ---------------------------------------------------------------- 工具

        // 下面这组 Set* 都做了 null 检查。
        // 原因：SerializedObject.FindProperty 在字段名写错时返回 null，直接取 .floatValue 会
        // 抛 NullReferenceException —— 编译器查不出来（字符串不是强类型），
        // 结果就是"点了菜单直接报错"。这里改成告警，让错误可定位、不中断整个生成流程。

        private static SerializedProperty Find(SerializedObject serialized, string fieldName)
        {
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                Debug.LogWarning($"[灰盒] 字段「{fieldName}」不存在于 {serialized.targetObject.GetType().Name} 上，已跳过。");
            }

            return property;
        }

        private static void SetReference(SerializedObject serialized, string fieldName, Object value)
        {
            SerializedProperty property = Find(serialized, fieldName);
            if (property != null) property.objectReferenceValue = value;
        }

        private static void SetFloat(SerializedObject serialized, string fieldName, float value)
        {
            SerializedProperty property = Find(serialized, fieldName);
            if (property != null) property.floatValue = value;
        }

        private static void SetBool(SerializedObject serialized, string fieldName, bool value)
        {
            SerializedProperty property = Find(serialized, fieldName);
            if (property != null) property.boolValue = value;
        }

        private static void SetVector2(SerializedObject serialized, string fieldName, Vector2 value)
        {
            SerializedProperty property = Find(serialized, fieldName);
            if (property != null) property.vector2Value = value;
        }

        private static void SetVector3(SerializedObject serialized, string fieldName, Vector3 value)
        {
            SerializedProperty property = Find(serialized, fieldName);
            if (property != null) property.vector3Value = value;
        }

        /// <summary>只清理生成器自己的子节点，不碰用户手动加在别处的对象。</summary>
        private static void ClearChildren(Transform root)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (Application.isPlaying) Object.Destroy(child.gameObject);
                else Object.DestroyImmediate(child.gameObject);
            }
        }
    }
}
#endif
