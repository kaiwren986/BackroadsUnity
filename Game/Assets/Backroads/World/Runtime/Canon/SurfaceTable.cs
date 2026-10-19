using UnityEngine;

namespace Backroads.World
{
    /// <summary>world.json surfaceTable (world.js lines 13-23): per-surface constants, exact float32 values.</summary>
    public sealed class SurfaceTable : ScriptableObject
    {
        public const byte Grass = 0, Dirt = 1, Sand = 2, Rock = 3, Road = 4, Gravel = 5, Concrete = 6, Track = 7, Mud = 8;
        public const int Count = 9;

        public string[] names;
        public string[] displayNames;
        public float[] softness;
        public float[] grip;
        public float[] digRate;
        public float[] roughness;
        /// <summary>Dust colour per surface (RGB, as in world.js DUST).</summary>
        public Color[] dust;
        public float mudMax = 1f;

        /// <summary>ROAD and CONCRETE never deform (03 section 4.4).</summary>
        public static bool IsLocked(byte s) => s == Road || s == Concrete;
    }
}
