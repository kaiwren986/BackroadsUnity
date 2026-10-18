using System;
using System.Collections;
using System.Collections.Generic;
using Backroads.Client;
using Backroads.Core;
using Backroads.World;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m1.perf.flythrough (PLAN 6.1 M1 lane gate, T1.5): a scripted flight through the 12 reference views of Millbrook
    /// (smooth legs between them, then one low pass along the main street), at the run's quality tier (-br-quality,
    /// default High) and window size (autotest.ps1: 2560x1440). Metrics: frame-time percentiles of the flight only
    /// (flythrough.p50/p95/p99/maxMs; the runner's own p95 also covers only the flight), the load-time build of all 256
    /// terrain chunks (chunks.buildAllMs, gate 1500), canon and world load times, chunk builds during the flight.
    /// </summary>
    public sealed class PerfFlythroughScenario : IAutotestScenario
    {
        public string Kind => "perf-flythrough";

        [Serializable]
        sealed class Params
        {
            public float legSeconds = 3.5f;
            public float warmupSeconds = 2f;
            public float loadTimeoutSeconds = 120f;
            public float p95GateMs = 16.6f;
            public float chunkGateMs = 1500f;
            public bool failOnGate = false;
            /// <summary>The gate's resolution. When the window is smaller (laptop displays), the camera renders off screen at this size.</summary>
            public int renderWidth = 2560;
            public int renderHeight = 1440;
            // diagnostics (negative = leave as the tier sets it): URP shadow distance and resolution, render scale,
            // tree near radius, the finest terrain LOD that casts shadows
            public float shadowDistance = -1;
            public int shadowResolution = -1;
            public float renderScale = -1;
            public float treeNearRadius = -1;
            public int terrainMaxShadowLod = -1;
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var rec = UnityEngine.Object.FindFirstObjectByType<MetricsRecorder>();
            if (rec != null) rec.Recording = false;
            yield return WorldScenarioUtil.WaitForMillbrook(ctx, p.loadTimeoutSeconds);
            var world = MillbrookWorld.Current;
            if (world == null || !world.Ready) yield break;

            ctx.Metric("chunks.buildAllMs", Math.Round(world.TerrainBuildAllMs, 1));
            ctx.Metric("load.canonMs", Math.Round(world.CanonLoadMs, 1));
            ctx.Metric("load.buildingsMs", Math.Round(world.BuildingsMs, 1));
            ctx.Metric("load.vegetationMs", Math.Round(world.VegetationMs, 1));
            ctx.Metric("load.totalMs", Math.Round(world.TotalLoadMs, 1));
            // the size autotest.ps1 asked for (-screen-width/-height: 2560x1440, or 1920x1080 for -MinSpec), else the params
            int tw = CommandLine.GetInt("-screen-width", p.renderWidth), th = CommandLine.GetInt("-screen-height", p.renderHeight);
            RenderTexture rt = null;
            int rw = Screen.width, rh = Screen.height;
            if (tw > 0 && th > 0 && (Screen.width < tw || Screen.height < th))
            {
                rt = new RenderTexture(tw, th, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "flythrough" };
                rt.Create();
                rw = tw; rh = th;
            }
            ctx.Metric("render.width", rw);
            ctx.Metric("render.height", rh);
            ctx.Metric("window.width", Screen.width);
            ctx.Metric("window.height", Screen.height);
            ctx.Note($"quality {QualityPreset.Describe(QualityPreset.Current)}{(QualityPreset.Requested ? "" : " (no -br-quality: project default)")}, " +
                     $"window {Screen.width}x{Screen.height}, rendered {rw}x{rh}{(rt != null ? " off screen (the display is smaller than the gate resolution)" : "")}, GPU {SystemInfo.graphicsDeviceName}");

            ctx.Note("URP: " + world.OverrideRendering(p.shadowDistance, p.shadowResolution, p.renderScale));
            if (p.treeNearRadius > 0) world.SetTreeNearRadius(p.treeNearRadius);
            if (p.terrainMaxShadowLod >= 0) world.Terrain.MaxShadowLod = p.terrainMaxShadowLod;
            ctx.Note($"trees near {world.TreeNearRadius:0} m, terrain shadows up to LOD {world.Terrain.MaxShadowLod}, lodScale {world.Terrain.LodScale}");

            WorldScenarioUtil.Script(world, true);
            var cams = world.Canon.world.cameras;
            var poses = new List<CanonTransforms.Pose>();
            foreach (var c in cams) poses.Add(CanonTransforms.Camera(c));
            // a low pass along Millbrook's main street to end with (near LODs, trees and buildings up close)
            var town = poses[0];
            var low0 = new CanonTransforms.Pose { position = new Vector3(40f, 22f, 10f), rotation = Quaternion.LookRotation(new Vector3(-1, -0.15f, 0.2f)) };
            var low1 = new CanonTransforms.Pose { position = new Vector3(110f, 18f, 50f), rotation = Quaternion.LookRotation(new Vector3(-1, -0.1f, 0.1f)) };
            poses.Add(low0);
            poses.Add(low1);
            poses.Add(town);

            var cam = world.cam;
            if (rt != null) cam.targetTexture = rt;
            cam.transform.SetPositionAndRotation(poses[0].position, poses[0].rotation);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < p.warmupSeconds) yield return null;

            var frames = new List<float>(4096);
            int builds0 = world.Terrain.TotalBuilds;
            if (rec != null) rec.Recording = true;
            for (int leg = 0; leg + 1 < poses.Count; leg++)
            {
                var a = poses[leg];
                var b = poses[leg + 1];
                float start = Time.realtimeSinceStartup;
                while (true)
                {
                    float u = Mathf.Clamp01((Time.realtimeSinceStartup - start) / p.legSeconds);
                    float s = u * u * (3 - 2 * u);
                    // arc up between views so the flight passes over the hills rather than through them
                    var pos = Vector3.Lerp(a.position, b.position, s) + Vector3.up * (Mathf.Sin(s * Mathf.PI) * 60f);
                    cam.transform.SetPositionAndRotation(pos, Quaternion.Slerp(a.rotation, b.rotation, s));
                    yield return null;
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                    if (u >= 1f) break;
                }
            }
            if (rec != null) rec.Recording = false;
            WorldScenarioUtil.Script(world, false);
            if (rt != null)
            {
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }

            frames.Sort();
            float P(float q) => frames.Count == 0 ? 0 : frames[Mathf.Clamp(Mathf.CeilToInt(q / 100f * frames.Count) - 1, 0, frames.Count - 1)];
            ctx.Metric("flythrough.frames", frames.Count);
            ctx.Metric("flythrough.p50Ms", Math.Round(P(50), 2));
            ctx.Metric("flythrough.p95Ms", Math.Round(P(95), 2));
            ctx.Metric("flythrough.p99Ms", Math.Round(P(99), 2));
            ctx.Metric("flythrough.maxMs", Math.Round(frames.Count > 0 ? frames[frames.Count - 1] : 0, 2));
            ctx.Metric("flythrough.chunkBuilds", world.Terrain.TotalBuilds - builds0);
            ctx.Note($"flythrough: {frames.Count} frames, p50 {P(50):F2} ms, p95 {P(95):F2} ms, p99 {P(99):F2} ms; all 256 chunks at load {world.TerrainBuildAllMs:F0} ms");
            bool p95Ok = P(95) <= p.p95GateMs, chunkOk = world.TerrainBuildAllMs <= p.chunkGateMs;
            ctx.Metric("gate.p95", p95Ok ? 1 : 0);
            ctx.Metric("gate.chunks", chunkOk ? 1 : 0);
            if (!chunkOk) ctx.Fail($"all chunks built in {world.TerrainBuildAllMs:F0} ms at load (gate {p.chunkGateMs} ms)");
            if (!p95Ok)
            {
                var msg = $"p95 frame {P(95):F2} ms over the {p.p95GateMs} ms gate";
                if (p.failOnGate) ctx.Fail(msg); else ctx.Note(msg + " (lane gate, reported)");
            }
        }
    }
}
