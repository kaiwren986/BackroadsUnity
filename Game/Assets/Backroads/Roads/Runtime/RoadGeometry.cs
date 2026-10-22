using System;
using System.Collections.Generic;

namespace Backroads.Roads
{
    /// <summary>
    /// A point in the Unity ground plane (x, z), double precision. Angles are atan2(z, x): counter-clockwise seen from
    /// above (+Y), so Left() is the counter-clockwise normal (-z, x), as in 05 §5.2.
    /// </summary>
    public readonly struct D2
    {
        public readonly double x, z;
        public D2(double x, double z) { this.x = x; this.z = z; }
        public static D2 operator +(D2 a, D2 b) => new D2(a.x + b.x, a.z + b.z);
        public static D2 operator -(D2 a, D2 b) => new D2(a.x - b.x, a.z - b.z);
        public static D2 operator *(D2 a, double k) => new D2(a.x * k, a.z * k);
        public static D2 operator *(double k, D2 a) => new D2(a.x * k, a.z * k);
        public double Dot(D2 b) => x * b.x + z * b.z;
        public double Cross(D2 b) => x * b.z - z * b.x;
        public double Length => Math.Sqrt(x * x + z * z);
        public D2 Normalized { get { double l = Length; return l > 0 ? new D2(x / l, z / l) : new D2(0, 0); } }
        public D2 Left => new D2(-z, x);
        public D2 Right => new D2(z, -x);
        public double Angle => Math.Atan2(z, x);
        public static double Distance(D2 a, D2 b) => (a - b).Length;
        public static D2 FromAngle(double a) => new D2(Math.Cos(a), Math.Sin(a));
        public override string ToString() => $"({x:F2}, {z:F2})";
    }

    /// <summary>A centreline point in Unity space, double precision.</summary>
    public readonly struct D3
    {
        public readonly double x, y, z;
        public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public D2 XZ => new D2(x, z);
        public static D3 Lerp(D3 a, D3 b, double t) => new D3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public override string ToString() => $"({x:F3}, {y:F3}, {z:F3})";
    }

    public static class Geo
    {
        public const double Deg = Math.PI / 180.0;

