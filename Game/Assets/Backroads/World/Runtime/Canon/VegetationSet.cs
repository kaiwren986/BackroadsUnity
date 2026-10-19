using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>vegetation.json of CanonData v1 (03 sections 4.8, 4.9): the species table, 5,265 trees and the rocks. JS space.</summary>
    public sealed class VegetationSet : ScriptableObject
    {
        [Serializable]
        public struct Species
        {
            public string name;
            public float h, trunk, strength, sMin, sMax, sway;
        }

        /// <summary>One tree: ground position (the mesh sits at y - 0.2), species index, scale, yaw (JS), tint and hue.</summary>
        [Serializable]
        public struct Tree
        {
            public float x, y, z;
            public int sp;
            public float s, rot, tint, hue;
        }

        /// <summary>One rock: ground position (the mesh sits at y - 0.25 s), scale, yaw (JS), brightness v.</summary>
        [Serializable]
        public struct Rock
        {
            public float x, y, z, s, rot, v;
        }

        public Species[] species;
        public Tree[] trees;
        public Rock[] rocks;

        public int SpeciesIndex(string name)
        {
            for (int i = 0; i < species.Length; i++) if (species[i].name == name) return i;
            return -1;
        }

        public int CountSpecies(int sp)
        {
            int c = 0;
            foreach (var t in trees) if (t.sp == sp) c++;
            return c;
        }

        /// <summary>three.js instance tint of a tree: (t, t (0.96 + 0.08 hue), t (0.86 + 0.1 hue)), linear (03 section 4.8).</summary>
        public static Vector3 TintOf(in Tree t) => new Vector3(t.tint, t.tint * (0.96f + t.hue * 0.08f), t.tint * (0.86f + t.hue * 0.1f));
    }
}
