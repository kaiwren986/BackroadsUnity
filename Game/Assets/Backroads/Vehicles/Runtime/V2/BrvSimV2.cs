using System;
using System.Runtime.CompilerServices;
using Backroads.World;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// BRV v2 core (card T3.5, behind <see cref="BrvFeatures.V2Driveline"/>; research 06 sections 3.4-3.8 and 4.1-4.2).
    /// Same rigid body, suspension rays, hull contacts, statics, winch, water and ruts as V0, but every rotating part is
    /// a shaft in one projected Gauss-Seidel solver run in 4 substeps of the 1/120 s step:
    /// <list type="bullet">
    /// <item>shafts: crank, gearbox output (the input lumped in while a gear is engaged), one per wheel (one per axle
    /// while its locker is on);</item>
    /// <item>rows: auto-clutch (bounded by its capacity times the engagement), open axle diffs with the final drive,
    /// brake + rolling resistance per wheel (bounded, with ABS), MF secant tyre rows (long: chassis and wheel shaft;
    /// lat: chassis) with a friction ellipse, the JS mud drag;</item>
    /// <item>each wheel's brake and tyre-long rows are solved together as one exact 2x2 block (the wheel's small inertia
    /// sits between the heavy chassis and the brake, which plain PGS converges on very slowly);</item>
    /// <item>at walking pace the tyre rows turn rigid and low-speed anchors (tyre deflection, brake slip angle) remove
    /// the solver's residual creep, so a vehicle holds on a 35 % slope.</item>
    /// </list>
    /// The engine is a torque map on a real crank (the JS torque curve, idle governor, soft limiter, engine drag) until
    /// engine-sim's indicated torque replaces it in M4; the gearbox is the JS automatic (shift on ground speed) as an AMT
    /// (fuel cut and rev-matching while the clutch is open). Driver aids: ABS (implicit, on by default) and crawl control
    /// in low range (driven-wheel slip capped at the tyre's peak by the brake, engine torque cut while a wheel slips).
    /// JS world space and doubles, like V0; Burst-compatible (stack scratch, no allocations).
    /// </summary>
    public static partial class BrvSim
    {
        const int ShCrank = 0, ShDrive = 1;
        const int V2MaxShafts = 2 + BrvDef.MaxWheels;
        const int V2MaxRows = 4 + 4 * BrvDef.MaxWheels;
        /// <summary>Anchor time constant (s): how fast the low-speed anchors pull residual creep back.</summary>
        const double AnchorTau = 0.08;
        const double AnchorMax = 0.05, AnchorBrakeMax = 0.25;

        struct V2Row
        {
            public double3 lin, ang, iang;
            public bool chassis;
            public int ns, s0, s1, s2;
            public double j0, j1, j2;
            public double bias, goh, lo, hi, k, lambda;
        }

        struct V2Wheel
        {
            public bool contact, bottomed, drive, hand;
            public int shaft;
            public double r, muBase, crr, mudCoef;
            public double3 arm, fd, sd, rS;
            public int rB, rT, rL, rM;
            public double f;          // low-speed anchor weight of this substep
            public bool abs;          // ABS is modulating this wheel's brake in this substep
            public double brakeMax;   // the foot brake's impulse bound in this substep (ABS diagnostics)
        }

        /// <summary>ABS target: this fraction of the tyre's peak slip (just under the peak, where the curve is flat).</summary>
        const double AbsSlip = 0.9;

        /// <summary>The overall JS ratio of a gear (low range included), as LegacyJsEngine.Ratio.</summary>
        static double JsRatio(in BrvState s, in BrvDef d, int gear)
        {
            double lr = s.lowRange ? LegacyJsEngine.LowRangeFactor : 1;
            return (gear > 0 ? d.ratios[gear - 1] : d.revRatio) * lr;
        }

        /// <summary>The v2 gearbox ratio relative to the final drive (sign = direction, low range included).</summary>
        public static double V2Ratio(in BrvState s, in BrvDef d)
        {
            var v = d.v2def;
            double lr = s.lowRange ? v.lowRange : 1;
            int g = s.gear;
            if (g > v.gearRatios.Length) g = v.gearRatios.Length;
            return (g > 0 ? v.gearRatios[g - 1] : g < 0 ? -v.reverseRatio : 0) * lr;
        }

        static bool V2FrontDriven(in BrvState s, in BrvDef d) => d.drive == BrvDef.DriveFwd || d.drive == BrvDef.DriveAwd || (s.lowRange && d.awdOnLow);
        static bool V2RearDriven(in BrvDef d) => d.drive == BrvDef.DriveRwd || d.drive == BrvDef.DriveAwd;

        /// <summary>Brings the v2 driveline block in line with the rest of the state (first v2 step, or after a V0/v1 state).</summary>
        public static void V2Sync(ref BrvState s, in BrvDef d)
        {
            var v = d.v2def;
            double om = 0; int n = 0;
            bool front = V2FrontDriven(s, d), rear = V2RearDriven(d);
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var l = d.wheels[i];
                if ((l.front && front) || (!l.front && rear)) { om += s.wheels[i].omega; n++; }
            }
            s.dl.driveOmega = n > 0 ? om / n * v.finalDrive : 0;
            double idle = v.idleRpm * Math.PI / 30;
            double eng = s.rpm * Math.PI / 30;
            s.dl.crankOmega = s.engineOn ? Math.Max(eng, idle) : eng;
            s.dl.clutch = 0;
            s.dl.lamClutch = s.dl.lamDiffFront = s.dl.lamDiffRear = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                ref var wh = ref s.wheels.ElementAt(i);
                wh.lamB = wh.lamT = wh.lamL = wh.lamM = 0;
                wh.anchorL = wh.anchorS = wh.anchorB = 0;
                wh.absCut = 0;
            }
            s.dl.init = true;
        }

        /// <summary>
        /// Test and tool helper: the vehicle rolls straight ahead at <paramref name="speed"/> m/s (body velocity along its
        /// forward axis, wheels turning at speed / r, the gear the JS automatic would hold, engine and v2 driveline matched).
        /// </summary>
        public static void SetRolling(ref BrvState s, in BrvDef d, double speed)
        {
            UpdateBasis(ref s);
            s.vel = s.ez * speed;
            s.angVel = 0;
            s.asleep = false;
            s.sleepT = 0;
            s.fwdSpeed = speed;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                ref var wh = ref s.wheels.ElementAt(i);
                wh.omega = speed / d.wheels[i].r;
            }
            if (!d.hasEngine || d.simple) return;
            double groundOm = Math.Abs(speed) / d.wheelR;
            int g = 1;
            if (speed >= 0)
            {
                int top = s.lowRange ? Math.Min(2, d.ratios.Length) : d.ratios.Length;
                while (g < top && groundOm * JsRatio(s, d, g) * 30 / Math.PI > d.eRedline * 0.9) g++;
            }
            else g = -1;
            s.gear = g;
            s.shiftT = 0;
            s.rpm = Math.Max(d.eIdle, groundOm * JsRatio(s, d, g) * 30 / Math.PI);
            if (!d.v2) return;
            V2Sync(ref s, d);
            s.dl.driveOmega = speed / d.wheelR * d.v2def.finalDrive;
            double R = V2Ratio(s, d);
            s.dl.crankOmega = Math.Max(d.v2def.idleRpm * Math.PI / 30, R * s.dl.driveOmega);
            s.dl.clutch = 1;
        }

        /// <summary>step(dt) for a BRV v2 vehicle (same signature and outputs as <see cref="Step"/>).</summary>
        public static unsafe void StepV2(ref BrvState s, in BrvDef d, in BrvWorld w, double dt, int self,
                                         ref NativeList<RutCall> ruts, ref NativeList<TreeKnock> knocks,
                                         NativeArray<BrvState> fleet, int fleetCount)
        {
            if (s.asleep) return;
            s.steps++;
            s.prevPos = s.pos;
            s.prevQuat = s.quat;
            UpdateBasis(ref s);
            s.fwdSpeed = Dot(s.vel, s.ez);
            double speedAbs = Math.Abs(s.fwdSpeed);
            s.steerAngle = -s.ctrl.steer * d.steer / (1 + speedAbs * 0.055);
            if (!s.dl.init) V2Sync(ref s, d);

            V2Gearbox(ref s, d, dt);
            Water(ref s, d, dt);

            int nw = s.wheels.Length;
            V2Wheel* ws = stackalloc V2Wheel[BrvDef.MaxWheels];
            V2Contacts(ref s, d, w, dt, ws);

            var v = d.v2def;
            int nsub = Math.Max(1, v.substeps);
            double hs = dt / nsub;
            double* om = stackalloc double[V2MaxShafts];
            double* invI = stackalloc double[V2MaxShafts];
            V2Row* rows = stackalloc V2Row[V2MaxRows];
            for (int k = 0; k < nsub; k++) V2Substep(ref s, d, hs, ws, om, invI, rows);

            V2AfterSubsteps(ref s, d, dt, ws);
            HullContacts(ref s, d, w, dt, self, ref ruts);
            Statics(ref s, d, w, self, ref knocks);
            if (s.winch.active) WinchStep(ref s, d, dt, fleet, fleetCount);
            StepTail(ref s, d, w, dt, self, ref ruts);
        }

        // ------------------------------------------------------------------ gearbox and clutch (once per step)

        static void V2Gearbox(ref BrvState s, in BrvDef d, double dt)
        {
            var v = d.v2def;
            if (s.shiftT > 0) s.shiftT -= dt;
            // the JS automatic, unchanged: shift on ground speed, not on spinning wheels (LegacyJsEngine.EngineForce)
            double lr = s.lowRange ? LegacyJsEngine.LowRangeFactor : 1;
            if (s.gear > 0 && s.shiftT <= 0)
            {
                double ratio = JsRatio(s, d, s.gear);
                double groundOm = Math.Abs(s.fwdSpeed) / d.wheelR;
                double groundRpm = groundOm * ratio * 30 / Math.PI;
                int top = s.lowRange ? Math.Min(2, d.ratios.Length) : d.ratios.Length;
                if (s.gear > top) { s.gear = top; s.shiftT = 0.18; }
                else if (groundRpm > d.eRedline * 0.9 && s.gear < top && s.ctrl.throttle > 0.1) { s.gear++; s.shiftT = 0.25; }
                else if (s.gear > 1)
                {
                    double lowerRpm = groundOm * d.ratios[s.gear - 2] * lr * 30 / Math.PI;
                    if (lowerRpm < d.eRedline * (s.ctrl.throttle > 0.6 ? 0.8 : 0.62) && groundRpm < d.eRedline * 0.5) { s.gear--; s.shiftT = 0.18; }
                }
            }
            // auto-clutch (06 section 4.2.1): engaged from just above idle up to the launch rpm, which rises with the
            // throttle (the JS clutch-slip rpm, idle .. 55 % of redline); open during a shift (torque interrupt)
            double rpm = s.dl.crankOmega * 30 / Math.PI;
            double thr = s.engineOn ? Clamp(s.ctrl.throttle, 0, 1) : 0;
            double lo = v.idleRpm + 0.04 * (v.redlineRpm - v.idleRpm);
            double hi = v.idleRpm + Math.Max(0.15, thr) * (v.redlineRpm * 0.55 - v.idleRpm);
            double target = s.engineOn ? SmoothStep(lo, Math.Max(hi, lo + 50), rpm) : 0;
            // overspeed protection: the AMT opens the clutch rather than let the wheels drag the crank past 115 % of
            // redline (rolling fast in a low gear, low range picked at speed)
            bool overspeed = Math.Abs(V2Ratio(s, d) * s.dl.driveOmega) * 30 / Math.PI > 1.15 * v.redlineRpm;
            if (overspeed) target = 0;
            double e = overspeed ? 0 : s.dl.clutch;
            // closes fast when the crank already turns with the gearbox (after a rev-matched shift), gently otherwise
            double slip = Math.Abs(s.dl.crankOmega - V2Ratio(s, d) * s.dl.driveOmega);
            double up = slip < 0.08 * Math.Max(Math.Abs(s.dl.crankOmega), 1) ? 20 : 6;
            e += Clamp(target - e, -12 * dt, up * dt);
            s.dl.clutch = s.shiftT > 0 ? 0 : Clamp(e, 0, 1);

            // crawl control in low range: cut the engine's torque while a driven wheel slips past the tyre's peak in the
            // driving direction (with an open diff the brake alone cannot hold the lighter wheel), restore it as it grips
            double worst = 0;
            if (s.lowRange && s.gear != 0)
            {
                bool front = V2FrontDriven(s, d), rear = V2RearDriven(d);
                double dir = s.gear > 0 ? 1 : -1;
                for (int i = 0; i < s.wheels.Length; i++)
                {
                    var l = d.wheels[i];
                    var wh = s.wheels[i];
                    if (wh.contact && ((l.front && front) || (!l.front && rear))) worst = Math.Max(worst, wh.kappa * dir);
                }
            }
            double over = worst / v.mfLong.peak - 1.5;
            s.dl.tcCut = over > 0 ? Math.Min(0.9, s.dl.tcCut + Math.Min(over, 4) * 2 * dt) : Math.Max(0, s.dl.tcCut - 1.5 * dt);
        }

        static double SmoothStep(double a, double b, double x)
        {
            double t = Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        // ------------------------------------------------------------------ contacts (once per step: positions are frozen)

        static unsafe void V2Contacts(ref BrvState s, in BrvDef d, in BrvWorld w, double dt, V2Wheel* ws)
        {
            var v = d.v2def;
            double3 ex = s.ex, ey = s.ey, ez = s.ez;
            double dx = -ey.x, dy = -ey.y, dz = -ey.z;
            double ri = d.rollInfluence;
            bool hand = s.ctrl.handbrake > 0.5;
            bool front = V2FrontDriven(s, d), rear = V2RearDriven(d);
            bool lumpF = s.dl.lockFront && v.lockerFront, lumpR = s.dl.lockRear && v.lockerRear;
            int shaft = 2;
            s.mudSink = 0;
            int nc = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var l = d.wheels[i];
                ref var wh = ref s.wheels.ElementAt(i);
                ref var q = ref ws[i];
                q = default;
                q.r = l.r;
                q.drive = (l.front && front) || (!l.front && rear);
                q.hand = hand && l.hand;
                q.rB = q.rT = q.rL = q.rM = -1;
                // shaft: a locked axle shares one shaft (pairs are (0,1), (2,3), ... in the vehicle.js layout)
                bool lumped = (i & 1) == 1 && i < 4 && (l.front ? lumpF : lumpR);
                q.shaft = lumped ? ws[i - 1].shaft : shaft++;

                double mx = s.pos.x + ex.x * l.mx + ey.x * l.my + ez.x * l.mz;
                double my = s.pos.y + ex.y * l.mx + ey.y * l.my + ez.y * l.mz;
                double mz = s.pos.z + ex.z * l.mx + ey.z * l.my + ez.z * l.mz;
                double maxT = l.rest + l.r + 0.04;
                double best = double.PositiveInfinity, cx = 0, cy = 0, cz = 0;
                for (int k = -1; k <= 1; k++)
                {
                    double off = k * 0.55 * l.r;
                    double ox = mx + ez.x * off, oy = my + ez.y * off, oz = mz + ez.z * off;
                    double t = RayDown(w, ox, oy, oz, dx, dy, dz, maxT);
                    if (t < 0) continue;
                    double len0 = t - Math.Sqrt(l.r * l.r - off * off);
                    if (len0 < best) { best = len0; cx = ox + dx * t; cy = oy + dy * t; cz = oz + dz * t; }
                }
                if (best > l.rest)
                {
                    wh.contact = false;
                    wh.load = 0;
                    wh.len = Math.Min(l.rest, wh.len + dt * 3);
                    wh.comp = l.rest - wh.len;
                    wh.lamT = wh.lamL = wh.lamM = 0;
                    wh.anchorL = wh.anchorS = 0;
                    continue;
                }
                nc++;
                wh.contact = true;
                q.contact = true;
                q.bottomed = best <= 0;
                double len = Math.Max(0, best);
                wh.len = len;
                wh.comp = l.rest - len;
                wh.cp = new double3(cx, cy, cz);
                wh.n = NormalAt(w, cx, cz);
                wh.surf = WorldGridQueriesRef.SurfaceAtJs(w.grid, cx, cz);

                // contact frame (as V0 tyres())
                double a = l.steer ? s.steerAngle : 0;
                double ca = Math.Cos(a), sa = Math.Sin(a);
                double fx = ez.x * ca + ex.x * sa, fy = ez.y * ca + ex.y * sa, fz = ez.z * ca + ex.z * sa;
                double3 n = wh.n;
                double fn = fx * n.x + fy * n.y + fz * n.z;
                fx -= n.x * fn; fy -= n.y * fn; fz -= n.z * fn;
                double fl = Hypot(fx, fy, fz); if (fl == 0) fl = 1;
                fx /= fl; fy /= fl; fz /= fl;
                wh.fd = new double3(fx, fy, fz);
                wh.sd = new double3(n.y * fz - n.z * fy, n.z * fx - n.x * fz, n.x * fy - n.y * fx);
                double3 arm = wh.cp - s.pos;
                wh.arm = arm;
                double up = Dot(arm, ey);
                double3 rS = arm;
                double kup = -up * (1 - ri);
                rS.x += ey.x * kup; rS.y += ey.y * kup; rS.z += ey.z * kup;
                wh.rS = rS;
                q.arm = arm; q.fd = wh.fd; q.sd = wh.sd; q.rS = rS;

                // grip of this patch: tyre class x surface, browser-game wear and water terms
                double wear = WorldGridQueriesRef.WearAtJs(w.grid, wh.cp.x, wh.cp.z) / 255.0;
                q.muBase = v.gripScale * TyreTables.SurfaceMu(wh.surf, v.tyre, d.mudGrip) * (1 - 0.18 * wear * Softness(wh.surf)) * (1 - s.inWater * 0.5);
                q.crr = TyreTables.RollingResistance(wh.surf, v.tyre);
                q.mudCoef = 0;
                if (wh.surf == SURF_MUD)
                {
                    // the JS mud resistance (sinkage drag and suction), per newton of wheel load
                    double mudDepth = WorldGridQueriesRef.MudAtJs(w.grid, wh.cp.x, wh.cp.z);
                    double sink = Math.Max(0, WorldGridQueriesRef.SinkAtJs(w.grid, wh.cp.x, wh.cp.z));
                    double sr = Clamp(sink / l.r, 0, 1.2);
                    double visc = 0.1 * mudDepth * Math.Min(Math.Abs(wh.vl), 6);
                    q.mudCoef = 0.04 + 0.12 * mudDepth + 0.3 * sr * sr + 0.12 * sr + visc;
                    if (sink > s.mudSink) s.mudSink = sink;
                }
            }
            s.nContact = Math.Max(1, nc);
        }

        // ------------------------------------------------------------------ one substep

        static unsafe void V2Substep(ref BrvState s, in BrvDef d, double hs, V2Wheel* ws, double* om, double* invI, V2Row* rows)
        {
            var v = d.v2def;
            double3 ey = s.ey;
            s.vel.y -= G * hs;

            // ---- suspension (explicit springs and dampers at the frozen compression; 06 section 3.5)
            double maxF = s.mass * G * 5;
            int nw = s.wheels.Length;
            for (int i = 0; i < nw; i++)
            {
                if (!ws[i].contact) continue;
                var l = d.wheels[i];
                ref var wh = ref s.wheels.ElementAt(i);
                double rx = wh.arm.x, ry = wh.arm.y, rz = wh.arm.z;
                double compVel = -VelDot(s, rx, ry, rz, ey.x, ey.y, ey.z);
                double F = l.k * wh.comp + l.c * compVel;
                if (F < 0) F = 0; else if (F > maxF) F = maxF;
                F *= Clamp(wh.n.x * ey.x + wh.n.y * ey.y + wh.n.z * ey.z, 0.3, 1);
                wh.load = F;
                double J = F * hs;
                Impulse(ref s, ey.x * J, ey.y * J, ey.z * J, rx, ry, rz);
                if (ws[i].bottomed)
                {
                    double vb = -VelDot(s, rx, ry, rz, ey.x, ey.y, ey.z);
                    if (vb > 0)
                    {
                        double j = vb / KAt(s, rx, ry, rz, ey.x, ey.y, ey.z) * 0.5;
                        Impulse(ref s, ey.x * j, ey.y * j, ey.z * j, rx, ry, rz);
                        wh.load += j / hs;
                        if (j > s.mass * 1.5) s.impact = Math.Max(s.impact, j);
                    }
                }
            }
            if (d.antiRoll > 0)
            {
                for (int i = 0; i + 1 < nw; i += 2)
                {
                    var wl = s.wheels[i]; var wr = s.wheels[i + 1];
                    double f = (wl.comp - wr.comp) * d.antiRoll * hs;
                    if (wl.contact) Impulse(ref s, ey.x * f, ey.y * f, ey.z * f, wl.arm.x, wl.arm.y, wl.arm.z);
                    if (wr.contact) Impulse(ref s, -ey.x * f, -ey.y * f, -ey.z * f, wr.arm.x, wr.arm.y, wr.arm.z);
                }
            }

            // ---- shafts
            double R = V2Ratio(s, d);
            int nsh = 2;
            for (int i = 0; i < nw; i++) nsh = Math.Max(nsh, ws[i].shaft + 1);
            double iDrive = v.outputInertia + v.gearboxInputInertia * R * R;
            om[ShCrank] = s.dl.crankOmega; invI[ShCrank] = 1 / v.crankInertia;
            om[ShDrive] = s.dl.driveOmega; invI[ShDrive] = 1 / iDrive;
            for (int sh = 2; sh < nsh; sh++) { om[sh] = 0; invI[sh] = 0; }
            for (int i = 0; i < nw; i++)
            {
                int sh = ws[i].shaft;
                double I = d.wheels[i].front ? v.wheelInertiaFront : v.wheelInertiaRear;
                // inertia-weighted mean when a locked axle shares the shaft (a freshly engaged locker merges momentum)
                om[sh] += s.wheels[i].omega * I;
                invI[sh] += I;
            }
            for (int sh = 2; sh < nsh; sh++) { om[sh] /= invI[sh]; invI[sh] = 1 / invI[sh]; }

            // ---- engine: torque map on the crank, explicit (06 section 3.8: external shaft torques)
            {
                double w0 = om[ShCrank];
                double rpm = w0 * 30 / Math.PI;
                double x = rpm / v.redlineRpm;
                bool on = s.engineOn;
                double thr = on ? Clamp(s.ctrl.throttle, 0, 1) : 0;
                double Tc = on && s.inWater <= 0.65 ? v.engineTorque * LegacyJsEngine.TorqueCurve(Clamp(x, 0, 1)) * thr * v.drivelineEfficiency : 0;
                Tc *= Clamp((1.02 - x) / 0.04, 0, 1);          // soft limiter between 98 and 102 % of redline
                Tc *= 1 - s.dl.tcCut;                           // crawl control (low range)
                double idle = v.idleRpm * Math.PI / 30;
                double Ti = on && w0 < idle ? Math.Min((idle - w0) * v.crankInertia / 0.1, 0.5 * v.engineTorque) : 0;
                if (on && s.shiftT > 0)
                {
                    // AMT shift (06 section 4.2.2): fuel cut while the clutch is open, and the ECU matches the crank to the
                    // new gear's speed so the clutch closes without a jerk
                    Tc = 0;
                    double sync = Clamp(R * s.dl.driveOmega, idle, v.redlineRpm * Math.PI / 30);
                    Ti = Clamp((sync - w0) * v.crankInertia / 0.05, -0.5 * v.engineTorque, 0.5 * v.engineTorque);
                }
                double Td = v.engineTorque * (0.02 + 0.22 * Math.Abs(x) * (1 - thr));
                double wn = w0 + (Tc + Ti) * hs * invI[ShCrank];
                double dd = Td * hs * invI[ShCrank];
                wn = wn > 0 ? Math.Max(0, wn - dd) : Math.Min(0, wn + dd);
                om[ShCrank] = wn;
                s.dl.engineTorque = Tc + Ti - Td;
            }

            // ---- rows (warm-started)
            double ws0 = v.warmStart;
            int nr = 0;
            int rClutch = -1, rDiffF = -1, rDiffR = -1;
            double cap = v.clutchCapacityNm * s.dl.clutch;
            if (cap > 0 && R != 0)
            {
                rClutch = nr;
                rows[nr++] = ShaftRow(ShCrank, 1, ShDrive, -R, -1, 0, -cap * hs, cap * hs, invI, s.dl.lamClutch * ws0);
            }
            double half = 0.5 * v.finalDrive;
            if (nw >= 2 && ws[0].drive)
            {
                rDiffF = nr;
                rows[nr++] = DiffRow(ws[0].shaft, ws[1].shaft, half, invI, s.dl.lamDiffFront * ws0);
            }
            if (nw >= 4 && ws[2].drive)
            {
                rDiffR = nr;
                rows[nr++] = DiffRow(ws[2].shaft, ws[3].shaft, half, invI, s.dl.lamDiffRear * ws0);
            }

            bool absOn = v.abs && !s.dl.absOff;
            double foot = Clamp(s.ctrl.brake, 0, 1);
            for (int i = 0; i < nw; i++)
            {
                ref var q = ref ws[i];
                ref var wh = ref s.wheels.ElementAt(i);
                int sh = q.shaft;
                double r = q.r;
                double wr = om[sh] * r;
                q.f = 0;
                // tyre state at the start of the substep
                double vx = 0, vy = 0, Fz = 0;
                if (q.contact)
                {
                    vx = VelDot(s, q.arm.x, q.arm.y, q.arm.z, q.fd.x, q.fd.y, q.fd.z);
                    vy = VelDot(s, q.rS.x, q.rS.y, q.rS.z, q.sd.x, q.sd.y, q.sd.z);
                    Fz = wh.load;
                }
                double vref = Math.Max(Math.Abs(vx), v.vLow);
                double kappa = (wr - vx) / vref, tanA = vy / vref;
                wh.kappa = kappa; wh.alpha = Math.Atan(tanA);

                // brake + rolling resistance + bearing: omega -> 0, bounded. With ABS (06 section 4.1) active - foot
                // brake, the wheel rolling on the ground above 1 m/s, no handbrake on it - the row can only slow the
                // wheel down to the speed that holds the tyre just under its peak slip, never lower: an ideal ABS
                // modulator, solved implicitly with the tyre (no pressure cycling).
                double Trest = v.bearingTorque + (q.contact ? q.crr * Fz * r : 0);
                double Tfoot = v.brakeTorque * foot;
                bool absMode = absOn && q.contact && Tfoot > 0 && !q.hand && Math.Abs(vx) > 1.0;
                // crawl control in low range (06 section 3.6, the JS "low range claws instead of digging"): a driven wheel
                // may not spin past the tyre's peak slip in the driving direction; its brake takes the excess
                bool crawl = !absMode && s.lowRange && q.drive && q.contact && Tfoot == 0 && !q.hand && s.gear != 0 && s.ctrl.throttle > 0.05;
                q.rB = nr;
                V2Row rb;
                if (absMode)
                {
                    double T = (Tfoot + Trest) * hs;
                    double wt = vx * (1 - AbsSlip * v.mfLong.peak) / r;
                    rb = vx > 0 ? ShaftRow(sh, 1, -1, 0, -1, 0, -T, 0, invI, wh.lamB * ws0) : ShaftRow(sh, 1, -1, 0, -1, 0, 0, T, invI, wh.lamB * ws0);
                    rb.bias = wt;
                    wh.anchorB = 0;
                }
                else if (crawl)
                {
                    double dir = s.gear > 0 ? 1 : -1;
                    double T = (v.brakeTorque + Trest) * hs;
                    double wcap = (vx + dir * AbsSlip * v.mfLong.peak * vref) / r;
                    rb = dir > 0 ? ShaftRow(sh, 1, -1, 0, -1, 0, -T, 0, invI, wh.lamB * ws0) : ShaftRow(sh, 1, -1, 0, -1, 0, 0, T, invI, wh.lamB * ws0);
                    rb.bias = wcap;
                    wh.anchorB = 0;
                }
                else
                {
                    double Tb = Tfoot + (q.hand ? v.handbrakeTorque : 0) + Trest;
                    double brakeStatic = Math.Abs(om[sh]) * r < v.vLow ? 1 : 0;
                    rb = ShaftRow(sh, 1, -1, 0, -1, 0, -Tb * hs, Tb * hs, invI, wh.lamB * ws0);
                    rb.bias = -brakeStatic * wh.anchorB / AnchorTau;
                    if (brakeStatic == 0) wh.anchorB = 0;
                }
                q.abs = absMode || crawl;
                q.brakeMax = absMode ? Tfoot * hs : 0;
                rows[nr++] = rb;

                if (!q.contact || Fz <= 1e-6)
                {
                    wh.lamT = wh.lamL = wh.lamM = 0;
                    continue;
                }

                // MF secant tyre (06 section 3.6), with load sensitivity
                double load = Clamp(1 + v.pD2 * (Fz - v.fz0) / v.fz0, 0.6, 1.25);
                double mu = q.muBase * load;
                double ak = Math.Abs(kappa), aa = Math.Abs(wh.alpha);
                double Fx = Fz * mu * v.mfLong.Mu(ak), Fy = Fz * mu * v.mfLat.Mu(aa);
                double Cx = ak > 1e-3 ? Fx / ak : Fz * mu * v.mfLong.Stiffness;
                double Cy = aa > 1e-3 ? Fy / Math.Abs(tanA) : Fz * mu * v.mfLat.Stiffness;
                double g = Clamp(Math.Max(Math.Abs(vx), Math.Abs(wr)) / v.vLow, 0, 1);   // rigid at walking pace
                q.f = 1 - g;
                if (q.f <= 0) { wh.anchorL = 0; wh.anchorS = 0; }
                double fxMax = Fz * mu * v.mfLong.D, fyMax = Fz * mu * v.mfLat.D;

                q.rT = nr;
                var rt = ChassisRow(s, q.arm, q.fd, sh, -r, invI, -fxMax * hs, fxMax * hs, wh.lamT * ws0);
                rt.goh = Cx > 0 ? vref / Cx * g / hs : 0;
                rt.k += rt.goh;
                rt.bias = -q.f * wh.anchorL / AnchorTau;
                rows[nr++] = rt;

                q.rL = nr;
                var rl = ChassisRow(s, q.rS, q.sd, -1, 0, invI, -fyMax * hs, fyMax * hs, wh.lamL * ws0);
                rl.goh = Cy > 0 ? vref / Cy * g / hs : 0;
                rl.k += rl.goh;
                rl.bias = -q.f * wh.anchorS / AnchorTau;
                rows[nr++] = rl;

                if (q.mudCoef > 0)
                {
                    double mudRes = q.mudCoef * Fz;
                    q.rM = nr;
                    rows[nr++] = ChassisRow(s, q.arm, q.fd, -1, 0, invI, -mudRes * hs, mudRes * hs, wh.lamM * ws0);
                }
                else wh.lamM = 0;
            }

            // apply the warm start
            for (int r = 0; r < nr; r++) if (rows[r].lambda != 0) Apply(ref s, rows[r], rows[r].lambda, om, invI);

            // ---- projected Gauss-Seidel
            int its = Math.Max(1, v.iterations);
            for (int it = 0; it < its; it++)
            {
                if (rClutch >= 0) SolveRow(ref s, ref rows[rClutch], om, invI);
                if (rDiffF >= 0) SolveRow(ref s, ref rows[rDiffF], om, invI);
                if (rDiffR >= 0) SolveRow(ref s, ref rows[rDiffR], om, invI);
                for (int i = 0; i < nw; i++)
                {
                    ref var q = ref ws[i];
                    if (q.rT >= 0)
                    {
                        SolveBlock(ref s, ref rows[q.rB], ref rows[q.rT], om, invI);
                        SolveRow(ref s, ref rows[q.rL], om, invI);
                        Ellipse(ref s, ref rows[q.rT], ref rows[q.rL], om, invI);
                        if (q.rM >= 0) SolveRow(ref s, ref rows[q.rM], om, invI);
                    }
                    else SolveRow(ref s, ref rows[q.rB], om, invI);
                }
            }

            // ---- write back: shaft speeds, warm starts, anchors, angles
            s.dl.crankOmega = om[ShCrank];
            s.dl.crankAngle = (s.dl.crankAngle + om[ShCrank] * hs) % (2 * Math.PI);
            s.dl.driveOmega = om[ShDrive];
            s.dl.lamClutch = rClutch >= 0 ? rows[rClutch].lambda : 0;
            s.dl.lamDiffFront = rDiffF >= 0 ? rows[rDiffF].lambda : 0;
            s.dl.lamDiffRear = rDiffR >= 0 ? rows[rDiffR].lambda : 0;
            for (int i = 0; i < nw; i++)
            {
                ref var q = ref ws[i];
                ref var wh = ref s.wheels.ElementAt(i);
                wh.omega = om[q.shaft];
                wh.angle += wh.omega * hs;
                var rb = rows[q.rB];
                wh.lamB = rb.lambda;
                // ABS diagnostics: how much of the foot brake the modulator held back (0 = none, HUD "ABS" light)
                wh.absCut = q.abs && q.brakeMax > 0 ? Clamp(1 - Math.Abs(rb.lambda) / q.brakeMax, 0, 1) : 0;
                if (q.abs) wh.anchorB = 0;
                else if (Math.Abs(om[q.shaft]) * q.r < v.vLow && Math.Abs(rb.lambda) < 0.99 * rb.hi)
                    wh.anchorB = Clamp(wh.anchorB + om[q.shaft] * hs, -AnchorBrakeMax, AnchorBrakeMax);
                else wh.anchorB = 0;
                if (q.rT < 0) continue;
                wh.lamT = rows[q.rT].lambda;
                wh.lamL = rows[q.rL].lambda;
                wh.lamM = q.rM >= 0 ? rows[q.rM].lambda : 0;
                if (q.f > 0)
                {
                    double vx = VelDot(s, q.arm.x, q.arm.y, q.arm.z, q.fd.x, q.fd.y, q.fd.z);
                    double vy = VelDot(s, q.rS.x, q.rS.y, q.rS.z, q.sd.x, q.sd.y, q.sd.z);
                    wh.anchorL = Clamp(wh.anchorL + (vx - wh.omega * q.r) * hs, -AnchorMax, AnchorMax);
                    wh.anchorS = Clamp(wh.anchorS + vy * hs, -AnchorMax, AnchorMax);
                }
            }
        }

        // ------------------------------------------------------------------ rows

        static unsafe V2Row ShaftRow(int a, double ja, int b, double jb, int c, double jc, double lo, double hi, double* invI, double lambda)
        {
            var r = new V2Row { s0 = a, j0 = ja, s1 = b, j1 = jb, s2 = c, j2 = jc, lo = lo, hi = hi };
            r.ns = c >= 0 ? 3 : b >= 0 ? 2 : 1;
            r.k = ja * ja * invI[a] + (b >= 0 ? jb * jb * invI[b] : 0) + (c >= 0 ? jc * jc * invI[c] : 0);
            r.lambda = Clamp(lambda, lo, hi);
            return r;
        }

        /// <summary>Open differential with its final drive: omega_drive - (fd/2)(omega_l + omega_r) = 0 (one shaft when locked).</summary>
        static unsafe V2Row DiffRow(int left, int right, double half, double* invI, double lambda)
        {
            if (left == right) return ShaftRow(ShDrive, 1, left, -2 * half, -1, 0, double.NegativeInfinity, double.PositiveInfinity, invI, lambda);
            return ShaftRow(ShDrive, 1, left, -half, right, -half, double.NegativeInfinity, double.PositiveInfinity, invI, lambda);
        }

        /// <summary>A row on the chassis (direction n at offset r) and optionally one shaft (coefficient j).</summary>
        static unsafe V2Row ChassisRow(in BrvState s, double3 r, double3 n, int shaft, double j, double* invI, double lo, double hi, double lambda)
        {
            var row = new V2Row { chassis = true, lin = n, lo = lo, hi = hi };
            row.ang = new double3(r.y * n.z - r.z * n.y, r.z * n.x - r.x * n.z, r.x * n.y - r.y * n.x);
            double3 ia = default;
            AddInvI(s, row.ang.x, row.ang.y, row.ang.z, ref ia, 1);
            row.iang = ia;
            row.k = s.invMass * Dot(n, n) + Dot(row.ang, ia);
            if (shaft >= 0) { row.ns = 1; row.s0 = shaft; row.j0 = j; row.k += j * j * invI[shaft]; }
            row.s1 = row.s2 = -1;
            row.lambda = Clamp(lambda, lo, hi);
            return row;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe double RowDot(in BrvState s, in V2Row r, double* om)
        {
            double v = 0;
            if (r.chassis) v = Dot(r.lin, s.vel) + Dot(r.ang, s.angVel);
            if (r.ns > 0) v += r.j0 * om[r.s0];
            if (r.ns > 1) v += r.j1 * om[r.s1];
            if (r.ns > 2) v += r.j2 * om[r.s2];
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void Apply(ref BrvState s, in V2Row r, double imp, double* om, double* invI)
        {
            if (r.chassis)
            {
                s.vel += r.lin * (imp * s.invMass);
                s.angVel += r.iang * imp;
            }
            if (r.ns > 0) om[r.s0] += r.j0 * imp * invI[r.s0];
            if (r.ns > 1) om[r.s1] += r.j1 * imp * invI[r.s1];
            if (r.ns > 2) om[r.s2] += r.j2 * imp * invI[r.s2];
        }

        static unsafe void SolveRow(ref BrvState s, ref V2Row r, double* om, double* invI)
        {
            if (r.k <= 0) return;
            double jv = RowDot(s, r, om);
            double dl = -(jv - r.bias + r.goh * r.lambda) / r.k;
            double nl = Clamp(r.lambda + dl, r.lo, r.hi);
            double d = nl - r.lambda;
            if (d != 0) Apply(ref s, r, d, om, invI);
            r.lambda = nl;
        }

        /// <summary>
        /// The brake row (b, shaft only) and the tyre-long row (t, chassis + the same shaft) solved together: the exact
        /// minimum of the 2x2 box QP (unconstrained, else the best of the four clamped edges).
        /// </summary>
        static unsafe void SolveBlock(ref BrvState s, ref V2Row b, ref V2Row t, double* om, double* invI)
        {
            double a11 = b.k, a22 = t.k;
            double a12 = b.j0 * t.j0 * invI[b.s0];          // both act on the wheel shaft
            double r1 = RowDot(s, b, om) - b.bias + b.goh * b.lambda;
            double r2 = RowDot(s, t, om) - t.bias + t.goh * t.lambda;
            double l1 = b.lo - b.lambda, u1 = b.hi - b.lambda, l2 = t.lo - t.lambda, u2 = t.hi - t.lambda;
            double x1, x2;
            double det = a11 * a22 - a12 * a12;
            x1 = det > 1e-12 * a11 * a22 ? (-r1 * a22 + r2 * a12) / det : 0;
            x2 = det > 1e-12 * a11 * a22 ? (-r2 * a11 + r1 * a12) / det : 0;
            if (!(det > 1e-12 * a11 * a22) || x1 < l1 || x1 > u1 || x2 < l2 || x2 > u2)
            {
                double bestF = double.PositiveInfinity, b1 = 0, b2 = 0;
                Edge1(a11, a12, a22, r1, r2, l1, l2, u2, ref bestF, ref b1, ref b2);
                Edge1(a11, a12, a22, r1, r2, u1, l2, u2, ref bestF, ref b1, ref b2);
                Edge2(a11, a12, a22, r1, r2, l2, l1, u1, ref bestF, ref b1, ref b2);
                Edge2(a11, a12, a22, r1, r2, u2, l1, u1, ref bestF, ref b1, ref b2);
                x1 = b1; x2 = b2;
            }
            if (x1 != 0) Apply(ref s, b, x1, om, invI);
            if (x2 != 0) Apply(ref s, t, x2, om, invI);
            b.lambda += x1;
            t.lambda += x2;
        }

        static void Edge1(double a11, double a12, double a22, double r1, double r2, double x1, double l2, double u2, ref double bestF, ref double b1, ref double b2)
        {
            if (double.IsInfinity(x1)) return;
            double x2 = a22 > 0 ? Clamp(-(r2 + a12 * x1) / a22, l2, u2) : 0;
            double f = 0.5 * (a11 * x1 * x1 + 2 * a12 * x1 * x2 + a22 * x2 * x2) + r1 * x1 + r2 * x2;
            if (f < bestF) { bestF = f; b1 = x1; b2 = x2; }
        }

        static void Edge2(double a11, double a12, double a22, double r1, double r2, double x2, double l1, double u1, ref double bestF, ref double b1, ref double b2)
        {
            if (double.IsInfinity(x2)) return;
            double x1 = a11 > 0 ? Clamp(-(r1 + a12 * x2) / a11, l1, u1) : 0;
            double f = 0.5 * (a11 * x1 * x1 + 2 * a12 * x1 * x2 + a22 * x2 * x2) + r1 * x1 + r2 * x2;
            if (f < bestF) { bestF = f; b1 = x1; b2 = x2; }
        }

        /// <summary>Friction ellipse (06 section 3.6): scale the long and lat impulses back onto it.</summary>
        static unsafe void Ellipse(ref BrvState s, ref V2Row t, ref V2Row l, double* om, double* invI)
        {
            double mx = t.hi, my = l.hi;
            if (!(mx > 0) || !(my > 0)) return;
            double ex = t.lambda / mx, ey = l.lambda / my;
            double e = ex * ex + ey * ey;
            if (e <= 1) return;
            double sc = 1 / Math.Sqrt(e);
            double dt = t.lambda * sc - t.lambda, dl = l.lambda * sc - l.lambda;
            Apply(ref s, t, dt, om, invI);
            Apply(ref s, l, dl, om, invI);
            t.lambda += dt;
            l.lambda += dl;
        }

        // ------------------------------------------------------------------ after the substeps (what V0's tyres() reports)

        static unsafe void V2AfterSubsteps(ref BrvState s, in BrvDef d, double dt, V2Wheel* ws)
        {
            var v = d.v2def;
            s.rpm = s.dl.crankOmega * 30 / Math.PI;
            s.omegaMax = 0;
            s.engineBrake = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                ref var q = ref ws[i];
                ref var wh = ref s.wheels.ElementAt(i);
                wh.skid = 0;
                if (!q.contact)
                {
                    wh.spin *= 0.95;
                    wh.sliding = false;
                    continue;
                }
                double vl = VelDot(s, q.arm.x, q.arm.y, q.arm.z, q.fd.x, q.fd.y, q.fd.z);
                double vs = VelDot(s, q.rS.x, q.rS.y, q.rS.z, q.sd.x, q.sd.y, q.sd.z);
                wh.vl = vl;
                wh.vsPre = vs;
                wh.mudRes = q.mudCoef * wh.load;
                double wr = wh.omega * q.r;
                // wheelspin as the JS "spin" (0..3): how much faster the tread turns than the ground goes
                double spin = Math.Abs(wr) > Math.Abs(vl) ? Clamp((Math.Abs(wr) - Math.Abs(vl)) / Math.Max(Math.Abs(vl), 1.0) - 0.05, 0, 3) : 0;
                wh.spin += (spin - wh.spin) * Math.Min(1, dt * 8);
                double lim = wh.load * q.muBase * v.mfLat.D * dt / Math.Max(1, v.substeps);
                wh.latLim = lim;
                wh.accL = wh.lamT; wh.accS = wh.lamL;
                wh.sliding = lim > 0 && Math.Abs(wh.lamL) >= lim * 0.95 && Math.Abs(vs) > 0.5;
                if (wh.sliding) wh.skid = Math.Max(wh.skid, Clamp((Math.Abs(vs) - 0.5) / 5, 0, 1));
                double slip = Math.Abs(wr - vl);
                if (slip > 1 && Math.Abs(wh.kappa) > v.mfLong.peak * 1.5) wh.skid = Math.Max(wh.skid, Clamp(slip / 8, 0, 1));
                wh.skid = Math.Max(wh.skid, Clamp(wh.spin * 0.6, 0, 1));
            }
        }
    }
}
