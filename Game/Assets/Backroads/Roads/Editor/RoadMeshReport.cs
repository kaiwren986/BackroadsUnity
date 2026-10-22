using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R3's checks in numbers (the card's done-when list) and the controls audit: artifacts/roads/(town)/markings-report.txt.
    /// </summary>
    public static class RoadMeshReport
    {
        public sealed class Checks
        {
            public int controlled, signs, stopSigns, yieldSigns, signals, plaques, blades;
            /// <summary>Roundabout entries (RoundaboutYield approaches): each has a YIELD on its right (and one on its raised splitter) and a yield line.</summary>
            public int roundaboutEntries, roundaboutYieldLines;
            public readonly List<string> signProblems = new List<string>(), signExceptions = new List<string>(), lineProblems = new List<string>(), tagProblems = new List<string>(), centreOnJunction = new List<string>();
            public double maxFromEdge, minFromKerb = double.MaxValue, maxFromKerb;
            public double townKm2, trianglesPerKm2;
        }

        static bool StopLike(Control c) => c == Control.MinorStop || c == Control.AllWayStop || c == Control.Signal;

        /// <summary>The card's checks: one sign per controlled approach on its right 0.6-2 m from the kerb and within 15.2 m of the intersected edge; stop and yield lines only where their sign is; every tagged control honoured; no centre line on a junction surface; the triangle budget.</summary>
        public static Checks Run(RoadGraph g, RoadSurfaces surf, IReadOnlyList<RoadNetworkAsset.Prop> props, RoadMeshes meshes, RoadChunkSet chunks, double playHalf)
        {
            var c = new Checks();
            var inv = CultureInfo.InvariantCulture;
            var byApproach = new Dictionary<(int, int), List<RoadNetworkAsset.Prop>>();
            foreach (var p in props)
            {
                if (p.kind == RoadPropKind.StreetName) { c.blades++; continue; }
                if (p.kind == RoadPropKind.AllWayPlaque) { c.plaques++; continue; }
                c.signs++;
                if (p.kind == RoadPropKind.Stop) c.stopSigns++;
                if (p.kind == RoadPropKind.Yield) c.yieldSigns++;
                if (p.kind == RoadPropKind.Signal) c.signals++;
                if (!byApproach.TryGetValue((p.node, p.arm), out var l)) byApproach[(p.node, p.arm)] = l = new List<RoadNetworkAsset.Prop>();
                l.Add(p);
                c.minFromKerb = Math.Min(c.minFromKerb, p.fromKerb);
                c.maxFromKerb = Math.Max(c.maxFromKerb, p.fromKerb);
                if (p.fromKerb < 0.6 - 1e-3 || p.fromKerb > 2.0 + 1e-3) c.signProblems.Add(string.Format(inv, "node {0} arm {1}: {2} {3:F2} m from the kerb (0.6-2 m)", p.node, p.arm, p.kind, p.fromKerb));
                // at most 15.2 m from the intersected edge (§2B.18), unless a marked crosswalk lies further out: then the
                // sign stands 4 ft before the crosswalk (§2B.18 ¶7 and the stop line rule win; listed)
                var arm = surf.Of(p.node)?.arms[p.arm];
                bool crosswalkFirst = arm != null && arm.crosswalk && p.fromEdge > 15.2;
                if (crosswalkFirst) c.signExceptions.Add(string.Format(inv, "node {0} arm {1}: {2} {3:F1} m from the intersected edge, 1.22 m before the crosswalk at {4:F1}-{5:F1} m (a long corner)", p.node, p.arm, p.kind, p.fromEdge, arm.sCwInner, arm.sCwOuter));
                else
                {
                    c.maxFromEdge = Math.Max(c.maxFromEdge, p.fromEdge);
                    if (p.fromEdge > 15.2 + 1e-3) c.signProblems.Add(string.Format(inv, "node {0} arm {1}: {2} {3:F1} m from the intersected edge (at most 15.2)", p.node, p.arm, p.kind, p.fromEdge));
                }
            }
            foreach (var ns in surf.nodes)
                for (int k = 0; k < ns.arms.Count; k++)
                {
                    var a = ns.arms[k];
                    bool controlled = a.control != Control.None && a.control != Control.RoundaboutYield;
                    byApproach.TryGetValue((ns.node, k), out var l);
                    int n = l?.Count ?? 0;
                    if (a.control == Control.RoundaboutYield)
                    {
                        // roads-R4: a YIELD right of the entry and, with a raised splitter wide enough, one on the splitter
                        c.roundaboutEntries++;
                        var leg = ns.roundabout != null && k < ns.roundabout.legs.Count ? ns.roundabout.legs[k] : null;
                        int most = leg != null && leg.splitters.Count > 0 ? 2 : 1;
                        if (n < 1 || n > most) c.signProblems.Add($"node {ns.node} arm {k}: {n} signs at a roundabout entry (1 to {most})");
                        if (l != null && l.Any(x => x.kind != RoadPropKind.Yield)) c.signProblems.Add($"node {ns.node} arm {k}: a sign other than YIELD at a roundabout entry");
                        if (n >= 1)
                        {
                            a.frame.ToFrame(new D2(l[0].position.x, l[0].position.z), out _, out double ur);
                            if (ur <= 0) c.signProblems.Add($"node {ns.node} arm {k}: the entry's YIELD is on the approach's left");
                        }
                        bool line = meshes.parts.Any(p => p.marking == MarkingKind.YieldLine && p.material == RoadMaterial.Paint && p.node == ns.node && p.arm == k);
                        if (line) c.roundaboutYieldLines++;
                        else c.lineProblems.Add($"node {ns.node} arm {k}: a roundabout entry without a yield line");
                        continue;
                    }
                    if (controlled) c.controlled++;
                    if (controlled && n != 1) c.signProblems.Add($"node {ns.node} arm {k}: {n} signs for one {a.control} approach");
                    if (!controlled && n > 0) c.signProblems.Add($"node {ns.node} arm {k}: a sign on an uncontrolled approach");
                    if (controlled && n == 1)
                    {
                        var want = a.control == Control.Yield ? RoadPropKind.Yield : a.control == Control.Signal ? RoadPropKind.Signal : RoadPropKind.Stop;
                        if (l[0].kind != want) c.signProblems.Add($"node {ns.node} arm {k}: {l[0].kind} for a {a.control} approach");
                        // on the right of the approaching traffic: the arm's left (+u) side
                        a.frame.ToFrame(new D2(l[0].position.x, l[0].position.z), out _, out double u);
                        if (u <= 0) c.signProblems.Add($"node {ns.node} arm {k}: the sign is on the approach's left");
                    }
                }
            // stop lines only with STOP or signals, yield lines only with YIELD
            foreach (var p in meshes.parts)
            {
                if (p.marking == MarkingKind.StopLine && p.material == RoadMaterial.Paint)
                {
                    var a = surf.Of(p.node)?.arms[p.arm];
                    if (a == null || !StopLike(a.control)) c.lineProblems.Add($"node {p.node} arm {p.arm}: a stop line without a STOP or signal");
                }
                if (p.marking == MarkingKind.YieldLine && p.material == RoadMaterial.Paint)
                {
                    var a = surf.Of(p.node)?.arms[p.arm];
                    if (a == null || a.control != Control.Yield && a.control != Control.RoundaboutYield) c.lineProblems.Add($"node {p.node} arm {p.arm}: a yield line without a YIELD");
                }
                var pc = p.Centroid;
                if (p.marking == MarkingKind.CentreLine && p.material == RoadMaterial.Paint && (p.lods & 1) != 0)
                    foreach (var ns in surf.nodes)
                    {
                        if (ns.polygon.Count < 3 || D2.Distance(ns.polygon[0], pc) > 80) continue;
                        for (int k = 0; k + 2 < p.tri.Count; k += 3)
                        {
                            var q = new D2((p.pos[p.tri[k]].x + p.pos[p.tri[k + 1]].x + p.pos[p.tri[k + 2]].x) / 3, (p.pos[p.tri[k]].z + p.pos[p.tri[k + 1]].z + p.pos[p.tri[k + 2]].z) / 3);
                            if (TownZone.InPolygon(ns.polygon, q) && Geom2.DistanceToBoundary(ns.polygon, q) > 0.05) { c.centreOnJunction.Add($"edge {p.edge}: centre line on junction {ns.node} at {q}"); break; }
                        }
                    }
            }
            foreach (var arm in g.arms)
                if (StopLike(arm.control) || arm.control == Control.Yield)
                {
                    var ns = surf.Of(arm.node);
                    if (ns == null) continue;
                    int k = ns.arms.FindIndex(x => x.arm == arm.id);
                    if (k < 0) continue;
                    bool hasLine = meshes.parts.Any(p => p.marking == MarkingKind.StopLine && p.node == arm.node && p.arm == k);
                    var e = g.edges[arm.edge];
                    var sa = ns.arms[k];
                    bool paved = KerbRules.Paved(KerbRules.Surface(e.cls, g.RoadOf(e.sourceRoad)?.surface))
                        || (sa.sStop >= 0 && SurfaceBuilder.PavedAt(g, surf, arm.edge, sa.atA ? sa.sStop : e.Length - sa.sStop));
                    if (StopLike(arm.control) && paved && ns.mode != SurfaceMode.Fallback && !hasLine) c.lineProblems.Add($"node {arm.node} arm {k}: a {arm.control} approach without a stop line");
                }
            // every mapped sign or signal is honoured, or says why not
            foreach (var f in g.features)
            {
                if (f.kind == RoadFeatureKind.Crossing) continue;
                if (f.problem.Length > 0) { c.tagProblems.Add($"osm node {f.osmNode} {f.kind}: {f.problem}"); continue; }
                if (f.node < 0 || f.arm < 0) { c.tagProblems.Add($"osm node {f.osmNode} {f.kind}: controls no approach (no reason recorded)"); continue; }
                var a = g.arms[f.arm];
                bool ok = f.kind == RoadFeatureKind.Stop ? a.control == Control.MinorStop || a.control == Control.AllWayStop
                    : f.kind == RoadFeatureKind.AllWayStop ? a.control == Control.AllWayStop
                    : f.kind == RoadFeatureKind.GiveWay ? a.control == Control.Yield : a.control == Control.Signal;
                if (!ok) c.tagProblems.Add($"osm node {f.osmNode} {f.kind}: its approach at node {f.node} ends up {a.control}");
            }
            c.townKm2 = (2 * playHalf) * (2 * playHalf) / 1e6;
            c.trianglesPerKm2 = chunks != null ? chunks.lod0Triangles / c.townKm2 : 0;
            return c;
        }

        public static string Write(KerbBaker.Bake k, RoadChunkSet chunks, RoadNetworkAsset asset)
        {
            var inv = CultureInfo.InvariantCulture;
            var g = k.roads.graph;
            double half = k.roads.source?.playHalf ?? 1024;
            var c = Run(g, k.roads.surfaces, k.props, k.meshes, chunks, half);
            var dir = RoadNetworkBaker.TownArtifactsDir(k.town);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R3 (markings, controls, signs, chunks) of {g.source}: props v{RoadPropBuilder.Version}, meshes v{RoadMeshBuilder.Version}");
            sb.AppendLine($"road network {asset?.contentHash}; chunks {chunks?.contentHash}");
            sb.AppendLine();
            sb.AppendLine("== Summary");
            sb.AppendLine(string.Format(inv, "{0} streets ({1} with placeholder names until the list is approved); {2} controlled approaches, {3} signs ({4} STOP, {5} YIELD, {6} signals), {7} all-way plaques, {8} street-name blades",
                k.streets.Count, k.streets.Count(s => s.placeholder), c.controlled, c.signs, c.stopSigns, c.yieldSigns, c.signals, c.plaques, c.blades));
            sb.AppendLine(string.Format(inv, "signs {0:F2}-{1:F2} m from the kerb face (or pavement edge), at most {2:F1} m from the intersected edge", c.signs > 0 ? c.minFromKerb : 0, c.maxFromKerb, c.maxFromEdge));
            if (chunks != null)
            {
                sb.AppendLine(string.Format(inv, "markings: centre line {0:F2} km ({1:F2} km in no-passing zones), edge line {2:F2} km, {3} stop lines, {4} crosswalks, {5} yield lines, detectable warnings {6:F0} m",
                    chunks.centreLineM / 1000, chunks.noPassingM / 1000, chunks.edgeLineM / 1000, chunks.stopLines, chunks.crosswalks, chunks.yieldLines, chunks.warningM));
                sb.AppendLine(string.Format(inv, "chunks: {0} of 64 m; triangles LOD0 {1}, LOD1 {2}, collider {3}; {4:F0} LOD0 triangles per km² over the {5:F2} km² play area (budget 60,000)",
                    chunks.chunks.Length, chunks.lod0Triangles, chunks.lod1Triangles, chunks.colliderTriangles, c.trianglesPerKm2, c.townKm2));
                int maxT = chunks.chunks.Length > 0 ? chunks.chunks.Max(x => x.lod0Triangles) : 0, maxV = chunks.chunks.Length > 0 ? chunks.chunks.Max(x => x.vertices) : 0;
                sb.AppendLine(string.Format(inv, "largest chunk: {0} LOD0 triangles, {1} vertices", maxT, maxV));
            }
            var parts = k.meshes.parts;
            sb.AppendLine("parts: " + string.Join(", ", parts.GroupBy(p => (p.material, p.material == RoadMaterial.Paint ? p.marking.ToString() : ""))
                .OrderBy(x => x.Key.material).Select(x => string.Format(inv, "{0}{1} {2} ({3} tris)", x.Key.material, x.Key.Item2.Length > 0 ? "/" + x.Key.Item2 : "", x.Count(), x.Sum(p => p.Triangles)))));
            sb.AppendLine("controls: " + string.Join(", ", g.arms.Where(a => a.control != Control.None).GroupBy(a => (a.control, a.controlSource)).OrderBy(x => x.Key.control)
                .Select(x => $"{x.Key.control} from {x.Key.controlSource.ToString().ToLowerInvariant()} {x.Count()}")));
            sb.AppendLine();
            void Section(string t, List<string> l)
            {
                sb.AppendLine($"== {t} ({l.Count})");
                foreach (var x in l) sb.AppendLine("  " + x);
                sb.AppendLine();
            }
            Section("Sign problems (one per controlled approach, right side, 0.6-2 m from the kerb, at most 15.2 m from the edge)", c.signProblems);
            Section("Signs set back by a marked crosswalk beyond 15.2 m (MUTCD: 4 ft before the crosswalk)", c.signExceptions);
            Section("Stop and yield line problems (paved approaches; gravel takes no paint)", c.lineProblems);
            Section("Centre lines on a junction surface", c.centreOnJunction);
            Section("Mapped controls not honoured, with the reason", c.tagProblems);
            Section("Mesh notes", k.meshes.notes);
            sb.AppendLine("== Streets (id | name | first OSM way | edges)");
            foreach (var s in k.streets) sb.AppendLine($"{s.id} | {s.name}{(s.placeholder ? " (placeholder)" : "")} | {s.firstWay} | {string.Join(" ", s.edges)}");
            var path = Path.Combine(dir, "markings-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            return path;
        }
    }
}
