using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Import rules for FBX files from the Blender pipeline (docs/research/07-art-pipeline.md sections 3.3 and 12).
    /// <list type="bullet">
    /// <item>Every FBX under Assets/Art/: file scale 1 and bakeAxisConversion off, because Blender already applied the
    /// transform (bake_space_transform=True); use one of the two, never both (Art.Canary proves it). Normals imported,
    /// Mikk tangents, no cameras or lights, no mesh compression.</item>
    /// <item>LOD Groups from <c>_LOD0/_LOD1/...</c> node names (<see cref="LodFromNames"/>), <c>UCX_</c> nodes become convex
    /// colliders with no renderer (<see cref="CollisionHulls"/>).</item>
    /// <item>Vehicles and wheels (Assets/Art/Vehicles/, Assets/Art/Wheels/): no animation, VH_ files readable (runtime
    /// dents), and every material remapped by name to the project slots (<see cref="VehicleMaterialSlots"/>) on the first
    /// import. The remap uses <see cref="Hook"/>; <see cref="RemapProbe"/> compares the two documented hooks (07 section 12).
    /// Each vehicle import depends on the slot material files, so a model imported before them is imported again.</item>
    /// </list>
    /// </summary>
    public sealed class BackroadsModelPostprocessor : AssetPostprocessor
    {
        public const string ArtRoot = "Assets/Art/";
        public const string VehicleRoot = "Assets/Art/Vehicles/";
        public const string WheelRoot = "Assets/Art/Wheels/";
        public const string CanaryRoot = "Assets/Art/Canary/";

        /// <summary>How the material remap is applied (07 section 12 asks which one works on the first import).</summary>
        public enum RemapHook
        {
            /// <summary>AssetImporter.AddRemap in OnPreprocessModel; materials stay "in prefab" (the default location).</summary>
            AddRemap,
            /// <summary>OnAssignMaterialModel returns the project material; materials stay "in prefab".</summary>
            AssignMaterialModel,
            /// <summary>OnAssignMaterialModel with the legacy External material location.</summary>
            AssignMaterialModelExternal,
        }

        /// <summary>
        /// The hook in use. On Unity 6000.3.20f1 all three remap every slot on the first import (RemapProbe,
        /// art/vehicles/truck/review/remap-probe.json); OnAssignMaterialModel is kept because it leaves the .meta clean
        /// (AddRemap writes an externalObjects entry for every name it knows, 38 today, most of them absent from the
        /// file) and the code stays the only place the map lives. RemapProbe switches it for its own imports and restores it.
        /// </summary>
        public static RemapHook Hook = RemapHook.AssignMaterialModel;

        public override uint GetVersion() => 3; // bump whenever the import rules change (A1: remap, LODs, UCX_; T3.4: buildings)

        public static bool Applies(string assetPath) =>
            assetPath.StartsWith(ArtRoot, System.StringComparison.Ordinal) &&
            assetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);

        public static bool IsVehicle(string assetPath) =>
            Applies(assetPath) &&
            (assetPath.StartsWith(VehicleRoot, System.StringComparison.Ordinal) || assetPath.StartsWith(WheelRoot, System.StringComparison.Ordinal));

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
            if (assetPath.StartsWith(CanaryRoot, System.StringComparison.Ordinal))
            {
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                mi.materialImportMode = ModelImporterMaterialImportMode.None;
            }
            if (BuildingMaterials.IsBuilding(assetPath))
            {
                // Buildings (card T3.4): static models on two project materials, no tangents (no normal maps), colliders
                // only from UCX_ hulls, vertex colours kept (pane ids in Panes_LOD*).
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                mi.importBlendShapes = false;
                mi.importTangents = ModelImporterTangents.None;
                mi.isReadable = false;
                mi.addCollider = false;
                mi.importVisibility = false;
                mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
                if (context != null)
                    foreach (var p in BuildingMaterials.SlotPaths()) context.DependsOnSourceAsset(p);
                return;
            }
            if (!IsVehicle(assetPath)) return;

            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.importBlendShapes = true;
            mi.isReadable = assetPath.Contains("/VH_");     // runtime dent deformation needs CPU-side vertices (07 3.3)
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.materialLocation = Hook == RemapHook.AssignMaterialModelExternal
                ? ModelImporterMaterialLocation.External
                : ModelImporterMaterialLocation.InPrefab;
            // Re-import when a slot material appears or changes (a fresh clone may import the model first).
            if (context != null)
                foreach (var slot in VehicleMaterialSlots.Slots) context.DependsOnSourceAsset(VehicleMaterialSlots.PathOf(slot));
            if (Hook != RemapHook.AddRemap)
            {
                // OnAssignMaterialModel decides: drop remaps an earlier AddRemap import left in the .meta
                foreach (var kv in mi.GetExternalObjectMap())
                    if (kv.Key.type == typeof(Material) && VehicleMaterialSlots.SlotFor(kv.Key.name) != null) mi.RemoveRemap(kv.Key);
                return;
            }
            foreach (var name in VehicleMaterialSlots.SourceNames())
            {
                var m = VehicleMaterialSlots.Target(name);
                if (m != null) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), name), m);
            }
        }

        Material OnAssignMaterialModel(Material material, Renderer renderer)
        {
            if (material == null) return null;
            if (BuildingMaterials.IsBuilding(assetPath)) return BuildingMaterials.Target(material.name);
            if (Hook == RemapHook.AddRemap || !IsVehicle(assetPath)) return null;
            return VehicleMaterialSlots.Target(material.name);
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!Applies(assetPath)) return;
            var log = new List<string>();
            int hulls = CollisionHulls.Build(root);
            int groups = LodFromNames.Build(root, log);
            if (Verbose && (hulls > 0 || groups > 0))
                Debug.Log($"[BR] art import {assetPath}: {groups} LOD groups, {hulls} UCX_ hulls; " + string.Join("; ", log));
        }

        /// <summary>Log what the rules did on each import (the probe and the batch tools turn it on).</summary>
        public static bool Verbose;
    }
}
