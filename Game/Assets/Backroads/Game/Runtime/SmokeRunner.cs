using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Backroads.Core;
using UnityEngine;

namespace Backroads.Game
{
    /// <summary>
    /// -br-smoke (PLAN 1.9 [S18]), compiled into every build including the Client build the user receives:
    /// boots, runs for -br-smoke-seconds (default 3) and at least 30 frames, takes a screenshot when rendering,
    /// samples the process's network endpoints, writes (out)/smoke.json and quits with 0 (pass) or 1.
    /// It fails on any logged exception or error, or on an outbound endpoint in a non-development build.
    /// </summary>
    public sealed class SmokeRunner : MonoBehaviour
    {
        public const string Flag = "-br-smoke";
        public const string OkLine = "SMOKE OK";

        [Serializable]
        public sealed class Result
        {
            public string status;
            public string build;
            public bool batchmode;
            public bool developmentBuild;
            public string graphics;
            public float seconds;
            public int frames;
            public int exceptions;
            public int errors;
            public string firstProblem;
            public bool lastSessionCrashed;
            public string crashReport;
            public string screenshot;
            public bool egressSupported;
            public int outboundEndpoints;
            public List<NetEgressProbe.Endpoint> endpoints = new List<NetEgressProbe.Endpoint>();
        }

        int _exceptions, _errors;
        string _firstProblem;

        void OnEnable() => Application.logMessageReceivedThreaded += OnLog;
        void OnDisable() => Application.logMessageReceivedThreaded -= OnLog;

        void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Exception) _exceptions++;
            else if (type == LogType.Error || type == LogType.Assert) _errors++;
            else return;
            _firstProblem ??= condition;
        }

        IEnumerator Start()
        {
            float seconds = CommandLine.GetFloat("-br-smoke-seconds", 3f);
            float t0 = Time.realtimeSinceStartup;
            int f0 = Time.frameCount;
            while (Time.realtimeSinceStartup - t0 < seconds || Time.frameCount - f0 < 30) yield return null;

            var r = new Result
            {
                build = BuildInfo.Current.Id,
                batchmode = Application.isBatchMode,
                developmentBuild = Debug.isDebugBuild,
                graphics = SystemInfo.graphicsDeviceType.ToString(),
                seconds = Time.realtimeSinceStartup - t0,
                frames = Time.frameCount - f0,
                lastSessionCrashed = CrashNotice.LastSessionCrashed,
                crashReport = CrashNotice.ReportFolder,
            };

            var outDir = BrPaths.OutDir;
            bool renders = !Application.isBatchMode && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (outDir != null && renders)
            {
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex != null)
                {
                    BrPaths.EnsureDir(outDir);
                    r.screenshot = Path.Combine(outDir, "smoke.png");
                    File.WriteAllBytes(r.screenshot, tex.EncodeToPNG());
                    Destroy(tex);
                }
            }

            r.egressSupported = NetEgressProbe.Supported;
            r.endpoints = NetEgressProbe.Sample();
            // Development players run Unity's PlayerConnection (profiler discovery); Classify allows only that, and only there.
            r.outboundEndpoints = NetEgressProbe.Classify(r.endpoints, Debug.isDebugBuild);
            bool egressOk = r.outboundEndpoints == 0;

            r.exceptions = _exceptions;
            r.errors = _errors;
            r.firstProblem = _firstProblem;
            bool ok = _exceptions == 0 && _errors == 0 && egressOk;
            r.status = ok ? "pass" : "fail";

            if (outDir != null)
            {
                BrPaths.EnsureDir(outDir);
                File.WriteAllText(Path.Combine(outDir, "smoke.json"), JsonUtility.ToJson(r, true));
            }
            if (ok) Log.Info($"{OkLine} frames={r.frames} seconds={r.seconds:F1} endpoints={r.endpoints.Count}");
            else Log.Warn($"SMOKE FAIL exceptions={_exceptions} errors={_errors} outbound={r.outboundEndpoints} first='{_firstProblem}'");
            Application.Quit(ok ? 0 : 1);
        }
    }
}
