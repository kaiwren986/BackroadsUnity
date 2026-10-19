// Backroads/Sky: gradient skybox for M1 ("sun + gradient ambient"), driven by DayNightPresenter through globals:
// _BrSkyZenith, _BrSkyHorizon (the JS fog colour, so distant terrain melts into the sky), _BrSkyGlow (warm horizon
// glow towards the sun), _BrSunDir (Unity space, towards the sun), _BrSunDisc (rgb colour, a = size). Stands in for
// three's Sky shader (sky.js) until the M5 look pass.
Shader "Backroads/Sky"
{
    Properties { }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _BrSkyZenith;
            float4 _BrSkyHorizon;
            float4 _BrSkyGlow;
            float4 _BrSunDir;
            float4 _BrSunDisc;
            // (BrCommon.hlsl BrSkyColor mirrors the gradient below for reflections)
            float4 _BrSky;

            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };

            V vert(A v)
            {
                V o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }

            half4 frag(V i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float up = saturate(d.y);
                float3 col = lerp(_BrSkyHorizon.rgb, _BrSkyZenith.rgb, pow(up, 0.55));
                // warm glow along the horizon towards the sun (sky.js glowColor * glowAmt * hz * (0.3 + 0.7 toward))
                float hz = pow(1.0 - up, 3.0);
                float2 a = normalize(d.xz + 1e-4), b = normalize(_BrSunDir.xz + 1e-4);
                float toward = pow(max(dot(a, b), 0.0), 2.0);
                col += _BrSkyGlow.rgb * _BrSkyGlow.a * hz * (0.3 + 0.7 * toward);
                // below the horizon: the horizon colour, slightly darker
                col = d.y < 0 ? _BrSkyHorizon.rgb * lerp(1.0, 0.85, saturate(-d.y * 4)) : col;
                // sun or moon disc with a soft halo
                float c = dot(d, normalize(_BrSunDir.xyz));
                float disc = smoothstep(1.0 - _BrSunDisc.a, 1.0 - _BrSunDisc.a * 0.6, c);
                float halo = pow(saturate(c), 600.0) * 0.6 + pow(saturate(c), 40.0) * 0.08;
                col += _BrSunDisc.rgb * (disc + halo) * step(-0.02, d.y);
                // stars (sky.js: 2,600 points, opacity smooth(0.35, 0.95, night)): about 2,000 on a hashed direction grid
                float3 g = d * 220.0;
                float3 cell = floor(g);
                float h = frac(sin(dot(cell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                float3 f = frac(g) - 0.5;
                float star = step(0.9965, h) * smoothstep(0.35, 0.0, length(f)) * (0.4 + 0.9 * frac(h * 91.7));
                col += star * smoothstep(0.35, 0.95, _BrSky.x) * smoothstep(-0.02, 0.05, d.y) * 1.4;
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
