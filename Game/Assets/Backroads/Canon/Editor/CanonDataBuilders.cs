using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Backroads.World;
using UnityEngine;

namespace Backroads.Canon.Editor
{
    /// <summary>JSON of CanonData v1 to the World ScriptableObjects. Values are copied as they are (JS space, no flip).</summary>
    public static class CanonDataBuilders
    {
        static Dictionary<string, object> O(object o) => o as Dictionary<string, object>;
        static List<object> A(object o) => o as List<object> ?? new List<object>();

        public static WorldInfo BuildWorldInfo(Dictionary<string, object> w, Dictionary<string, object> cams)
        {
            var info = ScriptableObject.CreateInstance<WorldInfo>();
            var g = O(CanonJson.Get(w, "grid"));
            info.size = CanonJson.F(g, "size"); info.half = CanonJson.F(g, "half"); info.cell = CanonJson.F(g, "cell");
            info.n = CanonJson.I(g, "N"); info.vn = CanonJson.I(g, "VN"); info.waterY = CanonJson.F(g, "waterY");
            info.tintN = CanonJson.I(g, "tintN"); info.tintCell = CanonJson.F(g, "tintCell");

            info.pois = A(CanonJson.Get(w, "pois")).Select(o =>
            {
                var p = O(o);
                var poi = new WorldInfo.Poi
                {
                    id = CanonJson.Str(p, "id"), name = CanonJson.Str(p, "name"), kind = CanonJson.Str(p, "kind"),
                    x = CanonJson.F(p, "x"), z = CanonJson.F(p, "z"), r = CanonJson.F(p, "r"), fall = CanonJson.F(p, "fall"), y = CanonJson.F(p, "y"),
                };
                if (O(CanonJson.Get(p, "quay")) is Dictionary<string, object> q)
                {
                    poi.hasQuay = true;
                    poi.quayX = CanonJson.F(q, "x"); poi.quayZ = CanonJson.F(q, "z"); poi.quayUx = CanonJson.F(q, "ux"); poi.quayUz = CanonJson.F(q, "uz");
                    poi.quayLen = CanonJson.F(q, "len"); poi.quayHw = CanonJson.F(q, "hw");
                }
                return poi;
            }).ToArray();
            var lake = O(CanonJson.Get(w, "lake"));
            info.lake = new WorldInfo.Circle { name = "Lake", x = CanonJson.F(lake, "x"), z = CanonJson.F(lake, "z"), r = CanonJson.F(lake, "r") };
            info.bogs = A(CanonJson.Get(w, "bogs")).Select(o => { var b = O(o); return new WorldInfo.Circle { name = CanonJson.Str(b, "name"), x = CanonJson.F(b, "x"), z = CanonJson.F(b, "z"), r = CanonJson.F(b, "r") }; }).ToArray();
            info.fields = A(CanonJson.Get(w, "fields")).Select(o => { var f = O(o); return new WorldInfo.Field { x = CanonJson.F(f, "x"), z = CanonJson.F(f, "z"), w = CanonJson.F(f, "w"), d = CanonJson.F(f, "d"), rot = CanonJson.F(f, "rot") }; }).ToArray();
            info.hamlets = A(CanonJson.Get(w, "hamlets")).Select(o => { var h = O(o); return new WorldInfo.Hamlet { name = CanonJson.Str(h, "name"), x = CanonJson.F(h, "x"), z = CanonJson.F(h, "z") }; }).ToArray();
            var town = O(CanonJson.Get(w, "town"));
            info.townRadius = CanonJson.F(town, "radius"); info.townBlockLen = CanonJson.F(town, "blockLen"); info.townBlockDepth = CanonJson.F(town, "blockDepth");

            info.roads = A(CanonJson.Get(w, "roads")).Select(o =>
            {
                var r = O(o);
                var pts = A(CanonJson.Get(r, "pts"));
                var road = new WorldInfo.Road
                {
                    id = CanonJson.I(r, "id"), kind = CanonJson.Str(r, "kind"), hw = CanonJson.F(r, "hw"), paved = CanonJson.Bool(r, "paved"),
                    startJ = CanonJson.Bool(r, "startJ"), endJ = CanonJson.Bool(r, "endJ"), len = CanonJson.Num(r, "len"),
                    x = new double[pts.Count], y = new double[pts.Count], z = new double[pts.Count], d = new double[pts.Count],
                };
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = A(pts[i]);
                    road.x[i] = CanonJson.Num(p[0]); road.y[i] = CanonJson.Num(p[1]); road.z[i] = CanonJson.Num(p[2]); road.d[i] = CanonJson.Num(p[3]);
                }
                return road;
            }).ToArray();

