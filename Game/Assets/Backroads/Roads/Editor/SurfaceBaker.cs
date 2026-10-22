using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R1 in the editor: a town's graph (R0o) -> SurfaceBuilder -> the checks, the report
    /// (artifacts/roads/(town)/surfaces-report.txt) and the junction maps (graph-junctions*.png).
    ///   menu: Backroads > Roads > Bake junction surfaces (R1)
    ///   batchmode: -executeMethod Backroads.Roads.Editor.RoadBakeCli.Surfaces
    /// </summary>
    public static class SurfaceBaker
    {
        public sealed class Bake
        {
            public string town;
            public RoadGraph graph;
            public RoadGraphInput input;
            public OsmImportReport import;
            public RoadSurfaces surfaces;
            public SurfaceChecks.Result checks;
        }

        public static Bake BuildTown(string townId)
        {
            var b = new Bake { town = townId };
            b.graph = RoadNetworkBaker.BuildTown(townId, out b.input, out b.import);
            b.surfaces = SurfaceBuilder.Build(b.graph, b.input, RoadOverrides.Load(townId));
            b.checks = SurfaceChecks.Run(b.graph, b.input, b.surfaces);
            return b;
        }

        [MenuItem("Backroads/Roads/Bake junction surfaces (R1)")]
        static void Menu()
        {
            foreach (var town in TownRoadSource.Towns())
            {
                var b = BuildTown(town);
                Debug.Log("[BR] " + town + ": " + SurfaceChecks.Summary(b.checks) + " -> " + WriteReport(b));
                foreach (var p in RoadGraphMap.WriteJunctions(b)) Debug.Log("[BR] wrote " + p);
            }
        }

        public static string WriteReport(Bake b)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var dir = RoadNetworkBaker.TownArtifactsDir(b.town);
            Directory.CreateDirectory(dir);
            var c = b.checks;
            var s = b.surfaces;
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads roads R1 (junction surfaces) of {b.graph.source}: builder v{SurfaceBuilder.Version}");
            sb.AppendLine();
            sb.AppendLine("== Summary");
            sb.AppendLine(SurfaceChecks.Summary(c));
            sb.AppendLine("modes: " + string.Join(", ", s.nodes.GroupBy(n => n.mode).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")));
            sb.AppendLine("corners: " + string.Join(", ", s.nodes.SelectMany(n => n.corners).GroupBy(k => k.kind).OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Count()}")));
            sb.AppendLine("fillet radii: " + string.Join(", ", s.nodes.SelectMany(n => n.corners).Where(k => k.kind == CornerKind.Fillet)
                .GroupBy(k => Math.Round(k.radius, 2)).OrderBy(x => x.Key).Select(x => string.Format(inv, "{0:0.##} m x{1}", x.Key, x.Count()))));
            var arms = s.nodes.Where(n => n.mode != SurfaceMode.Fallback).SelectMany(n => n.arms).ToList();
            sb.AppendLine($"stop lines {arms.Count(a => a.sStop >= 0)} ({arms.Count(a => a.stopMoved)} moved to a flatter spot); crosswalk bands {arms.Count(a => a.crosswalk)}; ribbons {s.ribbons.Count}; inlets {s.inlets.Count} ("
                          + string.Join(", ", s.inlets.GroupBy(i => i.why).OrderBy(x => x.Key).Select(x => $"{x.Count()} {x.Key}")) + ")");
            sb.AppendLine();
            void Section(string title, System.Collections.Generic.List<string> lines)
            {
                sb.AppendLine($"== {title} ({lines.Count})");
                foreach (var l in lines) sb.AppendLine("  " + l);
                sb.AppendLine();
            }
            Section("Fallbacks (no surface; the ribbons overlap)", c.fallbackNodes);
            Section("Not simple or not counter-clockwise", c.notSimple);
            Section("Junction area outside the bound (5 % regular, 15 % irregular) from the corridor estimate", c.areaOff);
            Section("Irregular junctions 5-15 % from the corridor estimate (allowed, listed)", c.irregularAreas);
            Section("Seam problems", c.seams);
            Section("Kerb lines entering a building", c.kerbInBuilding);
            Section("Known map conflicts (a building within one lane of a road's centreline; no width clears it)", c.knownMapConflicts);
            Section("Corners with a reduced radius, and why", c.reducedCorners);
            sb.AppendLine("== Notes");
            foreach (var n in s.notes) sb.AppendLine("  " + n);
            sb.AppendLine();
            sb.AppendLine("== Junctions (node | mode | arms: mouth / stop / crosswalk | corners: kind radius)");
            foreach (var ns in s.nodes)
            {
                var n = b.graph.nodes[ns.node];
                string armText = string.Join(" ", ns.arms.Select(a => string.Format(inv, "R{0}(hw {4:F1}/{5:F1}):{1:F1}{2}{3}", b.graph.edges[a.edge].sourceRoad, a.sMouth,
                    a.sStop >= 0 ? string.Format(inv, "/s{0:F1}{1}", a.sStop, a.stopMoved ? "*" : "") : "", a.crosswalk ? string.Format(inv, "/c{0:F1}-{1:F1}", a.sCwInner, a.sCwOuter) : "", a.hwR, a.hwL)));
                string cornerText = string.Join(" ", ns.corners.Select(k => k.kind == CornerKind.Fillet ? k.radius.ToString("0.#", inv) : k.kind.ToString().ToLowerInvariant()));
                sb.AppendLine(string.Format(inv, "{0} | {1} | {2} | {3} | area {4:F0} m2 (corridors {5:F0})", n.id, ns.mode, armText, cornerText, ns.area, ns.corridorArea));
            }
            var path = Path.Combine(dir, "surfaces-report.txt");
            File.WriteAllText(path, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            return path;
        }
    }
}
