using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Backroads.Sites
{
    /// <summary>
    /// Plan geometry on double2 for the site program: local metres of the town frame, x east and z north (stored in .y).
    /// Polygons are counter-clockwise seen from above and not closed (first != last), as in Backroads.Core.WorldBake.
    /// Pure arithmetic: no Unity engine types, no allocation in the hot helpers.
    /// </summary>
    public static class Poly2
    {
        public static double Cross(double2 a, double2 b) => a.x * b.y - a.y * b.x;

        /// <summary>The left normal of a direction (90 degrees counter-clockwise).</summary>
        public static double2 Perp(double2 d) => new double2(-d.y, d.x);

        /// <summary>Plan point to a frame whose u axis is the unit direction d and whose v axis is Perp(d) (a rotation).</summary>
        public static double2 ToFrame(double2 p, double2 d) => new double2(p.x * d.x + p.y * d.y, -p.x * d.y + p.y * d.x);

        /// <summary>Back from ToFrame.</summary>
        public static double2 FromFrame(double2 uv, double2 d) => new double2(uv.x * d.x - uv.y * d.y, uv.x * d.y + uv.y * d.x);

        public static double SignedArea(IReadOnlyList<double2> p)
        {
            double s = 0;
            int n = p.Count;
            for (int i = 0, j = n - 1; i < n; j = i++) s += p[j].x * p[i].y - p[i].x * p[j].y;
            return s * 0.5;
        }

        public static double Area(IReadOnlyList<double2> p) => Math.Abs(SignedArea(p));

        /// <summary>A CCW copy without a closing point or repeated consecutive points.</summary>
        public static double2[] Ccw(IReadOnlyList<double2> p)
        {
            var l = Clean(p, out _);
            if (SignedArea(l) < 0) l.Reverse();
            return l.ToArray();
        }

        /// <summary>The points without a closing point or repeats; keep[i] is the input index of output point i.</summary>
        public static List<double2> Clean(IReadOnlyList<double2> p, out List<int> keep)
        {
            var l = new List<double2>(p.Count);
            keep = new List<int>(p.Count);
            for (int i = 0; i < p.Count; i++)
            {
                if (l.Count > 0 && math.distancesq(l[l.Count - 1], p[i]) < 1e-12) continue;
                l.Add(p[i]);
                keep.Add(i);
            }
            while (l.Count > 1 && math.distancesq(l[0], l[l.Count - 1]) < 1e-12) { l.RemoveAt(l.Count - 1); keep.RemoveAt(keep.Count - 1); }
            return l;
        }

        /// <summary>Even-odd point in polygon (points on the boundary may go either way).</summary>
        public static bool Contains(IReadOnlyList<double2> p, double2 q)
        {
            bool c = false;
            int n = p.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double2 a = p[i], b = p[j];
                if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x - a.x) * (q.y - a.y) / (b.y - a.y) + a.x) c = !c;
            }
            return c;
        }

        public static double SegDist(double2 q, double2 a, double2 b)
        {
            var ab = b - a;
            double l2 = math.dot(ab, ab);
            double t = l2 > 0 ? math.clamp(math.dot(q - a, ab) / l2, 0, 1) : 0;
            return math.distance(q, a + t * ab);
        }

        /// <summary>Distance from q to the polygon's boundary, and the nearest edge (vertex i to i + 1).</summary>
        public static double BoundaryDistance(IReadOnlyList<double2> p, double2 q, out int edge)
        {
            double best = double.MaxValue;
            edge = -1;
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                double d = SegDist(q, p[i], p[(i + 1) % n]);
                if (d < best) { best = d; edge = i; }
            }
            return best;
        }

        static int Orient(double2 a, double2 b, double2 c)
        {
            double v = Cross(b - a, c - a);
            return v > 1e-12 ? 1 : v < -1e-12 ? -1 : 0;
        }

        static bool OnSeg(double2 a, double2 b, double2 q) =>
            q.x >= math.min(a.x, b.x) - 1e-12 && q.x <= math.max(a.x, b.x) + 1e-12 &&
            q.y >= math.min(a.y, b.y) - 1e-12 && q.y <= math.max(a.y, b.y) + 1e-12;

        /// <summary>True when segments ab and cd share a point (touching counts).</summary>
        public static bool SegmentsIntersect(double2 a, double2 b, double2 c, double2 d)
        {
            int o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
            if (o1 != o2 && o3 != o4) return true;
            if (o1 == 0 && OnSeg(a, b, c)) return true;
            if (o2 == 0 && OnSeg(a, b, d)) return true;
            if (o3 == 0 && OnSeg(c, d, a)) return true;
            if (o4 == 0 && OnSeg(c, d, b)) return true;
            return false;
        }

        public static double SegSegDist(double2 a, double2 b, double2 c, double2 d)
        {
            if (SegmentsIntersect(a, b, c, d)) return 0;
            return math.min(math.min(SegDist(a, c, d), SegDist(b, c, d)), math.min(SegDist(c, a, b), SegDist(d, a, b)));
        }

        /// <summary>Smallest distance between the boundaries of two polygons (0 when they cross).</summary>
        public static double BoundaryGap(IReadOnlyList<double2> a, IReadOnlyList<double2> b)
        {
            double best = double.MaxValue;
            for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
            {
                double d = SegSegDist(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count]);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>True when the convex polygon q lies inside polygon p (any shape), at least margin from its boundary.</summary>
        public static bool ConvexInside(IReadOnlyList<double2> q, IReadOnlyList<double2> p, double margin)
        {
            for (int i = 0; i < q.Count; i++) if (!Contains(p, q[i])) return false;
            for (int i = 0; i < p.Count; i++) if (Contains(q, p[i])) return false;
            return BoundaryGap(q, p) >= margin;
        }

        /// <summary>Separating-axis test for two convex polygons: true when their interiors overlap by more than eps.</summary>
        public static bool ConvexOverlap(IReadOnlyList<double2> a, IReadOnlyList<double2> b, double eps = 1e-6)
        {
            return !Separated(a, b, eps) && !Separated(b, a, eps);
        }

        static bool Separated(IReadOnlyList<double2> a, IReadOnlyList<double2> b, double eps)
        {
            for (int i = 0; i < a.Count; i++)
            {
                var e = a[(i + 1) % a.Count] - a[i];
                double len = math.length(e);
                if (len < 1e-12) continue;
                var n = new double2(e.y, -e.x) / len;
                double amin = double.MaxValue, amax = double.MinValue, bmin = double.MaxValue, bmax = double.MinValue;
                for (int k = 0; k < a.Count; k++) { double t = math.dot(a[k], n); amin = math.min(amin, t); amax = math.max(amax, t); }
                for (int k = 0; k < b.Count; k++) { double t = math.dot(b[k], n); bmin = math.min(bmin, t); bmax = math.max(bmax, t); }
                if (amax <= bmin + eps || bmax <= amin + eps) return true;
            }
            return false;
        }

        /// <summary>Area centroid (vertex mean for degenerate polygons).</summary>
        public static double2 Centroid(IReadOnlyList<double2> p)
        {
            double a = 0, cx = 0, cy = 0;
            int n = p.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double c = p[j].x * p[i].y - p[i].x * p[j].y;
                a += c; cx += (p[j].x + p[i].x) * c; cy += (p[j].y + p[i].y) * c;
            }
            if (Math.Abs(a) < 1e-12)
            {
                double2 s = 0;
                for (int i = 0; i < n; i++) s += p[i];
                return n > 0 ? s / n : s;
            }
            return new double2(cx, cy) / (3 * a);
        }

        /// <summary>Convex hull, CCW (Andrew's monotone chain), collinear points dropped.</summary>
        public static List<double2> ConvexHull(IReadOnlyList<double2> pts)
        {
            var s = new List<double2>(pts);
            s.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var h = new List<double2>(s.Count * 2);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int k = 0; k < s.Count; k++)
                {
                    var q = pass == 0 ? s[k] : s[s.Count - 1 - k];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 1] - h[h.Count - 2], q - h[h.Count - 2]) <= 1e-12) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        /// <summary>An oriented rectangle: centre, unit long axis, half lengths along the long and short axes.</summary>
        public struct Rect
        {
            public double2 centre, axis;
            public double halfLong, halfShort;
            public double2 Short => Perp(axis);

            /// <summary>Corners, CCW, starting at (-long, -short).</summary>
            public double2[] Corners()
            {
                var a = axis * halfLong;
                var b = Short * halfShort;
                return new[] { centre - a - b, centre + a - b, centre + a + b, centre - a + b };
            }
        }

        /// <summary>The minimum-area enclosing rectangle (rotating over hull edges; ties keep the first edge).</summary>
        public static Rect MinAreaRect(IReadOnlyList<double2> pts)
        {
            var h = ConvexHull(pts);
            var best = new Rect { centre = h.Count > 0 ? h[0] : 0, axis = new double2(1, 0) };
            double bestArea = double.MaxValue;
            for (int i = 0; i < h.Count; i++)
            {
                var e = h[(i + 1) % h.Count] - h[i];
                double len = math.length(e);
                if (len < 1e-9) continue;
                var d = e / len;
                double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
                for (int k = 0; k < h.Count; k++)
                {
                    var uv = ToFrame(h[k], d);
                    umin = math.min(umin, uv.x); umax = math.max(umax, uv.x);
                    vmin = math.min(vmin, uv.y); vmax = math.max(vmax, uv.y);
                }
                double area = (umax - umin) * (vmax - vmin);
                if (area < bestArea - 1e-9)
                {
                    bestArea = area;
                    var c = FromFrame(new double2((umin + umax) * 0.5, (vmin + vmax) * 0.5), d);
                    double hu = (umax - umin) * 0.5, hv = (vmax - vmin) * 0.5;
                    best = hu >= hv
                        ? new Rect { centre = c, axis = d, halfLong = hu, halfShort = hv }
                        : new Rect { centre = c, axis = Perp(d), halfLong = hv, halfShort = hu };
                }
            }
            if (best.axis.x < -1e-12 || (Math.Abs(best.axis.x) <= 1e-12 && best.axis.y < 0)) best.axis = -best.axis;
            return best;
        }

        /// <summary>Unit direction folded to [0, 180) degrees (an undirected axis), as a stable representative.</summary>
        public static double2 Axis(double2 d)
        {
            d = math.normalize(d);
            if (d.y < -1e-12 || (Math.Abs(d.y) <= 1e-12 && d.x < 0)) d = -d;
            return d;
        }

        /// <summary>Angle in degrees of an undirected axis, in [0, 180).</summary>
        public static double AxisDeg(double2 d)
        {
            double a = Math.Atan2(d.y, d.x) * 180 / Math.PI;
            a %= 180;
            if (a < 0) a += 180;
            if (a >= 180 - 1e-9) a = 0;
            return a;
        }

        /// <summary>Yaw in degrees for a plan direction: clockwise from north (+z), Unity's rotation about Y.</summary>
        public static double Yaw(double2 d)
        {
            double y = Math.Atan2(d.x, d.y) * 180 / Math.PI;
            if (y < 0) y += 360;
            return y;
        }

        /// <summary>Clip a polygon to the half plane dot(p, n) &lt;= c (Sutherland-Hodgman).</summary>
        public static List<double2> ClipHalf(IReadOnlyList<double2> p, double2 n, double c)
        {
            var r = new List<double2>(p.Count + 2);
            int m = p.Count;
            for (int i = 0; i < m; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % m];
                double da = math.dot(a, n) - c, db = math.dot(b, n) - c;
                if (da <= 0) r.Add(a);
                if ((da < 0 && db > 0) || (da > 0 && db < 0)) r.Add(a + (b - a) * (da / (da - db)));
            }
            return r;
        }
    }
}
