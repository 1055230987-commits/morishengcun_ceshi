using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Gameplay.Building
{
    /// <summary>
    /// 建造系统用的运行时材质工具。
    ///
    /// 【为什么单独抽出来 —— 这是一个真实的缺陷修复】
    /// 早先在 BuildArea 和 BuildController 里各写了一遍"创建半透明材质"，
    /// 两处都只设了 `_Surface` / `_Blend` / renderQueue。那是不够的：
    ///
    /// URP 的 Lit/Unlit 是【用 shader 变体 + 混合状态】实现透明的，
    /// 光把 `_Surface` 设成 Transparent 只会切换材质 Inspector 显示哪些属性，
    /// 真正决定"怎么混合"的是这几个混合状态：
    ///   _SrcBlend / _DstBlend   —— 混合方程（默认是 One / Zero，即不透明）
    ///   _ZWrite                 —— 透明物体必须关深度写入
    ///   _AlphaPremultiplyOn     —— 预乘 alpha 的开关
    /// 少了它们，材质仍然是【不透明】的，alpha 完全不起作用 ——
    /// 表现就是预览和建造区显示成实心色块。
    ///
    /// 所以这里按 URP 的约定把状态一次设全。两处调用同一个实现，
    /// 避免以后再出现"改了一处忘了另一处"。
    /// </summary>
    public static class BuildMaterialUtility
    {
        /// <summary>URP Unlit 的透明变体关键字。</summary>
        private static readonly string[] TransparentKeywords = { "_SURFACE_TYPE_TRANSPARENT" };

        /// <summary>URP Unlit 的不透明变体关键字（创建后要显式关掉）。</summary>
        private static readonly string[] OpaqueKeywords = { "_SURFACE_TYPE_OPAQUE" };

        /// <summary>
        /// 创建一个可用的半透明无光材质。
        ///
        /// 优先 URP/Unlit（能用顶点色与 _BaseColor），
        /// 退回 Sprites/Default（它在所有管线下都能画半透明，但颜色 API 不同）。
        /// 都拿不到时返回 null —— 调用方会退化成不透明，但不会崩。
        /// </summary>
        public static Material CreateTransparent(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            bool isUrpUnlit = shader != null;

            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            if (shader == null)
            {
                Debug.LogWarning(
                    $"[建造] 找不到可用的半透明 shader（试过 URP/Unlit、Sprites/Default、Unlit/Color），" +
                    $"「{name}」会显示成不透明。");
                return null;
            }

            Material material = new Material(shader) { name = name };

            if (isUrpUnlit) ApplyUrpTransparentState(material);

            // 两种 shader 的颜色属性名不同，能设就设
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);

            material.renderQueue = (int)RenderQueue.Transparent;
            return material;
        }

        /// <summary>
        /// 把 URP 材质的透明状态设全。
        ///
        /// 【为什么这些值是这个】和 URP 内置的 "Unlit > Transparent" 材质一致：
        ///   混合 = SrcAlpha / OneMinusSrcAlpha（标准 alpha 混合）
        ///   深度写入 = 关（否则透明物体会把后面的东西挡掉）
        ///   渲染队列 = Transparent
        /// 关键字也要显式切换，否则 shader 仍然走不透明分支。
        /// </summary>
        public static void ApplyUrpTransparentState(Material material)
        {
            if (material == null) return;

            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);       // 0 = Alpha

            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_AlphaPremultiplyOn")) material.SetFloat("_AlphaPremultiplyOn", 0f);

            // 关键字：透明开启、不透明关闭。
            // 不关掉 _SURFACE_TYPE_OPAQUE 的话，某些 URP 版本会同时命中两个分支。
            foreach (string keyword in TransparentKeywords) material.EnableKeyword(keyword);
            foreach (string keyword in OpaqueKeywords) material.DisableKeyword(keyword);

            // 有些 URP 版本用 _ALPHATEST_ON / _ALPHAPREMULTIPLY_ON 之类的附加关键字，
            // 这里顺手确保不冲突（材质新建时是干净的，主要是防止以后被别的代码污染）。
            material.DisableKeyword("_ALPHATEST_ON");

            material.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>改一个半透明材质的颜色（同时兼容两种 shader 的属性名）。</summary>
        public static void SetColor(Material material, Color color)
        {
            if (material == null) return;

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }
    }
}
