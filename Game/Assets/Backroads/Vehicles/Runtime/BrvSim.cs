using System;
using System.Runtime.CompilerServices;
using Backroads.World;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// BRV V0: a line-by-line port of the browser game's js/vehicle.js (oracle 552f7807): rigid body with raycast
    /// suspension against the heightfield, PGS tyre friction, the classic engine and automatic gearbox
    /// (<see cref="LegacyJsEngine"/>), low range and AWD-on-low, hull contacts, 2D statics against the props.js collider
    /// grid, water, mud grip and sinkage, and the winch rope (03 section 4.14, phase 1). Same equations, same order,
    /// same constants, IEEE doubles like the JS, in JS world coordinates (+X left); the Unity API flips at the
    /// boundary (BrCoords). Ruts are not applied here: rut() calls are recorded into a list in step order and the
    /// caller replays them into Backroads.Deform.StampSinks.Current after the step (Burst cannot call managed code).
    /// Every method is Burst-compatible (plain structs, NativeArrays, System.Math).
    /// </summary>
    public static partial class BrvSim
    {
        public const double STEP = 1.0 / 120.0;
        public const double G = 9.81;
        public const double WATER_Y = 0.0;

        // world.js SURF / SOFTNESS / GRIP (Float32Array values)
        public const byte SURF_GRASS = 0, SURF_DIRT = 1, SURF_ROAD = 4, SURF_CONCRETE = 6, SURF_MUD = 8;

        public static double Softness(int s)
        {
            switch (s)
            {
                case 0: return 0.5f;
                case 1: return 1.0f;
                case 2: return 0.85f;
                case 5: return 0.16f;
                case 7: return 0.3f;
                case 8: return 1.0f;
                default: return 0f;
            }
        }

        public static double Grip(int s)
        {
            switch (s)
            {
                case 0: return 0.8f;
                case 1: return 0.74f;
                case 2: return 0.64f;
                case 3: return 0.9f;
                case 4: return 1.08f;
                case 5: return 0.86f;
                case 6: return 1.02f;
                case 7: return 0.9f;
                case 8: return 0.44f;
                default: return 0f;
            }
        }

        // ------------------------------------------------------------------ JS numerics

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp(double v, double a, double b) => v < a ? a : v > b ? b : v;

        /// <summary>Math.sign(x) || 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SignOr1(double x) => x > 0 ? 1 : x < 0 ? -1 : 1;

        /// <summary>Math.sign(x) (0 for 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : 0;

        /// <summary>V8 Math.hypot with two arguments (max scaling + Kahan sum).</summary>
        public static double Hypot(double a, double b)
        {
            a = Math.Abs(a); b = Math.Abs(b);
            double m = Math.Max(a, b);
            if (m == 0) return 0;
            double sum = 0, comp = 0;
            double n = a / m, s = n * n - comp, p = sum + s;
            comp = (p - sum) - s; sum = p;
            n = b / m; s = n * n - comp; p = sum + s;
            sum = p;
            return Math.Sqrt(sum) * m;
        }

        /// <summary>V8 Math.hypot with three arguments.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Hypot(double a, double b, double c) => WorldGridQueriesRef.Hypot(a, b, c);

        // ------------------------------------------------------------------ world.js sampling (JS space, doubles)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double HeightAt(in BrvWorld w, double x, double z) => WorldGridQueriesRef.HeightAtJs(w.grid, x, z);

        static double3 NormalAt(in BrvWorld w, double x, double z) => WorldGridQueriesRef.NormalAtJs(w.grid, x, z);

        // ------------------------------------------------------------------ setup (constructor, setPayload, place, settle)

        /// <summary>The state of a freshly constructed vehicle.js Vehicle.</summary>
        public static BrvState Create(in BrvDef d)
        {
            var s = new BrvState
            {
                quat = new double4(0, 0, 0, 1), prevQuat = new double4(0, 0, 0, 1),
                ex = new double3(1, 0, 0), ey = new double3(0, 1, 0), ez = new double3(0, 0, 1),
                gear = 1, rpm = d.hasEngine ? d.eIdle : 800, fuel = 1,
                winch = new BrvWinch { target = -1 },
            };
            SetPayload(ref s, d, 0);
            for (int i = 0; i < d.wheels.Length; i++)
            {
                var l = d.wheels[i];
                s.wheels.Add(new BrvWheel
                {
                    contact = false, len = l.rest - d.c0, comp = d.c0, load = 0,
                    n = new double3(0, 1, 0), surf = 0,
                });
            }
            return s;
        }

        public static void SetPayload(ref BrvState s, in BrvDef d, double kg)
        {
            s.payload = kg;
            s.mass = d.mass + kg;
            s.invMass = 1 / s.mass;
            double w = d.boxW, h = d.boxH, l = d.boxL, m = s.mass * d.inertia;
            s.iI = new double3(12 / (m * (h * h + l * l)), 12 / (m * (w * w + l * l)), 12 / (m * (w * w + h * h)));
        }

        /// <summary>place(x, z, yaw, extra): JS world space.</summary>
        public static void Place(ref BrvState s, in BrvDef d, in BrvWorld w, double x, double z, double yaw, double extra = 0.0)
        {
            double y = HeightAt(w, x, z);
            double half = yaw / 2;
            s.quat = new double4(0, Math.Sin(half), 0, Math.Cos(half));
            s.pos = new double3(x, y + d.comY + 0.05 + extra, z);
            s.vel = 0;
            s.angVel = 0;
            s.prevPos = s.pos;
            s.prevQuat = s.quat;
            s.asleep = false;
            s.sleepT = 0;
        }

        public static void ResetUpright(ref BrvState s, in BrvDef d, in BrvWorld w)
        {
            double yaw = Math.Atan2(s.ez.x, s.ez.z);
            Place(ref s, d, w, s.pos.x, s.pos.z, yaw, 1.2);
        }

        public static void Wake(ref BrvState s) { s.asleep = false; s.sleepT = 0; }

        public static void Park(ref BrvState s)
        {
            s.driven = false;
            s.ctrl.throttle = 0; s.ctrl.brake = 1; s.ctrl.handbrake = 1; s.ctrl.steer = 0;
            s.steerIn = 0;
            s.horn = false;
        }

        // ------------------------------------------------------------------ helpers

        public static void UpdateBasis(ref BrvState s)
        {
            double x = s.quat.x, y = s.quat.y, z = s.quat.z, w = s.quat.w;
            s.ex = new double3(1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y));
            s.ey = new double3(2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x));
            s.ez = new double3(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y));
        }

        public static double3 ToWorld(in BrvState s, double lx, double ly, double lz)
        {
            double3 ex = s.ex, ey = s.ey, ez = s.ez, p = s.pos;
            return new double3(p.x + ex.x * lx + ey.x * ly + ez.x * lz, p.y + ex.y * lx + ey.y * ly + ez.y * lz, p.z + ex.z * lx + ey.z * ly + ez.z * lz);
        }

        /// <summary>Inverse inertia (world) applied to t, added into o scaled by sc.</summary>
        static void AddInvI(in BrvState s, double tx, double ty, double tz, ref double3 o, double sc)
        {
            double3 ex = s.ex, ey = s.ey, ez = s.ez, iI = s.iI;
            double a = (ex.x * tx + ex.y * ty + ex.z * tz) * iI.x;
            double b = (ey.x * tx + ey.y * ty + ey.z * tz) * iI.y;
            double c = (ez.x * tx + ez.y * ty + ez.z * tz) * iI.z;
            o.x += (ex.x * a + ey.x * b + ez.x * c) * sc;
            o.y += (ex.y * a + ey.y * b + ez.y * c) * sc;
            o.z += (ex.z * a + ey.z * b + ez.z * c) * sc;
        }

        /// <summary>1 / effective mass along n at offset r.</summary>
        public static double KAt(in BrvState s, double rx, double ry, double rz, double nx, double ny, double nz)
        {
            double cx = ry * nz - rz * ny, cy = rz * nx - rx * nz, cz = rx * ny - ry * nx;
            double3 ex = s.ex, ey = s.ey, ez = s.ez, iI = s.iI;
            double a = ex.x * cx + ex.y * cy + ex.z * cz, b = ey.x * cx + ey.y * cy + ey.z * cz, c = ez.x * cx + ez.y * cy + ez.z * cz;
            return s.invMass + a * a * iI.x + b * b * iI.y + c * c * iI.z;
        }

        public static double VelDot(in BrvState s, double rx, double ry, double rz, double nx, double ny, double nz)
        {
            double3 w = s.angVel, v = s.vel;
            double vx = v.x + w.y * rz - w.z * ry, vy = v.y + w.z * rx - w.x * rz, vz = v.z + w.x * ry - w.y * rx;
            return vx * nx + vy * ny + vz * nz;
        }

        public static void Impulse(ref BrvState s, double jx, double jy, double jz, double rx, double ry, double rz)
        {
            double im = s.invMass;
            s.vel.x += jx * im; s.vel.y += jy * im; s.vel.z += jz * im;
            AddInvI(s, ry * jz - rz * jy, rz * jx - rx * jz, rx * jy - ry * jx, ref s.angVel, 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Dot(double3 a, double3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Length(double3 a) => Math.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double LengthSq(double3 a) => a.x * a.x + a.y * a.y + a.z * a.z;

        static bool IsDrive(in BrvState s, in BrvDef d, in BrvWheelDef l) => l.drive || (s.lowRange && d.awdOnLow);

        // ------------------------------------------------------------------ controls (drive, called once per step in Unity)

        /// <summary>drive(fwd, back, steer, handbrake, dt): the driver's pedals become ctrl, with the JS driver aids.</summary>
        public static void Drive(ref BrvState s, in BrvDef d, double fwd, double back, double steer, double handbrake, double dt)
        {
            s.driven = true;
            Wake(ref s);
            double vf = s.fwdSpeed;
            if (d.simple)
            {
                s.ctrl.throttle = fwd - back;
                s.ctrl.brake = fwd < 0.05 && back < 0.05 ? 1 : 0;
                s.gear = s.ctrl.throttle < -0.05 ? -1 : 1;
            }
            else if (s.gear >= 0)
            {
                if (back > 0.1 && vf < 0.7 && fwd < 0.1)
                {
                    s.revHold += dt;
                    if (s.revHold > 0.22) { s.gear = -1; s.revHold = 0; }
                }
                else s.revHold = 0;
                s.ctrl.throttle = fwd;
                s.ctrl.brake = back;
                if (s.gear == 0) s.gear = 1;
            }
            else
            {
                if (fwd > 0.1 && vf > -0.7 && back < 0.1)
                {
                    s.fwdHold += dt;
                    if (s.fwdHold > 0.15) { s.gear = 1; s.fwdHold = 0; }
                }
                else s.fwdHold = 0;
                s.ctrl.throttle = back;
                s.ctrl.brake = fwd;
            }
            if (s.fuel <= 0) s.ctrl.throttle = 0;
            // auto-hold so parked vehicles don't creep downhill
            if (Math.Abs(s.ctrl.throttle) < 0.05 && Math.Abs(vf) < 0.5) s.ctrl.brake = Math.Max(s.ctrl.brake, 0.7);
            s.ctrl.handbrake = handbrake;
            double rate = Math.Abs(steer) > Math.Abs(s.steerIn) && Sign(steer) == Sign(s.steerIn) ? d.steerRate : d.steerRate * 1.7;
            s.steerIn += Clamp(steer - s.steerIn, -rate * dt, rate * dt);
            s.ctrl.steer = s.steerIn;
        }

        // ------------------------------------------------------------------ simulation step

        /// <summary>step(dt). <paramref name="self"/> is this vehicle's fleet index (for rut and knock records).</summary>
        public static void Step(ref BrvState s, in BrvDef d, in BrvWorld w, double dt, int self,
                                ref NativeList<RutCall> ruts, ref NativeList<TreeKnock> knocks,
                                NativeArray<BrvState> fleet, int fleetCount)
        {
            if (d.v2) { StepV2(ref s, d, w, dt, self, ref ruts, ref knocks, fleet, fleetCount); return; }
            if (s.asleep) return;
            s.steps++;
            s.prevPos = s.pos;
            s.prevQuat = s.quat;
            UpdateBasis(ref s);
            s.fwdSpeed = Dot(s.vel, s.ez);
            double speedAbs = Math.Abs(s.fwdSpeed);
            s.steerAngle = -s.ctrl.steer * d.steer / (1 + speedAbs * 0.055);

            s.vel.y -= G * dt;
            Water(ref s, d, dt);
            Suspension(ref s, d, w, dt);
            Tyres(ref s, d, w, dt);
            if (d.simple) TrackSteer(ref s, d, dt);
            HullContacts(ref s, d, w, dt, self, ref ruts);
            Statics(ref s, d, w, self, ref knocks);
            if (s.winch.active) WinchStep(ref s, d, dt, fleet, fleetCount);
            StepTail(ref s, d, w, dt, self, ref ruts);
        }

        /// <summary>The end of step() after the contacts (shared by V0 and v2): drag, damping, integration, the island
        /// clamp, ruts every 2nd step, mud on the body, the stuck timer, fuel and sleep.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StepTail(ref BrvState s, in BrvDef d, in BrvWorld w, double dt, int self, ref NativeList<RutCall> ruts)
        {
            // aero drag & damping
            double sp = Length(s.vel);
            if (sp > 0.01) s.vel *= Math.Max(0, 1 - d.drag * sp * dt * s.invMass);
            s.angVel *= 1 - 0.25 * dt;
            double wl = Length(s.angVel);
            if (wl > 14) s.angVel *= 14 / wl;

            // integrate
            s.prePos = s.pos;
            s.preQuat = s.quat;
            s.pos.x += s.vel.x * dt; s.pos.y += s.vel.y * dt; s.pos.z += s.vel.z * dt;
            {
                double4 q = s.quat; double3 wv = s.angVel;
                double ax = wv.x * 0.5 * dt, ay = wv.y * 0.5 * dt, az = wv.z * 0.5 * dt;
                double qx = q.x, qy = q.y, qz = q.z, qw = q.w;
                q.x += ax * qw + ay * qz - az * qy;
                q.y += ay * qw + az * qx - ax * qz;
                q.z += az * qw + ax * qy - ay * qx;
                q.w += -ax * qx - ay * qy - az * qz;
                double l = Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (l == 0) q = new double4(0, 0, 0, 1);
                else { l = 1 / l; q.x *= l; q.y *= l; q.z *= l; q.w *= l; }
                s.quat = q;
            }

            // keep inside the island (the world's half size from its grid layout, T3.1: 512 m on canon v1)
            double lim = (double)w.grid.Layout.Half - 6;
            if (Math.Abs(s.pos.x) > lim) { s.pos.x = Sign(s.pos.x) * lim; s.vel.x *= -0.2; }
            if (Math.Abs(s.pos.z) > lim) { s.pos.z = Sign(s.pos.z) * lim; s.vel.z *= -0.2; }
            double gy = HeightAt(w, s.pos.x, s.pos.z);
            if (s.pos.y < gy - 2.5) { s.pos.y = gy + 1.5; s.vel.y = 0; }

            s.speed = Length(s.vel);
            if ((++s.deformTick & 1) == 0) Deform(ref s, d, w, dt * 2, self, ref ruts);

            // mud cakes on; water and the car wash take it off
            int mudWheels = 0; double churn = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                ref var wh = ref s.wheels.ElementAt(i);
                if (!wh.contact || wh.surf != SURF_MUD) continue;
                mudWheels++;
                churn += Math.Abs(wh.vl) * 0.02 + wh.spin * 0.12;
            }
            if (mudWheels > 0) s.dirt = Math.Min(1, s.dirt + churn * dt * 0.35);
            if (s.inWater > 0.05) s.dirt = Math.Max(0, s.dirt - s.inWater * dt * 0.25);
            // stuck: flooring it in mud and going nowhere
            if (mudWheels > 0 && s.driven && Math.Abs(s.ctrl.throttle) > 0.5 && s.speed < 0.6) s.stuckT += dt;
            else s.stuckT = Math.Max(0, s.stuckT - dt * 2);

            // fuel
            if (s.driven && s.engineOn)
            {
                double use = d.fuelUse * (0.08 + Math.Abs(s.ctrl.throttle)) * dt;
                s.fuel = Math.Max(0, s.fuel - use);
            }

            // sleep when parked and still
            bool allContact = true;
            for (int i = 0; i < s.wheels.Length; i++) if (!s.wheels[i].contact) { allContact = false; break; }
            if (!s.driven && s.speed < 0.08 && LengthSq(s.angVel) < 0.004 && allContact)
            {
                s.sleepT += dt;
                if (s.sleepT > 1.2) { s.asleep = true; s.vel = 0; s.angVel = 0; s.prevPos = s.pos; s.prevQuat = s.quat; }
            }
            else s.sleepT = 0;
        }

        static void Water(ref BrvState s, in BrvDef d, double dt)
        {
            double bottom = s.pos.y + d.bcy - d.bhh;
            double depth = WATER_Y - bottom;
            s.inWater = 0;
            if (depth <= 0) return;
            double sub = Clamp(depth / (d.bhh * 2), 0, 1);
            s.inWater = sub;
            s.vel.y += G * sub * 0.95 * dt;
            double k = 1 - Math.Min(0.9, 1.6 * sub * dt);
            s.vel *= k;
            s.angVel *= 1 - Math.Min(0.9, 2.0 * sub * dt);
        }

        static void Suspension(ref BrvState s, in BrvDef d, in BrvWorld w, double dt)
        {
            double3 ey = s.ey, ez = s.ez, ex = s.ex;
            double dx = -ey.x, dy = -ey.y, dz = -ey.z;
            double maxF = s.mass * G * 5;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var l = d.wheels[i];
                ref var wh = ref s.wheels.ElementAt(i);
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
                    continue;
                }
                wh.contact = true;
                double len = Math.Max(0, best);
                double comp = l.rest - len;
                wh.len = len;
                wh.comp = comp;
                wh.cp = new double3(cx, cy, cz);
                wh.n = NormalAt(w, cx, cz);
                wh.surf = WorldGridQueriesRef.SurfaceAtJs(w.grid, cx, cz);
                double rx = cx - s.pos.x, ry = cy - s.pos.y, rz = cz - s.pos.z;
                double compVel = -VelDot(s, rx, ry, rz, ey.x, ey.y, ey.z);
                double F = l.k * comp + l.c * compVel;
                if (F < 0) F = 0; else if (F > maxF) F = maxF;
                // soften the push on very steep faces
                F *= Clamp(wh.n.x * ey.x + wh.n.y * ey.y + wh.n.z * ey.z, 0.3, 1);
                wh.load = F;
                double J = F * dt;
                Impulse(ref s, ey.x * J, ey.y * J, ey.z * J, rx, ry, rz);
                // bump stop
                if (best <= 0)
                {
                    double v = -VelDot(s, rx, ry, rz, ey.x, ey.y, ey.z);
                    if (v > 0)
                    {
                        double j = v / KAt(s, rx, ry, rz, ey.x, ey.y, ey.z) * 0.5;
                        Impulse(ref s, ey.x * j, ey.y * j, ey.z * j, rx, ry, rz);
                        wh.load += j / dt;
                        if (j > s.mass * 1.5) { s.impact = Math.Max(s.impact, j); }
                    }
                }
            }
            // anti-roll bars
            double ar = d.antiRoll;
            if (ar > 0)
            {
                for (int i = 0; i + 1 < s.wheels.Length; i += 2)
                {
                    var L = s.wheels[i]; var R = s.wheels[i + 1];
                    double f = (L.comp - R.comp) * ar * dt;
                    if (L.contact) Impulse(ref s, ey.x * f, ey.y * f, ey.z * f, L.cp.x - s.pos.x, L.cp.y - s.pos.y, L.cp.z - s.pos.z);
                    if (R.contact) Impulse(ref s, -ey.x * f, -ey.y * f, -ey.z * f, R.cp.x - s.pos.x, R.cp.y - s.pos.y, R.cp.z - s.pos.z);
                }
            }
        }

        static void Tyres(ref BrvState s, in BrvDef d, in BrvWorld w, double dt)
        {
            double3 ex = s.ex, ey = s.ey, ez = s.ez;
            double driveF = LegacyJsEngine.EngineForce(ref s, d, dt);
            bool hand = s.ctrl.handbrake > 0.5;
            double ri = d.rollInfluence;
            double simpleF = 0;
            if (d.simple)
            {
                double vmax = d.simpleVmax;
                double t = s.engineOn ? s.ctrl.throttle : 0;
                double vf = s.fwdSpeed;
                simpleF = t * d.simpleForce * Clamp(1 - (vf * Sign(t != 0 ? t : 1)) / vmax, 0, 1);
                s.rpm += ((s.engineOn ? 700 + Math.Abs(t) * 1400 : 0) - s.rpm) * Math.Min(1, dt * 6);
            }
            int nc = 0, nd = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                if (s.wheels[i].contact) nc++;
                if (IsDrive(s, d, d.wheels[i])) nd++;
            }
            s.nContact = Math.Max(1, nc);
            int nDrive = Math.Max(1, nd);
            s.mudSink = 0;
            int contactMask = 0;
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var l = d.wheels[i];
                ref var wh = ref s.wheels.ElementAt(i);
                bool isDrive = IsDrive(s, d, l);
                wh.skid = 0;
                if (!wh.contact)
                {
                    wh.spin *= 0.95;
                    double target = isDrive ? s.ctrl.throttle * 25 * (s.gear < 0 ? -1 : 1) : wh.omega * 0.99;
                    wh.omega += (target - wh.omega) * Math.Min(1, dt * 2);
                    wh.angle += wh.omega * dt;
                    continue;
                }
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
                if (d.simple) { double along = Dot(rS, ez); rS.x += ez.x * -along; rS.y += ez.y * -along; rS.z += ez.z * -along; }
                wh.rS = rS;
                wh.kF = KAt(s, arm.x, arm.y, arm.z, fx, fy, fz);
                wh.kS = KAt(s, rS.x, rS.y, rS.z, wh.sd.x, wh.sd.y, wh.sd.z);
                double wear = WorldGridQueriesRef.WearAtJs(w.grid, wh.cp.x, wh.cp.z) / 255.0;
                double mu = d.grip * Grip(wh.surf) * (1 - 0.18 * wear * Softness(wh.surf)) * (1 - s.inWater * 0.5);
                // mud: the tread decides the grip, and every centimetre the wheel sinks is mud it has to shove
                wh.mudRes = 0;
                if (wh.surf == SURF_MUD)
                {
                    mu *= d.mudGrip;
                    double mudDepth = WorldGridQueriesRef.MudAtJs(w.grid, wh.cp.x, wh.cp.z);
                    double sink = Math.Max(0, WorldGridQueriesRef.SinkAtJs(w.grid, wh.cp.x, wh.cp.z));
                    double sr = Clamp(sink / l.r, 0, 1.2);
                    // sinkage drag + suction that grows with speed: mud is a slow crawl, not a skid pan
                    double visc = 0.1 * mudDepth * Math.Min(Math.Abs(wh.vl), 6);
                    wh.mudRes = wh.load * (0.04 + 0.12 * mudDepth + 0.3 * sr * sr + 0.12 * sr + visc) * (d.simple ? 0.35 : 1);
                    if (sink > s.mudSink) s.mudSink = sink;
                }
                double maxImp = mu * wh.load * dt;
                wh.vl = VelDot(s, arm.x, arm.y, arm.z, fx, fy, fz);
                double vsPre = VelDot(s, rS.x, rS.y, rS.z, wh.sd.x, wh.sd.y, wh.sd.z);
                // drive impulse
                double dJ = 0;
                if (d.simple) dJ = (simpleF / s.nContact) * dt;
                else if (isDrive && !(hand && l.hand)) dJ = (driveF / nDrive) * dt;
                double spin = 0;
                if (Math.Abs(dJ) > maxImp)
                {
                    // low range crawls: it only feeds the tyres what they can grip, so they claw instead of digging in
                    if (s.lowRange) dJ = Sign(dJ) * maxImp * 0.97;
                    else
                    {
                        spin = Math.Min(3, (Math.Abs(dJ) - maxImp) / Math.Max(maxImp, 1e-3));
                        dJ = Sign(dJ) * maxImp * 0.92;
                    }
                }
                wh.spin += (spin - wh.spin) * Math.Min(1, dt * 8);
                if (dJ != 0) Impulse(ref s, fx * dJ, fy * dJ, fz * dJ, arm.x, arm.y, arm.z);
                // friction limits
                double brakeF = s.ctrl.brake * d.brake + d.roll * wh.load;
                if (isDrive) brakeF += s.engineBrake / nDrive;
                if (hand && l.hand)
                {
                    wh.longLim = maxImp * 0.9;
                    wh.latLim = maxImp * 0.42;
                    if (Math.Abs(wh.vl) > 1) wh.skid = Math.Max(wh.skid, Math.Min(1, Math.Abs(wh.vl) / 8));
                }
                else
                {
                    wh.longLim = Math.Min(brakeF * dt, Math.Max(0, maxImp - Math.Abs(dJ))) + wh.mudRes * dt;
                    double lat2 = maxImp * maxImp - dJ * dJ;
                    wh.latLim = Math.Sqrt(Math.Max(lat2, 0.12 * maxImp * maxImp)) * (wh.sliding ? 0.84 : 1);
                    if (s.ctrl.brake * d.brake * dt > maxImp && Math.Abs(wh.vl) > 2) wh.skid = Math.Max(wh.skid, 0.7);
                }
                wh.accL = 0; wh.accS = 0;
                wh.vsPre = vsPre;
                contactMask |= 1 << i;
            }
            // projected Gauss-Seidel over all tyre contacts
            for (int it = 0; it < 4; it++)
            {
                for (int i = 0; i < s.wheels.Length; i++)
                {
                    if ((contactMask & (1 << i)) == 0) continue;
                    ref var wh = ref s.wheels.ElementAt(i);
                    double3 f = wh.fd, r = wh.arm;
                    double vl = VelDot(s, r.x, r.y, r.z, f.x, f.y, f.z);
                    double nl = Clamp(wh.accL - vl / wh.kF, -wh.longLim, wh.longLim);
                    double dd = nl - wh.accL;
                    wh.accL = nl;
                    if (dd != 0) Impulse(ref s, f.x * dd, f.y * dd, f.z * dd, r.x, r.y, r.z);
                    double3 sd = wh.sd, rs = wh.rS;
                    double vs = VelDot(s, rs.x, rs.y, rs.z, sd.x, sd.y, sd.z);
                    nl = Clamp(wh.accS - vs / wh.kS, -wh.latLim, wh.latLim);
                    dd = nl - wh.accS;
                    wh.accS = nl;
                    if (dd != 0) Impulse(ref s, sd.x * dd, sd.y * dd, sd.z * dd, rs.x, rs.y, rs.z);
                }
            }
            for (int i = 0; i < s.wheels.Length; i++)
            {
                if ((contactMask & (1 << i)) == 0) continue;
                var l = d.wheels[i];
                ref var wh = ref s.wheels.ElementAt(i);
                bool isDrive = IsDrive(s, d, l);
                wh.sliding = wh.latLim > 0 && Math.Abs(wh.accS) >= wh.latLim * 0.985 && Math.Abs(wh.vsPre) > 0.5;
                if (wh.sliding) wh.skid = Math.Max(wh.skid, Clamp((Math.Abs(wh.vsPre) - 0.5) / 5, 0, 1));
                wh.skid = Math.Max(wh.skid, Clamp(wh.spin * 0.6, 0, 1));
                bool locked = s.ctrl.handbrake > 0.5 && l.hand;
                double target = locked ? 0 : wh.vl / l.r;
                if (isDrive && wh.spin > 0.05) target += wh.spin * 18 * (s.gear < 0 ? -1 : 1) * Sign(s.ctrl.throttle != 0 ? s.ctrl.throttle : 1);
                if (s.omegaMax != 0 && isDrive) target = Clamp(target, -s.omegaMax * 1.02, s.omegaMax * 1.02);
                wh.omega += (target - wh.omega) * Math.Min(1, dt * 20);
                wh.angle += wh.omega * dt;
            }
        }

        // ------------------------------------------------------------------ winch

        public static double3 WinchHook(in BrvState s, in BrvDef d) => ToWorld(s, 0, d.bcy - d.bhh * 0.45, d.bcz + d.bhl + 0.05);

        /// <summary>attachWinch(anchor, target): target is a fleet index or -1.</summary>
        public static void AttachWinch(ref BrvState s, in BrvDef d, double3 anchor, int target, NativeArray<BrvState> fleet)
        {
            double3 hook = WinchHook(s, d);
            double3 p = target >= 0 ? fleet[target].pos : anchor;
            double3 dv = hook - p;
            s.winch = new BrvWinch { active = true, anchor = anchor, target = target, len = Length(dv) + 0.2, reel = false, tension = 0 };
            Wake(ref s);
            if (target >= 0)
            {
                var t = fleet[target];
                Wake(ref t);
                fleet[target] = t;
            }
        }

        // A rope: it can only pull. The drum takes in slack, and reels in while you hold the throttle.
        static void WinchStep(ref BrvState s, in BrvDef d, double dt, NativeArray<BrvState> fleet, int fleetCount)
        {
            ref var wn = ref s.winch;
            double3 hook = WinchHook(s, d);
            bool hasTarget = wn.target >= 0 && wn.target < fleetCount;
            double3 A = hasTarget ? fleet[wn.target].pos : wn.anchor;
            double dx = A.x - hook.x, dy = A.y - hook.y, dz = A.z - hook.z;
            double dist = Hypot(dx, dy, dz); if (dist == 0) dist = 1e-3;
            if (wn.reel) wn.len = Math.Max(2.5, wn.len - 0.9 * dt);
            wn.tension *= 0.9;
            if (dist < wn.len) { wn.len = Math.Max(2.5, dist); return; }
            double nx = dx / dist, ny = dy / dist, nz = dz / dist;
            double rx = hook.x - s.pos.x, ry = hook.y - s.pos.y, rz = hook.z - s.pos.z;
            double vn = VelDot(s, rx, ry, rz, nx, ny, nz);
            double k = KAt(s, rx, ry, rz, nx, ny, nz);
            BrvState t = default;
            if (hasTarget)
            {
                t = fleet[wn.target];
                Wake(ref t);
                vn -= t.vel.x * nx + t.vel.y * ny + t.vel.z * nz;
                k += t.invMass;
            }
            double want = Math.Min(1.2, (dist - wn.len) * 3);
            if (vn >= want) { if (hasTarget) fleet[wn.target] = t; return; }
            double maxJ = s.mass * G * d.winchPull * dt;
            double j = Math.Min((want - vn) / k, maxJ);
            Impulse(ref s, nx * j, ny * j, nz * j, rx, ry, rz);
            if (hasTarget) { Impulse(ref t, -nx * j, -ny * j, -nz * j, 0, 0, 0); fleet[wn.target] = t; }
            wn.tension = j / dt;
        }

        // tracked vehicles turn by driving one track against the other
        static void TrackSteer(ref BrvState s, in BrvDef d, double dt)
        {
            bool anyContact = false;
            for (int i = 0; i < s.wheels.Length; i++) if (s.wheels[i].contact) { anyContact = true; break; }
            double steer = s.ctrl.steer;
            double3 ey = s.ey;
            double yawRate = Dot(s.angVel, ey);
            double target = -steer * 0.85 * (s.fwdSpeed < -0.3 ? -1 : 1);
            if (anyContact && s.engineOn)
            {
                double Iyy = 1 / s.iI.y;
                double dW = Clamp(target - yawRate, -3 * dt * 6, 3 * dt * 6);
                double L = dW * Iyy * 0.35;
                AddInvI(s, ey.x * L, ey.y * L, ey.z * L, ref s.angVel, 1);
            }
            double vf = s.fwdSpeed;
            s.trackSpeed = new double2(vf - yawRate * 1.2, vf + yawRate * 1.2);
        }

        // ------------------------------------------------------------------ hull contacts

        struct HullContact { public double rx, ry, rz, nx, ny, nz, acc, depth, mud, k, v0; }

        static void HullContacts(ref BrvState s, in BrvDef d, in BrvWorld w, double dt, int self, ref NativeList<RutCall> ruts)
        {
            double3 ex = s.ex, ey = s.ey, ez = s.ez;
            var list = new FixedList4096Bytes<HullContact>();
            double deepest = 0; int dn = -1;
            for (int hI = 0; hI < d.hull.Length; hI++)
            {
                double3 h = d.hull[hI];
                double px = s.pos.x + ex.x * h.x + ey.x * h.y + ez.x * h.z;
                double py = s.pos.y + ex.y * h.x + ey.y * h.y + ez.y * h.z;
                double pz = s.pos.z + ex.z * h.x + ey.z * h.y + ez.z * h.z;
                double gh = HeightAt(w, px, pz);
                if (py >= gh) continue;
                double3 n = NormalAt(w, px, pz);
                double depth = (gh - py) * n.y;
                double mud = WorldGridQueriesRef.SurfaceAtJs(w.grid, px, pz) == SURF_MUD ? WorldGridQueriesRef.MudAtJs(w.grid, px, pz) : 0;
                if (mud > 0 && (s.deformTick & 3) == 0)
                    ruts.Add(new RutCall { x = (float)px, z = (float)pz, radius = 0.7f, depth = (float)(mud * 0.9), rate = (float)(0.01 + Math.Min(0.05, depth * 0.3)), wearAdd = 4, berm = 0.5f, vehicle = self });
                var c = new HullContact { rx = px - s.pos.x, ry = py - s.pos.y, rz = pz - s.pos.z, nx = n.x, ny = n.y, nz = n.z, acc = 0, depth = depth, mud = mud };
                c.k = KAt(s, c.rx, c.ry, c.rz, c.nx, c.ny, c.nz);
                c.v0 = VelDot(s, c.rx, c.ry, c.rz, c.nx, c.ny, c.nz);
                list.Add(c);
                if (depth > deepest) { deepest = depth; dn = list.Length - 1; }
            }
            if (list.Length == 0) return;
            if (dn >= 0)
            {
                var c = list[dn];
                double corr = Math.Min(deepest, 0.4) * 0.35;
                s.pos.x += c.nx * corr; s.pos.y += c.ny * corr; s.pos.z += c.nz * corr;
            }
            for (int it = 0; it < 3; it++)
            {
                for (int i = 0; i < list.Length; i++)
                {
                    ref var c = ref list.ElementAt(i);
                    double vn = VelDot(s, c.rx, c.ry, c.rz, c.nx, c.ny, c.nz);
                    double target = c.v0 < -2 ? -0.15 * c.v0 : 0;
                    double na = Math.Max(0, c.acc + (target - vn) / c.k);
                    double dd = na - c.acc;
                    c.acc = na;
                    if (dd != 0) Impulse(ref s, c.nx * dd, c.ny * dd, c.nz * dd, c.rx, c.ry, c.rz);
                }
            }
            // scraping friction
            for (int i = 0; i < list.Length; i++)
            {
                var c = list[i];
                double3 wv = s.angVel, v = s.vel;
                double vx = v.x + wv.y * c.rz - wv.z * c.ry, vy = v.y + wv.z * c.rx - wv.x * c.rz, vz = v.z + wv.x * c.ry - wv.y * c.rx;
                double vn = vx * c.nx + vy * c.ny + vz * c.nz;
                vx -= c.nx * vn; vy -= c.ny * vn; vz -= c.nz * vn;
                double vt = Hypot(vx, vy, vz);
                if (vt < 1e-3) continue;
                double tx = vx / vt, ty = vy / vt, tz = vz / vt;
                double kt = KAt(s, c.rx, c.ry, c.rz, tx, ty, tz);
                double j = Math.Min(vt / kt, (c.mud > 0 ? 0.35 : 0.55) * c.acc);
                Impulse(ref s, -tx * j, -ty * j, -tz * j, c.rx, c.ry, c.rz);
                if (c.acc > s.mass * 1.2)
                {
                    if (c.acc > s.impact) { s.impact = c.acc; s.impactDir = new double3(c.nx, c.ny, c.nz); }
                }
            }
        }

        // ------------------------------------------------------------------ statics (props.js collider grid)

        const float CG = 16f;

        /// <summary>queryColliders(x, z, r): the records of the cells overlapping the square, deduplicated, in JS order.</summary>
        static void QueryColliders(in BrvWorld w, double x, double z, double r, ref FixedList4096Bytes<int> o)
        {
            o.Clear();
            if (!w.HasColliders) return;
            double half = w.grid.Layout.Half;
            int cgn = w.colliderCells;
            int i0 = Math.Max(0, (int)Math.Floor((x - r + half) / CG)), i1 = Math.Min(cgn - 1, (int)Math.Floor((x + r + half) / CG));
            int j0 = Math.Max(0, (int)Math.Floor((z - r + half) / CG)), j1 = Math.Min(cgn - 1, (int)Math.Floor((z + r + half) / CG));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int c = j * cgn + i;
                    for (int q = w.cellStart[c]; q < w.cellStart[c + 1]; q++)
                    {
                        int idx = w.cellItems[q];
                        bool seen = false;
                        for (int u = 0; u < o.Length; u++) if (o[u] == idx) { seen = true; break; }
                        if (seen) continue;
                        if (o.Length >= o.Capacity) return;
                        o.Add(idx);
                    }
                }
        }

        static void Statics(ref BrvState s, in BrvDef d, in BrvWorld w, int self, ref NativeList<TreeKnock> knocks)
        {
            if (!w.HasColliders) return;
            double3 ex = s.ex, ey = s.ey, ez = s.ez;
            // flattened yaw axes
            double lx = ex.x, lz = ex.z; double ll = Hypot(lx, lz); if (ll == 0) ll = 1; lx /= ll; lz /= ll;
            double fx = ez.x, fz = ez.z; double fl = Hypot(fx, fz); if (fl == 0) fl = 1; fx /= fl; fz /= fl;
            double bx = s.pos.x + ez.x * d.bcz + ey.x * d.bcy, bz = s.pos.z + ez.z * d.bcz + ey.z * d.bcy;
            double by0 = s.pos.y + d.bcy - d.bhh * Math.Abs(ey.y) - 0.2, by1 = s.pos.y + d.bcy + d.bhh;
            double hw = d.bhw, hl = d.bhl;
            var buf = new FixedList4096Bytes<int>();
            QueryColliders(w, bx, bz, Hypot(hw, hl) + 3, ref buf);
            int treeKind = 0; // ColliderGrid.Kinds[0] == "tree"
            // with the PhysX chassis (T3.6) PhysX resolves every static contact; BRV keeps only the browser game's
            // tree knock-over (a fast enough vehicle snaps the tree and drives on), decided before PhysX sees the tree
            bool physx = w.physx;
            for (int bi = 0; bi < buf.Length; bi++)
            {
                int ci = buf[bi];
                var c = w.colliders[ci];
                bool down = w.treeDown.IsCreated && w.treeDown[ci] != 0;
                double cy0 = c.y0, cy1 = down ? c.y0 : c.y1;
                if (cy1 < by0 || cy0 > by1) continue;
                if (c.type == 0)
                {
                    double cr = down ? 0 : c.r;
                    if (cr <= 0) continue;
                    double dx = c.x - bx, dz = c.z - bz;
                    double px = dx * lx + dz * lz, pz = dx * fx + dz * fz;
                    double qx = Clamp(px, -hw, hw), qz = Clamp(pz, -hl, hl);
                    double ddx = px - qx, ddz = pz - qz;
                    double dd = Hypot(ddx, ddz);
                    if (dd >= cr) continue;
                    double nlx, nlz, pen;
                    if (dd > 1e-4) { nlx = ddx / dd; nlz = ddz / dd; pen = cr - dd; }
                    else
                    {
                        double ox = hw - Math.Abs(px), oz = hl - Math.Abs(pz);
                        if (ox < oz) { nlx = SignOr1(px); nlz = 0; pen = ox + cr; } else { nlx = 0; nlz = SignOr1(pz); pen = oz + cr; }
                    }
                    // world normal pointing from vehicle towards obstacle
                    double nx = lx * nlx + fx * nlz, nz = lz * nlx + fz * nlz;
                    double cpx = bx + lx * qx + fx * qz, cpz = bz + lz * qx + fz * qz;
                    if (physx && !(c.kind == treeKind && c.tree >= 0)) continue;
                    if (c.kind == treeKind && c.tree >= 0)
                    {
                        double vn = VelDot(s, cpx - s.pos.x, 0, cpz - s.pos.z, nx, 0, nz);
                        double momentum = vn * s.mass;
                        double need = w.treeKnockMomentum.IsCreated ? w.treeKnockMomentum[ci] : double.PositiveInfinity;
                        if (vn > 4 && momentum > need)
                        {
                            if (w.treeDown.IsCreated)
                            {
                                var td = w.treeDown;
                                td[ci] = 1;
                            }
                            knocks.Add(new TreeKnock { collider = ci, tree = c.tree, dirX = (float)nx, dirZ = (float)nz, vehicle = self });
                            double j = momentum * 0.25;
                            Impulse(ref s, -nx * j, 0, -nz * j, cpx - s.pos.x, 0, cpz - s.pos.z);
                            s.impact = Math.Max(s.impact, j * 0.5);
                            s.impactDir = new double3(-nx, 0, -nz);
                            continue;
                        }
                    }
                    if (physx) continue;
                    ResolveStatic(ref s, -nx, -nz, pen, cpx, s.pos.y, cpz, c.kind == treeKind ? 0.15 : 0.25);
                }
                else
                {
                    if (physx) continue;
                    // OBB vs OBB in the ground plane (separating axis test)
                    double cs = Math.Cos(c.rot), sn = Math.Sin(c.rot);
                    double c1x = cs, c1z = -sn, c2x = sn, c2z = cs;
                    double dx = c.x - bx, dz = c.z - bz;
                    double minOv = double.PositiveInfinity, nx = 0, nz = 0; int owner = 0;
                    bool sep = false;
                    for (int a = 0; a < 4; a++)
                    {
                        double ax, az; int o;
                        if (a == 0) { ax = lx; az = lz; o = 0; }
                        else if (a == 1) { ax = fx; az = fz; o = 0; }
                        else if (a == 2) { ax = c1x; az = c1z; o = 1; }
                        else { ax = c2x; az = c2z; o = 1; }
                        double ra = hw * Math.Abs(lx * ax + lz * az) + hl * Math.Abs(fx * ax + fz * az);
                        double rb = c.hw * Math.Abs(c1x * ax + c1z * az) + c.hd * Math.Abs(c2x * ax + c2z * az);
                        double dist = dx * ax + dz * az;
                        double ov = ra + rb - Math.Abs(dist);
                        if (ov <= 0) { sep = true; break; }
                        if (ov < minOv) { minOv = ov; double sg = dist >= 0 ? 1 : -1; nx = ax * sg; nz = az * sg; owner = o; }
                    }
                    if (sep) continue;
                    double cpx, cpz;
                    if (owner == 1)
                    {
                        // our corner pokes into their face
                        double sx = SignOr1(lx * nx + lz * nz), sz = SignOr1(fx * nx + fz * nz);
                        cpx = bx + lx * hw * sx + fx * hl * sz; cpz = bz + lz * hw * sx + fz * hl * sz;
                    }
                    else
                    {
                        double sx = -SignOr1(c1x * nx + c1z * nz), sz = -SignOr1(c2x * nx + c2z * nz);
                        cpx = c.x + c1x * c.hw * sx + c2x * c.hd * sz; cpz = c.z + c1z * c.hw * sx + c2z * c.hd * sz;
                    }
                    ResolveStatic(ref s, -nx, -nz, minOv, cpx, s.pos.y, cpz, 0.2);
                }
            }
        }

        static void ResolveStatic(ref BrvState s, double nx, double nz, double pen, double px, double py, double pz, double e)
        {
            double corr = Math.Min(pen, 0.5) * 0.8;
            s.pos.x += nx * corr; s.pos.z += nz * corr;
            double rx = px - s.pos.x, ry = py - s.pos.y, rz = pz - s.pos.z;
            double vn = VelDot(s, rx, ry, rz, nx, 0, nz);
            if (vn >= 0) return;
            double k = KAt(s, rx, ry, rz, nx, 0, nz);
            double j = -(1 + e) * vn / k;
            Impulse(ref s, nx * j, 0, nz * j, rx, ry, rz);
            // friction along the wall
            double3 wv = s.angVel, v = s.vel;
            double vx = v.x + wv.y * rz - wv.z * ry, vz = v.z + wv.x * ry - wv.y * rx;
            double vnn = vx * nx + vz * nz;
            vx -= nx * vnn; vz -= nz * vnn;
            double vt = Hypot(vx, vz);
            if (vt > 1e-3)
            {
                double tx = vx / vt, tz = vz / vt;
                double kt = KAt(s, rx, ry, rz, tx, 0, tz);
                double jt = Math.Min(vt / kt, 0.3 * j);
                Impulse(ref s, -tx * jt, 0, -tz * jt, rx, ry, rz);
            }
            if (j > s.impact) { s.impact = j; s.impactDir = new double3(nx, 0, nz); }
        }

        // ------------------------------------------------------------------ ruts (every 2nd step)

        static void Deform(ref BrvState s, in BrvDef d, in BrvWorld w, double dt, int self, ref NativeList<RutCall> ruts)
        {
            for (int i = 0; i < s.wheels.Length; i++)
            {
                var l = d.wheels[i];
                var wh = s.wheels[i];
                if (!wh.contact) continue;
                double soft = Softness(wh.surf);
                if (soft <= 0) continue;
                bool moving = Math.Abs(wh.vl) > 0.25 || wh.spin > 0.1;
                if (!moving && !s.driven) continue;
                bool tracked = d.simple;
                double radius = Math.Max(0.42, l.w * 0.5 + 0.24);
                if (wh.surf == SURF_MUD)
                {
                    // deep mud: heavy wheels sink further, spinning wheels dig themselves in fast
                    double md = WorldGridQueriesRef.MudAtJs(w.grid, wh.cp.x, wh.cp.z);
                    double depth = md * 0.55 * Clamp(0.5 + wh.load / 30000, 0.55, 1.0) * (tracked ? 0.4 : 1);
                    double rate = 0.006 + Math.Abs(wh.vl) * dt * 0.35 + wh.spin * 0.05 + (wh.sliding ? 0.01 : 0);
                    double dir = Math.Abs(wh.vl) > 0.2 ? Sign(wh.vl) : (s.gear < 0 ? -1 : 1);
                    ruts.Add(new RutCall
                    {
                        x = (float)wh.cp.x, z = (float)wh.cp.z, radius = (float)radius, depth = (float)depth, rate = (float)rate,
                        wearAdd = (float)(12 + wh.spin * 20), berm = tracked ? 0.2f : 0.6f, fx = (float)(wh.fd.x * dir), fz = (float)(wh.fd.z * dir), vehicle = self,
                    });
                    continue;
                }
                double depth2 = tracked ? 0.07 : Clamp(0.03 + wh.load / 36000, 0, 0.34);
                double rate2 = 0.002 + Math.Abs(wh.vl) * dt * (tracked ? 0.03 : 0.11) + wh.spin * (tracked ? 0.002 : 0.014) + (wh.sliding ? 0.005 : 0);
                ruts.Add(new RutCall
                {
                    x = (float)wh.cp.x, z = (float)wh.cp.z, radius = (float)radius, depth = (float)depth2, rate = (float)rate2,
                    wearAdd = (float)((moving ? 9 : 1) + wh.spin * 18), berm = tracked ? 0f : 0.45f, fx = 0, fz = 0, vehicle = self,
                });
            }
            // the dozer blade (bladeWork: cutBlade, depositCapped, relax) arrives with the dozer on BRV v2 in M7
        }

        // ------------------------------------------------------------------ heightfield ray for suspension

        /// <summary>rayDown: fast fixed-point solve when roughly upright, marching otherwise.</summary>
        static double RayDown(in BrvWorld w, double ox, double oy, double oz, double dx, double dy, double dz, double maxT)
        {
            if (dy < -0.5)
            {
                double t = (oy - HeightAt(w, ox, oz)) / -dy;
                for (int k = 0; k < 3; k++)
                {
                    if (t < 0) return 0;
                    if (t > maxT + 1.5) return -1;
                    t = (oy - HeightAt(w, ox + dx * t, oz + dz * t)) / -dy;
                }
                if (t > maxT) return -1;
                return Math.Max(0, t);
            }
            return WorldGridQueriesRef.RayTerrainJs(w.grid, ox, oy, oz, dx, dy, dz, maxT, 0.12);
        }

        // ------------------------------------------------------------------ vehicle vs vehicle

        /// <summary>collideVehicles(A, B): ground-plane SAT with impulses on both bodies. Returns the impulse.</summary>
        public static double CollideVehicles(ref BrvState A, in BrvDef da, ref BrvState B, in BrvDef db)
        {
            if (A.asleep && B.asleep) return 0;
            double dxc = B.pos.x - A.pos.x, dzc = B.pos.z - A.pos.z;
            double reach = Hypot(da.bhw, da.bhl) + Hypot(db.bhw, db.bhl);
            if (dxc * dxc + dzc * dzc > reach * reach) return 0;
            if (Math.Abs(B.pos.y - A.pos.y) > da.bhh + db.bhh + 1) return 0;
            UpdateBasis(ref A); UpdateBasis(ref B);
            FlatAxes(A, out double alx, out double alz, out double afx, out double afz);
            FlatAxes(B, out double blx, out double blz, out double bfx, out double bfz);
            double acx = A.pos.x + A.ez.x * da.bcz, acz = A.pos.z + A.ez.z * da.bcz;
            double bcx = B.pos.x + B.ez.x * db.bcz, bcz = B.pos.z + B.ez.z * db.bcz;
            double dx = bcx - acx, dz = bcz - acz;
            double minOv = double.PositiveInfinity, nx = 0, nz = 0; int owner = 0;
            for (int a = 0; a < 4; a++)
            {
                double x, z; int o;
                if (a == 0) { x = alx; z = alz; o = 0; }
                else if (a == 1) { x = afx; z = afz; o = 0; }
                else if (a == 2) { x = blx; z = blz; o = 1; }
                else { x = bfx; z = bfz; o = 1; }
                double ra = da.bhw * Math.Abs(alx * x + alz * z) + da.bhl * Math.Abs(afx * x + afz * z);
                double rb = db.bhw * Math.Abs(blx * x + blz * z) + db.bhl * Math.Abs(bfx * x + bfz * z);
                double dist = dx * x + dz * z;
                double ov = ra + rb - Math.Abs(dist);
                if (ov <= 0) return 0;
                if (ov < minOv) { minOv = ov; double sg = dist >= 0 ? 1 : -1; nx = x * sg; nz = z * sg; owner = o; }
            }
            double px, pz;
            if (owner == 1)
            {
                double sx = SignOr1(alx * nx + alz * nz), sz = SignOr1(afx * nx + afz * nz);
                px = acx + alx * da.bhw * sx + afx * da.bhl * sz; pz = acz + alz * da.bhw * sx + afz * da.bhl * sz;
            }
            else
            {
                double sx = -SignOr1(blx * nx + blz * nz), sz = -SignOr1(bfx * nx + bfz * nz);
                px = bcx + blx * db.bhw * sx + bfx * db.bhl * sz; pz = bcz + blz * db.bhw * sx + bfz * db.bhl * sz;
            }
            Wake(ref A); Wake(ref B);
            double ia = A.invMass, ib = B.invMass, tot = ia + ib;
            double corr = Math.Min(minOv, 0.5) * 0.8;
            A.pos.x -= nx * corr * ia / tot; A.pos.z -= nz * corr * ia / tot;
            B.pos.x += nx * corr * ib / tot; B.pos.z += nz * corr * ib / tot;
            double py = (A.pos.y + B.pos.y) * 0.5;
            double rax = px - A.pos.x, ray = py - A.pos.y, raz = pz - A.pos.z;
            double rbx = px - B.pos.x, rby = py - B.pos.y, rbz = pz - B.pos.z;
            double vn = VelDot(B, rbx, rby, rbz, nx, 0, nz) - VelDot(A, rax, ray, raz, nx, 0, nz);
            if (vn >= 0) return 0;
            double k = KAt(A, rax, ray, raz, nx, 0, nz) + KAt(B, rbx, rby, rbz, nx, 0, nz);
            double j = -(1.25) * vn / k;
            Impulse(ref A, -nx * j, 0, -nz * j, rax, ray, raz);
            Impulse(ref B, nx * j, 0, nz * j, rbx, rby, rbz);
            if (j > A.impact) { A.impact = j; A.impactDir = new double3(-nx, 0, -nz); }
            if (j > B.impact) { B.impact = j; B.impactDir = new double3(nx, 0, nz); }
            return j;
        }

        static void FlatAxes(in BrvState v, out double lx, out double lz, out double fx, out double fz)
        {
            lx = v.ex.x; lz = v.ex.z; double ll = Hypot(lx, lz); if (ll == 0) ll = 1; lx /= ll; lz /= ll;
            fx = v.ez.x; fz = v.ez.z; double fl = Hypot(fx, fz); if (fl == 0) fl = 1; fx /= fl; fz /= fl;
        }
    }
}
