using System.Collections.Generic;
using System.Linq;
using Backroads.Art.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Backroads.Tests.EditMode.Art
{
    /// <summary>
    /// Backroads.Tests.EditMode.Art.Art.* : card T2.9 (Art A1). Today's truck from the browser game, imported twice
    /// into Assets/Art/Vehicles/Truck/: truck.glb through glTFast, VH_Truck.fbx through art/pipeline/glb_to_fbx.py and
    /// BackroadsModelPostprocessor; and the fixture VH_LodFixture.fbx from art/pipeline/br_lod_fixture.py.
    /// </summary>
    [TestFixture, Category("Art")]
    public class Art
    {
        const string TruckDir = ArtBatch.TruckDir;
        const string Fixture = "Assets/Art/Vehicles/_Fixtures/VH_LodFixture.fbx";
        const float PositionTol = 1e-4f;   // m
        const float BoundsTol = 1e-3f;     // m (1 mm)
        const float AngleTol = 0.01f;      // degrees
        static readonly string[] TruckNodes = { "Body", "Body_LOD1", "Caliper", "Wheel", "Wheel_LOD1" };

        static GameObject Load(string path)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.NotNull(go, path + " is not imported");
            return go;
        }

        static Transform Find(GameObject root, string name) =>
            root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);

        static string Names(IEnumerable<Renderer> rs) => string.Join(",", rs.Select(r => r.name).OrderBy(n => n, System.StringComparer.Ordinal));

        /// <summary>
        /// Same node names, transforms within 1e-4 m, bounds within 1 mm, the same submeshes and triangles, and every
        /// FBX material slot remapped to the project material its GLB name maps to, also on a model's first import.
        /// </summary>
        [Test]
        public void TruckFbxMatchesGlb()
        {
            var fbxPath = ArtBatch.TruckFbx;
            var glbPath = ArtBatch.TruckGlb;
            var mi = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            Assert.NotNull(mi, fbxPath + " is not imported by the ModelImporter");
            Assert.IsTrue(BackroadsModelPostprocessor.IsVehicle(fbxPath), "the postprocessor's vehicle rules must cover " + fbxPath);
            Assert.IsFalse(mi.bakeAxisConversion, "bakeAxisConversion must be off");
            Assert.AreEqual(1f, mi.globalScale, "globalScale");
            Assert.IsTrue(mi.useFileScale, "useFileScale");
            Assert.IsTrue(mi.isReadable, "VH_ files are readable (runtime dents)");
            var gi = AssetImporter.GetAtPath(glbPath);
            Assert.NotNull(gi, glbPath + " is not imported");
            StringAssert.Contains("Gltf", gi.GetType().Name, "the GLB must come through glTFast");

            var glb = Load(glbPath);
            var fbx = Load(fbxPath);
            Assert.Less(Quaternion.Angle(Quaternion.identity, fbx.transform.localRotation), AngleTol, "FBX root rotation must be identity");
            Assert.Less((fbx.transform.localScale - Vector3.one).magnitude, PositionTol, "FBX root scale must be 1");

            var r = ModelCompare.Compare(glb, fbx);
            TestContext.WriteLine($"nodes {r.nodes.Length}; max position {r.maxPositionDelta:G3} m, rotation {r.maxRotationDeltaDeg:G3} deg, " +
                                  $"scale {r.maxScaleDelta:G3}, bounds {r.maxBoundsDelta * 1000f:G3} mm (whole truck {r.totalBoundsDelta * 1000f:G3} mm); " +
                                  $"GLB {r.glbMin:F4}..{r.glbMax:F4}, FBX {r.fbxMin:F4}..{r.fbxMax:F4}");
            Assert.IsEmpty(r.duplicateNames, "duplicate node names");
            CollectionAssert.AreEquivalent(TruckNodes, r.nodes.Select(n => n.name).ToArray(), "mesh node names");
            Assert.IsTrue(r.namesMatch, "node names differ: " + string.Join(", ", r.nodes.Where(n => !n.inGlb || !n.inFbx).Select(n => n.name)));
            foreach (var n in r.nodes)
            {
                Assert.Less(n.positionDelta, PositionTol, n.name + " position");
                Assert.Less(n.rotationDeltaDeg, AngleTol, n.name + " rotation");
                Assert.Less(n.scaleDelta, PositionTol, n.name + " scale");
                Assert.Less(n.boundsDelta, BoundsTol, $"{n.name} bounds: GLB {n.glbMin:F4}..{n.glbMax:F4}, FBX {n.fbxMin:F4}..{n.fbxMax:F4}");
                CollectionAssert.AreEqual(n.glbTris, n.fbxTris, n.name + " submeshes and triangles per submesh");
                Assert.IsTrue(n.slotsMatch, $"{n.name} material slots: GLB [{string.Join(",", n.glbMaterials)}] -> expected [{string.Join(",", n.expectedMaterials)}], FBX [{string.Join(",", n.fbxMaterials)}]");
            }
            Assert.Less(r.totalBoundsDelta, BoundsTol, "whole-truck bounds");

            // The remap must hold on a model's first import, not only after a reimport (07 section 12).
            var dir = "Assets/Art/Vehicles/_FirstImport";
            try
            {
                var path = RemapProbe.ImportFreshCopy(fbxPath, dir, "VH_FirstImport.fbx");
                var m = RemapProbe.MeasureSlots(path);
                Assert.Greater(m.slots, 0, "fresh copy has no material slots");
                Assert.AreEqual(m.slots, m.remapped, "material slots not remapped on the first import:\n" + string.Join("\n", m.materials));
            }
            finally { AssetDatabase.DeleteAsset(dir); }
        }

        /// <summary>
        /// LOD Groups from _LOD names: same-suffix siblings share a level, a PART_ empty with LOD children gets its own
        /// group, today's "Body" + "Body_LOD1" naming makes Body level 0, UCX_ hulls become convex colliders with no
        /// renderer, and the transition heights follow 07 section 8.5.
        /// </summary>
        [Test]
        public void LodGroupFromNames()
        {
            Assert.IsTrue(LodFromNames.TryParse("Body_LOD0", out var b, out var l) && b == "Body" && l == 0);
            Assert.IsTrue(LodFromNames.TryParse("Glass_lod12", out b, out l) && b == "Glass" && l == 12);
            Assert.IsFalse(LodFromNames.TryParse("Body", out _, out _));
            Assert.IsFalse(LodFromNames.TryParse("Body_LOD", out _, out _));
            Assert.IsFalse(LodFromNames.TryParse("_LOD1", out _, out _));
            Assert.AreEqual(new[] { 0.30f, 0.12f, 0.04f, 0.01f }, Enumerable.Range(0, 4).Select(i => LodFromNames.HeightFor(i, 4)).ToArray());
            Assert.AreEqual(new[] { 0.30f, 0.01f }, Enumerable.Range(0, 2).Select(i => LodFromNames.HeightFor(i, 2)).ToArray());

            var fx = Load(Fixture);
            var groups = fx.GetComponentsInChildren<LODGroup>(true);
            Assert.AreEqual(2, groups.Length, "fixture LOD groups: " + string.Join(",", groups.Select(g => g.name)));

            var root = fx.GetComponent<LODGroup>();
            Assert.NotNull(root, "no LODGroup on the fixture root");
            var lods = root.GetLODs();
            Assert.AreEqual(3, lods.Length, "root levels");
            Assert.AreEqual("Crate_LOD0,Lid_LOD0", Names(lods[0].renderers));
            Assert.AreEqual("Crate_LOD1,Lid_LOD1", Names(lods[1].renderers));
            Assert.AreEqual("Crate_LOD2", Names(lods[2].renderers));
            Assert.AreEqual(new[] { 0.30f, 0.12f, 0.01f }, lods.Select(x => x.screenRelativeTransitionHeight).ToArray());
            Assert.AreEqual(LODFadeMode.CrossFade, root.fadeMode);
            Assert.Greater(root.size, 0.9f, "group size from the LOD0 meshes");

            var door = Find(fx, "PART_Door");
            Assert.NotNull(door, "PART_Door missing");
            var dg = door.GetComponent<LODGroup>();
            Assert.NotNull(dg, "PART_Door has no LODGroup of its own");
            var dl = dg.GetLODs();
            Assert.AreEqual(2, dl.Length);
            Assert.AreEqual("Door_LOD0", Names(dl[0].renderers));
            Assert.AreEqual("Door_LOD1", Names(dl[1].renderers));

            var ucx = Find(fx, "UCX_Crate_00");
            Assert.NotNull(ucx, "UCX_Crate_00 missing");
            var mc = ucx.GetComponent<MeshCollider>();
            Assert.NotNull(mc, "UCX_ node has no MeshCollider");
            Assert.IsTrue(mc.convex, "UCX_ collider must be convex");
            Assert.NotNull(mc.sharedMesh, "UCX_ collider has no mesh");
            Assert.IsNull(ucx.GetComponent<MeshRenderer>(), "UCX_ node must not render");
            Assert.IsNull(ucx.GetComponent<MeshFilter>(), "UCX_ node keeps no MeshFilter");
            Assert.IsFalse(groups.SelectMany(g => g.GetLODs()).SelectMany(x => x.renderers).Any(x => x != null && x.name.StartsWith("UCX_")), "UCX_ in a LOD level");

            // Identity remap for slot-named materials (phase B files): Crate -> M_Paint, Door -> M_Glass.
            Assert.AreEqual(VehicleMaterialSlots.PathOf("M_Paint"), AssetDatabase.GetAssetPath(Find(fx, "Crate_LOD0").GetComponent<Renderer>().sharedMaterial));
            Assert.AreEqual(VehicleMaterialSlots.PathOf("M_Glass"), AssetDatabase.GetAssetPath(Find(fx, "Door_LOD1").GetComponent<Renderer>().sharedMaterial));

            // Today's truck: Body and Wheel have no suffix; their _LOD1 siblings make them level 0. Caliper has no LOD1.
            var truck = Load(ArtBatch.TruckFbx);
            var tg = truck.GetComponentsInChildren<LODGroup>(true);
            Assert.AreEqual(1, tg.Length, "truck LOD groups");
            Assert.AreSame(truck, tg[0].gameObject, "the truck's LODGroup sits on the root");
            var tl = tg[0].GetLODs();
            Assert.AreEqual(2, tl.Length, "truck levels");
            Assert.AreEqual("Body,Wheel", Names(tl[0].renderers));
            Assert.AreEqual("Body_LOD1,Wheel_LOD1", Names(tl[1].renderers));
            Assert.AreEqual(new[] { 0.30f, 0.01f }, tl.Select(x => x.screenRelativeTransitionHeight).ToArray());
        }
    }
}
