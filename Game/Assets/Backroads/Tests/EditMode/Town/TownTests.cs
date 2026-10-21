using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Town
{
    /// <summary>
    /// Backroads.Tests.EditMode.Town.* (card T3.0, M3 gate): the frozen town source extract TownData/(town)/v1 written
    /// by tools/world/extract-town.mjs. ManifestHashes and NoRealNames are M3 headline gates. The town folder is found by
    /// listing TownData/, so no town name appears in this file.
    /// </summary>
    [TestFixture, Category("Town")]
    public class Town
    {
        // ------------------------------------------------------------------------------------------ helpers
        static List<string> TownDirs()
        {
            var root = RepoPaths.InRepo("TownData");
            var dirs = Directory.Exists(root)
                ? Directory.GetDirectories(root).Select(d => Path.Combine(d, "v1")).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderBy(d => d, StringComparer.Ordinal).ToList()
                : new List<string>();
            Assert.IsNotEmpty(dirs, "no TownData/<town>/v1/manifest.json (run node tools/world/extract-town.mjs <town-id>)");
            return dirs;
        }

        static Dictionary<string, object> Json(string file) => BrJson.Obj(BrJson.Parse(File.ReadAllText(file, Encoding.UTF8)), file);
        static Dictionary<string, object> O(object o, string what = "value") => BrJson.Obj(o, what);
        static List<object> A(object o, string what = "value") => BrJson.Arr(o, what);
        static double D(Dictionary<string, object> o, string k) => Convert.ToDouble(o[k]);
        static string TownId(string v1Dir) => Path.GetFileName(Path.GetDirectoryName(v1Dir));

        static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
        }

        static TownFrame FrameOf(Dictionary<string, object> manifest)
        {
            var f = O(manifest["frame"], "frame");
            var t = new TownFrame((int)D(f, "zone"), D(f, "originEasting"), D(f, "originNorthing"), D(f, "Y0"), (bool)f["northern"]);
            t.playHalf = D(O(manifest["playArea"]), "half");
            t.extractHalf = D(O(manifest["extract"]), "half");
            return t;
        }

        static Dictionary<string, string> Tags(object o) =>
            o == null ? new Dictionary<string, string>() : O(o, "tags").ToDictionary(kv => kv.Key, kv => kv.Value as string, StringComparer.Ordinal);

        // ------------------------------------------------------------------------------------------ ManifestHashes
        /// <summary>Every file of the extract matches its manifest entry (bytes and SHA-256); no file is unlisted; the DEM
        /// has the size its dem.json says.</summary>
        [Test]
        public void ManifestHashes()
        {
            foreach (var dir in TownDirs())
            {
                var m = Json(Path.Combine(dir, "manifest.json"));
                Assert.AreEqual("backroads-towndata", m["format"] as string, "manifest format");
                var files = O(m["files"], "files");
                var errors = new List<string>();
                foreach (var kv in files)
                {
                    var e = O(kv.Value, kv.Key);
                    var path = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) { errors.Add("missing " + kv.Key); continue; }
                    long bytes = new FileInfo(path).Length;
                    if (bytes != (long)D(e, "bytes")) errors.Add($"{kv.Key}: {bytes} bytes, manifest {D(e, "bytes")} (an LFS pointer? run git lfs pull)");
                    else if (Sha256(path) != e["sha256"] as string) errors.Add($"{kv.Key}: SHA-256 differs from the manifest");
                }
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(dir.Length + 1).Replace('\\', '/');
                    if (rel != "manifest.json" && !files.ContainsKey(rel)) errors.Add("not in the manifest: " + rel);
                }
                foreach (var need in new[] { "roads.json", "buildings.json", "sites.json", "landuse.json", "water.json", "dem/dem_1m.f32", "dem/dem.json", "sources.json", "credit.txt" })
                    if (!files.ContainsKey(need)) errors.Add("manifest lacks " + need);
                Assert.IsEmpty(errors, string.Join("\n", errors));

                var dem = Json(Path.Combine(dir, "dem", "dem.json"));
                long n = (long)D(dem, "n");
                Assert.AreEqual(n * n * 4, new FileInfo(Path.Combine(dir, "dem", "dem_1m.f32")).Length, "dem_1m.f32 holds n*n float32");
                Assert.AreEqual(D(O(m["frame"]), "Y0"), D(dem, "Y0"), "Y0 in the manifest frame and dem.json");
                Assert.AreEqual(O(m["dem"])["sha256"] as string, O(files["dem/dem_1m.f32"])["sha256"] as string, "the manifest's DEM pin");
                Assert.AreEqual(-D(O(m["extract"]), "half"), D(dem, "x0"), "the DEM starts at the extract's west edge");
                StringAssert.Contains("OpenStreetMap contributors", File.ReadAllText(Path.Combine(dir, "credit.txt")));
                StringAssert.Contains("3D Elevation Program", File.ReadAllText(Path.Combine(dir, "credit.txt")));
                TestContext.WriteLine($"{TownId(dir)}: {files.Count} files match manifest.json");
            }
        }

        // ------------------------------------------------------------------------------------------ NoRealNames
        // The strip rules of tools/world/town-lib.mjs (card T3.0 step 3, ADR-006 §6), restated here on purpose.
        static readonly string[] StripExact =
        {
            "name", "alt_name", "old_name", "brand", "operator", "phone", "website", "owner", "email", "fax", "url", "description",
            "note", "fixme", "FIXME", "inscription", "image", "is_in", "source", "old_ref", "official_ref", "artist", "architect", "subject", "flag:name",
        };
        static readonly string[] StripPrefixes =
        {
            "name:", "alt_name:", "old_name:", "brand:", "operator:", "addr:", "phone:", "website:", "contact:", "destination", "wiki",
            "tiger:", "gnis:", "nhd:", "is_in:", "source:", "ref:", "note:", "description:", "fixme:", "subject:", "image:", "old_addr", "url:", "email:",
        };
        static readonly string[] StripContains = { "name", "brand", "operator", "owner", "addr", "phone", "website", "email", "contact", "wikidata", "wikipedia" };
        static readonly Regex StateRef = new Regex(@"^(OH|SR)[ -]?\d+[A-Z]?$");
        static readonly Regex FullNameKey = new Regex(@"^((alt_|old_|official_|loc_|short_|reg_|nat_|int_|ref_)?name(_\d+)?(:.*)?|brand|operator|[a-z_:]*operator|addr:(street|city|place|housename))$");

        public static bool KeyDropped(string k) =>
            StripExact.Contains(k) || StripPrefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)) ||
            StripContains.Any(c => k.ToLowerInvariant().Contains(c));

        /// <summary>Every tags object anywhere in a JSON tree, with whether its owner is a highway way.</summary>
        static IEnumerable<(string where, Dictionary<string, object> tags, bool highwayWay)> TagObjects(object node, string where, bool inRoadWays)
        {
            if (node is Dictionary<string, object> d)
            {
                if (d.TryGetValue("tags", out var t) && t is Dictionary<string, object> tg)
                {
                    bool way = d.TryGetValue("osm", out var id) && id is string s && s.StartsWith("way/", StringComparison.Ordinal);
                    yield return (where + "/" + (d.TryGetValue("osm", out var o2) ? o2 : "?"), tg, inRoadWays && way && tg.ContainsKey("highway"));
                }
                foreach (var kv in d)
                {
                    if (kv.Key == "tags") continue;
                    foreach (var x in TagObjects(kv.Value, where + "." + kv.Key, inRoadWays && kv.Key == "ways" || inRoadWays && where.EndsWith(".ways", StringComparison.Ordinal)))
                        yield return x;
                }
            }
            else if (node is List<object> l)
            {
                for (int i = 0; i < l.Count; i++)
                    foreach (var x in TagObjects(l[i], where, inRoadWays)) yield return x;
            }
        }

        /// <summary>
        /// No dropped key survives anywhere in the extract (every "tags" object of every JSON file); ref survives only on
        /// highway ways of roads.json and only as a state route; the manifest's strip rules equal this test's; and no text
        /// of TownData, the C# sources or the classifier fixture holds the town's name or, when the raw download is cached
        /// (artifacts/world-src, in this worktree or the main checkout), any full name of the raw data.
        /// </summary>
        [Test]
        public void NoRealNames()
        {
            // The card's own list must be covered by the rule.
            foreach (var k in new[] { "name", "name:en", "alt_name", "old_name", "brand", "brand:wikidata", "operator", "addr:street", "addr:city", "phone", "website",
                "official_name", "tiger:name_base", "old_railway_operator", "destination", "description", "contact:phone" })
                Assert.IsTrue(KeyDropped(k), "the strip rule must drop " + k);

            var bad = new List<string>();
            int tagObjects = 0, refsKept = 0;
            foreach (var dir in TownDirs())
            {
                var m = Json(Path.Combine(dir, "manifest.json"));
                var strip = O(m["strip"], "strip");
                CollectionAssert.AreEqual(StripExact, A(strip["exact"]).Cast<string>().ToArray(), "manifest strip.exact = the test's list");
                CollectionAssert.AreEqual(StripPrefixes, A(strip["prefixes"]).Cast<string>().ToArray(), "manifest strip.prefixes = the test's list");
                CollectionAssert.AreEqual(StripContains, A(strip["contains"]).Cast<string>().ToArray(), "manifest strip.contains = the test's list");

                foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                {
                    var rel = TownId(dir) + "/" + file.Substring(dir.Length + 1).Replace('\\', '/');
                    var root = BrJson.Parse(File.ReadAllText(file, Encoding.UTF8));
                    bool roads = Path.GetFileName(file) == "roads.json";
                    foreach (var (where, tags, highwayWay) in TagObjects(root, rel, roads))
                    {
                        tagObjects++;
                        foreach (var kv in tags)
                        {
                            if (kv.Key == "ref")
                            {
                                var parts = (kv.Value as string ?? "").Split(';').Select(p => p.Trim());
                                if (!highwayWay || !parts.All(p => StateRef.IsMatch(p))) bad.Add($"{where}: ref={kv.Value}");
                                else refsKept++;
                            }
                            else if (KeyDropped(kv.Key)) bad.Add($"{where}: {kv.Key}");
                        }
                    }
                }
            }
            Assert.IsEmpty(bad, "dropped keys in TownData:\n" + string.Join("\n", bad.Take(60)));

            // Text scan: the town's own name (from its folder id, e.g. "<town>-<state>") and the raw data's full names.
            var texts = new List<string>();
            foreach (var dir in TownDirs())
                texts.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".txt", StringComparison.Ordinal)));
            texts.AddRange(Directory.GetFiles(Path.Combine(RepoPaths.Game, "Assets", "Backroads"), "*.cs", SearchOption.AllDirectories));
            texts.Add(FixturePath);
            var names = new HashSet<string>(StringComparer.Ordinal);
            int rawNames = 0;
            foreach (var dir in TownDirs())
            {
                var id = TownId(dir);
                var token = id.Split('-')[0].ToLowerInvariant();
                if (token.Length >= 4) names.Add(token);
                var raw = RawCache(id);
                if (raw == null) { TestContext.WriteLine($"{id}: raw download not cached here: full-name scan skipped (key rules checked)"); continue; }
                foreach (var e in A(Json(raw)["elements"]))
                {
                    var ed = O(e);
                    if (!ed.TryGetValue("tags", out var t) || !(t is Dictionary<string, object> td)) continue;
                    foreach (var kv in td)
                        if (FullNameKey.IsMatch(kv.Key))
                            foreach (var part in (kv.Value as string ?? "").Split(';'))
                            {
                                var s = part.Trim().ToLowerInvariant();
                                if (s.Length >= 4 && !IsGenericName(s) && names.Add(s)) rawNames++;
                            }
                }
            }
            var leaks = new List<string>();
            foreach (var f in texts)
            {
                var text = File.ReadAllText(f, Encoding.UTF8).ToLowerInvariant();
                foreach (var n in names) if (text.Contains(n)) leaks.Add(RepoPaths.Relative(f) + " holds a real name");
            }
            TestContext.WriteLine($"{tagObjects} tag objects checked, {refsKept} state-route refs kept; {texts.Count} files scanned for {names.Count} names ({rawNames} from the raw download)");
            Assert.IsEmpty(leaks, string.Join("\n", leaks.Distinct().Take(40)));
        }

        /// <summary>Every word generic ("main street", "2nd street", "county road 2"): ordinary English that code comments
        /// may hold (lane R's "Main Street" class), so it is not scanned. Names with any distinctive word stay in.</summary>
        static readonly HashSet<string> GenericWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "main", "north", "south", "east", "west", "northeast", "northwest", "southeast", "southwest", "street", "road", "lane", "avenue",
            "drive", "way", "court", "place", "river", "water", "county", "township", "state", "route", "highway", "first", "second", "third",
        };
        static readonly Regex Ordinal = new Regex(@"^\d+(st|nd|rd|th)?$");

        public static bool IsGenericName(string lower) =>
            lower.Split(new[] { ' ', '-', '.', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).All(w => GenericWords.Contains(w) || Ordinal.IsMatch(w));

        /// <summary>artifacts/world-src/(town)/osm.json of this checkout, or of the main checkout when this is a worktree.</summary>
        static string RawCache(string id)
        {
            var cands = new List<string> { Path.Combine(RepoPaths.Artifacts, "world-src", id, "osm.json") };
            var common = RepoPaths.Git("rev-parse --path-format=absolute --git-common-dir");
            if (!string.IsNullOrEmpty(common))
            {
                var main = Path.GetDirectoryName(Path.GetFullPath(common.Trim()).TrimEnd('\\', '/'));
                if (main != null) cands.Add(Path.Combine(main, "artifacts", "world-src", id, "osm.json"));
            }
            return cands.FirstOrDefault(File.Exists);
        }

        // ------------------------------------------------------------------------------------------ ClassifierFixture
        static string FixturePath => Path.Combine(RepoPaths.Game, "Assets", "Backroads", "Tests", "EditMode", "Town", "Fixtures", "classifier-fixture.json");

        static ClassifierInput InputOf(Dictionary<string, string> tags, IEnumerable<Dictionary<string, string>> pois, IEnumerable<string> lu, double area, Dictionary<string, object> ctx)
        {
            bool F(string k) => ctx != null && ctx.TryGetValue(k, out var v) && v is bool b && b;
            return new ClassifierInput
            {
                Tags = tags, Pois = pois.Select(p => (IReadOnlyDictionary<string, string>)p).ToList(), Landuse = new HashSet<string>(lu, StringComparer.Ordinal), Area = area,
                InFuel = F("inFuel"), NearFuel = F("nearFuel"), InMarina = F("inMarina"), InWorks = F("inWorks"), InSawmill = F("inSawmill"),
            };
        }

        static string Show(string type, string sub, bool inferred) => type + (sub != null ? "/" + sub : "") + (inferred ? "*" : "");

        /// <summary>The same polygon area as tools/osm/lib.mjs area() (shoelace, then the absolute value).</summary>
        static double PolyArea(List<object> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = A(poly[i]); var q = A(poly[(i + 1) % poly.Count]);
                a += Convert.ToDouble(p[0]) * Convert.ToDouble(q[1]) - Convert.ToDouble(q[0]) * Convert.ToDouble(p[1]);
            }
            return Math.Abs(a / 2);
        }

        /// <summary>
        /// The C# classifier agrees with measure.mjs typeOf on the 200 shared fixture cases (type, sub, inferred), and on
        /// every classified building of the extract (whose type the JS wrote).
        /// </summary>
        [Test]
        public void ClassifierFixture()
        {
            var fx = Json(FixturePath);
            var cases = A(fx["cases"], "cases");
            Assert.AreEqual(200, cases.Count, "the fixture holds 200 buildings");
            var bad = new List<string>();
            var types = new HashSet<string>();
            foreach (var c in cases.Select(x => O(x)))
            {
                var input = InputOf(Tags(c["tags"]), A(c["pois"]).Select(Tags), A(c["lu"]).Cast<string>(), D(c, "A"), O(c["ctx"]));
                var got = BusinessClassifier.Classify(input);
                var want = O(c["expect"]);
                string wt = want["type"] as string, ws = want.TryGetValue("sub", out var s) ? s as string : null;
                bool wi = want.TryGetValue("inferred", out var inf) && inf is bool bi && bi;
                types.Add(wt);
                if (got.Type != wt || got.Sub != ws || got.Inferred != wi)
                    bad.Add($"{c["id"]} ({c["note"]}): typeOf {Show(wt, ws, wi)}, C# {Show(got.Type, got.Sub, got.Inferred)}");
            }
            Assert.IsEmpty(bad, "C# and JS disagree:\n" + string.Join("\n", bad));
            Assert.GreaterOrEqual(types.Count, 20, "the fixture walks (nearly) every type");

            int buildings = 0;
            foreach (var dir in TownDirs())
                foreach (var b in A(Json(Path.Combine(dir, "buildings.json"))["buildings"]).Select(x => O(x)))
                {
                    var type = b["type"] as string;
                    var business = (BusinessType)Enum.Parse(typeof(BusinessType), b["business"] as string);
                    Assert.AreEqual(BusinessClassifier.BusinessOf(type), business, $"{b["osm"]}: business of {type}");
                    if (type == "unknown") continue;
                    buildings++;
                    var input = InputOf(Tags(b["tags"]), A(b["pois"]).Select(p => Tags(O(p)["tags"])), A(b["lu"]).Cast<string>(), PolyArea(A(b["poly"])), O(b["ctx"]));
                    var got = BusinessClassifier.Classify(input);
                    string ws = b.TryGetValue("sub", out var s) ? s as string : null;
                    bool wi = b.TryGetValue("inferred", out var inf) && inf is bool bi && bi;
                    if (got.Type != type || got.Sub != ws || got.Inferred != wi) bad.Add($"{TownId(dir)} {b["osm"]}: JS {Show(type, ws, wi)}, C# {Show(got.Type, got.Sub, got.Inferred)}");
                    if (b.TryGetValue("form", out var form)) Assert.Contains(form as string, new[] { "standalone", "streetfront" }, $"{b["osm"]}: form");
                }
            Assert.IsEmpty(bad, "C# and the extract disagree:\n" + string.Join("\n", bad.Take(40)));
            TestContext.WriteLine($"{cases.Count} fixture cases ({types.Count} types) and {buildings} extract buildings agree");
        }

        // ------------------------------------------------------------------------------------------ FrameRoundTrip
        static double Metres(double lat0, double lon0, double lat1, double lon1)
        {
            double dn = (lat1 - lat0) * 111132.954, de = (lon1 - lon0) * 111319.49 * Math.Cos(lat0 * Math.PI / 180);
            return Math.Sqrt(dn * dn + de * de);
        }

        /// <summary>
        /// lat/lon -> local -> lat/lon within 1 mm over the extract (and local -> lat/lon -> local); the origin maps to (0, 0);
        /// the C# projection matches the JS one (manifest frame.checks) within 1 mm.
        /// </summary>
        [Test]
        public void FrameRoundTrip()
        {
            foreach (var dir in TownDirs())
            {
                var m = Json(Path.Combine(dir, "manifest.json"));
                var frame = FrameOf(m);
                var fj = O(m["frame"]);
                var o = frame.ToLocal(D(fj, "originLat"), D(fj, "originLon"));
                Assert.Less(math.length(o), 0.001, "the origin maps to (0, 0)");

                foreach (var c in A(fj["checks"]).Select(x => O(x)))
                {
                    var p = frame.ToLocal(D(c, "lat"), D(c, "lon"));
                    Assert.Less(math.distance(p, new double2(D(c, "xFromLatLon"), D(c, "zFromLatLon"))), 0.001, $"C# vs JS at ({c["x"]}, {c["z"]})");
                    Assert.Less(math.distance(p, new double2(D(c, "x"), D(c, "z"))), 0.001, $"JS lat/lon of ({c["x"]}, {c["z"]})");
                }

                var bb = O(O(m["extract"])["overpassBbox"]);
                double s = D(bb, "south"), w = D(bb, "west"), n = D(bb, "north"), e = D(bb, "east");
                double worst = 0, worstLocal = 0;
                const int K = 40;
                for (int i = 0; i <= K; i++)
                    for (int j = 0; j <= K; j++)
                    {
                        double lat = s + (n - s) * i / K, lon = w + (e - w) * j / K;
                        var p = frame.ToLocal(lat, lon);
                        frame.ToLatLon(p, out var lat2, out var lon2);
                        worst = Math.Max(worst, Metres(lat, lon, lat2, lon2));
                        var q = new double2(-frame.extractHalf + 2 * frame.extractHalf * i / K, -frame.extractHalf + 2 * frame.extractHalf * j / K);
                        frame.ToLatLon(q, out var la, out var lo);
                        worstLocal = Math.Max(worstLocal, math.distance(q, frame.ToLocal(la, lo)));
                    }
                Assert.Less(worst, 0.001, "lat/lon -> local -> lat/lon");
                Assert.Less(worstLocal, 0.001, "local -> lat/lon -> local");
                Assert.AreEqual(frame.zone, Utm.ZoneOf(D(fj, "originLon")), "the frame's zone holds its origin");
                TestContext.WriteLine($"{TownId(dir)}: round trip worst {worst * 1000:F6} mm (lat/lon), {worstLocal * 1000:F6} mm (local) on {(K + 1) * (K + 1)} points");
            }
        }
    }
}
