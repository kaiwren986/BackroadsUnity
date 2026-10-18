// Backroads/Water: water.js, one plane at y = 0 (1424 m square): colour 0x103f52, opacity 0.86, roughness 0.06,
// metalness 0.1, the 256^2 ripple normal map (CanonData textures/water_n.png) tiled 90 times with strength 0.32, scrolling
// (0.0045, 0.0031) per second. UVs follow three's PlaneGeometry rotated -90 deg about X: u along JS +x, v along JS -z.
Shader "Backroads/Water"
{
    Properties
    {
        _BaseColor ("Colour", Color) = (0.0627, 0.2471, 0.3216, 0.86)
        _NormalMap ("Ripples", 2D) = "bump" {}
        _NormalStrength ("Normal strength", Float) = 0.32
        _Tiles ("Tiles over the plane", Float) = 90
        _PlaneSize ("Plane size (m)", Float) = 1424
        _Smoothness ("Smoothness", Range(0, 1)) = 0.94
        _Metallic ("Metallic", Range(0, 1)) = 0.1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #define _SURFACE_TYPE_TRANSPARENT 1
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "BrCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _NormalMap_ST;
                half _NormalStrength;
                float _Tiles;
                float _PlaneSize;
                half _Smoothness;
                half _Metallic;
            CBUFFER_END
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float fogFactor   : TEXCOORD1;
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
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 xz = BrJsXZ(i.positionWS);
                float half_ = _PlaneSize * 0.5;
                float t = _BrSky.z;
                float2 uv = float2((xz.x + half_) / _PlaneSize, (half_ - xz.y) / _PlaneSize) * _Tiles + float2(t * 0.0045, t * 0.0031);
                half3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), _NormalStrength);
                // tangent = JS +x = Unity -x, bitangent = JS -z = Unity -z, normal = up
                float3 n = normalize(float3(-tn.x, tn.z, -tn.y));
                return BrShadePbr(i.positionWS, n, i.positionCS, _BaseColor.rgb, _Metallic, _Smoothness, 0, _BaseColor.a, i.fogFactor);
            }
            ENDHLSL
        }
    }
}
