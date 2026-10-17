// 窗户玻璃：半透明 + 随昼夜变化的"透光发光"。
//
// 【为什么要自己写，而不是直接用 URP/Lit】
// URP/Lit 是受光材质：玻璃的亮度完全由环境光和太阳决定。
// 但窗户的观感来自"外面的光透过玻璃"，它应该比周围墙面亮 ——
// 尤其白天（太阳直射玻璃）和黄昏（低角度暖光）时最明显。
//
// 做法：在 URP/Lit 的 PBR 之上叠一层可控的自发光（emission），
// 强度由【全局属性 _WindowGlowAmount】驱动 ——
// 这样昼夜系统只要改一个全局值，场景里所有窗户一起变化，
// 不需要逐扇窗去 SetColor（性能上也没有逐材质的开销）。
//
// 【注意】这是"看起来透光"的近似，不是物理透射（真实透射需要烘焙 GI 或光追，
// URP 实时光下做不到 —— 详见工程笔记）。
Shader "Game/Environment/WindowGlass"
{
    // 【ShaderLab 属性块的四条硬性约束】本工程在这上面踩过四次，逐条记在这里。
    //
    // 这些说明【必须写在 Properties 块外面】—— 见第 1 条。
    //
    //   1. 属性块里不能有 `//` 注释 —— 整个 Properties 块会解析失败。
    //      （这是本工程最常犯的一条，已犯四次。）
    //   2. 属性显示名、[Header]/[Space] 的内容都必须是 ASCII。
    //      中文会报 `Parse error: syntax error, unexpected $undefined,
    //      expecting TVAL_ID or TVAL_VARREF`，而且【报错行号会偏到 [Header] 那一行】，
    //      真正的原因是下一行的中文显示名 —— 很难查。
    //   3. 只支持 [Header(...)] 和 [Space] 两种特性；
    //      [Tooltip(...)] 等其它特性会直接解析失败。
    //   4. 属性名不能重复 —— 重复会让整个 shader 静默失效（材质 Inspector 变空白）。
    //
    // 所以：显示名用英文，说明写在 HLSL 的注释里（那里可以随便写中文）。
    //
    // 属性一览（说明放这里，不放进块里）：
    //   _BaseColor        玻璃底色
    //   _Smoothness       光滑度
    //   _Metallic         金属度
    //   _Alpha            不透明度
    //   _GlowColor        透光颜色
    //   _GlowStrength     透光强度
    //   _GlowMinDaylight  起亮的环境亮度：低于它玻璃不再发光
    //                     （夜里窗户不该自己亮，除非室内开灯）
    Properties
    {
        [Header(Base)]
        _BaseColor ("Base Color (glass tint)", Color) = (0.62, 0.72, 0.80, 1)
        _Smoothness ("Smoothness", Range(0, 1)) = 0.92
        _Metallic ("Metallic", Range(0, 1)) = 0.0

        [Header(Alpha)]
        _Alpha ("Opacity", Range(0.05, 1)) = 0.55

        [Header(Glow by Day-Night)]
        _GlowColor ("Glow Color", Color) = (1.0, 0.94, 0.78, 1)
        _GlowStrength ("Glow Strength", Range(0, 8)) = 2.2
        _GlowMinDaylight ("Glow Start Daylight", Range(0, 1)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // 全局属性：不是 per-material，所以故意【不放在 Properties 块里】的对应 CBUFFER，
            // 而是直接声明 —— 由脚本用 Shader.SetGlobalFloat 驱动。
            //
            // 【重要】Shader.SetGlobalFloat 只对"没有暴露在材质属性块里"的属性生效。
            // 所以 _WindowGlowAmount 这里【没有】出现在上面的 Properties 块中 —— 这是刻意的，
            // 一旦写进属性块，材质上的值会覆盖全局值，昼夜控制就失效了。
            float _WindowGlowAmount;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float  _Smoothness;
                float  _Metallic;
                float  _Alpha;
                float4 _GlowColor;
                float  _GlowStrength;
                float  _GlowMinDaylight;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;

                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS);
                output.normalWS = normals.normalWS;

                output.fogFactor = ComputeFogFactor(positions.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));

                // ---- 主光（带阴影）----
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 lightDirWS = mainLight.direction;
                float3 lightColor = mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                // ---- 玻璃的漫反射 + 高光（简化版，够用）----
                float nDotL = saturate(dot(normalWS, lightDirWS));
                float3 diffuse = _BaseColor.rgb * lightColor * nDotL * 0.25;
                float3 ambient = SampleSH(normalWS) * _BaseColor.rgb;

                float3 halfDir = normalize(lightDirWS + viewDirWS);
                float spec = pow(saturate(dot(normalWS, halfDir)), lerp(4.0, 128.0, _Smoothness));
                float3 specular = lightColor * spec * _Smoothness;

                // ---- 随昼夜变化的透光 ----
                // _WindowGlowAmount 由昼夜系统写：白天为 1、夜里为 0。
                // _GlowMinDaylight 是"低于它就不发光"的门槛，避免夜里窗户自己亮着。
                float glow = saturate((_WindowGlowAmount - _GlowMinDaylight) / max(0.001, 1.0 - _GlowMinDaylight));

                float3 color = ambient + diffuse + specular;
                color += _GlowColor.rgb * glow * _GlowStrength * _GlowColor.a;

                // 白天玻璃稍微更实一点（看起来有反射），夜里更透（能看到室内）
                float alpha = saturate(_Alpha * lerp(0.75, 1.0, glow));

                color = MixFog(color, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
