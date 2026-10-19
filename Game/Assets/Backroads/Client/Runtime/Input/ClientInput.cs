using System.Collections.Generic;
using Backroads.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Backroads.Client
{
    /// <summary>
    /// T2.6 input driver (03 section 4.26): builds the Input System actions from bindings.json (plus the settings.json
    /// overrides), turns them into this frame's presses, levels, axes, look and zoom, and fills DriveInput (levels and
    /// press counters that only grow) for PlayerVehicle.Current every frame. The UI map is always on; the Driving map is
    /// on while a vehicle is driven, the game is not paused and the fly camera is off. Taps and holds: an entry with
    /// "hold" fires its own action on release before holdSeconds, the hold target once holdSeconds have passed.
    /// Client-only; runs before other scripts so they see this frame's input.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class ClientInput : MonoBehaviour
    {
        /// <summary>three's camera.js mouse sensitivity (rad per pixel).</summary>
        public const float MouseRadPerPixel = 0.0024f;
        public const float PadYawRate = 2.6f, PadPitchRate = 2.0f;

        public static ClientInput Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public BindingTable Table { get; private set; }
        public InputActionAsset Actions { get; private set; }
        public string Error { get; private set; }

        /// <summary>Menus that pause the game set this: the Driving map is off and the vehicle gets neutral levels.</summary>
        public bool Paused { get; set; }
        /// <summary>The fly camera is on: driving input off.</summary>
        public bool FlyMode { get; set; }
        /// <summary>A UI text field has the keyboard (the checklist's comments): driving input off.</summary>
        public bool Typing { get; set; }
        /// <summary>Settings: look sensitivity factor and vertical inversion.</summary>
        public float LookSensitivity = 1f;
        public bool InvertLookY;

        public bool DrivingActive => PlayerVehicle.Current != null && !Paused && !FlyMode && !Typing;

        /// <summary>Look this frame in radians (camera.js): + yaw turns the view right, + pitch raises the camera.</summary>
        public float LookYaw { get; private set; }
        public float LookPitch { get; private set; }
        /// <summary>Mouse wheel this frame: +1 away (zoom out), -1 towards (camera.js sign(deltaY)).</summary>
        public int ZoomWheel { get; private set; }
        /// <summary>The last DriveInput sent (tests and the F3 overlay).</summary>
        public DriveInput LastDrive => _drive;
        public int FramesSent { get; private set; }

        sealed class Slot
        {
            public string Map, Action, Kind, Hold, HoldKind;
            public char Dev;
            public InputAction Act;
            public float T0 = -1f;
            public bool HoldFired;
        }

        readonly List<Slot> _slots = new List<Slot>();
        readonly Dictionary<string, int> _presses = new Dictionary<string, int>();
        readonly Dictionary<string, bool> _held = new Dictionary<string, bool>();
        readonly Dictionary<string, float> _value = new Dictionary<string, float>();
        readonly Dictionary<string, float> _padAxis = new Dictionary<string, float>();
        readonly List<string> _names = new List<string>();
        InputActionMap _ui, _driving, _onFoot;
        DriveInput _drive;
        float _deadzone = 0.14f, _holdSeconds = 0.3f;

        /// <summary>Creates the driver under the persistent root (ClientUiBootstrap / PresentationRegistry).</summary>
        public static ClientInput Create(GameObject root, IReadOnlyList<BindingOverride> overrides)
        {
            if (Current != null) return Current;
            var go = new GameObject("BrInput");
            go.transform.SetParent(root != null ? root.transform : null, false);
            if (root == null) DontDestroyOnLoad(go);
            var ci = go.AddComponent<ClientInput>();
            ci.Load(overrides);
            return ci;
        }

        void Awake() { if (Current == null) Current = this; }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            if (Actions != null) { Actions.Disable(); Destroy(Actions); }
        }

        /// <summary>(Re)builds the actions from bindings.json with the given overrides; on a bad table keeps the defaults.</summary>
        public void Load(IReadOnlyList<BindingOverride> overrides)
        {
            var table = BindingTable.LoadDefault(out var err);
            if (table == null) { Error = err; Log.Error("input: " + err); return; }
            if (overrides != null && overrides.Count > 0)
            {
                var withUser = table.WithOverrides(overrides);
                var problems = withUser.Problems();
                if (problems.Count == 0) table = withUser;
                else Log.Warn("input: settings.json key changes ignored: " + string.Join("; ", problems));
            }
            Build(table);
        }

        /// <summary>Uses the given table (tests).</summary>
        public void Build(BindingTable table)
        {
            if (Actions != null) { Actions.Disable(); Destroy(Actions); }
            Table = table;
            _deadzone = table.deadzone;
            _holdSeconds = table.holdSeconds;
            Actions = table.CreateActions();
            _slots.Clear(); _presses.Clear(); _held.Clear(); _value.Clear(); _padAxis.Clear(); _names.Clear();
            var kindOf = new Dictionary<string, string>();
            foreach (var m in table.maps) foreach (var b in m.bindings) kindOf[b.action] = b.kind;
            foreach (var m in table.maps)
                for (int i = 0; i < m.bindings.Length; i++)
                {
                    var b = m.bindings[i];
                    foreach (char dev in new[] { 'k', 'p' })
                    {
                        var act = Actions.FindAction(m.name + "/" + BindingTable.ActionName(b.action, i, dev));
                        if (act == null) continue;
                        _slots.Add(new Slot
                        {
                            Map = m.name, Action = b.action, Kind = b.kind, Dev = dev, Act = act, Hold = string.IsNullOrEmpty(b.hold) ? null : b.hold,
                            HoldKind = !string.IsNullOrEmpty(b.hold) && kindOf.TryGetValue(b.hold, out var hk) ? hk : null,
                        });
                    }
                    if (!_presses.ContainsKey(b.action)) { _presses[b.action] = 0; _held[b.action] = false; _value[b.action] = 0; _padAxis[b.action] = 0; _names.Add(b.action); }
                }
            _ui = Actions.FindActionMap("UI");
            _driving = Actions.FindActionMap("Driving");
            _onFoot = Actions.FindActionMap("OnFoot");
            _ui?.Enable();
            Error = null;
            Log.Info($"input: {_slots.Count} actions from bindings.json v{table.version} (deadzone {_deadzone}, hold {_holdSeconds} s)");
        }

        // ------------------------------------------------------------------ queries (this frame)

        public int Presses(string action) => _presses.TryGetValue(action, out var n) ? n : 0;
        public bool Pressed(string action) => Presses(action) > 0;
        public bool Held(string action) => _held.TryGetValue(action, out var h) && h;
        public float Value(string action) => _value.TryGetValue(action, out var v) ? v : 0f;

        float Dz(float v) => Mathf.Abs(v) < _deadzone ? 0f : v;

        void Update()
        {
            if (Actions == null) return;
            bool driving = DrivingActive;
            if (_driving != null) { if (driving && !_driving.enabled) _driving.Enable(); else if (!driving && _driving.enabled) _driving.Disable(); }
            if (_onFoot != null && _onFoot.enabled) _onFoot.Disable();

            for (int i = 0; i < _names.Count; i++)
            {
                var n = _names[i];
                _presses[n] = 0; _held[n] = false; _value[n] = 0f; _padAxis[n] = 0f;
            }
            float now = Time.unscaledTime;
            Vector2 mouse = Vector2.zero, stick = Vector2.zero;
            float wheel = 0f;
            for (int s = 0; s < _slots.Count; s++)
            {
                var sl = _slots[s];
                var a = sl.Act;
                if (!a.enabled) { sl.T0 = -1f; sl.HoldFired = false; continue; }
                switch (sl.Kind)
                {
                    case "value":
                        _value[sl.Action] = Mathf.Max(_value[sl.Action], Mathf.Clamp01(a.ReadValue<float>()));
                        break;
                    case "axis":
                        if (sl.Dev == 'p') _padAxis[sl.Action] = Dz(a.ReadValue<float>());
                        else _value[sl.Action] = Mathf.Clamp(a.ReadValue<float>(), -1f, 1f);
                        break;
                    case "vector":
                        break; // OnFoot (M7)
                    case "delta":
                        if (sl.Dev == 'k') mouse += a.ReadValue<Vector2>();
                        else { var v = a.ReadValue<Vector2>(); stick += new Vector2(Dz(v.x), Dz(v.y)); }
                        break;
                    case "scroll":
                        if (sl.Dev == 'k') wheel += a.ReadValue<float>();
                        else if (a.WasPressedThisFrame()) _presses[sl.Action]++;
                        break;
                    default:
                        Button(sl, now);
                        break;
                }
            }
            // keyboard axes give way to the stick when it is out of its deadzone (main.js: if (gp.steer) steer = gp.steer)
            for (int i = 0; i < _names.Count; i++)
            {
                var n = _names[i];
                if (_padAxis[n] != 0f) _value[n] = _padAxis[n];
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float inv = InvertLookY ? -1f : 1f;
            LookYaw = mouse.x * MouseRadPerPixel * LookSensitivity + stick.x * dt * PadYawRate;
            LookPitch = (-mouse.y * MouseRadPerPixel * LookSensitivity - stick.y * dt * PadPitchRate) * inv;
            ZoomWheel = wheel > 0 ? -1 : wheel < 0 ? 1 : 0;
            SendDrive(driving);
        }

        void Button(Slot sl, float now)
        {
            var a = sl.Act;
            if (sl.Hold == null)
            {
                if (sl.Kind == "hold") { if (a.IsPressed()) _held[sl.Action] = true; }
                else if (a.WasPressedThisFrame()) _presses[sl.Action]++;
                return;
            }
            if (a.WasPressedThisFrame()) { sl.T0 = now; sl.HoldFired = false; }
            if (a.IsPressed() && sl.T0 >= 0f)
            {
                if (!sl.HoldFired && now - sl.T0 >= _holdSeconds)
                {
                    sl.HoldFired = true;
                    if (sl.HoldKind != "hold" && _presses.ContainsKey(sl.Hold)) _presses[sl.Hold]++;
                }
                if (sl.HoldFired && sl.HoldKind == "hold") _held[sl.Hold] = true;
            }
            if (a.WasReleasedThisFrame() || (!a.IsPressed() && sl.T0 >= 0f && !a.WasPressedThisFrame()))
            {
                if (!sl.HoldFired && sl.T0 >= 0f) _presses[sl.Action]++;
                sl.T0 = -1f; sl.HoldFired = false;
            }
        }

        void SendDrive(bool driving)
        {
            var v = PlayerVehicle.Current;
            if (PlayerVehicle.ScriptedInput) return; // an autotest feeds the vehicle itself
            if (v == null) return;
            var d = _drive;
            if (driving)
            {
                d.Throttle = Value("throttle");
                d.Brake = Value("brake");
                d.Steer = Value("steer");
                d.Handbrake = Held("handbrake");
                d.Horn = Held("horn");
                d.StarterHold = Held("starter");
                d.Clutch = Held("clutch");
                d.WinchReel = d.Throttle > 0.1f;
                d.IgnitionPresses += (uint)Presses("ignition");
                d.LowRangePresses += (uint)Presses("lowRange");
                d.WinchPresses += (uint)Presses("winch");
                d.ResetPresses += (uint)Presses("resetUpright");
                d.LightsPresses += (uint)Presses("lights");
                d.GearUpPresses += (uint)Presses("gearUp");
                d.GearDownPresses += (uint)Presses("gearDown");
                d.ManualPresses += (uint)Presses("manualToggle");
                d.GetInOutPresses += (uint)Presses("getInOut");
                d.BladeUpPresses += (uint)Presses("bladeUp");
                d.BladeDownPresses += (uint)Presses("bladeDown");
                d.DiffLockPresses += (uint)Presses("diffLock");
            }
            else
            {
                d.Throttle = d.Brake = d.Steer = 0f;
                d.Handbrake = d.Horn = d.StarterHold = d.Clutch = d.WinchReel = false;
            }
            _drive = d;
            v.SetInput(in d);
            FramesSent++;
        }
    }
}
