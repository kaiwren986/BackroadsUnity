using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Canon.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Backroads.World;
using Backroads.World.Bake;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>The Millbrook bake (T3.2), made once per test run and shared by the World.* bake tests.</summary>
    static class BakeCache
    {
        static WorldBakeResult s_Bake;

        public static WorldBakeResult Bake()
        {
            if (s_Bake != null) return s_Bake;
            var log = new List<string>();
            s_Bake = WorldBake.Run(new WorldBakeOptions { useBurst = true, log = log.Add });
            foreach (var l in log) TestContext.WriteLine(l);
            return s_Bake;
        }

        public static string Dir => WorldDataExport.WorldDir("millbrook");
    }

    /// <summary>
    /// T3.2 done-when gates on the Millbrook bake: World.BakeDeterministic, World.ManifestHashes, World.RiverLevels,
    /// World.DemFidelity, plus World.PlatesMatchRoadReference (lane R's reference evaluation of its plates at the grid's
    /// 0.5 m spacing, as the R2 handoff asked).
    /// </summary>
    public partial class World
    {
        /// <summary>
        /// Two bakes, one with the Burst kernels and one managed, give the same bytes for every exported file and the same
        /// plate list hash; both equal the committed manifest's hashes (so a bake in any fresh worktree reproduces them).
        /// </summary>
        [Test]
        public void BakeDeterministic()
        {
            var a = BakeCache.Bake();
            Assert.IsTrue(a.applied.ranBurst, "the Burst plate kernel ran managed code");
            var ha = WorldDataExport.Hashes(WorldDataExport.Files(a));
            SortedDictionary<string, string> hb;
            string platesB;
            {
                var b = WorldBake.Run(new WorldBakeOptions { useBurst = false });
                hb = WorldDataExport.Hashes(WorldDataExport.Files(b));
                platesB = b.platesHash;
            }
            GC.Collect();
            foreach (var kv in ha) TestContext.WriteLine($"{kv.Key} {kv.Value}");
            Assert.AreEqual(a.platesHash, platesB, "plate list hash");
            var diff = ha.Where(kv => hb[kv.Key] != kv.Value).Select(kv => kv.Key).ToList();
            Assert.IsEmpty(diff, "Burst and managed bakes differ in: " + string.Join(", ", diff));
            var committed = WorldBakeCli.CommittedHashes(BakeCache.Dir);
            Assert.IsNotEmpty(committed, "no committed WorldData/millbrook/v1/manifest.json");
            var stale = ha.Where(kv => !committed.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).ToList();
            Assert.IsEmpty(stale, "the committed WorldData differs from a fresh bake (re-run WorldBakeCli.Export): " + string.Join(", ", stale));
        }

        /// <summary>
        /// The committed manifest lists every exported file with its size and SHA-256, and they match the files; its grid
        /// block is the new Millbrook's layout; it names the TownData manifest it was baked from by hash.
        /// </summary>
        [Test]
        public void ManifestHashes()
        {
            var dir = BakeCache.Dir;
            var mPath = Path.Combine(dir, "manifest.json");
            Assert.IsTrue(File.Exists(mPath), "missing " + mPath);
            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(mPath)), "manifest");
            Assert.AreEqual(WorldDataExport.Format, CanonJson.Str(m, "format"));
            Assert.AreEqual(WorldGridLayout.Millbrook, WorldGridLayout.FromManifestFile(mPath), "grid block");
            var files = CanonJson.Obj(CanonJson.Get(m, "files"), "files");
            var want = WorldDataExport.OtherFiles.Concat(WorldDataExport.LayerFiles).OrderBy(s => s, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(want, files.Keys.OrderBy(s => s, StringComparer.Ordinal).ToList(), "file list");
            var fails = new List<string>();
            foreach (var kv in files)
            {
                var f = CanonJson.Obj(kv.Value);
                var path = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { fails.Add("missing " + kv.Key); continue; }
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length != (long)CanonJson.Num(f, "bytes")) fails.Add($"{kv.Key}: {bytes.Length} bytes, manifest {CanonJson.Num(f, "bytes")}");
                if (WorldDataExport.Sha256(bytes) != CanonJson.Str(f, "sha256")) fails.Add($"{kv.Key}: SHA-256 differs");
                if (Array.IndexOf(bytes, (byte)'\r') >= 0 && kv.Key.EndsWith(".json")) fails.Add(kv.Key + " has CR bytes");
            }
            var town = CanonJson.Obj(CanonJson.Get(m, "town"), "town");
            Assert.AreEqual(TownSource.Sha256File(Path.Combine(TownSource.TownDir(TownSource.DefaultTownId()), "manifest.json")), CanonJson.Str(town, "manifestSha256"), "TownData manifest hash");
            Assert.IsFalse(File.ReadAllText(mPath).Contains(TownSource.DefaultTownId()), "the manifest names the real town");
            var L = WorldGridLayout.Millbrook;
            Assert.AreEqual((long)L.Count * 4, new FileInfo(Path.Combine(dir, "terrain", "heights.f32")).Length);
            Assert.AreEqual((long)L.TintN * L.TintN * 8, new FileInfo(Path.Combine(dir, "terrain", "tint.f32")).Length);
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>
        /// Each reach has one flat water level (the committed water.json equals the bake), and inside every river polygon,
        /// away from plates and the weir and lock, the ground is either water (at least 0.1 m under the surface) or dry land
        /// at least 0.25 m over it: nothing sits awash at the water line. Beside the river (within 6 m, outside the polygons),
        /// no ground lies in the 0.3 m just under the nearest reach's level (no dry land below the water).
        /// </summary>
        [Test]
        public void RiverLevels()
        {
            var r = BakeCache.Bake();
            var g = r.grid;
            var water = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(BakeCache.Dir, "water.json"))), "water.json");
            var levels = CanonJson.Arr(CanonJson.Get(water, "reaches")).Select(o => CanonJson.Obj(o)).ToDictionary(o => CanonJson.Str(o, "id"), o => CanonJson.Num(o, "level"));
            var reaches = r.river.reaches.Where(x => x.id != "lock").ToList();
            Assert.AreEqual(2, reaches.Count, "two river reaches (above and below the dam)");
            foreach (var reach in reaches)
            {
                Assert.IsTrue(levels.ContainsKey(reach.id), "water.json lacks " + reach.id);
                Assert.AreEqual(reach.level, levels[reach.id], 1e-9, reach.id + " level");
                var src = r.town.reaches.First(x => x.osm == reach.id);
                TestContext.WriteLine($"{reach.id}: level {reach.level:F3} (lidar water p05 {src.p05:F3}, p95 {src.p95:F3}); {reach.wetVertices} carved vertices");
                Assert.Greater(reach.wetVertices, 100000, reach.id + " carved little");
            }
            Assert.Greater(reaches.Max(x => x.level) - reaches.Min(x => x.level), 2.5, "the dam's drop");
            int awash = 0, below = 0, checkedIn = 0, checkedOut = 0;
            var fails = new List<string>();
            for (int k = 0; k < g.Count; k++)
            {
                if (r.applied.touched[k] != 0 || r.river.structure[k] != 0) continue;
                int i = k % g.VN, j = k / g.VN;
                if (r.river.inPolygon[k])
                {
                    int lab = r.river.wet[k];
                    double level = lab != 0 ? r.river.reaches[lab - 1].level : NearestLevel(r, k);
                    double h = g.heights[k];
                    checkedIn++;
                    if (h > level - 0.1 && h < level + 0.25) { awash++; if (fails.Count < 10) fails.Add($"awash at {g.Local(i, j)}: {h:F3} vs level {level:F3}"); }
                }
                else if (r.river.toWater[k] <= 6 && r.river.nearestReach[k] > 0)
                {
                    double level = r.river.reaches[r.river.nearestReach[k] - 1].level;
                    double h = g.heights[k];
                    checkedOut++;
                    if (h < level && h >= level - 0.3) { below++; if (fails.Count < 10) fails.Add($"dry below the water at {g.Local(i, j)}: {h:F3} vs level {level:F3}"); }
                }
            }
            TestContext.WriteLine($"RiverLevels: {checkedIn} vertices in the river polygons, {awash} awash; {checkedOut} beside them, {below} just below the level; weir crest {r.river.weirCrest:F3}, lock level {r.river.lockLevel:F3}");
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>A plate's surface at q, extended past its edge: the plane, or the closest fan triangle's plane.</summary>
        static double Extend(Plate pl, double2 q)
        {
            if (pl.heights != PlateHeights.PerVertex) { var pp = PlateSink.PlaneOf(pl); return pp.x * q.x + pp.y * q.y + pp.z; }
            double bestM = double.MinValue, bestY = 0;
            foreach (var t in PlateSink.Triangulate(pl.polygon))
            {
                double2 a = pl.polygon[t.x], b = pl.polygon[t.y], c = pl.polygon[t.z];
                double2 v0 = b - a, v1 = c - a, v2 = q - a;
                double den = v0.x * v1.y - v1.x * v0.y;
                if (Math.Abs(den) < 1e-14) continue;
                double u = (v2.x * v1.y - v1.x * v2.y) / den, v = (v0.x * v2.y - v2.x * v0.y) / den;
                double m = Math.Min(Math.Min(u, v), 1 - u - v);
                if (m > bestM) { bestM = m; bestY = pl.vertexY[t.x] + u * (pl.vertexY[t.y] - pl.vertexY[t.x]) + v * (pl.vertexY[t.z] - pl.vertexY[t.x]); }
            }
            return bestY;
        }

        static double NearestLevel(WorldBakeResult r, int k) =>
            r.river.nearestReach != null && r.river.nearestReach[k] > 0 ? r.river.reaches[r.river.nearestReach[k] - 1].level : r.river.reaches[0].level;

        /// <summary>
        /// Outside roads, lots, pads (every plate and blend band), the river (and 10 m around it) and the weir and lock,
        /// the baked heights stay within 0.10 m of the lidar at 1,000 seeded points (grid vertices of the play area).
        /// </summary>
        [Test]
        public void DemFidelity()
        {
            var r = BakeCache.Bake();
            var g = r.grid;
            var rng = new Unity.Mathematics.Random(7331);
            int n = 0, tries = 0;
            double max = 0, sum = 0;
            var fails = new List<string>();
            while (n < 1000 && tries++ < 200000)
            {
                int i = rng.NextInt(0, g.VN), j = rng.NextInt(0, g.VN);
                int k = j * g.VN + i;
                if (r.applied.touched[k] != 0 || r.river.inPolygon[k] || r.river.structure[k] != 0 || r.river.toWater[k] <= 10) continue;
                var p = g.Local(i, j);
                double lidar = r.town.dem.Bilinear(p.x, p.y);
                double d = Math.Abs(g.heights[k] - lidar);
                max = Math.Max(max, d); sum += d; n++;
                if (d > 0.10 && fails.Count < 10) fails.Add($"{p}: {g.heights[k]:F3} vs lidar {lidar:F3}");
            }
            TestContext.WriteLine($"DemFidelity: {n} points, max |dh| {max:F4} m, mean {sum / Math.Max(1, n):F4} m");
            Assert.AreEqual(1000, n, "not enough free points");
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>
        /// Lane R's reference evaluation of its plates (PlateField: the winner by ADR-007's order, the fan triangulation)
        /// agrees with the applied grid at every vertex a road-stage plate set: within 2 mm at 99.5 % of them (the rest sit
        /// on the edge between two plates).
        /// </summary>
        [Test]
        public void PlatesMatchRoadReference()
        {
            var r = BakeCache.Bake();
            var g = r.grid;
            var roadPlates = r.applied.sorted.Where(p => p.Stage == PlateStage.Roads).ToList();
            var field = new PlateField(roadPlates);
            int n = 0, close = 0, none = 0;
            double max = 0;
            for (int k = 0; k < g.Count; k++)
            {
                int o = r.applied.owner[k];
                if (o < 0 || r.applied.sorted[o].Stage != PlateStage.Roads) continue;
                var p = g.Local(k % g.VN, k / g.VN);
                if (!field.Sample(new D2(p.x, p.y), out double y, out _)) { none++; continue; }
                double d = Math.Abs(g.heights[k] - y);
                n++;
                if (d <= 2e-3) close++;
                max = Math.Max(max, d);
            }
            TestContext.WriteLine($"PlatesMatchRoadReference: {n} road vertices compared, {close} within 2 mm, max {max:F4} m; {none} outside every plate by the reference (edge points)");
            Assert.Greater(n, 500000);
            Assert.GreaterOrEqual(close, n * 0.995);
        }

        /// <summary>
        /// The grid check of the R2 handoff at the grid's 0.5 m spacing: at 200,000 seeded points inside grid cells whose
        /// four corners lie on paved, no-deform plates (asphalt and concrete pavement, walks, aprons), the ground as the game
        /// reads it (bilinear between the corners) rises at most 0.03 m over the plates' own target (which is 5-6 cm under
        /// the paving meshes), so the terrain never pokes through a paved surface. The only exceptions allowed (at most
        /// 0.1 % of the points) are cells across a height step between two paved plates, where a corner lies off the
        /// winning plate's own surface by more than 3 cm and lane R's meshes step too; they are listed.
        /// </summary>
        [Test]
        public void PavementCoversTerrain()
        {
            var r = BakeCache.Bake();
            var g = r.grid;
            var field = new PlateField(r.applied.sorted.Where(p => p.Stage == PlateStage.Roads));
            bool Paved(int k) { int o = r.applied.owner[k]; return o >= 0 && r.applied.sorted[o].noDeform && r.applied.sorted[o].Stage == PlateStage.Roads; }
            var rng = new Unity.Mathematics.Random(4242);
            int n = 0, overAtStep = 0, tries = 0;
            double maxAll = double.MinValue;
            var fails = new List<string>();
            var stepAt = new List<string>();
            while (n < 200000 && tries++ < 20000000)
            {
                int i = rng.NextInt(0, g.N), j = rng.NextInt(0, g.N);
                int k = j * g.VN + i;
                if (!Paved(k) || !Paved(k + 1) || !Paved(k + g.VN) || !Paved(k + g.VN + 1)) continue;
                double fi = i + rng.NextDouble(), fj = j + rng.NextDouble();
                var p = new double2(g.Half - fi * g.Cell, fj * g.Cell - g.Half);
                if (!field.Sample(new D2(p.x, p.y), out double target, out var winner) || !winner.noDeform) continue;
                double rise = g.Sample(g.heights, p.x, p.y) - target;
                n++;
                maxAll = Math.Max(maxAll, rise);
                if (rise <= 0.03) continue;
                double step = 0;
                foreach (int c in new[] { k, k + 1, k + g.VN, k + g.VN + 1 })
                    step = Math.Max(step, Math.Abs(g.heights[c] - Extend(winner, g.Local(c % g.VN, c / g.VN))));
                if (step > 0.03)
                {
                    overAtStep++;
                    if (stepAt.Count < 10) stepAt.Add($"{p} ({winner.id}: rise {rise:F3} m, corner off its surface by {step:F3} m)");
                }
                else if (fails.Count < 10) fails.Add($"{p}: ground over {winner.id}'s target by {rise:F3} m in a cell without a step");
            }
            TestContext.WriteLine($"PavementCoversTerrain: {n} points in fully paved cells; largest rise over the plate target {maxAll:F4} m; " +
                                  $"{overAtStep} over 3 cm, all across a step between paved plates:\n  " + string.Join("\n  ", stepAt));
            Assert.Greater(n, 100000);
            Assert.IsEmpty(fails, string.Join("\n", fails));
            Assert.LessOrEqual(overAtStep, n / 1000, "more than 0.1 % of the points rise over 3 cm at steps");
        }
    }

    /// <summary>The plate applier on small synthetic grids (fast): ADR-007's order, blend bands, stages, the plane fit, Burst vs managed.</summary>
    [TestFixture, Category("World")]
    public class WorldPlates
    {
        static WorldGridLayout Small => WorldGridLayout.Create(129, 0.5f, 32f, 9, 8f);

        static BakeGrid Flat(float h = 10f)
        {
            var g = new BakeGrid(Small);
            for (int k = 0; k < g.Count; k++) { g.heights[k] = h; g.@base[k] = h; }
            return g;
        }

        static Plate Square(string id, double cx, double cz, double half, double y, PlateKind kind = PlateKind.Road, int priority = 0, float blend = 3f, byte surface = 4)
            => new Plate
            {
                id = id, kind = kind, polygon = new[] { new double2(cx - half, cz - half), new double2(cx + half, cz - half), new double2(cx + half, cz + half), new double2(cx - half, cz + half) },
                surface = surface, heights = PlateHeights.Plane, plane = new double3(0, 0, y), blend = blend, priority = priority,
            };

        static double At(BakeGrid g, double x, double z) => g.heights[g.K((int)Math.Round(g.GridI(x)), (int)Math.Round(g.GridJ(z)))];

        [Test]
        public void PlaneAndLinearBlend()
        {
            var g = Flat(10);
            var s = new PlateSink();
            s.Submit(Square("a", 0, 0, 4, 13, blend: 6));
            var res = s.Apply(g, true);
            Assert.AreEqual(13f, At(g, 0, 0));
            Assert.AreEqual(13f, At(g, 3.5, -3.5));
            // outside: at distance d from the edge x = 4, h = 13 + (10 - 13) d / 6
            foreach (var d in new[] { 0.5, 1.5, 3.0, 5.5 })
                Assert.AreEqual(13 - 3 * d / 6, At(g, 4 + d, 0), 1e-5, "blend at " + d);
            Assert.AreEqual(10f, At(g, 4 + 6.5, 0), "beyond the band");
            Assert.AreEqual(10f, At(g, 15, 15), "far away");
            Assert.AreEqual(4, g.surface[g.K((int)Math.Round(g.GridI(0)), (int)Math.Round(g.GridJ(0)))], "surface");
            Assert.Greater(res.blendedByStage[0], 0);
        }

        [Test]
        public void OrderIndependentAndHashed()
        {
            var plates = new List<Plate>();
            var rng = new Unity.Mathematics.Random(5);
            for (int n = 0; n < 40; n++)
                plates.Add(Square("p" + n.ToString("D2"), rng.NextDouble(-25, 25), rng.NextDouble(-25, 25), rng.NextDouble(0.6, 5), rng.NextDouble(8, 14),
                                  (PlateKind)rng.NextInt(0, 5), rng.NextInt(0, 4), (float)rng.NextDouble(0, 6), (byte)rng.NextInt(0, 9)));
            byte[] Run(IEnumerable<Plate> order, out string hash)
            {
                var g = Flat();
                var s = new PlateSink();
                foreach (var p in order) s.Submit(p);
                hash = s.Hash();
                s.Apply(g, true);
                return WorldDataExport.Bytes(g.heights).Concat(g.surface).ToArray();
            }
            var a = Run(plates, out var ha);
            var shuffled = plates.OrderBy(p => p.id.GetHashCode() ^ 0x5bd1e995).ToList();
            var b = Run(Enumerable.Reverse(plates), out var hb);
            var c = Run(shuffled, out var hc);
            Assert.AreEqual(ha, hb); Assert.AreEqual(ha, hc);
            CollectionAssert.AreEqual(a, b, "reversed submission");
            CollectionAssert.AreEqual(a, c, "shuffled submission");
        }

        [Test]
        public void PriorityStagesAndProtection()
        {
            var g = Flat(10);
            var s = new PlateSink();
            s.Submit(Square("road:low", 0, 0, 6, 11, PlateKind.Road, 0, 4));
            s.Submit(Square("road:high", 3, 0, 2, 12, PlateKind.Road, 5, 4));
            s.Submit(Square("lot:a", -14, 0, 4, 9, PlateKind.Lot, 99, 6));    // a later stage: its band must not touch the road plates
            s.Submit(Square("pad:b", 0, 0, 1, 15, PlateKind.Pad, 0, 2));     // Buildings stage beats Roads inside
            s.Apply(g, true);
            Assert.AreEqual(15f, At(g, 0, 0), "pad over the roads");
            Assert.AreEqual(12f, At(g, 3.5, 0), "higher priority wins within the stage");
            Assert.AreEqual(11f, At(g, -5, 0), "the lower plate where it is alone");
            Assert.AreEqual(11f, At(g, -5.5, 4), "a road interior is protected from the lot's band");
            Assert.AreEqual(9f, At(g, -14, 0), "lot");
            // between the lot (edge x = -10) and the road (edge x = -6): the lot's band eases toward the road-stage result
            double h = At(g, -8, 0);
            Assert.Greater(h, 9.0); Assert.Less(h, 11.0);
        }

        [Test]
        public void LeastSquaresClampedAndPerVertexConcave()
        {
            var g = Flat(0);
            var s = new PlateSink();
            var fit = new List<double3>();
            for (int a = -3; a <= 3; a++) for (int b = -3; b <= 3; b++) fit.Add(new double3(a - 15, 5 + 0.2 * a, b));
            var ls = Square("lot:ls", -15, 0, 4, 0, PlateKind.Lot);
            ls.heights = PlateHeights.LeastSquares; ls.fitPoints = fit.ToArray(); ls.maxSlope = 0.05f; ls.blend = 0;
            s.Submit(ls);
            var poly = new[] { new double2(5, -5), new double2(15, -5), new double2(15, 5), new double2(11, 5), new double2(11, -1), new double2(5, -1) }; // an L
            s.Submit(new Plate { id = "road:L", kind = PlateKind.Road, polygon = poly, surface = 6, heights = PlateHeights.PerVertex, vertexY = new[] { 1f, 2f, 3f, 4f, 3f, 2f }, blend = 0 });
            s.Apply(g, true);
            Assert.AreEqual(5.0, At(g, -15, 0), 1e-5, "the fitted plane through the centroid");
            Assert.AreEqual(5.0 + 0.05 * 3, At(g, -12, 0), 1e-5, "slope clamped to 5 %");
            Assert.AreEqual(1.0, At(g, 5.5, -4.5), 0.25, "near vertex 0");
            Assert.AreEqual(3.0, At(g, 14.5, 4.5), 0.3, "near vertex 2");
            Assert.AreEqual(0f, At(g, 8, 2), "the L's notch stays outside");
            var tris = PlateSink.Triangulate(poly);
            Assert.AreEqual(4, tris.Count, "an L hexagon is four triangles");
        }

        [Test]
        public void BurstMatchesManaged()
        {
            var rng = new Unity.Mathematics.Random(77);
            var plates = new List<Plate>();
            for (int n = 0; n < 60; n++)
            {
                var c = new double2(rng.NextDouble(-28, 28), rng.NextDouble(-28, 28));
                int m = rng.NextInt(3, 7);
                var poly = new double2[m];
                var ys = new float[m];
                for (int q = 0; q < m; q++)
                {
                    double ang = 2 * Math.PI * q / m + rng.NextDouble(0, 0.4);
                    poly[q] = c + new double2(Math.Cos(ang), Math.Sin(ang)) * rng.NextDouble(0.8, 4);
                    ys[q] = (float)rng.NextDouble(8, 12);
                }
                plates.Add(new Plate
                {
                    id = "x" + n.ToString("D2"), kind = (PlateKind)rng.NextInt(0, 5), polygon = poly, surface = (byte)rng.NextInt(0, 9),
                    heights = n % 2 == 0 ? PlateHeights.PerVertex : PlateHeights.Plane, vertexY = ys,
                    plane = new double3(rng.NextDouble(-0.1, 0.1), rng.NextDouble(-0.1, 0.1), rng.NextDouble(8, 12)),
                    blend = (float)rng.NextDouble(0, 8), priority = rng.NextInt(0, 3),
                });
            }
            byte[] Run(bool burst, out bool ran)
            {
                var g = Flat();
                for (int k = 0; k < g.Count; k++) g.heights[k] = (float)(10 + 0.01 * (k % 97));
                var s = new PlateSink();
                foreach (var p in plates) s.Submit(p);
                var res = s.Apply(g, burst);
                ran = res.ranBurst;
                return WorldDataExport.Bytes(g.heights).Concat(g.surface).ToArray();
            }
            var a = Run(true, out bool ranA);
            var b = Run(false, out _);
            Assert.IsTrue(ranA, "the Burst path ran managed code");
            CollectionAssert.AreEqual(a, b);
        }

        [Test]
        public void RejectsBadPlates()
        {
            var s = new PlateSink();
            s.Submit(Square("a", 0, 0, 1, 1));
            Assert.Throws<ArgumentException>(() => s.Submit(Square("a", 5, 5, 1, 1)), "duplicate id");
            Assert.Throws<ArgumentException>(() => s.Submit(new Plate { id = "b", polygon = new[] { new double2(0, 0), new double2(1, 0) } }), "two points");
            Assert.Throws<ArgumentException>(() => s.Submit(new Plate { id = "c", polygon = Square("x", 0, 0, 1, 1).polygon, heights = PlateHeights.PerVertex, vertexY = new[] { 1f } }), "vertexY count");
        }
    }
}
