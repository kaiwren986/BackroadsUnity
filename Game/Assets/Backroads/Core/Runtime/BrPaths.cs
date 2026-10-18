using System.IO;
using UnityEngine;

namespace Backroads.Core
{
    /// <summary>
    /// Where a run writes. -br-data overrides the data root (crash reports, playtest answers, session metrics);
    /// with -br-out and no -br-data, data goes to (out)/data, so automated runs never touch the user's own folder.
    /// Without either, the data root is Application.persistentDataPath (LocalLow\company\product).
    /// </summary>
    public static class BrPaths
    {
        public static string OutDir
        {
            get
            {
                var o = CommandLine.Get("-br-out");
                return string.IsNullOrEmpty(o) ? null : Path.GetFullPath(o);
            }
        }

        public static string DataRoot
        {
            get
            {
                var d = CommandLine.Get("-br-data");
                if (!string.IsNullOrEmpty(d)) return Path.GetFullPath(d);
                var o = OutDir;
                if (o != null) return Path.Combine(o, "data");
                return Application.persistentDataPath;
            }
        }

        public static string PlaytestDir => Path.Combine(DataRoot, "playtest");

        public static string EnsureDir(string path)
        {
            if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path);
            return path;
        }
    }
}
