using System;
using System.Collections.Generic;

namespace Backroads.Roads
{
    /// <summary>One edge's ribbon: the centreline and its two offset lines (05 §5.2 miter offset), lifted by PlainRibbons.Lift.</summary>
    public sealed class RibbonStrip
    {
        public int edge, sourceRoad;
        public RoadClass cls;
        public double hw, sStart, sEnd;
        public D3[] centre, left, right;
        /// <summary>2D arc length along the centre from the strip's start.</summary>
        public double[] s;
    }

    /// <summary>
    /// R0 "plain ribbons" (05 §9): one flat quad strip per graph edge at the canon centreline heights, full half-width each
    /// side, no junction shaping (R1 replaces them with mouths and junction surfaces). Plain C#; PlainRibbonMesh turns them
    /// into a Unity mesh.
    /// </summary>
    public static class PlainRibbons
    {
        /// <summary>Height above the canon centreline (the M1 ribbons use 0.045 m; this sits just above them).</summary>
        public const double Lift = 0.05;

        public struct Source
        {
            public int edge, sourceRoad;
            public RoadClass cls;
            public double hw, sStart, sEnd;
            public D3[] pts;
        }

        public static List<RibbonStrip> Build(RoadGraph g)
        {
            var src = new List<Source>(g.edges.Count);
            foreach (var e in g.edges)
                src.Add(new Source { edge = e.id, sourceRoad = e.sourceRoad, cls = e.cls, hw = e.hw, sStart = e.sStart, sEnd = e.sEnd, pts = e.pts });
            return Build(src);
        }

        /// <summary>
        /// Builds one strip per source. Consecutive pieces of the same road (one's sEnd equals the next one's sStart) take the
        /// miter across the split, so both strips share their seam vertices exactly.
        /// </summary>
        public static List<RibbonStrip> Build(IEnumerable<Source> sources)
        {
            var src = new List<Source>(sources);
            var list = new List<RibbonStrip>(src.Count);
            foreach (var cur in src)
            {
                D3[] prev = null, next = null;
                foreach (var o in src)
                {
                    if (o.sourceRoad != cur.sourceRoad || o.edge == cur.edge) continue;
                    if (Math.Abs(o.sEnd - cur.sStart) < 1e-6 && cur.sStart > 0) prev = o.pts;
                    if (Math.Abs(o.sStart - cur.sEnd) < 1e-6) next = o.pts;
                }
                int n = cur.pts.Length;
                // extended polyline: [point before the split] + pts + [point after the split]
                var ext = new List<D3>(n + 2);
                int off = 0;
                if (prev != null && prev.Length >= 2) { ext.Add(prev[prev.Length - 2]); off = 1; }
                ext.AddRange(cur.pts);
                if (next != null && next.Length >= 2) ext.Add(next[1]);

                var st = new RibbonStrip
                {
                    edge = cur.edge, sourceRoad = cur.sourceRoad, cls = cur.cls, hw = cur.hw, sStart = cur.sStart, sEnd = cur.sEnd,
                    centre = cur.pts, left = new D3[n], right = new D3[n], s = Polyline.ArcLength(cur.pts),
                };
                for (int i = 0; i < n; i++)
                {
                    var c = cur.pts[i];
                    var m = Polyline.MiterLeft(ext, i + off) * cur.hw;
                    double y = c.y + Lift;
                    st.left[i] = new D3(c.x + m.x, y, c.z + m.z);
                    st.right[i] = new D3(c.x - m.x, y, c.z - m.z);
                }
                list.Add(st);
            }
            return list;
        }
    }

