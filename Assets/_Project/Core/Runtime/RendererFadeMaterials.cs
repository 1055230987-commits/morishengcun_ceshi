using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>只拥有自己创建的透明材质；恢复时重新挂回原材质，保留全部渲染状态。</summary>
    public sealed class RendererFadeMaterials : IDisposable
    {
        private sealed class Entry
        {
            public Renderer Renderer;
            public Material[] Originals;
            public Material[] Faded;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private bool _applied;
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public RendererFadeMaterials(Renderer[] renderers)
        {
            var seen = new HashSet<Renderer>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !seen.Add(renderer)) continue;
                Material[] originals = renderer.sharedMaterials;
                var faded = new Material[originals.Length];
                for (int i = 0; i < originals.Length; i++)
                {
                    if (originals[i] == null) continue;
                    Material material = new Material(originals[i])
                    {
                        name = originals[i].name + " (Fade)",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    SetFloat(material, "_Surface", 1f);
                    SetFloat(material, "_Blend", 0f);
                    SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
                    SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    SetFloat(material, "_SrcBlendAlpha", (float)BlendMode.One);
                    SetFloat(material, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                    SetFloat(material, "_ZWrite", 0f);
                    material.renderQueue = (int)RenderQueue.Transparent;
                    material.DisableKeyword("_ALPHATEST_ON");
                    material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    faded[i] = material;
                }
                _entries.Add(new Entry { Renderer = renderer, Originals = originals, Faded = faded });
            }
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        public void Apply(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            foreach (Entry entry in _entries)
            {
                if (entry.Renderer == null) continue;
                if (!_applied) entry.Renderer.sharedMaterials = entry.Faded;
                for (int i = 0; i < entry.Faded.Length; i++)
                {
                    Material material = entry.Faded[i];
                    Material original = entry.Originals[i];
                    if (material == null || original == null) continue;
                    ApplyColor(material, original, BaseColorId, alpha);
                    ApplyColor(material, original, ColorId, alpha);
                }
            }
            _applied = true;
        }

        private static void ApplyColor(Material material, Material original, int property, float alpha)
        {
            if (!original.HasProperty(property)) return;
            Color color = original.GetColor(property);
            color.a *= alpha;
            material.SetColor(property, color);
        }

        public void Restore()
        {
            if (!_applied) return;
            foreach (Entry entry in _entries)
                if (entry.Renderer != null) entry.Renderer.sharedMaterials = entry.Originals;
            _applied = false;
        }

        public void Dispose()
        {
            Restore();
            foreach (Entry entry in _entries)
            {
                foreach (Material material in entry.Faded)
                {
                    if (material == null) continue;
                    if (Application.isPlaying) UnityEngine.Object.Destroy(material);
                    else UnityEngine.Object.DestroyImmediate(material);
                }
            }
            _entries.Clear();
        }
    }
}
