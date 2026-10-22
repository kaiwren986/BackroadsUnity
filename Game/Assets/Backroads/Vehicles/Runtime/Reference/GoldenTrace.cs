using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Backroads.Deform;
using Backroads.World;
using Unity.Mathematics;

namespace Backroads.Vehicles.Reference
{
    /// <summary>A golden-trace input file (tools/oracle/traces/&lt;id&gt;.json): the open-loop channels and key hits.</summary>
    public sealed class GoldenTraceInput
    {
        public string id, vehicle, about, sha256;
        public double startX, startZ, startYaw;
        public int steps, sampleEvery = 12;
        public double[] throttle, brake, steer, handbrake;
        public readonly List<(int step, string key)> hits = new List<(int, string)>();

        public static GoldenTraceInput Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var o = MiniJson.O(MiniJson.Parse(Encoding.UTF8.GetString(bytes)));
            var t = new GoldenTraceInput
            {
                id = MiniJson.S(o, "id"), vehicle = MiniJson.S(o, "vehicle"), about = MiniJson.S(o, "about"),
                steps = (int)MiniJson.D(o, "steps"), sha256 = Sha256(bytes),
            };
            var se = MiniJson.D(o, "sampleEvery");
            if (!double.IsNaN(se)) t.sampleEvery = (int)se;
            var start = MiniJson.O(MiniJson.Get(o, "start"));
            t.startX = MiniJson.D(start, "x"); t.startZ = MiniJson.D(start, "z"); t.startYaw = MiniJson.D(start, "yaw");
            var ch = MiniJson.O(MiniJson.Get(o, "channels"));
            t.throttle = Expand(ch, "throttle", t.steps);
            t.brake = Expand(ch, "brake", t.steps);
            t.steer = Expand(ch, "steer", t.steps);
            t.handbrake = Expand(ch, "handbrake", t.steps);
            if (MiniJson.Get(o, "hits") is List<object> hits)
                foreach (var h in hits)
                {
                    var ho = MiniJson.O(h);
                    t.hits.Add(((int)MiniJson.D(ho, "step"), MiniJson.S(ho, "key")));
                }
            return t;
        }

        /// <summary>Run-length [value, steps] to one value per step (goldenTraces.js expand).</summary>
        static double[] Expand(Dictionary<string, object> ch, string name, int steps)
        {
            var o = new double[steps];
            int k = 0;
            foreach (var run in MiniJson.A(MiniJson.Get(ch, name)))
            {
                var r = MiniJson.A(run);
                double v = MiniJson.D(r[0]);
                int n = (int)MiniJson.D(r[1]);
                for (int i = 0; i < n && k < steps; i++) o[k++] = v;
            }
            if (k != steps) throw new FormatException($"channel {name} covers {k} of {steps} steps");
            return o;
        }

        public static string Sha256(byte[] b)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(b);
            var sb = new StringBuilder(64);
            foreach (var x in h) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>Samples of a trace: the oracle's (CanonData/v1/reference/traces) or a C# replay, same fields.</summary>
    public sealed class GoldenTraceSamples
    {
        public static readonly string[] Fields =
        {
            "step", "t", "px", "py", "pz", "qx", "qy", "qz", "qw", "vx", "vy", "vz", "wx", "wy", "wz",
            "rpm", "gear", "mudSink", "speed", "fwdSpeed", "steerAngle", "lowRange", "stuckT", "contacts",
        };
        public const int Step = 0, T = 1, Px = 2, Py = 3, Pz = 4, Rpm = 15, Gear = 16, MudSink = 17, Speed = 18, FwdSpeed = 19, LowRange = 21, StuckT = 22, Contacts = 23;

        public string id, vehicle, inputSha256;
        public readonly List<double[]> rows = new List<double[]>();
        public double winchAnchorX = double.NaN, winchAnchorY = double.NaN, winchAnchorZ = double.NaN, winchLenAtHook = double.NaN;
        public int winchHookStep = -1;
        public double finalWinchLen = double.NaN, finalWinchTension = double.NaN, finalFuel = double.NaN, finalDirt = double.NaN;

