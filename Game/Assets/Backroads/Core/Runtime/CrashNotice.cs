using UnityEngine;

namespace Backroads.Core
{
    /// <summary>Set by CrashCollector when the previous session did not exit cleanly; read by the client UI.</summary>
    public static class CrashNotice
    {
        public const string Message = "The last session crashed; a report was saved for the agents";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { LastSessionCrashed = false; ReportFolder = null; }

        public static bool LastSessionCrashed { get; private set; }
        public static string ReportFolder { get; private set; }

        public static void Set(string folder) { LastSessionCrashed = true; ReportFolder = folder; }
    }
}