        /// <summary>Wraps an angle to (-pi, pi].</summary>
        public static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a <= -Math.PI) a += 2 * Math.PI;
            return a;
        }

        /// <summary>Wraps an angle to [0, 2 pi).</summary>
        public static double WrapPositive(double a)
        {
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            while (a < 0) a += 2 * Math.PI;
            return a;
        }

        /// <summary>How far a pair of arms bends from a straight line: |pi - |angle difference||, in [0, pi].</summary>
        public static double Bend(double angleA, double angleB) => Math.Abs(Math.PI - Math.Abs(Wrap(angleA - angleB)));
    }

    /// <summary>Polyline helpers in the ground plane, over points with a precomputed arc-length array.</summary>
    public static class Polyline
    {
        public struct Hit
        {
            public double distance, s, t;
            public int segment;
        }

        /// <summary>2D cumulative arc length of a polyline.</summary>
        public static double[] ArcLength(IReadOnlyList<D3> pts)
        {
            var s = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) s[i] = s[i - 1] + D2.Distance(pts[i - 1].XZ, pts[i].XZ);
            return s;
        }

        /// <summary>Closest point of the polyline to p in the ground plane (first one wins on ties, so the result is deterministic).</summary>
        public static Hit Project(IReadOnlyList<D3> pts, IReadOnlyList<double> s, D2 p)
        {
            var best = new Hit { distance = double.MaxValue };
            for (int i = 0; i < pts.Count - 1; i++)
            {
                D2 a = pts[i].XZ, e = pts[i + 1].XZ - a;
                double l2 = e.Dot(e);
                double t = l2 > 0 ? (p - a).Dot(e) / l2 : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double d = D2.Distance(a + e * t, p);
                if (d < best.distance)
                {
                    best.distance = d;
                    best.segment = i;
                    best.t = t;
                    best.s = s[i] + t * (s[i + 1] - s[i]);
                }
            }
            return best;
        }

        /// <summary>The point at arc length sq (clamped to the ends), interpolating x, y and z linearly within a segment.</summary>
        public static D3 At(IReadOnlyList<D3> pts, IReadOnlyList<double> s, double sq)
        {
            int n = pts.Count;
            if (sq <= s[0]) return pts[0];
            if (sq >= s[n - 1]) return pts[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int m = (lo + hi) >> 1;
                if (s[m] <= sq) lo = m; else hi = m;
            }
            double L = s[hi] - s[lo];
            return L > 0 ? D3.Lerp(pts[lo], pts[hi], (sq - s[lo]) / L) : pts[lo];
        }

        /// <summary>
        /// Offset vertex normal at point i (05 §5.2): the average of the adjacent segment normals (left = CCW), scaled so the
        /// offset line keeps its distance from both segments, with the miter length capped at 2.
        /// </summary>
        public static D2 MiterLeft(IReadOnlyList<D3> pts, int i)
        {
            int n = pts.Count;
            D2 nPrev = default, nNext = default;
            bool hasPrev = i > 0, hasNext = i < n - 1;
            if (hasPrev) nPrev = (pts[i].XZ - pts[i - 1].XZ).Normalized.Left;
            if (hasNext) nNext = (pts[i + 1].XZ - pts[i].XZ).Normalized.Left;
            if (!hasPrev) return nNext;
            if (!hasNext) return nPrev;
            var avg = (nPrev + nNext).Normalized;
            double c = avg.Dot(nNext);
            if (c < 0.5) c = 0.5; // miter length 1/c capped at 2
            return avg * (1.0 / c);
        }

        /// <summary>Distance from p to the segment a-b in the ground plane.</summary>
        public static double SegmentDistance(D2 a, D2 b, D2 p)
        {
            D2 e = b - a;
            double l2 = e.Dot(e);
            double t = l2 > 0 ? (p - a).Dot(e) / l2 : 0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return D2.Distance(a + e * t, p);
        }
    }

    /// <summary>
    /// The "town kerb zone" of 05 §5.1 step 3: the canon's convex hull of the street centrelines + a buffer, or a town's
    /// measured zone polygon (TownData manifest zones.kerbZone, any simple polygon, no buffer).
    /// </summary>
    public sealed class TownZone
    {
        public readonly D2[] Hull;   // counter-clockwise
        public readonly double Buffer;
        /// <summary>True when Hull is a given polygon (point-in-polygon test, no buffer) rather than a computed convex hull.</summary>
        public readonly bool IsPolygon;

        public TownZone(IEnumerable<D2> points, double buffer)
        {
            Hull = ConvexHull(points);
            Buffer = buffer;
        }

        TownZone(D2[] polygon)
        {
            Hull = (D2[])polygon.Clone();
            Buffer = 0;
            IsPolygon = true;
        }

        /// <summary>A zone that is exactly this polygon.</summary>
        public static TownZone FromPolygon(D2[] polygon) => new TownZone(polygon);

        public bool Contains(D2 p) => IsPolygon ? InPolygon(Hull, p) : InHull(p) || DistanceToHull(p) <= Buffer;

        /// <summary>Even-odd point-in-polygon test (any simple polygon, either winding).</summary>
        public static bool InPolygon(IReadOnlyList<D2> poly, D2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                D2 a = poly[i], b = poly[j];
                if ((a.z > p.z) != (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x) inside = !inside;
            }
            return inside;
        }

        public bool InHull(D2 p)
        {
            if (IsPolygon) return InPolygon(Hull, p);
            if (Hull.Length < 3) return false;
            for (int i = 0; i < Hull.Length; i++)
                if ((Hull[(i + 1) % Hull.Length] - Hull[i]).Cross(p - Hull[i]) < 0) return false;
            return true;
        }

        public double DistanceToHull(D2 p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < Hull.Length; i++)
                best = Math.Min(best, Polyline.SegmentDistance(Hull[i], Hull[(i + 1) % Hull.Length], p));
            return best;
        }

        /// <summary>The buffered boundary as a closed polygon (hull edges pushed out by Buffer, round corners every 10 degrees).</summary>
        public List<D2> Outline()
        {
            if (IsPolygon) return new List<D2>(Hull);
            var o = new List<D2>();
            int n = Hull.Length;
            for (int i = 0; i < n; i++)
            {
                D2 prev = Hull[(i + n - 1) % n], cur = Hull[i], next = Hull[(i + 1) % n];
                double a0 = (cur - prev).Normalized.Right.Angle, a1 = (next - cur).Normalized.Right.Angle;
                double sweep = Geo.WrapPositive(a1 - a0);
                if (sweep > Math.PI) sweep = 0;
                int steps = Math.Max(1, (int)Math.Ceiling(sweep / (10 * Geo.Deg)));
                for (int k = 0; k <= steps; k++) o.Add(cur + D2.FromAngle(a0 + sweep * k / steps) * Buffer);
            }
            return o;
        }

        /// <summary>Andrew's monotone chain; counter-clockwise, collinear points dropped. Deterministic for a given point order.</summary>
        public static D2[] ConvexHull(IEnumerable<D2> points)
        {
            var p = new List<D2>(points);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            if (p.Count < 3) return p.ToArray();
            var lo = new List<D2>();
            var up = new List<D2>();
            foreach (var q in p)
            {
                while (lo.Count >= 2 && (lo[lo.Count - 1] - lo[lo.Count - 2]).Cross(q - lo[lo.Count - 2]) <= 0) lo.RemoveAt(lo.Count - 1);
                lo.Add(q);
            }
            for (int i = p.Count - 1; i >= 0; i--)
            {
                var q = p[i];
                while (up.Count >= 2 && (up[up.Count - 1] - up[up.Count - 2]).Cross(q - up[up.Count - 2]) <= 0) up.RemoveAt(up.Count - 1);
                up.Add(q);
            }
            lo.RemoveAt(lo.Count - 1);
            up.RemoveAt(up.Count - 1);
            lo.AddRange(up);
            return lo.ToArray();
        }
    }
}
