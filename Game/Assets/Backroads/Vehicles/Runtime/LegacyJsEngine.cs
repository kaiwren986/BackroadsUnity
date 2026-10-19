using System;

namespace Backroads.Vehicles
{
    /// <summary>
    /// The browser game's engine and automatic gearbox (vehicle.js engineForce, torqueCurve), unchanged: a torque curve
    /// scaled by the throttle, the clutch-slip rpm in first and reverse, shifts on ground speed (not on spinning wheels),
    /// low range x2.6 on every ratio with the top gear capped at 2, the rev limiter on road speed, engine braking and
    /// the submerged cut-out. In M2 its rpm also drives engine-sim (FOLLOW_RPM, PLAN 3.5) through IVehicleEngine.
    /// </summary>
    public static class LegacyJsEngine
    {
        public const double LowRangeFactor = 2.6;
        public const double DrivelineEfficiency = 0.86;

        /// <summary>torqueCurve(x), x = rpm / redline.</summary>
        public static double TorqueCurve(double x) =>
            x < 0.12 ? 0.62 : x < 0.62 ? 0.62 + 0.38 * (x - 0.12) / 0.5 : x <= 1.0 ? 1 - 0.32 * (x - 0.62) / 0.38 : 0;

        /// <summary>The overall ratio of the current gear (reverse for gear &lt; 0), low range included.</summary>
        public static double Ratio(in BrvState s, in BrvDef d)
        {
            double lr = s.lowRange ? LowRangeFactor : 1;
            return (s.gear > 0 ? d.ratios[s.gear - 1] : d.revRatio) * lr;
        }

        /// <summary>engineForce(dt): the drive force at the tyres (signed for reverse); updates gear, rpm, omegaMax, engineBrake.</summary>
        public static double EngineForce(ref BrvState s, in BrvDef d, double dt)
        {
            if (d.simple) return 0;
            double om = 0; int n = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var l = d.wheels[i];
                if (l.drive || (s.lowRange && d.awdOnLow)) { om += s.wheels[i].omega; n++; }
            }
            om = n > 0 ? om / n : 0;
            if (s.shiftT > 0) s.shiftT -= dt;
            // low range multiplies every gear: crawling speed, lots of torque at the wheels
            double lr = s.lowRange ? LowRangeFactor : 1;
            double ratio = (s.gear > 0 ? d.ratios[s.gear - 1] : d.revRatio) * lr;
            double wheelRpm = Math.Abs(om) * ratio * 30 / Math.PI;
            // auto gearbox: shift on how fast the truck is actually going, not on spinning wheels,
            // or wheelspin in mud makes it hunt between gears and lose drive on every shift
            if (s.gear > 0 && s.shiftT <= 0)
            {
                double groundOm = Math.Abs(s.fwdSpeed) / d.wheelR;
                double groundRpm = groundOm * ratio * 30 / Math.PI;
                int top = s.lowRange ? Math.Min(2, d.ratios.Length) : d.ratios.Length;
                if (s.gear > top) { s.gear = top; s.shiftT = 0.18; }
                else if (groundRpm > d.eRedline * 0.9 && s.gear < top && s.ctrl.throttle > 0.1)
                {
                    s.gear++; s.shiftT = 0.25;
                }
                else if (s.gear > 1)
                {
                    double lowerRpm = groundOm * d.ratios[s.gear - 2] * lr * 30 / Math.PI;
                    if (lowerRpm < d.eRedline * (s.ctrl.throttle > 0.6 ? 0.8 : 0.62) && groundRpm < d.eRedline * 0.5) { s.gear--; s.shiftT = 0.18; }
                }
            }
            double thr = s.engineOn ? s.ctrl.throttle : 0;
            double clutchRpm = d.eIdle + thr * (d.eRedline * 0.55 - d.eIdle);
            bool low = s.gear == 1 || s.gear == -1;
            double eng = low ? Math.Max(wheelRpm, clutchRpm) : Math.Max(wheelRpm, d.eIdle);
            eng = BrvSim.Clamp(eng, d.eIdle, d.eRedline * 1.04);
            s.rpm += (eng - s.rpm) * Math.Min(1, dt * 14);
            if (!s.engineOn) s.rpm = 0;
            double T = d.eTorque * TorqueCurve(Math.Min(1, eng / d.eRedline)) * thr;
            s.omegaMax = (d.eRedline * Math.PI) / 30 / ratio;
            // rev limiter on road speed: wheelspin revs the engine but doesn't cut the drive
            if ((Math.Abs(s.fwdSpeed) / d.wheelR) * ratio * 30 / Math.PI > d.eRedline) T = 0;
            if (s.shiftT > 0) T *= 0.1;
            if (s.inWater > 0.65) T = 0;
            double F = (T * ratio * DrivelineEfficiency) / d.wheelR;
            s.engineBrake = thr < 0.05 && s.engineOn ? (d.eTorque * 0.22 * ratio * (eng / d.eRedline)) / d.wheelR : 0;
            return s.gear < 0 ? -F : F;
        }
    }
}
