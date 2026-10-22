// Sites.MatchesProfile (card sites-S5 step 2, design §13): the generated town measured against the study's bands.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>One measure of the generated town against the profile: the generated values, the study's band, inside or not.</summary>
    public sealed class ProfileRow
    {
        public string measure, path;
        public bool share;
        public string shareKey;
        public readonly List<double> values = new List<double>();
        public int studyN;
        public double p10 = double.NaN, p50 = double.NaN, p90 = double.NaN, studyShare = double.NaN;
        public double generated = double.NaN, tolerance;
        /// <summary>Counted: enough generated values (3) and study values (5).</summary>
        public bool counted;
        public bool inside;
        /// <summary>The share of generated values inside p10-p90 (distributions).</summary>
        public double valuesInside = double.NaN;
    }

    /// <summary>
    /// card sites-S5 step 2: the study's measures (research 08, tools/osm/measure.mjs, siteProfile.json) taken on the
    /// generated town: per served type, each lot's position round its building (front, side, rear, in the plan's street
    /// frame), area, lot-to-building area, gap to the building, gap to the road edge, width along the street, entrance
    /// count and surface; the fuel canopy's setback and orientation and the shop's position and gap to it; per house, the
    /// driveway's length and side, the garage's position, the setback and the frontage. A distribution measure is inside
    /// when the generated median lies in the study's p10-p90 band; a share when it lies within 0.15 of the study's (or two
    /// binomial standard errors on few sites). Counted only with at least 3 generated and 5 study values. The town passes
    /// at 70 % of the counted measures inside.
    /// </summary>
    public static class SiteProfileCheck
    {
        public const double Pass = 0.70, ShareTolerance = 0.15;
        public const int MinGenerated = 3, MinStudy = 5;

        public sealed class Result
        {
            public readonly List<ProfileRow> rows = new List<ProfileRow>();
            public int counted, inside;
            public double Score => counted == 0 ? 0 : (double)inside / counted;
            public bool Passed => Score >= Pass;
        }

        static string Served(BusinessType t, bool downtown)
        {
            if (downtown) return "downtown";
            switch (t)
            {
                case BusinessType.Office: return "office";
                case BusinessType.Restaurant: return "restaurant";
                case BusinessType.FastFood: return "fast_food";
                case BusinessType.Supermarket: return "supermarket";
                case BusinessType.Motel: return "motel";
                case BusinessType.CarRepair: return "car_repair";
                case BusinessType.Apartments: return "apartments";
                case BusinessType.Civic: return "civic";
                case BusinessType.Church: return "church";
                case BusinessType.School: return "school";
                case BusinessType.Retail: return "shop";
                default: return null;
            }
        }

        public static Result Run(SitePlanSet set, SiteProfile profile)
        {
            var res = new Result();
            var rows = new Dictionary<string, ProfileRow>(StringComparer.Ordinal);
            ProfileRow Dist(string measure, string path)
            {
                if (!rows.TryGetValue(measure, out var r)) rows[measure] = r = new ProfileRow { measure = measure, path = path };
                return r;
            }
            ProfileRow Share(string measure, string path, string key)
            {
                string k = measure + "=" + key;
                if (!rows.TryGetValue(k, out var r)) rows[k] = r = new ProfileRow { measure = measure + ": " + key, path = path, share = true, shareKey = key };
                return r;
            }
            void ShareOf(string measure, string path, string got, IEnumerable<string> keys)
            {
                foreach (var k in keys) Share(measure, path, k).values.Add(got == k ? 1 : 0);
            }

            var ctx = set.ctx;
            foreach (var plan in set.plans.OrderBy(p => p.parcel))
            {
                var parcel = ctx.parcels.parcels[plan.parcel];
                if (parcel.buildings.Length == 0) continue;
                var pb = new PlanBuilder(ctx, parcel, ctx.parcels.regions[plan.parcel]);
                var main = pb.main;
                var frame = pb.frame;
                var bbox = pb.Box(main);
                var kerb = frame.face?.kerb;
                double RoadGap(IReadOnlyList<double2> poly)
                {
                    if (kerb == null || kerb.Length < 2) return double.NaN;
                    double best = double.MaxValue;
                    foreach (var q in poly)
                        for (int i = 0; i + 1 < kerb.Length; i++) best = Math.Min(best, Poly2.SegDist(q, kerb[i], kerb[i + 1]));
                    return best;
                }

                // ---- lots, per served type
                string served = Served(plan.type, plan.downtown);
                if (served != null)
                    foreach (var lot in plan.lots)
                    {
                        if (lot.lot == null || lot.lot.Length < 3) continue;
                        string path = "parking.byServedType." + served;
                        double area = Poly2.Area(lot.lot);
                        Dist($"{served} lot area (m²)", path + ".area").values.Add(area);
                        if (main.area > 1) Dist($"{served} lot / building area", path + ".lotToBuildingArea").values.Add(area / main.area);
                        Dist($"{served} lot gap to building (m)", path + ".gapToBuilding").values.Add(Overlap(lot.lot, main.poly) ? 0 : Poly2.BoundaryGap(lot.lot, main.poly));
                        double rg = RoadGap(lot.lot);
                        if (!double.IsNaN(rg)) Dist($"{served} lot gap to road edge (m)", path + ".frontGapToRoadEdge").values.Add(rg);
                        var lb = frame.Box(lot.lot);
                        Dist($"{served} lot width along street (m)", path + ".widthAlongStreet").values.Add(lb.maxU - lb.minU);
                        Dist($"{served} lot entrances", path + ".entrances.count").values.Add(lot.entrances.Count);
                        ShareOf($"{served} lot position", path + ".position", Position(bbox, lb), new[] { "front", "side", "rear" });
                        var srf = plan.surfaces.FirstOrDefault(s => s.use == SurfaceUse.Lot && Poly2.Contains(s.outer, Poly2.Centroid(lot.lot)));
                        if (srf != null) ShareOf($"{served} lot surface", path + ".surface", srf.surface == SiteSurface.Asphalt ? "asphalt" : srf.surface == SiteSurface.Concrete ? "concrete" : srf.surface == SiteSurface.Gravel ? "gravel" : "other", new[] { "asphalt" });
                    }

                // ---- fuel
                if (plan.type == BusinessType.Fuel)
                {
                    double2[] canopy = pb.buildings.FirstOrDefault(b => b.OpenRoof)?.poly ?? plan.models.FirstOrDefault(m => m.key == "fuel.canopy").footprint;
                    if (canopy != null && canopy.Length >= 3)
                    {
                        double rg = RoadGap(canopy);
                        if (!double.IsNaN(rg)) Dist("fuel canopy setback from road edge (m)", "fuel.canopy.setbackFromRoadEdge").values.Add(rg);
                        var rect = Poly2.MinAreaRect(canopy);
                        double ang = Math.Abs(Poly2.AxisDeg(rect.axis) - Poly2.AxisDeg(frame.u));
                        ang = Math.Min(ang, 180 - ang);
                        ShareOf("fuel canopy orientation", "fuel.canopy.orientation", ang < 30 ? "parallel" : ang > 60 ? "perpendicular" : "oblique", new[] { "parallel", "perpendicular" });
                        var shop = pb.buildings.FirstOrDefault(b => !b.OpenRoof && !b.Accessory);
                        if (shop != null)
                        {
                            var cb = frame.Box(canopy);
                            var sb = pb.Box(shop);
                            string pos = sb.minV >= cb.maxV - 1 ? "behind" : sb.maxV <= cb.minV + 1 ? "front" : "beside";
                            ShareOf("fuel shop position", "fuel.shop.position", pos, new[] { "behind", "beside" });
                            Dist("fuel shop gap to canopy (m)", "fuel.shop.gapToCanopy").values.Add(Overlap(shop.poly, canopy) ? 0 : Poly2.BoundaryGap(shop.poly, canopy));
                        }
                    }
                }

                // ---- houses
                if (plan.type == BusinessType.House)
                {
                    double rg = RoadGap(main.poly);
                    if (!double.IsNaN(rg)) Dist("house setback from road edge (m)", "house.setbackFromRoadEdge").values.Add(rg);
                    double front = parcel.frontage.Where(f => ctx.fronts.TryGetValue(f.faceId, out var fl) && !fl.alley).Select(f => (double)f.Length).DefaultIfEmpty(0).Max();
                    if (front > 0) Dist("house frontage (m)", "house.frontageSpacing").values.Add(front);
                    foreach (var c in plan.cuts.Where(c => c != null && c.outcome != CutOutcome.Rejected).GroupBy(c => (c.faceId, c.s0, c.s1)).Select(g => g.First()))
                    {
                        var mid = (c.lotEdgeA + c.lotEdgeB) * 0.5;
                        var drive = plan.surfaces.FirstOrDefault(s => s.use == SurfaceUse.Drive && (Poly2.Contains(s.outer, mid) || Poly2.BoundaryDistance(s.outer, mid, out _) < 0.5));
                        if (drive == null) continue;
                        var n = math.normalizesafe(Poly2.Perp(c.lotEdgeB - c.lotEdgeA));
                        if (math.dot(Poly2.Centroid(drive.outer) - mid, n) < 0) n = -n;
                        Dist("house driveway length (m)", "house.driveways.lengthWithHouse").values.Add(drive.outer.Max(v => math.dot(v - mid, n)));
                        double du = frame.ToUV(Poly2.Centroid(drive.outer)).x;
                        ShareOf("house driveway side", "house.driveways.side", du > bbox.minU && du < bbox.maxU ? "centre" : "side", new[] { "side" });
                    }
                    foreach (var g in pb.buildings.Skip(1).Where(b => b.Accessory && b.tags.TryGetValue("building", out var v) && (v == "garage" || v == "garages" || v == "carport")))
                        ShareOf("house garage position", "house.garages.position", Position(bbox, pb.Box(g)), new[] { "rear", "side", "front" });
                }
            }

            foreach (var r in rows.Values.OrderBy(r => r.measure, StringComparer.Ordinal))
            {
                if (r.share)
                {
                    r.studyN = (int)profile.Q(r.path, "n", 0);
                    var sh = profile.Shares(r.path);
                    double total = sh.Sum(x => x.weight);
                    r.studyShare = sh.Where(x => x.key == r.shareKey).Select(x => x.weight).DefaultIfEmpty(0).Sum();
                    // shares among the kinds we can generate (no "across the street" lots): renormalised
                    if (r.path.EndsWith(".position", StringComparison.Ordinal) && r.path.StartsWith("parking", StringComparison.Ordinal))
                    {
                        double own = sh.Where(x => x.key == "front" || x.key == "side" || x.key == "rear").Sum(x => x.weight);
                        if (own > 0) r.studyShare /= own;
                    }
                    if (r.values.Count > 0) r.generated = r.values.Average();
                    double p = double.IsNaN(r.studyShare) ? 0.5 : r.studyShare;
                    r.tolerance = Math.Max(ShareTolerance, r.values.Count > 0 ? 2 * Math.Sqrt(Math.Max(p * (1 - p), 0.01) / r.values.Count) : 1);
                    r.counted = r.values.Count >= MinGenerated && r.studyN >= MinStudy && total > 0;
                    r.inside = r.counted && Math.Abs(r.generated - r.studyShare) <= r.tolerance;
                }
                else
                {
                    r.studyN = (int)profile.Q(r.path, "n", 0);
                    r.p10 = profile.Q(r.path, "p10", double.NaN);
                    r.p50 = profile.Q(r.path, "p50", double.NaN);
                    r.p90 = profile.Q(r.path, "p90", double.NaN);
                    if (r.values.Count > 0)
                    {
                        var v = r.values.OrderBy(x => x).ToList();
                        r.generated = v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[v.Count / 2 - 1] + v[v.Count / 2]);
                        if (!double.IsNaN(r.p10)) r.valuesInside = v.Count(x => x >= r.p10 && x <= r.p90) / (double)v.Count;
                    }
                    r.counted = r.values.Count >= MinGenerated && r.studyN >= MinStudy && !double.IsNaN(r.p10);
                    r.inside = r.counted && r.generated >= r.p10 && r.generated <= r.p90;
                }
                res.rows.Add(r);
                if (r.counted) { res.counted++; if (r.inside) res.inside++; }
            }
            return res;
        }

        /// <summary>Where box b lies round building box a in the street frame: front (towards the street), rear or side.</summary>
        static string Position((double minU, double maxU, double minV, double maxV) a, (double minU, double maxU, double minV, double maxV) b)
        {
            double cu = 0.5 * (b.minU + b.maxU) - 0.5 * (a.minU + a.maxU), cv = 0.5 * (b.minV + b.maxV) - 0.5 * (a.minV + a.maxV);
            double hu = Math.Max(1, 0.5 * (a.maxU - a.minU)), hv = Math.Max(1, 0.5 * (a.maxV - a.minV));
            if (Math.Abs(cu) / hu > Math.Abs(cv) / hv) return "side";
            return cv < 0 ? "front" : "rear";
        }

        static bool Overlap(double2[] a, double2[] b) => Clip.Area(Clip.Intersect(Clip.Region(a), Clip.Region(b))) > 0.01;

        public static string Table(Result r)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(ci, "Sites.MatchesProfile: {0} of {1} counted measures inside the study's band ({2:P0}; pass at {3:P0})", r.inside, r.counted, r.Score, Pass));
            sb.AppendLine("measure | n | generated | study (n) | band | inside | values inside");
            sb.AppendLine("---|---|---|---|---|---|---");
            foreach (var x in r.rows)
            {
                string band = x.share ? string.Format(ci, "{0:F2} ± {1:F2}", x.studyShare, x.tolerance) : string.Format(ci, "{0:0.##}-{1:0.##} (p50 {2:0.##})", x.p10, x.p90, x.p50);
                string gen = x.share ? string.Format(ci, "{0:F2}", x.generated) : string.Format(ci, "{0:0.##}", x.generated);
                string inside = !x.counted ? "n/a" : x.inside ? "yes" : "**no**";
                string vin = double.IsNaN(x.valuesInside) ? "" : string.Format(ci, "{0:P0}", x.valuesInside);
                sb.AppendLine($"{x.measure} | {x.values.Count} | {gen} | {x.studyN} | {band} | {inside} | {vin}");
            }
            return sb.ToString();
        }
    }
}
