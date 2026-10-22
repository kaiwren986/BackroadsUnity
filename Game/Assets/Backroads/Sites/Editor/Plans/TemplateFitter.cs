using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Real templates (design §6): for each template of the site's kind and each mirror, scale the template building onto
    /// ours (along and across the street, each within 0.75-1.33), carry its lot over, reject it when more than 5 % falls off
    /// the parcel, it overlaps a building or its entrances miss the frontage, else pack the lot at true size (stalls are
    /// never scaled) and score capacity and scale; the best at or above Threshold wins. The choice is made in the plan's
    /// first pass and kept (later passes only move entrances by R's answers).
    /// </summary>
    public static class TemplateFitter
    {
        public const double Threshold = 2.0;

        public sealed class Choice
        {
            public SiteTemplate template;
            public bool mirror;
            public double su, sv, score;
            public int capacity;
        }

        public static string[] KindsFor(BusinessType t, bool downtown)
        {
            if (downtown) return new[] { "downtown_rear", "downtown_side", "downtown_alley" };
            switch (t)
            {
                case BusinessType.Retail:
                case BusinessType.Office:
                case BusinessType.Restaurant:
                case BusinessType.Motel:
                case BusinessType.CarRepair:
                case BusinessType.Apartments:
                    return new[] { "retail_front", "retail_side", "retail_rear" };
                case BusinessType.FastFood: return new[] { "fast_food" };
                case BusinessType.Civic:
                case BusinessType.Church:
                case BusinessType.School: return new[] { "civic" };
                case BusinessType.Warehouse:
                case BusinessType.Industrial: return new[] { "warehouse" };
                default: return Array.Empty<string>();
            }
        }

        static (double minU, double maxU, double minV, double maxV) Box(double2[] p)
        {
            double a = double.MaxValue, b = double.MinValue, c = double.MaxValue, d = double.MinValue;
            foreach (var q in p) { a = Math.Min(a, q.x); b = Math.Max(b, q.x); c = Math.Min(c, q.y); d = Math.Max(d, q.y); }
            return (a, b, c, d);
        }

        /// <summary>Template point to world for our building's box.</summary>
        static double2 Map(PlanBuilder pb, double2 p, (double minU, double maxU, double minV, double maxV) tb, (double minU, double maxU, double minV, double maxV) ob,
            double su, double sv, bool mirror)
        {
            double tcu = 0.5 * (tb.minU + tb.maxU), ocu = 0.5 * (ob.minU + ob.maxU);
            double u = ocu + (p.x - tcu) * su * (mirror ? -1 : 1);
            double v = ob.minV + (p.y - tb.minV) * sv;
            return pb.frame.FromUV(u, v);
        }

        static double2[] MapAll(PlanBuilder pb, double2[] pts, (double, double, double, double) tb, (double, double, double, double) ob, double su, double sv, bool mirror)
        {
            var r = pts.Select(p => Map(pb, p, tb, ob, su, sv, mirror)).ToArray();
            return mirror ? r.Reverse().ToArray() : r;
        }

        /// <summary>Tries every template of the kinds; records each rejection; returns the best choice at or above Threshold.</summary>
        public static Choice Find(PlanBuilder pb, string[] kinds, List<string> rejections, double targetStalls)
        {
            if (pb.ctx.templates == null || pb.main == null || pb.frame.face == null || kinds.Length == 0) return null;
            var ob = pb.Box(pb.main);
            var buildings = new Paths64();
            foreach (var x in pb.buildings) buildings.AddRange(Clip.Region(x.poly));
            buildings = Clip.Union(buildings);
            var (lo, hi) = pb.Range(pb.frame.face.faceId);
            Choice best = null;
            foreach (var t in pb.ctx.templates.all.Where(t => kinds.Contains(t.type)).OrderBy(t => t.id, StringComparer.Ordinal))
                foreach (var mirror in new[] { false, true })
                {
                    string tag = t.id + (mirror ? " mirrored" : "");
                    if (t.building == null) { rejections.Add($"{tag}: no building"); break; }
                    var lotT = t.lot ?? (t.lots.Count > 0 ? t.lots[0] : null);
                    if (lotT == null) { rejections.Add($"{tag}: no lot"); break; }
                    var tb = Box(t.building);
                    double su = (ob.maxU - ob.minU) / Math.Max(1e-6, tb.maxU - tb.minU), sv = (ob.maxV - ob.minV) / Math.Max(1e-6, tb.maxV - tb.minV);
                    if (su < 0.75 || su > 1.33 || sv < 0.75 || sv > 1.33) { rejections.Add($"{tag}: scale {su:F2} x {sv:F2} outside 0.75-1.33"); break; }
                    var lot = Clip.Region(MapAll(pb, lotT, tb, ob, su, sv, mirror));
                    double la = Clip.Area(lot);
                    double off = Clip.Area(Clip.Difference(lot, pb.region));
                    if (la < 50 || off > 0.05 * la) { rejections.Add($"{tag}: {100 * off / Math.Max(la, 1e-6):F0} % of the lot falls off the parcel"); continue; }
                    double over = Clip.Area(Clip.Intersect(lot, buildings));
                    if (over > 1) { rejections.Add($"{tag}: the lot overlaps a building by {over:F0} m²"); continue; }
                    // Entrances on the street (v near 0 in the template), mapped onto our frontage.
                    var ents = new List<LotEntrance>();
                    bool miss = false;
                    foreach (var e in t.entrances.Where(e => Math.Abs(e.v) < 3))
                    {
                        var w = Map(pb, new double2(e.u, 0), tb, ob, su, sv, mirror);
                        double s = StreetFrame.SOn(pb.frame.face, w);
                        if (s < lo - 3 || s > hi + 3) { miss = true; break; }
                        var c = pb.frame.face.BackAt(Math.Max(lo, Math.Min(hi, s)), out var n);
                        var hit = Inside(lot, c, n);
                        if (hit == null) { miss = true; break; }
                        var side = Poly2.Perp(n) * 3.65;
                        ents.Add(new LotEntrance { a = hit.Value - side, b = hit.Value + side, flow = e.oneway ? EntranceFlow.EntryOnly : EntranceFlow.TwoWay });
                    }
                    if (miss) { rejections.Add($"{tag}: an entrance falls off the parcel's frontage"); continue; }
                    if (ents.Count == 0) { rejections.Add($"{tag}: no entrance from the street"); continue; }
                    var piece = Clip.Pieces(Clip.Intersect(lot, pb.Free(1.0))).FirstOrDefault();
                    if (piece == null) { rejections.Add($"{tag}: nothing left of the lot"); continue; }
                    var input = new LotInput { lot = Clip.Pts(piece[0]), frontageDir = pb.frame.u, toStreet = -pb.frame.v, seed = pb.seed, targets = new LotTargets { kerbed = pb.kerbZone } };
                    input.entrances.AddRange(ents);
                    input.building = new LotBuilding { footprint = pb.main.poly };
                    var lay = ParkingPacker.Pack(input, pb.ctx.dims);
                    if (lay.stalls.Count < 3) { rejections.Add($"{tag}: the fitted lot holds {lay.stalls.Count} stalls"); continue; }
                    double score = Math.Min(1.0, lay.stalls.Count / Math.Max(1, targetStalls)) + (1 - Math.Abs(Math.Log(su))) + (1 - Math.Abs(Math.Log(sv))) - (mirror ? 0.05 : 0);
                    if (score < Threshold) { rejections.Add($"{tag}: score {score:F2} below {Threshold:F1}"); continue; }
                    if (best == null || score > best.score + 1e-9) best = new Choice { template = t, mirror = mirror, su = su, sv = sv, score = score, capacity = lay.stalls.Count };
                }
            return best;
        }

        static double2? Inside(Paths64 lot, double2 c, double2 n)
        {
            for (double d = 0; d <= 60; d += 0.25)
            {
                var p = c + n * d;
                if (Clip.Contains(lot, p)) return p;
            }
            return null;
        }

        /// <summary>Applies a choice: the lot, its entrances as driveway requests, the drives, the packed stalls.</summary>
        public static bool Apply(PlanBuilder pb, Choice ch)
        {
            var t = ch.template;
            var ob = pb.Box(pb.main);
            var tb = Box(t.building);
            var lotT = t.lot ?? t.lots[0];
            var lot = Clip.Intersect(Clip.Region(MapAll(pb, lotT, tb, ob, ch.su, ch.sv, ch.mirror)), pb.region);
            var face = pb.frame.face;
            var (lo, hi) = pb.Range(face.faceId);
            var surface = LotHelper.LotSurface(pb, "template-surface");
            var ents = new List<LotEntrance>();
            foreach (var e in t.entrances.Where(e => Math.Abs(e.v) < 3))
            {
                var w = Map(pb, new double2(e.u, 0), tb, ob, ch.su, ch.sv, ch.mirror);
                double s = Math.Max(lo, Math.Min(hi, StreetFrame.SOn(face, w)));
                var th = pb.Entrance(face, s, e.oneway ? 4.5 : 7.3, DrivewayKind.Commercial, surface, lo, hi);
                if (!th.ok) continue;
                var en = LotHelper.EntranceOnLot(pb, lot, th, e.oneway ? 4.5 : 7.3, e.oneway ? EntranceFlow.EntryOnly : EntranceFlow.TwoWay);
                ents.Add(en);
            }
            if (ents.Count == 0) return false;
            var mainDoor = pb.Door(pb.main, DoorKind.Main, Poly2.Centroid(Clip.Pts(Clip.Pieces(lot)[0][0])) - pb.main.centroid, 1.8);
            pb.Door(pb.main, DoorKind.Service, pb.frame.v, 1.0, null, 0, mainDoor.wallEdge);
            var lay = LotHelper.Pack(pb, lot, ents, new LotTargets { kerbed = pb.kerbZone && surface != SiteSurface.Gravel, paved = surface != SiteSurface.Gravel }, 3, surface, "template lot " + t.id);
            foreach (var en in ents)
                foreach (var d in pb.plan.driveways)
                    if (d.id == en.requestId && pb.ctx.fronts.TryGetValue(d.faceId, out var df))
                    {
                        var start = df.BackAt(0.5 * (d.s0 + d.s1), out _);
                        var c = (en.a + en.b) * 0.5;
                        if (math.distance(start, c) > 0.5) pb.Surface(PlanBuilder.Strip(new[] { start, c }, 7.3), surface, SurfaceUse.Drive);
                    }
            if (lay == null) return false;
            pb.plan.source = PlanSource.Template;
            pb.plan.templateId = t.id;
            pb.plan.mirrored = ch.mirror;
            pb.plan.scale = new double2(ch.su, ch.sv);
            pb.plan.osmCredit = pb.plan.osmCredit.Concat(t.osm).Distinct().ToArray();
            return true;
        }
    }
}
