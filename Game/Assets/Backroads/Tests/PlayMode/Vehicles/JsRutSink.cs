using System;
using Backroads.Deform;
using Backroads.Vehicles;
using Backroads.World;

namespace Backroads.Tests.PlayMode.Vehicles
{
    /// <summary>
    /// Test-only stand-in for lane W's deformation (IStampSink): world.js rut() line by line in doubles on the
    /// float32 H / BASE, u8 WEAR and SURFACE arrays of a private WorldGrid copy, exactly as the browser game applied
    /// it while the golden traces were recorded. Lane W's integer-micrometre kernels replace it in play; the traces
    /// can run against either (Sink option of GoldenTraceReplay).
    /// </summary>
    sealed class JsRutSink : IStampSink
    {
        readonly int N, VN;
        readonly double HALF, CELL;
        readonly WorldGrid _g;
        readonly int[] _ring = new int[256];
        readonly float[] _ringW = new float[256];

        public long Calls { get; private set; }
        public double Removed { get; private set; }

        public JsRutSink(WorldGrid grid)
        {
            _g = grid ?? throw new ArgumentNullException(nameof(grid));
            N = grid.Layout.N; VN = grid.Layout.VN; HALF = grid.Layout.Half; CELL = grid.Layout.Cell;
        }

        public void Rut(float x, float z, float radius, float depth, float rate, float wearAdd, float berm = 0.45f, float fx = 0, float fz = 0)
        {
            Calls++;
            var H = _g.H; var BASE = _g.Base; var SURFACE = _g.Surface; var WEAR = _g.Wear;
            double gi = (x + HALF) / CELL, gj = (z + HALF) / CELL;
            double outer = radius + 0.55;
            double rc = outer / CELL;
            int i0 = Math.Max(1, (int)Math.Floor(gi - rc)), i1 = Math.Min(N - 1, (int)Math.Ceiling(gi + rc));
            int j0 = Math.Max(1, (int)Math.Floor(gj - rc)), j1 = Math.Min(N - 1, (int)Math.Ceiling(gj + rc));
            double removed = 0, ringW = 0;
            int ringN = 0;
            bool dir = fx != 0 || fz != 0;
            for (int j = j0; j <= j1; j++)
            {
                double dz = (j - gj) * CELL;
                for (int i = i0; i <= i1; i++)
                {
                    double dx = (i - gi) * CELL;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > outer) continue;
                    int k = j * VN + i;
                    byte s = SURFACE[k];
                    double soft = BrvSim.Softness(s);
                    if (soft <= 0) continue;
                    if (d < radius)
                    {
                        double f = 1 - (d / radius) * (d / radius);
                        double target = BASE[k] - depth * soft * f;
                        if (H[k] > target)
                        {
                            double dh = Math.Min(H[k] - target, rate * f * (0.5 + soft * 0.5));
                            H[k] = (float)(H[k] - dh);
                            removed += dh;
                        }
                        double w = WEAR[k] + wearAdd * f * (0.4 + soft);
                        WEAR[k] = w > 255 ? (byte)255 : (byte)w;
                        if (s == BrvSim.SURF_GRASS && WEAR[k] > 170) SURFACE[k] = BrvSim.SURF_DIRT;
                    }
                    else if (ringN < 256)
                    {
                        if (dir && dx * fx + dz * fz > d * 0.35) continue;
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
                    if (H[k] < BASE[k] + depth * 0.6) H[k] = (float)(H[k] + share * _ringW[r]);
                    double w = WEAR[k] + wearAdd * 0.3 * _ringW[r];
                    WEAR[k] = w > 255 ? (byte)255 : (byte)w;
                }
            }
            Removed += removed;
        }

        public void Brush(float x, float z, float radius, float amount) => throw new NotSupportedException("vehicles do not brush in M2");

        public void Relax(float x, float z, float radius, int iters = 2, float maxDiff = 0.36f) => throw new NotSupportedException("vehicles do not relax in M2");
    }
}
