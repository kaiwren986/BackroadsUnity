using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Backroads.Roads
{
    /// <summary>Checks over a built graph (05 §8 for R0) and a text report in the form of 05 §1.2's node table.</summary>
    public static class RoadGraphChecks
    {
        /// <summary>True when every node is reachable from node 0 (pavedOnly: over non-dirt edges, counting only nodes with a paved arm).</summary>
        public static bool Connected(RoadGraph g, bool pavedOnly, out int reached, out int total)
        {
            var adj = new List<int>[g.nodes.Count];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
            var counted = new bool[g.nodes.Count];
            foreach (var e in g.edges)
            {
                if (pavedOnly && !RoadClasses.IsPaved(e.cls)) continue;
                adj[e.nodeA].Add(e.nodeB);
                adj[e.nodeB].Add(e.nodeA);
                counted[e.nodeA] = counted[e.nodeB] = true;
            }
            total = 0;
            int start = -1;
            for (int i = 0; i < counted.Length; i++)
                if (counted[i] || !pavedOnly) { total++; if (start < 0) start = i; }
            reached = 0;
            if (start < 0) return true;
            var seen = new bool[g.nodes.Count];
            var stack = new Stack<int>();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                reached++;
                foreach (int v in adj[u])
                    if (!seen[v]) { seen[v] = true; stack.Push(v); }
            }
            return reached == total;
        }

        /// <summary>
        /// Every junction-like node with a major road has it on the highest-ranked arms (class first, 05 §5.1 step 7); one
        /// without has a reason (equal classes under an all-way stop, or a bend over the limit; complex pairs take the same rules since roads-R4).
        /// </summary>
        public static List<string> MajorProblems(RoadGraph g)
        {
            var bad = new List<string>();
            foreach (var n in g.nodes)
            {
                if (!n.IsJunctionLike) continue;
                // mapped signs decide the through road where they exist (roads-R0o step 5); the report lists every override
                if (n.controlSource == ControlSource.Tag) continue;
                int top = 0;
                foreach (var id in n.arms) top = Math.Max(top, RoadClasses.Rank(g.arms[id].cls));
                if (n.HasMajor)
                {
                    GraphArm a = g.Arm(n, n.majorA), b = g.Arm(n, n.majorB);
                    if (RoadClasses.Rank(a.cls) != top || RoadClasses.Rank(b.cls) != top)
                        bad.Add($"node {n.id}: major R{a.sourceRoad}/{a.cls} + R{b.sourceRoad}/{b.cls} is not the highest class at the node");
                    if (n.bestPairBend > g.settings.majorMaxBend)
                        bad.Add($"node {n.id}: major bends {n.bestPairBend / Geo.Deg:F1} degrees, over the limit");
                }
                else
                {
                    bool allSame = true;
                    foreach (var id in n.arms) if (g.arms[id].cls != g.Arm(n, 0).cls) allSame = false;
                    bool ok = (allSame && (n.control == Control.AllWayStop || n.control == Control.Signal))
                              || n.bestPairBend > g.settings.majorMaxBend;
                    if (!ok) bad.Add($"node {n.id} ({n.kind}): no major road and no reason for it");
                }
            }
            return bad;
        }

        /// <summary>Every approach that must stop or yield has exactly one control, and every major arm drives through (none).</summary>
        public static List<string> ControlProblems(RoadGraph g)
        {
            var bad = new List<string>();
            foreach (var n in g.nodes)
            {
                if (!n.IsJunctionLike && n.kind != NodeKind.DirtApron) continue;
                for (int k = 0; k < n.arms.Count; k++)
                {
                    var a = g.Arm(n, k);
                    bool major = k == n.majorA || k == n.majorB;
                    if (major && a.control != Control.None) bad.Add($"node {n.id}: major arm R{a.sourceRoad} has control {a.control}");
                    if ((a.control == Control.None) != (a.controlSource == ControlSource.None))
                        bad.Add($"node {n.id}: arm R{a.sourceRoad} control {a.control} with source {a.controlSource}");
                    // one kind of junction: all-way stops and signals on every approach or none
                    bool everyArm = n.control == Control.AllWayStop || n.control == Control.Signal;
                    if (everyArm && a.control != n.control) bad.Add($"node {n.id}: {n.control} junction, arm R{a.sourceRoad} has {a.control}");
                    if (!everyArm && (a.control == Control.AllWayStop || a.control == Control.Signal))
                        bad.Add($"node {n.id}: {n.control} junction, arm R{a.sourceRoad} has {a.control}");
                }
            }
            return bad;
        }
    }

    public static class RoadGraphReport
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Angle in the JS frame (x mirrored), degrees, as 05's tables quote them.</summary>
        public static double JsAngleDeg(double unityAngle) => Geo.Wrap(Math.PI - unityAngle) / Geo.Deg;

        public static string Summary(RoadGraph g)
        {
            int t = 0, x = 0, arms = 0;
            foreach (var n in g.nodes)
            {
                arms += n.Degree;
                if (n.kind == NodeKind.Junction) { if (n.Degree == 3) t++; else if (n.Degree >= 4) x++; }
            }
            return string.Format(Inv,
                "{0} roads / {1} points -> {2} nodes ({3} junctions: {4} T + {5} X; {6} dirt aprons; {7} L-bends; {8} dead ends; {9} class changes; {10} joins), {11} arm ends, {12} edges, {13} complex pair(s)",
                g.inputRoads, g.inputPoints, g.nodes.Count, g.CountKind(NodeKind.Junction), t, x, g.CountKind(NodeKind.DirtApron),
                g.CountKind(NodeKind.Bend), g.CountKind(NodeKind.DeadEnd), g.CountKind(NodeKind.ClassChange), g.CountKind(NodeKind.Join),
                arms, g.edges.Count, g.complexPairs.Count);
        }

        static string ClassLetter(RoadClass c) =>
            c == RoadClass.MainStreet ? "M" : c == RoadClass.Highway ? "H" : c == RoadClass.Street ? "S" : c == RoadClass.Dirt ? "D" : c == RoadClass.Yard ? "Y" : "?";

        /// <summary>True for the canon (JS frame, x mirrored); towns print their local frame (x east, z north).</summary>
        static bool Js(RoadGraph g) => string.IsNullOrEmpty(g.source) || g.source.StartsWith("CanonData", StringComparison.Ordinal);

        /// <summary>
        /// One line per node: id, traffic.json id, position (JS x, z for the canon; local x, z for a town), kind, arms
        /// (road/class @ angle, grade, control: s = stop, a = all-way stop, y = yield, g = signal; capital = mapped), major,
        /// control, flags.
        /// </summary>
        public static string NodeTable(RoadGraph g)
        {
            var sb = new StringBuilder();
            bool js = Js(g);
            sb.AppendLine(Summary(g));
            sb.AppendLine(js
                ? "node | legacy | JS (x, z) | kind | arms (road/class @ JS angle, grade) | major | control | kerb radii (building clearance at 7.5 m) | flags"
                : "node | legacy | local (x, z) | kind | arms (road/class @ angle, grade, control) | major | control (source) | kerb radii (building clearance at 7.5 m) | flags");
            foreach (var n in g.nodes)
            {
                var arms = new List<string>();
                foreach (var id in n.arms)
                {
                    var a = g.arms[id];
                    string ctl = "";
                    if (!js && a.control != Control.None)
                    {
                        string l = a.control == Control.MinorStop ? "s" : a.control == Control.AllWayStop ? "a" : a.control == Control.Yield ? "y" : a.control == Control.Signal ? "g" : "?";
                        ctl = " " + (a.controlSource == ControlSource.Tag ? l.ToUpperInvariant() : l);
                    }
                    arms.Add(string.Format(Inv, "R{0}/{1}@{2:F0}{3}{4}", a.sourceRoad, ClassLetter(a.cls), js ? JsAngleDeg(a.angle) : a.angle / Geo.Deg,
                        n.IsJunctionLike && Math.Abs(a.grade) > g.settings.steepGrade ? string.Format(Inv, " ({0:+0.0;-0.0}%)", a.grade * 100) : "", ctl));
                }
                string major = n.HasMajor
                    ? string.Format(Inv, "R{0}+R{1} (bend {2:F1})", g.Arm(n, n.majorA).sourceRoad, g.Arm(n, n.majorB).sourceRoad, n.bestPairBend / Geo.Deg)
                    : n.IsJunctionLike ? string.Format(Inv, "none (best pair bends {0:F1})", n.bestPairBend / Geo.Deg) : "-";
                var radii = new List<string>();
                double minClear = double.NaN;
                foreach (var cid in n.corners)
                {
                    var c = g.corners[cid];
                    if (c.kind == CornerKind.Fillet) radii.Add(c.radius.ToString("0.#", Inv));
                    else if (n.IsJunctionLike || n.kind == NodeKind.Bend) radii.Add(c.kind.ToString().ToLowerInvariant());
                    if (!double.IsNaN(c.clearance) && (double.IsNaN(minClear) || c.clearance < minClear)) minClear = c.clearance;
                }
                string corners = radii.Count == 0 ? "-" : string.Join("/", radii) + (double.IsNaN(minClear) ? "" : string.Format(Inv, " ({0:F2} m)", minClear));
                sb.AppendLine(string.Format(Inv, "{0} | {1} | ({2:F1}, {3:F1}) | {4}{5} | {6} | {7} | {8}{9} | {10} | {11}",
                    n.id, n.legacyId >= 0 ? n.legacyId.ToString(Inv) : "-", js ? -n.p.x : n.p.x, n.p.z, n.kind,
                    n.kind == NodeKind.Junction ? (n.Degree == 3 ? " T" : n.Degree == 4 ? " X" : " " + n.Degree) : "",
                    string.Join(" ", arms), major, n.control, js || n.controlSource == ControlSource.None ? "" : " (" + n.controlSource.ToString().ToLowerInvariant() + ")",
                    corners, n.flags == NodeFlags.None ? "" : n.flags.ToString()));
            }
            sb.AppendLine("notes:");
            foreach (var s in g.notes) sb.AppendLine("  " + s);
            return sb.ToString();
        }
    }
}
