using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Build.Editor;
using Backroads.Canon.Editor;
using Unity.Mathematics;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3 step 3 (ADR-006 W6, first nature): trees, hedgerows, reeds and rocks scattered over the baked ground, written
    /// in CanonData v1's vegetation.json format so the M1 renderer draws them with the canon's species (Pine, Spruce, Oak,
    /// Birch, Poplar, Maple, Bush, Snag, Reeds). Deterministic: every candidate comes from a jittered lattice and hashed
    /// numbers, no random state. Nothing stands on a plate or its band (roads, walks, verges, pads), within 3 m of a
    /// carriageway, in the water, on the dam or lock, or on gravel.
    /// - Forest (7 m lattice): the mapped wood (85 %) and the hollows' steep sides (slope 22-45 %, up to 80 %), thinned
    ///   to 30 % within 15 m of a road; a mixed hardwood stand (oak, maple, poplar, birch, some pine and spruce, snags).
    /// - Riparian (7 m): 4-25 m from the river, poplar, birch and maple at 45 %.
    /// - Meadow (35 m): lone oaks, maples and pines on flat grass away from roads.
    /// - Hedgerows: bushes every 2.6 m, 1.2 m outside every field edge, a tree every tenth.
    /// - Reeds (2.4 m): the marsh where the mud is deep, and the river's muddy banks; snags in the marsh.
    /// - Rocks (20 m) on rock faces.
    /// </summary>
    public sealed class NatureResult
    {
        public struct Tree { public double x, y, z, s, rot, tint, hue; public int sp; }
        public struct Rock { public double x, y, z, s, rot, v; }

        public readonly List<Tree> trees = new List<Tree>();
        public readonly List<Rock> rocks = new List<Rock>();
        public readonly SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        /// <summary>The species table as CanonData v1 has it (name, h, trunk, strength, s, sway), in its order.</summary>
        public List<Dictionary<string, object>> species = new List<Dictionary<string, object>>();
        public int Species(string name) => species.FindIndex(s => CanonJson.Str(s, "name") == name);
    }

    public static class NatureStep
    {
        static double U(uint seed, int a, int b)
        {
            double h = (DemKernel.Hash(seed, a, b) + 1.0) * 0.5;
            return h < 0 ? 0 : h >= 1 ? 0.9999999 : h;
        }

        static double Smooth(double a, double b, double x)
        {
            double t = (x - a) / (b - a);
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        public static NatureResult Run(BakeGrid g, TownSource t, RiverResult river, SurfaceStep.Result surf, PlateSink.Result plates, float[] natural, uint seed)
        {
            var res = new NatureResult();
            var vj = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(RepoPaths.InRepo("CanonData/v1/vegetation.json"))), "vegetation.json");
            foreach (var so in CanonJson.Arr(CanonJson.Get(vj, "species"))) res.species.Add(CanonJson.Obj(so));
            int Pine = res.Species("Pine"), Spruce = res.Species("Spruce"), Oak = res.Species("Oak"), Birch = res.Species("Birch"),
                Poplar = res.Species("Poplar"), Maple = res.Species("Maple"), Bush = res.Species("Bush"), Snag = res.Species("Snag"), Reeds = res.Species("Reeds");

            var wood = new bool[g.Count];
            var farm = new bool[g.Count];
            var fieldEdges = new List<double2[]>();
            foreach (var a in t.landuse)
            {
                if (a.Tag("natural") == "wood" || a.Tag("landuse") == "forest") foreach (var p in a.polys) Raster.Fill(g, p, k => wood[k] = true);
                if (a.Tag("landuse") == "farmland") foreach (var p in a.polys) { Raster.Fill(g, p, k => farm[k] = true); fieldEdges.Add(p.outer); }
            }

            bool Free(int k) =>
                (plates.touched == null || plates.touched[k] == 0) && river.wet[k] == 0 && river.structure[k] == 0 &&
                g.roadd[k] > 6 && g.surface[k] != 4 && g.surface[k] != 5 && g.surface[k] != 6;
            int K(double x, double z)
            {
                int i = (int)Math.Round(g.GridI(x)), j = (int)Math.Round(g.GridJ(z));
                i = Math.Max(0, Math.Min(g.N, i)); j = Math.Max(0, Math.Min(g.N, j));
                return j * g.VN + i;
            }
            void Add(string what, int sp, double x, double z, uint s, int cx, int cz)
            {
                var spec = res.species[sp];
                var range = CanonJson.Doubles(CanonJson.Get(spec, "s"));
                res.trees.Add(new NatureResult.Tree
                {
                    x = x, z = z, y = g.Sample(g.heights, x, z), sp = sp,
                    s = range[0] + (range[1] - range[0]) * U(s + 3, cx, cz), rot = U(s + 4, cx, cz) * 2 * Math.PI,
                    tint = 0.9 + 0.2 * U(s + 5, cx, cz), hue = U(s + 6, cx, cz),
                });
                res.counts[what] = res.counts.TryGetValue(what, out var n) ? n + 1 : 1;
            }
            int Pick(double u, (int sp, double w)[] mix)
            {
                double acc = 0;
                foreach (var m in mix) { acc += m.w; if (u < acc) return m.sp; }
                return mix[mix.Length - 1].sp;
            }
            void Lattice(double cell, uint salt, Action<double, double, int, int, uint> visit)
            {
                int n = (int)Math.Ceiling(2 * g.Half / cell);
                for (int cz = 0; cz < n; cz++)
                    for (int cx = 0; cx < n; cx++)
                    {
                        double x = -g.Half + (cx + U(salt, cx, cz)) * cell, z = -g.Half + (cz + U(salt + 1, cx, cz)) * cell;
                        if (Math.Abs(x) > g.Half - 1 || Math.Abs(z) > g.Half - 1) continue;
                        visit(x, z, cx, cz, salt);
                    }
            }

            var forestMix = new[] { (Oak, 0.32), (Maple, 0.28), (Poplar, 0.14), (Birch, 0.10), (Pine, 0.08), (Spruce, 0.05), (Snag, 0.03) };
            var riverMix = new[] { (Poplar, 0.4), (Birch, 0.3), (Maple, 0.2), (Oak, 0.1) };
            var meadowMix = new[] { (Oak, 0.5), (Maple, 0.3), (Pine, 0.2) };

            // forest and riparian trees
            Lattice(7.0, seed ^ 0xF0E57u, (x, z, cx, cz, s) =>
            {
                int k = K(x, z);
                if (!Free(k) || farm[k] || surf.marshMask[k]) return;
                double slope = SurfaceStep.Slope(g, natural, k);
                double d = wood[k] ? 0.85 : 0.8 * Smooth(0.22, 0.45, slope);
                if (g.roadd[k] < 30) d *= 0.3;
                double toWater = river.toWater[k];
                bool riparian = toWater >= 4 && toWater <= 25;
                if (riparian && d < 0.45) d = 0.45;
                if (slope > 1.2) d *= 0.3;
                if (U(s + 2, cx, cz) >= d) return;
                bool isRiver = riparian && !wood[k] && slope < 0.22;
                Add(isRiver ? "riparian" : wood[k] ? "wood" : "hillside", Pick(U(s + 7, cx, cz), isRiver ? riverMix : forestMix), x, z, s, cx, cz);
            });
            // lone meadow trees
            Lattice(35.0, seed ^ 0x3EAD0u, (x, z, cx, cz, s) =>
            {
                int k = K(x, z);
                if (!Free(k) || farm[k] || wood[k] || g.surface[k] != 0 || g.roadd[k] < 16) return;
                if (SurfaceStep.Slope(g, natural, k) > 0.22 || U(s + 2, cx, cz) >= 0.35) return;
                Add("meadow", Pick(U(s + 7, cx, cz), meadowMix), x, z, s, cx, cz);
            });
            // hedgerows along the fields, 1.2 m outside their edges
            uint hs = seed ^ 0x4ED6Eu;
            for (int f = 0; f < fieldEdges.Count; f++)
            {
                var ring = fieldEdges[f];
                double area = 0;
                for (int a = 0, b = ring.Length - 1; a < ring.Length; b = a++) area += ring[b].x * ring[a].y - ring[a].x * ring[b].y;
                double outSign = area >= 0 ? 1 : -1;   // CCW: outward is to the right of each edge
                int idx = 0;
                for (int e = 0; e < ring.Length; e++)
                {
                    var p0 = ring[e]; var p1 = ring[(e + 1) % ring.Length];
                    double len = math.length(p1 - p0);
                    if (len < 1e-6) continue;
                    var dir = (p1 - p0) / len;
                    var nrm = new double2(dir.y, -dir.x) * outSign;
                    for (double s = 1.3; s < len - 1.3; s += 2.6, idx++)
                    {
                        var q = p0 + dir * (s + 0.8 * (U(hs, f, idx) - 0.5)) + nrm * (1.2 + 0.6 * (U(hs + 1, f, idx) - 0.5));
                        int k = K(q.x, q.y);
                        if (!Free(k) || farm[k]) continue;
                        bool tree = U(hs + 2, f, idx) < 0.1;
                        Add(tree ? "hedgerow tree" : "hedgerow", tree ? (U(hs + 7, f, idx) < 0.5 ? Oak : Maple) : Bush, q.x, q.y, hs, f, idx);
                    }
                }
            }
            // reeds in the marsh and on the muddy river banks; snags in the marsh
            Lattice(2.4, seed ^ 0x2EED5u, (x, z, cx, cz, s) =>
            {
                int k = K(x, z);
                if (!Free(k)) return;
                double d = surf.marshMask[k] && g.mud[k] >= 150 ? 0.55 : g.surface[k] == SurfaceStep.Mud && river.toWater[k] <= 3 ? 0.4 : 0;
                if (d <= 0 || U(s + 2, cx, cz) >= d) return;
                Add("reeds", Reeds, x, z, s, cx, cz);
            });
            Lattice(30.0, seed ^ 0x5A65u, (x, z, cx, cz, s) =>
            {
                int k = K(x, z);
                if (!Free(k) || !surf.marshMask[k] || U(s + 2, cx, cz) >= 0.3) return;
                Add("marsh snag", Snag, x, z, s, cx, cz);
            });
            // rocks on rock faces
            Lattice(20.0, seed ^ 0x20C45u, (x, z, cx, cz, s) =>
            {
                int k = K(x, z);
                if (!Free(k) || g.surface[k] != SurfaceStep.Rock || U(s + 2, cx, cz) >= 0.3) return;
                res.rocks.Add(new NatureResult.Rock
                {
                    x = x, z = z, y = g.Sample(g.heights, x, z), s = 0.6 + 1.6 * U(s + 3, cx, cz), rot = U(s + 4, cx, cz) * 2 * Math.PI,
                    v = 0.85 + 0.3 * U(s + 5, cx, cz),
                });
            });
            res.counts["rocks"] = res.rocks.Count;
            return res;
        }
    }

    /// <summary>
    /// T3.3 step 4 (W7, provisional: the game places are lane G's in M9): the places on real features, in CanonData v1's
    /// world.json "pois" form (id, name, kind, x, z, r, fall, y), with the canon's fictional names: the garage (the car
    /// repair shop; four parking bays in front of it, between its front and the street, facing it), the mill (the large
    /// building by the dam that lane A builds as the mill), the fuel station (the fuel canopy), the river landing (the
    /// slipway's end at the water), the farm (the largest field), the town (the commercial core), and the scout's added
    /// quarry, depot and marsh. Positions are rounded to the millimetre, yaws to the microradian.
    /// </summary>
    public sealed class PlacesResult
    {
        public struct Poi { public string id, name, kind; public double x, z, r, fall, y; }
        public struct Bay { public double x, z, yawJs, y; }
        public struct Camera { public string id, name; public double3 pos, target; public double fov; }
        public readonly List<Poi> pois = new List<Poi>();
        public readonly List<Camera> cameras = new List<Camera>();
        public readonly List<Bay> bays = new List<Bay>();
        public readonly List<string> notes = new List<string>();
    }

    public static class PlacesStep
    {
        /// <summary>
        /// The 12 fixed views of m3.shots.millbrook (T3.3): the village street, the mill and dam, the bridge, the landing,
        /// the fuel station, the quarry hollow, the marsh, an overview, the garage, the farm, the depot and the river from
        /// the hill. Each looks at its target from the direction (one of 16) with the clearest line of sight over the ground
        /// at its distance and height; the street view prefers a camera on the carriageway.
        /// </summary>
        public static void Cameras(BakeGrid g, TownSource t, RiverResult river, PlateSink.Result plates, IReadOnlyList<Backroads.Roads.BridgeDeck> decks, PlacesResult r)
        {
            double Ground(double2 p) => g.Sample(g.heights, p.x, p.y);
            bool InBuilding(double2 p)
            {
                foreach (var b in t.buildings) if (b.inPlay && math.lengthsq(b.centroid - p) < 900 && Raster.Inside(p, new[] { b.poly })) return true;
                return false;
            }
            void View(string id, string name, double2 target, double tUp, double dist, double cUp, bool onRoad = false, double fov = 55)
            {
                double ty = Ground(target) + tUp;
                double bestScore = double.MaxValue;
                double3 bestPos = default;
                for (int a = 0; a < 16; a++)
                {
                    var dir = Dir16(a);
                    var c = target + dir * dist;
                    if (Math.Abs(c.x) > g.Half - 5 || Math.Abs(c.y) > g.Half - 5) continue;
                    double cy = Ground(c) + cUp;
                    double blocked = 0;
                    for (int s = 1; s < 24; s++)
                    {
                        double f = s / 24.0;
                        var p = c + (target - c) * f;
                        double ly = cy + (ty - cy) * f;
                        blocked = Math.Max(blocked, Ground(p) + 0.5 - ly);
                    }
                    double score = Math.Max(0, blocked) * 10;
                    if (InBuilding(c)) score += 1000;
                    int k = (int)Math.Round(g.GridJ(c.y)) * g.VN + (int)Math.Round(g.GridI(c.x));
                    if (river.wet[k] != 0 && cUp < 6) score += 50;
                    if (onRoad && (plates.carriageway == null || plates.carriageway[k] == 0)) score += 20;
                    if (score < bestScore - 1e-9) { bestScore = score; bestPos = new double3(c.x, cy, c.y); }
                }
                r.cameras.Add(new PlacesResult.Camera
                {
                    id = id, name = name, fov = fov,
                    pos = new double3(Math.Round(bestPos.x, 3), Math.Round(bestPos.y, 3), Math.Round(bestPos.z, 3)),
                    target = new double3(Math.Round(target.x, 3), Math.Round(ty, 3), Math.Round(target.y, 3)),
                });
            }
            double2? Poi(string id)
            {
                foreach (var p in r.pois) if (p.id == id) return new double2(p.x, p.z);
                return null;
            }
            if (Poi("town") is double2 town)
            {
                View("street", "Village street", town, 3, 45, 1.8, onRoad: true);
                View("overview", "Overview", town, 0, 650, 230, fov: 50);
            }
            if (t.weirs.Count > 0)
            {
                var w = t.weirs[0];
                View("dam", "Mill and dam", (w[0] + w[w.Length - 1]) * 0.5, 4, 110, 10);
            }
            var deck = decks?.FirstOrDefault(d => d.outline.Length > 0);
            if (deck != null)
            {
                double2 c = 0;
                foreach (var p in deck.outline) c += new double2(p.x, p.z);
                c /= deck.outline.Length;
                View("bridge", "Bridge", c, deck.yMax - Ground(c) + 1, 120, 6);
            }
            if (Poi("landing") is double2 landing) View("landing", "River landing", landing, 2, 45, 4);
            if (Poi("fuel") is double2 fuel) View("fuel", "Fuel station", fuel, 3, 40, 4);
            if (Poi("quarry") is double2 quarry) View("quarry", "Quarry hollow", quarry, 2, 90, 14);
            if (Poi("bog") is double2 bog) View("marsh", "Marsh", bog, 1, 100, 12);
            if (Poi("garage") is double2 garage) View("garage", "Garage", garage, 3, 35, 3);
            if (Poi("farm") is double2 farm) View("farm", "Farm", farm, 2, 130, 18);
            if (Poi("depot") is double2 depot) View("depot", "Depot", depot, 2, 60, 8);
            // the river from the hill: the lower reach seen from 350 m away and 40 m up
            if (river.reaches.Count > 0)
            {
                var low = river.reaches.Where(x => x.id != "lock").OrderBy(x => x.level).First();
                var ring = low.polys[0].outer;
                double2 c = 0;
                int n = 0;
                foreach (var p in ring) if (Math.Abs(p.x) < g.Half - 50 && Math.Abs(p.y) < g.Half - 50) { c += p; n++; }
                if (n > 0) View("river", "River from the hill", c / n, 0, 350, 40);
            }
        }

        /// <summary>Unit directions at k x 22.5 degrees, from exact constants (no trigonometry in the bake's numbers).</summary>
        static double2 Dir16(int k)
        {
            const double S1 = 0.38268343236508978, C1 = 0.92387953251128674, H = 0.70710678118654752;
            double[] c = { 1, C1, H, S1, 0, -S1, -H, -C1, -1, -C1, -H, -S1, 0, S1, H, C1 };
            double[] s = { 0, S1, H, C1, 1, C1, H, S1, 0, -S1, -H, -C1, -1, -C1, -H, -S1 };
            return new double2(c[k & 15], s[k & 15]);
        }

        public static PlacesResult Run(BakeGrid g, TownSource t, RiverResult river, PlateSink.Result plates)
        {
            var r = new PlacesResult();
            void Poi(string id, string name, string kind, double2 p, double rad, double fall)
            {
                r.pois.Add(new PlacesResult.Poi
                {
                    id = id, name = name, kind = kind, x = Math.Round(p.x, 3), z = Math.Round(p.y, 3), r = rad, fall = fall,
                    y = Math.Round(g.Sample(g.heights, p.x, p.y), 3),
                });
            }
            BuildingFootprint First(Func<BuildingFootprint, bool> f)
            {
                foreach (var b in t.buildings) if (b.inPlay && f(b)) return b;
                return null;
            }

            // the garage and its bays
            var garage = First(b => b.business == "CarRepair");
            if (garage != null)
            {
                Poi("garage", "Millbrook Garage", "garage", garage.centroid, 30, 24);
                var road = NearestCarriageway(g, plates, garage.centroid, 80);
                if (road.HasValue)
                {
                    var d = math.normalize(road.Value - garage.centroid);
                    double front = RayToEdge(garage.poly, garage.centroid, d);
                    var row = garage.centroid + d * (front + 4.0);
                    var side = new double2(-d.y, d.x);
                    double yawU = Math.Atan2(-d.x, -d.y);   // a car facing the building: forward = -d, Unity yaw from +z toward +x
                    for (int q = 0; q < 4; q++)
                    {
                        var p = row + side * ((q - 1.5) * 2.8);
                        r.bays.Add(new PlacesResult.Bay
                        {
                            x = Math.Round(p.x, 3), z = Math.Round(p.y, 3), yawJs = Math.Round(-yawU, 6), y = Math.Round(g.Sample(g.heights, p.x, p.y), 3),
                        });
                    }
                }
                else r.notes.Add("garage: no carriageway within 80 m, no bays");
            }
            else r.notes.Add("no car repair building: no garage");

            // the mill: the largest non-house building within 40 m of a weir
            BuildingFootprint mill = null;
            foreach (var b in t.buildings)
            {
                if (!b.inPlay || b.type == "house" || b.area < 300) continue;
                bool near = false;
                foreach (var w in t.weirs) if (Raster.DistanceToPolyline(b.centroid, w) <= 40 + Math.Sqrt(b.area)) near = true;
                if (near && (mill == null || b.area > mill.area)) mill = b;
            }
            if (mill != null) Poi("mill", "Pine Hollow Mill", "mill", mill.centroid, 44, 32); else r.notes.Add("no mill building by the weir");

            var fuel = First(b => b.business == "Fuel");
            if (fuel != null) Poi("fuel", "Route 9 Fuel", "fuel", fuel.centroid, 30, 24); else r.notes.Add("no fuel building");

            // the river landing: the slipway's point nearest the water
            var slip = t.serviceWays.Find(s => s.Tag("leisure") == "slipway" || s.Tag("service") == "slipway");
            if (slip != null)
            {
                double2 best = slip.pts[0];
                float bestD = float.MaxValue;
                foreach (var p in slip.pts)
                {
                    int k = Nearest(g, p);
                    float dw = river.wet[k] != 0 ? 0 : river.toWater[k];
                    if (dw < bestD) { bestD = dw; best = p; }
                }
                Poi("landing", "River Landing", "landing", best, 34, 26);
            }
            else r.notes.Add("no slipway: no river landing");

            // the farm: the largest field
            LanduseArea field = null;
            double fieldArea = 0;
            foreach (var a in t.landuse)
            {
                if (a.Tag("landuse") != "farmland") continue;
                foreach (var p in a.polys)
                {
                    double ar = Math.Abs(Area(p.outer));
                    if (ar > fieldArea) { fieldArea = ar; field = a; }
                }
            }
            if (field != null)
            {
                var c = Centroid(field.polys[0].outer);
                if (InPlay(g, c)) Poi("farm", "Harlow Farm", "farm", c, 44, 36);
            }

            // the town: the centre of the commercial buildings
            double2 sum = 0; int n = 0;
            foreach (var b in t.buildings)
                if (b.inPlay && (b.business == "Retail" || b.business == "Restaurant" || b.business == "Office" || b.business == "Civic")) { sum += b.centroid; n++; }
            if (n > 0) Poi("town", t.townName ?? "Millbrook", "town", sum / n, 80, 110);

            // the scout's added places
            var quarry = t.Site("quarry");
            if (quarry != null) Poi("quarry", "Greystone Quarry", "quarry", quarry.local, 52, 34);
            var depot = t.Site("depot");
            if (depot != null) Poi("depot", "Freight Depot", "depot", depot.local, 46, 34);
            var marsh = t.Site("wetland");
            if (marsh != null) Poi("bog", "Blackwater Bog", "bog", marsh.local, 60, 40);
            return r;
        }

        static bool InPlay(BakeGrid g, double2 p) => Math.Abs(p.x) < g.Half - 2 && Math.Abs(p.y) < g.Half - 2;

        static int Nearest(BakeGrid g, double2 p)
        {
            int i = (int)Math.Round(g.GridI(p.x)), j = (int)Math.Round(g.GridJ(p.y));
            i = Math.Max(0, Math.Min(g.N, i)); j = Math.Max(0, Math.Min(g.N, j));
            return j * g.VN + i;
        }

        static double2? NearestCarriageway(BakeGrid g, PlateSink.Result plates, double2 c, double radius)
        {
            g.Box(c.x, c.y, c.x, c.y, radius, out int i0, out int j0, out int i1, out int j1);
            double best = double.MaxValue;
            double2? at = null;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if (plates.carriageway[j * g.VN + i] == 0) continue;
                    var p = g.Local(i, j);
                    double d = math.lengthsq(p - c);
                    if (d < best) { best = d; at = p; }
                }
            return at;
        }

        /// <summary>The distance from c along dir to the polygon's boundary (the farthest crossing; 0 if none).</summary>
        static double RayToEdge(double2[] poly, double2 c, double2 dir)
        {
            double best = 0;
            for (int a = 0, b = poly.Length - 1; a < poly.Length; b = a++)
            {
                var p = poly[b]; var e = poly[a] - poly[b];
                double den = dir.x * e.y - dir.y * e.x;
                if (Math.Abs(den) < 1e-12) continue;
                var w = p - c;
                double t = (w.x * e.y - w.y * e.x) / den, u = (w.x * dir.y - w.y * dir.x) / den;
                if (t > 0 && u >= 0 && u <= 1) best = Math.Max(best, t);
            }
            return best;
        }

        static double Area(double2[] p)
        {
            double a = 0;
            for (int k = 0, l = p.Length - 1; k < p.Length; l = k++) a += p[l].x * p[k].y - p[k].x * p[l].y;
            return a / 2;
        }

        static double2 Centroid(double2[] p)
        {
            double a = 0, cx = 0, cz = 0;
            for (int k = 0, l = p.Length - 1; k < p.Length; l = k++)
            {
                double f = p[l].x * p[k].y - p[k].x * p[l].y;
                a += f; cx += (p[l].x + p[k].x) * f; cz += (p[l].y + p[k].y) * f;
            }
            return Math.Abs(a) < 1e-9 ? p[0] : new double2(cx / (3 * a), cz / (3 * a));
        }
    }
}
