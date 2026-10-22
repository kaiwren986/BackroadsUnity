using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.Build.Editor;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// Reads a town for the road bake (roads-R0o): TownData/(town)/v1 (or a test fixture folder with the same layout)
    /// -> OsmRoadSource. Files read: manifest.json (play area, zones, file hashes), roads.json, buildings.json (footprints),
    /// sites.json (mapped parking lots), dem/dem.json + the DEM. Every file read is checked against its manifest SHA-256
    /// first, so a stale or LFS-pointer checkout fails loudly instead of baking wrong roads.
    /// </summary>
    public static class TownRoadSource
    {
        /// <summary>TownData/(town)/v1 in this checkout.</summary>
        public static string TownDir(string townId) => RepoPaths.InRepo("TownData/" + townId + "/v1");

        /// <summary>The towns with a TownData/(town)/v1/manifest.json, sorted.</summary>
        public static List<string> Towns()
        {
            var root = RepoPaths.InRepo("TownData");
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "v1", "manifest.json")))
                .Select(Path.GetFileName).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        public static OsmRoadSource Load(string dir, string sourceName)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("no manifest.json in " + dir);
            var manifestBytes = File.ReadAllBytes(manifestPath);
            var m = Obj(BrJson.Parse(Encoding.UTF8.GetString(manifestBytes)), "manifest");
            var files = m.TryGetValue("files", out var fo) ? Obj(fo, "files") : new Dictionary<string, object>();

            byte[] Read(string rel, bool optional = false)
            {
                var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    if (optional) return null;
                    throw new FileNotFoundException($"{rel} is missing in {dir}");
                }
                var bytes = File.ReadAllBytes(path);
                if (!files.TryGetValue(rel, out var e)) throw new InvalidDataException($"{rel} is not listed in {manifestPath}");
                var want = Obj(e, rel)["sha256"] as string;
                if (Sha256(bytes) != want)
                    throw new InvalidDataException($"{rel}: SHA-256 differs from {manifestPath} ({bytes.Length} bytes; an LFS pointer? run git lfs pull)");
                return bytes;
            }

            var src = new OsmRoadSource { source = sourceName, manifestSha256 = Sha256(manifestBytes) };
            src.playHalf = D(Obj(m["playArea"], "playArea"), "half");
            if (m.TryGetValue("zones", out var zo) && zo != null)
            {
                var z = Obj(zo, "zones");
                src.kerbZone = ZonePolygon(z, "kerbZone");
                src.downtownZone = ZonePolygon(z, "downtownZone");
            }

            var roads = Obj(BrJson.Parse(Encoding.UTF8.GetString(Read("roads.json"))), "roads.json");
            foreach (var no in Arr(roads["nodes"], "nodes"))
            {
                var n = Obj(no);
                var node = new OsmRoadSource.Node { id = Convert.ToInt64(n["id"]), x = D(n, "x"), z = D(n, "z"), tags = Tags(n) };
                src.nodes[node.id] = node;
            }
            foreach (var wo in Arr(roads["ways"], "ways"))
            {
                var w = Obj(wo);
                string osm = w["osm"] as string;
                src.ways.Add(new OsmRoadSource.Way
                {
                    osm = osm, id = long.Parse(osm.Substring(osm.IndexOf('/') + 1)),
                    nodes = Arr(w["nodes"]).Select(Convert.ToInt64).ToArray(), tags = Tags(w),
                });
            }

            if (roads.TryGetValue("bridgeSpans", out var spans) && spans != null)
                foreach (var so in Arr(spans, "bridgeSpans"))
                {
                    var sp = Obj(so);
                    string what = sp.TryGetValue("over", out var ov) ? ov as string ?? "" : "";
                    if (sp.TryGetValue("ways", out var wl) && wl != null)
                        foreach (var w in Arr(wl)) if (w is string ws) src.bridgeOver[ws] = what;
                }

            var bb = Read("buildings.json", optional: true);
            if (bb != null)
            {
                var b = Obj(BrJson.Parse(Encoding.UTF8.GetString(bb)), "buildings.json");
                foreach (var bo in Arr(b["buildings"], "buildings"))
                {
                    var poly = Obj(bo)["poly"];
                    if (poly != null) src.footprints.Add(Points(poly));
                }
            }
            var sb = Read("sites.json", optional: true);
            if (sb != null)
            {
                var s = Obj(BrJson.Parse(Encoding.UTF8.GetString(sb)), "sites.json");
                if (s.TryGetValue("parking", out var po) && po != null)
                    foreach (var lot in Arr(po))
                        foreach (var pp in Arr(Obj(lot)["polys"]))
                            src.parkingLots.Add(Points(Obj(pp)["outer"]));
            }

            var demJson = Obj(BrJson.Parse(Encoding.UTF8.GetString(Read("dem/dem.json"))), "dem.json");
            int nx = (int)D(demJson, demJson.ContainsKey("nx") ? "nx" : "n"), nz = (int)D(demJson, demJson.ContainsKey("nz") ? "nz" : "n");
            var demFile = "dem/" + (demJson["file"] as string);
            var values = GridHeightField.ReadF32(Read(demFile));
            src.dem = new GridHeightField(values, nx, nz, D(demJson, "x0"), D(demJson, "z0"), D(demJson, "res"));
            return src;
        }

        public static OsmRoadSource LoadTown(string townId) => Load(TownDir(townId), "TownData/" + townId + "/v1");

        static D2[] ZonePolygon(Dictionary<string, object> zones, string key)
        {
            if (!zones.TryGetValue(key, out var zo) || zo == null) return null;
            var z = Obj(zo, key);
            return z.TryGetValue("polygon", out var p) && p != null ? Points(p) : null;
        }

        static D2[] Points(object o) => Arr(o).Select(p => { var a = Arr(p); return new D2(Convert.ToDouble(a[0]), Convert.ToDouble(a[1])); }).ToArray();

        static Dictionary<string, string> Tags(Dictionary<string, object> o)
        {
            var t = new Dictionary<string, string>(StringComparer.Ordinal);
            if (o.TryGetValue("tags", out var to) && to is Dictionary<string, object> d)
                foreach (var kv in d) t[kv.Key] = kv.Value as string ?? Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            return t;
        }

        static Dictionary<string, object> Obj(object o, string what = "value") => BrJson.Obj(o, what);
        static List<object> Arr(object o, string what = "value") => BrJson.Arr(o, what);
        static double D(Dictionary<string, object> o, string k) => Convert.ToDouble(o[k]);

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2")));
        }
    }
}