    /// <summary>
    /// "Ribbons cover the canon road centrelines within 0.1 m" (T2.8 done-when), measured four ways against the canon input:
    /// every canon point is a ribbon centre vertex; every ribbon edge vertex is hw from the canon centreline; samples every
    /// 0.25 m along each canon centreline, at the centre and at +-(hw - tolerance), fall inside the road's ribbon, with the
    /// ribbon surface at the canon height + Lift; and each road's edges tile its whole length.
    /// </summary>
    public static class RibbonCoverage
    {
        public sealed class Report
        {
            public int canonPoints, samples, uncoveredSamples, ribbonVertices;
            /// <summary>Largest 3D distance from a canon point to the nearest ribbon centre vertex (lift removed).</summary>
            public double maxPointError;
            /// <summary>Largest |distance from a ribbon edge vertex to the canon centreline - hw|.</summary>
            public double maxHalfWidthError;
            /// <summary>Largest |ribbon surface height - (canon height + Lift)| at the centre samples.</summary>
            public double maxHeightError;
            /// <summary>Largest gap or overlap between consecutive edges of one road, including the road ends.</summary>
            public double maxTilingGap;
            public string firstProblem = "";

            public bool Within(double tol) =>
                uncoveredSamples == 0 && maxPointError <= tol && maxHalfWidthError <= tol && maxHeightError <= tol && maxTilingGap <= tol;

