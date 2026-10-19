using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.EngineSim
{
    /// <summary>Torque (N m) and power (hp) against rpm, drawn with the UI Toolkit vector API.</summary>
    public sealed class DynoPlot : VisualElement
    {
        public readonly List<DynoSweep.Point> Points = new List<DynoSweep.Point>();
        public double MaxRpm = 7000;
        static readonly Color Axis = new Color(1f, 1f, 1f, 0.35f);
        static readonly Color Torque = new Color(0.36f, 0.74f, 1f);
        static readonly Color Power = new Color(1f, 0.64f, 0.25f);
        const float HpW = 745.699872f;

        readonly Label _caption;

        public DynoPlot()
        {
            style.height = 220;
            style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            style.marginTop = 6;
            generateVisualContent += Draw;
            _caption = new Label("Dyno sweep: press D or the button") { pickingMode = PickingMode.Ignore };
            _caption.style.position = Position.Absolute;
            _caption.style.left = 8;
            _caption.style.top = 4;
            _caption.style.fontSize = 12;
            _caption.style.color = new Color(1f, 1f, 1f, 0.8f);
            Add(_caption);
        }

        public void SetCaption(string text) => _caption.text = text;

        public void Clear()
        {
            Points.Clear();
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width < 20 || r.height < 20) return;
            var p = ctx.painter2D;
            float left = 40, right = 40, top = 22, bottom = 20;
            float w = r.width - left - right, h = r.height - top - bottom;
            double maxT = 50, maxP = 50;
            foreach (var pt in Points)
            {
                if (pt.torqueNm > maxT) maxT = pt.torqueNm;
                if (pt.powerW / HpW > maxP) maxP = pt.powerW / HpW;
            }
            maxT *= 1.15;
            maxP *= 1.15;
            p.lineWidth = 1;
            p.strokeColor = Axis;
            p.BeginPath();
            p.MoveTo(new Vector2(left, top));
            p.LineTo(new Vector2(left, top + h));
            p.LineTo(new Vector2(left + w, top + h));
            p.LineTo(new Vector2(left + w, top));
            p.Stroke();
            for (int rpm = 1000; rpm < MaxRpm; rpm += 1000)
            {
                float x = left + (float)(rpm / MaxRpm) * w;
                p.BeginPath();
                p.MoveTo(new Vector2(x, top + h));
                p.LineTo(new Vector2(x, top + h - 4));
                p.Stroke();
            }
            if (Points.Count == 0) return;
            DrawSeries(p, left, top, w, h, Torque, maxT, pt => pt.torqueNm);
            DrawSeries(p, left, top, w, h, Power, maxP, pt => pt.powerW / HpW);
        }

        void DrawSeries(Painter2D p, float left, float top, float w, float h, Color c, double max, System.Func<DynoSweep.Point, double> f)
        {
            p.strokeColor = c;
            p.lineWidth = 2;
            p.BeginPath();
            for (int i = 0; i < Points.Count; i++)
            {
                var v = new Vector2(left + (float)(Points[i].rpm / MaxRpm) * w, top + h - (float)(f(Points[i]) / max) * h);
                if (i == 0) p.MoveTo(v);
                else p.LineTo(v);
            }
            p.Stroke();
            p.fillColor = c;
            foreach (var pt in Points)
            {
                var v = new Vector2(left + (float)(pt.rpm / MaxRpm) * w, top + h - (float)(f(pt) / max) * h);
                p.BeginPath();
                p.Arc(v, 3, Angle.Degrees(0), Angle.Degrees(360));
                p.Fill();
            }
        }
    }
}
