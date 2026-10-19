// Backroads/Roads/Debug Ribbon: R0 plain ribbons (lane R). Vertex colour = road class colour, half-Lambert from the main
// light plus a constant ambient, fog; pulled towards the camera so it sits on the M1 ribbons and the carved road bed.
Shader "Backroads/Roads/Debug Ribbon"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+20" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            Offset -3, -6

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float3 normalWS : TEXCOORD0; float fog : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color * _Color;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                Light l = GetMainLight();
                half ndl = saturate(dot(normalize(i.normalWS), l.direction)) * 0.5h + 0.5h;
                half3 c = i.color.rgb * (0.45h + 0.55h * ndl * saturate(length(l.color)));
                return half4(MixFog(c, i.fog), 1);
            }
            ENDHLSL
        }
    }
}
