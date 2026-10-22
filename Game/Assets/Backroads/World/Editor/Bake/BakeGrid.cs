using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Backroads.World.Bake
{
    /// <summary>
    /// The layers of one baked world in grid order (CanonData v1 formats, ADR-006 section 3): k = j * VN + i with the
    /// JS-space position x_js = i * Cell - Half, z = j * Cell - Half, so the town's local frame (x east) is x = -x_js =
    /// Half - i * Cell. The bake works in this order from the start, so the export writes the arrays as they are.
    /// </summary>
    public sealed class BakeGrid
    {
        public readonly WorldGridLayout L;
        public readonly int VN, N, Count;
        public readonly double Cell, Half;

        /// <summary>The final heights (base after plates) and the natural ground (DEM, river, micro-noise).</summary>
        public readonly float[] heights, @base;
        public readonly byte[] surface, wear, roadd, mud;
        /// <summary>TintN x TintN x 2 (tb, td).</summary>
        public readonly float[] tint;

        public BakeGrid(WorldGridLayout layout)
        {
            if (!layout.IsValid) throw new ArgumentException("invalid layout");
            L = layout;
            VN = layout.VN; N = layout.N; Count = layout.Count;
            Cell = layout.Cell; Half = layout.Half;
            heights = new float[Count];
            @base = new float[Count];
            surface = new byte[Count];
            wear = new byte[Count];
            roadd = new byte[Count];
            mud = new byte[Count];
            tint = new float[layout.TintN * layout.TintN * 2];
        }

        /// <summary>Local plan position (x east, z north) of vertex (i, j).</summary>
        public double LocalX(int i) => Half - i * Cell;
        public double LocalZ(int j) => j * Cell - Half;
        public double2 Local(int i, int j) => new double2(Half - i * Cell, j * Cell - Half);

        /// <summary>Fractional grid coordinates of a local point.</summary>
        public double GridI(double x) => (Half - x) / Cell;
        public double GridJ(double z) => (z + Half) / Cell;

        public int K(int i, int j) => j * VN + i;

        /// <summary>The vertex index range [i0, i1] x [j0, j1] covering a local box grown by margin, clamped to the grid.</summary>
        public void Box(double xmin, double zmin, double xmax, double zmax, double margin, out int i0, out int j0, out int i1, out int j1)
        {
            // x runs backwards in i
            i0 = Math.Max(0, (int)Math.Floor(GridI(xmax + margin)));
            i1 = Math.Min(N, (int)Math.Ceiling(GridI(xmin - margin)));
            j0 = Math.Max(0, (int)Math.Floor(GridJ(zmin - margin)));
            j1 = Math.Min(N, (int)Math.Ceiling(GridJ(zmax + margin)));
        }

        /// <summary>Bilinear sample of a float layer at a local point (clamped).</summary>
        public double Sample(float[] layer, double x, double z)
        {
            double fi = GridI(x), fj = GridJ(z);
            fi = fi < 0 ? 0 : fi > N - 1e-6 ? N - 1e-6 : fi;
            fj = fj < 0 ? 0 : fj > N - 1e-6 ? N - 1e-6 : fj;
            int i = (int)fi, j = (int)fj;
            double ti = fi - i, tj = fj - j;
            int k = j * VN + i;
            double a = layer[k], b = layer[k + 1], c = layer[k + VN], d = layer[k + VN + 1];
            return (a + (b - a) * ti) * (1 - tj) + (c + (d - c) * ti) * tj;
        }
    }

    /// <summary>Deterministic polygon rasterization and distances on a BakeGrid (double arithmetic, even-odd rule).</summary>
    public static class Raster
    {
        /// <summary>
        /// Calls fill(k) for every grid vertex inside the polygon set (even-odd over all rings), row by row: the crossings of
        /// each row with the ring edges are sorted and the spans between pairs filled. A vertex exactly on an edge counts
        /// as inside the span to its west (half-open in i).
        /// </summary>
        public static void Fill(BakeGrid g, IEnumerable<double2[]> rings, Action<int> fill)
        {
            var edges = new List<(double2 a, double2 b)>();
            double zmin = double.MaxValue, zmax = double.MinValue, xmin = double.MaxValue, xmax = double.MinValue;
            foreach (var r in rings)
            {
                if (r == null || r.Length < 3) continue;
                for (int k = 0; k < r.Length; k++)
                {
                    var a = r[k]; var b = r[(k + 1) % r.Length];
                    edges.Add((a, b));
                    zmin = Math.Min(zmin, a.y); zmax = Math.Max(zmax, a.y); xmin = Math.Min(xmin, a.x); xmax = Math.Max(xmax, a.x);
                }
            }
            if (edges.Count == 0) return;
            g.Box(xmin, zmin, xmax, zmax, 0, out _, out int j0, out _, out int j1);
            var xs = new List<double>();
            for (int j = j0; j <= j1; j++)
            {
                double z = g.LocalZ(j);
                xs.Clear();
                foreach (var (a, b) in edges)
                {
                    if ((a.y > z) == (b.y > z)) continue;
                    xs.Add(a.x + (z - a.y) * (b.x - a.x) / (b.y - a.y));
                }
                if (xs.Count < 2) continue;
                xs.Sort();
                for (int p = 0; p + 1 < xs.Count; p += 2)
                {
                    // inside for x in [xa, xb); x falls as i grows, so the candidates are i in [floor(I(xb)), ceil(I(xa))]
                    double xa = xs[p], xb = xs[p + 1];
                    int ia = Math.Max(0, (int)Math.Floor(g.GridI(xb))), ib = Math.Min(g.N, (int)Math.Ceiling(g.GridI(xa)));
                    for (int i = ia; i <= ib; i++)
                    {
                        double x = g.LocalX(i);
                        if (x >= xa && x < xb) fill(j * g.VN + i);
                    }
                }
            }
        }

        public static void Fill(BakeGrid g, Poly p, Action<int> fill) => Fill(g, p.Rings(), fill);

        public static bool[] Mask(BakeGrid g, IEnumerable<Poly> polys)
        {
            var m = new bool[g.Count];
            foreach (var p in polys) Fill(g, p, k => m[k] = true);
            return m;
        }

        /// <summary>Even-odd point in polygon (rings).</summary>
        public static bool Inside(double2 q, IEnumerable<double2[]> rings)
        {
            bool inside = false;
            foreach (var r in rings)
            {
                if (r == null) continue;
                for (int a = 0, b = r.Length - 1; a < r.Length; b = a++)
                {
                    var pa = r[a]; var pb = r[b];
                    if ((pa.y > q.y) != (pb.y > q.y) && q.x < pa.x + (q.y - pa.y) * (pb.x - pa.x) / (pb.y - pa.y)) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Distance from q to segment ab, and the parameter of the closest point.</summary>
        public static double SegmentDistance(double2 q, double2 a, double2 b, out double t)
        {
            double2 ab = b - a, aq = q - a;
            double len2 = ab.x * ab.x + ab.y * ab.y;
            t = len2 > 0 ? (aq.x * ab.x + aq.y * ab.y) / len2 : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double dx = aq.x - ab.x * t, dz = aq.y - ab.y * t;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        public static double DistanceToRings(double2 q, IEnumerable<double2[]> rings)
        {
            double best = double.MaxValue;
            foreach (var r in rings)
                for (int a = 0, b = r.Length - 1; a < r.Length; b = a++)
                    best = Math.Min(best, SegmentDistance(q, r[b], r[a], out _));
            return best;
        }

        public static double DistanceToPolyline(double2 q, double2[] line)
        {
            double best = double.MaxValue;
            for (int k = 0; k + 1 < line.Length; k++) best = Math.Min(best, SegmentDistance(q, line[k], line[k + 1], out _));
            return best;
        }

        /// <summary>
        /// Chamfer distance transform (3-4 weights, two passes, in cells; cell = 3) inside the box [i0..i1] x [j0..j1]:
        /// d[k] = 0 where seed[k], else the chamfer distance to the nearest seed in the box, in metres (cell * w / 3);
        /// label[k] (if given) takes the label of the seed it came from. Deterministic (fixed scan order, first-found ties).
        /// </summary>
        public static float[] Chamfer(BakeGrid g, Func<int, bool> seed, int i0, int j0, int i1, int j1, int[] seedLabel = null, int[] labelOut = null)
        {
            int w = i1 - i0 + 1, h = j1 - j0 + 1;
            var d = new int[w * h];
            var lab = labelOut != null ? new int[w * h] : null;
            const int Inf = int.MaxValue / 4;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int k = (j0 + j) * g.VN + i0 + i;
                    bool s = seed(k);
                    d[j * w + i] = s ? 0 : Inf;
                    if (lab != null) lab[j * w + i] = s && seedLabel != null ? seedLabel[k] : -1;
                }
            void Relax(int at, int from, int cost)
            {
                int v = d[from] + cost;
                if (v < d[at]) { d[at] = v; if (lab != null) lab[at] = lab[from]; }
            }
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int at = j * w + i;
                    if (i > 0) Relax(at, at - 1, 3);
                    if (j > 0)
                    {
                        Relax(at, at - w, 3);
                        if (i > 0) Relax(at, at - w - 1, 4);
                        if (i < w - 1) Relax(at, at - w + 1, 4);
                    }
                }
            for (int j = h - 1; j >= 0; j--)
                for (int i = w - 1; i >= 0; i--)
                {
                    int at = j * w + i;
                    if (i < w - 1) Relax(at, at + 1, 3);
                    if (j < h - 1)
                    {
                        Relax(at, at + w, 3);
                        if (i < w - 1) Relax(at, at + w + 1, 4);
                        if (i > 0) Relax(at, at + w - 1, 4);
                    }
                }
            var outD = new float[w * h];
            for (int q = 0; q < outD.Length; q++) outD[q] = d[q] >= Inf ? float.MaxValue : (float)(d[q] * g.Cell / 3.0);
            if (labelOut != null)
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++) labelOut[(j0 + j) * g.VN + i0 + i] = lab[j * w + i];
            return outD;
        }
    }
}
