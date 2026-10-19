using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Backroads.Client;
using Backroads.World;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m1.shots.canon (PLAN 6.1 M1 headline gate, T1.5): the 12 reference cameras of CanonData v1 at 10:00 and 21:00,
    /// rendered at 1600x900 into an off-screen target (the window size does not matter), saved as (id)_(hhmm).png. Each
    /// shot is compared with the browser game's shot of the same view (CanonData/v1/reference/shots, found above the
    /// player): metrics shot.(name).mad (mean absolute RGB difference, 0-1) and shot.(name).ssim (luminance SSIM at
    /// 400x225). The evidence lists each JS shot before its Unity shot, so the contact sheet pairs them side by side.
    /// The shots use the browser game's default quality ("Medium") and a frozen clock, as the JS export did.
    /// </summary>
    public sealed class ShotsCanonScenario : IAutotestScenario
    {
        public string Kind => "shots-canon";

        [Serializable]
        sealed class Params
        {
            public int width = 1600;
            public int height = 900;
            public int settleFrames = 4;
            public float loadTimeoutSeconds = 120f;
            public bool jsMediumQuality = true;
            /// <summary>Fail when fewer shots than this were written (24 = all).</summary>
            public int minShots = 24;
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            yield return WorldScenarioUtil.WaitForMillbrook(ctx, p.loadTimeoutSeconds);
            var world = MillbrookWorld.Current;
            if (world == null || !world.Ready) yield break;
            if (!ctx.Renders) { ctx.Fail("shots need a window (no -batchmode)"); yield break; }

            WorldScenarioUtil.Script(world, true);
            world.FreezeClock = true;
            world.Sky.WindTime = 0;
            if (p.jsMediumQuality) world.UseJsMediumQuality();
            var info = world.Canon.world;
            var cam = world.cam;
            string repo = WorldScenarioUtil.FindRepoRoot();
            if (repo == null) ctx.Note("CanonData not found above the player: no image differences (shots only)");

            var rt = new RenderTexture(p.width, p.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "shots", antiAliasing = 1 };
            rt.Create();
            var tex = new Texture2D(p.width, p.height, TextureFormat.RGBA32, false);
            float fov = cam.fieldOfView;
            int written = 0;
            double madSum = 0, ssimSum = 0;
            int compared = 0;
            try
            {
                foreach (var hour in info.shotHours)
                {
                    world.SetHour(hour);
                    for (int i = 0; i < info.cameras.Length; i++)
                    {
                        var c = info.cameras[i];
                        var pose = CanonTransforms.Camera(c);
                        cam.transform.SetPositionAndRotation(pose.position, pose.rotation);
                        cam.fieldOfView = info.cameraFov;
                        world.SettleTerrain();
                        world.SetHour(hour); // lamp lights follow the camera
                        for (int f = 0; f < p.settleFrames; f++) yield return null;
                        // this frame renders into the 1600x900 target; read it back at the end of the frame
                        cam.targetTexture = rt;
                        yield return new WaitForEndOfFrame();
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, p.width, p.height), 0, 0, false);
                        tex.Apply(false);
                        RenderTexture.active = prev;
                        cam.targetTexture = null;

                        string name = $"{c.id}_{hour:00}00";
                        string path = Path.Combine(ctx.OutDir, name + ".png");
                        File.WriteAllBytes(path, tex.EncodeToPNG());
                        written++;
                        string rel = hour == 10 ? c.shot10 : c.shot21;
                        string js = repo != null && rel != null ? Path.Combine(repo, "CanonData", "v1", rel.Replace('/', Path.DirectorySeparatorChar)) : null;
                        if (js != null && File.Exists(js))
                        {
                            ctx.Evidence(js);
                            if (Compare(js, tex, out double mad, out double ssim))
                            {
                                ctx.Metric($"shot.{name}.mad", Math.Round(mad, 5));
                                ctx.Metric($"shot.{name}.ssim", Math.Round(ssim, 5));
                                madSum += mad; ssimSum += ssim; compared++;
                            }
                        }
                        ctx.Evidence(path);
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
                WorldScenarioUtil.Script(world, false);
            }
            ctx.Metric("shots.written", written);
            if (compared > 0)
            {
                ctx.Metric("shots.mad.mean", Math.Round(madSum / compared, 5));
                ctx.Metric("shots.ssim.mean", Math.Round(ssimSum / compared, 5));
            }
            ctx.Note($"{written} shots at {p.width}x{p.height}, {compared} compared with the JS shots (mean MAD {(compared > 0 ? madSum / compared : 0):F4}, mean SSIM {(compared > 0 ? ssimSum / compared : 0):F4}); the contact sheet pairs JS (left) with Unity (right)");
            if (written < p.minShots) ctx.Fail($"{written} shots written, expected {p.minShots}");
        }

        /// <summary>Mean absolute RGB difference (0-1) and luminance SSIM at a quarter of the resolution.</summary>
        static bool Compare(string jsPath, Texture2D unity, out double mad, out double ssim)
        {
            mad = ssim = 0;
            var js = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!js.LoadImage(File.ReadAllBytes(jsPath)) || js.width != unity.width || js.height != unity.height) return false;
                var a = js.GetPixels32();
                var b = unity.GetPixels32();
                long sum = 0;
                for (int i = 0; i < a.Length; i++)
                    sum += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b);
                mad = sum / (3.0 * 255.0 * a.Length);
                int w = js.width / 4, h = js.height / 4;
                var la = Luma(a, js.width, w, h);
                var lb = Luma(b, js.width, w, h);
                ssim = Ssim(la, lb, w, h);
                return true;
            }
            finally { UnityEngine.Object.Destroy(js); }
        }

        static double[] Luma(Color32[] px, int srcW, int w, int h)
        {
            var l = new double[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int dy = 0; dy < 4; dy++)
                        for (int dx = 0; dx < 4; dx++)
                        {
                            var c = px[(y * 4 + dy) * srcW + x * 4 + dx];
                            s += 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
                        }
                    l[y * w + x] = s / 16.0;
                }
            return l;
        }

        static double Ssim(double[] a, double[] b, int w, int h)
        {
            const int win = 8, stride = 4;
            const double c1 = (0.01 * 255) * (0.01 * 255), c2 = (0.03 * 255) * (0.03 * 255);
            double total = 0;
            int n = 0;
            for (int y = 0; y + win <= h; y += stride)
                for (int x = 0; x + win <= w; x += stride)
                {
                    double ma = 0, mb = 0;
                    for (int j = 0; j < win; j++)
                        for (int i = 0; i < win; i++) { ma += a[(y + j) * w + x + i]; mb += b[(y + j) * w + x + i]; }
                    ma /= win * win; mb /= win * win;
                    double va = 0, vb = 0, cov = 0;
                    for (int j = 0; j < win; j++)
                        for (int i = 0; i < win; i++)
                        {
                            double da = a[(y + j) * w + x + i] - ma, db = b[(y + j) * w + x + i] - mb;
                            va += da * da; vb += db * db; cov += da * db;
                        }
                    va /= win * win - 1; vb /= win * win - 1; cov /= win * win - 1;
                    total += ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                    n++;
                }
            return n > 0 ? total / n : 0;
        }
    }
}
