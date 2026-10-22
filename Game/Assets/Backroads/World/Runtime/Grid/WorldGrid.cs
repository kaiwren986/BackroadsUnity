using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// The world heightfield and its per-vertex layers (PLAN 2.6), identical on client and server. Its size comes from
    /// the world's manifest (Layout, T3.1): the canon test world is 2,049 x 2,049 at 0.5 m over 1,024 m, the new
    /// Millbrook 4,097 x 4,097 over 2,048 m. JS layout k = j*VN + i with x = i*Cell - Half, z = j*Cell - Half (JS space);
    /// only the query APIs flip to Unity space. H0 and BASE are the source layers and never change; DeltaUm (int32
    /// micrometres relative to H0) is the deformation state and H = H0 + DeltaUm * 1e-6 is derived (where nothing was
    /// deformed, H equals the source exactly).
    /// </summary>
    public sealed class WorldGrid : IDisposable
    {
        // The canon test world's size as compile-time constants, kept only so code outside lane W that reads CanonData v1
        // directly (lane R's canon map, lane V's JS rut mirror) still compiles. Everything that runs on a WorldGrid reads
        // grid.Layout or the view's Layout.
        const string V1Only = "the canon test world's (v1) size only: read grid.Layout / view.Layout, or WorldGridLayout.CanonV1 (T3.1)";
        [Obsolete(V1Only)] public const int N = 2048;
        [Obsolete(V1Only)] public const int VN = 2049;
        [Obsolete(V1Only)] public const int Count = 2049 * 2049;
        [Obsolete(V1Only)] public const float Cell = 0.5f;
        [Obsolete(V1Only)] public const float Half = 512f;
        [Obsolete(V1Only)] public const int TintN = 129;
        public const float MudMax = 1f;

        /// <summary>This grid's size (from the world's manifest).</summary>
        public readonly WorldGridLayout Layout;

        public NativeArray<float> H0;
        public NativeArray<float> Base;
        public NativeArray<int> DeltaUm;
        public NativeArray<float> H;
        public NativeArray<byte> Surface;
        public NativeArray<byte> Wear;
        public NativeArray<byte> Mud;
        public NativeArray<byte> Wet;
        public NativeArray<byte> Compact;
        public NativeArray<byte> NoDeform;
        public NativeArray<byte> RoadD;
        /// <summary>TintN x TintN x 2 (tb, td) colour-variation grid at TintCell (8 m).</summary>
        public NativeArray<float> Tint;

        public bool IsCreated => H.IsCreated;

        WorldGrid(WorldGridLayout layout)
        {
            if (!layout.IsValid) throw new ArgumentException("invalid WorldGridLayout");
            Layout = layout;
        }

        public int Index(int i, int j) => j * Layout.VN + i;

        /// <summary>Copies the source layers into persistent arrays; DeltaUm, WET, COMPACT and NODEFORM start at zero and H = H0.</summary>
        public static WorldGrid FromLayers(WorldGridLayout layout, NativeArray<float> heights, NativeArray<float> baseHeights,
                                           NativeArray<byte> surface, NativeArray<byte> wear, NativeArray<byte> roadd,
                                           NativeArray<byte> mud, NativeArray<float> tint)
        {
            int count = layout.Count;
            Check(heights.Length, count, "heights");
            Check(baseHeights.Length, count, "base");
            Check(surface.Length, count, "surface");
            Check(wear.Length, count, "wear");
            Check(roadd.Length, count, "roadd");
            Check(mud.Length, count, "mud");
            Check(tint.Length, layout.TintN * layout.TintN * 2, "tint");
            var g = new WorldGrid(layout);
            const Allocator A = Allocator.Persistent;
            g.H0 = new NativeArray<float>(heights, A);
            g.Base = new NativeArray<float>(baseHeights, A);
            g.H = new NativeArray<float>(heights, A);
            g.DeltaUm = new NativeArray<int>(count, A, NativeArrayOptions.ClearMemory);
            g.Surface = new NativeArray<byte>(surface, A);
            g.Wear = new NativeArray<byte>(wear, A);
            g.RoadD = new NativeArray<byte>(roadd, A);
            g.Mud = new NativeArray<byte>(mud, A);
            g.Wet = new NativeArray<byte>(count, A, NativeArrayOptions.ClearMemory);
            g.Compact = new NativeArray<byte>(count, A, NativeArrayOptions.ClearMemory);
            g.NoDeform = new NativeArray<byte>(count, A, NativeArrayOptions.ClearMemory);
            g.Tint = new NativeArray<float>(tint, A);
            return g;
        }

        /// <summary>From the imported canon: TextAsset.GetData views of the .bytes layers, copied once; the layout is the canon's grid.</summary>
        public static WorldGrid FromCanon(CanonAsset c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            return FromLayers(LayoutOf(c), c.heights.data.GetData<float>(), c.@base.data.GetData<float>(), c.surface.data.GetData<byte>(),
                              c.wear.data.GetData<byte>(), c.roadd.data.GetData<byte>(), c.mud.data.GetData<byte>(), c.tint.data.GetData<float>());
        }

        /// <summary>
        /// The canon's grid layout: its world info's grid block, imported from CanonData/v1 (the same block as
        /// manifest.json's "grid"; World.LayoutFromManifest checks they agree).
        /// </summary>
        public static WorldGridLayout LayoutOf(CanonAsset c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (c.world == null || c.world.vn <= 0) throw new InvalidOperationException("the imported canon has no grid block (re-import canon v1)");
            return WorldGridLayout.FromWorldInfo(c.world);
        }

        /// <summary>From raw little-endian files (CanonData/v1/terrain/* or a WorldData export), for tools and tests.</summary>
        public static WorldGrid FromBytes(WorldGridLayout layout, byte[] heights, byte[] baseHeights, byte[] surface, byte[] wear,
                                          byte[] roadd, byte[] mud, byte[] tint)
        {
            using var h = Reinterpret<float>(heights);
            using var b = Reinterpret<float>(baseHeights);
            using var s = new NativeArray<byte>(surface, Allocator.Temp);
            using var w = new NativeArray<byte>(wear, Allocator.Temp);
            using var r = new NativeArray<byte>(roadd, Allocator.Temp);
            using var m = new NativeArray<byte>(mud, Allocator.Temp);
            using var t = Reinterpret<float>(tint);
            return FromLayers(layout, h, b, s, w, r, m, t);
        }

        /// <summary>
        /// A synthetic grid (tests, test tracks): heights and surfaces from functions of the JS-space vertex position
        /// (x = i*Cell - Half, z = j*Cell - Half); BASE = heights, WEAR, ROADD and MUD zero, tint zero.
        /// </summary>
        public static WorldGrid FromFunctions(WorldGridLayout layout, Func<float, float, float> heightJs, Func<float, float, byte> surfaceJs = null)
        {
            if (heightJs == null) throw new ArgumentNullException(nameof(heightJs));
            int vn = layout.VN, count = layout.Count;
            var h = new NativeArray<float>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var s = new NativeArray<byte>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var zero = new NativeArray<byte>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var tint = new NativeArray<float>(layout.TintN * layout.TintN * 2, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            try
            {
                for (int j = 0; j < vn; j++)
                {
                    float z = layout.X(j);
                    for (int i = 0; i < vn; i++)
                    {
                        float x = layout.X(i);
                        int k = j * vn + i;
                        h[k] = heightJs(x, z);
                        if (surfaceJs != null) s[k] = surfaceJs(x, z);
                    }
                }
                return FromLayers(layout, h, h, s, zero, zero, zero, tint);
            }
            finally
            {
                h.Dispose(); s.Dispose(); zero.Dispose(); tint.Dispose();
            }
        }

        static NativeArray<T> Reinterpret<T>(byte[] bytes) where T : struct
        {
            var tmp = new NativeArray<byte>(bytes, Allocator.Temp);
            int size = UnsafeUtility.SizeOf<T>();
            var outArr = new NativeArray<T>(bytes.Length / size, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<byte>.Copy(tmp, outArr.Reinterpret<byte>(size), bytes.Length);
            tmp.Dispose();
            return outArr;
        }

        static void Check(int got, int want, string what)
        {
            if (got != want) throw new ArgumentException($"grid layer {what}: {got} elements, expected {want}");
        }

        /// <summary>Read-only view for queries and jobs (it carries the layout).</summary>
        public WorldGridView View => new WorldGridView { Layout = Layout, H = H, Base = Base, Surface = Surface, Wear = Wear, Mud = Mud, RoadD = RoadD, Tint = Tint };

        /// <summary>Recomputes H = H0 + DeltaUm * 1e-6 for the vertex rectangle [i0..i1] x [j0..j1] (inclusive), FloatMode.Strict.</summary>
        public void RebuildH(int i0, int j0, int i1, int j1)
        {
            int n = Layout.N;
            i0 = Mathf.Clamp(i0, 0, n); i1 = Mathf.Clamp(i1, 0, n);
            j0 = Mathf.Clamp(j0, 0, n); j1 = Mathf.Clamp(j1, 0, n);
            if (i0 > i1 || j0 > j1) return;
            new RebuildHJob { H0 = H0, DeltaUm = DeltaUm, H = H, VN = Layout.VN, I0 = i0, I1 = i1, J0 = j0 }
                .Schedule(j1 - j0 + 1, 16).Complete();
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct RebuildHJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> H0;
            [ReadOnly] public NativeArray<int> DeltaUm;
            [NativeDisableParallelForRestriction] public NativeArray<float> H;
            public int VN, I0, I1, J0;

            public void Execute(int row)
            {
                int j = J0 + row;
                for (int i = I0; i <= I1; i++)
                {
                    int k = j * VN + i;
                    int d = DeltaUm[k];
                    H[k] = d == 0 ? H0[k] : H0[k] + (float)(d * 1e-6);
                }
            }
        }

        public void Dispose()
        {
            if (H0.IsCreated) H0.Dispose();
            if (Base.IsCreated) Base.Dispose();
            if (DeltaUm.IsCreated) DeltaUm.Dispose();
            if (H.IsCreated) H.Dispose();
            if (Surface.IsCreated) Surface.Dispose();
            if (Wear.IsCreated) Wear.Dispose();
            if (Mud.IsCreated) Mud.Dispose();
            if (Wet.IsCreated) Wet.Dispose();
            if (Compact.IsCreated) Compact.Dispose();
            if (NoDeform.IsCreated) NoDeform.Dispose();
            if (RoadD.IsCreated) RoadD.Dispose();
            if (Tint.IsCreated) Tint.Dispose();
        }
    }

    /// <summary>Read-only layer view passed to WorldGridQueries / WorldGridQueriesRef and to jobs; it carries the layout.</summary>
    public struct WorldGridView
    {
        public WorldGridLayout Layout;
        [ReadOnly] public NativeArray<float> H;
        [ReadOnly] public NativeArray<float> Base;
        [ReadOnly] public NativeArray<byte> Surface;
        [ReadOnly] public NativeArray<byte> Wear;
        [ReadOnly] public NativeArray<byte> Mud;
        [ReadOnly] public NativeArray<byte> RoadD;
        [ReadOnly] public NativeArray<float> Tint;
    }
}
