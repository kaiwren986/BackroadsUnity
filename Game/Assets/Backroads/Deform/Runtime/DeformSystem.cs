using System;
using System.Collections.Generic;
using System.Diagnostics;
using Backroads.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Backroads.Deform
{
    /// <summary>
    /// T2.5: the ground deformation of one WorldGrid (PLAN 2.2 "WorldGrid", 2.4 step 6). It implements IStampSink: each
    /// world.js-style call is quantised to a stamp (RutStamp, BrushStamp, RelaxStamp), the stamp is decoded and applied
    /// by the integer kernels (a Burst job run synchronously on the calling thread; UseBurst = false runs the same code
    /// managed), the changed rectangle goes into Dirty (for the terrain chunks and the trees) and the touched tiles get
    /// a new version. Shared code (client and server).
    /// </summary>
    public sealed unsafe class DeformSystem : IStampSink, IDisposable
    {
        public WorldGrid Grid { get; }
        NativeArray<int> _h0Um, _baseUm, _falloff;
        NativeArray<StampResult> _result;
        NativeArray<byte> _ranManaged;

        /// <summary>Rectangles changed since the consumer last drained them (vertex indices, inclusive).</summary>
        public DirtyRectQueue Dirty { get; }
        public TileTable Tiles { get; }
        /// <summary>false: run the kernels as plain managed code (Deform.Deterministic compares both).</summary>
        public bool UseBurst = true;
        /// <summary>When not null, every applied stamp is appended (tests, replays).</summary>
        public List<StampRecord> Recording;

        public long StampsApplied { get; private set; }
        public double TotalStampMs { get; private set; }
        /// <summary>True when the last Burst-path call really ran Burst-compiled code.</summary>
        public bool LastRanBurst { get; private set; }

        readonly Stopwatch _sw = new Stopwatch();

        public DeformSystem(WorldGrid grid)
        {
            Grid = grid ?? throw new ArgumentNullException(nameof(grid));
            const Allocator A = Allocator.Persistent;
            _h0Um = new NativeArray<int>(WorldGrid.Count, A, NativeArrayOptions.UninitializedMemory);
            _baseUm = new NativeArray<int>(WorldGrid.Count, A, NativeArrayOptions.UninitializedMemory);
            new MicrometreJob { Src = grid.H0, Dst = _h0Um }.Schedule(WorldGrid.Count, 4096).Complete();
            new MicrometreJob { Src = grid.Base, Dst = _baseUm }.Schedule(WorldGrid.Count, 4096).Complete();
            _falloff = new NativeArray<int>(StampKernels.BrushFalloffQ20, A);
            _result = new NativeArray<StampResult>(1, A);
            _ranManaged = new NativeArray<byte>(1, A);
            Dirty = new DirtyRectQueue();
            Tiles = new TileTable();
        }

        /// <summary>The raw layer pointers the kernels use (valid while this system and its grid live).</summary>
        public DeformLayers Layers => new DeformLayers
        {
            Delta = (int*)Grid.DeltaUm.GetUnsafePtr(),
            H = (float*)Grid.H.GetUnsafePtr(),
            Wear = (byte*)Grid.Wear.GetUnsafePtr(),
            Surface = (byte*)Grid.Surface.GetUnsafePtr(),
            H0 = (float*)Grid.H0.GetUnsafeReadOnlyPtr(),
            H0Um = (int*)_h0Um.GetUnsafeReadOnlyPtr(),
            BaseUm = (int*)_baseUm.GetUnsafeReadOnlyPtr(),
            BrushFalloff = (int*)_falloff.GetUnsafeReadOnlyPtr(),
        };

        public NativeArray<int> H0Um => _h0Um;
        public NativeArray<int> BaseUm => _baseUm;

        // ------------------------------------------------------------------ IStampSink (JS arguments)

        public void Rut(float x, float z, float radius, float depth, float rate, float wearAdd, float berm = 0.45f, float fx = 0f, float fz = 0f)
            => Apply(StampRecord.Of(RutStamp.FromJs(x, z, radius, depth, rate, wearAdd, berm, fx, fz)));

        public void Brush(float x, float z, float radius, float amount)
            => Apply(StampRecord.Of(BrushStamp.FromJs(x, z, radius, amount)));

        public void Relax(float x, float z, float radius, int iters = 2, float maxDiff = 0.36f)
            => Apply(StampRecord.Of(RelaxStamp.FromJs(x, z, radius, iters, maxDiff)));

        // ------------------------------------------------------------------ stamps

        /// <summary>Applies one stamp (decode, kernel, dirty rectangle, tile versions).</summary>
        public StampResult Apply(in StampRecord stamp)
        {
            _sw.Restart();
            var call = stamp.Decode();
            var layers = Layers;
            StampResult r;
            if (UseBurst)
            {
                new KernelJob { L = layers, Call = call, Out = _result, RanManaged = _ranManaged }.Run();
                r = _result[0];
                LastRanBurst = _ranManaged[0] == 0;
            }
            else
            {
                r = StampKernels.Apply(layers, call);
                LastRanBurst = false;
            }
            Recording?.Add(stamp);
            if (r.Dirty)
            {
                Dirty.Add(r.I0, r.J0, r.I1, r.J1);
                Tiles.Touch(r.I0, r.J0, r.I1, r.J1);
            }
            StampsApplied++;
            TotalStampMs += _sw.Elapsed.TotalMilliseconds;
            return r;
        }

        /// <summary>Applies a stamp log in order.</summary>
        public void Apply(IReadOnlyList<StampRecord> log)
        {
            for (int i = 0; i < log.Count; i++) Apply(log[i]);
        }

        /// <summary>xxHash3 of one 32 x 32-cell tile over DeltaUm, WEAR and SURFACE (06 section 5.6).</summary>
        public ulong TileHash(int tx, int tz) => Tiles.Hash(Grid, tx, tz);

        public void Dispose()
        {
            if (_h0Um.IsCreated) _h0Um.Dispose();
            if (_baseUm.IsCreated) _baseUm.Dispose();
            if (_falloff.IsCreated) _falloff.Dispose();
            if (_result.IsCreated) _result.Dispose();
            if (_ranManaged.IsCreated) _ranManaged.Dispose();
            Dirty.Dispose();
            if (ReferenceEquals(StampSinks.Current, this)) StampSinks.Current = null;
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct KernelJob : IJob
        {
            public DeformLayers L;
            public KernelCall Call;
            public NativeArray<StampResult> Out;
            public NativeArray<byte> RanManaged;

            public void Execute()
            {
                byte managed = 0;
                MarkManaged(ref managed);
                RanManaged[0] = managed;
                Out[0] = StampKernels.Apply(L, Call);
            }

            [BurstDiscard] static void MarkManaged(ref byte m) => m = 1;
        }

        /// <summary>Canon heights rounded once to micrometres (half away from zero), identical on every machine.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct MicrometreJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Src;
            [WriteOnly] public NativeArray<int> Dst;

            public void Execute(int k)
            {
                double v = Src[k] * 1e6;
                Dst[k] = (int)(v >= 0 ? math.floor(v + 0.5) : -math.floor(-v + 0.5));
            }
        }
    }

    /// <summary>
    /// The changed vertex rectangles (PLAN 2.2 DirtyRectQueue): the deformation adds, the client drains once per frame
    /// and hands them to the terrain chunks (partial vertex updates) and the trees (re-snap).
    /// </summary>
    public sealed class DirtyRectQueue : IDisposable
    {
        NativeQueue<int4> _q = new NativeQueue<int4>(Allocator.Persistent);

        public int Count => _q.IsCreated ? _q.Count : 0;

        public void Add(int i0, int j0, int i1, int j1)
        {
            if (_q.IsCreated) _q.Enqueue(new int4(i0, j0, i1, j1));
        }

        public bool TryDequeue(out int4 rect)
        {
            rect = default;
            return _q.IsCreated && _q.TryDequeue(out rect);
        }

        public void Clear() { if (_q.IsCreated) _q.Clear(); }

        public void Dispose() { if (_q.IsCreated) _q.Dispose(); }
    }

    /// <summary>
    /// 64 x 64 tiles of 32 x 32 cells (16 m; PLAN 2.6): a version per tile, bumped by every stamp that touches it, and an
    /// xxHash3 over the tile's DeltaUm, WEAR and SURFACE on demand. Tile (tx, tz) owns vertices i in [32 tx, 32 tx + 31]
    /// (the last tile of a row also owns i = 2048), likewise j.
    /// </summary>
    public sealed class TileTable
    {
        public const int TileCells = 32, TilesPerSide = 64, Count = TilesPerSide * TilesPerSide;
        readonly uint[] _version = new uint[Count];

        public uint Version(int tx, int tz) => _version[tz * TilesPerSide + tx];

        public void Touch(int i0, int j0, int i1, int j1)
        {
            int tx0 = math.clamp(i0 / TileCells, 0, TilesPerSide - 1), tx1 = math.clamp(i1 / TileCells, 0, TilesPerSide - 1);
            int tz0 = math.clamp(j0 / TileCells, 0, TilesPerSide - 1), tz1 = math.clamp(j1 / TileCells, 0, TilesPerSide - 1);
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++) _version[tz * TilesPerSide + tx]++;
        }

        public static void VertexRange(int t, out int v0, out int v1)
        {
            v0 = t * TileCells;
            v1 = t == TilesPerSide - 1 ? WorldGrid.N : v0 + TileCells - 1;
        }

        public unsafe ulong Hash(WorldGrid g, int tx, int tz)
        {
            VertexRange(tx, out int i0, out int i1);
            VertexRange(tz, out int j0, out int j1);
            int w = i1 - i0 + 1, h = j1 - j0 + 1, n = w * h;
            var buf = new NativeArray<byte>(n * 6, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            byte* p = (byte*)buf.GetUnsafePtr();
            int* d = (int*)g.DeltaUm.GetUnsafeReadOnlyPtr();
            byte* wear = (byte*)g.Wear.GetUnsafeReadOnlyPtr(), surf = (byte*)g.Surface.GetUnsafeReadOnlyPtr();
            int o = 0;
            for (int j = j0; j <= j1; j++)
            {
                UnsafeUtility.MemCpy(p + o, d + (j * WorldGrid.VN + i0), w * 4);
                o += w * 4;
            }
            for (int j = j0; j <= j1; j++) { UnsafeUtility.MemCpy(p + o, wear + (j * WorldGrid.VN + i0), w); o += w; }
            for (int j = j0; j <= j1; j++) { UnsafeUtility.MemCpy(p + o, surf + (j * WorldGrid.VN + i0), w); o += w; }
            var hh = xxHash3.Hash64(p, o);
            buf.Dispose();
            return ((ulong)hh.y << 32) | hh.x;
        }

        /// <summary>All 4,096 tile hashes, row by row.</summary>
        public ulong[] HashAll(WorldGrid g)
        {
            var all = new ulong[Count];
            for (int tz = 0; tz < TilesPerSide; tz++)
                for (int tx = 0; tx < TilesPerSide; tx++) all[tz * TilesPerSide + tx] = Hash(g, tx, tz);
            return all;
        }
    }
}
