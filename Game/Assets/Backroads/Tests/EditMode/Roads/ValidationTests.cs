using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Roads;
using Backroads.Roads.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.Roads
{
    public partial class Roads
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// Roads.Validation, the M3 headline gate (05 §8 on the town, card T3.7): one fresh in-memory bake of the town
        /// (R0o-R4: graph, surfaces, kerbs, streets, props, bridges, meshes, chunks, plates; nothing saved) and every 05 §8
        /// check on it, through the checks the cards already use (RoadGraphChecks, SurfaceChecks, RoadMeshReport,
        /// RoadR4Report, RoadPlates.Terrain) plus the §8.3 grade report:
        ///   §8.1 every junction surface simple and counter-clockwise, its area near the corridor estimate;
        ///   §8.2 every ribbon end shares its mouth vertices bit for bit with its junction;
        ///   §8.3 approaches over 6 % near a stop line, junctions over 3 %, walk cross slopes over 2.1 %, ramps over 8.33 %
        ///        (listed exceptions, never a failure);
        ///   §8.4 every road side has a kerb face or an unkerbed edge, no kerb in a building;
        ///   §8.6 exactly one STOP or signal per controlled approach, stop lines only with them; YIELD signs and yield lines
        ///        at roundabout entries (the town has none: also checked on R4's synthetic ring);
        ///   §8.7 the plates' ground at least 0.03 m under every paved footprint and within 0.1 m of the edge 1 m outside;
        ///   R4's report with empty problem lists;
        ///   §8.9 two bakes give the same network, frontage, chunk and plate hashes, and the committed assets are current.
        /// §8.5 (lanes) waits for R5; §8.8's screenshots are the m3.shots.junctions autotest. The finer tests stay as they are.
        /// Writes artifacts/roads/(town)/validation-report.txt and grades-report.txt.
        /// </summary>
        [Test, Category("Roads")]
        public void Validation()
        {
            string town = TownRoadSource.Towns().First();
            var sw = Stopwatch.StartNew();
            var net = RoadNetworkBaker.BakeTownWithKerbs(town, out var b, out var k, out var frontage);
            var chunks = RoadNetworkBaker.BakeMeshes(k, net.contentHash, frontage.contentHash, out _);
            var plates = RoadPlateBuilder.Build(b.graph, b.surfaces, k.kerbs, b.source?.dem, k.settings);
            double bakeS = sw.Elapsed.TotalSeconds;
            RoadNetworkAsset net2 = null;
            KerbFrontageAsset frontage2 = null;
            RoadChunkSet chunks2 = null;
            RoundaboutDemo.Bake ring = null;
            try
            {
                var g = b.graph;
                var s = b.surfaces;
                var kn = k.kerbs;
                double half = b.source?.playHalf ?? 1024;
                var errors = new List<string>();
                void Err(string what, IEnumerable<string> lines) { foreach (var l in lines) errors.Add(what + ": " + l); }
                var out1 = new List<string>();
                void Say(string l) => out1.Add(l);

                Say($"Roads.Validation (05 §8) on {g.source}: one fresh bake in {bakeS:F1} s (importer v{OsmRoadImporter.Version}, graph v{RoadGraphBuilder.Version}, surfaces v{SurfaceBuilder.Version}, kerbs v{KerbBuilder.Version}, meshes v{RoadMeshBuilder.Version}, bridges v{BridgeBuilder.Version})");
                Say("graph: " + RoadGraphReport.Summary(g));

                // the graph: the major road by class, one kind of control per junction, no height conflicts
                Err("graph", RoadGraphChecks.MajorProblems(g));
                Err("graph", RoadGraphChecks.ControlProblems(g));
                if (b.import.gradeConflicts > 0) errors.Add($"import: {b.import.gradeConflicts} height segments over the grade limit and steeper than the real road");
                RoadGraphChecks.Connected(g, false, out int reached, out int total);
                Say(string.Format(Inv, "  connected: {0} of {1} nodes from node 0 ({2} groups apart, {3} kept because they reach the play-area edge); grade conflicts {4}",
                    reached, total, b.import.groups.Count, b.import.groups.Count(x => x.kept), b.import.gradeConflicts));

                // §8.1 polygons and §8.2 seams (SurfaceChecks, run by the bake)
                var sc = b.checks;
                Err("§8.1 not simple or not counter-clockwise", sc.notSimple);
                Err("§8.1 area off the corridor estimate", sc.areaOff);
                Err("§8.2 seam", sc.seams);
                if (sc.maxSeamError != 0) errors.Add($"§8.2: seam vertices differ by up to {sc.maxSeamError:E2} m (must be bit-shared)");
                if (sc.seamsChecked == 0) errors.Add("§8.2: no seam checked");
                if (sc.surfaces == 0) errors.Add("§8.1: no junction surface");
                foreach (var ns in s.nodes.Where(n => n.mode == SurfaceMode.Fallback))
                    errors.Add($"§8.1: node {ns.node} has no surface (fallback): {ns.problem}");
                Say(string.Format(Inv, "§8.1 polygons: {0} junction surfaces, all simple and counter-clockwise: {1}; fallbacks {2}; areas off {3} (regular worst {4:+0.0;-0.0} %, {5} irregular within 15 %)",
                    sc.surfaces, sc.notSimple.Count == 0 ? "yes" : "NO", sc.fallbacks, sc.areaOff.Count, (sc.worstAreaRatio - 1) * 100, sc.irregularAreas.Count));
                Say(string.Format(Inv, "§8.2 seams: {0} ribbon-to-mouth seams, {1} problems, worst {2:E1} m (bit-shared)", sc.seamsChecked, sc.seams.Count, sc.maxSeamError));

                // §8.3 grades: listed, never failed
                var gr = RoadGradeReport.Run(k);
                string gradePath = RoadGradeReport.Write(k, gr);
                if (gr.planesOver > 0) errors.Add($"§8.3: {gr.planesOver} plane junctions over 3 % (the plane is clamped to 3 %)");
                Say("§8.3 grades (listed exceptions): " + RoadGradeReport.Summary(gr));
                foreach (var l in gr.approaches.Concat(gr.planes).Concat(gr.walkCross).Concat(gr.ramps).Concat(gr.flares).Take(6)) Say("    " + l);

                // §8.4 kerbs vs the map's buildings
                Err("§8.4 road side with no kerb face", kn.unfaced);
                Err("§8.4 kerb in a building", sc.kerbInBuilding);
                Say(string.Format(Inv, "§8.4 kerbs: {0} faces ({1} kerbed), 0 road sides without one: {2}; kerbs in buildings {3} (+{4} known map conflicts); {5} faces closer to a building than the walk needs (reported)",
                    kn.faces.Count, kn.faces.Count(f => f.kerbed), kn.unfaced.Count == 0 ? "yes" : "NO", sc.kerbInBuilding.Count, sc.knownMapConflicts.Count, kn.clearance.Count));

                // §8.6 controls: signs and lines (RoadMeshReport); roundabout entries on the town and on R4's synthetic ring
                var mc = RoadMeshReport.Run(g, s, k.props, k.meshes, chunks, half);
                Err("§8.6 sign", mc.signProblems);
                Err("§8.6 line", mc.lineProblems);
                Err("§8.6 centre line on a junction", mc.centreOnJunction);
                Err("§8.6 mapped control", mc.tagProblems.Where(t => t.Contains("no reason recorded")));
                if (mc.stopSigns + mc.signals + mc.yieldSigns - YieldSignsAtRoundabouts(k.props, s) != mc.controlled)
                    errors.Add($"§8.6: {mc.controlled} controlled approaches but {mc.stopSigns} STOP, {mc.signals} signals and {mc.yieldSigns} YIELD signs");
                ring = RoundaboutDemo.Run(RoundaboutDemo.Ring(13.25, new[] { 0.0, 95, 185, 275 }));
                var rc = RoadMeshReport.Run(ring.g, ring.s, ring.props, ring.meshes, null, ring.src.playHalf);
                Err("§8.6 synthetic ring sign", rc.signProblems);
                Err("§8.6 synthetic ring line", rc.lineProblems);
                int ringNodes = ring.s.nodes.Count(n => n.mode == SurfaceMode.Roundabout);
                if (ringNodes != 1 || rc.roundaboutEntries != 4 || rc.roundaboutYieldLines != 4) errors.Add($"§8.6: the synthetic ring has {ringNodes} roundabouts, {rc.roundaboutEntries} entries, {rc.roundaboutYieldLines} yield lines (want 1, 4, 4)");
                Say(string.Format(Inv, "§8.6 controls: {0} controlled approaches, {1} signs ({2} STOP, {3} signals, {4} YIELD), {5} problems; {6} stop lines; {7} set back by a crosswalk; roundabout entries {8} (the town has none; synthetic ring: {9} entries, {10} YIELD, {11} yield lines, {12} problems)",
                    mc.controlled, mc.stopSigns + mc.signals + mc.yieldSigns, mc.stopSigns, mc.signals, mc.yieldSigns, mc.signProblems.Count + mc.lineProblems.Count,
                    chunks.stopLines, mc.signExceptions.Count, mc.roundaboutEntries, rc.roundaboutEntries, rc.yieldSigns, rc.roundaboutYieldLines, rc.signProblems.Count + rc.lineProblems.Count));

                // §8.7 terrain under and beside the pavement (the plates)
                var tr = RoadPlates.Terrain(new Kerbs.Bake { k = k, plates = plates });
                Err("§8.7 terrain", tr.fails);
                if (tr.offWalk >= 50) errors.Add($"§8.7: {tr.offWalk} walk samples outside every walk piece");
                if (tr.conflicts >= tr.outside / 200) errors.Add($"§8.7: {tr.conflicts} points between two paved edges at different heights (at most {tr.outside / 200})");
                if (tr.alleyConflicts >= tr.outside / 20) errors.Add($"§8.7: {tr.alleyConflicts} points between two edges at alley mouths (at most {tr.outside / 20})");
                if (tr.inside <= 100 || tr.outside <= 100) errors.Add($"§8.7: too few samples ({tr.inside} under, {tr.outside} outside)");
                Say($"§8.7 terrain: {plates.Count} plates; {tr.Summary}; {tr.fails.Count} failures");

                // roads-R4's report
                var r4 = RoadR4Report.Run(k, plates);
                Err("R4 close pair", r4.pairProblems);
                Err("R4 alley", r4.alleyProblems);
                Err("R4 apron", r4.apronProblems);
                Err("R4 taper", r4.taperProblems);
                Err("R4 bridge", r4.bridgeProblems);
                Err("R4 roundabout", r4.roundaboutProblems);
                if (r4.mapAlleys != r4.graphAlleys + r4.skippedAlleys) errors.Add($"R4: {r4.mapAlleys} alleys in the map, {r4.graphAlleys} in the graph + {r4.skippedAlleys} skipped");
                Say("R4: " + RoadR4Report.Summary(r4));

                // §8.9 determinism and the committed assets
                var sw2 = Stopwatch.StartNew();
                net2 = RoadNetworkBaker.BakeTownWithKerbs(town, out var b2, out var k2, out frontage2);
                chunks2 = RoadNetworkBaker.BakeMeshes(k2, net2.contentHash, frontage2.contentHash, out _);
                var plates2 = RoadPlateBuilder.Build(b2.graph, b2.surfaces, k2.kerbs, b2.source?.dem, k2.settings);
                string ph1 = Kerbs.PlateHash(plates), ph2 = Kerbs.PlateHash(plates2);
                if (net.contentHash != net2.contentHash) errors.Add($"§8.9: two bakes give road networks {net.contentHash} and {net2.contentHash}");
                if (frontage.contentHash != frontage2.contentHash) errors.Add($"§8.9: two bakes give frontages {frontage.contentHash} and {frontage2.contentHash}");
                if (chunks.contentHash != chunks2.contentHash) errors.Add($"§8.9: two bakes give road chunks {chunks.contentHash} and {chunks2.contentHash}");
                if (ph1 != ph2) errors.Add($"§8.9: two bakes give plates {ph1} and {ph2}");
                if (net.contentHash != net.ComputeHash()) errors.Add("§8.9: the fresh network's stored hash does not match its content");
                var cNet = RoadNetworkBaker.LoadBakedTown(town);
                var cFront = KerbBaker.LoadBakedTown(town);
                var cChunks = RoadChunkBaker.LoadBakedTown(town);
                string Cur(UnityEngine.Object o, string committed, string fresh) => o == null ? "MISSING" : committed == fresh ? "current" : "STALE";
                if (cNet == null) errors.Add("§8.9: no committed " + RoadNetworkBaker.TownAssetPath(town));
                else
                {
                    if (cNet.contentHash != cNet.ComputeHash()) errors.Add("§8.9: the committed network's content does not match its stored hash");
                    if (cNet.contentHash != net.contentHash) errors.Add($"§8.9: the committed network is stale ({cNet.contentHash} vs fresh {net.contentHash}): run RoadBakeCli.BakeTowns");
                }
                if (cFront == null) errors.Add("§8.9: no committed " + KerbBaker.TownAssetPath(town));
                else
                {
                    if (cFront.contentHash != cFront.ComputeHash()) errors.Add("§8.9: the committed frontage's content does not match its stored hash");
                    if (cFront.contentHash != frontage.contentHash) errors.Add($"§8.9: the committed frontage is stale ({cFront.contentHash} vs fresh {frontage.contentHash}): run RoadBakeCli.BakeTowns");
                }
                if (cChunks == null) errors.Add("§8.9: no committed " + RoadChunkBaker.TownAssetPath(town));
                else
                {
                    if (cChunks.contentHash != chunks.contentHash) errors.Add($"§8.9: the committed road chunks are stale ({cChunks.contentHash} vs fresh {chunks.contentHash}): run RoadBakeCli.BakeTowns");
                    if (cChunks.roadContentHash != net.contentHash || cChunks.frontageContentHash != frontage.contentHash) errors.Add("§8.9: the committed chunks were baked from another network or frontage");
                }
                Say(string.Format(Inv, "§8.9 determinism ({0:F1} s for the second bake): network {1}, frontage {2}, chunks {3}, plates {4}: two bakes {5}; committed: network {6}, frontage {7}, chunks {8}",
                    sw2.Elapsed.TotalSeconds, Short(net.contentHash), Short(frontage.contentHash), Short(chunks.contentHash), Short(ph1),
                    net.contentHash == net2.contentHash && frontage.contentHash == frontage2.contentHash && chunks.contentHash == chunks2.contentHash && ph1 == ph2 ? "agree" : "DIFFER",
                    Cur(cNet, cNet?.contentHash, net.contentHash), Cur(cFront, cFront?.contentHash, frontage.contentHash), Cur(cChunks, cChunks?.contentHash, chunks.contentHash)));
                Say("not here: §8.5 lanes (R5); §8.8 golden screenshots (the m3.shots.junctions autotest)");
                Say($"errors: {errors.Count}");
                foreach (var e in errors.Take(40)) Say("  " + e);

                var text = string.Join("\n", out1);
                TestContext.WriteLine(text);
                var dir = RoadNetworkBaker.TownArtifactsDir(town);
                Directory.CreateDirectory(dir);
                var report = Path.Combine(dir, "validation-report.txt");
                File.WriteAllText(report, text + "\n", new UTF8Encoding(false));
                TestContext.WriteLine("wrote " + report + ", " + gradePath);
                Assert.IsEmpty(errors, $"{errors.Count} errors, first: {errors.FirstOrDefault()}");
            }
            finally
            {
                foreach (var o in new UnityEngine.Object[] { net, frontage, chunks, net2, frontage2, chunks2 })
                    if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        static string Short(string hash) => string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Math.Min(12, hash.Length));

        /// <summary>YIELD signs that stand at roundabout entries (counted apart from the one sign per controlled approach).</summary>
        static int YieldSignsAtRoundabouts(IEnumerable<RoadNetworkAsset.Prop> props, RoadSurfaces s) =>
            props.Count(p => p.kind == RoadPropKind.Yield && s.Of(p.node)?.mode == SurfaceMode.Roundabout);
    }
}
