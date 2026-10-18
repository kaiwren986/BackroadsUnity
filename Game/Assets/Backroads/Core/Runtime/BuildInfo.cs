using System;
using System.IO;
using UnityEngine;

namespace Backroads.Core
{
    /// <summary>
    /// Version stamp data. BuildCli writes (Product)_Data/StreamingAssets/build.json into every player it builds
    /// (never into the project, so the hashed StreamingAssets tree stays unchanged). In the editor the stamp says "editor".
    /// </summary>
    [Serializable]
    public sealed class BuildInfo
    {
        public string milestone = "M0";
        public string git = "editor";
        public bool dirty;
        public string profile = "Editor";
        public string unity = "";
        public string builtAtUtc = "";
        public string canon = "-";
        public string esp = "-";

        public const string FileName = "build.json";

        static BuildInfo s_Current;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Current = null;

        public static BuildInfo Current => s_Current ??= Load();

        static BuildInfo Load()
        {
            try
            {
                var p = Path.Combine(Application.streamingAssetsPath, FileName);
                if (File.Exists(p))
                {
                    var b = JsonUtility.FromJson<BuildInfo>(File.ReadAllText(p));
                    if (b != null) return b;
                }
            }
            catch (Exception e) { Debug.LogWarning(Log.Prefix + "build.json unreadable: " + e.Message); }
            return new BuildInfo { unity = Application.unityVersion };
        }

        public string ShortGit => string.IsNullOrEmpty(git) ? "unknown" : (git.Length > 7 ? git.Substring(0, 7) : git);

        /// <summary>Build id used for playtest answer files, for example "M0-1a2b3c4-Dev".</summary>
        public string Id => $"{milestone}-{ShortGit}{(dirty ? "+" : "")}-{profile}";

        /// <summary>The bottom-left version stamp: milestone, git hash, profile, canon and ESP hashes.</summary>
        public string Stamp => $"Backroads {milestone}  {ShortGit}{(dirty ? "+" : "")}  {profile}  canon {canon}  esp {esp}";
    }
}
