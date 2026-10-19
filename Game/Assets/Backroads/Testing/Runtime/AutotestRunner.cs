using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Backroads.Client.UI;
using Backroads.Core;
using Backroads.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Debug = UnityEngine.Debug;

namespace Backroads.Testing
{
    /// <summary>
    /// Player autotests (PLAN 1.9), Dev builds only (BACKROADS_TESTING). -br-autotest (id) loads
    /// StreamingAssets/Autotest/(id).json, runs it, writes result.json (plus frametimes.csv, screenshots and a contact
    /// sheet) into -br-out, and quits with 0 (pass) or 1 (fail).
    /// Kinds: "boot" (smoke: run N seconds, screenshot), "crash-recover" (m0.crash.recover), "net-egress" (m0.net.egress),
    /// "playtest-f10" (m0.playtest.f10: F10 opens the checklist, a Yes answer is saved, F10 closes it).
    /// </summary>
    public sealed class AutotestRunner : MonoBehaviour
    {
        public const string Flag = "-br-autotest";

        [Serializable]
        public sealed class Scenario
        {
            public string id;
            public string kind = "boot";
            public float seconds = 3f;
            public bool screenshot = true;
            public int fpsCap;
            public float childTimeoutSeconds = 90f;
            public string notes;
        }

        [Serializable]
        public sealed class Result
        {
            public string id;
            public string kind;
            public string status;
            public string build;
            public bool developmentBuild;
            public string startedUtc;
            public float seconds;
            public int frames;
            public float p50FrameMs;
            public float p95FrameMs;
            public float p99FrameMs;
            public int exceptions;
            public int errors;
            public string firstProblem;
            public List<string> evidence = new List<string>();
            public List<string> failures = new List<string>();
            public List<string> notes = new List<string>();
            public List<NetEgressProbe.Endpoint> endpoints = new List<NetEgressProbe.Endpoint>();
            public List<NamedValue> metrics = new List<NamedValue>();
            public string gpu;
            public string quality;
        }

        [Serializable]
        public struct NamedValue
        {
            public string name;
            public double value;
        }

        Scenario _sc;
        string _scJson;
        Result _r;
        string _out;
        MetricsRecorder _metrics;
        int _exceptions, _errors;
        string _firstProblem;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            var id = CommandLine.Get(Flag);
            if (string.IsNullOrEmpty(id) || Application.isEditor) return;
            var go = new GameObject("AutotestRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<AutotestRunner>();
        }

        void OnEnable() => Application.logMessageReceivedThreaded += OnLog;
        void OnDisable() => Application.logMessageReceivedThreaded -= OnLog;

