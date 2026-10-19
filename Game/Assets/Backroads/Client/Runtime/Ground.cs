using Backroads.World;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Client
{
    /// <summary>WorldGrid queries in Unity space with Vector3 (for code without Unity.Mathematics); 0 when canon is not loaded.</summary>
    public static class Ground
    {
        public static bool Ready => CanonRuntime.IsLoaded;

        public static float HeightAt(Vector3 p) => Ready ? WorldGridQueries.HeightAt(CanonRuntime.Grid.View, new float3(p.x, p.y, p.z)) : 0f;

        public static Vector3 NormalAt(Vector3 p)
        {
            if (!Ready) return Vector3.up;
            var n = WorldGridQueries.NormalAt(CanonRuntime.Grid.View, new float3(p.x, p.y, p.z));
            return new Vector3(n.x, n.y, n.z);
        }

        public static byte SurfaceAt(Vector3 p) => Ready ? WorldGridQueries.SurfaceAt(CanonRuntime.Grid.View, new float3(p.x, p.y, p.z)) : (byte)0;
        public static float MudAt(Vector3 p) => Ready ? WorldGridQueries.MudAt(CanonRuntime.Grid.View, new float3(p.x, p.y, p.z)) : 0f;
        /// <summary>How far the ground was pushed below BASE here (world.js sinkAt), metres.</summary>
        public static float SinkAt(Vector3 p) => Ready ? WorldGridQueries.SinkAt(CanonRuntime.Grid.View, new float3(p.x, p.y, p.z)) : 0f;
    }
}
