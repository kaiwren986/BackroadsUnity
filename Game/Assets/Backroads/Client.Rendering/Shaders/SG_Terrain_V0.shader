// SG_Terrain_V0 (T1.3): the terrain.js ground shader, ported to a hand-written URP shader (HLSL rather than a Shader
// Graph asset, so agents can author and diff it). Vertex streams from TerrainChunkBuildJob:
//   COLOR     = colour of the vertex's own surface (linear) + blendw (share of the second surface)
//   TEXCOORD0 = colour of the second surface (linear) + wear
//   TEXCOORD1 = kindA (grass, rock, concrete, gravel weights), TEXCOORD2 = kindB, TEXCOORD3.x = wet
// Every world-space pattern samples the detail noise at the JS coordinates float2(-x, z) (03 section 2).
Shader "Backroads/SG_Terrain_V0"
{
    Properties
    {
        _Roughness ("Base roughness", Range(0, 1)) = 0.93
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
        float4 color      : COLOR;
        float4 uv0        : TEXCOORD0;
        float4 uv1        : TEXCOORD1;
        float4 uv2        : TEXCOORD2;
        float2 uv3        : TEXCOORD3;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

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
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2;
                float4 color2     : TEXCOORD3;
                float4 kindA      : TEXCOORD4;
                float4 kindB      : TEXCOORD5;
                float2 wetFog     : TEXCOORD6;
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
                o.color2 = v.uv0;
                o.kindA = v.uv1;
                o.kindB = v.uv2;
                o.wetFog = float2(v.uv3.x, ComputeFogFactor(p.positionCS.z));
                return o;
            }

            float cel(float n, float edge) { float w = fwidth(n) * 1.2 + 0.008; return smoothstep(edge - w, edge + w, n); }
            float hash2(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            half4 D(float2 uv) { return BrDetailTex(uv); }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 xz = BrJsXZ(i.positionWS);           // vWPos.xz in terrain.js
                float wy = i.positionWS.y;
                float vBlend = i.color.a, vWear = i.color2.a, vWet = i.wetFog.x;

                float d0 = D(xz * 0.29 + 0.5).b;
                float d1 = D(xz * 0.23).r;
                float d2 = D(xz * 0.041 + 0.37).g;
                float d3 = D(xz * 0.0067 + 0.11).b;
                // ragged but crisp boundary between the two surfaces this fragment sits between
                float edgeN = (d1 - 0.5) * 0.3 + (d0 - 0.5) * 0.14;
                float ew = fwidth(vBlend) * 0.9 + 0.02;
                float sb = smoothstep(0.5 - ew, 0.5 + ew, vBlend + edgeN * smoothstep(0.0, 0.08, vBlend) * smoothstep(1.0, 0.92, vBlend));
                float3 col = lerp(i.color.rgb, i.color2.rgb, sb);
                float4 kd = lerp(i.kindA, i.kindB, sb);
                float gW = kd.x, rW = kd.y, cW = kd.z, vW = kd.w;
                float sW = max(0.0, 1.0 - gW - rW - cW - vW);
                col *= 0.9 + (d2 * 0.6 + d3 * 0.4) * 0.2;

                // grass: painted patches, clumps, bright flecks
                float clump = D(xz * 0.11 + 0.21).r;
                float3 gTone = lerp(float3(0.93, 0.98, 0.92), float3(1.05, 1.03, 0.89), cel(d3, 0.52));
                gTone *= lerp(0.9, 1.0, cel(clump, 0.44)) * lerp(1.0, 1.06, cel(clump, 0.62));
                gTone *= lerp(0.94, 1.0, cel(d0, 0.32)) * lerp(1.0, 1.09, cel(d0, 0.7));
                // soil: damp patches, pebbles, dark clods
                float peb = D(xz * 0.37 + 0.13).r;
                float3 sTone = lerp(0.9, 1.0, cel(d1, 0.42)).xxx;
                sTone *= lerp(1.0, 1.2, cel(peb, 0.74)) * lerp(0.86, 1.0, cel(peb, 0.24));
                // gravel: dense light and dark stones (GLSL mat2(0.8, -0.6, 0.6, 0.8) * v)
                float2 rg = float2(0.8 * xz.x + 0.6 * xz.y, -0.6 * xz.x + 0.8 * xz.y);
                float grv = D(rg * 0.53 + 0.71).g * 0.6 + D(xz * 0.21 + 0.3).r * 0.4;
                float grf = D(xz * 1.9 + 0.4).b;
                float3 vTone = (lerp(0.9, 1.0, cel(grv, 0.38)) * lerp(1.0, 1.08, cel(grv, 0.63)) * lerp(0.9, 1.0, cel(grf, 0.3)) * lerp(1.0, 1.1, cel(grf, 0.72))).xxx;
                // concrete: 4 m slabs with dark joints
                float2 sl = xz / 4.0;
                float2 fe = abs(frac(sl) - 0.5);
                float joint = max(fe.x, fe.y);
                float jw = fwidth(joint) * 1.5 + 0.004;
                float3 cTone = ((0.95 + hash2(floor(sl)) * 0.08) * lerp(1.0, 0.72, smoothstep(0.5 - 0.008 - jw, 0.5 - 0.008, joint))).xxx;
                cTone *= lerp(0.93, 1.0, cel(d1, 0.35));
                // rock: tilted strata
                float str = wy * 0.55 + (xz.x + xz.y) * 0.08 + d2 * 2.5 + d1 * 0.6;
                float bnd = frac(str);
                float3 rTone = (lerp(0.86, 1.06, cel(bnd, 0.55)) * lerp(0.93, 1.0, cel(d0, 0.35))).xxx;
                rTone *= lerp(1.0, 0.82, cel(bnd, 0.94));
                float3 tones = gTone * gW + sTone * sW + vTone * vW + cTone * cW + rTone * rW;

                // mud: slick and swirled, puddles on the flat (GLSL mat2(0.8, 0.6, -0.6, 0.8) * (v * (0.09, 0.35)))
                float mudW = smoothstep(0.12, 0.24, vWet);
                float2 sv = xz * float2(0.09, 0.35);
                float sw1 = D(float2(0.8 * sv.x - 0.6 * sv.y, 0.6 * sv.x + 0.8 * sv.y)).r;
                float sw2 = D(xz * 0.6 + 0.2).g;
                float3 mudTone = (lerp(0.86, 1.07, cel(sw1, 0.5)) * lerp(1.0, 0.9, cel(sw2, 0.62))).xxx;
                col *= lerp(tones, mudTone, mudW);
                float pn = d1 * 0.55 + D(xz * 0.07 + 0.5).b * 0.45;
                float3 fn = normalize(cross(ddx(i.positionWS), ddy(i.positionWS)));
                float flatW = smoothstep(0.95, 0.99, abs(fn.y));
                float pe = 0.6 - 0.22 * smoothstep(0.3, 0.6, vWet);
                float puddle = mudW * flatW * max(cel(pn, pe), smoothstep(0.6, 0.9, vWet));
                col = lerp(col, col * float3(0.42, 0.44, 0.48), puddle * 0.85);
                float rim = mudW * flatW * (cel(pn, pe - 0.05) - cel(pn, pe));
                col *= 1.0 + rim * 0.12;
                // tyre streaks inside worn soil
                float streak = D(float2(xz.x * 1.7, xz.y * 1.7)).g;
                col *= 1.0 - vWear * 0.18 * smoothstep(0.35, 0.75, streak);

                float rough = _Roughness;
                rough = lerp(rough, 0.62, smoothstep(0.3, 0.95, vWear));
                rough = lerp(rough, 0.55, smoothstep(0.1, 0.35, vWet));
                rough = lerp(rough, 0.12, smoothstep(0.55, 0.95, vWet));
                rough = lerp(rough, 0.04, puddle);

                return BrShadePbr(i.positionWS, i.normalWS, i.positionCS, col, 0, 1 - rough, 0, 1, i.wetFog.y);
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
                return BrShadowPositionCS(TransformObjectToWorld(v.positionOS.xyz), TransformObjectToWorldNormal(v.normalOS));
            }
            half4 fragNull() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertDepth
            #pragma fragment fragNull
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            float4 vertDepth(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                return TransformObjectToHClip(v.positionOS.xyz);
            }
            half4 fragNull() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull Back

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
