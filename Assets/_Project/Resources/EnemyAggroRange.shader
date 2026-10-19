Shader "Game/Debug/EnemyAggroRange"
{
    Properties { _BaseColor ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float4 _BaseColor;
            struct Attributes { float4 position : POSITION; float4 color : COLOR; };
            struct Varyings { float4 position : SV_POSITION; float4 color : COLOR; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.position);
                output.color = input.color * _BaseColor;
                return output;
            }
            float4 Frag(Varyings input) : SV_Target { return input.color; }
            ENDHLSL
        }
    }
}
