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
    /// flip at the boundary: HeightAt(p) = HeightAtJs(-p.x, p.z), normals come back as (-nx, ny, nz). The grid's size
    /// comes from the view's Layout (T3.1); the cell is a power of two, so (x + Half) * InvCell equals world.js's
    /// (x + HALF) / CELL bit for bit. For parity tests use WorldGridQueriesRef (double precision, JS arithmetic).
    /// </summary>
    [BurstCompile]
    public static class WorldGridQueries
    {
        // ------------------------------------------------------------------ JS space (world.js lines 68-118)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HeightAtJs(in WorldGridView g, float x, float z)
        {
            float half = g.Layout.Half, inv = g.Layout.InvCell, max = g.Layout.MaxF;
            int vn = g.Layout.VN;
            float fx = (x + half) * inv, fz = (z + half) * inv;
            fx = math.clamp(fx, 0f, max);
            fz = math.clamp(fz, 0f, max);
            int i = (int)fx, j = (int)fz;
            float tx = fx - i, tz = fz - j;
            int k = j * vn + i;
            float a = g.H[k], b = g.H[k + 1], c = g.H[k + vn], d = g.H[k + vn + 1];
            return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
        }

        public static float3 NormalAtJs(in WorldGridView g, float x, float z)
        {
            float e = g.Layout.Cell;
            float nx = HeightAtJs(g, x - e, z) - HeightAtJs(g, x + e, z);
            float nz = HeightAtJs(g, x, z - e) - HeightAtJs(g, x, z + e);
            return math.normalize(new float3(nx, 2f * e, nz));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int NearestKJs(in WorldGridLayout l, float x, float z)
        {
            int i = math.clamp((int)math.floor((x + l.Half) * l.InvCell + 0.5f), 0, l.N);
            int j = math.clamp((int)math.floor((z + l.Half) * l.InvCell + 0.5f), 0, l.N);
            return j * l.VN + i;
        }

        public static byte SurfaceAtJs(in WorldGridView g, float x, float z) => g.Surface[NearestKJs(g.Layout, x, z)];
        public static float MudAtJs(in WorldGridView g, float x, float z) => g.Mud[NearestKJs(g.Layout, x, z)] * (WorldGrid.MudMax / 255f);
        public static float RoadDistAtJs(in WorldGridView g, float x, float z) => g.RoadD[NearestKJs(g.Layout, x, z)] * 0.5f;
        public static byte WearAtJs(in WorldGridView g, float x, float z) => g.Wear[NearestKJs(g.Layout, x, z)];

        public static float SinkAtJs(in WorldGridView g, float x, float z)
        {
            int k = NearestKJs(g.Layout, x, z);
            return g.Base[k] - g.H[k];
        }

        public static bool InBoundsJs(in WorldGridView g, float x, float z) => g.Layout.InBoundsJs(x, z);

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
