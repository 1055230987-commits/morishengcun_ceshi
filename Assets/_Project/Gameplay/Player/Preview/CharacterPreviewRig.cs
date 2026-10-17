using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Player
{
    /// <summary>
    /// 角色模型预览装置：用一台离屏相机把主角的模型渲染到一张贴图上，
    /// 供 UI（背包界面）显示，并支持用鼠标拖动旋转。
    ///
    /// 为什么用"离屏相机 + RenderTexture"而不是"第二个屏幕相机 + viewport"：
    ///   屏幕相机方案里，第二台相机会把自己看到的东西画到屏幕的某个区域上，
    ///   而主相机也会往同一片区域画 —— 谁盖谁、清屏怎么设置很容易互相打架。
    ///   离屏渲染完全不影响主画面：相机永不输出到屏幕，UI 只负责把贴图贴在界面上。
    ///
    /// 生成方式：由场景生成器根据玩家的外观自动克隆（见 GreyboxLevelBuilder）。
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public class CharacterPreviewRig : MonoBehaviour
    {
        [Header("渲染")]
        [Tooltip("预览贴图的分辨率。太大浪费性能，512 已经足够清楚。")]
        [SerializeField] private int _textureSize = 512;

        [Tooltip("相机视野角。越小越接近「长焦」，模型看起来越不容易变形。")]
        [SerializeField, Range(10f, 60f)] private float _fieldOfView = 30f;

        [Tooltip("相机相对角色的方向（会被归一化）。默认从正前方稍高处看。")]
        [SerializeField] private Vector3 _cameraOffset = new Vector3(0f, 0.15f, 3.2f);

        [Tooltip("相机看向的高度（角色腰部偏上，构图比较好看）。")]
        [SerializeField] private float _lookAtHeight = 0.95f;

        [Header("背景与光照")]
        [SerializeField] private Color _backgroundColor = new Color(0.13f, 0.13f, 0.15f, 1f);

        [Tooltip("主光强度。用模型原本的材质（URP/Lit）时需要足够强的光，否则会偏黑。")]
        [SerializeField, Range(0f, 4f)] private float _lightIntensity = 1.8f;

        [Header("旋转")]
        [Tooltip("不拖动时自动旋转的速度（度/秒）。\n" +
                 "默认 0 = 不自动转，完全由玩家拖动控制。\n" +
                 "（早先默认 12，模型会自己慢慢转 —— 玩家反馈不想要自动旋转。）")]
        [SerializeField] private float _autoSpinSpeed = 0f;

        [Tooltip("拖动灵敏度（度/像素）。\n" +
                 "【为什么默认给 2.0 这么大】IMGUI 的 Event.delta 是「每帧鼠标位移」，\n" +
                 "不是「这次拖动的总位移」—— 拖一下会收到很多条 MouseDrag，每条只有几像素。\n" +
                 "所以要乘一个较大的系数，累积起来手感才顺。\n" +
                 "参考：2.0 时拖动约 180 像素 ≈ 转一整圈。觉得太快调到 1.0，太慢调到 3.0。")]
        [SerializeField, Range(0.1f, 8f)] private float _dragSensitivity = 2.0f;

        private Camera _camera;
        private RenderTexture _renderTexture;
        private Transform _pivot;
        private Transform _character;
        private Light _light;
        private Light _fillLight;
        private float _yaw;
        private bool _built;
        private readonly List<Material> _ownedMaterials = new List<Material>();

        /// <summary>预览需要重渲染（由旋转/拖动打标记，LateUpdate 统一处理）。</summary>
        private bool _dirty;

        /// <summary>预览贴图。UI 用它显示。</summary>
        public RenderTexture Texture => _renderTexture;

        public bool IsReady => _built && _renderTexture != null;

        /// <summary>当前偏航角（度）。</summary>
        public float Yaw => _yaw;

        /// <summary>
        /// 拖动灵敏度（度/像素），供界面读取。
        ///
        /// 【为什么放在这里而不是界面里】灵敏度是"这个预览装置怎么转"的属性，
        /// 属于装置自己；界面只负责把鼠标位移交给 AddYaw。
        /// 早先它硬编码在 InventoryScreen 里（PreviewDragSensitivity），
        /// 想调就得改界面代码 —— 现在改成由装置提供，Inspector 里就能调。
        /// </summary>
        public float DragSensitivity => _dragSensitivity;

        private void Awake()
        {
            Build();
        }

        private void OnEnable() => Build();

        /// <summary>
        /// 标记"预览需要重渲染"。真正的渲染会在 LateUpdate 里做一次。
        ///
        /// 【为什么不能直接 Render()】
        /// 两条路都会走到这里，而两条路都不能同步渲染：
        ///   1. 旋转由界面驱动：InventoryScreen.OnGUI → HandlePreviewDrag → AddYaw
        ///      —— 这是 **IMGUI 事件回调**，时机不对。
        ///   2. 建装置：OnEnable → Build → FrameCamera
        ///      —— 这是 **进入播放模式、管线尚未就绪**的窗口。
        ///      在这里渲染会让 Unity 过早构造渲染管线，而 VolumeManager 的默认体积
        ///      配置还没准备好 → 管线构造抛异常 → Blitter 卡在"已初始化"状态
        ///      → 之后每次渲染都失败、完全没有画面。
        ///
        /// 所以统一"只打标记"，渲染集中到 LateUpdate —— 那里一定在渲染循环之外、
        /// 且管线已就绪；一帧最多渲染一次（拖动再频繁也不会重复渲染）。
        /// </summary>
        private void MarkDirty() => _dirty = true;

        /// <summary>
        /// 立即渲染一次预览贴图（供编辑器工具/自检使用）。
        ///
        /// 【为什么必须手动调】预览相机的 `enabled = false` ——
        /// 而 `Camera.enabled = false` 不只是"不输出到屏幕"，它同时让相机
        /// **完全不自动渲染**。那张 RenderTexture 因此从来没被画过，
        /// 界面拿到的是一张空白贴图（看起来就是"预览是空的/黑的"）。
        ///
        /// 早先的注释写成"只用于 RenderTexture，不要输出到屏幕"，把它误解成了
        /// "只是不往屏幕输出" —— 实际上它连 RenderTexture 也不画。
        /// </summary>
        public void RenderNow()
        {
            if (_camera == null || _renderTexture == null) return;
            if (!_renderTexture.IsCreated()) return;

            // 【硬保护 1】绝不在 IMGUI 事件回调里渲染。
            //
            // 旋转是由界面驱动的（InventoryScreen.OnGUI → HandlePreviewDrag → AddYaw），
            // 所以调用链有可能落在 GUI 事件里。那种时机同步渲染会和编辑器/URP 的
            // 管线状态机打架。这里直接拒绝，交给 LateUpdate。
            if (Event.current != null && Event.current.type != EventType.Layout &&
                Event.current.type != EventType.Repaint)
            {
                _dirty = true;
                return;
            }

            // 【硬保护 2】管线没就绪就不渲染。
            //
            // 这是本工程踩过的最严重的一次：在 Enter Play Mode 的域重载窗口里
            // 调 Camera.Render()，会促使 Unity 立刻构造渲染管线，而 VolumeManager
            // 的默认体积配置尚未就绪 → 管线构造函数抛 NullReferenceException
            // → 管线创建失败，但 Blitter 已初始化且不会 Cleanup
            // → 之后每次渲染都报 "Blitter is already initialized"，完全没画面。
            //
            // 所以务必确认当前已经有可用的渲染管线再渲染。留着脏标记，
            // 下一帧 LateUpdate 会再试。
            if (UnityEngine.Rendering.RenderPipelineManager.currentPipeline == null)
            {
                _dirty = true;
                return;
            }

            // 相机被禁用时 Render() 仍可手动调用；渲染结果进 targetTexture。
            _camera.Render();
            _dirty = false;
        }

        /// <summary>
        /// 生成整个预览装置。由生成器在克隆完外观后调用。
        ///
        /// 【注意】它有 `_built` 守卫，所以**同一个会话里只会真正执行一次**。
        /// 如果装置被外部改动了（例如清理工具删掉了子物体），必须用
        /// <see cref="Rebuild"/> 而不是再调一次 Build —— 否则这里会直接返回、
        /// 什么都不建（本工程实际踩过：删掉相机后 Build 不重建，预览彻底空掉）。
        /// </summary>
        public void Build()
        {
            if (_built || !gameObject.scene.IsValid()) return;

            // 整个装置放到远离游戏区域的地方，避免和场景里的东西互相遮挡/碰撞。
            transform.position = new Vector3(0f, -2000f, 0f);
            transform.rotation = Quaternion.identity;

            _renderTexture = new RenderTexture(_textureSize, _textureSize, 24, RenderTextureFormat.ARGB32)
            {
                name = "RT_CharacterPreview",
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 2
            };
            _renderTexture.Create();

            // 预览用的层级。放到一个没人用的层号上，并且不参与主相机的渲染。
            int layer = PreviewLayer;
            gameObject.layer = layer;

            GameObject cameraGo = GetOrCreateChild("预览相机");
            cameraGo.layer = layer;

            _camera = cameraGo.GetComponent<Camera>();
            if (_camera == null) _camera = cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = _backgroundColor;
            _camera.fieldOfView = _fieldOfView;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 20f;
            _camera.targetTexture = _renderTexture;
            _camera.cullingMask = 1 << layer;

            // 【注意语义】enabled = false 不只是"不输出到屏幕" ——
            // 它同时让相机【完全不自动渲染】，连 RenderTexture 也不画。
            // 所以贴图必须靠 RenderNow() 手动渲染，否则界面拿到的永远是空白。
            // （旧注释写成"只用于 RenderTexture，不要输出到屏幕"，把它误解成了
            //   "只是不往屏幕输出"，导致预览长期是空的。本工程踩过。）
            _camera.enabled = false;

            // 旋转轴：角色挂在它下面，转它就等于转角色。
            GameObject pivotGo = GetOrCreateChild("旋转轴");
            pivotGo.layer = layer;
            _pivot = pivotGo.transform;
            _character = _pivot.Find("预览模型");

            _light = ConfigureLight("预览主光", new Vector3(2f, 2.5f, 2f), _lightIntensity * 6f, Color.white);

            // 补光：从主角前方偏下打一盏较弱的灯。
            // 只有一盏主光时，朝向背面的部分会很暗，模型看起来像一块黑片。
            _fillLight = ConfigureLight("预览补光", new Vector3(-2f, 1f, 2f), _lightIntensity * 3.3f,
                new Color(0.85f, 0.88f, 1f));
            foreach (Renderer renderer in _pivot.GetComponentsInChildren<Renderer>(true))
                renderer.renderingLayerMask = PreviewRenderingLayerMask;
            FrameCamera();
            _built = true;
        }

        /// <summary>
        /// 彻底重建整套预览装置：删掉现有子物体、释放旧的 RenderTexture、
        /// 清掉所有内部引用，然后从零 Build 一次。
        ///
        /// 【为什么需要它】Build() 有 `_built` 守卫，同一会话里只执行一次。
        /// 一旦装置被外部改动（清理工具删子物体、脚本重载后 `_character` 变 null
        /// 导致旧「预览模型」变成孤儿），再调 Build() 不会有任何效果。
        ///
        /// 【为什么不能只删子物体再调 Build】`_built` 仍是 true → Build 直接返回。
        /// 必须把 `_built` 和所有引用一起清掉，让 Build 走完整流程。
        ///
        /// 返回是否成功建出装置（供编辑器工具自检用）。
        /// </summary>
        /// <param name="source">模型来源（玩家的外观层级）。为空则只清不建。</param>
        public bool Rebuild(GameObject source)
        {
            // ---- 1) 释放旧的 RenderTexture ----
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                if (Application.isPlaying) Destroy(_renderTexture);
                else DestroyImmediate(_renderTexture);
                _renderTexture = null;
            }

            // ---- 2) 删光所有子物体 ----
            // 用快照数组，避免边遍历边删。
            var children = new List<Transform>();
            foreach (Transform child in transform) children.Add(child);

            foreach (Transform child in children)
            {
                if (child == null) continue;
                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }

            // ---- 3) 清掉所有引用与状态 ----
            // 这一步是【关键】：只删子物体是不够的，`_built` 和这些引用还指着已销毁的对象。
            _ownedMaterials.Clear();
            _camera = null;
            _pivot = null;
            _character = null;
            _light = null;
            _fillLight = null;
            _yaw = 0f;
            _built = false;

            // ---- 4) 从零重建 ----
            if (source == null) return false;

            SetSourceFrom(source);
            return _built && _camera != null && _renderTexture != null;
        }

        // 普通 layer 负责相机可见性；Rendering Layer 负责 URP 光照隔离。
        // 使用有限范围的点光，还能避免预览灯参与 URP 主平行光选择。
        public const uint PreviewRenderingLayerMask = 1u << 1;

        private Light ConfigureLight(string childName, Vector3 position, float intensity, Color color)
        {
            GameObject child = GetOrCreateChild(childName);
            child.layer = PreviewLayer;
            child.transform.localPosition = position;
            Light light = child.GetComponent<Light>();
            if (light == null) light = child.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 6f;
            light.intensity = intensity;
            light.color = color;
            light.shadows = LightShadows.None;
            light.cullingMask = 1 << PreviewLayer;
            light.GetUniversalAdditionalLightData().renderingLayers = PreviewRenderingLayerMask;
            light.enabled = true;
            return light;
        }

        private GameObject GetOrCreateChild(string childName)
        {
            Transform existing = null;
            foreach (Transform child in transform)
            {
                if (child.name != childName) continue;
                if (existing == null) existing = child;
                else child.gameObject.SetActive(false); // 隔离旧版本积累的生成物，保留以便检查。
            }
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                return existing.gameObject;
            }
            var created = new GameObject(childName);
            created.transform.SetParent(transform, false);
            return created;
        }

        /// <summary>
        /// 把玩家身上用于显示的那部分（网格）克隆一份到预览轴下面。
        /// 只保留网格与渲染器，不带碰撞、不带脚本 —— 预览不该有任何游戏逻辑。
        /// </summary>
        public void SetSourceFrom(GameObject source)
        {
            Build();
            if (source == null) return;

            ClearCharacter();

            GameObject clone = new GameObject("预览模型");
            clone.transform.SetParent(_pivot, false);

            // 【把来源根节点自身的局部变换也带过来】
            // CopyRenderableHierarchy 只递归复制【子物体】，根节点自己的 localPosition
            // 会丢。玩家的「外观」层级里胶囊体有 localPosition = (0, 0.9, 0)，
            // 丢了之后整个模型会下沉 0.9 米 —— 相机按预期瞄准身体中部时就会照到空处
            // （本工程实际数据：偏离 16.3°，超出竖直半视野角 15°，模型被切在画面外）。
            clone.transform.localPosition = source.transform.localPosition;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale;

            clone.layer = PreviewLayer;

            CopyRenderableHierarchy(source.transform, clone.transform);

            _character = clone.transform;
            _yaw = 0f;

            ApplyYaw();
            FrameCamera();
        }

        /// <summary>递归复制"可渲染"的部分（MeshFilter + MeshRenderer），其它组件一律不带。</summary>
        private void CopyRenderableHierarchy(Transform source, Transform target)
        {
            MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();

            if (sourceFilter != null && sourceFilter.sharedMesh != null)
            {
                MeshFilter filter = target.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;

                MeshRenderer renderer = target.gameObject.AddComponent<MeshRenderer>();

                // 直接沿用原材质，不换成"无光照材质"。
                //
                // 早先的做法是 Shader.Find("Universal Render Pipeline/Unlit") 再新建材质，
                // 但 Shader.Find 在编辑器里对内置 shader 并不可靠（同样的问题在粒子特效上也踩过）：
                // 找不到时材质无效，模型就渲染成【纯黑】。
                //
                // 用原材质 + 给预览区加一盏灯，既不会有 shader 查找失败的风险，
                // 颜色也和游戏里完全一致。
                renderer.sharedMaterials = sourceRenderer != null
                    ? sourceRenderer.sharedMaterials
                    : new[] { CreateFallbackMaterial() };

                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.renderingLayerMask = PreviewRenderingLayerMask;
            }

            // 递归子物体，并保留原本的局部变换（否则朝向标记之类的会跑位）。
            foreach (Transform child in source)
            {
                GameObject childClone = new GameObject(child.name);
                childClone.layer = PreviewLayer;
                childClone.transform.SetParent(target, false);
                childClone.transform.localPosition = child.localPosition;
                childClone.transform.localRotation = child.localRotation;
                childClone.transform.localScale = child.localScale;

                CopyRenderableHierarchy(child, childClone.transform);
            }
        }

        /// <summary>
        /// 找不到原材质时的兜底。
        /// 依次尝试几个能确定存在的 shader，全失败就返回 null（Unity 会用内置默认材质，不会是黑的）。
        /// </summary>
        private Material CreateFallbackMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;

            Material material = new Material(shader) { name = "M_Preview_Fallback" };
            _ownedMaterials.Add(material);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.8f, 0.8f, 0.82f));
            if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0.8f, 0.8f, 0.82f));
            return material;
        }

        private void ClearCharacter()
        {
            if (_character != null)
            {
                if (Application.isPlaying) Destroy(_character.gameObject);
                else DestroyImmediate(_character.gameObject);
            }

            _character = null;
            foreach (Material material in _ownedMaterials)
            {
                if (material == null) continue;
                if (Application.isPlaying) Destroy(material);
                else DestroyImmediate(material);
            }
            _ownedMaterials.Clear();
        }

        /// <summary>
        /// 把相机摆到能看到整个角色的位置。
        ///
        /// 【为什么瞄准点要用"包围盒中心"而不是 _pivot.position + _lookAtHeight】
        /// 曾经的做法是 `focus = _pivot.position + up * _lookAtHeight`，它隐含假设
        /// "模型的几何中心就在旋转轴原点上方 _lookAtHeight 处"。这个假设会破：
        ///
        /// 预览模型是玩家「外观」层级的克隆，而 CopyRenderableHierarchy 只复制
        /// **子物体**的局部变换 —— 「外观」根节点自己的 localPosition（例如胶囊体的
        /// 0.9 米）【不会被带过来】。于是模型整体比预期低 0.9 米。
        ///
        /// 后果（本工程实际数据）：相机瞄准 y=0.95，模型中心在 y=0，相距 3.38 米时
        /// 偏离正前方 16.3°，而竖直半视野角只有 15° —— **模型被切在画面下边缘之外，
        /// 预览看起来是空的。**
        ///
        /// 改成直接测"所有 Renderer 的合并包围盒中心"，无论模型层级怎么变、根节点
        /// 的偏移有没有被带过来，相机都会对准真正的几何中心。
        /// </summary>
        private void FrameCamera()
        {
            if (_camera == null || _pivot == null) return;

            Renderer[] renderers = _pivot.GetComponentsInChildren<Renderer>(true);

            Vector3 focus;
            float height;

            if (renderers.Length == 0)
            {
                // 还没有模型（SetSourceFrom 之前）：退回到旋转轴位置，用默认高度。
                height = 1.8f;
                focus = _pivot.position + Vector3.up * Mathf.Clamp(_lookAtHeight, 0.2f, height);
            }
            else
            {
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

                height = Mathf.Max(0.5f, bounds.size.y);

                // 以包围盒中心为基准，再加一点点上偏（构图习惯：人物略靠上更自然）。
                // _lookAtHeight 在这里的含义变成"相对几何中心的额外上偏"，而不是绝对高度。
                focus = bounds.center + Vector3.up * Mathf.Clamp(_lookAtHeight - height * 0.5f, -height * 0.5f, height * 0.5f);
            }

            Vector3 direction = _cameraOffset.sqrMagnitude > 0.0001f
                ? _cameraOffset.normalized
                : Vector3.forward;

            // 距离按模型高度自适应，换更高的模型也不会出画。
            float distance = Mathf.Max(1.5f, height * 1.6f);
            _camera.transform.position = focus + direction * distance;
            _camera.transform.LookAt(focus);

            // 【绝对不要在这里渲染】
            // FrameCamera 会被 Build() 调用，而 Build() 会被 OnEnable() 调用 ——
            // 也就是【进入播放模式、域重载刚结束】的那个窗口。
            // 在那一刻调 Camera.Render() 会促使 Unity 立刻构造渲染管线，
            // 而此时 VolumeManager 的默认体积配置尚未就绪，
            // 管线构造函数会抛 NullReferenceException → 管线创建失败，
            // 但 Blitter 已经初始化且【不会 Cleanup】→ 之后每次渲染都撞
            // "Blitter is already initialized" → 永久卡死、完全没有画面。
            // （本工程实际发生过，日志里能完整看到这条链。）
            //
            // 所以这里只打脏标记，真正的渲染统一交给 LateUpdate ——
            // 那时管线一定已经就绪。
            MarkDirty();
        }

        private void Update()
        {
            if (!Application.isPlaying) return;
            if (!IsReady || _pivot == null) return;

            // 拖动旋转由 UI 通过 AddYaw 直接驱动（UI 才知道预览区的矩形在哪、鼠标有没有在里面），
            // 所以这里只需要处理"没有拖动时自动缓慢旋转"。
            //
            // 注意不要在这里读鼠标：本工程 activeInputHandler 是 Input System 包，
            // 用旧的 UnityEngine.Input 类会直接抛 InvalidOperationException（本工程踩过）。
            if (Mathf.Abs(_autoSpinSpeed) <= 0.001f) return;

            _yaw += _autoSpinSpeed * Time.deltaTime;
            ApplyYaw();   // 内部只会打脏标记
        }

        /// <summary>
        /// 统一在这里做"延迟渲染"。
        ///
        /// 放在 LateUpdate 的原因：它在**渲染循环之外**，且一帧只跑一次。
        /// 这样无论"自动旋转每帧改角度"还是"用户拖动改了角度"，
        /// 一帧最多只渲染一次预览，而且避开了 IMGUI 事件回调这种危险时机。
        /// </summary>
        private void LateUpdate()
        {
            if (!_dirty) return;

            // 编辑模式下也允许刷新（ExecuteAlways），这样重置装置后立刻能看到结果。
            RenderNow();
        }

        private void ApplyYaw()
        {
            if (_pivot == null) return;
            _pivot.localRotation = Quaternion.Euler(0f, _yaw, 0f);

            // 转过角度就要重渲染，否则贴图上还是旧的朝向。
            // 但【只打脏标记】，真正渲染放到 LateUpdate（见 MarkDirty 的说明）：
            // 这个方法的调用点之一是 IMGUI 事件回调，在那里同步 Render() 时机不对。
            MarkDirty();
        }

        /// <summary>由 UI 调用：直接旋转一个角度（拖动或滚轮）。</summary>
        public void AddYaw(float degrees)
        {
            _yaw += degrees;
            ApplyYaw();
        }

        private void OnDisable()
        {
            _built = false;
            if (_light != null) _light.enabled = false;
            if (_fillLight != null) _fillLight.enabled = false;
            if (_renderTexture == null) return;

            if (_camera != null) _camera.targetTexture = null;

            _renderTexture.Release();
            CoreUtils.Destroy(_renderTexture);
            _renderTexture = null;
        }

        private void OnDestroy()
        {
            OnDisable();
            ClearCharacter();
        }

        /// <summary>
        /// 预览用的层级号。
        /// 取 30：Unity 内置层只用到 0~7（TransparentFX/Water/UI 等）。
        /// 只要 [Camera] 的 cullingMask 不去包含它，就不会出现在主画面里。
        /// </summary>
        public const int PreviewLayer = 30;
    }
}
