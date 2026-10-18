using UnityEditor;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Import rules for FBX files from the Blender pipeline (docs/research/07-art-pipeline.md section 3.3), minimal M0
    /// version: everything under Assets/Art/ imports at file scale 1 with bakeAxisConversion off, because Blender
    /// already applied the transform (bake_space_transform=True). Use one of the two, never both; Art.Canary proves it.
    /// The A lane extends this (material remaps, UCX_ hulls, user properties) from A1.
    /// </summary>
    public sealed class BackroadsModelPostprocessor : AssetPostprocessor
    {
        public const string ArtRoot = "Assets/Art/";

        public override uint GetVersion() => 1; // bump whenever the import rules change

        public static bool Applies(string assetPath) =>
            assetPath.StartsWith(ArtRoot, System.StringComparison.Ordinal) &&
            assetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);

        void OnPreprocessModel()
        {
            if (!Applies(assetPath)) return;
            var mi = (ModelImporter)assetImporter;
            mi.useFileScale = true;
            mi.globalScale = 1f;
            mi.bakeAxisConversion = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            if (assetPath.StartsWith(ArtRoot + "Canary/", System.StringComparison.Ordinal))
            {
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
            }
        }
    }
}
