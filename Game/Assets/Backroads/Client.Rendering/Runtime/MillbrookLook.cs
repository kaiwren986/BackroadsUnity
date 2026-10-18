using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// The committed materials and look constants of the M1 static world (Client.Rendering/Materials/MillbrookLook.asset,
    /// written by the Backroads.Client.Editor scene builder). Millbrook.unity references it, so every material and shader
    /// variant ships; spawners clone the templates (one instance per recolourable building slot, one per tree species).
    /// Values follow props.js / terrain.js / roads.js / water.js / sky.js (03 sections 4.3-4.11).
    /// </summary>
    public sealed class MillbrookLook : ScriptableObject
    {
        [Header("Ground and roads")]
        public Material terrain;
        public Material road;
        public Material water;
        public Material sky;

        [Header("Buildings (templates for buildings.glb parts)")]
        public Material buildingPart;
        public Material buildingMetal;
        public Material bulb;
        public Material sign;
        public Material glassLit;
        public Material glassDark;

        [Header("Merged props (static_props.glb buckets: bld, metal, winLit, winDark, bulb, neon, signRed)")]
        public Material[] propBuckets = new Material[7];

        [Header("Vegetation")]
        public Material tree;
        public Material rock;

        [Header("Post-processing")]
        public VolumeProfile volume;

        [Header("Lighting calibration (three.js physical units -> URP)")]
        [Tooltip("URP light intensity per three.js DirectionalLight intensity (three divides diffuse by pi; URP does not).")]
        public float sunScale = 0.318f;
        [Tooltip("Scale of the gradient ambient that stands in for three's sky IBL plus hemisphere light.")]
        public float ambientScale = 1f;
        [Tooltip("Post exposure in EV at day; three: ACES with exposure 0.95 / 0.6.")]
        public float dayExposureEv = 0.66f;
        public float lampIntensity = 10f;
        public float lampRange = 26f;
    }
}
