using System;
using Backroads.World;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// The BRV test track (PLAN 6.1 M3, card T3.5): an analytic heightfield with the features the vehicle gates drive on,
    /// built straight into WorldGrid layers (no canon). JS world space, metres; the ground is at <see cref="Ground"/>
    /// (above the water line), grass unless a feature says otherwise.
    /// <list type="bullet">
    /// <item>Flat asphalt pad 300 x 400 m (launch lane along its east edge, ABS stops, turning circles) with the 30 m
    /// skidpad on it;</item>
    /// <item>four asphalt ramp lanes at 10, 20, 30 and 35 % (40 m up, a 12 m plateau, 40 m down);</item>
    /// <item>a concrete lane of ten 150 mm steps every 3 m;</item>
    /// <item>a mud patch 40 x 50 m, 0.6 m of mud;</item>
    /// <item>a wheel pit (1 m across, 0.5 m along z, 2 m deep) in an asphalt apron: park one wheel over it and the
    /// body settles on its corner beside the pit with that wheel hanging free (a wheel in the air, as on a jack).</item>
    /// </list>
    /// The grid has a WorldGridLayout of its own (<see cref="Layout"/>, T3.1); the scene BrvTestTrack.unity drives on it.
    /// </summary>
    public static class BrvTestTrack
    {
        public const double Ground = 5.0;

        // flat pad (asphalt)
        public const double PadX0 = -150, PadX1 = 150, PadZ0 = -500, PadZ1 = -100;
        /// <summary>The launch lane runs north (+z) along x = LaunchX, clear of the skidpad and the ABS lane.</summary>
        public const double LaunchX = 120;
        public static readonly double2 PadStart = new double2(0, -400);
        public static readonly double2 SkidpadCentre = new double2(0, -260);
        public const double SkidpadRadius = 30;

        // ramps: lane centres, start, length, plateau
        public static readonly double[] Grades = { 0.10, 0.20, 0.30, 0.35 };
        public const double RampLaneX0 = -135, RampLanePitch = 30, RampLaneHalfWidth = 10;
        public const double RampZ0 = 20, RampLength = 40, RampPlateau = 12;
        public const double LanesZ0 = 0, LanesZ1 = 130;

        // steps (concrete)
        public const double StepsX = 0, StepsHalfWidth = 10, StepsZ0 = 20, StepPitch = 3, StepHeight = 0.15;
        public const int StepCount = 10;

        // mud
        public const double MudX0 = 30, MudX1 = 70, MudZ0 = 20, MudZ1 = 70, MudDepth = 0.6;

        // wheel pit in an asphalt apron
        public static readonly double2 PitCentre = new double2(100, 40);
        public const double PitHalfX = 0.5, PitHalfZ = 0.25, PitDepth = 2.0;
        public const double ApronX0 = 85, ApronX1 = 115, ApronZ0 = 25, ApronZ1 = 55;

        public static double RampLaneX(int i) => RampLaneX0 + RampLanePitch * i;

        /// <summary>The middle of ramp lane i's climb (where hill hold parks).</summary>
        public static double2 RampMid(int i) => new double2(RampLaneX(i), RampZ0 + RampLength / 2);

        public static double Height(double x, double z)
        {
            // ramp lanes
            for (int i = 0; i < Grades.Length; i++)
            {
                if (Math.Abs(x - RampLaneX(i)) > RampLaneHalfWidth) continue;
                double g = Grades[i], top = g * RampLength;
                double u = z - RampZ0;
                if (u <= 0) return Ground;
                if (u <= RampLength) return Ground + g * u;
                u -= RampLength;
                if (u <= RampPlateau) return Ground + top;
                u -= RampPlateau;
                if (u <= RampLength) return Ground + top - g * u;
                return Ground;
            }
            // steps
            if (Math.Abs(x - StepsX) <= StepsHalfWidth && z >= StepsZ0)
            {
                int k = (int)Math.Floor((z - StepsZ0) / StepPitch);
                if (k <= StepCount) return Ground + StepHeight * Math.Min(k, StepCount);
                // after the top step: 10 m level, then down at 10 % to the ground
                double top = StepHeight * StepCount, u = z - (StepsZ0 + StepPitch * (StepCount + 1) + 10);
                return u <= 0 ? Ground + top : Ground + Math.Max(0, top - 0.1 * u);
            }
            // pit
            if (Math.Abs(x - PitCentre.x) <= PitHalfX && Math.Abs(z - PitCentre.y) <= PitHalfZ) return Ground - PitDepth;
            return Ground;
        }

        /// <summary>world.js SURF id at a point: 4 asphalt, 6 concrete, 8 mud, else 0 grass.</summary>
        public static byte Surface(double x, double z)
        {
            if (x >= PadX0 && x <= PadX1 && z >= PadZ0 && z <= PadZ1) return 4;
            if (z >= LanesZ0 && z <= LanesZ1)
                for (int i = 0; i < Grades.Length; i++)
                    if (Math.Abs(x - RampLaneX(i)) <= RampLaneHalfWidth) return 4;
            if (Math.Abs(x - StepsX) <= StepsHalfWidth && z >= LanesZ0 && z <= LanesZ1) return 6;
            if (x >= MudX0 && x <= MudX1 && z >= MudZ0 && z <= MudZ1) return 8;
            if (x >= ApronX0 && x <= ApronX1 && z >= ApronZ0 && z <= ApronZ1) return 4;
            return 0;
        }

        /// <summary>
        /// The track's own grid layout (T3.1): 1,024 m square at 0.5 m (2,049 vertices), tint every 8 m. The same size
        /// as canon v1, but nothing in BRV assumes it: the world edge and the collider cells come from the grid's layout.
        /// </summary>
        public static WorldGridLayout Layout => WorldGridLayout.Create(2049, 0.5f, 512f, 129, 8f);

        /// <summary>Builds the track as WorldGrid layers on <see cref="Layout"/> (the caller disposes the grid).</summary>
        public static WorldGrid Build() => Build(Layout);

        /// <summary>Builds the track on any layout that holds it (features outside the grid are cut off).</summary>
        public static WorldGrid Build(WorldGridLayout layout)
        {
            int vn = layout.VN, count = layout.Count;
            var h = new NativeArray<float>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var surf = new NativeArray<byte>(count, Allocator.Temp);
            var mud = new NativeArray<byte>(count, Allocator.Temp);
            var wear = new NativeArray<byte>(count, Allocator.Temp);
            var roadd = new NativeArray<byte>(count, Allocator.Temp);
            var tint = new NativeArray<float>(layout.TintN * layout.TintN * 2, Allocator.Temp);
            try
            {
                byte mudByte = (byte)Math.Round(MudDepth / WorldGrid.MudMax * 255.0);
                for (int j = 0; j < vn; j++)
                {
                    double z = layout.X(j);
                    for (int i = 0; i < vn; i++)
                    {
                        double x = layout.X(i);
                        int k = j * vn + i;
                        h[k] = (float)Height(x, z);
                        byte su = Surface(x, z);
                        surf[k] = su;
                        mud[k] = su == 8 ? mudByte : (byte)0;
                        roadd[k] = su == 4 || su == 6 ? (byte)0 : (byte)255;
                    }
                }
                return WorldGrid.FromLayers(layout, h, h, surf, wear, roadd, mud, tint);
            }
            finally
            {
                h.Dispose(); surf.Dispose(); mud.Dispose(); wear.Dispose(); roadd.Dispose(); tint.Dispose();
            }
        }

        /// <summary>A BrvWorldData on a fresh track (no static colliders). Dispose both the data and its Grid.</summary>
        public static BrvWorldData BuildWorld() => BrvWorldData.Create(Build(), null, null);
    }

    public static partial class BrvSim
    {
        /// <summary>
        /// place() on a slope: like <see cref="Place"/>, but the body is aligned with the ground under its centre (up =
        /// the ground normal, forward = the heading projected onto the ground), so it lands on all wheels at once.
        /// </summary>
        public static void PlaceAligned(ref BrvState s, in BrvDef d, in BrvWorld w, double x, double z, double yaw, double extra = 0.0)
        {
            double3 n = NormalAt(w, x, z);
            double3 f = new double3(Math.Sin(yaw), 0, Math.Cos(yaw));
            f -= n * Dot(f, n);
            f /= Length(f);
            double3 l = new double3(n.y * f.z - n.z * f.y, n.z * f.x - n.x * f.z, n.x * f.y - n.y * f.x);   // ex = ey x ez
            s.quat = FromBasis(l, n, f);
            double y = HeightAt(w, x, z);
            s.pos = new double3(x, y, z) + n * (d.comY + 0.05 + extra);
            s.vel = 0;
            s.angVel = 0;
            s.prevPos = s.pos;
            s.prevQuat = s.quat;
            s.asleep = false;
            s.sleepT = 0;
            UpdateBasis(ref s);
        }

        /// <summary>The quaternion (x, y, z, w) whose rotation has the columns ex, ey, ez.</summary>
        public static double4 FromBasis(double3 ex, double3 ey, double3 ez)
        {
            double m00 = ex.x, m10 = ex.y, m20 = ex.z, m01 = ey.x, m11 = ey.y, m21 = ey.z, m02 = ez.x, m12 = ez.y, m22 = ez.z;
            double tr = m00 + m11 + m22, qx, qy, qz, qw;
            if (tr > 0)
            {
                double S = Math.Sqrt(tr + 1) * 2;
                qw = 0.25 * S; qx = (m21 - m12) / S; qy = (m02 - m20) / S; qz = (m10 - m01) / S;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double S = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                qw = (m21 - m12) / S; qx = 0.25 * S; qy = (m01 + m10) / S; qz = (m02 + m20) / S;
            }
            else if (m11 > m22)
            {
                double S = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                qw = (m02 - m20) / S; qx = (m01 + m10) / S; qy = 0.25 * S; qz = (m12 + m21) / S;
            }
            else
            {
                double S = Math.Sqrt(1 + m22 - m00 - m11) * 2;
                qw = (m10 - m01) / S; qx = (m02 + m20) / S; qy = (m12 + m21) / S; qz = 0.25 * S;
            }
            double len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            return new double4(qx / len, qy / len, qz / len, qw / len);
        }
    }
}
