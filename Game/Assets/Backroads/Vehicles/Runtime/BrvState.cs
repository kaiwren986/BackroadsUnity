using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>vehicle.js this.ctrl: what the vehicle model acts on in a step (set by drive(), park() and settle()).</summary>
    public struct BrvControls
    {
        public double throttle, brake, steer, handbrake;
    }

    /// <summary>
    /// One wheel's state (vehicle.js this.wheels[i] minus the layout constants, which live in <see cref="BrvWheelDef"/>).
    /// The scratch fields of tyres() (sd .. vsPre) are kept too, so a snapshot is the complete object state.
    /// </summary>
    public struct BrvWheel
    {
        public bool contact, sliding;
        public byte surf;
        public double len, comp, load;
        public double3 cp, n;
        public double omega, angle, spin, skid, vl, mudRes;
        public double3 fd, sd, arm, rS;
        public double kF, kS, accL, accS, longLim, latLim, vsPre;

        // ---- BRV v2 only (V0 never reads or writes these; snapshot v2 block) ----
        /// <summary>Warm-start impulses of the wheel's rows: brake + rolling resistance, tyre long, tyre lat, mud drag.</summary>
        public double lamB, lamT, lamL, lamM;
        /// <summary>Low-speed anchors: tyre contact deflection along fd and sd (m), brake slip angle (rad).</summary>
        public double anchorL, anchorS, anchorB;
        /// <summary>ABS: how much of the brake torque is released (0 = full brake).</summary>
        public double absCut;
        /// <summary>Longitudinal slip ratio and slip angle of the last substep (diagnostics, ABS, HUD).</summary>
        public double kappa, alpha;
    }

    /// <summary>
    /// BRV v2 driveline state (06 section 4.1): the crank and the gearbox output shaft (the gearbox input is lumped into
    /// the output while a gear is engaged), the auto-clutch engagement and the warm-start impulses of the driveline
    /// rows. Wheel shaft speeds are the wheels' own omega.
    /// </summary>
    public struct BrvDriveline
    {
        /// <summary>The v2 block is valid (false after a v1 snapshot or a V0 state: the first v2 step syncs it).</summary>
        public bool init;
        public double crankOmega, crankAngle, driveOmega;
        /// <summary>Clutch engagement 0..1 (auto-clutch, 06 section 4.2.1).</summary>
        public double clutch;
        public double lamClutch, lamDiffFront, lamDiffRear;
        /// <summary>Selectable lockers (the locked axle is lumped into one shaft, 06 section 4.1).</summary>
        public bool lockFront, lockRear;
        /// <summary>ABS off (driver aid switch); ABS is on by default where the vehicle has it.</summary>
        public bool absOff;
        /// <summary>Engine torque at the crank in the last substep (N m, for reports).</summary>
        public double engineTorque;
    }

    /// <summary>The winch rope (vehicle.js this.winch): pull only; the drum takes in slack and reels while W is held.</summary>
    public struct BrvWinch
    {
        public bool active;
        /// <summary>Anchor point (JS world space) when hooked to a static object.</summary>
        public double3 anchor;
        /// <summary>Fleet index of the vehicle the hook is on, or -1.</summary>
        public int target;
        public double len;
        public bool reel;
        public double tension;
    }

    /// <summary>
    /// The complete state of one BRV V0 vehicle (PLAN 1.5, 03 section 4.14 phase 1): everything vehicle.js keeps on a
    /// Vehicle object between steps, in the browser game's JS world space (x flipped only at the Unity API, BrCoords).
    /// It is blittable, so a copy is a snapshot (snapshot in/out, <see cref="BrvSnapshot"/> for the byte form).
    /// </summary>
    public struct BrvState
    {
        // body
        public double3 pos, vel, angVel, prevPos;
        /// <summary>Quaternion (x, y, z, w), three.js order.</summary>
        public double4 quat, prevQuat;
        /// <summary>Body axes as of the last updateBasis() (the JS keeps them between steps).</summary>
        public double3 ex, ey, ez;
        public double3 iI;
        public double mass, invMass, payload;

        public BrvControls ctrl;
        public double steerIn, steerAngle;
        public int gear;
        public double rpm, shiftT, revHold, fwdHold, fuel;
        public bool lightsOn, horn, engineOn, asleep, driven, lowRange;
        public double sleepT;
        public double impact;
        public double3 impactDir;
        public double inWater, speed, fwdSpeed;
        public int deformTick;
        public double dirt, mudSink, stuckT;
        /// <summary>this.omegaMax (0 = unset, as the JS undefined) and this.engineBrake.</summary>
        public double omegaMax, engineBrake;
        public int nContact;
        public double2 trackSpeed;
        public BrvWinch winch;
        /// <summary>BRV v2 driveline (unused by V0).</summary>
        public BrvDriveline dl;
        public FixedList4096Bytes<BrvWheel> wheels;

        /// <summary>Fixed steps simulated since spawn (not in the JS; for diagnostics and the snapshot test).</summary>
        public long steps;
    }
}
