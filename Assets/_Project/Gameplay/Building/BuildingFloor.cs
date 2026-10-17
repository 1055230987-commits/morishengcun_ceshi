using UnityEngine;
using Game.Rendering;

namespace Game.World
{
    /// <summary>楼层的显示方式。用枚举而不是 bool，是为了以后能加更多档（例如"只显示轮廓"）。</summary>
    public enum FloorVisibility
    {
        /// <summary>完全隐藏：渲染与交互关闭，碰撞保留。</summary>
        Hidden = 0,

        /// <summary>半透明显示：能看到轮廓但不遮挡视线。交互关闭，碰撞保留。</summary>
        Ghosted = 1,

        /// <summary>完全显示：渲染、碰撞、交互全部开启。</summary>
        Visible = 2
    }

    /// <summary>
    /// 一层楼。
    ///
    /// 只负责"这一层怎么显示"，不负责判断该显示哪层 —— 那个决策属于 BuildingController。
    /// 这样拆开的好处：以后加地下室、三楼时，BuildingController 的规则只写一处，
    /// BuildingFloor 完全不用改。
    ///
    /// 注意：楼层索引和 Y 高度是两件事。floorIndex 是逻辑编号（-1 地下室、0 一楼、1 二楼……），
    /// Y 高度只由 transform 决定。**任何逻辑判断都不许读 transform.position.y。**
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingFloor : MonoBehaviour
    {
        [Header("楼层")]
        [Tooltip("逻辑楼层号。约定：-1 地下室、0 一楼、1 二楼、2 三楼……\n" +
                 "同一栋建筑内不可重复。")]
        [SerializeField] private int _floorIndex;

        [Tooltip("楼层显示名，只用于交互提示（例如「上二楼」「下一楼」）。逻辑上不使用它。")]
        [SerializeField] private string _displayName = "一楼";

        [Header("内容根节点（留空则用本物体）")]
        [Tooltip("这一层所有可见内容（墙、家具等）的父节点。留空 = 本物体自己。")]
        [SerializeField] private Transform _renderRoot;

        [Tooltip("这一层所有【参与阻挡】的碰撞体所在节点。留空 = 本物体。\n" +
                 "把「阻挡」这一类单独给根节点，是为了隐藏楼层时能一并关掉，\n" +
                 "避免玩家在二楼被一楼的隐藏墙体挡住。")]
        [SerializeField] private Transform _colliderRoot;

        [Tooltip("这一层所有可交互物的父节点。留空 = 本物体。")]
        [SerializeField] private Transform _interactionRoot;

        [Tooltip("这一层的【结构件】父节点（楼板、隔墙、天花板、楼梯）。\n" +
                 "隐藏本层时它的渲染会一起关掉，但碰撞【始终保留】——\n" +
                 "这样玩家在一楼时看不到二楼的任何东西，同时站在二楼时脚下依然有支撑。")]
        [SerializeField] private Transform _solidStructureRoot;

        [Header("半透明设置")]
        [Tooltip("「半透明显示」时的整体透明度。")]
        [SerializeField, Range(0.05f, 1f)] private float _ghostAlpha = 0.28f;

        private Renderer[] _renderers;
        private Renderer[] _solidRenderers;
        private Collider[] _colliders;
        private Behaviour[] _interactables;
        private RendererFadeMaterials _fadeMaterials;
        private bool _contentsDirty = true;
        private FloorVisibility _visibility = FloorVisibility.Visible;

        public int FloorIndex => _floorIndex;
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? $"楼层 {_floorIndex}" : _displayName;
        public FloorVisibility Visibility => _visibility;

        public Transform RenderRoot => _renderRoot != null ? _renderRoot : transform;
        public Transform ColliderRoot => _colliderRoot != null ? _colliderRoot : transform;
        public Transform InteractionRoot => _interactionRoot != null ? _interactionRoot : transform;

        /// <summary>结构件根节点（可能为 null，表示"没有单独的结构件"）。</summary>
        public Transform SolidStructureRoot => _solidStructureRoot;

        /// <summary>三个根节点是否都已配置。没配的话缓存内容会波及整栋建筑，必须拦住。</summary>
        public bool IsConfigured => _renderRoot != null && _colliderRoot != null;

        private void Awake()
        {
            CacheContents();
        }

        /// <summary>
        /// 收集这一层的内容引用。
        ///
        /// 只在配置变化或 Awake 时做一次 —— 每帧 GetComponentsInChildren 会很慢。
        /// 之所以需要"重新缓存"：场景生成器是先 AddComponent 再 ConfigureFloor 的，
        /// 如果 Awake 在配置之前跑过，缓存到的就是错的节点（甚至整栋建筑）。
        /// </summary>
        public void CacheContents()
        {
            ReleaseMaterials();
            _contentsDirty = false;

            if (!IsConfigured)
            {
                // 没有 render/collider 根节点时【不回退到 transform】：
                // 回退会把整栋建筑的内容都缓存进来，隐藏一层时连另一层一起消失。
                _renderers = new Renderer[0];
                _solidRenderers = new Renderer[0];
                _colliders = new Collider[0];
                _interactables = new Behaviour[0];

                Debug.LogWarning(
                    $"[{nameof(BuildingFloor)}]「{name}」没有配置内容根节点（_renderRoot / _colliderRoot），" +
                    "本层不会参与显示切换。请在 Inspector 里指定这两个节点。", this);
                return;
            }

            _renderers = RenderRoot.GetComponentsInChildren<Renderer>(true);
            _colliders = ColliderRoot.GetComponentsInChildren<Collider>(true);

            // 结构件的渲染单独收集：隐藏本层时要一起关掉，但碰撞不动。
            _solidRenderers = _solidStructureRoot != null
                ? _solidStructureRoot.GetComponentsInChildren<Renderer>(true)
                : new Renderer[0];

            // 交互物：所有挂在交互根节点下的 InteractableBase 子类。
            // 用基类取，所以以后加新的交互物类型不用改这里。
            _interactables = InteractionRoot.GetComponentsInChildren<InteractableBase>(true);

        }

        /// <summary>
        /// 由 BuildingController 调用：统一本层的半透明程度。
        /// 让控制器能统一控制（而不是每层各自在 Inspector 上调），
        /// 是为了保证"屋顶/外壳/楼层"淡出程度一致，观感不会花。
        /// </summary>
        public void SetGhostAlpha(float alpha)
        {
            _ghostAlpha = Mathf.Clamp(alpha, 0.02f, 1f);
        }

        /// <summary>由 BuildingController 调用。传入目标显示方式。</summary>
        public void SetVisibility(FloorVisibility visibility)
        {
            _visibility = visibility;

            if (_renderers == null || _contentsDirty) CacheContents();
            if (_renderers == null) return;

            switch (visibility)
            {
                case FloorVisibility.Visible:
                    // 只开渲染。**碰撞一律不动** —— 碰撞必须始终有效，
                    // 否则玩家站在二楼时脚下的楼板会消失、直接掉到一楼。
                    SetRenderersActive(true);
                    SetSolidRenderersActive(true);
                    RestoreOpaque();
                    SetInteractablesEnabled(IsCurrentFloorInteractable);
                    break;

                case FloorVisibility.Ghosted:
                    SetRenderersActive(true);
                    SetSolidRenderersActive(true);
                    ApplyGhostAlpha();
                    SetInteractablesEnabled(false);
                    break;

                case FloorVisibility.Hidden:
                    // 整层渲染关掉：室内陈设 + 结构件渲染。
                    // 玩家在一楼时，二楼就彻底看不见了 —— 但二楼的碰撞还在，
                    // 所以走到楼上依然有地板接着。
                    SetRenderersActive(false);
                    SetSolidRenderersActive(false);
                    SetInteractablesEnabled(false);
                    break;
            }
        }

        private void SetSolidRenderersActive(bool active)
        {
            for (int i = 0; i < _solidRenderers.Length; i++)
            {
                if (_solidRenderers[i] != null) _solidRenderers[i].enabled = active;
            }
        }

        /// <summary>
        /// 本层是否是"玩家当前所在层"。只有当前层的交互物才启用；
        /// 其它层即使显示出来（例如从室外看一楼），也不该能被隔空交互。
        /// </summary>
        public bool IsCurrentFloorInteractable { get; set; } = true;

        /// <summary>
        /// 不受楼层显示规则管辖（屋顶、外壳这类部件）。
        ///
        /// 它们的显隐由 BuildingController 直接控制，所以 ApplyVisibility **不应该**按
        /// "当前楼层"的规则去改它们 —— 否则玩家在室外时外壳会被判定成"非当前层"而隐藏，
        /// 整栋房子就没了。本工程实际踩到过这个坑。
        /// </summary>
        public bool IgnoreFloorVisibilityRules { get; set; }

        /// <summary>
        /// 只开关渲染，**完全不碰碰撞体**。
        ///
        /// 用于屋顶这类"需要让开视线但仍然要挡住玩家"的部件：
        ///   玩家在室内时屋顶要看不见（否则俯视视角下室内全被压住），
        ///   但它不能失去碰撞 —— 否则玩家会从二楼直接飞到房子外面去。
        /// </summary>
        public void SetRenderersVisibleOnly(bool visible)
        {
            if (_renderers == null || _contentsDirty) CacheContents();
            if (_renderers == null) return;

            SetRenderersActive(visible);
            if (visible) RestoreOpaque();
        }

        /// <summary>
        /// 只应用半透明，不改变任何开关。</summary>
        /// <remarks>
        /// 给屋顶/外壳这类"由外部决定显隐、但需要淡出效果"的部件用。
        /// </remarks>
        public void BuildingFloorGhost()
        {
            if (_renderers == null || _contentsDirty) CacheContents();
            ApplyGhostAlpha();
        }

        /// <summary>
        /// 只开关碰撞，不碰渲染。给"外壳"这类部件用：
        /// 从外面看它必须完整（挡住室内），玩家在室内时又要变淡方便看地图。
        /// </summary>
        public void SetCollidersActiveOnly(bool active)
        {
            SetCollidersEnabled(active);
        }

        /// <summary>
        /// 【只在编辑器生成时使用】直接开关这一层的碰撞。
        /// 运行时不要调用它 —— 楼层隐藏时碰撞必须保持有效。
        /// </summary>
        public void SetCollidersEnabled(bool enabled)
        {
            if (_colliders == null || _contentsDirty) CacheContents();

            for (int i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null) _colliders[i].enabled = enabled;
            }
        }