        public static GoldenTraceSamples LoadReference(string path)
        {
            var o = MiniJson.O(MiniJson.Parse(File.ReadAllText(path)));
            var t = new GoldenTraceSamples { id = MiniJson.S(o, "id"), vehicle = MiniJson.S(o, "vehicle"), inputSha256 = MiniJson.S(o, "inputSha256") };
            var fields = MiniJson.A(MiniJson.Get(o, "fields"));
            var map = new int[Fields.Length];
            for (int f = 0; f < Fields.Length; f++)
            {
                map[f] = fields.IndexOf(Fields[f]);
                if (map[f] < 0) throw new FormatException($"{path}: no field {Fields[f]}");
            }
            foreach (var row in MiniJson.A(MiniJson.Get(o, "samples")))
            {
                var r = MiniJson.A(row);
                var v = new double[Fields.Length];
                for (int f = 0; f < Fields.Length; f++) v[f] = MiniJson.D(r[map[f]]);
                t.rows.Add(v);
            }
            if (MiniJson.Get(o, "events") is List<object> evs)
                foreach (var e in evs)
                {
                    var eo = MiniJson.O(e);
                    if (MiniJson.Get(eo, "winch") is Dictionary<string, object> wv)
                    {
                        var a = MiniJson.A(MiniJson.Get(wv, "anchor"));
                        t.winchAnchorX = MiniJson.D(a[0]); t.winchAnchorY = MiniJson.D(a[1]); t.winchAnchorZ = MiniJson.D(a[2]);
                        t.winchLenAtHook = MiniJson.D(wv, "len");
                        t.winchHookStep = (int)MiniJson.D(eo, "step");
                    }
                }
            if (MiniJson.Get(o, "final") is Dictionary<string, object> fin)
            {
                if (MiniJson.Get(fin, "winch") is Dictionary<string, object> fw)
                {
                    t.finalWinchLen = MiniJson.D(fw, "len");
                    t.finalWinchTension = MiniJson.D(fw, "tension");
                }
                t.finalFuel = MiniJson.D(fin, "fuel");
                t.finalDirt = MiniJson.D(fin, "dirt");
            }
            return t;
        }

        /// <summary>
        /// The samples in the oracle's trace format (what <see cref="LoadReference"/> reads), values to 9 significant
        /// digits: the Unity self-trace baselines [S17] are written this way.
        /// </summary>
        public string ToJson(string about = null)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => double.IsNaN(v) ? "null" : v.ToString("G9", ci);
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"id\": \"").Append(id).Append("\",\n  \"vehicle\": \"").Append(vehicle).Append("\",\n  \"inputSha256\": \"").Append(inputSha256).Append("\",\n");
            if (about != null) sb.Append("  \"about\": \"").Append(about.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\n");
            sb.Append("  \"fields\": [").Append(string.Join(", ", Array.ConvertAll(Fields, f => "\"" + f + "\""))).Append("],\n  \"samples\": [\n");
            for (int i = 0; i < rows.Count; i++)
            {
                sb.Append("    [").Append(string.Join(", ", Array.ConvertAll(rows[i], N))).Append(i + 1 < rows.Count ? "],\n" : "]\n");
            }
            sb.Append("  ],\n  \"events\": [");
            if (winchHookStep >= 0)
                sb.Append("{\"step\": ").Append(winchHookStep).Append(", \"winch\": {\"anchor\": [").Append(N(winchAnchorX)).Append(", ").Append(N(winchAnchorY)).Append(", ")
                  .Append(N(winchAnchorZ)).Append("], \"len\": ").Append(N(winchLenAtHook)).Append("}}");
            sb.Append("],\n  \"final\": {");
            if (!double.IsNaN(finalWinchLen)) sb.Append("\"winch\": {\"len\": ").Append(N(finalWinchLen)).Append(", \"tension\": ").Append(N(finalWinchTension)).Append("}, ");
            sb.Append("\"fuel\": ").Append(N(finalFuel)).Append(", \"dirt\": ").Append(N(finalDirt)).Append("}\n}\n");
            return sb.ToString();
        }

        public double At(int row, int field) => rows[row][field];

