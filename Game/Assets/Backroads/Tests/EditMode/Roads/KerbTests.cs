using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// roads-R2 on the town and its fixtures (the card's done-when list): every block face has a kerb loop or an
    /// unkerbed edge; kerb-to-building clearance at least the walk width (1.0 m downtown) or reported; ramps on every
    /// kerbed corner within PROWAG's slopes; forbidden intervals where the card puts them; the frontage asset current,
    /// deterministic and faithful to the bake.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class Kerbs
    {
        internal sealed class Bake
        {
            public KerbBaker.Bake k;
            public List<Plate> plates;
            public RoadGraph g => k.roads.graph;
            public RoadSurfaces s => k.roads.surfaces;
            public KerbNetwork kn => k.kerbs;
        }

        static readonly Dictionary<string, Bake> s_Cache = new Dictionary<string, Bake>();

        internal static Bake Get(string which)
        {
            if (s_Cache.TryGetValue(which, out var b)) return b;
            string town = TownRoadSource.Towns().First();
            var src = which == "town" ? TownRoadSource.LoadTown(town) : TownRoadSource.Load(RepoPaths.InRepo("Game/Assets/Backroads/Tests/EditMode/Roads/Fixtures/" + which), "fixture/" + which);
            var input = OsmRoadImporter.Import(src, null, out var report);
            var g = RoadGraphBuilder.Build(input);
            var s = SurfaceBuilder.Build(g, input, which == "town" ? RoadOverrides.Load(town) : null);
            var roads = new SurfaceBaker.Bake { town = which == "town" ? town : "fixture-" + which, graph = g, input = input, import = report, surfaces = s, source = src, checks = SurfaceChecks.Run(g, input, s) };
            var k = KerbBaker.FromRoads(roads);
            b = new Bake { k = k, plates = RoadPlateBuilder.Build(g, s, k.kerbs, src.dem, k.settings) };
            s_Cache[which] = b;
            return b;
        }

        [TestCase("village")]
        [TestCase("west")]
        [TestCase("town")]
        public void LoopsAndFaces(string which)
        {
            var b = Get(which);
            var kn = b.kn;
            TestContext.WriteLine(KerbBaker.Summary(b.k));
            foreach (var l in kn.unfaced) TestContext.WriteLine("  " + l);
            Assert.Greater(kn.loops.Count, 0);
            Assert.Greater(kn.faces.Count, 10);
            // every road side (off bridges) has a face: a kerb loop where it is kerbed, an unkerbed edge elsewhere
            Assert.IsEmpty(kn.unfaced, string.Join("\n", kn.unfaced));
            foreach (var loop in kn.loops)
                for (int k = 0; k < loop.Count; k++)
                    Assert.IsTrue(loop.owner[k] == KerbOwner.Side || loop.owner[k] == KerbOwner.Cap || loop.owner[k] == KerbOwner.Edge,
                        $"loop {loop.id} segment {k} at {loop.pts[k]} borders {loop.owner[k]}: every kerb runs along a road side, a dead-end cap or the play-area edge");
            for (int i = 0; i < kn.faces.Count; i++)
            {
                var f = kn.faces[i];
                Assert.AreEqual(i, f.id, "faces are numbered in order");
                int n = f.Count;
                Assert.GreaterOrEqual(n, 2);
                foreach (var list in new[] { f.s, f.gutter, f.top, f.width, f.backY }) Assert.AreEqual(n, list.Count);
                Assert.AreEqual(n, f.back.Count);
                Assert.AreEqual(0, f.s[0]);
                for (int q = 1; q < n; q++) Assert.Greater(f.s[q], f.s[q - 1], $"face {f.id}: s increases");
                Assert.AreEqual(f.kerbed ? KerbKind.Barrier150 : KerbKind.None, f.kind);
                if (!f.kerbed) Assert.IsFalse(f.walk, "no walk without a kerb");
                for (int q = 0; q < n; q++)
                {
                    Assert.GreaterOrEqual(f.width[q], 0);
                    Assert.GreaterOrEqual(f.top[q], f.gutter[q] - 1e-9, "the kerb top is never below the gutter");
                    if (!f.kerbed) Assert.AreEqual(f.gutter[q], f.top[q], "unkerbed: top = gutter");
                }
                // the block is on the face's left: the walk back edge lies left of the kerb line
                if (f.walk)
                    for (int q = 0; q + 1 < n; q++)
                    {
                        var d = f.kerb[q + 1] - f.kerb[q];
                        if (f.width[q] > 0.05) Assert.Greater(d.Cross(f.back[q] - f.kerb[q]), -1e-6, $"face {f.id} station {q}: the walk lies on the block side");
                    }
            }
            // the kerb loops follow the R1 asphalt: every kerb point lies on a ribbon edge or a junction outline (within
            // the boolean's 0.1 mm grid), and a face's middle lies near its own road's pavement edge
            var edges = new List<(D2 a, D2 b)>();
            foreach (var r in b.s.ribbons)
            {
                int P = r.profile.Length;
                for (int q = 0; q + 1 < r.stations.Count; q++)
                {
                    edges.Add((r.stations[q][0].XZ, r.stations[q + 1][0].XZ));
                    edges.Add((r.stations[q][P - 1].XZ, r.stations[q + 1][P - 1].XZ));
                }
                edges.Add((r.stations[0][0].XZ, r.stations[0][P - 1].XZ));
                edges.Add((r.stations[r.stations.Count - 1][0].XZ, r.stations[r.stations.Count - 1][P - 1].XZ));
            }
            foreach (var ns in b.s.nodes)
                for (int k = 0; k < ns.polygon.Count; k++) edges.Add((ns.polygon[k], ns.polygon[(k + 1) % ns.polygon.Count]));
            var frames = new RoadFrame[b.g.edges.Count];
            foreach (var e in b.g.edges) frames[e.id] = new RoadFrame(e.pts);
            int checkedPts = 0;
            foreach (var f in kn.faces)
            {
                var p = f.kerb[f.Count / 2];
                double onAsphalt = edges.Min(e => Polyline.SegmentDistance(e.a, e.b, p));
                var fr = frames[f.edge];
                fr.ToFrame(p, out double s, out _);
                if (onAsphalt > 0.002) Assert.Fail($"face {f.id} (edge {f.edge}): kerb point {p} is {onAsphalt:F4} m off the R1 asphalt outline");
                if (s < 15 || s > fr.Length - 15) continue;
                var road = b.g.RoadOf(b.g.edges[f.edge].sourceRoad);
                fr.Project(p, f.side == 0 ? road.xs.hwLeft : -road.xs.hwRight, out double off);
                Assert.Less(off, 0.1, $"face {f.id} (edge {f.edge}): the kerb is its own road's pavement edge");
                checkedPts++;
            }
            Assert.Greater(checkedPts, kn.faces.Count / 4);
        }

        [TestCase("village")]
        [TestCase("town")]
        public void ClearanceReported(string which)
        {
            var b = Get(which);
            var st = b.k.settings;
            var reported = new HashSet<int>();
            foreach (var l in b.kn.clearance)
            {
                TestContext.WriteLine("  " + l);
                reported.Add(int.Parse(l.Split(' ')[1]));
            }
            // every walk face whose kerb comes closer to a building than the walk needs is in the report, and only those
            foreach (var f in b.kn.faces.Where(f => f.walk))
            {
                if (double.IsNaN(f.minClearance)) { Assert.IsFalse(reported.Contains(f.id)); continue; }
                bool downtownOnly = b.g.downtown != null && f.kerb.All(p => TownZone.InPolygon(b.g.downtown, p));
                double need = downtownOnly ? st.downtownMinClear : KerbRules.WalkWidth((RoadClass)(byte)f.cls, st);
                if (f.minClearance < Math.Min(need, st.downtownMinClear) - 1e-6) Assert.IsTrue(reported.Contains(f.id), $"face {f.id}: kerb {f.minClearance:F2} m from a building, not reported");
            }
            // and the walk never runs into a building: where a building is closer than the walk, the walk stops at it
            int stations = 0;
            foreach (var f in b.kn.faces.Where(f => f.walk))
                for (int q = 0; q < f.Count; q++, stations++)
                    Assert.LessOrEqual(f.width[q], st.kerbTop + Math.Max(st.downtownFill, st.walkMain) + 1e-6);
            TestContext.WriteLine($"{b.kn.clearance.Count} faces reported; {stations} walk stations");
        }

        [TestCase("village")]
        [TestCase("town")]
        public void RampsOnEveryKerbedCorner(string which)
        {
            var b = Get(which);
            var kn = b.kn;
            var st = b.k.settings;
            int kerbed = 0;
            Assert.LessOrEqual((st.barrier - st.dropped) / st.rampRun, 0.075 + 1e-9, "ramp runs at most 7.5 % (PROWAG 8.33 %)");
            foreach (var c in kn.corners)
            {
                Assert.Greater(c.ArcLength, 0);
                if (!c.kerbed) { Assert.AreEqual(RampKind.None, c.ramp); continue; }
                kerbed++;
                Assert.AreNotEqual(RampKind.None, c.ramp, $"corner {c.id} at node {c.node}: a kerbed corner without a ramp");
                Assert.GreaterOrEqual(c.dep1 - c.dep0, st.landing - 1e-9, $"corner {c.id}: the lowered stretch holds a 1.2 m landing");
                // the kerb is down at the lowered stretch on every kerbed face it touches
                double probe = c.ramp == RampKind.Depressed ? 0.5 * (c.dep0 + c.dep1) : c.open0;
                var loop = kn.loops[c.loop];
                bool found = false;
                foreach (var f in kn.faces.Where(f => f.loop == c.loop && f.kerbed && f.corners.Contains(c.id)))
                {
                    double t = probe;
                    while (t < f.t0 - 1e-9) t += loop.Length;
                    while (t > f.t1 + 1e-9) t -= loop.Length;
                    if (t < f.t0 || t > f.t1) continue;
                    found = true;
                    Assert.AreEqual(st.dropped, KerbBuilder.KerbHeight(kn, f, t, st), 1e-9, $"corner {c.id}: Dropped25 at the lowered stretch");
                }
                Assert.IsTrue(found, $"corner {c.id} (node {c.node}, loop {c.loop} of {loop.Length:F1} m): no kerbed face holds its lowered stretch {c.dep0:F1}-{c.dep1:F1} (arc {c.t0:F1}-{c.t1:F1}); faces "
                    + string.Join("; ", kn.faces.Where(f => f.loop == c.loop && (f.corners.Contains(c.id) || f.id == c.faceA || f.id == c.faceB))
                        .Select(f => $"{f.id} e{f.edge}/{f.side} {f.cls} kerbed {f.kerbed} walk {f.walk} {f.t0:F1}-{f.t1:F1}")));
            }
            Assert.Greater(kerbed, 0);
            // the walk's cross fall stays within 2 % everywhere (landings included)
            foreach (var f in kn.faces.Where(f => f.walk))
                for (int q = 0; q < f.Count; q++)
                    if (f.width[q] > st.kerbTop + 0.1)
                        Assert.LessOrEqual((f.backY[q] - f.top[q]) / (f.width[q] - st.kerbTop), 0.02 + 1e-9);
            TestContext.WriteLine($"{kerbed} kerbed corners: " + string.Join(", ", kn.corners.Where(c => c.kerbed).GroupBy(c => c.ramp).Select(x => $"{x.Key} {x.Count()}")));
        }

        [TestCase("village")]
        [TestCase("town")]
        public void ForbiddenIntervals(string which)
        {
            var b = Get(which);
            var kn = b.kn;
            var st = b.k.settings;
            foreach (var f in kn.faces)
            {
                foreach (var iv in f.forbidden) Assert.IsTrue(iv.s0 >= 0 && iv.s1 <= f.Length + 1e-3 && iv.s0 < iv.s1, $"face {f.id}: interval {iv.s0}-{iv.s1} on a {f.Length:F1} m face");
                if (f.bridge) Assert.IsTrue(f.forbidden.Any(i => i.reason == FrontageBlock.Other && i.s0 <= 1e-3 && i.s1 >= f.Length - 1e-3), "a bridge takes no driveway");
            }
            // corner returns: from the arc's tangent back by 3 m (local) or 9 m (MainStreet, Highway), on both faces
            foreach (var c in kn.corners)
            {
                var loop = kn.loops[c.loop];
                foreach (var (fid, atEnd) in new[] { (c.faceA, true), (c.faceB, false) })
                {
                    var f = kn.Face(fid);
                    if (f == null) continue;
                    double buf = f.cls == StreetClass.MainStreet || f.cls == StreetClass.Highway ? st.cornerReturnMajor : st.cornerReturnLocal;
                    double tangent = atEnd ? c.t0 : c.t1;
                    while (tangent < f.t0 - 1e-6) tangent += loop.Length;
                    while (tangent > f.t1 + 1e-6) tangent -= loop.Length;
                    double s = tangent - f.t0;
                    double want0 = atEnd ? Math.Max(0, s - buf) : Math.Max(0, Math.Min(f.Length, s)), want1 = atEnd ? Math.Min(f.Length, s) : Math.Min(f.Length, s + buf);
                    if (want1 - want0 < 0.05) continue;
                    Assert.IsTrue(f.forbidden.Any(i => i.reason == FrontageBlock.CornerReturn && i.s0 <= want0 + 1e-3 && i.s1 >= want1 - 1e-3),
                        $"corner {c.id} (node {c.node}): face {f.id} has no corner return over {want0:F1}-{want1:F1} m");
                }
            }
            // stop lines: the approach's kerb on the side of the stopping traffic carries the stop-line zone
            int stops = 0;
            foreach (var ns in b.s.nodes.Where(n => n.mode != SurfaceMode.Fallback))
                foreach (var a in ns.arms.Where(a => a.sStop >= 0))
                {
                    int side = a.atA ? 0 : 1;
                    var faces = kn.faces.Where(f => f.edge == a.edge && f.side == side).ToList();
                    var p = a.frame.P(a.sStop, a.hwL);
                    var on = faces.Where(f => { KerbBuilder.Project(f, p, out double d); return d < 0.5; }).ToList();
                    if (on.Count == 0) continue;
                    stops++;
                    Assert.IsTrue(on.Any(f => f.forbidden.Any(i => i.reason == FrontageBlock.StopLine)), $"node {ns.node}: no stop-line zone on the approach kerb");
                }
            Assert.Greater(stops, 0);
            int inlets = 0;
            foreach (var inlet in b.s.inlets.Where(i => i.edge >= 0))
            {
                var near = kn.faces.Where(f => f.edge == inlet.edge).Select(f => (f, s: KerbBuilder.Project(f, inlet.p.XZ, out double d), d)).Where(x => x.d <= 0.35).ToList();
                if (near.Count == 0) continue;
                inlets++;
                Assert.IsTrue(near.Any(x => x.f.forbidden.Any(i => i.reason == FrontageBlock.Inlet && i.s0 <= x.s + 1e-3 && i.s1 >= x.s - 1e-3)), $"inlet at {inlet.p}: no interval");
            }
            TestContext.WriteLine($"{stops} stop-line zones, {inlets} inlets checked; " + string.Join(", ", kn.faces.SelectMany(f => f.forbidden).GroupBy(i => i.reason).Select(x => $"{x.Key} {x.Count()}")));
        }

        [Test]
        public void FrontageAssetUpToDate()
        {
            string town = TownRoadSource.Towns().First();
            var baked = KerbBaker.LoadBakedTown(town);
            Assert.NotNull(baked, "no committed " + KerbBaker.TownAssetPath(town) + " (run RoadBakeCli.BakeTowns)");
            Assert.AreEqual(KerbFrontageAsset.FormatVersion, baked.formatVersion);
            Assert.AreEqual(baked.ComputeHash(), baked.contentHash, "the committed frontage's content matches its stored hash");
            var road = RoadNetworkBaker.LoadBakedTown(town);
            Assert.AreEqual(road.contentHash, baked.roadContentHash, "the frontage was baked with the committed road network");
            var b = Get("town");
            var fresh = KerbBaker.ToAsset(b.k, road.contentHash);
            try
            {
                Assert.AreEqual(baked.contentHash, fresh.contentHash, "the committed frontage is current (run RoadBakeCli.BakeTowns)");
                // the runtime source reads back exactly what the bake made (positions to float precision)
                var src = new KerbFrontageSource(baked);
                Assert.IsFalse(src.Provisional);
                Assert.AreEqual(b.kn.faces.Count, src.Faces.Count);
                foreach (var f in b.kn.faces)
                {
                    var k = src.Face(f.id);
                    Assert.NotNull(k);
                    Assert.AreEqual(f.Count, k.kerb.Length);
                    for (int i = 0; i < f.Count; i++)
                    {
                        Assert.AreEqual(f.kerb[i].x, k.kerb[i].x, 1e-3);
                        Assert.AreEqual(f.kerb[i].z, k.kerb[i].y, 1e-3);
                        Assert.AreEqual((float)f.s[i], k.s[i]);
                        Assert.AreEqual((float)f.gutter[i], k.gutter[i]);
                        Assert.AreEqual((float)f.top[i], k.topOfKerb[i]);
                        Assert.AreEqual((float)f.backY[i], k.walkBackY[i]);
                    }
                    Assert.AreEqual(f.forbidden.Count, k.forbidden.Length);
                    Assert.AreEqual(f.kind, k.kerbKind);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(fresh); }
        }

        [Test]
        public void Deterministic()
        {
            string town = TownRoadSource.Towns().First();
            var b = Get("town");
            var again = KerbBaker.FromRoads(b.k.roads);
            var a1 = KerbBaker.ToAsset(b.k, "x");
            var a2 = KerbBaker.ToAsset(again, "x");
            try { Assert.AreEqual(a1.contentHash, a2.contentHash, "two kerb bakes agree"); }
            finally { UnityEngine.Object.DestroyImmediate(a1); UnityEngine.Object.DestroyImmediate(a2); }
            var p2 = RoadPlateBuilder.Build(b.g, b.s, again.kerbs, b.k.roads.source.dem, again.settings);
            Assert.AreEqual(PlateHash(b.plates), PlateHash(p2), "two plate builds agree");
            TestContext.WriteLine($"{town}: {b.plates.Count} plates, hash {PlateHash(b.plates).Substring(0, 12)}");
        }

        internal static string PlateHash(List<Plate> plates)
        {
            var sorted = new List<Plate>(plates);
            sorted.Sort(PlateOrder.Comparison);
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                foreach (var p in sorted)
                {
                    w.Write(p.id); w.Write((byte)p.kind); w.Write(p.surface); w.Write(p.priority); w.Write(p.noDeform); w.Write(p.blend); w.Write(p.maxSlope);
                    foreach (var q in p.polygon) { w.Write(q.x); w.Write(q.y); }
                    foreach (var y in p.vertexY) w.Write(y);
                }
                w.Flush();
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(ms.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// roads-R2 step 6 (05 §5.10, §8.7) with a reference plate evaluation standing in for lane W's applier: the plates
    /// are well formed; the ground 0.25 m inside every paved footprint is at least 0.03 m under the surface; 1 m outside
    /// every paved edge it is within 0.1 m of the edge's height.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class RoadPlates
    {
        /// <summary>The surface of the kerbside partition at a point (pieces are convex: a fan from vertex 0).</summary>
        sealed class PieceLookup
        {
            readonly List<KerbPiece> m_P;
            readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();

            public PieceLookup(IEnumerable<KerbPiece> pieces)
            {
                m_P = pieces.ToList();
                for (int i = 0; i < m_P.Count; i++)
                {
                    var p = m_P[i].poly;
                    int x0 = C(p.Min(q => q.x)), x1 = C(p.Max(q => q.x)), z0 = C(p.Min(q => q.z)), z1 = C(p.Max(q => q.z));
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }

            static int C(double v) => (int)Math.Floor(v / 4);

            public bool Surface(D2 q, out double y)
            {
                y = double.NaN;
                if (!m_Cells.TryGetValue((C(q.x), C(q.z)), out var l)) return false;
                foreach (int i in l)
                {
                    var pc = m_P[i];
                    for (int k = 1; k + 1 < pc.poly.Length; k++)
                    {
                        D2 a = pc.poly[0], v0 = pc.poly[k] - a, v1 = pc.poly[k + 1] - a, v2 = q - a;
                        double den = v0.Cross(v1);
                        if (Math.Abs(den) < 1e-14) continue;
                        double u = v2.Cross(v1) / den, v = v0.Cross(v2) / den;
                        if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) continue;
                        y = pc.surfaceY[0] + u * (pc.surfaceY[k] - pc.surfaceY[0]) + v * (pc.surfaceY[k + 1] - pc.surfaceY[0]);
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>The R1 junction mesh's height at p (what R3 renders), false outside it.</summary>
        static bool MeshHeight(NodeSurface ns, D2 p, out double y)
        {
            y = double.NaN;
            for (int k = 0; k + 2 < ns.triangles.Count; k += 3)
            {
                D3 a = ns.vertices[ns.triangles[k]], b = ns.vertices[ns.triangles[k + 1]], c = ns.vertices[ns.triangles[k + 2]];
                D2 v0 = b.XZ - a.XZ, v1 = c.XZ - a.XZ, v2 = p - a.XZ;
                double den = v0.Cross(v1);
                if (Math.Abs(den) < 1e-12) continue;
                double u = v2.Cross(v1) / den, v = v0.Cross(v2) / den;
                if (u < -1e-9 || v < -1e-9 || u + v > 1 + 1e-9) continue;
                y = a.y + u * (b.y - a.y) + v * (c.y - a.y);
                return true;
            }
            return false;
        }

        [TestCase("village")]
        [TestCase("town")]
        public void WellFormed(string which)
        {
            var b = Kerbs.Get(which);
            var ids = new HashSet<string>();
            foreach (var p in b.plates)
            {
                Assert.IsTrue(ids.Add(p.id), "unique id " + p.id);
                Assert.GreaterOrEqual(p.polygon.Length, 3);
                Assert.AreEqual(PlateHeights.PerVertex, p.heights);
                Assert.AreEqual(p.polygon.Length, p.vertexY.Length);
                double area = 0;
                for (int i = 0, j = p.polygon.Length - 1; i < p.polygon.Length; j = i++) area += p.polygon[j].x * p.polygon[i].y - p.polygon[i].x * p.polygon[j].y;
                Assert.Greater(area, 0, p.id + " is counter-clockwise");
                Assert.AreEqual(PlateStage.Roads, p.Stage);
                Assert.IsTrue(p.blend >= 2 && p.blend <= 12, p.id + " blend");
                Assert.AreEqual(KerbRules.Paved(p.surface), p.noDeform, p.id + ": paved plates take no ruts");
            }
            TestContext.WriteLine($"{b.plates.Count} plates: " + string.Join(", ", b.plates.GroupBy(p => p.id.Substring(0, p.id.IndexOf(':'))).Select(x => $"{x.Key} {x.Count()}")));
        }

        [TestCase("village")]
        [TestCase("town")]
        public void TerrainUnderAndBesideThePavement(string which)
        {
            var b = Kerbs.Get(which);
            var field = new PlateField(b.plates);
            var st = b.k.settings;
            var g = b.g;
            int inside = 0, outside = 0, skipped = 0, conflicts = 0;
            var fails = new List<string>();
            var fallbacks = b.s.nodes.Where(n => n.mode == SurfaceMode.Fallback).Select(n => g.nodes[n.node].p.XZ).ToList();
            bool NearFallback(D2 p) => fallbacks.Any(q => D2.Distance(p, q) < 25);
            bool Structure(int edge) { var r = g.RoadOf(g.edges[edge].sourceRoad); return r != null && (r.bridge || r.tunnel); }

            void Under(D2 p, double surface, string what)
            {
                if (!field.Sample(p, out double y, out var win)) { fails.Add($"{what} at {p}: no plate"); return; }
                inside++;
                if (y > surface - 0.03 + 1e-6 && !NearFallback(p)) fails.Add($"{what} at {p}: ground {y:F3} vs surface {surface:F3} ({win.id})");
            }

            // ribbons: 0.25 m inside each pavement edge, halfway between stations
            foreach (var r in b.s.ribbons)
            {
                if (Structure(r.edge)) continue;
                var road = g.RoadOf(g.edges[r.edge].sourceRoad);
                if (!KerbRules.Paved(KerbRules.Surface(g.edges[r.edge].cls, road?.surface))) continue;
                int P = r.profile.Length;
                for (int q = 0; q + 1 < r.stations.Count; q++)
                    foreach (var (k0, k1) in new[] { (0, 1), (P - 1, P - 2) })
                    {
                        var e0 = D3.Lerp(r.stations[q][k0], r.stations[q + 1][k0], 0.5);
                        var e1 = D3.Lerp(r.stations[q][k1], r.stations[q + 1][k1], 0.5);
                        double t = 0.25 / Math.Max(0.25, D2.Distance(e0.XZ, e1.XZ));
                        var p = D3.Lerp(e0, e1, t);
                        Under(p.XZ, p.y, $"ribbon e{r.edge}");
                    }
            }
            // junctions: 0.25 m inside each outline edge's middle
            foreach (var ns in b.s.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.polygon.Count < 3 || ns.arms.All(a => Structure(a.edge))) continue;
                var main = ns.arms.OrderByDescending(a => RoadClasses.Rank(a.cls)).First();
                var road = g.RoadOf(g.edges[main.edge].sourceRoad);
                if (!KerbRules.Paved(KerbRules.Surface(main.cls, road?.surface))) continue;
                int m = ns.polygon.Count;
                for (int k = 0; k < m; k++)
                {
                    D2 a = ns.polygon[k], c = ns.polygon[(k + 1) % m];
                    if (D2.Distance(a, c) < 0.6) continue;
                    var p = (a + c) * 0.5 + (c - a).Normalized.Left * 0.25;
                    if (!TownZone.InPolygon(ns.polygon, p)) continue;
                    if (MeshHeight(ns, p, out double hy)) Under(p, hy, $"junction {ns.node}");
                }
            }
            // walks: 0.25 m behind the kerb face (on the kerb top) and 0.25 m inside the back edge, against the walk
            // surface of the kerbside piece there (the partition R3 meshes the walks from)
            var walks = new PieceLookup(b.kn.pieces.Where(p => p.kind == KerbPieceKind.Walk));
            int offWalk = 0;
            foreach (var f in b.kn.faces.Where(f => f.walk && !f.bridge))
                for (int i = 0; i + 1 < f.Count; i++)
                {
                    var n = (f.kerb[i + 1] - f.kerb[i]).Normalized.Left;
                    var k = (f.kerb[i] + f.kerb[i + 1]) * 0.5;
                    double w = 0.5 * (f.width[i] + f.width[i + 1]);
                    if (w < 0.75) continue;
                    foreach (var p in new[] { k + n * 0.25, k + n * (w - 0.25) })
                    {
                        if (!walks.Surface(p, out double surface)) { offWalk++; continue; }
                        Under(p, surface, $"walk f{f.id}");
                    }
                }
            Assert.Less(offWalk, 50, "walk samples outside every walk piece");
            // 1 m outside: behind the walk on the verge, beside an unkerbed pavement on the shoulder, against the nearest
            // point of the paved edge (the walk's back edge drops along the ramps at corners)
            var backEdges = new List<(D2 a, D2 b, double ya, double yb)>();
            var openEdges = new List<(D2 a, D2 b, double ya, double yb)>();
            foreach (var f in b.kn.faces.Where(f => !f.bridge))
                for (int i = 0; i + 1 < f.Count; i++)
                    if (f.kerbed) backEdges.Add((f.back[i], f.back[i + 1], f.backY[i], f.backY[i + 1]));
                    else openEdges.Add((f.kerb[i], f.kerb[i + 1], f.gutter[i], f.gutter[i + 1]));
            // the nearest paved edge's height, and whether another edge within 1 m more reach stands > 0.1 m apart from it
            // (two walks at different heights about 2 m apart: no ground can be within 0.1 m of both)
            double EdgeY(List<(D2 a, D2 b, double ya, double yb)> list, D2 p, out bool conflict)
            {
                double best = double.MaxValue, y = double.NaN;
                var near = new List<(double dist, double y)>();
                foreach (var e in list)
                {
                    if (Math.Abs(e.a.x - p.x) > 4 && Math.Abs(e.b.x - p.x) > 4) continue;
                    var d = e.b - e.a;
                    double l2 = d.Dot(d), t = l2 > 0 ? Math.Max(0, Math.Min(1, (p - e.a).Dot(d) / l2)) : 0;
                    double dist = D2.Distance(e.a + d * t, p), ye = e.ya + (e.yb - e.ya) * t;
                    near.Add((dist, ye));
                    if (dist < best) { best = dist; y = ye; }
                }
                double yy = y, bb = best;
                conflict = near.Any(x => x.dist <= bb + 1.0 && Math.Abs(x.y - yy) > 0.1);
                return y;
            }
            // the ground beside a building is its pad's (lane A and W, applied after the road plates)
            var footprints = b.k.roads.input.footprints.Where(x => x.corners != null && x.corners.Length >= 3).Select(x => x.corners).ToList();
            bool NearBuilding(D2 p) => footprints.Any(c => c.Any(q => Math.Abs(q.x - p.x) < 40 && Math.Abs(q.z - p.z) < 40) && (TownZone.InPolygon(c, p) || Geom2.DistanceToBoundary(c, p) < 1.0));
            int byBuilding = 0;
            foreach (var f in b.kn.faces.Where(f => !f.bridge))
            {
                var road = g.RoadOf(g.edges[f.edge].sourceRoad);
                if (!f.kerbed && !KerbRules.Paved(KerbRules.Surface(g.edges[f.edge].cls, road?.surface))) continue;
                for (int i = 0; i + 1 < f.Count; i++)
                {
                    var n = (f.kerb[i + 1] - f.kerb[i]).Normalized.Left;
                    var k = (f.kerb[i] + f.kerb[i + 1]) * 0.5;
                    double w = f.kerbed ? 0.5 * (f.width[i] + f.width[i + 1]) : 0;
                    var p = k + n * (w + 1.0);
                    // the ground there is a verge or shoulder piece (paved pieces of another road win where they reach)
                    if (!field.Sample(p, out double y, out var win) || win.priority != RoadPlateBuilder.PriorityVerge) { skipped++; continue; }
                    if (NearBuilding(p)) { byBuilding++; continue; }
                    bool verge = win.id.StartsWith("verge:", StringComparison.Ordinal);
                    double edgeY = EdgeY(verge ? backEdges : openEdges, p, out bool conflict);
                    outside++;
                    if (Math.Abs(y - edgeY) > 0.1) { if (conflict) conflicts++; else fails.Add($"1 m outside face {f.id} at {p}: ground {y:F3} vs the nearest edge {edgeY:F3} ({win.id})"); }
                }
            }
            TestContext.WriteLine($"{inside} points under the pavement, {outside} 1 m outside ({skipped} landed on another road's paving; {conflicts} between two edges at different heights; {byBuilding} within 1 m of a building), {fallbacks.Count} fallback nodes excused");
            Assert.Less(conflicts, outside / 200, "points between two paved edges at different heights stay rare");
            foreach (var l in fails.Take(30)) TestContext.WriteLine("  " + l);
            Assert.IsEmpty(fails, $"{fails.Count} failures, first: {fails.FirstOrDefault()}");
            Assert.Greater(inside, 100);
            Assert.Greater(outside, 100);
        }
    }

    /// <summary>
    /// roads-R2 step 5 (overview contract 3): DrivewayCutter.Resolve on a synthetic frontage: a clear request bakes,
    /// overlapping requests merge, a throat on a forbidden interval moves up to 3 m or is rejected with the interval's
    /// reason, mapped driveways move only off corner returns, unkerbed truck entrances get radius returns, and the answer
    /// is the same whatever order the requests come in.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class Driveways
    {
        sealed class Frontage : IKerbFrontageSource
        {
            public readonly List<KerbFrontage> faces = new List<KerbFrontage>();
            public bool Provisional => false;
            public IReadOnlyList<KerbFrontage> Faces => faces;
            public KerbFrontage Face(int id) => faces.FirstOrDefault(f => f.faceId == id);
        }

        /// <summary>A straight face along +x at z = 0 (block at z > 0), 60 m, gutter at y = 10, kerbed with a 1.65 m walk or unkerbed.</summary>
        static KerbFrontage Straight(int id, bool kerbed, params ForbiddenInterval[] forbidden)
        {
            int n = 31;
            var f = new KerbFrontage
            {
                faceId = id, osmWay = "way/" + id, kerb = new double2[n], s = new float[n], gutter = new float[n], topOfKerb = new float[n], walkBack = new double2[n], walkBackY = new float[n],
                kerbKind = kerbed ? KerbKind.Barrier150 : KerbKind.None, streetClass = StreetClass.Street, speedKmh = 40, forbidden = forbidden,
            };
            for (int i = 0; i < n; i++)
            {
                f.kerb[i] = new double2(2 * i, 0);
                f.s[i] = 2 * i;
                f.gutter[i] = 10;
                f.topOfKerb[i] = kerbed ? 10.15f : 10;
                f.walkBack[i] = new double2(2 * i, kerbed ? 1.65 : 0);
                f.walkBackY[i] = kerbed ? 10.15f + 0.015f * 1.5f : 10;
            }
            return f;
        }

        static ForbiddenInterval Iv(float a, float b, FrontageBlock r) => new ForbiddenInterval { s0 = a, s1 = b, reason = r };

        static DrivewayRequest Req(int id, int face, float s0, float s1, bool mapped = false, DrivewayKind kind = DrivewayKind.Residential, float ret = 0) =>
            new DrivewayRequest { id = id, faceId = face, s0 = s0, s1 = s1, kind = kind, mapped = mapped, returnRadius = ret, surface = SiteSurface.Concrete, osmWay = mapped ? "way/9" + id : null };

        static Frontage Source()
        {
            var src = new Frontage();
            src.faces.Add(Straight(1, true, Iv(0, 12, FrontageBlock.CornerReturn), Iv(20, 21.5f, FrontageBlock.Inlet), Iv(40, 41, FrontageBlock.Inlet), Iv(48, 60, FrontageBlock.CornerReturn)));
            src.faces.Add(Straight(2, false, Iv(0, 15, FrontageBlock.CornerReturn)));
            return src;
        }

        [Test]
        public void Resolve()
        {
            var cutter = new DrivewayCutter(Source());
            var reqs = new List<DrivewayRequest>
            {
                Req(10, 1, 30, 33),                 // clear
                Req(11, 1, 19, 22),                 // on the inlet: moves 2 m back to 17-20 (the smaller move; 2.5 m forwards also clears)
                Req(12, 1, 2, 5),                   // deep in the corner return: rejected
                Req(13, 1, 32, 35),                 // overlaps 10: merged
                Req(14, 1, 40.2f, 40.8f, true),     // mapped, on the second inlet: stays
                Req(15, 1, 10, 13, true),           // mapped, 2 m into the corner return: moves forwards 2 m
                Req(16, 1, 1, 4, true),             // mapped, deep in the corner return: rejected
                Req(17, 99, 1, 4),                  // no such face
                Req(18, 2, 30, 36, false, DrivewayKind.Truck, 9), // unkerbed truck entrance with 9 m radius returns
                Req(19, 1, 44, 47),                 // 1 m from the far corner return: baked as asked
            };
            var cuts = cutter.Resolve(reqs);
            Assert.AreEqual(reqs.Count, cuts.Length);
            for (int i = 0; i < reqs.Count; i++) Assert.AreEqual(reqs[i].id, cuts[i].requestId, "one cut per request, in request order");
            DrivewayCut C(int id) => cuts.First(c => c.requestId == id);
            foreach (var c in cuts) TestContext.WriteLine($"{c.requestId}: {c.outcome} {c.s0:F2}-{c.s1:F2} shift {c.shift:F2} merged {c.mergedWith} {c.rejectedBy} {c.reason}");

            var c10 = C(10);
            Assert.AreEqual(CutOutcome.Baked, c10.outcome);
            Assert.AreEqual(KerbKind.Dropped25, c10.kerb);
            Assert.AreEqual(30f, c10.s0, 1e-4);
            Assert.AreEqual(35f, c10.s1, 1e-4, "the shared driveway spans both throats");
            Assert.LessOrEqual(c10.crossSlope, 0.02f);
            Assert.AreEqual(30, c10.lotEdgeA.x, 1e-6); Assert.AreEqual(1.65, c10.lotEdgeA.y, 1e-6);
            Assert.AreEqual(35, c10.lotEdgeB.x, 1e-6);
            Assert.AreEqual(10f + 0.025f + 0.015f * 1.5f, c10.lotEdgeYA, 1e-5, "the lot meets the walk's back edge over the dropped kerb");
            Assert.Greater(Area(c10.apron), 5.0, "the apron spans the walk over the throat");

            var c13 = C(13);
            Assert.AreEqual(CutOutcome.Merged, c13.outcome);
            Assert.AreEqual(10, c13.mergedWith);
            Assert.AreEqual(c10.s0, c13.s0); Assert.AreEqual(c10.s1, c13.s1);

            var c11 = C(11);
            Assert.AreEqual(CutOutcome.Moved, c11.outcome);
            Assert.AreEqual(-2f, c11.shift, 1e-4);
            Assert.AreEqual(17f, c11.s0, 1e-4);

            var c12 = C(12);
            Assert.AreEqual(CutOutcome.Rejected, c12.outcome);
            Assert.AreEqual(FrontageBlock.CornerReturn, c12.rejectedBy);
            Assert.IsNotEmpty(c12.reason);

            Assert.AreEqual(CutOutcome.Baked, C(14).outcome, "a mapped driveway overrides an inlet");
            var c15 = C(15);
            Assert.AreEqual(CutOutcome.Moved, c15.outcome);
            Assert.AreEqual(2f, c15.shift, 1e-4, "a mapped driveway moves off the corner return");
            Assert.AreEqual(CutOutcome.Rejected, C(16).outcome);
            Assert.AreEqual(FrontageBlock.CornerReturn, C(16).rejectedBy);
            Assert.AreEqual(CutOutcome.Rejected, C(17).outcome);
            Assert.AreEqual(FrontageBlock.Other, C(17).rejectedBy);
            Assert.AreEqual(CutOutcome.Baked, C(19).outcome);

            var c18 = C(18);
            Assert.AreEqual(CutOutcome.Baked, c18.outcome);
            Assert.AreEqual(KerbKind.None, c18.kerb);
            Assert.AreEqual(1.2, c18.lotEdgeA.y, 1e-6, "unkerbed: the lot edge is the shoulder's outer edge");
            Assert.Less(c18.lotEdgeA.x, 30, "the radius return widens the entrance");
            Assert.Greater(c18.apron.Min(p => p.y), -1e-6);
            Assert.Less(c18.apron.Min(p => p.x), 21.0 + 1e-6, "9 m returns start 9 m before the throat at the pavement edge");

            // every baked or moved throat clears the intervals that bind it
            var src = Source();
            foreach (var (c, r) in cuts.Zip(reqs, (c, r) => (c, r)))
            {
                if (c.outcome == CutOutcome.Rejected) continue;
                var f = src.Face(c.faceId);
                foreach (var iv in f.forbidden)
                {
                    if (r.mapped && iv.reason != FrontageBlock.CornerReturn) continue;
                    if (c.outcome == CutOutcome.Merged) continue;
                    Assert.IsFalse(c.s0 < iv.s1 && c.s1 > iv.s0, $"request {r.id}: throat {c.s0}-{c.s1} on {iv.reason}");
                }
                Assert.LessOrEqual(Math.Abs(c.shift), DrivewayCutter.CutShiftMax + 1e-4f);
            }

            // the same requests in another order give the same cuts
            var shuffled = new List<DrivewayRequest>(reqs);
            shuffled.Reverse();
            var again = new DrivewayCutter(Source()).Resolve(shuffled);
            foreach (var c in again)
            {
                var o = C(c.requestId);
                Assert.AreEqual(o.outcome, c.outcome, $"request {c.requestId}");
                Assert.AreEqual(o.s0, c.s0); Assert.AreEqual(o.s1, c.s1); Assert.AreEqual(o.shift, c.shift); Assert.AreEqual(o.mergedWith, c.mergedWith);
            }
        }

        static double Area(double2[] p)
        {
            double a = 0;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++) a += p[j].x * p[i].y - p[i].x * p[j].y;
            return 0.5 * a;
        }

        /// <summary>The town's frontage takes a request on every open stretch: cuts there bake as asked and their aprons give road plates.</summary>
        [Test]
        public void OnTheTown()
        {
            var b = Kerbs.Get("town");
            var src = new KerbFrontageSource(b.kn);
            var cutter = new DrivewayCutter(src);
            var reqs = new List<DrivewayRequest>();
            int id = 0;
            foreach (var f in src.Faces)
            {
                // the middle of the face's longest open stretch, 3.5 m wide
                float best0 = 0, best1 = 0, cursor = 0;
                foreach (var iv in f.forbidden.OrderBy(i => i.s0).Concat(new[] { new ForbiddenInterval { s0 = f.Length, s1 = f.Length } }))
                {
                    if (iv.s0 - cursor > best1 - best0) { best0 = cursor; best1 = iv.s0; }
                    cursor = Math.Max(cursor, iv.s1);
                }
                if (best1 - best0 < 6) continue;
                float m = 0.5f * (best0 + best1);
                reqs.Add(new DrivewayRequest { id = id++, faceId = f.faceId, s0 = m - 1.75f, s1 = m + 1.75f, kind = DrivewayKind.Residential, surface = SiteSurface.Concrete });
            }
            var cuts = cutter.Resolve(reqs);
            Assert.Greater(cuts.Length, 50);
            foreach (var c in cuts)
            {
                Assert.AreEqual(CutOutcome.Baked, c.outcome, $"request {c.requestId} on face {c.faceId}: {c.reason}");
                Assert.Greater(Area(c.apron), 0, "apron counter-clockwise");
                Assert.LessOrEqual(c.crossSlope, 0.02f);
            }
            var plates = RoadPlateBuilder.Build(b.g, b.s, b.kn, b.k.roads.source.dem, b.k.settings, cuts, cutter);
            int aprons = plates.Count(p => p.id.StartsWith("apron:", StringComparison.Ordinal));
            Assert.Greater(aprons, cuts.Length);
            // under each kerbed cut the ground stays below the dropped walk
            var field = new PlateField(plates);
            var st = b.k.settings;
            foreach (var c in cuts)
            {
                var f = b.kn.Face(c.faceId);
                if (!f.walk) continue;
                double s = 0.5 * (c.s0 + c.s1);
                int i = 0;
                while (i + 2 < f.Count && f.s[i + 1] < s) i++;
                var n = (f.kerb[i + 1] - f.kerb[i]).Normalized.Left;
                double t = (s - f.s[i]) / (f.s[i + 1] - f.s[i]);
                var k = f.kerb[i] + (f.kerb[i + 1] - f.kerb[i]) * t;
                double gutter = f.gutter[i] + (f.gutter[i + 1] - f.gutter[i]) * t, w = f.width[i] + (f.width[i + 1] - f.width[i]) * t;
                if (w < 0.75) continue;
                var p = k + n * (w - 0.25);
                Assert.IsTrue(field.Sample(p, out double y, out var win));
                double surface = gutter + st.dropped + st.walkCrossSlope * (w - 0.25 - st.kerbTop);
                Assert.LessOrEqual(y, surface - 0.03 + 1e-6, $"cut on face {f.id}: ground {y:F3} under the dropped walk {surface:F3} ({win.id})");
            }
            TestContext.WriteLine($"{cuts.Length} cuts, {aprons} apron pieces");
        }
    }
}
