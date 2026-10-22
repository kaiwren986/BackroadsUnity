using System;
using System.Collections.Generic;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Clipper2 at millimetre resolution for the site bake: exact integer booleans, so regions built from the same
    /// vertices share their edges exactly (parcels tile their block) and every run gives the same bytes. Regions are
    /// Paths64 with outer rings counter-clockwise and holes clockwise (Clipper's positive orientation).
    /// </summary>
    public static class Clip
    {
        public const double Scale = 1000;

        public static Point64 Pt(double2 p) => new Point64((long)Math.Round(p.x * Scale), (long)Math.Round(p.y * Scale));
        public static double2 D(Point64 p) => new double2(p.X / Scale, p.Y / Scale);

        public static Path64 Path(IReadOnlyList<double2> p)
        {
            var r = new Path64(p.Count);
            foreach (var q in p) r.Add(Pt(q));
            return r;
        }

        public static double2[] Pts(Path64 p)
        {
            var r = new double2[p.Count];
            for (int i = 0; i < p.Count; i++) r[i] = D(p[i]);
            return r;
        }

        public static Paths64 Paths(params IReadOnlyList<double2>[] rings)
        {
            var r = new Paths64(rings.Length);
            foreach (var x in rings) if (x != null && x.Count >= 3) r.Add(Path(x));
            return r;
        }

        /// <summary>A region from an outer ring and holes (any orientation in, Clipper's orientation out).</summary>
        public static Paths64 Region(IReadOnlyList<double2> outer, IEnumerable<double2[]> holes = null)
        {
            var o = Path(outer);
            if (Clipper.Area(o) < 0) o.Reverse();
            var r = new Paths64 { o };
            if (holes != null)
                foreach (var h in holes)
                {
                    if (h == null || h.Length < 3) continue;
                    var hp = Path(h);
                    if (Clipper.Area(hp) > 0) hp.Reverse();
                    r.Add(hp);
                }
            return Clipper.Union(r, FillRule.NonZero);
        }

        /// <summary>Area in m² (holes subtract).</summary>
        public static double Area(Paths64 p) => Clipper.Area(p) / (Scale * Scale);
        public static double Area(Path64 p) => Clipper.Area(p) / (Scale * Scale);

        public static Paths64 Union(Paths64 a) => Clipper.Union(a, FillRule.NonZero);
        public static Paths64 Union(Paths64 a, Paths64 b) => Clipper.Union(a, b, FillRule.NonZero);
        public static Paths64 Intersect(Paths64 a, Paths64 b) => Clipper.Intersect(a, b, FillRule.NonZero);
        public static Paths64 Difference(Paths64 a, Paths64 b) => Clipper.Difference(a, b, FillRule.NonZero);

        /// <summary>Offset a closed region (delta in metres; negative shrinks).</summary>
        public static Paths64 Inflate(Paths64 p, double delta, JoinType join = JoinType.Round) =>
            Clipper.InflatePaths(p, delta * Scale, join, EndType.Polygon, 2.0, 0.05 * Scale);

        /// <summary>A band of half-width delta round open polylines (round ends).</summary>
        public static Paths64 Band(IEnumerable<double2[]> lines, double delta)
        {
            var open = new Paths64();
            foreach (var l in lines) if (l != null && l.Length >= 2) open.Add(Path(l));
            if (open.Count == 0) return new Paths64();
            return Clipper.InflatePaths(open, delta * Scale, JoinType.Round, EndType.Round, 2.0, 0.25 * Scale);
        }

        /// <summary>The connected pieces of a region, each an outer ring with its holes (outer first), largest first.</summary>
        public static List<Paths64> Pieces(Paths64 region)
        {
            var tree = new PolyTree64();
            var c = new Clipper64();
            c.AddSubject(region);
            c.Execute(ClipType.Union, FillRule.NonZero, tree);
            var r = new List<Paths64>();
            Collect(tree, r);
            r.Sort((x, y) =>
            {
                int k = Area(y).CompareTo(Area(x));
                return k != 0 ? k : Key(x).CompareTo(Key(y));
            });
            return r;
        }

        static void Collect(PolyPathBase node, List<Paths64> into)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var outer = (PolyPath64)((PolyPath64)node)[i];
                if (outer.Polygon == null) continue;
                var piece = new Paths64 { outer.Polygon };
                for (int j = 0; j < outer.Count; j++)
                {
                    var hole = outer[j];
                    if (hole.Polygon != null) piece.Add(hole.Polygon);
                    Collect(hole, into);   // islands inside the hole
                }
                into.Add(piece);
            }
        }

        /// <summary>A stable sort key: the smallest vertex (x, then y).</summary>
        public static long Key(Paths64 p)
        {
            long best = long.MaxValue;
            foreach (var path in p)
                foreach (var q in path)
                {
                    long k = q.X * 4_000_000_000L + q.Y;
                    if (k < best) best = k;
                }
            return best;
        }

        /// <summary>True when q lies inside the region (holes excluded; the boundary counts as inside).</summary>
        public static bool Contains(Paths64 region, double2 q)
        {
            var p = Pt(q);
            int inside = 0;
            foreach (var path in region)
            {
                var r = Clipper.PointInPolygon(p, path);
                if (r == PointInPolygonResult.IsOn) return true;
                if (r == PointInPolygonResult.IsInside) inside += Clipper.Area(path) > 0 ? 1 : -1;
            }
            return inside > 0;
        }

        /// <summary>Axis-aligned bounds in metres.</summary>
        public static (double2 min, double2 max) Bounds(Paths64 p)
        {
            var b = Clipper.GetBounds(p);
            return (new double2(b.left / Scale, b.top / Scale), new double2(b.right / Scale, b.bottom / Scale));
        }

        /// <summary>Length of the boundary two regions share (approximately: the overlap of a thin band round a).</summary>
        public static double SharedLength(Paths64 a, Paths64 b, double band = 0.05)
        {
            var grown = Inflate(a, band, JoinType.Miter);
            return Area(Intersect(grown, b)) / band;
        }
    }
}
