using System;

namespace Backroads.Core
{
    /// <summary>Render tiers of PLAN §4.3: Low (Deck class), Mid (GTX 1070 class), High (RTX 3070+, the default).</summary>
    public enum QualityTier { Low, Mid, High }

    /// <summary>Engine-sim tiers of PLAN §3.8. Each render tier picks one: Low -> Low, Mid -> High, High -> Ultra.</summary>
    public enum EngineAudioTier { Low, High, Ultra }

    /// <summary>The numbers of one tier (PLAN §4.3, 03 §4.12). Systems read what they need; Backroads.Game applies the Unity side.</summary>
    public sealed class QualityValues
    {
        public QualityTier Tier;
        public float RenderScale;
        public bool Stp;
        /// <summary>Application.targetFrameRate; 0 = uncapped.</summary>
        public int FpsCap;
        public int ShadowCascades;
        public int ShadowResolution;
        public float ShadowDistance;
        public bool Ssao;
        public bool Volumetrics;
        public float GrassDensity;
        public float GrassDistance;
        public int LampLights;
        public float TreeNearRadius;
        public float TerrainLodScale;
        public int ChunkBuildsPerFrame;
        public EngineAudioTier EngineAudio;
    }

    /// <summary>
    /// The quality tier for this run: -br-quality Low|Mid|High (Medium = Mid; Ultra = High render with Ultra engine audio).
    /// Without the switch the tier is High and nothing is changed at start-up (the project defaults). settings.json takes
    /// over the choice in M2. Lives in Core so every lane can read it; Backroads.Game applies it to Unity and URP.
    /// </summary>
    public static class QualityPreset
    {
        public const string Flag = "-br-quality";

        static QualityValues s_Current;
        static bool? s_Requested;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Current = null; s_Requested = null; }

        /// <summary>True when -br-quality was given (and parsed).</summary>
        public static bool Requested { get { _ = Current; return s_Requested == true; } }

        public static QualityTier Tier => Current.Tier;

        public static QualityValues Current
        {
            get
            {
                if (s_Current != null) return s_Current;
                var arg = CommandLine.Get(Flag);
                bool ok = TryParse(arg, out var tier, out var ultra);
                s_Requested = ok;
                if (!ok)
                {
                    if (arg != null) Log.Warn($"{Flag} '{arg}' not understood (Low, Mid, High, Ultra); using High");
                    tier = QualityTier.High;
                }
                s_Current = For(tier);
                if (ultra) s_Current.EngineAudio = EngineAudioTier.Ultra;
                return s_Current;
            }
        }

        /// <summary>Tests and tools can pin a tier (null goes back to the command line).</summary>
        public static void Override(QualityTier? tier)
        {
            s_Current = tier.HasValue ? For(tier.Value) : null;
            s_Requested = tier.HasValue ? true : (bool?)null;
        }

        public static bool TryParse(string s, out QualityTier tier, out bool ultra)
        {
            ultra = false;
            tier = QualityTier.High;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "low": tier = QualityTier.Low; return true;
                case "mid": case "medium": tier = QualityTier.Mid; return true;
                case "high": tier = QualityTier.High; return true;
                case "ultra": tier = QualityTier.High; ultra = true; return true;
                default: return false;
            }
        }

        public static QualityValues For(QualityTier tier)
        {
            switch (tier)
            {
                case QualityTier.Low:
                    return new QualityValues
                    {
                        Tier = tier, RenderScale = 0.7f, Stp = true, FpsCap = 30,
                        ShadowCascades = 2, ShadowResolution = 1024, ShadowDistance = 120f, Ssao = false, Volumetrics = false,
                        GrassDensity = 0.25f, GrassDistance = 40f, LampLights = 6,
                        TreeNearRadius = 90f, TerrainLodScale = 0.7f, ChunkBuildsPerFrame = 1, EngineAudio = EngineAudioTier.Low,
                    };
                case QualityTier.Mid:
                    return new QualityValues
                    {
                        Tier = tier, RenderScale = 1f, Stp = false, FpsCap = 0,
                        ShadowCascades = 4, ShadowResolution = 2048, ShadowDistance = 250f, Ssao = true, Volumetrics = false,
                        GrassDensity = 0.6f, GrassDistance = 80f, LampLights = 16,
                        TreeNearRadius = 140f, TerrainLodScale = 1f, ChunkBuildsPerFrame = 2, EngineAudio = EngineAudioTier.High,
                    };
                default:
                    return new QualityValues
                    {
                        Tier = QualityTier.High, RenderScale = 1f, Stp = false, FpsCap = 0,
                        ShadowCascades = 4, ShadowResolution = 4096, ShadowDistance = 400f, Ssao = true, Volumetrics = true,
                        GrassDensity = 1f, GrassDistance = 150f, LampLights = 78,
                        TreeNearRadius = 190f, TerrainLodScale = 1.3f, ChunkBuildsPerFrame = 3, EngineAudio = EngineAudioTier.Ultra,
                    };
            }
        }

        public static string Describe(QualityValues v) =>
            $"{v.Tier} (render {v.RenderScale:0.00}{(v.Stp ? " STP" : "")}, cap {(v.FpsCap > 0 ? v.FpsCap.ToString() : "none")}, " +
            $"shadows {v.ShadowCascades}/{v.ShadowResolution}/{v.ShadowDistance:0} m, engine audio {v.EngineAudio})";
    }
}
