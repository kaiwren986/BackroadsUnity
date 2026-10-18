using Backroads.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Backroads.Game
{
    /// <summary>
    /// Applies QualityPreset to Unity and URP at start-up when -br-quality was given (players only: in the editor it
    /// would rewrite the project's URP asset). Low uses the "Mobile" quality level, Mid and High use "PC"; the URP
    /// asset then gets the tier's render scale, upscaler and shadows. -br-fps-cap, applied after, still wins.
    /// </summary>
    public static class QualityApply
    {
        public static void AtBoot()
        {
            if (!QualityPreset.Requested || Application.isEditor) return;
            var v = QualityPreset.Current;
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Log.Info("QUALITY " + QualityPreset.Describe(v) + " (no rendering: only the values are used)");
                return;
            }

            int level = FindLevel(v.Tier == QualityTier.Low ? "Mobile" : "PC");
            if (level >= 0) QualitySettings.SetQualityLevel(level, true);

            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = v.RenderScale;
                urp.upscalingFilter = v.Stp ? UpscalingFilterSelection.STP : UpscalingFilterSelection.Auto;
                urp.shadowCascadeCount = v.ShadowCascades;
                urp.mainLightShadowmapResolution = v.ShadowResolution;
                urp.shadowDistance = v.ShadowDistance;
            }
            else Log.Warn("QUALITY: the active render pipeline is not URP; only the frame cap applies");

            if (v.FpsCap > 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = v.FpsCap; }
            Log.Info("QUALITY " + QualityPreset.Describe(v) + $" level {QualitySettings.names[QualitySettings.GetQualityLevel()]}");
        }

        static int FindLevel(string name)
        {
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
                if (names[i] == name) return i;
            return -1;
        }
    }
}
