using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Backroads.Build.Editor;
using Backroads.Canon.Editor;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.World.Bake
{
    /// <summary>
    /// The 1 m DEM of a TownData extract (dem/dem_1m.f32 + dem.json): float32, y = elevation - Y0, k = j * n + i at
    /// x = x0 + i * res, z = z0 + j * res (rows south to north, columns west to east). Double arithmetic throughout.
    /// </summary>
    public sealed class TownDem
    {
        public int n;
        public double x0, z0, res = 1;
        public float[] y;

        public float At(int i, int j)
        {
            i = i < 0 ? 0 : i >= n ? n - 1 : i;
            j = j < 0 ? 0 : j >= n ? n - 1 : j;
            return y[j * n + i];
        }

        /// <summary>Bilinear height at a local point (clamped at the border).</summary>
        public double Bilinear(double x, double z)
        {
            double fx = (x - x0) / res, fz = (z - z0) / res;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
            double tx = fx - i, tz = fz - j;
            double a = At(i, j), b = At(i + 1, j), c = At(i, j + 1), d = At(i + 1, j + 1);
            return (a + (b - a) * tx) * (1 - tz) + (c + (d - c) * tx) * tz;
        }

        public bool Contains(double x, double z) =>
            x >= x0 && z >= z0 && x <= x0 + (n - 1) * res && z <= z0 + (n - 1) * res;
    }

    /// <summary>A polygon with holes in the local frame (outer CCW, holes CW, not closed).</summary>
    public sealed class Poly
    {
        public double2[] outer;
        public double2[][] holes = Array.Empty<double2[]>();

        public IEnumerable<double2[]> Rings()
        {
            yield return outer;
            foreach (var h in holes) yield return h;
        }
    }

    /// <summary>A river reach (a river area of water.json) and the DEM's statistics inside it.</summary>
    public sealed class RiverReach
    {
        public string osm;
        public List<Poly> polys = new List<Poly>();
        public double p05, p50, p95, min, max;
    }

    public sealed class LanduseArea
    {
        public string osm;
        public Dictionary<string, string> tags = new Dictionary<string, string>();
        public List<Poly> polys = new List<Poly>();
        public string Tag(string k) => tags.TryGetValue(k, out var v) ? v : null;
    }

    public sealed class BuildingFootprint
    {
        public string osm;
        public int part;
        public double2[] poly;
        public double2[][] holes = Array.Empty<double2[]>();
        public double2 centroid;
        public double y;
        public bool inPlay;
        public string type;
    }

    /// <summary>An added place of the scout's list (tools/scout/towns.json "sites"): marsh, quarry, depot, track.</summary>
    public sealed class ScoutSite
    {
        public string cat, label;
        public double2 local;
    }

    /// <summary>
    /// What lane W's terrain bake reads from TownData (ADR-007 section 1): the frame, the play area, the DEM, the river
    /// reaches and water structures, land use, building footprints and the scout's added sites. Read-only; never edits
    /// TownData.
    /// </summary>
    public sealed class TownSource
    {
        public string townId, dir, townName;
        public string manifestSha256;
        public TownFrame frame;
        public double playHalf;
        public TownDem dem;
        public readonly List<RiverReach> reaches = new List<RiverReach>();
        /// <summary>The weir lines (waterway=weir), local plan.</summary>
        public readonly List<double2[]> weirs = new List<double2[]>();
        /// <summary>Lock channels (waterway=canal + lock=yes) and the lock gates.</summary>
        public readonly List<double2[]> locks = new List<double2[]>();
        public readonly List<double2> lockGates = new List<double2>();
        public readonly List<LanduseArea> landuse = new List<LanduseArea>();
        public readonly List<BuildingFootprint> buildings = new List<BuildingFootprint>();
        public readonly List<ScoutSite> scoutSites = new List<ScoutSite>();

        public static string TownDir(string townId) => RepoPaths.InRepo("TownData/" + townId + "/v1");

        public static TownSource Load(string townId) => Load(townId, TownDir(townId));

        public static TownSource Load(string townId, string dir)
        {
            var t = new TownSource { townId = townId, dir = dir };
            var manifestPath = Path.Combine(dir, "manifest.json");
            var mText = File.ReadAllText(manifestPath);
            t.manifestSha256 = Sha256File(manifestPath);
            var m = CanonJson.Obj(CanonJson.Parse(mText), "manifest");
            t.townName = CanonJson.Str(m, "townName");
            var f = CanonJson.Obj(CanonJson.Get(m, "frame"), "frame");
            t.frame = new TownFrame(CanonJson.I(f, "zone"), CanonJson.Num(f, "originEasting"), CanonJson.Num(f, "originNorthing"), CanonJson.Num(f, "Y0"),
                                    !CanonJson.Has(f, "northern") || CanonJson.Bool(f, "northern"));
            var play = CanonJson.Obj(CanonJson.Get(m, "playArea"), "playArea");
            t.playHalf = CanonJson.Num(play, "half");
            t.frame.playHalf = t.playHalf;
            var ex = CanonJson.Obj(CanonJson.Get(m, "extract"), "extract");
            t.frame.extractHalf = CanonJson.Num(ex, "half");

            t.dem = LoadDem(Path.Combine(dir, "dem"));
            LoadWater(t, CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(dir, "water.json"))), "water.json"));
            LoadLanduse(t, CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(dir, "landuse.json"))), "landuse.json"));
            LoadBuildings(t, CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(dir, "buildings.json"))), "buildings.json"));
            LoadScoutSites(t, f);
            return t;
        }

        public static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var s = File.OpenRead(path);
            return CanonRuntime.Hex(sha.ComputeHash(s));
        }

        static TownDem LoadDem(string demDir)
        {
            var j = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(demDir, "dem.json"))), "dem.json");
            var d = new TownDem { n = CanonJson.I(j, "n"), x0 = CanonJson.Num(j, "x0"), z0 = CanonJson.Num(j, "z0"), res = CanonJson.Num(j, "res", 1) };
            var bytes = File.ReadAllBytes(Path.Combine(demDir, CanonJson.Str(j, "file") ?? "dem_1m.f32"));
            if (bytes.Length != d.n * d.n * 4) throw new InvalidDataException($"DEM: {bytes.Length} bytes for {d.n}^2 floats");
            d.y = new float[d.n * d.n];
            Buffer.BlockCopy(bytes, 0, d.y, 0, bytes.Length);
            return d;
        }

        static double2 P(object o)
        {
            var a = CanonJson.Doubles(o);
            return new double2(a[0], a[1]);
        }

        static double2[] Pts(object o)
        {
            var l = CanonJson.Arr(o);
            var r = new double2[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = P(l[i]);
            return r;
        }

        static List<Poly> Polys(Dictionary<string, object> a)
        {
            var list = new List<Poly>();
            foreach (var po in CanonJson.Arr(CanonJson.Get(a, "polys")))
            {
                var p = CanonJson.Obj(po);
                var poly = new Poly { outer = Pts(CanonJson.Get(p, "outer")) };
                if (CanonJson.Has(p, "holes"))
                {
                    var hs = CanonJson.Arr(CanonJson.Get(p, "holes"));
                    poly.holes = new double2[hs.Count][];
                    for (int i = 0; i < hs.Count; i++) poly.holes[i] = Pts(hs[i]);
                }
                list.Add(poly);
            }
            return list;
        }

        static Dictionary<string, string> Tags(Dictionary<string, object> a)
        {
            var d = new Dictionary<string, string>();
            if (CanonJson.Get(a, "tags") is Dictionary<string, object> t)
                foreach (var kv in t) d[kv.Key] = kv.Value as string ?? Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            return d;
        }

        static void LoadWater(TownSource t, Dictionary<string, object> w)
        {
            var levels = new Dictionary<string, Dictionary<string, object>>();
            foreach (var lo in CanonJson.Arr(CanonJson.Get(w, "levels")))
            {
                var l = CanonJson.Obj(lo);
                levels[CanonJson.Str(l, "osm")] = l;
            }
            foreach (var ao in CanonJson.Arr(CanonJson.Get(w, "areas")))
            {
                var a = CanonJson.Obj(ao);
                if (!CanonJson.Bool(a, "river")) continue;
                var r = new RiverReach { osm = CanonJson.Str(a, "osm"), polys = Polys(a) };
                if (levels.TryGetValue(r.osm, out var l))
                {
                    r.p05 = CanonJson.Num(l, "p05"); r.p50 = CanonJson.Num(l, "p50"); r.p95 = CanonJson.Num(l, "p95");
                    r.min = CanonJson.Num(l, "min"); r.max = CanonJson.Num(l, "max");
                }
                t.reaches.Add(r);
            }
            t.reaches.Sort((a, b) => string.CompareOrdinal(a.osm, b.osm));
            foreach (var so in CanonJson.Arr(CanonJson.Get(w, "structures")))
            {
                var s = CanonJson.Obj(so);
                var tags = Tags(s);
                tags.TryGetValue("waterway", out var ww);
                if (ww == "weir" && CanonJson.Has(s, "parts"))
                    foreach (var part in CanonJson.Arr(CanonJson.Get(s, "parts"))) t.weirs.Add(Pts(part));
                else if (ww == "lock_gate" && CanonJson.Has(s, "p")) t.lockGates.Add(P(CanonJson.Get(s, "p")));
                else if (tags.TryGetValue("lock", out var lk) && lk == "yes" && CanonJson.Has(s, "parts"))
                    foreach (var part in CanonJson.Arr(CanonJson.Get(s, "parts"))) t.locks.Add(Pts(part));
            }
        }

        static void LoadLanduse(TownSource t, Dictionary<string, object> lu)
        {
            foreach (var ao in CanonJson.Arr(CanonJson.Get(lu, "areas")))
            {
                var a = CanonJson.Obj(ao);
                t.landuse.Add(new LanduseArea { osm = CanonJson.Str(a, "osm"), tags = Tags(a), polys = Polys(a) });
            }
            t.landuse.Sort((a, b) => string.CompareOrdinal(a.osm, b.osm));
        }

        static void LoadBuildings(TownSource t, Dictionary<string, object> bj)
        {
            foreach (var bo in CanonJson.Arr(CanonJson.Get(bj, "buildings")))
            {
                var b = CanonJson.Obj(bo);
                var f = new BuildingFootprint
                {
                    osm = CanonJson.Str(b, "osm"), part = CanonJson.I(b, "part", 0), poly = Pts(CanonJson.Get(b, "poly")),
                    centroid = P(CanonJson.Get(b, "c")), y = CanonJson.Num(b, "y"), inPlay = CanonJson.Bool(b, "inPlay"),
                    type = CanonJson.Str(b, "type"),
                };
                if (CanonJson.Has(b, "holes"))
                {
                    var hs = CanonJson.Arr(CanonJson.Get(b, "holes"));
                    f.holes = new double2[hs.Count][];
                    for (int i = 0; i < hs.Count; i++) f.holes[i] = Pts(hs[i]);
                }
                t.buildings.Add(f);
            }
            t.buildings.Sort((a, b) => { int c = string.CompareOrdinal(a.osm, b.osm); return c != 0 ? c : a.part.CompareTo(b.part); });
        }

        /// <summary>
        /// The scout's added places for this town (tools/scout/towns.json "sites", offsets in metres east and north of the
        /// town's place node), placed from the place node's own local position (manifest frame placeLatLon).
        /// </summary>
        static void LoadScoutSites(TownSource t, Dictionary<string, object> frame)
        {
            var path = RepoPaths.InRepo("tools/scout/towns.json");
            if (!File.Exists(path) || !CanonJson.Has(frame, "placeLatLon")) return;
            var ll = CanonJson.Doubles(CanonJson.Get(frame, "placeLatLon"));
            var node = t.frame.ToLocal(ll[0], ll[1]);
            var root = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(path)), "towns.json");
            foreach (var to in CanonJson.Arr(CanonJson.Get(root, "towns")))
            {
                var town = CanonJson.Obj(to);
                if (CanonJson.Str(town, "id") != t.townId) continue;
                foreach (var so in CanonJson.Arr(CanonJson.Get(town, "sites")))
                {
                    var s = CanonJson.Obj(so);
                    t.scoutSites.Add(new ScoutSite
                    {
                        cat = CanonJson.Str(s, "cat"), label = CanonJson.Str(s, "label"),
                        local = node + new double2(CanonJson.Num(s, "dx", 0), CanonJson.Num(s, "dy", 0)),
                    });
                }
            }
        }

        public ScoutSite Site(string cat) => scoutSites.Find(s => s.cat == cat);
    }
}
