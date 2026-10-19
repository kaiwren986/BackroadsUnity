using System;
using System.IO;
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

        /// <summary>Writes artifacts/roads/graph-report.txt (the node table, the ribbon coverage) and returns its path.</summary>
        public static string WriteReport(RoadGraph graph, RoadGraphInput input, RoadNetworkAsset asset)
        {
            Directory.CreateDirectory(ArtifactsDir);
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R0 bake, builder v{RoadGraphBuilder.Version}, canon manifest {graph.canonManifestSha256}");
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
    }
}
