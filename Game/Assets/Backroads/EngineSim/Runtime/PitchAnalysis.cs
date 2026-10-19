using System;

namespace Backroads.EngineSim
{
    /// <summary>
    /// The PLAN 1.9 pitch rule [T12], the same method as the native harness (tools/harness/harness.cpp checkPitch): a
    /// window passes if its autocorrelation period equals k firing intervals (k in {1, 2, 4, 8}, i.e. one of the engine's
    /// pitchOrders as a multiple of the cycle frequency rpm/120), or if its strongest spectral peak (20-1000 Hz) sits on
    /// one of those orders (any integer cycle harmonic when pitchOrders is empty). Tolerance is relative (0.03).
    /// </summary>
    public static class PitchAnalysis
    {
        public struct Result
        {
            public double rpm, cycleHz, acfHz, acfOrder, peakHz, peakOrder;
            public bool acfPass, peakPass;
            public bool Pass => acfPass || peakPass;

            public override string ToString() =>
                $"rpm {rpm:F0}: acf {acfHz:F1} Hz (order {acfOrder:F2}) {(acfPass ? "ok" : "--")}, peak {peakHz:F1} Hz (order {peakOrder:F2}) {(peakPass ? "ok" : "--")}";
        }

        /// <summary>x: mono samples (or one channel); offset/count: the window.</summary>
        public static Result Check(float[] x, int offset, int count, int stride, int sampleRate, double rpm, float[] orders, double tol = 0.03)
        {
            var r = new Result { rpm = rpm, cycleHz = rpm / 120.0 };
            if (count < 1024 || sampleRate <= 0 || rpm <= 0) return r;

            int d = Math.Max(1, sampleRate / 12000);           // decimate to about 12 kHz (the signal is below 2 kHz)
            double r2 = (double)sampleRate / d;
            int m = count / d;
            var y = new double[m];
            double mean = 0;
            for (int i = 0; i < m; i++)
            {
                double s = 0;
                for (int k = 0; k < d; k++) s += x[(offset + i * d + k) * stride];
                y[i] = s / d;
                mean += y[i];
            }
            mean /= m;
            for (int i = 0; i < m; i++) y[i] -= mean;

            int lagMin = Math.Max(2, (int)(r2 / 1000.0));
            int lagMax = Math.Min(m / 2, (int)(r2 / 20.0));
            var ac = new double[lagMax + 2];
            for (int lag = 1; lag <= lagMax + 1; lag++)
            {
                double s = 0, e0 = 0, e1 = 0;
                int len = m - lag;
                for (int i = 0; i < len; i++)
                {
                    s += y[i] * y[i + lag];
                    e0 += y[i] * y[i];
                    e1 += y[i + lag] * y[i + lag];
                }
                ac[lag] = e0 > 0 && e1 > 0 ? s / Math.Sqrt(e0 * e1) : 0;
            }
            int start = lagMin;
            while (start < lagMax && ac[start] > 0) start++;
            int best = -1;
            double bestV = -2;
            for (int lag = start; lag <= lagMax; lag++)
                if (ac[lag] > bestV) { bestV = ac[lag]; best = lag; }
            if (best > 0)
            {
                double lag = best;
                if (best > 1 && best <= lagMax)
                {
                    double a = ac[best - 1], b = ac[best], c = ac[best + 1];
                    double den = a - 2 * b + c;
                    if (den < 0) lag += 0.5 * (a - c) / den;
                }
                r.acfHz = r2 / lag;
            }

            // spectral peak 20-1000 Hz, Hann window, 0.5 Hz steps (Goertzel-style direct DFT)
            double bestP = 0;
            var win = new double[m];
            for (int i = 0; i < m; i++) win[i] = y[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (m - 1)));
            for (double f = 20.0; f <= 1000.0; f += 0.5)
            {
                double w = 2 * Math.Cos(2 * Math.PI * f / r2), s1 = 0, s2 = 0;
                for (int i = 0; i < m; i++)
                {
                    double s0 = win[i] + w * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }
                double p = s1 * s1 + s2 * s2 - w * s1 * s2;
                if (p > bestP) { bestP = p; r.peakHz = f; }
            }

            r.acfOrder = r.acfHz / r.cycleHz;
            r.peakOrder = r.peakHz / r.cycleHz;
            if (orders == null || orders.Length == 0)
            {
                double h = Math.Round(r.peakOrder);
                r.peakPass = h >= 1 && Near(r.peakOrder, h, tol);
                r.acfPass = Near(r.acfOrder, Math.Round(r.acfOrder), tol) && r.acfOrder >= 0.5;
            }
            else
            {
                foreach (var o in orders)
                {
                    if (Near(r.acfOrder, o, tol)) r.acfPass = true;
                    if (Near(r.peakOrder, o, tol)) r.peakPass = true;
                }
            }
            return r;
        }

        static bool Near(double v, double want, double tol) => want > 0 && Math.Abs(v - want) <= tol * want;

        public static double RmsDb(float[] x, int offset, int count, int stride)
        {
            double s = 0;
            for (int i = 0; i < count; i++) { double v = x[(offset + i) * stride]; s += v * v; }
            double rms = count > 0 ? Math.Sqrt(s / count) : 0;
            return rms > 0 ? 20 * Math.Log10(rms) : -200;
        }

        public static double ClipFraction(float[] x, int count, float limit = 0.999f)
        {
            int c = 0;
            for (int i = 0; i < count; i++) if (Math.Abs(x[i]) >= limit) c++;
            return count > 0 ? (double)c / count : 0;
        }
    }
}
