using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Backroads.Client;
using Backroads.Client.UI;
using Backroads.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace Backroads.Testing
{
    /// <summary>A keyboard of the scenario's own (like AutotestRunner's): queued states, so the Input System sees real key events.</summary>
    sealed class VirtualKeys : IDisposable
    {
        readonly Keyboard _kb;

        public VirtualKeys()
        {
            _kb = InputSystem.AddDevice<Keyboard>("ScenarioKeyboard");
            _kb.MakeCurrent();
        }

        public void Set(params Key[] keys) => InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));

        public IEnumerator Tap(Key k)
        {
            Set(k);
            yield return null; yield return null;
            Set();
            yield return null; yield return null;
        }

        public IEnumerator Hold(Key k, float seconds, Action eachFrame = null)
        {
            Set(k);
            float t = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t < seconds) { yield return new WaitForEndOfFrame(); eachFrame?.Invoke(); yield return null; }
            Set();
            yield return null; yield return null;
        }

        public void Dispose() { if (_kb != null && _kb.added) InputSystem.RemoveDevice(_kb); }
    }

    /// <summary>
    /// m2.hud.stub (T2.7 "done when", lane W): in Millbrook with a stub IDrivenVehicle at the garage, the input driver
    /// fills DriveInput (throttle, ignition tap, starter hold, low range), the chase and hood cameras follow the vehicle,
    /// the HUD shows its values and allocates nothing, F3 toggles the debug overlay, F8 the fly camera; then the stub
    /// drives through Blackwater Bog and the ground ruts (stamps, partial chunk updates). Screenshots: chase, hood, F3,
    /// the bog ruts from the chase camera and from above.
    /// </summary>
    public sealed class HudStubScenario : IAutotestScenario
    {
        public string Kind => "hud-stub";

        [Serializable]
        sealed class Params
        {
            public float loadTimeoutSeconds = 120f;
            public float bogSeconds = 14f;
            public float bogKmh = 18f;
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            yield return WorldScenarioUtil.WaitForMillbrook(ctx, p.loadTimeoutSeconds);
            var world = MillbrookWorld.Current;
            if (world == null || !world.Ready) yield break;
            if (!ctx.Renders) { ctx.Fail("hud-stub needs a window (no -batchmode)"); yield break; }
            var input = ClientInput.Current;
            if (input == null) { ctx.Fail("no ClientInput (bindings.json / Resources/BrClientConfig missing?)"); yield break; }
            var director = CameraDirector.Main;
            if (director == null) { ctx.Fail("no CameraDirector on the Millbrook camera"); yield break; }
            director.LockCursor = false;
            var overlay = OverlayController.Current;
            if (overlay == null) { ctx.Fail("no client overlay"); yield break; }

            var bay = world.Canon.placements.parking[0];
            var stub = StubDrivenVehicle.Spawn(bay.x, bay.z, bay.yaw, world.look);
            stub.Mode = StubDrivenVehicle.DriveMode.Input;
            world.SettleTerrain();
            using var keys = new VirtualKeys();
            for (int i = 0; i < 10; i++) yield return null;

            // -- the camera and the HUD follow the stub
            if (director.Current != CameraDirector.Mode.Chase) ctx.Fail($"camera mode {director.Current} with a vehicle, expected Chase");
            if (!overlay.Hud.Visible) ctx.Fail("the HUD is not shown while driving");
            if (input.FramesSent == 0 || stub.InputCalls == 0) ctx.Fail("the input driver sent no DriveInput");

            // -- throttle (W): DriveInput level, the stub moves, the HUD follows
            int hudMismatch = 0, hudChecks = 0;
            float maxChaseDist = 0, minChaseDist = 1e9f;
            yield return keys.Hold(Key.W, 3f, () =>
            {
                var h = stub.Hud;
                hudChecks++;
                if (overlay.Hud.SpeedShown != Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(h.SpeedKmh)), 0, 400).ToString()) hudMismatch++;
                float d = Vector3.Distance(director.transform.position, stub.Body.position);
                maxChaseDist = Mathf.Max(maxChaseDist, d); minChaseDist = Mathf.Min(minChaseDist, d);
            });
            if (stub.LastInput.Throttle > 0.01f) ctx.Fail("throttle stayed on after W was released");
            float kmh = stub.Hud.SpeedKmh;
            ctx.Metric("stub_kmh_after_throttle", kmh);
            if (kmh < 5f) ctx.Fail($"the stub did not move with W (speed {kmh:F1} km/h): DriveInput.Throttle not filled?");
            ctx.Metric("hud_speed_mismatch_frames", hudMismatch);
            ctx.Metric("hud_checks", hudChecks);
            if (hudMismatch > 2) ctx.Fail($"the HUD speed differed from the vehicle in {hudMismatch} of {hudChecks} frames");
            ctx.Metric("chase_distance_min", minChaseDist);
            ctx.Metric("chase_distance_max", maxChaseDist);
            if (maxChaseDist > 16f || minChaseDist < 2f) ctx.Fail($"chase camera distance {minChaseDist:F1}..{maxChaseDist:F1} m");
            var toBody = stub.Body.position - director.transform.position;
            float facing = Vector3.Dot(director.transform.forward, toBody.normalized);
            ctx.Metric("chase_facing_dot", facing);
            if (facing < 0.8f) ctx.Fail($"the chase camera does not look at the vehicle (dot {facing:F2})");
            yield return ctx.Screenshot("chase");

            // -- ignition tap, starter hold, low range
            uint ign0 = stub.LastInput.IgnitionPresses;
            yield return keys.Tap(Key.I);
            yield return null;
            if (stub.LastInput.IgnitionPresses != ign0 + 1) ctx.Fail($"tap I: IgnitionPresses {ign0} -> {stub.LastInput.IgnitionPresses}");
            if (stub.Hud.Ignition) ctx.Fail("tap I did not switch the stub's ignition off");
            yield return keys.Tap(Key.I);
            bool starterSeen = false;
            uint ign1 = stub.LastInput.IgnitionPresses;
            yield return keys.Hold(Key.I, 0.8f, () => starterSeen |= stub.LastInput.StarterHold);
            if (!starterSeen) ctx.Fail("hold I: StarterHold never set");
            if (stub.LastInput.StarterHold) ctx.Fail("StarterHold stayed on after I was released");
            if (stub.LastInput.IgnitionPresses != ign1) ctx.Fail("hold I also counted an ignition press");
            uint low0 = stub.LastInput.LowRangePresses;
            yield return keys.Tap(Key.X);
            if (stub.LastInput.LowRangePresses != low0 + 1) ctx.Fail("X: LowRangePresses not counted");
            yield return null;

            // -- hood camera (C) and back
            yield return keys.Tap(Key.C);
            yield return new WaitForEndOfFrame();   // after the cameras' LateUpdate, before the next fixed step
            if (director.Current != CameraDirector.Mode.Hood) ctx.Fail($"C: camera mode {director.Current}, expected Hood");
            float hoodOff = Vector3.Distance(director.transform.position, stub.HoodAnchor.position);
            ctx.Metric("hood_offset_m", hoodOff);
            if (hoodOff > 0.05f) ctx.Fail($"hood camera {hoodOff:F2} m from the hood anchor");
            yield return ctx.Screenshot("hood");
            yield return keys.Tap(Key.C);
            if (director.Current != CameraDirector.Mode.Chase) ctx.Fail("second C did not go back to the chase camera");

            // -- F3 overlay
            yield return keys.Tap(Key.F3);
            for (int i = 0; i < 20; i++) yield return null;
            if (!overlay.Debug.Visible) ctx.Fail("F3 did not open the debug overlay");
            else if (overlay.Debug.Text == null || !overlay.Debug.Text.Contains("FRAME") || !overlay.Debug.Text.Contains("SIM")) ctx.Fail("the debug overlay has no frame / sim lines");
            yield return ctx.Screenshot("f3_overlay");
            yield return keys.Tap(Key.F3);
            if (overlay.Debug.Visible) ctx.Fail("second F3 did not close the debug overlay");

            // -- fly camera (F8) and back
            yield return keys.Tap(Key.F8);
            if (director.Current != CameraDirector.Mode.Fly) ctx.Fail("F8 did not switch to the fly camera");
            yield return null;
            if (overlay.Hud.Visible) ctx.Fail("the HUD stayed on in the fly camera");
            yield return keys.Tap(Key.F8);
            if (director.Current == CameraDirector.Mode.Fly) ctx.Fail("second F8 did not go back to driving");

            // -- the bog: autopilot across Blackwater Bog, ruts and partial terrain updates
            var bog = world.Canon.world.bogs.Length > 0 ? world.Canon.world.bogs[0] : default;
            var a = BrCoords.Pos(bog.x - bog.r * 0.55f, 0, bog.z - bog.r * 0.15f);
            var b = BrCoords.Pos(bog.x + bog.r * 0.55f, 0, bog.z + bog.r * 0.15f);
            stub.Mode = StubDrivenVehicle.DriveMode.Autopilot;
            stub.CruiseKmh = p.bogKmh;
            stub.Waypoints = new List<Vector3> { b, a };
            var dirAB = (b - a); dirAB.y = 0;
            stub.Teleport(a, dirAB);
            director.Rig.Attach(stub);
            world.SettleTerrain();
            long stamps0 = world.Deform.StampsApplied;
            int partial0 = world.Terrain.PartialUpdates, builds0 = world.Terrain.TotalBuilds;
            HudView.ResetAllocation();
            var frameMs = new List<float>();
            var simMs = new List<float>();
            float t0 = Time.realtimeSinceStartup;
            bool shot = false;
            while (Time.realtimeSinceStartup - t0 < p.bogSeconds)
            {
                yield return null;
                frameMs.Add(Time.unscaledDeltaTime * 1000f);
                simMs.Add(SimTiming.LastFrameMs);
                if (!shot && Time.realtimeSinceStartup - t0 > p.bogSeconds * 0.6f) { shot = true; yield return ctx.Screenshot("bog_ruts_chase"); }
            }
            long stamps = world.Deform.StampsApplied - stamps0;
            int partial = world.Terrain.PartialUpdates - partial0;
            ctx.Metric("bog_stamps", stamps);
            ctx.Metric("bog_stub_stamps_sent", stub.StampsSent);
            ctx.Metric("bog_partial_chunk_updates", partial);
            ctx.Metric("bog_chunk_builds", world.Terrain.TotalBuilds - builds0);
            ctx.Metric("bog_trees_resnapped", world.Vegetation.Resnapped);
            ctx.Metric("bog_stamp_ms_total", world.Deform.TotalStampMs);
            ctx.Metric("bog_sunk_cm_now", stub.Hud.SunkCm);
            if (stamps < 100) ctx.Fail($"only {stamps} stamps applied in the bog");
            if (partial < 10) ctx.Fail($"only {partial} partial chunk updates in the bog");
            ctx.Metric("hud_ticks", HudView.Ticks);
            ctx.Metric("hud_alloc_bytes", HudView.AllocatedBytes);
            if (HudView.AllocatedBytes > 0) ctx.Fail($"the HUD allocated {HudView.AllocatedBytes} bytes in {HudView.Ticks} updates");
            frameMs.Sort(); simMs.Sort();
            if (frameMs.Count > 0)
            {
                ctx.Metric("bog_frame_p95_ms", frameMs[(int)(frameMs.Count * 0.95f)]);
                ctx.Metric("bog_sim_p95_ms", simMs[(int)(simMs.Count * 0.95f)]);
            }

            // the ruts from above (scripted camera)
            director.Scripted = true;
            var mid = (a + b) * 0.5f;
            mid.y = Ground.HeightAt(mid);
            var cam = director.GetComponent<Camera>();
            cam.transform.SetPositionAndRotation(mid + new Vector3(0f, 16f, -14f), Quaternion.LookRotation(new Vector3(0f, -16f, 14f), Vector3.up));
            stub.CruiseKmh = 0f;
            for (int i = 0; i < 6; i++) yield return null;
            yield return ctx.Screenshot("bog_ruts_above");
            director.Scripted = false;
            UnityEngine.Object.Destroy(stub.gameObject);
            for (int i = 0; i < 3; i++) yield return null;
            if (director.Current != CameraDirector.Mode.Fly) ctx.Fail("without a vehicle the camera did not go back to fly");
        }
    }

    /// <summary>
    /// m2.ui.screens (T2.6, lane W): Esc opens the menu and pauses the game; Controls (from bindings.json), Settings and
    /// Credits open from it and Esc brings it back; a quality change in Settings is applied and saved to settings.json;
    /// F1 shows and hides the controls list. Screenshots of each screen.
    /// </summary>
    public sealed class UiScreensScenario : IAutotestScenario
    {
        public string Kind => "ui-screens";

        public IEnumerator Run(AutotestContext ctx)
        {
            yield return WorldScenarioUtil.WaitForMillbrook(ctx, 120f);
            var world = MillbrookWorld.Current;
            if (world == null || !world.Ready) yield break;
            if (!ctx.Renders) { ctx.Fail("ui-screens needs a window (no -batchmode)"); yield break; }
            var overlay = OverlayController.Current;
            if (overlay == null || ClientInput.Current == null) { ctx.Fail("no client overlay or input"); yield break; }
            using var keys = new VirtualKeys();
            for (int i = 0; i < 5; i++) yield return null;

            yield return keys.Tap(Key.Escape);
            var menu = overlay.Menu;
            if (!menu.Visible) { ctx.Fail("Esc did not open the menu"); yield break; }
            if (Time.timeScale != 0f) ctx.Fail($"the menu did not pause the game (time scale {Time.timeScale})");
            var root = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None).Select(d => d.rootVisualElement).FirstOrDefault(r => r?.Q("quick-menu") != null);
            if (root == null) { ctx.Fail("no UI document with the menu"); yield break; }
            foreach (var name in new[] { "resume", "open-controls", "open-settings", "open-credits", "quit" })
                if (root.Q<Button>(name) == null) ctx.Fail("the menu has no button " + name);
            yield return ctx.Screenshot("menu");

            yield return Press(root, "open-controls");
            if (!overlay.Controls.Visible) ctx.Fail("Controls did not open");
            int rows = root.Q("controls")?.Query<Label>().ToList().Count ?? 0;
            ctx.Metric("controls_labels", rows);
            if (rows < 40) ctx.Fail($"the Controls screen has only {rows} labels");
            yield return ctx.Screenshot("controls");
            yield return keys.Tap(Key.Escape);
            if (overlay.Controls.Visible || !menu.Visible) ctx.Fail("Esc on Controls did not go back to the menu");

            yield return Press(root, "open-settings");
            if (!overlay.Settings.Visible) ctx.Fail("Settings did not open");
            yield return ctx.Screenshot("settings");
            var before = QualityPreset.Tier;
            yield return Press(root, "quality-mid");
            if (QualityPreset.Tier != QualityTier.Mid) ctx.Fail($"quality is {QualityPreset.Tier} after pressing Mid");
            var path = SettingsStore.DefaultPath;
            if (!System.IO.File.Exists(path)) ctx.Fail("settings.json was not written to " + path);
            else
            {
                ctx.Evidence(path);
                var saved = SettingsStore.Load(path, out var msg);
                if (msg != null || saved.quality != "Mid") ctx.Fail($"settings.json has quality '{saved.quality}' ({msg})");
            }
            ctx.Note($"quality {before} -> Mid, saved to {path}");
            yield return keys.Tap(Key.Escape);
            if (overlay.Settings.Visible || !menu.Visible) ctx.Fail("Esc on Settings did not go back to the menu");

            yield return Press(root, "open-credits");
            var credits = root.Q("credits");
            if (credits == null || credits.resolvedStyle.display != DisplayStyle.Flex) ctx.Fail("Credits did not open from the new menu");
            yield return keys.Tap(Key.Escape);
            if (!menu.Visible) ctx.Fail("Esc on Credits did not go back to the menu");

            yield return keys.Tap(Key.Escape);
            if (menu.Visible || menu.Paused) ctx.Fail("Esc on the menu did not resume");
            if (Time.timeScale == 0f) ctx.Fail("still paused after resume");

            yield return keys.Tap(Key.F1);
            for (int i = 0; i < 4; i++) yield return null;
            if (!overlay.Help.Visible) ctx.Fail("F1 did not show the controls list");
            yield return ctx.Screenshot("f1_controls");
            yield return keys.Tap(Key.F1);
            if (overlay.Help.Visible) ctx.Fail("second F1 did not hide the controls list");
        }

        static IEnumerator Press(VisualElement root, string button)
        {
            var b = root.Q<Button>(button);
            if (b != null) using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
            yield return null;
            yield return null;
        }
    }
}
