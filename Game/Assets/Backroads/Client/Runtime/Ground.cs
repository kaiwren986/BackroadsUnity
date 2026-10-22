using Backroads.World;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Client
{
    /// <summary>
    /// WorldGrid queries in Unity space with Vector3 (for code without Unity.Mathematics) on the active world (T3.3:
    /// ActiveWorld, the canon when only it is loaded); 0 when no world is loaded.
    /// </summary>
    public static class Ground
    {
        public static bool Ready => ActiveWorld.TryGrid(out _);

        static bool View(out WorldGridView v)
        {
            if (ActiveWorld.TryGrid(out var g)) { v = g.View; return true; }
            v = default;
            return false;
        }

        public static float HeightAt(Vector3 p) => View(out var v) ? WorldGridQueries.HeightAt(v, new float3(p.x, p.y, p.z)) : 0f;

        public static Vector3 NormalAt(Vector3 p)
        {
            if (!View(out var v)) return Vector3.up;
            var n = WorldGridQueries.NormalAt(v, new float3(p.x, p.y, p.z));
            return new Vector3(n.x, n.y, n.z);
        }

        public static byte SurfaceAt(Vector3 p) => View(out var v) ? WorldGridQueries.SurfaceAt(v, new float3(p.x, p.y, p.z)) : (byte)0;
        public static float MudAt(Vector3 p) => View(out var v) ? WorldGridQueries.MudAt(v, new float3(p.x, p.y, p.z)) : 0f;
        /// <summary>How far the ground was pushed below BASE here (world.js sinkAt), metres.</summary>
        public static float SinkAt(Vector3 p) => View(out var v) ? WorldGridQueries.SinkAt(v, new float3(p.x, p.y, p.z)) : 0f;
    }
}
