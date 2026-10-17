using System.Collections.Generic;
using Game.Save;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Game.World
{
    /// <summary>
    /// 灰盒体块工具。
    ///
    /// 只做一件事：用 Unity 自带 Cube 拼出占位几何体，用来判断人物比例、镜头距离和遮挡。
    /// 这些对象是**场景中的普通物体**（不是运行时生成的临时物），
    /// 所以可以直接手动微调、也能被以后的存档/关卡数据引用。
    /// </summary>
    public static class GreyboxPrimitives
    {
        // 灰盒调色板的资产路径。分目录存放（按"用途"而不是"资产类型"），和 _Project/Art 的结构一致。
        public const string GroundMaterialPath = "Assets/_Project/Art/Environment/Terrain/Materials/M_Grey_Ground.mat";
        public const string StructureMaterialPath = "Assets/_Project/Art/Environment/Buildings/Materials/M_Grey_Structure.mat";
        public const string PropMaterialPath = "Assets/_Project/Art/Environment/Props/Materials/M_Grey_Prop.mat";
        public const string TrunkMaterialPath = "Assets/_Project/Art/Environment/Vegetation/Materials/M_Tree_Trunk.mat";
        public const string CanopyMaterialPath = "Assets/_Project/Art/Environment/Vegetation/Materials/M_Tree_Leaves_Green.mat";
        public const string YellowCanopyMaterialPath = "Assets/_Project/Art/Environment/Vegetation/Materials/M_Tree_Leaves_Yellow.mat";
        public const string GrassMaterialPath = "Assets/_Project/Art/Environment/Vegetation/Materials/M_Grass.mat";
        public const string RockMaterialPath = "Assets/_Project/Art/Environment/Props/Materials/M_Rock.mat";

        /// <summary>水体材质与 shader 名。shader 是我们自己写的（URP 没有官方水体）。</summary>
        public const string WaterMaterialPath = "Assets/_Project/Rendering/Water/M_Water_Lake.mat";
        public const string WaterShaderName = "Game/Water/RealisticWater";

        /// <summary>水体外部贴图的资产路径。shader 里对应的属性名见 WaterTextureTools。</summary>
        public const string WaterTextureFolder = "Assets/_Project/Art/Environment/Water/Textures";
        public const string WaterNormalMapPath = WaterTextureFolder + "/T_Water_Normal.png";
        public const string WaterFoamNoisePath = WaterTextureFolder + "/T_Water_FoamNoise.png";
        public const string WaterCausticsPath = WaterTextureFolder + "/T_Water_Caustics.png";
        public const string WaterGlintPath = WaterTextureFolder + "/T_Water_Glint.png";

        /// <summary>窗户玻璃的材质路径与 shader 名。</summary>
        public const string WindowGlassMaterialPath = "Assets/_Project/Rendering/Environment/M_WindowGlass.mat";
        public const string WindowGlassShaderName = "Game/Environment/WindowGlass";

        /// <summary>
        /// 创建/更新窗户玻璃材质。
        ///
        /// 和 GetOrCreateWaterMaterial 同样的策略：shader 找不到时【不报错、返回 null】，
        /// 调用方退回普通材质 —— 这样即使 shader 有编译错误，场景也不会变粉红。
        /// </summary>
        public static Material GetOrCreateWindowGlassMaterial()
        {
#if UNITY_EDITOR
            Shader shader = Shader.Find(WindowGlassShaderName);

            if (shader == null)
            {
                Debug.LogWarning(
                    $"[灰盒] 找不到窗户玻璃 shader「{WindowGlassShaderName}」。\n" +
                    "可能是 shader 有编译错误（在 Project 窗口点一下它看 Inspector 的报错），" +
                    "或者还没被 Unity 导入。窗户会退化成普通不透明块。");
                return null;
            }

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(WindowGlassMaterialPath);

            if (existing == null)
            {
                if (!EnsureAssetFolder(WindowGlassMaterialPath)) return null;

                existing = new Material(shader) { name = "M_WindowGlass" };
                AssetDatabase.CreateAsset(existing, WindowGlassMaterialPath);
            }
            else
            {
                existing.shader = shader;
            }

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();

            return existing;
#else
            return null;
#endif
        }

        /// <summary>
        /// 创建/更新水体材质。
        ///
        /// shader 找不到时【不报错、返回 null】并打警告 ——
        /// 调用方会用回退材质，湖面变成一个平淡的半透明面而不是"粉红色错误材质"。
        /// 这样即使 shader 里有个语法错误，场景也不会整个坏掉。
        /// </summary>
        public static Material GetOrCreateWaterMaterial(Color shallow, Color deep)
        {
#if UNITY_EDITOR
            Shader shader = Shader.Find(WaterShaderName);

            if (shader == null)
            {
                Debug.LogWarning(
                    $"[灰盒] 找不到水体 shader「{WaterShaderName}」。\n" +
                    "可能是 shader 有编译错误（在 Project 窗口点一下它看 Inspector 的报错），" +
                    "或者还没被 Unity 导入。湖面会退化成普通半透明面。");
                return null;
            }

            Material existing = AssetDatabase.LoadAssetAtPath<Material>(WaterMaterialPath);

            if (existing == null)
            {
                if (!EnsureAssetFolder(WaterMaterialPath)) return null;

                existing = new Material(shader) { name = "M_Water_Lake" };
                AssetDatabase.CreateAsset(existing, WaterMaterialPath);
            }
            else
            {
                existing.shader = shader;
            }

            if (existing.HasProperty("_ShallowColor")) existing.SetColor("_ShallowColor", shallow);
            if (existing.HasProperty("_DeepColor")) existing.SetColor("_DeepColor", deep);

            // 顺便生成并挂上外部贴图。
            // 这样"重建场景"一步就能得到完整的水体，不需要用户再跑一次贴图菜单。
            EnsureWaterTextures(existing);

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();

            return existing;
#else
            return null;
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// 确保四张水体贴图存在并挂到材质上。
        ///
        /// 贴图已经存在时不重新生成 —— 否则用户换成自己的图之后，
        /// 下次重建场景会被程序化占位图覆盖掉。想重新生成请用菜单「游戏/水体/生成占位贴图」。
        /// </summary>
        private static void EnsureWaterTextures(Material material)
        {
            if (material == null) return;

            // 【注意】这里要传【资产文件路径】，不能传文件夹路径。
            // EnsureAssetFolder 内部会取 GetDirectoryName，传文件夹会把父目录当目标，
            // 真正的文件夹（.../Water/Textures）就不会被创建 —— 本工程踩过这个坑。
            if (!EnsureWaterTextureFolder()) return;

            AssignWaterTexture(material, "_NormalMap", WaterNormalMapPath, () => WaterTextureGenerator.CreateNormalMap());
            AssignWaterTexture(material, "_FoamNoiseTex", WaterFoamNoisePath, () => WaterTextureGenerator.CreateFoamNoise());
            AssignWaterTexture(material, "_CausticsTex", WaterCausticsPath, () => WaterTextureGenerator.CreateCaustics());
            AssignWaterTexture(material, "_GlintTex", WaterGlintPath, () => WaterTextureGenerator.CreateGlint());
        }

        /// <summary>
        /// 确保贴图文件夹存在 —— 关键是【磁盘上】也要存在。
        ///
        /// AssetDatabase.CreateFolder 通常会让磁盘目录同时出现，但那是"通常"不是保证；
        /// 而后面用的是 System.IO.File.WriteAllBytes，它只认磁盘。
        /// 所以这里两步都做：先用 AssetDatabase 登记（让 Unity 认得这个文件夹），
        /// 再检查磁盘、必要时用 System.IO 兜底创建。
        /// </summary>
        private static bool EnsureWaterTextureFolder()
        {
            // 借用任意一个贴图文件路径来触发目录创建（该方法内部会取目录部分）。
            if (!EnsureAssetFolder(WaterNormalMapPath))
            {
                Debug.LogError($"[灰盒] 无法创建水体贴图目录：{WaterTextureFolder}");
                return false;
            }

            // 磁盘兜底：AssetDatabase 与真实文件系统不同步时（本工程遇到过），这里会补上。
            string absoluteFolder = AssetPathToAbsolute(WaterTextureFolder);

            if (!string.IsNullOrEmpty(absoluteFolder) && !System.IO.Directory.Exists(absoluteFolder))
            {
                System.IO.Directory.CreateDirectory(absoluteFolder);
                AssetDatabase.Refresh();
            }

            return true;
        }

        /// <summary>把 "Assets/xxx" 形式的资产路径转成磁盘绝对路径。</summary>
        private static string AssetPathToAbsolute(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (!assetPath.StartsWith("Assets/")) return null;

            // Application.dataPath 就是项目里的 Assets 目录，所以直接把 "Assets/" 换成它。
            string projectRoot = Application.dataPath;
            if (projectRoot.EndsWith("/Assets")) projectRoot = projectRoot.Substring(0, projectRoot.Length - "/Assets".Length);

            return System.IO.Path.Combine(projectRoot, assetPath);
        }

        private static void AssignWaterTexture(Material material, string property, string assetPath, System.Func<Texture2D> create)
        {
            if (!material.HasProperty(property)) return;

            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);

            if (texture == null)
            {
                // 还没有就生成一张占位图。
                // 导入设置由 WaterTextureTools 负责（法线贴图必须声明成 NormalMap），
                // 所以这里生成完立刻刷新一次，让编辑器工具能接手。
                if (!EnsureWaterTextureFolder()) return;

                Texture2D generated = create();
                if (generated == null) return;

                byte[] png = generated.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(generated);

                // 【必须用绝对路径写盘】System.IO.File 不认 "Assets/..." 这种资产路径 ——
                // 它只会把它当相对路径，而相对路径是否可用取决于 Unity 的工作目录，
                // 不可靠（本工程踩过 DirectoryNotFoundException）。所以这里统一转成绝对路径。
                string absolutePath = AssetPathToAbsolute(assetPath);
                if (string.IsNullOrEmpty(absolutePath)) return;

                string absoluteDirectory = System.IO.Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(absoluteDirectory) && !System.IO.Directory.Exists(absoluteDirectory))
                {
                    System.IO.Directory.CreateDirectory(absoluteDirectory);
                }

                System.IO.File.WriteAllBytes(absolutePath, png);

                // 写完要让 AssetDatabase 知道有新文件
                AssetDatabase.ImportAsset(assetPath);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            }

            if (texture != null) material.SetTexture(property, texture);
        }
#endif

        /// <summary>一整套灰盒材质。由场景生成器一次性准备好，避免每个物体各自去建。</summary>
        public struct GreyboxPalette
        {
            public Material Ground;
            public Material Structure;
            public Material Crate;
            public Material Trunk;
            public Material Canopy;
            public Material YellowCanopy;
            public Material Grass;
            public Material Rock;
        }

        /// <summary>
        /// 创建/更新一整套灰盒材质。
        ///
        /// 颜色是【每次调用都写入】的（不是只在新建时写）：
        /// 否则改了这里的默认配色，已经生成过的材质资产不会跟着变 —— 表现就是"改了代码但画面没变"。
        /// 想恢复默认配色时，调用 EnsurePalette() 即可覆盖。
        /// </summary>
        public static GreyboxPalette EnsurePalette()
        {
            GreyboxPalette palette = new GreyboxPalette
            {
                // 干裂的土地：偏土黄，而不是纯灰 —— 纯灰看起来像水泥地，缺少"荒地"感
                Ground = GetOrCreateMaterial(GroundMaterialPath, new Color(0.34f, 0.30f, 0.24f)),

                // 建筑与围墙：冷灰
                Structure = GetOrCreateMaterial(StructureMaterialPath, new Color(0.52f, 0.52f, 0.55f)),

                // 箱子/集装箱：木质棕，和围墙区分开
                Crate = GetOrCreateMaterial(PropMaterialPath, new Color(0.45f, 0.33f, 0.20f)),

                // 树干：深褐
                Trunk = GetOrCreateMaterial(TrunkMaterialPath, new Color(0.29f, 0.20f, 0.13f)),

                // 绿叶
                Canopy = GetOrCreateMaterial(CanopyMaterialPath, new Color(0.22f, 0.42f, 0.19f)),

                // 黄叶（秋天的树 / 枯黄的树）
                YellowCanopy = GetOrCreateMaterial(YellowCanopyMaterialPath, new Color(0.68f, 0.55f, 0.13f)),

                // 草地面片（颜色主要来自顶点色，这里的基色只是个底）
                Grass = GetOrCreateMaterial(GrassMaterialPath, new Color(0.30f, 0.45f, 0.18f)),

                // 石头：中性灰（比地面亮一点，能从地面里"跳"出来）
                Rock = GetOrCreateMaterial(RockMaterialPath, new Color(0.44f, 0.44f, 0.42f))
            };

            // 地面用【顶点色】区分草地与泥地，所以必须打开材质的顶点色开关，
            // 否则 URP/Lit 会忽略顶点色，地面又是纯一色（本工程踩过类似的静默失效）。
            EnableVertexColors(palette.Ground);

            return palette;
        }

        /// <summary>打开材质的顶点色开关（URP/Lit 需要显式开启，否则忽略顶点色）。</summary>
        private static void EnableVertexColors(Material material)
        {
            if (material == null) return;
            if (!material.HasProperty("m_VertexColors")) return;

            material.SetFloat("m_VertexColors", 1f);   // 0 = Multiply，1 = Add（都能让顶点色生效）

#if UNITY_EDITOR
            EditorUtility.SetDirty(material);
#endif
        }

        /// <summary>
        /// 创建或更新一个材质资产（编辑器内）。
        /// <paramref name="updateExisting"/> = true 时会用传入颜色覆盖已有材质，用于"改了配色想重新应用"。
        /// 运行时（非编辑器）返回 null，调用方会用 Unity 内置默认材质，保证灰盒仍然可见。
        /// </summary>
        public static Material GetOrCreateMaterial(string assetPath, Color color, bool updateExisting = true)
        {
#if UNITY_EDITOR
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);

            if (existing != null)
            {
                if (!updateExisting) return existing;

                ApplyColor(existing, color);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                return existing;
            }

            if (!EnsureAssetFolder(assetPath))
            {
                Debug.LogError($"[灰盒] 无法创建材质目录：{assetPath}（该物体将使用 Unity 默认材质）");
                return null;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogWarning("[灰盒] 找不到可用的 Lit shader，该物体将使用 Unity 默认材质。");
                return null;
            }

            Material material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(assetPath) };
            ApplyColor(material, color);

            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            return material;
