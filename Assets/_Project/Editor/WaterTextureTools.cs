#if UNITY_EDITOR
using System.IO;
using Game.World;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 水体贴图工具。
    ///
    /// 水体 shader（Game/Water/RealisticWater）支持四张外部贴图：
    ///   _NormalMap     法线贴图（微观涟漪）
    ///   _FoamNoiseTex  泡沫 / 岸边消融噪声
    ///   _CausticsTex   水下焦散
    ///   _GlintTex      碎斑高光噪点
    ///
    /// 本工具做两件事：
    ///   1. 生成【程序化占位贴图】并保存成资产 —— 这样不找美术资源也能立刻看到效果
    ///   2. 把它们自动挂到水体材质上
    ///
    /// 【以后换成自己的图】直接在水体材质的 Inspector 里替换这四张贴图即可，
    /// shader 不需要改。法线贴图记得把导入设置的 Texture Type 设成 Normal map。
    /// </summary>
    public static class WaterTextureTools
    {
        private const string TextureFolder = "Assets/_Project/Art/Environment/Water/Textures";

        private const string NormalMapPath = TextureFolder + "/T_Water_Normal.png";
        private const string FoamNoisePath = TextureFolder + "/T_Water_FoamNoise.png";
        private const string CausticsPath = TextureFolder + "/T_Water_Caustics.png";
        private const string GlintPath = TextureFolder + "/T_Water_Glint.png";

        private const int TextureSize = 512;

        [MenuItem("游戏/水体/生成占位贴图并挂到材质", priority = 70)]
        public static void GenerateAndAssign()
        {
            if (!EditorFileUtility.EnsureAssetFolder(TextureFolder))
            {
                Debug.LogError($"[水体] 无法创建贴图目录：{TextureFolder}");
                return;
            }

            GenerateNormalMap();
            GenerateFoamNoise();
            GenerateCaustics();
            GenerateGlint();

            AssetDatabase.Refresh();

            ApplyImportSettings();
            AssignToMaterial();

            Debug.Log($"[水体] 占位贴图已生成到 {TextureFolder}，并已挂到 {GreyboxPrimitives.WaterMaterialPath}。");
        }

        // ------------------------------------------------------------------ 生成

        /// <summary>法线贴图：多层噪声的梯度 → 切线空间法线（RGB 存储）。</summary>
        private static void GenerateNormalMap()
        {
            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, true);

            const float strength = 2.4f;

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float u = (float)x / TextureSize;
                    float v = (float)y / TextureSize;

                    // 用相邻采样求梯度，得到"水面微起伏"的法线
                    float h = WaterNoise(u, v, 7f);
                    float hx = WaterNoise(u + 1f / TextureSize, v, 7f);
                    float hy = WaterNoise(u, v + 1f / TextureSize, 7f);

                    Vector3 normal = new Vector3(-(hx - h) * strength, -(hy - h) * strength, 1f).normalized;

                    // 法线贴图的编码：xyz 从 [-1,1] 映射到 [0,1]
                    texture.SetPixel(x, y, new Color(
                        normal.x * 0.5f + 0.5f,
                        normal.y * 0.5f + 0.5f,
                        normal.z * 0.5f + 0.5f,
                        1f));
                }
            }

            SaveTexture(texture, NormalMapPath);
        }

        /// <summary>泡沫 / 消融噪声：多八度噪声，红色通道是主要数据。</summary>
        private static void GenerateFoamNoise()
        {
            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, true);

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float u = (float)x / TextureSize;
                    float v = (float)y / TextureSize;

                    float value = Mathf.Clamp01(WaterNoise(u, v, 5f));

                    // 拉高对比，让"啃咬"的边界更明显
                    value = Mathf.Pow(value, 1.35f);

                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            SaveTexture(texture, FoamNoisePath);
        }

        /// <summary>焦散：两层噪声求交集，形成网状波光。</summary>
        private static void GenerateCaustics()
        {
            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, true);

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float u = (float)x / TextureSize;
                    float v = (float)y / TextureSize;

                    float n1 = WaterNoise(u, v, 6f);
                    float n2 = WaterNoise(u * 1.31f + 0.37f, v * 1.31f + 0.61f, 6f);

                    // min 求交集 → 只有两层都亮的细线留下来，就是焦散的网
                    float caustic = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(n1 - n2) * 3f), 3f);

                    // 焦散是加色的，所以偏亮
                    Color color = new Color(caustic, caustic, caustic, 1f);
                    texture.SetPixel(x, y, color);
                }
            }

            SaveTexture(texture, CausticsPath);
        }

        /// <summary>碎斑高光：高频稀疏噪点。</summary>
        private static void GenerateGlint()
        {
            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, true);

            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float u = (float)x / TextureSize;
                    float v = (float)y / TextureSize;

                    float value = WaterNoise(u, v, 24f);

                    // 只留最亮的一小部分，形成稀疏的闪点
                    value = Mathf.Clamp01((value - 0.55f) * 3.2f);
                    value = Mathf.Pow(value, 1.4f);

                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            SaveTexture(texture, GlintPath);
        }

        // ------------------------------------------------------------------ 噪声

        /// <summary>多八度值噪声，返回 0~1。u/v 会按 frequency 缩放。</summary>
        private static float WaterNoise(float u, float v, float frequency)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            float totalAmplitude = 0f;

            for (int octave = 0; octave < 5; octave++)
            {
                sum += ValueNoise(u * frequency, v * frequency) * amplitude;
                totalAmplitude += amplitude;
                frequency *= 2.03f;
                amplitude *= 0.5f;
            }

            return totalAmplitude > 0f ? sum / totalAmplitude : 0f;
        }

        private static float ValueNoise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);

            float xf = x - xi;
            float yf = y - yi;

            // 平滑插值
            float u = xf * xf * (3f - 2f * xf);
            float v = yf * yf * (3f - 2f * yf);

            float a = Hash01(xi, yi);
            float b = Hash01(xi + 1, yi);
            float c = Hash01(xi, yi + 1);
            float d = Hash01(xi + 1, yi + 1);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static float Hash01(int x, int y)
        {
            unchecked
            {
                int hash = x * 374761393 + y * 668265263;
                hash = (hash ^ (hash >> 13)) * 1274126177;
                hash ^= hash >> 16;
                return (hash & 0x7FFFFFFF) / (float)0x7FFFFFFF;
            }
        }

        // ------------------------------------------------------------------ 保存与导入

        private static void SaveTexture(Texture2D texture, string assetPath)
        {
            byte[] png = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);

            // 【必须用绝对路径写盘】System.IO.File 不认 "Assets/..." 这种资产路径，
            // 只会当相对路径处理，是否可用取决于 Unity 的工作目录 —— 不可靠。
            // 而且目录可能还不存在于磁盘上（AssetDatabase.CreateFolder 不保证同步），
            // 所以先转绝对路径、再确保目录、最后写文件。
            string absolutePath = AssetPathToAbsolute(assetPath);
            if (string.IsNullOrEmpty(absolutePath))
            {
                Debug.LogError($"[水体] 无法把资产路径转成绝对路径：{assetPath}");
                return;
            }

            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(absolutePath, png);
        }

        /// <summary>把 "Assets/xxx" 形式的资产路径转成磁盘绝对路径。</summary>
        private static string AssetPathToAbsolute(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            // Application.dataPath 就是项目里的 Assets 目录。
            string projectRoot = Application.dataPath;
            const string suffix = "/Assets";

            if (projectRoot.EndsWith(suffix))
            {
                projectRoot = projectRoot.Substring(0, projectRoot.Length - suffix.Length);
            }

            return Path.Combine(projectRoot, assetPath);
        }

        private static void ApplyImportSettings()
        {
            // 法线贴图必须声明成 NormalMap，否则 UnpackNormalScale 解出来的方向是错的。
            SetImporter(NormalMapPath, TextureImporterType.NormalMap);
            SetImporter(FoamNoisePath, TextureImporterType.Default);
            SetImporter(CausticsPath, TextureImporterType.Default);
            SetImporter(GlintPath, TextureImporterType.Default);
        }

        private static void SetImporter(string assetPath, TextureImporterType type)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;

            bool changed = false;

            if (importer.textureType != type)
            {
                importer.textureType = type;
                changed = true;
            }

            if (importer.wrapMode != TextureWrapMode.Repeat)
            {
                importer.wrapMode = TextureWrapMode.Repeat;   // 水面平铺必须 Repeat
                changed = true;
            }

            if (!importer.mipmapEnabled)
            {
                importer.mipmapEnabled = true;
                changed = true;
            }

            if (changed) importer.SaveAndReimport();
        }

        private static void AssignToMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(GreyboxPrimitives.WaterMaterialPath);

            if (material == null)
            {
                Debug.LogWarning(
                    $"[水体] 找不到水体材质 {GreyboxPrimitives.WaterMaterialPath}。" +
                    "请先重建场景（生成器会创建它），再执行本菜单。");
                return;
            }

            AssignTexture(material, "_NormalMap", NormalMapPath);
            AssignTexture(material, "_FoamNoiseTex", FoamNoisePath);
            AssignTexture(material, "_CausticsTex", CausticsPath);
            AssignTexture(material, "_GlintTex", GlintPath);

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
        }

        private static void AssignTexture(Material material, string property, string assetPath)
        {
            Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
            if (texture == null) return;
            if (!material.HasProperty(property)) return;

            material.SetTexture(property, texture);
        }
    }
}
#endif
