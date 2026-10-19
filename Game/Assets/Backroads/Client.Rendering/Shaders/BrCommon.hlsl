// Shared helpers for the Backroads hand-written URP shaders (lane W, M1).
// World-space patterns sample at the JS coordinates float2(-x, z) (03 section 2), so they match the browser game.
#ifndef BR_COMMON_INCLUDED
#define BR_COMMON_INCLUDED

// Environment reflections come from the analytic sky below (three.js reflects its sky PMREM), not from probes.
#define _ENVIRONMENTREFLECTIONS_OFF 1
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// CanonData textures/detail.png (256x256 RGB tileable noise, rows as the JS DataTexture), set as a global by MillbrookWorld.
TEXTURE2D(_BrDetail);
SAMPLER(sampler_BrDetail);

// Day/night state set by DayNightPresenter: x = night factor (0 day .. 1 night), y = time of day in hours, z = wind time,
// w = emission scale.
float4 _BrSky;

// Sky gradient globals (DayNightPresenter), linear colours; shared with Backroads/Sky.
float4 _BrSkyZenith;
float4 _BrSkyHorizon;
float4 _BrSkyGlow;
float4 _BrSunDir;

float2 BrJsXZ(float3 positionWS) { return float2(-positionWS.x, positionWS.z); }

// The sky's colour in direction d (gradient + warm horizon glow; no sun disc, no stars).
half3 BrSkyColor(float3 d)
{
    d = normalize(d);
    float up = saturate(d.y);
    float3 col = lerp(_BrSkyHorizon.rgb, _BrSkyZenith.rgb, pow(up, 0.55));
    float hz = pow(1.0 - up, 3.0);
    float2 a = normalize(d.xz + 1e-4), b = normalize(_BrSunDir.xz + 1e-4);
    float toward = pow(max(dot(a, b), 0.0), 2.0);
    col += _BrSkyGlow.rgb * _BrSkyGlow.a * hz * (0.3 + 0.7 * toward);
    return d.y < 0 ? _BrSkyHorizon.rgb * lerp(1.0, 0.85, saturate(-d.y * 4)) : col;
}

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
    // sky reflection (split-sum style approximation): Schlick-Fresnel with roughness, blurred towards the mean sky
    half3 V = inputData.viewDirectionWS, N = inputData.normalWS;
    half rough = 1 - smoothness;
    half nv = saturate(dot(N, V));
    half3 f0 = lerp(half3(0.04, 0.04, 0.04), albedo, metallic);
    half3 F = f0 + (max((1 - rough).xxx, f0) - f0) * pow(1 - nv, 5);
    half3 R = reflect(-V, N);
    half3 sky = lerp(BrSkyColor(R), (_BrSkyHorizon.rgb + _BrSkyZenith.rgb) * 0.5, saturate(rough * 1.4));
    c.rgb += sky * F * (1 - rough * rough * 0.75) * (1 - metallic * 0.5 * rough);
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
