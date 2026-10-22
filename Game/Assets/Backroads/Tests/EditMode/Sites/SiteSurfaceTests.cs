using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Sites;
using Backroads.Sites.Editor;
using Backroads.World.Bake;
using Clipper2Lib;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>
    /// Sites.Surfaces (card sites-S4 "done when") on the town: every plan drawn; the terrain 0.25 m inside every paved site
    /// polygon at least 3 cm under the surface (lane W's bake with the site plates; not within 0.75 m of a wall, where the
    /// building's pad meets the surface and the wall hides the seam); every driveway apron meets its lot
    /// within 5 mm; grades within design §9's limits or listed in the report; props inside their parcels and clear of
    /// stalls, aisles and doors; LOD0 triangles within SITE_TRI_BUDGET; deterministic; the committed bake current, its
    /// chunks loadable and their colliders where the surfaces are. And on the fixture parcels: the same checks per type.
    /// </summary>
    [TestFixture, Category("Sites")]
    public class SiteSurfacesTests
    {
        static SiteBakeResult s_Town;

        static SiteBakeResult Town()
        {
            if (s_Town != null) return s_Town;
            var town = SiteParcels.Towns().First();
            s_Town = SiteBake.Build(town);
            SiteBake.WriteArtifacts(s_Town);
            return s_Town;
        }

        [Test]
        public void EveryPlanDrawn()
        {
            var r = Town();
            TestContext.WriteLine(SiteBake.Summary(r));
            Assert.IsEmpty(r.problems, string.Join("\n", r.problems.Take(30)));
            var bad = new List<string>();
            var platesBySurface = new HashSet<string>(r.meshes.plates.Select(p => p.id.Length > 15 ? p.id.Substring(0, 15) : p.id), StringComparer.Ordinal);   // "site:pNNNN:sNN:"
            foreach (var plan in r.plans.plans)
            {
                Assert.IsTrue(r.meshes.grades.ContainsKey(plan.parcel), $"parcel {plan.parcel}: not graded");
                for (int si = 0; si < plan.surfaces.Count; si++)
                {
                    var s = plan.surfaces[si];
                    double area = Poly2.Area(s.outer) - s.holes.Sum(h => Poly2.Area(h));
                    if (area < 1.0) continue;
                    if (!platesBySurface.Contains($"site:p{plan.parcel:D4}:s{si:D2}:")) bad.Add($"parcel {plan.parcel}: {s.use} {s.surface} {area:F0} m² has no plates");
                }
            }
            // the paved meshes cover the paved area (kerbed islands cut out)
            double meshArea = 0;
            foreach (var p in r.meshes.meshes.parts)
            {
                if (p.material == Backroads.Roads.Editor.RoadMaterial.Paint || !p.collider || p.lods != 3) continue;
                for (int k = 0; k + 2 < p.tri.Count; k += 3)
                {
                    var a = p.pos[p.tri[k]]; var b = p.pos[p.tri[k + 1]]; var c = p.pos[p.tri[k + 2]];
                    meshArea += Math.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5;
                }
            }
            TestContext.WriteLine($"paved {r.meshes.pavedArea:F0} m², surface meshes and kerbs {meshArea:F0} m² in plan");
            Assert.That(meshArea, Is.GreaterThanOrEqualTo(r.meshes.pavedArea * 0.99), "the paved surfaces are meshed");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(30)));
        }

        [Test]
        public void TerrainUnderPavedSurfaces()
        {
            var r = Town();
            // the site plates and, as lane R will add them once the driveway file is wired in, R's apron plates for our cuts
            var kb = Backroads.Roads.Editor.KerbBaker.BuildTown(r.town);
            var cutter = new DrivewayCutter(KerbFrontageSource.LoadTown(r.town));
            var cuts = cutter.Resolve(r.plans.requests);
            var aprons = Backroads.Roads.Editor.RoadPlateBuilder.Build(kb.roads.graph, kb.roads.surfaces, kb.kerbs, kb.roads.source?.dem, kb.settings, cuts, cutter)
                .Where(p => p.id.StartsWith("apron:", StringComparison.Ordinal)).ToList();
            var bake = WorldBake.Run(new WorldBakeOptions { townId = r.town, registeredSources = false, extraPlates = r.meshes.plates.Concat(aprons).ToList() });
            var grid = bake.grid;
            // the paving as meshed (its triangles are the plates'), to measure the terrain against
            var paving = new Backroads.Roads.Editor.SurfaceIndex();
            foreach (var part in r.meshes.meshes.parts) if (part.arm == SiteMesher.SurfacePart) paving.Add(part);
            int n = 0, bad = 0;
            double worst = double.MinValue;
            var worstAt = "";
            var offenders = new List<(double over, string what, double2 q)>();
            foreach (var plan in r.plans.plans)
            {
                var g = r.meshes.grades[plan.parcel];
                var nearWalls = r.plans.ctx.parcels.parcels[plan.parcel].buildings.Select(bi => r.plans.ctx.data.buildings[bi].poly).ToList();
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
                            // beside a wall the building's pad meets the surface; the wall (down to groundMin - 0.6 m) hides that seam
                            if (nearWalls.Any(b => Poly2.Contains(b, q) || Poly2.BoundaryDistance(b, q, out _) < 0.75)) continue;
                            double ground = grid.Sample(grid.heights, q.x, q.y);
                            if (!paving.Height(new D2(q.x, q.y), out double top)) top = g.H(q);
                            n++;
                            double over = ground - (top - 0.03);
                            if (over > worst) { worst = over; worstAt = $"parcel {plan.parcel} {s.use} at ({q.x:F1}, {q.y:F1})"; }
                            if (over > 1e-4) { bad++; offenders.Add((over, $"parcel {plan.parcel} {s.use} {s.surface}", q)); }
                        }
                    }
                }
            }
            TestContext.WriteLine($"{n} points 0.25 m inside the paved site surfaces ({aprons.Count} apron plates of lane R for our cuts); the terrain is at least {-worst * 1000:F0} mm under (surface - 3 cm) at worst ({worstAt}); {bad} over");
            // the worst offenders and the plates that won the grid vertices round them
            foreach (var (over, what, q) in offenders.OrderByDescending(o => o.over).Take(12))
            {
                int i0 = (int)Math.Floor(grid.GridI(q.x)), j0 = (int)Math.Floor(grid.GridJ(q.y));
                var owners = new List<string>();
                for (int dj = 0; dj <= 1; dj++)
                    for (int di = 0; di <= 1; di++)
                    {
                        int k = grid.K(i0 + di, j0 + dj);
                        int o = bake.applied.owner[k];
                        owners.Add(o >= 0 && o < bake.applied.sorted.Count ? $"{bake.applied.sorted[o].id} y {grid.heights[k]:F2}" : $"none y {grid.heights[k]:F2}");
                    }
                TestContext.WriteLine($"  {what} at ({q.x:F2}, {q.y:F2}): {over * 1000:F0} mm over; vertices: {string.Join("; ", owners)}");
            }
            Assert.Greater(n, 1000);
            // a 0.5 m grid: a point may sit in a cell whose far corner is ground no plate covers (like lane R's own check, a few allowed)
            Assert.That(bad, Is.LessThanOrEqualTo(Math.Max(2, n / 1000)), $"terrain pokes through at {bad} points: worst {worst * 1000:F1} mm over surface - 3 cm at {worstAt}");
            Assert.That(worst, Is.LessThan(0.05), $"terrain pokes 5 cm or more through at {worstAt}");
        }

        [Test]
        public void ApronsMeetTheirLots()
        {
            var r = Town();
            int cuts = r.plans.plans.Sum(p => p.cuts.Where(c => c != null && c.outcome != CutOutcome.Rejected).Select(c => (c.faceId, c.s0, c.s1)).Distinct().Count());
            Assert.AreEqual(cuts, r.apronSeams.Count, "every baked cut checked");
            double worst = r.apronSeams.Select(a => a.gap).DefaultIfEmpty(0).Max();
            TestContext.WriteLine($"{r.apronSeams.Count} aprons; the lot meets each apron's lot edge within {worst * 1000:F2} mm");
            Assert.That(worst, Is.LessThanOrEqualTo(SiteBake.ApronSeamLimit));
            // and a site surface reaches every apron (stubs added where S3 drew no drive), else it is listed with its reason
            var report = SiteBake.Report(r);
            int missing = 0;
            foreach (var plan in r.plans.plans)
                foreach (var c in plan.cuts.Where(c => c != null && c.outcome != CutOutcome.Rejected).GroupBy(c => (c.faceId, c.s0, c.s1)).Select(g => g.First()))
                {
                    var mid = (c.lotEdgeA + c.lotEdgeB) * 0.5;
                    if (plan.surfaces.Any(s => Poly2.Contains(s.outer, mid) || Poly2.BoundaryDistance(s.outer, mid, out _) < 0.3)) continue;
                    missing++;
                    StringAssert.Contains($"parcel {plan.parcel} ({plan.type}, {plan.source}): request {c.requestId}", report, "an unreached apron not in the report");
                }
            TestContext.WriteLine($"{r.stubs.Count} drive stubs; {missing} aprons no surface of their plan reaches (listed)");
            Assert.That(missing, Is.LessThanOrEqualTo(Math.Max(1, cuts / 20)), "more than 5 % of the aprons lead onto nothing");
        }

        [Test]
        public void GradesWithinLimitsOrReported()
        {
            var r = Town();
            var report = SiteBake.Report(r);
            foreach (var g in r.grades) StringAssert.Contains(g.ToString(), report);
            foreach (var grp in r.grades.GroupBy(g => g.what)) TestContext.WriteLine($"{grp.Key}: {grp.Count()} plans, worst {grp.Max(x => x.value):F3}");
            // nothing listed for a grade inside its limit
            foreach (var g in r.grades) Assert.Greater(g.value, g.limit, g.ToString());
        }

        [Test]
        public void PropsInsideTheirParcelsAndClear()
        {
            var r = Town();
            var bad = new List<string>();
            var ctx = r.plans.ctx;
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
                foreach (var (dp, _, d) in SiteGrading.DoorPoints(plan, ctx))
                    if (math.distance(dp, q) < 0.8) bad.Add($"{p.key} p{p.parcel}: in front of a door");
                Assert.IsTrue(SitePropTable.Keys.ContainsKey(p.key), p.key);
            }
            TestContext.WriteLine($"{r.placements.Count} props ({r.propStats.synty} Synty, {r.propStats.own} ours), {r.propStats.moved.Values.Sum()} moved, {r.propStats.dropped.Values.Sum()} dropped");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(30)));
        }

        [Test]
        public void WithinTheTriangleBudget()
        {
            var r = Town();
            TestContext.WriteLine($"LOD0 {r.chunks.lod0Triangles}, LOD1 {r.chunks.lod1Triangles}, collider {r.chunks.colliderTriangles} triangles in {r.chunks.chunks.Length} chunks");
            Assert.That(r.chunks.lod0Triangles, Is.LessThanOrEqualTo(SiteBake.TriangleBudget));
            Assert.Greater(r.chunks.lod0Triangles, 0);
        }

        [Test]
        public void Deterministic()
        {
            var a = Town();
            var b = SiteBake.Build(a.town);
            Assert.AreEqual(a.hash, b.hash, "two site bakes differ");
        }

        [Test]
        public void CommittedBakeCurrentAndLoadable()
        {
            var r = Town();
            var (chunks, props) = SiteBake.LoadBaked(r.town);
            Assert.NotNull(chunks, "site chunks not baked (SitesBakeCli.Bake)");
            Assert.NotNull(props, "site props not baked (SitesBakeCli.Bake)");
            Assert.AreEqual(r.chunks.contentHash, chunks.contentHash, "the committed site chunks are stale: run SitesBakeCli.Bake");
            Assert.AreEqual(r.props.contentHash, props.contentHash, "the committed site props are stale: run SitesBakeCli.Bake");
            Assert.AreEqual(SiteBake.DrivewaysJson(r.plans), File.ReadAllText(SiteBake.DrivewaysPath(r.town)), "driveways.json is stale: run SitesBakeCli.Bake");
            Assert.NotNull(SiteChunks.LoadTown(r.town), "the chunks load from Resources");
            Assert.NotNull(SitePropSet.LoadTown(r.town), "the props load from Resources");
            // the colliders: a ray from above hits each of the busiest lots at its graded height
            var root = SiteChunks.Spawn(r.town, null, null);
            try
            {
                Physics.SyncTransforms();
                int hits = 0;
                foreach (var plan in r.plans.plans.Where(p => p.lots.Count > 0).OrderByDescending(p => p.lots.Sum(l => l.stalls.Count)))
                {
                    // a stall on paving (gravel lots are the terrain's own surface: no mesh, no collider of ours)
                    var stall = plan.lots.SelectMany(l => l.stalls).FirstOrDefault(st => plan.surfaces.Any(s => SiteMesher.Paved(s.surface) && Poly2.Contains(s.outer, Poly2.Centroid(st.poly))));
                    if (stall == null) continue;
                    if (hits >= 5) break;
                    var q = Poly2.Centroid(stall.poly);
                    double y = r.meshes.grades[plan.parcel].H(q);
                    // the mesh's own height there (its triangles are linear between the graded corners)
                    var idx = new Backroads.Roads.Editor.SurfaceIndex();
                    foreach (var part in r.meshes.meshes.parts) if (part.arm == SiteMesher.SurfacePart) idx.Add(part);
                    if (idx.Height(new D2(q.x, q.y), out var my)) y = my;
                    Assert.IsTrue(Physics.Raycast(new Vector3((float)q.x, (float)y + 30, (float)q.y), Vector3.down, out var hit, 60), $"parcel {plan.parcel}: no collider under a stall");
                    Assert.AreEqual(y, hit.point.y, 0.01, $"parcel {plan.parcel}: the collider at the surface's height");
                    hits++;
                }
                Assert.Greater(hits, 0);
                Assert.Greater(root.GetComponentsInChildren<MeshCollider>().Length, 0);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void PropPlacerPutsThemInAScene()
        {
            var r = Town();
            var set = SitePropSet.LoadTown(r.town);
            Assert.NotNull(set, "site props not baked");
            var root = Backroads.Sites.Client.Editor.SitePropPlacer.Place(set);
            try
            {
                var props = root.transform.Cast<Transform>().Where(t => t.name != "Fences" && !t.name.StartsWith("Lane A", StringComparison.Ordinal)).ToList();
                Assert.AreEqual(set.props.Length, props.Count, "one object per placement");
                for (int i = 0; i < props.Count; i++)
                {
                    Assert.Less(Vector3.Distance(props[i].position, set.props[i].position), 1e-3f, set.props[i].key);
                    Assert.AreEqual(0f, Mathf.DeltaAngle(set.props[i].yaw, props[i].eulerAngles.y), 0.01f, set.props[i].key);
                }
                Assert.AreEqual(set.models.Length, root.transform.Find("Lane A site models (stand-ins)").childCount, "a stand-in per lane A model");
                TestContext.WriteLine($"{props.Count} props placed ({props.Count(t => t.name.EndsWith("(ours)", StringComparison.Ordinal))} our own stand-ins where the Synty packs are not imported), {set.models.Length} lane A stand-ins");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ------------------------------------------------------------------------------------------ fixtures

        static IEnumerable<string> FixtureNames() => PlanFixtures.All().Select(f => f.name);

        [TestCaseSource(nameof(FixtureNames))]
        public void FixtureSurfaces(string name)
        {
            var f = PlanFixtures.All().First(x => x.name == name);
            var set = SitePlanner.Plan(f.ctx);
            var r = SiteBake.Stage("fixture", set, PlanFixtures.Ground());
            Assert.IsEmpty(r.problems, string.Join("\n", r.problems));
            Assert.That(r.apronSeams.Select(a => a.gap).DefaultIfEmpty(0).Max(), Is.LessThanOrEqualTo(SiteBake.ApronSeamLimit));
            foreach (var p in r.meshes.plates)
            {
                Assert.AreEqual(3, p.polygon.Length, p.id);
                Assert.Greater(Poly2.SignedArea(p.polygon), 0, p.id + ": CCW");
                Assert.IsFalse(p.id.StartsWith("road:", StringComparison.Ordinal) || p.id.StartsWith("apron:", StringComparison.Ordinal) || p.id.StartsWith("abutment:", StringComparison.Ordinal), p.id);
            }
            var plan = set.plans[0];
            int paved = plan.surfaces.Count(s => SiteMesher.Paved(s.surface));
            if (paved > 0) Assert.Greater(SiteBake.Lod0Triangles(r.meshes), 0, "paved surfaces meshed");
            TestContext.WriteLine($"{name}: {r.meshes.plates.Count} plates, {SiteBake.Lod0Triangles(r.meshes)} LOD0 triangles, kerbs {r.meshes.kerbM:F0} m, paint {r.meshes.paintM:F0} m, {r.placements.Count} props, {r.grades.Count} grade notes");
            foreach (var g in r.grades) TestContext.WriteLine("  " + g);
            Directory.CreateDirectory(Path.Combine(Backroads.Build.Editor.RepoPaths.Artifacts, "sites", "s4", "fixtures"));
            var pr = f.ctx.parcels.parcels[0].outer;
            var mn = new double2(pr.Min(q => q.x) - 6, -6);
            var mx = new double2(pr.Max(q => q.x) + 6, pr.Max(q => q.y) + 6);
            SiteBakeMap.Render(r, mn, mx, Math.Max(mx.x - mn.x, mx.y - mn.y) / 900.0, Path.Combine(Backroads.Build.Editor.RepoPaths.Artifacts, "sites", "s4", "fixtures", name + ".png"));
        }
    }
}
