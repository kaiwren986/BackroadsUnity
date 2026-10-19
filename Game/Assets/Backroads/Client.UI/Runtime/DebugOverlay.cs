using System;
using System.Text;
using Backroads.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>What the F3 overlay shows for the player's engine (engine-sim stats; lane V fills it in).</summary>
    public struct EngineDebugStats
    {
        public string Engine;
        public float Rpm, TorqueNm, MapKPa, Afr, WorkerMs, RingFill;
        public long Underruns, CoastEvents;
    }

    /// <summary>
    /// T2.7 F3 overlay (PLAN 2.2 DebugOverlay): frame time (p50 / p95 over the last 2 s), simulation main-thread time per
    /// frame (SimTiming), camera and quality, the player's vehicle (HUD values and the DriveInput sent), the ground
    /// deformation counters, and the engine-sim stats (rpm, torque, MAP, AFR, worker ms, ring fill, underruns, coast
    /// events) from EngineStatsSource, blank until the engine lane sets it. Refreshed four times a second.
    /// </summary>
    public sealed class DebugOverlay
    {
        /// <summary>Set by the engine-sim side (lane V) to show the player's engine; null leaves the engine fields blank.</summary>
        public static Func<EngineDebugStats?> EngineStatsSource;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => EngineStatsSource = null;

        VisualElement _panel;
        Label _text;
        readonly float[] _frames = new float[240];
        readonly float[] _sims = new float[240];
        readonly float[] _sorted = new float[240];
        int _n, _i;
        float _next;
        readonly StringBuilder _sb = new StringBuilder(1024);

        public bool Visible => Ui.IsShown(_panel);
        public string Text => _text?.text;

        public VisualElement Build()
        {
            _panel = new VisualElement { name = "debug-overlay", pickingMode = PickingMode.Ignore };
            var s = _panel.style;
            s.position = Position.Absolute; s.left = 12; s.top = 12;
            s.paddingLeft = 10; s.paddingRight = 10; s.paddingTop = 6; s.paddingBottom = 6;
            s.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            s.display = DisplayStyle.None;
            _text = Ui.Text("", 13, new Color(0.85f, 1f, 0.85f));
            _text.style.unityFontStyleAndWeight = FontStyle.Normal;
            _panel.Add(_text);
            return _panel;
        }

        public void Toggle() { Ui.Show(_panel, !Visible); _next = 0; }

        /// <summary>Records this frame; redraws the text when visible (every 0.25 s).</summary>
        public void Tick()
        {
            _frames[_i] = Time.unscaledDeltaTime * 1000f;
            _sims[_i] = SimTiming.LastFrameMs;
            _i = (_i + 1) % _frames.Length;
            if (_n < _frames.Length) _n++;
            if (!Visible || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            _text.text = Compose();
        }

        float Pct(float[] src, float p)
        {
            if (_n == 0) return 0f;
            Array.Copy(src, _sorted, _n);
            Array.Sort(_sorted, 0, _n);
            return _sorted[Mathf.Clamp(Mathf.CeilToInt(p / 100f * _n) - 1, 0, _n - 1)];
        }

        string Compose()
        {
            var sb = _sb;
            sb.Clear();
            float p50 = Pct(_frames, 50), p95 = Pct(_frames, 95);
            sb.Append("FRAME  p50 ").Append(p50.ToString("F1")).Append(" ms  p95 ").Append(p95.ToString("F1")).Append(" ms  (")
              .Append((1000f / Mathf.Max(0.01f, p50)).ToString("F0")).Append(" fps)\n");
            sb.Append("SIM    ").Append(SimTiming.LastFrameMs.ToString("F2")).Append(" ms this frame, ").Append(SimTiming.LastFrameSteps)
              .Append(" fixed steps, p95 ").Append(Pct(_sims, 95).ToString("F2")).Append(" ms\n");
            var dir = CameraDirector.Main;
            sb.Append("CAMERA ").Append(dir != null ? dir.Current.ToString() : "-").Append("   QUALITY ").Append(QualityPreset.Tier).Append('\n');
            var v = PlayerVehicle.Current;
            if (v != null)
            {
                var h = v.Hud;
                sb.Append("VEHICLE ").Append(v.DisplayName).Append("  ").Append(h.SpeedKmh.ToString("F1")).Append(" km/h  gear ").Append(h.Gear)
                  .Append(h.LowRange ? " LOW" : "").Append("  rpm ").Append(h.Rpm.ToString("F0")).Append(" / ").Append(h.RedlineRpm.ToString("F0"))
                  .Append("  ").Append(h.Surface).Append("  sunk ").Append(h.SunkCm.ToString("F1")).Append(" cm\n");
                var ci = ClientInput.Current;
                if (ci != null)
                {
                    var d = ci.LastDrive;
                    sb.Append("INPUT  throttle ").Append(d.Throttle.ToString("F2")).Append("  brake ").Append(d.Brake.ToString("F2"))
                      .Append("  steer ").Append(d.Steer.ToString("F2")).Append(d.Handbrake ? "  handbrake" : "").Append(d.StarterHold ? "  STARTER" : "")
                      .Append("  ignition presses ").Append(d.IgnitionPresses).Append('\n');
                }
            }
            else sb.Append("VEHICLE none\n");
            var e = EngineStatsSource?.Invoke();
            if (e.HasValue)
            {
                var x = e.Value;
                sb.Append("ENGINE ").Append(x.Engine).Append("  rpm ").Append(x.Rpm.ToString("F0")).Append("  torque ").Append(x.TorqueNm.ToString("F0"))
                  .Append(" N m  MAP ").Append(x.MapKPa.ToString("F0")).Append(" kPa  AFR ").Append(x.Afr.ToString("F1")).Append('\n')
                  .Append("       worker ").Append(x.WorkerMs.ToString("F2")).Append(" ms  ring ").Append((x.RingFill * 100f).ToString("F0"))
                  .Append(" %  underruns ").Append(x.Underruns).Append("  coast events ").Append(x.CoastEvents).Append('\n');
            }
            else sb.Append("ENGINE rpm -  torque -  MAP -  AFR -  worker -  ring -  underruns -  coast -\n");
            var w = MillbrookWorld.Current;
            if (w != null && w.Deform != null)
            {
                sb.Append("GROUND ").Append(w.Deform.StampsApplied).Append(" stamps (").Append(w.Deform.TotalStampMs.ToString("F1")).Append(" ms)  ")
                  .Append(w.DirtyRectsLastFrame).Append(" rects this frame  ").Append(w.Terrain.PartialUpdates).Append(" chunk updates  ")
                  .Append(w.Vegetation.Resnapped).Append(" trees moved");
            }
            return sb.ToString();
        }
    }
}
