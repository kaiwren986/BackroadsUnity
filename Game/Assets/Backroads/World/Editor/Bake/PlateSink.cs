using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Backroads.Core.WorldBake;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.2 step 4: lane W's IPlateSink (ADR-007). Plates are collected, then applied once, sorted by PlateOrder (stage,
    /// priority, id; submission order never matters):
    /// - per stage, every plate sets the grid vertices inside its polygon (even-odd) to its target height and its surface
    ///   (a later plate wins where plates overlap);
    /// - then the stage's blend bands: a vertex outside every plate of the stage, and outside every plate of an earlier
    ///   stage, eases linearly from the nearest plate edge's height back to its height before the stage over that plate's
    ///   blend width (h = before + (edge - before) * (1 - d / blend); with blend = 3 x the step the side slope stays at
    ///   1V:3H). Where bands overlap, the plate whose band weight is largest wins (the later plate on a tie). Nothing
    ///   outside polygon + blend changes.
    /// Heights: Plane (a x + b z + c), LeastSquares (fitted once, slope clamped to maxSlope, then a plane) and PerVertex
    /// (the fan from vertex 0 for a convex polygon, as lane R's reference PlateField, else deterministic ear clipping).
    /// The kernel is one static code path run as a Burst job (FloatMode.Strict) or as plain managed code; both give the
    /// same bytes (World.PlatesBurstMatchesManaged).
    /// </summary>
    public sealed class PlateSink : IPlateSink
    {
        public const int Version = 1;
        readonly List<Plate> _plates = new List<Plate>();
        readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Which plates count as carriageway for the road-distance layer (ROADD).</summary>
        public Func<Plate, bool> IsCarriageway = DefaultCarriageway;

        public static bool DefaultCarriageway(Plate p) =>
            p.kind == PlateKind.Road && p.id != null &&
            (p.id.StartsWith("road:", StringComparison.Ordinal) || p.id.StartsWith("abutment:", StringComparison.Ordinal) ||
             p.id.StartsWith("apron:", StringComparison.Ordinal));

        public int Count => _plates.Count;

        public void Submit(Plate plate)
        {
            if (plate == null) throw new ArgumentNullException(nameof(plate));
            if (string.IsNullOrEmpty(plate.id)) throw new ArgumentException("plate without id");
            if (!_ids.Add(plate.id)) throw new ArgumentException("duplicate plate id " + plate.id);
            if (plate.polygon == null || plate.polygon.Length < 3) throw new ArgumentException($"plate {plate.id}: polygon has fewer than 3 points");
            foreach (var p in plate.polygon)
                if (!math.all(math.isfinite(p))) throw new ArgumentException($"plate {plate.id}: polygon point is not finite");
            if (plate.heights == PlateHeights.PerVertex && (plate.vertexY == null || plate.vertexY.Length != plate.polygon.Length))
                throw new ArgumentException($"plate {plate.id}: PerVertex needs one vertexY per polygon point");
            if (plate.heights == PlateHeights.LeastSquares && (plate.fitPoints == null || plate.fitPoints.Length < 3))
                throw new ArgumentException($"plate {plate.id}: LeastSquares needs at least 3 fit points");
            if (!(plate.blend >= 0) || float.IsInfinity(plate.blend)) throw new ArgumentException($"plate {plate.id}: blend {plate.blend}");
            _plates.Add(plate);
        }

        public List<Plate> Sorted()
        {
            var l = new List<Plate>(_plates);
            l.Sort(PlateOrder.Comparison);
            return l;
        }

        /// <summary>SHA-256 (hex) of the sorted plates in a fixed binary form: the same plates in any order give the same hash.</summary>
        public string Hash()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                foreach (var p in Sorted())
                {
                    w.Write(p.id); w.Write((byte)p.kind); w.Write(p.surface); w.Write((byte)p.heights);
                    w.Write(p.polygon.Length);
                    foreach (var q in p.polygon) { w.Write(q.x); w.Write(q.y); }
                    w.Write(p.plane.x); w.Write(p.plane.y); w.Write(p.plane.z);
                    w.Write(p.fitPoints?.Length ?? 0);
                    if (p.fitPoints != null) foreach (var q in p.fitPoints) { w.Write(q.x); w.Write(q.y); w.Write(q.z); }
                    w.Write(p.vertexY?.Length ?? 0);
                    if (p.vertexY != null) foreach (var y in p.vertexY) w.Write(y);
                    w.Write(p.maxSlope); w.Write(p.blend); w.Write(p.priority); w.Write(p.noDeform);
                }
            }
            using var sha = SHA256.Create();
            return CanonRuntime.Hex(sha.ComputeHash(ms.ToArray()));
        }

        // ------------------------------------------------------------------------------------------ apply

        public sealed class Result
        {
            public int plates, noVertex;
            public readonly int[] platesByStage = new int[3], verticesByStage = new int[3], blendedByStage = new int[3];
            /// <summary>1 where a carriageway plate won the vertex (for ROADD).</summary>
            public byte[] carriageway;
            /// <summary>Index into Sorted() of the plate that set each vertex last, or -1.</summary>
            public int[] owner;
            /// <summary>1 inside a plate, 2 in a blend band only, 0 untouched by plates.</summary>
            public byte[] touched;
            public List<Plate> sorted;
            public bool ranBurst;
            public double ms;
        }

        /// <summary>Applies every plate to g.heights and g.surface (in place). The plates are not consumed.</summary>
        public Result Apply(BakeGrid g, bool useBurst)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new Result { sorted = Sorted() };
            var sorted = res.sorted;
            res.plates = sorted.Count;

            // flatten (managed, deterministic): local polygons, per-vertex heights, triangles, bounding boxes
            var recs = new NativeArray<PlateRec>(sorted.Count, Allocator.Persistent);
            int nv = 0, nt = 0;
            var tris = new List<int3>[sorted.Count];
            for (int p = 0; p < sorted.Count; p++)
            {
                nv += sorted[p].polygon.Length;
                tris[p] = sorted[p].heights == PlateHeights.PerVertex ? Triangulate(sorted[p].polygon) : new List<int3>();
                nt += tris[p].Count;
            }
            var verts = new NativeArray<double2>(Math.Max(1, nv), Allocator.Persistent);
            var vy = new NativeArray<double>(Math.Max(1, nv), Allocator.Persistent);
            var triArr = new NativeArray<int3>(Math.Max(1, nt), Allocator.Persistent);
            int vo = 0, to = 0;
            for (int p = 0; p < sorted.Count; p++)
            {
                var pl = sorted[p];
                double3 plane = PlaneOf(pl);
                double xmin = double.MaxValue, xmax = double.MinValue, zmin = double.MaxValue, zmax = double.MinValue;
                for (int v = 0; v < pl.polygon.Length; v++)
                {
                    var q = pl.polygon[v];
                    verts[vo + v] = q;
                    vy[vo + v] = pl.heights == PlateHeights.PerVertex ? pl.vertexY[v] : plane.x * q.x + plane.y * q.y + plane.z;
                    xmin = Math.Min(xmin, q.x); xmax = Math.Max(xmax, q.x); zmin = Math.Min(zmin, q.y); zmax = Math.Max(zmax, q.y);
                }
                foreach (var t in tris[p]) triArr[to++] = t;
                g.Box(xmin, zmin, xmax, zmax, 0, out int i0, out int j0, out int i1, out int j1);
                g.Box(xmin, zmin, xmax, zmax, pl.blend, out int bi0, out int bj0, out int bi1, out int bj1);
                recs[p] = new PlateRec
                {
                    v0 = vo, vn = pl.polygon.Length, t0 = to - tris[p].Count, tn = tris[p].Count,
                    mode = (byte)(pl.heights == PlateHeights.PerVertex ? 1 : 0), plane = plane, surface = pl.surface,
                    blend = pl.blend, stage = (byte)pl.Stage, carriage = (byte)(IsCarriageway(pl) ? 1 : 0),
                    i0 = i0, j0 = j0, i1 = i1, j1 = j1, bi0 = bi0, bj0 = bj0, bi1 = bi1, bj1 = bj1,
                };
                vo += pl.polygon.Length;
            }

            var ctx = new PlateKernel.Ctx
            {
                VN = g.VN, N = g.N, Cell = g.Cell, Half = g.Half,
                plates = recs, verts = verts, vy = vy, tris = triArr,
                heights = new NativeArray<float>(g.heights, Allocator.Persistent),
                surface = new NativeArray<byte>(g.surface, Allocator.Persistent),
                owner = new NativeArray<int>(g.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                lastOwner = new NativeArray<int>(g.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                protect = new NativeArray<byte>(g.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                carriage = new NativeArray<byte>(g.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                touched = new NativeArray<byte>(g.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                bestW = new NativeArray<float>(g.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                bestY = new NativeArray<float>(g.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory),
                counters = new NativeArray<int>(8, Allocator.Persistent, NativeArrayOptions.ClearMemory),
            };
            try
            {
                for (int k = 0; k < g.Count; k++) ctx.lastOwner[k] = -1;
                var ranManaged = new NativeArray<byte>(1, Allocator.Persistent);
                bool allBurst = true;
                for (int stage = 0; stage < 3; stage++)
                {
                    int p0 = 0;
                    while (p0 < sorted.Count && (int)sorted[p0].Stage < stage) p0++;
                    int p1 = p0;
                    while (p1 < sorted.Count && (int)sorted[p1].Stage == stage) p1++;
                    res.platesByStage[stage] = p1 - p0;
                    if (p1 == p0) continue;
                    for (int c = 0; c < 8; c++) ctx.counters[c] = 0;
                    if (useBurst)
                    {
                        new StageJob { c = ctx, p0 = p0, p1 = p1, ranManaged = ranManaged }.Run();
                        if (ranManaged[0] != 0) allBurst = false;
                    }
                    else PlateKernel.Stage(ref ctx, p0, p1);
                    res.verticesByStage[stage] = ctx.counters[0];
                    res.blendedByStage[stage] = ctx.counters[1];
                    res.noVertex += ctx.counters[2];
                }
                ranManaged.Dispose();
                res.ranBurst = useBurst && allBurst;
                ctx.heights.CopyTo(g.heights);
                ctx.surface.CopyTo(g.surface);
                res.carriageway = ctx.carriage.ToArray();
                res.owner = ctx.lastOwner.ToArray();
                res.touched = ctx.touched.ToArray();
            }
            finally
            {
                recs.Dispose(); verts.Dispose(); vy.Dispose(); triArr.Dispose();
                ctx.heights.Dispose(); ctx.surface.Dispose(); ctx.owner.Dispose(); ctx.lastOwner.Dispose(); ctx.protect.Dispose();
                ctx.carriage.Dispose(); ctx.touched.Dispose(); ctx.bestW.Dispose(); ctx.bestY.Dispose(); ctx.counters.Dispose();
            }
            res.ms = sw.Elapsed.TotalMilliseconds;
            return res;
        }

        /// <summary>The plate's plane (a, b, c): y = a x + b z + c. LeastSquares is fitted here and its slope clamped.</summary>
        public static double3 PlaneOf(Plate p)
        {
            if (p.heights == PlateHeights.Plane) return p.plane;
            if (p.heights != PlateHeights.LeastSquares) return double3.zero;
            // normal equations of y = a x + b z + c, centred for conditioning
            double mx = 0, mz = 0, my = 0;
            foreach (var q in p.fitPoints) { mx += q.x; my += q.y; mz += q.z; }
            int n = p.fitPoints.Length;
            mx /= n; my /= n; mz /= n;
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
            foreach (var q in p.fitPoints)
            {
                double dx = q.x - mx, dz = q.z - mz, dy = q.y - my;
                sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxy += dx * dy; szy += dz * dy;
            }
            double det = sxx * szz - sxz * sxz, a = 0, b = 0;
            if (Math.Abs(det) > 1e-12) { a = (sxy * szz - szy * sxz) / det; b = (szy * sxx - sxy * sxz) / det; }
            double g = Math.Sqrt(a * a + b * b);
            if (p.maxSlope > 0 && g > p.maxSlope) { a *= p.maxSlope / g; b *= p.maxSlope / g; }
            return new double3(a, b, my - a * mx - b * mz);
        }

        /// <summary>The fan from vertex 0 for a convex polygon; otherwise ear clipping (first ear in index order).</summary>
        public static List<int3> Triangulate(double2[] poly)
        {
            var tris = new List<int3>();
            int n = poly.Length;
            double area = 0;
            for (int k = 0, l = n - 1; k < n; l = k++) area += poly[l].x * poly[k].y - poly[k].x * poly[l].y;
            bool ccw = area >= 0;
            bool convex = true;
            for (int k = 0; k < n && convex; k++)
            {
                var a = poly[k]; var b = poly[(k + 1) % n]; var c = poly[(k + 2) % n];
                double cr = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
                if (ccw ? cr < -1e-12 : cr > 1e-12) convex = false;
            }
            if (convex)
            {
                for (int k = 1; k + 1 < n; k++) tris.Add(new int3(0, k, k + 1));
                return tris;
            }
            var idx = new List<int>();
            for (int k = 0; k < n; k++) idx.Add(k);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int q = 0; q < idx.Count; q++)
                {
                    int ia = idx[(q + idx.Count - 1) % idx.Count], ib = idx[q], ic = idx[(q + 1) % idx.Count];
                    var a = poly[ia]; var b = poly[ib]; var c = poly[ic];
                    double cr = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (ccw ? cr <= 1e-12 : cr >= -1e-12) continue;
                    bool empty = true;
                    foreach (int o in idx)
                    {
                        if (o == ia || o == ib || o == ic) continue;
                        if (InTri(poly[o], a, b, c)) { empty = false; break; }
                    }
                    if (!empty) continue;
                    tris.Add(new int3(ia, ib, ic));
                    idx.RemoveAt(q);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) tris.Add(new int3(idx[0], idx[1], idx[2]));
            return tris;
        }

        static bool InTri(double2 p, double2 a, double2 b, double2 c)
        {
            double d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            double d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            double d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        struct StageJob : IJob
        {
            public PlateKernel.Ctx c;
            public int p0, p1;
            public NativeArray<byte> ranManaged;

            public void Execute()
            {
                byte m = 0;
                Mark(ref m);
                ranManaged[0] = m;
                PlateKernel.Stage(ref c, p0, p1);
            }

            [BurstDiscard] static void Mark(ref byte m) => m = 1;
        }
    }

    public struct PlateRec
    {
        public int v0, vn, t0, tn;
        public byte mode, surface, stage, carriage;
        public double3 plane;
        public double blend;
        public int i0, j0, i1, j1, bi0, bj0, bi1, bj1;
    }

    /// <summary>The plate kernel shared by the Burst job and the managed path (no managed objects, IEEE doubles only).</summary>
    public static class PlateKernel
    {
        public struct Ctx
        {
            public int VN, N;
            public double Cell, Half;
            [ReadOnly] public NativeArray<PlateRec> plates;
            [ReadOnly] public NativeArray<double2> verts;
            [ReadOnly] public NativeArray<double> vy;
            [ReadOnly] public NativeArray<int3> tris;
            public NativeArray<float> heights;
            public NativeArray<byte> surface;
            /// <summary>Per stage: the plate (sorted index) that set the vertex, or -1; lastOwner over all stages.</summary>
            public NativeArray<int> owner, lastOwner;
            /// <summary>1 inside a plate of an earlier stage: blend bands never touch it.</summary>
            public NativeArray<byte> protect;
            public NativeArray<byte> carriage, touched;
            public NativeArray<float> bestW, bestY;
            /// <summary>[0] vertices set, [1] vertices blended, [2] plates that cover no vertex.</summary>
            public NativeArray<int> counters;
        }

        public static void Stage(ref Ctx c, int p0, int p1)
        {
            int count = c.VN * c.VN;
            for (int k = 0; k < count; k++) { c.owner[k] = -1; c.bestW[k] = 0f; }
            // 1. interiors, in order (later wins)
            for (int p = p0; p < p1; p++)
            {
                var r = c.plates[p];
                int set = 0;
                for (int j = r.j0; j <= r.j1; j++)
                {
                    double z = j * c.Cell - c.Half;
                    for (int i = r.i0; i <= r.i1; i++)
                    {
                        double x = c.Half - i * c.Cell;
                        if (!Inside(ref c, r, x, z)) continue;
                        int k = j * c.VN + i;
                        c.heights[k] = (float)Target(ref c, r, x, z);
                        c.surface[k] = r.surface;
                        c.owner[k] = p;
                        c.lastOwner[k] = p;
                        c.carriage[k] = r.carriage;
                        set++;
                    }
                }
                if (set == 0) c.counters[2] = c.counters[2] + 1;
            }
            // 2. blend bands: best weight per free vertex (later plate wins a tie)
            for (int p = p0; p < p1; p++)
            {
                var r = c.plates[p];
                if (!(r.blend > 0)) continue;
                for (int j = r.bj0; j <= r.bj1; j++)
                {
                    double z = j * c.Cell - c.Half;
                    for (int i = r.bi0; i <= r.bi1; i++)
                    {
                        int k = j * c.VN + i;
                        if (c.owner[k] >= 0 || c.protect[k] != 0) continue;
                        double x = c.Half - i * c.Cell;
                        double d = EdgeDistance(ref c, r, x, z, out double ey);
                        if (!(d < r.blend)) continue;
                        float w = (float)(1.0 - d / r.blend);
                        if (w >= c.bestW[k] && w > 0f) { c.bestW[k] = w; c.bestY[k] = (float)ey; }
                    }
                }
            }
            // 3. finish: ease the free vertices; protect this stage's interiors from later stages' bands
            int blended = 0, total = 0;
            for (int k = 0; k < count; k++)
            {
                if (c.owner[k] >= 0) { c.protect[k] = 1; c.touched[k] = 1; total++; continue; }
                float w = c.bestW[k];
                if (w > 0f)
                {
                    double before = c.heights[k];
                    c.heights[k] = (float)(before + ((double)c.bestY[k] - before) * w);
                    if (c.touched[k] == 0) c.touched[k] = 2;
                    blended++;
                }
            }
            c.counters[0] = total;
            c.counters[1] = blended;
        }

        static bool Inside(ref Ctx c, in PlateRec r, double x, double z)
        {
            bool inside = false;
            for (int a = 0, b = r.vn - 1; a < r.vn; b = a++)
            {
                double2 pa = c.verts[r.v0 + a], pb = c.verts[r.v0 + b];
                if ((pa.y > z) != (pb.y > z) && x < pa.x + (z - pa.y) * (pb.x - pa.x) / (pb.y - pa.y)) inside = !inside;
            }
            return inside;
        }

        static double Target(ref Ctx c, in PlateRec r, double x, double z)
        {
            if (r.mode == 0) return r.plane.x * x + r.plane.y * z + r.plane.z;
            // PerVertex: barycentric in the first triangle that contains the point (as PlateField), else the closest one
            double bestM = double.MinValue, bestY = 0;
            for (int t = 0; t < r.tn; t++)
            {
                int3 tri = c.tris[r.t0 + t];
                double2 a = c.verts[r.v0 + tri.x], b = c.verts[r.v0 + tri.y], cc = c.verts[r.v0 + tri.z];
                double2 v0 = b - a, v1 = cc - a, v2 = new double2(x, z) - a;
                double den = v0.x * v1.y - v1.x * v0.y;
                if (Math.Abs(den) < 1e-14) continue;
                double u = (v2.x * v1.y - v1.x * v2.y) / den, v = (v0.x * v2.y - v2.x * v0.y) / den;
                double ya = c.vy[r.v0 + tri.x], yb = c.vy[r.v0 + tri.y], yc = c.vy[r.v0 + tri.z];
                double y = ya + u * (yb - ya) + v * (yc - ya);
                const double eps = -1e-9;
                if (u >= eps && v >= eps && u + v <= 1 - eps) return y;
                double m = Math.Min(Math.Min(u, v), 1 - u - v);
                if (m > bestM) { bestM = m; bestY = y; }
            }
            return bestY;
        }

        /// <summary>Distance from (x, z) to the plate's boundary and the boundary height at the closest point.</summary>
        static double EdgeDistance(ref Ctx c, in PlateRec r, double x, double z, out double ey)
        {
            double best = double.MaxValue;
            ey = 0;
            for (int a = 0, b = r.vn - 1; a < r.vn; b = a++)
            {
                double2 pa = c.verts[r.v0 + b], pb = c.verts[r.v0 + a];
                double abx = pb.x - pa.x, abz = pb.y - pa.y, aqx = x - pa.x, aqz = z - pa.y;
                double len2 = abx * abx + abz * abz;
                double t = len2 > 0 ? (aqx * abx + aqz * abz) / len2 : 0;
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                double dx = aqx - abx * t, dz = aqz - abz * t;
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d < best)
                {
                    best = d;
                    double ya = c.vy[r.v0 + b], yb = c.vy[r.v0 + a];
                    ey = ya + (yb - ya) * t;
                }
            }
            return best;
        }
    }
}
