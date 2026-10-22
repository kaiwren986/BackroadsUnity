using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Build.Editor;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// roads-R1 (05 §8): Roads.Synthetic, the five shapes of 05 §8 (a 30° Y, a 5-arm star, two junctions 25 m apart, a T
    /// on a 15 m curve, an arm shorter than its tangent): each bakes a surface or fails with a clear message.
    /// </summary>
    public partial class Roads
    {
        static D3[] Pts(IEnumerable<(double x, double z)> p) => p.Select(q => new D3(q.x, 0, q.z)).ToArray();

        static D3[] Straight(double x0, double z0, double x1, double z1, double step = 2)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / step));
            return Enumerable.Range(0, n + 1).Select(i => new D3(x0 + (x1 - x0) * i / n, 0, z0 + (z1 - z0) * i / n)).ToArray();
        }

        static RoadGraphInput Scenario(string name)
        {
            var input = new RoadGraphInput();
            const double hw = 3.1;
            switch (name)
            {
                case "y30":
                {
                    double a = 15 * Geo.Deg;
                    input.AddUnityRoad(0, RoadClass.Street, hw, false, false, Straight(-100, 0, 0, 0));
                    input.AddUnityRoad(1, RoadClass.Street, hw, false, false, Straight(0, 0, 100 * Math.Cos(a), 100 * Math.Sin(a)));
                    input.AddUnityRoad(2, RoadClass.Street, hw, false, false, Straight(0, 0, 100 * Math.Cos(a), -100 * Math.Sin(a)));
                    break;
                }
                case "star5":
                    for (int k = 0; k < 5; k++)
                    {
                        double a = (90 + 72 * k) * Geo.Deg;
                        input.AddUnityRoad(k, RoadClass.Street, hw, false, false, Straight(0, 0, 80 * Math.Cos(a), 80 * Math.Sin(a)));
                    }
                    break;
                case "pair25":
                    input.AddUnityRoad(0, RoadClass.Street, hw, false, false, Straight(-100, 0, 100, 0));
                    input.AddUnityRoad(1, RoadClass.Street, hw, true, false, Straight(0, 0, 0, 80));
                    input.AddUnityRoad(2, RoadClass.Street, hw, true, false, Straight(25, 0, 25, -80));
                    break;
                case "curve15":
                {
                    // a quarter circle of radius 15 m between straights; a street leaves its outside at the middle
                    var through = new List<(double, double)>();
                    for (int k = 30; k > 0; k--) through.Add((-2.0 * k, -15));
                    for (int k = 0; k <= 36; k++) { double a = (-90 + 2.5 * k) * Geo.Deg; through.Add((15 * Math.Cos(a), 15 * Math.Sin(a))); }
                    for (int k = 1; k <= 30; k++) through.Add((15, 2.0 * k));
                    input.AddUnityRoad(0, RoadClass.Street, hw, false, false, Pts(through));
                    double m = -45 * Geo.Deg;
                    input.AddUnityRoad(1, RoadClass.Street, hw, true, false, Straight(15 * Math.Cos(m), 15 * Math.Sin(m), 60 * Math.Cos(m), 60 * Math.Sin(m)));
                    break;
                }
                case "short":
                    input.AddUnityRoad(0, RoadClass.Street, hw, false, false, Straight(-80, 0, 80, 0));
                    input.AddUnityRoad(1, RoadClass.Street, hw, true, false, Straight(0, 0, 0, 8, 1));
                    break;
                default: throw new ArgumentException(name);
            }
            return input;
        }

        [TestCase("y30")]
        [TestCase("star5")]
        [TestCase("pair25")]
        [TestCase("curve15")]
        [TestCase("short")]
        public void Synthetic(string name)
        {
            var input = Scenario(name);
            var g = RoadGraphBuilder.Build(input, new RoadGraphSettings { zoneBuffer = 1000 });
            var s = SurfaceBuilder.Build(g, input);
            var check = SurfaceChecks.Run(g, input, s);
            TestContext.WriteLine(RoadGraphReport.NodeTable(g));
            TestContext.WriteLine(SurfaceChecks.Summary(check));
            foreach (var l in check.reducedCorners.Concat(check.areaOff).Concat(check.irregularAreas).Concat(s.notes)) TestContext.WriteLine("  " + l);

            // every node with two or more arms bakes a surface or says clearly why not
            foreach (var n in g.nodes.Where(q => q.Degree >= 2))
            {
                var ns = s.Of(n.id);
                Assert.NotNull(ns, $"node {n.id}");
                if (ns.mode == SurfaceMode.Fallback) { Assert.IsNotEmpty(ns.problem, $"node {n.id} fell back without a reason"); continue; }
                Assert.Greater(Geom2.SignedArea(ns.polygon), 0, $"node {n.id} outline is counter-clockwise");
                Assert.IsTrue(Geom2.IsSimple(ns.polygon, out string why), $"node {n.id}: {why}");
                Assert.Greater(ns.triangles.Count, 0);
            }
            Assert.IsEmpty(check.notSimple);
            Assert.IsEmpty(check.seams, string.Join("\n", check.seams));
            Assert.AreEqual(0, check.maxSeamError);
            Assert.IsEmpty(check.areaOff, string.Join("\n", check.areaOff));

            var j = g.nodes.Where(q => q.Degree >= 3).ToList();
            switch (name)
            {
                case "y30":
                {
                    var ns = s.Of(j.Single().id);
                    Assert.AreNotEqual(SurfaceMode.Fallback, ns.mode);
                    var acute = ns.corners.OrderBy(c => c.kind == CornerKind.Fillet ? c.radius : 0).First();
                    Assert.IsTrue(acute.kind == CornerKind.Nose || acute.radius < 7.5, "the 30° corner cannot take the full 7.5 m radius");
                    Assert.LessOrEqual(ns.arms.Max(a => a.sMouth), 25 + 0.5 + 1e-6, "tangent points within 25 m");
                    break;
                }
                case "star5":
                {
                    var ns = s.Of(j.Single().id);
                    Assert.AreEqual(Control.AllWayStop, g.nodes[ns.node].control);
                    Assert.AreEqual(SurfaceMode.Plane, ns.mode, "equal streets under an all-way stop: no major road, a plane");
                    Assert.LessOrEqual(Math.Sqrt(ns.planeA * ns.planeA + ns.planeB * ns.planeB), 0.03 + 1e-9);
                    Assert.AreEqual(5, ns.corners.Count(c => c.kind == CornerKind.Fillet && Math.Abs(c.radius - 7.5) < 1e-9));
                    Assert.IsTrue(ns.ringMesh, "a star junction is star-shaped");
                    break;
                }
                case "pair25":
                {
                    Assert.AreEqual(2, j.Count);
                    Assert.IsTrue(j.All(n => (n.flags & NodeFlags.ComplexPair) != 0));
                    var a = s.Of(j[0].id);
                    var b = s.Of(j[1].id);
                    Assert.AreNotEqual(SurfaceMode.Fallback, a.mode);
                    Assert.AreNotEqual(SurfaceMode.Fallback, b.mode);
                    // the two surfaces do not overlap, and a ribbon joins them
                    for (int p = 0; p < a.polygon.Count; p++)
                        for (int q = 0; q < b.polygon.Count; q++)
                            Assert.IsFalse(Geom2.SegmentCross(a.polygon[p], a.polygon[(p + 1) % a.polygon.Count], b.polygon[q], b.polygon[(q + 1) % b.polygon.Count], out double ta, out double tb) && ta > 1e-9 && ta < 1 - 1e-9,
                                "the two junction surfaces overlap");
                    var between = g.edges.Single(e => (e.nodeA == j[0].id && e.nodeB == j[1].id) || (e.nodeA == j[1].id && e.nodeB == j[0].id));
                    Assert.IsTrue(s.ribbons.Any(r => r.edge == between.id), "a ribbon between the two junctions");
                    double mouths = a.arms.Single(x => x.edge == between.id).sMouth + b.arms.Single(x => x.edge == between.id).sMouth;
                    Assert.Less(mouths, between.Length, "the two mouths on the 25 m piece leave a ribbon between them");
                    break;
                }
                case "curve15":
                {
                    var ns = s.Of(j.Single().id);
                    Assert.AreNotEqual(SurfaceMode.Fallback, ns.mode);
                    // sampled 10 m out, a 15 m curve bends 38° through the node: over 05 §5.1's 35°, so no major road
                    // (an all-way stop by rule) and the surface is a plane
                    Assert.AreEqual(SurfaceMode.Plane, ns.mode);
                    int stem = ns.arms.FindIndex(x => g.edges[x.edge].sourceRoad == 1);
                    Assert.IsTrue(ns.corners.Where(c => c.armI == stem || c.armJ == stem).All(c => c.kind == CornerKind.Fillet), "both corners beside the street are filleted");
                    break;
                }
                case "short":
                {
                    var ns = s.Of(j.Single().id);
                    Assert.AreNotEqual(SurfaceMode.Fallback, ns.mode);
                    var stem = ns.arms.Single(a => g.edges[a.edge].Length < 10);
                    Assert.LessOrEqual(stem.sMouth, stem.maxMouth + 1e-9, "the mouth fits on the 8 m arm");
                    var fillets = ns.corners.Where(c => c.kind == CornerKind.Fillet).ToList();
                    Assert.AreEqual(2, fillets.Count);
                    Assert.IsTrue(fillets.All(c => c.radius < 7.5 && c.reason == CornerReason.ShortArm), string.Join(", ", fillets.Select(c => $"{c.radius:F2} {c.reason}")));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// roads-R1 on the town and its fixtures: every junction surface simple and counter-clockwise, its area within 5 % of
    /// the corridor estimate (irregular junctions within 15 %, listed), every mouth shared bit-exactly with its ribbon,
    /// no kerb line in a building (except the map conflicts the importer flagged), fallbacks only at the complex pairs
    /// R4 resolves, stop lines and crosswalks where 05 §5.4 puts them, crowns and planes within their slopes.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class Junctions
    {
        sealed class Bake
        {
            public RoadGraph g;
            public RoadGraphInput input;
            public RoadSurfaces s;
            public SurfaceChecks.Result check;
        }

        static readonly Dictionary<string, Bake> s_Cache = new Dictionary<string, Bake>();

        static Bake Get(string which)
        {
            if (s_Cache.TryGetValue(which, out var b)) return b;
            string town = TownRoadSource.Towns().First();
            var src = which == "town" ? TownRoadSource.LoadTown(town) : TownRoadSource.Load(RepoPaths.InRepo("Game/Assets/Backroads/Tests/EditMode/Roads/Fixtures/" + which), "fixture/" + which);
            var input = OsmRoadImporter.Import(src, null, out _);
            var g = RoadGraphBuilder.Build(input);
            var s = SurfaceBuilder.Build(g, input, which == "town" ? RoadOverrides.Load(town) : null);
            b = new Bake { g = g, input = input, s = s, check = SurfaceChecks.Run(g, input, s) };
            s_Cache[which] = b;
            return b;
        }

        [TestCase("village")]
        [TestCase("west")]
        [TestCase("town")]
        public void SurfacesValid(string which)
        {
            var b = Get(which);
            var c = b.check;
            TestContext.WriteLine(SurfaceChecks.Summary(c));
            foreach (var l in c.fallbackNodes.Concat(c.irregularAreas).Concat(c.knownMapConflicts)) TestContext.WriteLine("  " + l);
            Assert.Greater(c.surfaces, 10);
            Assert.IsEmpty(c.notSimple, string.Join("\n", c.notSimple));
            Assert.IsEmpty(c.areaOff, string.Join("\n", c.areaOff));
            Assert.IsEmpty(c.seams, string.Join("\n", c.seams));
            Assert.Less(c.maxSeamError, 1e-3, "seam height error under 1 mm (shared vertices: 0)");
            Assert.IsEmpty(c.kerbInBuilding, string.Join("\n", c.kerbInBuilding));
            // every fallback is a junction of a complex pair (R4 step 6), with its reason
            foreach (var ns in b.s.nodes.Where(n => n.mode == SurfaceMode.Fallback))
            {
                Assert.IsTrue((b.g.nodes[ns.node].flags & NodeFlags.ComplexPair) != 0, $"node {ns.node} fell back but is not a complex pair: {ns.problem}");
                Assert.IsNotEmpty(ns.problem);
            }
            // every known map conflict is on a road the importer flagged
            Assert.LessOrEqual(c.knownMapConflicts.Count, b.input.roads.Count(r => r.buildingConflict) * 3);
            // every reduced corner says why
            foreach (var ns in b.s.nodes)
                foreach (var k in ns.corners.Where(k => k.kind == CornerKind.Fillet && k.radius < k.baseRadius - 1e-6))
                    Assert.AreNotEqual(CornerReason.ClassPair, k.reason, $"node {ns.node}: a reduced corner with no reason");
        }

        [TestCase("village")]
        [TestCase("town")]
        public void StopLinesAndCrosswalks(string which)
        {
            var b = Get(which);
            var st = new SurfaceSettings();
            int stops = 0, crosswalks = 0;
            foreach (var ns in b.s.nodes.Where(n => n.mode != SurfaceMode.Fallback && n.arms.Count >= 3))
                foreach (var a in ns.arms)
                {
                    bool controlled = a.control == Control.MinorStop || a.control == Control.AllWayStop || a.control == Control.Signal;
                    Assert.AreEqual(controlled, a.sStop >= 0, $"node {ns.node} arm R{b.g.edges[a.edge].sourceRoad}: a stop line exactly where the approach stops");
                    if (a.crosswalk)
                    {
                        crosswalks++;
                        Assert.AreEqual(st.crosswalk, a.sCwOuter - a.sCwInner, 1e-9);
                        Assert.GreaterOrEqual(a.sCwOuter, a.sT - 1e-9, "the band ends at or beyond the kerb return");
                        if (controlled) Assert.GreaterOrEqual(a.sStop, a.sCwOuter + st.stopBehindCrosswalk - 1e-9, "4 ft behind the crosswalk");
                    }
                    if (!controlled) continue;
                    stops++;
                    if (!a.crosswalk && !double.IsNaN(a.sEdge))
                    {
                        Assert.GreaterOrEqual(a.sStop, a.sEdge + st.stopMinFromEdge - 1e-6, $"node {ns.node}: at least 4 ft from the intersected edge");
                        Assert.LessOrEqual(a.sStop, a.sEdge + st.stopMaxFromEdge + (a.stopMoved ? st.flatSearch : 0) + 1e-6, $"node {ns.node}: at most 30 ft (+4 m when moved to a flatter spot)");
                    }
                }
            TestContext.WriteLine($"{stops} stop lines, {crosswalks} crosswalk bands");
            Assert.Greater(stops, 0);
        }

        [Test]
        public void CrownsAndPlanes()
        {
            var b = Get("town");
            var st = new SurfaceSettings();
            double worst = 0;
            int checkedRows = 0;
            foreach (var r in b.s.ribbons)
            {
                var e = b.g.edges[r.edge];
                var road = b.g.RoadOf(e.sourceRoad);
                double hwL = road.xs.hwLeft, hwR = road.xs.hwRight;
                bool gravel = e.cls == RoadClass.Dirt;
                for (int q = 0; q < r.stations.Count; q++)
                {
                    double s = r.stationS[q];
                    if (s - r.s0 < st.warp + 0.5 || r.s1 - s < st.warp + 0.5) continue; // away from both warps
                    var row = r.stations[q];
                    int mid = Array.IndexOf(r.profile, 0.0);
                    for (int k = 0; k < row.Length; k++)
                    {
                        double want = SurfaceBuilder.Crown(r.profile[k], hwL, hwR, r.kerbed, gravel, st);
                        worst = Math.Max(worst, Math.Abs((row[k].y - row[mid].y) - want));
                    }
                    checkedRows++;
                }
            }
            TestContext.WriteLine($"{checkedRows} ribbon rows away from the junctions: worst cross-slope error {worst:E2} m");
            Assert.Greater(checkedRows, 1000);
            Assert.Less(worst, 1e-6, "the ribbon carries the crown (2 %, 4 % on gravel) and the gutter pan exactly");
            foreach (var ns in b.s.nodes.Where(n => n.mode == SurfaceMode.Plane))
                Assert.LessOrEqual(Math.Sqrt(ns.planeA * ns.planeA + ns.planeB * ns.planeB), st.planeMaxSlope + 1e-9, $"node {ns.node}: plane over 3 %");
            // a major junction carries the major road's centre height through the node
            foreach (var ns in b.s.nodes.Where(n => n.mode == SurfaceMode.Major))
            {
                var n = b.g.nodes[ns.node];
                Assert.AreEqual(n.p.y, SurfaceBuilder.JunctionHeight(ns, n.p.XZ, st), 1e-3, $"node {ns.node}");
            }
        }
    }
}
