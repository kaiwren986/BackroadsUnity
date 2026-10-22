using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Backroads.Build.Editor;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-overrides (05 §3, roads-R1 step 7): hand-authored, versioned per town, keyed by OSM node id so a re-bake keeps
    /// them. Assets/Backroads/Roads/Overrides/roads-overrides.(town).json:
    ///   { "format": "backroads-road-overrides", "version": 1, "town": "...",
    ///     "nodes": [ { "osmNode": 123, "radius": 6.0, "reason": "..." },
    ///                { "osmNode": 456, "polygon": [[x, z], ...], "reason": "..." },
    ///                { "osmNode": 789, "ribbons": true, "reason": "..." } ] }
    /// Every override needs a reason; the bake report lists each one it used.
    /// </summary>
    public static class RoadOverrides
    {
        public const string Dir = "Assets/Backroads/Roads/Overrides";

        public static string PathFor(string townId) => Path.Combine(RepoPaths.Game, Dir, "roads-overrides." + townId + ".json");

        public static Dictionary<long, NodeOverride> Load(string townId)
        {
            var map = new Dictionary<long, NodeOverride>();
            var path = PathFor(townId);
            if (!File.Exists(path)) return map;
            var root = BrJson.Obj(BrJson.Parse(File.ReadAllText(path, Encoding.UTF8)), path);
            if (BrJson.Str(root, "format") != "backroads-road-overrides") throw new InvalidDataException(path + ": format is not backroads-road-overrides");
            foreach (var o in BrJson.Arr(root["nodes"], "nodes"))
            {
                var d = BrJson.Obj(o);
                var ov = new NodeOverride { osmNode = Convert.ToInt64(d["osmNode"]), reason = BrJson.Str(d, "reason") ?? "" };
                if (ov.reason.Length == 0) throw new InvalidDataException($"{path}: the override for osm node {ov.osmNode} has no reason");
                if (d.TryGetValue("radius", out var r) && r != null) ov.radius = Convert.ToDouble(r);
                if (d.TryGetValue("ribbons", out var rb) && rb is bool b) ov.ribbons = b;
                if (d.TryGetValue("polygon", out var po) && po != null)
                {
                    var l = new List<D2>();
                    foreach (var p in BrJson.Arr(po)) { var a = BrJson.Arr(p); l.Add(new D2(Convert.ToDouble(a[0]), Convert.ToDouble(a[1]))); }
                    ov.polygon = l.ToArray();
                }
                if (map.ContainsKey(ov.osmNode)) throw new InvalidDataException($"{path}: osm node {ov.osmNode} appears twice");
                map[ov.osmNode] = ov;
            }
            return map;
        }
    }
}
