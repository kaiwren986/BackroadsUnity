using System;
using System.Collections;
using System.IO;
using Backroads.Client;
using Backroads.Roads;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m3.shots.sites (card sites-S5 step 4): fixed cameras over the town's sites (two houses, downtown front and rear, a
    /// retail lot, the fuel station and the other kinds the town has; the views and their framing come from lane S's bake,
    /// StreamingAssets/Autotest/m3.shots.sites.json) at noon and at dusk, 1600x900 off screen, saved as (id)_(hhmm).png
    /// for the contact sheet. The site meshes are lane S's chunk set (`BrSiteChunks_(town)`, lane R's chunk format and
    /// loader, drawn with lane R's road materials); when the scene has not spawned them (TownWorld before lane W wires them
    /// in), the scenario spawns them itself. Props come with the scene build (SitePropPlacer, editor only). Fails when the
    /// chunks are missing or fewer shots were written.
    /// </summary>
    public sealed class ShotsSitesScenario : IAutotestScenario
    {
        public string Kind => "shots-sites";
        const string SitesRoot = "Sites (S4 chunks)";

        [Serializable]
        sealed class View
        {
            public string id = "", name = "";
            public float[] pos = new float[3], target = new float[3];
            public float fov = 55f;
        }

        [Serializable]
        sealed class Params
        {
            public int width = 1600;
            public int height = 900;
            public int settleFrames = 6;
            public float loadTimeoutSeconds = 240f;
            public float[] hours = { 12f, 20f };
            public string chunks = "";
            public View[] views = new View[0];
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

            // the site meshes: the scene's, else spawned here from lane S's chunk set
            bool spawned = false;
            var sites = GameObject.Find(SitesRoot);
            if (sites == null)
            {
                var set = string.IsNullOrEmpty(p.chunks) ? null : Resources.Load<RoadChunkSet>(p.chunks);
                if (set == null) { ctx.Fail("no site chunks in Resources: " + p.chunks); yield break; }
                var mats = new Material[TownWorld.RoadMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = Resources.Load<Material>(TownWorld.RoadMaterials[i]);
                sites = RoadChunkLoader.Spawn(set, world.transform, mats, false);
                sites.name = SitesRoot;
                spawned = true;
            }
            ctx.Metric("sites.chunks", sites.transform.childCount);
            ctx.Metric("sites.spawnedHere", spawned ? 1 : 0);
            var props = GameObject.Find("Site props (S4)");
            ctx.Metric("sites.props", props != null ? props.transform.childCount : 0);

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
                    foreach (var v in p.views)
                    {
                        var pos = new Vector3(v.pos[0], v.pos[1], v.pos[2]);
                        var target = new Vector3(v.target[0], v.target[1], v.target[2]);
                        cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
                        if (v.fov > 1) cam.fieldOfView = v.fov;
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
                        string path = Path.Combine(ctx.OutDir, $"{v.id}_{hh:00}{mm:00}.png");
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
            ctx.Note($"{written} shots of {p.views.Length} sites at {p.width}x{p.height}; site chunks {(spawned ? "spawned by the scenario (the scene has none yet)" : "from the scene")}, " +
                     $"{(props != null ? props.transform.childCount + " props from the scene" : "no props in the scene yet")}");
            if (written < p.minShots) ctx.Fail($"{written} shots written, expected {p.minShots}");
        }
    }
}
