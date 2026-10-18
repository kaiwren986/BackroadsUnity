// Shared helpers for the Backroads hand-written URP shaders (lane W, M1).
// World-space patterns sample at the JS coordinates float2(-x, z) (03 section 2), so they match the browser game.
#ifndef BR_COMMON_INCLUDED
#define BR_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// CanonData textures/detail.png (256x256 RGB tileable noise, rows as the JS DataTexture), set as a global by MillbrookWorld.
TEXTURE2D(_BrDetail);
SAMPLER(sampler_BrDetail);

// Day/night state set by DayNightPresenter: x = night factor (0 day .. 1 night), y = time of day in hours, z = wind time.
float4 _BrSky;

float2 BrJsXZ(float3 positionWS) { return float2(-positionWS.x, positionWS.z); }

half4 BrDetailTex(float2 uv) { return SAMPLE_TEXTURE2D(_BrDetail, sampler_BrDetail, uv); }

// Lit shading through URP's own PBR path (main light + shadows, additional lights, SH ambient, fog).
half4 BrShadePbr(float3 positionWS, half3 normalWS, float4 positionCS, half3 albedo, half metallic, half smoothness, half3 emission, half alpha, float fogFactor)
{
    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.positionCS = positionCS;
    inputData.normalWS = NormalizeNormalPerPixel(normalWS);
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN) && !defined(_SURFACE_TYPE_TRANSPARENT)
    inputData.shadowCoord = ComputeScreenPos(TransformWorldToHClip(positionWS));
#else
    inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
#endif
    inputData.fogCoord = InitializeInputDataFog(float4(positionWS, 1.0), fogFactor);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
    inputData.bakedGI = SampleSH(inputData.normalWS);
    inputData.shadowMask = half4(1, 1, 1, 1);

    SurfaceData s = (SurfaceData)0;
    s.albedo = albedo;
    s.metallic = metallic;
    s.smoothness = smoothness;
    s.occlusion = 1;
    s.emission = emission;
    s.alpha = alpha;
    half4 c = UniversalFragmentPBR(inputData, s);
    c.rgb = MixFog(c.rgb, inputData.fogCoord);
    return c;
}

// ---------------------------------------------------------------- shadow caster / depth helpers
float3 _LightDirection;
float3 _LightPosition;

float4 BrShadowPositionCS(float3 positionWS, float3 normalWS)
{
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif
    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    return ApplyShadowClamping(positionCS);
}

half3 BrSrgbToLinear(half3 c) { return SRGBToLinear(c); }

#endif
