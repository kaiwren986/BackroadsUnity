using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// The building generator's materials (card T3.4, art/buildings/style-guide.md): one opaque palette-atlas material and
    /// one glass material, the only two names the generator writes. URP/Lit placeholders until the M5 night-light shader
    /// (the pane id sits in the glass mesh's vertex colour). Created by <see cref="CreateBuildingMaterials"/>, never
    /// overwritten, so hand tweaks survive.
    /// </summary>
    public static class BuildingMaterials
    {
        public const string BuildingRoot = "Assets/Art/Buildings/";
        public const string MaterialRoot = "Assets/Art/Materials/Buildings/";
        public const string PalettePath = BuildingRoot + "Textures/T_Bld_Palette.png";
        public const string Atlas = "M_Bld_Atlas";
        public const string Glass = "M_Bld_Glass";
        public static readonly string[] Slots = { Atlas, Glass };

        /// <summary>Glass colour, art/buildings/gen/palette.py GLASS (sRGB).</summary>
        public static readonly Color32 GlassColour = new Color32(0x4b, 0x60, 0x74, 255);

        public static string PathOf(string slot) => MaterialRoot + slot + ".mat";

        public static bool IsBuilding(string assetPath) =>
            BackroadsModelPostprocessor.Applies(assetPath) && assetPath.StartsWith(BuildingRoot, System.StringComparison.Ordinal);

        /// <summary>The project material for a material name written by the generator, or null.</summary>
        public static Material Target(string sourceName)
        {
            if (sourceName != Atlas && sourceName != Glass) return null;
            return AssetDatabase.LoadAssetAtPath<Material>(PathOf(sourceName));
        }

        public static void CreateBuildingMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new System.InvalidOperationException("URP Lit shader not found");
            ArtMaterials.EnsureFolder(MaterialRoot.TrimEnd('/'));
            AssetDatabase.ImportAsset(PalettePath, ImportAssetOptions.ForceSynchronousImport);
            // the palette's import settings live in its committed .meta (no texture postprocessor: that would reimport
            // every texture of every worktree once)
            if (AssetImporter.GetAtPath(PalettePath) is TextureImporter ti && !PaletteSettingsOk(ti))
            {
                ApplyPaletteSettings(ti);
                ti.SaveAndReimport();
            }
            var palette = AssetDatabase.LoadAssetAtPath<Texture2D>(PalettePath);
            if (palette == null) throw new System.IO.FileNotFoundException("palette not imported: " + PalettePath + " (run art\\buildings\\gen\\run.ps1)");
            int made = 0;
            if (AssetDatabase.LoadAssetAtPath<Material>(PathOf(Atlas)) == null)
            {
                var m = new Material(shader) { name = Atlas };
                m.SetTexture("_BaseMap", palette);
                m.SetColor("_BaseColor", Color.white);
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_Smoothness", 0.12f);
                AssetDatabase.CreateAsset(m, PathOf(Atlas));
                made++;
            }
            if (AssetDatabase.LoadAssetAtPath<Material>(PathOf(Glass)) == null)
            {
                var m = new Material(shader) { name = Glass };
                m.SetColor("_BaseColor", GlassColour);
                m.SetFloat("_Metallic", 0.1f);
                m.SetFloat("_Smoothness", 0.85f);
                AssetDatabase.CreateAsset(m, PathOf(Glass));
                made++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[BR] BuildingMaterials: {made} building materials created in {MaterialRoot}");
        }

        /// <summary>Palette atlas import: flat swatches, so no compression, bilinear, clamped, with mips (4 px margins).</summary>
        public static void ApplyPaletteSettings(TextureImporter ti)
        {
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.mipmapEnabled = true;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 512;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.isReadable = false;
        }

        public static bool PaletteSettingsOk(TextureImporter ti) =>
            ti.sRGBTexture && ti.mipmapEnabled && ti.filterMode == FilterMode.Bilinear && ti.wrapMode == TextureWrapMode.Clamp &&
            ti.maxTextureSize == 512 && ti.textureCompression == TextureImporterCompression.Uncompressed && !ti.isReadable &&
            ti.alphaSource == TextureImporterAlphaSource.None;

        public static IEnumerable<string> SlotPaths()
        {
            foreach (var s in Slots) yield return PathOf(s);
        }
    }
}
