using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// 05 §8.3, the grade report over a town bake (artifacts/roads/(town)/grades-report.txt). Nothing here fails a bake: the
    /// real ground is steep in places, so every grade over its limit is listed with where it is and why. Four lists:
    ///  - approaches steeper than 6 % within 10 m of a stop line (the same window SurfaceBuilder uses when it moves a stop
    ///    line to the flattest spot within 4 m);
    ///  - junction surfaces leaning more than 3 % (the least-squares plane through the surface mesh; a Plane junction is
    ///    clamped to 3 % by construction, a Major one carries its main road's grade through the node);
    ///  - walks whose cross slope is over 2.1 %;
    ///  - kerb ramps (depressed corners, alley mouths, kerb tapers) whose running slope along the kerb top is over 8.33 %,
    ///    and the flares beside perpendicular ramp openings over PROWAG's 10 %. Running slopes include the street's own
    ///    grade under the ramp (the gutter's slope under the level), which each line gives.
    /// Walk and ramp slopes are read the way an inspector reads them, with a 2 ft (0.61 m) level: across the walk behind
    /// the kerb top and at the back edge of every kerb segment (on the walk surface R3 meshes), and along the kerb top
    /// in 0.1 m steps over each ramp run. A level ignores kinks of a few centimetres that a finer chord would read as a
    /// steep slope (the flowline steps where a ribbon's gutter pan meets a junction outline).
    /// </summary>
    public static class RoadGradeReport
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const double ApproachLimit = 0.06, ApproachWindow = 10.0, PlaneLimit = 0.03, WalkCrossLimit = 0.021, RampLimit = 0.0833, FlareLimit = 0.10;
        /// <summary>The level's length (2 ft) and the step it slides by along a ramp.</summary>
        public const double Level = 0.61, Step = 0.1;

        public sealed class Checks
        {
            public readonly List<string> approaches = new List<string>(), planes = new List<string>(), walkCross = new List<string>(), ramps = new List<string>(), flares = new List<string>();
            /// <summary>Where a level across one face's walk ends on another face's walk more than 5 cm higher or lower (a step in the walk mesh), worst per face.</summary>
            public readonly List<string> walkSteps = new List<string>();
            public double worstWalkStep;
            public int stopLines, junctions, planeJunctions, walkLevels, rampRuns, flareRuns;
            public double worstApproach, worstPlane, worstPlaneMode, worstWalkCross, worstRamp, worstFlare;
            /// <summary>Plane-mode junctions over 3 % (0 by construction: the plane is clamped).</summary>
            public int planesOver;
        }

        public static Checks Run(KerbBaker.Bake k)
        {
            var c = new Checks();
            var g = k.roads.graph;
            var s = k.roads.surfaces;
            var kn = k.kerbs;
            var st = k.settings ?? new KerbSettings();

            // approaches within 10 m of a stop line
            foreach (var ns in s.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback) continue;
                for (int i = 0; i < ns.arms.Count; i++)
                {
                    var a = ns.arms[i];
                    if (a.sStop < 0) continue;
                    c.stopLines++;
                    double worst = 0, at = a.sStop;
                    for (double q = Math.Max(0, a.sStop - ApproachWindow); q <= Math.Min(a.frame.Length, a.sStop + ApproachWindow) + 1e-9; q += 0.5)
                    {
                        double gq = a.frame.Grade(q);
                        if (Math.Abs(gq) > Math.Abs(worst)) { worst = gq; at = q; }
                    }
                    c.worstApproach = Math.Max(c.worstApproach, Math.Abs(worst));
                    if (Math.Abs(worst) <= ApproachLimit + 1e-9) continue;
                    c.approaches.Add(string.Format(Inv, "node {0} arm {1} (R{2}, {3}, {4}): {5:F1} % at {6:F1} m from the node, {7} towards the junction; stop line at {8:F1} m{9}",
                        ns.node, i, g.edges[a.edge].sourceRoad, a.cls, a.control, Math.Abs(worst) * 100, at, worst > 0 ? "falling" : "rising", a.sStop,
                        a.stopMoved ? " (moved to the flattest spot within 4 m)" : ""));
                }
            }

            // junction surfaces: the least-squares plane through the mesh
            foreach (var ns in s.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.vertices.Count < 3 || g.nodes[ns.node].Degree < 3) continue;
                c.junctions++;
                if (ns.mode == SurfaceMode.Plane) c.planeJunctions++;
                double slope = PlaneSlope(ns.vertices, out double ga, out double gb);
                if (ns.mode == SurfaceMode.Plane)
                {
                    double clamped = Math.Sqrt(ns.planeA * ns.planeA + ns.planeB * ns.planeB);
                    c.worstPlaneMode = Math.Max(c.worstPlaneMode, clamped);
                    if (clamped > PlaneLimit + 1e-9) c.planesOver++;
                }
                c.worstPlane = Math.Max(c.worstPlane, slope);
                if (slope <= PlaneLimit + 1e-9) continue;
                var n = g.nodes[ns.node];
                string why = ns.mode == SurfaceMode.Major && ns.majorArmA >= 0 && ns.majorArmB >= 0
                    ? string.Format(Inv, "carries R{0}'s grade through the node", g.edges[ns.arms[ns.majorArmA].edge].sourceRoad)
                    : ns.mode == SurfaceMode.Plane ? "plane clamped to 3 %" : ns.mode.ToString();
                c.planes.Add(string.Format(Inv, "node {0} ({1}, {2} arms, {3}) at ({4:F0}, {5:F0}): {6:F1} % falling towards {7:F0} degrees; {8}",
                    n.id, n.kind, n.Degree, ns.mode, n.p.x, n.p.z, slope * 100, Math.Atan2(-gb, -ga) / Geo.Deg, why));
            }

            // walk cross slopes: a level across the walk at every kerb segment, behind the kerb top and at the back edge
            if (kn != null)
            {
                var walks = new WalkSurface(kn.pieces.Where(p => p.kind == KerbPieceKind.Walk));
                foreach (var f in kn.faces)
                {
                    if (!f.walk || f.bridge) continue;
                    double worst = 0, worstStep = 0;
                    D2 at = default, stepAt = default;
                    int stepOther = -1;
                    for (int i = 0; i + 1 < f.Count; i++)
                    {
                        var seg = f.kerb[i + 1] - f.kerb[i];
                        if (seg.Length < 0.05) continue;
                        var n = seg.Normalized.Left;
                        var k0 = (f.kerb[i] + f.kerb[i + 1]) * 0.5;
                        double w = 0.5 * (f.width[i] + f.width[i + 1]);
                        if (w < st.kerbTop + Level + 0.1) continue;
                        foreach (double u0 in new[] { st.kerbTop + 0.05, w - 0.05 - Level })
                        {
                            D2 pa = k0 + n * u0, pb = k0 + n * (u0 + Level);
                            // the face's own walk at both ends: its cross slope
                            if (walks.Height(pa, f.id, out double ya) && walks.Height(pb, f.id, out double yb))
                            {
                                c.walkLevels++;
                                double cross = Math.Abs(yb - ya) / Level;
                                c.worstWalkCross = Math.Max(c.worstWalkCross, cross);
                                if (cross > worst) { worst = cross; at = (pa + pb) * 0.5; }
                            }
                            // one end on another face's walk: a step where two walks meet
                            if (walks.Height(pa, -1, out double qa, out int fa) && walks.Height(pb, -1, out double qb, out int fb) && fa != fb)
                            {
                                double step = Math.Abs(qb - qa);
                                c.worstWalkStep = Math.Max(c.worstWalkStep, step);
                                if (step > 0.05 && step > worstStep) { worstStep = step; stepAt = (pa + pb) * 0.5; stepOther = fa == f.id ? fb : fa; }
                            }
                        }
                    }
                    if (worst > WalkCrossLimit + 1e-9)
                        c.walkCross.Add(string.Format(Inv, "face {0} (edge {1}, {2}) at ({3:F1}, {4:F1}): cross slope {5:F1} %", f.id, f.edge, f.cls, at.x, at.z, worst * 100));
                    if (worstStep > 0)
                        c.walkSteps.Add(string.Format(Inv, "face {0} (edge {1}, {2}) at ({3:F1}, {4:F1}): its walk meets face {5}'s {6:F2} m higher or lower within 0.61 m", f.id, f.edge, f.cls, stepAt.x, stepAt.z, stepOther, worstStep));
                }

                // ramps and flares: the kerb top's running slope along the loop
                foreach (var cr in kn.corners)
                {
                    if (!cr.kerbed || cr.ramp == RampKind.None) continue;
                    if (cr.ramp == RampKind.Depressed || cr.ramp == RampKind.AlleyMouth)
                    {
                        double run = cr.ramp == RampKind.Depressed ? st.rampRun : st.alleyFlare;
                        Ramp(kn, st, cr.loop, cr.dep0 - run, cr.dep0, $"corner {cr.id} at node {cr.node} ({cr.ramp}), near ramp", RampLimit, c, false);
                        Ramp(kn, st, cr.loop, cr.dep1, cr.dep1 + run, $"corner {cr.id} at node {cr.node} ({cr.ramp}), far ramp", RampLimit, c, false);
                    }
                    else
                    {
                        double half = 0.5 * st.perpRampWidth, flare = (st.barrier - st.dropped) / FlareLimit;
                        foreach (var (o, name) in new[] { (cr.open0, "first"), (cr.open1, "second") })
                        {
                            Ramp(kn, st, cr.loop, o - half - flare, o - half, $"corner {cr.id} at node {cr.node} (perpendicular), {name} opening, near flare", FlareLimit, c, true);
                            Ramp(kn, st, cr.loop, o + half, o + half + flare, $"corner {cr.id} at node {cr.node} (perpendicular), {name} opening, far flare", FlareLimit, c, true);
                        }
                    }
                }
                foreach (var f in kn.faces)
                {
                    if (!f.kerbed || f.bridge) continue;
                    if (f.taperStart) Ramp(kn, st, f.loop, f.t0, f.t0 + st.taper, $"face {f.id} (edge {f.edge}) kerb taper at its start", RampLimit, c, false);
                    if (f.taperEnd) Ramp(kn, st, f.loop, f.t1 - st.taper, f.t1, $"face {f.id} (edge {f.edge}) kerb taper at its end", RampLimit, c, false);
                }
            }
            return c;
        }

        /// <summary>The steepest reading of a 0.61 m level along the kerb top over [a, b] on a loop (0.1 m steps), listed when over the limit.</summary>
        static void Ramp(KerbNetwork kn, KerbSettings st, int loopId, double a, double b, string what, double limit, Checks c, bool flare)
        {
            var loop = kn.loops[loopId];
            double worst = 0, worstStreet = 0, at = a;
            bool any = false;
            double len = Math.Max(b - a, Level);
            double start = b - a < Level ? 0.5 * (a + b) - 0.5 * Level : a;
            int n = Math.Max(0, (int)Math.Floor((len - Level) / Step + 1e-9));
            for (int i = 0; i <= n; i++)
            {
                double t0 = start + i * Step, t1 = t0 + Level;
                if (!TopAt(kn, loop, t0, st, out double y0, out double g0) || !TopAt(kn, loop, t1, st, out double y1, out double g1)) continue;
                any = true;
                double slope = Math.Abs(y1 - y0) / Level;
                if (slope > worst) { worst = slope; worstStreet = Math.Abs(g1 - g0) / Level; at = 0.5 * (t0 + t1); }
            }
            if (!any) return;
            if (flare) { c.flareRuns++; c.worstFlare = Math.Max(c.worstFlare, worst); }
            else { c.rampRuns++; c.worstRamp = Math.Max(c.worstRamp, worst); }
            if (worst <= limit + 1e-9) return;
            var p = LoopPoint(loop, at);
            (flare ? c.flares : c.ramps).Add(string.Format(Inv, "{0} at ({1:F1}, {2:F1}): running slope {3:F1} %, the gutter under it {4:F1} % (limit {5:F2} %)",
                what, p.x, p.z, worst * 100, worstStreet * 100, limit * 100));
        }

        /// <summary>The kerb top and gutter at loop position t, from the kerbed face that holds it.</summary>
        static bool TopAt(KerbNetwork kn, KerbLoop loop, double t, KerbSettings st, out double top, out double gutter)
        {
            top = gutter = double.NaN;
            foreach (int fid in loop.faces)
            {
                var f = kn.faces[fid];
                if (!f.kerbed || f.bridge || f.Count < 2) continue;
                double sq = double.NaN;
                foreach (double wrap in new[] { 0.0, loop.Length, -loop.Length })
                {
                    double q = t + wrap - f.t0;
                    if (q >= -1e-9 && q <= f.Length + 1e-9) { sq = q; break; }
                }
                if (double.IsNaN(sq)) continue;
                sq = Math.Max(0, Math.Min(f.Length, sq));
                int i = 0;
                while (i + 2 < f.Count && f.s[i + 1] < sq) i++;
                double u = f.s[i + 1] - f.s[i] > 1e-12 ? (sq - f.s[i]) / (f.s[i + 1] - f.s[i]) : 0;
                gutter = f.gutter[i] + (f.gutter[i + 1] - f.gutter[i]) * u;
                top = gutter + KerbBuilder.KerbHeight(kn, f, f.t0 + sq, st);
                return true;
            }
            return false;
        }

        static D2 LoopPoint(KerbLoop loop, double t)
        {
            double L = loop.Length;
            t = ((t % L) + L) % L;
            int n = loop.Count;
            for (int k = 0; k < n; k++)
            {
                double t0 = loop.t[k], t1 = k + 1 < n ? loop.t[k + 1] : L;
                if (t > t1 && k + 1 < n) continue;
                var a = loop.pts[k].XZ;
                var b = loop.pts[(k + 1) % n].XZ;
                double u = t1 - t0 > 1e-12 ? (t - t0) / (t1 - t0) : 0;
                return a + (b - a) * Math.Max(0, Math.Min(1, u));
            }
            return loop.pts[0].XZ;
        }

        /// <summary>The slope of the least-squares plane y = a x + b z + c through the points.</summary>
        public static double PlaneSlope(IReadOnlyList<D3> v, out double a, out double b)
        {
            a = b = 0;
            double mx = 0, mz = 0, my = 0;
            foreach (var p in v) { mx += p.x; mz += p.z; my += p.y; }
            mx /= v.Count; mz /= v.Count; my /= v.Count;
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
            foreach (var p in v)
            {
                double x = p.x - mx, z = p.z - mz, y = p.y - my;
                sxx += x * x; sxz += x * z; szz += z * z; sxy += x * y; szy += z * y;
            }
            double det = sxx * szz - sxz * sxz;
            if (Math.Abs(det) < 1e-12) return 0;
            a = (sxy * szz - szy * sxz) / det;
            b = (szy * sxx - sxy * sxz) / det;
            return Math.Sqrt(a * a + b * b);
        }

        /// <summary>The walk surface R3 meshes: the walk pieces (convex, fanned from their first vertex) on a 4 m grid.</summary>
        sealed class WalkSurface
        {
            readonly List<KerbPiece> m_P;
            readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();

            public WalkSurface(IEnumerable<KerbPiece> pieces)
            {
                m_P = pieces.Where(p => p.poly != null && p.poly.Length >= 3).ToList();
                for (int i = 0; i < m_P.Count; i++)
                {
                    var p = m_P[i].poly;
                    int x0 = C(p.Min(q => q.x)), x1 = C(p.Max(q => q.x)), z0 = C(p.Min(q => q.z)), z1 = C(p.Max(q => q.z));
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }

            static int C(double v) => (int)Math.Floor(v / 4);

            /// <summary>The walk height at q on a piece of the given face (any face for -1).</summary>
            public bool Height(D2 q, int face, out double y) => Height(q, face, out y, out _);

            public bool Height(D2 q, int face, out double y, out int onFace)
            {
                y = double.NaN;
                onFace = -1;
                if (!m_Cells.TryGetValue((C(q.x), C(q.z)), out var l)) return false;
                foreach (int i in l)
                {
                    var pc = m_P[i];
                    if (face >= 0 && pc.face != face) continue;
                    for (int k = 1; k + 1 < pc.poly.Length; k++)
                    {
                        D2 a = pc.poly[0], v0 = pc.poly[k] - a, v1 = pc.poly[k + 1] - a, v2 = q - a;
                        double den = v0.Cross(v1);
                        if (Math.Abs(den) < 1e-14) continue;
                        double u = v2.Cross(v1) / den, v = v0.Cross(v2) / den;
                        if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) continue;
                        y = pc.surfaceY[0] + u * (pc.surfaceY[k] - pc.surfaceY[0]) + v * (pc.surfaceY[k + 1] - pc.surfaceY[0]);
                        onFace = pc.face;
                        return true;
                    }
                }
                return false;
            }
        }

        public static string Summary(Checks c) => string.Format(Inv,
            "{0} stop lines, {1} with the approach over 6 % within 10 m (worst {2:F1} %); {3} junction surfaces, {4} leaning over 3 % (worst {5:F1} %; {6} planes, worst {7:F1} %, {8} over); {9} levels across walks, {10} faces with a cross slope over 2.1 % (worst {11:F2} %), {18} faces whose walk meets another face's more than 5 cm apart (worst {19:F2} m); {12} ramp runs, {13} over 8.33 % (worst {14:F1} %); {15} flares, {16} over 10 % (worst {17:F1} %)",
            c.stopLines, c.approaches.Count, c.worstApproach * 100, c.junctions, c.planes.Count, c.worstPlane * 100, c.planeJunctions, c.worstPlaneMode * 100, c.planesOver,
            c.walkLevels, c.walkCross.Count, c.worstWalkCross * 100, c.rampRuns, c.ramps.Count, c.worstRamp * 100, c.flareRuns, c.flares.Count, c.worstFlare * 100,
            c.walkSteps.Count, c.worstWalkStep);

        public static string Write(KerbBaker.Bake k, Checks c = null)
        {
            c = c ?? Run(k);
            var g = k.roads.graph;
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads 05 §8.3 grade report of {g.source}: importer v{OsmRoadImporter.Version}, surfaces v{SurfaceBuilder.Version}, kerbs v{KerbBuilder.Version}");
            sb.AppendLine("Listed, not failed: the real ground is steep in places. Running slopes include the street's grade under the ramp.");
            sb.AppendLine();
            sb.AppendLine("== Summary");
            sb.AppendLine(Summary(c));
            void Section(string title, List<string> lines)
            {
                sb.AppendLine();
                sb.AppendLine($"== {title} ({lines.Count})");
                foreach (var l in lines) sb.AppendLine("  " + l);
            }
            Section("Approaches steeper than 6 % within 10 m of a stop line", c.approaches);
            Section("Junction surfaces leaning more than 3 % (least-squares plane through the mesh)", c.planes);
            Section("Walk cross slopes over 2.1 % (a 0.61 m level across the walk; the worst reading per face)", c.walkCross);
            Section("Steps where two faces' walks meet (a 0.61 m level across one face's walk ending on another's more than 5 cm apart; the worst per face)", c.walkSteps);
            Section("Kerb ramps (depressed corners, alley mouths, tapers) with a running slope over 8.33 % (a 0.61 m level along the kerb top)", c.ramps);
            Section("Flares beside perpendicular ramp openings over 10 %", c.flares);
            var dir = RoadNetworkBaker.TownArtifactsDir(k.town);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "grades-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            return path;
        }
    }
}
