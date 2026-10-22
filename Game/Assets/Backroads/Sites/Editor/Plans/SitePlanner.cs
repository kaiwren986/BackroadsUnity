using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>The site plans of a town (card sites-S3) with what they were built from and R's answers.</summary>
    public sealed class SitePlanSet
    {
        public string town;
        public PlanContext ctx;
        public readonly List<SitePlan> plans = new List<SitePlan>();
        public readonly Dictionary<int, SitePlan> byParcel = new Dictionary<int, SitePlan>();
        public readonly List<DrivewayRequest> requests = new List<DrivewayRequest>();
        public DrivewayCut[] cuts = Array.Empty<DrivewayCut>();
        /// <summary>Per parcel, every template tried and why it was rejected (pass 1).</summary>
        public readonly Dictionary<int, List<string>> rejections = new Dictionary<int, List<string>>();
        public readonly List<string> problems = new List<string>();
        public readonly List<string> notes = new List<string>();
        public int passes;
        public string hash;
    }

    /// <summary>
    /// sites-S3: one plan per parcel with a building (and per parcel holding a mapped lot of its own). Sources in order:
    /// the parcel's mapped lots and driveways (kept exactly), a real template of its kind, the kind's rules. Three passes
    /// for the driveways (card step 4): every plan asks for its cuts; R's cutter answers; plans re-plan once against the
    /// answers (a rejected cut is asked again at the next clear spot) and those are answered too; the last pass builds the
    /// geometry from the answers. Deterministic: every choice depends on the site's own seed and inputs.
    /// </summary>
    public static class SitePlanner
    {
        public const int Version = 1;

        public static SitePlanSet Plan(PlanContext ctx, IEnumerable<int> only = null)
        {
            var set = new SitePlanSet { town = ctx.town, ctx = ctx };
            var ids = (only ?? Planned(ctx.parcels)).ToList();
            var choices = new Dictionary<int, TemplateFitter.Choice>();
            List<SitePlan> Run(bool first)
            {
                var list = new List<SitePlan>();
                foreach (var id in ids) list.Add(PlanOne(ctx, ctx.parcels.parcels[id], ctx.parcels.regions[id], choices, first, set));
                return list;
            }
            ctx.answers = null;
            var plans = Run(true);
            set.passes = 1;
            if (ctx.cutter != null)
            {
                var req = plans.SelectMany(p => p.driveways).ToList();
                ctx.answers = Answer(ctx, req);
                plans = Run(false);
                set.passes = 2;
                var req2 = plans.SelectMany(p => p.driveways).ToList();
                if (req2.Any(r => !ctx.answers.ContainsKey(r.id)) || req2.Count != req.Count)
                {
                    ctx.answers = Answer(ctx, req2);
                    plans = Run(false);
                    set.passes = 3;
                }
            }
            foreach (var p in plans)
            {
                set.plans.Add(p);
                set.byParcel[p.parcel] = p;
                foreach (var r in p.driveways)
                {
                    set.requests.Add(r);
                    if (ctx.answers != null && ctx.answers.TryGetValue(r.id, out var c)) p.cuts.Add(c);
                }
            }
            set.cuts = set.plans.SelectMany(p => p.cuts).ToArray();
            Validate(set);
            set.hash = Hash(set);
            return set;
        }

        /// <summary>The parcels that get a plan: every parcel with a main building, every one holding a mapped lot or yard or a
        /// mapped driveway (S1).</summary>
        public static IEnumerable<int> Planned(ParcelSet ps)
        {
            var withDrive = new HashSet<int>(ps.requestParcel.Where(i => i >= 0));
            foreach (var p in ps.parcels)
                if ((p.kind == ParcelKind.Building && p.buildings.Length > 0) || p.fixedAreas.Length > 0 || withDrive.Contains(p.id)) yield return p.id;
        }

        static Dictionary<int, DrivewayCut> Answer(PlanContext ctx, List<DrivewayRequest> req)
        {
            var cuts = ctx.cutter.Resolve(req);
            var d = new Dictionary<int, DrivewayCut>();
            for (int i = 0; i < cuts.Length; i++) d[req[i].id] = cuts[i];
            return d;
        }

        static SitePlan PlanOne(PlanContext ctx, Parcel parcel, Paths64 region, Dictionary<int, TemplateFitter.Choice> choices, bool first, SitePlanSet set)
        {
            var pb = new PlanBuilder(ctx, parcel, region);
            if (parcel.kind != ParcelKind.Building)
            {
                MappedModule.Plan(pb);
                MappedLeftovers(pb);
                pb.plan.source = PlanSource.Mapped;
                pb.plan.type = BusinessType.Unknown;
                pb.plan.notes.Add(parcel.fixedAreas.Length > 0
                    ? "a mapped lot or yard of its own" + (ctx.parcels.fixedAreas[parcel.fixedAreas[0]].servedBy.Length > 0
                        ? " serving " + ctx.data.buildings[ctx.parcels.fixedAreas[parcel.fixedAreas[0]].servedBy[0]].osm : "")
                    : "vacant land with a mapped driveway");
                return pb.plan;
            }
            bool mapped = MappedModule.Plan(pb);
            var t = parcel.type;
            bool downtown = parcel.form == BuildingForm.Streetfront;
            pb.plan.downtown = downtown;
            // Templates for the lot kinds (not when a mapped lot already serves the site).
            var kinds = TemplateFitter.KindsFor(t, downtown);
            bool applied = false;
            if (!mapped && ctx.useTemplates && kinds.Length > 0 && t != BusinessType.House)
            {
                if (first)
                {
                    var rej = new List<string>();
                    double target = Math.Max(3, ratioTarget(pb));
                    choices[parcel.id] = TemplateFitter.Find(pb, kinds, rej, target);
                    set.rejections[parcel.id] = rej;
                }
                if (choices.TryGetValue(parcel.id, out var ch) && ch != null)
                {
                    applied = TemplateFitter.Apply(pb, ch);
                    if (!applied)
                    {
                        pb = new PlanBuilder(ctx, parcel, region);
                        pb.plan.notes.Add($"template {ch.template.id} did not apply this pass: rules");
                        pb.plan.downtown = downtown;
                    }
                }
            }
            if (!applied)
            {
                if (downtown) DowntownModule.Plan(pb, mapped);
                else switch (t)
                {
                    case BusinessType.House:
                    case BusinessType.Unknown:
                    case BusinessType.Accessory:
                        HouseModule.Plan(pb);
                        break;
                    case BusinessType.Fuel:
                    case BusinessType.Roof:
                        FuelModule.Plan(pb);
                        break;
                    case BusinessType.Civic:
                    case BusinessType.Church:
                    case BusinessType.School:
                        CivicModule.Plan(pb, mapped);
                        break;
                    case BusinessType.Warehouse:
                    case BusinessType.Industrial:
                        IndustrialModule.Plan(pb);
                        break;
                    case BusinessType.Farm:
                    case BusinessType.Quarry:
                    case BusinessType.Mill:
                    case BusinessType.Harbor:
                        RuralModule.Plan(pb);
                        break;
                    default:
                        CommercialModule.Plan(pb, mapped);
                        break;
                }
                if (mapped) pb.plan.source = PlanSource.Mapped;
            }
            else
            {
                // Template sites still get their props.
                var svc = pb.plan.doors.FirstOrDefault(d => d.kind == DoorKind.Service && d.building == pb.main.osm);
                if (svc.building != null)
                {
                    var (sp, so) = PlanBuilder.DoorAt(pb.main, svc);
                    pb.Prop("dumpster", sp + so * 3.0 + Poly2.Perp(so) * 3.0, so, "design §7: dumpster behind every commercial building");
                }
            }
            MappedLeftovers(pb);
            pb.plan.type = t;
            return pb.plan;
        }

        static double ratioTarget(PlanBuilder pb) => 1.5 * (pb.main?.area ?? 300) / 30.0;

        /// <summary>Mapped driveways of the parcel (S1) the module did not use: kept as they are, with their drive.</summary>
        static void MappedLeftovers(PlanBuilder pb)
        {
            var used = new HashSet<string>(pb.plan.driveways.Where(d => d.osmWay != null).Select(d => d.osmWay));
            for (int i = 0; i < pb.ctx.parcels.requests.Count; i++)
            {
                if (pb.ctx.parcels.requestParcel[i] != pb.parcel.id) continue;
                var r = pb.ctx.parcels.requests[i];
                if (r.osmWay != null && used.Contains(r.osmWay) || !pb.ctx.fronts.TryGetValue(r.faceId, out var face)) continue;
                var (lo, hi) = pb.Range(r.faceId);
                var t = pb.Entrance(face, 0.5 * (r.s0 + r.s1), r.s1 - r.s0, r.kind, r.surface, Math.Min(lo, r.s0), Math.Max(hi, r.s1), true, r.osmWay);
                if (!t.ok) continue;
                var way = pb.ctx.data.service.FirstOrDefault(w => w.osm == r.osmWay);
                var line = new List<double2> { t.Centre };
                if (way != null) line.AddRange(way.pts.Where(p => Clip.Contains(pb.region, p)));
                if (line.Count >= 2) pb.Surface(PlanBuilder.Strip(line, Math.Max(3.0, t.Width * 0.9)), r.surface, SurfaceUse.Drive);
                pb.plan.osmCredit = pb.plan.osmCredit.Concat(new[] { r.osmWay }).Distinct().ToArray();
            }
        }

        // ------------------------------------------------------------------------------------------ validation
        /// <summary>The card's "no plan overlaps a building, a road footprint, another parcel or a mapped area it does not own",
        /// stalls inside their lots, doors on their buildings.</summary>
        static void Validate(SitePlanSet set)
        {
            var ctx = set.ctx;
            foreach (var plan in set.plans)
            {
                var parcel = ctx.parcels.parcels[plan.parcel];
                var region = ctx.parcels.regions[plan.parcel];
                var all = new Paths64();
                foreach (var s in plan.surfaces)
                {
                    var r = Clip.Region(s.outer, s.holes);
                    double outside = Clip.Area(Clip.Difference(r, region));
                    if (outside > 0.05) set.problems.Add($"parcel {plan.parcel}: a {s.use} surface reaches {outside:F2} m² off the parcel");
                    double onB = Clip.Area(Clip.Intersect(r, ParcelBuildings(ctx, parcel)));
                    if (onB > 0.05) set.problems.Add($"parcel {plan.parcel}: a {s.use} surface covers {onB:F2} m² of a building");
                    double over = Clip.Area(Clip.Intersect(r, all));
                    if (over > 0.05) set.problems.Add($"parcel {plan.parcel}: surfaces overlap by {over:F2} m²");
                    all.AddRange(r);
                    all = Clip.Union(all);
                }
                // Mapped areas the parcel does not own lie on other parcels (S1), so "inside its own parcel" covers them; the
                // one exception, a building's standing room inside another's mapped area, is that building's own land.
                foreach (var lot in plan.lots)
                    foreach (var st in lot.stalls)
                        if (!Poly2.ConvexInside(st.poly, lot.lot, 0.5 - 1e-6)) { set.problems.Add($"parcel {plan.parcel}: a stall leaves its lot"); break; }
                var mine = new HashSet<string>(parcel.buildings.Select(i => ctx.data.buildings[i].osm));
                foreach (var d in plan.doors)
                {
                    if (!mine.Contains(d.building)) set.problems.Add($"parcel {plan.parcel}: a door on {d.building}, not its building");
                    var b = ctx.data.buildings.First(x => x.osm == d.building);
                    if (d.wallEdge < 0 || d.wallEdge >= b.poly.Length) set.problems.Add($"parcel {plan.parcel}: door wall {d.wallEdge} of {b.osm}");
                    else if (d.offset < 0 || d.offset > math.distance(b.poly[d.wallEdge], b.poly[(d.wallEdge + 1) % b.poly.Length]) + 1e-3)
                        set.problems.Add($"parcel {plan.parcel}: door offset {d.offset:F2} off its wall on {b.osm}");
                }
            }
        }

        static Paths64 ParcelBuildings(PlanContext ctx, Parcel p)
        {
            var r = new Paths64();
            foreach (var i in p.buildings) if (!ctx.data.buildings[i].OpenRoof) r.AddRange(Clip.Region(ctx.data.buildings[i].poly));
            return Clip.Union(r);
        }

        // ------------------------------------------------------------------------------------------ hash
        public static string Hash(SitePlanSet set)
        {
            var sb = new StringBuilder(1 << 18);
            var ci = CultureInfo.InvariantCulture;
            string M(double v) => Math.Round(v * 1000).ToString("R", ci);
            void Ring(double2[] r) { if (r == null) { sb.Append('-'); return; } foreach (var q in r) sb.Append(M(q.x)).Append(',').Append(M(q.y)).Append(';'); sb.Append('|'); }
            sb.Append("v").Append(Version).Append(';').Append(ParkingPacker.Version).Append(';').Append(set.town).Append('\n');
            foreach (var p in set.plans)
            {
                sb.Append(p.parcel).Append(':').Append((int)p.type).Append(':').Append((int)p.source).Append(':').Append(p.templateId).Append(p.mirrored ? 'm' : '-').Append(p.downtown ? 'd' : '-').Append('\n');
                foreach (var s in p.surfaces) { sb.Append((int)s.surface).Append((int)s.use); Ring(s.outer); foreach (var h in s.holes) Ring(h); }
                foreach (var l in p.lots) sb.Append(LotLayoutHash.Compute(l));
                foreach (var d in p.driveways) sb.Append(d.id).Append('@').Append(d.faceId).Append(':').Append(M(d.s0)).Append('-').Append(M(d.s1)).Append((int)d.kind).Append((int)d.surface).Append(d.mapped ? 'M' : '-');
                foreach (var c in p.cuts) sb.Append((int)c.outcome).Append(M(c.s0)).Append(M(c.s1));
                foreach (var d in p.doors) sb.Append(d.building).Append((int)d.kind).Append(d.wallEdge).Append(M(d.offset)).Append(M(d.width));
                foreach (var x in p.props) { sb.Append(x.key); sb.Append(M(x.pos.x)).Append(M(x.pos.y)).Append(Math.Round(x.yaw * 100).ToString("R", ci)); }
                foreach (var x in p.models) { sb.Append(x.key); Ring(x.footprint); }
                foreach (var x in p.fences) { sb.Append((int)x.kind); Ring(x.line); }
                foreach (var x in p.gates) { sb.Append(M(x.a.x)).Append(M(x.a.y)).Append(M(x.b.x)).Append(M(x.b.y)); }
                sb.Append('\n');
            }
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(64);
                foreach (var x in h) hex.Append(x.ToString("x2", ci));
                return hex.ToString();
            }
        }
    }
}
