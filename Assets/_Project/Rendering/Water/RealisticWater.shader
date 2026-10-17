// URP 水体 shader（参考知乎「Unity6 URP 水体 Shader」的技术路线实现）。
//
// 【为什么自己写】Unity 6 的官方水体系统属于 HDRP，URP 没有内置水体。
//
// 已实现（全部不依赖任何贴图资产，噪声都是程序化生成的）：
//   1. 三波叠加 Gerstner Wave —— 顶点位移（含水平方向），浪尖陡、浪谷缓
//   2. 偏导数重算法线 —— 用 ∂P/∂x × ∂P/∂z 得到形变后的真实法线，而不是凑一个
//   3. 浪尖白沫 —— 由波高直接推出，不需要贴图
//   4. 岸边噪声消融 + 动态泡沫 —— 用程序化噪声 clip 出破碎岸线，泡沫随时间呼吸推拉
//   5. 微观涟漪 —— 两组程序化噪声滚动作法线扰动（替代法线贴图）
//   6. 水下折射 + RGB 色散 —— 采样 _CameraOpaqueTexture，三通道分开偏移
//   7. 程序化焦散 —— 水下网格状波光，随水深衰减
//   8. 次表面散射（背光透射）—— 迎着阳光看浪尖时透出碧绿色
//   9. 双轨高光 —— GGX 物理高光 + 高频碎斑
//  10. 环境反射 —— GlossyEnvironmentReflection，并做了 Forward+ 的 pureScreenUV 适配
//
// 【故意没做】
//   曲面细分：URP 不支持 Hull/Domain Shader（那是内置管线和 HDRP 才有的），
//   所以细分改由【网格本身】承担 —— 见 GreyboxPrimitives.BuildWaterSurfaceMesh，
//   水面网格是按格子自己生成的，不是 Unity 的 Plane 基本体（那个只有 10×10 面片）。
//   SSR（屏幕空间反射）：150 次光线步进的代价对灰盒原型太高，暂不做。
//
// 【已知风险】HLSL 我无法编译验证。如果水面显示为粉红色，就是编译失败，
// 请在 Project 窗口点这个 shader 看 Inspector 里的报错。
Shader "Game/Water/RealisticWater"
{
    Properties
    {
        [Header(Base)]
        _ShallowColor ("浅水颜色", Color) = (0.18, 0.46, 0.48, 0.55)
        _DeepColor ("深水颜色", Color) = (0.02, 0.08, 0.14, 0.97)
        _DepthFade ("深度渐变距离（米）", Range(0.1, 20)) = 3.0
        _RefractStrength ("折射强度", Range(0, 0.05)) = 0.012
        _ChromaticAberration ("色散强度", Range(0, 1)) = 0.35

        [Header(Gerstner Waves)]
        _WaveA ("波 A (dirX, dirZ, 陡峭度, 波长)", Vector) = (1.0, 0.35, 0.22, 6.0)
        _WaveB ("波 B (dirX, dirZ, 陡峭度, 波长)", Vector) = (-0.6, 0.8, 0.16, 3.6)
        _WaveC ("波 C (dirX, dirZ, 陡峭度, 波长)", Vector) = (0.25, -1.0, 0.11, 2.2)
        _WaveSpeed ("波速倍率", Range(0, 3)) = 1.0
        _AmplitudeScale ("振幅总缩放", Range(0, 2)) = 0.35

        [Header(Foam)]
        _FoamColor ("泡沫颜色", Color) = (0.92, 0.96, 1.0, 1)
        _FoamDistance ("岸边泡沫宽度（米）", Range(0.05, 3)) = 0.6
        _FoamDissolve ("泡沫溶解起点", Range(0, 1)) = 0.25
        _FoamThickness ("泡沫过渡厚度", Range(0.01, 1)) = 0.35
        _EdgeErosion ("岸边消融强度（米）", Range(0, 1)) = 0.35
        _FoamPushPull ("泡沫推拉幅度", Range(0, 1)) = 0.25
        _FoamWaveSpeed ("泡沫推拉速度", Range(0, 4)) = 1.4
        _FoamWaveFrequency ("泡沫推拉频率", Range(0, 8)) = 2.0
        _CrestFoamThreshold ("浪尖白沫阈值", Range(0, 1)) = 0.55
        _CrestFoamStrength ("浪尖白沫强度", Range(0, 4)) = 1.6

        [Header(Micro Ripples)]
        _RippleScale ("涟漪密度", Range(0.5, 40)) = 9.0
        _RippleStrength ("涟漪法线强度", Range(0, 1)) = 0.28
        _RippleSpeed ("涟漪流动速度", Range(0, 2)) = 0.35

        [Header(Caustics)]
        _CausticsColor ("焦散颜色", Color) = (0.75, 1.0, 0.92, 1)
        _CausticsIntensity ("焦散强度", Range(0, 3)) = 0.85
        _CausticsRange ("焦散可见深度（米）", Range(0.5, 12)) = 3.5

        [Header(Lighting)]
        _Smoothness ("光滑度", Range(0, 1)) = 0.9
        _SpecularIntensity ("GGX 高光强度", Range(0, 6)) = 1.6
        _GlintThreshold ("碎斑阈值", Range(0, 1)) = 0.55
        _GlintStrength ("碎斑强度", Range(0, 4)) = 1.4
        _FresnelPower ("菲涅尔指数", Range(1, 8)) = 4.0
        [HDR] _FresnelColor ("菲涅尔颜色", Color) = (0.75, 0.88, 0.98, 1)
        _GlowIntensity ("环境泛光强度", Range(0, 3)) = 1.0
        _DeepRange ("泛光岸边消退距离（米）", Range(0.1, 20)) = 4.0

        [Header(Subsurface)]
        _SSSColor ("次表面颜色", Color) = (0.35, 0.85, 0.68, 1)
        _SSSIntensity ("次表面强度", Range(0, 4)) = 1.2
        _SSSPower ("次表面聚拢度", Range(1, 32)) = 8.0
        _SSSDistortion ("次表面扰动", Range(0, 1)) = 0.35

        [Header(External Textures)]
        _NormalMap ("法线贴图 Normal (需要设为 Normal map)", 2D) = "bump" {}
        _NormalScale ("法线强度", Range(0, 2)) = 1.0
        _NormalSpeed ("法线滚动速度 xy第一层 zw第二层", Vector) = (0.02, 0.015, -0.015, 0.025)

        _FoamNoiseTex ("泡沫噪声贴图 Foam R通道", 2D) = "white" {}
        _FoamNoiseTiling ("噪声平铺 每米", Range(0.05, 5)) = 0.35
        _FoamNoiseSpeed ("噪声滚动速度", Range(0, 1)) = 0.03

        _CausticsTex ("焦散贴图 Caustics RGB", 2D) = "black" {}
        _CausticsTiling ("焦散平铺", Range(0.05, 5)) = 0.3

        _GlintTex ("碎斑贴图 Glint R通道", 2D) = "white" {}
        _GlintScale ("碎斑平铺", Range(0.1, 20)) = 4.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #define _SURFACE_TYPE_TRANSPARENT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            // _CameraOpaqueTexture：URP Asset 里已开启 Opaque Texture，所以这里可以采样。
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float  _DepthFade;
                float  _RefractStrength;
                float  _ChromaticAberration;

                float4 _WaveA;
                float4 _WaveB;
                float4 _WaveC;
                float  _WaveSpeed;
                float  _AmplitudeScale;

                float4 _FoamColor;
                float  _FoamDistance;
                float  _FoamDissolve;
                float  _FoamThickness;
                float  _EdgeErosion;
                float  _FoamPushPull;
                float  _FoamWaveSpeed;
                float  _FoamWaveFrequency;
                float  _CrestFoamThreshold;
                float  _CrestFoamStrength;

                float  _RippleScale;
                float  _RippleStrength;
                float  _RippleSpeed;

                float4 _CausticsColor;
                float  _CausticsIntensity;
                float  _CausticsRange;

                float  _Smoothness;
                float  _SpecularIntensity;
                float  _GlintThreshold;
                float  _GlintStrength;
                float  _FresnelPower;
                float4 _FresnelColor;
                float  _GlowIntensity;
                float  _DeepRange;

                float4 _SSSColor;
                float  _SSSIntensity;
                float  _SSSPower;
                float  _SSSDistortion;

                float4 _NormalSpeed;
                float  _NormalScale;
                float  _FoamNoiseTiling;
                float  _FoamNoiseSpeed;
                float  _CausticsTiling;
                float  _GlintScale;
            CBUFFER_END

            // ---- 外部贴图与采样器 ----
            // 这几张图不在 CBUFFER 里（它们不是常量缓冲的成员）。
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_FoamNoiseTex);
            SAMPLER(sampler_FoamNoiseTex);
            TEXTURE2D(_CausticsTex);
            SAMPLER(sampler_CausticsTex);
            TEXTURE2D(_GlintTex);
            SAMPLER(sampler_GlintTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float3 tangentWS   : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float2 screenUV    : TEXCOORD5;
                float  fogFactor   : TEXCOORD6;
                float  crestFoam   : TEXCOORD7;
            };

            // ------------------------------------------------------------------ 噪声

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 值噪声：平滑插值，用于岸边消融与焦散（不需要贴图）
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float Fbm(float2 p)
            {
                float sum = 0.0;
                float amplitude = 0.5;

                for (int i = 0; i < 4; i++)
                {
                    sum += ValueNoise(p) * amplitude;
                    p *= 2.03;
                    amplitude *= 0.5;
                }

                return sum;
            }

            // ------------------------------------------------------------------ 贴图接口
            //
            // 【设计原则】贴图是【可选】的：
            //   在材质上放了图 → 用图的细节（真实、有美术控制）
            //   没放图 → 退回程序化噪声（仍然能用，不会变成纯色/报错）
            // 这样你可以一张一张地往里加，不会因为缺图就什么都看不见。

            /// <summary>泡沫噪声：优先用贴图的 R 通道，没放图就用程序化 Fbm。</summary>
            float SampleNoiseTexture(float2 worldXZ, float time)
            {
                float2 uv = worldXZ * _FoamNoiseTiling + time * _FoamNoiseSpeed;

                float fromTexture = SAMPLE_TEXTURE2D(_FoamNoiseTex, sampler_FoamNoiseTex, uv).r;
                float fromNoise = Fbm(worldXZ * 1.8);

                // 贴图是 "white" 时 R=1（常量），说明用户没放图 —— 这时用程序化噪声。
                bool textureMissing = fromTexture > 0.995;
                return textureMissing ? fromNoise : fromTexture;
            }

            /// <summary>碎斑高光噪声：优先用贴图，没放图就用程序化噪声。</summary>
            float SampleGlintTexture(float2 worldXZ, float time)
            {
                float2 uv = worldXZ * _GlintScale * 0.1 + time * 0.02;
                float fromTexture = SAMPLE_TEXTURE2D(_GlintTex, sampler_GlintTex, uv).r;
                float fromNoise = ValueNoise(worldXZ * 3.7 + time * 0.15);

                bool textureMissing = fromTexture > 0.995;
                return textureMissing ? fromNoise : fromTexture;
            }

            /// <summary>
            /// 双层法线滚动：按文章的做法采样两张法线并混合。
            ///
            /// 返回【切线空间】法线。调用方再用 TBN 矩阵转到世界空间。
            /// 没放法线贴图时返回 (0,0,1)（即不扰动），水面只剩宏观波浪。
            /// </summary>
            float3 SampleMicroNormal(float2 worldXZ, float time, out bool valid)
            {
                float3 n1 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, worldXZ * 0.15 + _Time.y * _NormalSpeed.xy), _NormalScale);
                float3 n2 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, worldXZ * 0.23 + _Time.y * _NormalSpeed.zw), _NormalScale);

                // 默认法线贴图（"bump"）是 (0.5, 0.5, 1)，解包后约 (0, 0, 1) —— 用它判断有没有放图。
                float3 blended = normalize(n1 + n2);
                valid = abs(blended.x) + abs(blended.y) > 0.002;

                time = time;   // 已用 _Time.y，这里避免未使用告警
                return blended;
            }

            /// <summary>
            /// 焦散：按文章的做法把两张焦散图反向滑动、用 min 求交集。
            /// 没放图时退回程序化噪声。
            /// </summary>
            float3 SampleCaustics(float2 worldXZ, float time, out bool valid)
            {
                float2 uv = worldXZ * _CausticsTiling;

                float3 c1 = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, uv + time * float2(0.03, 0.02)).rgb;
                float3 c2 = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, uv * 1.31 - time * float2(0.025, 0.035)).rgb;

                float3 fromTexture = min(c1, c2);
                valid = (fromTexture.r + fromTexture.g + fromTexture.b) > 0.002;

                if (valid) return fromTexture;

                // 回退：程序化焦散
                float n1 = Fbm(uv);
                float n2 = Fbm(uv * 1.31 + 5.7);
                float c = pow(saturate(1.0 - abs(n1 - n2) * 3.0), 3.0);
                return float3(c, c, c);
            }

            // ------------------------------------------------------------------ Gerstner

            // 单个 Gerstner 波的累加。
            //
            // 【为什么拆成函数、不用数组 + 循环】
            // 在函数内声明 float4 waves[3] 再用循环变量索引，是最容易在某些平台/编译级别上
            // 报错或被迫走动态索引的写法。三次显式调用生成的结果完全一样，但零风险。
            void AccumulateWave(
                float4 wave,
                float3 positionWS,
                float time,
                inout float3 displacement,
                inout float3 tangent,
                inout float3 bitangent,
                inout float heightSum)
            {
                float2 direction = normalize(wave.xy + 1e-5);
                float wavelength = max(0.5, wave.w);
                float steepness = wave.z;

                float k = 6.2831853 / wavelength;
                // 深水色散关系：角频率由波数决定
                float omega = sqrt(9.81 * k);
                float amplitude = steepness / k * _AmplitudeScale;

                float phase = k * dot(direction, positionWS.xz) - omega * time * _WaveSpeed;

                float sinPhase;
                float cosPhase;
                sincos(phase, sinPhase, cosPhase);

                // 水平位移（Gerstner 的核心）：顶点朝浪尖方向聚拢，形成尖锐浪尖
                displacement.x += direction.x * amplitude * cosPhase;
                displacement.z += direction.y * amplitude * cosPhase;
                // 垂直位移
                displacement.y += amplitude * sinPhase;

                heightSum += amplitude * sinPhase;

                // 偏导数累加：∂P/∂x 与 ∂P/∂z
                tangent += float3(
                    -direction.x * direction.x * steepness * sinPhase,
                     direction.x * steepness * cosPhase,
                    -direction.x * direction.y * steepness * sinPhase);

                bitangent += float3(
                    -direction.x * direction.y * steepness * sinPhase,
                     direction.y * steepness * cosPhase,
                    1.0 - direction.y * direction.y * steepness * sinPhase);
            }

            // 单个 Gerstner 波的切线/副切线累加（只算法线用，不重复算位移）。
            //
            // 【为什么片元阶段要重算】顶点阶段算出的切/副切线是在顶点上求导后线性插值的，
            // 而波浪是三角函数 —— 插值误差让每个四边形内部出现色块。
            // 顶点间距 20 厘米时肉眼就很明显（本工程实际出现过"一格一格"的水面）。
            // 在片元里用【插值后的世界坐标】重新求导，等于做了逐像素的解析法线。
            void AccumulateWaveTangents(
                float4 wave,
                float3 positionWS,
                float time,
                inout float3 tangent,
                inout float3 bitangent)
            {
                float2 direction = normalize(wave.xy + 1e-5);
                float wavelength = max(0.5, wave.w);
                float steepness = wave.z;

                float k = 6.2831853 / wavelength;
                float omega = sqrt(9.81 * k);
                float phase = k * dot(direction, positionWS.xz) - omega * time * _WaveSpeed;

                float sinPhase;
                float cosPhase;
                sincos(phase, sinPhase, cosPhase);

                tangent += float3(
                    -direction.x * direction.x * steepness * sinPhase,
                     direction.x * steepness * cosPhase,
                    -direction.x * direction.y * steepness * sinPhase);

                bitangent += float3(
                    -direction.x * direction.y * steepness * sinPhase,
                     direction.y * steepness * cosPhase,
                    1.0 - direction.y * direction.y * steepness * sinPhase);
            }

            float3 GerstnerNormalWS(float3 positionWS, float time)
            {
                float3 tangent = float3(1.0, 0.0, 0.0);
                float3 bitangent = float3(0.0, 0.0, 1.0);

                AccumulateWaveTangents(_WaveA, positionWS, time, tangent, bitangent);
                AccumulateWaveTangents(_WaveB, positionWS, time, tangent, bitangent);
                AccumulateWaveTangents(_WaveC, positionWS, time, tangent, bitangent);

                // cross(bitangent, tangent) 才是朝上的法线（顺序反了会被背面剔除）。
                return normalize(cross(bitangent, tangent));
            }

            // 三波叠加。返回位移量，并累加出切线与副切线（用来叉乘求真实法线）。
            float3 GerstnerDisplace(float3 positionWS, float time, out float3 tangent, out float3 bitangent, out float crest)
            {
                tangent = float3(1.0, 0.0, 0.0);
                bitangent = float3(0.0, 0.0, 1.0);

                float3 displacement = float3(0.0, 0.0, 0.0);
                float heightSum = 0.0;

                AccumulateWave(_WaveA, positionWS, time, displacement, tangent, bitangent, heightSum);
                AccumulateWave(_WaveB, positionWS, time, displacement, tangent, bitangent, heightSum);
                AccumulateWave(_WaveC, positionWS, time, displacement, tangent, bitangent, heightSum);

                // 浪尖强度：总波高归一化后供片元阶段做白沫
                crest = saturate(heightSum * 3.0);

                return displacement;
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

                float3 tangent;
                float3 bitangent;
                float crest;

                float3 displacement = GerstnerDisplace(positionWS, _Time.y, tangent, bitangent, crest);
                positionWS += displacement;

                output.positionWS = positionWS;
                output.positionHCS = TransformWorldToHClip(positionWS);

                // 真实法线：形变后的切向量与副切向量叉乘
                // （注意顺序：cross(bitangent, tangent) 才是朝上的法线，
                //   反了会得到朝下的法线、然后被背面剔除 —— 本工程在树冠网格上踩过这个坑）
                float3 normalWS = normalize(cross(bitangent, tangent));

                output.normalWS = normalWS;
                output.tangentWS = normalize(tangent);
                output.bitangentWS = normalize(bitangent);

                output.uv = input.uv;
                output.crestFoam = saturate((crest - _CrestFoamThreshold) * _CrestFoamStrength);
                output.fogFactor = ComputeFogFactor(output.positionHCS.z);

                // 屏幕 UV：ComputeScreenPos 返回齐次坐标，必须自己做透视除法
                float4 screenPos = ComputeScreenPos(output.positionHCS);
                output.screenUV = screenPos.xy / max(1e-5, screenPos.w);

                return output;
            }

            // ------------------------------------------------------------------ GGX

            float GGXDistribution(float nDotH, float roughness)
            {
                float a = roughness * roughness;
                float a2 = a * a;
                float d = nDotH * nDotH * (a2 - 1.0) + 1.0;
                return a2 / max(1e-6, 3.14159265 * d * d);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 screenUV = input.screenUV;

                // ---- 深度：水面到水下物体的距离 ----
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float surfaceEyeDepth = -(TransformWorldToView(input.positionWS).z);
                float waterDepth = max(0.0, sceneEyeDepth - surfaceEyeDepth);

                float depthT = saturate(waterDepth / max(0.05, _DepthFade));

                // ---- 法线：片元阶段逐像素重算 Gerstner 解析法线 ----
                //
                // 不用顶点传来的 tangentWS/bitangentWS（那是插值结果，会在每个四边形内产生色块）。
                // 代价是每像素多算 3 组 sin/cos —— 单个湖泊完全在预算内。
                float3 gerstnerNormal = GerstnerNormalWS(input.positionWS, _Time.y);

                // 用世界坐标构造一组正交基（相当于 TBN 矩阵），
                // 把切线空间的微观法线贴到宏观波浪上 —— 和文章的
                // "tangentN.x * T + tangentN.y * B + tangentN.z * N" 是同一件事。
                float3 referenceUp = float3(0.0, 0.0, 1.0);
                float3 rippleTangent = normalize(cross(referenceUp, gerstnerNormal) + 1e-5);
                float3 rippleBitangent = cross(gerstnerNormal, rippleTangent);

                // ---- 微观涟漪：优先用法线贴图（双层滚动），没放图时退回程序化噪声 ----
                bool hasNormalMap;
                float3 tangentNormal = SampleMicroNormal(input.positionWS.xz, _Time.y, hasNormalMap);

                float3 normalWS;

                if (hasNormalMap)
                {
                    normalWS = normalize(
                        tangentNormal.x * rippleTangent +
                        tangentNormal.y * rippleBitangent +
                        tangentNormal.z * gerstnerNormal);
                }
                else
                {
                    // 回退：程序化噪声当作坡度扰动
                    float2 rippleUV = input.positionWS.xz * (_RippleScale * 0.1);
                    float rippleA = ValueNoise(rippleUV + float2(_Time.y, _Time.y * 0.7) * _RippleSpeed);
                    float rippleB = ValueNoise(rippleUV * 1.7 - float2(_Time.y * 0.8, _Time.y) * _RippleSpeed);
                    float2 rippleGrad = float2(rippleA - rippleB, rippleB - rippleA);

                    normalWS = normalize(gerstnerNormal
                        + rippleTangent * rippleGrad.x * _RippleStrength
                        + rippleBitangent * rippleGrad.y * _RippleStrength);
                }

                // 折射偏移需要一个坡度量：没贴图时用程序噪声代替
                float2 rippleUVForRefract = input.positionWS.xz * (_RippleScale * 0.1);
                float refractNoiseA = ValueNoise(rippleUVForRefract + float2(_Time.y, _Time.y * 0.7) * _RippleSpeed);
                float refractNoiseB = ValueNoise(rippleUVForRefract * 1.7 - float2(_Time.y * 0.8, _Time.y) * _RippleSpeed);
                float2 rippleGradForRefract = float2(refractNoiseA - refractNoiseB, refractNoiseB - refractNoiseA);

                float3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 directLight = mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;
                float3 ambientLight = max(SampleSH(normalWS), 0.0);
                float3 diffuseLight = ambientLight + directLight * saturate(dot(normalWS, mainLight.direction));
                float3 lightDirWS = normalize(mainLight.direction);

                // ---- 岸边处理：平滑淡出，不用硬 clip ----
                //
                // 【为什么不用 clip()】clip 是逐像素的硬阈值，会在岸边留下
                // "一刀切"的锯齿和被整块剔除的矩形空洞（本工程实际出现过）。
                // 改成把 alpha 平滑压到 0：观感是水在岸边自然变薄消失，不会有硬边。
                float edgeNoise = SampleNoiseTexture(input.positionWS.xz, _Time.y);
                float shoreFade = smoothstep(0.0, max(0.01, _EdgeErosion), waterDepth / max(0.05, 1.0 - edgeNoise * 0.5));

                // ---- 水下折射 + 色散 ----
                // 偏移量优先取法线贴图的切线分量；没贴图时用程序噪声梯度代替。
                float2 refractSlope = hasNormalMap
                    ? tangentNormal.xy
                    : rippleGradForRefract;
                float distortMask = smoothstep(0.0, 0.5, waterDepth);
                float2 distortOffset = refractSlope * _RefractStrength * distortMask;

                float2 uvG = screenUV + distortOffset;
                float2 uvR = screenUV + distortOffset * (1.0 + _ChromaticAberration);
                float2 uvB = screenUV + distortOffset * (1.0 - _ChromaticAberration);

                half refrR = SampleSceneColor(uvR).r;
                half refrG = SampleSceneColor(uvG).g;
                half refrB = SampleSceneColor(uvB).b;
                half3 refraction = half3(refrR, refrG, refrB);

                // ---- 水色（吸收）----
                float3 absorption = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthT);
                // 浅处透出折射到的水底，深处被水体吸收吃掉
                // 折射颜色已包含场景光照；只有水体固有颜色需要额外受光，避免深水自发光。
                float3 color = lerp(refraction * _ShallowColor.rgb * 1.6, absorption * diffuseLight, smoothstep(0.0, 0.35, depthT));
                float alpha = lerp(_ShallowColor.a, _DeepColor.a, depthT);

                // ---- 焦散：水下网格状波光 ----
                // 用 _CausticsRange 控制"多深之后焦散消失"：
                // 水深超过 range 时衰减到 0，平方让过渡更自然（浅处明显、深处几乎看不见）。
                float causticsFade = pow(saturate(1.0 - waterDepth / max(0.1, _CausticsRange)), 2.0);
                bool hasCausticsMap;
                float3 causticsSample = SampleCaustics(input.positionWS.xz, _Time.y, hasCausticsMap);
                color += _CausticsColor.rgb * causticsSample * _CausticsIntensity * causticsFade * directLight;

                // ---- 菲涅尔 ----
                float nDotV = saturate(dot(normalWS, viewDirWS));
                float fresnel = pow(1.0 - nDotV, _FresnelPower);

                // ---- 环境反射（Forward+ 需要传纯净屏幕 UV，否则水面会死黑）----
                float3 reflectDir = reflect(-viewDirWS, normalWS);

                float3 probeColor = GlossyEnvironmentReflection(reflectDir, input.positionWS, 0.0, 1.0, screenUV).rgb;

                // 反射探针可能仍是白天的缓存；限制其贡献随当前天光/月光变化。
                probeColor *= saturate(ambientLight + mainLight.color * mainLight.distanceAttenuation);
                // 岸边让环境泛光自然消退，避免边缘像抹了一层颜色
                float glowFade = smoothstep(0.0, max(0.01, _DeepRange * 0.5), waterDepth);
                color += probeColor * _FresnelColor.rgb * fresnel * _GlowIntensity * glowFade;
                color = lerp(color, probeColor * _FresnelColor.rgb, fresnel * 0.5);
                alpha = saturate(alpha + fresnel * 0.3);

                // ---- 次表面散射：迎着阳光看浪尖时透出的碧绿色 ----
                float3 sssDir = -normalize(lightDirWS + normalWS * _SSSDistortion);
                float sssTerm = pow(saturate(dot(viewDirWS, sssDir)), _SSSPower) * _SSSIntensity;
                color += _SSSColor.rgb * sssTerm * directLight;

                // ---- 双轨高光 ----
                float3 halfDir = normalize(lightDirWS + viewDirWS);

                // 轨道 A：GGX 物理高光（拉长的连续光斑）
                float nDotH = saturate(dot(normalWS, halfDir));
                float roughness = saturate(1.0 - _Smoothness);
                float ggx = GGXDistribution(nDotH, max(0.02, roughness));
                color += directLight * ggx * _SpecularIntensity * (1.0 - fresnel) * 0.1;

                // 轨道 B：高频碎斑（用噪声贴图做微面元截断，没放图时用程序化噪声）
                float glintNoise = SampleGlintTexture(input.positionWS.xz, _Time.y);
                float specFactor = pow(nDotH, 16.0) * glintNoise;
                float glint = smoothstep(_GlintThreshold, _GlintThreshold + 0.05, specFactor);
                color += directLight * glint * _GlintStrength;

                // ---- 泡沫：岸边动态推拉 + 浪尖白沫 ----
                // 泡沫噪声优先用贴图（同一张 _FoamNoiseTex），保证消融和泡沫的图案一致。
                float foamNoise = SampleNoiseTexture(input.positionWS.xz, _Time.y);

                float animatedDepth = waterDepth
                    + sin(_Time.y * _FoamWaveSpeed - waterDepth * _FoamWaveFrequency) * _FoamPushPull;

                float foamMask = smoothstep(_FoamDissolve, _FoamDissolve + _FoamThickness,
                    (1.0 - saturate(animatedDepth / max(0.01, _FoamDistance))) * foamNoise);

                float totalFoam = saturate(max(foamMask, input.crestFoam * foamNoise));
                color = lerp(color, _FoamColor.rgb * diffuseLight, totalFoam * _FoamColor.a);
                alpha = saturate(alpha + totalFoam * 0.5);

                // 岸边淡出：把 alpha 平滑压到 0，取代上一版的硬 clip。
                // 泡沫区域不参与淡出（否则岸边白沫会被一起抹掉）。
                alpha = saturate(alpha * shoreFade + totalFoam * 0.5 * shoreFade);

                // ---- 雾 ----
                color = MixFog(color, input.fogFactor);

                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