            info.lots = A(CanonJson.Get(w, "lots")).Select(o =>
            {
                var l = O(o);
                return new WorldInfo.Lot
                {
                    x = CanonJson.F(l, "x"), z = CanonJson.F(l, "z"), nx = CanonJson.F(l, "nx"), nz = CanonJson.F(l, "nz"), tx = CanonJson.F(l, "tx"), tz = CanonJson.F(l, "tz"),
                    w = CanonJson.F(l, "w"), depth = CanonJson.F(l, "depth"), roadY = CanonJson.F(l, "roadY"), padY = CanonJson.F(l, "padY"),
                    zone = CanonJson.Str(l, "zone"), hamlet = CanonJson.Get(l, "hamlet")?.ToString(), contiguous = CanonJson.Bool(l, "contiguous"),
                };
            }).ToArray();

            info.shotWidth = CanonJson.I(cams, "width"); info.shotHeight = CanonJson.I(cams, "height");
            info.cameraFov = CanonJson.F(cams, "fov"); info.cameraNear = CanonJson.F(cams, "near"); info.cameraFar = CanonJson.F(cams, "far");
            info.shotHours = CanonJson.Ints(CanonJson.Get(cams, "times"));
            info.cameras = A(CanonJson.Get(cams, "cameras")).Select(o =>
            {
                var c = O(o);
                var pos = CanonJson.Floats(CanonJson.Get(c, "pos"));
                var tgt = CanonJson.Floats(CanonJson.Get(c, "target"));
                var q = CanonJson.Floats(CanonJson.Get(c, "quat"));
                var shots = O(CanonJson.Get(c, "shots"));
                return new WorldInfo.CameraPose
                {
                    id = CanonJson.Str(c, "id"), name = CanonJson.Str(c, "name"),
                    pos = new Vector3(pos[0], pos[1], pos[2]), target = new Vector3(tgt[0], tgt[1], tgt[2]), quat = new Vector4(q[0], q[1], q[2], q[3]),
                    shot10 = CanonJson.Str(shots, "10"), shot21 = CanonJson.Str(shots, "21"),
                };
            }).ToArray();
            return info;
        }

        public static SurfaceTable BuildSurfaceTable(Dictionary<string, object> w)
        {
            var t = O(CanonJson.Get(w, "surfaceTable"));
            var s = ScriptableObject.CreateInstance<SurfaceTable>();
            s.names = CanonJson.Strings(CanonJson.Get(t, "names"));
            s.displayNames = CanonJson.Strings(CanonJson.Get(t, "displayNames"));
            s.softness = CanonJson.Floats(CanonJson.Get(t, "softness"));
            s.grip = CanonJson.Floats(CanonJson.Get(t, "grip"));
            s.digRate = CanonJson.Floats(CanonJson.Get(t, "digRate"));
            s.roughness = CanonJson.Floats(CanonJson.Get(t, "roughness"));
            s.dust = A(CanonJson.Get(t, "dust")).Select(o => { var c = CanonJson.Floats(o); return new Color(c[0], c[1], c[2], 1f); }).ToArray();
            s.mudMax = CanonJson.F(t, "mudMax", 1f);
            return s;
        }

        static Color32 Hex(string s)
        {
            s = s.TrimStart('#');
            int v = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Int(v);
        }

        static Color32 Int(long v) => new Color32((byte)((v >> 16) & 255), (byte)((v >> 8) & 255), (byte)(v & 255), 255);

        static void Colors(object o, out string[] slots, out Color32[] colors)
        {
            var d = O(o);
            if (d == null) { slots = Array.Empty<string>(); colors = Array.Empty<Color32>(); return; }
            slots = d.Keys.ToArray();
            colors = d.Values.Select(v => v is string s ? Hex(s) : Int((long)CanonJson.Num(v))).ToArray();
        }

