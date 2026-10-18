using System;
using System.Collections;
using System.Diagnostics;
using Backroads.Client.Rendering;
using Backroads.Core;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Backroads.Client
{
    /// <summary>
    /// The Millbrook scene's composition root (T1.4; the scene itself is a thin bootstrap, PLAN 1.2): loads canon
    /// (CanonRuntime), then builds the terrain chunks, the GLB buildings, trees and rocks, the merged static props, the
    /// road ribbons, the water plane and the day/night lighting. LOD settings follow QualityPreset (-br-quality);
    /// UseJsMediumQuality() switches to the browser game's default "Medium" values for side-by-side shots.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class MillbrookWorld : MonoBehaviour
    {
        public MillbrookLook look;
        public Camera cam;
        public Light sun;
        public Volume volume;
        [Tooltip("Hour of day at start (the M1 contact sheet uses 10:00 and 21:00).")]
        public float startHour = 10f;
        [Tooltip("Reference camera to start at (0 = Millbrook, cameras.json); -1 keeps the scene camera.")]
        public int startBookmark = 0;

        public static MillbrookWorld Current { get; private set; }

        public bool Ready { get; private set; }
        public string Error { get; private set; }
        public TerrainChunkSystem Terrain { get; private set; }
        public BuildingSpawner Buildings { get; private set; }
        public VegetationRenderer Vegetation { get; private set; }
        public PropSpawner Props { get; private set; }
        public DayNightPresenter Sky { get; private set; }
        public CanonAsset Canon { get; private set; }

        /// <summary>Load timings in milliseconds (reported by the perf autotest).</summary>
        public double CanonLoadMs, TerrainBuildAllMs, BuildingsMs, VegetationMs, PropsMs, TotalLoadMs;
        /// <summary>When true the wind clock stays at 0, as in the JS shots (clock frozen).</summary>
        public bool FreezeClock;
        public float TreeNearRadius { get; private set; } = 140f;
        float _shadowDistanceBefore = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        void Awake() => Current = this;

        IEnumerator Start()
        {
            var total = Stopwatch.StartNew();
            Canon = CanonRuntime.Asset;
            if (Canon == null) { Fail(CanonRuntime.NotImportedMessage); yield break; }
            if (cam == null) cam = Camera.main;
            if (startBookmark >= 0 && startBookmark < Canon.world.cameras.Length)
            {
                var pose = CanonTransforms.Camera(Canon.world.cameras[startBookmark]);
                cam.transform.SetPositionAndRotation(pose.position, pose.rotation);
                cam.fieldOfView = Canon.world.cameraFov;
            }
            try
            {
                CanonLoadMs = CanonRuntime.Load();
            }
            catch (Exception e) { Fail("canon load failed: " + e.Message); yield break; }
            var grid = CanonRuntime.Grid;
            Shader.SetGlobalTexture("_BrDetail", Canon.detail);
            var q = QualityPreset.Current;
            TreeNearRadius = q.TreeNearRadius;
            // JS shadows cover a +-75 m box around the view's focus; URP's shadow distance is from the camera. Without
            // -br-quality (QualityApply leaves the project's 50 m) use the tier's distance; restored on destroy.
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                if (!QualityPreset.Requested)
                {
                    _shadowDistanceBefore = urp.shadowDistance;
                    urp.shadowDistance = Mathf.Max(urp.shadowDistance, q.ShadowDistance);
                }
                // QualityApply turns STP on for Low; if the player was built without STP's compute shaders (no pipeline
                // asset used STP at build time) every post-processed frame throws. Fall back to bilinear and say so.
                if (urp.upscalingFilter == UpscalingFilterSelection.STP && StpResourcesMissing())
                {
                    urp.upscalingFilter = UpscalingFilterSelection.Auto;
                    Log.Warn("STP upscaling was requested but its compute shaders are not in this build; Millbrook uses bilinear upscaling instead");
                }
            }

            var sw = Stopwatch.StartNew();
            Terrain = new TerrainChunkSystem(grid, look.terrain, transform) { LodScale = q.TerrainLodScale, BuildsPerFrame = q.ChunkBuildsPerFrame };
            TerrainBuildAllMs = Terrain.BuildAll(cam.transform.position);

            sw.Restart();
            Buildings = new BuildingSpawner(Canon, look, transform);
            BuildingsMs = sw.Elapsed.TotalMilliseconds;
            yield return null;

            sw.Restart();
            Vegetation = new VegetationRenderer(Canon, look, transform, TreeNearRadius);
            VegetationMs = sw.Elapsed.TotalMilliseconds;
            yield return null;

            sw.Restart();
            Props = new PropSpawner(Canon.staticProps, look, transform);
            RoadRibbons.Spawn(Canon.world, look.road, transform, out int roads);
            WaterSurface.Spawn(look.water, Canon.waterNormal, transform);
            PropsMs = sw.Elapsed.TotalMilliseconds;

            Sky = new DayNightPresenter(sun, look, Canon.placements, transform, volume) { LampLightCount = Mathf.Max(6, q.LampLights) };
            Sky.SetHour(startHour, cam.transform.position);
            TotalLoadMs = total.Elapsed.TotalMilliseconds;
            Log.Info($"Millbrook ready in {TotalLoadMs:F0} ms: canon {CanonLoadMs:F0} ms, terrain 256 chunks {TerrainBuildAllMs:F0} ms, " +
                     $"{Buildings.Buildings} buildings ({Buildings.Panes} panes, {Buildings.LitPanes} lit) {BuildingsMs:F0} ms, " +
                     $"{Vegetation.Trees} trees + {Vegetation.Rocks} rocks {VegetationMs:F0} ms, {Props.Chunks} prop chunks, {roads} paved roads; quality {q.Tier}");
            Ready = true;
        }

        /// <summary>True when STP's runtime resources (internal STP.RuntimeResources) are absent from this player.</summary>
        static bool StpResourcesMissing()
        {
            try
            {
                var t = typeof(STP).GetNestedType("RuntimeResources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (t == null) return false;
                var m = typeof(GraphicsSettings).GetMethod("TryGetRenderPipelineSettings")?.MakeGenericMethod(t);
                if (m == null) return false;
                var args = new object[] { null };
                if (!(bool)m.Invoke(null, args) || args[0] == null) return true;
                return t.GetProperty("setupCS")?.GetValue(args[0]) == null;
            }
            catch (Exception) { return false; }
        }

        void Fail(string why)
        {
            Error = why;
            Log.Error("Millbrook: " + why);
        }

        void LateUpdate()
        {
            if (!Ready || cam == null) return;
            var p = cam.transform.position;
            Terrain.Update(p);
            Sky.Tick(Time.deltaTime, p, !FreezeClock);
        }

        /// <summary>Sets the hour of day (0-24).</summary>
        public void SetHour(float hour)
        {
            if (Sky != null) Sky.SetHour(hour, cam != null ? cam.transform.position : Vector3.zero);
        }

        /// <summary>
        /// The browser game's default ("Medium", main.js quality 1) LOD values, which the reference shots used: terrain
        /// lodScale 1, trees near radius 140 m, the six nearest lamp lights.
        /// </summary>
        public void UseJsMediumQuality()
        {
            if (Terrain != null) { Terrain.LodScale = 1f; Terrain.BuildsPerFrame = 2; }
            SetTreeNearRadius(140f);
            if (Sky != null) Sky.LampLightCount = 6;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                if (_shadowDistanceBefore < 0) _shadowDistanceBefore = urp.shadowDistance;
                urp.shadowDistance = 250f; // the Mid tier; JS Medium covers +-75 m around a focus ~100 m away
            }
        }

        public void SetTreeNearRadius(float radius)
        {
            TreeNearRadius = radius;
            if (Vegetation == null) return;
            float bias = Mathf.Max(0.01f, QualitySettings.lodBias);
            float near = 1f / (2f * radius * Mathf.Tan(30f * Mathf.Deg2Rad)) * bias;
            foreach (var lg in Vegetation.Root.GetComponentsInChildren<LODGroup>())
            {
                var lods = lg.GetLODs();
                if (lods.Length < 2) continue;
                lods[0].screenRelativeTransitionHeight = near;
                lg.SetLODs(lods);
            }
        }

        /// <summary>
        /// Test and diagnostics hook: overrides the URP shadow distance, shadow resolution and render scale (negative
        /// values keep the current ones; the shadow distance is restored on destroy) and describes the result.
        /// </summary>
        public string OverrideRendering(float shadowDistance, int shadowResolution, float renderScale)
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)) return "not URP";
            if (shadowDistance >= 0)
            {
                if (_shadowDistanceBefore < 0) _shadowDistanceBefore = urp.shadowDistance;
                urp.shadowDistance = shadowDistance;
            }
            if (shadowResolution > 0) urp.mainLightShadowmapResolution = shadowResolution;
            if (renderScale > 0) urp.renderScale = renderScale;
            return $"shadows {urp.shadowCascadeCount} cascades, {urp.mainLightShadowmapResolution} px, {urp.shadowDistance:0} m; " +
                   $"render scale {urp.renderScale:0.00}; upscaler {urp.upscalingFilter}";
        }

        /// <summary>Builds every terrain chunk the camera needs right now (after a teleport); returns milliseconds.</summary>
        public double SettleTerrain() => Terrain != null && cam != null ? Terrain.BuildAll(cam.transform.position) : 0;

        void OnDestroy()
        {
            if (Current == this) Current = null;
            if (_shadowDistanceBefore >= 0 && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp) urp.shadowDistance = _shadowDistanceBefore;
            Terrain?.Dispose();
            Buildings?.Destroy();
            Vegetation?.Destroy();
            Props?.Destroy();
            CanonRuntime.Unload();
        }
    }
}
