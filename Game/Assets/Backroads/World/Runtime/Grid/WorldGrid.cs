using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// The world heightfield and its per-vertex layers (PLAN 2.6), identical on client and server. JS layout
    /// k = j*2049 + i with x = i*0.5 - 512, z = j*0.5 - 512 (JS space); only the query APIs flip to Unity space.
    /// H0 and BASE are canon and never change; DeltaUm (int32 micrometres relative to H0) is the deformation state and
    /// H = H0 + DeltaUm * 1e-6 is derived (where nothing was deformed, H equals canon exactly).
    /// </summary>
    public sealed class WorldGrid : IDisposable
    {
        public const int N = 2048;
        public const int VN = 2049;
        public const int Count = VN * VN;
        public const float Cell = 0.5f;
        public const float Half = 512f;
        public const int TintN = 129;
        public const float MudMax = 1f;

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
        /// <summary>129 x 129 x 2 (tb, td) colour-variation grid at 8 m.</summary>
        public NativeArray<float> Tint;

        public bool IsCreated => H.IsCreated;

        public static int Index(int i, int j) => j * VN + i;

        /// <summary>Copies the canon layers into persistent arrays; DeltaUm, WET, COMPACT and NODEFORM start at zero and H = H0.</summary>
        public static WorldGrid FromLayers(NativeArray<float> heights, NativeArray<float> baseHeights, NativeArray<byte> surface,
                                           NativeArray<byte> wear, NativeArray<byte> roadd, NativeArray<byte> mud, NativeArray<float> tint)
        {
            Check(heights.Length, Count, "heights");
            Check(baseHeights.Length, Count, "base");
            Check(surface.Length, Count, "surface");
            Check(wear.Length, Count, "wear");
            Check(roadd.Length, Count, "roadd");
            Check(mud.Length, Count, "mud");
            Check(tint.Length, TintN * TintN * 2, "tint");
            var g = new WorldGrid();
            const Allocator A = Allocator.Persistent;
            g.H0 = new NativeArray<float>(heights, A);
            g.Base = new NativeArray<float>(baseHeights, A);
            g.H = new NativeArray<float>(heights, A);
            g.DeltaUm = new NativeArray<int>(Count, A, NativeArrayOptions.ClearMemory);
            g.Surface = new NativeArray<byte>(surface, A);
            g.Wear = new NativeArray<byte>(wear, A);
            g.RoadD = new NativeArray<byte>(roadd, A);
            g.Mud = new NativeArray<byte>(mud, A);
            g.Wet = new NativeArray<byte>(Count, A, NativeArrayOptions.ClearMemory);
            g.Compact = new NativeArray<byte>(Count, A, NativeArrayOptions.ClearMemory);
            g.NoDeform = new NativeArray<byte>(Count, A, NativeArrayOptions.ClearMemory);
            g.Tint = new NativeArray<float>(tint, A);
            return g;
        }

        /// <summary>From the imported canon: TextAsset.GetData views of the .bytes layers, copied once.</summary>
        public static WorldGrid FromCanon(CanonAsset c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            return FromLayers(c.heights.data.GetData<float>(), c.@base.data.GetData<float>(), c.surface.data.GetData<byte>(),
                              c.wear.data.GetData<byte>(), c.roadd.data.GetData<byte>(), c.mud.data.GetData<byte>(), c.tint.data.GetData<float>());
        }

        /// <summary>From raw little-endian files (CanonData/v1/terrain/*), for tools and tests.</summary>
        public static WorldGrid FromBytes(byte[] heights, byte[] baseHeights, byte[] surface, byte[] wear, byte[] roadd, byte[] mud, byte[] tint)
        {
            using var h = Reinterpret<float>(heights);
            using var b = Reinterpret<float>(baseHeights);
            using var s = new NativeArray<byte>(surface, Allocator.Temp);
            using var w = new NativeArray<byte>(wear, Allocator.Temp);
            using var r = new NativeArray<byte>(roadd, Allocator.Temp);
            using var m = new NativeArray<byte>(mud, Allocator.Temp);
            using var t = Reinterpret<float>(tint);
            return FromLayers(h, b, s, w, r, m, t);
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
            if (got != want) throw new ArgumentException($"canon layer {what}: {got} elements, expected {want}");
        }

        /// <summary>Read-only view for queries and jobs.</summary>
        public WorldGridView View => new WorldGridView { H = H, Base = Base, Surface = Surface, Wear = Wear, Mud = Mud, RoadD = RoadD, Tint = Tint };

        /// <summary>Recomputes H = H0 + DeltaUm * 1e-6 for the vertex rectangle [i0..i1] x [j0..j1] (inclusive), FloatMode.Strict.</summary>
        public void RebuildH(int i0, int j0, int i1, int j1)
        {
            i0 = Mathf.Clamp(i0, 0, N); i1 = Mathf.Clamp(i1, 0, N);
            j0 = Mathf.Clamp(j0, 0, N); j1 = Mathf.Clamp(j1, 0, N);
            if (i0 > i1 || j0 > j1) return;
            new RebuildHJob { H0 = H0, DeltaUm = DeltaUm, H = H, I0 = i0, I1 = i1, J0 = j0 }
                .Schedule(j1 - j0 + 1, 16).Complete();
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct RebuildHJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> H0;
            [ReadOnly] public NativeArray<int> DeltaUm;
            [NativeDisableParallelForRestriction] public NativeArray<float> H;
            public int I0, I1, J0;

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

    /// <summary>Read-only layer view passed to WorldGridQueries / WorldGridQueriesRef and to jobs.</summary>
    public struct WorldGridView
    {
        [ReadOnly] public NativeArray<float> H;
        [ReadOnly] public NativeArray<float> Base;
        [ReadOnly] public NativeArray<byte> Surface;
        [ReadOnly] public NativeArray<byte> Wear;
        [ReadOnly] public NativeArray<byte> Mud;
        [ReadOnly] public NativeArray<byte> RoadD;
        [ReadOnly] public NativeArray<float> Tint;
    }
}
