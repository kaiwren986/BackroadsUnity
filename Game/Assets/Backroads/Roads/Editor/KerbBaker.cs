using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Core.WorldBake;
using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R2 in the editor: a town's R0o graph and R1 surfaces -> KerbBuilder (loops, faces, corners, ramps, forbidden
    /// intervals) -> Assets/Backroads/Roads/Baked/Resources/BrKerbFrontage_(town).asset, the report
    /// (artifacts/roads/(town)/kerbs-report.txt), the maps (graph-kerbs*.png) and the road plates (RoadPlateBuilder) for
    /// lane W. RoadBakeCli.BakeTowns bakes it with the road network.
    ///   menu: Backroads > Roads > Bake kerbs and frontage (R2)
    /// </summary>
    public static class KerbBaker
    {
        public sealed class Bake
        {
            public SurfaceBaker.Bake roads;
            public KerbNetwork kerbs;
            public KerbSettings settings;
            public string town => roads.town;
        }

        public static string TownAssetPath(string townId) => RoadNetworkBaker.BakedDir + "/" + KerbFrontageAsset.TownResourceName(townId) + ".asset";

        public static Bake BuildTown(string townId) => FromRoads(SurfaceBaker.BuildTown(townId));

        public static Bake FromRoads(SurfaceBaker.Bake roads, KerbSettings st = null)
        {
            st = st ?? new KerbSettings();
            double half = roads.source != null ? roads.source.playHalf : 1024;
            return new Bake { roads = roads, settings = st, kerbs = KerbBuilder.Build(roads.graph, roads.input, roads.surfaces, half, st) };
        }

        /// <summary>A fresh frontage asset (not saved) for a bake; roadHash links it to the road network it was built with.</summary>
        public static KerbFrontageAsset ToAsset(Bake b, string roadHash)
        {
            var a = ScriptableObject.CreateInstance<KerbFrontageAsset>();
            a.name = KerbFrontageAsset.TownResourceName(b.town);
            a.SetFrom(b.kerbs, b.roads.graph.source, b.roads.graph.sourceManifestSha256, roadHash, KerbBuilder.Version);
            return a;
        }

        public static KerbFrontageAsset LoadBakedTown(string townId) => AssetDatabase.LoadAssetAtPath<KerbFrontageAsset>(TownAssetPath(townId));

        /// <summary>Saves the frontage asset in place (its GUID kept across re-bakes), writes the report and the maps.</summary>
        public static string Save(Bake b, KerbFrontageAsset fresh)
        {
            RoadNetworkBaker.EnsureFolder(RoadNetworkBaker.BakedDir);
            string path = TownAssetPath(b.town);
            var asset = LoadBakedTown(b.town);
            if (asset == null)
            {
                AssetDatabase.CreateAsset(fresh, path);
                asset = fresh;
            }
            else
            {
                EditorUtility.CopySerialized(fresh, asset);
                asset.name = KerbFrontageAsset.TownResourceName(b.town);
                EditorUtility.SetDirty(asset);
                UnityEngine.Object.DestroyImmediate(fresh);
            }
            AssetDatabase.SaveAssets();
            WriteReport(b, asset);
            RoadGraphMap.WriteKerbs(b);
            return path;
        }

        [MenuItem("Backroads/Roads/Bake kerbs and frontage (R2)")]
        static void Menu()
        {
            foreach (var town in TownRoadSource.Towns())
            {
                var b = BuildTown(town);
                var road = RoadNetworkBaker.LoadBakedTown(town);
                var path = Save(b, ToAsset(b, road != null ? road.contentHash : ""));
                Debug.Log("[BR] " + town + ": " + Summary(b) + " -> " + path);
            }
        }

        public static string Summary(Bake b)
        {
            var k = b.kerbs;
            int kerbed = k.faces.Count(f => f.kerbed), walks = k.faces.Count(f => f.walk);
            int kc = k.corners.Count(c => c.kerbed), ramps = k.corners.Count(c => c.kerbed && c.ramp != RampKind.None);
            return string.Format(CultureInfo.InvariantCulture, "{0} block loops ({1} holes, {2} slivers dropped), {3} faces ({4} kerbed, {5} with a walk, {6} unkerbed), {7} kerb returns ({8} kerbed, {9} with ramps), {10} sides unfaced, {11} faces closer to a building than the walk needs",
                k.loops.Count, k.loops.Count(l => l.hole), k.slivers, k.faces.Count, kerbed, walks, k.faces.Count - kerbed, k.corners.Count, kc, ramps, k.unfaced.Count, k.clearance.Count);
        }

        /// <summary>artifacts/roads/(town)/kerbs-report.txt.</summary>
        public static string WriteReport(Bake b, KerbFrontageAsset asset)
        {
            var inv = CultureInfo.InvariantCulture;
            var dir = RoadNetworkBaker.TownArtifactsDir(b.town);
            Directory.CreateDirectory(dir);
            var k = b.kerbs;
            var st = b.settings;
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R2 (kerbs, walks, ramps, frontage) of {b.roads.graph.source}: kerb builder v{KerbBuilder.Version}, plates v{RoadPlateBuilder.Version}");
            sb.AppendLine($"frontage content hash {asset?.contentHash}; road network {asset?.roadContentHash}");
            sb.AppendLine();
            sb.AppendLine("== Summary");
            sb.AppendLine(Summary(b));
            double kerbLen = k.faces.Where(f => f.kerbed).Sum(f => f.Length), walkLen = k.faces.Where(f => f.walk).Sum(f => f.Length), openLen = k.faces.Where(f => !f.kerbed).Sum(f => f.Length);
            sb.AppendLine(string.Format(inv, "kerb {0:F2} km, walks {1:F2} km, unkerbed edges {2:F2} km", kerbLen / 1000, walkLen / 1000, openLen / 1000));
            var widths = k.faces.Where(f => f.walk).SelectMany(f => f.width).ToList();
            if (widths.Count > 0)
                sb.AppendLine(string.Format(inv, "walk depth (kerb face to back edge, kerb top included): min {0:F2} m, median {1:F2} m, max {2:F2} m", widths.Min(), widths.OrderBy(x => x).ElementAt(widths.Count / 2), widths.Max()));
            sb.AppendLine("faces by class: " + string.Join(", ", k.faces.GroupBy(f => (f.cls, f.kerbed)).OrderBy(x => x.Key.cls).ThenBy(x => x.Key.kerbed)
                .Select(x => string.Format(inv, "{0}{1} {2}", x.Key.cls, x.Key.kerbed ? " kerbed" : "", x.Count()))));
            sb.AppendLine("ramps: " + string.Join(", ", k.corners.Where(c => c.kerbed).GroupBy(c => c.ramp).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")));
            var forb = k.faces.SelectMany(f => f.forbidden).GroupBy(f => f.reason).OrderBy(x => x.Key);
            sb.AppendLine("forbidden intervals: " + string.Join(", ", forb.Select(x => string.Format(inv, "{0} {1} ({2:F0} m)", x.Key, x.Count(), x.Sum(i => i.s1 - i.s0)))));
            double usable = 0, total = 0;
            foreach (var f in k.faces.Where(f => !f.bridge))
            {
                total += f.Length;
                usable += f.Length - Covered(f);
            }
            sb.AppendLine(string.Format(inv, "frontage open to driveways: {0:F2} of {1:F2} km ({2:F0} %)", usable / 1000, total / 1000, total > 0 ? 100 * usable / total : 0));
            sb.AppendLine(string.Format(inv, "ramp running slope {0:F1} % (design 7.5 %, maximum 8.33 %); walk cross fall {1:F1} % (maximum 2 %); landing {2:F1} x {2:F1} m; detectable warning {3:F2} m deep",
                100 * (st.barrier - st.dropped) / st.rampRun, 100 * st.walkCrossSlope, st.landing, st.warningDepth));
            sb.AppendLine();
            void Section(string title, IList<string> lines)
            {
                sb.AppendLine($"== {title} ({lines.Count})");
                foreach (var l in lines) sb.AppendLine("  " + l);
                sb.AppendLine();
            }
            Section("Kerb closer to a building than the walk needs (05 §8.4: the walk width; downtown 1.0 m)", k.clearance);
            Section("Road sides with no face (covered by other roads or junction surfaces)", k.unfaced);
            Section("Notes", k.notes);
            sb.AppendLine("== Kerb returns (corner | node | kind radius | arc | faces | ramp | lowered stretch)");
            foreach (var c in k.corners)
                sb.AppendLine(string.Format(inv, "{0} | node {1} | {2} {3:F1} m | {4:F1} m | {5} -> {6} | {7} | {8:F1} m", c.id, c.node, c.kind, c.radius, c.ArcLength, c.faceA, c.faceB,
                    c.kerbed ? c.ramp.ToString() : "unkerbed", c.ramp == RampKind.None ? 0 : c.dep1 - c.dep0));
            sb.AppendLine();
            sb.AppendLine("== Faces (id | osm way | class | side of edge | length | kerb | walk depth | forbidden)");
            foreach (var f in k.faces)
                sb.AppendLine(string.Format(inv, "{0} | {1} | {2} | e{3} {4} | {5:F1} m | {6} | {7} | {8}", f.id, f.osmWay, f.cls, f.edge, f.side == 0 ? "L" : "R", f.Length, f.kind,
                    f.walk ? string.Format(inv, "{0:F2}-{1:F2} m", f.width.Min(), f.width.Max()) : "-",
                    string.Join(" ", f.forbidden.Select(i => string.Format(inv, "{0}:{1:F1}-{2:F1}", Short(i.reason), i.s0, i.s1)))));
            var path = Path.Combine(dir, "kerbs-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            return path;
        }

        static string Short(FrontageBlock b)
        {
            switch (b)
            {
                case FrontageBlock.CornerReturn: return "C";
                case FrontageBlock.Crosswalk: return "X";
                case FrontageBlock.Ramp: return "R";
                case FrontageBlock.StopLine: return "S";
                case FrontageBlock.Inlet: return "I";
                case FrontageBlock.Pole: return "P";
                default: return "O";
            }
        }

        /// <summary>Metres of the face inside at least one forbidden interval.</summary>
        public static double Covered(KerbFace f)
        {
            var iv = f.forbidden.Select(i => (a: (double)i.s0, b: (double)i.s1)).OrderBy(i => i.a).ToList();
            double total = 0, end = double.NegativeInfinity;
            foreach (var (a, b) in iv)
            {
                double s = Math.Max(a, end);
                if (b > s) total += b - s;
                end = Math.Max(end, b);
            }
            return total;
        }
    }
}
