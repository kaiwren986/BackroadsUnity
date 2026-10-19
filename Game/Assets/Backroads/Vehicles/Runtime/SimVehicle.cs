using System;
using Backroads.Core;
using UnityEngine;

namespace Backroads.Vehicles
{
    /// <summary>
    /// One vehicle of a <see cref="VehicleSimSystem"/>: its fleet slot, its driver input (DriveInput levels plus the
    /// presses counted since the last fixed step) and its engine (IVehicleEngine from VehicleEngines.Create, null =
    /// silent: the server, or before lane vs registers the engine-sim factory). Shared code: no rendering here.
    /// </summary>
    public sealed class SimVehicle : IDisposable
    {
        public readonly VehicleSimSystem System;
        public readonly int Index;
        public readonly ClassicVehicleDef Def;

        DriveInput _input;
        DriveInput _seen;
        bool _seenValid;
        int _lightsP, _resetP, _lowP, _winchP, _ignitionP, _getInOutP;
        bool _ignition = true;
        double _starterT;
        bool _wasOn;

        /// <summary>The engine (sound), or null.</summary>
        public IVehicleEngine Engine { get; private set; }
        /// <summary>Where the engine's AudioSource goes (the exhaust socket); set by the presentation.</summary>
        public Transform ExhaustAnchor;
        /// <summary>The player is driving this vehicle (vehicleControls runs for it).</summary>
        public bool IsPlayer { get; private set; }
        /// <summary>GetInOut presses (E) since the last call: the system switches vehicles on them.</summary>
        public int TakeGetInOutPresses() { int n = _getInOutP; _getInOutP = 0; return n; }
        /// <summary>Last step's impact impulse (vehicle.js impact; reset every step).</summary>
        public double LastImpact { get; internal set; }
        public BrvControlEvents LastEvents { get; internal set; }

        internal SimVehicle(VehicleSimSystem system, int index, ClassicVehicleDef def)
        {
            System = system;
            Index = index;
            Def = def;
        }

        public ref BrvState State => ref System.Fleet.State(Index);
        public ref readonly BrvDef Brv => ref System.Fleet.Def(Index);

        public string DisplayName => Def.name;

        /// <summary>Unity-space COM position and rotation of the current step, and of the previous one (for interpolation).</summary>
        public Vector3 Position => BrvFleet.UnityPos(State.pos);
        public Quaternion Rotation => BrvFleet.UnityRot(State.quat);
        public Vector3 PrevPosition => BrvFleet.UnityPos(State.prevPos);
        public Quaternion PrevRotation => BrvFleet.UnityRot(State.prevQuat);
        public Vector3 Velocity => BrvFleet.UnityDir(State.vel);

        /// <summary>Interpolated pose between the last two fixed steps (alpha 0..1).</summary>
        public void InterpolatedPose(float alpha, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.LerpUnclamped(PrevPosition, Position, alpha);
            rot = Quaternion.SlerpUnclamped(PrevRotation, Rotation, alpha);
        }

        /// <summary>
        /// The driver's input (03 section 4.26). Levels replace the previous ones; for the press counters the vehicle
        /// acts on each increase since the last value it saw (the first call after becoming the player's vehicle only
        /// sets the baseline).
        /// </summary>
        public void SetInput(in DriveInput input)
        {
            if (_seenValid)
            {
                _lightsP += Delta(input.LightsPresses, _seen.LightsPresses);
                _resetP += Delta(input.ResetPresses, _seen.ResetPresses);
                _lowP += Delta(input.LowRangePresses, _seen.LowRangePresses);
                _winchP += Delta(input.WinchPresses, _seen.WinchPresses);
                _ignitionP += Delta(input.IgnitionPresses, _seen.IgnitionPresses);
                _getInOutP += Delta(input.GetInOutPresses, _seen.GetInOutPresses);
            }
            _seen = input;
            _seenValid = true;
            _input = input;
        }

        static int Delta(uint now, uint before) => now >= before ? (int)Math.Min(8, now - before) : 0;

        public DriveInput LastInput => _input;
        public bool IgnitionOn => _ignition;

        /// <summary>Called by the system when the player gets in (enterVehicle) or out (park + engine off).</summary>
        internal void SetPlayer(bool on)
        {
            IsPlayer = on;
            _seenValid = false;
            _input = default;
            _lightsP = _resetP = _lowP = _winchP = _ignitionP = _getInOutP = 0;
            if (on)
            {
                _ignition = true;
                ref var s = ref State;
                BrvSim.Wake(ref s);
                s.driven = true;
                s.engineOn = s.fuel > 0;
                EnsureEngine();
            }
            else
            {
                System.Fleet.Park(Index);
                State.engineOn = false;   // main.js exitVehicle: park, engine off, lights off
                State.lightsOn = false;
                DisposeEngine();
            }
        }

