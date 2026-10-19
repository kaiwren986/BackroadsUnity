using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Backroads.Client;
using Backroads.Core;
using Backroads.Vehicles;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m2.vehicles.drive (card T2.2, "you play" in a Dev build): Millbrook starts with the four M2 vehicles settled on
    /// the garage bays and the player in the truck (PlayerVehicle.Current); E at the bay switches into the nearest
    /// parked vehicle; L and H work; then a scripted DriveInput drives the truck off its bay and brakes it to a stop
    /// (lane W's real input arrives at merge). Reports the simulation main-thread time per rendered frame (PLAN 6.1 M2
    /// lane gate, p95 at most 3.5 ms at High). Screenshots: the garage bays, and the truck under way.
    /// </summary>
    public sealed class VehicleDriveScenario : IAutotestScenario
    {
        public string Kind => "vehicles-drive";

        [Serializable]
        sealed class Params
        {
            public float waitSeconds = 90f;
            public float throttle = 0.6f;
            public float driveSeconds = 3f;
            public float steer = 0f;
            public float brakeSeconds = 3f;
            public float minDistanceM = 8f;
            public float simP95MsBudget = 3.5f;
            public string startVehicle = "Hauler 7.5t";
        }

        DriveInput _in;

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            float t0 = Time.realtimeSinceStartup;
            VehicleSimSystem sys = null;
            while (Time.realtimeSinceStartup - t0 < p.waitSeconds)
            {
                sys = VehicleSimSystem.Current;
                var w = MillbrookWorld.Current;
                if (sys != null && sys.Vehicles.Count >= 4 && PlayerVehicle.Current != null && w != null && (w.Ready || w.Error != null)) break;
                yield return null;
            }
            if (sys == null || PlayerVehicle.Current == null) { ctx.Fail($"no vehicles after {p.waitSeconds:F0} s (VehicleSimSystem {(sys == null ? "missing" : sys.Vehicles.Count + " vehicles")}, PlayerVehicle {(PlayerVehicle.Current == null ? "none" : "set")})"); yield break; }
            var world = MillbrookWorld.Current;
            var cam = world != null && world.cam != null ? world.cam : Camera.main;
            var player = PlayerVehicle.Current;
            ctx.Note($"vehicles: {sys.Vehicles.Count}; player in the {player.DisplayName}");
            if (player.DisplayName != p.startVehicle) ctx.Fail($"the player starts in the {player.DisplayName}, expected the {p.startVehicle}");
            var truck = sys.Player;

            // parked and settled on the bays
            var bays = Backroads.World.CanonRuntime.Asset.placements.parking;
            float worstBay = 0, worstV = 0;
            foreach (var v in sys.Vehicles)
            {
                var b = bays[Array.IndexOf(ClassicVehicleDefs.Order, v.Def.id)];
                var bayU = new Vector3(-b.x, 0, b.z);
                var pos = v.Position; pos.y = 0;
                worstBay = Mathf.Max(worstBay, Vector3.Distance(pos, bayU));
                worstV = Mathf.Max(worstV, v.Velocity.magnitude);
            }
            ctx.Metric("bayOffsetMaxM", worstBay);
            ctx.Metric("parkedSpeedMaxMps", worstV);
            if (worstBay > 1.5f) ctx.Fail($"a vehicle is {worstBay:F2} m off its bay");
            if (worstV >= 0.01f) ctx.Fail($"a parked vehicle moves at {worstV:F3} m/s");

            // garage shot: from the road side, looking at the bays
            if (cam != null)
            {
                var centre = Vector3.zero;
                foreach (var v in sys.Vehicles) centre += v.Position;
                centre /= sys.Vehicles.Count;
                var fwd = truck.Rotation * Vector3.forward;
                cam.transform.position = centre + fwd * 16f + Vector3.up * 6f + Vector3.Cross(Vector3.up, fwd) * 6f;
                cam.transform.LookAt(centre + Vector3.up * 0.8f);
                yield return ctx.Wait(0.5f);
                yield return ctx.Screenshot("garage_bays");
            }

            // E at the bay: into the nearest parked vehicle, then back into the truck
            var nearest = sys.NearestOther(truck, 12f);
            _in = default;
            Feed(player);
            yield return null;
            _in.GetInOutPresses++;
            Feed(player);
            yield return new WaitForFixedUpdate();
            yield return null;
            if (nearest == null || sys.Player != nearest) ctx.Fail($"E did not switch into the nearest vehicle ({nearest?.DisplayName ?? "none within 12 m"}); now {sys.Player?.DisplayName}");
            else ctx.Note($"E: switched into the {sys.Player.DisplayName}, PlayerVehicle {PlayerVehicle.Current?.DisplayName}");
            if (PlayerVehicle.Current == null || PlayerVehicle.Current.DisplayName != sys.Player?.DisplayName) ctx.Fail("PlayerVehicle.Current did not follow the switch");
            sys.SetPlayer(truck);
            yield return null;
            player = PlayerVehicle.Current;
            if (player == null || player.DisplayName != p.startVehicle) { ctx.Fail("could not get back into the truck"); yield break; }

            // L and H
            _in = default;
            Feed(player);
            yield return null;
            _in.LightsPresses++;
            _in.Horn = true;
            Feed(player);
            yield return new WaitForFixedUpdate();
            yield return null;
            if (!truck.State.lightsOn) ctx.Fail("L did not switch the lights on");
            if (!truck.State.horn) ctx.Fail("H did not sound the horn");
            _in.LightsPresses++;
            _in.Horn = false;
            Feed(player);
            yield return new WaitForFixedUpdate();
            yield return null;

            // drive off the bay and stop
            sys.ResetTiming();
            var start = truck.Position;
            float maxKmh = 0, maxImpact = 0, tEnd = Time.time + p.driveSeconds;
            bool shot = false;
            int engineSteps = 0;
            while (Time.time < tEnd)
            {
                _in.Throttle = p.throttle; _in.Brake = 0; _in.Steer = p.steer;
                Feed(player);
                maxKmh = Mathf.Max(maxKmh, player.Hud.SpeedKmh);
                maxImpact = Mathf.Max(maxImpact, (float)truck.LastImpact);
                if (truck.Engine != null) engineSteps++;
                Follow(cam, player);
                if (!shot && Time.time > tEnd - p.driveSeconds * 0.4f) { shot = true; yield return ctx.Screenshot("truck_driving"); }
                yield return null;
            }
            // brake to a stop, then let go (held S would select reverse, as in the browser game)
            tEnd = Time.time + p.brakeSeconds;
            var brakeFrom = truck.Position;
            float brakeKmh = player.Hud.SpeedKmh;
            float stopAfter = -1, brakeStart = Time.time, brakeDist = 0;
            while (Time.time < tEnd)
            {
                bool stopped = stopAfter >= 0 || player.Hud.SpeedKmh < 1f;
                if (stopped && stopAfter < 0) { stopAfter = Time.time - brakeStart; brakeDist = Vector3.Distance(brakeFrom, truck.Position); }
                _in.Throttle = 0; _in.Brake = stopped ? 0 : 1; _in.Steer = 0;
                Feed(player);
                Follow(cam, player);
                yield return null;
            }
            ctx.Metric("brakeFromKmh", brakeKmh);
            ctx.Metric("impactMaxNs", maxImpact);
            ctx.Metric("stopSeconds", stopAfter);
            ctx.Metric("brakeDistanceM", brakeDist);
            _in.Brake = 0;
            Feed(player);
            float dist = Vector3.Distance(start, truck.Position);
            float endKmh = player.Hud.SpeedKmh;
            float p95 = sys.SimMsPercentile(95), p50 = sys.SimMsPercentile(50), pmax = sys.SimMsPercentile(100);
            ctx.Metric("distanceM", dist);
            ctx.Metric("maxKmh", maxKmh);
            ctx.Metric("endKmh", endKmh);
            ctx.Metric("simMsP95", p95);
            ctx.Metric("simMsP50", p50);
            ctx.Metric("simMsMax", pmax);
            ctx.Metric("engineAttached", truck.Engine != null ? 1 : 0);
            ctx.Metric("fixedSteps", sys.TotalSteps);
            ctx.Note($"drove {dist:F1} m, top {maxKmh:F1} km/h, {endKmh:F1} km/h after braking; engine {(truck.Engine != null ? truck.Engine.EngineId : "none (silent)")}; " +
                     $"sim main thread per frame p50 {p50:F3} ms, p95 {p95:F3} ms, max {pmax:F3} ms");
            if (dist < p.minDistanceM) ctx.Fail($"the truck moved only {dist:F1} m");
            if (endKmh > 2f) ctx.Fail($"the truck did not stop ({endKmh:F1} km/h)");
            if (p95 > p.simP95MsBudget) ctx.Fail($"simulation main-thread time per frame p95 {p95:F2} ms > {p.simP95MsBudget} ms");
            // night: L puts the headlights on (vehicleModels.js setLights + the two spots of main.js)
            if (world != null && cam != null && ctx.Renders)
            {
                world.SetHour(21f);
                _in.LightsPresses++;
                Feed(player);
                yield return new WaitForFixedUpdate();
                yield return null;
                var b = player.Body;
                var f = b.rotation * Vector3.forward; f.y = 0; f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
                cam.transform.position = b.position + f * 11f + Vector3.Cross(Vector3.up, f) * 4f + Vector3.up * 2.2f;
                cam.transform.LookAt(b.position + Vector3.up * 0.8f);
                yield return ctx.Wait(0.5f);
                yield return ctx.Screenshot("truck_night_lights");
                if (!truck.State.lightsOn) ctx.Fail("L did not switch the lights on at night");
                world.SetHour(10f);
            }
            File.WriteAllText(Path.Combine(ctx.OutDir, "vehicles.txt"),
                $"player {player.DisplayName}\ndistance {dist:F2} m\nmax {maxKmh:F1} km/h\nsim ms p50 {p50:F3} p95 {p95:F3} max {pmax:F3}\n");
        }

        void Feed(IDrivenVehicle v) => v?.SetInput(_in);

        static void Follow(Camera cam, IDrivenVehicle v)
        {
            if (cam == null || v == null) return;
            var b = v.Body;
            var back = b.rotation * Vector3.back;
            back.y = 0;
            back = back.sqrMagnitude > 1e-4f ? back.normalized : Vector3.back;
            cam.transform.position = b.position + back * 13f + Vector3.up * 5f;
            cam.transform.LookAt(b.position + Vector3.up * 1.5f);
        }
    }
}
