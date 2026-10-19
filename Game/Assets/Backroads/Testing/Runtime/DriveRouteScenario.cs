using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Core;
using Backroads.EngineSim;
using Backroads.Roads;
using Backroads.Vehicles;
using Backroads.World;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m2.drive.route9 (PLAN 6.1 M2 headline, card T2.10): the player's truck drives from the Millbrook garage to the fuel
    /// station along the road graph (lane R's RoadNetworkAsset, shortest path), steered by a pure-pursuit driver through the
    /// real DriveInput path, with lane W's chase camera and HUD and lane V's engine-sim V8 in FOLLOW_RPM.
    /// Gates: the PLAN 1.9 pitch rule within ±3 % in the steadiest window of every gear used; 0 underruns (counted from 1 s
    /// after the engine exists, and not across the screenshot); p95 frame ≤ 16.6 ms at High. Writes route_report.json and
    /// engine_tap.wav; screenshots at the start, mid-route and at the fuel station.
    /// </summary>
    public sealed class DriveRouteScenario : IAutotestScenario
    {
        public string Kind => "drive-route";

        [Serializable]
        sealed class Params
        {
            public string startVehicle = "Hauler 7.5t";
            public string targetPoi = "fuel";
            public float arriveRadius = 30f;
            public float cruiseKmh = 45f;
            public float cornerKmh = 22f;
            public float maxSeconds = 150f;
            public float waitSeconds = 60f;
            public float pitchTolerance = 0.03f;
            public float pitchWindowS = 0.4f;
            public float maxRpmSpread = 0.04f;
            public int maxUnderruns = 0;
            public float frameP95MsBudget = 16.6f;
        }

        [Serializable]
        sealed class GearWindow
        {
            public int gear;
            public float fromS, toS;
            public double rpm, spread, acfHz, acfOrder, peakHz, peakOrder;
            public bool pass;
        }

        [Serializable]
        sealed class Report
        {
            public string vehicle, engine, quality;
            public bool arrived;
            public float seconds, distanceM, maxKmh, endDistanceToTarget, frameP95Ms, frameP50Ms;
            public int routeNodes, routePoints, gearsUsed, underruns, underrunFrames, underrunsAcrossShots, coastEvents;
            public double trackingRmsRpm;
            public List<GearWindow> gears = new List<GearWindow>();
            public List<string> failures = new List<string>();
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var report = new Report { quality = QualityPreset.Describe(QualityPreset.Current) };

            // --- wait for Millbrook, the vehicles and the player's engine
            VehicleSimSystem sys = null;
            float w0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - w0 < p.waitSeconds)
            {
                sys = VehicleSimSystem.Current;
                if (sys != null && sys.Player != null && PlayerVehicle.Current != null && CanonRuntime.IsAvailable) break;
                yield return null;
            }
            if (sys == null || sys.Player == null || PlayerVehicle.Current == null) { ctx.Fail("no player vehicle in Millbrook"); yield break; }
            var player = PlayerVehicle.Current;
            report.vehicle = player.DisplayName;
            if (player.DisplayName != p.startVehicle) ctx.Fail($"the player starts in the {player.DisplayName}, expected the {p.startVehicle}");

            PlayerVehicle.ScriptedInput = true;
            var input = new DriveInput();
            player.SetInput(input);

            EngineSimVehicleEngine engine = null;
            for (float e0 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - e0 < 10f; )
            {
                engine = sys.Player.Engine as EngineSimVehicleEngine;
                if (engine != null && engine.Engine != null) break;
                yield return null;
            }
            if (engine == null || engine.Engine == null) { ctx.Fail("the player's vehicle has no engine-sim engine (VehicleEngines not registered?)"); yield break; }
            report.engine = engine.EngineId;
            var tap = engine.AudioObject.AddComponent<AudioTap>();
            engine.Audio.Tap = tap;
            // the engine's first second (creation, starter) is not the gate's
            for (float e1 = Time.realtimeSinceStartup; Time.realtimeSinceStartup - e1 < 1.2f; ) yield return null;

            // --- route over the road graph
            var net = RoadNetworkAsset.Load();
            if (net == null || net.nodes.Length == 0) { ctx.Fail("no RoadNetworkAsset (lane R's bake) in the build"); yield break; }
            if (!CanonRuntime.Asset.world.TryGetPoi(p.targetPoi, out var poi)) { ctx.Fail("no POI " + p.targetPoi); yield break; }
            var targetJs = new Vector3(poi.x, 0, poi.z);
            var start = player.Body.position;
            // the asset stores Unity positions; if its nearest node to the truck is far, it holds JS x, so flip it
            bool flip = NearestNodeDistance(net, start, false) > NearestNodeDistance(net, start, true) + 5f;
            Vector3 Pos(Vector3 v) => flip ? new Vector3(-v.x, v.y, v.z) : v;
            var target = BrCoords.Pos(targetJs.x, 0, targetJs.z);
            target.y = start.y;
            var path = PlanRoute(net, Pos, start, target, out int nodes);
            if (path.Count < 2) { ctx.Fail("no route from the garage to " + p.targetPoi); yield break; }
            path.Add(target);
            report.routeNodes = nodes;
            report.routePoints = path.Count;
            ctx.Note($"route: {nodes} graph nodes, {path.Count} points, {RouteLength(path):F0} m, graph coordinates {(flip ? "JS (flipped)" : "Unity")}");

            yield return ctx.Screenshot("route9_start");

            // --- drive
            int rate = AudioSettings.outputSampleRate;
            int ch = Mathf.Max(1, tap.Channels);
            var captured = new List<float>(rate * 2 * 120);
            var samples = new List<(float t, float rpm, int gear, int sampleIndex)>(16384);
            var frameMs = new List<float>(16384);
            tap.Clear();
            tap.Recording = true;
            engine.ResetTracking();
            var s0 = engine.Engine.Stats();
            int shotUnderruns = 0;
            bool midShot = false;
            float t0 = Time.unscaledTime, dist = 0f, lastFrameT = Time.unscaledTime;
            Vector3 last = player.Body.position;
            int seg = 0;
            while (true)
            {
                float t = Time.unscaledTime - t0;
                var pos = player.Body.position;
                dist += Vector3.Distance(new Vector3(pos.x, 0, pos.z), new Vector3(last.x, 0, last.z));
                last = pos;
                float toTarget = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(target.x, target.z));
                if (toTarget < p.arriveRadius) { report.arrived = true; break; }
                if (t > p.maxSeconds) break;

                // pure pursuit: advance along the path, steer at a point ahead, slow for corners
                var hud = player.Hud;
                float kmh = Mathf.Abs(hud.SpeedKmh);
                report.maxKmh = Mathf.Max(report.maxKmh, kmh);
                while (seg < path.Count - 2 && Flat(pos, path[seg + 1]) < 8f) seg++;
                float look = 7f + 0.35f * kmh / 3.6f;
                var aim = PointAhead(path, seg, pos, look);
                var fwd = player.Body.forward;
                float angle = Vector2.SignedAngle(new Vector2(aim.x - pos.x, aim.z - pos.z), new Vector2(fwd.x, fwd.z)); // + = target to the right
                float bendAhead = BendAhead(path, seg, pos, 25f);
                float targetKmh = bendAhead > 35f || Mathf.Abs(angle) > 25f ? p.cornerKmh : p.cruiseKmh;
                input.Steer = Mathf.Clamp(angle / 30f, -1f, 1f);
                input.Throttle = Mathf.Clamp01((targetKmh - kmh) / 10f);
                input.Brake = kmh > targetKmh + 4f && kmh > 3f ? Mathf.Clamp01((kmh - targetKmh) / 12f) : 0f;
                player.SetInput(input);

                int before = captured.Count;
                tap.Drain(captured);
                samples.Add((t, engine.EngineRpm, hud.Gear, before / ch));
                float now = Time.unscaledTime;
                frameMs.Add((now - lastFrameT) * 1000f);
                lastFrameT = now;

                if (!midShot && dist > RouteLength(path) * 0.5f)
                {
                    midShot = true;
                    var a = engine.Engine.Stats().audioUnderruns;
                    yield return ctx.Screenshot("route9_mid");
                    yield return null;
                    shotUnderruns += engine.Engine.Stats().audioUnderruns - a;
                    lastFrameT = Time.unscaledTime;  // the capture frame is not a game frame
                    continue;
                }
                yield return null;
            }
            input.Throttle = 0; input.Brake = 0; input.Steer = 0; input.Handbrake = true;
            player.SetInput(input);
            var s1 = engine.Engine.Stats();
            tap.Drain(captured);
            tap.Recording = false;
            report.seconds = Time.unscaledTime - t0;
            report.distanceM = dist;
            report.endDistanceToTarget = Vector2.Distance(new Vector2(player.Body.position.x, player.Body.position.z), new Vector2(target.x, target.z));
            report.underrunsAcrossShots = shotUnderruns;
            report.underruns = s1.audioUnderruns - s0.audioUnderruns - shotUnderruns;
            report.underrunFrames = s1.audioUnderrunFrames - s0.audioUnderrunFrames;
            report.coastEvents = s1.coastEvents - s0.coastEvents;
            report.trackingRmsRpm = engine.TrackingStats.Rms;
            var sorted = frameMs.Skip(5).OrderBy(x => x).ToList();
            report.frameP95Ms = sorted.Count > 0 ? sorted[(int)(0.95f * (sorted.Count - 1))] : 0;
            report.frameP50Ms = sorted.Count > 0 ? sorted[sorted.Count / 2] : 0;

            // --- pitch per gear: the steadiest window of each gear
            int mono = captured.Count / ch;
            var x = new float[mono];
            for (int i = 0; i < mono; i++) x[i] = captured[i * ch];
            AudioTap.SaveWav(Path.Combine(ctx.OutDir, "engine_tap.wav"), captured.ToArray(), captured.Count, ch, rate);
            ctx.Evidence(Path.Combine(ctx.OutDir, "engine_tap.wav"));
            float idle = engine.Def.idleRpm > 0 ? engine.Def.idleRpm : 800f;
            foreach (int gear in samples.Select(s => s.gear).Where(g => g > 0).Distinct().OrderBy(g => g))
            {
                GearWindow best = null;
                for (int i = 0; i < samples.Count; i++)
                {
                    if (samples[i].gear != gear) continue;
                    float tA = samples[i].t, tB = tA + p.pitchWindowS;
                    int j = i;
                    double mn = double.MaxValue, mx = 0, sum = 0;
                    int n = 0;
                    bool sameGear = true;
                    for (; j < samples.Count && samples[j].t < tB; j++)
                    {
                        if (samples[j].gear != gear) { sameGear = false; break; }
                        mn = Math.Min(mn, samples[j].rpm); mx = Math.Max(mx, samples[j].rpm); sum += samples[j].rpm; n++;
                    }
                    if (!sameGear || j >= samples.Count || n < 5) continue;
                    double mean = sum / n, spread = (mx - mn) / mean;
                    if (mean < 1.3 * idle || spread > p.maxRpmSpread) continue;
                    if (best == null || spread < best.spread)
                        best = new GearWindow { gear = gear, fromS = tA, toS = tB, rpm = mean, spread = spread };
                }
                if (best == null) { ctx.Note($"gear {gear}: no steady window (spread ≤ {p.maxRpmSpread:P0} above idle) to check the pitch"); continue; }
                int i0 = SampleAt(samples, best.fromS), i1 = SampleAt(samples, best.toS);
                if (i1 - i0 < 2048) { ctx.Note($"gear {gear}: window too short in the capture"); continue; }
                var r = PitchAnalysis.Check(x, i0, i1 - i0, 1, rate, best.rpm, engine.Def.pitchOrders, p.pitchTolerance);
                best.acfHz = r.acfHz; best.acfOrder = r.acfOrder; best.peakHz = r.peakHz; best.peakOrder = r.peakOrder; best.pass = r.Pass;
                report.gears.Add(best);
                ctx.Note($"pitch gear {gear} at {best.rpm:F0} rpm (spread {best.spread:P1}): {r}");
                ctx.Metric($"pitch_gear{gear}_rpm", best.rpm);
                ctx.Metric($"pitch_gear{gear}_pass", best.pass ? 1 : 0);
                if (!best.pass) report.failures.Add($"pitch in gear {gear} at {best.rpm:F0} rpm: acf {best.acfHz:F1} Hz (order {best.acfOrder:F2}), peak {best.peakHz:F1} Hz");
            }
            report.gearsUsed = samples.Select(s => s.gear).Where(g => g > 0).Distinct().Count();

            if (!report.arrived) report.failures.Add($"did not reach the {p.targetPoi} within {p.maxSeconds:F0} s ({report.endDistanceToTarget:F0} m left)");
            if (report.gears.Count < 2) report.failures.Add($"pitch checked in {report.gears.Count} gear(s); at least 2 needed");
            if (report.underruns > p.maxUnderruns) report.failures.Add($"{report.underruns} underruns ({report.underrunFrames} frames)");
            // Uncapped: the PLAN budget (p95 ≤ 16.6 ms at High). Under a frame cap every frame lasts the cap's period by
            // design, so the gate there is holding the cap (p95 within 5 % of the period).
            int cap = Application.targetFrameRate;
            if (cap > 0)
            {
                float period = 1000f / cap;
                ctx.Metric("frame_cap", cap);
                if (report.frameP95Ms > period * 1.05f) report.failures.Add($"p95 frame {report.frameP95Ms:F2} ms does not hold the {cap} fps cap ({period:F2} ms)");
            }
            else if (QualityPreset.Tier == QualityTier.High && report.frameP95Ms > p.frameP95MsBudget)
                report.failures.Add($"p95 frame {report.frameP95Ms:F2} ms > {p.frameP95MsBudget} ms at High");

            ctx.Metric("arrived", report.arrived ? 1 : 0);
            ctx.Metric("seconds", report.seconds);
            ctx.Metric("distance_m", report.distanceM);
            ctx.Metric("max_kmh", report.maxKmh);
            ctx.Metric("gears_used", report.gearsUsed);
            ctx.Metric("gears_pitch_checked", report.gears.Count);
            ctx.Metric("underruns", report.underruns);
            ctx.Metric("underruns_across_shots", report.underrunsAcrossShots);
            ctx.Metric("coast_events", report.coastEvents);
            ctx.Metric("tracking_rms_rpm", report.trackingRmsRpm);
            ctx.Metric("frame_p95_ms", report.frameP95Ms);
            ctx.Metric("frame_p50_ms", report.frameP50Ms);
            File.WriteAllText(Path.Combine(ctx.OutDir, "route_report.json"), JsonUtility.ToJson(report, true));
            ctx.Evidence(Path.Combine(ctx.OutDir, "route_report.json"));
            yield return ctx.Screenshot("route9_fuel");
            foreach (var f in report.failures) ctx.Fail(f);
            PlayerVehicle.ScriptedInput = false;
        }

        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        static float RouteLength(List<Vector3> path)
        {
            float d = 0;
            for (int i = 1; i < path.Count; i++) d += Flat(path[i - 1], path[i]);
            return d;
        }

        static int SampleAt(List<(float t, float rpm, int gear, int sampleIndex)> s, float t)
        {
            for (int i = 0; i < s.Count; i++) if (s[i].t >= t) return s[i].sampleIndex;
            return s.Count > 0 ? s[s.Count - 1].sampleIndex : 0;
        }

        static Vector3 PointAhead(List<Vector3> path, int seg, Vector3 pos, float look)
        {
            float left = look - Flat(pos, path[Mathf.Min(seg + 1, path.Count - 1)]);
            int i = seg + 1;
            while (left > 0 && i < path.Count - 1) { left -= Flat(path[i], path[i + 1]); i++; }
            return path[Mathf.Min(i, path.Count - 1)];
        }

        /// <summary>The largest heading change (degrees) over the next <paramref name="ahead"/> metres of path.</summary>
        static float BendAhead(List<Vector3> path, int seg, Vector3 pos, float ahead)
        {
            float d = 0, worst = 0;
            for (int i = seg + 1; i < path.Count - 1 && d < ahead; i++)
            {
                var a = new Vector2(path[i].x - path[i - 1].x, path[i].z - path[i - 1].z);
                var b = new Vector2(path[i + 1].x - path[i].x, path[i + 1].z - path[i].z);
                if (a.sqrMagnitude > 1e-4f && b.sqrMagnitude > 1e-4f) worst = Mathf.Max(worst, Mathf.Abs(Vector2.SignedAngle(a, b)));
                d += b.magnitude;
            }
            return worst;
        }

        static float NearestNodeDistance(RoadNetworkAsset net, Vector3 p, bool flip)
        {
            float best = float.MaxValue;
            foreach (var n in net.nodes)
            {
                var q = flip ? new Vector3(-n.position.x, n.position.y, n.position.z) : n.position;
                best = Mathf.Min(best, Flat(p, q));
            }
            return best;
        }

        /// <summary>Dijkstra over the graph's edges from the node nearest <paramref name="from"/> to the node nearest
        /// <paramref name="to"/>; returns the centreline points along the way (each edge in travel direction).</summary>
        static List<Vector3> PlanRoute(RoadNetworkAsset net, Func<Vector3, Vector3> pos, Vector3 from, Vector3 to, out int nodeCount)
        {
            int Nearest(Vector3 p)
            {
                int bi = -1; float bd = float.MaxValue;
                for (int i = 0; i < net.nodes.Length; i++) { float d = Flat(p, pos(net.nodes[i].position)); if (d < bd) { bd = d; bi = i; } }
                return bi;
            }
            var idToIndex = new Dictionary<int, int>();
            for (int i = 0; i < net.nodes.Length; i++) idToIndex[net.nodes[i].id] = i;
            int src = Nearest(from), dst = Nearest(to);
            var dist = Enumerable.Repeat(float.MaxValue, net.nodes.Length).ToArray();
            var prevEdge = Enumerable.Repeat(-1, net.nodes.Length).ToArray();
            var done = new bool[net.nodes.Length];
            dist[src] = 0;
            for (int k = 0; k < net.nodes.Length; k++)
            {
                int u = -1;
                for (int i = 0; i < net.nodes.Length; i++) if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
                if (u < 0 || dist[u] == float.MaxValue || u == dst) break;
                done[u] = true;
                for (int e = 0; e < net.edges.Length; e++)
                {
                    var ed = net.edges[e];
                    if (!idToIndex.TryGetValue(ed.nodeA, out int a) || !idToIndex.TryGetValue(ed.nodeB, out int b)) continue;
                    int v = a == u ? b : b == u ? a : -1;
                    if (v < 0) continue;
                    float nd = dist[u] + Mathf.Max(0.1f, ed.length);
                    if (nd < dist[v]) { dist[v] = nd; prevEdge[v] = e; }
                }
            }
            var points = new List<Vector3>();
            nodeCount = 0;
            if (dist[dst] == float.MaxValue) return points;
            var chain = new List<(int edge, bool forward)>();
            for (int v = dst; v != src;)
            {
                var ed = net.edges[prevEdge[v]];
                int a = idToIndex[ed.nodeA], b = idToIndex[ed.nodeB];
                bool forward = b == v;   // travelled A -> B
                chain.Add((prevEdge[v], forward));
                v = forward ? a : b;
            }
            chain.Reverse();
            nodeCount = chain.Count + 1;
            points.Add(from);
            foreach (var (e, forward) in chain)
            {
                var pts = net.edges[e].points;
                if (pts == null) continue;
                for (int i = 0; i < pts.Length; i++) points.Add(pos(pts[forward ? i : pts.Length - 1 - i]));
            }
            return points;
        }
    }
}
