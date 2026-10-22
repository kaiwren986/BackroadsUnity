using System;
using Backroads.World;

namespace Backroads.Deform
{
    /// <summary>
    /// Reference path (like World's WorldGridQueriesRef): a line-by-line double-precision mirror of the browser game's
    /// world.js rut / brush / relax (552f780, lines 137-403) on float32 H and BASE and u8 SURFACE and WEAR, so the
    /// arithmetic is JS's: every expression in doubles, every store to H rounded to float32 (Float32Array), every store
    /// to WEAR truncated (Uint8Array). Tests replay CanonData/v1/reference/deform/calls.json through it, prove it
    /// bit-exact against the per-call fnv1a hashes, and then measure the integer kernels (StampKernels) against it at
    /// every call. Never used by the game. The grid's size is a WorldGridLayout (the canon test world's by default; T3.1).
    /// </summary>
    public sealed class JsDeformReference
    {
        public const double WaterY = 0;
        readonly int N, VN;
        readonly double Half, Cell;
        public const byte GRASS = 0, DIRT = 1, ROAD = 4, CONCRETE = 6, TRACK = 7;

        /// <summary>world.js SOFTNESS and DIG_RATE are Float32Arrays: these are their float32 values.</summary>
        public static readonly float[] Softness = { 0.5f, 1.0f, 0.85f, 0f, 0f, 0.16f, 0f, 0.3f, 1.0f };
        public static readonly float[] DigRate = { 1f, 1f, 1.2f, 0.3f, 0f, 0.8f, 0f, 0.9f, 1.4f };

        public readonly float[] H;
        public readonly float[] Base;
        public readonly byte[] Surface;
        public readonly byte[] Wear;

        readonly int[] _ring = new int[256];
        readonly float[] _ringW = new float[256];

        /// <summary>The dirty rectangle of the last call (world.js markDirty), or Dirty = false.</summary>
        public int DirtyI0, DirtyJ0, DirtyI1, DirtyJ1;
        public bool Dirty;

        public JsDeformReference(float[] h, float[] baseHeights, byte[] surface, byte[] wear)
            : this(WorldGridLayout.CanonV1, h, baseHeights, surface, wear) { }

        public JsDeformReference(WorldGridLayout layout, float[] h, float[] baseHeights, byte[] surface, byte[] wear)
        {
            if (h.Length != layout.Count) throw new ArgumentException($"heights: {h.Length} values for a {layout.VN}^2 grid");
            N = layout.N; VN = layout.VN; Half = layout.Half; Cell = layout.Cell;
            H = h; Base = baseHeights; Surface = surface; Wear = wear;
        }

        static bool IsLocked(byte s) => s == ROAD || s == CONCRETE;

        void MarkDirty(int i0, int j0, int i1, int j1) { Dirty = true; DirtyI0 = i0; DirtyJ0 = j0; DirtyI1 = i1; DirtyJ1 = j1; }

        static byte ToU8(double w) => (byte)(int)Math.Truncate(w); // Uint8Array store of 0..255

        /// <summary>world.js:137 rut. Returns the removed height sum (m), as JS.</summary>
        public double Rut(double x, double z, double radius, double depth, double rate, double wearAdd, double berm = 0.45, double fx = 0, double fz = 0)
        {
            Dirty = false;
            double gi = (x + Half) / Cell, gj = (z + Half) / Cell;
            double outer = radius + 0.55;
            double rc = outer / Cell;
            int i0 = (int)Math.Max(1, Math.Floor(gi - rc)), i1 = (int)Math.Min(N - 1, Math.Ceiling(gi + rc));
            int j0 = (int)Math.Max(1, Math.Floor(gj - rc)), j1 = (int)Math.Min(N - 1, Math.Ceiling(gj + rc));
            double removed = 0, ringW = 0;
            int ringN = 0;
            bool changed = false, touched = false;
            bool hasDir = (fx != 0 && !double.IsNaN(fx)) || (fz != 0 && !double.IsNaN(fz));
            for (int j = j0; j <= j1; j++)
            {
                double dz = (j - gj) * Cell;
                for (int i = i0; i <= i1; i++)
                {
                    double dx = (i - gi) * Cell;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > outer) continue;
                    int k = j * VN + i;
                    byte s = Surface[k];
                    double soft = Softness[s];
                    if (soft <= 0) continue;
                    touched = true;
                    if (d < radius)
                    {
                        double f = 1 - (d / radius) * (d / radius);
                        double target = Base[k] - depth * soft * f;
                        if (H[k] > target)
                        {
                            double dh = Math.Min(H[k] - target, rate * f * (0.5 + soft * 0.5));
                            H[k] = (float)(H[k] - dh); removed += dh; changed = true;
                        }
                        double w = Wear[k] + wearAdd * f * (0.4 + soft);
                        Wear[k] = w > 255 ? (byte)255 : ToU8(w);
                        if (s == GRASS && Wear[k] > 170) Surface[k] = DIRT;
                    }
                    else if (ringN < 256)
                    {
                        if (hasDir && dx * fx + dz * fz > d * 0.35) continue;
                        double w = 1 - (d - radius) / 0.55;
                        _ring[ringN] = k; _ringW[ringN] = (float)w; ringW += w; ringN++;
                    }
                }
            }
            if (removed > 0 && ringW > 0 && berm > 0)
            {
                double share = (removed * berm) / ringW;
                for (int r = 0; r < ringN; r++)
                {
                    int k = _ring[r];
                    if (H[k] < Base[k] + depth * 0.6) H[k] = (float)(H[k] + share * _ringW[r]);
                    double w = Wear[k] + wearAdd * 0.3 * _ringW[r];
                    Wear[k] = w > 255 ? (byte)255 : ToU8(w);
                }
            }
            if (changed) MarkDirty(i0, j0, i1, j1);
            else if (touched && wearAdd > 0) MarkDirty(i0, j0, i1, j1);
            return removed;
        }

