using System.Linq;
using Backroads.Core;
using Backroads.EngineSim;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.EngineSim
{
    /// <summary>Backroads.Tests.EditMode.EngineSim.VehicleEngine.* (lane V, M2, card T2.4): the rpm map and the roster.</summary>
    [TestFixture]
    public class VehicleEngine
    {
        /// <summary>PLAN 3.5: idle onto idle, redline onto 97 % of the engine's redline, linear between; proportional below idle.</summary>
        [Test]
        public void RpmMapFollowsPlan35()
        {
            // the truck (vehicle.js: idle 650, redline 2700) onto v8_bigblock_a (idle 918, redline 5500)
            var m = new RpmMap(650, 2700, 918, 5500, 7000);
            Assert.AreEqual(918f, m.Map(650), 1e-3f, "idle");
            Assert.AreEqual(0.97f * 5500, m.Map(2700), 1e-2f, "redline");
            float mid = 650 + 0.5f * (2700 - 650);
            Assert.AreEqual(918 + 0.5f * (0.97f * 5500 - 918), m.Map(mid), 1e-2f, "halfway");
            Assert.AreEqual((0.97f * 5500 - 918) / (2700 - 650), m.Slope, 1e-5f, "slope");
            Assert.AreEqual(0f, m.Map(0), "0 stays 0");
            Assert.AreEqual(0f, m.Map(-5), "no negative rpm");
            Assert.AreEqual(0.5f * 918, m.Map(325), 1e-3f, "below idle: proportional");
            Assert.LessOrEqual(m.Map(1e6f), 7000f, "never past the rev limit");
            // monotonic over the whole range
            float last = -1;
            for (float r = 0; r <= 3000; r += 5) { float v = m.Map(r); Assert.GreaterOrEqual(v, last, $"monotonic at {r}"); last = v; }
        }

        /// <summary>U6 and PLAN 3.10: the truck's gas big-block V8 is in the roster under its fictional id, beside the other three.</summary>
        [Test]
        public void RosterHasTheVehicleEngines()
        {
            foreach (var id in new[] { "v8_bigblock_a", "v8_cross_a", "f4_boxer_a", "i4_synth" })
            {
                var d = EngineCatalog.Roster.FirstOrDefault(r => r.id == id);
                Assert.IsNotNull(d, id + " missing from the roster");
                Assert.IsTrue(d.roster && !d.devOnly, id + " must ship");
                Assert.Greater(d.IdleRpm, 0, id + " idle");
                Assert.That(d.IdleRpm, Is.InRange(d.idleRpmMin, d.idleRpmMax), id + " measured idle outside its declared range");
                Assert.Greater(d.redlineRpm, d.IdleRpm * 2, id + " redline");
            }
            var v8 = EngineCatalog.Find("v8_bigblock_a");
            Assert.AreEqual(8, v8.cylinders);
            Assert.AreEqual("v8_bigblock_a", v8.builtinName);
            foreach (var v in FollowRamp.Roster)
                Assert.IsNotNull(EngineCatalog.Find(v.EngineId), $"{v.Name}: {v.EngineId} not in the catalog");
        }

        /// <summary>Client builds register the engine-sim factory with VehicleEngines; an unknown id gives null, never an exception.</summary>
        [Test]
        public void UnknownEngineIsNull()
        {
            Assert.IsNull(EngineSimVehicleEngine.Create("no_such_engine", EngineAudioTier.High));
        }
    }
}