        void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Exception) _exceptions++;
            else if (type == LogType.Error || type == LogType.Assert) _errors++;
            else return;
            _firstProblem ??= condition;
        }

        public static Scenario LoadScenario(string id) => LoadScenario(id, out _);

        public static Scenario LoadScenario(string id, out string json)
        {
            json = null;
            var path = Path.Combine(Application.streamingAssetsPath, "Autotest", id + ".json");
            if (!File.Exists(path)) return null;
            json = File.ReadAllText(path);
            var s = JsonUtility.FromJson<Scenario>(json);
            if (s != null && string.IsNullOrEmpty(s.id)) s.id = id;
            return s;
        }

        IEnumerator Start()
        {
            var id = CommandLine.Get(Flag);
            _out = BrPaths.EnsureDir(BrPaths.OutDir ?? Path.Combine(Application.persistentDataPath, "autotest", id));
            _r = new Result { id = id, build = BuildInfo.Current.Id, developmentBuild = Debug.isDebugBuild, startedUtc = DateTime.UtcNow.ToString("o"), gpu = SystemInfo.graphicsDeviceName, quality = (QualityPreset.Requested ? "" : "default ") + QualityPreset.Describe(QualityPreset.Current) };
            _sc = LoadScenario(id, out _scJson);
            if (_sc == null) { Fail($"no scenario StreamingAssets/Autotest/{id}.json"); Finish(); yield break; }
            _r.kind = _sc.kind;
            // Autotests start from background scripts and often never get the foreground; by default the Input System
            // disables the keyboard without focus and drops the simulated key events (lane I-b handoff).
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            if (_sc.fpsCap > 0 && CommandLine.GetInt("-br-fps-cap", 0) == 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = _sc.fpsCap; }
            _metrics = gameObject.AddComponent<MetricsRecorder>();
            Log.Info($"autotest {id} ({_sc.kind}) -> {_out}");

            float t0 = Time.realtimeSinceStartup;
            int f0 = Time.frameCount;
            switch (_sc.kind)
            {
                case "boot": yield return RunBoot(); break;
                case "net-egress": yield return RunNetEgress(); break;
                case "crash-recover": yield return RunCrashRecover(); break;
                case "playtest-f10": yield return RunPlaytestF10(); break;
                default:
                    var lane = AutotestScenarios.Create(_sc.kind);
                    if (lane == null) Fail("unknown scenario kind " + _sc.kind);
                    else yield return lane.Run(new AutotestContext(this, _sc, _scJson, _out));
                    break;
            }
            _r.seconds = Time.realtimeSinceStartup - t0;
            _r.frames = Time.frameCount - f0;
            Finish();
        }

        internal void Fail(string why) { _r.failures.Add(why); Log.Warn("autotest failure: " + why); }
        internal void Note(string note) => _r.notes.Add(note);
        internal void AddEvidence(string path) => _r.evidence.Add(path);
        internal void Metric(string name, double value) => _r.metrics.Add(new NamedValue { name = name, value = value });

        internal bool Renders => !Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        internal IEnumerator Wait(float seconds)
        {
            float t = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t < seconds) yield return null;
        }

        internal IEnumerator Screenshot(string name)
        {
            if (!Renders) { _r.notes.Add($"screenshot {name} skipped: no rendering (batchmode or null device)"); yield break; }
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex == null) { Fail("screenshot failed"); yield break; }
            var path = Path.Combine(_out, name + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            _r.evidence.Add(path);
        }

        IEnumerator RunBoot()
        {
            yield return Wait(_sc.seconds);
            if (_sc.screenshot)
            {
                yield return Screenshot("shot_000");
                if (Renders && !File.Exists(Path.Combine(_out, "shot_000.png"))) Fail("no screenshot written");
            }
        }

        IEnumerator RunNetEgress()
        {
            var seen = new Dictionary<string, NetEgressProbe.Endpoint>();
            if (!NetEgressProbe.Supported) { Fail("endpoint tables unsupported on this platform"); yield break; }
            float t = Time.realtimeSinceStartup;
            int samples = 0;
            while (Time.realtimeSinceStartup - t < _sc.seconds)
            {
                foreach (var e in NetEgressProbe.Sample())
                {
                    var key = $"{e.protocol} {e.local}:{e.localPort} {e.remote}:{e.remotePort} {e.state}";
                    if (!seen.ContainsKey(key)) seen[key] = e;
                }
                samples++;
                yield return Wait(1f);
            }
            _r.endpoints.AddRange(seen.Values);
            _r.notes.Add($"{samples} samples of this process's TCP/UDP tables over {_sc.seconds:F0} s (GetExtendedTcpTable/GetExtendedUdpTable by PID)");
            NetEgressProbe.Classify(_r.endpoints, Debug.isDebugBuild);
            foreach (var e in _r.endpoints)
            {
                if (!e.outbound) continue;
                if (!string.IsNullOrEmpty(e.allowedReason)) _r.notes.Add($"allowed: {e.protocol} {e.local}:{e.localPort} ({e.allowedReason})");
                else Fail($"outbound endpoint {e.protocol} {e.local}:{e.localPort} -> {e.remote}:{e.remotePort} {e.state}");
            }
        }

        Keyboard _kb;

        // A virtual keyboard of our own: the real one may already have been disabled by an earlier focus loss, and a
        // disabled device drops queued events. Its events also make it Keyboard.current, which the client UI reads.
        IEnumerator PressKey(Key key)
        {
            if (_kb == null) _kb = InputSystem.AddDevice<Keyboard>("AutotestKeyboard");
            if (!_kb.enabled) InputSystem.EnableDevice(_kb);
            _kb.MakeCurrent();
            yield return null;
            var kb = _kb;
            InputSystem.QueueStateEvent(kb, new KeyboardState(key));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            yield return null;
            yield return null;
        }

        IEnumerator RunPlaytestF10()
        {
            if (!Renders) { Fail("playtest-f10 needs a window (no -batchmode)"); yield break; }
            yield return Wait(Mathf.Max(1f, _sc.seconds));
            var cl = PlaytestChecklist.Current;
            if (cl == null) { Fail("no playtest checklist (client UI did not start)"); yield break; }
            if (cl.Items.Count == 0) Fail("checklist has no items (StreamingAssets/Playtest/<milestone>.json)");
            if (cl.Visible) Fail("checklist visible before F10");
            yield return PressKey(Key.F10);
            if (!cl.Visible) { Fail("F10 did not open the checklist"); yield break; }
            yield return Screenshot("f10_open");
            foreach (var it in cl.Items)
                if (!cl.Press(it.id, "yes")) Fail("no Yes button for " + it.id);
            yield return null;
            foreach (var it in cl.Items)
                if (cl.AnswerOf(it.id) != "yes") Fail($"{it.id}: answer is '{cl.AnswerOf(it.id)}' after pressing Yes");
            yield return Screenshot("f10_answered");
            yield return PressKey(Key.F10);
            if (cl.Visible) Fail("second F10 did not close the checklist");
            if (!File.Exists(cl.AnswersPath)) Fail("answers not saved to " + cl.AnswersPath);
            else
            {
                _r.evidence.Add(cl.AnswersPath);
                if (!FileContains(cl.AnswersPath, "\"yes\"")) Fail("saved answers do not contain the Yes answers");
            }
        }

        // m0.crash.recover: phase 1 runs a copy of this player with -br-crash-test (managed exception, then the process
        // ends without a clean exit); phase 2 boots it again on the same data folder; the crash report must exist.
        IEnumerator RunCrashRecover()
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { Fail("cannot find own executable"); yield break; }
            // The children get their own data folder; this runner's own session.lock lives in (out)/data.
            string data = Path.Combine(_out, "crash-data");
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.CreateDirectory(data);
            // -br-ci marks the children as script-started, so the CI locks never mistake them for the user playing.
            string common = $"-batchmode -nographics -br-silent -br-ci -br-data \"{data}\"";

            int code1 = 0;
            yield return RunChild(exe, $"{common} -br-crash-test -br-out \"{Path.Combine(_out, "phase1")}\" -logFile \"{Path.Combine(_out, "phase1.log")}\"", c => code1 = c);
            _r.notes.Add($"phase 1 (-br-crash-test) exit code {code1}");
            if (code1 == 0) Fail("phase 1 exited cleanly; the crash test did not crash");
            if (!File.Exists(Path.Combine(data, CrashCollector.LockName))) Fail("phase 1 left no session.lock");
            var log1 = Path.Combine(_out, "phase1.log");
            if (!FileContains(log1, "br-crash-test: deliberate managed exception")) Fail("phase 1 log has no managed exception");

            int code2 = -1;
            yield return RunChild(exe, $"{common} -br-smoke -br-out \"{Path.Combine(_out, "phase2")}\" -logFile \"{Path.Combine(_out, "phase2.log")}\"", c => code2 = c);
            _r.notes.Add($"phase 2 (-br-smoke) exit code {code2}");
            if (code2 != 0) Fail($"phase 2 exit code {code2}");

            var playtest = Path.Combine(data, "playtest");
            var reports = Directory.Exists(playtest) ? Directory.GetDirectories(playtest, "crash-*") : new string[0];
            if (reports.Length == 0) Fail("no crash-* report folder after phase 2");
            else
            {
                var r = reports[0];
                _r.evidence.Add(r);
                var info = Path.Combine(r, "crash-info.json");
                if (!File.Exists(info)) Fail("crash-info.json missing");
                else if (!FileContains(info, "br-crash-test")) Fail("crash-info.json does not record the managed exception");
                if (!File.Exists(Path.Combine(r, "Player-prev.log"))) Fail("previous session log not copied");
            }
            if (!FileContains(Path.Combine(_out, "phase2.log"), CrashNotice.Message)) Fail("phase 2 did not report the crash");
            if (File.Exists(Path.Combine(data, CrashCollector.LockName))) Fail("phase 2 left session.lock behind (unclean exit)");
        }

        IEnumerator RunChild(string exe, string args, Action<int> done)
        {
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception e) { Fail("could not start child: " + e.Message); done(-1); yield break; }
            float t = Time.realtimeSinceStartup;
            while (!p.HasExited && Time.realtimeSinceStartup - t < _sc.childTimeoutSeconds) yield return null;
            if (!p.HasExited) { Fail("child timed out: " + args); try { p.Kill(); } catch { } done(-2); yield break; }
            done(p.ExitCode);
        }

        static bool FileContains(string path, string text)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var rd = new StreamReader(s, Encoding.UTF8)) return rd.ReadToEnd().Contains(text);
            }
            catch { return false; }
        }

        void Finish()
        {
            if (_metrics != null)
            {
                _metrics.Recording = false;
                var csv = Path.Combine(_out, "frametimes.csv");
                _metrics.WriteCsv(csv);
                _r.evidence.Add(csv);
                _r.p50FrameMs = _metrics.Percentile(50); _r.p95FrameMs = _metrics.Percentile(95); _r.p99FrameMs = _metrics.Percentile(99);
            }
            var shots = new List<string>();
            foreach (var e in _r.evidence) if (e.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) shots.Add(e);
            if (shots.Count > 0)
            {
                var sheet = ContactSheet.Build(shots, Path.Combine(_out, "contact_sheet.png"));
                if (sheet != null) _r.evidence.Add(sheet);
            }
            _r.exceptions = _exceptions;
            _r.errors = _errors;
            _r.firstProblem = _firstProblem;
            if (_exceptions > 0) Fail($"{_exceptions} exception(s) logged; first: {_firstProblem}");
            else if (_errors > 0) Fail($"{_errors} error(s) logged; first: {_firstProblem}");
            _r.status = _r.failures.Count == 0 ? "pass" : "fail";
            File.WriteAllText(Path.Combine(_out, "result.json"), JsonUtility.ToJson(_r, true));
            Log.Info($"AUTOTEST {_r.id} {_r.status.ToUpperInvariant()} ({_r.failures.Count} failures) -> {_out}");
            Application.Quit(_r.status == "pass" ? 0 : 1);
        }
    }
}
