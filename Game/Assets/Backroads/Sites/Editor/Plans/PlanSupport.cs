using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// SITE_SEED (design §10): a 64-bit hash of (TOWN_ID, OSM type, OSM id) of a site's main building, so every random
    /// choice of a site depends on that site alone (editing one site never reshuffles another).
    /// </summary>
    public static class SiteSeed
    {
        public static ulong Of(string town, string osm)
        {
            // FNV-1a 64 over "town|type|id", then a splitmix64 finaliser.
            ulong h = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes((town ?? "") + "|" + (osm ?? "").Replace('/', '|')))
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return Mix(h);
        }

        public static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A small deterministic random stream for one site (splitmix64 from the site's seed and a named draw).</summary>
    public sealed class SiteRng
    {
        ulong m_State;

        public SiteRng(ulong seed, string draw)
        {
            ulong h = seed;
            foreach (var c in draw) h = SiteSeed.Mix(h ^ c);
            m_State = h;
        }

        public ulong Next() { m_State += 0x9E3779B97F4A7C15UL; return SiteSeed.Mix(m_State); }

        /// <summary>Uniform in [0, 1).</summary>
        public double Value() => (Next() >> 11) * (1.0 / (1UL << 53));

        /// <summary>A key chosen with the given weights (keys in the order given; ties never arise).</summary>
        public T Pick<T>(IReadOnlyList<(T key, double weight)> options)
        {
            double sum = 0;
            foreach (var o in options) sum += Math.Max(0, o.weight);
            if (sum <= 0) return options[0].key;
            double x = Value() * sum;
            foreach (var o in options)
            {
                x -= Math.Max(0, o.weight);
                if (x < 0) return o.key;
            }
            return options[options.Count - 1].key;
        }
    }

    /// <summary>
    /// Typed access to siteProfile.json (research 08): distributions (n, p10, p25, p50, p75, p90) and shares by dotted
    /// path, with a fallback where a path is missing (the [doc] value the design names).
    /// </summary>
    public sealed class SiteProfile
    {
        readonly object m_Root;
        public string sha256;

        public SiteProfile(object root, string sha = null) { m_Root = root; sha256 = sha; }

        public static SiteProfile Load(string repoRoot)
        {
            var path = Path.Combine(repoRoot, "Game", "Assets", "Content", "sites", "siteProfile.json");
            var bytes = File.ReadAllBytes(path);
            return new SiteProfile(BrJson.Parse(Encoding.UTF8.GetString(bytes)), SiteTownData.Sha256(bytes));
        }

        public object Root => m_Root;

        object Get(string path)
        {
            object o = m_Root;
            foreach (var part in path.Split('.'))
            {
                if (!(o is Dictionary<string, object> d) || !d.TryGetValue(part, out o)) return null;
            }
            return o;
        }

        /// <summary>A quantile ("p50", "p25", ...) of the distribution at path, or the fallback.</summary>
        public double Q(string path, string q, double fallback)
        {
            if (Get(path) is Dictionary<string, object> d && d.TryGetValue(q, out var v) && v is double x) return x;
            return fallback;
        }

        public double P50(string path, double fallback) => Q(path, "p50", fallback);

        /// <summary>The numeric children of a share object (without "n"), in file order.</summary>
        public List<(string key, double weight)> Shares(string path, params (string key, double weight)[] fallback)
        {
            var r = new List<(string, double)>();
            if (Get(path) is Dictionary<string, object> d)
                foreach (var kv in d)
                    if (kv.Key != "n" && kv.Value is double x) r.Add((kv.Key, x));
            if (r.Count == 0) r.AddRange(fallback);
            return r;
        }
    }

    /// <summary>One real layout of siteTemplates.json (research 08 §10), in its street frame (u along the street, v away
    /// from it, v = 0 on the street centreline).</summary>
    public sealed class SiteTemplate
    {
        public string id, type, town, credit;
        public double score, roadHalf;
        public double2[] building, lot, canopy, yard, garage;
        public readonly List<double2[]> lots = new List<double2[]>(), others = new List<double2[]>(), aisles = new List<double2[]>();
        public readonly List<(double u, double v, bool oneway, string svc)> entrances = new List<(double, double, bool, string)>();
        public double2[] driveway;
        public readonly List<string> osm = new List<string>();
    }

    public sealed class SiteTemplates
    {
        public readonly List<SiteTemplate> all = new List<SiteTemplate>();
        public string sha256;

        public static SiteTemplates Load(string repoRoot)
        {
            var path = Path.Combine(repoRoot, "Game", "Assets", "Content", "sites", "siteTemplates.json");
            var bytes = File.ReadAllBytes(path);
            var t = new SiteTemplates { sha256 = SiteTownData.Sha256(bytes) };
            var root = BrJson.Obj(BrJson.Parse(Encoding.UTF8.GetString(bytes)));
            foreach (var o in BrJson.Arr(root["templates"]))
            {
                var d = BrJson.Obj(o);
                var s = new SiteTemplate { id = Str(d, "id"), type = Str(d, "type"), town = Str(d, "town"), credit = Str(d, "credit"), score = Num(d, "score") };
                if (d.TryGetValue("road", out var road) && road is Dictionary<string, object> rd) s.roadHalf = Num(rd, "hwAssumed", 4.5);
                s.building = PolyOf(d, "building");
                s.lot = PolyOf(d, "lot");
                s.canopy = PolyOf(d, "canopy");
                s.yard = PolyOf(d, "yard");
                s.garage = PolyOf(d, "garage");
                if (d.TryGetValue("driveway", out var dw) && dw is Dictionary<string, object> dwd && dwd.TryGetValue("line", out var dl)) s.driveway = Pts(dl);
                if (d.TryGetValue("lots", out var ls) && ls is List<object> ll)
                    foreach (var x in ll) if (x is Dictionary<string, object> xd && xd.TryGetValue("poly", out var pp)) s.lots.Add(Pts(pp));
                if (d.TryGetValue("buildings", out var bs) && bs is List<object> bl)
                    foreach (var x in bl) if (x is Dictionary<string, object> xd && xd.TryGetValue("poly", out var pp)) s.others.Add(Pts(pp));
                if (d.TryGetValue("others", out var os) && os is List<object> ol)
                    foreach (var x in ol) if (x is Dictionary<string, object> xd && xd.TryGetValue("poly", out var pp)) s.others.Add(Pts(pp));
                if (d.TryGetValue("aisles", out var al) && al is List<object> all)
                    foreach (var x in all) if (x is Dictionary<string, object> xd && xd.TryGetValue("line", out var pp)) s.aisles.Add(Pts(pp));
                if (d.TryGetValue("entrances", out var es) && es is List<object> el)
                    foreach (var x in el)
                        if (x is Dictionary<string, object> xd)
                            s.entrances.Add((Num(xd, "u"), Num(xd, "v"), xd.TryGetValue("oneway", out var ow) && ow is string ows && ows == "yes", Str(xd, "svc")));
                if (d.TryGetValue("osm", out var om) && om is Dictionary<string, object> omd)
                    foreach (var kv in omd) if (kv.Value is string sv) s.osm.Add(sv);
                t.all.Add(s);
            }
            return t;
        }

        static double2[] PolyOf(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is Dictionary<string, object> vd && vd.TryGetValue("poly", out var p) && p is List<object> ? Pts(p) : null;

        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
        static double Num(Dictionary<string, object> d, string k, double fallback = 0) =>
            d.TryGetValue(k, out var v) && v is double x ? x : fallback;

        static double2[] Pts(object o)
        {
            var l = BrJson.Arr(o);
            var r = new double2[l.Count];
            for (int i = 0; i < l.Count; i++)
            {
                var p = BrJson.Arr(l[i]);
                r[i] = new double2(Convert.ToDouble(p[0], CultureInfo.InvariantCulture), Convert.ToDouble(p[1], CultureInfo.InvariantCulture));
            }
            return r;
        }
    }

    /// <summary>
    /// The street frame of a parcel (research 08 §1.3): u along its main frontage face (to the right when facing the
    /// site from the street, which is the face's own direction), v away from the street, origin on the walk's back edge
    /// at the middle of the frontage. Approximate on curved faces, which the plans tolerate (everything is clipped to the
    /// parcel in the world frame).
    /// </summary>
    public sealed class StreetFrame
    {
        public FrontLine face;
        public FrontageRef front;
        public double2 origin, u, v;
        public double sMid;

        public double2 ToUV(double2 p)
        {
            var d = p - origin;
            return new double2(math.dot(d, u), math.dot(d, v));
        }

        public double2 FromUV(double2 q) => origin + u * q.x + v * q.y;

        public double2 FromUV(double uu, double vv) => FromUV(new double2(uu, vv));

        public double2[] FromUV(IEnumerable<double2> qs)
        {
            var r = new List<double2>();
            foreach (var q in qs) r.Add(FromUV(q));
            return r.ToArray();
        }

        /// <summary>A rectangle u0..u1 x v0..v1 of the frame as a world polygon (CCW).</summary>
        public double2[] Rect(double u0, double u1, double v0, double v1) =>
            new[] { FromUV(u0, v0), FromUV(u1, v0), FromUV(u1, v1), FromUV(u0, v1) };

        /// <summary>Extents of points in the frame.</summary>
        public (double minU, double maxU, double minV, double maxV) Box(IEnumerable<double2> pts)
        {
            double a = double.MaxValue, b = double.MinValue, c = double.MaxValue, d = double.MinValue;
            foreach (var p in pts)
            {
                var q = ToUV(p);
                a = Math.Min(a, q.x); b = Math.Max(b, q.x); c = Math.Min(c, q.y); d = Math.Max(d, q.y);
            }
            return (a, b, c, d);
        }

        /// <summary>Arc length on the face nearest a world point (projection onto the back edge, s of the kerb line).</summary>
        public static double SOn(FrontLine f, double2 p)
        {
            double best = double.MaxValue, s = 0;
            for (int i = 0; i + 1 < f.back.Length; i++)
            {
                var a = f.back[i];
                var b = f.back[i + 1];
                var ab = b - a;
                double l2 = math.dot(ab, ab);
                double t = l2 > 0 ? math.clamp(math.dot(p - a, ab) / l2, 0, 1) : 0;
                double d = math.distance(p, a + ab * t);
                if (d < best - 1e-9) { best = d; s = f.s[i] + t * (f.s[i + 1] - f.s[i]); }
            }
            return s;
        }

        public static StreetFrame Of(FrontLine face, FrontageRef front)
        {
            var f = new StreetFrame { face = face, front = front, sMid = 0.5 * (front.s0 + front.s1) };
            f.origin = face.BackAt(f.sMid, out var n);
            f.v = n;
            f.u = new double2(n.y, -n.x);
            return f;
        }

        /// <summary>A frame for a parcel with no frontage: v from the easement's street end towards the parcel.</summary>
        public static StreetFrame Facing(double2 origin, double2 inward)
        {
            var n = math.normalize(inward);
            return new StreetFrame { origin = origin, v = n, u = new double2(n.y, -n.x) };
        }
    }
}
