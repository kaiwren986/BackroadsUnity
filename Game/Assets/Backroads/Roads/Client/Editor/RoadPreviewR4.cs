using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Roads.Client.Editor
{
    /// <summary>
    /// roads-R4 previews (batchmode with graphics, like Shots): the river bridge from the bank and along its deck, an
    /// alley mouth, a gravel road's paved apron and STOP, a kerb taper at the town edge (artifacts/roads/(town)/r4-*.png),
    /// and the demo roundabout on a synthetic map (artifacts/roads/roundabout-demo/*.png).
    ///   Unity.exe -batchmode -projectPath Game -executeMethod Backroads.Roads.Client.Editor.RoadPreview.ShotsR4 -quit
    /// </summary>
    public static partial class RoadPreview
    {
        public static void ShotsR4()
        {
            int code = 0;
            try
            {
                foreach (var town in TownRoadSource.Towns())
                    foreach (var p in RenderR4(town)) Debug.Log("[BR] wrote " + p);
                foreach (var p in RenderRoundabout()) Debug.Log("[BR] wrote " + p);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static void Lights()
        {
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 2.2f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(48, 210, 0);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.47f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.2f);
            RenderSettings.fog = false;
        }

        static Vector3 V(D3 p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
        static Vector3 V(D2 p, double y) => new Vector3((float)p.x, (float)y, (float)p.z);

        public static List<string> RenderR4(string town)
        {
            EnsureMaterials();
            var paths = new List<string>();
            var net = RoadNetworkBaker.LoadBakedTown(town);
            if (net == null) throw new InvalidOperationException("no baked road network for " + town);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var a = RoadNetworkBaker.BakeTownWithKerbs(town, out var bake, out var kerbs, out var frontage);
            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(frontage);
            var plates = RoadPlateBuilder.Build(bake.graph, bake.surfaces, kerbs.kerbs, bake.source.dem, kerbs.settings);
            var field = new PlateField(plates);
            Globals();
            Lights();
            RoadChunkView.SpawnTown(town, null);
            RoadPropPlacer.Place(net);

            var g = bake.graph;
            var views = new List<(string name, Vector3 target, Vector3 eye, float size)>();
            // the river bridge: from the bank beside its first pier, and along the deck from the west abutment
            var deck = kerbs.bridges?.OrderByDescending(d => d.length).FirstOrDefault();
            if (deck != null)
            {
                double sp = deck.piers.Count > 0 ? deck.piers[0] : 0.5 * deck.length;
                var c = deck.frame.P(sp, 0);
                double y = deck.frame.Y(sp);
                var side = deck.frame.Dir(sp).Left;
                views.Add(("bridge-side", V(c, y - 3), V(c + side * 70 + deck.frame.Dir(sp) * -25, y - 2), 320));
                bool westA = deck.frame.P(0, 0).x < deck.frame.P(deck.length, 0).x;
                double s0 = westA ? 0 : deck.length;
                var dirIn = westA ? deck.frame.Dir(0) : deck.frame.Dir(deck.length) * -1;
                var e0 = deck.frame.P(s0, 0);
                views.Add(("bridge-deck", V(e0 + dirIn * 40, deck.frame.Y(s0) + 1), V(e0 + dirIn * -18 + dirIn.Left * 3, deck.frame.Y(s0) + 4), 200));
                var ab = deck.frame.P(westA ? deck.sA : deck.length - deck.sB, 0);
                views.Add(("bridge-abutment", V(ab, deck.frame.Y(westA ? deck.sA : deck.length - deck.sB) - 4), V(ab + side * 28 + dirIn * 22, deck.frame.Y(s0) - 2), 200));
            }
            // an alley mouth
            var alley = kerbs.kerbs.corners.FirstOrDefault(c => c.ramp == RampKind.AlleyMouth);
            if (alley != null)
            {
                var t = V(g.nodes[alley.node].p);
                views.Add(("alley-mouth", t, t + new Vector3(12, 7, -14), 140));
            }
            // a gravel road's apron and STOP at a paved road
            foreach (var r in bake.surfaces.ribbons)
            {
                if (r.apronA <= 0 && r.apronB <= 0) continue;
                var e = g.edges[r.edge];
                var f = new RoadFrame(e.pts);
                bool atA = r.apronA > 0;
                double sIn = atA ? r.s0 + 0.5 * r.apronA : r.s1 - 0.5 * r.apronB, sOut = atA ? r.s0 + r.apronA + 14 : r.s1 - r.apronB - 14;
                var t = V(f.P(sIn, 0), f.Y(sIn));
                views.Add(("gravel-apron", t, V(f.P(sOut, 0), f.Y(sOut) + 4), 140));
                break;
            }
            // a kerb taper where a kerbed street meets an unkerbed road
            var taper = kerbs.kerbs.faces.FirstOrDefault(fc => fc.kerbed && (fc.taperEnd || fc.taperStart) && g.nodes.Any(n => n.kind == NodeKind.ClassChange && D2.Distance(n.p.XZ, fc.taperEnd ? fc.kerb[fc.Count - 1] : fc.kerb[0]) < 20));
            if (taper != null)
            {
                var p = taper.taperEnd ? taper.kerb[taper.Count - 1] : taper.kerb[0];
                double y = taper.taperEnd ? taper.gutter[taper.Count - 1] : taper.gutter[0];
                var t = V(p, y);
                views.Add(("kerb-taper", t, t + new Vector3(-9, 4, -10), 140));
            }
            int k = 0;
            foreach (var (name, target, eye, size) in views)
            {
                var ground = Ground(bake.source.dem, field, target, size, 1.0f);
                paths.Add(Shot(RoadNetworkBaker.TownArtifactsDir(town), $"r4-{k++:D2}-{name}.png", target, eye));
                UnityEngine.Object.DestroyImmediate(ground);
            }
            return paths;
        }

        /// <summary>The demo roundabout (a synthetic ring with four uneven legs), meshed, signed and on its plates.</summary>
        public static List<string> RenderRoundabout()
        {
            EnsureMaterials();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Globals();
            Lights();
            var b = RoundaboutDemo.Run(RoundaboutDemo.Ring(13.25, new[] { 0.0, 95, 185, 275 }));
            var set = RoadChunkBaker.Build(b.meshes, b.src.playHalf, b.src.source, "", "", out var raw, out _);
            var mats = RoadChunkView.LoadMaterials();
            var root = new GameObject("Roundabout demo");
            foreach (var c in set.chunks)
            {
                RoadChunkSet.ReadChunk(raw, c, out var lod0, out var lod1, out var col, "demo");
                var go = new GameObject("chunk");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = c.centre;
                go.AddComponent<MeshFilter>().sharedMesh = lod0;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.Take(lod0.subMeshCount).ToArray();
            }
            UnityEngine.Object.DestroyImmediate(set);
            var asset = ScriptableObject.CreateInstance<RoadNetworkAsset>();
            asset.SetFrom(b.g, b.report, b.s, b.streets, b.props, RoadPropBuilder.Version);
            RoadPropPlacer.Place(asset);
            var field = new PlateField(b.plates);
            var ground = Ground(b.src.dem, field, new Vector3(0, 10, 0), 160, 0.5f);
            var dir = Path.Combine(RoadNetworkBaker.ArtifactsDir, "roundabout-demo");
            var paths = new List<string>
            {
                Shot(dir, "roundabout-above.png", new Vector3(0, 10, 0), new Vector3(0.1f, 85, -12)),
                Shot(dir, "roundabout-approach.png", new Vector3(0, 10.5f, 0), new Vector3(46, 11.6f, -2.2f)),
                Shot(dir, "roundabout-splitter.png", new Vector3(20, 10, 0), new Vector3(30, 14, -14)),
            };
            UnityEngine.Object.DestroyImmediate(ground);
            UnityEngine.Object.DestroyImmediate(asset);
            return paths;
        }
    }
}