        /// <summary>The BrvInput for this fixed step (consumes the counted presses).</summary>
        internal BrvInput TakeStepInput()
        {
            for (int i = 0; i < _ignitionP; i++) _ignition = !_ignition;
            _ignitionP = 0;
            var inp = new BrvInput
            {
                active = IsPlayer,
                fwd = Mathf.Clamp01(_input.Throttle), back = Mathf.Clamp01(_input.Brake), steer = Mathf.Clamp(_input.Steer, -1f, 1f),
                hand = _input.Handbrake ? 1 : 0, horn = _input.Horn, ignition = _ignition,
                lightsPresses = _lightsP, resetPresses = _resetP, lowRangePresses = _lowP, winchPresses = _winchP,
            };
            _lightsP = _resetP = _lowP = _winchP = 0;
            return inp;
        }

        /// <summary>After the step: the winch also reels on DriveInput.WinchReel; the engine gets the classic engine's state.</summary>
        internal void AfterStep(double dt)
        {
            ref var s = ref State;
            if (IsPlayer && s.winch.active && _input.WinchReel) s.winch.reel = true;
            if (s.engineOn && !_wasOn) _starterT = 0.5;
            _wasOn = s.engineOn;
            if (_starterT > 0) _starterT -= dt;
            if (Engine == null) return;
            var st = EngineStateNow();
            try { Engine.Step(st, (float)dt); }
            catch (Exception e) { Log.Error($"{Def.id}: engine {Engine.EngineId} failed: {e.Message}"); DisposeEngine(); }
        }

        public EngineState EngineStateNow()
        {
            ref var s = ref State;
            return new EngineState
            {
                Rpm = (float)s.rpm, IdleRpm = (float)Def.engineIdle, RedlineRpm = (float)Def.engineRedline,
                Throttle = s.engineOn ? (float)s.ctrl.throttle : 0f, Ignition = s.engineOn,
                Starter = s.engineOn && (_starterT > 0 || (_input.StarterHold && s.rpm < Def.engineIdle * 0.8)),
                Gear = s.gear, LowRange = s.lowRange, SpeedMps = (float)Math.Abs(s.fwdSpeed),
            };
        }

        /// <summary>Asks VehicleEngines for this vehicle's engine (each id of Def.engineIds in turn) once.</summary>
        public void EnsureEngine()
        {
            if (Engine != null || Def.engineIds == null) return;
            foreach (var id in Def.engineIds)
            {
                IVehicleEngine e = null;
                try { e = VehicleEngines.Create(id); }
                catch (Exception ex) { Log.Error($"{Def.id}: VehicleEngines.Create({id}) threw {ex.Message}"); }
                if (e == null) continue;
                Engine = e;
                if (ExhaustAnchor != null) e.AttachTo(ExhaustAnchor);
                Log.Info($"{Def.id}: engine {e.EngineId}");
                return;
            }
        }

        public void DisposeEngine()
        {
            var e = Engine;
            Engine = null;
            if (e == null) return;
            try { e.Dispose(); } catch (Exception ex) { Log.Error($"{Def.id}: engine dispose: {ex.Message}"); }
        }

        /// <summary>What the HUD shows (hud.js speedo): |forward speed|, gear, rpm, surface, sinkage, winch.</summary>
        public VehicleHud Hud
        {
            get
            {
                ref var s = ref State;
                string surf = "Airborne";
                for (int i = 0; i < s.wheels.Length; i++)
                    if (s.wheels[i].contact) { surf = SurfaceName(s.wheels[i].surf); break; }
                return new VehicleHud
                {
                    SpeedKmh = (float)(Math.Abs(s.fwdSpeed) * 3.6),
                    Gear = s.gear, LowRange = s.lowRange,
                    Rpm = Engine != null ? Engine.DisplayRpm : (float)s.rpm,
                    RedlineRpm = Engine != null ? Engine.DisplayRedlineRpm : Def.hasEngine ? (float)Def.engineRedline : 2200f,
                    Surface = surf, SunkCm = (float)(s.mudSink * 100), Ignition = s.engineOn, WinchOn = s.winch.active,
                    EngineName = Engine != null ? Engine.EngineId : "classic",
                };
            }
        }

        /// <summary>world.js SURF_NAMES.</summary>
        public static string SurfaceName(int s)
        {
            switch (s)
            {
                case 0: return "Grass";
                case 1: return "Dirt";
                case 2: return "Sand";
                case 3: return "Rock";
                case 4: return "Asphalt";
                case 5: return "Gravel";
                case 6: return "Concrete";
                case 7: return "Dirt road";
                case 8: return "Mud";
                default: return "?";
            }
        }

        public void Dispose() => DisposeEngine();
    }
}
