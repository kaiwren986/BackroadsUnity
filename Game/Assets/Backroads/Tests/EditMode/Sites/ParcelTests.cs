using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Sites;
using Backroads.Sites.Editor;
using Clipper2Lib;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>
    /// Sites.Parcels (card sites-S1 "done when") on the town: every building except accessory buildings has exactly one
    /// parcel; parcels tile each block without gaps or overlaps above 0.1 m²; every parcel has a frontage face or an
    /// easement to one; every mapped lot and yard lies inside its parcel; two builds give the same hash. Also: the faces
    /// are roads-R2's published KerbFrontage, mapped driveways become requests lane R's cutter answers. The report and
    /// plan maps go to artifacts/sites/(town)/.
    /// </summary>
    [TestFixture, Category("Sites")]
    public class Parcels
    {
        static ParcelSet s_Set;

        static ParcelSet Town()
        {
            if (s_Set != null) return s_Set;
            var town = SiteParcels.Towns().First();
            s_Set = SiteParcels.BuildTown(town);
            SiteParcels.WriteArtifacts(s_Set);
            return s_Set;
        }

        [Test]
        public void EveryBuildingHasOneParcel()
        {
            var set = Town();
            TestContext.WriteLine(SiteParcels.Summary(set));
            var bad = new List<string>();
            var seen = new Dictionary<int, int>();
            foreach (var p in set.parcels)
                foreach (var b in p.buildings)
                {
                    if (seen.TryGetValue(b, out var other)) bad.Add($"{set.data.buildings[b].osm} on parcels {other} and {p.id}");
                    seen[b] = p.id;
                }
            int mains = 0;
            foreach (var b in set.data.buildings)
            {
                if (!b.inPlay || b.Accessory) continue;
                mains++;
                if (!seen.ContainsKey(b.index)) bad.Add($"{b.osm} ({b.business}) has no parcel");
                else if (set.parcels[seen[b.index]].kind != ParcelKind.Building) bad.Add($"{b.osm} on a {set.parcels[seen[b.index]].kind} parcel");
            }
            foreach (var p in set.parcels)
            {
                if (p.kind != ParcelKind.Building) { Assert.IsEmpty(p.buildings.Where(b => !set.data.buildings[b].Accessory), $"parcel {p.id} ({p.kind}) holds a main building"); continue; }
                Assert.IsFalse(set.data.buildings[p.buildings[0]].Accessory, $"parcel {p.id}: its first building is its main one");
                // One main building; a fuel canopy (building=roof) joins its shop's parcel.
                bool Canopy(int b) => set.data.buildings[b].business == BusinessType.Fuel && set.data.buildings[b].tags.TryGetValue("building", out var v) && (v == "roof" || v == "canopy");
                var mainsHere = p.buildings.Where(b => !set.data.buildings[b].Accessory).ToList();
                Assert.LessOrEqual(mainsHere.Count(b => !Canopy(b)), 1, $"parcel {p.id}: more than one main building");
                Assert.GreaterOrEqual(mainsHere.Count, 1, $"parcel {p.id}: no main building");
                // The main building stands on its parcel.
                var c = set.data.buildings[p.buildings[0]].centroid;
                Assert.IsTrue(Clip.Contains(set.regions[p.id], c), $"parcel {p.id}: its building's centre lies off it");
            }
            TestContext.WriteLine($"{mains} main buildings, {seen.Count} buildings on parcels");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(40)));
        }

        [Test]
        public void ParcelsTileEachBlock()
        {
            var set = Town();
            var bad = new List<string>();
            double worstGap = 0, worstOver = 0;
            for (int b = 0; b < set.blocks.blocks.Count; b++)
            {
                var ids = set.parcels.Where(p => p.block == b).Select(p => p.id).ToList();
                var all = new Paths64();
                foreach (var i in ids) all.AddRange(set.regions[i]);
                var union = Clip.Union(all);
                foreach (var g in Clip.Pieces(Clip.Difference(set.blocks.regions[b], union)))
                {
                    double a = Clip.Area(g);
                    worstGap = Math.Max(worstGap, a);
                    if (a > 0.1) bad.Add($"block {b}: a gap of {a:F3} m² at {Clip.Bounds(g).min}");
                }
                foreach (var g in Clip.Pieces(Clip.Difference(union, set.blocks.regions[b])))
                {
                    double a = Clip.Area(g);
                    worstOver = Math.Max(worstOver, a);
                    if (a > 0.1) bad.Add($"block {b}: {a:F3} m² of parcels outside the block at {Clip.Bounds(g).min}");
                }
                for (int x = 0; x < ids.Count; x++)
                {
                    var bx = Clip.Bounds(set.regions[ids[x]]);
                    for (int y = x + 1; y < ids.Count; y++)
                    {
                        var by = Clip.Bounds(set.regions[ids[y]]);
                        if (bx.max.x < by.min.x || by.max.x < bx.min.x || bx.max.y < by.min.y || by.max.y < bx.min.y) continue;
                        double a = Clip.Area(Clip.Intersect(set.regions[ids[x]], set.regions[ids[y]]));
                        worstOver = Math.Max(worstOver, a);
                        if (a > 0.1) bad.Add($"parcels {ids[x]} and {ids[y]} overlap by {a:F3} m²");
                    }
                }
                // Every parcel is one piece.
                foreach (var i in ids) Assert.AreEqual(1, Clip.Pieces(set.regions[i]).Count, $"parcel {i} is not one piece");
            }
            TestContext.WriteLine($"{set.blocks.blocks.Count} blocks; largest gap {worstGap:F4} m², largest overlap {worstOver:F4} m² (Clipper rounds crossings to 1 mm)");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(40)));
        }

        [Test]
        public void FrontageOrEasement()
        {
            var set = Town();
            var faces = set.blocks.fronts.ToDictionary(f => f.faceId);
            var bad = new List<string>();
            foreach (var p in set.parcels)
            {
                foreach (var f in p.frontage)
                {
                    Assert.IsTrue(faces.ContainsKey(f.faceId), $"parcel {p.id}: unknown face {f.faceId}");
                    Assert.Less(f.s0, f.s1);
                    Assert.GreaterOrEqual(f.s0, -1e-3f);
                    Assert.LessOrEqual(f.s1, faces[f.faceId].Length + 1e-3f);
                    Assert.AreEqual(faces[f.faceId].block, p.block, "a parcel fronts faces of its own block");
                }
                if (p.frontage.Length == 0)
                {
                    if (p.easement == null || p.easement.Length < 2 || !faces.ContainsKey(p.easementFace)) bad.Add($"parcel {p.id} ({p.kind}, {p.area:F0} m²) has neither frontage nor an easement");
                }
                else Assert.IsNull(p.easement, $"parcel {p.id}: an easement although it fronts a street");
            }
            int withFront = set.parcels.Count(p => p.frontage.Length > 0);
            TestContext.WriteLine($"{withFront} of {set.parcels.Count} parcels front a street, {set.parcels.Count(p => p.easement != null)} reach one by an easement; " +
                                  $"{set.parcels.Count(p => p.corner)} corner, {set.parcels.Count(p => p.alley)} with an alley");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(40)));
            Assert.Greater(withFront, set.parcels.Count / 2);
        }

        [Test]
        public void MappedLotsInsideTheirParcel()
        {
            var set = Town();
            var blocks = new Paths64();
            foreach (var r in set.blocks.regions) blocks.AddRange(r);
            blocks = Clip.Union(blocks);
            Assert.Greater(set.fixedAreas.Count, 0, "the town maps lots");
            foreach (var f in set.fixedAreas)
            {
                var fr = Clip.Region(f.outer, f.holes);
                var inBlocks = Clip.Intersect(fr, blocks);
                var holders = set.parcels.Where(p => p.fixedAreas.Contains(f.id)).ToList();
                var held = new Paths64();
                foreach (var p in holders) held.AddRange(set.regions[p.id]);
                // A building standing inside the mapped area keeps its footprint plus ParcelBuilder.StandingRoom.
                foreach (var p in set.parcels)
                    if (p.kind == ParcelKind.Building && !holders.Contains(p) && Clip.Contains(fr, set.data.buildings[p.buildings[0]].centroid))
                        foreach (var bi in p.buildings)
                            held.AddRange(Clip.Inflate(Clip.Region(set.data.buildings[bi].poly), ParcelBuilder.StandingRoom + 0.01, JoinType.Miter));
                double outside = Clip.Area(Clip.Difference(inBlocks, Clip.Union(held)));
                TestContext.WriteLine($"{f.osm} {f.kind}: {Clip.Area(inBlocks):F0} of {f.area:F0} m² on blocks, parcels {string.Join(",", holders.Select(p => p.id))}, " +
                                      $"served by {string.Join(",", f.servedBy.Select(i => set.data.buildings[i].osm))}, {outside:F3} m² outside them");
                if (Clip.Area(inBlocks) < 1) continue;
                Assert.IsNotEmpty(holders, f.osm + " is on no parcel");
                Assert.Less(outside, 0.1, f.osm + " sticks out of its parcel");
                if (f.servedBy.Length > 0)
                {
                    var host = set.ParcelOf(f.servedBy[0]);
                    Assert.NotNull(host);
                    Assert.IsTrue(holders.Any(p => p.id == host.id) || holders.All(p => p.block != host.block) || holders.All(p => p.buildings.Length == 0),
                        f.osm + ": on its building's block the lot joins the building's parcel");
                }
            }
        }

        [Test]
        public void Deterministic()
        {
            var a = Town();
            var b = SiteParcels.BuildTown(a.town);
            Assert.AreEqual(a.hash, b.hash, "two builds of the parcels differ");
            Assert.AreEqual(ParcelBuilder.Hash(a), a.hash);
        }

        [Test]
        public void FacesAreTheR2Frontage()
        {
            var set = Town();
            var src = KerbFrontageSource.LoadTown(set.town);
            Assert.NotNull(src, "the town's KerbFrontage asset");
            Assert.AreEqual(src.Faces.Count, set.blocks.fronts.Count, "one front line per published face");
            foreach (var f in set.blocks.fronts)
            {
                var k = src.Face(f.faceId);
                Assert.NotNull(k, $"face {f.faceId}");
                Assert.AreEqual(k.osmWay, f.osmWay);
                Assert.AreEqual(k.Length, f.Length, 1e-3, $"face {f.faceId} length");
                Assert.AreEqual(k.kerb.Length, f.kerb.Length);
                Assert.Less(math.distance(k.walkBack[0], f.back[0]), 1e-3);
            }
        }

        [Test]
        public void MappedDrivewaysBecomeRequests()
        {
            var set = Town();
            var faces = set.blocks.fronts.ToDictionary(f => f.faceId);
            TestContext.WriteLine($"{set.requests.Count} requests from {set.data.service.Count} mapped service ways");
            Assert.Greater(set.requests.Count, 0);
            var ids = new HashSet<int>();
            for (int i = 0; i < set.requests.Count; i++)
            {
                var r = set.requests[i];
                Assert.IsTrue(ids.Add(r.id), "unique request ids");
                Assert.IsTrue(r.mapped);
                Assert.IsFalse(string.IsNullOrEmpty(r.osmWay));
                Assert.IsTrue(faces.ContainsKey(r.faceId));
                Assert.Less(r.s0, r.s1);
                Assert.GreaterOrEqual(r.s0, 0f);
                Assert.LessOrEqual(r.s1, faces[r.faceId].Length + 1e-3f);
                Assert.GreaterOrEqual(set.requestParcel[i], 0, $"request {r.id} ({r.osmWay}) lies on no parcel");
            }
            // Lane R's cutter answers every one (baked, moved, merged or rejected with a reason).
            var cutter = new DrivewayCutter(KerbFrontageSource.LoadTown(set.town));
            var cuts = cutter.Resolve(set.requests);
            Assert.AreEqual(set.requests.Count, cuts.Length);
            var outcomes = cuts.GroupBy(c => c.outcome).Select(g => $"{g.Key} {g.Count()}");
            TestContext.WriteLine("cutter: " + string.Join(", ", outcomes));
            for (int i = 0; i < cuts.Length; i++)
            {
                Assert.AreEqual(set.requests[i].id, cuts[i].requestId);
                if (cuts[i].outcome == CutOutcome.Rejected) TestContext.WriteLine($"  rejected {set.requests[i].osmWay}: {cuts[i].rejectedBy} {cuts[i].reason}");
            }
        }
    }
}
