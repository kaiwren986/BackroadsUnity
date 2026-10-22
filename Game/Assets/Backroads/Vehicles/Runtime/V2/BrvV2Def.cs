using System;
using Unity.Collections;

namespace Backroads.Vehicles
{
    /// <summary>Tyre classes of research 06 section 3.7 (column order of its surface table).</summary>
    public enum TyreClass : byte { Road = 0, AllTerrain = 1, MudTerrain = 2, Truck = 3, Gravel = 4, Slick = 5 }

    /// <summary>One Magic Formula curve: mu(x) = D sin(C atan(B x - E (B x - atan(B x)))) (06 section 3.6).</summary>
    public struct MfCurve
    {
        public double B, C, D, E;
        /// <summary>Slip at the peak (kappa, or alpha in radians), from the 06 table.</summary>
        public double peak;

        public MfCurve(double b, double c, double d, double e, double peak) { B = b; C = c; D = d; E = e; this.peak = peak; }

        /// <summary>mu at slip x &gt;= 0 (peak value D).</summary>
        public double Mu(double x)
        {
            double bx = B * x;
            return D * Math.Sin(C * Math.Atan(bx - E * (bx - Math.Atan(bx))));
        }

        /// <summary>Slope at zero slip, B C D (the normalised slip or cornering stiffness, "CS" in 06).</summary>
        public double Stiffness => B * C * D;
    }

    /// <summary>
    /// The tyre and surface tables of research 06 section 3.7 [calc + tune]: longitudinal and lateral MF coefficients
    /// per tyre class (dry asphalt), the surface multipliers on D and the rolling resistance per browser-game surface
    /// (world.js SURF ids), and the load sensitivity pD2.
    /// </summary>
    public static class TyreTables
    {
        public static MfCurve Long(TyreClass c)
        {
            switch (c)
            {
                case TyreClass.Road: return new MfCurve(12.1, 1.65, 1.00, -0.17, 0.11);
                case TyreClass.Slick: return new MfCurve(13.3, 1.55, 1.65, -0.68, 0.10);
                case TyreClass.AllTerrain: return new MfCurve(10.9, 1.60, 0.92, -0.18, 0.13);
                case TyreClass.MudTerrain: return new MfCurve(9.9, 1.55, 0.85, -0.04, 0.16);
                case TyreClass.Truck: return new MfCurve(11.7, 1.60, 0.80, 0.05, 0.13);
                default: return new MfCurve(7.5, 1.50, 0.80, -0.13, 0.22);   // gravel
            }
        }

        public static MfCurve Lat(TyreClass c)
        {
            const double Deg = Math.PI / 180;
            switch (c)
            {
                case TyreClass.Road: return new MfCurve(12.8, 1.40, 0.95, -0.63, 7.5 * Deg);
                case TyreClass.Slick: return new MfCurve(12.9, 1.45, 1.60, -1.27, 6.0 * Deg);
                case TyreClass.AllTerrain: return new MfCurve(10.9, 1.35, 0.88, -0.89, 9.0 * Deg);
                case TyreClass.MudTerrain: return new MfCurve(9.4, 1.30, 0.82, -1.13, 11.0 * Deg);
                case TyreClass.Truck: return new MfCurve(9.4, 1.30, 0.78, -1.14, 11.0 * Deg);
                default: return new MfCurve(7.7, 1.25, 0.78, -1.18, 15.0 * Deg);   // gravel
            }
        }

        /// <summary>Load sensitivity pD2 (06 section 3.7): road -0.08, AT -0.08, truck -0.05, slick -0.15.</summary>
        public static double LoadSensitivity(TyreClass c) => c == TyreClass.Truck ? -0.05 : c == TyreClass.Slick ? -0.15 : -0.08;

        /// <summary>
        /// Multiplier on D for a world.js surface id (0 grass, 1 dirt, 2 sand, 3 rock, 4 road, 5 gravel, 6 concrete,
        /// 7 track, 8 mud). Mud uses the browser game's GRIP ratio (0.44 / 1.08) times the vehicle's mudGrip (the "lug
        /// factor" of 06 section 3.7) until the soil model (06 section 5.3) arrives.
        /// </summary>
        public static double SurfaceMu(int surf, TyreClass c, double mudGrip)
        {
            int k = (int)c;
            switch (surf)
            {
                case 4: return Pick(k, 1.0, 0.95, 0.9, 1.0, 0.85, 1.0);
                case 6: return Pick(k, 0.97, 0.93, 0.88, 0.97, 0.85, 0.95);
                case 5: return Pick(k, 0.72, 0.80, 0.82, 0.75, 1.0, 0.55);
                case 7: return Pick(k, 0.75, 0.85, 0.88, 0.78, 1.0, 0.55);
                case 0: return Pick(k, 0.62, 0.72, 0.80, 0.65, 0.85, 0.45);
                case 1: return Pick(k, 0.68, 0.80, 0.85, 0.70, 0.95, 0.50);
                case 2: return Pick(k, 0.55, 0.68, 0.70, 0.58, 0.75, 0.40);
                case 3: return Pick(k, 0.85, 0.92, 0.95, 0.85, 0.9, 0.80);
                case 8: return (0.44 / 1.08) * mudGrip;
                default: return Pick(k, 0.62, 0.72, 0.80, 0.65, 0.85, 0.45);
            }
        }

