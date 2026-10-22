using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Unity.Mathematics;

namespace Backroads.Sites
{
    /// <summary>
    /// A stable fingerprint of a lot layout (design §10): every position quantised to millimetres and every angle to
    /// 0.01 degrees, written in list order, then SHA-256. Two packs of the same input must give the same hash.
    /// </summary>
    public static class LotLayoutHash
    {
        public static string Compute(LotLayout l)
        {
            var sb = new StringBuilder(4096);
            void P(double2 p) => sb.Append(Mm(p.x)).Append(',').Append(Mm(p.y)).Append(';');
            void Ps(double2[] ps) { if (ps == null) { sb.Append('-'); return; } foreach (var p in ps) P(p); sb.Append('|'); }
            sb.Append("v").Append(l.report.version).Append('\n');
            Ps(l.lot);
            foreach (var s in l.stalls) { sb.Append((int)s.kind).Append(':'); Ps(s.poly); sb.Append(s.lane).Append(',').Append(s.run).Append('\n'); }
            foreach (var x in l.lanes) { sb.Append((int)x.kind).Append(x.oneWay ? 'o' : 't'); P(x.a); P(x.b); sb.Append(Mm(x.width)); Ps(x.poly); sb.Append('\n'); }
            foreach (var x in l.islands) { sb.Append((int)x.kind).Append(x.kerbed ? 'k' : 'p'); Ps(x.poly); }
            foreach (var x in l.kerbs) { sb.Append((int)x.kind).Append(x.closed ? 'c' : 'o'); Ps(x.line); }
            foreach (var x in l.wheelStops) { P(x.a); P(x.b); sb.Append(x.stall); }
            foreach (var x in l.lines) { P(x.a); P(x.b); sb.Append(Mm(x.width)).Append((int)x.colour); }
            foreach (var x in l.signs) { sb.Append((int)x.symbol); P(x.pos); sb.Append(Deg(x.yaw)).Append((int)x.colour); }
            foreach (var x in l.hatches) { sb.Append((int)x.colour); Ps(x.poly); }
            foreach (var x in l.ramps) { P(x.pos); P(x.dir); sb.Append(Mm(x.width)); }
            foreach (var x in l.lamps) P(x);
            foreach (var x in l.props) { sb.Append(x.key); P(x.pos); sb.Append(Deg(x.yaw)); }
            if (l.driveThrough != null) { Ps(l.driveThrough.path); sb.Append(l.driveThrough.stackCars); P(l.driveThrough.window); P(l.driveThrough.orderBoard); }
            Ps(l.fireLane);
            var r = l.report;
            sb.Append(r.capacity).Append(',').Append(r.total).Append(',').Append(r.accessible).Append(',').Append(r.van).Append(',').Append(r.dirIndex)
              .Append(',').Append(Deg(r.angle)).Append(',').Append(Mm(r.phase)).Append(',').Append(r.spineMode);
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(64);
                foreach (var b in h) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        static string Mm(double v) => Math.Round(v * 1000).ToString("R", CultureInfo.InvariantCulture);
        static string Deg(double v) => Math.Round(v * 100).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A plan map of a lot layout as SVG (north up, 1 m = scale px): the debug view and the handoff pictures.</summary>
    public static class LotLayoutSvg
    {
        public static string Render(LotLayout l, double2[] building = null, string title = null, double scale = 6)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Grow(double2[] ps) { if (ps == null) return; foreach (var p in ps) { minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y); } }
            Grow(l.lot);
            Grow(building);
            if (l.driveThrough != null) foreach (var p in l.driveThrough.pieces) Grow(p);
            if (minX > maxX) { minX = minY = 0; maxX = maxY = 10; }
            double pad = 6;
            minX -= pad; minY -= pad; maxX += pad; maxY += pad;
            double w = (maxX - minX) * scale, h = (maxY - minY) * scale + (title != null ? 22 : 0);
            double top = title != null ? 22 : 0;
            var sb = new StringBuilder(16384);
            var ci = CultureInfo.InvariantCulture;
            string X(double x) => ((x - minX) * scale).ToString("0.##", ci);
            string Y(double y) => (top + (maxY - y) * scale).ToString("0.##", ci);
            string Pts(double2[] ps) { var s = new StringBuilder(); foreach (var p in ps) s.Append(X(p.x)).Append(',').Append(Y(p.y)).Append(' '); return s.ToString(); }
            void Poly(double2[] ps, string fill, string stroke, double sw, string extra = "")
            {
                if (ps == null || ps.Length < 2) return;
                sb.Append("<polygon points=\"").Append(Pts(ps)).Append("\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke)
                  .Append("\" stroke-width=\"").Append(sw.ToString("0.##", ci)).Append('"').Append(extra).Append("/>\n");
            }
            void Line(double2 a, double2 b, string stroke, double sw, string extra = "") =>
                sb.Append("<line x1=\"").Append(X(a.x)).Append("\" y1=\"").Append(Y(a.y)).Append("\" x2=\"").Append(X(b.x)).Append("\" y2=\"").Append(Y(b.y))
                  .Append("\" stroke=\"").Append(stroke).Append("\" stroke-width=\"").Append(sw.ToString("0.##", ci)).Append('"').Append(extra).Append("/>\n");
            void Dot(double2 p, double r, string fill) =>
                sb.Append("<circle cx=\"").Append(X(p.x)).Append("\" cy=\"").Append(Y(p.y)).Append("\" r=\"").Append(r.ToString("0.##", ci)).Append("\" fill=\"").Append(fill).Append("\"/>\n");

            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(w.ToString("0", ci)).Append("\" height=\"").Append(h.ToString("0", ci))
              .Append("\" viewBox=\"0 0 ").Append(w.ToString("0", ci)).Append(' ').Append(h.ToString("0", ci)).Append("\" font-family=\"sans-serif\">\n");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#cfd8c4\"/>\n");
            if (title != null) sb.Append("<text x=\"6\" y=\"16\" font-size=\"13\" fill=\"#222\">").Append(Esc(title)).Append("</text>\n");
            Poly(l.lot, "#9a9a96", "#555", 1.5);
            foreach (var x in l.lanes) Poly(x.poly, x.kind == LaneKind.FireLane ? "#b79a9a" : x.kind == LaneKind.DriveThrough ? "#8f8aa6" : "#7f7f7c", "none", 0);
            foreach (var x in l.islands) Poly(x.poly, "#6f9a5a", x.kerbed ? "#e8e8e0" : "none", 1);
            foreach (var x in l.stalls)
                Poly(x.poly, x.kind == StallKind.Van ? "#3d6fc4" : x.kind == StallKind.Accessible ? "#6d92d6" : x.kind == StallKind.Trailer ? "#a59c7c" : "#a9a9a5", "#333", 0.6);
            foreach (var x in l.hatches) Poly(x.poly, "none", x.colour == MarkColour.Blue ? "#1f4fa8" : "#eee", 1.2, " stroke-dasharray=\"3,2\"");
            foreach (var x in l.lines) Line(x.a, x.b, x.colour == MarkColour.Blue ? "#1f4fa8" : x.colour == MarkColour.Red ? "#c02020" : "#ffffff", Math.Max(1, x.width * scale));
            foreach (var x in l.kerbs)
            {
                if (x.line == null || x.line.Length < 2) continue;
                sb.Append(x.closed ? "<polygon" : "<polyline").Append(" points=\"").Append(Pts(x.line)).Append("\" fill=\"none\" stroke=\"#f2f2ea\" stroke-width=\"2\"/>\n");
            }
            foreach (var x in l.wheelStops) Line(x.a, x.b, "#e0a030", 2.5);
            if (l.driveThrough != null)
            {
                var p = l.driveThrough.path;
                for (int i = 0; i + 1 < p.Length; i++) Line(p[i], p[i + 1], "#5b3f9e", 1.5, " stroke-dasharray=\"6,3\"");
                Dot(l.driveThrough.window, 3, "#5b3f9e");
                Dot(l.driveThrough.orderBoard, 3, "#9e3f8a");
            }
            if (building != null) Poly(building, "#5e4b3c", "#2e241c", 1);
            foreach (var x in l.signs)
            {
                if (x.symbol == MarkSymbol.Arrow)
                {
                    double yaw = x.yaw * Math.PI / 180;
                    var d = new double2(Math.Sin(yaw), Math.Cos(yaw));
                    Line(x.pos - d * 1.2, x.pos + d * 1.2, "#ffffff", 2);
                    Line(x.pos + d * 1.2, x.pos + d * 0.3 + Poly2.Perp(d) * 0.8, "#ffffff", 2);
                    Line(x.pos + d * 1.2, x.pos + d * 0.3 - Poly2.Perp(d) * 0.8, "#ffffff", 2);
                }
                else Dot(x.pos, 0.6 * scale, x.colour == MarkColour.Red ? "#c02020" : "#1f4fa8");
            }
            foreach (var x in l.ramps) Dot(x.pos, 0.4 * scale, "#f0f0f0");
            foreach (var x in l.lamps) Dot(x, 0.45 * scale, "#ffd23a");
            foreach (var x in l.props) Dot(x.pos, 0.3 * scale, "#d03030");
            foreach (var e in l.entrances) Line(e.a, e.b, "#e03030", 3);
            // 10 m scale bar, bottom left.
            double bx = minX + 2, by = minY + 2;
            Line(new double2(bx, by), new double2(bx + 10, by), "#111", 2);
            sb.Append("<text x=\"").Append(X(bx)).Append("\" y=\"").Append(Y(by + 1)).Append("\" font-size=\"10\" fill=\"#111\">10 m</text>\n");
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
