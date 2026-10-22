using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// The parcels' plan map (card sites-S1 step 5) as SVG, north up: parcels coloured by business type (vacant land
    /// pale, public land green), buildings dark, fixed areas hatched, frontage faces as coloured lines on the walk's back
    /// edge (one colour per parcel, alternating), easements dashed, mapped driveway requests as red ticks.
    /// </summary>
    public static class ParcelMap
    {
        static string Colour(Parcel p)
        {
            if (p.kind == ParcelKind.Vacant) return p.fixedAreas.Length > 0 ? "#c9c3a8" : "#e9e6d8";
            if (p.kind == ParcelKind.Public) return "#a9d39a";
            switch (p.type)
            {
                case BusinessType.House: return "#f4d38a";
                case BusinessType.Apartments: return "#f0b46a";
                case BusinessType.Retail:
                case BusinessType.Downtown: return "#e88a8a";
                case BusinessType.Office: return "#d58ac9";
                case BusinessType.Restaurant:
                case BusinessType.FastFood: return "#f09a5a";
                case BusinessType.Fuel: return "#e06060";
                case BusinessType.CarRepair: return "#b08a6a";
                case BusinessType.Motel: return "#9ab0e8";
                case BusinessType.Civic:
                case BusinessType.Church:
                case BusinessType.School: return "#8ab8e0";
                case BusinessType.Warehouse:
                case BusinessType.Industrial: return "#a0a0b8";
                case BusinessType.Farm: return "#c8d890";
                default: return "#d0d0d0";
            }
        }

        public static string Svg(ParcelSet set, (double2 min, double2 max)? view, double scale, string title)
        {
            double2 mn, mx;
            if (view.HasValue) { mn = view.Value.min; mx = view.Value.max; }
            else
            {
                mn = new double2(double.MaxValue, double.MaxValue);
                mx = new double2(double.MinValue, double.MinValue);
                foreach (var p in set.parcels) foreach (var q in p.outer) { mn = math.min(mn, q); mx = math.max(mx, q); }
                mn -= 20; mx += 20;
            }
            double w = (mx.x - mn.x) * scale, h = (mx.y - mn.y) * scale + 24;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1 << 20);
            string X(double x) => ((x - mn.x) * scale).ToString("0.#", ci);
            string Y(double y) => (24 + (mx.y - y) * scale).ToString("0.#", ci);
            bool Visible(double2[] r)
            {
                foreach (var q in r) if (q.x >= mn.x && q.x <= mx.x && q.y >= mn.y && q.y <= mx.y) return true;
                return false;
            }
            string D(double2[] ring)
            {
                var s = new StringBuilder("M");
                for (int i = 0; i < ring.Length; i++) s.Append(i == 0 ? "" : " L").Append(X(ring[i].x)).Append(',').Append(Y(ring[i].y));
                return s.Append(" Z").ToString();
            }
            string P(double2[] line)
            {
                var s = new StringBuilder();
                foreach (var q in line) s.Append(X(q.x)).Append(',').Append(Y(q.y)).Append(' ');
                return s.ToString();
            }
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(w.ToString("0", ci)).Append("\" height=\"").Append(h.ToString("0", ci))
              .Append("\" font-family=\"sans-serif\">\n<defs><pattern id=\"hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">")
              .Append("<line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"#6b5d2e\" stroke-width=\"1.2\"/></pattern></defs>\n")
              .Append("<rect width=\"100%\" height=\"100%\" fill=\"#7d7d7a\"/>\n");
            // Blocks (light) under the parcels, so gaps would show.
            foreach (var b in set.blocks.blocks)
            {
                var d = new StringBuilder();
                foreach (var r in b.outers) if (Visible(r)) d.Append(D(r)).Append(' ');
                foreach (var r in b.holes) d.Append(D(r)).Append(' ');
                if (d.Length > 0) sb.Append("<path d=\"").Append(d).Append("\" fill=\"#ff00ff\" fill-rule=\"evenodd\"/>\n");
            }
            foreach (var p in set.parcels)
            {
                if (!Visible(p.outer)) continue;
                var d = new StringBuilder(D(p.outer));
                foreach (var r in p.holes) d.Append(' ').Append(D(r));
                sb.Append("<path d=\"").Append(d).Append("\" fill=\"").Append(Colour(p)).Append("\" fill-rule=\"evenodd\" stroke=\"#444\" stroke-width=\"0.6\"/>\n");
            }
            foreach (var f in set.fixedAreas)
                if (Visible(f.outer)) sb.Append("<path d=\"").Append(D(f.outer)).Append("\" fill=\"url(#hatch)\" stroke=\"#6b5d2e\" stroke-width=\"1\"/>\n");
            foreach (var b in set.data.buildings)
                if (b.inPlay && Visible(b.poly)) sb.Append("<path d=\"").Append(D(b.poly)).Append("\" fill=\"").Append(b.Accessory ? "#6e5a48" : "#3d2f24").Append("\"/>\n");
            // Frontage: each parcel's stretches on the back edge, alternating colours.
            var fronts = new Dictionary<int, FrontLine>();
            foreach (var f in set.blocks.fronts) fronts[f.faceId] = f;
            foreach (var p in set.parcels)
                foreach (var fr in p.frontage)
                {
                    if (!fronts.TryGetValue(fr.faceId, out var fl)) continue;
                    var pts = new List<double2>();
                    for (double t = fr.s0; t <= fr.s1 + 1e-6; t += 1.0) pts.Add(fl.BackAt(t, out _));
                    pts.Add(fl.BackAt(fr.s1, out _));
                    if (!Visible(pts.ToArray())) continue;
                    sb.Append("<polyline points=\"").Append(P(pts.ToArray())).Append("\" fill=\"none\" stroke=\"").Append(p.id % 2 == 0 ? "#1f5fbf" : "#19a0a0")
                      .Append("\" stroke-width=\"").Append(Math.Max(1.5, scale * 0.6).ToString("0.#", ci)).Append("\"/>\n");
                }
            foreach (var p in set.parcels)
                if (p.easement != null && Visible(p.easement))
                    sb.Append("<polyline points=\"").Append(P(p.easement)).Append("\" fill=\"none\" stroke=\"#202020\" stroke-dasharray=\"4,3\" stroke-width=\"1\"/>\n");
            foreach (var r in set.requests)
            {
                if (!fronts.TryGetValue(r.faceId, out var fl)) continue;
                var a = fl.KerbAt(r.s0);
                var b = fl.KerbAt(r.s1);
                if (!Visible(new[] { a, b })) continue;
                sb.Append("<line x1=\"").Append(X(a.x)).Append("\" y1=\"").Append(Y(a.y)).Append("\" x2=\"").Append(X(b.x)).Append("\" y2=\"").Append(Y(b.y))
                  .Append("\" stroke=\"#e01010\" stroke-width=\"").Append(Math.Max(2, scale * 1.2).ToString("0.#", ci)).Append("\"/>\n");
            }
            sb.Append("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"22\" fill=\"#fff\"/><text x=\"6\" y=\"16\" font-size=\"13\">")
              .Append(title).Append(": ").Append(set.parcels.Count).Append(" parcels</text>\n");
            sb.Append("</svg>\n");
            return sb.ToString();
        }
    }
}