#else
            return null;
#endif
        }

        private static void ApplyColor(Material material, Color color)
        {
            if (material == null) return;

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);

            // 灰盒不要高光，否则方块看起来像塑料。
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.05f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.05f);
        }

#if UNITY_EDITOR
        /// <summary>
        /// 确保资产所在目录存在且已被 AssetDatabase 登记。
        ///
        /// 注意：这里没有复用 Game.Editor 程序集里的 EditorFileUtility ——
        /// 本类属于 Game.Runtime，引用 Game.Editor 会造成循环依赖。
        /// 逻辑很短，各自保留一份比引入程序集依赖更干净。
        /// </summary>
        private static bool EnsureAssetFolder(string assetFilePath)
        {
            string directory = System.IO.Path.GetDirectoryName(assetFilePath);
            if (string.IsNullOrEmpty(directory)) return false;

            directory = directory.Replace('\\', '/');

            if (!AssetDatabase.IsValidFolder(directory))
            {
                string[] parts = directory.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }

                AssetDatabase.Refresh();

                if (!AssetDatabase.IsValidFolder(directory)) return false;
            }

            return true;
        }
#endif

        /// <summary>
        /// 生成一块【带矩形洞】的平板（地板 / 天花板）。
        ///
        /// 为什么需要它：楼梯井需要在天花板和楼板上开洞。
        /// 用一次性生成的四块平板拼出来，比布尔运算或自定义网格简单可靠得多。
        ///
        /// 坐标为局部坐标（以 parent 为原点）。holeMin/holeMax 是洞的 XZ 范围。
        /// 洞用 (NaN, NaN) 传入表示"不开洞"。
        /// </summary>
        public static void CreateSlabWithHole(
            string name,
            Transform parent,
            Vector2 size,           // 平板总尺寸 XZ
            float topY,             // 平板顶面高度
            float thickness,
            Vector2 holeMin,
            Vector2 holeMax,
            Material material,
            string objectType,
            string objectIdPrefix)
        {
            float halfX = size.x * 0.5f;
            float halfZ = size.y * 0.5f;
            float bottomY = topY - thickness;
            float centerY = (topY + bottomY) * 0.5f;

            bool hasHole = !float.IsNaN(holeMin.x) && !float.IsNaN(holeMin.y) &&
                           !float.IsNaN(holeMax.x) && !float.IsNaN(holeMax.y);

            if (!hasHole)
            {
                CreateBlock($"{name}_整块", parent, new Vector3(0f, centerY, 0f),
                    new Vector3(size.x, thickness, size.y), material, objectType, $"{objectIdPrefix}/full");
                return;
            }

            // 先做一个"洞外围的环"：南条 + 北条 + 中间两侧条
            // 南条：z 从 -halfZ 到 holeMin.z
            if (holeMin.y > -halfZ + 0.01f)
            {
                float depth = holeMin.y + halfZ;
                CreateBlock($"{name}_南条", parent,
                    new Vector3(0f, centerY, (-halfZ + holeMin.y) * 0.5f),
                    new Vector3(size.x, thickness, depth), material, objectType, $"{objectIdPrefix}/south");
            }

            // 北条：z 从 holeMax.z 到 halfZ
            if (holeMax.y < halfZ - 0.01f)
            {
                float depth = halfZ - holeMax.y;
                CreateBlock($"{name}_北条", parent,
                    new Vector3(0f, centerY, (holeMax.y + halfZ) * 0.5f),
                    new Vector3(size.x, thickness, depth), material, objectType, $"{objectIdPrefix}/north");
            }

            // 洞两侧（在洞的 z 范围内，补上 x 方向剩下的部分）
            float holeDepth = holeMax.y - holeMin.y;
            if (holeDepth > 0.01f)
            {
                float holeCenterZ = (holeMin.y + holeMax.y) * 0.5f;

                if (holeMin.x > -halfX + 0.01f)
                {
                    float width = holeMin.x + halfX;
                    CreateBlock($"{name}_西条", parent,
                        new Vector3((-halfX + holeMin.x) * 0.5f, centerY, holeCenterZ),
                        new Vector3(width, thickness, holeDepth), material, objectType, $"{objectIdPrefix}/west");
                }

                if (holeMax.x < halfX - 0.01f)
                {
                    float width = halfX - holeMax.x;
                    CreateBlock($"{name}_东条", parent,
                        new Vector3((holeMax.x + halfX) * 0.5f, centerY, holeCenterZ),
                        new Vector3(width, thickness, holeDepth), material, objectType, $"{objectIdPrefix}/east");
                }
            }
        }

        /// <summary>创建一个长方体块（Cube 缩放而成）。体积小于 0.02 时直接跳过，避免出现压扁的碎片。</summary>
        public static GameObject CreateBlock(
            string name,
            Transform parent,
            Vector3 center,
            Vector3 size,
            Material material,
            string objectType = "greybox",
            string objectId = null)
        {
            size = new Vector3(Mathf.Max(size.x, 0.02f), Mathf.Max(size.y, 0.02f), Mathf.Max(size.z, 0.02f));

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;

            if (material != null)
            {
                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;
            }

            WorldObjectId id = go.GetComponent<WorldObjectId>();
            if (id == null) id = go.AddComponent<WorldObjectId>();
            id.SetId(objectId ?? name, objectType);
            return go;
        }

        /// <summary>
        /// 创建一个球体（用于树冠这类需要圆润形状的地方）。
        ///
        /// 关于面数：Unity 内置球体是固定细分的低模（约 515 顶点），
        /// 对灰盒阶段完全够用，不需要专门去外部工具做低模。
        /// 内置球体直径是 1，所以 <paramref name="size"/> 传直径即可。
        /// </summary>
        public static GameObject CreateSphere(
            string name,
            Transform parent,
            Vector3 center,
            float size,
            Material material,
            string objectType = "greybox",
            string objectId = null)
        {
            size = Mathf.Max(size, 0.02f);

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = Vector3.one * size;

            // 内置球体自带 SphereCollider。树冠这类纯外观部件不需要碰撞，
            // 留着会在头顶形成看不见的空气墙，所以直接去掉。
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(collider);
                else UnityEngine.Object.DestroyImmediate(collider);
            }

            if (material != null)
            {
                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;
            }

            WorldObjectId id = go.GetComponent<WorldObjectId>();
            if (id == null) id = go.AddComponent<WorldObjectId>();
            id.SetId(objectId ?? name, objectType);
            return go;
        }

        /// <summary>
        /// 创建一个"不规则球"：形状大致是个球，但表面有明显起伏。
        ///
        /// 做法：把球面顶点按【方向】做值噪声扰动 ——
        /// 相邻顶点的方向接近，扰动值也接近，所以表面是连贯起伏的，不会破面。
        ///
        /// 多个鼓包（lobeCount &gt; 1）会合并进【同一个网格】：
        ///   每个鼓包有自己的位置偏移和噪声偏移，看起来像好几团叶子挤在一起；
        ///   但它们在同一个 mesh 里，所以只占一个物体、一次 draw call。
        ///
        /// 种子固定，所以重建场景时同一棵树长得一样，不会每次都不一样。
        /// </summary>
        public static GameObject CreateBlob(
            string name,
            Transform parent,
            Vector3 center,
            float radius,
            Material material,
            int seed,
            int lobeCount = 3,
            float noiseAmount = 0.26f,
            string objectType = "greybox",
            string objectId = null)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;

            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (filter != null)
            {
                filter.sharedMesh = BuildBlobMesh(
                    Mathf.Max(0.05f, radius),
                    Mathf.Max(1, lobeCount),
                    Mathf.Clamp(noiseAmount, 0f, 0.8f),
                    seed);
            }

            // 树冠这类纯外观部件不要碰撞体，否则头顶会有看不见的空气墙。
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(collider);
                else UnityEngine.Object.DestroyImmediate(collider);
            }

            if (material != null)
            {
                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;
            }

            WorldObjectId id = go.GetComponent<WorldObjectId>();
            if (id == null) id = go.AddComponent<WorldObjectId>();
            id.SetId(objectId ?? name, objectType);
            return go;
        }

        /// <summary>
        /// 生成不规则球的网格。
        /// 顶点数 = lobes × (segments+1) × (rings+1)，默认约 3 × 8 × 5 = 120 个，是明显的低模。
        /// </summary>
        public static Mesh BuildBlobMesh(float radius, int lobeCount, float noiseAmount, int seed)
        {
            const int segments = 7;   // 经度分段。段数越少棱面感越强。
            const int rings = 4;      // 纬度分段

            int verticesPerLobe = (segments + 1) * (rings + 1);

            Vector3[] vertices = new Vector3[verticesPerLobe * lobeCount];
            Vector3[] normals = new Vector3[vertices.Length];
            Vector2[] uvs = new Vector2[vertices.Length];
            int[] triangles = new int[segments * rings * 6 * lobeCount];

            System.Random random = new System.Random(seed);
            int vertexIndex = 0;
            int triangleIndex = 0;

            for (int lobe = 0; lobe < lobeCount; lobe++)
            {
                // 每个鼓包的位置偏移。第一个鼓包保持在原点，保证整体重心不跑偏。
                Vector3 offset = Vector3.zero;
                float lobeRadius = radius;

                if (lobe > 0)
                {
                    float offsetAngle = (float)random.NextDouble() * Mathf.PI * 2f;
                    float offsetDistance = radius * (0.30f + (float)random.NextDouble() * 0.28f);

                    offset = new Vector3(
                        Mathf.Cos(offsetAngle) * offsetDistance,
                        ((float)random.NextDouble() - 0.5f) * radius * 0.55f,
                        Mathf.Sin(offsetAngle) * offsetDistance);

                    lobeRadius = radius * (0.62f + (float)random.NextDouble() * 0.30f);
                }

                // 每个鼓包用不同的噪声偏移，这样它们表面的起伏互不相同。
                int noiseSeed = seed * 31 + lobe * 17;

                int lobeStart = vertexIndex;

                for (int ring = 0; ring <= rings; ring++)
                {
                    float v = (float)ring / rings;
                    float theta = v * Mathf.PI;              // 0 = 上极点
                    float sinTheta = Mathf.Sin(theta);
                    float cosTheta = Mathf.Cos(theta);

                    for (int segment = 0; segment <= segments; segment++)
                    {
                        float u = (float)segment / segments;
                        float phi = u * Mathf.PI * 2f;

                        Vector3 direction = new Vector3(
                            sinTheta * Mathf.Cos(phi),
                            cosTheta,
                            sinTheta * Mathf.Sin(phi));

                        // 噪声按方向取值 → 相邻顶点结果接近 → 表面连贯起伏。
                        float noise = DirectionNoise(direction * 2.1f, noiseSeed);
                        float deformedRadius = lobeRadius * (1f + noise * noiseAmount);

                        vertices[vertexIndex] = offset + direction * deformedRadius;
                        normals[vertexIndex] = direction;   // 占位，最后会用平面着色重算
                        uvs[vertexIndex] = new Vector2(u, 1f - v);
                        vertexIndex++;
                    }
                }

                // 该鼓包的三角形。
                //
                // 【绕序必须让法线朝外】。
                // 这个网格的参数化下，∂P/∂θ × ∂P/∂φ 指向球心（朝内），
                // 所以按 (a, a+row, a+1) 的顺序会得到朝内的法线 ——
                // 表现就是"从外面只能看到背面、模型像破了一样"（本工程实际踩到过）。
                // 因此下面统一用 (a, a+1, a+row) 的顺序，法线朝外。
                for (int ring = 0; ring < rings; ring++)
                {
                    for (int segment = 0; segment < segments; segment++)
                    {
                        int current = lobeStart + ring * (segments + 1) + segment;
                        int next = current + segments + 1;

                        triangles[triangleIndex++] = current;
                        triangles[triangleIndex++] = current + 1;
                        triangles[triangleIndex++] = next;

                        triangles[triangleIndex++] = current + 1;
                        triangles[triangleIndex++] = next + 1;
                        triangles[triangleIndex++] = next;
                    }
                }
            }

            Mesh mesh = new Mesh { name = $"Blob_{seed}" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;

            // 平面着色：每个三角面独立法线。
            // 低模用平面着色，棱面感更明显、形状更容易读；
            // 用 RecalculateNormals 做平滑着色的话，这么低的分段会显得"糊成一团"。
            RecalculateFlatNormals(mesh);

            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>
        /// 重算法线并让每个三角面使用自己的法线（平面着色）。
        /// 做法：把顶点按三角形拆开，每个三角形单独一个法线。
        /// </summary>
        /// <summary>
        /// 给地面网格加碰撞体。
        ///
        /// 【为什么必须有这一步】Unity 自带的 Plane 基本体自带 MeshCollider，
        /// 换成自己生成的网格后就没有任何碰撞体了 —— 玩家会直接穿过地面掉进虚空。
        /// 本工程实际踩到过这个坑（换地面的当天玩家开局就掉下去）。
        ///
        /// 地面网格是 ~14k 三角形的规则网格，属于"静态、不形变"的典型情况，
        /// 所以直接用 MeshCollider 是合适的做法，不需要换 BoxCollider 拼。
        /// </summary>
        public static MeshCollider AddTerrainCollider(GameObject target, Mesh terrainMesh)
        {
            if (target == null)
            {
                Debug.LogError("[灰盒] 地面对象为空，无法添加碰撞体 —— 玩家会掉进虚空。");
                return null;
            }

            MeshCollider collider = target.GetComponent<MeshCollider>();
            if (collider == null) collider = target.AddComponent<MeshCollider>();

            collider.sharedMesh = terrainMesh;
            collider.convex = false;   // 静态地面用非凸，凸体会被顶点数限制（最多 255 个）

            if (terrainMesh != null && terrainMesh.vertexCount > 60000)
            {
                Debug.LogWarning(
                    $"[灰盒] 地面网格顶点数 {terrainMesh.vertexCount} 偏多，MeshCollider 可能开销较大。" +
                    "以后可以改成单独生成一份低分辨率碰撞网格。");
            }

            return collider;
        }

        // ------------------------------------------------------------------ 网格化楼板与天花板

        /// <summary>
        /// 按【占地形状】生成楼板。
        ///
        /// why：矩形房屋可以用一整块方块当楼板，但 L 形 / '7' 形 / T 形这种带缺口的形状，
        /// 一整块方块会把缺口也铺上。所以按 gridSize 米的标准格子逐格生成 ——
        /// 落在形状内的格子生成，落在缺口里的跳过。
        ///
        /// 判断"某格是否在形状内"用的是【格子中心点】，所以形状边界会有半格的误差。
        /// 这是刻意的取舍：格子越小越精确，但顶点数按平方增长。
        /// 配合"墙体额外加厚半格"就能把缝隙盖住。
        /// </summary>
        public static Transform BuildFloorFromFootprint(
            string name,
            Transform parent,
            List<Vector2> polygonXZ,
            float gridSize,
            float topY,
            float thickness,
            Material material,
            string objectType,
            string objectIdPrefix)
        {
            Transform group = CreateGroup(name, parent);

            FindBounds(polygonXZ, out float minX, out float maxX, out float minZ, out float maxZ);

            float centerY = topY - thickness * 0.5f;
            int count = 0;

            for (float x = minX + gridSize * 0.5f; x < maxX; x += gridSize)
            {
                for (float z = minZ + gridSize * 0.5f; z < maxZ; z += gridSize)
                {
                    if (!IsPointInsidePolygon(x, z, polygonXZ)) continue;

                    count++;
                    CreateBlock($"{name}_格_{count:000}", group,
                        new Vector3(x, centerY, z),
                        new Vector3(gridSize, thickness, gridSize),
                        material, objectType, $"{objectIdPrefix}/{count:000}");
                }
            }

            if (count == 0)
            {
                Debug.LogWarning($"[灰盒] 「{name}」没有任何格子落在占地形状内 —— 检查 polygonXZ 是否为空或绕序异常。");
            }

            return group;
        }

        /// <summary>
        /// 按【占地形状】生成天花板，并在指定区域开洞（楼梯井）。
        ///
        /// 和楼板用同一套格子判断，保证上下对齐。
        /// </summary>
        public static Transform BuildCeilingFromFootprint(
            string name,
            Transform parent,
            List<Vector2> polygonXZ,
            float gridSize,
            float topY,
            float thickness,
            Material material,
            string objectType,
            string objectIdPrefix,
            bool hasHole,
            Vector2 holeMin,
            Vector2 holeMax)
        {
            Transform group = CreateGroup(name, parent);

            FindBounds(polygonXZ, out float minX, out float maxX, out float minZ, out float maxZ);

            float centerY = topY - thickness * 0.5f;
            int count = 0;

            for (float x = minX + gridSize * 0.5f; x < maxX; x += gridSize)
            {
                for (float z = minZ + gridSize * 0.5f; z < maxZ; z += gridSize)
                {
                    if (!IsPointInsidePolygon(x, z, polygonXZ)) continue;

                    // 楼梯井：洞内的格子不生成（但碰撞也不生成 —— 所以楼梯必须自己填满这一段）
                    if (hasHole &&
                        x > holeMin.x && x < holeMax.x &&
                        z > holeMin.y && z < holeMax.y)
                    {
                        continue;
                    }

                    count++;
                    CreateBlock($"{name}_格_{count:000}", group,
                        new Vector3(x, centerY, z),
                        new Vector3(gridSize, thickness, gridSize),
                        material, objectType, $"{objectIdPrefix}/{count:000}");
                }
            }

            return group;
        }

        private static void FindBounds(List<Vector2> points, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = float.MaxValue; maxX = float.MinValue;
            minZ = float.MaxValue; maxZ = float.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                Vector2 p = points[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }
        }

        /// <summary>射线法判断点是否在多边形内（多边形用 XZ 平面的 (x, z) 表示）。</summary>
        public static bool IsPointInsidePolygon(float x, float z, List<Vector2> polygonXZ)
        {
            if (polygonXZ == null || polygonXZ.Count < 3) return false;

            bool inside = false;
            int count = polygonXZ.Count;

            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Vector2 a = polygonXZ[i];
                Vector2 b = polygonXZ[j];

                // 射线沿 +X 方向。判断边 (b → a) 是否跨过 z 这条水平线。
                bool crosses = (a.y > z) != (b.y > z);
                if (!crosses) continue;

                float t = (z - a.y) / (b.y - a.y);
                float intersectX = a.x + t * (b.x - a.x);

                if (x < intersectX) inside = !inside;
            }

            return inside;
        }

        // ------------------------------------------------------------------ 湖泊

        /// <summary>
        /// 生成湖盆网格：一块下凹的碗形地面。
        ///
        /// 形状：外圈压在 y = 0（和地形齐平，避免出现缝隙），往里平滑下沉到 -depth，
        /// 底部一块平台。这样从岸上看是自然的斜坡，不会像"方形坑"。
        ///
        /// 顶点色：岸边（泥色）→ 浅水（沙色）→ 深水（暗色），
        /// 所以即使水很透明也不会看到"水泥池底"。
        /// </summary>
        public static Mesh BuildLakeBedMesh(
            float sizeX,
            float sizeZ,
            float depth,
            float slopeFraction,
            Color shoreColor,
            Color shallowColor,
            Color deepColor,
            int seed)
        {
            const int cells = 16;   // 每边细分。16×16 对湖泊足够，顶点约 289 个。

            int verticesPerRow = cells + 1;
            int vertexCount = verticesPerRow * verticesPerRow;

            Vector3[] vertices = new Vector3[vertexCount];
            Color[] colors = new Color[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];
            int[] triangles = new int[cells * cells * 6];

            float halfX = sizeX * 0.5f;
            float halfZ = sizeZ * 0.5f;

            // slopeFraction：从边缘往里走多少比例时到达最低点。
            float slope = Mathf.Clamp(slopeFraction, 0.05f, 0.9f);

            for (int z = 0; z <= cells; z++)
            {
                for (int x = 0; x <= cells; x++)
                {
                    int index = z * verticesPerRow + x;

                    float u = (float)x / cells;
                    float v = (float)z / cells;

                    float localX = Mathf.Lerp(-halfX, halfX, u);
                    float localZ = Mathf.Lerp(-halfZ, halfZ, v);

                    // 到边缘的归一化距离：0 = 边缘，1 = 中心。
                    float edgeDistance = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v)) / 0.5f;

                    // 平滑下沉曲线：边缘附近变化慢（岸），中间到达最深。
                    float t = Mathf.Clamp01(edgeDistance / slope);
                    float smooth = t * t * (3f - 2f * t);   // smoothstep，避免出现硬折角

                    // 轻微噪声，避免边坡看起来像数学曲面。
                    float noise = DirectionNoise(new Vector3(localX * 0.25f, 3.7f, localZ * 0.25f), seed) * 0.06f;

                    float y = Mathf.Lerp(0f, -depth, Mathf.Clamp01(smooth + noise));

                    vertices[index] = new Vector3(localX, y, localZ);
                    colors[index] = SampleLakeBedColor(smooth, shoreColor, shallowColor, deepColor);
                    uvs[index] = new Vector2(u, v);
                }
            }

            int triangleIndex = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int current = z * verticesPerRow + x;
                    int next = current + verticesPerRow;

                    // 绕序保证法线朝上
                    triangles[triangleIndex++] = current;
                    triangles[triangleIndex++] = next;
                    triangles[triangleIndex++] = current + 1;

                    triangles[triangleIndex++] = current + 1;
                    triangles[triangleIndex++] = next;
                    triangles[triangleIndex++] = next + 1;
                }
            }

            Mesh mesh = new Mesh { name = "LakeBed" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        private static Color SampleLakeBedColor(float depthT, Color shoreColor, Color shallowColor, Color deepColor)
        {
            // depthT：0 = 岸边，1 = 最深
            if (depthT < 0.35f)
            {
                return Color.Lerp(shoreColor, shallowColor, depthT / 0.35f);
            }

            return Color.Lerp(shallowColor, deepColor, (depthT - 0.35f) / 0.65f);
        }

        /// <summary>
        /// 生成水面网格。
        ///
        /// 【为什么不能用 Unity 的 Plane 基本体】
        /// Plane 只有 10×10 个面片（共 121 个顶点）。波浪位移是【顶点着色器】做的，
        /// 顶点太少的话正弦波根本表现不出来 —— 水会看起来是一块平的半透明板。
        ///
        /// 【为什么默认 128】波浪位移在顶点上算、三角形内部靠线性插值，
        /// 格子越粗插值误差越大 → 每格边界出现折线感，看起来像"一格一格"的。
        /// 13×9 米的湖用 128 格时每格约 10×7 厘米，肉眼不再看得出格子。
        /// 顶点数 129² ≈ 1.7 万，对单个湖泊完全在预算内。
        /// </summary>
        public static Mesh BuildWaterSurfaceMesh(Vector2 size, int cells = 128)
        {
            cells = Mathf.Clamp(cells, 4, 256);

            int verticesPerRow = cells + 1;
            int vertexCount = verticesPerRow * verticesPerRow;

            Vector3[] vertices = new Vector3[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];
            int[] triangles = new int[cells * cells * 6];

            float halfX = size.x * 0.5f;
            float halfZ = size.y * 0.5f;

            for (int z = 0; z <= cells; z++)
            {
                for (int x = 0; x <= cells; x++)
                {
                    int index = z * verticesPerRow + x;

                    float u = (float)x / cells;
                    float v = (float)z / cells;

                    // 水面在 XZ 平面，Y 由 shader 的 Gerstner 波位移决定（起点是 0）。
                    vertices[index] = new Vector3(
                        Mathf.Lerp(-halfX, halfX, u),
                        0f,
                        Mathf.Lerp(-halfZ, halfZ, v));

                    uvs[index] = new Vector2(u, v);
                }
            }

            int triangleIndex = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int current = z * verticesPerRow + x;
                    int next = current + verticesPerRow;

                    // 绕序保证法线朝上
                    triangles[triangleIndex++] = current;
                    triangles[triangleIndex++] = next;
                    triangles[triangleIndex++] = current + 1;

                    triangles[triangleIndex++] = current + 1;
                    triangles[triangleIndex++] = next;
                    triangles[triangleIndex++] = next + 1;
                }
            }

            Mesh mesh = new Mesh { name = "WaterSurface" };
            mesh.vertices = vertices;
            // 静态法线朝上：真正的法线由 shader 用偏导数算，这里只是占位。
            mesh.normals = BuildUpwardNormals(vertexCount);
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();

            return mesh;
        }

        private static Vector3[] BuildUpwardNormals(int count)
        {
            Vector3[] normals = new Vector3[count];
            for (int i = 0; i < count; i++) normals[i] = Vector3.up;
            return normals;
        }

        /// <summary>
        /// 给湖泊生成【水面】。
        ///
        /// 关键实现点：
        ///   1. 网格自己生成（分辨率够高，波浪才看得出来），不是 Plane 基本体
        ///   2. 水面在【凹地的正中间高度】：湖底最低 -depth、岸是 0，
        ///      水面放 -depth * 0.45，既有"深水看不到底"，岸线也不会露在空气里
        ///   3. 不加碰撞体 —— 玩家在水里走的是湖底地形
        /// </summary>
        public static GameObject CreateWaterSurface(
            string name,
            Transform parent,
            Vector2 center,
            Vector2 size,
            float waterLevel,
            Material material,
            string objectType = "water")
        {
            GameObject water = new GameObject(name);
            water.transform.SetParent(parent, false);
            water.transform.localPosition = new Vector3(center.x, waterLevel, center.y);

            MeshFilter filter = water.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildWaterSurfaceMesh(size);

            MeshRenderer renderer = water.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            // 水面不投影也不接收阴影：它太薄，投影会很脏。
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            WorldObjectId id = water.AddComponent<WorldObjectId>();
            id.SetId(objectType, objectType);

            return water;
        }

        // ------------------------------------------------------------------ 地面与草

        /// <summary>
        /// 生成地面的网格。
        ///
        /// 用网格而不是 Unity 自带的 Plane：只有自己生成网格才能给每个顶点写颜色，
        /// 从而用【顶点色】区分草地和泥地（Plane 只有一个面，没法做区域差异）。
        ///
        /// 颜色由噪声决定，所以草地和泥地是不规则的大块交错，边界自然过渡。
        /// 分辨率 cellSize 越小、颜色越细腻，但顶点越多。
        /// </summary>
        /// <summary>地形上要挖掉的一块区域（用来给湖泊让位）。</summary>
        public struct TerrainHole
        {
            public Vector2 Center;   // 局部坐标（地图中心为原点）
            public Vector2 Size;

            public TerrainHole(Vector2 center, Vector2 size)
            {
                Center = center;
                Size = size;
            }
        }

        public static Mesh BuildTerrainMesh(
            float sizeX,
            float sizeZ,
            float cellSize,
            Color mudColor,
            Color grassColor,
            int seed,
            float noiseScale = 0.055f,
            float grassThreshold = 0.42f,
            TerrainHole[] holes = null)
        {
            cellSize = Mathf.Max(0.25f, cellSize);

            int cellsX = Mathf.Max(1, Mathf.CeilToInt(sizeX / cellSize));
            int cellsZ = Mathf.Max(1, Mathf.CeilToInt(sizeZ / cellSize));

            int verticesPerRow = cellsX + 1;
            int vertexCount = verticesPerRow * (cellsZ + 1);

            Vector3[] vertices = new Vector3[vertexCount];
            Color[] colors = new Color[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];

            // 三角面列表先收集再转数组：因为要跳过洞里的格子，数量不固定。
            List<int> triangleList = new List<int>(cellsX * cellsZ * 6);

            float stepX = sizeX / cellsX;
            float stepZ = sizeZ / cellsZ;

            float halfX = sizeX * 0.5f;
            float halfZ = sizeZ * 0.5f;

            Vector2 center = new Vector2(sizeX * 0.5f, sizeZ * 0.5f);

            for (int z = 0; z <= cellsZ; z++)
            {
                for (int x = 0; x <= cellsX; x++)
                {
                    int index = z * verticesPerRow + x;

                    float worldX = x * stepX;
                    float worldZ = z * stepZ;

                    // 地面在 y = 0 平面；世界坐标以地图中心为原点。
                    vertices[index] = new Vector3(worldX - halfX, 0f, worldZ - halfZ);
                    colors[index] = SampleTerrainColor(worldX, worldZ, center, mudColor, grassColor, seed, noiseScale, grassThreshold);
                    uvs[index] = new Vector2(worldX, worldZ);
                }
            }

            int holeCount = holes != null ? holes.Length : 0;
            int skippedCells = 0;

            for (int z = 0; z < cellsZ; z++)
            {
                for (int x = 0; x < cellsX; x++)
                {
                    int current = z * verticesPerRow + x;
                    int next = current + verticesPerRow;

                    float cellCenterX = (x + 0.5f) * stepX - halfX;
                    float cellCenterZ = (z + 0.5f) * stepZ - halfZ;

                    if (IsInsideAnyHole(cellCenterX, cellCenterZ, holes))
                    {
                        skippedCells++;
                        continue;   // 湖面处不生成地面，否则会把湖盖住
                    }

                    // 绕序保证法线朝上（本工程在树冠上因为绕序反了踩过"只看到背面"的坑）。
                    triangleList.Add(current);
                    triangleList.Add(next);
                    triangleList.Add(current + 1);

                    triangleList.Add(current + 1);
                    triangleList.Add(next);
                    triangleList.Add(next + 1);
                }
            }

            Mesh mesh = new Mesh { name = "Terrain" };
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.uv = uvs;
            mesh.triangles = triangleList.ToArray();
            RecalculateFlatNormals(mesh);
            mesh.RecalculateBounds();

            if (holeCount > 0)
            {
                Debug.Log($"[灰盒] 地形已为 {holeCount} 个湖泊开洞：跳过 {skippedCells} 个格子。");
            }

            return mesh;
        }

        /// <summary>
        /// 用【格子级】精度判断某个格子中心算不算在洞里。
        ///
        /// 为什么逐格子而不是整块矩形跳过：格子 1 米、湖泊十几米，
        /// 整块跳过会在湖边留下一个巨大的方角缺口；逐格子判断能把缺口限制在 1 米以内，
        /// 再让湖盆外圈压在缺口边缘上就看不见了。
        /// </summary>
        private static bool IsInsideAnyHole(float x, float z, TerrainHole[] holes)
        {
            if (holes == null) return false;

            for (int i = 0; i < holes.Length; i++)
            {
                TerrainHole hole = holes[i];
                float halfSizeX = hole.Size.x * 0.5f;
                float halfSizeZ = hole.Size.y * 0.5f;

                if (x > hole.Center.x - halfSizeX && x < hole.Center.x + halfSizeX &&
                    z > hole.Center.y - halfSizeZ && z < hole.Center.y + halfSizeZ)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 采样某个世界坐标处的草地/泥地颜色。
        /// 两层不同频率的噪声叠加，避免出现"每个斑块一样大"的规律感。
        /// </summary>
        public static Color SampleTerrainColor(
            float worldX,
            float worldZ,
            Vector2 center,
            Color mudColor,
            Color grassColor,
            int seed,
            float noiseScale,
            float grassThreshold)
        {
            float large = DirectionNoise(new Vector3((worldX - center.x) * noiseScale, 0f, (worldZ - center.y) * noiseScale), seed);
            float small = DirectionNoise(new Vector3((worldX - center.x) * noiseScale * 3.7f, 11.3f, (worldZ - center.y) * noiseScale * 3.7f), seed + 4099);

            float mask = large * 0.75f + small * 0.25f;
            float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(grassThreshold - 0.18f, grassThreshold + 0.18f, mask));

            Color color = Color.Lerp(mudColor, grassColor, blend);

            // 轻微明暗抖动，让大片同色区域不至于死板。
            float jitter = DirectionNoise(new Vector3(worldX * 1.9f, 5.1f, worldZ * 1.9f), seed + 811) * 0.05f;
            color.r = Mathf.Clamp01(color.r + jitter);
            color.g = Mathf.Clamp01(color.g + jitter);
            color.b = Mathf.Clamp01(color.b + jitter);
            color.a = 1f;

            return color;
        }

        /// <summary>草叶生成参数。</summary>
        public struct GrassSettings
        {
            public int Seed;
            public float Density;          // 每平方米多少片（近似值）
            public float MinHeight;
            public float MaxHeight;
            public float MinWidth;
            public float MaxWidth;
            public Color BaseColor;
            public Color TipColor;
            public float NoiseScale;       // 草地/泥地噪声的频率，必须和地面用的一致
            public float GrassThreshold;   // 和地面用的一致，保证草只长在草地上
            public int ChunkSize;          // 每多少米合并成一个网格
        }

        /// <summary>
        /// 在指定的正方形区域里散布草叶，按 chunk 合并成若干网格对象。
        ///
        /// 为什么按 chunk 合并而不是"一片草一个物体"：
        ///   8000 个 GameObject 会让编辑器卡顿、场景文件巨大。合并成几十个网格对象后
        ///   顶点数完全一样，但对象数从 8000 降到几十个。
        /// </summary>
        public static void BuildGrassField(
            Transform parent,
            float sizeX,
            float sizeZ,
            Material material,
            GrassSettings settings)
        {
            int chunkSize = Mathf.Max(2, settings.ChunkSize);
            int chunksX = Mathf.CeilToInt(sizeX / chunkSize);
            int chunksZ = Mathf.CeilToInt(sizeZ / chunkSize);

            float halfX = sizeX * 0.5f;
            float halfZ = sizeZ * 0.5f;

            Vector2 center = new Vector2(sizeX * 0.5f, sizeZ * 0.5f);
            int bladesPerChunk = Mathf.Max(1, Mathf.RoundToInt(settings.Density * chunkSize * chunkSize));

            for (int cz = 0; cz < chunksZ; cz++)
            {
                for (int cx = 0; cx < chunksX; cx++)
                {
                    // 每个 chunk 用独立种子，这样增删一个 chunk 不会让其它 chunk 的草全部重排。
                    System.Random random = new System.Random(settings.Seed + cz * 7919 + cx * 104729);

                    List<Vector3> vertices = new List<Vector3>();
                    List<Color> colors = new List<Color>();

                    float chunkMinX = cx * chunkSize - halfX;
                    float chunkMinZ = cz * chunkSize - halfZ;

                    for (int i = 0; i < bladesPerChunk; i++)
                    {
                        float worldX = chunkMinX + (float)random.NextDouble() * chunkSize;
                        float worldZ = chunkMinZ + (float)random.NextDouble() * chunkSize;

                        // 先判断这里是不是草地：不是就不长草。
                        // 用和地面完全相同的噪声参数，保证"草长在草地上"、不会长到泥地里。
                        float grassMask = SampleGrassMask(worldX, worldZ, center, settings.Seed,
                            settings.NoiseScale, settings.GrassThreshold);

                        if (grassMask < 0.45f) continue;

                        float height = Mathf.Lerp(settings.MinHeight, settings.MaxHeight, (float)random.NextDouble());
                        float width = Mathf.Lerp(settings.MinWidth, settings.MaxWidth, (float)random.NextDouble());

                        // 随机偏航角：单片草从侧面看是一条线，靠朝向分散来保证任何角度都有一批是正对相机的。
                        float yaw = (float)random.NextDouble() * 360f;
                        float lean = ((float)random.NextDouble() - 0.5f) * 22f;

                        AppendGrassBlade(vertices, colors,
                            new Vector3(worldX, 0f, worldZ),
                            height, width, yaw, lean,
                            settings.BaseColor, settings.TipColor);
                    }

                    if (vertices.Count == 0) continue;

                    GameObject chunkGo = new GameObject($"草地_{cx:00}_{cz:00}");
                    chunkGo.transform.SetParent(parent, false);

                    Mesh mesh = new Mesh { name = chunkGo.name };
                    mesh.SetVertices(vertices);
                    mesh.SetColors(colors);
                    mesh.SetTriangles(BuildSequentialTriangles(vertices.Count), 0);
                    RecalculateFlatNormals(mesh);
                    mesh.RecalculateBounds();

                    MeshFilter filter = chunkGo.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;

                    MeshRenderer renderer = chunkGo.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }
        }

        /// <summary>
        /// 草地遮罩：1 = 完全是草地，0 = 完全是泥地。
        /// 和 SampleTerrainColor 用同一套噪声参数，保证"草长在草地上"。
        /// </summary>
        private static float SampleGrassMask(
            float worldX, float worldZ, Vector2 center, int seed, float noiseScale, float grassThreshold)
        {
            float large = DirectionNoise(new Vector3((worldX - center.x) * noiseScale, 0f, (worldZ - center.y) * noiseScale), seed);
            float small = DirectionNoise(new Vector3((worldX - center.x) * noiseScale * 3.7f, 11.3f, (worldZ - center.y) * noiseScale * 3.7f), seed + 4099);

            float mask = large * 0.75f + small * 0.25f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(grassThreshold - 0.18f, grassThreshold + 0.18f, mask));
        }

        /// <summary>一片草叶 = 一个四边形（两个三角形），底部宽、顶部收窄并向一侧倾斜。</summary>
        private static void AppendGrassBlade(
            List<Vector3> vertices,
            List<Color> colors,
            Vector3 basePosition,
            float height,
            float width,
            float yawDegrees,
            float leanDegrees,
            Color baseColor,
            Color tipColor)
        {
            Quaternion yaw = Quaternion.Euler(0f, yawDegrees, 0f);
            Quaternion lean = yaw * Quaternion.Euler(leanDegrees, 0f, 0f);

            Vector3 right = yaw * (Vector3.right * (width * 0.5f));
            Vector3 up = lean * Vector3.up;
            Vector3 tipCenter = basePosition + up * height;

            // 顶点顺序：左下、右下、左上、右上 —— 配合索引 (0,2,1)(1,2,3) 得到朝 +Z 的法线。
            vertices.Add(basePosition - right);
            vertices.Add(basePosition + right);
            vertices.Add(tipCenter - right * 0.35f);
            vertices.Add(tipCenter + right * 0.35f);

            colors.Add(baseColor);
            colors.Add(baseColor);
            colors.Add(tipColor);
            colors.Add(tipColor);
        }

        private static int[] BuildSequentialTriangles(int vertexCount)
        {
            int[] triangles = new int[vertexCount / 4 * 6];
            int triangleIndex = 0;

            for (int i = 0; i < vertexCount; i += 4)
            {
                triangles[triangleIndex++] = i;
                triangles[triangleIndex++] = i + 2;
                triangles[triangleIndex++] = i + 1;

                triangles[triangleIndex++] = i + 1;
                triangles[triangleIndex++] = i + 2;
                triangles[triangleIndex++] = i + 3;
            }

            return triangles;
        }

        private static void RecalculateFlatNormals(Mesh mesh)
        {
            Vector3[] sourceVertices = mesh.vertices;
            int[] sourceTriangles = mesh.triangles;
            Vector2[] sourceUvs = mesh.uv;
            Color[] sourceColors = mesh.colors;

            bool hasUvs = sourceUvs != null && sourceUvs.Length == sourceVertices.Length;
            bool hasColors = sourceColors != null && sourceColors.Length == sourceVertices.Length;

            int count = sourceTriangles.Length;

            Vector3[] flatVertices = new Vector3[count];
            Vector3[] flatNormals = new Vector3[count];
            int[] flatTriangles = new int[count];
            Vector2[] flatUvs = hasUvs ? new Vector2[count] : null;
            Color[] flatColors = hasColors ? new Color[count] : null;

            for (int i = 0; i < count; i += 3)
            {
                int a = sourceTriangles[i];
                int b = sourceTriangles[i + 1];
                int c = sourceTriangles[i + 2];

                Vector3 va = sourceVertices[a];
                Vector3 vb = sourceVertices[b];
                Vector3 vc = sourceVertices[c];

                Vector3 normal = Vector3.Cross(vb - va, vc - va).normalized;

                flatVertices[i] = va;
                flatVertices[i + 1] = vb;
                flatVertices[i + 2] = vc;

                flatNormals[i] = normal;
                flatNormals[i + 1] = normal;
                flatNormals[i + 2] = normal;

                if (hasUvs)
                {
                    flatUvs[i] = sourceUvs[a];
                    flatUvs[i + 1] = sourceUvs[b];
                    flatUvs[i + 2] = sourceUvs[c];
                }

                if (hasColors)
                {
                    flatColors[i] = sourceColors[a];
                    flatColors[i + 1] = sourceColors[b];
                    flatColors[i + 2] = sourceColors[c];
                }

                flatTriangles[i] = i;
                flatTriangles[i + 1] = i + 1;
                flatTriangles[i + 2] = i + 2;
            }

            mesh.Clear();

            // 【必须显式声明 32 位索引】
            //
            // 平面着色会把每个三角面拆成 3 个独立顶点，所以顶点数 = 三角面数 × 3。
            // Unity 的 Mesh 默认索引格式是 16 位，上限 65535 个顶点 —— 超过之后
            // 索引会被截断，表现是【网格只渲染出一部分】（本工程把地面从 60×60
            // 扩到 148.8×148.8 之后正好踩到：44,402 面 → 133,206 顶点，
            // 超过上限一倍，于是地面"消失了一半"）。
            //
            // 光靠"先写 vertices 再写 triangles"是不够可靠的：Unity 有时会自动升级
            // 索引格式、有时不会，取决于写入顺序与具体版本。显式声明才是确定的。
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.vertices = flatVertices;
            mesh.normals = flatNormals;
            mesh.triangles = flatTriangles;

            // 顶点色必须在 Clear() 之后重新写入！
            // 地面是靠顶点色区分草地/泥地的，忘了搬的话地面会变成一片纯色（本工程踩过这个坑）。
            if (hasColors) mesh.colors = flatColors;
            if (hasUvs) mesh.uv = flatUvs;
        }

        /// <summary>
        /// 基于方向的三维值噪声，返回 -1 ~ 1。
        /// 用哈希而不是 Mathf.PerlinNoise：Perlin 的取值受整数格点限制，
        /// 而我们希望"同样的方向永远得到同样的扰动"，哈希更直接也更快。
        /// </summary>
        private static float DirectionNoise(Vector3 point, int seed)
        {
            int x = Mathf.FloorToInt(point.x);
            int y = Mathf.FloorToInt(point.y);
            int z = Mathf.FloorToInt(point.z);

            float fx = point.x - x;
            float fy = point.y - y;
            float fz = point.z - z;

            // 平滑插值权重（跟 Perlin 一样的 6t⁵-15t⁴+10t³），保证表面不出现硬折角。
            float ux = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
            float uy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
            float uz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);

            float c000 = Hash01(x, y, z, seed);
            float c100 = Hash01(x + 1, y, z, seed);
            float c010 = Hash01(x, y + 1, z, seed);
            float c110 = Hash01(x + 1, y + 1, z, seed);
            float c001 = Hash01(x, y, z + 1, seed);
            float c101 = Hash01(x + 1, y, z + 1, seed);
            float c011 = Hash01(x, y + 1, z + 1, seed);
            float c111 = Hash01(x + 1, y + 1, z + 1, seed);

            float x00 = Mathf.Lerp(c000, c100, ux);
            float x10 = Mathf.Lerp(c010, c110, ux);
            float x01 = Mathf.Lerp(c001, c101, ux);
            float x11 = Mathf.Lerp(c011, c111, ux);

            float y0 = Mathf.Lerp(x00, x10, uy);
            float y1 = Mathf.Lerp(x01, x11, uy);

            return Mathf.Lerp(y0, y1, uz) * 2f - 1f;
        }

        /// <summary>整数哈希 → 0~1 的伪随机值。</summary>
        private static float Hash01(int x, int y, int z, int seed)
        {
            unchecked
            {
                int hash = seed * 374761393;
                hash = (hash ^ x) * 668265263;
                hash = (hash ^ y) * 1274126177;
                hash = (hash ^ z) * 2147483647;
                hash ^= hash >> 13;
                hash *= 1274126177;
                hash ^= hash >> 16;

                return (hash & 0x7FFFFFFF) / (float)0x7FFFFFFF;
            }
        }

        /// <summary>
        /// 给已有对象补上稳定 ID（存档/联机用）。
        /// 刻意放在运行时工具里：编辑器脚本就不需要直接引用存档类型，
        /// 少一层跨程序集依赖，也少一个未来改名时容易漏掉的引用点。
        /// </summary>
        public static void EnsureObjectId(GameObject target, string objectId, string objectType)
        {
            if (target == null) return;

            WorldObjectId id = target.GetComponent<WorldObjectId>();
            if (id == null) id = target.AddComponent<WorldObjectId>();
            id.SetId(objectId, objectType);
        }

        /// <summary>按「起点-终点 + 厚度」创建一段墙（水平或竖直都自动处理）。</summary>
        public static GameObject CreateWall(
            string name,
            Transform parent,
            Vector3 start,
            Vector3 end,
            float thickness,
            float height,
            Material material,
            string objectType = "wall",
            string objectId = null)
        {
            Vector3 center = (start + end) * 0.5f;
            Vector3 size = new Vector3(
                Mathf.Abs(end.x - start.x) + thickness,
                height,
                Mathf.Abs(end.z - start.z) + thickness);

            center.y = height * 0.5f;
            return CreateBlock(name, parent, center, size, material, objectType, objectId);
        }

        /// <summary>创建一个空的分组节点，便于在 Hierarchy 里折叠整理。</summary>
        public static Transform CreateGroup(string name, Transform parent, Vector3 localPosition = default)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }
    }
}