        /// <summary>First time a field reaches a value (linear interpolation between samples), or NaN.</summary>
        public double CrossingTime(int field, double value, double scale = 1)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                double a = rows[i - 1][field] * scale, b = rows[i][field] * scale;
                if (a < value && b >= value) return rows[i - 1][T] + (rows[i][T] - rows[i - 1][T]) * (value - a) / (b - a);
                if (i == 1 && a >= value) return rows[0][T];
            }
            return double.NaN;
        }

        /// <summary>Ground-plane path length between two sample rows.</summary>
        public double PathLength(int from, int to)
        {
            double d = 0;
            for (int i = from + 1; i <= to && i < rows.Count; i++)
                d += Math.Sqrt(Sq(rows[i][Px] - rows[i - 1][Px]) + Sq(rows[i][Pz] - rows[i - 1][Pz]));
            return d;
        }

        static double Sq(double x) => x * x;

        /// <summary>The row whose step is the first at or after <paramref name="step"/>.</summary>
        public int RowAtOrAfter(int step)
        {
            for (int i = 0; i < rows.Count; i++) if (rows[i][Step] >= step) return i;
            return rows.Count - 1;
        }
    }

    /// <summary>
    /// Replays a golden trace in C# the way tools/oracle/goldenTraces.js recorded it: boot (every vehicle placed on its
    /// garage bay in VEHICLE_ORDER, settled and parked, the player in the pickup), then the trace vehicle placed at the
    /// start and settled, then the open-loop channels through vehicleControls and one fixed step per frame, sampling
    /// every 0.1 s. No AI traffic; the dozer is not built in M2 (it is parked at bay 4 in the browser game, out of
    /// every trace's way).
    /// </summary>
    public static class GoldenTraceReplay
    {
        public sealed class Options
        {
            public BrvWorldData World;
            public PlacementSet.Parking[] Parking;
            /// <summary>Where the ruts go (null: no deformation).</summary>
            public IStampSink Sink;
            public bool UseBurst = true;
            /// <summary>Called after every step with the trace vehicle's state (diagnostics).</summary>
            public Action<int, BrvState> OnStep;
            /// <summary>
            /// Called once the boot is done (every vehicle placed and settled), before the trace's first step: attach a
            /// PhysX chassis here (T3.6). The returned action, if any, runs after every fleet step (the physics step).
            /// </summary>
            public Func<BrvFleet, Action> Attach;
        }

        public static GoldenTraceSamples Run(GoldenTraceInput input, Options opt)
        {
            using var fleet = new BrvFleet(opt.World) { UseBurst = opt.UseBurst, Sink = opt.Sink, NoDeformation = opt.Sink == null };
            // boot: VEHICLE_ORDER on the parking bays, each placed, settled and parked
            foreach (var id in ClassicVehicleDefs.M2) fleet.Add(ClassicVehicleDefs.Get(id));
            for (int i = 0; i < fleet.Count; i++)
            {
                int bay = Array.IndexOf(ClassicVehicleDefs.Order, fleet.Classic(i).id);
                var spot = opt.Parking[bay];
                fleet.Place(i, spot.x, spot.z, spot.yaw);
                fleet.Settle(i);
                fleet.Park(i);
            }
            // enterVehicle(G.vehicles[0]): the pickup
            int pickup = fleet.IndexOf("pickup");
            {
                ref var p = ref fleet.State(pickup);
                BrvSim.Wake(ref p);
                p.driven = true;
                p.engineOn = p.fuel > 0;
                p.lightsOn = false;
            }
            int v = fleet.IndexOf(input.vehicle);
            if (v < 0) throw new ArgumentException($"no vehicle {input.vehicle}");
            if (v != pickup)
            {
                fleet.Park(pickup);
                fleet.State(pickup).engineOn = false;
            }
            fleet.Place(v, input.startX, input.startZ, input.startYaw);
            fleet.Settle(v);
            {
                ref var s = ref fleet.State(v);
                BrvSim.Wake(ref s);
                s.driven = true; s.engineOn = true; s.lightsOn = false; s.fuel = 1;
            }

            var afterStep = opt.Attach?.Invoke(fleet);
            var o = new GoldenTraceSamples { id = input.id, vehicle = input.vehicle, inputSha256 = input.sha256 };
            Sample(o, 0, fleet.State(v));
            var hits = new Dictionary<int, List<string>>();
            foreach (var (step, key) in input.hits)
            {
                if (!hits.TryGetValue(step, out var l)) hits[step] = l = new List<string>();
                l.Add(key);
            }
            for (int k = 0; k < input.steps; k++)
            {
                var inp = new BrvInput
                {
                    active = true, ignition = true,
                    fwd = input.throttle[k], back = input.brake[k], steer = input.steer[k], hand = input.handbrake[k] > 0.5 ? 1 : 0,
                };
                if (hits.TryGetValue(k, out var keys))
                    foreach (var key in keys)
                    {
                        if (key == "KeyX") inp.lowRangePresses++;
                        else if (key == "KeyG") inp.winchPresses++;
                        else if (key == "KeyL") inp.lightsPresses++;
                        else if (key == "KeyR") inp.resetPresses++;
                    }
                fleet.SetInput(v, inp);
                bool hadWinch = fleet.State(v).winch.active;
                fleet.Step();
                afterStep?.Invoke();
                for (int i = 0; i < fleet.Count; i++) fleet.State(i).impact = 0;
                ref var s = ref fleet.State(v);
                if (!hadWinch && s.winch.active && o.winchHookStep < 0)
                {
                    o.winchHookStep = k;
                    o.winchAnchorX = s.winch.anchor.x; o.winchAnchorY = s.winch.anchor.y; o.winchAnchorZ = s.winch.anchor.z;
                    o.winchLenAtHook = s.winch.len;
                }
                opt.OnStep?.Invoke(k, s);
                if ((k + 1) % input.sampleEvery == 0) Sample(o, k + 1, s);
            }
            var fs = fleet.State(v);
            if (fs.winch.active) { o.finalWinchLen = fs.winch.len; o.finalWinchTension = fs.winch.tension; }
            o.finalFuel = fs.fuel;
            o.finalDirt = fs.dirt;
            return o;
        }

        static void Sample(GoldenTraceSamples o, int k, in BrvState v)
        {
            int contacts = 0;
            for (int i = 0; i < v.wheels.Length; i++) if (v.wheels[i].contact) contacts++;
            o.rows.Add(new double[]
            {
                k, k * BrvSim.STEP, v.pos.x, v.pos.y, v.pos.z, v.quat.x, v.quat.y, v.quat.z, v.quat.w, v.vel.x, v.vel.y, v.vel.z,
                v.angVel.x, v.angVel.y, v.angVel.z, v.rpm, v.gear, v.mudSink, v.speed, v.fwdSpeed, v.steerAngle, v.lowRange ? 1 : 0, v.stuckT, contacts,
            });
        }
    }
}
