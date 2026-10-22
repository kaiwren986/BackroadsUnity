using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// roads-R4 (the card's done-when list): every mapped roundabout and mini-roundabout bakes with legs at least 70
    /// degrees apart or a reported exception, and none is invented (the town has none); every bridge has a deck,
    /// parapets and abutments, and the ground under it changes only under the abutment plates; every unpaved road
    /// meeting a paved one has a paved apron and a STOP; the town's alleys match the map; close junction pairs merge
    /// or stay two junctions with the usual rules; kerbs taper to flush where they end at an unkerbed road.
    /// Roundabouts are tested on synthetic maps (closed rings, a mini node, legs too close, a ring cut open).
    /// </summary>
    [TestFixture, Category("Roads")]
    public class RoadsR4
    {
        // ------------------------------------------------------------------ synthetic maps

        sealed class Synth
        {
            public readonly OsmRoadSource src = new OsmRoadSource { source = "synthetic", playHalf = 200 };
            long m_Way = 1;

            public Synth(double y = 10)
            {
                const int n = 601;
                var h = new float[n * n];
                for (int k = 0; k < h.Length; k++) h[k] = (float)y;
                src.dem = new GridHeightField(h, n, n, -300, -300, 1);
                src.kerbZone = new[] { new D2(-160, -160), new D2(160, -160), new D2(160, 160), new D2(-160, 160) };
            }

            public void Node(long id, double x, double z, params string[] tags) =>
                src.nodes[id] = new OsmRoadSource.Node { id = id, x = x, z = z, tags = Tags(tags) };

            public string Way(long[] nodes, params string[] tags)
            {
                long id = m_Way++;
                src.ways.Add(new OsmRoadSource.Way { id = id, osm = "way/" + id, nodes = nodes, tags = Tags(tags) });
                return "way/" + id;
            }

            static Dictionary<string, string> Tags(string[] kv)
            {
                var t = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var s in kv) { int i = s.IndexOf('='); t[s.Substring(0, i)] = s.Substring(i + 1); }
                return t;
            }
        }

        sealed class Run
        {
            public RoadGraphInput input;
            public OsmImportReport report;
            public RoadGraph g;
            public RoadSurfaces s;
            public KerbNetwork kn;
            public List<RoadNetworkAsset.Prop> props;
            public RoadMeshes meshes;
            public List<Plate> plates;
        }

        static Run Bake(Synth syn, bool kerbs = true)
        {
            var r = new Run();
            r.input = OsmRoadImporter.Import(syn.src, null, out r.report);
            r.g = RoadGraphBuilder.Build(r.input);
            r.s = SurfaceBuilder.Build(r.g, r.input);
            if (!kerbs) return r;
            var st = new KerbSettings();
            r.kn = KerbBuilder.Build(r.g, r.input, r.s, syn.src.playHalf, st);
            var streets = StreetNamer.Assign(r.g, new List<string>(), false);
            r.props = RoadPropBuilder.Build(r.g, r.s, r.kn, streets, st);
            r.meshes = RoadMeshBuilder.Build(r.g, r.s, r.kn, streets, st);
            r.plates = RoadPlateBuilder.Build(r.g, r.s, r.kn, syn.src.dem, st);
            return r;
        }

        /// <summary>A closed junction=roundabout ring of the given centreline radius (16 nodes, one way) and straight legs at the given angles.</summary>
        static Synth Ring(double radius, double[] legDeg, string legHighway = "residential", bool close = true)
        {
            var s = new Synth();
            var ring = new List<long>();
            for (int k = 0; k < 16; k++)
            {
                double a = 2 * Math.PI * k / 16;
                s.Node(100 + k, radius * Math.Cos(a), radius * Math.Sin(a));
                ring.Add(100 + k);
            }
            if (close) ring.Add(100);
            else ring.RemoveAt(ring.Count - 1);
            s.Way(ring.ToArray(), "highway=tertiary", "junction=roundabout");
            long id = 1000;
            foreach (var deg in legDeg)
            {
                double a = deg * Geo.Deg;
                // the ring node nearest the leg's direction
                int k = (int)Math.Round(a / (2 * Math.PI) * 16) % 16;
                long n1 = id++, n2 = id++;
                s.Node(n1, 60 * Math.Cos(a), 60 * Math.Sin(a));
                s.Node(n2, 140 * Math.Cos(a), 140 * Math.Sin(a));
                s.Way(new long[] { 100 + k, n1, n2 }, "highway=" + legHighway);
            }
            return s;
        }

        static GraphNode RoundaboutNode(RoadGraph g) => g.nodes.SingleOrDefault(n => n.kind == NodeKind.Roundabout);

        [Test]
        public void RoundaboutFromAClosedRing()
        {
            var r = Bake(Ring(13.25, new[] { 0.0, 90, 180, 270 }));
            TestContext.WriteLine(string.Join("\n", r.report.roundabouts));
            TestContext.WriteLine(string.Join("\n", r.g.notes.Concat(r.s.notes)));
            var n = RoundaboutNode(r.g);
            Assert.NotNull(n, "one Roundabout node");
            Assert.AreEqual(4, n.Degree, "four legs");
            Assert.AreEqual(Control.RoundaboutYield, n.control);
            foreach (var id in n.arms) Assert.AreEqual(Control.RoundaboutYield, r.g.arms[id].control, "every entry yields");
            Assert.IsTrue(r.report.skipped.Any(x => x.Contains("a roundabout ring")), "the ring way leaves the import with its reason");
            Assert.AreEqual(1, r.report.roundabouts.Count);

            var ns = r.s.Of(n.id);
            Assert.AreEqual(SurfaceMode.Roundabout, ns.mode, ns.problem);
            var G = ns.roundabout;
            TestContext.WriteLine(RoundaboutBuilder.Describe(G));
            Assert.AreEqual(32.0, G.icd, 0.2, "ICD = 2 (ring radius + half the circulatory width)");
            Assert.AreEqual(G.Ro - 5.5, G.Ri, 1e-9);
            Assert.AreEqual(G.Ri - 3.0, G.Ra, 1e-9, "a 3 m truck apron");
            Assert.IsFalse(G.mini);
            Assert.IsTrue(Geom2.IsSimple(ns.polygon, out string why), why);
            Assert.Greater(Geom2.SignedArea(ns.polygon), Math.PI * G.Ro * G.Ro, "the surface holds the whole inscribed circle");
            Assert.AreEqual(4, ns.corners.Count);
            foreach (var L in G.legs)
            {
                var a = ns.arms[L.arm];
                Assert.AreEqual(2, L.splitters.Count, "a raised splitter in two pieces (the cut-through at the crosswalk)");
                Assert.AreEqual(G.Ro + 6.0, L.sCwInner, 1e-9, "the crosswalk one car length behind the yield line");
                Assert.Greater(a.sMouth, L.splitterS1, "the splitter ends inside the surface");
                Assert.AreEqual(-1, a.sStop, "no stop line at a yield entry");
                foreach (var sp in L.splitters) Assert.Greater(Geom2.SignedArea(sp), 1.0);
                // the lanes beside the splitter keep the leg's width
                double s = 0.5 * (L.splitterS0 + L.splitterS1);
                Assert.AreEqual(a.hwL, L.EdgeL(s) - L.FlareAt(s), 1e-9);
            }
            Assert.IsEmpty(r.g.notes.Where(x => x.StartsWith("roundabout exception", StringComparison.Ordinal)));

            // signs, paint, islands, ramps and plates
            var yields = r.props.Where(p => p.node == n.id && p.kind == RoadPropKind.Yield).ToList();
            Assert.AreEqual(8, yields.Count, "a YIELD on the right of each entry and one on its splitter");
            Assert.IsFalse(r.props.Any(p => p.node == n.id && p.kind == RoadPropKind.Stop));
            Assert.AreEqual(4, r.meshes.parts.Count(p => p.marking == MarkingKind.YieldLine && p.node == n.id), "yield lines at every entry");
            Assert.AreEqual(0, r.meshes.parts.Count(p => p.marking == MarkingKind.StopLine && p.node == n.id), "no stop lines");
            Assert.AreEqual(4, r.meshes.parts.Count(p => p.marking == MarkingKind.Crosswalk && p.node == n.id && p.material == RoadMaterial.Paint) / 2, "two lines per leg crosswalk");
            var islands = r.meshes.parts.Where(p => p.material == RoadMaterial.Concrete && p.node == n.id).ToList();
            Assert.AreEqual(1 + 8, islands.Count, "the apron and island, and eight splitter pieces");
            Assert.IsTrue(islands.All(p => p.collider));
            var kc = r.kn.corners.Where(c => c.node == n.id && c.kerbed).ToList();
            Assert.AreEqual(4, kc.Count, "four kerbed outer curves");
            Assert.IsTrue(kc.All(c => c.ramp == RampKind.Perpendicular), "ramps at the leg crosswalks");
            Assert.IsTrue(r.plates.Any(p => p.id.StartsWith("island:", StringComparison.Ordinal) && p.surface == RoadPlateBuilder.Grass));
            Assert.IsTrue(r.plates.Any(p => p.id.StartsWith("splitter:", StringComparison.Ordinal)));
            TestContext.WriteLine($"{yields.Count} YIELD signs, {islands.Count} island parts ({islands.Sum(p => p.Triangles)} triangles), {r.kn.corners.Count(c => c.node == n.id)} kerb curves");
        }

        [Test]
        public void MiniRoundaboutFromANode()
        {
            var s = new Synth();
            s.Node(1, -140, 0); s.Node(2, 0, 0, "highway=mini_roundabout"); s.Node(3, 140, 0);
            s.Node(4, 0, -140); s.Node(5, 0, 140);
            s.Way(new long[] { 1, 2, 3 }, "highway=residential");
            s.Way(new long[] { 4, 2, 5 }, "highway=residential");
            var r = Bake(s);
            var n = RoundaboutNode(r.g);
            Assert.NotNull(n);
            var ns = r.s.Of(n.id);
            Assert.AreEqual(SurfaceMode.Roundabout, ns.mode, ns.problem);
            var G = ns.roundabout;
            TestContext.WriteLine(RoundaboutBuilder.Describe(G));
            Assert.IsTrue(G.mini);
            Assert.AreEqual(18.0, G.icd, 1e-9, "the default mini diameter");
            Assert.AreEqual(G.Ri, G.Ra, 1e-9, "no truck apron: the whole island is mountable");
            Assert.IsTrue(G.legs.All(L => L.splitters.Count == 0), "painted splitters only");
            Assert.IsTrue(Geom2.IsSimple(ns.polygon, out string why), why);
        }

        [Test]
        public void SmallRingBecomesAMini()
        {
            var r = Bake(Ring(8.0, new[] { 0.0, 120, 240 }), kerbs: false);
            var ns = r.s.Of(RoundaboutNode(r.g).id);
            Assert.IsTrue(ns.roundabout.mini, "ICD 21.5 m < 26 m");
        }

        [Test]
        public void LegsTooCloseAreAReportedException()
        {
            var r = Bake(Ring(13.25, new[] { 0.0, 50, 180 }), kerbs: false);
            var n = RoundaboutNode(r.g);
            Assert.NotNull(n);
            TestContext.WriteLine(string.Join("\n", r.g.notes.Concat(r.s.notes)));
            Assert.IsTrue(r.g.notes.Any(x => x.StartsWith("roundabout exception", StringComparison.Ordinal)), "legs under 70 degrees apart are reported");
            var ns = r.s.Of(n.id);
            Assert.IsTrue(ns.mode == SurfaceMode.Roundabout || ns.mode == SurfaceMode.Fallback);
            if (ns.mode == SurfaceMode.Roundabout) Assert.IsTrue(Geom2.IsSimple(ns.polygon, out string why), why);
        }

        // ------------------------------------------------------------------ the town

        static KerbBaker.Bake s_Town;
        static List<Plate> s_TownPlates;
        static RoadNetworkAsset s_TownAsset;

        static KerbBaker.Bake Town()
        {
            if (s_Town != null) return s_Town;
            string town = TownRoadSource.Towns().First();
            s_TownAsset = RoadNetworkBaker.BakeTownWithKerbs(town, out var b, out var k, out var f);
            UnityEngine.Object.DestroyImmediate(f);
            k.meshes = RoadMeshBuilder.Build(b.graph, b.surfaces, k.kerbs, k.streets, k.settings, k.bridges, b.source.dem);
            s_TownPlates = RoadPlateBuilder.Build(b.graph, b.surfaces, k.kerbs, b.source.dem, k.settings);
            return s_Town = k;
        }

        [OneTimeTearDown]
        public void Release()
        {
            if (s_TownAsset != null) UnityEngine.Object.DestroyImmediate(s_TownAsset);
            s_TownAsset = null; s_Town = null; s_TownPlates = null;
        }

        [Test]
        public void TownChecksPass()
        {
            var k = Town();
            var c = RoadR4Report.Run(k, s_TownPlates);
            TestContext.WriteLine(RoadR4Report.Summary(c));
            foreach (var l in c.bridges.Concat(c.roundabouts)) TestContext.WriteLine("  " + l);
            Assert.IsEmpty(c.pairProblems, string.Join("; ", c.pairProblems));
            Assert.IsEmpty(c.alleyProblems, string.Join("; ", c.alleyProblems));
            Assert.IsEmpty(c.apronProblems, string.Join("; ", c.apronProblems));
            Assert.IsEmpty(c.taperProblems, string.Join("; ", c.taperProblems));
            Assert.IsEmpty(c.bridgeProblems, string.Join("; ", c.bridgeProblems));
            Assert.IsEmpty(c.roundaboutProblems, string.Join("; ", c.roundaboutProblems));
            Assert.AreEqual(c.mapAlleys, c.graphAlleys + c.skippedAlleys, "the alley count matches the map");
            Assert.Greater(c.alleyMouths, 0);
            Assert.Greater(c.unpavedMeetings, 0);
            Assert.Greater(c.kerbTapers, 0);
        }

        [Test]
        public void TownHasNoInventedRoundabout()
        {
            var k = Town();
            Assert.AreEqual(0, k.roads.graph.CountKind(NodeKind.Roundabout));
            Assert.IsEmpty(k.roads.import.roundabouts, "the town maps no roundabout");
            Assert.IsEmpty(s_TownAsset.roundabouts);
        }

        [Test]
        public void RiverBridgeIsOneDeck()
        {
            var k = Town();
            var deck = k.bridges.OrderByDescending(d => d.length).First();
            TestContext.WriteLine(BridgeBuilder.Describe(deck));
            Assert.AreEqual("river", deck.over);
            Assert.Greater(deck.length, 200, "the 226 m river bridge in one piece");
            Assert.Greater(deck.FreeSpan, 150);
            Assert.GreaterOrEqual(deck.piers.Count, 4, "spans of at most 45 m");
            for (int i = 0; i + 1 < deck.piers.Count; i++) Assert.LessOrEqual(deck.piers[i + 1] - deck.piers[i], 45.0 + 1e-6);
            Assert.GreaterOrEqual(deck.clearanceMin, 0.5);
            Assert.IsTrue(Geom2.IsSimple(deck.outline, out string why), why);
            // the asset carries the footprint for lane W
            var ad = s_TownAsset.decks.Single(d => d.length > 200);
            Assert.AreEqual(deck.pieces.Count, ad.edges.Length);
            Assert.AreEqual(deck.outline.Length, ad.outline.Length);
            // the ground under the free span keeps the DEM: no plate but the abutments' and other roads'
            Assert.IsTrue(s_TownPlates.Any(p => p.id.StartsWith("abutment:", StringComparison.Ordinal)));
            // parapets collide; the parapet top stands 0.85 m over the pavement edge
            Assert.IsTrue(k.meshes.parts.Any(p => p.edge == deck.edge && p.collider && p.material == RoadMaterial.Concrete));
        }

        [Test]
        public void OpenRingStaysRoads()
        {
            var r = Bake(Ring(13.25, new[] { 0.0, 90, 180, 270 }, close: false), kerbs: false);
            Assert.IsNull(RoundaboutNode(r.g), "a ring that does not close is not a roundabout");
            Assert.IsTrue(r.report.roundabouts.Any(x => x.Contains("does not close")));
        }
    }
}
