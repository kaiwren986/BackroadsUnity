using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// BRV v2 head start (T2.3, behind <see cref="BrvFeatures.V2Driveline"/>): every rotating part is a 1-D shaft
    /// (inertia, angular velocity, angle), and the couplings between them (clutch, gears, differentials, brakes, and in
    /// M3 the tyres to the chassis) are rows of one projected Gauss-Seidel solver (06 sections 3.4, 3.8, 4.1).
    /// </summary>
    public struct Shaft
    {
        /// <summary>Inertia, kg m^2 (> 0).</summary>
        public double inertia;
        public double omega;
        public double angle;
        /// <summary>External torque applied explicitly each substep (engine indicated torque, starter, ...), N m.</summary>
        public double torque;

        public double AngularMomentum => inertia * omega;
    }

    /// <summary>The chassis as rows see it (06 section 3.8): mass, world inverse inertia, linear and angular velocity.</summary>
    public struct RowBody
    {
        public double invMass;
        /// <summary>World-space inverse inertia tensor (columns).</summary>
        public double3x3 invInertia;
        public double3 linVel, angVel;
    }

    /// <summary>
    /// One soft, bounded velocity constraint (06 section 3.6): J v + (gamma / h) lambda = bias, lambda clamped to
    /// [lo, hi] (impulse per substep). J spans the chassis (lin, ang) and up to three shafts. Warm-started lambda.
    /// </summary>
    public struct Row
    {
        public const int MaxShafts = 3;
        public double3 lin, ang;
        public int s0, s1, s2;
        public double j0, j1, j2;
        public int shafts;
        public double bias;
        /// <summary>Compliance over the substep, gamma / h (0 = rigid).</summary>
        public double gammaOverH;
        public double lo, hi;
        public double effMass;
        public double lambda;
        /// <summary>Row kind (debugging, reports).</summary>
        public RowKind kind;

        public static Row ShaftRow(RowKind kind, int a, double ja, int b = -1, double jb = 0, int c = -1, double jc = 0, double lo = double.NegativeInfinity, double hi = double.PositiveInfinity)
        {
            var r = new Row { kind = kind, s0 = a, j0 = ja, s1 = b, j1 = jb, s2 = c, j2 = jc, lo = lo, hi = hi };
            r.shafts = c >= 0 ? 3 : b >= 0 ? 2 : 1;
            return r;
        }
    }

    public enum RowKind : byte { Custom, Clutch, Gear, DiffOpen, DiffLock, Brake, RollingResistance, TyreLong, TyreLat }

    /// <summary>
    /// The shaft graph of one vehicle and its PGS row solver (06 section 3.8 pseudocode): external torques are
    /// integrated explicitly, then <see cref="Iterations"/> sweeps over the rows apply bounded impulses to the shafts
    /// (and the chassis, when a row has a chassis Jacobian). Burst-friendly (NativeLists, plain math).
    /// </summary>
    public struct ShaftGraph : IDisposable
    {
        public NativeList<Shaft> Shafts;
        public NativeList<Row> Rows;
        public int Iterations;
        /// <summary>Warm start: lambda of the previous substep times this (0.9 in 06 section 3.4).</summary>
        public double WarmStart;

        public static ShaftGraph Create(Allocator allocator, int iterations = 12)
        {
            if (!BrvFeatures.V2Driveline) throw new InvalidOperationException("BRV v2 is behind the " + BrvFeatures.V2Flag + " flag (off by default in M2)");
            return new ShaftGraph
            {
                Shafts = new NativeList<Shaft>(8, allocator),
                Rows = new NativeList<Row>(16, allocator),
                Iterations = iterations,
                WarmStart = 0.9,
            };
        }

        public int AddShaft(double inertia, double omega = 0)
        {
            if (!(inertia > 0)) throw new ArgumentException("shaft inertia must be > 0");
            Shafts.Add(new Shaft { inertia = inertia, omega = omega });
            return Shafts.Length - 1;
        }

        public int AddRow(Row r)
        {
            Rows.Add(r);
            return Rows.Length - 1;
        }

        /// <summary>Clutch (06 section 4.1): omega_a - omega_b = 0, bounded by the torque capacity times engagement.</summary>
        public int AddClutch(int a, int b, double capacityNm, double engagement = 1) =>
            AddRow(Row.ShaftRow(RowKind.Clutch, a, 1, b, -1, lo: -capacityNm * engagement, hi: capacityNm * engagement));

        /// <summary>Gear (06 section 4.1): omega_in - ratio * omega_out = 0, unbounded.</summary>
        public int AddGear(int input, int output, double ratio) => AddRow(Row.ShaftRow(RowKind.Gear, input, 1, output, -ratio));

        /// <summary>
        /// Open differential with its final drive: omega_pinion - ratio * (omega_l + omega_r) / 2 = 0 (the carrier turns at the
        /// mean wheel speed, the pinion ratio times faster); the equal torque split falls out of the Jacobian (06 section 4.1).
        /// </summary>
        public int AddOpenDiff(int pinion, int left, int right, double ratio) => AddRow(Row.ShaftRow(RowKind.DiffOpen, pinion, 1, left, -0.5 * ratio, right, -0.5 * ratio));

        /// <summary>Locker / LSD row: omega_l - omega_r = 0, bounded by the lock torque.</summary>
        public int AddDiffLock(int left, int right, double lockNm = double.PositiveInfinity) => AddRow(Row.ShaftRow(RowKind.DiffLock, left, 1, right, -1, lo: -lockNm, hi: lockNm));

        /// <summary>Brake: omega = 0, bounded by the brake torque.</summary>
        public int AddBrake(int shaft, double torqueNm) => AddRow(Row.ShaftRow(RowKind.Brake, shaft, 1, lo: -torqueNm, hi: torqueNm));

        /// <summary>
        /// One substep of length h: explicit external torques, then PGS. Row bounds are torques (N m) or forces (N);
        /// they become impulses here (x h). Returns the summed |delta lambda| of the last sweep (convergence).
        /// </summary>
        public double Substep(double h, ref RowBody body, bool hasBody)
        {
            for (int i = 0; i < Shafts.Length; i++)
            {
                ref var s = ref Shafts.ElementAt(i);
                s.omega += s.torque * h / s.inertia;
            }
            for (int r = 0; r < Rows.Length; r++)
            {
                ref var row = ref Rows.ElementAt(r);
                double k = row.gammaOverH;
                if (hasBody)
                {
                    k += math.dot(row.lin, row.lin) * body.invMass;
                    k += math.dot(row.ang, math.mul(body.invInertia, row.ang));
                }
                if (row.shafts > 0) k += row.j0 * row.j0 / Shafts[row.s0].inertia;
                if (row.shafts > 1) k += row.j1 * row.j1 / Shafts[row.s1].inertia;
                if (row.shafts > 2) k += row.j2 * row.j2 / Shafts[row.s2].inertia;
                row.effMass = k > 0 ? 1 / k : 0;
                row.lambda *= WarmStart;
                double lo = row.lo * h, hi = row.hi * h;
                row.lambda = math.clamp(row.lambda, lo, hi);
                if (row.lambda != 0) Apply(ref row, row.lambda, ref body, hasBody);
            }
            double last = 0;
            for (int it = 0; it < Iterations; it++)
            {
                last = 0;
                for (int r = 0; r < Rows.Length; r++)
                {
                    ref var row = ref Rows.ElementAt(r);
                    double jv = Dot(row, body, hasBody);
                    double dl = -(jv - row.bias + row.gammaOverH * row.lambda) * row.effMass;
                    double nl = math.clamp(row.lambda + dl, row.lo * h, row.hi * h);
                    double d = nl - row.lambda;
                    if (d != 0) Apply(ref row, d, ref body, hasBody);
                    row.lambda = nl;
                    last += math.abs(d);
                }
            }
            for (int i = 0; i < Shafts.Length; i++)
            {
                ref var s = ref Shafts.ElementAt(i);
                s.angle += s.omega * h;
            }
            return last;
        }

        double Dot(in Row r, in RowBody body, bool hasBody)
        {
            double v = 0;
            if (hasBody) v += math.dot(r.lin, body.linVel) + math.dot(r.ang, body.angVel);
            if (r.shafts > 0) v += r.j0 * Shafts[r.s0].omega;
            if (r.shafts > 1) v += r.j1 * Shafts[r.s1].omega;
            if (r.shafts > 2) v += r.j2 * Shafts[r.s2].omega;
            return v;
        }

        void Apply(ref Row r, double impulse, ref RowBody body, bool hasBody)
        {
            if (hasBody)
            {
                body.linVel += r.lin * (impulse * body.invMass);
                body.angVel += math.mul(body.invInertia, r.ang * impulse);
            }
            if (r.shafts > 0) { ref var s = ref Shafts.ElementAt(r.s0); s.omega += r.j0 * impulse / s.inertia; }
            if (r.shafts > 1) { ref var s = ref Shafts.ElementAt(r.s1); s.omega += r.j1 * impulse / s.inertia; }
            if (r.shafts > 2) { ref var s = ref Shafts.ElementAt(r.s2); s.omega += r.j2 * impulse / s.inertia; }
        }

        public double TotalAngularMomentum()
        {
            double l = 0;
            for (int i = 0; i < Shafts.Length; i++) l += Shafts[i].AngularMomentum;
            return l;
        }

        public void Dispose()
        {
            if (Shafts.IsCreated) Shafts.Dispose();
            if (Rows.IsCreated) Rows.Dispose();
        }
    }
}
