using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Site plans as an SVG plan map, north up: parcel lines, surfaces by material (asphalt dark grey, concrete light,
    /// gravel tan, dirt brown, pavers red-brown), stalls and lanes of every lot, buildings, doors as coloured ticks on the
    /// walls (main red, service orange, garage and bay blue, dock purple, window green), props as dots, lane A's model
    /// requests outlined in purple, fences dashed, driveway cuts on the walk's back edge (green baked, yellow moved or
    /// merged, red rejected).
    /// </summary>
    public static class SitePlanMap
    {
        static string Fill(SiteSurface s)
        {
            switch (s)
            {
                case SiteSurface.Asphalt: return "#5b5b5e";
                case SiteSurface.Concrete: return "#c4c2bb";
                case SiteSurface.Gravel: return "#b9a77f";
                case SiteSurface.Dirt: return "#8d7152";
                case SiteSurface.Pavers: return "#9a6551";
                default: return "#7fae6c";
            }
        }

        static string DoorColour(DoorKind k)
        {
            switch (k)
            {
                case DoorKind.Main: return "#e02020";
                case DoorKind.Service: return "#f08a10";
                case DoorKind.Garage:
                case DoorKind.Bay: return "#2060e0";
                case DoorKind.Dock: return "#8a30c0";
                default: return "#20a040";
            }
        }

        public static string Svg(SitePlanSet s, (double2 min, double2 max)? view, double scale, string title)
        {
            double2 mn, mx;
            var ps = s.ctx.parcels;
            if (view.HasValue) { mn = view.Value.min; mx = view.Value.max; }
            else
            {
                mn = new double2(double.MaxValue, double.MaxValue);
                mx = new double2(double.MinValue, double.MinValue);
                foreach (var p in s.plans) foreach (var q in ps.parcels[p.parcel].outer) { mn = math.min(mn, q); mx = math.max(mx, q); }
                mn -= 10; mx += 10;
            }
            var ci = CultureInfo.InvariantCulture;
            double w = (mx.x - mn.x) * scale, h = (mx.y - mn.y) * scale + 24;
            var sb = new StringBuilder(1 << 20);
            string X(double x) => ((x - mn.x) * scale).ToString("0.#", ci);
            string Y(double y) => (24 + (mx.y - y) * scale).ToString("0.#", ci);
            bool Vis(IEnumerable<double2> r) => r.Any(q => q.x >= mn.x && q.x <= mx.x && q.y >= mn.y && q.y <= mx.y);
            string D(double2[] ring)
            {
                var o = new StringBuilder("M");
                for (int i = 0; i < ring.Length; i++) o.Append(i == 0 ? "" : " L").Append(X(ring[i].x)).Append(',').Append(Y(ring[i].y));
                return o.Append(" Z").ToString();
            }
            string P(IEnumerable<double2> line)
            {
                var o = new StringBuilder();
                foreach (var q in line) o.Append(X(q.x)).Append(',').Append(Y(q.y)).Append(' ');
                return o.ToString();
            }
            void Line(double2 a, double2 b, string col, double sw, string extra = "") =>
                sb.Append("<line x1=\"").Append(X(a.x)).Append("\" y1=\"").Append(Y(a.y)).Append("\" x2=\"").Append(X(b.x)).Append("\" y2=\"").Append(Y(b.y))
                  .Append("\" stroke=\"").Append(col).Append("\" stroke-width=\"").Append(sw.ToString("0.##", ci)).Append('"').Append(extra).Append("/>\n");
            void Dot(double2 p, double r, string col) =>
                sb.Append("<circle cx=\"").Append(X(p.x)).Append("\" cy=\"").Append(Y(p.y)).Append("\" r=\"").Append(r.ToString("0.##", ci)).Append("\" fill=\"").Append(col).Append("\"/>\n");

            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(w.ToString("0", ci)).Append("\" height=\"").Append(h.ToString("0", ci)).Append("\" font-family=\"sans-serif\">\n")
              .Append("<rect width=\"100%\" height=\"100%\" fill=\"#7a7a78\"/>\n");
            foreach (var b in ps.blocks.blocks)
                foreach (var r in b.outers)
                    if (Vis(r)) sb.Append("<path d=\"").Append(D(r)).Append("\" fill=\"#a9c48e\"/>\n");
            foreach (var p in ps.parcels)
                if (Vis(p.outer)) sb.Append("<path d=\"").Append(D(p.outer)).Append("\" fill=\"none\" stroke=\"#4d6b3a\" stroke-width=\"0.7\"/>\n");
            foreach (var plan in s.plans)
            {
                foreach (var srf in plan.surfaces)
                {
                    if (!Vis(srf.outer)) continue;
                    var d = new StringBuilder(D(srf.outer));
                    foreach (var hole in srf.holes) d.Append(' ').Append(D(hole));
                    sb.Append("<path d=\"").Append(d).Append("\" fill=\"").Append(Fill(srf.surface)).Append("\" fill-rule=\"evenodd\"/>\n");
                }
                foreach (var lot in plan.lots)
                {
                    if (!Vis(lot.lot)) continue;
                    foreach (var isl in lot.islands) sb.Append("<path d=\"").Append(D(isl.poly)).Append("\" fill=\"#6f9a5a\"/>\n");
                    foreach (var st in lot.stalls)
                        sb.Append("<path d=\"").Append(D(st.poly)).Append("\" fill=\"").Append(st.kind == StallKind.Accessible || st.kind == StallKind.Van ? "#5f84c8" : "none")
                          .Append("\" stroke=\"#e8e8e8\" stroke-width=\"0.6\"/>\n");
                    foreach (var l in lot.lamps) Dot(l, Math.Max(1, 0.4 * scale), "#ffd23a");
                }
                foreach (var m in plan.models)
                    if (m.footprint != null && Vis(m.footprint)) sb.Append("<path d=\"").Append(D(m.footprint)).Append("\" fill=\"#d9c8f0\" stroke=\"#7a3fb0\" stroke-width=\"1\"/>\n");
                foreach (var f in plan.fences)
                    if (Vis(f.line)) sb.Append("<polyline points=\"").Append(P(f.line)).Append("\" fill=\"none\" stroke=\"#303030\" stroke-dasharray=\"3,2\" stroke-width=\"1.2\"/>\n");
                foreach (var g in plan.gates) Line(g.a, g.b, "#ff9020", 2.5);
            }
            foreach (var b in s.ctx.data.buildings)
                if (b.inPlay && Vis(b.poly)) sb.Append("<path d=\"").Append(D(b.poly)).Append("\" fill=\"").Append(b.Accessory ? "#6e5a48" : "#3d2f24").Append("\"/>\n");
            foreach (var plan in s.plans)
            {
                foreach (var d in plan.doors)
                {
                    var b = s.ctx.data.buildings.FirstOrDefault(x => x.osm == d.building);
                    if (b == null || d.wallEdge >= b.poly.Length) continue;
                    var p0 = b.poly[d.wallEdge];
                    var e = math.normalizesafe(b.poly[(d.wallEdge + 1) % b.poly.Length] - p0);
                    var c = p0 + e * d.offset;
                    if (!Vis(new[] { c })) continue;
                    Line(c - e * (d.width * 0.5), c + e * (d.width * 0.5), DoorColour(d.kind), Math.Max(2, 0.5 * scale));
                }
                foreach (var pr in plan.props) if (Vis(new[] { pr.pos })) Dot(pr.pos, Math.Max(1, 0.35 * scale), "#d03030");
                foreach (var c in plan.cuts)
                {
                    if (c.outcome == CutOutcome.Rejected || !s.ctx.fronts.TryGetValue(c.faceId, out var f)) continue;
                    var a = f.BackAt(c.s0, out _);
                    var b = f.BackAt(c.s1, out _);
                    if (Vis(new[] { a, b })) Line(a, b, c.outcome == CutOutcome.Baked ? "#20c040" : "#f0d020", Math.Max(2, 0.8 * scale));
                }
                foreach (var r in plan.driveways)
                {
                    var cut = plan.cuts.FirstOrDefault(c => c.requestId == r.id);
                    if (cut == null || cut.outcome != CutOutcome.Rejected || !s.ctx.fronts.TryGetValue(r.faceId, out var f)) continue;
                    var a = f.BackAt(r.s0, out _);
                    var b = f.BackAt(r.s1, out _);
                    if (Vis(new[] { a, b })) Line(a, b, "#ff2020", Math.Max(2, 0.8 * scale), " stroke-dasharray=\"2,2\"");
                }
            }
            sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"22\" fill=\"#fff\"/><text x=\"6\" y=\"16\" font-size=\"13\">")
              .Append(title).Append(": ").Append(s.plans.Count).Append(" plans</text>\n</svg>\n");
            return sb.ToString();
        }
    }
}