        public static PlacementSet BuildPlacements(Dictionary<string, object> p)
        {
            var s = ScriptableObject.CreateInstance<PlacementSet>();
            s.buildings = A(CanonJson.Get(p, "buildings")).Select(o =>
            {
                var b = O(o);
                Colors(CanonJson.Get(b, "colors"), out var slots, out var cols);
                return new PlacementSet.Building
                {
                    model = CanonJson.Str(b, "model"), src = CanonJson.Str(b, "src"),
                    x = CanonJson.Num(b, "x"), y = CanonJson.Num(b, "y"), z = CanonJson.Num(b, "z"), rot = CanonJson.Num(b, "rot"),
                    sx = CanonJson.F(b, "sx"), sz = CanonJson.F(b, "sz"), w = CanonJson.F(b, "w"), d = CanonJson.F(b, "d"),
                    colorSlots = slots, colors = cols, lit = CanonJson.Str(b, "lit") ?? "",
                    panes = CanonJson.Strings(CanonJson.Get(b, "panes")), m = CanonJson.Doubles(CanonJson.Get(b, "M")),
                };
            }).ToArray();

            s.typed = A(CanonJson.Get(p, "typed")).Select(o => BuildTyped(O(o))).ToArray();
            s.lamps = A(CanonJson.Get(p, "lamps")).Select(o => { var l = O(o); return new PlacementSet.Lamp { x = CanonJson.F(l, "x"), y = CanonJson.F(l, "y"), z = CanonJson.F(l, "z"), gy = CanonJson.F(l, "gy") }; }).ToArray();
            s.parking = A(CanonJson.Get(p, "parking")).Select(o => { var l = O(o); return new PlacementSet.Parking { x = CanonJson.F(l, "x"), z = CanonJson.F(l, "z"), yaw = CanonJson.F(l, "yaw") }; }).ToArray();
            var rings = O(CanonJson.Get(p, "rings"));
            s.rings = rings == null ? Array.Empty<PlacementSet.Ring>() : rings.Select(kv => { var r = O(kv.Value); return new PlacementSet.Ring { poi = kv.Key, x = CanonJson.F(r, "x"), z = CanonJson.F(r, "z"), y = CanonJson.F(r, "y") }; }).ToArray();
            s.footprints = A(CanonJson.Get(p, "footprints")).Select(o =>
            {
                var f = O(o);
                return new PlacementSet.Footprint
                {
                    x = CanonJson.F(f, "x"), z = CanonJson.F(f, "z"), r = CanonJson.F(f, "r"), rot = CanonJson.F(f, "rot", 0f),
                    hw = CanonJson.F(f, "hw", 0f), hd = CanonJson.F(f, "hd", 0f), hasRect = CanonJson.Has(f, "hw"),
                };
            }).ToArray();
            PlacementSet.Boat Boat(object o) { var b = O(o); return new PlacementSet.Boat { x = CanonJson.F(b, "x"), z = CanonJson.F(b, "z"), ang = CanonJson.F(b, "ang"), phase = CanonJson.F(b, "phase", 0f) }; }
            s.boats = A(CanonJson.Get(p, "boats")).Select(Boat).ToArray();
            s.boatSpots = A(CanonJson.Get(p, "boatSpots")).Select(Boat).ToArray();
            s.colliders = A(CanonJson.Get(p, "colliders")).Select(o =>
            {
                var c = O(o);
                bool box = CanonJson.Str(c, "type") == "box";
                return new PlacementSet.Collider
                {
                    type = box ? PlacementSet.ColliderType.Box : PlacementSet.ColliderType.Circle, kind = CanonJson.Str(c, "kind"),
                    x = CanonJson.F(c, "x"), z = CanonJson.F(c, "z"), r = CanonJson.F(c, "r", 0f), hw = CanonJson.F(c, "hw", 0f), hd = CanonJson.F(c, "hd", 0f),
                    rot = CanonJson.F(c, "rot", 0f), y0 = CanonJson.F(c, "y0"), y1 = CanonJson.F(c, "y1"), tree = CanonJson.I(c, "tree", -1),
                };
            }).ToArray();
            s.boxes = A(CanonJson.Get(p, "boxes")).Select(o =>
            {
                var b = O(o);
                return new PlacementSet.Box { x = CanonJson.F(b, "x"), z = CanonJson.F(b, "z"), hw = CanonJson.F(b, "hw"), hd = CanonJson.F(b, "hd"), rot = CanonJson.F(b, "rot"), y0 = CanonJson.F(b, "y0"), y1 = CanonJson.F(b, "y1") };
            }).ToArray();
            s.opTypes = A(CanonJson.Get(p, "ops")).Select(o => A(o).FirstOrDefault() as string).ToArray();
            s.rngCalls = CanonJson.I(p, "rngCalls", 0);
            return s;
        }

