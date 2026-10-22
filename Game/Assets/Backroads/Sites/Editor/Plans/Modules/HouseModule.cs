using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Houses (design §4.2): a driveway on one side of the house (79 %) or centred to a pad in front (21 %), to the
    /// house's mapped garage when it has one (behind 64 %, beside 28 %, in front 8 % in the towns measured), else to a
    /// parking pad beside the house; a walk from the sidewalk to the front door on streets with walks; a mailbox at the
    /// road where there is no walk; the front door on the street wall, a side door on the drive side, the garage door
    /// facing the drive. Mapped driveways are kept (source Mapped).
    /// </summary>
    public static class HouseModule
    {
        public static void Plan(PlanBuilder pb)
        {
            var b = pb.main;
            var box = pb.Box(b);
            var face = pb.frame.face;
            double cu = 0.5 * (box.minU + box.maxU);
            // The garage: an accessory building of 15-100 m² on the parcel, the nearest to the street.
            SiteBuilding garage = pb.buildings.Skip(1).Where(x => x.Accessory && x.area >= 15 && x.area <= 100)
                .OrderBy(x => pb.Box(x).minV).ThenBy(x => x.index).FirstOrDefault();
            var rural = face == null || !face.kerbed;

            // Doors first (lots and walks aim at them).
            var main = pb.Door(b, DoorKind.Main, -pb.frame.v, 0.95);

            if (face == null)
            {
                // No frontage: the parcel reaches the street by an easement across others; a pad by the house.
                Pad(pb, b, box, cu, garage, rural);
                pb.plan.notes.Add("no frontage: reached by an easement; no driveway of its own");
                pb.plan.source = PlanSource.Rules;
                Props(pb, b, box, cu, rural, double.NaN);
                return;
            }

            var (lo, hi) = pb.Range(face.faceId);
            var surface = Surface(pb, rural);
            double width = rural ? 3.0 : 3.6;

            // A mapped driveway of this parcel (S1) is kept.
            int mi = -1;
            for (int i = 0; i < pb.ctx.parcels.requests.Count; i++)
                if (pb.ctx.parcels.requestParcel[i] == pb.parcel.id && pb.ctx.parcels.requests[i].faceId == face.faceId) { mi = i; break; }
            if (mi >= 0)
            {
                var r = pb.ctx.parcels.requests[mi];
                var way = pb.ctx.data.service.FirstOrDefault(w => w.osm == r.osmWay);
                var t = pb.Entrance(face, 0.5 * (r.s0 + r.s1), r.s1 - r.s0, r.kind, r.surface, Math.Min(lo, r.s0), Math.Max(hi, r.s1), true, r.osmWay);
                if (t.ok && way != null)
                {
                    var line = new List<double2> { t.Centre };
                    // The mapped way's points on the parcel, in order away from the street.
                    var pts = way.pts.Where(p => Clip.Contains(pb.region, p)).ToList();
                    if (pts.Count > 0 && math.distance(pts[0], t.Centre) > math.distance(pts[pts.Count - 1], t.Centre)) pts.Reverse();
                    line.AddRange(pts);
                    if (line.Count < 2) line.Add(t.Centre + pb.frame.v * 6);
                    pb.Surface(PlanBuilder.Strip(line, Math.Max(width, t.Width * 0.9)), r.surface, SurfaceUse.Drive);
                    var end = line[line.Count - 1];
                    if (garage != null) GarageDoor(pb, garage, end);
                    pb.Door(b, DoorKind.Service, end - b.centroid, 0.9, end, 0, main.wallEdge);
                    pb.plan.source = PlanSource.Mapped;
                    pb.plan.osmCredit = new[] { r.osmWay };
                    Walk(pb, b, box, main, face);
                    Props(pb, b, box, pb.frame.ToUV(t.Centre).x, rural, width);
                    return;
                }
            }

            // Which side: towards the garage when there is one; else by the measured shares among the sides that fit.
            double vProbe = Math.Max(0.5, Math.Min(box.minV + 1, box.maxV - 0.5));
            var (pu0, pu1) = LotHelper.SpanAt(pb, vProbe, cu);
            double gapL = box.minU - pu0, gapR = pu1 - box.maxU;
            double need = width + 1.0;
            var opts = new List<(string key, double weight)>();
            double sideShare = pb.ctx.profile?.Shares("house.driveways.side").FirstOrDefault(x => x.key == "side").weight ?? 0.787;
            if (sideShare <= 0) sideShare = 0.787;
            if (gapL >= need) opts.Add(("left", sideShare * (gapL >= gapR ? 0.6 : 0.4)));
            if (gapR >= need) opts.Add(("right", sideShare * (gapR > gapL ? 0.6 : 0.4)));
            if (box.minV >= 7.5) opts.Add(("centre", 1 - sideShare));
            string side;
            if (garage != null)
            {
                var g = pb.Box(garage);
                double gc = 0.5 * (g.minU + g.maxU);
                side = g.minV < box.minV - 3 && box.minV >= 7.5 ? "centre" : gc < cu ? "left" : "right";
                if (side == "left" && gapL < need && gapR >= need) side = "right";
                if (side == "right" && gapR < need && gapL >= need) side = "left";
            }
            else if (opts.Count == 0) side = box.minV >= 4 ? "centre" : "none";
            else side = pb.Rng("drive-side").Pick(opts);

            if (side == "none")
            {
                pb.plan.notes.Add("no room for a driveway: parks on the street");
                pb.plan.source = PlanSource.Rules;
                Walk(pb, b, box, main, face);
                Props(pb, b, box, cu, rural, double.NaN);
                return;
            }

            double uDrive = side == "left" ? Math.Max(pu0 + width * 0.5 + 0.3, box.minU - 0.6 - width * 0.5)
                          : side == "right" ? Math.Min(pu1 - width * 0.5 - 0.3, box.maxU + 0.6 + width * 0.5)
                          : cu + (pb.Rng("drive-centre").Value() - 0.5) * Math.Max(0, box.maxU - box.minU - 6);
            var th = pb.Entrance(face, pb.SAt(uDrive), width, DrivewayKind.Residential, surface, lo, hi);
            if (!th.ok)
            {
                pb.plan.notes.Add("driveway refused by the street: parks on the street");
                pb.plan.source = PlanSource.Rules;
                Walk(pb, b, box, main, face);
                Props(pb, b, box, cu, rural, double.NaN);
                return;
            }
            double ud = pb.frame.ToUV(th.Centre).x;
            var start = th.Centre;
            var path = new List<double2> { start };
            double2 endPt;
            if (garage != null)
            {
                var g = pb.Box(garage);
                double gc = 0.5 * (g.minU + g.maxU);
                double vStop = Math.Max(1.0, g.minV - 0.2);
                if (side == "centre" || (gc - ud) * (gc - ud) < 4)
                {
                    path.Add(pb.frame.FromUV(ud, vStop));
                    endPt = path[path.Count - 1];
                }
                else
                {
                    // Up the side, then across to the garage's door wall (an apron in front of it).
                    double vTurn = Math.Max(1.0, g.minV - 6.0);
                    path.Add(pb.frame.FromUV(ud, Math.Max(vTurn, 1.0)));
                    endPt = pb.frame.FromUV(gc, Math.Max(vTurn, 1.0));
                }
                pb.Surface(PlanBuilder.Strip(path, width), surface, SurfaceUse.Drive);
                // Apron 6 m deep in front of the garage (concrete in town, as the drive elsewhere).
                pb.Surface(pb.Zone(g.minU, g.maxU, Math.Max(0.5, g.minV - 6), g.minV), rural ? surface : SiteSurface.Concrete, SurfaceUse.Apron);
                if (!(side == "centre" || (gc - ud) * (gc - ud) < 4))
                    pb.Surface(PlanBuilder.Strip(new[] { path[path.Count - 1], endPt }, width), surface, SurfaceUse.Drive);
                GarageDoor(pb, garage, pb.frame.FromUV(gc, g.minV - 3));
            }
            else if (side == "centre")
            {
                double v0 = Math.Max(0.5, box.minV - 6.5);
                path.Add(pb.frame.FromUV(ud, v0));
                pb.Surface(PlanBuilder.Strip(path, width), surface, SurfaceUse.Drive);
                pb.Surface(pb.Zone(ud - 3, ud + 3, v0, box.minV - 0.6), surface, SurfaceUse.Drive);
                endPt = pb.frame.FromUV(ud, box.minV - 3);
            }
            else
            {
                // Up the side to a pad beside the house (double width where it fits).
                double vEnd = Math.Min(box.maxV, box.minV + 7.0);
                path.Add(pb.frame.FromUV(ud, vEnd));
                pb.Surface(PlanBuilder.Strip(path, width), surface, SurfaceUse.Drive);
                double padOut = side == "left" ? Math.Max(pu0 + 0.3, ud - 3.0) : ud - width * 0.5;
                double padIn = side == "left" ? ud + width * 0.5 : Math.Min(pu1 - 0.3, ud + 3.0);
                pb.Surface(pb.Zone(Math.Min(padOut, padIn), Math.Max(padOut, padIn), box.minV, vEnd), surface, SurfaceUse.Drive);
                endPt = pb.frame.FromUV(ud, box.minV + 3.5);
            }
            pb.Door(b, DoorKind.Service, endPt - b.centroid, 0.9, endPt, 0, main.wallEdge);
            Walk(pb, b, box, main, face);
            pb.plan.source = PlanSource.Rules;
            Props(pb, b, box, ud, rural, width);
        }

        static SiteSurface Surface(PlanBuilder pb, bool rural)
        {
            var r = pb.Rng("drive-surface").Value();
            // Mapped driveways (research 08 surfaces.driveways): unpaved 34 %, asphalt 32 %, paved 19 %, gravel 10 %, concrete 3 %;
            // the village (kerbed streets) leans paved, the countryside unpaved.
            if (!rural) return r < 0.50 ? SiteSurface.Asphalt : r < 0.72 ? SiteSurface.Concrete : r < 0.92 ? SiteSurface.Gravel : SiteSurface.Pavers;
            return r < 0.45 ? SiteSurface.Gravel : r < 0.65 ? SiteSurface.Dirt : r < 0.92 ? SiteSurface.Asphalt : SiteSurface.Concrete;
        }

        static void GarageDoor(PlanBuilder pb, SiteBuilding g, double2 toward)
        {
            var gb = pb.Box(g);
            double w = gb.maxU - gb.minU >= 6.0 ? 4.9 : 2.7;
            pb.Door(g, DoorKind.Garage, toward - g.centroid, w, toward, w + 0.6);
        }

        static void Pad(PlanBuilder pb, SiteBuilding b, (double minU, double maxU, double minV, double maxV) box, double cu, SiteBuilding garage, bool rural)
        {
            var pad = Clip.Intersect(pb.Free(0.6), Clip.Region(Poly2.Ccw(new[]
            {
                b.centroid + (pb.frame.u * 6 - pb.frame.v * 12), b.centroid + (pb.frame.u * 12 - pb.frame.v * 12),
                b.centroid + (pb.frame.u * 12 + pb.frame.v * 0), b.centroid + (pb.frame.u * 6 + pb.frame.v * 0),
            })));
            if (Clip.Area(pad) > 12) pb.Surface(pad, SiteSurface.Gravel, SurfaceUse.Drive);
            if (garage != null) GarageDoor(pb, garage, b.centroid);
        }

        static void Walk(PlanBuilder pb, SiteBuilding b, (double minU, double maxU, double minV, double maxV) box, DoorSpec main, FrontLine face)
        {
            if (face == null || !face.kerbed) return;
            var (door, outward) = PlanBuilder.DoorAt(b, main);
            var q = pb.frame.ToUV(door);
            if (q.y < 1.5) return;
            var path = new[] { pb.frame.FromUV(q.x, 0), pb.frame.FromUV(q.x, Math.Max(0.5, q.y - 0.4)) };
            pb.Surface(PlanBuilder.Strip(path, 1.2), SiteSurface.Concrete, SurfaceUse.Walk);
        }

        static void Props(PlanBuilder pb, SiteBuilding b, (double minU, double maxU, double minV, double maxV) box, double uDrive, bool rural, double width)
        {
            var rng = pb.Rng("house-props");
            if (rural && !double.IsNaN(width))
                pb.Prop("mailbox", pb.frame.FromUV(uDrive + width * 0.5 + 1.0, 1.2), -pb.frame.v, "design §7: mailbox at the road on rural streets");
            if (!double.IsNaN(width))
                pb.Prop("trash.bin", pb.frame.FromUV(uDrive + (uDrive < 0.5 * (box.minU + box.maxU) ? -1 : 1) * (width * 0.5 + 0.8), box.minV + 2.0), -pb.frame.v, "design §7: bin by the garage or pad");
            if (rng.Value() < 0.3 && box.maxV + 6 < pb.ParcelBox.maxV)
                pb.Prop("washing.line", pb.frame.FromUV(0.5 * (box.minU + box.maxU), box.maxV + 5.0), pb.frame.u, "design §7: washing line in the back yard (30 %)");
        }
    }
}
