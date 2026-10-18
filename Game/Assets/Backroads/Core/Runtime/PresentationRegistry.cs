using System;
using System.Collections.Generic;
using UnityEngine;

namespace Backroads.Core
{
    /// <summary>
    /// Client assemblies (excluded from server builds by "!UNITY_SERVER") register their presentation here from
    /// [RuntimeInitializeOnLoadMethod(AfterAssembliesLoaded)]; GameBootstrap starts them once the boot scene is loaded.
    /// Shared code never references a client assembly (Architecture rule 2).
    /// </summary>
    public static class PresentationRegistry
    {
        static readonly List<KeyValuePair<string, Action<GameObject>>> s_Entries = new List<KeyValuePair<string, Action<GameObject>>>();
        static bool s_Started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Entries.Clear(); s_Started = false; }

        public static int Count => s_Entries.Count;

        public static void Register(string id, Action<GameObject> start)
        {
            for (int i = 0; i < s_Entries.Count; i++)
                if (s_Entries[i].Key == id) { s_Entries[i] = new KeyValuePair<string, Action<GameObject>>(id, start); return; }
            s_Entries.Add(new KeyValuePair<string, Action<GameObject>>(id, start));
        }

        /// <summary>Called once by GameBootstrap with the persistent root object.</summary>
        public static void StartAll(GameObject root)
        {
            if (s_Started) return;
            s_Started = true;
            foreach (var e in s_Entries)
            {
                try { e.Value(root); Log.Info($"presentation '{e.Key}' started"); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