            public override string ToString() =>
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0} canon points, {1} ribbon vertices, {2} samples ({3} uncovered); max point error {4:G3} m, half-width error {5:G3} m, height error {6:G3} m, tiling gap {7:G3} m{8}",
                    canonPoints, ribbonVertices, samples, uncoveredSamples, maxPointError, maxHalfWidthError, maxHeightError, maxTilingGap,
                    firstProblem.Length > 0 ? "; first problem: " + firstProblem : "");
        }

        public static Report Measure(IReadOnlyList<RibbonStrip> strips, RoadGraphInput input, double tolerance = 0.1, double sampleStep = 0.25)
        {
            var rep = new Report();
            var byRoad = new Dictionary<int, List<RibbonStrip>>();
            foreach (var st in strips)
            {
                if (!byRoad.TryGetValue(st.sourceRoad, out var l)) byRoad[st.sourceRoad] = l = new List<RibbonStrip>();
                l.Add(st);
                rep.ribbonVertices += st.left.Length * 2;
            }
            foreach (var road in input.roads)
            {
                if (!byRoad.TryGetValue(road.id, out var mine) || mine.Count == 0)
                {
                    rep.uncoveredSamples++;
                    Problem(rep, $"road {road.id} has no ribbon");
                    continue;
                }
                mine.Sort((a, b) => a.sStart.CompareTo(b.sStart));

                // tiling
                double gap = Math.Abs(mine[0].sStart);
                for (int k = 1; k < mine.Count; k++) gap = Math.Max(gap, Math.Abs(mine[k].sStart - mine[k - 1].sEnd));
                gap = Math.Max(gap, Math.Abs(road.Length - mine[mine.Count - 1].sEnd));
                rep.maxTilingGap = Math.Max(rep.maxTilingGap, gap);

                // canon points are ribbon centre vertices
                foreach (var p in road.pts)
                {
                    rep.canonPoints++;
                    double best = double.MaxValue;
                    foreach (var st in mine)
                        for (int i = 0; i < st.left.Length; i++)
                        {
                            double cx = 0.5 * (st.left[i].x + st.right[i].x), cy = 0.5 * (st.left[i].y + st.right[i].y) - PlainRibbons.Lift,
                                   cz = 0.5 * (st.left[i].z + st.right[i].z);
                            double dx = cx - p.x, dy = cy - p.y, dz = cz - p.z;
                            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                        }
                    if (best > rep.maxPointError) rep.maxPointError = best;
                    if (best > tolerance) Problem(rep, $"road {road.id} point {p} is {best:G3} m from the ribbon centre");
                }

                // ribbon edge vertices are hw from the canon centreline
                foreach (var st in mine)
                    for (int i = 0; i < st.left.Length; i++)
                        foreach (var v in new[] { st.left[i], st.right[i] })
                        {
                            double err = Math.Abs(Polyline.Project(road.pts, road.d, v.XZ).distance - road.hw);
                            if (err > rep.maxHalfWidthError) rep.maxHalfWidthError = err;
                            if (err > tolerance) Problem(rep, $"edge {st.edge} vertex {i} is {err:G3} m off its half-width");
                        }

                // samples along the canon centreline lie inside the ribbon, at the right height
                // the first and last samples sit 1 mm inside the road's ends: exactly at an end they lie on the ribbon's end
                // edge, where float vertices (the baked asset) put half of them outside by ~1e-5 m
                const double endInset = 0.001;
                int steps = Math.Max(1, (int)Math.Ceiling(road.Length / sampleStep));
                for (int k = 0; k <= steps; k++)
                {
                    double s = Math.Max(endInset, Math.Min(road.Length - endInset, k * sampleStep));
                    var c = Polyline.At(road.pts, road.d, s);
                    var t = Tangent(road, s);
                    double off = Math.Max(0, road.hw - tolerance);
                    for (int side = -1; side <= 1; side++)
                    {
                        var q = c.XZ + t.Left * (off * side);
                        rep.samples++;
                        if (!Locate(mine, s, q, out double y))
                        {
                            rep.uncoveredSamples++;
                            Problem(rep, $"road {road.id} at s = {s:F2} side {side} is outside its ribbon");
                            continue;
                        }
                        if (side == 0)
                        {
                            double err = Math.Abs(y - (c.y + PlainRibbons.Lift));
                            if (err > rep.maxHeightError) rep.maxHeightError = err;
                        }
                    }
                }
            }
            return rep;
        }

        static void Problem(Report r, string msg)
        {
            if (r.firstProblem.Length == 0) r.firstProblem = msg;
        }

        static D2 Tangent(RoadGraphInput.Road r, double s)
        {
            int i = 0;
            while (i < r.d.Length - 2 && r.d[i + 1] < s) i++;
            var t = (r.pts[i + 1].XZ - r.pts[i].XZ).Normalized;
            return t;
        }

        /// <summary>Finds a ribbon triangle containing q (searching near s first, then everywhere on the road) and its height there.</summary>
        static bool Locate(List<RibbonStrip> strips, double s, D2 q, out double y)
        {
            foreach (var st in strips)
            {
                if (s < st.sStart - 0.5 || s > st.sEnd + 0.5) continue;
                int i0 = Index(st.s, s - st.sStart);
                for (int i = Math.Max(0, i0 - 3); i < Math.Min(st.left.Length - 1, i0 + 4); i++)
                    if (InQuad(st, i, q, out y)) return true;
            }
            foreach (var st in strips)
                for (int i = 0; i < st.left.Length - 1; i++)
                    if (InQuad(st, i, q, out y)) return true;
            y = double.NaN;
            return false;
        }

        static int Index(double[] s, double v)
        {
            int lo = 0, hi = s.Length - 1;
            if (v <= s[0]) return 0;
            if (v >= s[hi]) return hi;
            while (hi - lo > 1)
            {
                int m = (lo + hi) >> 1;
                if (s[m] <= v) lo = m; else hi = m;
            }
            return lo;
        }

        static bool InQuad(RibbonStrip st, int i, D2 q, out double y) =>
            InTri(st.left[i], st.right[i], st.left[i + 1], q, out y) || InTri(st.right[i], st.right[i + 1], st.left[i + 1], q, out y);

        static bool InTri(D3 a, D3 b, D3 c, D2 q, out double y)
        {
            y = double.NaN;
            D2 v0 = b.XZ - a.XZ, v1 = c.XZ - a.XZ, v2 = q - a.XZ;
            double den = v0.Cross(v1);
            if (Math.Abs(den) < 1e-12) return false;
            double u = v2.Cross(v1) / den, v = v0.Cross(v2) / den;
            const double eps = 1e-9;
            if (u < -eps || v < -eps || u + v > 1 + eps) return false;
            y = a.y + u * (b.y - a.y) + v * (c.y - a.y);
            return true;
        }
    }
}
