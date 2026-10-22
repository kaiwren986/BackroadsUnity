using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Backroads.Client;
using Backroads.Core;
using Backroads.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Backroads.Testing
{
    /// <summary>Shared helpers of lane W's autotests on the new Millbrook (T3.3, Millbrook.unity and TownWorld).</summary>
    static class TownScenarioUtil
    {
        public const string SceneName = "Millbrook";

        /// <summary>Loads Millbrook.unity unless it is open, then waits until TownWorld has built everything (or failed).</summary>
        public static IEnumerator Load(AutotestContext ctx, float timeoutSeconds)
        {
            if (SceneManager.GetActiveScene().name != SceneName)
            {
                if (!Application.CanStreamedLevelBeLoaded(SceneName)) { ctx.Fail("Millbrook.unity is not in this build"); yield break; }
                var op = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
                while (op != null && !op.isDone) yield return null;
            }
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
            {
                var w = TownWorld.Current;
                if (w != null && (w.Ready || w.Error != null)) break;
                yield return null;
            }
            var world = TownWorld.Current;
            if (world == null) ctx.Fail("no TownWorld in Millbrook.unity");
            else if (world.Error != null) ctx.Fail("Millbrook failed to load: " + world.Error);
            else if (!world.Ready) ctx.Fail($"Millbrook not ready after {timeoutSeconds:F0} s");
        }

        public static void Script(TownWorld w, bool scripted)
        {
            var fly = w.cam.GetComponent<FreeFlyCamera>();
            if (fly != null) fly.ScriptedControl = scripted;
            var dir = w.cam.GetComponent<CameraDirector>();
            if (dir != null) dir.Scripted = scripted;
        }

        public static void Loads(AutotestContext ctx, TownWorld w)
        {
            ctx.Metric("load.totalMs", Math.Round(w.TotalLoadMs, 1));
            ctx.Metric("load.dataMs", Math.Round(w.DataLoadMs, 1));
            ctx.Metric("chunks.buildAllMs", Math.Round(w.TerrainBuildAllMs, 1));
            ctx.Metric("load.roadsMs", Math.Round(w.RoadsMs, 1));
            ctx.Metric("load.buildingsMs", Math.Round(w.BuildingsMs, 1));
            ctx.Metric("load.vegetationMs", Math.Round(w.VegetationMs, 1));
            ctx.Metric("world.chunks", w.Terrain.ChunkCount);
            ctx.Metric("world.models", w.Models);
            ctx.Metric("world.greyBoxes", w.GreyBoxes);
            ctx.Metric("world.trees", w.Vegetation.Trees);
        }
    }

    /// <summary>
    /// m3.shots.millbrook (T3.3 done-when): the 12 fixed views of the new Millbrook (WorldData places.json cameras: the
    /// village street, the mill and dam, the bridge, the landing, the fuel station, the quarry hollow, the marsh, an
    /// overview, the garage, the farm, the depot, the river from the hill) at noon and at dusk, 1600x900 off screen,
    /// saved as (id)_(hhmm).png for the contact sheet. Fails when a view is missing or fewer shots were written.
    /// </summary>
    public sealed class ShotsMillbrookScenario : IAutotestScenario
    {
        public string Kind => "shots-millbrook";

        [Serializable]
        sealed class Params
        {
            public int width = 1600;
            public int height = 900;
            public int settleFrames = 6;
            public float loadTimeoutSeconds = 240f;
            public float[] hours = { 12f, 20f };
            public string[] views = { "street", "dam", "bridge", "landing", "fuel", "quarry", "marsh", "overview", "garage", "farm", "depot", "river" };
            public int minShots = 24;
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            yield return TownScenarioUtil.Load(ctx, p.loadTimeoutSeconds);
            var world = TownWorld.Current;
            if (world == null || !world.Ready) yield break;
            if (!ctx.Renders) { ctx.Fail("shots need a window (no -batchmode)"); yield break; }
            TownScenarioUtil.Loads(ctx, world);
            TownScenarioUtil.Script(world, true);
            world.FreezeClock = true;
            world.Sky.WindTime = 0;
            var cam = world.cam;
            var rt = new RenderTexture(p.width, p.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "shots", antiAliasing = 1 };
            rt.Create();
            var tex = new Texture2D(p.width, p.height, TextureFormat.RGBA32, false);
            float fov = cam.fieldOfView;
            int written = 0;
            try
            {
                foreach (var hour in p.hours)
                {
                    foreach (var id in p.views)
                    {
                        if (!world.GoTo(id)) { ctx.Fail("no view " + id + " in places.json"); continue; }
                        world.SettleTerrain();
                        world.SetHour(hour);
                        for (int f = 0; f < p.settleFrames; f++) yield return null;
                        cam.targetTexture = rt;
                        yield return new WaitForEndOfFrame();
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, p.width, p.height), 0, 0, false);
                        tex.Apply(false);
                        RenderTexture.active = prev;
                        cam.targetTexture = null;
                        int hh = (int)hour, mm = (int)Math.Round((hour - hh) * 60);
                        string path = Path.Combine(ctx.OutDir, $"{id}_{hh:00}{mm:00}.png");
                        File.WriteAllBytes(path, tex.EncodeToPNG());
                        ctx.Evidence(path);
                        written++;
                    }
                }
            }
            finally
            {
                cam.targetTexture = null;
                cam.fieldOfView = fov;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
                UnityEngine.Object.Destroy(tex);
                TownScenarioUtil.Script(world, false);
            }
            ctx.Metric("shots.written", written);
            ctx.Note($"{written} shots of the new Millbrook at {p.width}x{p.height} ({world.Models} models, {world.GreyBoxes} grey boxes, {world.Vegetation.Trees} trees; loaded in {world.TotalLoadMs:F0} ms)");
            if (written < p.minShots) ctx.Fail($"{written} shots written, expected {p.minShots}");
        }
    }

    /// <summary>
    /// m3.perf.flythrough (T3.3 done-when, report only until the M5 budget): a scripted flight through the 12 views of the
    /// new Millbrook and a low pass along the village street, at the run's quality tier (default High) and window size
    /// (2560x1440 from autotest.ps1, off screen when the display is smaller). Metrics as m1.perf.flythrough: frame-time
    /// percentiles of the flight, chunk builds, the load times.
    /// </summary>
    public sealed class PerfMillbrookScenario : IAutotestScenario
    {
        public string Kind => "perf-flythrough-millbrook";

        [Serializable]
        sealed class Params
        {
            public float legSeconds = 4f;
            public float warmupSeconds = 2f;
            public float loadTimeoutSeconds = 240f;
            public float p95GateMs = 16.6f;
            public bool failOnGate = false;
            public int renderWidth = 2560;
            public int renderHeight = 1440;
            public string[] views = { "overview", "street", "garage", "dam", "bridge", "landing", "fuel", "marsh", "farm", "river", "depot", "quarry" };
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var rec = UnityEngine.Object.FindFirstObjectByType<MetricsRecorder>();
            if (rec != null) rec.Recording = false;
            yield return TownScenarioUtil.Load(ctx, p.loadTimeoutSeconds);
            var world = TownWorld.Current;
            if (world == null || !world.Ready) yield break;
            TownScenarioUtil.Loads(ctx, world);
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
            ctx.Note($"quality {QualityPreset.Describe(QualityPreset.Current)}, window {Screen.width}x{Screen.height}, rendered {rw}x{rh}, GPU {SystemInfo.graphicsDeviceName}");

            var poses = new List<(Vector3 pos, Quaternion rot)>();
            foreach (var id in p.views)
                if (world.TryBookmark(id, out var b)) poses.Add((b.position, Quaternion.LookRotation(b.target - b.position, Vector3.up)));
            if (world.TryBookmark("street", out var st))
            {
                // a low pass along the village street to end with (near LODs, buildings and trees up close)
                var fwd = st.target - st.position; fwd.y = 0;
                poses.Add((st.position, Quaternion.LookRotation(fwd.normalized, Vector3.up)));
                poses.Add((st.position + fwd.normalized * 120f + Vector3.up * 2f, Quaternion.LookRotation(fwd.normalized, Vector3.up)));
            }
            if (poses.Count < 2) { ctx.Fail("not enough views in places.json"); yield break; }
            TownScenarioUtil.Script(world, true);
            var cam = world.cam;
            if (rt != null) cam.targetTexture = rt;
            cam.transform.SetPositionAndRotation(poses[0].pos, poses[0].rot);
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < p.warmupSeconds) yield return null;
            var frames = new List<float>(8192);
            int builds0 = world.Terrain.TotalBuilds;
            if (rec != null) rec.Recording = true;
            for (int leg = 0; leg + 1 < poses.Count; leg++)
            {
                var a = poses[leg];
                var b = poses[leg + 1];
                float start = Time.realtimeSinceStartup;
                float arc = Mathf.Min(120f, Vector3.Distance(a.pos, b.pos) * 0.25f);
                while (true)
                {
                    float u = Mathf.Clamp01((Time.realtimeSinceStartup - start) / p.legSeconds);
                    float s = u * u * (3 - 2 * u);
                    var pos = Vector3.Lerp(a.pos, b.pos, s) + Vector3.up * (Mathf.Sin(s * Mathf.PI) * arc);
                    float ground = Ground.HeightAt(pos);
                    if (pos.y < ground + 2f) pos.y = ground + 2f;
                    cam.transform.SetPositionAndRotation(pos, Quaternion.Slerp(a.rot, b.rot, s));
                    yield return null;
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                    if (u >= 1f) break;
                }
            }
            if (rec != null) rec.Recording = false;
            TownScenarioUtil.Script(world, false);
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
            ctx.Note($"flythrough of the new Millbrook: {frames.Count} frames, p50 {P(50):F2} ms, p95 {P(95):F2} ms, p99 {P(99):F2} ms; " +
                     $"{world.Terrain.ChunkCount} chunks at load {world.TerrainBuildAllMs:F0} ms, loaded in {world.TotalLoadMs:F0} ms (report only until M5)");
            if (P(95) > p.p95GateMs)
            {
                var msg = $"p95 frame {P(95):F2} ms over {p.p95GateMs:F1} ms";
                if (p.failOnGate) ctx.Fail(msg); else ctx.Note(msg + " (reported; the budget is set at M5)");
            }
        }
    }
}
