using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// 水体贴图的程序化生成（运行时程序集，编辑器用）。
    ///
    /// 【为什么放在 Game.Runtime 而不是 Editor】水体材质是在场景生成器里创建的，
    /// 生成材质时希望顺便把贴图也生成好，不然用户必须记得再跑一次菜单。
    /// 但 Game.Runtime 不能引用 Game.Editor 程序集，所以生成逻辑放在这一侧，
    /// 由编辑器工具（WaterTextureTools）负责设置导入类型。
    ///
    /// 【生成的只是占位图】目的是"不找美术资源也能立刻看到完整效果"。
    /// 换成自己的图：直接在水体材质的 Inspector 里替换即可，shader 不用改。
    /// </summary>
    public static class WaterTextureGenerator
    {
        public const int TextureSize = 512;

        /// <summary>生成一张法线贴图（切线空间，编码为 RGB）。</summary>
        public static Texture2D CreateNormalMap(int size = TextureSize)
        {
            Texture2D texture = NewTexture(size);
            const float strength = 2.4f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;
                    float v = (float)y / size;

                    // 相邻采样求梯度 → 得到微起伏的法线
                    float h = Noise(u, v, 7f);
                    float hx = Noise(u + 1f / size, v, 7f);
                    float hy = Noise(u, v + 1f / size, 7f);

                    Vector3 normal = new Vector3(-(hx - h) * strength, -(hy - h) * strength, 1f).normalized;

                    texture.SetPixel(x, y, new Color(
                        normal.x * 0.5f + 0.5f,
                        normal.y * 0.5f + 0.5f,
                        normal.z * 0.5f + 0.5f,
                        1f));
                }
            }

            texture.Apply();
            return texture;
        }

        /// <summary>泡沫 / 岸边消融噪声（用 R 通道）。</summary>
        public static Texture2D CreateFoamNoise(int size = TextureSize)
        {
            Texture2D texture = NewTexture(size);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float value = Mathf.Clamp01(Noise((float)x / size, (float)y / size, 5f));

                    // 拉高对比：让"啃咬"的边界更明显
                    value = Mathf.Pow(value, 1.35f);

                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            texture.Apply();
            return texture;
        }

        /// <summary>焦散：两层噪声求交集，形成网状波光。</summary>
        public static Texture2D CreateCaustics(int size = TextureSize)
        {
            Texture2D texture = NewTexture(size);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;
                    float v = (float)y / size;

                    float n1 = Noise(u, v, 6f);
                    float n2 = Noise(u * 1.31f + 0.37f, v * 1.31f + 0.61f, 6f);

                    // abs 差越小越接近"两层都亮" → 取幂后变成细线，就是焦散网
                    float caustic = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(n1 - n2) * 3f), 3f);

                    texture.SetPixel(x, y, new Color(caustic, caustic, caustic, 1f));
                }
            }

            texture.Apply();
            return texture;
        }

        /// <summary>碎斑高光：稀疏的高频噪点。</summary>
        public static Texture2D CreateGlint(int size = TextureSize)
        {
            Texture2D texture = NewTexture(size);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float value = Noise((float)x / size, (float)y / size, 24f);

                    // 只留最亮的一小部分 → 稀疏闪点
                    value = Mathf.Clamp01((value - 0.55f) * 3.2f);
                    value = Mathf.Pow(value, 1.4f);

                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            texture.Apply();
            return texture;
        }

        private static Texture2D NewTexture(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, true);
        }

        // ------------------------------------------------------------------ 噪声

        private static float Noise(float u, float v, float frequency)
        {
            float sum = 0f;
            float amplitude = 0.5f;
            float total = 0f;

            for (int octave = 0; octave < 5; octave++)
            {
                sum += ValueNoise(u * frequency, v * frequency) * amplitude;
                total += amplitude;
                frequency *= 2.03f;
                amplitude *= 0.5f;
            }

            return total > 0f ? sum / total : 0f;
        }

        private static float ValueNoise(float x, float y)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);

            float xf = x - xi;
            float yf = y - yi;

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
    }
}