        /// <summary>Rolling resistance coefficient C_rr per surface (06 section 3.7); mud resistance is the JS sinkage term instead.</summary>
        public static double RollingResistance(int surf, TyreClass c)
        {
            switch (surf)
            {
                case 4: return c == TyreClass.Truck ? 0.006 : 0.012;
                case 6: return 0.012;
                case 5: return 0.020;
                case 7: return 0.025;
                case 0: return 0.05;
                case 1: return 0.04;
                case 2: return 0.08;
                case 3: return 0.015;
                case 8: return 0.0;
                default: return 0.05;
            }
        }

        static double Pick(int k, double a, double b, double c, double d, double e, double f) =>
            k == 0 ? a : k == 1 ? b : k == 2 ? c : k == 3 ? d : k == 4 ? e : f;
    }

    /// <summary>
    /// The BRV v2 constants of one vehicle (blittable, inside <see cref="BrvDef"/>): tyres, shaft inertias, clutch,
    /// gearbox ratios (the browser game's overall ratios split into gearbox x final drive, so v2 shifts and tops out
    /// where V0 does), brakes with ABS, the torque-map engine of M3 (the JS torque curve on a real crank shaft) and
    /// the solver settings (06 sections 3.4, 3.7, 4.1, 7). Values marked [est] / [tune] in 06 are starting points.
    /// </summary>
    public struct BrvV2Def
    {
        public TyreClass tyre;
        public MfCurve mfLong, mfLat;
        public double pD2;
        /// <summary>Rated static load per wheel, N (mass g / wheels).</summary>
        public double fz0;
        /// <summary>Vehicle grip multiplier on D (1 = the 06 tables as they are).</summary>
        public double gripScale;

        public double crankInertia, gearboxInputInertia, outputInertia;
        public double wheelInertiaFront, wheelInertiaRear;
        public double clutchCapacityNm;
        public double finalDrive;
        /// <summary>Gearbox ratios relative to the final drive (top gear 1:1), forward gears.</summary>
        public FixedList128Bytes<double> gearRatios;
        /// <summary>Reverse gearbox ratio relative to the final drive (positive; the sign is applied by the gear).</summary>
        public double reverseRatio;
        public double lowRange;

        /// <summary>Foot brake and handbrake torque per wheel, N m (the JS forces times the wheel radius).</summary>
        public double brakeTorque, handbrakeTorque;
        public bool abs;
        /// <summary>Wheel bearing and driveline drag per wheel when not braked, N m.</summary>
        public double bearingTorque;

        /// <summary>Torque-map engine (M3; engine-sim's indicated torque replaces it in M4).</summary>
        public double engineTorque, idleRpm, redlineRpm, drivelineEfficiency;

        public int substeps, iterations;
        public double warmStart;
        /// <summary>Low-speed floor of the slip reference speed, m/s (V_LOW, 06 section 3.6).</summary>
        public double vLow;

        public bool lockerFront, lockerRear;

        public static BrvV2Def FromClassic(ClassicVehicleDef def, in BrvDef b)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            var t = def.id switch
            {
                "hatch" => TyreClass.Road,
                "rally" => TyreClass.Gravel,
                "pickup" => TyreClass.AllTerrain,
                "truck" => TyreClass.Truck,
                _ => def.mass > 3000 ? TyreClass.Truck : TyreClass.AllTerrain,
            };
            var v = new BrvV2Def
            {
                tyre = t,
                mfLong = TyreTables.Long(t),
                mfLat = TyreTables.Lat(t),
                pD2 = TyreTables.LoadSensitivity(t),
                fz0 = def.mass * BrvSim.G / Math.Max(1, b.wheels.Length),
                gripScale = 1.0,
                // 06 section 7.1 wheel inertias; crank includes the flywheel [est]
                crankInertia = def.id switch { "hatch" => 0.15, "rally" => 0.2, "pickup" => 0.35, "truck" => 0.9, _ => def.mass > 3000 ? 0.9 : 0.3 },
                gearboxInputInertia = def.mass > 3000 ? 0.05 : 0.02,
                outputInertia = def.mass > 3000 ? 0.08 : 0.03,
                wheelInertiaFront = def.id switch { "hatch" => 1.1, "rally" => 1.2, "pickup" => 3.7, "truck" => 9.0, _ => def.mass > 3000 ? 9.0 : 3.0 },
                wheelInertiaRear = def.id switch { "hatch" => 1.1, "rally" => 1.2, "pickup" => 3.7, "truck" => def.wheelDual ? 18.0 : 9.0, _ => def.mass > 3000 ? 9.0 : 3.0 },
                clutchCapacityNm = 1.5 * def.engineTorque,
                lowRange = LegacyJsEngine.LowRangeFactor,
                brakeTorque = def.brake * def.wheelR,
                handbrakeTorque = def.handbrake * def.wheelR,
                abs = true,
                bearingTorque = 2.0,
                engineTorque = def.engineTorque,
                idleRpm = def.engineIdle,
                redlineRpm = def.engineRedline,
                drivelineEfficiency = LegacyJsEngine.DrivelineEfficiency,
                substeps = 4,
                iterations = 12,
                warmStart = 0.9,
                vLow = 0.5,
                lockerFront = def.drive == "awd" || def.drive == "fwd",
                lockerRear = def.drive == "awd" || def.drive == "rwd",
            };
            if (b.ratios.Length > 0)
            {
                double fd = b.ratios[b.ratios.Length - 1];
                v.finalDrive = fd;
                for (int i = 0; i < b.ratios.Length; i++) v.gearRatios.Add(b.ratios[i] / fd);
                v.reverseRatio = b.revRatio / fd;
            }
            return v;
        }
    }
}
