using System.Collections.Generic;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// T1.4: the 72 placed GLB buildings (placements.json, frozen buildings.glb through glTFast). Transforms come from
    /// each placement's three.js matrix (CanonTransforms.Building). Each recolourable slot (Wall, Roof, Door, Awning,
    /// Accent, Cladding) gets its own material instance per building with the canon colour (or the model's default);
    /// fixed slots share one material per glTF material. Glass panes pick the lit or dark window material from the
    /// placement's lit string, indexed in three.js traverse order (pane names resolved by Render.PaneNamesResolve).
    /// Materials follow props.js: buildings roughness 0.82 / metalness 0.04, Metal 0.45 / 0.65, Bulb and Sign emissive.
    /// </summary>
    public sealed class BuildingSpawner
    {
        public static readonly string[] Recolourable = { "Wall", "Roof", "Door", "Awning", "Accent", "Cladding" };

        public Transform Root { get; }
        public int Buildings { get; private set; }
        public int Panes { get; private set; }
        public int LitPanes { get; private set; }
        public int MaterialInstances { get; private set; }
        public readonly List<Material> Created = new List<Material>();

        readonly Dictionary<string, Material> _fixed = new Dictionary<string, Material>();
        readonly MillbrookLook _look;
        readonly CanonModelSet _models;

        public BuildingSpawner(CanonAsset canon, MillbrookLook look, Transform parent)
        {
            _look = look;
            _models = canon.buildings;
            Root = new GameObject("Buildings").transform;
            Root.SetParent(parent, false);
            var placements = canon.placements.buildings;
            for (int b = 0; b < placements.Length; b++) Spawn(b, placements[b]);
        }

        static bool IsRecolourable(string m) => System.Array.IndexOf(Recolourable, m) >= 0;

        Material Fixed(string name)
        {
            if (_fixed.TryGetValue(name, out var m)) return m;
            switch (name)
            {
                case "Bulb": m = _look.bulb; break;
                case "Sign": m = _look.sign; break;
                case "Metal":
                    m = new Material(_look.buildingMetal) { name = "bld_Metal" };
                    m.SetColor("_BaseColor", BrColor.LinearToHex(_models.DefaultColor(name)));
                    Created.Add(m);
                    break;
                default:
                    m = new Material(_look.buildingPart) { name = "bld_" + name };
                    m.SetColor("_BaseColor", BrColor.LinearToHex(_models.DefaultColor(name)));
                    Created.Add(m);
                    break;
            }
            _fixed[name] = m;
            return m;
        }

        void Spawn(int index, PlacementSet.Building b)
        {
            var model = _models.Find(b.model);
            if (model == null) { Backroads.Core.Log.Warn("BuildingSpawner: no model " + b.model); return; }
            var go = new GameObject($"{b.model}_{index:00}");
            var t = go.transform;
            t.SetParent(Root, false);
            var pose = CanonTransforms.Building(b);
            t.localPosition = pose.position;
            t.localRotation = pose.rotation;
            t.localScale = pose.scale;

            var slots = new Dictionary<string, Material>();
            Material Slot(string name)
            {
                if (!IsRecolourable(name)) return Fixed(name);
                if (slots.TryGetValue(name, out var m)) return m;
                var col = b.TryGetColor(name, out var c32) ? BrColor.Of(c32) : BrColor.LinearToHex(_models.DefaultColor(name));
                m = new Material(_look.buildingPart) { name = $"{go.name}_{name}" };
                m.SetColor("_BaseColor", col);
                Created.Add(m);
                MaterialInstances++;
                slots[name] = m;
                return m;
            }

            int pane = 0;
            foreach (var part in model.parts)
            {
                var pgo = new GameObject(part.node);
                var pt = pgo.transform;
                pt.SetParent(t, false);
                pt.localPosition = part.localPosition;
                pt.localRotation = part.localRotation;
                pt.localScale = part.localScale;
                pgo.AddComponent<MeshFilter>().sharedMesh = part.mesh;
                var r = pgo.AddComponent<MeshRenderer>();
                var mats = new Material[part.materials.Length];
                if (part.isPane)
                {
                    bool lit = b.lit != null && pane < b.lit.Length && b.lit[pane] == '1';
                    for (int i = 0; i < mats.Length; i++) mats[i] = lit ? _look.glassLit : _look.glassDark;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    pane++;
                    Panes++;
                    if (lit) LitPanes++;
                }
                else
                {
                    for (int i = 0; i < mats.Length; i++) mats[i] = Slot(part.materials[i]);
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
                r.sharedMaterials = mats;
            }
            Buildings++;
        }

        public void Destroy()
        {
            foreach (var m in Created) TerrainChunkSystem.Kill(m);
            Created.Clear();
            if (Root != null) TerrainChunkSystem.Kill(Root.gameObject);
        }
    }
}
