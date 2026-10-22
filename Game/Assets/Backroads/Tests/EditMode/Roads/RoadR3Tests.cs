using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// roads-R3 (the card's done-when list): exactly one STOP (or YIELD, or signal) per controlled approach, on its right,
    /// 0.6-2 m from the kerb face and within 15.2 m of the intersected edge (or 4 ft before a marked crosswalk further
    /// out, listed); stop lines only with STOP or signals and on every paved one; no yield line without a YIELD; every
    /// tagged control honoured or listed with its reason; centre lines never on a junction surface; dashes phase-locked;
    /// paint over its surface; one name per street; the chunk bake within 60k triangles per km², deterministic, current,
    /// readable, its colliders on the plates' paved surfaces.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class RoadsR3
    {
        sealed class Bake
        {
            public Kerbs.Bake k;
            public List<StreetInfo> streets;
            public List<RoadNetworkAsset.Prop> props;
            public RoadMeshes meshes;
            public RoadChunkSet chunks;
            public RoadMeshReport.Checks checks;
            public double half;
        }

        static readonly Dictionary<string, Bake> s_Cache = new Dictionary<string, Bake>();

        static Bake Get(string which)
        {
            if (s_Cache.TryGetValue(which, out var b)) return b;
            var k = Kerbs.Get(which);
            var names = StreetNameList.Load();
            var streets = StreetNamer.Assign(k.g, names.names, names.approved);
            var props = RoadPropBuilder.Build(k.g, k.s, k.kn, streets, k.k.settings);
            var meshes = RoadMeshBuilder.Build(k.g, k.s, k.kn, streets, k.k.settings);
            double half = k.k.roads.source.playHalf;
            var chunks = RoadChunkBaker.Build(meshes, half, k.g.source, "", "", out _, out _);
            b = new Bake { k = k, streets = streets, props = props, meshes = meshes, chunks = chunks, half = half, checks = RoadMeshReport.Run(k.g, k.s, props, meshes, chunks, half) };
            s_Cache[which] = b;
            return b;
        }

        [OneTimeTearDown]
        public void Release()
        {
            foreach (var b in s_Cache.Values) if (b.chunks != null) UnityEngine.Object.DestroyImmediate(b.chunks);
            s_Cache.Clear();
        }

        [TestCase("village")]
        [TestCase("town")]
        public void SignsAtEveryControlledApproach(string which)
        {
            var b = Get(which);
            var c = b.checks;
            TestContext.WriteLine($"{c.controlled} controlled approaches, {c.signs} signs ({c.stopSigns} STOP), {c.plaques} plaques, {c.blades} blades; {c.signExceptions.Count} set back by a crosswalk");
            foreach (var l in c.signExceptions) TestContext.WriteLine("  " + l);
            Assert.Greater(c.controlled, 0);
            Assert.IsEmpty(c.signProblems, string.Join("\n", c.signProblems));
            Assert.AreEqual(c.controlled, c.signs, "one sign per controlled approach");
            Assert.LessOrEqual(c.maxFromEdge, 15.2 + 1e-3);
            // every crosswalk exception really is a stop line set back by a crosswalk beyond 15.2 m
            foreach (var l in c.signExceptions) StringAssert.Contains("before the crosswalk", l);
            Assert.LessOrEqual(c.signExceptions.Count, 3, "signs set back by a crosswalk stay rare (long corners for R4)");
            // street-name blades name two different, existing streets
            foreach (var p in b.props.Where(p => p.kind == RoadPropKind.StreetName))
            {
                Assert.IsTrue(p.street >= 0 && p.street < b.streets.Count && p.street2 >= 0 && p.street2 < b.streets.Count);
                Assert.AreNotEqual(p.street, p.street2);
            }
        }

        /// <summary>The synthetic five-arm star of R1 is an all-way stop: five STOPs, five plaques on their posts.</summary>
        [Test]
        public void AllWayStopGetsPlaques()
        {
            var input = Roads.ScenarioPublic("star5");
            var g = RoadGraphBuilder.Build(input, new RoadGraphSettings { zoneBuffer = 1000 });
            foreach (var r in input.roads) r.inKerbZone = true;
            var s = SurfaceBuilder.Build(g, input);
            var kn = KerbBuilder.Build(g, input, s, 1024);
            var streets = StreetNamer.Assign(g, null, false);
            var props = RoadPropBuilder.Build(g, s, kn, streets);
            var node = g.nodes.First(n => n.Degree == 5);
            Assert.AreEqual(Control.AllWayStop, node.control);
            var stops = props.Where(p => p.node == node.id && p.kind == RoadPropKind.Stop).ToList();
            var plaques = props.Where(p => p.node == node.id && p.kind == RoadPropKind.AllWayPlaque).ToList();
            Assert.AreEqual(5, stops.Count);
            Assert.AreEqual(5, plaques.Count);
            foreach (var pl in plaques)
            {
                Assert.IsTrue(stops.Any(st => st.position == pl.position && st.arm == pl.arm), "the plaque hangs on its STOP post");
                Assert.Less(pl.mount, stops.First(st => st.arm == pl.arm).mount, "under the STOP sign");
            }
            var meshes = RoadMeshBuilder.Build(g, s, kn, streets);
            Assert.AreEqual(5, meshes.parts.Count(p => p.marking == MarkingKind.StopLine && (p.lods & 1) != 0), "a stop line on every all-way approach");
        }

        [TestCase("village")]
        [TestCase("town")]
        public void LinesWhereTheirSignsAre(string which)
        {
            var b = Get(which);
            var c = b.checks;
            Assert.IsEmpty(c.lineProblems, string.Join("\n", c.lineProblems));
            Assert.IsEmpty(c.centreOnJunction, string.Join("\n", c.centreOnJunction));
            Assert.IsFalse(c.tagProblems.Any(t => t.Contains("no reason recorded")), "every mapped control not honoured says why");
            foreach (var t in c.tagProblems) TestContext.WriteLine("  " + t);
            Assert.AreEqual(0, b.meshes.parts.Count(p => p.marking == MarkingKind.YieldLine), "no yield lines without roundabouts (R4)");
            TestContext.WriteLine($"{b.meshes.stopLines} stop lines, {b.meshes.crosswalks} crosswalks, centre {b.meshes.centreLineM:F0} m ({b.meshes.noPassingM:F0} m no passing), edge {b.meshes.edgeLineM:F0} m");
        }

        [TestCase("town")]
        public void PaintLiesOnItsSurface(string which)
        {
            var b = Get(which);
            var asphalt = new SurfaceIndex();
            foreach (var p in b.meshes.parts.Where(p => p.material == RoadMaterial.Asphalt)) asphalt.Add(p);
            int on = 0, phased = 0;
            var frames = new Dictionary<int, RoadFrame>();
            foreach (var p in b.meshes.parts.Where(p => p.material == RoadMaterial.Paint && p.marking != MarkingKind.Warning && (p.lods & 1) != 0))
            {
                foreach (var v in p.pos)
                {
                    if (!asphalt.Height(v.XZ, out double y)) continue;
                    on++;
                    Assert.GreaterOrEqual(v.y, y + 0.003, $"{p.marking} paint at {v} sinks into the asphalt ({y:F4})");
                    Assert.LessOrEqual(v.y, y + 0.03, $"{p.marking} paint at {v} floats over the asphalt ({y:F4})");
                }
                // a dash (3.05 m or less) lies inside one 12.2 m period at its start: phase-locked to s = 0 of its edge
                if (p.dashed && p.edge >= 0)
                {
                    if (!frames.TryGetValue(p.edge, out var f)) frames[p.edge] = f = new RoadFrame(b.k.g.edges[p.edge].pts);
                    f.ToFrame(new D2((p.pos[0].x + p.pos[1].x) / 2, (p.pos[0].z + p.pos[1].z) / 2), out double s0, out _);
                    int n = p.pos.Count;
                    f.ToFrame(new D2((p.pos[n - 2].x + p.pos[n - 1].x) / 2, (p.pos[n - 2].z + p.pos[n - 1].z) / 2), out double s1, out _);
                    phased++;
                    double k0 = Math.Floor((Math.Min(s0, s1) + 0.05) / 12.2);
                    Assert.LessOrEqual(Math.Max(s0, s1), k0 * 12.2 + 3.05 + 0.05, $"edge {p.edge}: dash {s0:F2}-{s1:F2} is off the 12.2 m phase");
                }
            }
            TestContext.WriteLine($"{on} paint vertices over asphalt, {phased} dashes checked");
            Assert.Greater(on, 1000);
        }

        [TestCase("village")]
        [TestCase("town")]
        public void OneNamePerStreet(string which)
        {
            var b = Get(which);
            var g = b.k.g;
            var seen = new Dictionary<int, int>();
            foreach (var s in b.streets)
                foreach (int e in s.edges)
                {
                    Assert.IsFalse(seen.ContainsKey(e), $"edge {e} in two streets");
                    seen[e] = s.id;
                }
            foreach (var e in g.edges)
                Assert.AreEqual(StreetNamer.Named(e.cls), seen.ContainsKey(e.id), $"edge {e.id} ({e.cls}): named streets cover exactly the named classes");
            for (int i = 1; i < b.streets.Count; i++)
                Assert.LessOrEqual(b.streets[i - 1].firstWayId == 0 ? long.MaxValue : b.streets[i - 1].firstWayId, b.streets[i].firstWayId == 0 ? long.MaxValue : b.streets[i].firstWayId, "streets in OSM way order");
            var list = StreetNameList.Load();
            Assert.GreaterOrEqual(list.names.Count, b.streets.Count, "the proposed list has a name for every street");
            Assert.AreEqual(list.names.Count, list.names.Distinct().Count(), "no name twice");
            if (!list.approved) Assert.IsTrue(b.streets.All(s => s.placeholder && s.name.StartsWith("Street ", StringComparison.Ordinal)), "placeholders until the list is approved");
            // approved, the names come in order
            var approved = StreetNamer.Assign(g, list.names, true);
            for (int i = 0; i < approved.Count; i++) Assert.AreEqual(list.names[i], approved[i].name);
        }

        [TestCase("town")]
        public void ChunksWithinBudgetAndReadable(string which)
        {
            var b = Get(which);
            var set = b.chunks;
            double km2 = (2 * b.half) * (2 * b.half) / 1e6;
            TestContext.WriteLine($"{set.chunks.Length} chunks, LOD0 {set.lod0Triangles}, LOD1 {set.lod1Triangles}, collider {set.colliderTriangles}: {set.lod0Triangles / km2:F0} triangles per km²");
            Assert.LessOrEqual(set.lod0Triangles / km2, 60000, "at most 60k triangles per km² of town");
            Assert.LessOrEqual(set.lod1Triangles, set.lod0Triangles);
            foreach (var c in set.chunks) Assert.Less(c.vertices, 65536, "a chunk fits 16-bit indices");
            // determinism: the same bake twice
            var again = RoadChunkBaker.Build(RoadMeshBuilder.Build(b.k.g, b.k.s, b.k.kn, b.streets, b.k.k.settings), b.half, b.k.g.source, "", "", out _, out _);
            try { Assert.AreEqual(set.contentHash, again.contentHash, "two mesh bakes agree"); }
            finally { UnityEngine.Object.DestroyImmediate(again); }
        }

        [Test]
        public void CommittedChunksCurrentAndLoadable()
        {
            string town = TownRoadSource.Towns().First();
            var baked = RoadChunkBaker.LoadBakedTown(town);
            Assert.NotNull(baked, "no committed " + RoadChunkBaker.TownAssetPath(town) + " (run RoadBakeCli.BakeTowns)");
            Assert.NotNull(baked.data, "the chunk data file is attached");
            var road = RoadNetworkBaker.LoadBakedTown(town);
            var frontage = KerbBaker.LoadBakedTown(town);
            Assert.AreEqual(road.contentHash, baked.roadContentHash);
            Assert.AreEqual(frontage.contentHash, baked.frontageContentHash);
            Assert.AreEqual(RoadNetworkAsset.FormatVersion, road.formatVersion);
            Assert.Greater(road.streets.Length, 0);
            Assert.Greater(road.props.Length, 0);
            // the committed set matches a fresh bake (same network and frontage hashes)
            var b = Get("town");
            var fresh = RoadChunkBaker.Build(b.meshes, b.half, b.k.g.source, road.contentHash, frontage.contentHash, out _, out _);
            try { Assert.AreEqual(baked.contentHash, fresh.contentHash, "the committed road chunks are current (run RoadBakeCli.BakeTowns)"); }
            finally { UnityEngine.Object.DestroyImmediate(fresh); }
            // every chunk decodes into meshes and a collider
            var all = baked.Decompress();
            int tris = 0;
            foreach (var c in baked.chunks)
            {
                RoadChunkSet.ReadChunk(all, c, out var l0, out var l1, out var col);
                Assert.AreEqual(c.vertices, l0.vertexCount);
                Assert.AreEqual(3, l0.subMeshCount);
                int t = 0;
                for (int s = 0; s < 3; s++) t += (int)l0.GetIndexCount(s) / 3;
                Assert.AreEqual(c.lod0Triangles, t);
                Assert.AreEqual(c.colliderTriangles, (int)col.GetIndexCount(0) / 3);
                tris += t;
                UnityEngine.Object.DestroyImmediate(l0); UnityEngine.Object.DestroyImmediate(l1); UnityEngine.Object.DestroyImmediate(col);
            }
            Assert.AreEqual(baked.lod0Triangles, tris);
        }

        /// <summary>The colliders lie on the plates' paved surfaces (05 §5.13: grid surface ids and colliders agree) and a ray finds the road.</summary>
        [Test]
        public void CollidersOnPavedPlates()
        {
            var b = Get("town");
            var field = new PlateField(b.k.plates);
            int checkedTris = 0, agree = 0;
            foreach (var p in b.meshes.parts.Where(p => p.collider))
                for (int k = 0; k + 2 < p.tri.Count; k += 9)
                {
                    D3 a = p.pos[p.tri[k]], c = p.pos[p.tri[k + 1]], d = p.pos[p.tri[k + 2]];
                    // horizontal-ish triangles only (kerb faces stand on the line between two plates)
                    double ux = c.x - a.x, uz = c.z - a.z, vx = d.x - a.x, vz = d.z - a.z;
                    if (Math.Abs(ux * vz - uz * vx) < 0.05) continue;
                    var q = new D2((a.x + c.x + d.x) / 3, (a.z + c.z + d.z) / 3);
                    if (!field.Sample(q, out _, out var win)) continue;
                    checkedTris++;
                    bool ok = p.material == RoadMaterial.Asphalt ? win.surface == 4 || win.surface == 6 : win.surface == 6 || win.surface == 4;
                    if (ok) agree++;
                }
            TestContext.WriteLine($"{agree} of {checkedTris} collider triangles on a paved plate");
            Assert.Greater(checkedTris, 1000);
            Assert.GreaterOrEqual(agree, checkedTris * 0.99);

            // load the committed chunks without renderers (a server) and drop a ray onto a junction
            string town = TownRoadSource.Towns().First();
            var set = RoadChunkBaker.LoadBakedTown(town);
            var root = RoadChunkLoader.Spawn(set, null, null);
            try
            {
                Physics.SyncTransforms();
                var ns = b.k.s.nodes.First(n => n.mode == SurfaceMode.Major && n.arms.Count >= 4);
                var node = b.k.g.nodes[ns.node];
                var origin = new Vector3((float)node.p.x, (float)node.p.y + 50, (float)node.p.z);
                Assert.IsTrue(Physics.Raycast(origin, Vector3.down, out var hit, 100), "a ray from above hits the road collider");
                Assert.AreEqual(SurfaceBuilder.JunctionHeight(ns, node.p.XZ, new SurfaceSettings()), hit.point.y, 0.02, "at the junction surface's height");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}

namespace Backroads.Tests.EditMode.Roads
{
    public partial class Roads
    {
        /// <summary>R1's synthetic shapes for the other fixtures (a 30° Y, a 5-arm star, two close junctions, a T on a curve, a short arm).</summary>
        internal static Backroads.Roads.RoadGraphInput ScenarioPublic(string name) => Scenario(name);
    }
}
