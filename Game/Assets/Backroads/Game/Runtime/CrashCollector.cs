using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Backroads.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Backroads.Game
{
    /// <summary>
    /// Crash capture (PLAN 1.9 [S5]), in every build. Writes (data)/session.lock at start and deletes it on a clean exit.
    /// If the lock is still there at the next start, the previous session crashed: the collector copies that session's
    /// player log, Unity's crash folders (%TEMP%\company\product\Crashes) and the plugin log into
    /// (data)/playtest/crash-(time)/ with crash-info.json, and the client shows CrashNotice.Message.
    /// Inactive in the editor unless -br-data is given.
    /// </summary>
    public sealed class CrashCollector : MonoBehaviour
    {
        public const string CrashTestFlag = "-br-crash-test";
        public const string LockName = "session.lock";

        [Serializable]
        public sealed class SessionLock
        {
            public int pid;
            public string startedUtc;
            public string build;
            public string logPath;
            public string lastException;
        }

        [Serializable]
        sealed class CrashInfo
        {
            public string detectedUtc;
            public string detectedBy;
            public SessionLock previous;
            public string[] files;
        }

        static string s_LockPath;
        static SessionLock s_Lock;
        static bool s_Active;
        static bool s_SkipCleanup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_LockPath = null; s_Lock = null; s_Active = false; s_SkipCleanup = false; }

        public static string LockPath => s_LockPath;

        public static void Begin(GameObject root)
        {
            if (s_Active) return;
            if (Application.isEditor && string.IsNullOrEmpty(CommandLine.Get("-br-data"))) return;
            s_Active = true;
            try
            {
                var dir = BrPaths.EnsureDir(BrPaths.DataRoot);
                s_LockPath = Path.Combine(dir, LockName);
                if (File.Exists(s_LockPath)) Recover(File.ReadAllText(s_LockPath));
                s_Lock = new SessionLock
                {
                    pid = Process.GetCurrentProcess().Id,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    build = BuildInfo.Current.Id,
                    logPath = Application.consoleLogPath,
                };
                WriteLock();
                Application.logMessageReceivedThreaded += OnLog;
                Application.quitting += OnQuitting;
                root.AddComponent<CrashCollector>();
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        static void WriteLock()
        {
            try { File.WriteAllText(s_LockPath, JsonUtility.ToJson(s_Lock, true)); }
            catch (Exception e) { Debug.LogWarning(Log.Prefix + "session.lock not written: " + e.Message); }
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception || s_Lock == null) return;
            lock (s_Lock)
            {
                var text = condition + "\n" + stackTrace;
                s_Lock.lastException = text.Length > 4000 ? text.Substring(0, 4000) : text;
                WriteLock();
            }
        }

        static void OnQuitting()
        {
            if (s_SkipCleanup || s_LockPath == null) return;
            try { if (File.Exists(s_LockPath)) File.Delete(s_LockPath); }
            catch (Exception e) { Debug.LogWarning(Log.Prefix + "session.lock not deleted: " + e.Message); }
        }

        static void Recover(string lockJson)
        {
            SessionLock prev = null;
            try { prev = JsonUtility.FromJson<SessionLock>(lockJson); } catch { /* unreadable lock: still a crash */ }
            prev ??= new SessionLock();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var folder = BrPaths.EnsureDir(Path.Combine(BrPaths.PlaytestDir, "crash-" + stamp));
            var files = new System.Collections.Generic.List<string>();

            DateTime since = DateTime.MinValue;
            if (DateTime.TryParse(prev.startedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) since = t.ToUniversalTime();

            // The previous session's log: its own -logFile path if that is not this session's log, else Unity's rotated Player-prev.log.
            var current = Application.consoleLogPath;
            if (!string.IsNullOrEmpty(prev.logPath) && File.Exists(prev.logPath) &&
                !string.Equals(Path.GetFullPath(prev.logPath), string.IsNullOrEmpty(current) ? "" : Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                files.Add(CopyInto(prev.logPath, folder, "Player-prev.log"));
            else
            {
                var dir = string.IsNullOrEmpty(current) ? Application.persistentDataPath : Path.GetDirectoryName(current);
                var rotated = Path.Combine(dir ?? "", "Player-prev.log");
                if (File.Exists(rotated)) files.Add(CopyInto(rotated, folder, "Player-prev.log"));
            }

            // Unity's crash handler output (minidumps) for this product, newer than the crashed session's start.
            var crashes = Path.Combine(Path.GetTempPath(), Application.companyName, Application.productName, "Crashes");
            if (Directory.Exists(crashes))
            {
                foreach (var d in Directory.GetDirectories(crashes))
                {
                    if (Directory.GetLastWriteTimeUtc(d) < since) continue;
                    var dst = Path.Combine(folder, "Crashes", Path.GetFileName(d));
                    CopyDir(d, dst);
                    files.Add(dst);
                }
            }

            // The plugin's own log (engine-sim, from M1) sits next to the player log.
            var pluginLog = Path.Combine(BrPaths.DataRoot, "EngineSimPlugin.log");
            if (File.Exists(pluginLog)) files.Add(CopyInto(pluginLog, folder, "EngineSimPlugin.log"));

            var info = new CrashInfo
            {
                detectedUtc = DateTime.UtcNow.ToString("o"),
                detectedBy = BuildInfo.Current.Id,
                previous = prev,
                files = files.ToArray(),
            };
            File.WriteAllText(Path.Combine(folder, "crash-info.json"), JsonUtility.ToJson(info, true));
            CrashNotice.Set(folder);
            Log.Warn(CrashNotice.Message + ": " + folder);
        }

        static string CopyInto(string src, string folder, string name)
        {
            var dst = Path.Combine(folder, name);
            try
            {
                // The source may still be open (a log of a process that is being torn down): read it shared.
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var o = File.Create(dst)) i.CopyTo(o);
            }
            catch (Exception e) { Debug.LogWarning(Log.Prefix + $"crash report: {src} not copied: {e.Message}"); }
            return dst;
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) CopyInto(f, dst, Path.GetFileName(f));
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>Ends the process the way a crash would: no quitting callbacks, session.lock stays behind.</summary>
        public static void SimulateHardCrash()
        {
            s_SkipCleanup = true;
            Log.Warn("crash test: terminating the process without a clean exit");
            Process.GetCurrentProcess().Kill();
        }
    }

    /// <summary>
    /// -br-crash-test: after one second, throws a managed exception from game code (logged by Unity and recorded in
    /// session.lock), then ends the process without a clean exit, so the next boot must report the crash.
    /// (From M1 the same switch also forces a native fault on an engine worker, PLAN 1.9.)
    /// </summary>
    public sealed class CrashTest : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(1f);
            StartCoroutine(Throw());
            yield return null;
            yield return null;
            CrashCollector.SimulateHardCrash();
        }

        static IEnumerator Throw()
        {
            yield return null;
            throw new InvalidOperationException("br-crash-test: deliberate managed exception");
        }
    }
}
