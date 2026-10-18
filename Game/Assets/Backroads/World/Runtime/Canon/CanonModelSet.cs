using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// The frozen canon models (buildings.glb or trees.glb, CanonData/refs/models) as the importer resolved them from
    /// glTFast's import: per model, every mesh node with its transform relative to the model root (Unity space) and
    /// the glTF material name of each submesh. Spawners build their GameObjects from this, so builds never need
    /// glTFast's own materials.
    /// </summary>
    public sealed class CanonModelSet : ScriptableObject
    {
        [Serializable]
        public sealed class Part
        {
            /// <summary>glTF node name as glTFast imported it ("Glass.067").</summary>
            public string node;
            /// <summary>The same name sanitised the way three.js GLTFLoader does ("Glass067"), which placements.json uses.</summary>
            public string threeName;
            public Mesh mesh;
            /// <summary>glTF material name per submesh, numeric suffix removed ("Glass", "Wall").</summary>
            public string[] materials;
            /// <summary>Node transform relative to the model root, Unity space.</summary>
            public Vector3 localPosition;
            public Quaternion localRotation = Quaternion.identity;
            public Vector3 localScale = Vector3.one;
            /// <summary>True when every submesh uses the Glass material (one window pane).</summary>
            public bool isPane;
        }

        [Serializable]
        public sealed class Model
        {
            public string name;
            /// <summary>Parts in three.js traverse order (depth first, children in glTF order).</summary>
            public Part[] parts;
            /// <summary>Bounds of all parts in model-root space (Unity).</summary>
            public Bounds bounds;

            public int PaneCount
            {
                get
                {
                    int c = 0;
                    foreach (var p in parts) if (p.isPane) c++;
                    return c;
                }
            }
        }

        public string source;
        public string sha256;
        public Model[] models;
        /// <summary>glTF materials (suffix removed) and their baseColorFactor, linear as glTF stores it.</summary>
        public string[] materialNames;
        public Color[] materialColors;

        /// <summary>The glTF default colour of a material, linear; white when unknown.</summary>
        public Color DefaultColor(string material)
        {
            if (materialNames != null)
                for (int i = 0; i < materialNames.Length; i++)
                    if (materialNames[i] == material) return materialColors[i];
            return Color.white;
        }

        public Model Find(string name)
        {
            if (models != null)
                foreach (var m in models)
                    if (m.name == name) return m;
            return null;
        }
    }
}
