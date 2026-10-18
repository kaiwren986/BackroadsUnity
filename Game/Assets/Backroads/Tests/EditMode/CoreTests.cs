using Backroads.Core;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.EditMode
{
    /// <summary>Backroads.Tests.EditMode.Core.*</summary>
    [TestFixture]
    public class Core
    {
        const float Eps = 1e-5f;

        static void Near(Vector3 expected, Vector3 actual, string what) =>
            Assert.Less((expected - actual).magnitude, Eps, $"{what}: expected {expected:F5}, got {actual:F5}");

        /// <summary>03 section 2: JS +x maps to Unity -x; JS yaw +pi/2 maps to -90 degrees; rotations, quaternions and matrices agree.</summary>
        [Test]
        public void CoordsCanary()
        {
            Near(new Vector3(-1, 0, 0), BrCoords.Pos(1, 0, 0), "JS +x");
            Near(new Vector3(0, 1, 0), BrCoords.Pos(0, 1, 0), "JS +y");
            Near(new Vector3(0, 0, 1), BrCoords.Pos(0, 0, 1), "JS +z");
            Assert.AreEqual(-90f, BrCoords.YawDeg(Mathf.PI / 2f), 1e-4f, "JS yaw +pi/2");
            Assert.AreEqual(180f, BrCoords.YawDeg(-Mathf.PI), 1e-4f, "JS yaw -pi");

            // three.js rotation.y = +pi/2 turns the model's front (+z) to JS +x (its left). The same turn in Unity
            // must point the front at Unity -x (also the left), i.e. Pos(JS-rotated forward) == Unity-rotated forward.
            for (float yaw = -3f; yaw <= 3f; yaw += 0.75f)
            {
                var jsFwd = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
                var unityFwd = Quaternion.Euler(0, BrCoords.YawDeg(yaw), 0) * Vector3.forward;
                Near(BrCoords.Pos(jsFwd.x, jsFwd.y, jsFwd.z), unityFwd, $"yaw {yaw}");

                // three.js quaternion for rotation.y = yaw: (0, sin(yaw/2), 0, cos(yaw/2)).
                var q = BrCoords.Rot(0, Mathf.Sin(yaw / 2), 0, Mathf.Cos(yaw / 2));
                Near(unityFwd, q * Vector3.forward, $"quaternion yaw {yaw}");
            }

            // A general rotation: Rot(q) applied to Pos(v) equals Pos(q_js applied to v).
            var qa = new Quaternion(0.1f, 0.7f, -0.3f, 0.64f).normalized; // a JS-space quaternion, written as raw numbers
            var v = new Vector3(0.3f, -1.2f, 2.5f);
            var jsRotated = RotateRightHanded(qa, v);
            var uq = BrCoords.Rot(qa.x, qa.y, qa.z, qa.w);
            Near(BrCoords.Pos(jsRotated.x, jsRotated.y, jsRotated.z), uq * BrCoords.Pos(v.x, v.y, v.z), "general quaternion");

            // three.js Matrix4.elements (column-major): translation (3, 4, 5) and the same general rotation.
            var m = Matrix4x4.TRS(Vector3.zero, qa, Vector3.one); // rotation math is handedness-free on raw numbers
            var e = new float[16];
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++) e[c * 4 + r] = m[r, c];
            e[12] = 3; e[13] = 4; e[14] = 5;
            var um = BrCoords.Mat(e);
            Near(BrCoords.Pos(3, 4, 5), um.MultiplyPoint3x4(Vector3.zero), "matrix translation");
            Near(BrCoords.Pos(3 + jsRotated.x, 4 + jsRotated.y, 5 + jsRotated.z), um.MultiplyPoint3x4(BrCoords.Pos(v.x, v.y, v.z)), "matrix rotation");
        }

        /// <summary>q * v * q^-1 on raw numbers: the same formula three.js uses (it does not depend on handedness).</summary>
        static Vector3 RotateRightHanded(Quaternion q, Vector3 v) => q * v;

        /// <summary>PLAN §4.3 and §3.8: -br-quality parses, each tier carries its table, and the default is High.</summary>
        [Test]
        public void QualityPresetTiers()
        {
            try
            {
                CommandLine.Override(new[] { "x" });
                QualityPreset.Override(null);
                Assert.IsFalse(QualityPreset.Requested, "no switch: not requested");
                Assert.AreEqual(QualityTier.High, QualityPreset.Tier, "default tier");

                CommandLine.Override(new[] { "x", QualityPreset.Flag, "Medium" });
                QualityPreset.Override(null);
                Assert.IsTrue(QualityPreset.Requested);
                Assert.AreEqual(QualityTier.Mid, QualityPreset.Tier, "Medium = Mid");

                CommandLine.Override(new[] { "x", QualityPreset.Flag, "ultra" });
                QualityPreset.Override(null);
                Assert.AreEqual(QualityTier.High, QualityPreset.Tier, "Ultra renders as High");
                Assert.AreEqual(EngineAudioTier.Ultra, QualityPreset.Current.EngineAudio);

                var low = QualityPreset.For(QualityTier.Low);
                Assert.AreEqual(0.7f, low.RenderScale, 1e-6f);
                Assert.IsTrue(low.Stp);
                Assert.AreEqual(30, low.FpsCap);
                Assert.AreEqual(120f, low.ShadowDistance);
                Assert.AreEqual(EngineAudioTier.Low, low.EngineAudio);
                Assert.AreEqual(EngineAudioTier.High, QualityPreset.For(QualityTier.Mid).EngineAudio);
                var high = QualityPreset.For(QualityTier.High);
                Assert.AreEqual(4096, high.ShadowResolution);
                Assert.AreEqual(190f, high.TreeNearRadius);
                Assert.AreEqual(78, high.LampLights);
            }
            finally
            {
                CommandLine.Override(null);
                QualityPreset.Override(null);
            }
        }
    }
}
