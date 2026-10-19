using System;
using UnityEngine;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// Today's browser-game vehicle models (the oracle's assets/vehicles/*.glb, imported by glTFast) as the editor
    /// builder resolved them: per vehicle the Body, Body_LOD1, Wheel, Wheel_LOD1 and Caliper meshes with the glTF
    /// material name of each submesh, and the glTF material values. Runtime materials come from the two URP templates
    /// here (vehicleModels.js swaps Paint, Glass and the lights for its own; everything else keeps the glTF colours),
    /// so builds never need glTFast's materials. Lives in Content/core/vehicles/Resources (BrVehicleModels).
    /// </summary>
    public sealed class VehicleModelSet : ScriptableObject
    {
        public const string ResourceName = "BrVehicleModels";

        [Serializable]
        public sealed class Part
        {
            public Mesh mesh;
            /// <summary>glTF material name per submesh ("Paint", "Glass", ...).</summary>
            public string[] materials;
        }

        [Serializable]
        public sealed class MaterialInfo
        {
            public string name;
            /// <summary>baseColorFactor (linear, as glTF stores it); alpha = opacity.</summary>
            public Color baseColor = Color.white;
            public float metallic, roughness = 1f;
            public bool blend;
            public Color emissive = Color.black;
        }

        [Serializable]
        public sealed class Model
        {
            public string id;
            public string source;
            public Part body, bodyLod1, wheel, wheelLod1, caliper;
            public MaterialInfo[] materials;

            public MaterialInfo Material(string name)
            {
                if (materials != null) foreach (var m in materials) if (m.name == name) return m;
                return null;
            }
        }

        public Model[] models;
        /// <summary>URP/Lit, opaque, two-sided, emission on (black unless a light).</summary>
        public Material opaqueTemplate;
        /// <summary>URP/Lit, transparent, two-sided, no depth write (vehicleModels.js glassMat).</summary>
        public Material glassTemplate;

        public Model Find(string id)
        {
            if (models != null) foreach (var m in models) if (m.id == id) return m;
            return null;
        }

        static VehicleModelSet s_Loaded;

        public static VehicleModelSet Load()
        {
            if (s_Loaded == null) s_Loaded = Resources.Load<VehicleModelSet>(ResourceName);
            return s_Loaded;
        }
    }
}
