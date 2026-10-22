using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Sites;
using Backroads.Sites.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>
    /// Sites.SitePlans (card sites-S3 "done when") on the town: every non-accessory building has a plan with its source and
    /// business type; the report shows the sources per type and every rejected template with the reason; no plan overlaps a
    /// building, a road footprint, another parcel or a mapped area it does not own; R's cutter answers every driveway;
    /// door specs for lane A; deterministic. And per business type, the plan of a fixture parcel matches its reviewed
    /// snapshot (Fixtures/plans/*.json; set BR_SITES_SNAPSHOT=write to rewrite them after reviewing the maps in
    /// artifacts/sites/s3/fixtures).
    /// </summary>
    [TestFixture, Category("Sites")]
    public class SitePlansTests
    {
        static SitePlanSet s_Town;

        static SitePlanSet Town()
        {
            if (s_Town != null) return s_Town;
            var town = SiteParcels.Towns().First();
            s_Town = SitePlans.BuildTown(town);
            SitePlans.WriteArtifacts(s_Town);
            return s_Town;
        }

        [Test]
        public void EveryBuildingHasAPlan()
        {
            var s = Town();
            TestContext.WriteLine(SitePlans.Summary(s));
            var bad = new List<string>();
            foreach (var b in s.ctx.data.buildings)
            {
                if (!b.inPlay || b.Accessory) continue;
                var p = s.ctx.parcels.ParcelOf(b.index);
                if (p == null) { bad.Add($"{b.osm}: no parcel"); continue; }
                if (!s.byParcel.TryGetValue(p.id, out var plan)) { bad.Add($"{b.osm}: parcel {p.id} has no plan"); continue; }
                if (plan.type != p.type) bad.Add($"{b.osm}: plan type {plan.type} != parcel type {p.type}");
                if (plan.source == PlanSource.Template && string.IsNullOrEmpty(plan.templateId)) bad.Add($"{b.osm}: a template plan without its template");
            }
            foreach (var f in s.ctx.parcels.fixedAreas)
                if (f.parcel >= 0 && !s.byParcel.ContainsKey(f.parcel)) bad.Add($"{f.osm}: its parcel {f.parcel} has no plan");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(30)));
        }

        [Test]
        public void ReportShowsSourcesAndRejectedTemplates()
        {
            var s = Town();
            var report = SitePlans.Report(s);
            StringAssert.Contains("Sources per type", report);
            StringAssert.Contains("Templates rejected", report);
            int listed = 0;
            foreach (var kv in s.rejections)
                foreach (var r in kv.Value) { StringAssert.Contains(r, report); listed++; }
            TestContext.WriteLine($"{listed} template rejections listed with their reasons");
            foreach (var g in s.plans.GroupBy(p => p.source)) TestContext.WriteLine($"{g.Key}: {g.Count()}");
            Assert.Greater(listed, 0, "templates are tried on the town's commercial sites");
        }

        [Test]
        public void NoPlanOverlapsWhatItDoesNotOwn()
        {
            var s = Town();
            Assert.IsEmpty(s.problems, string.Join("\n", s.problems.Take(30)));
            // Independently: every surface on its own parcel (so off the roads, which no block holds, and off other parcels),
            // off every building.
            var bad = new List<string>();
            foreach (var plan in s.plans)
            {
                var region = s.ctx.parcels.regions[plan.parcel];
                foreach (var srf in plan.surfaces)
                {
                    var r = Clip.Region(srf.outer, srf.holes);
                    double off = Clip.Area(Clip.Difference(r, region));
                    if (off > 0.05) bad.Add($"parcel {plan.parcel}: {srf.use} {off:F2} m² off the parcel");
                    foreach (var b in s.ctx.data.buildings)
                    {
                        if (!b.inPlay || b.OpenRoof) continue;   // surfaces run under canopies and carports
                        double on = Clip.Area(Clip.Intersect(r, Clip.Region(b.poly)));
                        if (on > 0.05) bad.Add($"parcel {plan.parcel}: {srf.use} covers {on:F2} m² of {b.osm}");
                    }
                }
            }
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(30)));
        }

        [Test]
        public void DrivewaysAnsweredByLaneR()
        {
            var s = Town();
            Assert.NotNull(s.ctx.cutter, "R2's cutter");
            Assert.AreEqual(s.requests.Count, s.cuts.Length, "one answer per request");
            var ids = new HashSet<int>();
            foreach (var r in s.requests) Assert.IsTrue(ids.Add(r.id), "request ids unique");
            var outcomes = s.cuts.GroupBy(c => c.outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
            TestContext.WriteLine($"{s.requests.Count} requests in {s.passes} passes: " + string.Join(", ", outcomes));
            foreach (var plan in s.plans)
                foreach (var c in plan.cuts.Where(c => c.outcome == CutOutcome.Rejected))
                    Assert.IsTrue(plan.notes.Any(n => n.Contains("rejected")), $"parcel {plan.parcel}: a rejected driveway the plan does not mention");
            // Mapped driveways (S1) stay in their plans.
            foreach (var r in s.ctx.parcels.requests)
                Assert.IsTrue(s.requests.Any(q => q.mapped && q.osmWay == r.osmWay), $"{r.osmWay}: a mapped driveway no plan asks for");
        }

        [Test]
        public void DoorSpecsForLaneA()
        {
            var s = Town();
            var json = SitePlans.DoorsJson(s);
            var root = BrJson.Obj(BrJson.Parse(json));
            Assert.AreEqual("backroads-doorspecs", root["format"]);
            var doors = BrJson.Arr(root["doors"]);
            Assert.AreEqual(s.plans.Sum(p => p.doors.Count), doors.Count);
            var byOsm = s.ctx.data.buildings.ToDictionary(b => b.osm);
            foreach (var o in doors)
            {
                var d = BrJson.Obj(o);
                var b = byOsm[(string)d["building"]];
                int e = (int)(double)d["wallEdge"];
                Assert.That(e, Is.InRange(0, b.poly.Length - 1));
                double len = Unity.Mathematics.math.distance(b.poly[e], b.poly[(e + 1) % b.poly.Length]);
                Assert.That((double)d["offset"], Is.InRange(0.0, len + 1e-3));
                Assert.IsTrue(Enum.TryParse<DoorKind>((string)d["kind"], out _));
            }
            // Every main building has a main door.
            foreach (var plan in s.plans)
            {
                var p = s.ctx.parcels.parcels[plan.parcel];
                if (p.kind != ParcelKind.Building || p.buildings.Length == 0) continue;   // a campground's cabins are accessory
                var main = s.ctx.data.buildings[p.buildings[0]];
                if (plan.type == BusinessType.Fuel && main.tags.TryGetValue("building", out var v) && v == "roof") continue;
                Assert.IsTrue(plan.doors.Any(d => d.building == main.osm && (d.kind == DoorKind.Main || d.kind == DoorKind.Bay)), $"{main.osm}: no main door");
            }
            TestContext.WriteLine($"{doors.Count} door specs for {doors.Select(o => (string)BrJson.Obj(o)["building"]).Distinct().Count()} buildings");
        }

        [Test]
        public void Deterministic()
        {
            var a = Town();
            var b = SitePlans.BuildTown(a.town);
            Assert.AreEqual(a.hash, b.hash, "two builds of the site plans differ");
        }

        // ------------------------------------------------------------------------------------------ snapshots per type

        static IEnumerable<string> FixtureNames() => PlanFixtures.All().Select(f => f.name);

        [TestCaseSource(nameof(FixtureNames))]
        public void FixtureMatchesSnapshot(string name)
        {
            var f = PlanFixtures.All().First(x => x.name == name);
            var set = SitePlanner.Plan(f.ctx);
            Assert.AreEqual(1, set.plans.Count);
            var plan = set.plans[0];
            Assert.IsEmpty(set.problems, string.Join("\n", set.problems));
            var dir = Path.Combine(RepoPaths.Artifacts, "sites", "s3", "fixtures");
            Directory.CreateDirectory(dir);
            var pr = f.ctx.parcels.parcels[0].outer;
            var mn = new Unity.Mathematics.double2(pr.Min(q => q.x) - 6, -6);
            var mx = new Unity.Mathematics.double2(pr.Max(q => q.x) + 6, pr.Max(q => q.y) + 6);
            File.WriteAllText(Path.Combine(dir, name + ".svg"), SitePlanMap.Svg(set, (mn, mx), 900.0 / Math.Max(mx.x - mn.x, mx.y - mn.y), name), new UTF8Encoding(false));
            var summary = Summary(name, plan, set.hash);
            TestContext.WriteLine(summary);
            var path = RepoPaths.InRepo("Game/Assets/Backroads/Tests/EditMode/Sites/Fixtures/plans/" + name + ".json");
            if (Environment.GetEnvironmentVariable("BR_SITES_SNAPSHOT") == "write")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, summary, new UTF8Encoding(false));
                return;
            }
            Assert.IsTrue(File.Exists(path), $"no snapshot for {name}: review artifacts/sites/s3/fixtures/{name}.svg, then run with BR_SITES_SNAPSHOT=write");
            var want = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.AreEqual(want, summary, $"{name}: the plan changed; review artifacts/sites/s3/fixtures/{name}.svg and rewrite the snapshot if it is right");
        }

        /// <summary>A readable snapshot of a plan: source, counts by kind, and the plan's hash (every coordinate, mm).</summary>
        static string Summary(string name, SitePlan p, string hash)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"fixture\": \"{name}\",\n");
            sb.Append($"  \"type\": \"{p.type}\",\n  \"source\": \"{p.source}\",\n  \"template\": \"{p.templateId ?? ""}\",\n  \"downtown\": {(p.downtown ? "true" : "false")},\n");
            sb.Append("  \"surfaces\": {");
            sb.Append(string.Join(", ", p.surfaces.GroupBy(s => s.use + ":" + s.surface).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => string.Format(ci, "\"{0}\": {1:F0}", g.Key, g.Sum(s => Poly2.Area(s.outer) - s.holes.Sum(h => Poly2.Area(h)))))));
            sb.Append("},\n");
            sb.Append($"  \"lots\": {p.lots.Count},\n  \"stalls\": {p.lots.Sum(l => l.stalls.Count)},\n  \"accessible\": {p.lots.Sum(l => l.AccessibleCount)},\n");
            sb.Append("  \"driveways\": [" + string.Join(", ", p.driveways.Select(d => string.Format(ci, "\"{0} {1:F1} m{2}\"", d.kind, d.s1 - d.s0, d.mapped ? " mapped" : ""))) + "],\n");
            sb.Append("  \"doors\": {" + string.Join(", ", p.doors.GroupBy(d => d.kind).OrderBy(g => g.Key).Select(g => $"\"{g.Key}\": {g.Count()}")) + "},\n");
            sb.Append("  \"props\": {" + string.Join(", ", p.props.GroupBy(x => x.key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"\"{g.Key}\": {g.Count()}")) + "},\n");
            sb.Append("  \"models\": {" + string.Join(", ", p.models.GroupBy(x => x.key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"\"{g.Key}\": {g.Count()}")) + "},\n");
            sb.Append($"  \"fences\": {p.fences.Count},\n  \"gates\": {p.gates.Count},\n");
            sb.Append("  \"notes\": [" + string.Join(", ", p.notes.Select(n => "\"" + n.Replace("\"", "'") + "\"")) + "],\n");
            sb.Append($"  \"hash\": \"{hash}\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
