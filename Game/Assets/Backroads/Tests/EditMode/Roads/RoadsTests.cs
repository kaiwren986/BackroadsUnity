using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Canon.Editor;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// T2.8 Roads R0 (05 §5.1, §9): the cleaned graph built from CanonData v1 matches 05's node table, picks the major road
    /// by class, is connected, and its plain ribbons cover the canon centrelines within 0.1 m. Node numbers are 05's
    /// (traffic.json's) where the legacy network had the node; JS coordinates are quoted as 05 quotes them.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class Roads
    {
        static RoadGraph s_Graph;
        static RoadGraphInput s_Input;

        static RoadGraph G()
        {
            if (s_Graph != null) return s_Graph;
            s_Graph = RoadNetworkBaker.BuildFromCanon(out s_Input, out var msg);
            Assert.NotNull(s_Graph, msg);
            return s_Graph;
        }

        static RoadGraphInput Input()
        {
            G();
            return s_Input;
        }

        static GraphNode N(int id)
        {
            var n = G().nodes[id];
            Assert.AreEqual(id, n.id);
            return n;
        }

        static int[] ArmRoads(GraphNode n) => n.arms.Select(a => G().arms[a].sourceRoad).OrderBy(r => r).ToArray();

        static string Ids(IEnumerable<GraphNode> l) => string.Join(",", l.Select(n => n.id));

        // 05 §1.2 (node inventory) after the §5.1 cleanup
        static readonly int[] Junctions = { 0, 1, 2, 3, 4, 5, 7, 9, 16, 17, 19 };
        static readonly int[] XJunctions = { 2, 4, 5, 7, 9, 16 };
        static readonly int[] TJunctions = { 0, 1, 3, 17, 19 };
        static readonly int[] Bends = { 13, 14, 15, 18, 20 };
        static readonly int[] DeadEnds = { 6, 8, 10, 11, 12 };
        static readonly (double x, double z)[] DirtEndsJs = { (-204, -95), (-256, 194), (326, -91), (141, -250), (90, 153), (186, -46) };

        [Test]
        public void NodeCounts()
        {
            var g = G();
            TestContext.WriteLine(RoadGraphReport.NodeTable(g));
            Assert.AreEqual(24, g.inputRoads, "canon roads");
            Assert.AreEqual(1722, g.inputPoints, "canon road points");

            var kinds = g.nodes.GroupBy(n => n.kind).ToDictionary(k => k.Key, k => k.ToList());
            List<GraphNode> Of(NodeKind k) => kinds.TryGetValue(k, out var l) ? l : new List<GraphNode>();

            Assert.AreEqual(Junctions, Of(NodeKind.Junction).Select(n => n.id).OrderBy(i => i).ToArray(), "paved junctions (11: 5 T + 6 X)");
            Assert.AreEqual(XJunctions, Of(NodeKind.Junction).Where(n => n.Degree == 4).Select(n => n.id).OrderBy(i => i).ToArray(), "X junctions");
            Assert.AreEqual(TJunctions, Of(NodeKind.Junction).Where(n => n.Degree == 3).Select(n => n.id).OrderBy(i => i).ToArray(), "T junctions (square counted as a T)");
            Assert.AreEqual(Bends, Of(NodeKind.Bend).Select(n => n.id).OrderBy(i => i).ToArray(), "L-bends");
            Assert.IsTrue(Of(NodeKind.Bend).All(n => n.Degree == 2));
            Assert.AreEqual(DeadEnds, Of(NodeKind.DeadEnd).Select(n => n.id).OrderBy(i => i).ToArray(), "work-site dead ends");
            Assert.IsTrue(Of(NodeKind.DeadEnd).All(n => n.Degree == 1));
            Assert.AreEqual(6, Of(NodeKind.DirtApron).Count, "dirt-road-to-highway T junctions: " + Ids(Of(NodeKind.DirtApron)));
            Assert.AreEqual(4, Of(NodeKind.ClassChange).Count, "town-edge class changes: " + Ids(Of(NodeKind.ClassChange)));
            Assert.AreEqual(0, Of(NodeKind.Join).Count, "joins left over: " + Ids(Of(NodeKind.Join)));
            Assert.AreEqual(31, g.nodes.Count, "nodes");
            Assert.AreEqual(40, g.edges.Count, "edges");
            Assert.AreEqual(80, g.nodes.Sum(n => n.Degree), "arm ends (2 per edge)");
            Assert.AreEqual(80, g.arms.Count);

            // every traffic.json node keeps its number
            Assert.AreEqual(21, Input().legacyNodes.Count);
            for (int i = 0; i <= 20; i++) Assert.AreEqual(i, N(i).legacyId, $"node {i} legacy id");
            Assert.IsTrue(g.nodes.Where(n => n.id > 20).All(n => n.legacyId < 0));

            // the six dirt ends of 05 §1.2 ("not in net" before), each a T: one dirt arm, two arms of one paved road
            foreach (var (x, z) in DirtEndsJs)
            {
                var n = g.nodes.OrderBy(q => Math.Abs(-q.p.x - x) + Math.Abs(q.p.z - z)).First();
                Assert.AreEqual(NodeKind.DirtApron, n.kind, $"dirt end ({x}, {z})");
                Assert.Less(Math.Sqrt((-n.p.x - x) * (-n.p.x - x) + (n.p.z - z) * (n.p.z - z)), 1.0, $"dirt apron near ({x}, {z})");
                var arms = n.arms.Select(a => g.arms[a]).ToList();
                Assert.AreEqual(3, arms.Count);
                Assert.AreEqual(1, arms.Count(a => a.cls == RoadClass.Dirt));
                Assert.AreEqual(1, arms.Where(a => a.cls != RoadClass.Dirt).Select(a => a.sourceRoad).Distinct().Count());
            }

            // arm sets as in 05 §1.2
            var expectArms = new Dictionary<int, int[]>
            {
                [0] = new[] { 0, 1, 2 }, [1] = new[] { 0, 0, 3 }, [2] = new[] { 0, 0, 22, 23 }, [3] = new[] { 0, 4, 5 },
                [4] = new[] { 1, 1, 20, 21 }, [5] = new[] { 1, 1, 17, 18 }, [7] = new[] { 2, 2, 13, 14 }, [9] = new[] { 3, 3, 10, 11 },
                [16] = new[] { 11, 12, 19, 20 }, [17] = new[] { 12, 16, 17 }, [19] = new[] { 14, 15, 21 },
            };
            foreach (var kv in expectArms) Assert.AreEqual(kv.Value, ArmRoads(N(kv.Key)), $"node {kv.Key} arms");
        }

        [Test]
        public void MajorRoadByClass()
        {
            var g = G();
            var problems = RoadGraphChecks.MajorProblems(g);
            Assert.IsEmpty(problems, string.Join("\n", problems));

            void Major(int node, int roadA, int roadB, Control control)
            {
                var n = N(node);
                Assert.IsTrue(n.HasMajor, $"node {node} has no major road");
                var got = new[] { g.Arm(n, n.majorA).sourceRoad, g.Arm(n, n.majorB).sourceRoad }.OrderBy(r => r).ToArray();
                Assert.AreEqual(new[] { roadA, roadB }.OrderBy(r => r).ToArray(), got, $"node {node} major road");
                Assert.AreEqual(control, n.control, $"node {node} control");
            }

            // the two fixes of 05 §0 item 2: the highway stays major and the streets stop
            Major(2, 0, 0, Control.MinorStop);
            Major(5, 1, 1, Control.MinorStop);
            Major(3, 0, 5, Control.MinorStop);
            Major(4, 1, 1, Control.MinorStop);
            Major(7, 2, 2, Control.MinorStop);
            Major(9, 3, 3, Control.MinorStop);
            Major(17, 16, 17, Control.MinorStop);
            Major(19, 14, 15, Control.MinorStop);
            Major(1, 0, 0, Control.None); // complex pair with node 0: no control until decision U8

            // node 16: the only street-by-street X, all-way stop, so no major (05 §5.1 step 7, §6.2 rule 4)
            Assert.IsFalse(N(16).HasMajor);
            Assert.AreEqual(Control.AllWayStop, N(16).control);
            // node 0: the square; its best pair bends 39 degrees (over 0.6 rad), and it waits for the roundabout decision
            Assert.IsFalse(N(0).HasMajor);
            Assert.Greater(N(0).bestPairBend, g.settings.majorMaxBend);

            // what trafficNet picked (traffic.json): the streets at nodes 2 and 5 (the bug 05 fixes)
            var legacy = CanonImporter.LoadImported().traffic;
            int LegacyRoad(int edge) => legacy.edges.First(e => e.id == edge).road;
            Assert.IsTrue(legacy.nodes[2].major.Select(LegacyRoad).All(r => r >= 9), "trafficNet made streets major at node 2");
            Assert.IsTrue(legacy.nodes[5].major.Select(LegacyRoad).All(r => r >= 9), "trafficNet made streets major at node 5");

            // dirt aprons: the highway goes through, the dirt road stops
            foreach (var n in g.nodes.Where(q => q.kind == NodeKind.DirtApron))
            {
                Assert.IsTrue(n.HasMajor, $"dirt apron {n.id} has no major road");
                Assert.AreNotEqual(RoadClass.Dirt, g.Arm(n, n.majorA).cls);
                Assert.AreNotEqual(RoadClass.Dirt, g.Arm(n, n.majorB).cls);
                Assert.AreEqual(Control.MinorStop, n.control);
            }
        }

        [Test]
        public void GraphConnected()
        {
            Assert.IsTrue(RoadGraphChecks.Connected(G(), false, out int r, out int t), $"all roads: reached {r} of {t} nodes");
            Assert.AreEqual(31, t);
            Assert.IsTrue(RoadGraphChecks.Connected(G(), true, out r, out t), $"paved roads only: reached {r} of {t} nodes");
            foreach (var e in G().edges)
            {
                Assert.AreNotEqual(e.nodeA, e.nodeB, $"edge {e.id} is a loop");
                Assert.GreaterOrEqual(e.pts.Length, 2);
                Assert.Less(D2.Distance(e.pts[0].XZ, G().nodes[e.nodeA].p.XZ), G().settings.clusterRadius + 1e-9);
                Assert.Less(D2.Distance(e.pts[e.pts.Length - 1].XZ, G().nodes[e.nodeB].p.XZ), G().settings.clusterRadius + 1e-9);
            }
        }

        [Test]
        public void RibbonsCoverCentrelines()
        {
            var asset = RoadNetworkBaker.LoadBaked();
            Assert.NotNull(asset, "no baked asset at " + RoadNetworkBaker.AssetPath);
            var fromAsset = RibbonCoverage.Measure(PlainRibbonMesh.Strips(asset), Input());
            TestContext.WriteLine("baked asset ribbons: " + fromAsset);
            Assert.IsTrue(fromAsset.Within(0.1), fromAsset.ToString());
            Assert.AreEqual(1722, fromAsset.canonPoints);

            var fromGraph = RibbonCoverage.Measure(PlainRibbons.Build(G()), Input());
            TestContext.WriteLine("double-precision ribbons: " + fromGraph);
            Assert.IsTrue(fromGraph.Within(0.1), fromGraph.ToString());

            var mesh = PlainRibbonMesh.ToMesh(PlainRibbonMesh.Strips(asset));
            Assert.AreEqual(asset.edges.Sum(e => e.points.Length) * 2, mesh.vertexCount);
            Assert.AreEqual(asset.edges.Sum(e => e.points.Length - 1) * 6, mesh.triangles.Length);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void EdgesKeepCanonPoints()
        {
            var g = G();
            foreach (var road in Input().roads)
            {
                var mine = g.edges.Where(e => e.sourceRoad == road.id).OrderBy(e => e.sStart).ToList();
                Assert.IsNotEmpty(mine, $"road {road.id} has no edges");
                Assert.AreEqual(0, mine[0].sStart);
                Assert.AreEqual(road.Length, mine[mine.Count - 1].sEnd);
                for (int k = 1; k < mine.Count; k++) Assert.AreEqual(mine[k - 1].sEnd, mine[k].sStart, $"road {road.id} edges do not tile");
                var all = new HashSet<(double, double, double)>(mine.SelectMany(e => e.pts).Select(p => (p.x, p.y, p.z)));
                foreach (var p in road.pts) Assert.IsTrue(all.Contains((p.x, p.y, p.z)), $"road {road.id} lost canon point {p}");
                foreach (var e in mine)
                    foreach (var p in e.pts)
                        Assert.Less(Polyline.Project(road.pts, road.d, p.XZ).distance, 1e-9, $"edge {e.id} point {p} is off its canon road");
            }
        }

        [Test]
        public void TownZoneSplit()
        {
            var g = G();
            var cc = g.nodes.Where(n => n.kind == NodeKind.ClassChange).ToList();
            Assert.AreEqual(new[] { 0, 1, 2, 3 }, cc.Select(n => g.Arm(n, 0).sourceRoad).OrderBy(r => r).ToArray(), "one class change on each of R0-R3");
            foreach (var n in cc)
            {
                var cls = n.arms.Select(a => g.arms[a].cls).OrderBy(c => c).ToArray();
                Assert.AreEqual(new[] { RoadClass.Highway, RoadClass.MainStreet }, cls, $"node {n.id}");
                Assert.AreEqual(g.Arm(n, 0).sourceRoad, g.Arm(n, 1).sourceRoad);
            }
            foreach (var e in g.edges)
            {
                if (e.sourceRoad >= 9) Assert.AreEqual(RoadClass.Street, e.cls, $"edge {e.id}");
                else if (e.sourceRoad >= 6) Assert.AreEqual(RoadClass.Dirt, e.cls, $"edge {e.id}");
                else if (e.sourceRoad >= 4) Assert.AreEqual(RoadClass.Highway, e.cls, $"edge {e.id} (R4/R5 never enter town)");
                else Assert.IsTrue(e.cls == RoadClass.Highway || e.cls == RoadClass.MainStreet);
            }
            // the junctions in town are on MainStreet
            foreach (var id in new[] { 0, 1, 2, 4, 5, 7, 9 })
                Assert.IsTrue(N(id).arms.Select(a => g.arms[a]).Where(a => a.sourceRoad <= 3).All(a => a.cls == RoadClass.MainStreet), $"node {id}");
            foreach (var road in Input().roads.Where(r => r.cls == RoadClass.Street))
                foreach (var p in road.pts) Assert.IsTrue(g.zone.Contains(p.XZ));
        }

        [Test]
        public void ComplexPairNeedsDecision()
        {
            var g = G();
            Assert.AreEqual(1, g.complexPairs.Count, string.Join("; ", g.notes));
            var p = g.complexPairs[0];
            Assert.AreEqual((0, 1), (p.a, p.b));
            Assert.AreEqual(13.8, p.distance, 0.1, "05 §0 item 1: the harbor road branches 13.8 m from the square");
            Assert.IsTrue((N(0).flags & NodeFlags.ComplexPair) != 0 && (N(1).flags & NodeFlags.ComplexPair) != 0);
        }

        [Test]
        public void ArmAnglesMatchResearch()
        {
            var g = G();
            // 05 §1.2: node 2 arm gaps 113/72/97/78 degrees; the smallest angle at any paved junction 71.9 degrees
            var n2 = N(2);
            var gaps = new List<double>();
            for (int i = 0; i < n2.Degree; i++)
                gaps.Add(Geo.WrapPositive(g.Arm(n2, (i + 1) % n2.Degree).angle - g.Arm(n2, i).angle) / Geo.Deg);
            var want = new[] { 72.0, 78.0, 97.0, 113.0 };
            var got = gaps.OrderBy(x => x).ToArray();
            for (int i = 0; i < 4; i++) Assert.AreEqual(want[i], got[i], 3.0, "node 2 gaps " + string.Join("/", gaps.Select(x => x.ToString("F1"))));
            double minGap = g.nodes.Where(n => n.kind == NodeKind.Junction).Min(n => n.minArmGap) / Geo.Deg;
            Assert.AreEqual(71.9, minGap, 2.0, "smallest arm gap at a paved junction");
            // node 0's arms as 05 quotes them in the JS frame: R0@-18, R1@127, R2@-127
            var n0 = N(0);
            var js = n0.arms.Select(a => g.arms[a]).ToDictionary(a => a.sourceRoad, a => RoadGraphReport.JsAngleDeg(a.angle));
            Assert.AreEqual(-18, js[0], 4.0);
            Assert.AreEqual(127, js[1], 4.0);
            Assert.AreEqual(-127, js[2], 4.0);
        }

        [Test]
        public void SteepApproaches()
        {
            var g = G();
            var steep = g.nodes.Where(n => n.kind == NodeKind.Junction && (n.flags & NodeFlags.SteepApproach) != 0).Select(n => n.id).ToArray();
            Assert.AreEqual(new[] { 3, 5, 17 }, steep, "05 §1.2: grades over 6 % near a junction only at nodes 3, 5 and 17");
            double Grade(int node, int road) => N(node).arms.Select(a => g.arms[a]).Where(a => a.sourceRoad == road).Max(a => Math.Abs(a.grade)) * 100;
            Assert.AreEqual(9.1, Grade(5, 18), 0.3, "node 5 R18");
            Assert.AreEqual(9.0, Grade(17, 16), 0.3, "node 17");
            Assert.AreEqual(6.5, Grade(3, 5), 0.5, "node 3");
        }

        [Test]
        public void KerbClearanceMatchesResearch()
        {
            var g = G();
            double MinClear(int node) => N(node).corners.Select(c => g.corners[c].clearance).Where(c => !double.IsNaN(c)).Min();
            // 05 §1.3 "closest building corner to the filleted kerb line" at r = 7.5 m
            Assert.AreEqual(1.22, MinClear(9), 0.05, "node 9");
            Assert.AreEqual(2.09, MinClear(7), 0.05, "node 7");
            Assert.AreEqual(3.34, MinClear(2), 0.05, "node 2");
            Assert.AreEqual(3.95, MinClear(4), 0.05, "node 4");
            Assert.AreEqual(3.28, MinClear(16), 0.05, "node 16");

            double[] Radii(int node) => N(node).corners.Select(c => g.corners[c]).Where(c => c.kind == CornerKind.Fillet).Select(c => c.radius).ToArray();
            // 05 §7: 6 m downtown at nodes 7 and 9; 7.5 m at the other street corners; 12 m in the fuel yard; 6 m inside L-bends
            foreach (var id in new[] { 7, 9 }) Assert.IsTrue(Radii(id).All(r => r == 6.0), $"node {id}: {string.Join("/", Radii(id))}");
            foreach (var id in new[] { 2, 4, 5, 16, 17, 19 }) Assert.IsTrue(Radii(id).All(r => r == 7.5), $"node {id}: {string.Join("/", Radii(id))}");
            Assert.IsTrue(Radii(3).All(r => r == 12.0), "node 3");
            foreach (var id in Bends) Assert.AreEqual(new[] { 6.0 }, Radii(id), $"bend {id}");
            foreach (var n in g.nodes.Where(q => q.kind == NodeKind.DirtApron))
                Assert.IsTrue(n.corners.Select(c => g.corners[c]).Where(c => c.kind == CornerKind.Fillet && c.opening < Math.PI / 2 + 0.01 && c.opening > Math.PI / 2 - 0.01).All(c => c.radius == 9.0));
        }

        [Test]
        public void BakeDeterministic()
        {
            var a = RoadNetworkBaker.BakeInMemory(out _, out _, out var m1);
            var b = RoadNetworkBaker.BakeInMemory(out _, out _, out var m2);
            Assert.NotNull(a, m1);
            Assert.NotNull(b, m2);
            Assert.AreEqual(a.contentHash, b.contentHash);
            Assert.AreEqual(a.contentHash, a.ComputeHash());
            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(b);
        }

        [Test]
        public void BakedAssetUpToDate()
        {
            var baked = RoadNetworkBaker.LoadBaked();
            Assert.NotNull(baked, "missing " + RoadNetworkBaker.AssetPath + ": run Backroads > Roads > Bake road network (R0)");
            Assert.AreEqual(baked.contentHash, baked.ComputeHash(), "the committed asset's content does not match its stored hash");
            var fresh = RoadNetworkBaker.BakeInMemory(out _, out _, out var msg);
            Assert.NotNull(fresh, msg);
            Assert.AreEqual(fresh.contentHash, baked.contentHash, "the committed road asset is stale: re-bake (Backroads > Roads > Bake road network (R0))");
            Assert.AreEqual(CanonImporter.LoadImported().manifestSha256, baked.sourceManifestSha256);
            Assert.AreEqual("CanonData/v1", baked.source);
            Assert.AreSame(baked, RoadNetworkAsset.Load(), "Resources.Load finds the committed asset");
            UnityEngine.Object.DestroyImmediate(fresh);
        }

        [Test]
        public void M1RibbonsComparison()
        {
            // lane W's M1 ribbons (roads.js style) next to R0's: the same centrelines, so the kerb lines agree closely
            var canon = CanonImporter.LoadImported();
            var m1 = Backroads.Client.Rendering.RoadRibbons.Build(canon.world, out int paved);
            Assert.AreEqual(21, paved);
            var v = m1.vertices;
            var strips = PlainRibbonMesh.Strips(RoadNetworkBaker.LoadBaked());
            var edgeVerts = strips.SelectMany(s => s.left.Concat(s.right)).Select(p => new Vector2((float)p.x, (float)p.z)).ToList();
            double worst = 0;
            foreach (var p in v)
            {
                var q = new Vector2(p.x, p.z);
                double best = edgeVerts.Min(e => (e - q).sqrMagnitude);
                worst = Math.Max(worst, Math.Sqrt(best));
            }
            TestContext.WriteLine($"M1 ribbons: {v.Length} vertices on {paved} paved roads; farthest from an R0 ribbon edge vertex: {worst:F3} m");
            Assert.Less(worst, 0.1);
            UnityEngine.Object.DestroyImmediate(m1);
        }

        [Test]
        public void DebugMapsWrite()
        {
            var paths = RoadGraphMap.WriteDefault(out var msg);
            Assert.AreEqual(2, paths.Count, msg);
            foreach (var p in paths)
            {
                Assert.IsTrue(File.Exists(p), p);
                Assert.Greater(new FileInfo(p).Length, 10000, p);
                TestContext.WriteLine("wrote " + p);
            }
            var report = RoadNetworkBaker.WriteReport(G(), Input(), RoadNetworkBaker.LoadBaked());
            TestContext.WriteLine("wrote " + report);
        }

        // ------------------------------------------------------------------ synthetic cases

        static D3[] Line(double x0, double z0, double x1, double z1, double step = 2)
        {
            int n = Math.Max(1, (int)Math.Round(Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / step));
            var p = new D3[n + 1];
            for (int i = 0; i <= n; i++) p[i] = new D3(x0 + (x1 - x0) * i / n, 0, z0 + (z1 - z0) * i / n);
            return p;
        }

        [Test]
        public void SyntheticCrossTeeAndBend()
        {
            var input = new RoadGraphInput();
            input.AddUnityRoad(0, RoadClass.Highway, 3.6, false, false, Line(-100, 0, 100, 0));     // through road
            input.AddUnityRoad(1, RoadClass.Street, 3.1, false, true, Line(0, -60, 0, -0.4));        // ends 0.4 m short: still a T split
            input.AddUnityRoad(2, RoadClass.Street, 3.1, true, true, Line(0.3, 0, 0.3, 60));         // starts 0.3 m off: same node -> X
            input.AddUnityRoad(3, RoadClass.Street, 3.1, true, false, Line(0.3, 60, 60, 60));        // L-bend at (0.3, 60)
            input.AddUnityRoad(4, RoadClass.Street, 3.1, true, false, Line(50, 1.5, 50, 40));        // 1.5 m off the highway: T at x = 50
            input.AddUnityRoad(5, RoadClass.Street, 3.1, true, false, Line(-98.8, 0.5, -98.8, 40));  // projects 1.2 m from the highway's end: no split
            var g = RoadGraphBuilder.Build(input, new RoadGraphSettings { zoneBuffer = 1000 }); // all in town: no class-change splits here
            TestContext.WriteLine(RoadGraphReport.NodeTable(g));

            var x = g.nodes.Single(n => n.Degree == 4);
            Assert.AreEqual(NodeKind.Junction, x.kind);
            // the two street ends project to x = 0 and x = 0.3 on the highway: one split at their mean, on its centreline
            Assert.Less(D2.Distance(x.p.XZ, new D2(0.15, 0)), 1e-9, "the X sits on the through road's centreline: " + x.p);
            Assert.AreEqual(new[] { 0, 0 }, new[] { g.Arm(x, x.majorA).sourceRoad, g.Arm(x, x.majorB).sourceRoad }, "class first: the highway");
            Assert.AreEqual(Control.MinorStop, x.control);

            var bend = g.nodes.Single(n => n.kind == NodeKind.Bend);
            Assert.Less(D2.Distance(bend.p.XZ, new D2(0.3, 60)), 1e-9);
            var bc = bend.corners.Select(c => g.corners[c]).OrderBy(c => c.kind).ToArray();
            Assert.AreEqual(CornerKind.Fillet, bc[0].kind);
            Assert.AreEqual(6.0, bc[0].radius);
            Assert.AreEqual(CornerKind.Miter, bc[1].kind);

            // road 4 starts 1.5 m from the highway: beyond the 1 m tolerance, so it stays a dead end
            Assert.IsFalse(g.nodes.Any(n => n.Degree >= 3 && Math.Abs(n.p.x - 50) < 1), "a road 1.5 m away must not split the highway");
            // road 5 lands 1.2 m from the highway's own end (< 2 m clearance): no split there either
            Assert.AreEqual(1, g.edges.Count(e => e.sourceRoad == 0 && e.sStart == 0), "highway starts once");
            Assert.AreEqual(2, g.edges.Count(e => e.sourceRoad == 0), "the highway is split once (at the X)");
            Assert.IsTrue(RoadGraphChecks.Connected(g, false, out _, out _) == false, "roads 4 and 5 are islands");
        }

        [Test]
        public void SyntheticClassBeatsStraightness()
        {
            // a straight street pair and a highway that bends 30 degrees at the junction: the highway is still major
            var input = new RoadGraphInput();
            double a = 30 * Geo.Deg;
            var hwyIn = Line(-80, 0, 0, 0);
            var hwyOut = Line(0, 0, 80 * Math.Cos(a), 80 * Math.Sin(a));
            input.AddUnityRoad(0, RoadClass.Highway, 3.6, false, false, hwyIn.Concat(hwyOut.Skip(1)).ToArray());
            input.AddUnityRoad(1, RoadClass.Street, 3.1, false, true, Line(-20, -60, 0, 0));
            input.AddUnityRoad(2, RoadClass.Street, 3.1, true, false, Line(0, 0, 20, 60));
            var g = RoadGraphBuilder.Build(input, new RoadGraphSettings { zoneBuffer = 1000 });
            var x = g.nodes.Single(n => n.Degree == 4);
            Assert.IsTrue(x.HasMajor);
            Assert.AreEqual(new[] { 0, 0 }, new[] { g.Arm(x, x.majorA).sourceRoad, g.Arm(x, x.majorB).sourceRoad });
            Assert.AreEqual(30, x.bestPairBend / Geo.Deg, 1.0);
            Assert.IsEmpty(RoadGraphChecks.MajorProblems(g));
        }
    }
}
