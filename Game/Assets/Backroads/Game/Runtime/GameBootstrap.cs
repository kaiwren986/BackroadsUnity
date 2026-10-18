using Backroads.Core;
using UnityEngine;

namespace Backroads.Game
{
    /// <summary>
    /// Composition root. Creates the persistent "Backroads" object before the boot scene loads (CrashCollector writes
    /// its session.lock as early as possible), applies the common switches, and starts the registered client
    /// presentation and the smoke runner once the scene is loaded. Present in every build, server included.
    /// </summary>
    public static class GameBootstrap
    {
        public static GameObject Root { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Root = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeforeSceneLoad()
        {
            if (Root != null) return;
            Root = new GameObject("Backroads");
            Object.DontDestroyOnLoad(Root);

            var b = BuildInfo.Current;
            Log.Info($"boot {b.Id} unity {Application.unityVersion} batchmode={Application.isBatchMode} " +
                     $"gfx={SystemInfo.graphicsDeviceType} data={BrPaths.DataRoot}");

            int cap = CommandLine.GetInt("-br-fps-cap", 0);
            if (cap > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = cap; Log.Info($"fps cap {cap}"); }
            if (CommandLine.Has("-br-silent")) AudioListener.volume = 0f;

            CrashCollector.Begin(Root);
            SessionMetrics.Begin(Root);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            if (Root == null) BeforeSceneLoad();
            PresentationRegistry.StartAll(Root);
            if (CommandLine.Has(SmokeRunner.Flag)) Root.AddComponent<SmokeRunner>();
            if (CommandLine.Has(CrashCollector.CrashTestFlag)) Root.AddComponent<CrashTest>();
        }
    }
}
