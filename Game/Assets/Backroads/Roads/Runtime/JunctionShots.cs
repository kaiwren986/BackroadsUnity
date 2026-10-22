using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Backroads.Roads
{
    /// <summary>
    /// The cameras of the m3.shots.junctions autotest (05 §8.8, the M3 gate's screenshots of the roads): eight
    /// representative places of a town's road network, picked by rule from the baked network so they follow any re-bake.
    ///   main-street   a downtown junction on the main street, seen from a side street that stops at it (crosswalks, stop line, STOP);
    ///   residential-x a four-way junction of local streets outside downtown, from a stopping approach;
    ///   alley-mouth   an alley meeting a kerbed street (dropped-kerb mouth, no STOP), from the street;
    ///   gravel-apron  a gravel or dirt road stopping at a paved one (its paved apron, stop line and STOP), from the gravel road;
    ///   bridge        the longest bridge from the road before one end, looking along its deck (abutment, parapets, piers);
    ///   town-edge     a class change where the town's kerbs end at a country road (the kerb taper), from the country side;
    ///   complex-pair  the closest pair of junctions kept as two (05 §5.1 step 5), from above one approach;
    ///   rural-t       a T on a country highway outside the town, from its stopping approach.
    /// Each camera stands over its approach's right lane (the side its sign is on), some metres above the road, and looks at
    /// the junction. Every place is a different node; the choice is deterministic (scores, then the lowest node id).
    /// Positions are the network's (Unity world space; the scene puts the road chunks at the same place).
    /// </summary>
    public static class JunctionShots
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The ids in shot order.</summary>
        public static readonly string[] Ids = { "main-street", "residential-x", "alley-mouth", "gravel-apron", "bridge", "town-edge", "complex-pair", "rural-t" };

        public struct Shot
        {
            public string id, what;
            /// <summary>The node it looks at (for the bridge, the deck's end node), and the approach (position in the node's arms) the camera stands on, or -1.</summary>
            public int node, arm;
            public Vector3 position, target;
            /// <summary>Why this place: the rule's numbers, for the evidence list.</summary>
            public string why;
        }

        public static List<Shot> Pick(RoadNetworkAsset a, out List<string> missing)
        {
            var shots = new List<Shot>();
            var none = new List<string>();
            missing = none;
            var used = new HashSet<int>();
            var ctx = new Ctx(a);
            void Add(string id, Shot? s)
            {
                if (s == null) { none.Add(id); return; }
                var v = s.Value;
                v.id = id;
                used.Add(v.node);
                shots.Add(v);
            }
            Add("main-street", MainStreet(ctx, used));
            Add("residential-x", ResidentialX(ctx, used));
            Add("alley-mouth", AlleyMouth(ctx, used));
            Add("gravel-apron", GravelApron(ctx, used));
            Add("bridge", Bridge(ctx, used));
            Add("town-edge", TownEdge(ctx, used));
            Add("complex-pair", ComplexPair(ctx, used));
            Add("rural-t", RuralT(ctx, used));
            return shots;
        }

        public static string Describe(Shot s) => string.Format(Inv, "{0}: node {1}, {2}; camera ({3:F1}, {4:F1}, {5:F1}) looking at ({6:F1}, {7:F1}, {8:F1}); {9}",
            s.id, s.node, s.what, s.position.x, s.position.y, s.position.z, s.target.x, s.target.y, s.target.z, s.why);

        // ------------------------------------------------------------------ the rules

        sealed class Ctx
        {
            public readonly RoadNetworkAsset a;
            readonly Dictionary<int, RoadNetworkAsset.Road> m_Roads = new Dictionary<int, RoadNetworkAsset.Road>();

            public Ctx(RoadNetworkAsset asset)
            {
                a = asset;
                foreach (var r in a.roads) m_Roads[r.id] = r;
            }

            public RoadNetworkAsset.Road Road(in RoadNetworkAsset.Arm arm) => m_Roads.TryGetValue(arm.sourceRoad, out var r) ? r : null;
            public bool Paved(in RoadNetworkAsset.Arm arm) => KerbRules.Paved(KerbRules.Surface(arm.cls, Road(arm)?.surface));
            public bool Alley(in RoadNetworkAsset.Arm arm) => Road(arm)?.service == "alley";
            public bool Kerbed(in RoadNetworkAsset.Arm arm) => Road(arm)?.inKerbZone == true && (arm.cls == RoadClass.Street || arm.cls == RoadClass.MainStreet);
            public bool Stops(in RoadNetworkAsset.Arm arm) => arm.control == Control.MinorStop || arm.control == Control.AllWayStop || arm.control == Control.Signal;

            public RoadNetworkAsset.Arm[] Arms(in RoadNetworkAsset.Node n)
            {
                var l = new RoadNetworkAsset.Arm[n.armCount];
                for (int k = 0; k < n.armCount; k++) l[k] = a.arms[n.firstArm + k];
                return l;
            }

            public bool In(Vector2[] zone, Vector3 p) => zone != null && zone.Length >= 3 && InPolygon(zone, p.x, p.z);
        }

        static Shot? MainStreet(Ctx c, HashSet<int> used)
        {
            Shot? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var n in c.a.nodes)
            {
                if (n.kind != NodeKind.Junction || n.armCount < 3 || used.Contains(n.id) || !c.In(c.a.downtownZone, n.position)) continue;
                var arms = c.Arms(n);
                int main = 0, crosswalks = 0, side = -1;
                for (int k = 0; k < arms.Length; k++)
                {
                    if (arms[k].cls == RoadClass.MainStreet) main++;
                    if (arms[k].crosswalk) crosswalks++;
                    if (arms[k].cls != RoadClass.MainStreet && c.Stops(arms[k]) && c.Paved(arms[k]) && (side < 0 || arms[k].length > arms[side].length)) side = k;
                }
                if (main < 2 || side < 0) continue;
                double score = crosswalks * 10 + arms.Length * 5 - ((n.flags & NodeFlags.ComplexPair) != 0 ? 20 : 0) + Math.Min(arms[side].length, 40) * 0.1;
                if (score > bestScore) { bestScore = score; best = From(c, n, side, 22, 1.2f, 4.5f, "the main street from a side street that stops at it", $"{arms.Length} arms, {crosswalks} crosswalks, side street R{arms[side].sourceRoad} stops"); }
            }
            return best;
        }

        static Shot? ResidentialX(Ctx c, HashSet<int> used)
        {
            Shot? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var n in c.a.nodes)
            {
                if (n.kind != NodeKind.Junction || n.armCount != 4 || used.Contains(n.id) || (n.flags & NodeFlags.ComplexPair) != 0 || c.In(c.a.downtownZone, n.position)) continue;
                var arms = c.Arms(n);
                bool ok = true;
                double minLen = double.MaxValue;
                int stop = -1;
                for (int k = 0; k < 4; k++)
                {
                    if (arms[k].cls != RoadClass.Street || c.Alley(arms[k]) || !c.Paved(arms[k]) || !c.Kerbed(arms[k])) ok = false;
                    minLen = Math.Min(minLen, arms[k].length);
                    if (c.Stops(arms[k]) && (stop < 0 || arms[k].length > arms[stop].length)) stop = k;
                }
                if (!ok) continue;
                int cam = stop >= 0 ? stop : Longest(arms);
                double score = Math.Min(minLen, 60) + (stop >= 0 ? 100 : 0);
                if (score > bestScore) { bestScore = score; best = From(c, n, cam, 22, 1.2f, 4.5f, "four local streets meeting", string.Format(Inv, "4 kerbed street arms, the shortest {0:F0} m{1}", minLen, stop >= 0 ? ", from a stopping approach" : "")); }
            }
            return best;
        }

        static Shot? AlleyMouth(Ctx c, HashSet<int> used)
        {
            Shot? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var n in c.a.nodes)
            {
                if (n.kind != NodeKind.Junction || n.armCount != 3 || used.Contains(n.id) || (n.flags & NodeFlags.ComplexPair) != 0) continue;
                var arms = c.Arms(n);
                int alley = -1, alleys = 0;
                bool streets = true;
                for (int k = 0; k < arms.Length; k++)
                {
                    if (c.Alley(arms[k])) { alley = k; alleys++; }
                    else if (!c.Kerbed(arms[k]) || !c.Paved(arms[k])) streets = false;
                }
                if (alleys != 1 || !streets) continue;
                // the street arm most square to the alley: the camera looks along the street at the mouth on its side
                int cam = -1;
                double bestDot = double.MaxValue, minLen = double.MaxValue;
                for (int k = 0; k < arms.Length; k++)
                {
                    if (k == alley) continue;
                    minLen = Math.Min(minLen, arms[k].length);
                    double d = Math.Abs(Vector2.Dot(arms[k].dir, arms[alley].dir));
                    if (d < bestDot - 1e-6 || (Math.Abs(d - bestDot) <= 1e-6 && arms[k].length > arms[cam].length)) { bestDot = d; cam = k; }
                }
                double score = Math.Min(minLen, 60) - bestDot * 20;
                if (score > bestScore)
                {
                    bestScore = score;
                    // from the far half of the street, on the alley's line, looking into the mouth
                    var ad = new Vector3(arms[alley].dir.x, 0, arms[alley].dir.y).normalized;
                    best = new Shot
                    {
                        node = n.id, arm = -1, position = n.position - ad * (arms[cam].halfWidth + 2.5f) + Vector3.up * 5f, target = n.position + ad * 10f + Vector3.up * 0.5f,
                        what = "an alley's dropped-kerb mouth on a kerbed street, from across the street",
                        why = $"alley R{arms[alley].sourceRoad} meets street R{arms[cam].sourceRoad}",
                    };
                }
            }
            return best;
        }

        static Shot? GravelApron(Ctx c, HashSet<int> used)
        {
            Shot? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var n in c.a.nodes)
            {
                if (n.armCount < 3 || used.Contains(n.id) || n.kind == NodeKind.Roundabout) continue;
                var arms = c.Arms(n);
                bool paved = false;
                foreach (var x in arms) paved |= c.Paved(x);
                if (!paved) continue;
                for (int k = 0; k < arms.Length; k++)
                {
                    if (c.Paved(arms[k]) || c.Alley(arms[k]) || !c.Stops(arms[k])) continue;
                    // square on to the paved road it meets reads best (a shallow Y hides the apron)
                    double square = 1;
                    for (int j = 0; j < arms.Length; j++)
                        if (j != k && c.Paved(arms[j])) square = Math.Min(square, 1 - Math.Abs(Vector2.Dot(arms[k].dir, arms[j].dir)));
                    double score = Math.Min(arms[k].length, 80) - Math.Abs(arms[k].grade) * 200 + square * 60;
                    if (score > bestScore) { bestScore = score; best = From(c, n, k, 26, 1.0f, 4.0f, "a gravel road stopping at a paved one", string.Format(Inv, "unpaved R{0} ({1}), {2:F0} m, grade {3:+0.0;-0.0} %", arms[k].sourceRoad, c.Road(arms[k])?.surface is string s && s.Length > 0 ? s : arms[k].cls.ToString(), arms[k].length, arms[k].grade * 100)); }
                }
            }
            return best;
        }

        static Shot? Bridge(Ctx c, HashSet<int> used)
        {
            int best = -1;
            for (int i = 0; i < c.a.decks.Length; i++)
                if (c.a.decks[i].edges != null && c.a.decks[i].edges.Length > 0 && (best < 0 || c.a.decks[i].length > c.a.decks[best].length)) best = i;
            if (best < 0) return null;
            var d = c.a.decks[best];
            // the deck as one polyline, its pieces oriented end to end
            var pts = new List<Vector3>();
            int startNode = -1, endNode = -1;
            foreach (int eid in d.edges)
            {
                var e = EdgeById(c.a, eid);
                if (e == null || e.points == null || e.points.Length < 2) continue;
                var p = (Vector3[])e.points.Clone();
                int a = e.nodeA, b = e.nodeB;
                if (pts.Count > 0)
                {
                    var last = pts[pts.Count - 1];
                    if ((p[p.Length - 1] - last).sqrMagnitude < (p[0] - last).sqrMagnitude) { Array.Reverse(p); (a, b) = (b, a); }
                }
                else if (d.edges.Length > 1)
                {
                    var next = EdgeById(c.a, d.edges[1]);
                    if (next != null && next.points != null && next.points.Length > 0)
                    {
                        float toStart = Math.Min((next.points[0] - p[0]).sqrMagnitude, (next.points[next.points.Length - 1] - p[0]).sqrMagnitude);
                        float toEnd = Math.Min((next.points[0] - p[p.Length - 1]).sqrMagnitude, (next.points[next.points.Length - 1] - p[p.Length - 1]).sqrMagnitude);
                        if (toStart < toEnd) { Array.Reverse(p); (a, b) = (b, a); }
                    }
                }
                if (startNode < 0) startNode = a;
                endNode = b;
                pts.AddRange(p);
            }
            if (pts.Count < 2) return null;
            // stand before the west end (the lower x), deterministically
            if (pts[pts.Count - 1].x < pts[0].x) { pts.Reverse(); (startNode, endNode) = (endNode, startNode); }
            var p0 = pts[0];
            var ahead = Along(pts, 40);
            var dir = new Vector3(ahead.x - p0.x, 0, ahead.z - p0.z).normalized;
            var pos = p0 - dir * 24 + Vector3.up * 5;
            var target = Along(pts, 60) + Vector3.up * 1.0f;
            if (used.Contains(startNode)) return null;
            return new Shot
            {
                node = startNode, arm = -1, position = pos, target = target, what = "the river bridge from the road before its west end, looking along the deck",
                why = string.Format(Inv, "deck of {0:F0} m over the {1} ({2} edges, {3} piers)", d.length, string.IsNullOrEmpty(d.over) ? "gap" : d.over, d.edges.Length, d.piers?.Length ?? 0),
            };
        }

        static Shot? TownEdge(Ctx c, HashSet<int> used)
        {
            Shot? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var n in c.a.nodes)
            {
                if (n.kind != NodeKind.ClassChange || n.armCount != 2 || used.Contains(n.id)) continue;
                var arms = c.Arms(n);
                int town = c.Kerbed(arms[0]) && !c.Kerbed(arms[1]) ? 0 : c.Kerbed(arms[1]) && !c.Kerbed(arms[0]) ? 1 : -1;
                if (town < 0 || !c.Paved(arms[0]) || !c.Paved(arms[1])) continue;
                int country = 1 - town;
                double score = Math.Min(arms[country].length, 80) - Math.Abs(arms[country].grade) * 300 - Math.Abs(arms[town].grade) * 300;
                if (score > bestScore) { bestScore = score; best = From(c, n, country, 20, 1.0f, 3.0f, "the town's kerbs ending at a country road (the kerb taper)", string.Format(Inv, "R{0} ({1}) becomes kerbed R{2} ({3})", arms[country].sourceRoad, arms[country].cls, arms[town].sourceRoad, arms[town].cls)); }
            }
            return best;
        }

        static Shot? ComplexPair(Ctx c, HashSet<int> used)
        {
            RoadNetworkAsset.Pair? best = null;
            foreach (var p in c.a.complexPairs)
            {
                if (used.Contains(p.a) || used.Contains(p.b)) continue;
                if (NodeById(c.a, p.a) is not RoadNetworkAsset.Node na || NodeById(c.a, p.b) is not RoadNetworkAsset.Node nb) continue;
                if (na.kind != NodeKind.Junction || nb.kind != NodeKind.Junction) continue;
                if (best == null || p.distance < best.Value.distance - 1e-4 || (Math.Abs(p.distance - best.Value.distance) <= 1e-4 && Math.Min(p.a, p.b) < Math.Min(best.Value.a, best.Value.b))) best = p;
            }
            if (best == null) return null;
            var pr = best.Value;
            var a = NodeById(c.a, Math.Min(pr.a, pr.b)).Value;
            var b = NodeById(c.a, Math.Max(pr.a, pr.b)).Value;
            var arms = c.Arms(a);
            // an approach of the first junction that does not lead to the second, the longest
            int cam = -1;
            for (int k = 0; k < arms.Length; k++)
            {
                var e = EdgeById(c.a, arms[k].edge);
                if (e == null || e.nodeA == b.id || e.nodeB == b.id) continue;
                if (cam < 0 || arms[k].length > arms[cam].length) cam = k;
            }
            if (cam < 0) return null;
            var s = From(c, a, cam, 30, 1.0f, 8.0f, "two junctions close together kept as two", string.Format(Inv, "nodes {0} and {1}, {2:F1} m apart", a.id, b.id, pr.distance));
            var v = s.Value;
            v.target = (a.position + b.position) * 0.5f + Vector3.up * 0.8f;
            return v;
        }

        static Shot? RuralT(Ctx c, HashSet<int> used)
        {
            Shot? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var n in c.a.nodes)
            {
                if (n.kind != NodeKind.Junction || n.armCount != 3 || !n.HasMajor || used.Contains(n.id) || c.In(c.a.townZone, n.position)) continue;
                if ((n.flags & (NodeFlags.ComplexPair | NodeFlags.PlayAreaEdge)) != 0) continue;
                var arms = c.Arms(n);
                if (arms[n.majorA].cls != RoadClass.Highway || arms[n.majorB].cls != RoadClass.Highway) continue;
                int minor = 3 - n.majorA - n.majorB;
                if (minor < 0 || minor > 2 || !c.Stops(arms[minor]) || !c.Paved(arms[minor])) continue;
                double score = Math.Min(Math.Min(arms[0].length, Math.Min(arms[1].length, arms[2].length)), 80) - Math.Abs(arms[minor].grade) * 200;
                if (score > bestScore) { bestScore = score; best = From(c, n, minor, 25, 1.0f, 4.0f, "a T on the country highway, from its stopping side road", $"highway R{arms[n.majorA].sourceRoad}/R{arms[n.majorB].sourceRoad}, side road R{arms[minor].sourceRoad} stops"); }
            }
            return best;
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>A camera on approach k of node n: dist metres out along the arm's centreline (at least 9 m behind its stop line), u metres to the approaching traffic's right, h over the road, looking at the node.</summary>
        static Shot? From(Ctx c, in RoadNetworkAsset.Node n, int k, float dist, float u, float h, string what, string why)
        {
            var arm = c.a.arms[n.firstArm + k];
            var e = EdgeById(c.a, arm.edge);
            if (e == null || e.points == null || e.points.Length < 2) return null;
            var pts = new List<Vector3>(e.points);
            if (!arm.atA) pts.Reverse();
            // behind the stop line and its sign (a sign set back by a far crosswalk stands up to 25 m out)
            if (arm.sStop >= 0) dist = Math.Max(dist, arm.sStop + 9f);
            var p = Along(pts, dist);
            var ahead = Along(pts, Math.Max(0, dist - 2));
            var outward = new Vector3(p.x - ahead.x, 0, p.z - ahead.z);
            if (outward.sqrMagnitude < 1e-8) outward = new Vector3(arm.dir.x, 0, arm.dir.y);
            outward.Normalize();
            // +u = left of outward = right of the approaching traffic (where its sign stands)
            var left = new Vector3(-outward.z, 0, outward.x);
            var pos = p + left * u + Vector3.up * h;
            return new Shot { node = n.id, arm = k, position = pos, target = n.position + Vector3.up * 0.8f, what = what, why = why };
        }

        /// <summary>The point dist metres along a polyline (straight on past its end).</summary>
        static Vector3 Along(List<Vector3> pts, float dist)
        {
            float s = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                float l = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (s + l >= dist && l > 1e-6) return Vector3.Lerp(a, b, (dist - s) / l);
                s += l;
            }
            var end = pts[pts.Count - 1];
            var prev = pts[pts.Count - 2];
            var d = new Vector3(end.x - prev.x, 0, end.z - prev.z);
            return d.sqrMagnitude > 1e-8 ? end + d.normalized * (dist - s) : end;
        }

        static int Longest(RoadNetworkAsset.Arm[] arms)
        {
            int best = 0;
            for (int k = 1; k < arms.Length; k++) if (arms[k].length > arms[best].length) best = k;
            return best;
        }

        static RoadNetworkAsset.Edge EdgeById(RoadNetworkAsset a, int id)
        {
            if (id >= 0 && id < a.edges.Length && a.edges[id].id == id) return a.edges[id];
            foreach (var e in a.edges) if (e.id == id) return e;
            return null;
        }

        static RoadNetworkAsset.Node? NodeById(RoadNetworkAsset a, int id)
        {
            if (id >= 0 && id < a.nodes.Length && a.nodes[id].id == id) return a.nodes[id];
            foreach (var n in a.nodes) if (n.id == id) return n;
            return null;
        }

        static bool InPolygon(Vector2[] poly, float x, float z)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > z) != (poly[j].y > z) && x < (poly[j].x - poly[i].x) * (z - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }
    }
}
