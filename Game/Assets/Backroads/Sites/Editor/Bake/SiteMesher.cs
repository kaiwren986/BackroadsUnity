// Site surfaces, kerbs, wheel stops, markings and their plates (card sites-S4 steps 1-3, design §2.2, §9).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Clipper2Lib;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Sites.Editor
{
    public sealed class SiteMeshSettings
    {
        /// <summary>Surfaces are cut into cells on a world grid this size (their triangles carry the graded heights).</summary>
        public double cell = 4.0;
        /// <summary>Mesh parts are at most this big (they go whole into the 64 m chunk of their centroid, like R's).</summary>
        public double part = 32.0;
        /// <summary>Plates sit under the meshes as lane R's do (KerbSettings: asphalt 0.06 m, kerb and walk 0.05 m).</summary>
        public double sinkAsphalt = 0.06, sinkConcrete = 0.05;
        /// <summary>A grass verge round every paved surface (inside the parcel), its ground just under the paving's edge, so a
        /// coarse terrain grid never pokes through near the edge (lane R's flat verge behind the walks).</summary>
        public double vergeWidth = 1.0, vergeDrop = 0.04;
        public double kerbTop = 0.15, kerbSkirt = 0.15, kerbFaceDrop = 0.03;
        /// <summary>Kerbed islands: the planting bed stays at the lot's plate level this far in from the kerb, then rises to 2 cm under the kerb top over the same distance.</summary>
        public double islandInset = 0.5;
        public double paintLift = 0.005, symbolLift = 0.007;
        /// <summary>Cells are split until their triangles lie within this of the grade.</summary>
        public double gradeTolerance = 0.01;
        /// <summary>Concrete slabs: joints every 3.6 m both ways (forecourts, aprons, pads); walks scored every 1.5 m.</summary>
        public double jointSpacing = 3.6, jointWidth = 0.015, scoreSpacing = 1.5;
        public double wheelStopLength = 1.8, wheelStopDepth = 0.15, wheelStopHeight = 0.12;
        public const int PriorityForeignVerge = -1, PriorityVerge = 0, PriorityYard = 5, PriorityPaved = 10, PriorityIsland = 20;
    }

    /// <summary>The site meshes (in lane R's part format) and plates of a town, with what they came to.</summary>
    public sealed class SiteMeshes
    {
        public readonly RoadMeshes meshes = new RoadMeshes();
        public readonly List<Plate> plates = new List<Plate>();
        public readonly SortedDictionary<int, PlanGrade> grades = new SortedDictionary<int, PlanGrade>();
        public readonly List<string> notes = new List<string>();
        public double pavedArea, unpavedArea, vergeArea, kerbM, paintM;
        public int wheelStops, symbols, islands;
    }

    /// <summary>
    /// Card sites-S4 steps 1-3. Per plan: every surface polygon (kerbed islands cut out of the paving) is cut into cells
    /// of a 4 m world grid and triangulated; each triangle takes the plan's graded height (PlanGrade.H) at its corners. Paved
    /// surfaces (asphalt, concrete, pavers) become mesh parts in lane R's format (asphalt and concrete submeshes, UV0 world
    /// planar x/4, z/4, lot asphalt lighter than the road's through the vertex colour, collider on) and, triangle by
    /// triangle, PerVertex plates 0.06 m (asphalt) or 0.05 m (concrete) under them, so lane W's terrain reproduces them
    /// exactly and never pokes through (lane R's convention); gravel, dirt and grass are plates only, the terrain's own
    /// surface, 4 cm under the graded height so they sit just below any paving beside them. A 1 m grass verge round the paving keeps the ground at its edge. Kerbs (150 mm barrier, 100 mm mountable)
    /// follow S2's kerb lines (face, top, back skirt), broken at kerb ramps; kerbed islands get a planting bed rising to the
    /// kerb; wheel stops are our own 1.8 x 0.15 x 0.12 m bars. Paint (stall lines, stop bars, fire-lane kerb paint, hatching,
    /// accessible symbols, arrows, fire-lane stencils, concrete joints) is draped 5 mm over the paving, LOD0 only.
    /// </summary>
    public static class SiteMesher
    {
        static readonly Vector3 Up = new Vector3(0, 1, 0);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly Color32 PaintWhite = RoadMeshBuilder.White, PaintYellow = RoadMeshBuilder.Yellow;
        public static readonly Color32 PaintBlue = new Color32(38, 92, 168, 255), PaintRed = new Color32(168, 34, 30, 255), Joint = new Color32(72, 72, 68, 255);

        public static byte SurfaceId(SiteSurface s) =>
            s == SiteSurface.Asphalt ? (byte)4 : s == SiteSurface.Concrete || s == SiteSurface.Pavers ? (byte)6 : s == SiteSurface.Gravel ? (byte)5 : s == SiteSurface.Dirt ? (byte)1 : (byte)0;

        public static bool Paved(SiteSurface s) => s == SiteSurface.Asphalt || s == SiteSurface.Concrete || s == SiteSurface.Pavers;

        public static void BuildPlan(SitePlan plan, PlanContext ctx, PlanGrade g, SiteMeshes o, SiteMeshSettings st)
        {
            o.grades[plan.parcel] = g;
            var parcelRegion = ctx.parcels.regions[plan.parcel];
            var kerbedIslands = new Paths64();
            foreach (var lot in plan.lots)
                foreach (var isl in lot.islands)
                    if (isl.kerbed && isl.poly != null && isl.poly.Length >= 3) kerbedIslands.AddRange(Clip.Region(isl.poly));
            kerbedIslands = Clip.Union(kerbedIslands);
            var idx = new SurfaceIndex();
            var paved = new Paths64();
            var all = new Paths64();
            byte patch = (byte)(150 + (plan.parcel * 37) % 90);
            for (int si = 0; si < plan.surfaces.Count; si++)
            {
                var s = plan.surfaces[si];
                var region = Clip.Region(s.outer, s.holes);
                all.AddRange(region);
                bool isPaved = Paved(s.surface);
                if (isPaved && kerbedIslands.Count > 0) region = Clip.Difference(region, kerbedIslands);
                if (Clip.Area(region) < 0.05) continue;
                if (isPaved) { paved.AddRange(region); o.pavedArea += Clip.Area(region); }
                else o.unpavedArea += Clip.Area(region);
                double sink = !isPaved ? st.vergeDrop : s.surface == SiteSurface.Asphalt ? st.sinkAsphalt : st.sinkConcrete;
                var mat = s.surface == SiteSurface.Asphalt ? RoadMaterial.Asphalt : RoadMaterial.Concrete;
                var colour = s.surface == SiteSurface.Asphalt
                    ? new Color32((byte)Math.Round(Math.Min(1f, Math.Max(0f, s.wear)) * 255), 0, patch, 0)
                    : new Color32(0, 0, patch, 255);
                var parts = new SortedDictionary<(long, long), RoadMeshPart>();
                int tri = 0;
                foreach (var piece in CellPieces(region, st.cell, o.notes, plan.parcel))
                {
                    var tl = Graded(piece, st.cell, g, st.gradeTolerance);
                    if (tl.Count == 0) { o.notes.Add($"parcel {plan.parcel}: a {s.use} piece did not triangulate"); continue; }
                    var c = Poly2.Centroid(piece);
                    var key = ((long)Math.Floor(c.x / st.part), (long)Math.Floor(c.y / st.part));
                    RoadMeshPart part = null;
                    if (isPaved && !parts.TryGetValue(key, out part))
                        parts[key] = part = new RoadMeshPart { material = mat, collider = true, smooth = 0, lods = 3, arm = SurfacePart };
                    foreach (var tr in tl)
                    {
                        var a = tr[0]; var b = tr[1]; var cc = tr[2];
                        double ha = g.H(a), hb = g.H(b), hc = g.H(cc);
                        if (part != null)
                        {
                            int ia = part.Add(new D3(a.x, ha, a.y), World(a), Vector2.zero, colour, Normal(g, a));
                            int ib = part.Add(new D3(b.x, hb, b.y), World(b), Vector2.zero, colour, Normal(g, b));
                            int ic = part.Add(new D3(cc.x, hc, cc.y), World(cc), Vector2.zero, colour, Normal(g, cc));
                            part.Tri(ia, ib, ic, Up);
                        }
                        o.plates.Add(TriPlate(string.Format(Inv, "site:p{0:D4}:s{1:D2}:{2:D5}", plan.parcel, si, tri++),
                            isPaved ? PlateKind.Lot : PlateKind.Yard, SurfaceId(s.surface), isPaved,
                            isPaved ? SiteMeshSettings.PriorityPaved : SiteMeshSettings.PriorityYard,
                            a, b, cc, ha - sink, hb - sink, hc - sink, ctx, g));
                    }
                }
                foreach (var part in parts.Values)
                {
                    o.meshes.parts.Add(part);
                    idx.Add(part);
                }
            }
            // A grass verge round the paving, off every other surface of the plan and every building, inside the block (never
            // over lane R's walk or street; up to 1 m onto a neighbour's land where the paving meets the property line, where
            // the neighbour's own surfaces win by priority).
            if (paved.Count > 0)
            {
                var pv = Clip.Union(paved);
                var grown = Clip.Inflate(pv, st.vergeWidth, JoinType.Miter);
                var blocked = Clip.Union(all);
                var bb = Clipper.GetBounds(grown);
                foreach (var b in ctx.data.buildings)
                {
                    if (!b.inPlay) continue;
                    bool near = false;
                    foreach (var q in b.poly) if (q.x * Clip.Scale >= bb.left - 5000 && q.x * Clip.Scale <= bb.right + 5000 && q.y * Clip.Scale >= bb.top - 5000 && q.y * Clip.Scale <= bb.bottom + 5000) { near = true; break; }
                    if (near) blocked = Clip.Union(blocked, Clip.Region(b.poly));
                }
                var parcel = ctx.parcels.parcels[plan.parcel];
                var within = parcel.block >= 0 && parcel.block < ctx.parcels.blocks.regions.Count ? ctx.parcels.blocks.regions[parcel.block] : parcelRegion;
                var verge = Clip.Intersect(Clip.Difference(grown, blocked), within);
                o.vergeArea += Clip.Area(verge);
                // on its own land the verge outranks the neighbours' verges; on a neighbour's land it yields to everything there
                foreach (var (part, prio, tag) in new[] { (Clip.Intersect(verge, parcelRegion), SiteMeshSettings.PriorityVerge, "verge"),
                                                          (Clip.Difference(verge, parcelRegion), SiteMeshSettings.PriorityForeignVerge, "vergex") })
                {
                    int tri = 0;
                    foreach (var piece in CellPieces(part, st.cell, o.notes, plan.parcel))
                        foreach (var tr in Graded(piece, st.cell, g, st.gradeTolerance))
                        {
                            var a = tr[0]; var b = tr[1]; var c = tr[2];
                            o.plates.Add(TriPlate(string.Format(Inv, "site:p{0:D4}:{1}:{2:D5}", plan.parcel, tag, tri++), PlateKind.Yard, 0, false,
                                prio, a, b, c, g.H(a) - st.vergeDrop, g.H(b) - st.vergeDrop, g.H(c) - st.vergeDrop, ctx, g));
                        }
                }
            }
            // Lots: island beds, kerbs, wheel stops, markings.
            for (int li = 0; li < plan.lots.Count; li++)
            {
                var lot = plan.lots[li];
                for (int ii = 0; ii < lot.islands.Count; ii++)
                {
                    var isl = lot.islands[ii];
                    if (!isl.kerbed || isl.poly == null || isl.poly.Length < 3) continue;
                    IslandBed(plan, li, ii, isl, g, o, st, ctx);
                }
                foreach (var k in lot.kerbs) Kerb(lot, k, g, o, st);
                foreach (var w in lot.wheelStops) WheelStop(w, g, o, st);
                Markings(lot, g, idx, o, st);
            }
            Joints(plan, g, idx, o, st);
        }

        // ------------------------------------------------------------------------------------------ plates

        static Plate TriPlate(string id, PlateKind kind, byte surface, bool noDeform, int priority, double2 a, double2 b, double2 c,
            double ya, double yb, double yc, PlanContext ctx, PlanGrade g)
        {
            // CCW (the contract), blend 3 x the largest step from the natural ground (1.5-12 m, like lane W's pads)
            if (Poly2.Cross(b - a, c - a) < 0) { (b, c) = (c, b); (yb, yc) = (yc, yb); }
            double step = 0;
            var ground = g.Ground;
            step = Math.Max(step, Math.Abs(ya - ground.At(a)));
            step = Math.Max(step, Math.Abs(yb - ground.At(b)));
            step = Math.Max(step, Math.Abs(yc - ground.At(c)));
            return new Plate
            {
                id = id, kind = kind, polygon = new[] { a, b, c }, surface = surface, heights = PlateHeights.PerVertex,
                vertexY = new[] { (float)ya, (float)yb, (float)yc }, maxSlope = 0, blend = (float)Math.Max(1.5, Math.Min(12, 3 * step)),
                priority = priority, noDeform = noDeform,
            };
        }

        static void IslandBed(SitePlan plan, int li, int ii, Island isl, PlanGrade g, SiteMeshes o, SiteMeshSettings st, PlanContext ctx)
        {
            o.islands++;
            var poly = Poly2.Ccw(isl.poly);
            double rise = st.kerbTop - 0.02 + st.sinkAsphalt;
            double Y(double2 p)
            {
                double d = Poly2.Contains(poly, p) ? Poly2.BoundaryDistance(poly, p, out _) : 0;
                // level with the lot plate for the first cell (so the terrain never rises through the asphalt beside the island), then up to the bed
                return g.H(p) - st.sinkAsphalt + rise * Math.Max(0, Math.Min(1, (d - st.islandInset) / st.islandInset));
            }
            int tri = 0;
            foreach (var piece in CellPieces(Clip.Region(poly), 0.5, o.notes, plan.parcel))
            {
                foreach (var tr in Triangles(piece, 0.5))
                {
                    var a = tr[0]; var b = tr[1]; var c = tr[2];
                    o.plates.Add(TriPlate(string.Format(Inv, "site:p{0:D4}:l{1}:i{2:D2}:{3:D4}", plan.parcel, li, ii, tri++), PlateKind.Lot, 0, false,
                        SiteMeshSettings.PriorityIsland, a, b, c, Y(a), Y(b), Y(c), ctx, g));
                }
            }
        }

        // ------------------------------------------------------------------------------------------ kerbs and wheel stops

        static void Kerb(LotLayout lot, LotKerb k, PlanGrade g, SiteMeshes o, SiteMeshSettings st)
        {
            if (k.kind != KerbKind.Barrier150 && k.kind != KerbKind.Mountable100) return;
            if (k.line == null || k.line.Length < 2) return;
            double h = k.kind == KerbKind.Barrier150 ? 0.15 : 0.10;
            double lean = k.kind == KerbKind.Mountable100 ? 0.10 : 0.0;
            // which side is the lot: islands are CCW rings with the lot outside (on the right); open lines: test a point
            var pts = new List<double2>(k.line);
            if (k.closed) pts = new List<double2>(Poly2.Ccw(k.line));
            if (k.closed) pts.Add(pts[0]);
            bool lotLeft;
            if (k.closed) lotLeft = false;
            else
            {
                var m = (pts[0] + pts[1]) * 0.5;
                var n = Poly2.Perp(math.normalizesafe(pts[1] - pts[0]));
                lotLeft = lot.lot != null && Poly2.Contains(lot.lot, m + n * 0.3);
            }
            // densify to <= 2 m and drop the stretches at kerb ramps
            var dense = new List<double2>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double L = math.distance(pts[i], pts[i + 1]);
                int n = Math.Max(1, (int)Math.Ceiling(L / 2.0));
                for (int j = 0; j < n; j++) dense.Add(math.lerp(pts[i], pts[i + 1], (double)j / n));
            }
            dense.Add(pts[pts.Count - 1]);
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, smooth = 0, lods = 3 };
            var col = new Color32(0, 0, 200, 255);
            for (int i = 0; i + 1 < dense.Count; i++)
            {
                double2 a = dense[i], b = dense[i + 1];
                var mid = (a + b) * 0.5;
                bool ramp = false;
                foreach (var r in lot.ramps) if (math.distance(mid, r.pos) < r.width * 0.5 + 0.3) { ramp = true; break; }
                if (ramp) continue;
                var dir = math.normalizesafe(b - a);
                var left = Poly2.Perp(dir);
                var toLot = lotLeft ? left : -left;
                var outw = -toLot;
                double ya = g.H(a), yb = g.H(b);
                // face (towards the lot), top, back skirt
                Quad(part, P(a, ya - st.kerbFaceDrop), P(b, yb - st.kerbFaceDrop), P(b + outw * lean, yb + h), P(a + outw * lean, ya + h), V(toLot), col);
                Quad(part, P(a + outw * lean, ya + h), P(b + outw * lean, yb + h), P(b + outw * (lean + st.kerbTop), yb + h), P(a + outw * (lean + st.kerbTop), ya + h), Up, col);
                Quad(part, P(a + outw * (lean + st.kerbTop), ya + h), P(b + outw * (lean + st.kerbTop), yb + h), P(b + outw * (lean + st.kerbTop), yb - st.kerbSkirt), P(a + outw * (lean + st.kerbTop), ya - st.kerbSkirt), V(outw), col);
                o.kerbM += math.distance(a, b);
            }
            if (part.tri.Count > 0) o.meshes.parts.Add(part);
        }

        static void WheelStop(WheelStop w, PlanGrade g, SiteMeshes o, SiteMeshSettings st)
        {
            var c = (w.a + w.b) * 0.5;
            var u = math.normalizesafe(w.b - w.a);
            var v = Poly2.Perp(u);
            double hl = st.wheelStopLength * 0.5, hd = st.wheelStopDepth * 0.5;
            double y0 = g.H(c) - 0.01, y1 = g.H(c) + st.wheelStopHeight;
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, smooth = 0, lods = 1 };
            var col = new Color32(0, 0, 230, 255);
            double2 p00 = c - u * hl - v * hd, p10 = c + u * hl - v * hd, p11 = c + u * hl + v * hd, p01 = c - u * hl + v * hd;
            Quad(part, P(p00, y1), P(p10, y1), P(p11, y1), P(p01, y1), Up, col);
            Quad(part, P(p00, y0), P(p10, y0), P(p10, y1), P(p00, y1), V(-v), col);
            Quad(part, P(p11, y0), P(p01, y0), P(p01, y1), P(p11, y1), V(v), col);
            Quad(part, P(p10, y0), P(p11, y0), P(p11, y1), P(p10, y1), V(u), col);
            Quad(part, P(p01, y0), P(p00, y0), P(p00, y1), P(p01, y1), V(-u), col);
            o.meshes.parts.Add(part);
            o.wheelStops++;
        }

        // ------------------------------------------------------------------------------------------ markings

        static Color32 Colour(MarkColour c) => c == MarkColour.Yellow ? PaintYellow : c == MarkColour.Blue ? PaintBlue : c == MarkColour.Red ? PaintRed : PaintWhite;

        static void Markings(LotLayout lot, PlanGrade g, SurfaceIndex idx, SiteMeshes o, SiteMeshSettings st)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Paint, collider = false, smooth = 0, lods = 1 };
            foreach (var l in lot.lines) Strip(part, l.a, l.b, l.width, Colour(l.colour), g, idx, st.paintLift, o);
            foreach (var h in lot.hatches)
            {
                if (h.poly == null || h.poly.Length < 3) continue;
                var rings = new List<double2[]> { h.poly };
                foreach (var (a, b) in HatchLines(h.poly, h.angleDeg, Math.Max(0.3, h.spacing)))
                    foreach (var (p, q) in ClipSegment(rings, a, b)) Strip(part, p, q, 0.1, Colour(h.colour), g, idx, st.paintLift, o);
                // the hatched area's outline
                for (int i = 0; i < h.poly.Length; i++) Strip(part, h.poly[i], h.poly[(i + 1) % h.poly.Length], 0.1, Colour(h.colour), g, idx, st.paintLift, o);
            }
            foreach (var s in lot.signs) Symbol(part, s, g, idx, st, o);
            if (part.tri.Count > 0) o.meshes.parts.Add(part);
        }

        /// <summary>Concrete joints (forecourts, aprons, pads, every 3.6 m both ways along the slab's long axis) and walk scores (every 1.5 m across).</summary>
        static void Joints(SitePlan plan, PlanGrade g, SurfaceIndex idx, SiteMeshes o, SiteMeshSettings st)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Paint, collider = false, smooth = 0, lods = 1 };
            foreach (var s in plan.surfaces)
            {
                if (s.surface != SiteSurface.Concrete) continue;
                bool walk = s.use == SurfaceUse.Walk || s.use == SurfaceUse.Drive;
                var rect = Poly2.MinAreaRect(s.outer);
                var rings = new List<double2[]> { s.outer };
                rings.AddRange(s.holes);
                var dirs = walk ? new[] { Poly2.Perp(rect.axis) } : new[] { rect.axis, Poly2.Perp(rect.axis) };
                foreach (var d in dirs)
                {
                    // lines along d, spaced across it
                    var n = Poly2.Perp(d);
                    double spacing = walk ? st.scoreSpacing : st.jointSpacing;
                    double ext = rect.halfLong + rect.halfShort + 1;
                    for (double t = -ext + spacing; t < ext; t += spacing)
                    {
                        var a = rect.centre + n * t - d * ext;
                        var b = rect.centre + n * t + d * ext;
                        foreach (var (p, q) in ClipSegment(rings, a, b))
                            if (math.distance(p, q) > 0.3) Strip(part, p, q, st.jointWidth, Joint, g, idx, st.paintLift * 0.6, o);
                    }
                }
            }
            if (part.tri.Count > 0) o.meshes.parts.Add(part);
        }

        static void Strip(RoadMeshPart part, double2 a, double2 b, double width, Color32 col, PlanGrade g, SurfaceIndex idx, double lift, SiteMeshes o)
        {
            double L = math.distance(a, b);
            if (L < 1e-3) return;
            var dir = (b - a) / L;
            var n = Poly2.Perp(dir) * (width * 0.5);
            int pieces = Math.Max(1, (int)Math.Ceiling(L / 1.0));
            int prevL = -1, prevR = -1;
            for (int i = 0; i <= pieces; i++)
            {
                var c = a + dir * (L * i / pieces);
                int il = part.Add(Drape(c + n, g, idx, lift), World(c + n), Vector2.zero, col, Up);
                int ir = part.Add(Drape(c - n, g, idx, lift), World(c - n), Vector2.zero, col, Up);
                if (prevL >= 0)
                {
                    part.Tri(prevL, prevR, ir, Up);
                    part.Tri(prevL, ir, il, Up);
                }
                prevL = il; prevR = ir;
            }
            o.paintM += L;
        }

        static D3 Drape(double2 p, PlanGrade g, SurfaceIndex idx, double lift)
        {
            double y = idx.Height(new D2(p.x, p.y), out var h) ? h : g.H(p);
            return new D3(p.x, y + lift, p.y);
        }

        /// <summary>Accessible symbol (blue square, white wheelchair: wheel ring, seat, back and head), arrow, fire-lane stencil (a red frame).</summary>
        static void Symbol(RoadMeshPart part, MarkSign s, PlanGrade g, SurfaceIndex idx, SiteMeshSettings st, SiteMeshes o)
        {
            double yaw = s.yaw * Math.PI / 180.0;
            var f = new double2(Math.Sin(yaw), Math.Cos(yaw));       // forwards (the yaw's direction)
            var r = new double2(f.y, -f.x);                          // right
            double2 W(double x, double y) => s.pos + r * x + f * y;  // x right, y forwards
            o.symbols++;
            switch (s.symbol)
            {
                case MarkSymbol.Accessible:
                {
                    double h = s.size * 0.5;
                    Poly(part, new[] { W(-h, -h), W(h, -h), W(h, h), W(-h, h) }, PaintBlue, g, idx, st.paintLift);
                    double k = s.size / 1.5;
                    var white = PaintWhite;
                    Ring(part, W(0.05 * k, -0.25 * k), 0.30 * k, 0.22 * k, f, r, white, g, idx, st.symbolLift);
                    Poly(part, new[] { W(-0.12 * k, -0.02 * k), W(0.02 * k, -0.02 * k), W(0.02 * k, 0.35 * k), W(-0.12 * k, 0.35 * k) }, white, g, idx, st.symbolLift);
                    Poly(part, new[] { W(-0.12 * k, -0.02 * k), W(0.32 * k, -0.02 * k), W(0.32 * k, 0.08 * k), W(-0.12 * k, 0.08 * k) }, white, g, idx, st.symbolLift);
                    Ring(part, W(-0.05 * k, 0.50 * k), 0.09 * k, 0.0, f, r, white, g, idx, st.symbolLift);
                    break;
                }
                case MarkSymbol.Arrow:
                {
                    double len = s.size, sw = 0.15, hw = 0.45, hl = len * 0.35;
                    Poly(part, new[] { W(-sw, -len * 0.5), W(sw, -len * 0.5), W(sw, len * 0.5 - hl), W(-sw, len * 0.5 - hl) }, Colour(s.colour), g, idx, st.paintLift);
                    Poly(part, new[] { W(-hw, len * 0.5 - hl), W(hw, len * 0.5 - hl), W(0, len * 0.5) }, Colour(s.colour), g, idx, st.paintLift);
                    break;
                }
                default:
                {
                    double hw = s.size * 0.5, hh = 0.3, t = 0.1;
                    var c = Colour(s.colour);
                    Poly(part, new[] { W(-hw, -hh), W(hw, -hh), W(hw, -hh + t), W(-hw, -hh + t) }, c, g, idx, st.paintLift);
                    Poly(part, new[] { W(-hw, hh - t), W(hw, hh - t), W(hw, hh), W(-hw, hh) }, c, g, idx, st.paintLift);
                    Poly(part, new[] { W(-hw, -hh + t), W(-hw + t, -hh + t), W(-hw + t, hh - t), W(-hw, hh - t) }, c, g, idx, st.paintLift);
                    Poly(part, new[] { W(hw - t, -hh + t), W(hw, -hh + t), W(hw, hh - t), W(hw - t, hh - t) }, c, g, idx, st.paintLift);
                    break;
                }
            }
        }

        static void Poly(RoadMeshPart part, double2[] poly, Color32 col, PlanGrade g, SurfaceIndex idx, double lift)
        {
            var p = Poly2.Ccw(poly);
            int b = part.pos.Count;
            foreach (var q in p) part.Add(Drape(q, g, idx, lift), World(q), Vector2.zero, col, Up);
            for (int i = 1; i + 1 < p.Length; i++) part.Tri(b, b + i, b + i + 1, Up);
        }

        static void Ring(RoadMeshPart part, double2 c, double ro, double ri, double2 f, double2 r, Color32 col, PlanGrade g, SurfaceIndex idx, double lift)
        {
            const int N = 16;
            if (ri <= 0)
            {
                var disc = new double2[N];
                for (int i = 0; i < N; i++) { double a = 2 * Math.PI * i / N; disc[i] = c + r * (Math.Cos(a) * ro) + f * (Math.Sin(a) * ro); }
                Poly(part, disc, col, g, idx, lift);
                return;
            }
            for (int i = 0; i < N; i++)
            {
                double a0 = 2 * Math.PI * i / N, a1 = 2 * Math.PI * (i + 1) / N;
                double2 U(double a, double rr) => c + r * (Math.Cos(a) * rr) + f * (Math.Sin(a) * rr);
                Poly(part, new[] { U(a0, ri), U(a0, ro), U(a1, ro), U(a1, ri) }, col, g, idx, lift);
            }
        }

        /// <summary>Hatch lines across a polygon's bounding box at an angle (degrees), spacing apart.</summary>
        static IEnumerable<(double2 a, double2 b)> HatchLines(double2[] poly, float angleDeg, double spacing)
        {
            double ang = angleDeg * Math.PI / 180;
            var d = new double2(Math.Cos(ang), Math.Sin(ang));
            var n = Poly2.Perp(d);
            double lo = double.MaxValue, hi = double.MinValue, a0 = double.MaxValue, a1 = double.MinValue;
            foreach (var p in poly)
            {
                double t = math.dot(p, n), u = math.dot(p, d);
                lo = Math.Min(lo, t); hi = Math.Max(hi, t); a0 = Math.Min(a0, u); a1 = Math.Max(a1, u);
            }
            for (double t = Math.Ceiling(lo / spacing) * spacing; t <= hi; t += spacing)
                yield return (n * t + d * (a0 - 1), n * t + d * (a1 + 1));
        }

        /// <summary>The parts of segment a-b inside the rings (even-odd: an outer ring and its holes).</summary>
        public static List<(double2, double2)> ClipSegment(IReadOnlyList<double2[]> rings, double2 a, double2 b)
        {
            var ts = new List<double> { 0, 1 };
            var ab = b - a;
            foreach (var ring in rings)
                for (int i = 0; i < ring.Length; i++)
                {
                    double2 c = ring[i], d = ring[(i + 1) % ring.Length], cd = d - c;
                    double den = Poly2.Cross(ab, cd);
                    if (Math.Abs(den) < 1e-12) continue;
                    double t = Poly2.Cross(c - a, cd) / den, u = Poly2.Cross(c - a, ab) / den;
                    if (t > 0 && t < 1 && u >= 0 && u <= 1) ts.Add(t);
                }
            ts.Sort();
            var o = new List<(double2, double2)>();
            for (int i = 0; i + 1 < ts.Count; i++)
            {
                if (ts[i + 1] - ts[i] < 1e-9) continue;
                var m = a + ab * ((ts[i] + ts[i + 1]) * 0.5);
                int inside = 0;
                foreach (var ring in rings) if (Poly2.Contains(ring, m)) inside++;
                if (inside % 2 == 1) o.Add((a + ab * ts[i], a + ab * ts[i + 1]));
            }
            return o;
        }

        // ------------------------------------------------------------------------------------------ cells and triangles

        /// <summary>
        /// A region cut into the cells of a world grid (cell metres): simple CCW pieces without holes (a cell whose piece has a
        /// hole is split into four, down to 0.5 m; below that the hole is dropped and noted).
        /// </summary>
        public static List<double2[]> CellPieces(Paths64 region, double cell, List<string> notes, int parcel)
        {
            var o = new List<double2[]>();
            if (region == null || region.Count == 0) return o;
            var bb = Clipper.GetBounds(region);
            double x0 = bb.left / Clip.Scale, y0 = bb.top / Clip.Scale, x1 = bb.right / Clip.Scale, y1 = bb.bottom / Clip.Scale;
            long i0 = (long)Math.Floor(x0 / cell), i1 = (long)Math.Floor(x1 / cell), j0 = (long)Math.Floor(y0 / cell), j1 = (long)Math.Floor(y1 / cell);
            for (long j = j0; j <= j1; j++)
                for (long i = i0; i <= i1; i++)
                    Cell(region, i * cell, j * cell, cell, o, notes, parcel);
            return o;
        }

        static void Cell(Paths64 region, double x, double y, double size, List<double2[]> o, List<string> notes, int parcel)
        {
            var sq = Clip.Region(new[] { new double2(x, y), new double2(x + size, y), new double2(x + size, y + size), new double2(x, y + size) });
            var inter = Clip.Intersect(region, sq);
            if (inter.Count == 0 || Clip.Area(inter) < 1e-4) return;
            foreach (var piece in Clip.Pieces(inter))
            {
                if (Clip.Area(piece) < 1e-4) continue;
                if (piece.Count == 1)
                {
                    var p = Poly2.Ccw(Clip.Pts(piece[0]));
                    if (p.Length >= 3) o.Add(p);
                    continue;
                }
                if (size > 0.6)
                {
                    double h = size * 0.5;
                    for (int k = 0; k < 4; k++) Cell(piece, x + (k & 1) * h, y + (k >> 1) * h, h, o, notes, parcel);
                    continue;
                }
                notes?.Add($"parcel {parcel}: a hole under {size:F2} m covered");
                var q = Poly2.Ccw(Clip.Pts(piece[0]));
                if (q.Length >= 3) o.Add(q);
            }
        }

        /// <summary>RoadMeshPart.arm of the site's surface parts (asphalt and concrete paving; kerbs, wheel stops and paint keep -1).</summary>
        public const int SurfacePart = 1;

        /// <summary>The triangles of a piece, its cell split in four (down to 0.5 m) until each lies within tol of the grade
        /// at its edge midpoints and centroid (so the flat triangles follow ramps and anchors; plates use the same ones).</summary>
        public static List<double2[]> Graded(double2[] piece, double cell, PlanGrade g, double tol)
        {
            var tris = Triangles(piece, cell);
            if (cell <= 0.5 + 1e-9 || tris.Count == 0) return tris;
            foreach (var t in tris)
                if (Deviation(t, g) > tol)
                {
                    var o = new List<double2[]>();
                    foreach (var sub in CellPieces(Clip.Region(piece), cell * 0.5, null, -1)) o.AddRange(Graded(sub, cell * 0.5, g, tol));
                    return o;
                }
            return tris;
        }

        static double Deviation(double2[] t, PlanGrade g)
        {
            double ha = g.H(t[0]), hb = g.H(t[1]), hc = g.H(t[2]);
            double d = Math.Abs(g.H((t[0] + t[1]) * 0.5) - (ha + hb) * 0.5);
            d = Math.Max(d, Math.Abs(g.H((t[1] + t[2]) * 0.5) - (hb + hc) * 0.5));
            d = Math.Max(d, Math.Abs(g.H((t[2] + t[0]) * 0.5) - (hc + ha) * 0.5));
            return Math.Max(d, Math.Abs(g.H((t[0] + t[1] + t[2]) / 3.0) - (ha + hb + hc) / 3.0));
        }

        /// <summary>The triangles of a piece: its own triangulation, else the piece cut into a finer grid (down to 1/16 of a cell) and those.</summary>
        public static List<double2[]> Triangles(double2[] piece, double cell)
        {
            var o = new List<double2[]>();
            var t = Triangulate(piece);
            if (t != null)
            {
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    double2 a = piece[t[k]], b = piece[t[k + 1]], c = piece[t[k + 2]];
                    if (Math.Abs(Poly2.Cross(b - a, c - a)) > 1e-6) o.Add(new[] { a, b, c });   // no slivers of collinear points
                }
                return o;
            }
            if (cell <= 0.3) return o;
            foreach (var sub in CellPieces(Clip.Region(piece), cell / 4, null, -1)) o.AddRange(Triangles(sub, cell / 4));
            return o;
        }

        /// <summary>Triangle indices of a simple CCW polygon: a fan from vertex 0 when convex, else ear clipping; null on failure.</summary>
        public static List<int> Triangulate(double2[] p)
        {
            int n = p.Length;
            if (n < 3) return null;
            bool convex = true;
            for (int i = 0; i < n && convex; i++)
                if (Poly2.Cross(p[(i + 1) % n] - p[i], p[(i + 2) % n] - p[(i + 1) % n]) < -1e-12) convex = false;
            var t = new List<int>();
            if (convex)
            {
                for (int i = 1; i + 1 < n; i++) { t.Add(0); t.Add(i); t.Add(i + 1); }
                return t;
            }
            var d2 = new List<D2>(n);
            foreach (var q in p) d2.Add(new D2(q.x, q.y));
            t = Geom2.EarClip(d2);
            return t.Count == 3 * (n - 2) ? t : null;
        }

        // ------------------------------------------------------------------------------------------ helpers

        static D3 P(double2 p, double y) => new D3(p.x, y, p.y);
        static Vector3 V(double2 d) => new Vector3((float)d.x, 0, (float)d.y);
        static Vector2 World(double2 p) => new Vector2((float)(p.x / 4), (float)(p.y / 4));

        static Vector3 Normal(PlanGrade g, double2 p)
        {
            const double e = 0.25;
            double gx = (g.H(p + new double2(e, 0)) - g.H(p - new double2(e, 0))) / (2 * e);
            double gz = (g.H(p + new double2(0, e)) - g.H(p - new double2(0, e))) / (2 * e);
            var n = new Vector3((float)-gx, 1f, (float)-gz);
            return n.normalized;
        }

        static void Quad(RoadMeshPart part, D3 a, D3 b, D3 c, D3 d, Vector3 front, Color32 col)
        {
            int ia = part.Add(a, new Vector2((float)(a.x / 4), (float)(a.z / 4)), Vector2.zero, col, front);
            int ib = part.Add(b, new Vector2((float)(b.x / 4), (float)(b.z / 4)), Vector2.zero, col, front);
            int ic = part.Add(c, new Vector2((float)(c.x / 4), (float)(c.z / 4)), Vector2.zero, col, front);
            int id = part.Add(d, new Vector2((float)(d.x / 4), (float)(d.z / 4)), Vector2.zero, col, front);
            part.Tri(ia, ib, ic, front);
            part.Tri(ia, ic, id, front);
        }
    }
}
