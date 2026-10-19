using System;
using Unity.Collections;

namespace Backroads.Vehicles
{
    /// <summary>
    /// BRV v2 driveline layout (06 section 4.1-4.2), skeleton for M3: which shafts exist and how the rows join them.
    /// M2 builds it for a classic vehicle with estimated inertias (06 section 4.1 "typical inertias") so the solver can
    /// be exercised; the tuned values and the tyre rows (06 section 3.6) arrive with the v2 core in M3.
    /// </summary>
    public struct DrivelineDef
    {
        public double crankInertia, gearboxInputInertia, outputInertia, wheelInertia;
        public double clutchCapacityNm;
        public double finalDrive;
        public FixedList128Bytes<double> gearRatios;
        public byte drive;
        public int wheels;

        /// <summary>
        /// From a classic vehicle: the JS overall ratios split into gearbox x final drive (final drive = the top ratio,
        /// so top gear is 1:1), clutch capacity 1.5 x peak torque (06 section 4.1), inertias by vehicle class [est].
        /// </summary>
        public static DrivelineDef FromClassic(ClassicVehicleDef def)
        {
            var b = BrvDef.Create(def);
            if (b.ratios.Length == 0) throw new ArgumentException($"{def.id} has no gearbox");
            double fd = b.ratios[b.ratios.Length - 1];
            var d = new DrivelineDef
            {
                crankInertia = def.mass > 3000 ? 0.9 : def.mass > 1500 ? 0.35 : 0.2,
                gearboxInputInertia = 0.02,
                outputInertia = 0.03,
                wheelInertia = def.mass > 3000 ? 16 : def.wheelR > 0.4 ? 3.0 : 1.1,
                clutchCapacityNm = 1.5 * def.engineTorque,
                finalDrive = fd,
                drive = b.drive,
                wheels = b.wheels.Length,
            };
            for (int i = 0; i < b.ratios.Length; i++) d.gearRatios.Add(b.ratios[i] / fd);
            return d;
        }
    }

    /// <summary>Shaft indices of a built driveline.</summary>
    public struct DrivelineShafts
    {
        public int crank, gearboxIn, output;
        public FixedList64Bytes<int> wheels;
        public int clutchRow, gearRow;
    }

    public static class DrivelineBuilder
    {
        /// <summary>
        /// crank -(clutch)- gearbox input -(gear)- output -(open diff per driven axle)- wheels. Wheel pairs follow the
        /// V0 layout (front pair, rear pair); AWD splits through a locked centre (both axle diffs on the output shaft).
        /// </summary>
        public static DrivelineShafts Build(ref ShaftGraph g, in DrivelineDef d, int gear)
        {
            var s = new DrivelineShafts();
            s.crank = g.AddShaft(d.crankInertia);
            s.gearboxIn = g.AddShaft(d.gearboxInputInertia);
            s.output = g.AddShaft(d.outputInertia);
            for (int i = 0; i < d.wheels; i++) s.wheels.Add(g.AddShaft(d.wheelInertia));
            s.clutchRow = g.AddClutch(s.crank, s.gearboxIn, d.clutchCapacityNm);
            s.gearRow = g.AddGear(s.gearboxIn, s.output, d.gearRatios[Math.Max(0, Math.Min(d.gearRatios.Length - 1, gear - 1))]);
            bool front = d.drive == BrvDef.DriveFwd || d.drive == BrvDef.DriveAwd;
            bool rear = d.drive == BrvDef.DriveRwd || d.drive == BrvDef.DriveAwd;
            if (front && d.wheels >= 2) g.AddOpenDiff(s.output, s.wheels[0], s.wheels[1], d.finalDrive);
            if (rear && d.wheels >= 4) g.AddOpenDiff(s.output, s.wheels[2], s.wheels[3], d.finalDrive);
            return s;
        }
    }
}
