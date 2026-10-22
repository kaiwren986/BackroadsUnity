// Sites.Validation (card sites-S5 step 1, design §13): every check of the site program on one fresh bake of a town.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Backroads.World.Bake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>What the validation found: errors fail it; notes are the listed exceptions (design §9's grades and the like).</summary>
    public sealed class SiteValidationResult
    {
        public readonly List<string> errors = new List<string>();
        public readonly List<string> lines = new List<string>();
        public int terrainPoints, terrainOver;
        public double terrainWorst;
        public string terrainWorstAt = "";
        public readonly List<string> terrainOffenders = new List<string>();
    }

    /// <summary>
    /// card sites-S5 step 1: the site program's checks on one bake (design §13), each with its count in the report:
    ///   plans complete: every building in play but the accessory ones on a parcel with a plan of its type and source;
    ///   no overlaps: every surface on its own parcel and inside its block (so off the streets and walks, which no block
    ///     holds, and off the other plans, whose parcels tile the blocks), off every building but open roofs;
    ///   stalls inside their lots, each reachable from an entrance through the lot's lanes;
    ///   ADA counts: accessible and van stalls at least the 2010 table's numbers;
    ///   entrances are baked cuts: every lot entrance asked of lane R was baked, moved or merged, not rejected;
    ///   grades: the lot meets every apron within 5 mm; grades and cut/fill outside design §9's limits listed;
    ///   terrain under the surfaces: lane W's bake with the site plates (and lane R's apron plates for the cuts), the
    ///     ground 0.25 m inside the paving at least 3 cm under it (at most 0.1 % of points over, none by 5 cm);
    ///   props clear of stalls, aisles, buildings and doors, inside their parcels;
    ///   every template-sourced plan lists the OSM ids it was made from (its credit);
    ///   determinism: two bakes give the same hash, and the committed bake and manifest are current.
    /// </summary>
    public static class SiteValidation
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static SiteValidationResult Run(SiteBakeResult r, SiteBakeResult second = null, bool terrain = true, bool committed = true)
        {
            var v = new SiteValidationResult();
            var ctx = r.plans.ctx;
            void Check(string what, List<string> bad, string ok)
            {
                if (bad.Count == 0) v.lines.Add($"ok   {what}: {ok}");
                else { v.lines.Add($"FAIL {what}: {bad.Count}, first: {bad[0]}"); foreach (var b in bad) v.errors.Add(what + ": " + b); }
            }

            // ---- plans complete
            var bad = new List<string>();
            int buildings = 0;
            foreach (var b in ctx.data.buildings)
            {
                if (!b.inPlay || b.Accessory) continue;
                buildings++;
                var p = ctx.parcels.ParcelOf(b.index);
                if (p == null) { bad.Add($"{b.osm}: no parcel"); continue; }
                if (!r.plans.byParcel.TryGetValue(p.id, out var plan)) { bad.Add($"{b.osm}: parcel {p.id} has no plan"); continue; }
                if (plan.type != p.type) bad.Add($"{b.osm}: plan type {plan.type} != parcel type {p.type}");
                if (plan.source == PlanSource.Template && string.IsNullOrEmpty(plan.templateId)) bad.Add($"{b.osm}: a template plan without its template");
            }
            foreach (var f in ctx.parcels.fixedAreas)
                if (f.parcel >= 0 && !r.plans.byParcel.ContainsKey(f.parcel)) bad.Add($"{f.osm}: its parcel {f.parcel} has no plan");
            Check("plans complete", bad, $"{buildings} buildings, {r.plans.plans.Count} plans ({string.Join(", ", r.plans.plans.GroupBy(p => p.source).OrderBy(g => g.Key).Select(g => g.Key + " " + g.Count()))})");

            // ---- no overlaps: own parcel, own block, off buildings
            bad = new List<string>();
            int surfaces = 0;
            foreach (var plan in r.plans.plans)
            {
                var parcel = ctx.parcels.parcels[plan.parcel];
                var region = ctx.parcels.regions[plan.parcel];
                var block = parcel.block >= 0 && parcel.block < ctx.parcels.blocks.regions.Count ? ctx.parcels.blocks.regions[parcel.block] : region;
                var walls = new Paths64();
                foreach (var bi in parcel.buildings) if (!ctx.data.buildings[bi].OpenRoof) walls.AddRange(Clip.Region(ctx.data.buildings[bi].poly));
                walls = Clip.Union(walls);
                foreach (var s in plan.surfaces)
                {
                    surfaces++;
                    var sr = Clip.Region(s.outer, s.holes);
                    double off = Clip.Area(Clip.Difference(sr, region));
                    if (off > 0.05) bad.Add($"parcel {plan.parcel}: a {s.use} reaches {off:F2} m² off its parcel");
                    double offBlock = Clip.Area(Clip.Difference(sr, block));
                    if (offBlock > 0.05) bad.Add($"parcel {plan.parcel}: a {s.use} reaches {offBlock:F2} m² off its block (onto a street or walk)");
                    double onB = Clip.Area(Clip.Intersect(sr, walls));
                    if (onB > 0.05) bad.Add($"parcel {plan.parcel}: a {s.use} covers {onB:F2} m² of a building");
                }
            }
            Check("no overlaps", bad, $"{surfaces} surfaces on their own parcels and blocks, off the buildings");

            // ---- stalls inside their lots, reachable
            bad = new List<string>();
            int stalls = 0, lots = 0;
            foreach (var plan in r.plans.plans)
                for (int li = 0; li < plan.lots.Count; li++)
                {
                    var lot = plan.lots[li];
                    if (lot.stalls.Count == 0) continue;
                    lots++;
                    foreach (var st in lot.stalls)
                    {
                        stalls++;
                        if (!Poly2.ConvexInside(st.poly, lot.lot, 0.5)) bad.Add($"parcel {plan.parcel} lot {li}: a stall leaves its lot");
                    }
                    foreach (var why in Unreachable(lot)) bad.Add($"parcel {plan.parcel} lot {li}: {why}");
                }
            Check("stalls inside their lots and reachable", bad, $"{stalls} stalls in {lots} lots, each on a lane reachable from an entrance");

            // ---- ADA counts
            bad = new List<string>();
            int acc = 0, req = 0;
            foreach (var plan in r.plans.plans)
                foreach (var lot in plan.lots)
                {
                    var rep = lot.report;
                    if (rep == null || rep.required == 0) continue;
                    acc += lot.AccessibleCount;
                    req += rep.required;
                    if (lot.AccessibleCount < rep.required) bad.Add($"parcel {plan.parcel}: {lot.AccessibleCount} accessible stalls, {rep.required} required");
                    int van = lot.Count(StallKind.Van);
                    if (van < rep.requiredVan) bad.Add($"parcel {plan.parcel}: {van} van stalls, {rep.requiredVan} required");
                }
            Check("ADA counts", bad, $"{acc} accessible stalls where {req} are required (2010 ADA 208.2)");

            // ---- entrances are baked cuts
            bad = new List<string>();
            int asked = 0;
            var cutOf = new Dictionary<int, DrivewayCut>();
            foreach (var c in r.plans.cuts) if (c != null) cutOf[c.requestId] = c;
            foreach (var plan in r.plans.plans)
                foreach (var lot in plan.lots)
                    foreach (var e in lot.entrances)
                    {
                        if (e.requestId < 0) continue;
                        asked++;
                        if (!cutOf.TryGetValue(e.requestId, out var c)) bad.Add($"parcel {plan.parcel}: entrance {e.requestId} has no answer from lane R");
                        else if (c.outcome == CutOutcome.Rejected) bad.Add($"parcel {plan.parcel}: entrance {e.requestId} was rejected ({c.reason})");
                    }
            Check("entrances are baked cuts", bad, $"{asked} lot entrances from the street, each a baked, moved or merged cut");

            // ---- grades: apron seams (errors), the rest listed
            bad = r.apronSeams.Where(a => a.gap > SiteBake.ApronSeamLimit).Select(a => $"parcel {a.parcel}: request {a.request}: {a.gap * 1000:F1} mm").ToList();
            Check("aprons meet their lots", bad, string.Format(Inv, "{0} aprons within {1:F2} mm", r.apronSeams.Count, r.apronSeams.Select(a => a.gap).DefaultIfEmpty(0).Max() * 1000));
            v.lines.Add($"note grades and cut/fill outside design §9's limits (listed in surfaces-report.txt): " +
                        string.Join(", ", r.grades.GroupBy(g => g.what).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
            v.lines.Add($"note aprons no surface of their plan reaches: {r.unreached.Count} (listed); drive stubs added: {r.stubs.Count(s => !s.Contains("no room"))}");

            // ---- terrain under the paving
            if (terrain)
            {
                Terrain(r, v);
                bad = new List<string>();
                if (v.terrainOver > Math.Max(2, v.terrainPoints / 1000)) bad.Add($"{v.terrainOver} of {v.terrainPoints} points over, worst {v.terrainWorst * 1000:F0} mm at {v.terrainWorstAt}");
                if (v.terrainWorst >= 0.05) bad.Add($"the ground {v.terrainWorst * 1000:F0} mm over at {v.terrainWorstAt}");
                Check("terrain under the surfaces", bad, string.Format(Inv, "{0} points 0.25 m inside the paving, {1} over (worst {2:F0} mm over surface - 3 cm{3})",
                    v.terrainPoints, v.terrainOver, Math.Max(0, v.terrainWorst) * 1000, v.terrainOver > 0 ? " at " + v.terrainWorstAt : ""));
                foreach (var o in v.terrainOffenders) v.lines.Add("     " + o);
            }

            // ---- props clear
            bad = new List<string>();
            foreach (var p in r.placements)
            {
                var q = new double2(p.position.x, p.position.z);
                if (!Clip.Contains(ctx.parcels.regions[p.parcel], q)) bad.Add($"{p.key} p{p.parcel}: off its parcel");
                var plan = r.plans.byParcel[p.parcel];
                foreach (var lot in plan.lots)
                {
                    if (lot.stalls.Any(s => Poly2.Contains(s.poly, q))) bad.Add($"{p.key} p{p.parcel}: on a stall");
                    if (lot.lanes.Any(l => l.poly != null && Poly2.Contains(l.poly, q))) bad.Add($"{p.key} p{p.parcel}: in an aisle");
                }
                foreach (var bi in ctx.parcels.parcels[p.parcel].buildings)
                    if (!ctx.data.buildings[bi].OpenRoof && Poly2.Contains(ctx.data.buildings[bi].poly, q)) bad.Add($"{p.key} p{p.parcel}: inside a building");
                foreach (var (dp, _, _) in SiteGrading.DoorPoints(plan, ctx))
                    if (math.distance(dp, q) < 0.8) bad.Add($"{p.key} p{p.parcel}: in front of a door");
            }
            Check("props clear", bad, $"{r.placements.Count} props inside their parcels, clear of stalls, aisles, buildings and doors ({r.propStats.dropped.Values.Sum()} dropped, listed)");

            // ---- template credits
            bad = r.plans.plans.Where(p => p.source == PlanSource.Template && (p.osmCredit == null || p.osmCredit.Length == 0)).Select(p => $"parcel {p.parcel}: {p.templateId} without its OSM ids").ToList();
            Check("template plans credit their OSM sources", bad, $"{r.plans.plans.Count(p => p.source == PlanSource.Template)} template plans, each with its OSM ids");

            // ---- determinism and the committed bake
            bad = new List<string>();
            if (second != null && second.hash != r.hash) bad.Add($"two bakes differ: {r.hash} vs {second.hash}");
            if (committed)
            {
                var m = SiteBake.ReadManifest(r.town);
                if (m == null) bad.Add("no committed manifest (SitesBakeCli.Bake)");
                else foreach (var d in SiteBake.ManifestDifferences(m, SiteBake.Manifest(r))) bad.Add("stale: " + d);
            }
            Check("bake deterministic and current", bad, $"hash {r.hash.Substring(0, 12)}{(second != null ? ", two bakes agree" : "")}{(committed ? ", the committed bake and manifest current" : "")}");
            return v;
        }

        /// <summary>An entrance opens onto a lane this close (less than a stall's depth: nothing can stand between them).</summary>
        public const double EntranceReach = 3.0;

        /// <summary>A lot's stalls whose lane no entrance reaches through the lot's lanes (lanes joined where they touch).</summary>
        public static List<string> Unreachable(LotLayout lot)
        {
            var o = new List<string>();
            int n = lot.lanes.Count;
            if (lot.entrances.Count == 0) { o.Add($"{lot.stalls.Count} stalls and no entrance"); return o; }
            var regions = lot.lanes.Select(l => l.poly != null && l.poly.Length >= 3 ? Clip.Inflate(Clip.Region(l.poly), 0.25, JoinType.Miter) : new Paths64()).ToList();
            var reach = new bool[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                foreach (var e in lot.entrances)
                {
                    var m = (e.a + e.b) * 0.5;
                    if (lot.lanes[i].poly != null && lot.lanes[i].poly.Length >= 3 && (Poly2.Contains(lot.lanes[i].poly, m) || Poly2.BoundaryDistance(lot.lanes[i].poly, m, out _) < EntranceReach))
                    { if (!reach[i]) { reach[i] = true; queue.Enqueue(i); } }
                }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                for (int j = 0; j < n; j++)
                    if (!reach[j] && regions[i].Count > 0 && regions[j].Count > 0 && Clip.Area(Clip.Intersect(regions[i], regions[j])) > 1e-3)
                    { reach[j] = true; queue.Enqueue(j); }
            }
            int lost = 0;
            foreach (var st in lot.stalls)
                if (st.lane < 0 || st.lane >= n || !reach[st.lane]) lost++;
            if (lost > 0)
            {
                var gaps = lot.entrances.Select(e =>
                {
                    var m = (e.a + e.b) * 0.5;
                    double d = lot.lanes.Where(l => l.poly != null && l.poly.Length >= 3).Select(l => Poly2.Contains(l.poly, m) ? 0 : Poly2.BoundaryDistance(l.poly, m, out _)).DefaultIfEmpty(double.NaN).Min();
                    return string.Format(CultureInfo.InvariantCulture, "({0:F1}, {1:F1}) {2:F1} m from a lane", m.x, m.y, d);
                });
                o.Add($"{lost} of {lot.stalls.Count} stalls on lanes no entrance reaches ({n} lanes; entrances {string.Join(", ", gaps)})");
            }
            return o;
        }

        /// <summary>
        /// Lane W's bake with the site plates and lane R's apron plates for the plans' cuts: the ground 0.25 m inside every
        /// paved surface (not within 0.75 m of a wall, which hides the seam with the building's pad) against the paving as
        /// meshed, which must stand at least 3 cm over it.
        /// </summary>
        public static void Terrain(SiteBakeResult r, SiteValidationResult v)
        {
            var kb = KerbBaker.BuildTown(r.town);
            var cutter = new DrivewayCutter(KerbFrontageSource.LoadTown(r.town));
            var cuts = cutter.Resolve(r.plans.requests);
            var aprons = RoadPlateBuilder.Build(kb.roads.graph, kb.roads.surfaces, kb.kerbs, kb.roads.source?.dem, kb.settings, cuts, cutter)
                .Where(p => p.id.StartsWith("apron:", StringComparison.Ordinal)).ToList();
            var bake = WorldBake.Run(new WorldBakeOptions { townId = r.town, registeredSources = false, extraPlates = r.meshes.plates.Concat(aprons).ToList() });
            var grid = bake.grid;
            var paving = new SurfaceIndex();
            foreach (var part in r.meshes.meshes.parts) if (part.arm == SiteMesher.SurfacePart) paving.Add(part);
            var offenders = new List<(double over, string what, double2 q)>();
            v.terrainWorst = double.MinValue;
            foreach (var plan in r.plans.plans)
            {
                var g = r.meshes.grades[plan.parcel];
                var walls = r.plans.ctx.parcels.parcels[plan.parcel].buildings.Select(bi => r.plans.ctx.data.buildings[bi].poly).ToList();
                foreach (var s in plan.surfaces)
                {
                    if (!SiteMesher.Paved(s.surface)) continue;
                    var inset = Clip.Inflate(Clip.Region(s.outer, s.holes), -0.25, JoinType.Miter);
                    foreach (var lot in plan.lots)
                        foreach (var isl in lot.islands)
                            if (isl.kerbed && isl.poly != null && isl.poly.Length >= 3) inset = Clip.Difference(inset, Clip.Inflate(Clip.Region(isl.poly), 0.25, JoinType.Miter));
                    foreach (var piece in Clip.Pieces(inset))
                    {
                        var outer = Clip.Pts(piece[0]);
                        var holes = piece.Skip(1).Select(Clip.Pts).ToArray();
                        foreach (var q in SiteGrading.Grid(outer, 1.5, 200))
                        {
                            if (holes.Any(h => Poly2.Contains(h, q))) continue;
                            if (walls.Any(b => Poly2.Contains(b, q) || Poly2.BoundaryDistance(b, q, out _) < 0.75)) continue;
                            double ground = grid.Sample(grid.heights, q.x, q.y);
                            if (!paving.Height(new D2(q.x, q.y), out double top)) top = g.H(q);
                            v.terrainPoints++;
                            double over = ground - (top - 0.03);
                            if (over > v.terrainWorst) { v.terrainWorst = over; v.terrainWorstAt = string.Format(Inv, "parcel {0} {1} ({2:F1}, {3:F1})", plan.parcel, s.use, q.x, q.y); }
                            if (over > 1e-4) { v.terrainOver++; offenders.Add((over, $"parcel {plan.parcel} {s.use} {s.surface}", q)); }
                        }
                    }
                }
            }
            foreach (var (over, what, q) in offenders.OrderByDescending(o => o.over).Take(10))
            {
                int i0 = (int)Math.Floor(grid.GridI(q.x)), j0 = (int)Math.Floor(grid.GridJ(q.y));
                var owners = new List<string>();
                for (int dj = 0; dj <= 1; dj++)
                    for (int di = 0; di <= 1; di++)
                    {
                        int k = grid.K(i0 + di, j0 + dj);
                        int ow = bake.applied.owner[k];
                        owners.Add(ow >= 0 && ow < bake.applied.sorted.Count ? bake.applied.sorted[ow].id : "none");
                    }
                v.terrainOffenders.Add(string.Format(Inv, "{0} at ({1:F2}, {2:F2}): {3:F0} mm over; ground from {4}", what, q.x, q.y, over * 1000, string.Join(", ", owners)));
            }
        }

        public static string Report(SiteBakeResult r, SiteValidationResult v)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Sites.Validation (design §13, card sites-S5) on {r.town}: {(v.errors.Count == 0 ? "pass" : v.errors.Count + " errors")}");
            sb.AppendLine(SiteBake.Summary(r));
            foreach (var l in v.lines) sb.AppendLine(l);
            return sb.ToString();
        }

        public static string WriteArtifacts(SiteBakeResult r, SiteValidationResult v)
        {
            var dir = SiteParcels.ArtifactsDir(r.town);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "validation-report.txt");
            File.WriteAllText(path, Report(r, v).Replace("\r\n", "\n"), new UTF8Encoding(false));
            return path;
        }
    }
}
