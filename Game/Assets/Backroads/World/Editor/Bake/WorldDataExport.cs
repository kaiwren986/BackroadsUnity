using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Unity.Mathematics;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.2 step 5 (W8): writes a bake to WorldData/(world)/v1/ in the CanonData v1 layer formats (headerless little-endian;
    /// k = j * VN + i, JS space x_js = i * cell - half = -x, z = j * cell - half): terrain/heights.f32, base.f32,
    /// surface.u8, wear.u8, roadd.u8, mud.u8, tint.f32 (TintN^2 x 2); water.json (river reaches with their flat levels, the
    /// weir, the lock) and structures.json (the bridge decks, for dropping the ground's collision under them), both in JS
    /// space; and manifest.json (format, the grid block that WorldGridLayout reads, the frame, the TownData manifest's
    /// hash (not its folder name, which is the real town's) and in-game name, the bake's commit and versions, the plate
    /// list's hash, and bytes + SHA-256 of every other file). Only
    /// manifest.json carries the commit, so the other files' hashes depend on the inputs alone.
    /// </summary>
    public static class WorldDataExport
    {
        public const string Format = "backroads-world";
        public const int FormatVersion = 1;

        public static readonly string[] LayerFiles =
            { "terrain/heights.f32", "terrain/base.f32", "terrain/surface.u8", "terrain/wear.u8", "terrain/roadd.u8", "terrain/mud.u8", "terrain/tint.f32" };

        public static string WorldDir(string worldId) => RepoPaths.InRepo("WorldData/" + worldId + "/v1");

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The bytes of every exported file except the manifest, by path.</summary>
        public static SortedDictionary<string, byte[]> Files(WorldBakeResult r)
        {
            var g = r.grid;
            var f = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["terrain/heights.f32"] = Bytes(g.heights),
                ["terrain/base.f32"] = Bytes(g.@base),
                ["terrain/surface.u8"] = (byte[])g.surface.Clone(),
                ["terrain/wear.u8"] = (byte[])g.wear.Clone(),
                ["terrain/roadd.u8"] = (byte[])g.roadd.Clone(),
                ["terrain/mud.u8"] = (byte[])g.mud.Clone(),
                ["terrain/tint.f32"] = Bytes(g.tint),
                ["water.json"] = Encoding.UTF8.GetBytes(WaterJson(r)),
                ["structures.json"] = Encoding.UTF8.GetBytes(StructuresJson(r)),
            };
            return f;
        }

        public static byte[] Bytes(float[] a)
        {
            var b = new byte[a.Length * 4];
            Buffer.BlockCopy(a, 0, b, 0, b.Length);
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("big-endian host");
            return b;
        }

        public static string Sha256(byte[] b)
        {
            using var sha = SHA256.Create();
            return CanonRuntime.Hex(sha.ComputeHash(b));
        }

        public static SortedDictionary<string, string> Hashes(SortedDictionary<string, byte[]> files)
        {
            var h = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in files) h[kv.Key] = Sha256(kv.Value);
            return h;
        }

        /// <summary>Writes every file and the manifest to dir; returns the manifest text.</summary>
        public static string Write(WorldBakeResult r, string dir)
        {
            var files = Files(r);
            foreach (var kv in files)
            {
                var path = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, kv.Value);
            }
            var manifest = Manifest(r, files);
            File.WriteAllBytes(Path.Combine(dir, "manifest.json"), Encoding.UTF8.GetBytes(manifest));
            return manifest;
        }

        // ------------------------------------------------------------------------------------------ JSON (sorted keys, LF)

        static string N(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", Inv);
        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>A ring in JS space (x_js = -x), rounded to the millimetre like TownData.</summary>
        static string Ring(IReadOnlyList<double2> ring)
        {
            var sb = new StringBuilder("[");
            for (int k = 0; k < ring.Count; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append('[').Append(N(Math.Round(-ring[k].x, 3) + 0.0)).Append(',').Append(N(Math.Round(ring[k].y, 3) + 0.0)).Append(']');
            }
            return sb.Append(']').ToString();
        }

        static string WaterJson(WorldBakeResult r)
        {
            var rv = r.river;
            var sb = new StringBuilder();
            sb.Append("{\n  \"format\": \"backroads-world-water\",\n  \"lock\": {");
            sb.Append("\"chambers\": [").Append(string.Join(",", rv.lockChambers.Select(c => Ring(c)))).Append("], ");
            sb.Append("\"level\": ").Append(N(rv.lockLevel)).Append(", ");
            sb.Append("\"wallTop\": ").Append(N(rv.lockWallTop)).Append(", ");
            sb.Append("\"walls\": [").Append(string.Join(",", rv.lockWalls.Select(c => Ring(c)))).Append("]},\n");
            sb.Append("  \"note\": \"river reaches with one flat water level each (y = elevation - Y0, the TownData frame); polygons in JS space (x_js = -x east, z north), outer CCW in the local frame; the weir crest and the lock chamber (walled, emptied to the lower level)\",\n");
            sb.Append("  \"reaches\": [");
            bool first = true;
            foreach (var reach in rv.reaches)
            {
                if (reach.id == "lock") continue;
                sb.Append(first ? "\n" : ",\n");
                first = false;
                sb.Append("    {\"id\": ").Append(Q(reach.id)).Append(", \"level\": ").Append(N(reach.level)).Append(", \"polys\": [");
                sb.Append(string.Join(",", reach.polys.Select(p =>
                    "{\"holes\": [" + string.Join(",", p.holes.Select(h => Ring(h))) + "], \"outer\": " + Ring(p.outer) + "}")));
                sb.Append("], \"wetVertices\": ").Append(reach.wetVertices).Append('}');
            }
            sb.Append("\n  ],\n  \"version\": 1,\n  \"weir\": {\"crest\": ").Append(N(rv.weirCrest)).Append(", \"lines\": [");
            sb.Append(string.Join(",", rv.weirLines.Select(l => Ring(l)))).Append("]}\n}\n");
            return sb.ToString();
        }

        static string StructuresJson(WorldBakeResult r)
        {
            var sb = new StringBuilder("{\n  \"decks\": [");
            for (int i = 0; i < r.decks.Count; i++)
            {
                var d = r.decks[i];
                var outline = d.outline.Select(p => new double2(p.x, p.z)).ToList();
                bool overRiver = OverRiver(r, outline);
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {\"id\": ").Append(d.id).Append(", \"length\": ").Append(N(Math.Round(d.length, 3)));
                sb.Append(", \"osmWay\": ").Append(Q(d.osmWay)).Append(", \"outline\": ").Append(Ring(outline));
                sb.Append(", \"over\": ").Append(Q(d.over)).Append(", \"overRiver\": ").Append(overRiver ? "true" : "false");
                sb.Append(", \"yMax\": ").Append(N(Math.Round(d.yMax, 3))).Append(", \"yMin\": ").Append(N(Math.Round(d.yMin, 3))).Append('}');
            }
            sb.Append("\n  ],\n  \"format\": \"backroads-world-structures\",\n");
            sb.Append("  \"note\": \"bridge decks of lane R (BridgeBuilder): outline = the deck's footprint (outer faces), JS space; where overRiver, the ground's collision under the deck is dropped at run time (T3.3)\",\n");
            sb.Append("  \"version\": 1\n}\n");
            return sb.ToString();
        }

        static bool OverRiver(WorldBakeResult r, List<double2> outline)
        {
            foreach (var reach in r.river.reaches)
                foreach (var p in reach.polys)
                    foreach (var q in outline)
                        if (Raster.Inside(q, p.Rings())) return true;
            return false;
        }

        public static string Manifest(WorldBakeResult r, SortedDictionary<string, byte[]> files)
        {
            var L = r.grid.L;
            var t = r.town;
            string commit = RepoPaths.Git("rev-parse HEAD") ?? "unknown";
            string status = RepoPaths.Git("status --porcelain --untracked-files=no -- Game/Assets/Backroads tools/world TownData") ?? "";
            var sb = new StringBuilder("{\n");
            sb.Append("  \"bake\": {");
            sb.Append("\"commit\": ").Append(Q(commit)).Append(", \"dirty\": ").Append(status.Trim().Length > 0 ? "true" : "false");
            sb.Append(", \"plates\": {\"byStage\": [").Append(string.Join(", ", r.applied.platesByStage)).Append("], \"count\": ").Append(r.applied.plates);
            sb.Append(", \"extra\": ").Append(r.extraPlates).Append(", \"pads\": ").Append(r.padPlates).Append(", \"roads\": ").Append(r.roadPlates);
            sb.Append(", \"sha256\": ").Append(Q(r.platesHash));
            sb.Append(", \"sources\": {").Append(string.Join(", ", r.sourcePlates.Select(kv => Q(kv.Key) + ": " + kv.Value))).Append("}}");
            sb.Append(", \"seed\": ").Append(r.options.seed.ToString(Inv));
            sb.Append(", \"versions\": {\"bridgeBuilder\": ").Append(BridgeBuilder.Version).Append(", \"plateSink\": ").Append(PlateSink.Version);
            sb.Append(", \"roadPlateBuilder\": ").Append(RoadPlateBuilder.Version).Append(", \"worldBake\": ").Append(WorldBake.Version).Append("}},\n");
            sb.Append("  \"files\": {");
            bool first = true;
            foreach (var kv in files)
            {
                sb.Append(first ? "\n" : ",\n");
                first = false;
                sb.Append("    ").Append(Q(kv.Key)).Append(": {\"bytes\": ").Append(kv.Value.Length).Append(", \"sha256\": ").Append(Q(Sha256(kv.Value))).Append('}');
            }
            sb.Append("\n  },\n");
            sb.Append("  \"format\": ").Append(Q(Format)).Append(",\n");
            sb.Append("  \"frame\": {\"Y0\": ").Append(N(t.frame.y0)).Append(", \"northern\": ").Append(t.frame.northern ? "true" : "false");
            sb.Append(", \"originEasting\": ").Append(N(t.frame.originEasting)).Append(", \"originNorthing\": ").Append(N(t.frame.originNorthing));
            sb.Append(", \"zone\": ").Append(t.frame.zone).Append("},\n");
            sb.Append("  \"grid\": {\"N\": ").Append(L.N).Append(", \"VN\": ").Append(L.VN).Append(", \"cell\": ").Append(N(L.Cell)).Append(", \"half\": ").Append(N(L.Half));
            sb.Append(", \"layout\": \"k = j*VN + i; x = i*cell - half; z = j*cell - half (JS space: x = -east); tint: ((j*tintN + i)*2 + {0: tb, 1: td}), x = i*tintCell - half\"");
            sb.Append(", \"size\": ").Append(N(L.Size)).Append(", \"tintCell\": ").Append(N(L.TintCell)).Append(", \"tintN\": ").Append(L.TintN).Append("},\n");
            sb.Append("  \"layers\": \"CanonData v1 formats: heights and base float32, surface (Grass 0, Dirt 1, Sand 2, Rock 3, Road 4, Gravel 5, Concrete 6, Track 7, Mud 8), wear, roadd (x 0.5 m) and mud (/ 255) u8, tint float32 pairs; y = elevation - Y0\",\n");
            // the extract by its manifest's hash and the in-game name: the real town's id never ships (ADR-006 section 6)
            sb.Append("  \"town\": {\"manifestSha256\": ").Append(Q(t.manifestSha256)).Append(", \"name\": ").Append(Q(t.townName)).Append(", \"version\": 1},\n");
            sb.Append("  \"version\": ").Append(FormatVersion).Append(",\n");
            sb.Append("  \"world\": ").Append(Q(r.options.worldId)).Append("\n}\n");
            return sb.ToString();
        }
    }
}
