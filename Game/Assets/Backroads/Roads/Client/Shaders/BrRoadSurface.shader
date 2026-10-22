// Backroads/Roads/Surface: the roads-R3 chunk meshes (05 §5.9), lane R. One shader, three materials:
//   Asphalt / Concrete: _BaseColor varied by the world-space detail noise (lane W's _BrDetail global), the vertex colour's
//     B (patch id, a few percent of tone), G (wetness: darker and glossier in gutters and at inlets) and A (edge blend:
//     slightly lighter where an unkerbed pavement meets its shoulder); R (traffic wear, R5) polishes wheel paths.
//   Paint (_Paint = 1): the vertex colour is the paint, broken up a little by the noise; pulled forwards with Offset.
// Lit through lane W's BrShadePbr (URP PBR, main light shadows, additional lights, SH, fog, sky reflection).
Shader "Backroads/Roads/Surface"
{
    Properties
    {
        _BaseColor ("Base colour", Color) = (0.06, 0.06, 0.065, 1)
        _Roughness ("Roughness", Range(0, 1)) = 0.86
        _DetailStrength ("Detail strength", Range(0, 1)) = 0.6
        _Paint ("Paint (vertex colour is the albedo)", Float) = 0
        _OffsetFactor ("Depth offset factor", Float) = 0
        _OffsetUnits ("Depth offset units", Float) = 0
    }

    HLSLINCLUDE
    #include "../../../Client.Rendering/Shaders/BrCommon.hlsl"

    CBUFFER_START(UnityPerMaterial)
        half4 _BaseColor;
        half _Roughness;
        half _DetailStrength;
        half _Paint;
        float _OffsetFactor;
        float _OffsetUnits;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float3 normalOS   : NORMAL;
        float2 uv0        : TEXCOORD0;
        float2 uv1        : TEXCOORD1;
        half4 color       : COLOR;
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
            Cull Back
            Offset [_OffsetFactor], [_OffsetUnits]

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
                half4 color       : TEXCOORD2;
                float fog         : TEXCOORD3;
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
                o.color = v.color;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 xz = BrJsXZ(i.positionWS);
                float n1 = BrDetailTex(xz * 0.9).r;
                float n2 = BrDetailTex(xz * 0.11).g;
                float n3 = BrDetailTex(xz * 0.013).b;
                float detail = lerp(1.0, (0.78 + 0.45 * n1) * (0.82 + 0.35 * n2) * (0.9 + 0.2 * n3), _DetailStrength);
                float3 albedo;
                float rough = _Roughness;
                if (_Paint > 0.5)
                {
                    albedo = i.color.rgb * lerp(1.0, 0.8 + 0.25 * n1, _DetailStrength);
                }
                else
                {
                    float patch = 0.94 + 0.12 * i.color.b;
                    float wet = i.color.g;
                    float wear = i.color.r;
                    albedo = _BaseColor.rgb * detail * patch;
                    albedo *= lerp(1.04, 1.0, i.color.a);
                    albedo *= 1.0 - 0.35 * wet;
                    rough = lerp(rough, 0.35, wet * 0.8);
                    rough = lerp(rough, rough * 0.8, wear);
                }
                return BrShadePbr(i.positionWS, i.normalWS, i.positionCS, albedo, 0, 1 - rough, 0, 1, i.fog);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertShadow
            #pragma fragment fragNull
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            float4 vertShadow(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                return BrShadowPositionCS(ws, TransformObjectToWorldNormal(v.normalOS));
            }
            half4 fragNull() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Back
            Offset [_OffsetFactor], [_OffsetUnits]

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
            ZWrite On Cull Back
            Offset [_OffsetFactor], [_OffsetUnits]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertDN
            #pragma fragment fragDN
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            struct VDN { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            VDN vertDN(Attributes v)
            {
                VDN o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 fragDN(VDN i) : SV_Target { return half4(NormalizeNormalPerPixel(i.normalWS), 0); }
            ENDHLSL
        }
    }
}
