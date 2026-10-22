using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Core.WorldBake;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R4's report and checks (artifacts/roads/(town)/r4-report.txt): each close junction pair and how it was
    /// resolved, the alleys against the map, every unpaved road meeting a paved one (apron and STOP), the kerb tapers,
    /// the bridges (deck, abutments, piers, clearance; no plate under a free span) and the roundabouts (mapped, built,
    /// legs at least 70 degrees apart or an exception). The tests assert the problem lists are empty.
    /// </summary>
    public static class RoadR4Report
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Checks
        {
            public readonly List<string> pairs = new List<string>(), alleys = new List<string>(), aprons = new List<string>(), tapers = new List<string>(),
                bridges = new List<string>(), roundabouts = new List<string>();
            public readonly List<string> pairProblems = new List<string>(), alleyProblems = new List<string>(), apronProblems = new List<string>(),
                taperProblems = new List<string>(), bridgeProblems = new List<string>(), roundaboutProblems = new List<string>();
            public int mapAlleys, graphAlleys, skippedAlleys, alleyMouths, unpavedMeetings, kerbTapers, decks;
        }

        public static Checks Run(KerbBaker.Bake k, List<Plate> plates = null)
        {
            var c = new Checks();
            var b = k.roads;
            var g = b.graph;
            var s = b.surfaces;
            var kn = k.kerbs;
            var src = b.source;

            // close junction pairs: merged at import, or two junctions each with a surface and the usual controls
            foreach (var x in b.import.mergedJunctions) c.pairs.Add("merged: " + x);
            var majors = RoadGraphChecks.MajorProblems(g);
            foreach (var p in g.complexPairs.OrderBy(p => p.distance))
            {
                var sa = s.Of(p.a);
                var sb = s.Of(p.b);
                string Mode(NodeSurface ns) => ns == null ? "no surface" : ns.mode.ToString();
                string Ctl(int node) => string.Join(" ", g.nodes[node].arms.Select(id => g.arms[id].control == Control.None ? "-" : g.arms[id].control.ToString()));
                c.pairs.Add(string.Format(Inv, "kept: nodes {0} and {1}, {2:F1} m apart: surfaces {3} / {4}; controls {5} / {6}", p.a, p.b, p.distance, Mode(sa), Mode(sb), Ctl(p.a), Ctl(p.b)));
                if (sa == null || sa.mode == SurfaceMode.Fallback || sb == null || sb.mode == SurfaceMode.Fallback)
                    c.pairProblems.Add(string.Format(Inv, "nodes {0} and {1}: a junction without a surface", p.a, p.b));
                foreach (var mp in majors.Where(m => m.StartsWith($"node {p.a} ", StringComparison.Ordinal) || m.StartsWith($"node {p.b} ", StringComparison.Ordinal)))
                    c.pairProblems.Add(mp);
            }

            // alleys: every service=alley way in the play area is in a road or skipped with its reason; every alley arm has no STOP and a dropped kerb
            if (src != null)
            {
                var inRoads = new HashSet<string>(g.roads.SelectMany(r => r.osmWays), StringComparer.Ordinal);
                foreach (var w in src.ways)
                {
                    if (!w.tags.TryGetValue("service", out var sv) || sv != "alley") continue;
                    if (!w.nodes.Any(id => src.nodes.TryGetValue(id, out var n) && Math.Abs(n.x) <= src.playHalf && Math.Abs(n.z) <= src.playHalf)) continue;
                    c.mapAlleys++;
                    if (inRoads.Contains(w.osm)) { c.graphAlleys++; continue; }
                    var why = b.import.skipped.FirstOrDefault(x => x.StartsWith(w.osm + " ", StringComparison.Ordinal));
                    if (why != null) { c.skippedAlleys++; c.alleys.Add("skipped: " + why); }
                    else c.alleyProblems.Add($"{w.osm}: a mapped alley neither in the graph nor skipped with a reason");
                }
            }
            c.alleyMouths = kn.corners.Count(x => x.ramp == RampKind.AlleyMouth);
            foreach (var n in g.nodes)
            {
                if (n.Degree < 3) continue;
                foreach (var id in n.arms)
                {
                    var a = g.arms[id];
                    if (!RoadGraphBuilder.IsAlley(g, a)) continue;
                    c.alleys.Add(string.Format(Inv, "node {0} arm R{1}: {2}", n.id, a.sourceRoad, a.control == Control.None ? "no control" : a.control + (a.controlSource == ControlSource.Tag ? " (a mapped sign)" : "")));
                    // a mapped sign stands (roads-R0o); the rules never put one on an alley
                    if ((a.control == Control.MinorStop || a.control == Control.Yield) && a.controlSource != ControlSource.Tag) c.alleyProblems.Add($"node {n.id} arm R{a.sourceRoad}: an alley with a {a.control} by rule");
                }
            }

            // unpaved meets paved: a paved apron and a STOP on every such approach
            foreach (var n in g.nodes)
            {
                if (n.Degree < 3) continue;
                bool anyPaved = n.arms.Any(id => Paved(g, g.arms[id]));
                if (!anyPaved) continue;
                foreach (var id in n.arms)
                {
                    var a = g.arms[id];
                    if (Paved(g, a) || RoadGraphBuilder.IsAlley(g, a)) continue;
                    c.unpavedMeetings++;
                    var r = s.ribbons.FirstOrDefault(x => x.edge == a.edge);
                    double apron = r == null ? 0 : a.atA ? r.apronA : r.apronB;
                    bool stop = a.control == Control.MinorStop || a.control == Control.AllWayStop || a.control == Control.Signal;
                    bool sign = k.props != null && k.props.Any(p => p.node == n.id && p.kind == RoadPropKind.Stop && s.Of(n.id) != null && s.Of(n.id).arms[p.arm].arm == a.id);
                    c.aprons.Add(string.Format(Inv, "node {0} arm R{1} ({2}, {3}): apron {4:F1} m, {5}{6}", n.id, a.sourceRoad, a.cls, g.RoadOf(a.sourceRoad)?.surface ?? "", apron, a.control, sign ? ", STOP sign" : ""));
                    if (r != null && apron <= 0) c.apronProblems.Add($"node {n.id} arm R{a.sourceRoad}: no paved apron");
                    if (!stop) c.apronProblems.Add($"node {n.id} arm R{a.sourceRoad}: {a.control}, not a STOP");
                    else if (!sign && s.Of(n.id)?.mode != SurfaceMode.Fallback) c.apronProblems.Add($"node {n.id} arm R{a.sourceRoad}: no STOP sign");
                }
            }

            // kerb tapers: every kerbed face that meets an unkerbed one on its loop ends flush
            var st = k.settings;
            foreach (var f in kn.faces)
            {
                if (!f.kerbed || !(f.taperStart || f.taperEnd)) continue;
                c.kerbTapers++;
                double t = f.taperEnd ? f.t1 - 1e-6 : f.t0 + 1e-6;
                double h = KerbBuilder.KerbHeight(kn, f, t, st);
                bool classChange = g.nodes.Any(n => n.kind == NodeKind.ClassChange && D2.Distance(n.p.XZ, f.taperEnd ? f.kerb[f.Count - 1] : f.kerb[0]) < 25);
                if (classChange) c.tapers.Add(string.Format(Inv, "face {0} (edge {1}) at the town edge: {2} end tapers to {3:F3} m", f.id, f.edge, f.taperEnd ? "far" : "near", h));
                if (h > 1e-3) c.taperProblems.Add(string.Format(Inv, "face {0}: the kerb ends {1:F3} m high", f.id, h));
            }

            // bridges
            var field = plates != null ? new PlateField(plates) : null;
            foreach (var d in k.bridges ?? new List<BridgeDeck>())
            {
                c.decks++;
                c.bridges.Add(BridgeBuilder.Describe(d) + (d.notes.Count > 0 ? "; " + string.Join("; ", d.notes) : ""));
                if (d.FreeSpan <= 0) continue;
                if (!(d.clearanceMin >= new BridgeSettings().clearance - 1e-9)) c.bridgeProblems.Add(string.Format(Inv, "deck {0}: the ground comes within {1:F2} m of the soffit", d.id, d.clearanceMin));
                if (!Geom2.IsSimple(d.outline, out string why)) c.bridgeProblems.Add($"deck {d.id}: its outline is not simple: {why}");
                if (k.meshes != null && !k.meshes.parts.Any(p => p.edge == d.edge && p.collider && p.material == RoadMaterial.Concrete))
                    c.bridgeProblems.Add($"deck {d.id}: no parapet");
                if (field != null)
                {
                    // the deck's own plates (its ribbons, its nodes' surfaces) never reach under the free span; another
                    // road passing beneath keeps its own (listed)
                    var own = new HashSet<int>(d.pieces.Select(x => x.edge));
                    var ownNodes = new HashSet<int>(d.pieces.SelectMany(x => new[] { g.edges[x.edge].nodeA, g.edges[x.edge].nodeB }));
                    var under = new SortedSet<int>();
                    for (double sq = d.sA + 1; sq <= d.length - d.sB - 1; sq += 2)
                        foreach (double u in new[] { -d.Outer(sq, -1) + 0.2, 0.0, d.Outer(sq, 1) - 0.2 })
                        {
                            if (!field.Sample(d.frame.P(sq, u), out _, out var win) || win.id.StartsWith("abutment:", StringComparison.Ordinal)) continue;
                            OwnerOf(win.id, kn, out int edge, out int node);
                            if ((edge >= 0 && own.Contains(edge)) || (node >= 0 && ownNodes.Contains(node)))
                                c.bridgeProblems.Add(string.Format(Inv, "deck {0}: its own plate {1} under the free span at s {2:F1}", d.id, win.id, sq));
                            else if (edge >= 0) under.Add(edge);
                        }
                    if (under.Count > 0) c.bridges.Add(string.Format(Inv, "  roads under deck {0}: edges {1} (their plates stay)", d.id, string.Join(" ", under)));
                }
            }

            // roundabouts
            if (b.import.roundabouts.Count == 0) c.roundabouts.Add("the map has no junction=roundabout ring and no mini-roundabout node: none built (none is invented)");
            foreach (var x in b.import.roundabouts) c.roundabouts.Add("mapped: " + x);
            foreach (var ns in s.nodes)
            {
                if (ns.roundabout == null) continue;
                c.roundabouts.Add("built: " + RoundaboutBuilder.Describe(ns.roundabout));
                bool exception = g.notes.Any(x => x.StartsWith("roundabout exception: node " + ns.node + " ", StringComparison.Ordinal));
                if (ns.roundabout.minLegGapDeg < g.settings.minRoundaboutLegGap - 1e-6 && !exception)
                    c.roundaboutProblems.Add($"node {ns.node}: legs under {g.settings.minRoundaboutLegGap:F0} degrees apart without a reported exception");
            }
            foreach (var n in g.nodes.Where(n => n.kind == NodeKind.Roundabout))
            {
                var ns = s.Of(n.id);
                if (ns == null || ns.mode != SurfaceMode.Roundabout) c.roundaboutProblems.Add($"node {n.id}: a mapped roundabout without its surface ({ns?.problem})");
            }
            return c;
        }

        /// <summary>The edge or node a plate belongs to, from its id (road:eNNNN, road:nNNNN, walk/verge/shoulder:fNNNNN).</summary>
        static void OwnerOf(string id, KerbNetwork kn, out int edge, out int node)
        {
            edge = node = -1;
            int Num(int at) { int v = 0, i = at; while (i < id.Length && char.IsDigit(id[i])) v = v * 10 + (id[i++] - '0'); return v; }
            if (id.StartsWith("road:e", StringComparison.Ordinal)) edge = Num(6);
            else if (id.StartsWith("road:n", StringComparison.Ordinal)) node = Num(6);
            else
            {
                int f = id.IndexOf(":f", StringComparison.Ordinal);
                if (f > 0) { var face = kn.Face(Num(f + 2)); if (face != null) edge = face.edge; }
            }
        }

        static bool Paved(RoadGraph g, GraphArm a) => KerbRules.Paved(KerbRules.Surface(a.cls, g.RoadOf(a.sourceRoad)?.surface));

        public static string Summary(Checks c) => string.Format(Inv,
            "{0} close pairs ({1} problems); alleys {2} in the map = {3} in the graph + {4} skipped, {5} alley mouths ({6} problems); {7} unpaved meetings ({8} problems); {9} kerb tapers ({10} problems); {11} bridges ({12} problems); {13} roundabouts built ({14} problems)",
            c.pairs.Count, c.pairProblems.Count, c.mapAlleys, c.graphAlleys, c.skippedAlleys, c.alleyMouths, c.alleyProblems.Count, c.unpavedMeetings, c.apronProblems.Count,
            c.kerbTapers, c.taperProblems.Count, c.decks, c.bridgeProblems.Count, c.roundabouts.Count(x => x.StartsWith("built:", StringComparison.Ordinal)), c.roundaboutProblems.Count);

        public static string Write(KerbBaker.Bake k, List<Plate> plates)
        {
            var c = Run(k, plates);
            var g = k.roads.graph;
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R4 (roundabouts, bridges, aprons, class changes, alleys, close pairs) of {g.source}: importer v{OsmRoadImporter.Version}, builder v{RoadGraphBuilder.Version}, surfaces v{SurfaceBuilder.Version}, kerbs v{KerbBuilder.Version}, bridges v{BridgeBuilder.Version}, meshes v{RoadMeshBuilder.Version}");
            sb.AppendLine();
            sb.AppendLine("== Summary");
            sb.AppendLine(Summary(c));
            void Section(string title, List<string> lines, List<string> problems)
            {
                sb.AppendLine();
                sb.AppendLine($"== {title} ({lines.Count})");
                foreach (var l in lines) sb.AppendLine("  " + l);
                if (problems.Count > 0)
                {
                    sb.AppendLine($"  problems ({problems.Count}):");
                    foreach (var l in problems) sb.AppendLine("    " + l);
                }
            }
            Section("Close junction pairs (merged under 8 m along a way; else two junctions under the usual rules)", c.pairs, c.pairProblems);
            Section($"Alleys ({c.mapAlleys} mapped in the play area: {c.graphAlleys} in the graph, {c.skippedAlleys} skipped; {c.alleyMouths} dropped-kerb mouths)", c.alleys, c.alleyProblems);
            Section("Unpaved roads meeting paved ones (paved apron, STOP)", c.aprons, c.apronProblems);
            Section($"Kerb tapers at the town edge ({c.kerbTapers} tapers in all, alley mouths and rural corners included)", c.tapers, c.taperProblems);
            Section("Bridges", c.bridges, c.bridgeProblems);
            Section("Roundabouts", c.roundabouts, c.roundaboutProblems);
            var dir = RoadNetworkBaker.TownArtifactsDir(k.town);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "r4-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r\n", "\n"));
            return path;
        }
    }
}