        /// <summary>world.js:187 brush. Returns the moved volume (m^3), as JS.</summary>
        public double Brush(double x, double z, double radius, double amount)
        {
            Dirty = false;
            double gi = (x + Half) / Cell, gj = (z + Half) / Cell;
            double rc = radius / Cell;
            int i0 = (int)Math.Max(1, Math.Floor(gi - rc)), i1 = (int)Math.Min(N - 1, Math.Ceiling(gi + rc));
            int j0 = (int)Math.Max(1, Math.Floor(gj - rc)), j1 = (int)Math.Min(N - 1, Math.Ceiling(gj + rc));
            double vol = 0;
            for (int j = j0; j <= j1; j++)
            {
                double dz = (j - gj) * Cell;
                for (int i = i0; i <= i1; i++)
                {
                    double dx = (i - gi) * Cell;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d >= radius) continue;
                    int k = j * VN + i;
                    byte s = Surface[k];
                    if (IsLocked(s)) continue;
                    double f = 1 - d / radius; f = f * f * (3 - 2 * f);
                    double dh = amount * f * (amount < 0 ? DigRate[s] : 1);
                    double v = H[k] + dh, lo = Math.Max(Base[k] - 12, WaterY - 14), hi = Base[k] + 12;
                    double nh = v < lo ? lo : v > hi ? hi : v;
                    vol += Math.Abs(nh - H[k]);
                    H[k] = (float)nh;
                    if (s == GRASS || s == TRACK) Surface[k] = DIRT;
                    double w = Math.Max(Wear[k], 150 * f);
                    Wear[k] = w > 255 ? (byte)255 : ToU8(w);
                }
            }
            MarkDirty(i0, j0, i1, j1);
            return vol * Cell * Cell;
        }

        /// <summary>world.js:370 relax without the optional half-plane (blade) freeze.</summary>
        public void Relax(double x, double z, double radius, int iters = 2, double maxDiff = 0.36)
        {
            int gi = (int)Math.Floor((x + Half) / Cell + 0.5), gj = (int)Math.Floor((z + Half) / Cell + 0.5); // Math.round
            int rc = (int)Math.Ceiling(radius / Cell);
            int i0 = Math.Max(1, gi - rc), i1 = Math.Min(N - 1, gi + rc);
            int j0 = Math.Max(1, gj - rc), j1 = Math.Min(N - 1, gj + rc);
            int r2 = rc * rc;
            int[] nb = { 1, -1, VN, -VN };
            for (int it = 0; it < iters; it++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        if ((i - gi) * (i - gi) + (j - gj) * (j - gj) > r2) continue;
                        int k = j * VN + i;
                        if (Wear[k] < 100 || IsLocked(Surface[k])) continue;
                        for (int n = 0; n < 4; n++)
                        {
                            int kn = k + nb[n];
                            if (IsLocked(Surface[kn])) continue;
                            double d = (double)H[k] - H[kn];
                            if (d > maxDiff)
                            {
                                double m = (d - maxDiff) * 0.25;
                                H[k] = (float)(H[k] - m); H[kn] = (float)(H[kn] + m);
                                if (Wear[kn] < 120) Wear[kn] = 120;
                                if (Surface[kn] == GRASS) Surface[kn] = DIRT;
                            }
                        }
                    }
            MarkDirty(i0 - 1, j0 - 1, i1 + 1, j1 + 1);
        }
    }
}
