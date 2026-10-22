// The views of the autotest m3.shots.sites (card sites-S5 step 4), chosen from the baked plans.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// card sites-S5 step 4: twelve fixed cameras over the town's sites for the autotest m3.shots.sites, written with its
    /// parameters to StreamingAssets/Autotest/m3.shots.sites.json by SitesBakeCli.Bake. The card's list first (two houses,
    /// downtown front and rear, a retail lot, fast food, a supermarket, fuel, a warehouse, a farm, a quarry or mill, a
    /// harbour); the kinds the town lacks are named in the file's notes and their places taken by other sites of the town
    /// (more retail lots, the school, the mapped lots and yards). Each camera looks at the middle of the site's surfaces from
    /// the street side, a little to one side, far enough to frame them (20-90 m), at the graded height.
    /// </summary>
    public static class SiteShots
    {
        public const int Views = 12;

        public sealed class View
        {
            public string id, name;
            public double3 pos, target;
            public float fov = 55f;
        }

        public static (List<View> views, List<string> missing) Choose(SiteBakeResult r)
        {
            var plans = r.plans.plans;
            var used = new HashSet<int>();
            var views = new List<View>();
            var missing = new List<string>();
            SitePlan Pick(IEnumerable<SitePlan> from) => from.Where(p => !used.Contains(p.parcel)).OrderByDescending(Score).ThenBy(p => p.parcel).FirstOrDefault();
            double Score(SitePlan p) => p.lots.Sum(l => l.stalls.Count) * 10 + p.surfaces.Sum(s => Poly2.Area(s.outer)) * 0.01;
            void Want(string id, string what, IEnumerable<SitePlan> from, bool rear = false, string lacking = null)
            {
                var p = Pick(from);
                if (p == null) { missing.Add(lacking ?? what); return; }
                used.Add(p.parcel);
                views.Add(Frame(r, p, id, $"{what}, parcel {p.parcel} ({p.type}, {p.source})", rear));
            }
            var houses = plans.Where(p => p.type == BusinessType.House && p.surfaces.Any(s => s.use == SurfaceUse.Drive)).ToList();
            Want("house", "a house with its drive", houses.Where(p => p.surfaces.Any(s => s.surface != SiteSurface.Gravel)));
            Want("house-gravel", "a house on a gravel drive", houses.Where(p => p.surfaces.Any(s => s.use == SurfaceUse.Drive && s.surface == SiteSurface.Gravel)));
            var downtownShops = plans.Where(p => p.downtown && (p.type == BusinessType.Retail || p.type == BusinessType.Restaurant || p.type == BusinessType.Office)).ToList();
            Want("downtown-front", "downtown from the street", downtownShops.Count > 0 ? downtownShops : plans.Where(p => p.downtown).ToList());
            Want("downtown-rear", "downtown from the rear", plans.Where(p => p.downtown && p.lots.Count > 0), rear: true);
            Want("retail", "a retail lot", plans.Where(p => p.type == BusinessType.Retail && !p.downtown && p.lots.Count > 0));
            Want("fast-food", "fast food", plans.Where(p => p.type == BusinessType.FastFood), lacking: "fast food");
            Want("supermarket", "a supermarket", plans.Where(p => p.type == BusinessType.Supermarket), lacking: "supermarket");
            Want("fuel", "the fuel station", plans.Where(p => p.type == BusinessType.Fuel));
            Want("warehouse", "a warehouse or works", plans.Where(p => p.type == BusinessType.Warehouse || p.type == BusinessType.Industrial), lacking: "warehouse or works");
            Want("farm", "a farm", plans.Where(p => p.type == BusinessType.Farm), lacking: "farm");
            Want("quarry-mill", "a quarry or mill", plans.Where(p => p.type == BusinessType.Quarry || p.type == BusinessType.Mill), lacking: "quarry or mill");
            Want("harbor", "a harbour or marina", plans.Where(p => p.type == BusinessType.Harbor), lacking: "harbour or marina");
            // the town's other sites in the places of what it lacks
            var fill = new List<(string id, string what, IEnumerable<SitePlan> from)>
            {
                ("retail-2", "another retail lot", plans.Where(p => p.type == BusinessType.Retail && p.lots.Count > 0)),
                ("school", "the school", plans.Where(p => p.type == BusinessType.School || p.type == BusinessType.Civic || p.type == BusinessType.Church)),
                ("mapped-lot", "a mapped lot", plans.Where(p => p.source == PlanSource.Mapped && p.lots.Count > 0)),
                ("yard", "a mapped yard", plans.Where(p => p.surfaces.Any(s => s.use == SurfaceUse.Yard))),
                ("retail-3", "a third retail site", plans.Where(p => p.type == BusinessType.Retail)),
                ("house-2", "another house", houses),
                ("house-3", "a third house", houses),
            };
            foreach (var (id, what, from) in fill)
            {
                if (views.Count >= Views) break;
                var p = Pick(from);
                if (p == null) continue;
                used.Add(p.parcel);
                views.Add(Frame(r, p, id, $"{what}, parcel {p.parcel} ({p.type}, {p.source})", false));
            }
            return (views, missing);
        }

        static View Frame(SiteBakeResult r, SitePlan plan, string id, string name, bool rear)
        {
            var ctx = r.plans.ctx;
            var parcel = ctx.parcels.parcels[plan.parcel];
            var pb = new PlanBuilder(ctx, parcel, ctx.parcels.regions[plan.parcel]);
            var g = r.meshes.grades[plan.parcel];
            var pts = plan.surfaces.SelectMany(s => s.outer).Concat(pb.main != null ? pb.main.poly : Array.Empty<double2>()).ToList();
            if (pts.Count == 0) pts.AddRange(parcel.outer);
            double2 mn = pts[0], mx = pts[0];
            foreach (var q in pts) { mn = math.min(mn, q); mx = math.max(mx, q); }
            var c = (mn + mx) * 0.5;
            double radius = math.distance(mn, mx) * 0.5;
            double dist = Math.Max(20, Math.Min(90, radius * 1.6));
            var v = pb.frame.v;   // into the site from the street
            var u = pb.frame.u;
            var from = rear ? c + v * (dist * 0.8) + u * (dist * 0.35) : c - v * (dist * 0.8) + u * (dist * 0.35);
            double ty = g.H(c);
            double cy = Math.Max(r.ground.At(from) + 3, ty + dist * 0.55);
            return new View { id = id, name = name, target = new double3(c.x, ty, c.y), pos = new double3(from.x, cy, from.y) };
        }

        /// <summary>The autotest's parameters (StreamingAssets/Autotest/m3.shots.sites.json): LF only, a hashed tree.</summary>
        public static string Json(SiteBakeResult r)
        {
            var ci = CultureInfo.InvariantCulture;
            var (views, missing) = Choose(r);
            string V(double3 p) => string.Format(ci, "[{0:F2}, {1:F2}, {2:F2}]", p.x, p.y, p.z);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"id\": \"m3.shots.sites\",\n  \"kind\": \"shots-sites\",\n  \"seconds\": 0,\n  \"screenshot\": false,\n  \"childTimeoutSeconds\": 600,\n");
            sb.Append("  \"width\": 1600,\n  \"height\": 900,\n  \"settleFrames\": 6,\n  \"loadTimeoutSeconds\": 240,\n  \"hours\": [12, 20],\n");
            sb.Append("  \"chunks\": \"").Append(SiteChunks.TownResourceName(r.town)).Append("\",\n");
            sb.Append("  \"minShots\": ").Append(views.Count * 2).Append(",\n");
            sb.Append("  \"views\": [\n");
            for (int i = 0; i < views.Count; i++)
            {
                var w = views[i];
                sb.Append("    {\"id\": \"").Append(w.id).Append("\", \"name\": \"").Append(w.name).Append("\", \"pos\": ").Append(V(w.pos)).Append(", \"target\": ").Append(V(w.target))
                  .Append(", \"fov\": ").Append(w.fov.ToString("F0", ci)).Append('}').Append(i + 1 < views.Count ? ",\n" : "\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"notes\": \"sites-S5 (card step 4): ").Append(views.Count).Append(" fixed cameras over the town's sites at noon and at dusk, 1600x900, chosen and framed by SitesBakeCli.Bake from the plans");
            if (missing.Count > 0) sb.Append("; the town has no ").Append(string.Join(", ", missing)).Append(": other sites of the town stand in");
            sb.Append(".\"\n}\n");
            return sb.ToString();
        }
    }
}
