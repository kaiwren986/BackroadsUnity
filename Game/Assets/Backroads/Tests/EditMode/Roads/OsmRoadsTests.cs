using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// roads-R0o (M3, lane R): the OSM road graph on the real ground. On two committed 600 x 600 m fixtures clipped from
    /// the town extract (tools/world/roads/make-fixture.mjs; ODbL credit beside them), on the whole town (TownData) and
    /// on small synthetic towns for the tags the town does not use: every drivable way is in the graph or skipped with a
    /// reason; junctions and dead ends match a table computed from the raw OSM topology; the graph is connected (groups
    /// apart from it reach the play-area edge); every mapped control lands on the approach it faces; heights follow the
    /// smoothed DEM; grades stay within 11 % (dirt 15 %) unless the real road is steeper; the edge rule holds; the bake
    /// is deterministic and the committed town asset is current.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class OsmRoads
    {
        sealed class Built
        {
            public OsmRoadSource src;
            public RoadGraphInput input;
            public OsmImportReport report;
            public RoadGraph g;
        }

        static readonly Dictionary<string, Built> s_Cache = new Dictionary<string, Built>();

        static string FixtureDir(string name) => RepoPaths.InRepo("Game/Assets/Backroads/Tests/EditMode/Roads/Fixtures/" + name);

        static string TownId()
        {
            var towns = TownRoadSource.Towns();
            Assert.IsNotEmpty(towns, "no TownData/<town>/v1");
            return towns[0];
        }

        /// <summary>"village" and "west" are fixtures; "town" is the first TownData town.</summary>
        static Built Get(string which)
        {
            if (s_Cache.TryGetValue(which, out var b)) return b;
            var src = which == "town" ? TownRoadSource.LoadTown(TownId()) : TownRoadSource.Load(FixtureDir(which), "fixture/" + which);
            var input = OsmRoadImporter.Import(src, null, out var report);
            b = new Built { src = src, input = input, report = report, g = RoadGraphBuilder.Build(input) };
            s_Cache[which] = b;
            return b;
        }

        // ------------------------------------------------------------------ the hand-checked tables
        // From the raw OSM topology by tools/world/roads/make-fixture.mjs --table (osm node, fixture x, z, road ends meeting
        // there), inside the fixture's inner square less 30 m; checked against the fixture maps by eye.

        static readonly (long osm, double x, double z, int arms)[] Village =
        {
            (198869517, 133.6, 12.2, 4), (198869518, 83.4, 13.7, 4), (198869519, 33.1, 16.4, 3), (198870588, -155.8, 127.2, 3),
            (198870591, -158.1, 61.3, 4), (198870593, -161.2, -7.8, 3), (198870595, 37.1, 124.9, 3), (198870597, 36.4, 55.0, 4),
            (198876515, -174.4, -88.7, 3), (198876525, -173.2, -7.6, 3), (198876548, 85.6, 53.0, 3), (198876552, 82.1, -16.2, 3),
            (198876567, 33.3, 20.8, 3), (198876569, -42.3, 22.9, 3), (198879157, -44.1, -10.8, 4), (198879164, 11.2, -47.1, 4),
            (198879166, 49.5, -62.5, 4), (198879167, 81.5, -78.3, 4), (198884998, 139.5, 122.4, 3), (198885004, -37.2, 123.5, 4),
            (198885008, -82.1, 124.9, 4), (198885010, -195.2, 128.4, 4), (198905706, 129.5, -21.7, 3), (198905708, 135.5, 51.0, 4),
            (198911974, 162.9, -31.1, 4), (198911977, 108.6, -18.2, 3), (198911979, 27.7, -13.9, 4), (198911981, -89.1, -9.3, 3),
            (198911983, -198.8, -7.0, 3), (198916819, 36.9, 95.3, 3), (198917255, 17.5, -134.2, 3), (198917257, -20.5, -117.5, 4),
            (198917265, -89.6, -92.3, 3), (198928873, 218.4, 42.0, 3), (198928881, -41.0, 57.5, 4), (198928885, -85.6, 58.7, 4),
            (198928887, -197.8, 62.1, 4), (198932076, -57.6, -198.4, 3), (198941064, -190.7, 193.0, 3), (198941069, -146.7, 191.9, 3),
            (198946637, -77.6, 188.6, 3), (198956116, -36.2, 186.5, 3), (10705548313, -125.0, 60.1, 3), (10705548314, -120.7, 126.1, 4),
            (10705548315, -117.9, 190.5, 3), (10705548316, 137.4, 91.1, 3), (13071715404, 188.3, -6.3, 3), (13071715408, 70.2, -15.7, 3),
            (13071715411, -94.1, -9.2, 3),
        };

        static readonly (long osm, double x, double z, int arms)[] West =
        {
            (198863814, 63.1, -73.9, 3), (198863816, 16.4, -73.1, 1), (198875205, 1.1, -98.5, 1), (198875207, 20.2, -128.0, 3),
            (198881836, 56.1, -177.4, 3), (198881849, 58.1, -111.6, 4), (198881869, 67.5, -41.4, 3), (198881885, 83.9, 206.0, 3),
            (198885025, 165.6, -43.0, 3), (198911991, 213.1, -180.3, 3), (198911993, 162.6, -179.0, 3), (198911995, -80.6, -173.3, 3),
            (198921910, 218.3, -111.4, 4), (198926606, 165.2, -110.5, 4), (3977767415, 78.4, 93.3, 3), (3977767416, -9.7, 110.9, 3),
            (3977767437, 122.8, 207.4, 3), (9448039297, -1.3, -175.8, 3), (11812039827, 56.2, -134.6, 3), (11812039828, -36.1, -174.3, 3),
        };

        [TestCase("village")]
        [TestCase("west")]
        public void NodesMatchTheTable(string fixture)
        {
            var b = Get(fixture);
            var table = fixture == "village" ? Village : West;
            double lim = b.src.playHalf - 50 - 30;
            var problems = new List<string>();
            // the table rounds positions to 0.1 m: a node within 0.08 m is the table's
            foreach (var row in table)
            {
                var n = b.g.nodes.OrderBy(q => D2.Distance(q.p.XZ, new D2(row.x, row.z))).First();
                double d = D2.Distance(n.p.XZ, new D2(row.x, row.z));
                if (d > 0.08) { problems.Add($"osm node {row.osm} ({row.x}, {row.z}): no graph node (nearest {d:F2} m)"); continue; }
                if (n.Degree != row.arms) problems.Add($"osm node {row.osm}: {n.Degree} arms, table {row.arms}");
                var want = row.arms == 1 ? NodeKind.DeadEnd : NodeKind.Junction;
                if (n.kind != want && !(want == NodeKind.Junction && n.kind == NodeKind.DirtApron)) problems.Add($"osm node {row.osm}: {n.kind}, table {want}");
            }
            // and no junction or dead end in the checked square that the table does not list
            foreach (var n in b.g.nodes)
            {
                if (Math.Abs(n.p.x) > lim - 0.5 || Math.Abs(n.p.z) > lim - 0.5) continue;
                if (n.Degree == 2) continue;
                if (!table.Any(r => D2.Distance(n.p.XZ, new D2(r.x, r.z)) <= 0.08)) problems.Add($"graph node {n.id} {n.kind} ({n.Degree} arms) at {n.p} is not in the table");
            }
            TestContext.WriteLine(RoadGraphReport.Summary(b.g));
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [TestCase("village")]
        [TestCase("west")]
        [TestCase("town")]
        public void EveryWayAccountedFor(string which)
        {
            var b = Get(which);
            var inRoads = new HashSet<string>(b.input.roads.SelectMany(r => r.osmWays));
            var missing = b.src.ways.Where(w => !inRoads.Contains(w.osm) && !b.report.skipped.Any(s => s.StartsWith(w.osm + " ", StringComparison.Ordinal))).Select(w => w.osm).ToList();
            TestContext.WriteLine($"{b.report.waysRead} ways: {b.report.waysUsed} in the graph; skipped: " + string.Join("; ", b.report.skippedByReason.Select(kv => $"{kv.Value} {kv.Key}")));
            Assert.IsEmpty(missing, "ways neither in the graph nor skipped with a reason: " + string.Join(" ", missing));
            Assert.AreEqual(b.report.waysRead, b.report.waysUsed + b.report.skipped.Count);
        }

        [TestCase("village")]
        [TestCase("west")]
        [TestCase("town")]
        public void Connected(string which)
        {
            var g = Get(which).g;
            var comp = Components(g);
            int main = comp.GroupBy(c => c).OrderByDescending(x => x.Count()).First().Key;
            var lonely = comp.Select((c, i) => (c, i)).Where(t => t.c != main).GroupBy(t => t.c)
                .Where(grp => !grp.Any(t => (g.nodes[t.i].flags & NodeFlags.PlayAreaEdge) != 0)).Select(grp => grp.First().i).ToList();
            Assert.IsEmpty(lonely, "groups apart from the network that do not reach the play-area edge: nodes " + string.Join(", ", lonely));
            Assert.IsEmpty(RoadGraphChecks.MajorProblems(g), string.Join("\n", RoadGraphChecks.MajorProblems(g)));
            Assert.IsEmpty(RoadGraphChecks.ControlProblems(g), string.Join("\n", RoadGraphChecks.ControlProblems(g)));
        }

        static int[] Components(RoadGraph g)
        {
            var c = Enumerable.Repeat(-1, g.nodes.Count).ToArray();
            var adj = g.nodes.Select(_ => new List<int>()).ToArray();
            foreach (var e in g.edges) { adj[e.nodeA].Add(e.nodeB); adj[e.nodeB].Add(e.nodeA); }
            int k = 0;
            for (int s = 0; s < c.Length; s++)
            {
                if (c[s] >= 0) continue;
                var st = new Stack<int>();
                st.Push(s);
                c[s] = k;
                while (st.Count > 0)
                    foreach (int v in adj[st.Pop()])
                        if (c[v] < 0) { c[v] = k; st.Push(v); }
                k++;
            }
            return c;
        }

        /// <summary>
        /// Every mapped stop sign with a direction controls the approach it faces: on its right arm, from the tag, and
        /// geometrically: the junction lies ahead of the sign in its direction of travel, within 60 m, and the arm points
        /// back towards the sign.
        /// </summary>
        [TestCase("village")]
        [TestCase("west")]
        [TestCase("town")]
        public void ControlsOnTheirApproach(string which)
        {
            var b = Get(which);
            var g = b.g;
            var problems = new List<string>();
            int placed = 0;
            foreach (var f in g.features.Where(f => f.kind != RoadFeatureKind.Crossing))
            {
                if (f.problem.Length > 0) { problems.Add($"osm node {f.osmNode}: {f.problem}"); continue; }
                var e = g.edges[f.edge];
                var sign = Polyline.At(e.pts, e.s, f.s).XZ;
                var tangent = (Polyline.At(e.pts, e.s, Math.Min(e.Length, f.s + 1)).XZ - Polyline.At(e.pts, e.s, Math.Max(0, f.s - 1)).XZ).Normalized;
                var node = g.nodes[f.node];
                var arm = g.arms[f.arm];
                var toNode = node.p.XZ - sign;
                if (toNode.Length > RoadGraphBuilder.SignReach + 1) problems.Add($"osm node {f.osmNode}: junction {node.id} is {toNode.Length:F1} m away");
                if (f.direction != 0 && toNode.Length > 0.5 && tangent.Dot(toNode) * f.direction <= 0) problems.Add($"osm node {f.osmNode}: junction {node.id} is behind the sign");
                if (toNode.Length > 0.5 && arm.dir.Dot(sign - node.p.XZ) <= 0) problems.Add($"osm node {f.osmNode}: arm R{arm.sourceRoad} at node {node.id} points away from the sign");
                if (arm.controlSource != ControlSource.Tag) problems.Add($"osm node {f.osmNode}: its arm's control comes from {arm.controlSource}");
                else placed++;
            }
            var tagArms = g.arms.Count(a => a.controlSource == ControlSource.Tag);
            TestContext.WriteLine($"{placed} mapped controls placed; {tagArms} approaches controlled by a mapped sign; {g.arms.Count(a => a.controlSource == ControlSource.Rule)} by rule");
            Assert.IsEmpty(problems, string.Join("\n", problems));
            Assert.AreEqual(tagArms, g.arms.Where(a => a.controlSource == ControlSource.Tag).Select(a => a.controlOsmNode).Distinct().Count(), "one mapped sign per approach");
        }

        /// <summary>
        /// Heights: an independent box smoothing (r 12 m, 4 passes) of the DEM along each edge agrees with the baked
        /// centreline within 0.05 m wherever the smoothing cannot see the edge's ends (50 m in) or a bridge deck; and no
        /// stretch is over the grade limit unless the real road is that steep.
        /// </summary>
        [TestCase("village")]
        [TestCase("west")]
        [TestCase("town")]
        public void HeightsFollowTheSmoothedDem(string which)
        {
            var b = Get(which);
            var bridges = b.input.roads.Where(r => r.bridge).SelectMany(r => r.pts.Select(p => p.XZ)).ToList();
            double worst = 0;
            int compared = 0;
            string where = "";
            foreach (var e in b.g.edges)
            {
                if (e.Length < 110) continue;
                var road = b.g.RoadOf(e.sourceRoad);
                if (road != null && road.bridge) continue;
                var raw = e.pts.Select(p => b.src.dem.Sample(p.x, p.z)).ToArray();
                var y = raw;
                for (int pass = 0; pass < 4; pass++) y = Box(y, e.s, 12);
                for (int i = 0; i < e.pts.Length; i++)
                {
                    if (e.s[i] < 50 || e.Length - e.s[i] < 50) continue;
                    var p = e.pts[i].XZ;
                    if (bridges.Any(q => D2.Distance(q, p) < 60)) continue;
                    double d = Math.Abs(e.pts[i].y - y[i]);
                    compared++;
                    if (d > worst) { worst = d; where = $"edge {e.id} (R{e.sourceRoad}) at {e.pts[i]}"; }
                }
            }
            TestContext.WriteLine($"{compared} samples compared; worst {worst:F4} m at {where}; DEM deviation mean {b.report.demMeanAbs:F3} m, p95 {b.report.demP95Abs:F3} m, max {b.report.demMaxAbs:F3} m; {b.report.steep.Count} steep stretches");
            Assert.Greater(compared, 20);
            Assert.LessOrEqual(worst, 0.05, "baked heights leave the smoothed DEM at " + where);
            Assert.AreEqual(0, b.report.gradeConflicts, "stretches over the grade limit and steeper than the real road");
            foreach (var s in b.report.steep) Assert.LessOrEqual(s.maxGrade, s.realGrade + 0.0101, $"steep stretch at {s.at}: more than a point steeper than the real road");
            // and every paved edge segment of 1 m or more is within the limit or inside a reported steep stretch
            foreach (var e in b.g.edges)
            {
                var road = b.g.RoadOf(e.sourceRoad);
                if (road != null && road.bridge) continue;
                double lim = e.cls == RoadClass.Dirt ? 0.15 : 0.11;
                for (int i = 1; i < e.pts.Length; i++)
                {
                    double ds = e.s[i] - e.s[i - 1];
                    if (ds < 1.0) continue;
                    double gr = Math.Abs(e.pts[i].y - e.pts[i - 1].y) / ds;
                    if (gr <= lim + 1e-4) continue;
                    double sr = e.sStart + 0.5 * (e.s[i - 1] + e.s[i]);
                    Assert.IsTrue(b.report.steep.Any(s => s.road == e.sourceRoad && sr >= s.s0 - 0.01 && sr <= s.s1 + 0.01),
                        $"edge {e.id} (R{e.sourceRoad}) is {gr * 100:F1} % at {e.pts[i]} with no steep stretch reported");
                }
            }
        }

        static double[] Box(double[] y, double[] s, double R)
        {
            int n = y.Length;
            var F = new double[n];
            for (int i = 1; i < n; i++) F[i] = F[i - 1] + 0.5 * (y[i] + y[i - 1]) * (s[i] - s[i - 1]);
            double I(double q)
            {
                if (q <= s[0]) return 0;
                if (q >= s[n - 1]) return F[n - 1];
                int k = 0;
                while (s[k + 1] < q) k++;
                double t = (q - s[k]) / (s[k + 1] - s[k]);
                double yq = y[k] + (y[k + 1] - y[k]) * t;
                return F[k] + 0.5 * (y[k] + yq) * (q - s[k]);
            }
            var o = new double[n];
            for (int i = 0; i < n; i++)
            {
                double r = Math.Min(R, Math.Min(s[i] - s[0], s[n - 1] - s[i]));
                o[i] = r < 1e-6 ? y[i] : (I(s[i] + r) - I(s[i] - r)) / (2 * r);
            }
            return o;
        }

        [Test]
        public void EdgeRule()
        {
            var b = Get("town");
            double H = b.src.playHalf - 50;
            foreach (var r in b.input.roads)
                foreach (var p in r.pts)
                    Assert.IsTrue(Math.Abs(p.x) <= H + 1e-6 && Math.Abs(p.z) <= H + 1e-6, $"R{r.id} reaches {p}, beyond {H} m");
            Assert.IsNotEmpty(b.report.edgeCuts, "the town's roads leave its play area");
            foreach (var c in b.report.edgeCuts)
            {
                Assert.GreaterOrEqual(c.road, 0, $"cut at {c.at} has no road");
                Assert.AreEqual(H, Math.Max(Math.Abs(c.at.x), Math.Abs(c.at.z)), 1e-6, $"cut at {c.at}");
                var n = b.g.nodes.OrderBy(q => D2.Distance(q.p.XZ, c.at)).First();
                Assert.Less(D2.Distance(n.p.XZ, c.at), 1e-6);
                Assert.AreEqual(NodeKind.DeadEnd, n.kind, $"the cut at {c.at} ends the road");
                Assert.IsTrue((n.flags & NodeFlags.PlayAreaEdge) != 0);
            }
            Assert.AreEqual(b.report.edgeCuts.Count, b.g.nodes.Count(n => (n.flags & NodeFlags.PlayAreaEdge) != 0));
            TestContext.WriteLine(string.Join("\n", b.report.edgeCuts.Select(c => $"{c.side}: R{c.road} {c.osmWay} at {c.at}, next node {c.junctionBeyond:F0} m beyond")));
        }

        [TestCase("village")]
        [TestCase("town")]
        public void BakeDeterministic(string which)
        {
            RoadNetworkAsset Bake()
            {
                var src = which == "town" ? TownRoadSource.LoadTown(TownId()) : TownRoadSource.Load(FixtureDir(which), "fixture/" + which);
                var input = OsmRoadImporter.Import(src, null, out var report);
                var a = ScriptableObject.CreateInstance<RoadNetworkAsset>();
                a.SetFrom(RoadGraphBuilder.Build(input), report);
                return a;
            }
            var x = Bake();
            var y = Bake();
            Assert.AreEqual(x.contentHash, y.contentHash);
            Assert.AreEqual(x.contentHash, x.ComputeHash());
            UnityEngine.Object.DestroyImmediate(x);
            UnityEngine.Object.DestroyImmediate(y);
        }

        [Test]
        public void TownAssetUpToDate()
        {
            foreach (var town in TownRoadSource.Towns())
            {
                var baked = RoadNetworkBaker.LoadBakedTown(town);
                Assert.NotNull(baked, "missing " + RoadNetworkBaker.TownAssetPath(town) + ": run Backroads > Roads > Bake town road networks (R0o)");
                Assert.AreEqual(baked.contentHash, baked.ComputeHash(), "the committed asset's content does not match its stored hash");
                var fresh = RoadNetworkBaker.BakeTownInMemory(town, out _, out _, out _);
                Assert.AreEqual(fresh.contentHash, baked.contentHash, $"the committed {town} road asset is stale: re-bake (Backroads > Roads > Bake town road networks (R0o))");
                Assert.AreEqual(2, baked.formatVersion);
                Assert.AreEqual("TownData/" + town + "/v1", baked.source);
                Assert.AreSame(baked, RoadNetworkAsset.LoadTown(town), "Resources.Load finds the committed asset");
                UnityEngine.Object.DestroyImmediate(fresh);
            }
        }

        [Test]
        public void TownMapsWrite()
        {
            var paths = RoadGraphMap.WriteTown(TownId(), out var msg);
            Assert.AreEqual(2, paths.Count, msg);
            foreach (var p in paths)
            {
                Assert.Greater(new FileInfo(p).Length, 10000, p);
                TestContext.WriteLine("wrote " + p);
            }
        }

        // ------------------------------------------------------------------ provisional kerb frontage (contract 2 stand-in)

        [Test]
        public void ProvisionalFrontage()
        {
            var asset = RoadNetworkBaker.LoadBakedTown(TownId());
            Assert.NotNull(asset);
            IKerbFrontageSource f = new ProvisionalKerbFrontage(asset);
            Assert.IsTrue(f.Provisional);
            Assert.AreEqual(asset.edges.Count(e => e.cls != RoadClass.Driveway) * 2, f.Faces.Count);
            Assert.AreEqual(f.Faces.Count, f.Faces.Select(x => x.faceId).Distinct().Count(), "face ids are unique");
            int kerbed = 0, offsetOk = 0, rightOk = 0, sampled = 0;
            foreach (var face in f.Faces)
            {
                int n = face.kerb.Length;
                Assert.AreEqual(n, face.s.Length); Assert.AreEqual(n, face.topOfKerb.Length); Assert.AreEqual(n, face.gutter.Length);
                Assert.AreEqual(n, face.walkBack.Length); Assert.AreEqual(n, face.walkBackY.Length);
                Assert.AreEqual(0f, face.s[0]);
                for (int i = 1; i < n; i++) Assert.GreaterOrEqual(face.s[i], face.s[i - 1]);
                Assert.IsTrue(face.provisional);
                Assert.AreSame(face, f.Face(face.faceId));
                foreach (var iv in face.forbidden) Assert.IsTrue(iv.s0 >= 0 && iv.s1 <= face.Length + 1e-3 && iv.s0 <= iv.s1);
                if (face.kerbKind == KerbKind.Barrier150) kerbed++;
                // the kerb lies the half-width from the centreline, and the street is on the face's right
                var e = asset.edges[face.faceId / 2];
                var road = asset.RoadById(e.sourceRoad);
                double hw = face.faceId % 2 == 0 ? road.hwLeft : road.hwRight;
                var centre = e.points.Select(p => new D3(p.x, p.y, p.z)).ToArray();
                var cs = Polyline.ArcLength(centre);
                int mid = n / 2;
                if (mid + 1 >= n) continue;
                sampled++;
                var k = new D2(face.kerb[mid].x, face.kerb[mid].y);
                var hit = Polyline.Project(centre, cs, k);
                if (Math.Abs(hit.distance - hw) <= 0.05) offsetOk++;
                var dir = new D2(face.kerb[mid + 1].x - face.kerb[mid].x, face.kerb[mid + 1].y - face.kerb[mid].y);
                if (dir.Cross(Polyline.At(centre, cs, hit.s).XZ - k) < 0) rightOk++;
            }
            TestContext.WriteLine($"{f.Faces.Count} provisional faces, {kerbed} kerbed; kerb at the half-width {offsetOk}/{sampled}; street on the right {rightOk}/{sampled}");
            Assert.Greater(kerbed, 0);
            Assert.GreaterOrEqual(offsetOk, 0.98 * sampled, "kerb lines sit the road's half-width from its centreline");
            Assert.AreEqual(sampled, rightOk, "every face has the street on its right");
        }

        // ------------------------------------------------------------------ synthetic towns: the tags the town does not use

        sealed class Synth
        {
            public readonly OsmRoadSource src = new OsmRoadSource { source = "synthetic", playHalf = 200 };
            long m_Way = 1;

            public Synth(Func<double, double, double> height)
            {
                const int n = 601;
                var h = new float[n * n];
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++) h[j * n + i] = (float)height(-300 + i, -300 + j);
                src.dem = new GridHeightField(h, n, n, -300, -300, 1);
            }

            public void Node(long id, double x, double z, params string[] tags) =>
                src.nodes[id] = new OsmRoadSource.Node { id = id, x = x, z = z, tags = Tags(tags) };

            public void Way(long[] nodes, params string[] tags)
            {
                long id = m_Way++;
                src.ways.Add(new OsmRoadSource.Way { id = id, osm = "way/" + id, nodes = nodes, tags = Tags(tags) });
            }

            static Dictionary<string, string> Tags(string[] kv)
            {
                var t = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var s in kv) { int i = s.IndexOf('='); t[s.Substring(0, i)] = s.Substring(i + 1); }
                return t;
            }

            public static D2[] Box(double x0, double z0, double x1, double z1) => new[] { new D2(x0, z0), new D2(x1, z0), new D2(x1, z1), new D2(x0, z1) };
        }

        /// <summary>
        /// A highway through a small town (kerb zone x -90..90, downtown -90..-30) with: an X where both side streets carry
        /// mapped stops (a width tag; lanes + parking tags), a T controlled by rule, an all-way stop (stop=all on one
        /// approach), a signal on the junction node, a give-way on a street opposite a gravel track, a mapped crossing, a
        /// footway, a mapped driveway, and both highway ends beyond the play area (half 200, so cut at 150).
        /// </summary>
        static (RoadGraphInput input, OsmImportReport report, RoadGraph g) Junctions()
        {
            var s = new Synth((x, z) => 10 + 0.02 * x);
            s.Node(1, -180, 0); s.Node(2, -120, 0, "highway=traffic_signals"); s.Node(3, -60, 0); s.Node(4, -10, 0, "highway=crossing", "crossing=marked");
            s.Node(5, 0, 0); s.Node(6, 60, 0); s.Node(7, 180, 0);
            s.Way(new long[] { 1, 2, 3, 4, 5, 6, 7 }, "highway=secondary");
            s.Node(10, 0, 12, "highway=stop", "direction=backward"); s.Node(11, 0, 40); s.Node(12, 0, 90);
            s.Way(new long[] { 5, 10, 11, 12 }, "highway=residential", "width=8");
            s.Node(20, 0, -90); s.Node(21, 0, -12, "highway=stop", "direction=forward");
            s.Way(new long[] { 20, 21, 5 }, "highway=residential", "lanes=3", "parking:both=lane");
            s.Node(30, 60, 60); s.Node(31, 60, 100);
            s.Way(new long[] { 6, 30, 31 }, "highway=residential");
            s.Node(40, 20, 60); s.Node(41, 40, 60, "highway=stop", "stop=all", "direction=forward"); s.Node(42, 100, 60);
            s.Way(new long[] { 40, 41, 30, 42 }, "highway=residential");
            s.Node(50, -60, 8, "highway=give_way", "direction=backward"); s.Node(51, -60, 60);
            s.Way(new long[] { 3, 50, 51 }, "highway=residential");
            s.Node(60, -60, -70);
            s.Way(new long[] { 3, 60 }, "highway=track", "surface=gravel");
            s.Node(70, -120, 50);
            s.Way(new long[] { 2, 70 }, "highway=residential");
            s.Node(80, -30, -30); s.Node(81, -30, -5);
            s.Way(new long[] { 80, 81 }, "highway=footway");
            s.Node(90, 20, 95);
            s.Way(new long[] { 12, 90 }, "highway=service", "service=driveway");
            s.src.kerbZone = Synth.Box(-90, -120, 90, 120);
            s.src.downtownZone = Synth.Box(-90, -20, -30, 20);
            var input = OsmRoadImporter.Import(s.src, null, out var report);
            return (input, report, RoadGraphBuilder.Build(input));
        }

        static GraphNode At(RoadGraph g, double x, double z) => g.nodes.Single(n => D2.Distance(n.p.XZ, new D2(x, z)) < 1e-6);

        static GraphArm ArmTowards(RoadGraph g, GraphNode n, double x, double z)
        {
            var d = (new D2(x, z) - n.p.XZ).Normalized;
            return n.arms.Select(a => g.arms[a]).OrderByDescending(a => a.dir.Dot(d)).First();
        }

        [Test]
        public void SyntheticCrossSectionsFromTags()
        {
            var (input, report, g) = Junctions();
            TestContext.WriteLine(RoadGraphReport.NodeTable(g));
            RoadGraphInput.Road RoadAt(double x, double z) => input.roads.Single(r =>
            {
                var hit = Polyline.Project(r.pts, r.d, new D2(x, z));
                return hit.distance < 1e-6 && hit.s > 0.5 && hit.s < r.Length - 0.5;
            });
            var w = RoadAt(0, 60);
            Assert.AreEqual(8.0, w.xs.Width, 1e-9); Assert.AreEqual("width", w.xs.source);
            var l = RoadAt(0, -50);
            Assert.AreEqual(3, l.xs.lanes); Assert.AreEqual("lanes", l.xs.source);
            Assert.AreEqual(3 * 3.3 + 2 * 2.4, l.xs.Width, 1e-9);
            Assert.AreEqual(2.4, l.xs.parkingLeft, 1e-9); Assert.AreEqual(2.4, l.xs.parkingRight, 1e-9);
            var down = RoadAt(-50, 0);
            Assert.AreEqual(RoadClass.MainStreet, down.cls); Assert.IsTrue(down.inDowntown);
            Assert.AreEqual(2 * 3.3 + 2 * 2.4 + 2 * 0.6, down.xs.Width, 1e-9, "downtown MainStreet: parking both sides by default");
            var main = RoadAt(30, 0);
            Assert.AreEqual(RoadClass.MainStreet, main.cls); Assert.IsFalse(main.inDowntown);
            Assert.AreEqual(2 * 3.3 + 2 * 0.6, main.xs.Width, 1e-9);
            var hwy = RoadAt(120, 0);
            Assert.AreEqual(RoadClass.Highway, hwy.cls); Assert.AreEqual(7.2, hwy.xs.Width, 1e-9); Assert.AreEqual(88.5, hwy.speedKmh, 1e-9);
            var rural = RoadAt(-120, 30);
            Assert.AreEqual(RoadClass.Street, rural.cls); Assert.IsFalse(rural.inKerbZone); Assert.AreEqual(6.2, rural.xs.Width, 1e-9);
            var town = RoadAt(60, 80);
            Assert.IsTrue(town.inKerbZone); Assert.AreEqual(9.0, town.xs.Width, 1e-9);
            Assert.AreEqual(RoadClass.Dirt, RoadAt(-60, -40).cls, "a gravel track is Dirt");
            // the class changes at the kerb zone and the join at the downtown edge
            Assert.AreEqual(NodeKind.ClassChange, At(g, -90, 0).kind);
            Assert.AreEqual(NodeKind.ClassChange, At(g, 90, 0).kind);
            Assert.AreEqual(NodeKind.Join, At(g, -30, 0).kind);
            // skipped and mapped
            Assert.IsTrue(report.skipped.Any(x => x.StartsWith("way/9 highway=footway", StringComparison.Ordinal)), string.Join("\n", report.skipped));
            Assert.AreEqual(1, report.driveways.Count);
            Assert.AreEqual(12, report.driveways[0].node);
            // the highway leaves the play area on both sides: cut 50 m inside the edge
            Assert.AreEqual(2, report.edgeCuts.Count);
            Assert.IsTrue((At(g, -150, 0).flags & NodeFlags.PlayAreaEdge) != 0);
            Assert.IsTrue((At(g, 150, 0).flags & NodeFlags.PlayAreaEdge) != 0);
        }

        [Test]
        public void SyntheticControlsFromTags()
        {
            var (_, _, g) = Junctions();
            // the X: both side streets stop from their mapped signs; the highway drives through
            var x = At(g, 0, 0);
            Assert.AreEqual(Control.MinorStop, x.control); Assert.AreEqual(ControlSource.Tag, x.controlSource);
            Assert.AreEqual(ControlSource.Tag, ArmTowards(g, x, 0, 50).controlSource);
            Assert.AreEqual(10, ArmTowards(g, x, 0, 50).controlOsmNode);
            Assert.AreEqual(21, ArmTowards(g, x, 0, -50).controlOsmNode);
            Assert.AreEqual(Control.None, ArmTowards(g, x, 50, 0).control);
            Assert.AreEqual(Control.None, ArmTowards(g, x, -50, 0).control);
            // the T: no sign, so the street stops by rule
            var t = At(g, 60, 0);
            Assert.AreEqual(ControlSource.Rule, t.controlSource);
            Assert.AreEqual(Control.MinorStop, ArmTowards(g, t, 60, 50).control);
            // stop=all on one approach: an all-way stop
            var a = At(g, 60, 60);
            Assert.AreEqual(Control.AllWayStop, a.control);
            Assert.IsTrue(a.arms.All(id => g.arms[id].control == Control.AllWayStop));
            Assert.AreEqual(ControlSource.Tag, ArmTowards(g, a, 20, 60).controlSource);
            Assert.IsFalse(a.HasMajor);
            // a signal mapped on the junction node controls every approach
            var sig = At(g, -120, 0);
            Assert.AreEqual(Control.Signal, sig.control);
            Assert.IsTrue(sig.arms.All(id => g.arms[id].control == Control.Signal));
            // give-way on the street, opposite a gravel track that stops by rule; the highway drives through
            var y = At(g, -60, 0);
            Assert.AreEqual(NodeKind.DirtApron, y.kind);
            Assert.AreEqual(Control.Yield, ArmTowards(g, y, -60, 50).control);
            Assert.AreEqual(ControlSource.Tag, ArmTowards(g, y, -60, 50).controlSource);
            Assert.AreEqual(Control.MinorStop, ArmTowards(g, y, -60, -50).control);
            Assert.AreEqual(ControlSource.Rule, ArmTowards(g, y, -60, -50).controlSource);
            Assert.AreEqual(Control.None, ArmTowards(g, y, -100, 0).control);
            // the crossing belongs to the X's west approach
            var cr = g.features.Single(f => f.kind == RoadFeatureKind.Crossing);
            Assert.AreEqual(x.id, cr.node);
            Assert.AreEqual("marked", cr.crossing);
            Assert.IsEmpty(RoadGraphChecks.ControlProblems(g), string.Join("\n", RoadGraphChecks.ControlProblems(g)));
        }

        [Test]
        public void SyntheticBridgeDeckIsStraight()
        {
            // a river 60 m wide and 18 m deep between banks; the road crosses on a 100 m bridge from bank top to bank top
            var s = new Synth((x, z) => Math.Abs(x) >= 50 ? 20 : Math.Abs(x) <= 30 ? 2 : 2 + 18 * (Math.Abs(x) - 30) / 20);
            s.Node(1, -180, 0); s.Node(2, -50, 0); s.Node(3, 50, 0); s.Node(4, 180, 0);
            s.Way(new long[] { 1, 2 }, "highway=secondary");
            s.Way(new long[] { 2, 3 }, "highway=secondary", "bridge=yes", "layer=1");
            s.Way(new long[] { 3, 4 }, "highway=secondary");
            s.src.playHalf = 300;
            var input = OsmRoadImporter.Import(s.src, null, out _);
            var g = RoadGraphBuilder.Build(input);
            var deck = input.roads.Single(r => r.bridge);
            Assert.AreEqual(100, deck.Length, 1e-6);
            double y0 = deck.pts[0].y, y1 = deck.pts[deck.pts.Length - 1].y;
            for (int i = 0; i < deck.pts.Length; i++)
                Assert.AreEqual(y0 + (y1 - y0) * deck.d[i] / deck.Length, deck.pts[i].y, 1e-6, $"deck point {i}");
            Assert.Greater(Math.Min(y0, y1), 17, "the deck spans the river at bank height, not down at the water");
            Assert.AreEqual(2, g.CountKind(NodeKind.Join), "the deck ends are joins (tags change)");
            // the approaches meet the deck flush
            foreach (var r in input.roads.Where(r => !r.bridge))
            {
                var end = r.pts.OrderBy(p => Math.Abs(Math.Abs(p.x) - 50)).First();
                Assert.AreEqual(end.x < 0 ? y0 : y1, end.y, 1e-6);
            }
        }
    }
}
