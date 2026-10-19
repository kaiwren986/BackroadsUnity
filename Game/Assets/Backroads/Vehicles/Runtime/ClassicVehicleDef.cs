using System;
using System.Collections.Generic;

namespace Backroads.Vehicles
{
    /// <summary>
    /// One vehicle of the browser game's DEFS table (js/vehicle.js, exported verbatim to CanonData/v1/gameplay/vehicles.json),
    /// plus the model META of js/vehicleModels.js. All lengths in metres, model space as in the JS (+Z forward, +X left).
    /// Values are the JS numbers (doubles). <see cref="ClassicVehicleDefs"/> holds the shipped table; EditMode
    /// Vehicle.ClassicDefsMatchCanon checks it field by field against vehicles.json, so the two never drift.
    /// The only intended differences are the display names (U9: the pickup is the Kestrel 4x4, the dozer the Badger D3)
    /// and the Unity-only engine ids.
    /// </summary>
    [Serializable]
    public sealed class ClassicVehicleDef
    {
        public string id;
        /// <summary>Shipped display name (fictional, PLAN 3.10).</summary>
        public string name;
        public string blurb;
        public int color;
        public double mass, inertia;
        public double boxW, boxH, boxL, boxY, boxZ;
        public double comY, comZ;
        public double wheelR, wheelW, wheelX, wheelFront, wheelRear;
        public bool wheelDual;
        /// <summary>Track road-wheel rows (dozer); null for wheeled vehicles.</summary>
        public double[] wheelRows;
        public double suspRest, suspK, suspC;
        /// <summary>"fwd", "rwd", "awd" or "tracked".</summary>
        public string drive;
        public double steer, steerRate;
        public bool hasEngine;
        public double engineTorque, engineIdle, engineRedline, engineVmax, engineFirst;
        public int engineGears;
        public bool hasSimple;
        public double simpleForce, simpleVmax;
        public bool hasBlade;
        public double bladeZ, bladeW, bladeY0;
        public double brake, handbrake, grip, drag, roll, antiRoll, rollInfluence;
        public int cap;
        public double camDist, camHeight, fuelUse;
        public int soundCyl;
        public double soundPitch, soundRough;
        public double mudGrip;
        public bool awdOnLow;
        public double seatX, seatY, seatZ;
        /// <summary>def.winchPull ?? 1.3 (no DEFS entry sets it).</summary>
        public double winchPull = 1.3;

        // ---- vehicleModels.js META ----
        /// <summary>Exhaust socket in model space (JS: ground at y = 0, +X left).</summary>
        public double exhaustX, exhaustY, exhaustZ;
        public bool exhaustUp;
        public double glassOpacity = 0.36;
        /// <summary>META.defaultPaint [from, to]: when the colour is the first, the model is painted the second.</summary>
        public int[] defaultPaint;
        /// <summary>META.cargo (pickup bed crates), or NaN.</summary>
        public double cargoY = double.NaN;
        public double[] cargoZ;

        // ---- Unity additions ----
        /// <summary>EngineCatalog ids to try in order (lane vs registers them; the first one that exists is used).</summary>
        public string[] engineIds;

        public bool IsTracked => drive == "tracked";
    }

    /// <summary>The shipped classic vehicles (browser game DEFS, VEHICLE_ORDER) with the U9 display names.</summary>
    public static class ClassicVehicleDefs
    {
        /// <summary>vehicle.js VEHICLE_ORDER (also the parking bay order at the Millbrook garage).</summary>
        public static readonly string[] Order = { "pickup", "hatch", "rally", "truck", "dozer" };

        /// <summary>The vehicles drivable in M2 (the dozer is built on BRV v2 in M7), in VEHICLE_ORDER.</summary>
        public static readonly string[] M2 = { "pickup", "hatch", "rally", "truck" };

        static Dictionary<string, ClassicVehicleDef> s_All;

        public static IReadOnlyDictionary<string, ClassicVehicleDef> All => s_All ??= Build();