        static PlacementSet.Typed BuildTyped(Dictionary<string, object> t)
        {
            var r = new PlacementSet.Typed { type = CanonJson.Str(t, "type"), name = CanonJson.Str(t, "name"), lit = CanonJson.Str(t, "lit"), poi = CanonJson.Str(t, "poi") };
            r.hasPose = CanonJson.Has(t, "x") && CanonJson.Has(t, "z");
            r.x = CanonJson.Num(t, "x", 0); r.y = CanonJson.Num(t, "y", 0); r.z = CanonJson.Num(t, "z", 0);
            r.hasRot = CanonJson.Has(t, "rot");
            r.rot = CanonJson.Num(t, "rot", 0);
            var keys = new List<string>();
            var vals = new List<double>();
            void Flatten(string prefix, object v)
            {
                switch (v)
                {
                    case double d: keys.Add(prefix); vals.Add(d); break;
                    case bool b: keys.Add(prefix); vals.Add(b ? 1 : 0); break;
                    case Dictionary<string, object> o: foreach (var kv in o) Flatten(prefix + "." + kv.Key, kv.Value); break;
                    case List<object> l: for (int i = 0; i < l.Count; i++) Flatten(prefix + "[" + i + "]", l[i]); break;
                }
            }
            foreach (var kv in t)
            {
                if (kv.Key == "type" || kv.Key == "name" || kv.Key == "colors" || kv.Key == "lit" || kv.Key == "poi") continue;
                if (kv.Key == "x" || kv.Key == "y" || kv.Key == "z" || kv.Key == "rot") continue;
                Flatten(kv.Key, kv.Value);
            }
            r.keys = keys.ToArray();
            r.values = vals.ToArray();
            Colors(CanonJson.Get(t, "colors"), out r.colorSlots, out r.colors);
            // dock records carry their pose inside q: expose it as the record's position too
            if (!r.hasPose && r.TryGet("q.x", out var qx) && r.TryGet("q.z", out var qz)) { r.x = qx; r.z = qz; }
            return r;
        }

        public static VegetationSet BuildVegetation(Dictionary<string, object> v)
        {
            var s = ScriptableObject.CreateInstance<VegetationSet>();
            s.species = A(CanonJson.Get(v, "species")).Select(o =>
            {
                var sp = O(o);
                var sc = CanonJson.Floats(CanonJson.Get(sp, "s"));
                return new VegetationSet.Species
                {
                    name = CanonJson.Str(sp, "name"), h = CanonJson.F(sp, "h"), trunk = CanonJson.F(sp, "trunk"), strength = CanonJson.F(sp, "strength"),
                    sMin = sc[0], sMax = sc[1], sway = CanonJson.F(sp, "sway"),
                };
            }).ToArray();
            s.trees = A(CanonJson.Get(v, "trees")).Select(o =>
            {
                var t = CanonJson.Doubles(o);
                return new VegetationSet.Tree { x = (float)t[0], y = (float)t[1], z = (float)t[2], sp = (int)t[3], s = (float)t[4], rot = (float)t[5], tint = (float)t[6], hue = (float)t[7] };
            }).ToArray();
            s.rocks = A(CanonJson.Get(v, "rocks")).Select(o =>
            {
                var t = CanonJson.Doubles(o);
                return new VegetationSet.Rock { x = (float)t[0], y = (float)t[1], z = (float)t[2], s = (float)t[3], rot = (float)t[4], v = (float)t[5] };
            }).ToArray();
            return s;
        }

