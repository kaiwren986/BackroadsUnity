using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Backroads.Core;
using UnityEngine;

namespace Backroads.EngineSim
{
    /// <summary>
    /// A scripted <see cref="EngineState"/> drive for an <see cref="IVehicleEngine"/> without a vehicle (card T2.4): the
    /// Engine Bench's vehicle demo, the PlayMode test EngineSim.FollowInVehicle and the m2.engine.follow autotest. The
    /// classic JS engine's rpm is integrated the way vehicle.js engineForce() does it (rpm += (eng - rpm) * min(1, dt * 14),
    /// eng clamped to idle..1.04 x redline), with eng scripted: key off, key on (starter held 0.5 s, as SimVehicle does) and
    /// idle, full pedal pulling to 95 % of the redline, an upshift drop to 60 %, pulling again, lifting off to idle, key off.
    /// </summary>
    public static class FollowRamp
    {
        /// <summary>A vehicle's classic engine (oracle js/vehicle.js DEFS at 552f7807) and its engine-sim engine (PLAN 3.10, U6).</summary>
        public readonly struct Vehicle
        {
            public readonly string Name, EngineId;
            public readonly float IdleJs, RedlineJs;
            public Vehicle(string name, string engineId, float idleJs, float redlineJs) { Name = name; EngineId = engineId; IdleJs = idleJs; RedlineJs = redlineJs; }
        }

        public static readonly Vehicle[] Roster =
        {
            new Vehicle("truck", "v8_bigblock_a", 650, 2700),
            new Vehicle("pickup", "v8_cross_a", 750, 5600),
            new Vehicle("rally", "f4_boxer_a", 950, 7600),
            new Vehicle("hatch", "i4_synth", 850, 6800),
        };

        public const float KeyOnS = 0.5f, StarterS = 0.5f, PullS = 2.0f, ShiftS = 5.0f, LiftS = 7.0f, KeyOffS = 9.0f, EndS = 9.75f;

        /// <summary>The scripted pedal and target engine speed ("eng" in vehicle.js) at time t.</summary>
        public static void Script(in Vehicle v, float t, out bool ignition, out float throttle, out float eng)
        {
            ignition = t >= KeyOnS && t < KeyOffS;
            throttle = t >= PullS && t < LiftS ? 1f : 0f;
            float idle = v.IdleJs, red = v.RedlineJs;
            if (t < PullS) eng = idle;
            else if (t < ShiftS) eng = Mathf.Lerp(idle, 0.95f * red, (t - PullS) / (ShiftS - PullS));
            else if (t < LiftS) eng = Mathf.Lerp(0.60f * red, 0.90f * red, (t - ShiftS) / (LiftS - ShiftS));
            else eng = Mathf.Lerp(0.90f * red, idle, Mathf.Clamp01((t - LiftS) / 1.0f));
            eng = Mathf.Clamp(eng, idle, red * 1.04f);
        }

        /// <summary>One step of the classic engine's rpm (vehicle.js: smoothing at 14/s, 0 while the engine is off).</summary>
        public static EngineState Next(in Vehicle v, float t, float dt, ref float rpmJs)
        {
            Script(v, t, out bool ignition, out float throttle, out float eng);
            rpmJs += (eng - rpmJs) * Mathf.Min(1f, dt * 14f);
            if (!ignition) rpmJs = 0f;
            return new EngineState
            {
                Rpm = rpmJs, IdleRpm = v.IdleJs, RedlineRpm = v.RedlineJs, Throttle = throttle, Ignition = ignition,
                // the vehicle lane's SimVehicle holds the starter for 0.5 s after the engine comes on
                Starter = ignition && t < KeyOnS + StarterS, Gear = ignition ? (t >= ShiftS ? 2 : 1) : 0, SpeedMps = 0f,
            };
        }

        public static bool TryFind(string engineId, out Vehicle v)
        {
            foreach (var r in Roster) if (r.EngineId == engineId) { v = r; return true; }
            v = default;
            return false;
        }
    }

    /// <summary>
    /// Steps one <see cref="IVehicleEngine"/> through <see cref="FollowRamp"/> in FixedUpdate, as a vehicle would (after its
    /// own step), and records what happened.
    /// </summary>
    public sealed class FollowRampDriver : MonoBehaviour
    {
        public IVehicleEngine Engine;
        public FollowRamp.Vehicle Vehicle;
        public float Time01 => Mathf.Clamp01(_t / FollowRamp.EndS);
        public bool Done => _t >= FollowRamp.EndS;
        public float T => _t;
        public int Steps { get; private set; }
        public float RpmJs => _rpmJs;
        /// <summary>CSV rows: t, js rpm, mapped rpm, engine-sim rpm (newest result), display rpm, phase.</summary>
        public readonly StringBuilder Csv = new StringBuilder("t,rpm_js,mapped,engine_rpm,display_rpm,phase\n");
        /// <summary>Tracking counts only from this time on (the first FOLLOW steps after the key turns are included).</summary>
        public float CountFromS = FollowRamp.KeyOnS;

        float _t, _rpmJs;
        bool _reset;

        void FixedUpdate()
        {
            if (Engine == null || Done) return;
            float dt = UnityEngine.Time.fixedDeltaTime;
            _t += dt;
            var s = FollowRamp.Next(Vehicle, _t, dt, ref _rpmJs);
            if (!_reset && _t >= CountFromS && Engine is EngineSimVehicleEngine e0) { e0.ResetTracking(); _reset = true; }
            Engine.Step(s, dt);
            Steps++;
            if (Engine is EngineSimVehicleEngine e)
            {
                var ci = CultureInfo.InvariantCulture;
                Csv.Append(_t.ToString("F4", ci)).Append(',').Append(_rpmJs.ToString("F1", ci)).Append(',')
                    .Append(e.TargetRpm.ToString("F1", ci)).Append(',').Append(e.EngineRpm.ToString("F1", ci)).Append(',')
                    .Append(e.DisplayRpm.ToString("F1", ci)).Append(',').Append(e.Phase).Append('\n');
            }
        }
    }
}
