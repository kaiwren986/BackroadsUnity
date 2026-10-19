using System.Collections.Generic;
using Backroads.Client;
using Backroads.Client.Rendering;
using Backroads.Core;
using Backroads.Deform;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// T2.7 test vehicle (Dev builds and the editor only): a pickup-sized box on four wheels that implements
    /// IDrivenVehicle, so the input driver, the cameras and the HUD can be checked before lane V's vehicles arrive. It
    /// either follows waypoints (autopilot) or drives from the DriveInput it is sent (a simple arcade model), sits on the
    /// WorldGrid, fills VehicleHud with changing values (speed, gear, rpm, surface, sunk), toggles ignition, low range
    /// and the winch from the press counters, and ruts soft ground through StampSinks.Current like vehicle.js deform().
    /// Start it with -br-stub-vehicle on a Dev build, or from an autotest.
    /// </summary>
    public sealed class StubDrivenVehicle : MonoBehaviour, IDrivenVehicle, IVehicleCameraHints
    {
        public const string Flag = "-br-stub-vehicle";
        static readonly string[] SurfNames = { "Grass", "Dirt", "Sand", "Rock", "Asphalt", "Gravel", "Concrete", "Dirt road", "Mud" };
        static readonly float[] Softness = { 0.5f, 1.0f, 0.85f, 0f, 0f, 0.16f, 0f, 0.3f, 1.0f };

        public enum DriveMode { Autopilot, Input }

        public DriveMode Mode = DriveMode.Autopilot;
        public float CruiseKmh = 30f;
        public List<Vector3> Waypoints = new List<Vector3>();
        public bool Loop = true;
        public bool Ruts = true;

        public string DisplayName => "Test pickup (stub)";
        public Transform Body => transform;
        public Transform HoodAnchor { get; private set; }
        public Vector3 Velocity { get; private set; }
        public VehicleHud Hud => _hud;
        public float CameraDistance => 7.6f;   // camera.js pickup
        public float CameraHeight => 2.6f;

        /// <summary>The last input from the driver, and how many times it came.</summary>
        public DriveInput LastInput { get; private set; }
        public int InputCalls { get; private set; }
        public int StampsSent { get; private set; }
        public int Waypoint { get; private set; }

        VehicleHud _hud;
        float _speed, _yaw, _step;
        uint _ign, _low, _winch;
        int _tick;
        readonly Vector3[] _wheels =
        {
            new Vector3(-0.86f, 0f, 1.62f), new Vector3(0.86f, 0f, 1.62f), new Vector3(-0.86f, 0f, -1.62f), new Vector3(0.86f, 0f, -1.62f),
        };

        public void SetInput(in DriveInput input)
        {
            if (input.IgnitionPresses != _ign) { _hud.Ignition = !_hud.Ignition; _ign = input.IgnitionPresses; }
            if (input.LowRangePresses != _low) { _hud.LowRange = !_hud.LowRange; _low = input.LowRangePresses; }
            if (input.WinchPresses != _winch) { _hud.WinchOn = !_hud.WinchOn; _winch = input.WinchPresses; }
            LastInput = input;
            InputCalls++;
        }

        /// <summary>Builds the stub at a JS position and yaw (radians, JS), makes it the player's vehicle.</summary>
        public static StubDrivenVehicle Spawn(float xJs, float zJs, float yawJs, MillbrookLook look)
        {
            var go = new GameObject("StubDrivenVehicle");
            var v = go.AddComponent<StubDrivenVehicle>();
            v.Build(look);
            v._yaw = -yawJs;   // Unity heading = -JS yaw
            v.Place(BrCoords.Pos(xJs, 0f, zJs));
            v._hud = new VehicleHud { Ignition = true, RedlineRpm = 5600f, EngineName = "stub", Surface = "", Gear = 0 };
            PlayerVehicle.Current = v;
            return v;
        }

        /// <summary>Moves the stub to a Unity position, heading along a Unity direction, standing still.</summary>
        public void Teleport(Vector3 pos, Vector3 heading)
        {
            _yaw = Mathf.Atan2(heading.x, heading.z);
            _speed = 0f;
            Waypoint = 0;
            Place(pos);
            Velocity = Vector3.zero;
        }

        void OnDestroy()
        {
            if (ReferenceEquals(PlayerVehicle.Current, this)) PlayerVehicle.Current = null;
        }

        void Build(MillbrookLook look)
        {
            var baseMat = look != null ? look.buildingPart : null;
            Material Mat(Color c)
            {
                if (baseMat == null) return null;
                var m = new Material(baseMat);
                m.SetColor("_BaseColor", c);
                return m;
            }
            var red = Mat(new Color(0.70f, 0.20f, 0.17f));
            var dark = Mat(new Color(0.08f, 0.08f, 0.09f));
            var glass = Mat(new Color(0.15f, 0.22f, 0.28f));
            Box("body", new Vector3(0f, 1.05f, -0.1f), new Vector3(1.98f, 0.75f, 5.2f), red);
            Box("cab", new Vector3(0f, 1.75f, 0.55f), new Vector3(1.8f, 0.7f, 2.0f), red);
            Box("windscreen", new Vector3(0f, 1.78f, 1.56f), new Vector3(1.7f, 0.55f, 0.05f), glass);
            foreach (var w in _wheels)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(c.GetComponent<Collider>());
                c.name = "wheel";
                c.transform.SetParent(transform, false);
                c.transform.localPosition = w + new Vector3(0f, 0.43f, 0f);
                c.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                c.transform.localScale = new Vector3(0.86f, 0.15f, 0.86f);
                if (dark != null) c.GetComponent<MeshRenderer>().sharedMaterial = dark;
            }
            var hood = new GameObject("HoodAnchor").transform;
            hood.SetParent(transform, false);
            hood.localPosition = new Vector3(-0.42f, 1.95f, 1.62f);  // just ahead of the (opaque) stub windscreen
            HoodAnchor = hood;
        }

        void Box(string name, Vector3 pos, Vector3 size, Material m)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(b.GetComponent<Collider>());
            b.name = name;
            b.transform.SetParent(transform, false);
            b.transform.localPosition = pos;
            b.transform.localScale = size;
            if (m != null) b.GetComponent<MeshRenderer>().sharedMaterial = m;
        }

        void Place(Vector3 p)
        {
            if (Ground.Ready)
            {
                float h = Mathf.Max(Ground.HeightAt(p), -1f);
                var n = Ground.NormalAt(p);
                var fwd = new Vector3(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
                fwd = Vector3.ProjectOnPlane(fwd, n).normalized;
                transform.SetPositionAndRotation(new Vector3(p.x, h, p.z), Quaternion.LookRotation(fwd, n));
            }
            else transform.SetPositionAndRotation(p, Quaternion.Euler(0f, _yaw * Mathf.Rad2Deg, 0f));
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (dt <= 0f) return;
            var p = transform.position;
            float target;
            float steer;
            if (Mode == DriveMode.Input)
            {
                var i = LastInput;
                target = _hud.Ignition ? (i.Throttle * 60f - i.Brake * 20f) / 3.6f : 0f;
                steer = i.Steer;
                _speed = Mathf.MoveTowards(_speed, target, (i.Brake > 0.1f && _speed > 0.5f ? 9f : 3f) * dt);
                _yaw += steer * 0.6f * dt * Mathf.Clamp(_speed / 5f, -1f, 1f);
            }
            else
            {
                target = CruiseKmh / 3.6f;
                if (Waypoints.Count > 0)
                {
                    var to = Waypoints[Waypoint] - p;
                    to.y = 0;
                    if (to.magnitude < 4f)
                    {
                        Waypoint++;
                        if (Waypoint >= Waypoints.Count) { if (Loop) Waypoint = 0; else { Waypoint = Waypoints.Count - 1; target = 0f; } }
                    }
                    float want = Mathf.Atan2(to.x, to.z);
                    float d = Mathf.DeltaAngle(_yaw * Mathf.Rad2Deg, want * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    _yaw += Mathf.Clamp(d, -0.8f * dt, 0.8f * dt);
                }
                _speed = Mathf.MoveTowards(_speed, target, 3f * dt);
            }
            var fwd = new Vector3(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));
            var next = p + fwd * (_speed * dt);
            Place(next);
            Velocity = (transform.position - p) / dt;
            UpdateHud();
            if (Ruts && (++_tick & 1) == 0) Rut(dt * 2f);
        }

        void UpdateHud()
        {
            float kmh = Velocity.magnitude * 3.6f;
            _hud.SpeedKmh = kmh;
            int gear = _speed < -0.2f ? -1 : kmh < 1f ? 0 : kmh < 18f ? 1 : kmh < 34f ? 2 : kmh < 52f ? 3 : kmh < 72f ? 4 : 5;
            _hud.Gear = gear;
            float lo = gear <= 1 ? 0f : gear == 2 ? 18f : gear == 3 ? 34f : gear == 4 ? 52f : 72f, hi = gear <= 1 ? 18f : gear == 2 ? 34f : gear == 3 ? 52f : gear == 4 ? 72f : 110f;
            _hud.Rpm = _hud.Ignition ? 750f + Mathf.Clamp01((kmh - lo) / (hi - lo)) * 4600f : 0f;
            if (Ground.Ready)
            {
                var pos = transform.position;
                byte s = Ground.SurfaceAt(pos);
                _hud.Surface = s < SurfNames.Length ? SurfNames[s] : "?";
                float sunk = 0f;   // the deepest rut under a wheel (hud.js shows the wheels' sink)
                foreach (var w in _wheels) sunk = Mathf.Max(sunk, Ground.SinkAt(transform.TransformPoint(w)));
                _hud.SunkCm = sunk * 100f;
            }
        }

        /// <summary>vehicle.js deform() for four rolling wheels (no spin, no slide), through the M2 stamp contract.</summary>
        void Rut(float dt)
        {
            var sink = StampSinks.Current;
            if (sink == null || !Ground.Ready || Mathf.Abs(_speed) < 0.25f) return;
            var fwdJs = BrCoords.Dir(transform.forward.x, transform.forward.y, transform.forward.z);
            float dir = _speed >= 0f ? 1f : -1f;
            foreach (var w in _wheels)
            {
                var cp = transform.TransformPoint(w);
                byte s = Ground.SurfaceAt(cp);
                if (s >= Softness.Length || Softness[s] <= 0f) continue;
                float x = -cp.x, z = cp.z;
                const float radius = 0.42f, load = 4600f;
                if (s == 8)
                {
                    float md = Ground.MudAt(cp);
                    float depth = md * 0.55f * Mathf.Clamp(0.5f + load / 30000f, 0.55f, 1f);
                    float rate = 0.006f + Mathf.Abs(_speed) * dt * 0.35f;
                    sink.Rut(x, z, radius, depth, rate, 12f, 0.6f, fwdJs.x * dir, fwdJs.z * dir);
                }
                else
                {
                    float depth = Mathf.Clamp(0.03f + load / 36000f, 0f, 0.34f);
                    float rate = 0.002f + Mathf.Abs(_speed) * dt * 0.11f;
                    sink.Rut(x, z, radius, depth, rate, 9f, 0.45f);
                }
                StampsSent++;
            }
        }

        // ------------------------------------------------------------------ -br-stub-vehicle

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!CommandLine.Has(Flag)) return;
            var go = new GameObject("StubVehicleBoot");
            DontDestroyOnLoad(go);
            go.AddComponent<StubBoot>();
        }

        sealed class StubBoot : MonoBehaviour
        {
            System.Collections.IEnumerator Start()
            {
                while (MillbrookWorld.Current == null || !MillbrookWorld.Current.Ready) yield return null;
                var w = MillbrookWorld.Current;
                var bay = w.Canon.placements.parking.Length > 0 ? w.Canon.placements.parking[0] : default;
                var v = Spawn(bay.x, bay.z, bay.yaw, w.look);
                v.Mode = DriveMode.Input;
                v.Ruts = true;
                Log.Info($"stub vehicle at the garage bay ({bay.x:F1}, {bay.z:F1}): drive it with the M2 controls");
                Destroy(gameObject);
            }
        }
    }
}
