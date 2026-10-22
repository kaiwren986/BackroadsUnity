using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// The site program's view of a town's source extract (TownData/(town)/v1, overview contract 1): buildings, mapped
    /// lots, yards, service ways and points (sites.json), land use (landuse.json), the manifest's zones. Read-only; real
    /// names were stripped at extraction (ADR-006 §6).
    /// </summary>
    public sealed class SiteTownData
    {
        public string townId, dir;
        public string manifestSha256;
        public readonly List<SiteBuilding> buildings = new List<SiteBuilding>();
        public readonly List<MappedArea> parking = new List<MappedArea>(), fuel = new List<MappedArea>(), yards = new List<MappedArea>(),
            landuse = new List<MappedArea>(), water = new List<MappedArea>();
        public readonly List<ServiceWay> service = new List<ServiceWay>();
        public readonly List<MappedLine> barriers = new List<MappedLine>();
        public readonly List<MappedPoint> points = new List<MappedPoint>(), entrances = new List<MappedPoint>();
        public double2[] kerbZone, downtownZone;
        public double playHalf = 1024;

        public sealed class MappedArea
        {
            public string osm;
            public Dictionary<string, string> tags = new Dictionary<string, string>();
            /// <summary>Outer rings (CCW) with their holes (CW).</summary>
            public readonly List<(double2[] outer, double2[][] holes)> polys = new List<(double2[], double2[][])>();
            public double area;
            public string Tag(string k) => tags.TryGetValue(k, out var v) ? v : null;
        }

        public sealed class ServiceWay
        {
            public string osm;
            public Dictionary<string, string> tags = new Dictionary<string, string>();
            public double2[] pts;
            public long[] nodes;
            public string Service => tags.TryGetValue("service", out var v) ? v : null;
        }

        public sealed class MappedLine
        {
            public string osm;
            public Dictionary<string, string> tags = new Dictionary<string, string>();
            public List<double2[]> parts = new List<double2[]>();
        }

        public sealed class MappedPoint
        {
            public string osm, host;
            public double2 p;
            public Dictionary<string, string> tags = new Dictionary<string, string>();
        }

        /// <summary>Every town folder under TownData (sorted), e.g. "(town)-(state)".</summary>
        public static List<string> Towns(string repoRoot)
        {
            var r = new List<string>();
            var root = Path.Combine(repoRoot, "TownData");
            if (!Directory.Exists(root)) return r;
            foreach (var d in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(d, "v1", "manifest.json"))) r.Add(Path.GetFileName(d));
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        public static SiteTownData Load(string repoRoot, string townId)
        {
            var t = new SiteTownData { townId = townId, dir = Path.Combine(repoRoot, "TownData", townId, "v1") };
            var manifestPath = Path.Combine(t.dir, "manifest.json");
            t.manifestSha256 = Sha256(File.ReadAllBytes(manifestPath));
            var m = Obj(BrJson.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)));
            if (m.TryGetValue("playArea", out var pa) && pa is Dictionary<string, object> pad && pad.TryGetValue("half", out var h)) t.playHalf = Num(h);
            if (m.TryGetValue("zones", out var zo) && zo is Dictionary<string, object> zd)
            {
                t.kerbZone = ZonePoly(zd, "kerbZone");
                t.downtownZone = ZonePoly(zd, "downtownZone");
            }

            var b = Obj(BrJson.Parse(File.ReadAllText(Path.Combine(t.dir, "buildings.json"), Encoding.UTF8)));
            foreach (var o in Arr(b["buildings"]))
            {
                var d = Obj(o);
                var sb = new SiteBuilding
                {
                    index = t.buildings.Count, osm = Str(d, "osm"), poly = Poly2.Ccw(Pts(d["poly"])), area = Num(d["area"]),
                    business = ParseBusiness(Str(d, "business")), type = Str(d, "type"), inPlay = d.TryGetValue("inPlay", out var ip) && ip is bool bb && bb,
                    inferred = d.TryGetValue("inferred", out var inf) && inf is bool ib && ib, street = Str(d, "street"),
                    form = Str(d, "form") == "streetfront" ? BuildingForm.Streetfront : Str(d, "form") == "standalone" ? BuildingForm.Standalone : BuildingForm.None,
                };
                if (d.TryGetValue("setback", out var st) && st is double sd) sb.setback = (float)sd;
                if (d.TryGetValue("y", out var fy) && fy is double fyd) sb.floorY = fyd;
                sb.centroid = d.TryGetValue("c", out var c) ? Pt(c) : Poly2.Centroid(sb.poly);
                sb.tags = Tags(d);
                t.buildings.Add(sb);
            }

            var s = Obj(BrJson.Parse(File.ReadAllText(Path.Combine(t.dir, "sites.json"), Encoding.UTF8)));
            Areas(s, "parking", t.parking);
            Areas(s, "fuel", t.fuel);
            Areas(s, "yards", t.yards);
            if (s.TryGetValue("service", out var sv))
                foreach (var o in Arr(sv))
                {
                    var d = Obj(o);
                    var w = new ServiceWay { osm = Str(d, "osm"), tags = Tags(d), pts = Pts(d["pts"]) };
                    if (d.TryGetValue("nodes", out var nd) && nd is List<object> nl)
                    {
                        w.nodes = new long[nl.Count];
                        for (int i = 0; i < nl.Count; i++) w.nodes[i] = (long)Num(nl[i]);
                    }
                    t.service.Add(w);
                }
            if (s.TryGetValue("barriers", out var br))
                foreach (var o in Arr(br))
                {
                    var d = Obj(o);
                    var l = new MappedLine { osm = Str(d, "osm"), tags = Tags(d) };
                    if (d.TryGetValue("parts", out var parts)) foreach (var p in Arr(parts)) l.parts.Add(Pts(p));
                    t.barriers.Add(l);
                }
            Points(s, "points", t.points);
            Points(s, "entrances", t.entrances);

            var lu = Obj(BrJson.Parse(File.ReadAllText(Path.Combine(t.dir, "landuse.json"), Encoding.UTF8)));
            Areas(lu, "areas", t.landuse);
            var wpath = Path.Combine(t.dir, "water.json");
            if (File.Exists(wpath)) Areas(Obj(BrJson.Parse(File.ReadAllText(wpath, Encoding.UTF8))), "areas", t.water);
            return t;
        }

        public bool InPlay(double2 p) => Math.Abs(p.x) <= playHalf && Math.Abs(p.y) <= playHalf;

        // ------------------------------------------------------------------------------------------ JSON helpers
        static void Areas(Dictionary<string, object> root, string key, List<MappedArea> into)
        {
            if (!root.TryGetValue(key, out var v) || !(v is List<object> l)) return;
            foreach (var o in l)
            {
                var d = Obj(o);
                var a = new MappedArea { osm = Str(d, "osm"), tags = Tags(d) };
                if (d.TryGetValue("area", out var ar)) a.area = Num(ar);
                if (d.TryGetValue("polys", out var ps))
                    foreach (var po in Arr(ps))
                    {
                        var pd = Obj(po);
                        var outer = Poly2.Ccw(Pts(pd["outer"]));
                        var holes = new List<double2[]>();
                        if (pd.TryGetValue("holes", out var hs) && hs is List<object> hl)
                            foreach (var ho in hl)
                            {
                                var hp = Poly2.Ccw(Pts(ho));
                                Array.Reverse(hp);
                                holes.Add(hp);
                            }
                        a.polys.Add((outer, holes.ToArray()));
                    }
                else if (d.TryGetValue("poly", out var single)) a.polys.Add((Poly2.Ccw(Pts(single)), Array.Empty<double2[]>()));
                into.Add(a);
            }
        }

        static void Points(Dictionary<string, object> root, string key, List<MappedPoint> into)
        {
            if (!root.TryGetValue(key, out var v) || !(v is List<object> l)) return;
            foreach (var o in l)
            {
                var d = Obj(o);
                into.Add(new MappedPoint { osm = Str(d, "osm"), host = Str(d, "host"), p = Pt(d["p"]), tags = Tags(d) });
            }
        }

        static double2[] ZonePoly(Dictionary<string, object> zones, string key)
        {
            if (!zones.TryGetValue(key, out var z)) return null;
            if (z is Dictionary<string, object> zd)
            {
                foreach (var k in new[] { "polygon", "poly", "outer", "ring" })
                    if (zd.TryGetValue(k, out var p) && p is List<object>) return Poly2.Ccw(Pts(p));
                return null;
            }
            return z is List<object> ? Poly2.Ccw(Pts(z)) : null;
        }

        static Dictionary<string, string> Tags(Dictionary<string, object> d)
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            if (d.TryGetValue("tags", out var t) && t is Dictionary<string, object> td)
                foreach (var kv in td) r[kv.Key] = kv.Value is string s ? s : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return r;
        }

        static BusinessType ParseBusiness(string s) => s != null && Enum.TryParse<BusinessType>(s, false, out var b) ? b : BusinessType.Unknown;

        static Dictionary<string, object> Obj(object o) => BrJson.Obj(o);
        static List<object> Arr(object o) => BrJson.Arr(o);
        static string Str(Dictionary<string, object> d, string k) => BrJson.Str(d, k);
        static double Num(object o) => o is double d ? d : Convert.ToDouble(o, CultureInfo.InvariantCulture);
        static double2 Pt(object o) { var l = Arr(o); return new double2(Num(l[0]), Num(l[1])); }

        static double2[] Pts(object o)
        {
            var l = Arr(o);
            var r = new double2[l.Count];
            for (int i = 0; i < l.Count; i++) r[i] = Pt(l[i]);
            return r;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(64);
                foreach (var x in h) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
