using System.Linq;
using Backroads.Art.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Backroads.Tests.EditMode
{
    /// <summary>Backroads.Tests.EditMode.Art.*</summary>
    [TestFixture]
    public class Art
    {
        public const string CanaryPath = "Assets/Art/Canary/CANARY.fbx";
        const float Eps = 1e-3f;

        /// <summary>
        /// The axis canary (07 section 3.2, PLAN T0.8): art/pipeline/br_canary.py exports MARK_Front at game (0,0,2),
        /// MARK_Left at (1,0,0) and MARK_Up at (0,1,0) with the vehicle FBX flags; after import MARK_Front.z > 0,
        /// MARK_Left.x < 0 (Unity +X is right), MARK_Up.y > 0, root rotation identity and scale 1.
        /// </summary>
        [Test]
        public void Canary()
        {
            var mi = AssetImporter.GetAtPath(CanaryPath) as ModelImporter;
            Assert.NotNull(mi, CanaryPath + " is not imported (run art/pipeline/br_canary.py and copy the FBX)");
            Assert.IsTrue(BackroadsModelPostprocessor.Applies(CanaryPath), "BackroadsModelPostprocessor does not cover the canary");
            Assert.IsFalse(mi.bakeAxisConversion, "bakeAxisConversion must be off: Blender bakes the space transform");
            Assert.AreEqual(1f, mi.globalScale, "globalScale");
            Assert.IsTrue(mi.useFileScale, "useFileScale");

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(CanaryPath);
            Assert.NotNull(root);
            Assert.Less(Quaternion.Angle(Quaternion.identity, root.transform.localRotation), 0.01f, "root rotation must be identity");
            Assert.Less((root.transform.localScale - Vector3.one).magnitude, Eps, "root scale must be 1");

            Transform Find(string n) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
            var front = Find("MARK_Front");
            var left = Find("MARK_Left");
            var up = Find("MARK_Up");
            Assert.NotNull(front, "MARK_Front missing"); Assert.NotNull(left, "MARK_Left missing"); Assert.NotNull(up, "MARK_Up missing");

            TestContext.WriteLine($"fileScale {mi.fileScale}; Front {front.position:F4} Left {left.position:F4} Up {up.position:F4}");
            Assert.Greater(front.position.z, 0f, "MARK_Front.z > 0");
            Assert.Less(left.position.x, 0f, "MARK_Left.x < 0 (Unity +X is right)");
            Assert.Greater(up.position.y, 0f, "MARK_Up.y > 0");

            // Exact positions: Unity = (-gx, gy, gz), metres, no extra scale.
            Assert.Less((front.position - new Vector3(0, 0, 2)).magnitude, Eps, "MARK_Front at Unity (0,0,2)");
            Assert.Less((left.position - new Vector3(-1, 0, 0)).magnitude, Eps, "MARK_Left at Unity (-1,0,0)");
            Assert.Less((up.position - new Vector3(0, 1, 0)).magnitude, Eps, "MARK_Up at Unity (0,1,0)");

            foreach (var t in new[] { front, left, up })
            {
                Assert.Less(Quaternion.Angle(Quaternion.identity, t.rotation), 0.01f, t.name + " rotation must be identity");
                Assert.Less((t.lossyScale - Vector3.one).magnitude, Eps, t.name + " scale must be 1");
                var mf = t.GetComponent<MeshFilter>();
                Assert.NotNull(mf, t.name + " has no mesh");
                Assert.AreEqual(0.10f, mf.sharedMesh.bounds.size.x, 0.002f, t.name + " is a 10 cm cube");
            }
        }
    }
}
