using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Client;
using Backroads.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Backroads.Tests.EditMode.Input
{
    /// <summary>
    /// T2.6 headline gate Input.NoDuplicateBindings (PLAN 6.1 M2): Content/core/bindings.json and the Input System
    /// actions generated from it bind no key or button twice within one action map (nor across the UI map and the
    /// Driving or OnFoot map, which are on together); every driving action has a keyboard and a gamepad route; the card's
    /// bindings are there; every path resolves on a real keyboard, mouse and gamepad.
    /// </summary>
    [TestFixture, Category("Input")]
    public class Input
    {
        static string BindingsPath => Path.Combine(Application.dataPath, "Content", "core", "bindings.json");

        static BindingTable Table() => BindingTable.Parse(File.ReadAllText(BindingsPath));

        [Test]
        public void NoDuplicateBindings()
        {
            var t = Table();
            var problems = t.Problems();
            TestContext.WriteLine($"bindings.json v{t.version}: " + string.Join(", ", t.maps.Select(m => $"{m.name} {m.bindings.Length} entries")) + $"; deadzone {t.deadzone}, hold {t.holdSeconds} s");
            Assert.IsEmpty(problems, "bindings.json:\n" + string.Join("\n", problems));

            // the generated Input System actions: every concrete control path, per map
            var asset = t.CreateActions();
            try
            {
                var perMap = new Dictionary<string, List<string>>();
                foreach (var map in asset.actionMaps)
                {
                    var paths = map.bindings.Where(b => !b.isComposite).Select(b => BindingTable.Normalize(b.path)).ToList();
                    perMap[map.name] = paths;
                    var dups = paths.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    Assert.IsEmpty(dups, $"generated map {map.name} binds twice: " + string.Join(", ", dups));
                    int entries = t.Map(map.name).bindings.Sum(b => b.AllControls.Count());
                    Assert.AreEqual(entries, paths.Count, $"generated map {map.name} has {paths.Count} control bindings, the table {entries}");
                }
                foreach (var mode in new[] { "Driving", "OnFoot" })
                {
                    var shared = perMap[mode].Intersect(perMap["UI"]).ToList();
                    Assert.IsEmpty(shared, $"UI and {mode} share " + string.Join(", ", shared));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }

            // the check itself catches a duplicate
            var broken = BindingTable.Parse(JsonUtility.ToJson(t));
            var brake = broken.Map("Driving").bindings.First(b => b.action == "brake");
            brake.keys = brake.keys.Concat(new[] { "<Keyboard>/w" }).ToArray();
            Assert.IsTrue(broken.Problems().Any(p => p.Contains("bound twice")), "a duplicate W was not reported");
            var cross = BindingTable.Parse(JsonUtility.ToJson(t));
            var horn = cross.Map("Driving").bindings.First(b => b.action == "horn");
            horn.keys = new[] { "<Keyboard>/f1" };
            Assert.IsTrue(cross.Problems().Any(p => p.Contains("is in UI")), "F1 in UI and Driving was not reported");
        }

        /// <summary>The card's list (T2.6 step 1) and every DriveInput field's source.</summary>
        [Test]
        public void CardBindings()
        {
            var t = Table();
            Assert.AreEqual(0.14f, t.deadzone, 1e-6f, "deadzone");
            void Has(string map, string action, string control, string hold = null)
            {
                var m = t.Map(map);
                var e = m.bindings.FirstOrDefault(b => b.action == action && b.AllControls.Any(c => BindingTable.Normalize(c) == BindingTable.Normalize(control)));
                Assert.NotNull(e, $"{map}.{action} is not bound to {control}");
                if (hold != null) Assert.AreEqual(hold, e.hold, $"{map}.{action} on {control}: hold");
            }
            // the browser game's keys (03 section 4.26)
            Has("Driving", "throttle", "<Keyboard>/w"); Has("Driving", "brake", "<Keyboard>/s");
            Has("Driving", "steer", "<Keyboard>/a"); Has("Driving", "steer", "<Keyboard>/d");
            Has("Driving", "handbrake", "<Keyboard>/space"); Has("Driving", "getInOut", "<Keyboard>/e");
            Has("Driving", "lights", "<Keyboard>/l"); Has("Driving", "horn", "<Keyboard>/h");
            Has("Driving", "camera", "<Keyboard>/c"); Has("Driving", "resetUpright", "<Keyboard>/r");
            Has("Driving", "lowRange", "<Keyboard>/x"); Has("Driving", "winch", "<Keyboard>/g");
            Has("Driving", "bladeUp", "<Keyboard>/q"); Has("Driving", "bladeDown", "<Keyboard>/z");
            Has("UI", "map", "<Keyboard>/m"); Has("UI", "help", "<Keyboard>/f1"); Has("UI", "graphics", "<Keyboard>/f2");
            Has("UI", "timeBack", "<Keyboard>/leftBracket"); Has("UI", "timeForward", "<Keyboard>/rightBracket");
            // M2 additions
            Has("Driving", "ignition", "<Keyboard>/i", hold: "starter");
            Has("Driving", "gearUp", "<Keyboard>/leftShift"); Has("Driving", "gearDown", "<Keyboard>/leftCtrl");
            Has("Driving", "clutch", "<Keyboard>/v"); Has("Driving", "manualToggle", "<Keyboard>/f4");
            Has("Driving", "diffLock", "<Keyboard>/k");
            Has("UI", "menu", "<Keyboard>/escape"); Has("UI", "debug", "<Keyboard>/f3"); Has("UI", "checklist", "<Keyboard>/f10");
            // gamepad examples of the card
            Has("UI", "menu", "<Gamepad>/start", hold: "resetUpright");
            Has("Driving", "gearUp", "<Gamepad>/dpad/up"); Has("Driving", "gearDown", "<Gamepad>/dpad/down");
            Has("Driving", "ignition", "<Gamepad>/dpad/left", hold: "starter");
            Has("Driving", "throttle", "<Gamepad>/rightTrigger"); Has("Driving", "brake", "<Gamepad>/leftTrigger");
            Has("Driving", "steer", "<Gamepad>/leftStick/x"); Has("Driving", "handbrake", "<Gamepad>/buttonSouth");

            // every DriveInput field is fed (WinchReel is the throttle while the winch is on, as in main.js)
            var driving = new HashSet<string>(t.Map("Driving").bindings.Select(b => b.action));
            foreach (var a in new[] { "throttle", "brake", "steer", "handbrake", "horn", "starter", "clutch", "ignition", "lowRange", "winch",
                                      "resetUpright", "lights", "gearUp", "gearDown", "manualToggle", "getInOut", "bladeUp", "bladeDown", "diffLock" })
                Assert.IsTrue(driving.Contains(a), "Driving has no action " + a);
            var fields = typeof(DriveInput).GetFields().Select(f => f.Name).ToList();
            Assert.AreEqual(20, fields.Count, "DriveInput changed: update ClientInput.SendDrive and this test: " + string.Join(", ", fields));
        }

        /// <summary>Every path in the table names a control that exists on a keyboard, a mouse or a gamepad.</summary>
        [Test]
        public void PathsResolve()
        {
            var t = Table();
            var kb = InputSystem.AddDevice<Keyboard>("BrTestKeyboard");
            var mouse = InputSystem.AddDevice<Mouse>("BrTestMouse");
            var pad = InputSystem.AddDevice<Gamepad>("BrTestGamepad");
            try
            {
                var bad = new List<string>();
                foreach (var m in t.maps)
                    foreach (var b in m.bindings)
                        foreach (var c in b.AllControls)
                        {
                            var n = BindingTable.Normalize(c);
                            InputDevice dev = n.StartsWith("<keyboard>") ? kb : n.StartsWith("<mouse>") ? mouse : (InputDevice)pad;
                            if (InputControlPath.TryFindControl(dev, c) == null) bad.Add($"{m.name}.{b.action}: {c}");
                        }
                Assert.IsEmpty(bad, "paths that match no control:\n" + string.Join("\n", bad));
            }
            finally
            {
                InputSystem.RemoveDevice(kb); InputSystem.RemoveDevice(mouse); InputSystem.RemoveDevice(pad);
            }
        }
    }

    /// <summary>T2.6 headline gate Settings.RoundTrip: settings.json survives a restart; an unknown version falls back to defaults with a message.</summary>
    [TestFixture, Category("Input")]
    public class Settings
    {
        string _dir;

        [SetUp] public void SetUp() { _dir = Path.Combine(Path.GetTempPath(), "br-settings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); }
        [TearDown] public void TearDown() { try { Directory.Delete(_dir, true); } catch { } QualityPreset.Override(null); }

        [Test]
        public void RoundTrip()
        {
            var path = Path.Combine(_dir, SettingsStore.FileName);
            var s = new GameSettings
            {
                quality = "Low", masterVolume = 0.42f, engineVolume = 0.7f, effectsVolume = 0.33f,
                width = 1600, height = 900, fullscreen = "Windowed", lookSensitivity = 1.7f, invertLookY = true,
                bindings = new List<BindingOverride> { new BindingOverride { map = "Driving", action = "horn", slot = "keys", paths = new[] { "<Keyboard>/j" } } },
            };
            Assert.IsTrue(SettingsStore.Save(s, path, out var err), err);
            var text = File.ReadAllText(path);
            StringAssert.Contains("\"version\": 1", text);

            // "restart": a fresh load from the file
            var back = SettingsStore.Load(path, out var msg);
            Assert.IsNull(msg, "a good file gave a message: " + msg);
            Assert.IsTrue(back.SameAs(s), "round trip changed the settings:\n" + text + "\nvs\n" + JsonUtility.ToJson(back, true));
            Assert.AreEqual("Low", back.quality);
            Assert.AreEqual(0.42f, back.masterVolume);
            Assert.AreEqual(1600, back.width);
            Assert.AreEqual("Windowed", back.fullscreen);
            Assert.IsTrue(back.invertLookY);
            Assert.AreEqual("<Keyboard>/j", back.bindings[0].paths[0]);
            var again = SettingsStore.Load(path, out _);
            Assert.IsTrue(again.SameAs(back), "a second restart differs");

            // the quality tier applies through QualityPreset (the editor skips the Unity side, as QualityApply does)
            SettingsStore.Apply(back, atBoot: true);
            Assert.AreEqual(QualityTier.Low, QualityPreset.Tier);

            // key changes go through the binding table; one that makes a duplicate is caught
            var table = BindingTable.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Content", "core", "bindings.json")));
            var mine = table.WithOverrides(back.bindings);
            Assert.AreEqual("<Keyboard>/j", mine.Map("Driving").bindings.First(b => b.action == "horn").keys[0]);
            Assert.IsEmpty(mine.Problems());
            var clash = table.WithOverrides(new[] { new BindingOverride { map = "Driving", action = "horn", slot = "keys", paths = new[] { "<Keyboard>/w" } } });
            Assert.IsNotEmpty(clash.Problems(), "horn on W should clash with the throttle");
        }

        [Test]
        public void UnknownVersionFallsBack()
        {
            var path = Path.Combine(_dir, SettingsStore.FileName);
            File.WriteAllText(path, "{\"version\": 99, \"quality\": \"Low\", \"masterVolume\": 0.1}");
            var s = SettingsStore.Load(path, out var msg);
            TestContext.WriteLine("message: " + msg);
            Assert.IsNotNull(msg, "no message for version 99");
            StringAssert.Contains("version 99", msg);
            Assert.IsTrue(s.SameAs(new GameSettings()), "not the defaults");
            Assert.IsTrue(File.Exists(path + ".bak"), "the old file was not kept");

            File.WriteAllText(path, "{ not json");
            s = SettingsStore.Load(path, out msg);
            Assert.IsNotNull(msg, "no message for a damaged file");
            Assert.IsTrue(s.SameAs(new GameSettings()));

            File.Delete(path);
            s = SettingsStore.Load(path, out msg);
            Assert.IsNull(msg, "a missing file is not an error");
            Assert.IsTrue(s.SameAs(new GameSettings()));
        }
    }
}
