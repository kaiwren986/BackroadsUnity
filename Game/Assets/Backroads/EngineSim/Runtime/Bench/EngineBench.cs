using System;
using System.Collections.Generic;
using Backroads.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Backroads.EngineSim
{
    /// <summary>
    /// The Engine Bench (PLAN 6.1 M1 "You see and play"): one engine-sim engine in FREE mode, heard through
    /// EngineSimAudio. Tap I for ignition, hold I to crank (like a key), W or the right trigger for throttle, L for a
    /// road-like load, D (or the button) for a dyno sweep. The panel shows rpm, torque, MAP, AFR, worker time, real-time
    /// factor, ring fill and target, underruns and coast events. Client builds list the roster engines; Dev builds add the
    /// upstream scripts. Built in code, so it works in EngineBench.unity or, with -br-bench, in any scene.
    /// </summary>
    public sealed class EngineBench : MonoBehaviour
    {
        public const string Flag = "-br-bench";
        public const string SceneName = "EngineBench";
        public const string PanelSettingsResource = "BrPanelSettings";
        const float HoldForStarter = 0.3f;

        public static EngineBench Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>
        /// EngineBench.unity holds only a camera and a light (so server builds never meet a client script in it); the
        /// bench itself is made here whenever that scene loads (at start, from the Esc menu, or in the editor), and in
        /// any first scene with -br-bench.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (Application.isBatchMode) return;
            if (!CommandLine.Has(Flag) && SceneManager.GetActiveScene().name != SceneName) return;
            if (FindAnyObjectByType<EngineBench>() == null) Create();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Application.isBatchMode || scene.name != SceneName) return;
            if (FindAnyObjectByType<EngineBench>() == null) Create();
        }

        /// <summary>Creates the bench in the current scene (camera, panel, engine). Used by -br-bench and autotests.</summary>
        public static EngineBench Create()
        {
            var go = new GameObject("EngineBench");
            return go.AddComponent<EngineBench>();
        }

        // ---- state ----
        public EngineDef Def { get; private set; }
        public EngineSimEngine Engine { get; private set; }
        public AudioSource Source { get; private set; }
        public EngineSimAudio Audio { get; private set; }
        public AudioTap Tap { get; private set; }
        public string LastError { get; private set; }
        public bool LoadOn;
        public readonly DynoSweep Sweep = new DynoSweep();

        /// <summary>Autotests drive the bench through these instead of the keyboard.</summary>
        public bool ScriptControl;
        public float ScriptThrottle;
        public bool ScriptIgnition, ScriptStarter;

        IReadOnlyList<EngineDef> _engines;
        float _throttle, _pressStart = -1f;
        bool _ignition, _starterHeld;
        float _lastUi;
        EspStepOut _last;
        float _torqueSmoothed;

        // ---- UI ----
        UIDocument _doc;
        Label _title, _rpm, _state, _readout, _help, _error;
        Button _loadButton, _sweepButton, _ignitionButton;
        DynoPlot _plot;
        readonly List<Button> _engineButtons = new List<Button>();

        void Awake()
        {
            Current = this;
            EngineSimEngine.SetCrashFolder();
            if (Camera.main == null && FindAnyObjectByType<Camera>() == null)
            {
                var cam = new GameObject("BenchCamera", typeof(Camera)).GetComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.11f, 0.12f);
                cam.transform.SetParent(transform, false);
            }
            if (FindAnyObjectByType<AudioListener>() == null) gameObject.AddComponent<AudioListener>();
            _engines = EngineCatalog.Available();
        }

        void Start()
        {
            BuildUi();
            Select(EngineCatalog.Find("f4_boxer_a") ?? _engines[0]);
        }

        void OnDestroy()
        {
            Sweep.Cancel();
            Unload();
            if (Current == this) Current = null;
        }

        void OnApplicationQuit()
        {
            Sweep.Cancel();
            Unload();
        }

        /// <summary>Loads an engine (stops the previous one first). False and LastError when it fails.</summary>
        public bool Select(EngineDef def)
        {
            Unload();
            Def = def;
            Sweep.Cancel();
            _plot?.Clear();
            _plot?.SetCaption("Dyno sweep: press D or the button");
            _ignition = false;
            _throttle = 0f;
            var engine = EngineSimEngine.Create(def, EngineSimEngine.Options.ForPlayback(), out var err);
            if (engine == null)
            {
                LastError = err;
                Log.Warn("[EngineBench] " + err);
                RefreshUi();
                return false;
            }
            LastError = null;
            Engine = engine;
            engine.SetWorker(1, true);  // above normal, no power throttling (PLAN 3.4 scheduling)
            var go = new GameObject("Engine " + def.id);
            go.transform.SetParent(transform, false);
            Source = go.AddComponent<AudioSource>();
            Source.playOnAwake = false;
            Source.loop = true;
            Source.spatialBlend = 0f;   // the bench is "at the engine"; S-AUD-1 covers 3D
            Audio = go.AddComponent<EngineSimAudio>();
            Tap = go.AddComponent<AudioTap>();   // after EngineSimAudio: sees what Unity received
            Tap.Recording = false;
            Audio.Attach(engine);
            Source.Play();
            if (_plot != null) _plot.MaxRpm = Math.Max(4000, Math.Ceiling((engine.Info.revLimitRadS * 9.5492966 + 600) / 1000) * 1000);
            RefreshUi();
            return true;
        }

        void Unload()
        {
            if (Source != null) Source.Stop();
            if (Audio != null) Audio.Detach();
            Engine?.Dispose();
            Engine = null;
            if (Source != null) Destroy(Source.gameObject);
            Source = null;
            Audio = null;
            Tap = null;
        }

        /// <summary>Road-like absorber on the crank, like research 02's bench load: 0.87 x peak torque x (rpm/5000)^2.</summary>
        public double LoadTorque(double crankSpeed)
        {
            if (!LoadOn || Def == null) return 0;
            double peak = Def.peakTorqueNm > 0 ? Def.peakTorqueNm : 230;
            double r = crankSpeed / (5000 * Math.PI / 30);
            return 0.87 * peak * r * r;
        }

        public void StartSweep()
        {
            if (Def == null) return;
            _plot?.Clear();
            double top = Engine != null ? Math.Floor(Engine.Info.redlineRadS * 9.5492966 / 500) * 500 : Def.redlineRpm;
            Sweep.Start(Def, 1500, Math.Max(2000, top), 500);
            _plot?.SetCaption(Sweep.Error ?? $"Dyno sweep of {Def.displayName}: WOT, 1500-{top:F0} rpm");
        }

        void Update()
        {
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            if (!ScriptControl)
            {
                // I: tap = ignition on/off, hold = starter (ignition on while cranking), like turning a key
                if (kb != null && kb.iKey.wasPressedThisFrame) _pressStart = Time.unscaledTime;
                bool held = kb != null && kb.iKey.isPressed && _pressStart >= 0 && Time.unscaledTime - _pressStart >= HoldForStarter;
                if (held) { _starterHeld = true; _ignition = true; }
                if (kb != null && kb.iKey.wasReleasedThisFrame)
                {
                    if (!_starterHeld && _pressStart >= 0) _ignition = !_ignition;
                    _starterHeld = false;
                    _pressStart = -1f;
                }
                float want = 0f;
                if (kb != null && kb.wKey.isPressed) want = 1f;
                if (pad != null) want = Mathf.Max(want, pad.rightTrigger.ReadValue());
                _throttle = Mathf.MoveTowards(_throttle, want, Time.unscaledDeltaTime * 12f);
                if (kb != null && kb.lKey.wasPressedThisFrame) LoadOn = !LoadOn;
                if (kb != null && kb.dKey.wasPressedThisFrame) StartSweep();
            }
            else
            {
                _throttle = Mathf.Clamp01(ScriptThrottle);
                _ignition = ScriptIgnition;
                _starterHeld = ScriptStarter;
            }
            if (Engine != null)
            {
                Engine.Throttle = _throttle;
                Engine.Ignition = _ignition;
                Engine.Starter = _starterHeld;
            }
            while (Sweep.TryDequeue(out var pt))
            {
                _plot?.Points.Add(pt);
                _plot?.MarkDirtyRepaint();
                if (_plot != null && _plot.Points.Count == Sweep.Total) _plot.SetCaption(SweepSummary());
            }
            if (Sweep.Error != null) _plot?.SetCaption("Dyno sweep failed: " + Sweep.Error);
            EngineSimLog.Flush();
            if (Time.unscaledTime - _lastUi > 0.1f) { _lastUi = Time.unscaledTime; RefreshUi(); }
        }

        void FixedUpdate()
        {
            if (Engine == null) return;
            var d = EspDrivelineIn.Of(EspMode.Free);
            d.loadTorqueNm = LoadTorque(_last.crankSpeed);
            _last = Engine.FixedStep(Time.fixedDeltaTime, d);
            _torqueSmoothed += 0.05f * ((float)_last.indicatedTorqueNm - _torqueSmoothed);
        }

        string SweepSummary()
        {
            if (_plot == null || _plot.Points.Count == 0) return "";
            DynoSweep.Point peakT = _plot.Points[0], peakP = _plot.Points[0];
            foreach (var p in _plot.Points)
            {
                if (p.torqueNm > peakT.torqueNm) peakT = p;
                if (p.powerW > peakP.powerW) peakP = p;
            }
            return $"{Def.displayName}: peak {peakT.torqueNm:F0} N m at {peakT.rpm:F0} rpm (blue), {peakP.powerW / 745.7:F0} hp at {peakP.rpm:F0} rpm (orange)";
        }

        // ---------------------------------------------------------------------------------------------------------------
        // UI Toolkit panel, built in code

        static readonly Color Panel = new Color(0.07f, 0.08f, 0.09f, 0.90f);
        static readonly Color Ink = new Color(0.93f, 0.93f, 0.90f);
        static readonly Color Dim = new Color(0.65f, 0.66f, 0.64f);
        static readonly Color Accent = new Color(0.36f, 0.74f, 1f);

        void BuildUi()
        {
            var panelSettings = Resources.Load<PanelSettings>(PanelSettingsResource);
            if (panelSettings == null) { Log.Warn("[EngineBench] Resources/BrPanelSettings missing; no panel"); return; }
            _doc = gameObject.AddComponent<UIDocument>();
            _doc.panelSettings = panelSettings;
            _doc.sortingOrder = 10;
            var root = _doc.rootVisualElement;
            root.style.flexGrow = 1;
            root.style.flexDirection = FlexDirection.Row;
            root.pickingMode = PickingMode.Ignore;

            var list = new ScrollView { name = "bench-engines" };
            list.style.width = 280;
            list.style.backgroundColor = Panel;
            list.style.paddingLeft = 8; list.style.paddingRight = 8; list.style.paddingTop = 8;
            var head = new Label("Engines") { style = { color = Dim, fontSize = 13, marginBottom = 4 } };
            list.Add(head);
            foreach (var def in _engines)
            {
                var d = def;
                var b = new Button(() => Select(d)) { text = d.roster ? d.displayName : d.displayName, name = "engine-" + d.id };
                b.style.unityTextAlign = TextAnchor.MiddleLeft;
                b.style.fontSize = 13;
                b.userData = d;
                _engineButtons.Add(b);
                list.Add(b);
            }
            root.Add(list);

            var main = new VisualElement { name = "bench-main" };
            main.style.flexGrow = 1;
            main.style.paddingLeft = 16; main.style.paddingRight = 16; main.style.paddingTop = 12;
            main.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);
            _title = new Label { style = { fontSize = 22, color = Ink, unityFontStyleAndWeight = FontStyle.Bold } };
            _rpm = new Label { name = "bench-rpm", style = { fontSize = 64, color = Accent, unityFontStyleAndWeight = FontStyle.Bold } };
            _state = new Label { style = { fontSize = 16, color = Ink } };
            _readout = new Label { name = "bench-readout", style = { fontSize = 14, color = Ink, whiteSpace = WhiteSpace.Normal } };
            _error = new Label { style = { fontSize = 14, color = new Color(1f, 0.55f, 0.45f), whiteSpace = WhiteSpace.Normal } };
            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            _ignitionButton = new Button(() => _ignition = !_ignition) { text = "Ignition (tap I)" };
            var starter = new Button { text = "Starter (hold I)" };
            starter.RegisterCallback<PointerDownEvent>(_ => _starterHeld = true, TrickleDown.TrickleDown);
            starter.RegisterCallback<PointerUpEvent>(_ => _starterHeld = false);
            starter.RegisterCallback<PointerLeaveEvent>(_ => _starterHeld = false);
            _loadButton = new Button(() => LoadOn = !LoadOn) { text = "Load (L)" };
            _sweepButton = new Button(StartSweep) { text = "Dyno sweep (D)", name = "bench-dyno" };
            buttons.Add(_ignitionButton);
            buttons.Add(starter);
            buttons.Add(_loadButton);
            buttons.Add(_sweepButton);
            _plot = new DynoPlot { name = "bench-plot" };
            _help = new Label("Tap I: ignition on/off.  Hold I: crank the engine.  W or right trigger: throttle.  L: road-like load.  D: dyno sweep.")
            { style = { fontSize = 13, color = Dim, marginTop = 8, whiteSpace = WhiteSpace.Normal } };
            main.Add(_title);
            main.Add(_rpm);
            main.Add(_state);
            main.Add(buttons);
            main.Add(_readout);
            main.Add(_error);
            main.Add(_plot);
            main.Add(_help);
            root.Add(main);
        }

        void RefreshUi()
        {
            if (_doc == null) return;
            foreach (var b in _engineButtons)
            {
                bool sel = b.userData == Def;
                b.style.backgroundColor = sel ? new Color(0.20f, 0.36f, 0.50f) : new Color(0.18f, 0.19f, 0.21f);
                b.style.color = Ink;
            }
            _title.text = Def == null ? "Engine Bench" : $"Engine Bench: {Def.displayName}";
            _error.text = LastError ?? "";
            if (Engine == null)
            {
                _rpm.text = "--";
                _state.text = Native.Available(out var why) ? "no engine loaded" : why;
                _readout.text = "";
                return;
            }
            var s = Engine.Stats();
            var o = _last;
            _rpm.text = $"{o.rpm:F0} rpm";
            _state.text = $"ignition {(_ignition ? "ON" : "off")}   starter {(_starterHeld ? "CRANKING" : "off")}   throttle {_throttle * 100:F0} %   load {(LoadOn ? "on" : "off")}" +
                          (o.revLimitedHint != 0 ? "   LIMITER" : "");
            float rate = AudioSettings.outputSampleRate;
            _readout.text =
                $"torque {_torqueSmoothed:F0} N m (net of load)   MAP {o.manifoldPressurePa / 1000:F1} kPa   AFR {o.afr:F1}   throttle plate {o.throttleOpen * 100:F0} % open\n" +
                $"worker {s.workerMsEma:F2} ms per step (CPU {s.cpuMsEma:F2})   real-time factor {s.rtf:F2}   P-core share {s.pCoreShare * 100:F0} %\n" +
                $"audio ring {s.ringFillFrames / rate * 1000:F1} ms (target {s.ringTargetFrames / rate * 1000:F1} ms)   underruns {s.audioUnderruns}   " +
                $"coast events {s.coastEvents}   stale collects {s.staleSteps}   drift trim {s.driftTrim * 100:+0.00;-0.00} %\n" +
                $"main thread per FixedUpdate p99 {Engine.CostPercentileMs(99):F3} ms   frame p95 {s.frameP95Ms:F1} ms   " +
                $"{Engine.Info.cylinders} cyl, {Engine.Info.simFrequency} Hz, {Engine.Info.fluidSteps} gas substeps, IR {Engine.Info.irTaps} taps, tier {QualityPreset.Current.EngineAudio}";
            _loadButton.text = LoadOn ? "Load: on (L)" : "Load: off (L)";
            _ignitionButton.text = _ignition ? "Ignition: ON (tap I)" : "Ignition: off (tap I)";
            _sweepButton.SetEnabled(!Sweep.Running);
        }
    }
}
