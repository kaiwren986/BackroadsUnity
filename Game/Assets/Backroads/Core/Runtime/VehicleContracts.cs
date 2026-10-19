using System;
using UnityEngine;

namespace Backroads.Core
{
    // M2 contracts between the vehicle simulation (Backroads.Vehicles, lane V), the engine sound (Backroads.EngineSim,
    // lane V), and input, cameras and HUD (Backroads.Client / Client.UI, lane W). Each side codes against these and
    // nothing else, so the lanes work in parallel; changes go through the integrator (PLAN §1.5, rule 3).

    /// <summary>What a vehicle tells its engine every fixed step (M2: the classic JS engine's state, PLAN §3.5).</summary>
    public struct EngineState
    {
        /// <summary>The vehicle model's engine speed (M2: LegacyJsEngine's rpm).</summary>
        public float Rpm;
        /// <summary>The vehicle model's idle and redline, for the §3.5 rpm map.</summary>
        public float IdleRpm, RedlineRpm;
        /// <summary>Pedal 0..1.</summary>
        public float Throttle;
        public bool Ignition;
        public bool Starter;
        /// <summary>-1 reverse, 0 neutral, 1.. forward.</summary>
        public int Gear;
        public bool LowRange;
        public float SpeedMps;
    }

    /// <summary>
    /// An engine as the vehicle sees it (rule 3: Backroads.Vehicles sees engines only through this; BRV runs without
    /// the DLL). Created through <see cref="VehicleEngines"/>; null on the server or when no engine is registered.
    /// </summary>
    public interface IVehicleEngine : IDisposable
    {
        /// <summary>The EngineCatalog id, e.g. "v8_bigblock_a".</summary>
        string EngineId { get; }

        /// <summary>Called once per fixed step, from FixedUpdate, after the vehicle step.</summary>
        void Step(in EngineState state, float dt);

        /// <summary>The rpm to show on the tach (M2: the mapped engine-sim rpm).</summary>
        float DisplayRpm { get; }

        /// <summary>Where the sound comes from (the exhaust socket); the engine parents its AudioSource here.</summary>
        void AttachTo(Transform exhaust);
    }

    /// <summary>Factory registry: Backroads.EngineSim registers itself in client builds; the server has none.</summary>
    public static class VehicleEngines
    {
        static Func<string, IVehicleEngine> s_Factory;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Factory = null;

        public static void Register(Func<string, IVehicleEngine> factory) => s_Factory = factory;

        /// <summary>An engine for the id, or null (no factory, unknown id, or failure; logged by the factory).</summary>
        public static IVehicleEngine Create(string engineId) => s_Factory?.Invoke(engineId);
    }

    /// <summary>
    /// Driving input for one vehicle (03 §4.26). Analog values and holds are levels; one-shot actions are press
    /// counters that only increase, so a press is never lost between Update and FixedUpdate: the vehicle acts on each
    /// increase since the last value it saw.
    /// </summary>
    public struct DriveInput
    {
        public float Throttle;      // 0..1 (W, RT)
        public float Brake;         // 0..1 (S, LT): brake, then reverse when stopped (JS behaviour)
        public float Steer;         // -1..1 (A/D, left stick), deadzone already applied
        public bool Handbrake;      // Space, A
        public bool Horn;           // H, X
        public bool StarterHold;    // hold I, hold D-pad left
        public bool Clutch;         // hold V (used from M4)
        public bool WinchReel;      // hold W while the winch is on
        public uint IgnitionPresses, LowRangePresses, WinchPresses, ResetPresses, LightsPresses;
        public uint GearUpPresses, GearDownPresses, ManualPresses, GetInOutPresses;
        public uint BladeUpPresses, BladeDownPresses, DiffLockPresses; // M7
    }

    /// <summary>What the HUD and the F3 overlay show for the player's vehicle.</summary>
    public struct VehicleHud
    {
        public float SpeedKmh;
        /// <summary>-1 reverse, 0 neutral, 1.. forward.</summary>
        public int Gear;
        public bool LowRange;
        public float Rpm;
        public float RedlineRpm;
        public string Surface;
        public float SunkCm;
        public bool Ignition;
        public bool WinchOn;
        public string EngineName;
    }

    /// <summary>A vehicle the player can drive.</summary>
    public interface IDrivenVehicle
    {
        string DisplayName { get; }
        /// <summary>The body transform (interpolated for rendering).</summary>
        Transform Body { get; }
        /// <summary>Hood camera anchor.</summary>
        Transform HoodAnchor { get; }
        Vector3 Velocity { get; }
        VehicleHud Hud { get; }
        void SetInput(in DriveInput input);
    }

    /// <summary>The vehicle the player is driving now; set by the vehicle lane's spawner, read by input, cameras and HUD.</summary>
    public static class PlayerVehicle
    {
        static IDrivenVehicle s_Current;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Current = null; Changed = null; }

        public static event Action<IDrivenVehicle> Changed;

        public static IDrivenVehicle Current
        {
            get => s_Current;
            set { if (ReferenceEquals(s_Current, value)) return; s_Current = value; Changed?.Invoke(value); }
        }
    }
}
