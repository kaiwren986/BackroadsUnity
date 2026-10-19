using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Backroads.Vehicles.Reference
{
    /// <summary>
    /// The M2 headline numbers of Vehicle.GoldenTraces (PLAN 6.1 M2, 03 section 3.6), computed the same way from the
    /// oracle's samples and from a C# replay (0.1 s samples, linear interpolation), plus a per-second comparison.
    /// </summary>
    public static class GoldenTraceGates
    {
        public sealed class Gate
        {
            public string trace, name, unit;
            public double expected, measured, tolerance;
            /// <summary>True: |measured - expected| &lt;= tolerance * |expected|; false: a pass/fail check (expected 1 = pass).</summary>
            public bool relative;
            public bool pass;
            public string note;

            public double DeltaPercent => relative && expected != 0 ? (measured - expected) / Math.Abs(expected) * 100 : double.NaN;

            public override string ToString()
            {
                var ci = CultureInfo.InvariantCulture;
                return relative
                    ? string.Format(ci, "{0} {1}: oracle {2:F3} {3}, C# {4:F3} {3} ({5:+0.0;-0.0} %, tolerance +-{6:0} %) {7}{8}",
                        trace, name, expected, unit, measured, DeltaPercent, tolerance * 100, pass ? "PASS" : "MISS", string.IsNullOrEmpty(note) ? "" : " - " + note)
                    : string.Format(ci, "{0} {1}: {2}{3}", trace, name, pass ? "PASS" : "FAIL", string.IsNullOrEmpty(note) ? "" : " - " + note);
            }
        }

        /// <summary>The gate for one trace id (null if the id has no gate).</summary>
        public static Gate Evaluate(GoldenTraceInput input, GoldenTraceSamples oracle, GoldenTraceSamples run)
        {
            switch (input.id)
            {
                case "truck_0_80_route9": return SpeedTime(input, oracle, run, 80);
                case "pickup_0_100_route9": return SpeedTime(input, oracle, run, 100);
                case "pickup_brake_80":
                {
                    int brakeStep = Array.FindIndex(input.brake, b => b > 0);
                    double eo = BrakeDistance(oracle, brakeStep, out double vo), er = BrakeDistance(run, brakeStep, out double vr);
                    return Rel(input.id, "80-0 km/h braking distance", "m", eo, er, 0.08,
                        string.Format(CultureInfo.InvariantCulture, "speed at brake onset: oracle {0:F1} km/h, C# {1:F1} km/h", vo * 3.6, vr * 3.6));
                }
                case "truck_quarry_low":
                {
                    double gain = oracle.At(oracle.rows.Count - 1, GoldenTraceSamples.Py) - oracle.At(0, GoldenTraceSamples.Py);
                    double target = 0.9 * gain;
                    double to = ClimbTime(oracle, target), tr = ClimbTime(run, target);
                    return Rel(input.id, $"low-range climb time to +{target:F2} m", "s", to, tr, 0.10,
                        string.Format(CultureInfo.InvariantCulture, "unloaded, as recorded; C# low range {0}, gear {1} at the end",
                            run.At(run.rows.Count - 1, GoldenTraceSamples.LowRange) > 0 ? "on" : "OFF", run.At(run.rows.Count - 1, GoldenTraceSamples.Gear)));
                }
                case "hatch_bog_10s":
                    return Rel(input.id, "sink depth at 10 s", "m", oracle.At(oracle.rows.Count - 1, GoldenTraceSamples.MudSink), run.At(run.rows.Count - 1, GoldenTraceSamples.MudSink), 0.15,
                        string.Format(CultureInfo.InvariantCulture, "stuck time at 10 s: oracle {0:F2} s, C# {1:F2} s", oracle.At(oracle.rows.Count - 1, GoldenTraceSamples.StuckT), run.At(run.rows.Count - 1, GoldenTraceSamples.StuckT)));
                case "winch_15m_tree":
                {
                    var g = new Gate { trace = input.id, name = "winch 15 m to a tree completes", relative = false, expected = 1 };
                    var ci = CultureInfo.InvariantCulture;
                    bool hooked = run.winchHookStep >= 0;
                    double anchorErr = hooked ? Math.Sqrt(Sq(run.winchAnchorX - oracle.winchAnchorX) + Sq(run.winchAnchorZ - oracle.winchAnchorZ)) : double.NaN;
                    double moved = run.PathLength(0, run.rows.Count - 1), movedO = oracle.PathLength(0, oracle.rows.Count - 1);
                    bool sameTree = hooked && anchorErr < 1.0;
                    bool reeled = !double.IsNaN(run.finalWinchLen) && run.finalWinchLen <= 6.0;
                    g.pass = hooked && sameTree && reeled && moved >= 10;
                    g.measured = g.pass ? 1 : 0;
                    g.note = string.Format(ci,
                        "hooked {0} (anchor {1:F2} m from the oracle's, rope {2:F2} m vs {3:F2} m), final rope {4:F2} m vs oracle {5:F2} m ({6:+0.0;-0.0} %), moved {7:F1} m vs {8:F1} m",
                        hooked ? "at step " + run.winchHookStep : "NO", anchorErr, run.winchLenAtHook, oracle.winchLenAtHook,
                        run.finalWinchLen, oracle.finalWinchLen, (run.finalWinchLen - oracle.finalWinchLen) / oracle.finalWinchLen * 100, moved, movedO);
                    return g;
                }
            }
            return null;
        }

        /// <summary>
        /// 0-to-target time. The trace lifts the throttle at the step where the speed reaches the target, so the 0.1 s
        /// samples peak just under it (79.98, 99.98 km/h): the time is taken where the samples cross 99 % of the
        /// target (79.2, 99 km/h), linearly interpolated, the same way for the oracle and for C#.
        /// </summary>
        static Gate SpeedTime(GoldenTraceInput input, GoldenTraceSamples oracle, GoldenTraceSamples run, double kmh)
        {
            double at = 0.99 * kmh;
            int lift = Array.FindLastIndex(input.throttle, t => t > 0) + 1;
            var g = Rel(input.id, $"0-{kmh:0} km/h time", "s", oracle.CrossingTime(GoldenTraceSamples.Speed, at, 3.6), run.CrossingTime(GoldenTraceSamples.Speed, at, 3.6), 0.07,
                string.Format(CultureInfo.InvariantCulture, "crossing of {0:F1} km/h in the 0.1 s samples; the oracle reached {1:0} km/h and lifted at step {2} ({3:F2} s); {4}", at, kmh, lift, lift * BrvSim.STEP, MaxKmh(run)));
            return g;
        }

        static Gate Rel(string trace, string name, string unit, double expected, double measured, double tol, string note)
        {
            var g = new Gate { trace = trace, name = name, unit = unit, expected = expected, measured = measured, tolerance = tol, relative = true, note = note };
            g.pass = !double.IsNaN(measured) && !double.IsNaN(expected) && Math.Abs(measured - expected) <= tol * Math.Abs(expected);
            return g;
        }

        static string MaxKmh(GoldenTraceSamples s)
        {
            double m = 0;
            foreach (var r in s.rows) m = Math.Max(m, r[GoldenTraceSamples.Speed] * 3.6);
            return string.Format(CultureInfo.InvariantCulture, "C# top speed {0:F1} km/h", m);
        }

        /// <summary>Path length from the first sample at or after the brake onset to the first sample below 0.3 m/s.</summary>
        public static double BrakeDistance(GoldenTraceSamples s, int brakeStep, out double speedAtOnset)
        {
            int a = s.RowAtOrAfter(brakeStep);
            speedAtOnset = s.At(a, GoldenTraceSamples.Speed);
            int b = a;
            while (b < s.rows.Count - 1 && s.At(b, GoldenTraceSamples.Speed) >= 0.3) b++;
            return s.PathLength(a, b);
        }

        /// <summary>First time the vehicle is <paramref name="gain"/> metres above its start height.</summary>
        public static double ClimbTime(GoldenTraceSamples s, double gain)
        {
            double y0 = s.At(0, GoldenTraceSamples.Py);
            for (int i = 1; i < s.rows.Count; i++)
            {
                double a = s.At(i - 1, GoldenTraceSamples.Py) - y0, b = s.At(i, GoldenTraceSamples.Py) - y0;
                if (a < gain && b >= gain) return s.At(i - 1, GoldenTraceSamples.T) + (s.At(i, GoldenTraceSamples.T) - s.At(i - 1, GoldenTraceSamples.T)) * (gain - a) / (b - a);
            }
            return double.NaN;
        }

        static double Sq(double x) => x * x;

        /// <summary>Per-second statistics (03 section 3.6): speed, rpm, gear, sink and position, oracle vs C#.</summary>
        public static string PerSecond(GoldenTraceSamples oracle, GoldenTraceSamples run, out double meanAbsKmh, out double maxAbsKmh, out double gearMatch, out double maxPosErr)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("|  t s | km/h oracle | km/h C# | rpm oracle | rpm C# | gear o/C# | sink cm o/C# | pos err m |");
            sb.AppendLine("|---:|---:|---:|---:|---:|:---:|:---:|---:|");
            double sum = 0; int n = 0, match = 0; maxAbsKmh = 0;
            int rows = Math.Min(oracle.rows.Count, run.rows.Count);
            maxPosErr = 0;
            for (int i = 0; i < rows; i++)
            {
                var o = oracle.rows[i]; var r = run.rows[i];
                maxPosErr = Math.Max(maxPosErr, Math.Sqrt(Sq(o[GoldenTraceSamples.Px] - r[GoldenTraceSamples.Px]) + Sq(o[GoldenTraceSamples.Py] - r[GoldenTraceSamples.Py]) + Sq(o[GoldenTraceSamples.Pz] - r[GoldenTraceSamples.Pz])));
                if (((int)o[GoldenTraceSamples.Step]) % 120 != 0) continue;
                double dk = Math.Abs(o[GoldenTraceSamples.Speed] - r[GoldenTraceSamples.Speed]) * 3.6;
                sum += dk; n++; maxAbsKmh = Math.Max(maxAbsKmh, dk);
                if ((int)o[GoldenTraceSamples.Gear] == (int)r[GoldenTraceSamples.Gear]) match++;
                double pe = Math.Sqrt(Sq(o[GoldenTraceSamples.Px] - r[GoldenTraceSamples.Px]) + Sq(o[GoldenTraceSamples.Py] - r[GoldenTraceSamples.Py]) + Sq(o[GoldenTraceSamples.Pz] - r[GoldenTraceSamples.Pz]));
                sb.AppendLine(string.Format(ci, "| {0,4:F0} | {1:F1} | {2:F1} | {3:F0} | {4:F0} | {5}/{6} | {7:F1}/{8:F1} | {9:F2} |",
                    o[GoldenTraceSamples.T], o[GoldenTraceSamples.Speed] * 3.6, r[GoldenTraceSamples.Speed] * 3.6, o[GoldenTraceSamples.Rpm], r[GoldenTraceSamples.Rpm],
                    o[GoldenTraceSamples.Gear], r[GoldenTraceSamples.Gear], o[GoldenTraceSamples.MudSink] * 100, r[GoldenTraceSamples.MudSink] * 100, pe));
            }
            meanAbsKmh = n > 0 ? sum / n : double.NaN;
            gearMatch = n > 0 ? (double)match / n : double.NaN;
            return sb.ToString();
        }
    }
}
