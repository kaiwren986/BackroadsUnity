using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads.Editor;
using UnityEditor;
using UnityEngine;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// sites-S1 in the editor: a town's blocks and parcels from TownData and roads-R2's kerb network (baked in memory, the
    /// same code that writes the committed KerbFrontage asset), the report artifacts/sites/(town)/parcels-report.txt and
    /// the plan maps parcels*.svg.
    ///   menu: Backroads > Sites > Build parcels (S1)
    ///   Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Sites.Editor.SitesBakeCli.Parcels
    /// </summary>
    public static class SiteParcels
    {
        public static string ArtifactsDir(string town) => Path.Combine(RepoPaths.Artifacts, "sites", town);

        public static List<string> Towns() => SiteTownData.Towns(RepoPaths.Root);

        public static ParcelSet BuildTown(string town, ParcelSettings st = null)
        {
            var data = SiteTownData.Load(RepoPaths.Root, town);
            return ParcelBuilder.Build(data, BuildBlocks(data), st);
        }

        /// <summary>The town's blocks from roads-R2's kerb network, baked in memory.</summary>
        public static BlockBuilder.Result BuildBlocks(SiteTownData data)
        {
            var kb = KerbBaker.BuildTown(data.townId);
            var alleys = new HashSet<string>(data.service.Where(w => w.Service == "alley").Select(w => w.osm), StringComparer.Ordinal);
            var keep = data.buildings.Where(b => b.inPlay && !b.Accessory).Select(b => b.poly);
            return BlockBuilder.Build(kb.kerbs, data.water, alleys, data.playHalf, keep);
        }

        /// <summary>The blocks and faces as JSON (artifacts/sites/(town)/blocks.json, a debugging aid).</summary>
        public static string BlocksJson(BlockBuilder.Result r)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1 << 20);
            const char Q = '"';
            void Pts(Unity.Mathematics.double2[] p)
            {
                sb.Append('[');
                for (int i = 0; i < p.Length; i++) sb.Append(i == 0 ? "" : ",").Append('[').Append(p[i].x.ToString("R", ci)).Append(',').Append(p[i].y.ToString("R", ci)).Append(']');
                sb.Append(']');
            }
            void Key(string k) => sb.Append(Q).Append(k).Append(Q).Append(':');
            sb.Append('{'); Key("ruralDepth"); sb.Append(r.ruralDepth.ToString("R", ci)).Append(','); Key("blocks"); sb.Append('[');
            for (int i = 0; i < r.blocks.Count; i++)
            {
                var b = r.blocks[i];
                sb.Append(i == 0 ? "{" : ",{");
                Key("id"); sb.Append(b.id).Append(',');
                Key("loop"); sb.Append(b.loop).Append(',');
                Key("rural"); sb.Append(b.rural ? "true" : "false").Append(',');
                Key("area"); sb.Append(b.area.ToString("R", ci)).Append(',');
                Key("faces"); sb.Append('[').Append(string.Join(",", b.faces)).Append("],");
                Key("outers"); sb.Append('[');
                for (int k = 0; k < b.outers.Count; k++) { if (k > 0) sb.Append(','); Pts(b.outers[k]); }
                sb.Append("],");
                Key("holes"); sb.Append('[');
                for (int k = 0; k < b.holes.Count; k++) { if (k > 0) sb.Append(','); Pts(b.holes[k]); }
                sb.Append("]}");
            }
            sb.Append("],"); Key("fronts"); sb.Append('[');
            for (int i = 0; i < r.fronts.Count; i++)
            {
                var f = r.fronts[i];
                sb.Append(i == 0 ? "{" : ",{");
                Key("faceId"); sb.Append(f.faceId).Append(',');
                Key("block"); sb.Append(f.block).Append(',');
                Key("osmWay"); sb.Append(Q).Append(f.osmWay).Append(Q).Append(',');
                Key("street"); sb.Append((int)f.street).Append(',');
                Key("kerbed"); sb.Append(f.kerbed ? "true" : "false").Append(',');
                Key("alley"); sb.Append(f.alley ? "true" : "false").Append(',');
                Key("bridge"); sb.Append(f.bridge ? "true" : "false").Append(',');
                Key("kerb"); Pts(f.kerb); sb.Append(',');
                Key("back"); Pts(f.back); sb.Append(',');
                Key("s"); sb.Append('[');
                for (int k = 0; k < f.s.Length; k++) sb.Append(k == 0 ? "" : ",").Append(f.s[k].ToString("R", ci));
                sb.Append("]}");
            }
            sb.Append("],"); Key("notes"); sb.Append('[');
            for (int i = 0; i < r.notes.Count; i++) sb.Append(i == 0 ? "" : ",").Append(Q).Append(r.notes[i].Replace(Q, '\'')).Append(Q);
            sb.Append("]}");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>Writes the report and the plan maps; returns the report path.</summary>
        public static string WriteArtifacts(ParcelSet set)
        {
            var dir = ArtifactsDir(set.town);
            Directory.CreateDirectory(dir);
            var enc = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(dir, "parcels.svg"), ParcelMap.Svg(set, null, 0.5, "parcels (S1)"), enc);
            var zone = set.data.kerbZone;
            if (zone != null && zone.Length > 2)
            {
                Bounds(zone, out var mn, out var mx);
                File.WriteAllText(Path.Combine(dir, "parcels-village.svg"), ParcelMap.Svg(set, (mn, mx), 1.6, "parcels (S1): the village"), enc);
            }
            var down = set.data.downtownZone;
            if (down != null && down.Length > 2)
            {
                Bounds(down, out var mn, out var mx);
                File.WriteAllText(Path.Combine(dir, "parcels-downtown.svg"), ParcelMap.Svg(set, (mn - 40, mx + 40), 3.0, "parcels (S1): downtown"), enc);
            }
            var report = Path.Combine(dir, "parcels-report.txt");
            File.WriteAllText(report, Report(set), enc);
            return report;
        }

        static void Bounds(Unity.Mathematics.double2[] p, out Unity.Mathematics.double2 mn, out Unity.Mathematics.double2 mx)
        {
            mn = new Unity.Mathematics.double2(double.MaxValue, double.MaxValue);
            mx = new Unity.Mathematics.double2(double.MinValue, double.MinValue);
            foreach (var q in p) { mn = Unity.Mathematics.math.min(mn, q); mx = Unity.Mathematics.math.max(mx, q); }
        }

        public static string Summary(ParcelSet set)
        {
            int build = set.parcels.Count(p => p.kind == ParcelKind.Building), vac = set.parcels.Count(p => p.kind == ParcelKind.Vacant), pub = set.parcels.Count(p => p.kind == ParcelKind.Public);
            int corner = set.parcels.Count(p => p.corner), alley = set.parcels.Count(p => p.alley), ease = set.parcels.Count(p => p.easement != null);
            return $"{set.town}: {set.blocks.blocks.Count} blocks ({set.blocks.blocks.Count(b => b.rural)} cut at {set.blocks.ruralDepth:F0} m), {set.parcels.Count} parcels " +
                   $"({build} with a building, {vac} vacant, {pub} public), {corner} corner, {alley} alley, {ease} by easement; {set.fixedAreas.Count} fixed areas; " +
                   $"{set.requests.Count} mapped driveway requests; {set.sliversMerged} slivers merged; hash {set.hash.Substring(0, 12)}";
        }

        public static string Report(ParcelSet set)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("sites-S1 parcels report (ParcelBuilder v" + ParcelBuilder.Version + ")");
            sb.AppendLine(Summary(set));
            sb.AppendLine("TownData manifest sha256 " + set.data.manifestSha256);
            sb.AppendLine();
            sb.AppendLine("By type (parcels with a building): count, median area m2, median frontage m, median depth m");
            foreach (var g in set.parcels.Where(p => p.kind == ParcelKind.Building).GroupBy(p => p.type).OrderBy(g => g.Key))
            {
                var areas = g.Select(p => p.area).OrderBy(x => x).ToList();
                var fronts = g.Select(p => p.frontage.Where(f => !IsAlley(set, f.faceId)).Sum(f => (double)f.Length)).OrderBy(x => x).ToList();
                var depths = g.Select(p => (double)p.depth).OrderBy(x => x).ToList();
                sb.AppendLine(string.Format(ci, "  {0,-12} {1,4}  {2,8:F0}  {3,6:F1}  {4,6:F1}", g.Key, g.Count(), areas[areas.Count / 2], fronts[fronts.Count / 2], depths[depths.Count / 2]));
            }
            sb.AppendLine();
            sb.AppendLine("Blocks: id, loop, area m2, rural, faces, parcels");
            foreach (var b in set.blocks.blocks)
                sb.AppendLine(string.Format(ci, "  {0,3} loop {1,3} {2,10:F0} {3} faces {4,3} parcels {5,3}", b.id, b.loop, b.area, b.rural ? "rural" : "     ", b.faces.Length, set.parcels.Count(p => p.block == b.id)));
            sb.AppendLine();
            sb.AppendLine("Fixed areas: id, osm, kind, area m2, served by, parcel");
            foreach (var f in set.fixedAreas)
                sb.AppendLine(string.Format(ci, "  {0,3} {1,-18} {2,-11} {3,8:F0}  {4,-30} parcel {5}", f.id, f.osm, f.kind, f.area,
                    f.servedBy.Length == 0 ? "(none)" : string.Join(", ", f.servedBy.Select(i => set.data.buildings[i].osm)), f.parcel));
            sb.AppendLine();
            sb.AppendLine("Mapped driveway requests: id, way, face, s0-s1, kind, surface, parcel");
            for (int i = 0; i < set.requests.Count; i++)
            {
                var r = set.requests[i];
                sb.AppendLine(string.Format(ci, "  {0,3} {1,-18} face {2,4} {3,7:F1}-{4,7:F1} {5,-11} {6,-8} parcel {7}", r.id, r.osmWay, r.faceId, r.s0, r.s1, r.kind, r.surface, set.requestParcel[i]));
            }
            sb.AppendLine();
            sb.AppendLine("Parcels: id, block, kind, type, area m2, depth m, buildings, frontage (face s0-s1), flags");
            foreach (var p in set.parcels)
            {
                var b = string.Join(" ", p.buildings.Select(i => set.data.buildings[i].osm));
                var f = string.Join(" ", p.frontage.Select(x => string.Format(ci, "{0}:{1:F1}-{2:F1}", x.faceId, x.s0, x.s1)));
                var flags = (p.corner ? "corner " : "") + (p.alley ? "alley " : "") + (p.easement != null ? "easement(face " + p.easementFace + ") " : "") +
                            (p.fixedAreas.Length > 0 ? "fixed " + string.Join(",", p.fixedAreas) + " " : "") + (p.landuse != null ? p.landuse : "");
                sb.AppendLine(string.Format(ci, "  {0,4} b{1,-3} {2,-8} {3,-11} {4,9:F0} {5,6:F1}  {6}  [{7}] {8}", p.id, p.block, p.kind, p.kind == ParcelKind.Building ? p.type.ToString() : "-", p.area, p.depth, b, f, flags));
            }
            sb.AppendLine();
            sb.AppendLine("Notes:");
            foreach (var n in set.blocks.notes.Concat(set.notes)) sb.AppendLine("  " + n);
            return sb.ToString();
        }

        static bool IsAlley(ParcelSet set, int face)
        {
            foreach (var f in set.blocks.fronts) if (f.faceId == face) return f.alley;
            return false;
        }

        [MenuItem("Backroads/Sites/Build parcels (S1)")]
        static void Menu()
        {
            foreach (var town in Towns())
            {
                var set = BuildTown(town);
                Debug.Log("[BR] sites: " + Summary(set) + " -> " + WriteArtifacts(set));
            }
        }
    }

    /// <summary>Batchmode entry points of the site program (PLAN §1.9; tools\ci\sites-bake.ps1 comes with sites-S5).</summary>
    public static class SitesBakeCli
    {
        /// <summary>Builds every town's site plans twice, checks the hashes match and nothing is wrong, writes the report, maps and
        /// door specs (also to tools/world/sites/(town)/doors.json for lane A). Exit 1 on failure.</summary>
        public static void Plans()
        {
            int code = 0;
            try
            {
                foreach (var town in SiteParcels.Towns())
                {
                    var parcels = SiteParcels.BuildTown(town);
                    var a = SitePlans.BuildTown(town, parcels);
                    var b = SitePlans.BuildTown(town, SiteParcels.BuildTown(town));
                    Debug.Log("[BR] sites: " + SitePlans.Summary(a));
                    if (a.hash != b.hash) { Debug.LogError($"[BR] sites: {town}: two plan builds differ ({a.hash} vs {b.hash})"); code = 1; }
                    foreach (var p in a.problems.Take(20)) Debug.LogError("[BR] sites: " + p);
                    if (a.problems.Count > 0) code = 1;
                    Debug.Log("[BR] sites: report " + SitePlans.WriteArtifacts(a));
                    var doors = SitePlans.DoorsPath(town);
                    Directory.CreateDirectory(Path.GetDirectoryName(doors));
                    File.WriteAllText(doors, SitePlans.DoorsJson(a), new UTF8Encoding(false));
                    Debug.Log("[BR] sites: door specs " + doors);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[BR] sites: Plans FAILED: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>Builds every town's parcels twice, checks the hashes match, writes the report and maps. Exit 1 on failure.</summary>
        public static void Parcels()
        {
            int code = 0;
            try
            {
                foreach (var town in SiteParcels.Towns())
                {
                    var data = SiteTownData.Load(RepoPaths.Root, town);
                    var blocks = SiteParcels.BuildBlocks(data);
                    Directory.CreateDirectory(SiteParcels.ArtifactsDir(town));
                    File.WriteAllText(Path.Combine(SiteParcels.ArtifactsDir(town), "blocks.json"), SiteParcels.BlocksJson(blocks), new UTF8Encoding(false));
                    var a = ParcelBuilder.Build(data, blocks);
                    var b = ParcelBuilder.Build(SiteTownData.Load(RepoPaths.Root, town), SiteParcels.BuildBlocks(data));
                    Debug.Log("[BR] sites: " + SiteParcels.Summary(a));
                    if (a.hash != b.hash) { Debug.LogError($"[BR] sites: {town}: two builds differ ({a.hash} vs {b.hash})"); code = 1; }
                    Debug.Log("[BR] sites: report " + SiteParcels.WriteArtifacts(a));
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[BR] sites: Parcels FAILED: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
