using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// sites-S3 in the editor: a town's site plans from its parcels (S1), R2's frontage and cutter, the profile and the
    /// templates; the report (artifacts/sites/(town)/plans-report.txt), plan maps (plans*.svg) and the door specs for
    /// lane A's building generator (tools/world/sites/(town)/doors.json, lane A's doors.placeholder.json format).
    ///   menu: Backroads > Sites > Build site plans (S3)
    ///   Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Sites.Editor.SitesBakeCli.Plans
    /// </summary>
    public static class SitePlans
    {
        public static SitePlanSet BuildTown(string town, ParcelSet parcels = null)
        {
            parcels = parcels ?? SiteParcels.BuildTown(town);
            var src = KerbFrontageSource.LoadTown(town);
            var ctx = new PlanContext(town, parcels.data, parcels)
            {
                kerbs = src,
                cutter = src != null ? new DrivewayCutter(src) : null,
                profile = SiteProfile.Load(RepoPaths.Root),
                templates = SiteTemplates.Load(RepoPaths.Root),
            };
            return SitePlanner.Plan(ctx);
        }

        public static string DoorsPath(string town) => Path.Combine(RepoPaths.Root, "tools", "world", "sites", town, "doors.json");

        public static string Summary(SitePlanSet s)
        {
            var bySource = s.plans.GroupBy(p => p.source).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
            int stalls = s.plans.Sum(p => p.lots.Sum(l => l.stalls.Count));
            var outcomes = s.cuts.GroupBy(c => c.outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
            return $"{s.town}: {s.plans.Count} site plans ({string.Join(", ", bySource)}), {s.plans.Sum(p => p.lots.Count)} lots with {stalls} stalls, " +
                   $"{s.requests.Count} driveway requests ({string.Join(", ", outcomes)}), {s.plans.Sum(p => p.doors.Count)} doors, {s.plans.Sum(p => p.props.Count)} props, " +
                   $"{s.plans.Sum(p => p.models.Count)} model requests, {s.problems.Count} problems, {s.passes} passes; hash {s.hash.Substring(0, 12)}";
        }

        public static string Report(SitePlanSet s)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("sites-S3 site plans report (SitePlanner v" + SitePlanner.Version + ", ParkingPacker v" + ParkingPacker.Version + ")");
            sb.AppendLine(Summary(s));
            sb.AppendLine($"TownData manifest {s.ctx.data.manifestSha256}; siteProfile {s.ctx.profile?.sha256}; siteTemplates {s.ctx.templates?.sha256}; parcels {s.ctx.parcels.hash}");
            sb.AppendLine();
            sb.AppendLine("Sources per type (plans: mapped / template / rules):");
            foreach (var g in s.plans.GroupBy(p => (p.downtown ? "Downtown" : p.type.ToString())).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine(string.Format(ci, "  {0,-12} {1,4}: mapped {2,3}, template {3,3}, rules {4,3}", g.Key, g.Count(),
                    g.Count(p => p.source == PlanSource.Mapped), g.Count(p => p.source == PlanSource.Template), g.Count(p => p.source == PlanSource.Rules)));
            sb.AppendLine();
            sb.AppendLine("Templates used:");
            foreach (var p in s.plans.Where(p => p.source == PlanSource.Template))
                sb.AppendLine(string.Format(ci, "  parcel {0,4} {1,-11} {2}{3} scale {4:F2} x {5:F2}", p.parcel, p.type, p.templateId, p.mirrored ? " (mirrored)" : "", p.scale.x, p.scale.y));
            sb.AppendLine();
            sb.AppendLine("Templates rejected (every one tried, with the reason):");
            foreach (var kv in s.rejections.OrderBy(k => k.Key))
            {
                if (kv.Value.Count == 0) continue;
                sb.AppendLine($"  parcel {kv.Key} ({s.ctx.parcels.parcels[kv.Key].type}):");
                foreach (var r in kv.Value) sb.AppendLine("    " + r);
            }
            sb.AppendLine();
            sb.AppendLine("Driveway answers from R2's cutter:");
            foreach (var c in s.cuts.Where(c => c.outcome != CutOutcome.Baked))
                sb.AppendLine(string.Format(ci, "  request {0,6} face {1,4} {2}{3}{4}", c.requestId, c.faceId, c.outcome,
                    c.outcome == CutOutcome.Moved ? string.Format(ci, " by {0:F1} m", c.shift) : "", c.outcome == CutOutcome.Rejected ? ": " + c.reason : c.outcome == CutOutcome.Merged ? " with " + c.mergedWith : ""));
            sb.AppendLine();
            sb.AppendLine("Plans: parcel, type, source, surfaces, lots (stalls), driveways, doors, props, models, notes");
            foreach (var p in s.plans)
            {
                sb.AppendLine(string.Format(ci, "  {0,4} {1,-11}{2} {3,-8} surf {4,2} lots {5} ({6,3}) drives {7} doors {8,2} props {9,2} models {10,2}{11}",
                    p.parcel, p.type, p.downtown ? "*" : " ", p.source, p.surfaces.Count, p.lots.Count, p.lots.Sum(l => l.stalls.Count), p.driveways.Count, p.doors.Count,
                    p.props.Count, p.models.Count, p.notes.Count > 0 ? "  " + string.Join("; ", p.notes) : ""));
            }
            sb.AppendLine();
            sb.AppendLine("Problems:");
            foreach (var x in s.problems) sb.AppendLine("  " + x);
            return sb.ToString().Replace("\r\n", "\n");
        }

        /// <summary>The door specs for lane A (art/buildings/gen: run.ps1 -Doors), sorted like lane A's placeholder file.</summary>
        public static string DoorsJson(SitePlanSet s)
        {
            var ci = CultureInfo.InvariantCulture;
            var doors = s.plans.SelectMany(p => p.doors).ToList();
            doors.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.building, b.building);
                if (c != 0) return c;
                c = a.part.CompareTo(b.part);
                if (c != 0) return c;
                c = a.wallEdge.CompareTo(b.wallEdge);
                if (c != 0) return c;
                c = Math.Round(a.offset, 3).CompareTo(Math.Round(b.offset, 3));
                return c != 0 ? c : string.CompareOrdinal(a.kind.ToString(), b.kind.ToString());
            });
            var sb = new StringBuilder();
            sb.Append("{\n \"format\": \"backroads-doorspecs\",\n \"version\": 1,\n");
            sb.Append(" \"note\": \"DoorSpecs (Backroads.Core.WorldBake.DoorSpec fields) from the site plans, sites-S3 (SitePlanner v").Append(SitePlanner.Version)
              .Append("). wallEdge indexes the building polygon of buildings.json; offset is metres from that vertex to the door centre; sillY null = NaN.\",\n");
            sb.Append(" \"doors\": [\n");
            for (int i = 0; i < doors.Count; i++)
            {
                var d = doors[i];
                sb.Append("  {\"building\": \"").Append(d.building).Append("\", \"part\": ").Append(d.part).Append(", \"kind\": \"").Append(d.kind)
                  .Append("\", \"wallEdge\": ").Append(d.wallEdge).Append(", \"offset\": ").Append(Math.Round(d.offset, 3).ToString("0.###", ci))
                  .Append(", \"width\": ").Append(Math.Round(d.width, 3).ToString("0.###", ci)).Append(", \"sillY\": ")
                  .Append(float.IsNaN(d.sillY) ? "null" : Math.Round(d.sillY, 3).ToString("0.###", ci)).Append('}').Append(i + 1 < doors.Count ? ",\n" : "\n");
            }
            sb.Append(" ]\n}\n");
            return sb.ToString();
        }

        public static string WriteArtifacts(SitePlanSet s)
        {
            var dir = SiteParcels.ArtifactsDir(s.town);
            Directory.CreateDirectory(dir);
            var enc = new UTF8Encoding(false);
            var report = Path.Combine(dir, "plans-report.txt");
            File.WriteAllText(report, Report(s), enc);
            File.WriteAllText(Path.Combine(dir, "doors.json"), DoorsJson(s), enc);
            var zone = s.ctx.data.kerbZone;
            if (zone != null && zone.Length > 2)
            {
                var mn = new double2(double.MaxValue, double.MaxValue);
                var mx = new double2(double.MinValue, double.MinValue);
                foreach (var q in zone) { mn = math.min(mn, q); mx = math.max(mx, q); }
                File.WriteAllText(Path.Combine(dir, "plans-village.svg"), SitePlanMap.Svg(s, (mn, mx), 1.6, "site plans (S3): the village"), enc);
            }
            var down = s.ctx.data.downtownZone;
            if (down != null && down.Length > 2)
            {
                var mn = new double2(double.MaxValue, double.MaxValue);
                var mx = new double2(double.MinValue, double.MinValue);
                foreach (var q in down) { mn = math.min(mn, q); mx = math.max(mx, q); }
                File.WriteAllText(Path.Combine(dir, "plans-downtown.svg"), SitePlanMap.Svg(s, (mn - 40, mx + 40), 3.5, "site plans (S3): downtown"), enc);
            }
            return report;
        }

        [MenuItem("Backroads/Sites/Build site plans (S3)")]
        static void Menu()
        {
            foreach (var town in SiteParcels.Towns())
            {
                var s = BuildTown(town);
                Debug.Log("[BR] sites: " + Summary(s) + " -> " + WriteArtifacts(s));
            }
        }
    }
}
