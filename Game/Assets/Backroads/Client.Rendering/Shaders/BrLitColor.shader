// Backroads/Lit Color: the MeshStandardMaterial stand-in for lane W's static world (T1.4).
//   _VERTEX_COLOR : albedo *= vertex colour (linear, as three.js treats COLOR_0 and the merged prop buckets)
//   _USER_TINT    : albedo *= the tree instance tint packed in Renderer.SetShaderUserValue (03 section 4.8):
//                   bits 0-15 = tint * 4096, bits 16-31 = hue * 65535; tint (t, t (0.96 + 0.08 hue), t (0.86 + 0.1 hue));
//                   hue bits all set = rock grey (v, 0.97 v, 0.92 v) (props.js buildRocks)
//   _WIND         : props.js treeMaterial sway (bend grows with height squared), phase from the JS-space origin
// Emission = _EmissionColor * (_EmissionBase + _EmissionNight * night) with night from _BrSky.x (props.js setNight).
Shader "Backroads/Lit Color"
{
    Properties
    {
        _BaseColor ("Base colour", Color) = (1, 1, 1, 1)
        _Smoothness ("Smoothness (1 - roughness)", Range(0, 1)) = 0.18
        _Metallic ("Metallic", Range(0, 1)) = 0.04
        [HDR] _EmissionColor ("Emission colour", Color) = (0, 0, 0, 1)
        _EmissionBase ("Emission at day", Float) = 0
        _EmissionNight ("Extra emission at night", Float) = 0
        _Sway ("Wind sway", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Toggle(_VERTEX_COLOR)] _VertexColor ("Vertex colour", Float) = 0
        [Toggle(_USER_TINT)] _UserTint ("Tree tint (renderer user value)", Float) = 0
        [Toggle(_WIND)] _Wind ("Wind", Float) = 0
    }

    HLSLINCLUDE
    #include "BrCommon.hlsl"

    CBUFFER_START(UnityPerMaterial)
        half4 _BaseColor;
        half4 _EmissionColor;
        half _Smoothness;
        half _Metallic;
        half _EmissionBase;
        half _EmissionNight;
        half _Sway;
        half _Cull;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
        float3 normalOS   : NORMAL;
        float4 color      : COLOR;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    float3 BrWind(float3 p)
    {
    #if defined(_WIND)
        float3 ipU = TransformObjectToWorld(float3(0, 0, 0));
        float2 ip = float2(-ipU.x, ipU.z);          // JS-space instance origin
        float t = _BrSky.z;
        float hb = max(p.y - 1.5, 0.0);
        float gust = 0.6 + 0.4 * sin(t * 0.37 + ip.x * 0.013 + ip.y * 0.011);
        float ph = t * 1.25 + ip.x * 0.17 + ip.y * 0.13;
        float bend = hb * hb * 0.0016 * _Sway * gust;
        p.x -= sin(ph) * bend + sin(ph * 2.7 + p.y) * bend * 0.25;   // JS +x is Unity -x (model space too)
        p.z += cos(ph * 0.83) * bend * 0.6;
    #endif
        return p;
    }

    half3 BrUserTint()
    {
    #if defined(_USER_TINT)
        uint v = unity_RendererUserValue;
        float t = (v & 0xFFFFu) / 4096.0;
        uint hq = v >> 16;
        if (hq == 0xFFFFu) return half3(t, t * 0.97, t * 0.92);   // rocks: (v, 0.97 v, 0.92 v)
        float hue = hq / 65535.0;
        return half3(t, t * (0.96 + 0.08 * hue), t * (0.86 + 0.1 * hue));
    #else
        return half3(1, 1, 1);
    #endif
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _VERTEX_COLOR
            #pragma shader_feature_local _USER_TINT
            #pragma shader_feature_local _WIND
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_LAYERS
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #if defined(LOD_FADE_CROSSFADE)
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            #endif

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                half4 color       : TEXCOORD2;
                float fogFactor   : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                VertexPositionInputs p = GetVertexPositionInputs(BrWind(v.positionOS.xyz));
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                half4 c = _BaseColor;
            #if defined(_VERTEX_COLOR)
                c.rgb *= v.color.rgb;
            #endif
                c.rgb *= BrUserTint();
                o.color = c;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
            #if defined(LOD_FADE_CROSSFADE)
                LODFadeCrossFade(i.positionCS);
            #endif
                float3 n = front ? i.normalWS : -i.normalWS;
                half3 emission = _EmissionColor.rgb * (_EmissionBase + _EmissionNight * _BrSky.x);
                return BrShadePbr(i.positionWS, n, i.positionCS, i.color.rgb, _Metallic, _Smoothness, emission, 1, i.fogFactor);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertShadow
            #pragma fragment fragNull
            #pragma shader_feature_local _WIND
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            float4 vertShadow(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                return BrShadowPositionCS(TransformObjectToWorld(BrWind(v.positionOS.xyz)), TransformObjectToWorldNormal(v.normalOS));
            }
            half4 fragNull() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertDepth
            #pragma fragment fragNull
            #pragma shader_feature_local _WIND
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            float4 vertDepth(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                return TransformObjectToHClip(BrWind(v.positionOS.xyz));
            }
            half4 fragNull() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertDN
            #pragma fragment fragDN
            #pragma shader_feature_local _WIND
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            struct VDN { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            VDN vertDN(Attributes v)
            {
                VDN o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(BrWind(v.positionOS.xyz));
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 fragDN(VDN i, bool front : SV_IsFrontFace) : SV_Target
            {
                float3 n = NormalizeNormalPerPixel(front ? i.normalWS : -i.normalWS);
                return half4(n, 0);
            }
            ENDHLSL
        }
    }
}
