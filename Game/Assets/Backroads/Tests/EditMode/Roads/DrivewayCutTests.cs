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
    /// Lane S's driveway cuts in the road meshes (sites-S4's request to lane R): the town's driveways.json resolves to the
    /// same cuts lane S graded against (its answers); at every kerbed cut the kerb face is down to Dropped25 over the throat
    /// and back at the barrier past the flares, the walk over the throat sits at the dropped kerb top plus the walk's cross
    /// fall (the height lane S's driveway meets at the lot edge), and the ground of the apron plates stays at least 0.03 m
    /// under it; every paved cut on an unkerbed road has its slab across the shoulder.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class RoadCuts
    {
        sealed class Bake
        {
            public Kerbs.Bake k;
            public RoadDriveways.DrivewayFile file;
            public DrivewayCut[] cuts;
            public RoadMeshes meshes;
            public List<Plate> plates;
        }

        static Bake s_Bake;

        static Bake Get()
        {
            if (s_Bake != null) return s_Bake;
            string town = TownRoadSource.Towns().First();
            var k = Kerbs.Get("town");
            var b = new Bake { k = k, file = RoadDriveways.Load(town) };
            Assert.NotNull(b.file, "no " + RoadDriveways.PathFor(town));
            b.cuts = RoadDriveways.ForBake(k.k);
            var names = StreetNameList.Load();
            var streets = StreetNamer.Assign(k.g, names.names, names.approved);
            b.meshes = RoadMeshBuilder.Build(k.g, k.s, k.kn, streets, k.k.settings, null, null, b.cuts, k.k.cutter);
            b.plates = RoadPlateBuilder.Build(k.g, k.s, k.kn, k.k.roads.source.dem, k.k.settings, b.cuts, k.k.cutter);
            return s_Bake = b;
        }

        [OneTimeTearDown]
        public void Release() => s_Bake = null;

        [Test]
        public void CutsMatchLaneSAnswers()
        {
            var b = Get();
            Assert.AreEqual(b.file.requests.Count, b.cuts.Length);
            Assert.AreEqual(b.file.requests.Count, b.file.answers.Count, "an answer for every request");
            var byId = b.cuts.ToDictionary(c => c.requestId);
            foreach (var a in b.file.answers)
            {
                var c = byId[a.requestId];
                Assert.AreEqual(a.outcome, c.outcome.ToString(), $"request {a.requestId}");
                Assert.AreEqual(a.faceId, c.faceId, $"request {a.requestId}");
                if (c.outcome == CutOutcome.Rejected) continue;
                Assert.AreEqual(a.s0, c.s0, 1e-3, $"request {a.requestId} throat start");
                Assert.AreEqual(a.s1, c.s1, 1e-3, $"request {a.requestId} throat end");
                Assert.AreEqual(a.mergedWith, c.mergedWith, $"request {a.requestId}");
            }
            var m = b.meshes;
            TestContext.WriteLine($"{b.cuts.Length} requests: " + string.Join(", ", b.cuts.GroupBy(c => c.outcome).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")) +
                                  $"; in the meshes {m.cutsKerbed} kerbed cuts ({m.droppedKerbM:F0} m of throat), {m.cutsSlab} paved slabs across a shoulder, {m.cutsUnpaved} unpaved");
            Assert.Greater(m.cutsKerbed, 0);
        }

        [Test]
        public void KerbAndWalkDropAtEveryKerbedCut()
        {
            var b = Get();
            var kn = b.k.kn;
            var st = b.k.k.settings;
            // the walk surface the meshes draw (the concrete pieces in plan) and the kerb face's top vertices
            var walk = new SurfaceIndex();
            foreach (var p in b.meshes.parts.Where(p => p.material == RoadMaterial.Concrete && p.collider && p.smooth == 2)) walk.Add(p);
            var faceTops = b.meshes.parts.Where(p => p.material == RoadMaterial.Concrete && p.collider && p.smooth == 0).SelectMany(p => p.pos).ToList();
            var field = new PlateField(b.plates);
            var spans = RoadPlateBuilder.CutSpans(kn, b.cuts, b.k.k.cutter);
            int cuts = 0, carried = 0, walkPoints = 0;
            var fails = new List<string>();
            foreach (var span in spans)
            {
                var f = span.face;
                if (f.bridge || !f.kerbed || !f.walk) continue;
                var c = new { span.s0, span.s1 };
                float flare = (float)span.flare;
                if (span.carried) carried++; else cuts++;
                // the kerb face: dropped at the throat's ends (stations of the mesh), back at the barrier past each flare
                foreach (var (s, want) in new[] { ((double)c.s0, st.dropped), (c.s1, st.dropped), (c.s0 - flare - 0.05, st.barrier), (c.s1 + flare + 0.05, st.barrier) })
                {
                    if (s < 0.05 || s > f.Length - 0.05) continue;
                    var kp = At(f, f.kerb, s);
                    double g = Interp(f, f.gutter, s);
                    double h = RoadPlateBuilder.CutKerbHeight(f, s, c.s0, c.s1, flare, st);
                    if (want > st.dropped && spans.Any(o => o.face == f && o.s0 != span.s0 && s > o.s0 - 3.5 && s < o.s1 + 3.5)) continue;
                    double top = faceTops.Where(v => Math.Abs(v.x - kp.x) < 0.002 && Math.Abs(v.z - kp.z) < 0.002).Select(v => v.y).DefaultIfEmpty(double.NaN).Max();
                    if (double.IsNaN(top)) { if (want == st.dropped) fails.Add($"face {f.id}: no kerb vertex at the throat end s {s:F2}"); continue; }
                    if (Math.Abs(top - (g + h)) > 2e-3) fails.Add($"face {f.id} cut {c.s0:F1}-{c.s1:F1}: kerb top at s {s:F2} is {top - g:F3} m over the gutter, want {h:F3}");
                }
                // the walk over the throat and both flares, every 0.25 m, behind the kerb, mid-walk and at the back: the lowered
                // kerb top (or a lower one already there) plus the cross fall, measured at each probe's own place on the kerb;
                // the apron plate's ground at least 0.03 m under it
                var others = spans.Where(o => o.face == f).Select(o => (o.s0, o.s1, fl: o.flare)).ToList();
                for (double s = Math.Max(0.05, c.s0 - flare); s <= Math.Min(f.Length - 0.05, c.s1 + flare) + 1e-9; s += 0.25)
                {
                    int im = Seg(f, s);
                    var k0 = At(f, f.kerb, s);
                    var n = (f.kerb[im + 1] - f.kerb[im]).Normalized.Left;
                    double w = Interp(f, f.width, s);
                    foreach (double u in new[] { 0.05, 0.5 * w, w - 0.1 })
                    {
                        if (u > w - 0.05 || u < 0.05) continue;
                        var q = k0 + n * u;
                        if (!walk.Height(q, out double y)) continue;
                        double sq = Project(f, q, out double uq);
                        if (sq < 0.05 || sq > f.Length - 0.05) continue;
                        walkPoints++;
                        double g = Interp(f, f.gutter, sq);
                        double h = others.Min(o => RoadPlateBuilder.CutKerbHeight(f, sq, o.s0, o.s1, o.fl, st));
                        double want = g + h + st.walkCrossSlope * Math.Max(0, uq - st.kerbTop);
                        // (within 0.3 m of a face end the probe may land on the next face's walk, square to its own kerb)
                        if (sq > 0.3 && sq < f.Length - 0.3 && Math.Abs(y - want) > 3e-3) fails.Add($"face {f.id} cut {c.s0:F2}-{c.s1:F2}: walk at s {sq:F2} u {uq:F2} is {y - g:F4} m over the gutter, want {want - g:F4}");
                        if (field.Sample(q, out double ground, out var win) && ground > y - 0.03 + 1e-6)
                            fails.Add($"face {f.id} cut {c.s0:F2}-{c.s1:F2}: ground {ground:F3} under the apron at {y:F3}, s {sq:F2} u {uq:F2} ({win.id})");
                    }
                }
            }
            TestContext.WriteLine($"{cuts} kerbed cuts checked ({carried} carried on into the next face), {walkPoints} walk points over the throats and flares");
            foreach (var l in fails.Take(30)) TestContext.WriteLine("  " + l);
            Assert.IsEmpty(fails, $"{fails.Count} failures, first: {fails.FirstOrDefault()}");
            Assert.Greater(cuts, 10);
            Assert.Greater(walkPoints, cuts);
        }

        [Test]
        public void SlabsOnUnkerbedPavedCuts()
        {
            var b = Get();
            var kn = b.k.kn;
            int want = 0;
            var done = new HashSet<(int, float, float)>();
            foreach (var c in b.cuts)
            {
                if (c.outcome == CutOutcome.Rejected || !done.Add((c.faceId, c.s0, c.s1))) continue;
                var f = kn.Face(c.faceId);
                if (f == null || f.bridge || f.kerbed || !b.k.k.cutter.CutShape(c.requestId, out _, out _, out var surface)) continue;
                if (KerbRules.Paved(KerbRules.Surface(surface))) want++;
            }
            Assert.AreEqual(want, b.meshes.cutsSlab);
            Assert.IsFalse(b.meshes.notes.Any(x => x.StartsWith("driveway slab", StringComparison.Ordinal)), string.Join("; ", b.meshes.notes));
            TestContext.WriteLine($"{want} paved cuts on unkerbed roads, {b.meshes.cutsUnpaved} unpaved (the ground's own surface)");
        }

        /// <summary>The station of q's nearest point on the kerb line, and q's distance from it.</summary>
        static double Project(KerbFace f, D2 q, out double dist)
        {
            double best = double.MaxValue, s = 0;
            for (int i = 0; i + 1 < f.Count; i++)
            {
                var a = f.kerb[i];
                var d = f.kerb[i + 1] - a;
                double l2 = d.Dot(d);
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, (q - a).Dot(d) / l2)) : 0;
                double e = D2.Distance(a + d * t, q);
                if (e < best) { best = e; s = f.s[i] + t * (f.s[i + 1] - f.s[i]); }
            }
            dist = best;
            return s;
        }

        static int Seg(KerbFace f, double s)
        {
            int i = 0;
            while (i + 2 < f.Count && f.s[i + 1] < s) i++;
            return i;
        }

        static D2 At(KerbFace f, List<D2> p, double s)
        {
            int i = Seg(f, s);
            double t = f.s[i + 1] > f.s[i] ? (s - f.s[i]) / (f.s[i + 1] - f.s[i]) : 0;
            return p[i] + (p[i + 1] - p[i]) * Math.Max(0, Math.Min(1, t));
        }

        static double Interp(KerbFace f, List<double> v, double s)
        {
            int i = Seg(f, s);
            double t = f.s[i + 1] > f.s[i] ? (s - f.s[i]) / (f.s[i + 1] - f.s[i]) : 0;
            t = Math.Max(0, Math.Min(1, t));
            return v[i] + (v[i + 1] - v[i]) * t;
        }
    }
}
