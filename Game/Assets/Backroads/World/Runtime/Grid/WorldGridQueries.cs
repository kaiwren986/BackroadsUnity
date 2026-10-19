using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Backroads.World
{
    /// <summary>
    /// Float fast path of the world queries (PLAN 1.5, 03 section 4.1), Burst-compatible, for physics, cameras and
    /// placement. The *Js methods take JS-space coordinates and mirror world.js; the plain methods take Unity space and
    /// flip at the boundary: HeightAt(p) = HeightAtJs(-p.x, p.z), normals come back as (-nx, ny, nz).
    /// For parity tests use WorldGridQueriesRef (double precision, JS arithmetic).
    /// </summary>
    [BurstCompile]
    public static class WorldGridQueries
    {
        const int N = WorldGrid.N, VN = WorldGrid.VN;
        const float Half = WorldGrid.Half, Cell = WorldGrid.Cell;

        // ------------------------------------------------------------------ JS space (world.js lines 68-118)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HeightAtJs(in WorldGridView g, float x, float z)
        {
            float fx = (x + Half) / Cell, fz = (z + Half) / Cell;
            fx = math.clamp(fx, 0f, N - 1e-3f);
            fz = math.clamp(fz, 0f, N - 1e-3f);
            int i = (int)fx, j = (int)fz;
            float tx = fx - i, tz = fz - j;
            int k = j * VN + i;
            float a = g.H[k], b = g.H[k + 1], c = g.H[k + VN], d = g.H[k + VN + 1];
            return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
        }

        public static float3 NormalAtJs(in WorldGridView g, float x, float z)
        {
            const float e = Cell;
            float nx = HeightAtJs(g, x - e, z) - HeightAtJs(g, x + e, z);
            float nz = HeightAtJs(g, x, z - e) - HeightAtJs(g, x, z + e);
            return math.normalize(new float3(nx, 2f * e, nz));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int NearestKJs(float x, float z)
        {
            int i = math.clamp((int)math.floor((x + Half) / Cell + 0.5f), 0, N);
            int j = math.clamp((int)math.floor((z + Half) / Cell + 0.5f), 0, N);
            return j * VN + i;
        }

        public static byte SurfaceAtJs(in WorldGridView g, float x, float z) => g.Surface[NearestKJs(x, z)];
        public static float MudAtJs(in WorldGridView g, float x, float z) => g.Mud[NearestKJs(x, z)] * (WorldGrid.MudMax / 255f);
        public static float RoadDistAtJs(in WorldGridView g, float x, float z) => g.RoadD[NearestKJs(x, z)] * 0.5f;
        public static byte WearAtJs(in WorldGridView g, float x, float z) => g.Wear[NearestKJs(x, z)];

        public static float SinkAtJs(in WorldGridView g, float x, float z)
        {
            int k = NearestKJs(x, z);
            return g.Base[k] - g.H[k];
        }

        public static bool InBoundsJs(float x, float z) => x > -Half + 2 && x < Half - 2 && z > -Half + 2 && z < Half - 2;

        /// <summary>Ray march against the heightfield: hit distance, 0 when the origin is underground, -1 for a miss.</summary>
        public static float RayTerrainJs(in WorldGridView g, float3 o, float3 d, float maxT, float step = 0.5f)
        {
            if (o.y - HeightAtJs(g, o.x, o.z) <= 0f) return 0f;
            float tPrev = 0f;
            for (float t = math.min(step, maxT); ; t = math.min(t + step, maxT))
            {
                float3 p = o + d * t;
                if (p.y - HeightAtJs(g, p.x, p.z) <= 0f)
                {
                    float a = tPrev, b = t;
                    for (int k = 0; k < 7; k++)
                    {
                        float m = (a + b) * 0.5f;
                        float3 q = o + d * m;
                        if (q.y - HeightAtJs(g, q.x, q.z) > 0f) a = m; else b = m;
                    }
                    return (a + b) * 0.5f;
                }
                if (t >= maxT) return -1f;
                tPrev = t;
            }
        }

        // ------------------------------------------------------------------ Unity space

        public static float HeightAt(in WorldGridView g, float3 p) => HeightAtJs(g, -p.x, p.z);

        public static float3 NormalAt(in WorldGridView g, float3 p)
        {
            var n = NormalAtJs(g, -p.x, p.z);
            return new float3(-n.x, n.y, n.z);
        }

        public static byte SurfaceAt(in WorldGridView g, float3 p) => SurfaceAtJs(g, -p.x, p.z);
        public static float MudAt(in WorldGridView g, float3 p) => MudAtJs(g, -p.x, p.z);
        public static float SinkAt(in WorldGridView g, float3 p) => SinkAtJs(g, -p.x, p.z);
        public static float RoadDistAt(in WorldGridView g, float3 p) => RoadDistAtJs(g, -p.x, p.z);

        public static float RayTerrain(in WorldGridView g, float3 origin, float3 dir, float maxT, float step = 0.5f) =>
            RayTerrainJs(g, new float3(-origin.x, origin.y, origin.z), new float3(-dir.x, dir.y, dir.z), maxT, step);
    }

    /// <summary>Burst batch of height + normal + surface queries (Unity space), e.g. wheel probes or tests.</summary>
    [BurstCompile]
    public struct HeightQueryJob : IJobParallelFor
    {
        public WorldGridView Grid;
        [ReadOnly] public NativeArray<float3> Points;
        public NativeArray<float> Heights;
        public NativeArray<float3> Normals;
        public NativeArray<byte> Surfaces;

        public void Execute(int i)
        {
            var p = Points[i];
            Heights[i] = WorldGridQueries.HeightAt(Grid, p);
            Normals[i] = WorldGridQueries.NormalAt(Grid, p);
            Surfaces[i] = WorldGridQueries.SurfaceAt(Grid, p);
        }
    }

    /// <summary>Burst batch of terrain ray casts (Unity space).</summary>
    [BurstCompile]
    public struct RayQueryJob : IJobParallelFor
    {
        public WorldGridView Grid;
        [ReadOnly] public NativeArray<float3> Origins;
        [ReadOnly] public NativeArray<float3> Dirs;
        public float MaxT, Step;
        public NativeArray<float> Hits;

        public void Execute(int i) => Hits[i] = WorldGridQueries.RayTerrain(Grid, Origins[i], Dirs[i], MaxT, Step);
    }
}
