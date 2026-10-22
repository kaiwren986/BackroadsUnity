using System;
using System.Collections.Generic;

namespace Backroads.Roads
{
    /// <summary>
    /// The frame of a centreline (roads-R1, 05 §5.2): F(s, u) = c(s) + m(s) u, where c is the polyline at arc length s and m
    /// interpolates linearly, segment by segment, between the vertex miter vectors (05's Offset: the average of the
    /// adjacent segment normals, miter length capped at 2). Every edge line, offset, mouth line and ribbon vertex comes
    /// from this one function, so they agree to the last bit: the offset polyline at u has the vertices c_i + m_i u and is
    /// straight in between, exactly as F is. +u is the left (counter-clockwise) side of the direction of travel.
    /// </summary>
    public sealed class RoadFrame
    {
        public readonly D2[] c;
        public readonly double[] y, s;
        public readonly D2[] m;

        public RoadFrame(IReadOnlyList<D3> pts)
        {
            int n = pts.Count;
            if (n < 2) throw new ArgumentException("a frame needs two points");
            c = new D2[n]; y = new double[n]; s = new double[n]; m = new D2[n];
            for (int i = 0; i < n; i++) { c[i] = pts[i].XZ; y[i] = pts[i].y; }
            for (int i = 1; i < n; i++) s[i] = s[i - 1] + D2.Distance(c[i - 1], c[i]);
            for (int i = 0; i < n; i++) m[i] = Polyline.MiterLeft(pts, i);
        }

        RoadFrame(D2[] c, double[] y, double[] s, D2[] m) { this.c = c; this.y = y; this.s = s; this.m = m; }

        public double Length => s[s.Length - 1];
        public int Count => c.Length;

        /// <summary>The same road, run the other way (s' = L - s; left and right swap, so m flips).</summary>
        public RoadFrame Reversed()
        {
            int n = c.Length;
            var c2 = new D2[n]; var y2 = new double[n]; var s2 = new double[n]; var m2 = new D2[n];
            double L = Length;
            for (int i = 0; i < n; i++)
            {
                c2[i] = c[n - 1 - i]; y2[i] = y[n - 1 - i]; s2[i] = L - s[n - 1 - i]; m2[i] = m[n - 1 - i] * -1;
            }
            s2[0] = 0;
            return new RoadFrame(c2, y2, s2, m2);
        }

        /// <summary>The segment holding sq (clamped) and the parameter t in it.</summary>
        public int Seg(double sq, out double t)
        {
            int n = s.Length;
            if (sq <= 0) { t = 0; return 0; }
            if (sq >= s[n - 1]) { t = 1; return n - 2; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int k = (lo + hi) >> 1; if (s[k] <= sq) lo = k; else hi = k; }
            double l = s[hi] - s[lo];
            t = l > 0 ? (sq - s[lo]) / l : 0;
            return lo;
        }

        public D2 P(double sq, double u)
        {
            int i = Seg(sq, out double t);
            return Lerp(c[i], c[i + 1], t) + Lerp(m[i], m[i + 1], t) * u;
        }

        public D2 Centre(double sq) => P(sq, 0);

        /// <summary>The centreline height at sq (linear between points).</summary>
        public double Y(double sq)
        {
            int i = Seg(sq, out double t);
            return y[i] + (y[i + 1] - y[i]) * t;
        }

        /// <summary>The unit direction of the segment holding sq.</summary>
        public D2 Dir(double sq)
        {
            int i = Seg(sq, out _);
            return (c[i + 1] - c[i]).Normalized;
        }

        /// <summary>The offset polyline at lateral distance u (left positive), as vertices.</summary>
        public D2[] Offset(double u)
        {
            var o = new D2[c.Length];
            for (int i = 0; i < c.Length; i++) o[i] = c[i] + m[i] * u;
            return o;
        }

        /// <summary>The grade dy/ds of the centreline at sq, over +-half metres.</summary>
        public double Grade(double sq, double half = 1.5)
        {
            double a = Math.Max(0, sq - half), b = Math.Min(Length, sq + half);
            return b - a > 1e-6 ? (Y(b) - Y(a)) / (b - a) : 0;
        }

        static D2 Lerp(D2 a, D2 b, double t) => new D2(a.x + (b.x - a.x) * t, a.z + (b.z - a.z) * t);

        /// <summary>
        /// The first crossing of the offset lines (this frame at u, other at v) with this frame's s in (sMin, sMax]: the
        /// crossing with the smallest s on this frame (walking outward from the node). False when they do not cross.
        /// </summary>
        public bool FirstCrossing(double u, RoadFrame other, double v, double sMax, double otherMax, out double sa, out double sb, out D2 p)
        {
            sa = sb = double.NaN;
            p = default;
            var A = Offset(u);
            var B = other.Offset(v);
            double best = double.MaxValue;
            for (int i = 0; i + 1 < A.Length && s[i] <= sMax; i++)
                for (int j = 0; j + 1 < B.Length && other.s[j] <= otherMax; j++)
                {
                    if (!Geom2.SegmentCross(A[i], A[i + 1], B[j], B[j + 1], out double ta, out double tb)) continue;
                    double s1 = s[i] + ta * (s[i + 1] - s[i]);
                    if (s1 >= best) continue;
                    best = s1;
                    sa = s1;
                    sb = other.s[j] + tb * (other.s[j + 1] - other.s[j]);
                    p = A[i] + (A[i + 1] - A[i]) * ta;
                }
            return best < double.MaxValue;
        }