        public static ClassicVehicleDef Get(string id) =>
            All.TryGetValue(id ?? "", out var d) ? d : throw new ArgumentException($"no classic vehicle '{id}'");

        static Dictionary<string, ClassicVehicleDef> Build()
        {
            var d = new Dictionary<string, ClassicVehicleDef>();
            d["hatch"] = new ClassicVehicleDef
            {
                id = "hatch", name = "Pip Hatchback", blurb = "Nippy little runabout. Parcels and fish.",
                color = 0x2e9fd4, mass = 1050, inertia = 1.2,
                boxW = 1.72, boxH = 1.12, boxL = 3.9, boxY = 0.86, boxZ = 0,
                comY = 0.5, comZ = 0.12,
                wheelR = 0.31, wheelW = 0.21, wheelX = 0.76, wheelFront = 1.26, wheelRear = -1.22,
                suspRest = 0.3, suspK = 30000, suspC = 3100,
                drive = "fwd", steer = 0.6, steerRate = 3.4,
                hasEngine = true, engineTorque = 175, engineIdle = 850, engineRedline = 6800, engineVmax = 41, engineGears = 5, engineFirst = 0.27,
                brake = 6500, handbrake = 5000, grip = 1.12, drag = 0.42, roll = 0.014, antiRoll = 9000, rollInfluence = 0.22,
                cap = 4, camDist = 6.4, camHeight = 2.1, fuelUse = 0.0008, soundCyl = 4, soundPitch = 1.12, soundRough = 0.18, mudGrip = 0.75,
                seatX = 0.38, seatY = 1.15, seatZ = -0.1,
                exhaustX = -0.48, exhaustY = 0.27, exhaustZ = -2.0,
                engineIds = new[] { "i4_vvt_a", "i4_synth" },
            };
            d["rally"] = new ClassicVehicleDef
            {
                id = "rally", name = "Sprint RS", blurb = "Rear-drive rally coupe. Loves gravel, hates cargo.",
                color = 0xf26a21, mass = 1180, inertia = 1.15,
                boxW = 1.84, boxH = 1.0, boxL = 4.2, boxY = 0.78, boxZ = 0,
                comY = 0.46, comZ = 0.0,
                wheelR = 0.33, wheelW = 0.26, wheelX = 0.82, wheelFront = 1.38, wheelRear = -1.3,
                suspRest = 0.3, suspK = 34000, suspC = 3600,
                drive = "rwd", steer = 0.62, steerRate = 4.2,
                hasEngine = true, engineTorque = 330, engineIdle = 950, engineRedline = 7600, engineVmax = 52, engineGears = 5, engineFirst = 0.25,
                brake = 8000, handbrake = 7000, grip = 1.2, drag = 0.38, roll = 0.012, antiRoll = 14000, rollInfluence = 0.18,
                cap = 2, camDist = 6.6, camHeight = 2.0, fuelUse = 0.0012, soundCyl = 4, soundPitch = 1.35, soundRough = 0.35, mudGrip = 1.0,
                seatX = 0.4, seatY = 1.0, seatZ = -0.25,
                exhaustX = -0.5, exhaustY = 0.3, exhaustZ = -2.2,
                engineIds = new[] { "f4_boxer_a" },
            };
            d["pickup"] = new ClassicVehicleDef
            {
                id = "pickup", name = "Kestrel 4x4", blurb = "All-wheel-drive pickup. Goes where roads do not.",
                color = 0xb3322c, mass = 1900, inertia = 1.3,
                boxW = 1.98, boxH = 1.35, boxL = 5.2, boxY = 1.3, boxZ = -0.1,
                comY = 0.82, comZ = 0.25,
                wheelR = 0.43, wheelW = 0.3, wheelX = 0.86, wheelFront = 1.62, wheelRear = -1.62,
                suspRest = 0.38, suspK = 38000, suspC = 4300,
                drive = "awd", steer = 0.58, steerRate = 3.0,
                hasEngine = true, engineTorque = 430, engineIdle = 750, engineRedline = 5600, engineVmax = 38, engineGears = 5, engineFirst = 0.24,
                brake = 9000, handbrake = 7000, grip = 1.08, drag = 0.62, roll = 0.016, antiRoll = 13000, rollInfluence = 0.25,
                cap = 8, camDist = 7.6, camHeight = 2.6, fuelUse = 0.0011, soundCyl = 6, soundPitch = 0.82, soundRough = 0.4, mudGrip = 1.35,
                seatX = 0.42, seatY = 1.55, seatZ = 0.35,
                exhaustX = -0.7, exhaustY = 0.62, exhaustZ = -2.88,
                cargoY = 0.885, cargoZ = new[] { -0.95, -1.81 },
                engineIds = new[] { "v8_cross_a" },
            };
            d["truck"] = new ClassicVehicleDef
            {
                id = "truck", name = "Hauler 7.5t", blurb = "Box truck. Big loads, big brakes, big mirrors.",
                color = 0xeeeeea, mass = 5400, inertia = 1.5,
                boxW = 2.45, boxH = 2.85, boxL = 7.9, boxY = 2.13, boxZ = -0.6,
                comY = 1.15, comZ = 0.1,
                wheelR = 0.5, wheelW = 0.34, wheelX = 1.0, wheelFront = 2.45, wheelRear = -2.15, wheelDual = true,
                suspRest = 0.32, suspK = 115000, suspC = 13000,
                drive = "rwd", steer = 0.6, steerRate = 2.3,
                hasEngine = true, engineTorque = 760, engineIdle = 650, engineRedline = 2700, engineVmax = 27, engineGears = 6, engineFirst = 0.17,
                brake = 26000, handbrake = 20000, grip = 0.98, drag = 1.5, roll = 0.012, antiRoll = 52000, rollInfluence = 0.35,
                cap = 24, camDist = 12.5, camHeight = 4.2, fuelUse = 0.0007, soundCyl = 6, soundPitch = 0.55, soundRough = 0.75, mudGrip = 1.25,
                awdOnLow = true,
                seatX = 0.55, seatY = 2.25, seatZ = 2.6,
                exhaustX = -1.05, exhaustY = 3.55, exhaustZ = 1.68, exhaustUp = true, glassOpacity = 0.88,
                defaultPaint = new[] { 0xeeeeea, 0x2f6f4f },
                engineIds = new[] { "v8_bigblock_a" },
            };
            d["dozer"] = new ClassicVehicleDef
            {
                id = "dozer", name = "Badger D3", blurb = "Tracked dozer. Q / Z raise and lower the blade.",
                color = 0xf2b20f, mass = 9000, inertia = 1.6,
                boxW = 2.4, boxH = 2.0, boxL = 3.5, boxY = 1.62, boxZ = -0.1,
                comY = 0.85, comZ = 0.15,
                wheelR = 0.42, wheelW = 0.6, wheelX = 1.12, wheelRows = new[] { 1.5, 0.75, 0, -0.75, -1.5 },
                suspRest = 0.32, suspK = 150000, suspC = 15000,
                drive = "tracked", steer = 0, steerRate = 2.5,
                hasSimple = true, simpleForce = 72000, simpleVmax = 3.8,
                hasBlade = true, bladeZ = 2.45, bladeW = 3.5, bladeY0 = 0.0,
                brake = 40000, handbrake = 40000, grip = 1.35, drag = 2.0, roll = 0.05, antiRoll = 0, rollInfluence = 0.3,
                cap = 0, camDist = 10.5, camHeight = 4.0, fuelUse = 0.0006, soundCyl = 6, soundPitch = 0.42, soundRough = 0.9, mudGrip = 1.6,
                seatX = 0.0, seatY = 2.3, seatZ = -0.5,
                exhaustX = 0.55, exhaustY = 2.95, exhaustZ = 1.1, exhaustUp = true,
                engineIds = new string[0],
            };
            return d;
        }
    }
}
