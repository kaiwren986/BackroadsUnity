// Grading of site plans (card sites-S4 step 2, design docs/design/site-layouts.md §9).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>The town's ground before grading: TownData's 1 m DEM (the same samples lane W's terrain and pads start from).</summary>
    public sealed class SiteGround
    {
        public readonly GridHeightField dem;
        public SiteGround(GridHeightField dem) { this.dem = dem; }

        public static SiteGround Load(string repoRoot, string townId)
        {
            var dir = Path.Combine(repoRoot, "TownData", townId, "v1", "dem");
            var j = BrJson.Obj(BrJson.Parse(File.ReadAllText(Path.Combine(dir, "dem.json"), Encoding.UTF8)));
            double N(string k) => Convert.ToDouble(j[k], System.Globalization.CultureInfo.InvariantCulture);
            int nx = (int)N(j.ContainsKey("nx") ? "nx" : "n"), nz = (int)N(j.ContainsKey("nz") ? "nz" : "n");
            var values = GridHeightField.ReadF32(File.ReadAllBytes(Path.Combine(dir, (string)j["file"])));
            return new SiteGround(new GridHeightField(values, nx, nz, N("x0"), N("z0"), N("res")));
        }

        public double At(double2 p) => dem.Sample(p.x, p.y);

        /// <summary>The DEM averaged over a 3 m disc (17 samples): what a drive or walk follows away from its lot.</summary>
        public double Smooth(double2 p)
        {
            double s = 2 * At(p), w = 2;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                var d = new double2(Math.Cos(a), Math.Sin(a));
                s += At(p + d * 1.5) + 0.5 * At(p + d * 3.0);
                w += 1.5;
            }
            return s / w;
        }
    }

    /// <summary>Where a graded surface's height is pinned: a driveway apron's lot edge, a door's sill, the walk's back edge.</summary>
    public enum AnchorKind : byte { Apron = 0, Door = 1, Walk = 2 }

    /// <summary>
    /// One plan's grading (card S4 step 2, design §9): heights are a base field plus anchor corrections. The base is the
    /// plan's platform plane on its lots, forecourts, yards, pads and site aprons (a least-squares plane through its
    /// entrances, the sills of the doors that open onto it and the DEM under it, 40/30/30, its grade clamped to the
    /// strictest limit of what it carries: forecourts and lots with accessible stalls 2 %, lots and pads 5 %, yards 8 %),
    /// easing over 8 m to the smoothed DEM, which drives and walks follow away from the lot. Anchors pin it exactly: lane
    /// R's apron lot edges (DrivewayCut.lotEdgeA/B at lotEdgeYA/YB), door sills (the building's floor, where lane W's pad
    /// and lane A's model stand) and, where a surface meets the public walk away from a cut, the walk's back edge
    /// (KerbFrontage walkBackY). Each anchor is a short segment (0.5 m) or a point, exact along its length; their
    /// differences to the base are blended by inverse distance against the base itself, so a lone anchor's correction
    /// falls off as L / (L + d) with L = |difference| / 3 % (2-40 m): it adds at most 3 % of grade, and between two
    /// anchors the surface runs straight from one to the other.
    /// </summary>
    public sealed class PlanGrade
    {
        public struct Anchor
        {
            public double2 a, b;
            public double ya, yb, da, db, la, lb, w;
            public AnchorKind kind;
            /// <summary>Anchors of one group (a cut, one face's walk edge, a door) are consecutive.</summary>
            public int group;
        }

        public const double PlatformBlend = 8.0, FadeGrade = 0.03, FadeMin = 2.0, FadeMax = 40.0;

        public readonly int parcel;
        readonly SiteGround m_G;
        /// <summary>y = plane.x * x + plane.y * z + plane.z on the platform.</summary>
        public double3 plane;
        public double planeSlope, maxSlope;
        /// <summary>Above maxSlope when the plane had to follow a hillside to stay within the cut/fill limit, else 0.</summary>
        public double relaxed;
        public bool clamped, hasPlatform;
        public readonly List<double2[]> platforms = new List<double2[]>();
        public readonly List<Anchor> anchors = new List<Anchor>();
        /// <summary>Doors too far above (or below) the graded surface to meet it flush: the rise their steps take.</summary>
        public readonly List<(DoorSpec door, double rise)> steps = new List<(DoorSpec, double)>();
        public readonly List<string> notes = new List<string>();

        public PlanGrade(int parcel, SiteGround g) { this.parcel = parcel; m_G = g; }

        public SiteGround Ground => m_G;

        public double Plane(double2 p) => plane.x * p.x + plane.y * p.y + plane.z;

        /// <summary>Distance from p to the nearest platform (0 inside one).</summary>
        public double PlatformDistance(double2 p)
        {
            double best = double.MaxValue;
            foreach (var poly in platforms)
            {
                if (Poly2.Contains(poly, p)) return 0;
                best = Math.Min(best, Poly2.BoundaryDistance(poly, p, out _));
            }
            return best;
        }

        /// <summary>
        /// A drive or walk from the street: heights along it (s from its street edge a-b, inward n) follow the smoothed
        /// ground but climb no faster than MaxGrade from the street edge, nor from its far end where that meets a lot or a
        /// door; flat across. Within Band metres outside it, the surroundings ease into it (so verges and banks stay continuous).
        /// </summary>
        public sealed class Ramp
        {
            public const double Step = 0.5, MaxGrade = 0.10, Band = 3.0;
            public double2 a, b, mid, n;
            public double ya, yb, S;
            public double[] rise;
            public double2[] poly;
            public bool ends;

            /// <summary>The street's tilt across the mouth fades out over this far into the drive (level across beyond).</summary>
            public const double TiltFade = 6.0;

            public double At(double2 p)
            {
                var ab = b - a;
                double L2 = math.lengthsq(ab);
                double t = L2 < 1e-12 ? 0.5 : math.clamp(math.dot(p - a, ab) / L2, 0, 1);
                double s = math.clamp(math.dot(p - mid, n), 0, S);
                double fade = Math.Max(0, 1 - s / TiltFade);
                double edge = (ya + yb) * 0.5 + (ya + (yb - ya) * t - (ya + yb) * 0.5) * fade;
                double f = s / Step;
                int i = (int)Math.Floor(f);
                if (i >= rise.Length - 1) return edge + rise[rise.Length - 1];
                return edge + rise[i] + (rise[i + 1] - rise[i]) * (f - i);
            }
        }

        public readonly List<Ramp> ramps = new List<Ramp>();

        public double Base(double2 p)
        {
            double other = BaseOther(p);
            if (ramps.Count == 0) return other;
            double sw = 0, sr = 0;
            foreach (var r in ramps)
            {
                double d = Poly2.Contains(r.poly, p) ? 0 : Poly2.BoundaryDistance(r.poly, p, out _);
                if (d >= Ramp.Band) continue;
                double t = d / Ramp.Band, w = 1 - t * t * (3 - 2 * t);
                sw += w;
                sr += w * r.At(p);
            }
            if (sw <= 0) return other;
            if (sw >= 1) return sr / sw;
            return sr + (1 - sw) * other;
        }

        /// <summary>The base without the ramps: the platform plane easing to the smoothed DEM.</summary>
        public double BaseOther(double2 p)
        {
            if (!hasPlatform) return m_G.Smooth(p);
            double d = PlatformDistance(p);
            if (d <= 0) return Plane(p);
            if (d >= PlatformBlend) return m_G.Smooth(p);
            double t = d / PlatformBlend, w = 1 - t * t * (3 - 2 * t);
            return w * Plane(p) + (1 - w) * m_G.Smooth(p);
        }

        /// <summary>The graded height (top of the surface) at p.</summary>
        public double H(double2 p)
        {
            double b = Base(p);
            if (anchors.Count == 0) return b;
            // per group (one cut, one face's walk edge, one door): its difference near p by inverse squared distance over its
            // pieces, eased by 1 / (1 + (d / L)^2) with d its distance; the groups blended by inverse squared distance
            double cw = 0, cs = 0;
            int i = 0;
            while (i < anchors.Count)
            {
                int grp = anchors[i].group;
                double num = 0, den = 0, ln = 0, dmin = double.MaxValue;
                for (; i < anchors.Count && anchors[i].group == grp; i++)
                {
                    var an = anchors[i];
                    var ab = an.b - an.a;
                    double L2 = math.lengthsq(ab);
                    double t = L2 < 1e-12 ? 0 : math.clamp(math.dot(p - an.a, ab) / L2, 0, 1);
                    double d = math.distance(p, an.a + ab * t);
                    double D = an.da + (an.db - an.da) * t, L = an.la + (an.lb - an.la) * t;
                    if (d < 1e-9) return an.ya + (an.yb - an.ya) * t;   // on the anchor: its own height
                    double phi = an.w / (d * d);
                    num += phi * D;
                    den += phi;
                    ln += phi * L;
                    dmin = Math.Min(dmin, d);
                }
                double x = dmin / (ln / den);
                double cg = (num / den) / (1 + x * x);
                double wg = 1.0 / dmin;   // between groups by inverse distance: straight from one to the next
                cs += wg * cg;
                cw += wg;
            }
            return b + cs / cw;
        }
        /// <summary>The grade (rise over run) at p, by central differences 0.25 m apart.</summary>
        public double Slope(double2 p)
        {
            const double e = 0.25;
            double gx = (H(p + new double2(e, 0)) - H(p - new double2(e, 0))) / (2 * e);
            double gz = (H(p + new double2(0, e)) - H(p - new double2(0, e))) / (2 * e);
            return Math.Sqrt(gx * gx + gz * gz);
        }

        /// <summary>Doors pull the surface to their sill only this far (beyond, the door gets steps), and add at most 5 % of grade.</summary>
        public const double DoorStepMax = 0.6, DoorFadeGrade = 0.05;

        static double Fade(double d, double grade) => Math.Max(FadeMin, Math.Min(FadeMax, Math.Abs(d) / grade));

        int m_Groups;

        /// <summary>A new anchor group id (anchors of a group must be added together).</summary>
        public int NextGroup() => m_Groups++;

        /// <summary>A point anchor (a door sill).</summary>
        public void AddPoint(double2 p, double y, AnchorKind kind, double fadeGrade = DoorFadeGrade) => AddSegment(p, p, y, y, kind, 1.0, fadeGrade);

        /// <summary>A segment anchor, y linear along it (split into pieces of at most 0.5 m, each exact at its ends).</summary>
        public void AddSegment(double2 a, double2 b, double ya, double yb, AnchorKind kind, double weight = -1, double fadeGrade = FadeGrade, int group = -1)
        {
            if (group < 0) group = NextGroup();
            double len = math.distance(a, b);
            double piece = kind == AnchorKind.Apron ? 0.25 : 0.5;   // aprons: the 5 mm seam
            int n = Math.Max(1, (int)Math.Ceiling(len / piece - 1e-9));
            if (len < 1e-9) n = 1;
            for (int i = 0; i < n; i++)
            {
                double t0 = (double)i / n, t1 = (double)(i + 1) / n;
                var p0 = math.lerp(a, b, t0);
                var p1 = math.lerp(a, b, t1);
                double y0 = ya + (yb - ya) * t0, y1 = ya + (yb - ya) * t1;
                double d0 = y0 - Base(p0), d1 = y1 - Base(p1);
                anchors.Add(new Anchor
                {
                    a = p0, b = p1, ya = y0, yb = y1, da = d0, db = d1, la = Fade(d0, fadeGrade), lb = Fade(d1, fadeGrade),
                    w = weight > 0 ? weight : Math.Max(0.1, len / n), kind = kind, group = group,
                });
            }
        }
    }
    /// <summary>Builds the grading of every plan (card S4 step 2).</summary>
    public static class SiteGrading
    {
        public const float LotSlope = 0.05f, AccessibleSlope = 0.02f, ForecourtSlope = 0.02f, DriveSlope = 0.12f, YardSlope = 0.08f;
        public const double CutFillLimit = 1.5;

        public static bool IsPlatform(SurfaceUse u) => u == SurfaceUse.Lot || u == SurfaceUse.Forecourt || u == SurfaceUse.Yard || u == SurfaceUse.Pad || u == SurfaceUse.Apron;

        /// <summary>The grade limit of a surface use (design §9; accessible stalls are checked on their own).</summary>
        public static float Limit(SurfaceUse u) =>
            u == SurfaceUse.Forecourt ? ForecourtSlope : u == SurfaceUse.Yard ? YardSlope : u == SurfaceUse.Drive || u == SurfaceUse.Walk ? DriveSlope : LotSlope;

        public static PlanGrade Grade(SitePlan plan, PlanContext ctx, SiteGround g)
        {
            var pg = new PlanGrade(plan.parcel, g);
            var parcel = ctx.parcels.parcels[plan.parcel];
            float limit = YardSlope;
            foreach (var s in plan.surfaces)
            {
                if (!IsPlatform(s.use)) continue;
                pg.platforms.Add(s.outer);
                limit = Math.Min(limit, Limit(s.use));
            }
            if (plan.lots.Any(l => l.AccessibleCount > 0)) limit = Math.Min(limit, AccessibleSlope);
            pg.maxSlope = limit;
            pg.hasPlatform = pg.platforms.Count > 0;
            var doors = DoorPoints(plan, ctx);
            // the cuts' lot edges, then where surfaces meet the walk away from them
            var cuts = new List<(double2 a, double2 b, double ya, double yb)>();
            var cutRanges = new List<(int face, double s0, double s1)>();
            var seen = new HashSet<(int, float, float)>();
            foreach (var c in plan.cuts)
            {
                if (c == null || c.outcome == CutOutcome.Rejected) continue;
                if (!seen.Add((c.faceId, c.s0, c.s1))) continue;
                cutRanges.Add((c.faceId, c.s0 - 3.0, c.s1 + 3.0));
                cuts.Add((c.lotEdgeA, c.lotEdgeB, c.lotEdgeYA, c.lotEdgeYB));
            }
            var walks = WalkEdges(plan, parcel, ctx, cutRanges);
            // ---- the platform plane: entrances 40 %, sills 30 %, the DEM 30 % (shares of what there is)
            if (pg.hasPlatform)
            {
                bool Near(double2 q) => pg.platforms.Any(poly => Poly2.Contains(poly, q) || Poly2.BoundaryDistance(poly, q, out _) < 3.0);
                var dem = new List<double2>();
                foreach (var poly in pg.platforms) dem.AddRange(Grid(poly, 2.0, 400));
                var sills = doors.Where(d => Near(d.p)).ToList();
                var ent = new List<(double2 p, double y, double w)>();
                foreach (var (a, b, ya, yb) in cuts.Concat(walks.Select(w => (w.a, w.b, w.ya, w.yb))))
                {
                    var mid = (a + b) * 0.5;
                    if (Near(mid)) ent.Add((mid, (ya + yb) * 0.5, Math.Max(0.1, math.distance(a, b))));
                }
                // the tilt from the ground alone (a few points could tip it anywhere), with a ring round small platforms
                if (dem.Count < 8)
                {
                    var c = Poly2.Centroid(pg.platforms[0]);
                    for (int k = 0; k < 8; k++) dem.Add(c + new double2(Math.Cos(k * Math.PI / 4), Math.Sin(k * Math.PI / 4)) * 4.0);
                }
                var ground = dem.Select(q => (q, g.At(q), 1.0 / dem.Count)).ToList();
                var tilt = Fit(ground, limit, out pg.planeSlope, out pg.clamped);
                if (pg.clamped)
                {
                    // On a hillside the clamped plane would cut and fill too deep: relax its grade (up to the drive limit) just
                    // enough to keep it within CutFillLimit of the ground (the grade is listed in the report either way).
                    double Dev(double3 pl)
                    {
                        double mean = ground.Average(t => t.Item2 - (pl.x * t.Item1.x + pl.y * t.Item1.y));
                        return ground.Max(t => Math.Abs(t.Item2 - (pl.x * t.Item1.x + pl.y * t.Item1.y) - mean));
                    }
                    var free = Fit(ground, 0, out _, out _);
                    double cap = Math.Min(pg.planeSlope, DriveSlope);
                    if (Dev(tilt) > CutFillLimit && cap > limit)
                    {
                        double lo = limit, hi = cap;
                        double3 At(double s) => new double3(free.x * s / pg.planeSlope, free.y * s / pg.planeSlope, 0);
                        if (Dev(At(hi)) <= CutFillLimit)
                            for (int it = 0; it < 30; it++) { double m = 0.5 * (lo + hi); if (Dev(At(m)) <= CutFillLimit) hi = m; else lo = m; }
                        tilt = At(hi);
                        pg.relaxed = hi;
                        pg.notes.Add($"platform plane relaxed to {hi * 100:F1} % to keep within {CutFillLimit} m of the ground (it falls {pg.planeSlope * 100:F1} %)");
                    }
                    else pg.notes.Add($"platform plane clamped to {limit * 100:F0} % (the ground falls {pg.planeSlope * 100:F1} %)");
                }
                // its height: entrances 40 %, sills 30 %, the ground 30 % (shares of what there is)
                double wDem = 0.3, wSill = sills.Count > 0 ? 0.3 : 0, wEnt = ent.Count > 0 ? 0.4 : 0, tot = wDem + wSill + wEnt;
                double Res(double2 q, double y) => y - (tilt.x * q.x + tilt.y * q.y);
                double level = wDem / tot * ground.Average(t => Res(t.Item1, t.Item2));
                if (sills.Count > 0) level += wSill / tot * sills.Average(d => Res(d.p, d.y));
                if (ent.Count > 0) level += wEnt / tot * ent.Sum(e => e.w * Res(e.p, e.y)) / ent.Sum(e => e.w);
                pg.plane = new double3(tilt.x, tilt.y, level);
            }
            // ---- ramps: drives from the street, cut into the bank where the ground is steeper than 10 % (walks follow the
            // grade round them: a walk beside a drive with a profile of its own would leave a bank between the two)
            for (int si = 0; si < plan.surfaces.Count; si++)
            {
                var s = plan.surfaces[si];
                if (s.use != SurfaceUse.Drive) continue;
                (double2 a, double2 b, double ya, double yb)? edge = null;
                foreach (var c in cuts)
                {
                    var m = (c.a + c.b) * 0.5;
                    if (Poly2.Contains(s.outer, m) || Poly2.BoundaryDistance(s.outer, m, out _) < 0.3) { edge = c; break; }
                }
                if (edge == null)
                {
                    var pts = new List<(double2 p, double y)>();
                    foreach (var w in walks) if (w.surface == si) { pts.Add((w.a, w.ya)); pts.Add((w.b, w.yb)); }
                    double best = -1;
                    for (int i = 0; i < pts.Count; i++)
                        for (int j = i + 1; j < pts.Count; j++)
                        {
                            double d = math.distance(pts[i].p, pts[j].p);
                            if (d > best) { best = d; edge = (pts[i].p, pts[j].p, pts[i].y, pts[j].y); }
                        }
                    if (best < 0.5) edge = null;
                }
                if (edge == null) continue;
                var (ea, eb, eya, eyb) = edge.Value;
                var mid = (ea + eb) * 0.5;
                var n = math.normalizesafe(Poly2.Perp(eb - ea));
                if (math.dot(Poly2.Centroid(s.outer) - mid, n) < 0) n = -n;
                double S = s.outer.Max(v => math.dot(v - mid, n));
                if (S < 1.0) continue;
                var r = new PlanGrade.Ramp { a = ea, b = eb, mid = mid, n = n, ya = eya, yb = eyb, S = S, poly = s.outer };
                double y0 = (eya + eyb) * 0.5, k = PlanGrade.Ramp.MaxGrade;
                var far = mid + n * S;
                double? end = null;
                if (pg.hasPlatform && pg.platforms.Any(poly => Poly2.Contains(poly, far) || Poly2.BoundaryDistance(poly, far, out _) < 3.0)) end = pg.BaseOther(far) - y0;
                else
                {
                    var door = doors.Where(d => math.distance(d.p, far) < 3.0).OrderBy(d => math.distance(d.p, far)).Select(d => (double?)d.y).FirstOrDefault();
                    if (door.HasValue) end = door.Value - y0;
                }
                r.ends = end.HasValue;
                int N = (int)Math.Ceiling(S / PlanGrade.Ramp.Step) + 1;
                r.rise = new double[N];
                bool straight = end.HasValue && Math.Abs(end.Value) > k * S;
                for (int i = 0; i < N; i++)
                {
                    double sx = Math.Min(S, i * PlanGrade.Ramp.Step);
                    if (straight) { r.rise[i] = end.Value * sx / S; continue; }
                    double gd = g.Smooth(mid + n * sx) - y0;
                    double lo = -k * sx, hi = k * sx;
                    if (end.HasValue) { lo = Math.Max(lo, end.Value - k * (S - sx)); hi = Math.Min(hi, end.Value + k * (S - sx)); }
                    r.rise[i] = Math.Max(lo, Math.Min(hi, gd));
                }
                pg.ramps.Add(r);
            }
            // ---- anchors: apron lot edges, door sills, the walk's back edge
            foreach (var (a, b, ya, yb) in cuts) pg.AddSegment(a, b, ya, yb, AnchorKind.Apron);
            foreach (var face in walks.GroupBy(w => w.face))
            {
                int gid = pg.NextGroup();
                foreach (var (a, b, ya, yb, _, _) in face) pg.AddSegment(a, b, ya, yb, AnchorKind.Walk, group: gid);
            }
            // doors last, against the surface the others made: flush within 0.6 m, else steps up (or down) to the door
            var doorPts = new List<(double2 p, double y)>();
            foreach (var d in doors)
            {
                if (!plan.surfaces.Any(s => Poly2.Contains(s.outer, d.p) || Poly2.BoundaryDistance(s.outer, d.p, out _) < 1.0)) continue;
                double h0 = pg.H(d.p);
                if (Math.Abs(d.y - h0) <= PlanGrade.DoorStepMax) doorPts.Add((d.p, d.y));
                else pg.steps.Add((d.door, d.y - h0));
            }
            foreach (var (p, y) in doorPts) pg.AddPoint(p, y, AnchorKind.Door);
            return pg;
        }
        /// <summary>Each door's centre on its wall and the floor height of its building (where it meets the ground).</summary>
        public static List<(double2 p, double y, DoorSpec door)> DoorPoints(SitePlan plan, PlanContext ctx)
        {
            var o = new List<(double2, double, DoorSpec)>();
            var byOsm = new Dictionary<string, SiteBuilding>();
            foreach (var bi in ctx.parcels.parcels[plan.parcel].buildings) byOsm[ctx.data.buildings[bi].osm] = ctx.data.buildings[bi];
            foreach (var d in plan.doors)
            {
                if (!byOsm.TryGetValue(d.building, out var b) || double.IsNaN(b.floorY)) continue;
                var (p, outward) = PlanBuilder.DoorAt(b, d);
                o.Add((p + outward * 0.05, b.floorY, d));
            }
            return o;
        }

        /// <summary>Where a surface meets the public walk's back edge away from a cut (a front lot, a walk to the door): the
        /// stretches of its outline within 0.15 m of the back edge, with walkBackY at their ends, so it meets the walk flush.</summary>
        static List<(double2 a, double2 b, double ya, double yb, int face, int surface)> WalkEdges(SitePlan plan, Parcel parcel, PlanContext ctx, List<(int face, double s0, double s1)> cuts)
        {
            var o = new List<(double2, double2, double, double, int, int)>();
            if (ctx.kerbs == null) return o;
            foreach (var fr in parcel.frontage)
            {
                var kf = ctx.kerbs.Face(fr.faceId);
                if (kf == null || kf.walkBack == null || kf.walkBack.Length < 2) continue;
                bool On(double2 q, out double y)
                {
                    y = 0;
                    if (!OnBackEdge(kf, q, 0.15, out double sq, out y)) return false;
                    foreach (var c in cuts) if (c.face == kf.faceId && sq >= c.s0 && sq <= c.s1) return false;
                    return true;
                }
                for (int si = 0; si < plan.surfaces.Count; si++)
                {
                    var ring = plan.surfaces[si].outer;
                    for (int i = 0; i < ring.Length; i++)
                    {
                        double2 a = ring[i], b = ring[(i + 1) % ring.Length];
                        double len = math.distance(a, b);
                        int n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
                        for (int k = 0; k < n; k++)
                        {
                            var q0 = math.lerp(a, b, (double)k / n);
                            var q1 = math.lerp(a, b, (double)(k + 1) / n);
                            if (On(q0, out double y0) && On(q1, out double y1)) o.Add((q0, q1, y0, y1, kf.faceId, si));
                        }
                    }
                }
            }
            return o;
        }
        /// <summary>True when q lies within tol of the face's walk back edge; s and walkBackY there.</summary>
        public static bool OnBackEdge(KerbFrontage f, double2 q, double tol, out double s, out double y)
        {
            s = y = 0;
            double best = double.MaxValue;
            for (int i = 0; i + 1 < f.walkBack.Length; i++)
            {
                double2 a = f.walkBack[i], b = f.walkBack[i + 1], ab = b - a;
                double L2 = math.lengthsq(ab);
                double t = L2 < 1e-12 ? 0 : math.clamp(math.dot(q - a, ab) / L2, 0, 1);
                double d = math.distance(q, a + ab * t);
                if (d < best)
                {
                    best = d;
                    s = f.s[i] + (f.s[i + 1] - f.s[i]) * t;
                    y = f.walkBackY[i] + (f.walkBackY[i + 1] - f.walkBackY[i]) * t;
                }
            }
            return best <= tol;
        }

        /// <summary>Points on a grid inside a polygon (spacing grows until there are at most max of them; the centroid if none).</summary>
        public static List<double2> Grid(double2[] poly, double spacing, int max)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in poly) { x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); }
            while ((x1 - x0) * (y1 - y0) / (spacing * spacing) > max * 2) spacing *= 1.5;
            var o = new List<double2>();
            for (double y = Math.Floor(y0 / spacing) * spacing + spacing * 0.5; y < y1; y += spacing)
                for (double x = Math.Floor(x0 / spacing) * spacing + spacing * 0.5; x < x1; x += spacing)
                {
                    var q = new double2(x, y);
                    if (Poly2.Contains(poly, q)) o.Add(q);
                }
            if (o.Count == 0) o.Add(Poly2.Centroid(poly));
            return o;
        }

        /// <summary>Weighted least-squares plane y = a x + b z + c, its grade clamped to maxSlope (then through the weighted centroid).</summary>
        public static double3 Fit(List<(double2 p, double y, double w)> pts, double maxSlope, out double slope, out bool clamped)
        {
            double W = 0, mx = 0, mz = 0, my = 0;
            foreach (var (p, y, w) in pts) { W += w; mx += w * p.x; mz += w * p.y; my += w * y; }
            clamped = false;
            slope = 0;
            if (W <= 0) return new double3(0, 0, 0);
            mx /= W; mz /= W; my /= W;
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
            foreach (var (p, y, w) in pts)
            {
                double dx = p.x - mx, dz = p.y - mz, dy = y - my;
                sxx += w * dx * dx; sxz += w * dx * dz; szz += w * dz * dz; sxy += w * dx * dy; szy += w * dz * dy;
            }
            double det = sxx * szz - sxz * sxz, a = 0, b = 0;
            if (Math.Abs(det) > 1e-12)
            {
                a = (sxy * szz - szy * sxz) / det;
                b = (szy * sxx - sxy * sxz) / det;
            }
            else if (sxx > 1e-12) a = sxy / sxx;
            else if (szz > 1e-12) b = szy / szz;
            slope = Math.Sqrt(a * a + b * b);
            if (maxSlope > 0 && slope > maxSlope)
            {
                double k = maxSlope / slope;
                a *= k; b *= k;
                clamped = true;
            }
            return new double3(a, b, my - a * mx - b * mz);
        }
    }
}
