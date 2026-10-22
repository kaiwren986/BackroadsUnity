using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Canon.Editor;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Backroads.World.Bake
{
    /// <summary>
    /// The T3.2 bake from the command line and the menu:
    ///   menu: Backroads > World > Bake Millbrook (T3.2)
    ///   batchmode: tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Export   (writes WorldData/millbrook/v1)
    ///              tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Verify   (bakes in memory, compares with the committed manifest)
    /// Both write artifacts/world/(world)/bake-report.txt and the previews (bake-*.png).
    /// </summary>
    public static class WorldBakeCli
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [MenuItem("Backroads/World/Bake Millbrook (T3.2)")]
        static void Menu() => Run(true);

        public static void Export() => Exit(Run(true));

        public static void Verify() => Exit(Run(false));

        static void Exit(int code)
        {
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Arg(string name, string fallback)
        {
            var a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback;
        }

        static int Run(bool write)
        {
            try
            {
                var o = new WorldBakeOptions { townId = Arg("-town", null), worldId = Arg("-world", "millbrook"), log = s => Debug.Log("[BR] " + s) };
                var r = WorldBake.Run(o);
                var dir = Arg("-out", null) ?? WorldDataExport.WorldDir(o.worldId);
                var art = Path.Combine(RepoPaths.Artifacts, "world", o.worldId);
                Directory.CreateDirectory(art);
                var files = WorldDataExport.Files(r);
                var hashes = WorldDataExport.Hashes(files);
                string verdict;
                int code = 0;
                if (write)
                {
                    WorldDataExport.Write(r, dir);
                    verdict = "written to " + RepoPaths.Relative(dir);
                }
                else
                {
                    var committed = CommittedHashes(dir);
                    var bad = hashes.Where(kv => !committed.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).ToList();
                    verdict = bad.Count == 0 ? "matches the committed manifest" : "DIFFERS from the committed manifest: " + string.Join(", ", bad);
                    code = bad.Count == 0 ? 0 : 1;
                }
                var report = WorldBakeReport.Text(r, hashes, verdict);
                File.WriteAllText(Path.Combine(art, "bake-report.txt"), report);
                WorldBakePreview.WriteAll(r, art);
                Debug.Log("[BR] world bake: " + verdict + "\n" + report);
                return code;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return 1;
            }
        }

        /// <summary>The "files" hashes of a committed WorldData manifest (empty when there is none).</summary>
        public static System.Collections.Generic.Dictionary<string, string> CommittedHashes(string dir)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            var path = Path.Combine(dir, "manifest.json");
            if (!File.Exists(path)) return d;
            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(path)), "manifest");
            if (CanonJson.Get(m, "files") is System.Collections.Generic.Dictionary<string, object> f)
                foreach (var kv in f) d[kv.Key] = CanonJson.Str(CanonJson.Obj(kv.Value), "sha256");
            return d;
        }
    }

    public static class WorldBakeReport
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Text(WorldBakeResult r, System.Collections.Generic.IDictionary<string, string> hashes, string verdict)
        {
            var g = r.grid;
            var sb = new StringBuilder();
            sb.AppendLine($"Backroads world bake (T3.2) of {r.options.worldId} from TownData/{r.town.townId}/v1: worldBake v{WorldBake.Version}, plates v{PlateSink.Version}; {verdict}");
            sb.AppendLine($"grid: {g.L}");
            sb.AppendLine("times (ms): " + string.Join(", ", r.ms.Select(kv => kv.Key + " " + kv.Value.ToString("F0", Inv))));
            sb.AppendLine();
            sb.AppendLine("river:");
            foreach (var reach in r.river.reaches)
                sb.AppendLine(string.Format(Inv, "  {0}: level {1:F3} m, {2} carved vertices", reach.id, reach.level, reach.wetVertices));
            sb.AppendLine(string.Format(Inv, "  weir crest {0:F3} m on {1} line(s); lock chambers {2} (level {3:F3}, walls to {4:F3}); {5} vertices beside the water lifted to 5 cm over it",
                r.river.weirCrest, r.river.weirLines.Count, r.river.lockChambers.Count, r.river.lockLevel, r.river.lockWallTop, r.river.lifted));
            var s = r.surfaces;
            sb.AppendLine(string.Format(Inv, "surfaces (vertices before plates): farmland {0}, wood {1}, marsh {2}, quarry gravel {3}, rock {4}, river banks {5}, riverbed {6}",
                s.farmland, s.wood, s.marsh, s.quarry, s.rock, s.bank, s.riverbed));
            var a = r.applied;
            sb.AppendLine(string.Format(Inv, "plates: {0} (roads {1}, pads {2}, extra {3}; by stage {4}); vertices set by stage {5}; blended by stage {6}; plates covering no vertex {7}; {8:F0} ms, Burst {9}; list sha256 {10}",
                a.plates, r.roadPlates, r.padPlates, r.extraPlates, string.Join("/", a.platesByStage), string.Join("/", a.verticesByStage), string.Join("/", a.blendedByStage),
                a.noVertex, a.ms, a.ranBurst, r.platesHash));
            sb.AppendLine(string.Format(Inv, "road distance: {0} carriageway vertices", r.carriageway));
            var counts = new int[16];
            foreach (var b in g.surface) counts[Math.Min(15, (int)b)]++;
            sb.AppendLine("surface counts (final): " + string.Join(", ", Enumerable.Range(0, 9).Select(i => $"{i}:{counts[i]}")));
            double hmin = double.MaxValue, hmax = double.MinValue;
            foreach (var h in g.heights) { hmin = Math.Min(hmin, h); hmax = Math.Max(hmax, h); }
            sb.AppendLine(string.Format(Inv, "heights {0:F2} .. {1:F2} m", hmin, hmax));
            sb.AppendLine(string.Format(Inv, "decks: {0}", string.Join("; ", r.decks.Select(d => $"#{d.id} {d.osmWay} over {d.over} {d.length:F1} m"))));
            sb.AppendLine();
            sb.AppendLine("files:");
            foreach (var kv in hashes) sb.AppendLine("  " + kv.Key + "  " + kv.Value);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Preview maps of a bake (artifacts/world/(world)/bake-*.png): the whole square at 1 m per pixel (hillshade x surface
    /// colour, water blue) and close-ups at 0.5 m per pixel of the bridge, the weir and lock, the marsh and the quarry.
    /// </summary>
    public static class WorldBakePreview
    {
        static readonly Color32[] SurfaceColour =
        {
            new Color32(92, 133, 64, 255), new Color32(122, 92, 61, 255), new Color32(204, 184, 140, 255), new Color32(128, 122, 115, 255),
            new Color32(64, 64, 69, 255), new Color32(140, 133, 120, 255), new Color32(158, 156, 148, 255), new Color32(143, 115, 84, 255),
            new Color32(77, 56, 36, 255),
        };

        public static void WriteAll(WorldBakeResult r, string dir)
        {
            var g = r.grid;
            Write(r, Path.Combine(dir, "bake-overview.png"), new double2(0, 0), g.Half, 2048);
            var bridge = r.decks.FirstOrDefault(d => d.over == "river");
            var bc = bridge != null && bridge.outline.Length > 0
                ? new double2(bridge.outline.Average(p => p.x), bridge.outline.Average(p => p.z)) : new double2(666, 345);
            Write(r, Path.Combine(dir, "bake-bridge.png"), bc, 160, 640);
            if (r.town.weirs.Count > 0)
            {
                var w = r.town.weirs[0];
                Write(r, Path.Combine(dir, "bake-weir-lock.png"), new double2(w.Average(p => p.x), w.Average(p => p.y)), 110, 440);
            }
            var marsh = r.town.Site("wetland");
            if (marsh != null) Write(r, Path.Combine(dir, "bake-marsh.png"), marsh.local, 200, 800);
            var quarry = r.town.Site("quarry");
            if (quarry != null) Write(r, Path.Combine(dir, "bake-quarry.png"), quarry.local, 90, 360);
        }

        /// <summary>A square map centred at c (local), half side `half` metres, size x size pixels, north up, east right.</summary>
        public static void Write(WorldBakeResult r, string path, double2 c, double half, int size)
        {
            var g = r.grid;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            double mpp = 2 * half / size;
            var light = math.normalize(new double3(-1, 1.4, 1));   // from the north-west, local frame (x east)
            for (int py = 0; py < size; py++)
                for (int pxi = 0; pxi < size; pxi++)
                {
                    double x = c.x - half + (pxi + 0.5) * mpp, z = c.y - half + (py + 0.5) * mpp;
                    int i = (int)Math.Round(g.GridI(x)), j = (int)Math.Round(g.GridJ(z));
                    if (i < 1 || j < 1 || i >= g.N || j >= g.N) { px[py * size + pxi] = new Color32(0, 0, 0, 255); continue; }
                    int k = j * g.VN + i;
                    // the local-frame normal: x east = -i
                    double dx = (g.heights[k - 1] - g.heights[k + 1]) / (2 * g.Cell);
                    double dz = (g.heights[k + g.VN] - g.heights[k - g.VN]) / (2 * g.Cell);
                    var n = math.normalize(new double3(-dx, 1, -dz));
                    double shade = 0.45 + 0.6 * Math.Max(0, math.dot(n, light));
                    var sc = SurfaceColour[Math.Min(8, (int)g.surface[k])];
                    double rr = sc.r * shade, gg = sc.g * shade, bb = sc.b * shade;
                    int wet = r.river.wet[k];
                    if (wet != 0)
                    {
                        double depth = r.river.reaches[wet - 1].level - g.heights[k];
                        if (depth > 0)
                        {
                            double t = Math.Min(1, 0.45 + depth / 4);
                            rr = rr * (1 - t) + 40 * t; gg = gg * (1 - t) + 85 * t; bb = bb * (1 - t) + 120 * t;
                        }
                    }
                    if (r.applied.touched != null && r.applied.touched[k] == 2) { rr *= 0.92; gg *= 0.92; bb *= 0.92; }
                    px[py * size + pxi] = new Color32((byte)Math.Min(255, rr), (byte)Math.Min(255, gg), (byte)Math.Min(255, bb), 255);
                }
            tex.SetPixels32(px);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
