using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Creates the vehicle slot materials (07 section 4.3) as plain URP/Lit placeholders until phase C replaces their
    /// shaders with SG_CarPaint, SG_VehicleTrim, SG_VehicleGlass and SG_VehicleLights. Existing materials are never
    /// overwritten, so hand tweaks survive. Colours come from art/legacy/vehicles.py MATDEF (sRGB hex).
    ///   Unity -batchmode -quit -executeMethod Backroads.Art.Editor.ArtMaterials.CreateVehicleSlots
    /// </summary>
    public static class ArtMaterials
    {
        struct Look
        {
            public uint rgb; public float metallic, smoothness, alpha;
            public Look(uint rgb, float metallic, float smoothness, float alpha = 1f) { this.rgb = rgb; this.metallic = metallic; this.smoothness = smoothness; this.alpha = alpha; }
        }

        static readonly Dictionary<string, Look> Looks = new Dictionary<string, Look>
        {
            ["M_Paint"] = new Look(0x2e9fd4, 0.5f, 0.70f),        // Paint (clear coat comes with SG_CarPaint)
            ["M_Trim"] = new Look(0x3a3c40, 0.6f, 0.50f),          // between Trim, Steel and Chrome
            ["M_Glass"] = new Look(0x10161b, 0.0f, 0.97f, 0.38f),  // Glass
            ["M_Lights"] = new Look(0xe0e0e0, 0.0f, 0.88f),        // Reverse; lamp colours come from vertex channels later
            ["M_Tyre"] = new Look(0x151516, 0.0f, 0.08f),          // Rubber
            ["M_Rim"] = new Look(0xc8ccd2, 1.0f, 0.78f),           // Alloy
            ["M_Interior"] = new Look(0x1d1e20, 0.0f, 0.15f),      // Interior
            ["M_Chassis"] = new Look(0x0c0c0d, 0.0f, 0.10f),       // Under
            ["M_Decal"] = new Look(0xe9e6da, 0.0f, 0.50f),         // Plate
            ["M_VehicleFar"] = new Look(0x7a7d82, 0.2f, 0.40f),
        };

        public static void CreateVehicleSlots()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new System.InvalidOperationException("URP Lit shader not found");
            EnsureFolder(VehicleMaterialSlots.MaterialRoot.TrimEnd('/'));
            int made = 0;
            foreach (var slot in VehicleMaterialSlots.Slots)
            {
                var path = VehicleMaterialSlots.PathOf(slot);
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) continue;
                var look = Looks[slot];
                var m = new Material(shader) { name = slot };
                var c = new Color32((byte)(look.rgb >> 16), (byte)(look.rgb >> 8), (byte)look.rgb, 255);
                Color col = c;
                col.a = look.alpha;
                m.SetColor("_BaseColor", col);
                m.SetFloat("_Metallic", look.metallic);
                m.SetFloat("_Smoothness", look.smoothness);
                if (look.alpha < 1f) MakeTransparent(m);
                AssetDatabase.CreateAsset(m, path);
                made++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[BR] ArtMaterials: {made} vehicle slot materials created in {VehicleMaterialSlots.MaterialRoot}");
        }

        /// <summary>Creates an Assets/... folder (and its parents) through the AssetDatabase.</summary>
        public static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        static void MakeTransparent(Material m)
        {
            m.SetFloat("_Surface", 1f);   // Transparent
            m.SetFloat("_Blend", 0f);     // Alpha
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("DepthOnly", false);
        }
    }
}
