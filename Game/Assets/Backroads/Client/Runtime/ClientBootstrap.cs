using System;
using Backroads.Core;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Backroads.Client
{
    /// <summary>
    /// Starts the client side of M2 through PresentationRegistry (after the boot scene loads, before any Start): loads and
    /// applies settings.json, creates the input driver from bindings.json, and installs the simulation timer.
    /// </summary>
    public static class ClientBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() => PresentationRegistry.Register("client-input", Start);

        static void Start(GameObject root)
        {
            var s = SettingsStore.Current;
            if (!Application.isBatchMode) ClientInput.Create(root, s.bindings);
            SettingsStore.Apply(s, atBoot: true);
            SimTiming.Install();
        }
    }

    /// <summary>Short messages at the top of the screen (hud.js toast); the UI shows them.</summary>
    public static class Toasts
    {
        public static event Action<string, bool> Shown;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Shown = null;

        public static void Show(string text, bool bad = false)
        {
            Log.Info("toast: " + text);
            Shown?.Invoke(text, bad);
        }
    }

    /// <summary>
    /// Simulation main-thread time per rendered frame (PLAN 6.1 continuous gate, F3 overlay): the wall time of the
    /// FixedUpdate player-loop phase (every fixed step of the frame: scripts, physics) measured by two markers inserted at
    /// its start and end, published at the start of PreUpdate.
    /// </summary>
    public static class SimTiming
    {
        struct BrFixedBegin { }
        struct BrFixedEnd { }
        struct BrPublish { }

        static long s_Begin, s_Accum;
        static int s_Steps;

        /// <summary>Milliseconds spent in fixed steps during the last frame, and how many steps ran.</summary>
        public static float LastFrameMs { get; private set; }
        public static int LastFrameSteps { get; private set; }
        public static bool Installed { get; private set; }

        public static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Contains(loop, typeof(BrFixedBegin))) { Installed = true; return; }
            var subs = loop.subSystemList;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type == typeof(FixedUpdate))
                {
                    var list = new System.Collections.Generic.List<PlayerLoopSystem>(subs[i].subSystemList ?? new PlayerLoopSystem[0]);
                    list.Insert(0, new PlayerLoopSystem { type = typeof(BrFixedBegin), updateDelegate = () => s_Begin = System.Diagnostics.Stopwatch.GetTimestamp() });
                    list.Add(new PlayerLoopSystem { type = typeof(BrFixedEnd), updateDelegate = () => { s_Accum += System.Diagnostics.Stopwatch.GetTimestamp() - s_Begin; s_Steps++; } });
                    subs[i].subSystemList = list.ToArray();
                }
                else if (subs[i].type == typeof(PreUpdate))
                {
                    var list = new System.Collections.Generic.List<PlayerLoopSystem>(subs[i].subSystemList ?? new PlayerLoopSystem[0]);
                    list.Insert(0, new PlayerLoopSystem { type = typeof(BrPublish), updateDelegate = Publish });
                    subs[i].subSystemList = list.ToArray();
                }
            }
            loop.subSystemList = subs;
            PlayerLoop.SetPlayerLoop(loop);
            Installed = true;
        }

        static void Publish()
        {
            LastFrameMs = (float)(s_Accum * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            LastFrameSteps = s_Steps;
            s_Accum = 0;
            s_Steps = 0;
        }

        static bool Contains(PlayerLoopSystem s, Type t)
        {
            if (s.type == t) return true;
            if (s.subSystemList == null) return false;
            foreach (var c in s.subSystemList) if (Contains(c, t)) return true;
            return false;
        }
    }
}
