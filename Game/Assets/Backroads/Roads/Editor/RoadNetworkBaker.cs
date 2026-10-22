using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Backroads.Canon.Editor;
using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// R0 road bake (05 §9): imported canon (CanonImporter, which verifies every manifest hash) -> RoadGraphBuilder ->
    /// Assets/Backroads/Roads/Baked/Resources/BrRoadNetwork.asset, plus a text report in artifacts/roads/.
    ///   menu: Backroads > Roads > Bake road network (R0)
    ///   batchmode: -executeMethod Backroads.Roads.Editor.RoadBakeCli.Bake [-verifyHash]
    /// The asset keeps its GUID across re-bakes (updated in place).
    /// </summary>
    public static class RoadNetworkBaker
    {
        public const string BakedDir = "Assets/Backroads/Roads/Baked/Resources";
        public const string AssetPath = BakedDir + "/" + RoadNetworkAsset.ResourceName + ".asset";

        public static string ArtifactsDir => Path.Combine(CanonImporter.RepoRoot, "artifacts", "roads");

        [MenuItem("Backroads/Roads/Bake road network (R0)")]
        static void BakeMenu()
        {
            bool ok = Bake(out var msg);
            if (ok) Debug.Log("[BR] " + msg); else Debug.LogError("[BR] " + msg);
        }

        /// <summary>Builds the graph from the imported canon (importing it first when needed).</summary>
        public static RoadGraph BuildFromCanon(out RoadGraphInput input, out string message)
        {
            input = null;
            if (!CanonImporter.EnsureImported(out message)) return null;
            var canon = CanonImporter.LoadImported();
            if (canon == null) { message = "no imported canon (CanonImporter.LoadImported returned null)"; return null; }
            input = RoadGraphCanon.FromCanon(canon);
            return RoadGraphBuilder.Build(input);
        }

        /// <summary>A fresh asset instance (not saved) from the current canon, for comparisons.</summary>
        public static RoadNetworkAsset BakeInMemory(out RoadGraph graph, out RoadGraphInput input, out string message)
        {
            graph = BuildFromCanon(out input, out message);
            if (graph == null) return null;
            var a = ScriptableObject.CreateInstance<RoadNetworkAsset>();
            a.name = RoadNetworkAsset.ResourceName;
            a.SetFrom(graph);
            return a;
        }

        public static RoadNetworkAsset LoadBaked() => AssetDatabase.LoadAssetAtPath<RoadNetworkAsset>(AssetPath);

        public static bool Bake(out string message)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fresh = BakeInMemory(out var graph, out var input, out message);
            if (fresh == null) return false;
            EnsureFolder(BakedDir);
            var asset = LoadBaked();
            if (asset == null)
            {
                AssetDatabase.CreateAsset(fresh, AssetPath);
                asset = fresh;
            }
            else
            {
                EditorUtility.CopySerialized(fresh, asset);
                asset.name = RoadNetworkAsset.ResourceName;
                EditorUtility.SetDirty(asset);
                UnityEngine.Object.DestroyImmediate(fresh);
            }
            AssetDatabase.SaveAssets();
            WriteReport(graph, input, asset);
            message = $"roads R0 baked in {sw.Elapsed.TotalMilliseconds:F0} ms: {RoadGraphReport.Summary(graph)}; content {asset.contentHash.Substring(0, 12)} -> {AssetPath}";
            return true;
        }

        /// <summary>Creates an asset folder (and its parents) through the AssetDatabase.</summary>
        public static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            int cut = assetFolder.LastIndexOf('/');
            string parent = assetFolder.Substring(0, cut), leaf = assetFolder.Substring(cut + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // ------------------------------------------------------------------ towns (roads-R0o)

        public static string TownAssetPath(string townId) => BakedDir + "/" + RoadNetworkAsset.TownResourceName(townId) + ".asset";

        public static string TownArtifactsDir(string townId) => Path.Combine(ArtifactsDir, townId);

        /// <summary>TownData/(town)/v1 -> OsmRoadImporter -> RoadGraphBuilder.</summary>
        public static RoadGraph BuildTown(string townId, out RoadGraphInput input, out OsmImportReport report) =>
            BuildTown(townId, out input, out report, out _);

        /// <summary>The same, also handing back the town source (its DEM, zones and footprints) for the later steps.</summary>
        public static RoadGraph BuildTown(string townId, out RoadGraphInput input, out OsmImportReport report, out OsmRoadSource source)
        {
            source = TownRoadSource.LoadTown(townId);
            input = OsmRoadImporter.Import(source, null, out report);
            return RoadGraphBuilder.Build(input);
        }

        public static RoadNetworkAsset BakeTownInMemory(string townId, out RoadGraph graph, out RoadGraphInput input, out OsmImportReport report) =>
            BakeTownInMemory(townId, out graph, out input, out report, out _);

        /// <summary>R0o, R1, R2 and R3's streets and props into a fresh road network asset (the same as BakeTownWithKerbs).</summary>
        public static RoadNetworkAsset BakeTownInMemory(string townId, out RoadGraph graph, out RoadGraphInput input, out OsmImportReport report, out RoadSurfaces surfaces)
        {
            var a = BakeTownWithKerbs(townId, out var b, out _, out var frontage);
            UnityEngine.Object.DestroyImmediate(frontage);
            graph = b.graph; input = b.input; report = b.import; surfaces = b.surfaces;
            return a;
        }

        /// <summary>
        /// R0o + R1 + R2 + R3 (streets, props) in memory: the road network asset and the kerb frontage asset (fresh, not
        /// saved, the frontage linked to the network's content hash) and the bakes behind them.
        /// </summary>
        public static RoadNetworkAsset BakeTownWithKerbs(string townId, out SurfaceBaker.Bake bake, out KerbBaker.Bake kerbs, out KerbFrontageAsset frontage)
        {
            var b = new SurfaceBaker.Bake { town = townId };
            b.graph = BuildTown(townId, out b.input, out b.import, out b.source);
            b.surfaces = SurfaceBuilder.Build(b.graph, b.input, RoadOverrides.Load(townId));
            b.checks = SurfaceChecks.Run(b.graph, b.input, b.surfaces);
            bake = b;
            kerbs = KerbBaker.FromRoads(b);
            var names = StreetNameList.Load();
            kerbs.streets = StreetNamer.Assign(b.graph, names.names, names.approved);
            kerbs.props = RoadPropBuilder.Build(b.graph, b.surfaces, kerbs.kerbs, kerbs.streets, kerbs.settings);
            kerbs.bridges = BridgeBuilder.Build(b.graph, b.source?.dem, null, b.source?.bridgeOver);
            var a = ScriptableObject.CreateInstance<RoadNetworkAsset>();
            a.name = RoadNetworkAsset.TownResourceName(townId);
            a.SetFrom(b.graph, b.import, b.surfaces, kerbs.streets, kerbs.props, RoadPropBuilder.Version, kerbs.bridges);
            frontage = KerbBaker.ToAsset(kerbs, a.contentHash);
            return a;
        }

        /// <summary>
        /// roads-R3: the chunked road meshes of a kerb bake (not saved), linked to the network and frontage it was built from,
        /// with lane S's driveway cuts (tools/world/sites/(town)/driveways.json, when the town has one) dropped into the kerbs
        /// and walks; the cuts land in k.cuts.
        /// </summary>
        public static RoadChunkSet BakeMeshes(KerbBaker.Bake k, string roadHash, string frontageHash, out byte[] packed)
        {
            var cuts = RoadDriveways.ForBake(k);
            k.meshes = RoadMeshBuilder.Build(k.roads.graph, k.roads.surfaces, k.kerbs, k.streets, k.settings, k.bridges, k.roads.source?.dem, cuts, k.cutter);
            double half = k.roads.source?.playHalf ?? 1024;
            return RoadChunkBaker.Build(k.meshes, half, k.roads.graph.source, roadHash, frontageHash, k.drivewaysSha256, out _, out packed);
        }

        public static RoadNetworkAsset LoadBakedTown(string townId) => AssetDatabase.LoadAssetAtPath<RoadNetworkAsset>(TownAssetPath(townId));

        [MenuItem("Backroads/Roads/Bake town road networks (R0o)")]
        static void BakeTownsMenu()
        {
            foreach (var town in TownRoadSource.Towns())
            {
                bool ok = BakeTown(town, out var msg);
                if (ok) Debug.Log("[BR] " + msg); else Debug.LogError("[BR] " + msg);
            }
        }

        public static bool BakeTown(string townId, out string message)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fresh = BakeTownWithKerbs(townId, out var bake, out var kerbs, out var frontage);
            RoadGraph graph = bake.graph;
            var input = bake.input;
            var report = bake.import;
            EnsureFolder(BakedDir);
            string path = TownAssetPath(townId);
            var asset = LoadBakedTown(townId);
            if (asset == null)
            {
                AssetDatabase.CreateAsset(fresh, path);
                asset = fresh;
            }
            else
            {
                EditorUtility.CopySerialized(fresh, asset);
                asset.name = RoadNetworkAsset.TownResourceName(townId);
                EditorUtility.SetDirty(asset);
                UnityEngine.Object.DestroyImmediate(fresh);
            }
            AssetDatabase.SaveAssets();
            var reportPath = WriteTownReport(townId, graph, input, report, asset);
            var surfPath = SurfaceBaker.WriteReport(bake);
            RoadGraphMap.WriteJunctions(bake);
            frontage.roadContentHash = asset.contentHash;
            frontage.contentHash = frontage.ComputeHash();
            string frontageHash = frontage.contentHash;
            var kerbPath = KerbBaker.Save(kerbs, frontage);
            var chunkSet = BakeMeshes(kerbs, asset.contentHash, frontageHash, out var packed);
            int tris = chunkSet.lod0Triangles, nChunks = chunkSet.chunks.Length;
            var chunkPath = RoadChunkBaker.Save(townId, chunkSet, packed);
            var r4Plates = RoadPlateBuilder.Build(graph, bake.surfaces, kerbs.kerbs, bake.source?.dem, kerbs.settings, kerbs.cuts, kerbs.cutter);
            var r4Path = RoadR4Report.Write(kerbs, r4Plates);
            message = $"roads R0o + R1 + R2 + R3 baked {townId} in {sw.Elapsed.TotalMilliseconds:F0} ms: {RoadGraphReport.Summary(graph)}; {SurfaceChecks.Summary(bake.checks)}; {KerbBaker.Summary(kerbs)}; {nChunks} road chunks, {tris} triangles; content {asset.contentHash.Substring(0, 12)} -> {path}, {kerbPath}, {chunkPath}; reports {reportPath}, {surfPath}, {r4Path}";
            return true;
        }

        /// <summary>A junction's approaches as "R12 stop, R13 -, ...", for the close-pair lines.</summary>
        static string ArmControls(RoadGraph g, GraphNode n)
        {
            var parts = new List<string>();
            foreach (var id in n.arms)
            {
                var a = g.arms[id];
                parts.Add($"R{a.sourceRoad} {(a.control == Control.None ? "-" : a.control.ToString())}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// artifacts/roads/(town)/graph-report.txt: the card's report (junctions by kind, controls by source, complex pairs,
        /// steepest approaches), the import (ways used and skipped, cross sections, heights, the edge, mapped driveways),
        /// the node table and the notes.
        /// </summary>
        public static string WriteTownReport(string townId, RoadGraph g, RoadGraphInput input, OsmImportReport r, RoadNetworkAsset asset)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var dir = TownArtifactsDir(townId);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R0o bake of {g.source}: importer v{OsmRoadImporter.Version}, builder v{RoadGraphBuilder.Version}, manifest {g.sourceManifestSha256}");
            sb.AppendLine($"content hash {asset?.contentHash}");
            sb.AppendLine();
            sb.AppendLine("== Summary");
            sb.AppendLine(RoadGraphReport.Summary(g));
            var deg = g.nodes.Where(n => n.kind == NodeKind.Junction).GroupBy(n => n.Degree).OrderBy(x => x.Key).Select(x => $"{x.Count()} with {x.Key} arms");
            sb.AppendLine("junctions: " + string.Join(", ", deg));
            sb.AppendLine("nodes by kind: " + string.Join(", ", g.nodes.GroupBy(n => n.kind).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")));
            sb.AppendLine($"play-area edge ends: {g.nodes.Count(n => (n.flags & NodeFlags.PlayAreaEdge) != 0)}");
            RoadGraphChecks.Connected(g, false, out int reached, out int total);
            sb.AppendLine($"connected: {reached} of {total} nodes reachable from node 0; groups apart from the network: {r.groups.Count} ({r.groups.Count(x => x.kept)} kept)");
            foreach (var x in r.groups)
                sb.AppendLine(string.Format(inv, "  {0}: {1:F0} m at ({2:F0}, {3:F0}), {4}: {5}", x.kept ? "kept" : "dropped", x.length, x.at.x, x.at.z, x.reason, string.Join(" ", x.osmWays)));
            sb.AppendLine();
            sb.AppendLine("== Controls (per approach)");
            var ctl = g.arms.Where(a => a.control != Control.None).GroupBy(a => (a.control, a.controlSource)).OrderBy(x => x.Key.control).ThenBy(x => x.Key.controlSource);
            foreach (var x in ctl) sb.AppendLine($"  {x.Key.control} from {x.Key.controlSource.ToString().ToLowerInvariant()}: {x.Count()}");
            sb.AppendLine("nodes: " + string.Join(", ", g.nodes.Where(n => n.control != Control.None).GroupBy(n => (n.control, n.controlSource)).OrderBy(x => x.Key.control)
                .Select(x => $"{x.Key.control}/{x.Key.controlSource.ToString().ToLowerInvariant()} {x.Count()}")));
            sb.AppendLine($"mapped features: {g.features.Count} ({string.Join(", ", g.features.GroupBy(f => f.kind).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}"))}); " +
                          $"controlling nothing: {g.features.Count(f => f.problem.Length > 0)}; outside the play area: {r.featuresOutside}");
            foreach (var f in g.features.Where(f => f.problem.Length > 0)) sb.AppendLine($"  osm node {f.osmNode} {f.kind}: {f.problem}");
            sb.AppendLine($"signs overriding the class-first through road: {g.nodes.Count(n => (n.flags & NodeFlags.TagOverridesRule) != 0)}");
            sb.AppendLine();
            sb.AppendLine($"== Close junction pairs (roads-R4): {r.mergedJunctions.Count} merged at import (joined by a way under {new OsmImportSettings().mergeJunctions:F0} m), {g.complexPairs.Count} kept as two junctions (< 30 m)");
            foreach (var x in r.mergedJunctions) sb.AppendLine("  merged: " + x);
            foreach (var p in g.complexPairs.OrderBy(p => p.distance))
                sb.AppendLine(string.Format(inv, "  kept: nodes {0} and {1}: {2:F1} m at ({3:F0}, {4:F0}); controls {5} / {6}", p.a, p.b, p.distance, g.nodes[p.a].p.x, g.nodes[p.a].p.z,
                    ArmControls(g, g.nodes[p.a]), ArmControls(g, g.nodes[p.b])));
            sb.AppendLine();
            sb.AppendLine("== Steepest approaches (first 20 m, junctions)");
            foreach (var x in g.arms.Where(a => g.nodes[a.node].IsJunctionLike).OrderByDescending(a => Math.Abs(a.grade)).Take(15))
                sb.AppendLine(string.Format(inv, "  node {0} arm R{1} ({2}): {3:+0.0;-0.0} %", x.node, x.sourceRoad, x.cls, x.grade * 100));
            sb.AppendLine();
            sb.AppendLine("== Import");
            sb.AppendLine($"ways read {r.waysRead}, used {r.waysUsed}; {r.segments} pieces -> {r.chains} chains, {r.cornerBends} L-bends split, {r.strokes} height strokes -> {r.roads} roads");
            sb.AppendLine("skipped: " + string.Join("; ", r.skippedByReason.Select(kv => $"{kv.Value} {kv.Key}")));
            foreach (var s in r.skipped) sb.AppendLine("  " + s);
            sb.AppendLine("roads by class: " + string.Join(", ", input.roads.GroupBy(x => x.cls).OrderBy(x => x.Key)
                .Select(x => string.Format(inv, "{0} {1} ({2:F2} km)", x.Key, x.Count(), x.Sum(q => q.Length) / 1000))));
            sb.AppendLine("cross sections: " + string.Join(", ", input.roads.GroupBy(x => (x.cls, x.xs.source, w: Math.Round(x.xs.Width, 2))).OrderBy(x => x.Key.cls).ThenBy(x => x.Key.w)
                .Select(x => string.Format(inv, "{0} {1:F2} m from {2} x{3}", x.Key.cls, x.Key.w, x.Key.source, x.Count()))));
            sb.AppendLine($"road sides narrowed to clear a building: {r.fitted.Count}; still inside one lane of a building: {r.cannotClear.Count}");
            foreach (var x in r.fitted) sb.AppendLine("  " + x);
            foreach (var x in r.cannotClear) sb.AppendLine("  CANNOT CLEAR: " + x);
            sb.AppendLine(string.Format(inv, "heights: {0} samples; warped onto junctions {1}; moved by the final grade clamp {2}; largest change from the smoothed DEM outside warps and decks {3:F3} m; segments over the limit and steeper than the real road {4}",
                r.samples, r.warpedSamples, r.clampedSamples, r.maxDevFromSmoothed, r.gradeConflicts));
            sb.AppendLine(string.Format(inv, "centreline vs bare-earth DEM (outside decks): mean {0:F3} m, p95 {1:F3} m, max {2:F3} m", r.demMeanAbs, r.demP95Abs, r.demMaxAbs));
            sb.AppendLine($"steeper than the limit because the real road is ({r.steep.Count(s => s.road >= 0)} stretches in the play area):");
            foreach (var s in r.steep.Where(s => s.road >= 0).OrderByDescending(s => s.maxGrade))
                sb.AppendLine(string.Format(inv, "  road R{0} at ({1:F0}, {2:F0}): {3:F1} m, up to {4:F1} % (real road {5:F1} %)", s.road, s.at.x, s.at.z, s.length, s.maxGrade * 100, s.realGrade * 100));
            sb.AppendLine($"play-area edge (EDGE_RULE, roads end 50 m inside): {r.edgeCuts.Count} cuts");
            foreach (var c in r.edgeCuts)
                sb.AppendLine(string.Format(inv, "  {0} side: road R{1} ({2}) at ({3:F0}, {4:F0}); its next node is {5:F0} m beyond the cut", c.side, c.road, c.osmWay, c.at.x, c.at.z, c.junctionBeyond));
            sb.AppendLine($"mapped driveways and aisles meeting a road (for sites-S1): {r.driveways.Count}");
            sb.AppendLine();
            sb.Append(RoadGraphReport.NodeTable(g));
            var path = Path.Combine(dir, "graph-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            return path;
        }

        /// <summary>Writes artifacts/roads/graph-report.txt (the node table, the ribbon coverage) and returns its path.</summary>
        public static string WriteReport(RoadGraph graph, RoadGraphInput input, RoadNetworkAsset asset)
        {
            Directory.CreateDirectory(ArtifactsDir);
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R0 bake, builder v{RoadGraphBuilder.Version}, canon manifest {graph.sourceManifestSha256}");
            sb.AppendLine($"content hash {asset?.contentHash}");
            sb.AppendLine();
            sb.Append(RoadGraphReport.NodeTable(graph));
            var strips = asset != null ? PlainRibbonMesh.Strips(asset) : PlainRibbons.Build(graph);
            sb.AppendLine();
            sb.AppendLine("plain ribbons vs canon centrelines: " + RibbonCoverage.Measure(strips, input));
            var path = Path.Combine(ArtifactsDir, "graph-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            return path;
        }
    }

    /// <summary>Batchmode entry points (PLAN 1.9 roads-bake: -executeMethod Backroads.Roads.Editor.RoadBakeCli.Bake -verifyHash).</summary>
    public static class RoadBakeCli
    {
        /// <summary>
        /// Without -verifyHash: bakes and saves. With -verifyHash: bakes in memory and fails (exit 1) unless the committed
        /// asset has the same content hash, its stored hash matches its content, and two bakes agree.
        /// </summary>
        public static void Bake()
        {
            int code = 0;
            try
            {
                bool verify = Environment.GetCommandLineArgs().Contains("-verifyHash");
                if (verify)
                {
                    var a = RoadNetworkBaker.BakeInMemory(out var g, out var input, out var msg);
                    var b = RoadNetworkBaker.BakeInMemory(out _, out _, out _);
                    var baked = RoadNetworkBaker.LoadBaked();
                    if (a == null) { Debug.LogError("[BR] roads bake failed: " + msg); code = 2; }
                    else if (b == null || a.contentHash != b.contentHash) { Debug.LogError("[BR] roads bake is not deterministic"); code = 1; }
                    else if (baked == null) { Debug.LogError("[BR] no committed " + RoadNetworkBaker.AssetPath); code = 1; }
                    else if (baked.ComputeHash() != baked.contentHash) { Debug.LogError("[BR] committed road asset content does not match its stored hash"); code = 1; }
                    else if (baked.contentHash != a.contentHash) { Debug.LogError($"[BR] committed road asset is stale: {baked.contentHash} vs fresh {a.contentHash}"); code = 1; }
                    else Debug.Log($"[BR] roads bake verified: {a.contentHash}; {RoadGraphReport.Summary(g)}");
                }
                else
                {
                    bool ok = RoadNetworkBaker.Bake(out var msg);
                    if (ok) Debug.Log("[BR] " + msg); else { Debug.LogError("[BR] " + msg); code = 2; }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>
        /// roads-R2: builds every town's kerbs in memory (nothing saved) and writes the kerb report, the kerb maps and a
        /// plate count.  -executeMethod Backroads.Roads.Editor.RoadBakeCli.Kerbs
        /// </summary>
        public static void Kerbs()
        {
            int code = 0;
            try
            {
                foreach (var town in TownRoadSource.Towns())
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var b = KerbBaker.BuildTown(town);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    var plates = RoadPlateBuilder.Build(b.roads.graph, b.roads.surfaces, b.kerbs, b.roads.source?.dem, b.settings);
                    var fresh = KerbBaker.ToAsset(b, "");
                    Debug.Log($"[BR] {town}: R2 in {ms:F0} ms: {KerbBaker.Summary(b)}; {plates.Count} plates; report {KerbBaker.WriteReport(b, fresh)}");
                    foreach (var p in RoadGraphMap.WriteKerbs(b)) Debug.Log("[BR] wrote " + p);
                    // -at x,z [-size m]: a close-up there too (debugging)
                    var args = Environment.GetCommandLineArgs();
                    int ai = Array.IndexOf(args, "-at");
                    if (ai >= 0 && ai + 1 < args.Length)
                    {
                        var xz = args[ai + 1].Split(',');
                        int si = Array.IndexOf(args, "-size");
                        float size = si >= 0 && si + 1 < args.Length ? float.Parse(args[si + 1], System.Globalization.CultureInfo.InvariantCulture) : 30f;
                        var at = new Vector2(float.Parse(xz[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xz[1], System.Globalization.CultureInfo.InvariantCulture));
                        Debug.Log("[BR] wrote " + RoadGraphMap.WriteKerbsAt(b, at, size));
                    }
                    UnityEngine.Object.DestroyImmediate(fresh);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>
        /// roads-R3: every town's streets, props and road meshes in memory (nothing saved): counts and the mesh report.
        ///   -executeMethod Backroads.Roads.Editor.RoadBakeCli.Meshes
        /// </summary>
        public static void Meshes()
        {
            int code = 0;
            try
            {
                foreach (var town in TownRoadSource.Towns())
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var a = RoadNetworkBaker.BakeTownWithKerbs(town, out _, out var k, out var f);
                    var c = RoadNetworkBaker.BakeMeshes(k, a.contentHash, f.contentHash, out var packed);
                    Debug.Log($"[BR] {town}: R3 in {sw.Elapsed.TotalMilliseconds:F0} ms: {k.streets.Count} streets, {k.props.Count} props; {c.chunks.Length} chunks, LOD0 {c.lod0Triangles} / LOD1 {c.lod1Triangles} / collider {c.colliderTriangles} triangles, {packed.Length / 1024} KiB packed; report {RoadMeshReport.Write(k, c, a)}");
                    var plates = RoadPlateBuilder.Build(k.roads.graph, k.roads.surfaces, k.kerbs, k.roads.source?.dem, k.settings, k.cuts, k.cutter);
                    Debug.Log($"[BR] {town}: R4 {RoadR4Report.Summary(RoadR4Report.Run(k, plates))}; report {RoadR4Report.Write(k, plates)}");
                    UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(f); UnityEngine.Object.DestroyImmediate(c);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>roads-R1: bakes every town's junction surfaces in memory and writes the report and the junction maps.</summary>
        public static void Surfaces()
        {
            int code = 0;
            try
            {
                foreach (var town in TownRoadSource.Towns())
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var b = SurfaceBaker.BuildTown(town);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    Debug.Log($"[BR] {town}: R1 in {ms:F0} ms: {SurfaceChecks.Summary(b.checks)}; report {SurfaceBaker.WriteReport(b)}");
                    foreach (var p in RoadGraphMap.WriteJunctions(b)) Debug.Log("[BR] wrote " + p);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>
        /// roads-R0o: bakes every TownData town (Assets/Backroads/Roads/Baked/Resources/BrRoadNetwork_(town).asset, the report
        /// and the maps in artifacts/roads/(town)/). With -verifyHash: bakes twice in memory and fails (exit 1) unless the two
        /// agree and match the committed asset.
        ///   -executeMethod Backroads.Roads.Editor.RoadBakeCli.BakeTowns [-verifyHash]
        /// </summary>
        public static void BakeTowns()
        {
            int code = 0;
            try
            {
                bool verify = Environment.GetCommandLineArgs().Contains("-verifyHash");
                var towns = TownRoadSource.Towns();
                if (towns.Count == 0) { Debug.LogError("[BR] no TownData/<town>/v1"); code = 2; }
                foreach (var town in towns)
                {
                    if (verify)
                    {
                        var a = RoadNetworkBaker.BakeTownWithKerbs(town, out var ba, out var ka, out var fa);
                        var b = RoadNetworkBaker.BakeTownWithKerbs(town, out _, out var kb, out var fb);
                        var ca = RoadNetworkBaker.BakeMeshes(ka, a.contentHash, fa.contentHash, out _);
                        var cb = RoadNetworkBaker.BakeMeshes(kb, b.contentHash, fb.contentHash, out _);
                        var bakedC = RoadChunkBaker.LoadBakedTown(town);
                        var g = ba.graph;
                        var baked = RoadNetworkBaker.LoadBakedTown(town);
                        var bakedK = KerbBaker.LoadBakedTown(town);
                        if (ca.contentHash != cb.contentHash) { Debug.LogError($"[BR] {town}: road mesh bake is not deterministic"); code = 1; }
                        else if (bakedC == null) { Debug.LogError("[BR] no committed " + RoadChunkBaker.TownAssetPath(town)); code = 1; }
                        else if (bakedC.contentHash != ca.contentHash) { Debug.LogError($"[BR] {town}: committed road chunks are stale: {bakedC.contentHash} vs fresh {ca.contentHash}"); code = 1; }
                        else if (a.contentHash != b.contentHash) { Debug.LogError($"[BR] {town}: roads bake is not deterministic"); code = 1; }
                        else if (fa.contentHash != fb.contentHash) { Debug.LogError($"[BR] {town}: kerb frontage bake is not deterministic"); code = 1; }
                        else if (baked == null) { Debug.LogError("[BR] no committed " + RoadNetworkBaker.TownAssetPath(town)); code = 1; }
                        else if (bakedK == null) { Debug.LogError("[BR] no committed " + KerbBaker.TownAssetPath(town)); code = 1; }
                        else if (baked.ComputeHash() != baked.contentHash) { Debug.LogError($"[BR] {town}: committed asset content does not match its stored hash"); code = 1; }
                        else if (bakedK.ComputeHash() != bakedK.contentHash) { Debug.LogError($"[BR] {town}: committed kerb frontage content does not match its stored hash"); code = 1; }
                        else if (baked.contentHash != a.contentHash) { Debug.LogError($"[BR] {town}: committed road asset is stale: {baked.contentHash} vs fresh {a.contentHash}"); code = 1; }
                        else if (bakedK.contentHash != fa.contentHash) { Debug.LogError($"[BR] {town}: committed kerb frontage is stale: {bakedK.contentHash} vs fresh {fa.contentHash}"); code = 1; }
                        else Debug.Log($"[BR] {town}: roads bake verified: {a.contentHash}, frontage {fa.contentHash}, chunks {ca.contentHash}; {RoadGraphReport.Summary(g)}; {KerbBaker.Summary(ka)}; {ca.chunks.Length} chunks, {ca.lod0Triangles} triangles");
                    }
                    else
                    {
                        bool ok = RoadNetworkBaker.BakeTown(town, out var msg);
                        if (ok) Debug.Log("[BR] " + msg); else { Debug.LogError("[BR] " + msg); code = 2; }
                        foreach (var p in RoadGraphMap.WriteTown(town, out var mm)) Debug.Log("[BR] wrote " + p);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 2;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
