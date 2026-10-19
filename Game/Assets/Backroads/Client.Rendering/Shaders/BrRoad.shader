// Backroads/Road: roads.js asphalt ribbon with its markings (03 section 4.5), drawn over the carved road bed.
//   TEXCOORD0 = roadUv (u across 0..1, d along in metres), TEXCOORD1.x = roadFade (centre line near junction ends)
// Asphalt 0.052 grey with 3 detail octaves at float2(-x, z); edge lines at u 0.04-0.062 and 0.938-0.96; dashed yellow
// centre at u 0.488-0.512 with a 9 m period; darker wheel tracks; markings rougher (0.62). Double-sided, pulled forward
// like polygonOffset(-2, -4).
Shader "Backroads/Road"
{
    Properties
    {
        _Roughness ("Roughness", Range(0, 1)) = 0.86
    }

    HLSLINCLUDE
    #include "BrCommon.hlsl"

    CBUFFER_START(UnityPerMaterial)
        half _Roughness;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float3 normalOS   : NORMAL;
        float2 roadUv     : TEXCOORD0;
        float2 fade       : TEXCOORD1;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            Offset -2, -4

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_LAYERS
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 roadUv     : TEXCOORD2;
                float2 fadeFog    : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.roadUv = v.roadUv;
                o.fadeFog = float2(v.fade.x, ComputeFogFactor(p.positionCS.z));
                return o;
            }

            float band(float x, float a, float b, float aa) { return smoothstep(a - aa, a + aa, x) - smoothstep(b - aa, b + aa, x); }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 xz = BrJsXZ(i.positionWS);
                float n1 = BrDetailTex(xz * 0.9).r;
                float n2 = BrDetailTex(xz * 0.11).g;
                float n3 = BrDetailTex(xz * 0.013).b;
                float3 asphalt = float3(0.052, 0.052, 0.056) * (0.78 + 0.45 * n1) * (0.82 + 0.35 * n2) * (0.9 + 0.2 * n3);
                float u = i.roadUv.x;
                float aa = fwidth(u) * 1.2;
                float edgeL = band(u, 0.04, 0.062, aa);
                float edgeR = band(u, 0.938, 0.96, aa);
                float f9 = frac(i.roadUv.y / 9.0);
                float dash = smoothstep(0.0, 0.04, f9) * (1.0 - smoothstep(0.5, 0.54, f9));
                float centre = band(u, 0.488, 0.512, aa) * dash;
                float worn = 0.65 + 0.35 * smoothstep(0.25, 0.7, n2);
                float3 col = asphalt;
                float tracks = band(u, 0.17, 0.33, 0.06) + band(u, 0.67, 0.83, 0.06);
                col *= 1.0 - 0.12 * tracks;
                col = lerp(col, float3(0.62, 0.62, 0.6), (edgeL + edgeR) * worn);
                col = lerp(col, float3(0.75, 0.52, 0.08), centre * worn * i.fadeFog.x);
                float rough = lerp(_Roughness, 0.62, (edgeL + edgeR + centre) * 0.8);
                float3 n = front ? i.normalWS : -i.normalWS;
                return BrShadePbr(i.positionWS, n, i.positionCS, col, 0, 1 - rough, 0, 1, i.fadeFog.y);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Off
            Offset -2, -4

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertDepth
            #pragma fragment fragNull
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            float4 vertDepth(Attributes v) : SV_POSITION { UNITY_SETUP_INSTANCE_ID(v); return TransformObjectToHClip(v.positionOS.xyz); }
            half4 fragNull() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull Off
            Offset -2, -4

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertDN
            #pragma fragment fragDN
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            struct VDN { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            VDN vertDN(Attributes v) { VDN o; UNITY_SETUP_INSTANCE_ID(v); UNITY_TRANSFER_INSTANCE_ID(v, o); o.positionCS = TransformObjectToHClip(v.positionOS.xyz); return o; }
            half4 fragDN(VDN i) : SV_Target { return half4(0, 1, 0, 0); }
            ENDHLSL
        }
    }
}