        /// <summary>The arc length on the centre where the offset line at u passes closest to q, and that distance.</summary>
        public double Project(D2 q, double u, out double distance, double sMax = double.MaxValue)
        {
            var A = Offset(u);
            distance = double.MaxValue;
            double best = 0;
            for (int i = 0; i + 1 < A.Length && s[i] <= sMax; i++)
            {
                D2 e = A[i + 1] - A[i];
                double l2 = e.Dot(e);
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, (q - A[i]).Dot(e) / l2)) : 0;
                double d = D2.Distance(A[i] + e * t, q);
                if (d < distance) { distance = d; best = s[i] + t * (s[i + 1] - s[i]); }
            }
            return best;
        }

        /// <summary>The (s, u) of a plan point in this frame: s by projection onto the centreline, u signed (left positive).</summary>
        public void ToFrame(D2 q, out double sq, out double u)
        {
            sq = Project(q, 0, out double d);
            int i = Seg(sq, out _);
            var dir = (c[i + 1] - c[i]).Normalized;
            double side = dir.Cross(q - Centre(sq));
            u = side >= 0 ? d : -d;
        }
    }

    /// <summary>Plan-view polygon helpers for the junction surfaces (CCW = counter-clockwise seen from above, x right, z up).</summary>
    public static class Geom2
    {
        /// <summary>Proper or touching crossing of segments a0-a1 and b0-b1, with the parameters on each (in [0, 1]).</summary>
        public static bool SegmentCross(D2 a0, D2 a1, D2 b0, D2 b1, out double ta, out double tb)
        {
            ta = tb = 0;
            D2 r = a1 - a0, q = b1 - b0;
            double den = r.Cross(q);
            if (Math.Abs(den) < 1e-14) return false;
            D2 w = b0 - a0;
            ta = w.Cross(q) / den;
            tb = w.Cross(r) / den;
            return ta >= 0 && ta <= 1 && tb >= 0 && tb <= 1;
        }

        public static double SignedArea(IReadOnlyList<D2> p)
        {
            double a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].x * p[i].z - p[i].x * p[j].z;
            return 0.5 * a;
        }

        /// <summary>Drops points within tol of the previous one (and the last of the first); keeps the order.</summary>
        public static List<D2> Weld(IReadOnlyList<D2> p, double tol)
        {
            var o = new List<D2>();
            foreach (var q in p)
                if (o.Count == 0 || D2.Distance(o[o.Count - 1], q) > tol) o.Add(q);
            while (o.Count > 2 && D2.Distance(o[0], o[o.Count - 1]) <= tol) o.RemoveAt(o.Count - 1);
            return o;
        }

        /// <summary>True when no two non-adjacent edges touch (O(n^2), 05 §5.5) and no vertex repeats.</summary>
        public static bool IsSimple(IReadOnlyList<D2> p, out string why)
        {
            why = "";
            int n = p.Count;
            if (n < 3) { why = "fewer than 3 points"; return false; }
            for (int i = 0; i < n; i++)
            {
                D2 a0 = p[i], a1 = p[(i + 1) % n];
                if (D2.Distance(a0, a1) < 1e-9) { why = $"repeated point {i}"; return false; }
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                    D2 b0 = p[j], b1 = p[(j + 1) % n];
                    if (SegmentCross(a0, a1, b0, b1, out _, out _)) { why = $"edges {i} and {j} cross near {a0}"; return false; }
                }
            }
            return true;
        }

        /// <summary>Star-shaped about q: q inside and the angle around q increasing along the boundary (one full turn).</summary>
        public static bool IsStarShaped(IReadOnlyList<D2> p, D2 q)
        {
            if (!TownZone.InPolygon(p, q)) return false;
            double turn = 0;
            for (int i = 0; i < p.Count; i++)
            {
                D2 a = p[i] - q, b = p[(i + 1) % p.Count] - q;
                if (a.Length < 1e-6 || b.Length < 1e-6) return false;
                double d = Math.Atan2(a.Cross(b), a.Dot(b));
                if (d <= 1e-9) return false;
                turn += d;
            }
            return Math.Abs(turn - 2 * Math.PI) < 1e-6;
        }

        /// <summary>Ear clipping of a simple CCW polygon (deterministic: the first ear in index order each round).</summary>
        public static List<int> EarClip(IReadOnlyList<D2> p)
        {
            var idx = new List<int>();
            for (int i = 0; i < p.Count; i++) idx.Add(i);
            var tris = new List<int>();
            int guard = 0;
            while (idx.Count > 3 && guard++ < 100000)
            {
                bool cut = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                    D2 a = p[ia], b = p[ib], c = p[ic];
                    if ((b - a).Cross(c - b) <= 1e-12) continue; // reflex or flat
                    bool inside = false;
                    foreach (int other in idx)
                    {
                        if (other == ia || other == ib || other == ic) continue;
                        if (InTriangle(p[other], a, b, c)) { inside = true; break; }
                    }
                    if (inside) continue;
                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    idx.RemoveAt(k);
                    cut = true;
                    break;
                }
                if (!cut) break; // degenerate: give up (the caller reports it)
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            return tris;
        }

        static bool InTriangle(D2 q, D2 a, D2 b, D2 c) =>
            (b - a).Cross(q - a) >= -1e-12 && (c - b).Cross(q - b) >= -1e-12 && (a - c).Cross(q - c) >= -1e-12;

        /// <summary>Shortest distance from q to the closed polygon's boundary.</summary>
        public static double DistanceToBoundary(IReadOnlyList<D2> poly, D2 q)
        {
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) best = Math.Min(best, Polyline.SegmentDistance(poly[j], poly[i], q));
            return best;
        }
    }
}
