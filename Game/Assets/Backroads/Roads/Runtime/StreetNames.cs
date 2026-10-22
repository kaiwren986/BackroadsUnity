using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>A street: the roads one name runs along (through bends, joins, class changes and straight across junctions).</summary>
    public sealed class StreetInfo
    {
        public int id;
        public string name = "";
        /// <summary>True until the user approves the name list: the name is "Street NN", a stand-in for blades and the map.</summary>
        public bool placeholder;
        public readonly List<int> roads = new List<int>(), edges = new List<int>();
        /// <summary>The smallest OSM way of the street ("way/123"): its stable key across bakes.</summary>
        public string firstWay = "";
        public long firstWayId;
    }

    /// <summary>
    /// roads-R3 step 3: one fictional name per street (ADR-006 §6: the town's real street names never ship and the extract
    /// has none). A street is a chain of edges: two named arms of a node continue each other when they are the node's only
    /// named arms (bends, joins, class changes, a T whose third arm is an alley) or, at a junction, when they are the
    /// straightest pair of the same kind (through roads with through roads, streets with streets) bending at most 35°,
    /// the node's through road first. Streets are numbered by their smallest OSM way id, so a name stays with its street
    /// across bakes; names come from our own list in that order. Service roads and driveways are not named.
    /// </summary>
    public static class StreetNamer
    {
        public const double MaxBendDeg = 35.0;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static bool Named(RoadClass c) => c == RoadClass.Highway || c == RoadClass.MainStreet || c == RoadClass.Street || c == RoadClass.Dirt;

        static int Family(RoadClass c) => RoadClasses.IsThroughRoad(c) ? 0 : c == RoadClass.Street ? 1 : 2;

        public static List<StreetInfo> Assign(RoadGraph g, IReadOnlyList<string> names, bool approved)
        {
            // union-find over edges
            var parent = new int[g.edges.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

            foreach (var n in g.nodes)
            {
                var named = new List<int>();
                for (int k = 0; k < n.Degree; k++) if (Named(g.Arm(n, k).cls)) named.Add(k);
                if (named.Count < 2) continue;
                if (named.Count == 2)
                {
                    Union(g.Arm(n, named[0]).edge, g.Arm(n, named[1]).edge);
                    continue;
                }
                var used = new HashSet<int>();
                var pairs = new List<(double bend, int a, int b)>();
                for (int i = 0; i < named.Count; i++)
                    for (int j = i + 1; j < named.Count; j++)
                    {
                        var A = g.Arm(n, named[i]);
                        var B = g.Arm(n, named[j]);
                        if (Family(A.cls) != Family(B.cls)) continue;
                        double bend = Geo.Bend(A.angle, B.angle) / Geo.Deg;
                        if (bend > MaxBendDeg) continue;
                        bool major = (named[i] == n.majorA && named[j] == n.majorB) || (named[i] == n.majorB && named[j] == n.majorA);
                        pairs.Add((major ? -1 : bend, named[i], named[j]));
                    }
                pairs.Sort((x, y) =>
                {
                    int r = x.bend.CompareTo(y.bend);
                    if (r != 0) return r;
                    r = x.a.CompareTo(y.a);
                    return r != 0 ? r : x.b.CompareTo(y.b);
                });
                foreach (var (_, a, b) in pairs)
                {
                    if (used.Contains(a) || used.Contains(b)) continue;
                    used.Add(a); used.Add(b);
                    Union(g.Arm(n, a).edge, g.Arm(n, b).edge);
                }
            }

            var byRoot = new Dictionary<int, StreetInfo>();
            foreach (var e in g.edges)
            {
                if (!Named(e.cls)) continue;
                int root = Find(e.id);
                if (!byRoot.TryGetValue(root, out var s)) byRoot[root] = s = new StreetInfo();
                s.edges.Add(e.id);
                if (!s.roads.Contains(e.sourceRoad)) s.roads.Add(e.sourceRoad);
                var road = g.RoadOf(e.sourceRoad);
                if (road?.osmWays != null)
                    foreach (var w in road.osmWays)
                    {
                        long id = WayId(w);
                        if (id > 0 && (s.firstWayId == 0 || id < s.firstWayId)) { s.firstWayId = id; s.firstWay = w; }
                    }
            }
            var streets = new List<StreetInfo>(byRoot.Values);
            foreach (var s in streets) s.roads.Sort();
            streets.Sort((a, b) =>
            {
                long x = a.firstWayId == 0 ? long.MaxValue : a.firstWayId, y = b.firstWayId == 0 ? long.MaxValue : b.firstWayId;
                int r = x.CompareTo(y);
                return r != 0 ? r : a.roads[0].CompareTo(b.roads[0]);
            });
            for (int i = 0; i < streets.Count; i++)
            {
                var s = streets[i];
                s.id = i;
                bool listed = approved && names != null && i < names.Count && !string.IsNullOrWhiteSpace(names[i]);
                s.placeholder = !listed;
                s.name = listed ? names[i].Trim() : string.Format(Inv, "Street {0:D2}", i + 1);
            }
            return streets;
        }

        static long WayId(string w)
        {
            if (string.IsNullOrEmpty(w)) return 0;
            int slash = w.IndexOf('/');
            return long.TryParse(slash >= 0 ? w.Substring(slash + 1) : w, NumberStyles.Integer, Inv, out long id) ? id : 0;
        }
    }
}
