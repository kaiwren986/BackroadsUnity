// Backroads/Light Pool: props.js buildLightPools, the fake pool of light under each street lamp: a 15 m quad at the
// lamp's ground + 0.09, additive, colour 0xffc27a, opacity 0.2 * night, radial falloff (1 - d)^1.8. Pulled forward like
// polygonOffset(-4, -8). UV (0..1) across the quad; fog fades it out.
Shader "Backroads/Light Pool"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.761, 0.478, 1)
        _Opacity ("Opacity at full night", Float) = 0.2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            Cull Off
            Offset -4, -8

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "BrCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Opacity;
            CBUFFER_END

            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float fog : TEXCOORD1; };

            V vert(A v)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                float d = min(1.0, length(i.uv * 2 - 1));
                float a = pow(1 - d, 1.8) * _Opacity * _BrSky.x;
                half3 c = _Color.rgb * a;
                c = MixFogColor(c, half3(0, 0, 0), i.fog);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
