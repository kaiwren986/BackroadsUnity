using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Sites;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>
    /// Sites.ParkingSynthetic (card sites-S2 "done when"): on synthetic lots, every stall inside its lot and reachable from
    /// an entrance through the aisles; nothing overlaps; accessible counts follow the 2010 ADA table; capacity within 10 %
    /// of a hand-computed optimum on 10 reference rectangles; deterministic; under 20 ms per lot. Plan maps of every lot
    /// go to artifacts/sites/s2/*.svg.
    /// </summary>
    [TestFixture, Category("Sites")]
    public class ParkingSynthetic
    {
        static readonly Dictionary<string, (LotInput input, LotLayout layout)> s_Cache = new Dictionary<string, (LotInput, LotLayout)>();

        static IEnumerable<string> LotNames() => SyntheticLots.All.Select(c => c.name);

        static SyntheticLots.Case CaseOf(string name) => SyntheticLots.All.First(c => c.name == name);

        static (LotInput input, LotLayout layout) Get(string name)
        {
            if (s_Cache.TryGetValue(name, out var r)) return r;
            var input = CaseOf(name).make();
            var layout = ParkingPacker.Pack(input);
            r = (input, layout);
            s_Cache[name] = r;
            WriteSvg(name, input, layout);
            return r;
        }

        static void WriteSvg(string name, LotInput input, LotLayout l)
        {
            var dir = Path.Combine(RepoPaths.Artifacts, "sites", "s2");
            Directory.CreateDirectory(dir);
            var rep = l.report;
            string title = $"{name}: {rep.total} stalls ({rep.accessible} accessible, {rep.van} van), aisles {rep.dirDeg:F0} deg, stalls {rep.angle:F0} deg, mode {rep.spineMode}";
            File.WriteAllText(Path.Combine(dir, name + ".svg"), LotLayoutSvg.Render(l, input.building?.footprint, title), new UTF8Encoding(false));
        }

        static string Summary(string name, LotLayout l)
        {
            var r = l.report;
            return $"{name,-26} dir {r.dirDeg,5:F1} angle {r.angle,2:F0} mode {r.spineMode} sq {(r.squeezed ? 1 : 0)} capacity {r.capacity,3} total {r.total,3} " +
                   $"acc {r.accessible} van {r.van} dead {r.deadEnds} islands {l.islands.Count} lamps {l.lamps.Count} stops {l.wheelStops.Count} cand {r.candidates}" +
                   (r.notes.Count > 0 ? "  notes: " + string.Join("; ", r.notes) : "");
        }

        // ------------------------------------------------------------------------------------------ per lot

        [TestCaseSource(nameof(LotNames))]
        public void Lot(string name)
        {
            var (input, l) = Get(name);
            TestContext.WriteLine(Summary(name, l));
            Assert.IsTrue(l.report.valid, "layout invalid: " + string.Join("; ", l.report.notes));
            Assert.Greater(l.stalls.Count, 0, "no stall");
            var lot = l.lot;
            var bad = new List<string>();

            // Every stall wholly inside the lot, 0.5 m from its edge.
            for (int i = 0; i < l.stalls.Count; i++)
                if (!Poly2.ConvexInside(l.stalls[i].poly, lot, 0.5 - 1e-6)) bad.Add($"stall {i} at {Fmt(l.stalls[i].front)} is not 0.5 m inside the lot");

            // No stall overlaps another stall, a lane, an island, the building (+1 m) or an entrance's first 6 m.
            for (int i = 0; i < l.stalls.Count; i++)
            {
                var s = l.stalls[i].poly;
                for (int j = i + 1; j < l.stalls.Count; j++)
                    if (Poly2.ConvexOverlap(s, l.stalls[j].poly, 1e-4)) bad.Add($"stalls {i} and {j} overlap");
                for (int k = 0; k < l.lanes.Count; k++)
                    if (Poly2.ConvexOverlap(s, l.lanes[k].poly, 1e-4)) bad.Add($"stall {i} overlaps lane {k} ({l.lanes[k].kind})");
                for (int k = 0; k < l.islands.Count; k++)
                    if (Poly2.ConvexOverlap(s, l.islands[k].poly, 1e-4)) bad.Add($"stall {i} overlaps island {k}");
                if (input.building?.footprint != null)
                {
                    var b = Poly2.Ccw(input.building.footprint);
                    if (s.Any(p => Poly2.Contains(b, p)) || Poly2.BoundaryGap(s, b) < 1.0 - 1e-6) bad.Add($"stall {i} within 1 m of the building");
                }
                foreach (var e in l.entrances)
                {
                    var n = Inward(lot, e);
                    var t = math.normalize(e.b - e.a);
                    var zone = Poly2.Ccw(new[] { e.a - t * 0.5, e.b + t * 0.5, e.b + t * 0.5 + n * 6, e.a - t * 0.5 + n * 6 });
                    if (Poly2.ConvexOverlap(s, zone, 1e-4)) bad.Add($"stall {i} in the first 6 m of the entrance at {Fmt(e.Mid)}");
                }
            }
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(30)));

            // Every stall reachable from an entrance through the lanes (a graph of the output, independent of the packer).
            var reach = ReachableLanes(l);
            for (int i = 0; i < l.stalls.Count; i++)
            {
                var st = l.stalls[i];
                if (st.lane < 0 || st.lane >= l.lanes.Count) { bad.Add($"stall {i} has no aisle"); continue; }
                var lane = l.lanes[st.lane];
                if (lane.kind != LaneKind.Aisle) bad.Add($"stall {i} opens onto a {lane.kind}");
                if (!reach.Contains(st.lane)) bad.Add($"stall {i} at {Fmt(st.front)}: its aisle is not reachable from an entrance");
                if (!Poly2.Contains(lane.poly, st.front) && Poly2.BoundaryDistance(lane.poly, st.front, out _) > 0.05) bad.Add($"stall {i}: its front is not on its aisle");
            }
            // One-way aisles: open at both ends (a lane or an entrance), never a dead end.
            for (int k = 0; k < l.lanes.Count; k++)
            {
                var ln = l.lanes[k];
                if (!ln.oneWay || ln.kind != LaneKind.Aisle) continue;
                if (!EndOpen(l, k, ln.a) || !EndOpen(l, k, ln.b)) bad.Add($"one-way aisle {k} has a dead end");
            }
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(30)));

            // Accessible stalls: the ADA count for the lot's total, one van per six, nearer the door than the median stall.
            if (input.targets.accessible && input.targets.stallKind != StallKind.Trailer)
            {
                int total = l.stalls.Count, acc = l.AccessibleCount, van = l.Count(StallKind.Van);
                Assert.GreaterOrEqual(acc, Ada.Required(total), "accessible stalls");
                Assert.LessOrEqual(acc, Ada.Required(total) + 1, "accessible stalls (at most one over the table after the conversion)");
                Assert.GreaterOrEqual(van, Ada.RequiredVan(acc), "van-accessible stalls");
                var door = input.building != null ? (input.building.hasDoor ? input.building.door : Poly2.Centroid(input.building.footprint)) : Poly2.Centroid(lot);
                var all = l.stalls.Select(s => math.distance(s.front, door)).OrderBy(d => d).ToList();
                double median = all[all.Count / 2];
                foreach (var s in l.stalls.Where(s => s.kind == StallKind.Accessible || s.kind == StallKind.Van))
                    Assert.LessOrEqual(math.distance(s.front, door), median + 1e-6, "accessible stall farther from the door than the median stall");
                // Each accessible stall has an access aisle beside it (a blue hatch touching it) and a sign; paved lots paint the symbol.
                foreach (var s in l.stalls.Where(s => s.kind == StallKind.Accessible || s.kind == StallKind.Van))
                {
                    if (input.targets.paved)
                        Assert.IsTrue(l.hatches.Any(h => h.colour == MarkColour.Blue && Poly2.BoundaryGap(h.poly, s.poly) < 1e-3), "access aisle beside an accessible stall");
                    Assert.IsTrue(l.ramps.Any(r => math.distance(r.pos, s.back) < 6.0), "kerb ramp from the access aisle");
                    Assert.IsTrue(l.props.Any(p => p.key.StartsWith("sign.", StringComparison.Ordinal) && p.key.Contains("ccessible") && math.distance(p.pos, s.back) < 1.0), "accessible sign at the stall head");
                }
                if (input.targets.paved) Assert.AreEqual(acc, l.signs.Count(x => x.symbol == MarkSymbol.Accessible), "one symbol per accessible stall");
            }

            // Islands: never more than 15 stalls side by side without an island (stalls that touch form a chain).
            foreach (var g in l.stalls.GroupBy(s => s.run))
            {
                var run = g.ToList();
                var seen = new bool[run.Count];
                for (int i = 0; i < run.Count; i++)
                {
                    if (seen[i]) continue;
                    int size = 0;
                    var stack = new Stack<int>();
                    stack.Push(i);
                    seen[i] = true;
                    while (stack.Count > 0)
                    {
                        int a = stack.Pop();
                        size++;
                        for (int j = 0; j < run.Count; j++)
                            if (!seen[j] && Poly2.BoundaryGap(run[a].poly, run[j].poly) < 1e-6) { seen[j] = true; stack.Push(j); }
                    }
                    Assert.LessOrEqual(size, ParkingDims.Default.islandEvery, $"run {g.Key}: {size} stalls side by side without an island");
                }
            }

            // Paint and kerbs by surface.
            if (!input.targets.paved) { Assert.IsEmpty(l.lines, "gravel lots are not painted"); Assert.IsEmpty(l.kerbs, "no kerbs on gravel"); }
            else Assert.Greater(l.lines.Count, l.stalls.Count, "stall lines");
            bool kerbed = input.targets.kerbed && input.targets.paved;
            if (kerbed) Assert.IsTrue(l.kerbs.Count > 0 && l.kerbs.All(k => k.kind == KerbKind.Barrier150), "Barrier150 kerbs on paved kerb-zone lots");
            else Assert.IsEmpty(l.kerbs, "kerbs only on paved lots in the kerb zone");
            Assert.IsTrue(l.islands.All(x => x.kerbed == kerbed), "islands kerbed exactly on kerbed lots");

            // Lamps: no stall head more than lampMax / 2 (+ a stall) from a pole; poles at least 10 m apart.
            var dims = ParkingDims.Default;
            foreach (var s in l.stalls)
                Assert.LessOrEqual(l.lamps.Min(p => math.distance(p, s.back)), dims.lampMax * 0.5 + 6.0, $"stall at {Fmt(s.back)} far from a lamp");
            for (int i = 0; i < l.lamps.Count; i++)
                for (int j = i + 1; j < l.lamps.Count; j++)
                    Assert.GreaterOrEqual(math.distance(l.lamps[i], l.lamps[j]), 10.0, "lamp poles too close");
        }

        // ------------------------------------------------------------------------------------------ the ADA table

        [Test]
        public void AdaTable()
        {
            // 2010 ADA Standards table 208.2, written out by hand.
            var rows = new (int upTo, int req)[] { (25, 1), (50, 2), (75, 3), (100, 4), (150, 5), (200, 6), (300, 7), (400, 8), (500, 9) };
            int lo = 1;
            foreach (var (upTo, req) in rows)
            {
                for (int t = lo; t <= upTo; t++) Assert.AreEqual(req, Ada.Required(t), $"{t} stalls");
                lo = upTo + 1;
            }
            Assert.AreEqual(0, Ada.Required(0));
            Assert.AreEqual(11, Ada.Required(501), "2 % above 500, rounded up");
            Assert.AreEqual(20, Ada.Required(1000));
            Assert.AreEqual(21, Ada.Required(1001), "20 + 1 per 100 over 1000");
            for (int a = 1; a <= 6; a++) Assert.AreEqual(1, Ada.RequiredVan(a), "one van space per six or fraction");
            Assert.AreEqual(2, Ada.RequiredVan(7));
            Assert.AreEqual(2, Ada.RequiredVan(12));
            Assert.AreEqual(3, Ada.RequiredVan(13));

            // Lots of 1-500 stalls: the conversion meets the table for every total the packer produces (long single rows).
            var counts = new List<string>();
            foreach (double len in new[] { 8.0, 14, 30, 60, 75, 140, 200, 300, 420, 560, 700 })
            {
                var input = new LotInput { lot = SyntheticLots.Rect(len, 26), seed = 5 };
                input.entrances.Add(SyntheticLots.Ent(0, 9, 0, 16.3));
                var l = ParkingPacker.Pack(input);
                int total = l.stalls.Count;
                counts.Add($"{len} m: {total} stalls, {l.AccessibleCount} accessible ({l.Count(StallKind.Van)} van)");
                if (total == 0) continue;
                Assert.GreaterOrEqual(l.AccessibleCount, Ada.Required(total), $"{total} stalls");
                Assert.GreaterOrEqual(l.Count(StallKind.Van), Ada.RequiredVan(l.AccessibleCount));
            }
            TestContext.WriteLine(string.Join("\n", counts));
        }

        // ------------------------------------------------------------------------------------------ capacity

        [Test]
        public void CapacityNearHandOptimum()
        {
            var lines = new List<string>();
            var bad = new List<string>();
            foreach (var c in SyntheticLots.References)
            {
                var l = Get(c.name).layout;
                int cap = l.report.capacity;
                double dev = (cap - c.handOptimum) / (double)c.handOptimum;
                lines.Add($"{c.name,-12} packer {cap,3}  hand {c.handOptimum,3}  {dev * 100,6:F1} %   ({c.handNote})");
                if (Math.Abs(dev) > 0.10 + 1e-9) bad.Add($"{c.name}: {cap} vs hand {c.handOptimum} ({dev * 100:F1} %)");
            }
            TestContext.WriteLine(string.Join("\n", lines));
            Assert.AreEqual(10, SyntheticLots.References.Count);
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        // ------------------------------------------------------------------------------------------ determinism, speed

        [Test]
        public void Deterministic()
        {
            foreach (var c in SyntheticLots.All)
            {
                var h1 = LotLayoutHash.Compute(ParkingPacker.Pack(c.make()));
                var h2 = LotLayoutHash.Compute(ParkingPacker.Pack(c.make()));
                Assert.AreEqual(h1, h2, c.name + ": two packs differ");
                Assert.AreEqual(h1, LotLayoutHash.Compute(Get(c.name).layout), c.name + ": differs from the cached pack");
            }
        }

        [Test]
        public void UnderTwentyMillisecondsPerLot()
        {
            var lines = new List<string>();
            var bad = new List<string>();
            foreach (var c in SyntheticLots.All)
            {
                var input = c.make();
                ParkingPacker.Pack(input);   // warm-up (JIT)
                double best = double.MaxValue;
                for (int k = 0; k < 3; k++)
                {
                    var sw = Stopwatch.StartNew();
                    ParkingPacker.Pack(c.make());
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
                lines.Add($"{c.name,-26} {best,6:F2} ms");
                if (best >= 20) bad.Add($"{c.name}: {best:F1} ms");
            }
            TestContext.WriteLine(string.Join("\n", lines));
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        // ------------------------------------------------------------------------------------------ profile

        [Test]
        public void SizesFromTheProfile()
        {
            var path = RepoPaths.InRepo("Game/Assets/Content/sites/siteProfile.json");
            var d = ParkingDims.FromProfile(BrJson.Parse(File.ReadAllText(path)));
            Assert.AreEqual("siteProfile.json", d.source);
            Assert.AreEqual(2.75, d.stallWidth, 1e-9, "stall width = the profile's p50");
            Assert.AreEqual(5.5, d.stallLength, 1e-9, "stall length 5.5 m [doc], consistent with the measured module");
            Assert.LessOrEqual(d.squeezeWidth, d.stallWidth);
            Assert.AreEqual(18.4, d.moduleP50, 1e-9);
            Assert.IsTrue(d.ModuleInBand, $"90-degree module {d.Module90} m outside the measured p25-p75 band {d.moduleP25}-{d.moduleP75} m");
            var l = ParkingPacker.Pack(CaseOf("rect_60x40").make(), d);
            Assert.IsTrue(l.report.moduleInBand);
            Assert.AreEqual(LotLayoutHash.Compute(Get("rect_60x40").layout), LotLayoutHash.Compute(l), "the profile's sizes are the defaults");
        }

        // ------------------------------------------------------------------------------------------ features

        [Test]
        public void OneWayAnglesLeanWithTheFlow()
        {
            var l = Get("angled_one_way").layout;
            TestContext.WriteLine(Summary("angled_one_way", l));
            Assert.Less(l.report.angle, 89.9f, "a 17 m lot takes angled stalls on a one-way aisle");
            var oneWay = l.lanes.Where(x => x.kind == LaneKind.Aisle && x.oneWay).ToList();
            Assert.IsNotEmpty(oneWay);
            foreach (var ln in oneWay) Assert.Greater(ln.b.x, ln.a.x, "flow from the entry-only entrance (west) to the exit-only one (east)");
            Assert.IsTrue(l.signs.Any(s => s.symbol == MarkSymbol.Arrow), "arrows on one-way aisles");
            foreach (var s in l.stalls)
            {
                var lane = l.lanes[s.lane];
                var flow = math.normalize(lane.b - lane.a);
                Assert.Greater(math.dot(math.normalize(s.back - s.front), flow), 0.3, "angled stalls lean with the flow");
            }
            Assert.IsTrue(l.lines.Any(x => Math.Abs(x.width - 0.6f) < 1e-6), "stop bar at the exit onto the main street");
        }

        [Test]
        public void FireLaneAlongTheBigBox()
        {
            var (input, l) = Get("bigbox");
            TestContext.WriteLine(Summary("bigbox", l));
            Assert.NotNull(l.fireLane);
            Assert.IsTrue(l.stalls.All(s => !Poly2.ConvexOverlap(s.poly, l.fireLane, 1e-4)), "no stall in the fire lane");
            Assert.IsTrue(l.lines.Any(x => x.colour == MarkColour.Red), "painted fire-lane kerb");
            Assert.IsTrue(l.signs.Any(x => x.symbol == MarkSymbol.FireLane));
            Assert.AreEqual(2, l.lines.Count(x => Math.Abs(x.width - 0.6f) < 1e-6), "stop bars at both exits onto the main street");
            Assert.Greater(l.stalls.Count, 100, "a big-box lot");
        }

        [Test]
        public void DriveThroughStacksSixToEightCars()
        {
            var (input, l) = Get("fastfood");
            TestContext.WriteLine(Summary("fastfood", l));
            Assert.NotNull(l.driveThrough);
            Assert.That(l.driveThrough.stackCars, Is.InRange(6, 8));
            Assert.AreEqual(3.7f, l.driveThrough.width, 1e-6);
            foreach (var piece in l.driveThrough.pieces)
                Assert.IsTrue(l.stalls.All(s => !Poly2.ConvexOverlap(s.poly, piece, 1e-4)), "no stall on the drive-through lane");
            // The building is on the driver's left all the way (pickup window on the driver's side).
            var c = Poly2.Centroid(input.building.footprint);
            var p = l.driveThrough.path;
            for (int i = 0; i + 1 < p.Length - 1; i++)
                Assert.Greater(Poly2.Cross(p[i + 1] - p[i], c - p[i]), 0, "building on the left of the lane");
        }

        [Test]
        public void WheelStopsWhereStallsFaceTheWalk()
        {
            var (input, l) = Get("kerbed_walk");
            TestContext.WriteLine(Summary("kerbed_walk", l));
            // Stalls whose head is within 1.5 m of the walk edge (z = 0) have a stop; on this kerbed lot no other edge does.
            foreach (var (s, i) in l.stalls.Select((s, i) => (s, i)))
            {
                bool facesWalk = s.back.y <= 1.5 + 1e-6 && math.normalize(s.back - s.front).y < -0.5;
                bool facesBuilding = Poly2.BoundaryDistance(Poly2.Ccw(input.building.footprint), s.back, out _) <= 2.5;
                bool has = l.wheelStops.Any(w => w.stall == i);
                if (facesWalk || facesBuilding) Assert.IsTrue(has, $"stall {i} faces the walk or the building without a wheel stop");
            }
            var gravel = Get("gravel").layout;
            foreach (var (s, i) in gravel.stalls.Select((s, i) => (s, i)))
                if (Poly2.BoundaryDistance(gravel.lot, s.back, out _) <= 1.0) Assert.IsTrue(gravel.wheelStops.Any(w => w.stall == i), "unkerbed lot: wheel stops at the property line");
        }

        [Test]
        public void TrailerStalls()
        {
            var l = Get("trailer_yard").layout;
            TestContext.WriteLine(Summary("trailer_yard", l));
            Assert.IsTrue(l.stalls.All(s => s.kind == StallKind.Trailer));
            foreach (var s in l.stalls)
            {
                double w = math.distance(s.poly[0], s.poly[1]), d = math.distance(s.poly[1], s.poly[2]);
                Assert.AreEqual(3.7 * 16, w * d, 0.5, "trailer stalls are 3.7 x 16 m");
            }
            Assert.AreEqual(0, l.AccessibleCount);
        }

        // ------------------------------------------------------------------------------------------ helpers

        static string Fmt(double2 p) => $"({p.x:F1}, {p.y:F1})";

        static double2 Inward(double2[] lot, LotEntrance e)
        {
            var n = Poly2.Perp(math.normalize(e.b - e.a));
            return Poly2.Contains(lot, e.Mid + n * 0.3) ? n : -n;
        }

        static HashSet<int> ReachableLanes(LotLayout l)
        {
            var lanes = l.lanes;
            var seen = new HashSet<int>();
            var q = new Queue<int>();
            for (int k = 0; k < lanes.Count; k++)
            {
                if (lanes[k].kind == LaneKind.DriveThrough) continue;
                foreach (var e in l.entrances)
                {
                    var mid = e.Mid;
                    if (Poly2.Contains(lanes[k].poly, mid) || Poly2.BoundaryDistance(lanes[k].poly, mid, out _) <= 0.6) { if (seen.Add(k)) q.Enqueue(k); break; }
                }
            }
            while (q.Count > 0)
            {
                int a = q.Dequeue();
                for (int k = 0; k < lanes.Count; k++)
                {
                    if (seen.Contains(k) || lanes[k].kind == LaneKind.DriveThrough) continue;
                    if (Poly2.ConvexOverlap(lanes[a].poly, lanes[k].poly, -0.05)) { seen.Add(k); q.Enqueue(k); }
                }
            }
            return seen;
        }

        static bool EndOpen(LotLayout l, int lane, double2 p)
        {
            for (int k = 0; k < l.lanes.Count; k++)
            {
                if (k == lane || l.lanes[k].kind != LaneKind.Spine) continue;
                if (Poly2.Contains(l.lanes[k].poly, p) || Poly2.BoundaryDistance(l.lanes[k].poly, p, out _) <= 1.0) return true;
            }
            foreach (var e in l.entrances)
                if (Poly2.SegDist(p, e.a, e.b) <= 1.0) return true;
            return false;
        }
    }
}
