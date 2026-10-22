using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Backroads.Client;
using Backroads.Roads;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m3.shots.junctions (the M3 gate's road screenshots, 05 §8.8; lane R): fixed cameras on eight representative places of
    /// the new Millbrook's road network, picked from the baked network by JunctionShots (a downtown main-street junction,
    /// a residential X, an alley mouth, a gravel apron with its STOP, the river bridge's end, the town-edge kerb taper, a
    /// close junction pair, a rural T), at noon and at night, 1600x900 off screen, saved as (id)_(hhmm).png for the
    /// contact sheet, with junctions.txt listing each camera and why its place was chosen. Fails when the scene has no
    /// road chunks, when the network the scene's chunks were baked from is not in the build, when a place is missing, or
    /// when fewer shots were written.
    /// </summary>
    public sealed class ShotsJunctionsScenario : IAutotestScenario
    {
        public string Kind => "shots-junctions";

        [Serializable]
        sealed class Params
        {
            public int width = 1600;
            public int height = 900;
            public int settleFrames = 8;
            public float loadTimeoutSeconds = 240f;
            public float[] hours = { 12f, 22f };
            public float fov = 60f;
            /// <summary>The camera stays at least this far over the ground (a bank higher than the road beside it).</summary>
            public float minClearance = 1.5f;
            public int minShots = 16;
            /// <summary>The eight road places picked from the network (false: only the fixed views below).</summary>
            public bool places = true;
            /// <summary>Extra fixed views (world positions), e.g. a look at particular driveway mouths.</summary>
            public View[] views = new View[0];
        }

        [Serializable]
        sealed class View
        {
            public string id = "", what = "";
            public float[] pos = new float[3], target = new float[3];
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            yield return TownScenarioUtil.Load(ctx, p.loadTimeoutSeconds);
            var world = TownWorld.Current;
            if (world == null || !world.Ready) yield break;
            if (!ctx.Renders) { ctx.Fail("shots need a window (no -batchmode)"); yield break; }
            TownScenarioUtil.Loads(ctx, world);
            if (world.RoadsRoot == null) { ctx.Fail("the scene has no road chunks (TownWorld.RoadsRoot)"); yield break; }

            // the network the scene's road chunks were baked from (its content hash names it)
            var chunks = world.Data.roadChunks as RoadChunkSet;
            RoadNetworkAsset net = null;
            foreach (var a in Resources.LoadAll<RoadNetworkAsset>(""))
                if (chunks != null && a.contentHash == chunks.roadContentHash) net = a;
            if (net == null) { ctx.Fail("the road network of the scene's chunks (" + (chunks != null ? chunks.roadContentHash : "no chunks") + ") is not in the build"); yield break; }
            var missing = new List<string>();
            var shots = p.places ? JunctionShots.Pick(net, out missing) : new List<JunctionShots.Shot>();
            foreach (var m in missing) ctx.Fail("no place for the " + m + " shot in the road network");
            foreach (var v in p.views ?? new View[0])
                if (v.pos != null && v.pos.Length == 3 && v.target != null && v.target.Length == 3)
                    shots.Add(new JunctionShots.Shot { id = v.id, what = v.what, node = -1, arm = -1, position = new Vector3(v.pos[0], v.pos[1], v.pos[2]), target = new Vector3(v.target[0], v.target[1], v.target[2]), why = "a fixed view" });
            var list = new StringBuilder();
            list.AppendLine($"m3.shots.junctions: {shots.Count} places from road network {net.contentHash} ({net.source})");
            foreach (var s in shots) list.AppendLine(JunctionShots.Describe(s));
            string listPath = Path.Combine(ctx.OutDir, "junctions.txt");
            File.WriteAllText(listPath, list.ToString().Replace("\r", ""));
            ctx.Evidence(listPath);
            if (shots.Count == 0) yield break;

            TownScenarioUtil.Script(world, true);
            world.FreezeClock = true;
            world.Sky.WindTime = 0;
            var cam = world.cam;
            var fly = cam.GetComponent<FreeFlyCamera>();
            var rt = new RenderTexture(p.width, p.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "shots", antiAliasing = 1 };
            rt.Create();
            var tex = new Texture2D(p.width, p.height, TextureFormat.RGBA32, false);
            float fov = cam.fieldOfView;
            int written = 0, lifted = 0;
            try
            {
                foreach (var hour in p.hours)
                {
                    foreach (var s in shots)
                    {
                        var pos = world.transform.TransformPoint(s.position);
                        var target = world.transform.TransformPoint(s.target);
                        float ground = Ground.HeightAt(pos);
                        if (pos.y < ground + p.minClearance) { pos.y = ground + p.minClearance; lifted++; }
                        cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
                        cam.fieldOfView = p.fov;
                        if (fly != null) fly.SyncFromTransform();
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
                        string path = Path.Combine(ctx.OutDir, $"{s.id}_{hh:00}{mm:00}.png");
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
            ctx.Metric("shots.places", shots.Count);
            ctx.Metric("shots.liftedOverGround", lifted);
            ctx.Note($"{written} shots of {shots.Count} road places at {p.width}x{p.height} ({string.Join(", ", Ids(shots))}); network {net.contentHash.Substring(0, 12)}; {lifted} cameras lifted to clear the ground");
            if (written < p.minShots) ctx.Fail($"{written} shots written, expected {p.minShots}");
        }

        static IEnumerable<string> Ids(List<JunctionShots.Shot> shots)
        {
            foreach (var s in shots) yield return s.id + " (node " + s.node + ")";
        }
    }
}
