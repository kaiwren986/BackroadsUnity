using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>Where a lot sits relative to its building in the street frame (research 08 §3.1).</summary>
    public enum LotPosition : byte { Front = 0, Side = 1, Rear = 2 }

    /// <summary>Lot zones, trimming, entrances and packing shared by the business modules.</summary>
    public static class LotHelper
    {
        /// <summary>When set, every packed lot is also drawn there as SVG (debugging).</summary>
        public static string DebugDir;

        /// <summary>The parcel's u-extent along the line v = vv of the frame (the span holding uRef, else the widest).</summary>
        public static (double u0, double u1) SpanAt(PlanBuilder pb, double vv, double uRef)
        {
            var a = pb.frame.FromUV(-5000, vv);
            var b = pb.frame.FromUV(5000, vv);
            var us = new List<double>();
            var ring = pb.parcel.outer;
            for (int i = 0; i < ring.Length; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Length];
                var r = b - a;
                var s = q - p;
                double den = Poly2.Cross(r, s);
                if (Math.Abs(den) < 1e-12) continue;
                double t = Poly2.Cross(p - a, s) / den, w = Poly2.Cross(p - a, r) / den;
                if (w < 0 || w >= 1) continue;
                us.Add(-5000 + t * 10000);
            }
            us.Sort();
            double best0 = double.NaN, best1 = double.NaN;
            for (int k = 0; k + 1 < us.Count; k += 2)
            {
                if (uRef >= us[k] && uRef <= us[k + 1]) return (us[k], us[k + 1]);
                if (double.IsNaN(best0) || us[k + 1] - us[k] > best1 - best0) { best0 = us[k]; best1 = us[k + 1]; }
            }
            return double.IsNaN(best0) ? (uRef, uRef) : (best0, best1);
        }

        /// <summary>The part of zone within distance t of the line through o (frame uv) along dir (frame uv, unit), on the side
        /// dir points to, chosen so its area is about target (all of zone when smaller).</summary>
        public static Paths64 TrimToArea(PlanBuilder pb, Paths64 zone, double2 o, double2 dir, double target, double minDist = 0)
        {
            double full = Clip.Area(zone);
            if (full <= target) return zone;
            double lo = minDist, hi = Math.Max(400, minDist + 1);
            Paths64 best = zone;
            for (int it = 0; it < 30; it++)
            {
                double t = 0.5 * (lo + hi);
                var side = new double2(-dir.y, dir.x);
                var rect = new[] { o - side * 2000, o + side * 2000, o + side * 2000 + dir * t, o - side * 2000 + dir * t };
                var cut = Clip.Intersect(zone, Clip.Region(pb.frame.FromUV(rect)));
                double a = Clip.Area(cut);
                if (a >= target) { best = cut; hi = t; } else lo = t;
            }
            return best;
        }

        /// <summary>The lot edge kinds for the packer: the street edge is a walk, a building wall a building, the rest property.</summary>
        public static LotEdgeKind[] EdgeKinds(PlanBuilder pb, double2[] lot)
        {
            var kinds = new LotEdgeKind[lot.Length];
            for (int i = 0; i < lot.Length; i++)
            {
                var m = (lot[i] + lot[(i + 1) % lot.Length]) * 0.5;
                kinds[i] = LotEdgeKind.Property;
                foreach (var f in pb.parcel.frontage)
                    if (pb.ctx.fronts.TryGetValue(f.faceId, out var fl) && DistToLine(fl.back, m) < 0.3) { kinds[i] = LotEdgeKind.Walk; break; }
                if (kinds[i] == LotEdgeKind.Property)
                    foreach (var b in pb.buildings)
                        if (Poly2.BoundaryDistance(b.poly, m, out _) < 1.6) { kinds[i] = LotEdgeKind.Building; break; }
            }
            return kinds;
        }

        static double DistToLine(double2[] line, double2 p)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < line.Length; i++) best = Math.Min(best, Poly2.SegDist(p, line[i], line[i + 1]));
            return best;
        }

        /// <summary>Where a straight drive from the throat (along the throat's inward normal, up to 30 m) first meets the lot;
        /// else the lot edge nearest the throat (the caller draws the drive to it): the lot entrance.</summary>
        public static LotEntrance EntranceOnLot(PlanBuilder pb, Paths64 lot, Throat t, double width, EntranceFlow flow = EntranceFlow.TwoWay)
        {
            var c = t.Centre;
            var dir = math.lengthsq(t.inward) > 0 ? t.inward : pb.frame.v;
            for (double d = 0; d <= 30; d += 0.25)
            {
                var p = c + dir * d;
                if (!Clip.Contains(lot, p)) continue;
                var e = EntryNear(lot, p, width);
                e.flow = flow; e.street = t.face != null ? t.face.street : StreetClass.Street; e.requestId = t.request;
                return e;
            }
            var n = EntryNear(lot, c, width);
            n.flow = flow; n.street = t.face != null ? t.face.street : StreetClass.Street; n.requestId = t.request;
            return n;
        }

        /// <summary>A throat of the given width on the lot's outer edge at the point nearest `from`, along that edge.</summary>
        public static LotEntrance EntryNear(Paths64 lot, double2 from, double width)
        {
            var pieces = Clip.Pieces(lot);
            double best = double.MaxValue;
            double2 at = from, along = new double2(1, 0);
            foreach (var piece in pieces)
            {
                var r = Clip.Pts(piece[0]);
                for (int i = 0; i < r.Length; i++)
                {
                    var a = r[i];
                    var b = r[(i + 1) % r.Length];
                    var ab = b - a;
                    double l2 = math.dot(ab, ab);
                    if (l2 < 1e-9) continue;
                    double tt = math.clamp(math.dot(from - a, ab) / l2, 0, 1);
                    var q = a + ab * tt;
                    double d = math.distance(q, from);
                    if (d < best - 1e-9)
                    {
                        best = d;
                        along = ab / math.sqrt(l2);
                        // Keep the throat on the edge: slide it inside the segment where the edge is long enough.
                        double L = math.sqrt(l2), half = Math.Min(width * 0.5, L * 0.5);
                        double s = math.clamp(tt * L, half, L - half);
                        at = a + along * s;
                    }
                }
            }
            return new LotEntrance { a = at - along * (width * 0.5), b = at + along * (width * 0.5) };
        }

        /// <summary>The thickness of a region's largest piece (the short side of its minimum-area rectangle).</summary>
        public static double Thickness(Paths64 region)
        {
            var pieces = Clip.Pieces(region);
            if (pieces.Count == 0) return 0;
            var r = Poly2.MinAreaRect(Clip.Pts(pieces[0][0]));
            return 2 * r.halfShort;
        }

        /// <summary>A single-loaded parking module and the lot's 0.5 m margins.</summary>
        public const double MinLotThickness = 5.5 + 7.3 + 1.0;

        /// <summary>Packs a lot (the largest piece of region) and adds its surface and layout to the plan. Null when fewer
        /// than minStalls fit.</summary>
        public static LotLayout Pack(PlanBuilder pb, Paths64 region, List<LotEntrance> entrances, LotTargets targets, int minStalls,
            SiteSurface surface, string why, double2 preferredAisle = default)
        {
            var pieces = Clip.Pieces(Clip.Intersect(region, pb.Open));
            if (pieces.Count == 0) return null;
            var piece = pieces[0];
            var outer = Clip.Pts(piece[0]);
            if (Poly2.Area(outer) < 60) return null;
            var input = new LotInput
            {
                lot = outer, edgeKinds = EdgeKinds(pb, outer), frontageDir = pb.frame.u, toStreet = -pb.frame.v, seed = pb.seed,
                targets = targets, preferredAisle = preferredAisle,
            };
            input.entrances.AddRange(entrances);
            if (pb.main != null)
            {
                input.building = new LotBuilding { footprint = pb.main.poly };
                var door = pb.plan.doors.FirstOrDefault(d => d.building == pb.main.osm && d.kind == DoorKind.Main);
                if (door.building != null) { input.building.hasDoor = true; input.building.door = PlanBuilder.DoorAt(pb.main, door).pos; }
            }
            for (int i = 1; i < piece.Count; i++) input.noPark.Add(Clip.Pts(piece[i]));   // holes stay clear
            // Entrances must sit on the lot's edge: any that does not is moved to the nearest edge point.
            var lotPiece = new Paths64 { piece[0] };
            for (int i = 0; i < input.entrances.Count; i++)
            {
                var e = input.entrances[i];
                var mid = e.Mid;
                if (Poly2.BoundaryDistance(outer, mid, out _) <= 0.6) continue;
                var n = EntryNear(lotPiece, mid, Math.Max(3.0, e.Width));
                n.flow = e.flow; n.street = e.street; n.requestId = e.requestId; n.truck = e.truck;
                input.entrances[i] = n;
            }
            var layout = ParkingPacker.Pack(input, pb.ctx.dims);
            if (DebugDir != null)
                System.IO.File.WriteAllText(System.IO.Path.Combine(DebugDir, $"parcel-{pb.parcel.id}-{why.Replace(' ', '_').Replace('/', '_')}.svg"),
                    LotLayoutSvg.Render(layout, pb.main?.poly, $"parcel {pb.parcel.id} {why}: {layout.stalls.Count} stalls; {string.Join("; ", layout.report.notes)}"));
            if (layout.stalls.Count < minStalls)
            {
                pb.plan.notes.Add($"{why}: lot of {Poly2.Area(outer):F0} m² holds {layout.stalls.Count} stalls (< {minStalls}): dropped");
                return null;
            }
            pb.Surface(new Paths64 { piece[0] }, surface, SurfaceUse.Lot, surface == SiteSurface.Asphalt ? 0.3f : 0f);
            pb.plan.lots.Add(layout);
            return layout;
        }

        /// <summary>A paved surface for lots by the town's measured shares (asphalt 76 %), always asphalt in the kerb zone.</summary>
        public static SiteSurface LotSurface(PlanBuilder pb, string draw)
        {
            if (pb.kerbZone) return SiteSurface.Asphalt;
            var r = pb.Rng(draw).Value();
            return r < 0.80 ? SiteSurface.Asphalt : r < 0.84 ? SiteSurface.Concrete : SiteSurface.Gravel;
        }
    }

    /// <summary>
    /// Mapped lots and yards (card S3 step 1, "Mapped"): the parcel's fixed areas keep their outlines exactly; a parking lot
    /// gets its stalls (along its mapped aisles when it has any), a yard its surface; entrances are the mapped driveways
    /// that reach it, else one on the parcel's frontage.
    /// </summary>
    public static class MappedModule
    {
        public static bool Plan(PlanBuilder pb)
        {
            if (pb.parcel.fixedAreas.Length == 0) return false;
            bool any = false;
            var credit = new List<string>();
            foreach (var fid in pb.parcel.fixedAreas)
            {
                var f = pb.ctx.parcels.fixedAreas[fid];
                var area = Clip.Intersect(Clip.Region(f.outer, f.holes), pb.region);
                if (Clip.Area(area) < 20) continue;
                credit.Add(f.osm);
                any = true;
                var entrances = new List<LotEntrance>();
                var drives = new List<(double2[] line, double width, SiteSurface surface)>();
                // Mapped driveway requests of this parcel (S1) that lead into this area.
                for (int i = 0; i < pb.ctx.parcels.requests.Count; i++)
                {
                    if (pb.ctx.parcels.requestParcel[i] != pb.parcel.id) continue;
                    var r = pb.ctx.parcels.requests[i];
                    if (!pb.ctx.fronts.TryGetValue(r.faceId, out var face)) continue;
                    var way = pb.ctx.data.service.FirstOrDefault(w => w.osm == r.osmWay);
                    if (way == null || !way.pts.Any(p => Clip.Contains(Clip.Inflate(area, 3), p))) continue;
                    var (lo, hi) = pb.Range(r.faceId);
                    var t = pb.Entrance(face, 0.5 * (r.s0 + r.s1), r.s1 - r.s0, r.kind, r.surface, Math.Min(lo, r.s0), Math.Max(hi, r.s1), true, r.osmWay);
                    if (!t.ok) continue;
                    credit.Add(r.osmWay);
                    var line = new List<double2> { t.Centre };
                    line.AddRange(way.pts.Where(p => Clip.Contains(pb.region, p)));
                    var lotEntry = EntryAlong(area, line, Math.Max(4.0, t.Width)) ?? LotHelper.EntryNear(area, t.Centre, Math.Max(4.0, t.Width));
                    entrances.Add(lotEntry);
                    drives.Add((line.ToArray(), Math.Max(4.0, t.Width), r.surface));
                }
                if (entrances.Count == 0 && pb.frame.face != null)
                {
                    // One entrance on the frontage nearest the area.
                    var c = Poly2.Centroid(f.outer);
                    double s = StreetFrame.SOn(pb.frame.face, c);
                    var (lo, hi) = pb.Range(pb.frame.face.faceId);
                    var t = pb.Entrance(pb.frame.face, s, 7.3, DrivewayKind.Commercial, SiteSurface.Asphalt, lo, hi);
                    if (t.ok)
                    {
                        var e = LotHelper.EntranceOnLot(pb, area, t, 7.3);
                        entrances.Add(e);
                        drives.Add((new[] { t.Centre, (e.a + e.b) * 0.5 }, 7.3, SiteSurface.Asphalt));
                    }
                }
                if (entrances.Count == 0) pb.plan.notes.Add($"{f.osm}: no way in (the parcel has no frontage)");
                switch (f.kind)
                {
                    case FixedKind.Parking:
                    {
                        // The longest mapped aisle inside sets the aisle direction.
                        double2 aisle = 0;
                        double best = 0;
                        foreach (var w in pb.ctx.data.service.Where(w => w.Service == "parking_aisle"))
                            for (int k = 0; k + 1 < w.pts.Length; k++)
                            {
                                var m = (w.pts[k] + w.pts[k + 1]) * 0.5;
                                double len = math.distance(w.pts[k], w.pts[k + 1]);
                                if (len > best && Poly2.Contains(f.outer, m)) { best = len; aisle = w.pts[k + 1] - w.pts[k]; credit.Add(w.osm); }
                            }
                        var surf = SurfaceOfTag(pb, f.osm, LotHelper.LotSurface(pb, "mapped-lot"));
                        var lay = LotHelper.Pack(pb, area, entrances,
                            new LotTargets { paved = surf != SiteSurface.Gravel && surf != SiteSurface.Dirt, kerbed = pb.kerbZone && surf != SiteSurface.Gravel }, 1, surf,
                            "mapped lot " + f.osm, aisle);
                        if (lay == null) pb.Surface(area, surf, SurfaceUse.Lot, 0.3f);
                        break;
                    }
                    case FixedKind.Fuel:
                        pb.Surface(area, SiteSurface.Concrete, SurfaceUse.Forecourt);
                        break;
                    default:
                        pb.Surface(area, SiteSurface.Gravel, SurfaceUse.Yard);
                        break;
                }
                foreach (var (line, width, surface) in drives) pb.Surface(PlanBuilder.Strip(line, width), surface, SurfaceUse.Drive);
            }
            if (!any) return false;
            pb.plan.source = PlanSource.Mapped;
            pb.plan.osmCredit = pb.plan.osmCredit.Concat(credit).Distinct().ToArray();
            return true;
        }

        static SiteSurface SurfaceOfTag(PlanBuilder pb, string osm, SiteSurface fallback)
        {
            foreach (var a in pb.ctx.data.parking)
                if (a.osm == osm)
                    switch (a.Tag("surface"))
                    {
                        case "asphalt": case "paved": return SiteSurface.Asphalt;
                        case "concrete": return SiteSurface.Concrete;
                        case "gravel": case "fine_gravel": case "compacted": case "unpaved": return SiteSurface.Gravel;
                        case "dirt": case "ground": case "grass": return SiteSurface.Dirt;
                    }
            return fallback;
        }

        /// <summary>The lot entrance where a polyline first enters the area.</summary>
        static LotEntrance EntryAlong(Paths64 area, List<double2> line, double width)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var a = line[i];
                var b = line[i + 1];
                double L = math.distance(a, b);
                for (double t = 0; t <= L; t += 0.25)
                {
                    var p = a + (b - a) * (t / Math.Max(L, 1e-9));
                    if (!Clip.Contains(area, p)) continue;
                    var d = math.normalizesafe(b - a);
                    var side = Poly2.Perp(d) * (width * 0.5);
                    return new LotEntrance { a = p - side, b = p + side };
                }
            }
            return null;
        }
    }
}
