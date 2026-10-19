using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// placements.json of CanonData v1 (03 section 3.3, PLAN 1.8.2): placed GLB buildings, the typed records from the
    /// oracle's wrapper hooks, lamps, parking bays, delivery rings, footprints, boats and the static collider records.
    /// JS space throughout (Unity = (-x, y, z), yaw = -rot).
    /// </summary>
    public sealed class PlacementSet : ScriptableObject
    {
        [Serializable]
        public sealed class Building
        {
            public string model, src;
            public double x, y, z, rot;
            public float sx, sz, w, d;
            /// <summary>Recolourable slot names (Wall, Roof, Door, Awning, Accent, Cladding) and their sRGB colours.</summary>
            public string[] colorSlots;
            public Color32[] colors;
            /// <summary>'1' or '0' per pane, in the order of <see cref="panes"/> (three.js traverse order).</summary>
            public string lit;
            /// <summary>three.js-sanitised glTF node names of the model's Glass panes ("Glass067" for node "Glass.067").</summary>
            public string[] panes;
            /// <summary>three.js Matrix4.elements of the placed model (column-major, JS space).</summary>
            public double[] m;

            public bool TryGetColor(string slot, out Color32 c)
            {
                if (colorSlots != null)
                    for (int i = 0; i < colorSlots.Length; i++)
                        if (colorSlots[i] == slot) { c = colors[i]; return true; }
                c = default;
                return false;
            }
        }

        /// <summary>
        /// A typed record (lamp, model, silo, dock, fuelStation, crusher, sawShed). Numeric fields beyond the pose are kept
        /// by name in <see cref="keys"/>/<see cref="values"/>; nested objects are flattened with dots ("q.ux").
        /// </summary>
        [Serializable]
        public sealed class Typed
        {
            public string type, name;
            public bool hasPose, hasRot;
            public double x, y, z, rot;
            public string[] keys;
            public double[] values;
            public string[] colorSlots;
            public Color32[] colors;
            public string lit;
            public string poi;

            public bool TryGet(string key, out double v)
            {
                if (keys != null)
                    for (int i = 0; i < keys.Length; i++)
                        if (keys[i] == key) { v = values[i]; return true; }
                v = 0;
                return false;
            }
        }

        [Serializable]
        public struct Lamp { public float x, y, z, gy; }

        [Serializable]
        public struct Parking { public float x, z, yaw; }

        [Serializable]
        public struct Ring { public string poi; public float x, z, y; }

        [Serializable]
        public struct Footprint { public float x, z, r, rot, hw, hd; public bool hasRect; }

        [Serializable]
        public struct Boat { public float x, z, ang, phase; }

        public enum ColliderType : byte { Circle = 0, Box = 1 }

        [Serializable]
        public struct Collider
        {
            public ColliderType type;
            public string kind;
            public float x, z, r, hw, hd, rot, y0, y1;
            /// <summary>Index into VegetationSet.trees for tree colliders, else -1.</summary>
            public int tree;
        }

        [Serializable]
        public struct Box { public float x, z, hw, hd, rot, y0, y1; }

        public Building[] buildings;
        public Typed[] typed;
        public Lamp[] lamps;
        public Parking[] parking;
        public Ring[] rings;
        public Footprint[] footprints;
        public Boat[] boats;
        public Boat[] boatSpots;
        public Collider[] colliders;
        public Box[] boxes;
        /// <summary>The op name of every canon replay op in placements.json "ops", in order (model, box, mailbox, fence).</summary>
        public string[] opTypes;
        public int rngCalls;

        public int CountTyped(string type)
        {
            int c = 0;
            if (typed != null) foreach (var t in typed) if (t.type == type) c++;
            return c;
        }

        public int CountColliders(ColliderType type, string kind)
        {
            int c = 0;
            if (colliders != null) foreach (var t in colliders) if (t.type == type && t.kind == kind) c++;
            return c;
        }
    }
}
