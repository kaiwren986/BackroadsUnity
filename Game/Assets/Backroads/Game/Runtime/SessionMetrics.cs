using System;
using System.IO;
using Backroads.Core;
using UnityEngine;

namespace Backroads.Game
{
    /// <summary>
    /// Light metrics of the user's own sessions (PLAN 1.9 [S5]), in every build: a frame-time histogram, audio underruns,
    /// coast events and torque-map fallbacks (the last three are counters other systems bump from M1 on). Written on exit to
    /// (data)/playtest/(build)-session-(time).json, next to the F10 answers. Off in the editor and in batchmode (servers, CI)
    /// unless -br-data or -br-out names a folder.
    /// </summary>
    public sealed class SessionMetrics : MonoBehaviour
    {
        /// <summary>Upper bucket edges in milliseconds; the last bucket is everything slower.</summary>
        public static readonly float[] BucketEdgesMs = { 8.33f, 11.1f, 16.7f, 22.2f, 33.3f, 50f, 100f };

        [Serializable]
        public sealed class Report
        {
            public string build;
            public string startedUtc;
            public string endedUtc;
            public float seconds;
            public int frames;
            public float meanFrameMs;
            public float worstFrameMs;
            public float[] bucketEdgesMs;
            public int[] histogram;
            public int audioUnderruns;
            public int coastEvents;
            public int torqueMapFallbacks;
        }

        public static int AudioUnderruns;
        public static int CoastEvents;
        public static int TorqueMapFallbacks;

        static SessionMetrics s_Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Instance = null; AudioUnderruns = 0; CoastEvents = 0; TorqueMapFallbacks = 0; }

        readonly int[] _hist = new int[BucketEdgesMs.Length + 1];
        int _frames;
        double _sumMs;
        float _worst;
        DateTime _started;
        bool _written;

        public static void Begin(GameObject root)
        {
            if (s_Instance != null) return;
            bool explicitData = !string.IsNullOrEmpty(CommandLine.Get("-br-data")) || BrPaths.OutDir != null;
            // The user's own sessions only: not the editor, and not headless servers or CI runs unless they name a data folder.
            if ((Application.isEditor || Application.isBatchMode) && !explicitData) return;
            s_Instance = root.AddComponent<SessionMetrics>();
        }

        void Awake() => _started = DateTime.UtcNow;

        void Update()
        {
            if (Time.frameCount < 3) return; // the first frames include loading
            float ms = Time.unscaledDeltaTime * 1000f;
            int b = 0;
            while (b < BucketEdgesMs.Length && ms > BucketEdgesMs[b]) b++;
            _hist[b]++;
            _frames++;
            _sumMs += ms;
            if (ms > _worst) _worst = ms;
        }

        void OnApplicationQuit() => Write();

        public Report Snapshot() => new Report
        {
            build = BuildInfo.Current.Id,
            startedUtc = _started.ToString("o"),
            endedUtc = DateTime.UtcNow.ToString("o"),
            seconds = (float)(DateTime.UtcNow - _started).TotalSeconds,
            frames = _frames,
            meanFrameMs = _frames > 0 ? (float)(_sumMs / _frames) : 0f,
            worstFrameMs = _worst,
            bucketEdgesMs = BucketEdgesMs,
            histogram = (int[])_hist.Clone(),
            audioUnderruns = AudioUnderruns,
            coastEvents = CoastEvents,
            torqueMapFallbacks = TorqueMapFallbacks,
        };

        void Write()
        {
            if (_written) return;
            _written = true;
            try
            {
                var dir = BrPaths.EnsureDir(BrPaths.PlaytestDir);
                var path = Path.Combine(dir, $"{BuildInfo.Current.Id}-session-{_started.ToLocalTime():yyyyMMdd-HHmmss}.json");
                File.WriteAllText(path, JsonUtility.ToJson(Snapshot(), true));
            }
            catch (Exception e) { Debug.LogWarning(Log.Prefix + "session metrics not written: " + e.Message); }
        }
    }
}