        private void SetRenderersActive(bool active)
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] != null) _renderers[i].enabled = active;
            }
        }

        private void SetInteractablesEnabled(bool enabled)
        {
            for (int i = 0; i < _interactables.Length; i++)
            {
                if (_interactables[i] != null) _interactables[i].enabled = enabled;
            }
        }

        // ------------------------------------------------------------------ 半透明

        private void ApplyGhostAlpha()
        {
            if (_fadeMaterials == null) _fadeMaterials = new RendererFadeMaterials(_renderers);
            _fadeMaterials.Apply(_ghostAlpha);
        }

        private void RestoreOpaque() => _fadeMaterials?.Restore();

        private void ReleaseMaterials()
        {
            _fadeMaterials?.Dispose();
            _fadeMaterials = null;
        }

        private void OnDestroy() => ReleaseMaterials();
#if UNITY_EDITOR
        /// <summary>编辑器专用：场景生成器用来配置楼层（保持字段封装）。</summary>
        public void ConfigureFloor(int floorIndex, string displayName, Transform renderRoot, Transform colliderRoot, Transform interactionRoot, Transform solidStructureRoot = null)
        {
            _floorIndex = floorIndex;
            _displayName = displayName;
            _renderRoot = renderRoot;
            _colliderRoot = colliderRoot;
            _interactionRoot = interactionRoot;
            _solidStructureRoot = solidStructureRoot;

            // 标记为脏：下次 SetVisibility 会重新收集内容。
            // 这一步很关键 —— 生成器是先 AddComponent 再 ConfigureFloor，
            // Awake 里那次缓存用的还是未配置的节点。
            _contentsDirty = true;
            CacheContents();
        }
#endif

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 1.5f, new Vector3(4f, 3f, 4f));
        }
    }
}
