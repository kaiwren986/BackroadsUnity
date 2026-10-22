using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>One wheel (or track road wheel) of the vehicle.js layout, in model space relative to the COM.</summary>
    public struct BrvWheelDef
    {
        public double x, z;
        public bool steer, drive, hand, front;
        public int side;
        public double r, w;
        /// <summary>Suspension mount relative to the COM (JS: mx = x, my = mountModelY - com.y, mz = z - com.z).</summary>
        public double mx, my, mz;
        public double rest, k, c;
    }

    /// <summary>
    /// The constants of one classic vehicle in a blittable form for the Burst step (vehicle.js constructor: box,
    /// wheel layout, hull points, gear ratios). Built from a <see cref="ClassicVehicleDef"/> by <see cref="Create"/>.
    /// </summary>
    public struct BrvDef
    {
        public const int MaxWheels = 10;
        public const int MaxGears = 8;
        public const byte DriveFwd = 0, DriveRwd = 1, DriveAwd = 2, DriveTracked = 3;

        public double mass, inertia;
        public double boxW, boxH, boxL, boxY, boxZ;
        public double comY, comZ;
        public double wheelR, wheelW;
        public bool dual;
        public byte drive;
        public double steer, steerRate;
        public bool hasEngine;
        public double eTorque, eIdle, eRedline, eVmax, eFirst;
        public int eGears;
        public FixedList128Bytes<double> ratios;
        public double revRatio;
        public bool simple;
        public double simpleForce, simpleVmax;
        public bool hasBlade;
        public double brake, handbrake, grip, drag, roll, antiRoll, rollInfluence, fuelUse, mudGrip, winchPull;
        public bool awdOnLow;

        /// <summary>this.box: COM-relative centre and half extents.</summary>
        public double bcx, bcy, bcz, bhw, bhh, bhl;
        public double mountModelY;
        /// <summary>Static compression per wheel at rest (def.mass * G / wheels / k).</summary>
        public double c0;
        public FixedList4096Bytes<BrvWheelDef> wheels;
        /// <summary>14 hull points relative to the COM (8 corners at 96 %, bottom front/rear/sides/centre, top centre).</summary>
        public FixedList512Bytes<double3> hull;
        public int nDriveStatic;

        /// <summary>
        /// This vehicle steps with BRV v2 (shafts, MF tyres; <see cref="BrvSim.StepV2"/>). Set by BrvFleet.Add from
        /// <see cref="BrvFeatures.V2Driveline"/>; tracked vehicles ("simple") stay on V0 until the dozer moves to v2 (M7).
        /// </summary>
        public bool v2;
        public BrvV2Def v2def;

        public int WheelCount => wheels.Length;

        public static BrvDef Create(ClassicVehicleDef def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            var d = new BrvDef
            {
                mass = def.mass, inertia = def.inertia,
                boxW = def.boxW, boxH = def.boxH, boxL = def.boxL, boxY = def.boxY, boxZ = def.boxZ,
                comY = def.comY, comZ = def.comZ,
                wheelR = def.wheelR, wheelW = def.wheelW, dual = def.wheelDual,
                drive = def.drive switch { "fwd" => DriveFwd, "rwd" => DriveRwd, "awd" => DriveAwd, "tracked" => DriveTracked, _ => throw new ArgumentException($"{def.id}: drive '{def.drive}'") },
                steer = def.steer, steerRate = def.steerRate,
                hasEngine = def.hasEngine,
                eTorque = def.engineTorque, eIdle = def.engineIdle, eRedline = def.engineRedline, eVmax = def.engineVmax, eFirst = def.engineFirst,
                eGears = def.engineGears,
                simple = def.hasSimple, simpleForce = def.simpleForce, simpleVmax = def.simpleVmax,
                hasBlade = def.hasBlade,
                brake = def.brake, handbrake = def.handbrake, grip = def.grip, drag = def.drag, roll = def.roll,
                antiRoll = def.antiRoll, rollInfluence = def.rollInfluence, fuelUse = def.fuelUse, mudGrip = def.mudGrip,
                winchPull = def.winchPull, awdOnLow = def.awdOnLow,
            };
            // this.box
            d.bcx = 0;
            d.bcy = def.boxY - def.comY;
            d.bcz = def.boxZ - def.comZ;
            d.bhw = def.boxW / 2;
            d.bhh = def.boxH / 2;
            d.bhl = def.boxL / 2;

            // wheels (layout order as in the JS: left then right, front pair then rear pair; rows front to back)
            var layout = new FixedList4096Bytes<BrvWheelDef>();
            if (def.wheelRows != null)
            {
                foreach (var z in def.wheelRows)
                    for (int sx = 1; sx >= -1; sx -= 2)
                        layout.Add(new BrvWheelDef { x = sx * def.wheelX, z = z, steer = false, drive = true, hand = true, side = sx, front = false });
            }
            else
            {
                bool fd = def.drive == "fwd" || def.drive == "awd", rd = def.drive == "rwd" || def.drive == "awd";
                for (int sx = 1; sx >= -1; sx -= 2)
                    layout.Add(new BrvWheelDef { x = sx * def.wheelX, z = def.wheelFront, steer = true, drive = fd, hand = false, side = sx, front = true });
                for (int sx = 1; sx >= -1; sx -= 2)
                    layout.Add(new BrvWheelDef { x = sx * def.wheelX, z = def.wheelRear, steer = false, drive = rd, hand = true, side = sx, front = false });
            }
            if (layout.Length > MaxWheels) throw new ArgumentException($"{def.id}: {layout.Length} wheels");
            const double G = BrvSim.G;
            d.c0 = (def.mass * G / layout.Length) / def.suspK;
            d.mountModelY = def.wheelR + def.suspRest - d.c0;
            d.nDriveStatic = 0;
            for (int i = 0; i < layout.Length; i++)
            {
                var l = layout[i];
                l.r = def.wheelR; l.w = def.wheelW;
                l.mx = l.x; l.my = d.mountModelY - def.comY; l.mz = l.z - def.comZ;
                l.rest = def.suspRest; l.k = def.suspK; l.c = def.suspC;
                if (l.drive) d.nDriveStatic++;
                d.wheels.Add(l);
            }

            // hull points (local, relative to COM)
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                        d.hull.Add(new double3(sx * d.bhw * 0.96, d.bcy + sy * d.bhh, d.bcz + sz * d.bhl * 0.96));
            d.hull.Add(new double3(0, d.bcy - d.bhh, d.bcz + d.bhl));
            d.hull.Add(new double3(0, d.bcy - d.bhh, d.bcz - d.bhl));
            d.hull.Add(new double3(d.bhw, d.bcy - d.bhh, d.bcz));
            d.hull.Add(new double3(-d.bhw, d.bcy - d.bhh, d.bcz));
            d.hull.Add(new double3(0, d.bcy - d.bhh, d.bcz));
            d.hull.Add(new double3(0, d.bcy + d.bhh, d.bcz));

            // gearbox
            if (def.hasEngine)
            {
                if (def.engineGears > MaxGears) throw new ArgumentException($"{def.id}: {def.engineGears} gears");
                double wR = (def.engineRedline * Math.PI) / 30;
                double top = (wR * def.wheelR) / def.engineVmax, first = (wR * def.wheelR) / (def.engineVmax * def.engineFirst);
                double q = Math.Pow(first / top, 1.0 / (def.engineGears - 1));
                for (int i = 0; i < def.engineGears; i++) d.ratios.Add(top * Math.Pow(q, def.engineGears - 1 - i));
                d.revRatio = first * 0.95;
            }
            if (def.hasEngine && !def.hasSimple) d.v2def = BrvV2Def.FromClassic(def, d);
            return d;
        }
    }
}
