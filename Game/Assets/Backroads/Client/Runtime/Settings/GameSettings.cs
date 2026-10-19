using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Core;
using Backroads.Game;
using UnityEngine;

namespace Backroads.Client
{
    /// <summary>
    /// T2.6 settings.json (PLAN 6.1 M2 [S13]) in the data folder (Application.persistentDataPath; automated runs use
    /// their own, BrPaths.DataRoot): quality tier, volumes, resolution, look and key changes, with a version field.
    /// </summary>
    [Serializable]
    public sealed class GameSettings
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        /// <summary>"" (the build's default, or -br-quality), Low, Mid, High or Ultra.</summary>
        public string quality = "";
        public float masterVolume = 1f;
        public float engineVolume = 1f;
        public float effectsVolume = 1f;
        /// <summary>0 x 0: keep the size the game started with.</summary>
        public int width;
        public int height;
        /// <summary>"" (keep), FullScreenWindow, ExclusiveFullScreen, Windowed.</summary>
        public string fullscreen = "";
        public float lookSensitivity = 1f;
        public bool invertLookY;
        /// <summary>Changes to bindings.json entries (map, action, keys or pad).</summary>
        public List<BindingOverride> bindings = new List<BindingOverride>();

        public GameSettings Clone() => JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(this));

        public bool SameAs(GameSettings o) => o != null && JsonUtility.ToJson(this) == JsonUtility.ToJson(o);
    }

    /// <summary>Reads, writes and applies settings.json.</summary>
    public static class SettingsStore
    {
        public const string FileName = "settings.json";

        static GameSettings s_Current;
        /// <summary>The message of the last load (null when the file was fine or absent), for a toast.</summary>
        public static string LastMessage { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Current = null; LastMessage = null; }

        public static string DefaultPath => Path.Combine(BrPaths.DataRoot, FileName);

        /// <summary>This run's settings (loaded from DefaultPath on first use).</summary>
        public static GameSettings Current
        {
            get
            {
                if (s_Current == null) { s_Current = Load(DefaultPath, out var msg); LastMessage = msg; if (msg != null) Log.Warn("settings: " + msg); }
                return s_Current;
            }
        }

        /// <summary>
        /// Loads a settings file. Missing: defaults, no message. Unreadable or another version: defaults and a message;
        /// the old file is kept next to it as settings.json.bak.
        /// </summary>
        public static GameSettings Load(string path, out string message)
        {
            message = null;
            if (!File.Exists(path)) return new GameSettings();
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e) { message = $"settings.json could not be read ({e.Message}); using the defaults"; return new GameSettings(); }
            GameSettings s = null;
            int version = -1;
            try
            {
                var probe = JsonUtility.FromJson<VersionProbe>(text);
                version = probe?.version ?? -1;
                if (version == GameSettings.CurrentVersion) s = JsonUtility.FromJson<GameSettings>(text);
            }
            catch (Exception e) { message = $"settings.json is damaged ({e.Message}); using the defaults"; }
            if (s == null)
            {
                message ??= version < 0
                    ? "settings.json has no version; using the defaults"
                    : $"settings.json is version {version}, this build reads version {GameSettings.CurrentVersion}; using the defaults";
                Backup(path);
                return new GameSettings();
            }
            s.bindings ??= new List<BindingOverride>();
            s.quality ??= "";
            s.fullscreen ??= "";
            return s;
        }

        [Serializable] sealed class VersionProbe { public int version = -1; }

        static void Backup(string path)
        {
            try { File.Copy(path, path + ".bak", true); }
            catch (Exception e) { Log.Warn("settings: could not keep a copy of the old file: " + e.Message); }
        }

        public static bool Save(GameSettings s, string path, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                s.version = GameSettings.CurrentVersion;
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(s, true));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>Saves Current to DefaultPath (logs a failure).</summary>
        public static void SaveCurrent()
        {
            if (!Save(Current, DefaultPath, out var err)) Log.Warn("settings: not saved: " + err);
        }

        /// <summary>
        /// Applies everything but the key changes (ClientInput reads those): the quality tier through QualityPreset
        /// (unless -br-quality was given; players only, as QualityApply), the master volume (unless -br-silent), the
        /// resolution (unless -screen-width was given), look sensitivity and inversion.
        /// </summary>
        public static void Apply(GameSettings s, bool atBoot)
        {
            if (s == null) return;
            if (!CommandLine.Has(QualityPreset.Flag) && QualityPreset.TryParse(s.quality, out var tier, out bool ultra))
            {
                if (atBoot || tier != QualityPreset.Tier)
                {
                    QualityPreset.Override(tier);
                    if (ultra) QualityPreset.Current.EngineAudio = EngineAudioTier.Ultra;
                    QualityApply.AtBoot();
                }
            }
            if (!CommandLine.Has("-br-silent")) AudioListener.volume = Mathf.Clamp01(s.masterVolume);
            if (s.width > 0 && s.height > 0 && !CommandLine.Has("-screen-width") && !Application.isEditor && !Application.isBatchMode)
            {
                var mode = Enum.TryParse<FullScreenMode>(s.fullscreen, out var fm) ? fm : Screen.fullScreenMode;
                if (Screen.width != s.width || Screen.height != s.height || Screen.fullScreenMode != mode) Screen.SetResolution(s.width, s.height, mode);
            }
            var ci = ClientInput.Current;
            if (ci != null) { ci.LookSensitivity = Mathf.Clamp(s.lookSensitivity, 0.2f, 4f); ci.InvertLookY = s.invertLookY; }
        }
    }
}
