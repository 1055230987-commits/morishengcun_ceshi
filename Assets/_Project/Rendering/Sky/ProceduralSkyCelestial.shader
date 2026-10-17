// 写实散射天空 + 太阳/月亮圆盘
//
// 设计思路：
//   底色仍然用 Unity 的 Procedural 天空（大气散射），本 shader 只负责"往上叠天体"，
//   所以观感和原来的 M_NightSky 一致，不会出现"卡通天 + 写实地"的割裂。
//
// 三个关键实现技巧（借鉴自 Kelvin Van Hoorn 的 skybox 教程与 Inigo Quilez 的球体求交思路）：
//   1. 天空盒里每个像素的【视线方向】= normalize(世界坐标 - 相机位置)，直接用它算天体角度，
//      不需要访问主光源 API，跨 URP 版本更稳、也更快。
//   2. 用 distance(viewDir, celestialDir) 配合 smoothstep 画圆盘，边缘天然柔滑，无需贴图。
//   3. 太阳颜色用 exp(-k*x*x) 高斯衰减做一个软光晕，比硬边更有"曝光过度"的真实感。
//
// 随机亮星：用带旋转的 value noise 哈希，不需要任何贴图。

Shader "Game/ProceduralSkyCelestial"
{
    Properties
    {
        // ---------- 大气（与 Skybox/Procedural 同名同义，方便和普通天空盒互换） ----------
        [MainColor] _SkyTint("天空色调", Color) = (0.62, 0.76, 1.0, 1)
        _EquatorColor("地平线色调", Color) = (0.56, 0.68, 0.92, 1)
        _GroundColor("地面色", Color) = (0.16, 0.15, 0.14, 1)
        _AtmosphereThickness("大气厚度", Range(0.2, 5)) = 1.0
        _Exposure("曝光", Range(0.2, 5)) = 1.35

        // ---------- 昼夜混色 ----------
        _NightAmount("夜晚权重（控制器统一驱动）", Range(0, 1)) = 0
        _DaySkyColor("白天天空色", Color) = (0.62, 0.76, 1.0, 1)
        _NightSkyColor("夜晚天空色", Color) = (0.05, 0.06, 0.11, 1)
        _DayHorizonColor("白天水平线色", Color) = (0.66, 0.72, 0.82, 1)
        _NightHorizonColor("夜晚水平线色", Color) = (0.04, 0.05, 0.09, 1)
        _SunsetHorizonColor("朝霞晚霞色", Color) = (0.95, 0.45, 0.22, 1)

        // ---------- 太阳 ----------
        _SunDirection("太阳方向", Vector) = (0.3, 0.5, 0.8, 0)
        _SunColor("太阳颜色", Color) = (1, 0.96, 0.88, 1)
        [HDR] _SunHaloColor("太阳光晕颜色", Color) = (1.6, 1.2, 0.75, 1)
        _SunRadius("太阳大小", Range(0.001, 0.2)) = 0.012
        _SunSoftness("太阳边缘柔化", Range(0.001, 0.2)) = 0.02
        _SunHaloFalloff("太阳光晕范围", Range(0.05, 3)) = 0.6
        _SunHaloStrength("太阳光晕强度", Range(0, 3)) = 0.5

        // ---------- 月亮 ----------
        _MoonDirection("月亮方向", Vector) = (0, 0.5, -1, 0)
        _MoonColor("月亮颜色", Color) = (0.92, 0.94, 1.0, 1)
        _MoonRadius("月亮大小", Range(0.001, 0.2)) = 0.02
        _MoonSoftness("月亮边缘柔化", Range(0.001, 0.2)) = 0.012
        [HDR] _MoonGlowColor("月亮光晕颜色", Color) = (0.45, 0.55, 0.8, 1)
        _MoonGlowStrength("月亮光晕强度", Range(0, 2)) = 0.35
        _MoonVisibility("月亮可见度", Range(0, 1)) = 0

        // ---------- 星空 ----------
        _StarDensity("星星密度", Range(0, 1)) = 0.9965
        _StarBrightness("星星亮度", Range(0, 4)) = 0.55
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Background"
            "RenderPipeline" = "UniversalPipeline"
            "PreviewType" = "Skybox"
            "Queue" = "Background"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _SkyTint;
                float4 _EquatorColor;
                float4 _GroundColor;
                float _AtmosphereThickness;
                float _Exposure;
                float _NightAmount;

                float4 _DaySkyColor;
                float4 _NightSkyColor;
                float4 _DayHorizonColor;
                float4 _NightHorizonColor;
                float4 _SunsetHorizonColor;

                float4 _SunDirection;
                float4 _SunColor;
                float4 _SunHaloColor;
                float _SunRadius;
                float _SunSoftness;
                float _SunHaloFalloff;
                float _SunHaloStrength;

                float4 _MoonDirection;
                float4 _MoonColor;
                float _MoonRadius;
                float _MoonSoftness;
                float4 _MoonGlowColor;
                float _MoonGlowStrength;
                float _MoonVisibility;

                float _StarDensity;
                float _StarBrightness;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // 关键技巧：天空盒顶点直接当方向用。
                // Unity 提交给天空盒的网格本来就是一个"以相机为中心的单位球"，
                // 所以 object space 的顶点位置就是方向向量，不需要再做矩阵变换。
                output.positionWS = input.positionOS.xyz;
                return output;
            }

            float3 Atmosphere(float3 dir)
            {
                float up = saturate(dir.y);

                // 简易瑞利近似：地平线偏厚偏亮、天顶偏薄偏蓝。
                float thickness = max(_AtmosphereThickness, 0.05);
                float horizonBand = exp(-up * (3.0 / thickness));
                float vertical = pow(up, 1.0 / thickness);

                float3 zenith = _DaySkyColor.rgb;
                float3 horizon = _DayHorizonColor.rgb;

                float3 color = lerp(horizon, zenith, vertical);
                color += horizon * horizonBand * 0.35;

                // 地平线以下过渡到地面色。
                float below = saturate(-dir.y * 4.0);
                color = lerp(color, _GroundColor.rgb, below);

                return color * _Exposure;
            }

            // 3D 方向场哈希：直接在方向上取噪波，没有任何 UV 接缝问题。
            // （早先用的是"方向映射成格子再查哈希"，那种做法在球面经度接缝处会裂开一条异常带，
            //   因为 floor() 在接缝两侧不连续。）
            float DirHash(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float StarField(float3 dir)
            {
                // _StarDensity 越高，星星越多（0 = 几乎没有，1 = 最密）。
                float threshold = lerp(0.9995, 0.9950, saturate(_StarDensity));

                // 三层不同密度，让星星有大有小、有稀有密。每层只花 1 次哈希。
                float stars = 0.0;

                float h1 = DirHash(dir * 180.0);
                stars += smoothstep(threshold, 1.0000, h1);

                float h2 = DirHash(dir * 310.0);
                stars += smoothstep(min(threshold + 0.0015, 0.9999), 1.0000, h2) * 1.4;

                float h3 = DirHash(dir * 520.0);
                stars += smoothstep(min(threshold + 0.0028, 0.9999), 1.0000, h3) * 2.0;

                return stars;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.positionWS);
                float3 sunDir = normalize(_SunDirection.xyz);
                float3 moonDir = normalize(_MoonDirection.xyz);

                float sunHeight = sunDir.y;
                float night = saturate(_NightAmount);

                // 地平线附近更暖（朝霞/晚霞），用太阳高度控制强度。
                float horizonTint = 1.0 - smoothstep(0.0, 0.35, abs(sunHeight));

                // ---------- 天空底色 ----------
                float3 daySky = Atmosphere(dir);
                daySky = lerp(daySky, _SunsetHorizonColor.rgb * _Exposure, horizonTint * saturate(1.0 - night) * 0.45);

                float3 nightSky = lerp(_NightHorizonColor.rgb, _NightSkyColor.rgb, saturate(dir.y));
                nightSky = lerp(nightSky, _GroundColor.rgb * 0.1, saturate(-dir.y * 4.0)) * _Exposure;

                float3 color = lerp(daySky, nightSky, night);

                // ---------- 星空（只在夜里出现） ----------
                float stars = 0.0;
                if (night > 0.001)
                    stars = StarField(dir) * _StarBrightness * night * smoothstep(0.0, 0.08, dir.y);
                color += stars * float3(0.9, 0.93, 1.0);

                // ---------- 太阳 ----------
                float sunDist = distance(dir, sunDir);
                float sunDisc = 1.0 - smoothstep(_SunRadius, _SunRadius + _SunSoftness, sunDist);

                // 高斯软光晕：exp(-(x/k)^2)，比硬边更接近肉眼/相机过曝的观感。
                float haloX = sunDist / max(_SunHaloFalloff, 0.001);
                float sunHalo = exp(-haloX * haloX) * _SunHaloStrength;

                float sunVisible = (1.0 - night) * smoothstep(-0.025, 0.02, sunDir.y) * smoothstep(-0.02, 0.01, dir.y);
                color += (_SunColor.rgb * sunDisc + _SunHaloColor.rgb * sunHalo) * sunVisible;

                // ---------- 月亮 ----------
                float moonDist = distance(dir, moonDir);
                float moonDisc = 1.0 - smoothstep(_MoonRadius, _MoonRadius + _MoonSoftness, moonDist);

                float moonHaloX = moonDist / max(_MoonRadius * 6.0, 0.001);
                float moonHalo = exp(-moonHaloX * moonHaloX) * _MoonGlowStrength;

                float moonVisible = saturate(_MoonVisibility) * smoothstep(-0.025, 0.02, moonDir.y) * smoothstep(-0.02, 0.01, dir.y);
                color += (_MoonColor.rgb * moonDisc + _MoonGlowColor.rgb * moonHalo) * moonVisible;

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
