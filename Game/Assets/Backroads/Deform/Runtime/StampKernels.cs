using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Backroads.Deform
{
    /// <summary>
    /// Raw views of the WorldGrid layers the kernels read and write (PLAN 2.6). Delta is DeltaUm (int32 micrometres
    /// relative to H0, the authoritative state); H is the derived float32 height (H0 + Delta * 1e-6) that wheels and the
    /// terrain read; H0Um and BaseUm are the canon heights H0 and BASE rounded once to micrometres (DeformGrid).
    /// N, VN, CellShift and OriginQ20 are the grid's size (WorldGridLayout, T3.1).
    /// </summary>
    public unsafe struct DeformLayers
    {
        /// <summary>Cells and vertices per side; Q20 units per cell are 2^CellShift; OriginQ20 = the grid's half size in Q20.</summary>
        public int N, VN, CellShift;
        public long OriginQ20;
        [NativeDisableUnsafePtrRestriction] public int* Delta;
        [NativeDisableUnsafePtrRestriction] public float* H;
        [NativeDisableUnsafePtrRestriction] public byte* Wear;
        [NativeDisableUnsafePtrRestriction] public byte* Surface;
        [NativeDisableUnsafePtrRestriction] public float* H0;
        [NativeDisableUnsafePtrRestriction] public int* H0Um;
        [NativeDisableUnsafePtrRestriction] public int* BaseUm;
        /// <summary>StampKernels.BrushFalloffQ20 (4,097 entries), as a pointer so Burst never touches a managed array.</summary>
        [NativeDisableUnsafePtrRestriction] public int* BrushFalloff;
    }

    /// <summary>What one kernel call did: the world.js markDirty rectangle (vertex indices, inclusive) and its result.</summary>
    public struct StampResult
    {
        public int I0, J0, I1, J1;
        public bool Dirty;
        /// <summary>rut: removed height sum in micrometres (JS return value x 1e6); brush: moved volume in um x cells.</summary>
        public long ValueUm;
        /// <summary>Vertices whose Delta changed.</summary>
        public int Changed;
    }

    /// <summary>
    /// A decoded stamp: the integer arguments of one kernel call (StampRecord.Decode). Burst jobs take this, so the
    /// stamp decode (integer CORDIC included) stays in one place.
    /// </summary>
    public struct KernelCall
    {
        public StampKind Kind;
        /// <summary>Q20 m (JS x, z) and Q20 m radius.</summary>
        public long X, Z, Radius;
        /// <summary>rut: depth; brush: signed amount; relax: maxDiff (all micrometres).</summary>
        public long A;
        /// <summary>rut: rate (micrometres per call).</summary>
        public long B;
        /// <summary>rut: berm in 1/200, wear in 1/4; relax: iterations.</summary>
        public int Berm200, Wear4, Iters;
        /// <summary>rut: rolling direction in Q30 (0, 0 = none).</summary>
        public long Fx, Fz;
    }

    /// <summary>
    /// T2.5 integer kernels (06 section 5.6, PLAN 2.6 [T2]): world.js rut / brush / relax rewritten in fixed point on
    /// DeltaUm, so every machine that applies the same stamps gets the same integers (no dependence on float modes).
    /// Units: horizontal Q20 metres from the grid origin (JS x = -Half is 0, -512 m on the canon test world; one 0.5 m
    /// cell is 2^19; the size comes from DeformLayers, T3.1), heights in
    /// micrometres, falloffs and weights in Q20 / Q16. Rounding: every height increment is rounded half away from zero
    /// once per vertex and call; the berm spoil of a call is split over the ring by largest remainder (ties by ring
    /// order), so no micrometre is created or lost. The derived H is rewritten for each changed vertex with the same
    /// expression as WorldGrid.RebuildH. Locked surfaces (ROAD, CONCRETE) are never deformed (03 section 4.4).
    /// </summary>
    public static unsafe class StampKernels
    {
        public const byte GRASS = 0, DIRT = 1, ROAD = 4, CONCRETE = 6, TRACK = 7;

        /// <summary>world.js SOFTNESS (float32 values) in Q16, rounded: 0.5, 1, 0.85, 0, 0, 0.16, 0, 0.3, 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SoftQ16(byte s)
        {
            switch (s)
            {
                case 0: return 32768; case 1: return 65536; case 2: return 55706; case 5: return 10486;
                case 7: return 19661; case 8: return 65536; default: return 0;
            }
        }

        /// <summary>world.js DIG_RATE (float32 values) in Q16, rounded: 1, 1, 1.2, 0.3, 0, 0.8, 0, 0.9, 1.4.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DigQ16(byte s)
        {
            switch (s)
            {
                case 0: return 65536; case 1: return 65536; case 2: return 78643; case 3: return 19661;
                case 5: return 52429; case 7: return 58982; case 8: return 91750; default: return 0;
            }
        }

        /// <summary>
        /// The Q12 falloff table: brush's smoothstep(1 - d/r) indexed by u = (d/r)^2 in steps of 1/4096 (so no square
        /// root per vertex), values in Q20, built with integer arithmetic only.
        /// </summary>
        public static readonly int[] BrushFalloffQ20 = BuildBrushFalloff();

        static int[] BuildBrushFalloff()
        {
            var t = new int[4097];
            for (int n = 0; n <= 4096; n++)
            {
                long s24 = ISqrt((long)n << 36);                 // sqrt(n / 4096) in Q24
                long t24 = (1L << 24) - s24;                      // 1 - d/r
                long t2 = (t24 * t24 + (1L << 23)) >> 24;
                long f24 = (t2 * (3L * (1 << 24) - 2 * t24) + (1L << 23)) >> 24;
                t[n] = (int)((f24 + 8) >> 4);                     // Q20
            }
            return t;
        }

        /// <summary>floor(sqrt(v)) for v >= 0, exact (the double estimate is corrected in integers).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ISqrt(long v)
        {
            if (v <= 0) return 0;
            long r = (long)Math.Sqrt(v);
            while (r * r > v) r--;
            while ((r + 1) * (r + 1) <= v) r++;
            return r;
        }

        /// <summary>Rounds n / d (d > 0) half away from zero.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long DivRound(long n, long d) => n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d);

        /// <summary>floor(n / d) for d > 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long FloorDiv(long n, long d) => n >= 0 ? n / d : -((-n + d - 1) / d);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long CeilDiv(long n, long d) => -FloorDiv(-n, d);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsLocked(byte s) => s == ROAD || s == CONCRETE;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SetH(in DeformLayers L, int k)
        {
            int d = L.Delta[k];
            L.H[k] = d == 0 ? L.H0[k] : L.H0[k] + (float)(d * 1e-6);
        }

        /// <summary>Runs one decoded call.</summary>
        public static StampResult Apply(in DeformLayers L, in KernelCall c)
        {
            switch (c.Kind)
            {
                case StampKind.Rut: return Rut(L, c.X, c.Z, c.Radius, c.A, c.B, c.Berm200, c.Wear4, c.Fx, c.Fz);
                case StampKind.Brush: return Brush(L, c.X, c.Z, c.Radius, c.A);
                case StampKind.Relax: return Relax(L, c.X, c.Z, c.Radius, c.Iters, c.A);
                default: return default;
            }
        }

        /// <summary>
        /// world.js:137 rut on a decoded stamp. x, z, radius in Q20; depth and rate in micrometres; berm in 1/200;
        /// wearAdd in quarters; (fx, fz) the rolling direction in Q30 (both 0: no direction).
        /// </summary>
        public static StampResult Rut(in DeformLayers L, long x, long z, long radius, long depthUm, long rateUm,
                                      int berm200, int wear4, long fx, long fz)
        {
            var res = new StampResult();
            if (radius <= 0) return res;
            int N = L.N, VN = L.VN, CellShift = L.CellShift;
            long gx = x + L.OriginQ20, gz = z + L.OriginQ20;
            const long Ring = 11L << 20;                          // 0.55 m = 11/20 m: compared as 20 * d
            long outer20 = 20 * radius + Ring;                    // 20 * (radius + 0.55 m)
            long outer = (outer20 + 19) / 20;                     // >= radius + 0.55 m, for the box
            int i0 = (int)Math.Max(1, FloorDiv(gx - outer, 1L << CellShift)), i1 = (int)Math.Min(N - 1, CeilDiv(gx + outer, 1L << CellShift));
            int j0 = (int)Math.Max(1, FloorDiv(gz - outer, 1L << CellShift)), j1 = (int)Math.Min(N - 1, CeilDiv(gz + outer, 1L << CellShift));
            if (i0 > i1 || j0 > j1) return res;
            long r2 = radius * radius;
            long outer2x400 = outer20 * outer20;
            bool hasDir = fx != 0 || fz != 0;
            int* ring = stackalloc int[256];
            int* ringW = stackalloc int[256];
            int ringN = 0;
            long ringSum = 0, removed = 0;
            bool changed = false, touched = false;
            for (int j = j0; j <= j1; j++)
            {
                long dz = ((long)j << CellShift) - gz;
                for (int i = i0; i <= i1; i++)
                {
                    long dx = ((long)i << CellShift) - gx;
                    long d2 = dx * dx + dz * dz;
                    if (400 * d2 > outer2x400) continue;
                    int k = j * VN + i;
                    byte s = L.Surface[k];
                    int soft = SoftQ16(s);
                    if (soft <= 0) continue;
                    touched = true;
                    if (d2 < r2)
                    {
                        long f = ((r2 - d2) << 20) / r2;                       // 1 - (d/r)^2, Q20 (floor)
                        long target = (long)L.BaseUm[k] - L.H0Um[k] - ((depthUm * soft * f + (1L << 35)) >> 36);
                        long cur = L.Delta[k];
                        if (cur > target)
                        {
                            long step = (rateUm * f * (65536 + soft) + (1L << 36)) >> 37; // rate f (0.5 + 0.5 soft)
                            long dh = Math.Min(cur - target, step);
                            if (dh != 0) { L.Delta[k] = (int)(cur - dh); SetH(L, k); res.Changed++; }
                            removed += dh; changed = true;
                        }
                        // WEAR += wearAdd f (0.4 + soft), truncated like a Uint8Array store
                        long inc = wear4 * f * (131072 + 5L * soft) / (20L << 36);
                        long w = L.Wear[k] + inc;
                        L.Wear[k] = (byte)(w > 255 ? 255 : w);
                        if (s == GRASS && L.Wear[k] > 170) L.Surface[k] = DIRT;
                    }
                    else if (ringN < 256)
                    {
                        long d = ISqrt(d2);
                        if (hasDir)
                        {
                            // dx fx + dz fz > 0.35 d  (Q50 against Q20 x Q30)
                            long dot = (dx * fx + dz * fz) >> 10;               // Q40
                            if (20 * dot > 7 * (d << 20)) continue;
                        }
                        // w = 1 - (d - r) / 0.55 m, Q16
                        long wq = 65536 - DivRound(5 * (d - radius), 44);
                        if (wq < 0) wq = 0; else if (wq > 65536) wq = 65536;
                        ring[ringN] = k; ringW[ringN] = (int)wq; ringSum += wq; ringN++;
                    }
                }
            }
            if (removed > 0 && ringSum > 0 && berm200 > 0)
            {
                long spoil = DivRound(removed * berm200, 200);
                long cap = (depthUm * 3 + 2) / 5;                                // 0.6 depth
                // largest remainder: floor shares, then +1 um to the largest fractions (ties by ring order)
                long* rem = stackalloc long[256];
                long given = 0;
                long* add = stackalloc long[256];
                for (int r = 0; r < ringN; r++)
                {
                    long num = spoil * ringW[r];
                    add[r] = num / ringSum;
                    rem[r] = num - add[r] * ringSum;
                    given += add[r];
                }
                for (long left = spoil - given; left > 0; left--)
                {
                    int best = -1;
                    for (int r = 0; r < ringN; r++) if (rem[r] >= 0 && (best < 0 || rem[r] > rem[best])) best = r;
                    if (best < 0) break;
                    add[best]++; rem[best] = -1;
                }
                for (int r = 0; r < ringN; r++)
                {
                    int k = ring[r];
                    long cur = L.Delta[k];
                    if (cur < (long)L.BaseUm[k] - L.H0Um[k] + cap && add[r] != 0)
                    {
                        L.Delta[k] = (int)(cur + add[r]); SetH(L, k); res.Changed++;
                    }
                    long w = L.Wear[k] + (long)wear4 * 3 * ringW[r] / (40L << 16);
                    L.Wear[k] = (byte)(w > 255 ? 255 : w);
                }
            }
            res.ValueUm = removed;
            res.I0 = i0; res.J0 = j0; res.I1 = i1; res.J1 = j1;
            res.Dirty = changed || (touched && wear4 > 0);
            return res;
        }

        /// <summary>
        /// world.js:187 brush on a decoded stamp: x, z, radius in Q20 (radius at most 2 m), amount in micrometres
        /// (> 0 raises, &lt; 0 digs). ValueUm = sum of |height change| in micrometres (JS volume = ValueUm * 1e-6 * 0.25).
        /// </summary>
        public static StampResult Brush(in DeformLayers L, long x, long z, long radius, long amountUm)
        {
            var res = new StampResult();
            if (radius <= 0 || radius > (2L << 20)) return res;
            int N = L.N, VN = L.VN, CellShift = L.CellShift;
            long gx = x + L.OriginQ20, gz = z + L.OriginQ20;
            int i0 = (int)Math.Max(1, FloorDiv(gx - radius, 1L << CellShift)), i1 = (int)Math.Min(N - 1, CeilDiv(gx + radius, 1L << CellShift));
            int j0 = (int)Math.Max(1, FloorDiv(gz - radius, 1L << CellShift)), j1 = (int)Math.Min(N - 1, CeilDiv(gz + radius, 1L << CellShift));
            if (i0 > i1 || j0 > j1) return res;
            long r2 = radius * radius;
            int* tab = L.BrushFalloff;
            long vol = 0;
            for (int j = j0; j <= j1; j++)
            {
                long dz = ((long)j << CellShift) - gz;
                for (int i = i0; i <= i1; i++)
                {
                    long dx = ((long)i << CellShift) - gx;
                    long d2 = dx * dx + dz * dz;
                    if (d2 >= r2) continue;
                    int k = j * VN + i;
                    byte s = L.Surface[k];
                    if (IsLocked(s)) continue;
                    long u = (d2 << 20) / r2;                                  // (d/r)^2, Q20
                    int n = (int)(u >> 8), fr = (int)(u & 255);
                    long f = ((long)tab[n] * (256 - fr) + (long)tab[n + 1] * fr + 128) >> 8; // Q20
                    long dig = amountUm < 0 ? DigQ16(s) : 65536;
                    long dh = DivRound(amountUm * f * dig, 1L << 36);
                    long baseRel = (long)L.BaseUm[k] - L.H0Um[k];
                    long lo = Math.Max(baseRel - 12_000_000, -14_000_000L - L.H0Um[k]), hi = baseRel + 12_000_000;
                    long cur = L.Delta[k];
                    long nh = cur + dh;
                    if (nh < lo) nh = lo; else if (nh > hi) nh = hi;
                    vol += Math.Abs(nh - cur);
                    if (nh != cur) { L.Delta[k] = (int)nh; SetH(L, k); res.Changed++; }
                    if (s == GRASS || s == TRACK) L.Surface[k] = DIRT;
                    long w = (150 * f) >> 20;
                    if (w > L.Wear[k]) L.Wear[k] = (byte)(w > 255 ? 255 : w);
                }
            }
            res.ValueUm = vol;
            res.I0 = i0; res.J0 = j0; res.I1 = i1; res.J1 = j1;
            res.Dirty = true;
            return res;
        }

        /// <summary>
        /// world.js:370 relax (angle of repose) on a decoded stamp: x, z, radius in Q20, maxDiff in micrometres. Only
        /// vertices with WEAR >= 100 give soil; each neighbour pair steeper than maxDiff moves (d - maxDiff) / 4.
        /// </summary>
        public static StampResult Relax(in DeformLayers L, long x, long z, long radius, int iters, long maxDiffUm)
        {
            var res = new StampResult();
            int N = L.N, VN = L.VN, CellShift = L.CellShift;
            long gx = x + L.OriginQ20, gz = z + L.OriginQ20;
            long HalfCell = 1L << (CellShift - 1);
            int gi = (int)FloorDiv(gx + HalfCell, 1L << CellShift), gj = (int)FloorDiv(gz + HalfCell, 1L << CellShift); // Math.round
            int rc = (int)CeilDiv(Math.Max(0, radius), 1L << CellShift);
            int i0 = Math.Max(1, gi - rc), i1 = Math.Min(N - 1, gi + rc);
            int j0 = Math.Max(1, gj - rc), j1 = Math.Min(N - 1, gj + rc);
            res.I0 = i0 - 1; res.J0 = j0 - 1; res.I1 = i1 + 1; res.J1 = j1 + 1;
            res.Dirty = true;
            if (i0 > i1 || j0 > j1) return res;
            int r2 = rc * rc;
            for (int it = 0; it < iters; it++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        if ((i - gi) * (i - gi) + (j - gj) * (j - gj) > r2) continue;
                        int k = j * VN + i;
                        if (L.Wear[k] < 100 || IsLocked(L.Surface[k])) continue;
                        for (int n = 0; n < 4; n++)
                        {
                            int kn = n == 0 ? k + 1 : n == 1 ? k - 1 : n == 2 ? k + VN : k - VN;
                            if (IsLocked(L.Surface[kn])) continue;
                            long d = ((long)L.H0Um[k] + L.Delta[k]) - ((long)L.H0Um[kn] + L.Delta[kn]);
                            if (d > maxDiffUm)
                            {
                                long m = DivRound(d - maxDiffUm, 4);
                                if (m != 0)
                                {
                                    L.Delta[k] -= (int)m; L.Delta[kn] += (int)m;
                                    SetH(L, k); SetH(L, kn); res.Changed += 2;
                                }
                                if (L.Wear[kn] < 120) L.Wear[kn] = 120;
                                if (L.Surface[kn] == GRASS) L.Surface[kn] = DIRT;
                            }
                        }
                    }
            return res;
        }
    }
}

#if !UNITY_2017_1_OR_NEWER
namespace Unity.Collections.LowLevel.Unsafe { sealed class NativeDisableUnsafePtrRestrictionAttribute : System.Attribute { } }
#endif
