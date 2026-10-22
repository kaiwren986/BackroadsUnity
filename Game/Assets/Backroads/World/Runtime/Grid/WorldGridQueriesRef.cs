using System;
using Unity.Mathematics;

namespace Backroads.World
{
    /// <summary>
    /// Double-precision reference path [T14]: a line-by-line mirror of world.js heightAt / normalAt / surfaceAt /
    /// rayTerrain (lines 68-118) with JavaScript's number arithmetic (IEEE doubles over the float32 H array, the same
    /// operation order, Math.round semantics). Plain C# on purpose: no Burst, no FMA contraction. Parity tests compare it
    /// with terrain/probes.json. The *Js methods take JS space; the others take Unity space and flip at the boundary.
    /// The grid's size comes from the view's Layout (T3.1).
    /// </summary>
    public static class WorldGridQueriesRef
    {
        public static double HeightAtJs(in WorldGridView g, double x, double z)
        {
            double half = g.Layout.Half, cell = g.Layout.Cell, max = g.Layout.N - 1e-4;
            int vn = g.Layout.VN;
            double fx = (x + half) / cell, fz = (z + half) / cell;
            if (fx < 0) fx = 0; else if (fx > max) fx = max;
            if (fz < 0) fz = 0; else if (fz > max) fz = max;
            int i = (int)fx, j = (int)fz;
            double tx = fx - i, tz = fz - j;
            int k = j * vn + i;
            double a = g.H[k], b = g.H[k + 1], c = g.H[k + vn], d = g.H[k + vn + 1];
            return (a + (b - a) * tx) * (1 - tz) + (c + (d - c) * tx) * tz;
        }

        public static double3 NormalAtJs(in WorldGridView g, double x, double z)
        {
            double e = g.Layout.Cell;
            double nx = HeightAtJs(g, x - e, z) - HeightAtJs(g, x + e, z);
            double nz = HeightAtJs(g, x, z - e) - HeightAtJs(g, x, z + e);
            double ny = 2 * e;
            double l = Hypot(nx, ny, nz);
            return new double3(nx / l, ny / l, nz / l);
        }

        /// <summary>Math.hypot for three finite arguments, as V8 computes it (scaled by the largest magnitude).</summary>
        public static double Hypot(double a, double b, double c)
        {
            double m = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
            if (m == 0) return 0;
            double sum = 0, comp = 0;
            Kahan(Math.Abs(a) / m, ref sum, ref comp);
            Kahan(Math.Abs(b) / m, ref sum, ref comp);
            Kahan(Math.Abs(c) / m, ref sum, ref comp);
            return Math.Sqrt(sum) * m;
        }

        static void Kahan(double r, ref double sum, ref double comp)
        {
            double s = r * r - comp;
            double prel = sum + s;
            comp = (prel - sum) - s;
            sum = prel;
        }

        /// <summary>JavaScript Math.round: the nearest integer, ties toward +infinity.</summary>
        public static double JsRound(double v)
        {
            double f = Math.Floor(v);
            return v - f >= 0.5 ? f + 1 : f;
        }

        public static int NearestKJs(in WorldGridLayout l, double x, double z)
        {
            double half = l.Half, cell = l.Cell;
            int n = l.N;
            double i = JsRound((x + half) / cell), j = JsRound((z + half) / cell);
            i = i < 0 ? 0 : i > n ? n : i;
            j = j < 0 ? 0 : j > n ? n : j;
            return (int)j * l.VN + (int)i;
        }

        public static byte SurfaceAtJs(in WorldGridView g, double x, double z) => g.Surface[NearestKJs(g.Layout, x, z)];
        public static byte WearAtJs(in WorldGridView g, double x, double z) => g.Wear[NearestKJs(g.Layout, x, z)];
        public static double RoadDistAtJs(in WorldGridView g, double x, double z) => g.RoadD[NearestKJs(g.Layout, x, z)] * 0.5;
        public static double MudAtJs(in WorldGridView g, double x, double z) => g.Mud[NearestKJs(g.Layout, x, z)] * (WorldGrid.MudMax / 255.0);

        public static double SinkAtJs(in WorldGridView g, double x, double z)
        {
            int k = NearestKJs(g.Layout, x, z);
            return (double)g.Base[k] - g.H[k];
        }

        /// <summary>rayTerrain: 0.5 m march, then 7 bisections. Hit distance, 0 when the origin is underground, -1 for a miss.</summary>
        public static double RayTerrainJs(in WorldGridView g, double ox, double oy, double oz, double dx, double dy, double dz, double maxT, double step = 0.5)
        {
            if (oy - HeightAtJs(g, ox, oz) <= 0) return 0;
            double tPrev = 0;
            for (double t = Math.Min(step, maxT); ; t = Math.Min(t + step, maxT))
            {
                double f = oy + dy * t - HeightAtJs(g, ox + dx * t, oz + dz * t);
                if (f <= 0)
                {
                    double a = tPrev, b = t;
                    for (int k = 0; k < 7; k++)
                    {
                        double m = (a + b) * 0.5;
                        if (oy + dy * m - HeightAtJs(g, ox + dx * m, oz + dz * m) > 0) a = m; else b = m;
                    }
                    return (a + b) * 0.5;
                }
                if (t >= maxT) return -1;
                tPrev = t;
            }
        }

        // ------------------------------------------------------------------ Unity space (x flipped at the boundary)

        public static double HeightAt(in WorldGridView g, double3 p) => HeightAtJs(g, -p.x, p.z);

        public static double3 NormalAt(in WorldGridView g, double3 p)
        {
            var n = NormalAtJs(g, -p.x, p.z);
            return new double3(-n.x, n.y, n.z);
        }

        public static byte SurfaceAt(in WorldGridView g, double3 p) => SurfaceAtJs(g, -p.x, p.z);

        public static double RayTerrain(in WorldGridView g, double3 origin, double3 dir, double maxT, double step = 0.5) =>
            RayTerrainJs(g, -origin.x, origin.y, origin.z, -dir.x, dir.y, dir.z, maxT, step);
    }
}
