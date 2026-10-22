// The site bake (card sites-S4): plans -> grading, meshes, plates, props -> chunks and assets.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Backroads.World.Bake;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Backroads.Sites.Editor
{
    /// <summary>A grade or cut/fill outside design §9's limits (reported, card S4 "all grades within the limits or reported").</summary>
    public struct GradeIssue
    {
        public int parcel;
        public string what;
        public double value, limit;
        public double2 at;
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "parcel {0,4}: {1} {2:F3} (limit {3:F3}) at ({4:F1}, {5:F1})", parcel, what, value, limit, at.x, at.y);
    }

    public sealed class SiteBakeResult
    {
        public string town;
        public SitePlanSet plans;
        public SiteGround ground;
        public SiteMeshSettings settings;
        public readonly SiteMeshes meshes = new SiteMeshes();
        public readonly SitePropStats propStats = new SitePropStats();
        public readonly List<SitePropSet.Placement> placements = new List<SitePropSet.Placement>();
        public readonly List<SitePropSet.FenceRun> fences = new List<SitePropSet.FenceRun>();
        public readonly List<SitePropSet.GateSpan> gates = new List<SitePropSet.GateSpan>();
        public readonly List<SitePropSet.ModelBox> models = new List<SitePropSet.ModelBox>();
        public SitePropSet props;
        public RoadChunkSet chunks;
        public byte[] raw, packed;
        public string platesHash, hash;
        public readonly List<GradeIssue> grades = new List<GradeIssue>();
        /// <summary>Per baked cut: the largest gap between the lot surface and the apron's lot edge (m).</summary>
        public readonly List<(int request, int parcel, double gap)> apronSeams = new List<(int, int, double)>();
        public readonly List<string> problems = new List<string>();
        /// <summary>Drive stubs added where a baked cut led onto nothing of its plan.</summary>
        public readonly List<string> stubs = new List<string>();
        /// <summary>Baked cuts no surface of their plan reaches (their apron leads onto grass).</summary>
        public readonly List<string> unreached = new List<string>();
        public double ms;
    }

    /// <summary>
    /// card sites-S4: a town's site bake. From the S3 plans: the grading of every plan (SiteGrading), the surfaces, kerbs,
    /// wheel stops and markings in lane R's mesh format and their plates (SiteMesher), the props (SiteProps); then the 64 m
    /// chunks with lane R's baker (the same grid as the roads and the terrain) and the checks of the card's "done when":
    /// every plan drawn, every apron meets its lot within 5 mm, grades within design §9's limits or listed, props inside
    /// their parcels and clear, LOD0 triangles within SITE_TRI_BUDGET. Outputs: `BrSiteChunks_(town)` (RoadChunkSet + gzip
    /// data, LFS) and `BrSiteProps_(town)` in Sites/Baked/Resources, `tools/world/sites/(town)/driveways.json` (the driveway
    /// requests, for lane R's road meshes and lane W's apron plates), the report and maps in artifacts/sites/(town).
    ///   menu: Backroads > Sites > Bake surfaces and props (S4)
    ///   Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Sites.Editor.SitesBakeCli.Bake [-verifyHash]
    /// </summary>
    public static class SiteBake
    {
        public const int Version = 1;
        /// <summary>SITE_TRI_BUDGET (card S4): LOD0 triangles of all site meshes in a town.</summary>
        public const int TriangleBudget = 150000;
        public const double ApronSeamLimit = 0.005;
        public const string BakedDir = "Assets/Backroads/Sites/Baked/Resources";

        public static string ChunksAssetPath(string town) => BakedDir + "/" + SiteChunks.TownResourceName(town) + ".asset";
        public static string ChunksDataPath(string town) => BakedDir + "/" + SiteChunks.TownResourceName(town) + ".sitebake.bytes";
        public static string PropsAssetPath(string town) => BakedDir + "/" + SitePropSet.TownResourceName(town) + ".asset";
        public static string DrivewaysPath(string town) => Path.Combine(RepoPaths.Root, "tools", "world", "sites", town, "driveways.json");

        public static SiteBakeResult Build(string town, SitePlanSet plans = null, SiteGround ground = null, SiteMeshSettings st = null)
        {
            var sw = Stopwatch.StartNew();
            var r = Stage(town, plans ?? SitePlans.BuildTown(town), ground ?? SiteGround.Load(RepoPaths.Root, town), st);
            var ctx = r.plans.ctx;
            r.chunks = RoadChunkBaker.Build(r.meshes.meshes, ctx.data.playHalf, "sites:" + town, r.plans.hash, ctx.data.manifestSha256, out r.raw, out r.packed);
            r.props = ScriptableObject.CreateInstance<SitePropSet>();
            r.props.bakeVersion = Version;
            r.props.source = "sites:" + town;
            r.props.plansHash = r.plans.hash;
            r.props.props = r.placements.ToArray();
            r.props.fences = r.fences.ToArray();
            r.props.gates = r.gates.ToArray();
            r.props.models = r.models.ToArray();
            r.props.contentHash = PropsHash(r.props);
            r.hash = Sha(r.chunks.contentHash + "|" + r.props.contentHash + "|" + r.platesHash);
            if (r.chunks.lod0Triangles > TriangleBudget) r.problems.Add($"{r.chunks.lod0Triangles} LOD0 triangles over SITE_TRI_BUDGET {TriangleBudget}");
            r.ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        /// <summary>The bake before its Unity assets: grading, mesh parts, plates, props and the checks (runs anywhere).</summary>
        public static SiteBakeResult Stage(string town, SitePlanSet plans, SiteGround ground, SiteMeshSettings st = null)
        {
            var r = new SiteBakeResult { town = town, settings = st ?? new SiteMeshSettings(), plans = plans, ground = ground };
            var ctx = plans.ctx;
            foreach (var plan in plans.plans.OrderBy(p => p.parcel)) Stubs(plan, ctx, r);
            foreach (var plan in plans.plans.OrderBy(p => p.parcel))
            {
                var g = SiteGrading.Grade(plan, ctx, ground);
                SiteMesher.BuildPlan(plan, ctx, g, r.meshes, r.settings);
                SiteProps.Build(plan, ctx, g, r.placements, r.fences, r.gates, r.models, r.propStats);
                Check(plan, ctx, g, r);
            }
            r.platesHash = PlatesHash(r.meshes.plates);
            foreach (var u in r.propStats.unknownKeys) r.problems.Add("prop key not in the table: " + u);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in r.meshes.plates) if (!ids.Add(p.id)) { r.problems.Add("duplicate plate id " + p.id); break; }
            return r;
        }

        /// <summary>LOD0 triangles of the mesh parts (what the chunks will hold).</summary>
        public static int Lod0Triangles(SiteMeshes m) => m.meshes.parts.Where(p => (p.lods & 1) != 0).Sum(p => p.Triangles);

        /// <summary>
        /// A drive stub where a baked cut's apron leads onto nothing of its plan (S3 asked for the driveway but drew no drive
        /// to it, or one that stops short): from the apron's lot edge straight into the parcel, far enough to reach the
        /// plan's nearest surface (4 m when it has none; at most 12 m), off buildings and existing surfaces, in the
        /// request's surface. Listed in the report.
        /// </summary>
        static void Stubs(SitePlan plan, PlanContext ctx, SiteBakeResult r)
        {
            var region = ctx.parcels.regions[plan.parcel];
            var seen = new HashSet<(int, float, float)>();
            foreach (var c in plan.cuts)
            {
                if (c == null || c.outcome == CutOutcome.Rejected || !seen.Add((c.faceId, c.s0, c.s1))) continue;
                var m = (c.lotEdgeA + c.lotEdgeB) * 0.5;
                double reach = plan.surfaces.Count == 0 ? double.MaxValue : plan.surfaces.Min(s => Poly2.Contains(s.outer, m) ? 0 : Poly2.BoundaryDistance(s.outer, m, out _));
                if (reach <= 0.3) continue;
                var n = math.normalizesafe(Poly2.Perp(c.lotEdgeB - c.lotEdgeA));
                double depth = reach > 12 ? 4.0 : Math.Min(12.0, reach + 0.5);
                Clipper2Lib.Paths64 Rect(double2 dir) => Clip.Region(new[] { c.lotEdgeA, c.lotEdgeB, c.lotEdgeB + dir * depth, c.lotEdgeA + dir * depth });
                // into the parcel: the side the stub overlaps it most (its edge may lie a little behind R's lot edge)
                if (Clip.Area(Clip.Intersect(Rect(-n), region)) > Clip.Area(Clip.Intersect(Rect(n), region))) n = -n;
                var rect = Rect(n);
                var blocked = new Clipper2Lib.Paths64();
                foreach (var bi in ctx.parcels.parcels[plan.parcel].buildings) if (!ctx.data.buildings[bi].OpenRoof) blocked.AddRange(Clip.Region(ctx.data.buildings[bi].poly));
                foreach (var s in plan.surfaces) blocked.AddRange(Clip.Region(s.outer, s.holes));
                var keep = Clip.Difference(Clip.Intersect(rect, region), Clip.Union(blocked));
                var req = plan.driveways.FirstOrDefault(d => d.id == c.requestId);
                var surface = req.id == c.requestId ? req.surface : SiteSurface.Asphalt;
                int added = 0;
                foreach (var piece in Clip.Pieces(keep))
                {
                    if (Clip.Area(piece) < 1.0) continue;
                    plan.surfaces.Add(new SurfacePoly { outer = Poly2.Ccw(Clip.Pts(piece[0])), holes = piece.Skip(1).Select(Clip.Pts).ToArray(), surface = surface, use = SurfaceUse.Drive });
                    added++;
                }
                r.stubs.Add($"parcel {plan.parcel} ({plan.type}): request {c.requestId}: {(added > 0 ? $"a {depth:F1} m {surface} stub" : "no room for a stub")} (the plan's nearest surface {(reach > 1e6 ? "none" : reach.ToString("F1", CultureInfo.InvariantCulture) + " m")})");
            }
        }

        /// <summary>The card's checks per plan: apron seams (problems over 5 mm), grades and cut/fill (listed).</summary>
        static void Check(SitePlan plan, PlanContext ctx, PlanGrade g, SiteBakeResult r)
        {
            var seen = new HashSet<(int, float, float)>();
            foreach (var c in plan.cuts)
            {
                if (c == null || c.outcome == CutOutcome.Rejected || !seen.Add((c.faceId, c.s0, c.s1))) continue;
                double gap = 0;
                for (int i = 0; i <= 8; i++)
                {
                    double t = i / 8.0;
                    var p = math.lerp(c.lotEdgeA, c.lotEdgeB, t);
                    gap = Math.Max(gap, Math.Abs(g.H(p) - (c.lotEdgeYA + (c.lotEdgeYB - c.lotEdgeYA) * t)));
                }
                r.apronSeams.Add((c.requestId, plan.parcel, gap));
                var m = (c.lotEdgeA + c.lotEdgeB) * 0.5;
                double reach = plan.surfaces.Count == 0 ? double.MaxValue : plan.surfaces.Min(s => Poly2.Contains(s.outer, m) ? 0 : Poly2.BoundaryDistance(s.outer, m, out _));
                if (reach > 0.3)
                    r.unreached.Add($"parcel {plan.parcel} ({plan.type}, {plan.source}): request {c.requestId} {c.outcome}{(c.mergedWith >= 0 ? " with " + c.mergedWith : "")}: nearest surface {(reach > 1e6 ? "none" : reach.ToString("F1", CultureInfo.InvariantCulture) + " m")}; surfaces {string.Join(", ", plan.surfaces.Select(s => s.use + "/" + s.surface))}; notes {string.Join("; ", plan.notes)}");
                if (gap > ApronSeamLimit) r.problems.Add($"parcel {plan.parcel}: request {c.requestId}: the lot is {gap * 1000:F1} mm off the apron edge");
            }
            double cut = 0, fill = 0;
            double2 cutAt = 0, fillAt = 0;
            var worst = new Dictionary<SurfaceUse, (double v, double2 at)>();
            foreach (var s in plan.surfaces)
            {
                foreach (var q in SiteGrading.Grid(s.outer, 1.5, 300))
                {
                    if (s.holes.Any(h => Poly2.Contains(h, q))) continue;
                    double d = g.H(q) - r.ground.At(q);
                    if (-d > cut) { cut = -d; cutAt = q; }
                    if (d > fill) { fill = d; fillAt = q; }
                    double sl = g.Slope(q);
                    if (!worst.TryGetValue(s.use, out var w) || sl > w.v) worst[s.use] = (sl, q);
                }
            }
            foreach (var kv in worst.OrderBy(k => k.Key))
            {
                double lim = SiteGrading.Limit(kv.Key) + 0.005;
                if (kv.Value.v > lim) r.grades.Add(new GradeIssue { parcel = plan.parcel, what = kv.Key + " grade", value = kv.Value.v, limit = SiteGrading.Limit(kv.Key), at = kv.Value.at });
            }
            foreach (var lot in plan.lots)
                foreach (var stall in lot.stalls)
                {
                    if (stall.kind != StallKind.Accessible && stall.kind != StallKind.Van) continue;
                    double worstAcc = 0;
                    var at = Poly2.Centroid(stall.poly);
                    foreach (var q in stall.poly.Concat(new[] { Poly2.Centroid(stall.poly) })) { double sl = g.Slope(q); if (sl > worstAcc) { worstAcc = sl; at = q; } }
                    if (worstAcc > SiteGrading.AccessibleSlope + 0.005)
                        r.grades.Add(new GradeIssue { parcel = plan.parcel, what = "accessible stall grade", value = worstAcc, limit = SiteGrading.AccessibleSlope, at = at });
                }
            if (cut > SiteGrading.CutFillLimit) r.grades.Add(new GradeIssue { parcel = plan.parcel, what = "cut", value = cut, limit = SiteGrading.CutFillLimit, at = cutAt });
            if (fill > SiteGrading.CutFillLimit) r.grades.Add(new GradeIssue { parcel = plan.parcel, what = "fill", value = fill, limit = SiteGrading.CutFillLimit, at = fillAt });
        }

        // ------------------------------------------------------------------------------------------ hashes

        static string Sha(string s)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
        }

        static long Mm(double v) => (long)Math.Round(v * 1000);

        public static string PlatesHash(List<Plate> plates)
        {
            var sb = new StringBuilder();
            foreach (var p in plates)
            {
                sb.Append(p.id).Append('|').Append((int)p.kind).Append('|').Append(p.surface).Append('|').Append(p.priority).Append('|').Append(Mm(p.blend)).Append('|').Append(p.noDeform ? 1 : 0);
                for (int i = 0; i < p.polygon.Length; i++) sb.Append('|').Append(Mm(p.polygon[i].x)).Append(',').Append(Mm(p.polygon[i].y)).Append(',').Append(Mm(p.vertexY[i]));
                sb.Append('\n');
            }
            return Sha(sb.ToString());
        }

        public static string PropsHash(SitePropSet s)
        {
            var sb = new StringBuilder();
            sb.Append(s.formatVersion).Append('|').Append(s.bakeVersion).Append('|').Append(s.plansHash).Append('\n');
            foreach (var p in s.props)
                sb.Append(p.key).Append('|').Append(string.Join(",", p.prefabs ?? Array.Empty<string>())).Append('|').Append(Mm(p.position.x)).Append(',').Append(Mm(p.position.y)).Append(',').Append(Mm(p.position.z))
                  .Append('|').Append((long)Math.Round(p.yaw * 100)).Append('|').Append(p.parcel).Append('\n');
            foreach (var f in s.fences)
            {
                sb.Append("fence|").Append((int)f.kind).Append('|').Append(Mm(f.height)).Append('|').Append(f.parcel);
                foreach (var q in f.line) sb.Append('|').Append(Mm(q.x)).Append(',').Append(Mm(q.y)).Append(',').Append(Mm(q.z));
                sb.Append('\n');
            }
            foreach (var g in s.gates)
                sb.Append("gate|").Append((int)g.kind).Append('|').Append(g.sliding ? 1 : 0).Append('|').Append(Mm(g.a.x)).Append(',').Append(Mm(g.a.z)).Append('|').Append(Mm(g.b.x)).Append(',').Append(Mm(g.b.z)).Append('\n');
            foreach (var m in s.models)
            {
                sb.Append("model|").Append(m.key).Append('|').Append(Mm(m.y)).Append('|').Append(Mm(m.height)).Append('|').Append(m.parcel);
                foreach (var q in m.footprint) sb.Append('|').Append(Mm(q.x)).Append(',').Append(Mm(q.y));
                sb.Append('\n');
            }
            return Sha(sb.ToString());
        }

        // ------------------------------------------------------------------------------------------ outputs

        /// <summary>Saves the chunk data, the chunk set and the prop set in place (GUIDs kept across re-bakes) and the driveway file.</summary>
        public static void Save(SiteBakeResult r)
        {
            EnsureFolder(BakedDir);
            string town = r.town;
            var full = Path.Combine(RepoPaths.Game, ChunksDataPath(town));
            var old = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (old == null || !old.SequenceEqual(r.packed))
            {
                File.WriteAllBytes(full, r.packed);
                AssetDatabase.ImportAsset(ChunksDataPath(town), ImportAssetOptions.ForceSynchronousImport);
            }
            var fresh = UnityEngine.Object.Instantiate(r.chunks);
            fresh.data = AssetDatabase.LoadAssetAtPath<TextAsset>(ChunksDataPath(town));
            SaveAsset(fresh, ChunksAssetPath(town), SiteChunks.TownResourceName(town));
            SaveAsset(UnityEngine.Object.Instantiate(r.props), PropsAssetPath(town), SitePropSet.TownResourceName(town));
            AssetDatabase.SaveAssets();
            var dw = DrivewaysPath(town);
            Directory.CreateDirectory(Path.GetDirectoryName(dw));
            File.WriteAllText(dw, DrivewaysJson(r.plans), new UTF8Encoding(false));
        }

        static void SaveAsset<T>(T fresh, string path, string name) where T : ScriptableObject
        {
            fresh.name = name;
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) AssetDatabase.CreateAsset(fresh, path);
            else
            {
                EditorUtility.CopySerialized(fresh, asset);
                asset.name = name;
                EditorUtility.SetDirty(asset);
                UnityEngine.Object.DestroyImmediate(fresh);
            }
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>The committed chunk set and prop set, if baked.</summary>
        public static (RoadChunkSet chunks, SitePropSet props) LoadBaked(string town) =>
            (AssetDatabase.LoadAssetAtPath<RoadChunkSet>(ChunksAssetPath(town)), AssetDatabase.LoadAssetAtPath<SitePropSet>(PropsAssetPath(town)));

        /// <summary>
        /// The driveway requests of every plan in the contract's terms (overview contract 3), sorted by id, with R's answers
        /// as resolved here: lane R's road meshes (dropped kerbs, apron slabs) and lane W's bake (R's apron plates) call
        /// DrivewayCutter.Resolve on the same list and get the same cuts.
        /// </summary>
        public static string DrivewaysJson(SitePlanSet s)
        {
            var ci = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("R", ci);
            var sb = new StringBuilder();
            sb.Append("{\n  \"format\": \"backroads-driveways\",\n  \"version\": 1,\n");
            sb.Append("  \"note\": \"lane S sites-S4: every site plan's driveway requests (overview contract 3) in the order R's cutter takes them; resolve them all at once with DrivewayCutter.Resolve to get the cuts the sites graded against (answers below).\",\n");
            sb.Append("  \"plansHash\": \"").Append(s.hash).Append("\",\n  \"requests\": [\n");
            for (int i = 0; i < s.requests.Count; i++)
            {
                var r = s.requests[i];
                sb.Append("    {\"id\": ").Append(r.id).Append(", \"faceId\": ").Append(r.faceId).Append(", \"s0\": ").Append(F(r.s0)).Append(", \"s1\": ").Append(F(r.s1))
                  .Append(", \"kind\": \"").Append(r.kind).Append("\", \"flareLength\": ").Append(F(r.flareLength)).Append(", \"returnRadius\": ").Append(F(r.returnRadius))
                  .Append(", \"surface\": \"").Append(r.surface).Append("\", \"mapped\": ").Append(r.mapped ? "true" : "false")
                  .Append(", \"osmWay\": ").Append(r.osmWay == null ? "null" : "\"" + r.osmWay + "\"").Append('}').Append(i + 1 < s.requests.Count ? ",\n" : "\n");
            }
            sb.Append("  ],\n  \"answers\": [\n");
            for (int i = 0; i < s.cuts.Length; i++)
            {
                var c = s.cuts[i];
                sb.Append("    {\"requestId\": ").Append(c.requestId).Append(", \"outcome\": \"").Append(c.outcome).Append("\", \"faceId\": ").Append(c.faceId)
                  .Append(", \"s0\": ").Append(F(c.s0)).Append(", \"s1\": ").Append(F(c.s1)).Append(", \"mergedWith\": ").Append(c.mergedWith).Append('}')
                  .Append(i + 1 < s.cuts.Length ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        public static string Summary(SiteBakeResult r)
        {
            var m = r.meshes;
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} plans baked: {2:F0} m² paved, {3:F0} m² gravel/dirt/grass, {4:F0} m² verge; {5} chunks, LOD0 {6} triangles (budget {7}), LOD1 {8}, collider {9}; " +
                "{10} plates; kerbs {11:F0} m, paint {12:F0} m, {13} symbols, {14} wheel stops, {15} island beds; {16} props ({17} Synty, {18} ours, {19} moved, {20} dropped), " +
                "{21} fence runs, {22} gates, {23} lane A stand-ins; {24} grade notes, {25} problems; {26:F0} ms; hash {27}",
                r.town, r.plans.plans.Count, m.pavedArea, m.unpavedArea, m.vergeArea, r.chunks.chunks.Length, r.chunks.lod0Triangles, TriangleBudget, r.chunks.lod1Triangles,
                r.chunks.colliderTriangles, m.plates.Count, m.kerbM, m.paintM, m.symbols, m.wheelStops, m.islands, r.props.props.Length, r.propStats.synty, r.propStats.own,
                r.propStats.moved.Values.Sum(), r.propStats.dropped.Values.Sum(), r.props.fences.Length, r.props.gates.Length, r.props.models.Length, r.grades.Count,
                r.problems.Count, r.ms, r.hash.Substring(0, 12));
        }

        public static string Report(SiteBakeResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"sites-S4 surfaces and props report (SiteBake v{Version}, SiteMesher, RoadChunkBaker of lane R)");
            sb.AppendLine(Summary(r));
            sb.AppendLine($"plans {r.plans.hash}; chunks {r.chunks.contentHash}; props {r.props.contentHash}; plates {r.platesHash}");
            sb.AppendLine();
            sb.AppendLine("Problems:");
            foreach (var p in r.problems) sb.AppendLine("  " + p);
            if (r.problems.Count == 0) sb.AppendLine("  none");
            sb.AppendLine();
            var seams = r.apronSeams.Select(a => a.gap).DefaultIfEmpty(0).Max();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Apron seams: {0} cuts, the lot meets each apron's lot edge within {1:F2} mm at worst (limit {2} mm)", r.apronSeams.Count, seams * 1000, ApronSeamLimit * 1000));
            sb.AppendLine();
            sb.AppendLine($"Drive stubs added where S3's plan drew no drive to a baked cut ({r.stubs.Count}):");
            foreach (var s in r.stubs) sb.AppendLine("  " + s);
            sb.AppendLine();
            sb.AppendLine($"Aprons no surface of their plan reaches ({r.unreached.Count}):");
            foreach (var u in r.unreached) sb.AppendLine("  " + u);
            sb.AppendLine();
            sb.AppendLine("Grades and cut/fill outside design §9's limits (listed, card S4):");
            foreach (var grp in r.grades.GroupBy(g => g.what).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine($"  {grp.Key}: {grp.Count()} plans, worst {grp.Max(x => x.value):F3} (limit {grp.First().limit:F3})");
            foreach (var g in r.grades) sb.AppendLine("    " + g);
            sb.AppendLine();
            sb.AppendLine("The steepest grades and the anchors within 4 m of them (kind a-b, y, difference to the base, fade length):");
            var steep = r.grades.Where(x => x.what.EndsWith("grade", StringComparison.Ordinal)).OrderByDescending(x => x.value).ToList();
            var shown = steep.Take(8).Concat(steep.Where((x, k) => k >= 8 && k % Math.Max(1, steep.Count / 8) == 0).Take(8));
            foreach (var gi in shown)
            {
                sb.AppendLine("  " + gi);
                if (!r.meshes.grades.TryGetValue(gi.parcel, out var pg)) continue;
                const double e = 0.25;
                double gx = (pg.H(gi.at + new double2(e, 0)) - pg.H(gi.at - new double2(e, 0))) / (2 * e), gz = (pg.H(gi.at + new double2(0, e)) - pg.H(gi.at - new double2(0, e))) / (2 * e);
                double bx = (pg.Base(gi.at + new double2(e, 0)) - pg.Base(gi.at - new double2(e, 0))) / (2 * e), bz = (pg.Base(gi.at + new double2(0, e)) - pg.Base(gi.at - new double2(0, e))) / (2 * e);
                double ox = (pg.BaseOther(gi.at + new double2(e, 0)) - pg.BaseOther(gi.at - new double2(e, 0))) / (2 * e), oz = (pg.BaseOther(gi.at + new double2(0, e)) - pg.BaseOther(gi.at - new double2(0, e))) / (2 * e);
                double sx = (r.ground.Smooth(gi.at + new double2(e, 0)) - r.ground.Smooth(gi.at - new double2(e, 0))) / (2 * e), sz = (r.ground.Smooth(gi.at + new double2(0, e)) - r.ground.Smooth(gi.at - new double2(0, e))) / (2 * e);
                int nr = pg.ramps.Count(rp => (Poly2.Contains(rp.poly, gi.at) ? 0 : Poly2.BoundaryDistance(rp.poly, gi.at, out _)) < PlanGrade.Ramp.Band);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      H {0:F2}, base {1:F2}, ground {2:F2}; gradient of H ({3:F3}, {4:F3}), base ({5:F3}, {6:F3}), without ramps ({7:F3}, {8:F3}), smoothed ground ({9:F3}, {10:F3}); ramps near {11} of {12}",
                    pg.H(gi.at), pg.Base(gi.at), r.ground.At(gi.at), gx, gz, bx, bz, ox, oz, sx, sz, nr, pg.ramps.Count));
                foreach (var an in pg.anchors.Where(an => Poly2.SegDist(gi.at, an.a, an.b) < 6).OrderBy(an => Poly2.SegDist(gi.at, an.a, an.b)).Take(6).Concat(pg.anchors.Where(an => an.kind != AnchorKind.Walk).OrderBy(an => Poly2.SegDist(gi.at, an.a, an.b)).Take(4)))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "      {0} ({1:F1}, {2:F1})-({3:F1}, {4:F1}) y {5:F2}..{6:F2} d {7:F2}..{8:F2} L {9:F1}",
                        an.kind, an.a.x, an.a.y, an.b.x, an.b.y, an.ya, an.yb, an.da, an.db, an.la));
            }
            sb.AppendLine();
            sb.AppendLine("Plans graded: parcel, plane grade (and its limit), clamped, anchors (aprons/doors/walk)");
            foreach (var kv in r.meshes.grades)
            {
                var g = kv.Value;
                int na = g.anchors.Count(a => a.kind == AnchorKind.Apron), nd = g.anchors.Count(a => a.kind == AnchorKind.Door), nw = g.anchors.Count(a => a.kind == AnchorKind.Walk);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,4}: plane {1}, {2}, anchors {3}/{4}/{5}", kv.Key,
                    g.hasPlatform ? $"{Math.Sqrt(g.plane.x * g.plane.x + g.plane.y * g.plane.y) * 100:F1} % (ground {g.planeSlope * 100:F1} %, limit {g.maxSlope * 100:F0} %)" : "none (drives and walks follow the ground)",
                    g.relaxed > 0 ? $"relaxed to {g.relaxed * 100:F1} % (hillside)" : g.clamped ? "clamped" : "free", na, nd, nw));
            }
            sb.AppendLine();
            sb.AppendLine("Props by key: placed (moved, dropped), Synty prefabs or ours");
            foreach (var kv in r.propStats.placed)
            {
                r.propStats.moved.TryGetValue(kv.Key, out var mv);
                r.propStats.dropped.TryGetValue(kv.Key, out var dr);
                var stack = SitePropTable.Pick(kv.Key, 0);
                sb.AppendLine($"  {kv.Key,-18} {kv.Value,4} ({mv} moved, {dr} dropped)  {(stack.Length == 0 ? "ours" : string.Join(" + ", stack.Select(Path.GetFileNameWithoutExtension)))}");
            }
            foreach (var kv in r.propStats.dropped.Where(k => !r.propStats.placed.ContainsKey(k.Key))) sb.AppendLine($"  {kv.Key,-18}    0 (0 moved, {kv.Value} dropped)");
            foreach (var n in r.propStats.notes) sb.AppendLine("    " + n);
            sb.AppendLine();
            sb.AppendLine("Notes:");
            foreach (var n in r.meshes.notes.Distinct()) sb.AppendLine("  " + n);
            return sb.ToString();
        }

        public static string WriteArtifacts(SiteBakeResult r)
        {
            var dir = SiteParcels.ArtifactsDir(r.town);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "surfaces-report.txt");
            File.WriteAllText(path, Report(r), new UTF8Encoding(false));
            return path;
        }

        [MenuItem("Backroads/Sites/Bake surfaces and props (S4)")]
        static void Menu()
        {
            foreach (var town in SiteParcels.Towns())
            {
                var r = Build(town);
                Save(r);
                Debug.Log("[BR] sites: " + Summary(r) + "\n" + WriteArtifacts(r));
            }
        }
    }

    /// <summary>
    /// Lane S's plates for lane W's terrain bake (overview contract 4, ADR-007 stage Sites): registered as source "sites".
    /// Off until lane W re-exports WorldData with them: World.BakeDeterministic compares a fresh bake (registered sources
    /// included) with the committed WorldData hashes, so turning this on changes WorldData (lane W's) in the same merge.
    /// To turn it on: set Enabled to true (or call Register before WorldBakeCli.Export).
    /// </summary>
    public static class SitePlateSource
    {
        public const string Name = "sites";
        public static readonly bool Enabled = false;

        [InitializeOnLoadMethod]
        static void Init()
        {
            if (Enabled) Register();
        }

        public static void Register() => WorldBakePlateSources.Register(Name, town => SiteBake.Build(town).meshes.plates);
    }

    public static partial class SitesBakeCli
    {
        /// <summary>
        /// Bakes every town's site surfaces and props twice, checks the hashes match and nothing is wrong, writes the report and
        /// maps, saves the chunk and prop assets and tools/world/sites/(town)/driveways.json. With -verifyHash: saves nothing and
        /// fails if the committed assets differ from a fresh bake (stale). Exit 1 on failure.
        /// </summary>
        public static void Bake()
        {
            int code = 0;
            bool verify = Environment.GetCommandLineArgs().Any(a => a == "-verifyHash");
            try
            {
                foreach (var town in SiteParcels.Towns())
                {
                    var a = SiteBake.Build(town);
                    var b = SiteBake.Build(town);
                    Debug.Log("[BR] sites: " + SiteBake.Summary(a));
                    if (a.hash != b.hash) { Debug.LogError($"[BR] sites: {town}: two bakes differ ({a.hash} vs {b.hash})"); code = 1; }
                    foreach (var p in a.problems.Take(20)) Debug.LogError("[BR] sites: " + p);
                    if (a.problems.Count > 0) code = 1;
                    Debug.Log("[BR] sites: report " + SiteBake.WriteArtifacts(a));
                    Debug.Log("[BR] sites: maps " + SiteBakeMap.WriteArtifacts(a));
                    if (verify)
                    {
                        var (chunks, props) = SiteBake.LoadBaked(town);
                        if (chunks == null || props == null) { Debug.LogError($"[BR] sites: {town}: not baked"); code = 1; }
                        else if (chunks.contentHash != a.chunks.contentHash || props.contentHash != a.props.contentHash)
                        { Debug.LogError($"[BR] sites: {town}: the committed site bake is stale (chunks {chunks.contentHash} vs {a.chunks.contentHash}, props {props.contentHash} vs {a.props.contentHash})"); code = 1; }
                        else if (!File.Exists(SiteBake.DrivewaysPath(town)) || File.ReadAllText(SiteBake.DrivewaysPath(town)) != SiteBake.DrivewaysJson(a.plans))
                        { Debug.LogError($"[BR] sites: {town}: driveways.json is stale"); code = 1; }
                    }
                    else
                    {
                        SiteBake.Save(a);
                        Debug.Log($"[BR] sites: saved {SiteBake.ChunksAssetPath(town)}, {SiteBake.PropsAssetPath(town)}, {SiteBake.DrivewaysPath(town)}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[BR] sites: Bake FAILED: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
