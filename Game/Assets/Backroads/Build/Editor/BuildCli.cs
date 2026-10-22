using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Core;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Backroads.Build.Editor
{
    /// <summary>
    /// Player builds (PLAN 1.9). Batchmode:
    ///   Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Build.Editor.BuildCli.Build -profile Dev -milestone M0
    /// Profiles: Dev (development, BACKROADS_TESTING, autotests) and Client (what the user receives) go to builds\M(n)\;
    /// ServerHeadless (Windows Mono player with UNITY_SERVER, rule 8) goes to builds\M(n)-server\. -out overrides the folder.
    /// Exit codes: 0 built, 1 build failed, 2 bad arguments, 3 refused because artifacts\.user-playing exists.
    /// </summary>
    public static class BuildCli
    {
        public enum Profile { Dev, Client, ServerHeadless }

        public const string ExeName = "Backroads.exe";
        public const string ServerExeName = "BackroadsServer.exe";
        public const string TestingDefine = "BACKROADS_TESTING";
        public const string ServerDefine = "UNITY_SERVER";

        /// <summary>
        /// The canon test world (lane W; the old Millbrook.unity, renamed in T3.1, ADR-006 section 1): CanonData v1, where
        /// every parity test and the M2 autotests run. The first scene of Dev builds. From M3 on it is Dev-only: Client
        /// builds leave it out once the new Millbrook scene exists.
        /// </summary>
        public const string CanonTestWorldScenePath = "Assets/Backroads/Scenes/CanonTestWorld.unity";

        /// <summary>The new Millbrook (lane W, T3.3, ADR-006): the real town, in every build once its file exists.</summary>
        public const string MillbrookScenePath = "Assets/Backroads/Scenes/Millbrook.unity";

        /// <summary>The Engine Bench (lane V, M1): a camera and a light; EngineBench builds itself when the scene loads.</summary>
        public const string EngineBenchScenePath = "Assets/Backroads/Scenes/EngineBench.unity";

        /// <summary>
        /// CanonTestWorld, Millbrook, EngineBench, TestYard: each only when its file exists (the test yard always does).
        /// The editor's build settings list these; a build takes ScenesFor(profile).
        /// </summary>
        public static string[] Scenes
        {
            get
            {
                var list = new List<string>();
                if (File.Exists(Path.Combine(RepoPaths.Game, CanonTestWorldScenePath))) list.Add(CanonTestWorldScenePath);
                if (File.Exists(Path.Combine(RepoPaths.Game, MillbrookScenePath))) list.Add(MillbrookScenePath);
                if (File.Exists(Path.Combine(RepoPaths.Game, EngineBenchScenePath))) list.Add(EngineBenchScenePath);
                list.Add(TestYardBuilder.ScenePath);
                return list.ToArray();
            }
        }

        /// <summary>
        /// The scenes of one build profile: Scenes, except that Client builds leave the canon test world out once the new
        /// Millbrook scene exists (T3.1 step 2: Dev-only from M3 on; until T3.3 it is the only drivable world).
        /// </summary>
        public static string[] ScenesFor(Profile profile)
        {
            var list = new List<string>(Scenes);
            if (profile == Profile.Client && list.Contains(MillbrookScenePath)) list.Remove(CanonTestWorldScenePath);
            return list.ToArray();
        }

        public sealed class Outcome
        {
            public Profile profile;
            public string milestone;
            public string outDir;
            public string exe;
            public bool success;
            public string error;
            public double seconds;
            public long bytes;
            public List<string> problems = new List<string>();
        }

        // ---------------------------------------------------------------------------------------------------------
        public static void Build()
        {
            int code;
            try
            {
                var args = Environment.GetCommandLineArgs();
                string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
                if (!Enum.TryParse(Arg("-profile") ?? "Dev", true, out Profile profile)) { Debug.LogError("[BR] unknown -profile"); EditorApplication.Exit(2); return; }
                var milestone = Arg("-milestone") ?? "M0";
                var o = BuildProfile(profile, milestone, Arg("-out"), args.Contains("-force"));
                code = o.success ? 0 : (o.error != null && o.error.StartsWith("refused") ? 3 : 1);
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static string DefaultOutDir(Profile p, string milestone) =>
            Path.Combine(RepoPaths.Builds, p == Profile.ServerHeadless ? milestone + "-server" : milestone);

        public static Outcome BuildProfile(Profile profile, string milestone, string outDir = null, bool force = false)
        {
            var sw = Stopwatch.StartNew();
            var o = new Outcome { profile = profile, milestone = milestone, outDir = Path.GetFullPath(outDir ?? DefaultOutDir(profile, milestone)) };
            bool server = profile == Profile.ServerHeadless;
            o.exe = Path.Combine(o.outDir, server ? ServerExeName : ExeName);

            if (!force && File.Exists(RepoPaths.UserPlayingLock))
            {
                o.error = "refused: artifacts\\.user-playing exists (the user is playing a build)";
                Debug.LogError("[BR] " + o.error);
                WriteReport(o);
                return o;
            }

            // Every build carries canon (lane W, T1.1): verify CanonData v1 and import it when missing or stale.
            if (!Backroads.Canon.Editor.CanonImporter.EnsureImported(out var canonMessage))
            {
                o.error = canonMessage;
                Debug.LogError("[BR] BuildCli: " + canonMessage);
                WriteReport(o);
                return o;
            }
            Debug.Log("[BR] BuildCli: " + canonMessage);

            CleanOutDir(o.outDir, server ? ServerExeName : ExeName);
            Directory.CreateDirectory(o.outDir);

            var options = new BuildPlayerOptions
            {
                scenes = ScenesFor(profile),
                locationPathName = o.exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            };
            switch (profile)
            {
                case Profile.Dev:
                    options.options |= BuildOptions.Development;
                    options.extraScriptingDefines = new[] { TestingDefine };
                    break;
                case Profile.Client:
                    break;
                case Profile.ServerHeadless:
                    options.extraScriptingDefines = new[] { ServerDefine };
                    break;
            }

            Debug.Log($"[BR] BuildCli: {profile} {milestone} -> {o.exe}");
            // Rule 8: the plugin importer's define constraint (!UNITY_SERVER, set by ProjectSetup) is not applied to
            // BuildPlayerOptions.extraScriptingDefines, so native plugins are also filtered here for the server player.
            var excluded = server ? ExcludeClientPlugins() : new List<PluginImporter>();
            BuildReport report;
            try { report = BuildPipeline.BuildPlayer(options); }
            finally { foreach (var pi in excluded) { try { pi.SetIncludeInBuildDelegate(null); } catch (ArgumentNullException) { pi.SetIncludeInBuildDelegate(_ => true); } } }
            var summary = report.summary;
            o.success = summary.result == BuildResult.Succeeded;
            o.bytes = (long)summary.totalSize;
            if (!o.success)
            {
                o.error = $"{summary.result}, {summary.totalErrors} errors";
                foreach (var step in report.steps)
                    foreach (var m in step.messages)
                        if (m.type == LogType.Error || m.type == LogType.Exception) o.problems.Add(m.content);
            }
            else
            {
                var dataDir = Path.Combine(o.outDir, Path.GetFileNameWithoutExtension(o.exe) + "_Data");
                WriteBuildInfo(dataDir, profile, milestone);
                WriteNotices(Path.Combine(o.outDir, "THIRD_PARTY_NOTICES.txt"), milestone);
                if (server)
                {
                    o.problems.AddRange(ServerContents.Check(o.outDir));
                    if (o.problems.Count > 0) { o.success = false; o.error = "server contents (rule 8): " + string.Join("; ", o.problems); }
                    WriteText(Path.Combine(o.outDir, "Server.bat"),
                        "@echo off\r\nrem Dedicated server canary (M0). Binds to 127.0.0.1 only.\r\n\"%~dp0" + ServerExeName + "\" -batchmode -nographics -br-bind 127.0.0.1 %*\r\n");
                }
                else
                {
                    if (profile == Profile.Client)
                    {
                        // Burst writes debug symbols next to every player; they are not for the user's folder.
                        foreach (var d in Directory.GetDirectories(o.outDir, "*_BurstDebugInformation_DoNotShip")) Directory.Delete(d, true);
                    }
                    WritePlayBat(Path.Combine(o.outDir, "Play.bat"));
                    WritePlaytestMd(Path.Combine(o.outDir, "PLAYTEST.md"), milestone, profile);
                }
            }
            o.seconds = sw.Elapsed.TotalSeconds;
            WriteReport(o);
            if (o.success) Debug.Log($"[BR] BuildCli: {profile} OK in {o.seconds:F0} s, {o.bytes / (1024 * 1024)} MB -> {o.outDir}");
            else Debug.LogError($"[BR] BuildCli: {profile} FAILED: {o.error}");
            return o;
        }

        /// <summary>Native plugins whose define constraints say !UNITY_SERVER (EngineSimPlugin.dll) are left out of this build.</summary>
        static List<PluginImporter> ExcludeClientPlugins()
        {
            var list = new List<PluginImporter>();
            foreach (var pi in PluginImporter.GetAllImporters())
            {
                if (!pi.isNativePlugin || pi.DefineConstraints == null || !pi.DefineConstraints.Contains("!" + ServerDefine)) continue;
                pi.SetIncludeInBuildDelegate(_ => false);
                list.Add(pi);
                Debug.Log("[BR] BuildCli: server build excludes " + pi.assetPath);
            }
            return list;
        }

        static void CleanOutDir(string dir, string exeName)
        {
            if (!Directory.Exists(dir)) return;
            var underBuilds = Path.GetFullPath(dir).StartsWith(Path.GetFullPath(RepoPaths.Builds) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!underBuilds && !File.Exists(Path.Combine(dir, exeName)))
                throw new InvalidOperationException($"refusing to clean {dir}: not under builds\\ and not a previous build");
            Directory.Delete(dir, true);
        }

        static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

        static void WriteBuildInfo(string dataDir, Profile profile, string milestone)
        {
            var sa = Path.Combine(dataDir, "StreamingAssets");
            Directory.CreateDirectory(sa);
            var git = RepoPaths.Git("rev-parse HEAD") ?? "unknown";
            var status = RepoPaths.Git("status --porcelain --untracked-files=no");
            var b = new BuildInfo
            {
                milestone = milestone, git = git, dirty = !string.IsNullOrEmpty(status), profile = profile.ToString(),
                unity = Application.unityVersion, builtAtUtc = DateTime.UtcNow.ToString("o"), canon = CanonStamp(), esp = "-",
            };
            WriteText(Path.Combine(sa, BuildInfo.FileName), JsonUtility.ToJson(b, true));
        }

        static string CanonStamp()
        {
            var a = Backroads.Canon.Editor.CanonImporter.LoadImported();
            return a == null || string.IsNullOrEmpty(a.manifestSha256) ? "-" : "v1 " + a.manifestSha256.Substring(0, 12) + " H=" + a.HeightsShort;
        }

        /// <summary>Play.bat marks the machine as "user playing" (PLAN 1.9 machine locks) while the game runs.</summary>
        public static string PlayBatText() =>
            "@echo off\r\n" +
            "setlocal\r\n" +
            "rem Starts Backroads. While it runs, artifacts\\.user-playing tells the agents not to start heavy work.\r\n" +
            "set \"BR_ART=%~dp0..\\..\\artifacts\"\r\n" +
            "if exist \"%BR_ART%\\\" echo %DATE% %TIME% > \"%BR_ART%\\.user-playing\"\r\n" +
            "start \"\" /wait \"%~dp0" + ExeName + "\" %*\r\n" +
            "if exist \"%BR_ART%\\.user-playing\" del \"%BR_ART%\\.user-playing\"\r\n" +
            "endlocal\r\n";

        static void WritePlayBat(string path) => WriteText(path, PlayBatText());

        static void WritePlaytestMd(string path, string milestone, Profile profile)
        {
            var src = Path.Combine(RepoPaths.Game, "Assets", "StreamingAssets", "Playtest", milestone + ".json");
            var sb = new StringBuilder();
            sb.Append($"# Backroads {milestone}: what to try\n\n");
            Dictionary<string, object> j = null;
            if (File.Exists(src)) j = BrJson.Obj(BrJson.Parse(File.ReadAllText(src)));
            var summary = j != null ? BrJson.Str(j, "summary") : null;
            if (!string.IsNullOrEmpty(summary)) sb.Append(summary).Append("\n\n");
            sb.Append("## How to play\n\n1. Double-click `Play.bat` in this folder.\n2. Press **F10** for the playtest checklist. Answer each item; the agents read your answers.\n3. Close the window when you are done.\n\n");
            if (j != null && j.TryGetValue("tryThis", out var tt) && tt is List<object> tryThis && tryThis.Count > 0)
            {
                sb.Append("## Try this\n\n");
                foreach (var t in tryThis) sb.Append("- ").Append(t).Append('\n');
                sb.Append('\n');
            }
            if (j != null && j.TryGetValue("items", out var it) && it is List<object> items && items.Count > 0)
            {
                sb.Append("## The F10 checklist\n\n");
                foreach (var x in items.OfType<Dictionary<string, object>>()) sb.Append("- ").Append(BrJson.Str(x, "text")).Append('\n');
                sb.Append($"\nYour answers are saved to `%USERPROFILE%\\AppData\\LocalLow\\{PlayerSettings.companyName}\\{PlayerSettings.productName}\\playtest\\`.\n\n");
            }
            if (j != null && j.TryGetValue("knownIssues", out var ki) && ki is List<object> issues && issues.Count > 0)
            {
                sb.Append("## Known issues\n\n");
                foreach (var t in issues) sb.Append("- ").Append(t).Append('\n');
                sb.Append('\n');
            }
            sb.Append("## If something goes wrong\n\nIf the game crashes, the next start says \"").Append(CrashNotice.Message)
              .Append("\" and keeps the report in the `playtest` folder above, so the agents can look at it.\n\n");
            sb.Append($"Build: {milestone}, profile {profile}, git {RepoPaths.Git("rev-parse --short HEAD") ?? "unknown"}, Unity {Application.unityVersion}.\n");
            sb.Append("Third-party software and licences: `THIRD_PARTY_NOTICES.txt`.\n");
            WriteText(path, sb.ToString());
        }

        /// <summary>THIRD_PARTY_NOTICES.txt from the shipped entries of third-party.json [C1].</summary>
        public static string NoticesText(string milestone)
        {
            var reg = BrJson.Obj(BrJson.Parse(File.ReadAllText(RepoPaths.InRepo("third-party.json"))));
            var sb = new StringBuilder();
            sb.Append("Backroads ").Append(milestone).Append(": third-party notices\n");
            sb.Append("=====================================\n\n");
            sb.Append("This program uses the Unity engine, (c) Unity Technologies, under the Unity Terms of Service.\n\n");
            foreach (var e in BrJson.Arr(reg["entries"]).OfType<Dictionary<string, object>>())
            {
                if (!(e.TryGetValue("shipped", out var s) && s is bool b && b)) continue;
                sb.Append(BrJson.Str(e, "name"));
                var v = BrJson.Str(e, "version");
                if (!string.IsNullOrEmpty(v)) sb.Append(" (").Append(v).Append(')');
                sb.Append('\n');
                sb.Append("  Licence: ").Append(BrJson.Str(e, "licence")).Append('\n');
                var c = BrJson.Str(e, "copyright");
                if (!string.IsNullOrEmpty(c)) sb.Append("  Copyright: ").Append(c).Append('\n');
                sb.Append("  Source: ").Append(BrJson.Str(e, "source")).Append('\n');
                var n = BrJson.Str(e, "note");
                if (!string.IsNullOrEmpty(n)) sb.Append("  Note: ").Append(n).Append('\n');
                sb.Append('\n');
            }
            return sb.ToString().Replace("\n", "\r\n");
        }

        static void WriteNotices(string path, string milestone) => WriteText(path, NoticesText(milestone));

        static void WriteReport(Outcome o)
        {
            try
            {
                Directory.CreateDirectory(RepoPaths.Artifacts);
                var sb = new StringBuilder("{\n");
                sb.Append($"  \"profile\": \"{o.profile}\",\n  \"milestone\": \"{o.milestone}\",\n");
                sb.Append($"  \"success\": {(o.success ? "true" : "false")},\n  \"seconds\": {o.seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"bytes\": {o.bytes},\n  \"exe\": \"{Esc(o.exe)}\",\n  \"error\": {(o.error == null ? "null" : "\"" + Esc(o.error) + "\"")},\n");
                sb.Append("  \"problems\": [").Append(string.Join(", ", o.problems.Take(50).Select(p => "\"" + Esc(p) + "\""))).Append("]\n}\n");
                WriteText(Path.Combine(RepoPaths.Artifacts, $"build-{o.profile}-{o.milestone}.json"), sb.ToString());
            }
            catch (Exception e) { Debug.LogWarning("[BR] build report not written: " + e.Message); }
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

        // ---------------------------------------------------------------------------------------------------------
        // Architecture rule 7: the player scripts compile with UNITY_SERVER (fast check in the merge tier).
        public sealed class ServerCompile
        {
            public bool success;
            public List<string> assemblies = new List<string>();
            public List<string> problems = new List<string>();
            public double seconds;
        }

        public static void CompileServerScripts()
        {
            var r = CompileServerScriptsTo(Path.Combine(RepoPaths.Artifacts, "server-compile"));
            foreach (var p in r.problems) Debug.LogError("[BR] server compile: " + p);
            Debug.Log($"[BR] CompileServerScripts {(r.success ? "OK" : "FAILED")} in {r.seconds:F0} s: {string.Join(", ", r.assemblies.Where(a => a.StartsWith("Backroads")))}");
            if (Application.isBatchMode) EditorApplication.Exit(r.success ? 0 : 1);
        }

        public static ServerCompile CompileServerScriptsTo(string outDir)
        {
            var sw = Stopwatch.StartNew();
            var r = new ServerCompile();
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var settings = new ScriptCompilationSettings
            {
                target = BuildTarget.StandaloneWindows64,
                group = BuildTargetGroup.Standalone,
                options = ScriptCompilationOptions.None,
                extraScriptingDefines = new[] { ServerDefine },
            };
            var result = PlayerBuildInterface.CompilePlayerScripts(settings, outDir);
            r.assemblies.AddRange(result.assemblies ?? Enumerable.Empty<string>());
            if (r.assemblies.Count == 0) r.problems.Add("compilation produced no assemblies (compile errors are in the editor log)");
            foreach (var need in ServerContents.Required)
                if (r.assemblies.Count > 0 && !r.assemblies.Contains(need)) r.problems.Add("missing " + need);
            foreach (var a in r.assemblies)
                if (ServerContents.IsForbiddenAssembly(a)) r.problems.Add("client assembly compiled under UNITY_SERVER: " + a);
            r.success = r.problems.Count == 0;
            r.seconds = sw.Elapsed.TotalSeconds;
            return r;
        }
    }
}
