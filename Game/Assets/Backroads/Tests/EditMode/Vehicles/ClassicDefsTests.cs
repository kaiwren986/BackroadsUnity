using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Canon.Editor;
using Backroads.Vehicles;
using Backroads.Vehicles.Reference;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.Vehicles
{
    /// <summary>
    /// The shipped classic vehicle table equals the browser game's DEFS/META (CanonData/v1/gameplay/vehicles.json) in
    /// every number, and the derived constants (gear ratios, mount height, inertia, hull, wheel layout) equal the
    /// oracle's "derived" block. Only the display names differ (U9), and they carry no real marks.
    /// </summary>
    [TestFixture, Category("Vehicle")]
    public class Vehicle
    {
        static Dictionary<string, object> s_Json;

        static Dictionary<string, object> Json() =>
            s_Json ??= MiniJson.O(MiniJson.Parse(File.ReadAllText(Path.Combine(CanonImporter.RepoRoot, "CanonData", "v1", "gameplay", "vehicles.json"))));

        static Dictionary<string, object> O(Dictionary<string, object> o, string k) => MiniJson.O(MiniJson.Get(o, k));

        static void Eq(List<string> fails, string what, double want, double got, double tol = 0)
        {
            if (double.IsNaN(want) && double.IsNaN(got)) return;
            double t = tol * Math.Max(1, Math.Abs(want));
            if (!(Math.Abs(want - got) <= t)) fails.Add($"{what}: canon {want:R}, C# {got:R}");
        }

        [Test]
        public void ClassicDefsMatchCanon()
        {
            var defs = O(Json(), "DEFS");
            var meta = O(Json(), "META");
            var fails = new List<string>();
            CollectionAssert.AreEqual(MiniJson.A(MiniJson.Get(Json(), "VEHICLE_ORDER")).Cast<string>().ToArray(), ClassicVehicleDefs.Order);
            Assert.AreEqual(BrvSim.STEP, MiniJson.D(Json(), "STEP"));
            foreach (var id in ClassicVehicleDefs.Order)
            {
                var j = O(defs, id);
                var c = ClassicVehicleDefs.Get(id);
                string p = id + ".";
                Assert.AreEqual(MiniJson.S(j, "id"), c.id);
                Assert.AreEqual(MiniJson.S(j, "blurb"), c.blurb, p + "blurb");
                Eq(fails, p + "color", MiniJson.D(j, "color"), c.color);
                Eq(fails, p + "mass", MiniJson.D(j, "mass"), c.mass);
                Eq(fails, p + "inertia", MiniJson.D(j, "inertia"), c.inertia);
                var box = O(j, "box");
                Eq(fails, p + "box.w", MiniJson.D(box, "w"), c.boxW); Eq(fails, p + "box.h", MiniJson.D(box, "h"), c.boxH);
                Eq(fails, p + "box.l", MiniJson.D(box, "l"), c.boxL); Eq(fails, p + "box.y", MiniJson.D(box, "y"), c.boxY);
                Eq(fails, p + "box.z", MiniJson.D(box, "z"), c.boxZ);
                var com = O(j, "com");
                Eq(fails, p + "com.y", MiniJson.D(com, "y"), c.comY); Eq(fails, p + "com.z", MiniJson.D(com, "z"), c.comZ);
                var wh = O(j, "wheel");
                Eq(fails, p + "wheel.r", MiniJson.D(wh, "r"), c.wheelR); Eq(fails, p + "wheel.w", MiniJson.D(wh, "w"), c.wheelW);
                Eq(fails, p + "wheel.x", MiniJson.D(wh, "x"), c.wheelX);
                if (MiniJson.Get(wh, "rows") is List<object> rows)
                {
                    Assert.NotNull(c.wheelRows, p + "wheel.rows");
                    CollectionAssert.AreEqual(rows.Select(MiniJson.D).ToArray(), c.wheelRows, p + "wheel.rows");
                }
                else
                {
                    Eq(fails, p + "wheel.front", MiniJson.D(wh, "front"), c.wheelFront); Eq(fails, p + "wheel.rear", MiniJson.D(wh, "rear"), c.wheelRear);
                    Assert.AreEqual(MiniJson.Get(wh, "dual") is bool dual && dual, c.wheelDual, p + "wheel.dual");
                }
                var su = O(j, "susp");
                Eq(fails, p + "susp.rest", MiniJson.D(su, "rest"), c.suspRest); Eq(fails, p + "susp.k", MiniJson.D(su, "k"), c.suspK);
                Eq(fails, p + "susp.c", MiniJson.D(su, "c"), c.suspC);
                Assert.AreEqual(MiniJson.S(j, "drive"), c.drive, p + "drive");
                Eq(fails, p + "steer", MiniJson.D(j, "steer"), c.steer); Eq(fails, p + "steerRate", MiniJson.D(j, "steerRate"), c.steerRate);
                if (MiniJson.Get(j, "engine") is Dictionary<string, object> e)
                {
                    Assert.IsTrue(c.hasEngine, p + "engine");
                    Eq(fails, p + "engine.torque", MiniJson.D(e, "torque"), c.engineTorque); Eq(fails, p + "engine.idle", MiniJson.D(e, "idle"), c.engineIdle);
                    Eq(fails, p + "engine.redline", MiniJson.D(e, "redline"), c.engineRedline); Eq(fails, p + "engine.vmax", MiniJson.D(e, "vmax"), c.engineVmax);
                    Eq(fails, p + "engine.gears", MiniJson.D(e, "gears"), c.engineGears); Eq(fails, p + "engine.first", MiniJson.D(e, "first"), c.engineFirst);
                }
                else Assert.IsFalse(c.hasEngine, p + "engine");
                if (MiniJson.Get(j, "simple") is Dictionary<string, object> sm)
                {
                    Assert.IsTrue(c.hasSimple);
                    Eq(fails, p + "simple.force", MiniJson.D(sm, "force"), c.simpleForce); Eq(fails, p + "simple.vmax", MiniJson.D(sm, "vmax"), c.simpleVmax);
                }
                else Assert.IsFalse(c.hasSimple, p + "simple");
                if (MiniJson.Get(j, "blade") is Dictionary<string, object> bl)
                {
                    Assert.IsTrue(c.hasBlade);
                    Eq(fails, p + "blade.z", MiniJson.D(bl, "z"), c.bladeZ); Eq(fails, p + "blade.w", MiniJson.D(bl, "w"), c.bladeW);
                    Eq(fails, p + "blade.y0", MiniJson.D(bl, "y0"), c.bladeY0);
                }
                else Assert.IsFalse(c.hasBlade, p + "blade");
                foreach (var (k, v) in new (string, double)[]
                {
                    ("brake", c.brake), ("handbrake", c.handbrake), ("grip", c.grip), ("drag", c.drag), ("roll", c.roll), ("antiRoll", c.antiRoll),
                    ("rollInfluence", c.rollInfluence), ("cap", c.cap), ("camDist", c.camDist), ("camHeight", c.camHeight), ("fuelUse", c.fuelUse), ("mudGrip", c.mudGrip),
                })
                    Eq(fails, p + k, MiniJson.D(j, k), v);
                var snd = O(j, "sound");
                Eq(fails, p + "sound.cyl", MiniJson.D(snd, "cyl"), c.soundCyl); Eq(fails, p + "sound.pitch", MiniJson.D(snd, "pitch"), c.soundPitch);
                Eq(fails, p + "sound.rough", MiniJson.D(snd, "rough"), c.soundRough);
                Assert.AreEqual(MiniJson.Get(j, "awdOnLow") is bool awd && awd, c.awdOnLow, p + "awdOnLow");
                var seat = O(j, "seat");
                Eq(fails, p + "seat.x", MiniJson.D(seat, "x"), c.seatX); Eq(fails, p + "seat.y", MiniJson.D(seat, "y"), c.seatY); Eq(fails, p + "seat.z", MiniJson.D(seat, "z"), c.seatZ);
                Assert.IsNull(MiniJson.Get(j, "winchPull"), p + "winchPull is not in DEFS (the JS default 1.3 applies)");
                // META
                var m = O(meta, id);
                var ex = MiniJson.A(MiniJson.Get(m, "exhaust"));
                Eq(fails, p + "exhaust.x", MiniJson.D(ex[0]), c.exhaustX); Eq(fails, p + "exhaust.y", MiniJson.D(ex[1]), c.exhaustY); Eq(fails, p + "exhaust.z", MiniJson.D(ex[2]), c.exhaustZ);
                Assert.AreEqual(MiniJson.Get(m, "up") is bool up && up, c.exhaustUp, p + "up");
                Eq(fails, p + "glassOpacity", MiniJson.Get(m, "glassOpacity") is double go ? go : 0.36, c.glassOpacity);
                if (MiniJson.Get(m, "defaultPaint") is List<object> dp) CollectionAssert.AreEqual(dp.Select(x => (int)MiniJson.D(x)).ToArray(), c.defaultPaint, p + "defaultPaint");
                else Assert.IsNull(c.defaultPaint, p + "defaultPaint");
                if (MiniJson.Get(m, "cargo") is Dictionary<string, object> cg)
                {
                    Eq(fails, p + "cargo.y", MiniJson.D(cg, "y"), c.cargoY);
                    CollectionAssert.AreEqual(MiniJson.A(MiniJson.Get(cg, "z")).Select(MiniJson.D).ToArray(), c.cargoZ, p + "cargo.z");
                }
                else Assert.IsTrue(double.IsNaN(c.cargoY), p + "cargo");
            }
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>U9 display names: the pickup and the dozer are renamed, the others keep the browser game's names.</summary>
        [Test]
        public void DisplayNames()
        {
            var defs = O(Json(), "DEFS");
            Assert.AreEqual("Kestrel 4x4", ClassicVehicleDefs.Get("pickup").name);
            Assert.AreEqual("Badger D3", ClassicVehicleDefs.Get("dozer").name);
            foreach (var id in new[] { "hatch", "rally", "truck" }) Assert.AreEqual(MiniJson.S(O(defs, id), "name"), ClassicVehicleDefs.Get(id).name, id);
            // PLAN 3.10 denylist (the dozer's "D3" was chosen by the user in U9 and is reported to I, not checked here)
            var deny = new[] { "Honda", "Subaru", "Ferrari", "Chevrolet", "BMW", "Audi", "Toyota", "Ford", "Ranger", "Isuzu", "Hino", "Volvo", "Scania", "Mercedes", "Caterpillar", "Muncher" };
            foreach (var c in ClassicVehicleDefs.All.Values)
                foreach (var w in deny)
                {
                    StringAssert.DoesNotContain(w, c.name, c.id);
                    StringAssert.DoesNotContain(w, c.blurb, c.id);
                }
        }

        /// <summary>BrvDef / BrvSim.Create reproduce vehicles.json "derived": ratios, mount height, box, inertia, hull, wheel layout.</summary>
        [Test]
        public void DerivedMatchCanon()
        {
            var derived = O(Json(), "derived");
            var fails = new List<string>();
            foreach (var id in ClassicVehicleDefs.Order)
            {
                var j = O(derived, id);
                var d = BrvDef.Create(ClassicVehicleDefs.Get(id));
                var s = BrvSim.Create(d);
                string p = id + ".";
                Eq(fails, p + "mass", MiniJson.D(j, "mass"), s.mass);
                if (MiniJson.Get(j, "ratios") is List<object> ratios)
                {
                    Assert.AreEqual(ratios.Count, d.ratios.Length, p + "gears");
                    for (int i = 0; i < ratios.Count; i++) Eq(fails, $"{p}ratios[{i}]", MiniJson.D(ratios[i]), d.ratios[i], 1e-13);
                    Eq(fails, p + "revRatio", MiniJson.D(j, "revRatio"), d.revRatio, 1e-13);
                }
                else Assert.AreEqual(0, d.ratios.Length, p + "ratios");
                Eq(fails, p + "mountModelY", MiniJson.D(j, "mountModelY"), d.mountModelY, 1e-15);
                var box = O(j, "box");
                Eq(fails, p + "box.cy", MiniJson.D(box, "cy"), d.bcy, 1e-15); Eq(fails, p + "box.cz", MiniJson.D(box, "cz"), d.bcz, 1e-15);
                Eq(fails, p + "box.hw", MiniJson.D(box, "hw"), d.bhw); Eq(fails, p + "box.hh", MiniJson.D(box, "hh"), d.bhh); Eq(fails, p + "box.hl", MiniJson.D(box, "hl"), d.bhl);
                var iI = MiniJson.A(MiniJson.Get(j, "iI"));
                Eq(fails, p + "iI.x", MiniJson.D(iI[0]), s.iI.x, 1e-15); Eq(fails, p + "iI.y", MiniJson.D(iI[1]), s.iI.y, 1e-15); Eq(fails, p + "iI.z", MiniJson.D(iI[2]), s.iI.z, 1e-15);
                var hull = MiniJson.A(MiniJson.Get(j, "hull"));
                Assert.AreEqual(hull.Count, d.hull.Length, p + "hull");
                for (int i = 0; i < hull.Count; i++)
                {
                    var h = MiniJson.A(hull[i]);
                    Eq(fails, $"{p}hull[{i}].x", MiniJson.D(h[0]), d.hull[i].x, 1e-15); Eq(fails, $"{p}hull[{i}].y", MiniJson.D(h[1]), d.hull[i].y, 1e-15);
                    Eq(fails, $"{p}hull[{i}].z", MiniJson.D(h[2]), d.hull[i].z, 1e-15);
                }
                var wheels = MiniJson.A(MiniJson.Get(j, "wheels"));
                Assert.AreEqual(wheels.Count, d.wheels.Length, p + "wheels");
                int nDrive = 0;
                for (int i = 0; i < wheels.Count; i++)
                {
                    var w = MiniJson.O(wheels[i]); var l = d.wheels[i];
                    string q = $"{p}wheels[{i}].";
                    Eq(fails, q + "x", MiniJson.D(w, "x"), l.x); Eq(fails, q + "z", MiniJson.D(w, "z"), l.z);
                    Eq(fails, q + "mx", MiniJson.D(w, "mx"), l.mx); Eq(fails, q + "my", MiniJson.D(w, "my"), l.my, 1e-15); Eq(fails, q + "mz", MiniJson.D(w, "mz"), l.mz, 1e-15);
                    Assert.AreEqual((bool)MiniJson.Get(w, "steer"), l.steer, q + "steer");
                    Assert.AreEqual((bool)MiniJson.Get(w, "drive"), l.drive, q + "drive");
                    Assert.AreEqual((bool)MiniJson.Get(w, "hand"), l.hand, q + "hand");
                    Eq(fails, q + "side", MiniJson.D(w, "side"), l.side);
                    Eq(fails, q + "rest", MiniJson.D(w, "rest"), l.rest); Eq(fails, q + "k", MiniJson.D(w, "k"), l.k); Eq(fails, q + "c", MiniJson.D(w, "c"), l.c);
                    if (l.drive) nDrive++;
                }
                Eq(fails, p + "nDrive", MiniJson.D(j, "nDrive"), nDrive);
            }
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>The overall ratios of 03 section 4.14 [measured] and the top speeds they give.</summary>
        [Test]
        public void TopSpeeds()
        {
            foreach (var (id, kmh) in new[] { ("hatch", 148.0), ("rally", 187.0), ("pickup", 137.0), ("truck", 97.0) })
            {
                var c = ClassicVehicleDefs.Get(id);
                var d = BrvDef.Create(c);
                double top = (c.engineRedline * Math.PI / 30) * c.wheelR / d.ratios[d.ratios.Length - 1] * 3.6;
                Assert.AreEqual(kmh, top, 1.0, id);
            }
        }
    }
}