        public static LegacyTrafficNetwork BuildTraffic(Dictionary<string, object> t)
        {
            var n = ScriptableObject.CreateInstance<LegacyTrafficNetwork>();
            n.nodes = A(CanonJson.Get(t, "nodes")).Select(o =>
            {
                var d = O(o);
                var node = new LegacyTrafficNetwork.Node
                {
                    id = CanonJson.I(d, "id"), x = CanonJson.F(d, "x"), z = CanonJson.F(d, "z"), r = CanonJson.F(d, "R"),
                    dead = CanonJson.Bool(d, "dead"), failed = CanonJson.Bool(d, "failed"),
                    arms = A(CanonJson.Get(d, "arms")).Select(a => { var ad = O(a); return new LegacyTrafficNetwork.Arm { edge = CanonJson.I(ad, "edge"), @out = CanonJson.I(ad, "out"), ang = CanonJson.F(ad, "ang") }; }).ToArray(),
                    major = CanonJson.Ints(CanonJson.Get(d, "major")),
                    inLanes = CanonJson.Ints(CanonJson.Get(d, "inLanes")), outLanes = CanonJson.Ints(CanonJson.Get(d, "outLanes")), conns = CanonJson.Ints(CanonJson.Get(d, "conns")),
                };
                if (O(CanonJson.Get(d, "rb")) is Dictionary<string, object> rb)
                {
                    var c = O(CanonJson.Get(rb, "C"));
                    node.hasRoundabout = true;
                    node.rbCx = CanonJson.F(c, "x"); node.rbCz = CanonJson.F(c, "z"); node.rbR = CanonJson.F(rb, "R");
                    node.rbClear = CanonJson.F(rb, "clear", 0f); node.rbRing = CanonJson.F(rb, "ring", 0f); node.rbScore = CanonJson.F(rb, "score", 0f);
                }
                return node;
            }).ToArray();
            n.edges = A(CanonJson.Get(t, "edges")).Select(o =>
            {
                var d = O(o);
                var pts = A(CanonJson.Get(d, "pts"));
                var flat = new float[pts.Count * 2];
                for (int i = 0; i < pts.Count; i++) { var p = CanonJson.Floats(pts[i]); flat[i * 2] = p[0]; flat[i * 2 + 1] = p[1]; }
                return new LegacyTrafficNetwork.Edge { id = CanonJson.I(d, "id"), a = CanonJson.I(d, "a"), b = CanonJson.I(d, "b"), road = CanonJson.I(d, "road"), pts = flat };
            }).ToArray();
            n.segs = A(CanonJson.Get(t, "segs")).Select(o =>
            {
                var d = O(o);
                return new LegacyTrafficNetwork.Seg
                {
                    id = CanonJson.I(d, "id"), kind = CanonJson.Str(d, "kind"), n = CanonJson.I(d, "n", 0), len = CanonJson.F(d, "len"), kMax = CanonJson.F(d, "kMax", 0f),
                    x = CanonJson.Floats(CanonJson.Get(d, "x")), z = CanonJson.Floats(CanonJson.Get(d, "z")), s = CanonJson.Floats(CanonJson.Get(d, "s")),
                    k = CanonJson.Floats(CanonJson.Get(d, "k")), cap = CanonJson.Floats(CanonJson.Get(d, "cap")),
                    next = CanonJson.Ints(CanonJson.Get(d, "next")), compat = CanonJson.Ints(CanonJson.Get(d, "compat")),
                    edge = CanonJson.I(d, "edge", -1), from = CanonJson.I(d, "from", -1), to = CanonJson.I(d, "to", -1), node = CanonJson.I(d, "node", -1),
                    dir = CanonJson.I(d, "dir", 0), turn = CanonJson.I(d, "turn", 0), yard = CanonJson.I(d, "yard", -1),
                    minor = CanonJson.Bool(d, "minor"), major = CanonJson.Bool(d, "major"), noTruck = CanonJson.Bool(d, "noTruck"), rb = CanonJson.Bool(d, "rb"),
                    phIn = CanonJson.F(d, "phIn"), phOut = CanonJson.F(d, "phOut"), rRadius = CanonJson.F(d, "R"), clear = CanonJson.F(d, "clear"),
                };
            }).ToArray();
            n.lanes = CanonJson.Ints(CanonJson.Get(t, "lanes"));
            n.conns = CanonJson.Ints(CanonJson.Get(t, "conns"));
            n.loops = CanonJson.Ints(CanonJson.Get(t, "loops"));
            n.skipped = A(CanonJson.Get(t, "skipped")).Count;
            return n;
        }
    }
}
