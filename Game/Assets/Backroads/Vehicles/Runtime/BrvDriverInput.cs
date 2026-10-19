using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// The driver's input for one fixed step, as main.js vehicleControls() reads it: pedals and steering as levels,
    /// one-shot keys as the number of presses since the previous step (from DriveInput's press counters).
    /// </summary>
    public struct BrvInput
    {
        /// <summary>This vehicle is driven by the player this step (vehicleControls runs only for the player's vehicle).</summary>
        public bool active;
        public double fwd, back, steer, hand;
        public bool horn;
        /// <summary>Unity addition (tap I): when false the engine stays off; the JS starts it whenever there is fuel.</summary>
        public bool ignition;
        public int lightsPresses, resetPresses, lowRangePresses, winchPresses;
    }

    /// <summary>What vehicleControls() reported this step, for the HUD and toasts.</summary>
    public struct BrvControlEvents
    {
        public int lowRangeToggled, winchHooked, winchReleased, winchNoAnchor, resets, resetRefused;
    }

    public static partial class BrvSim
    {
        /// <summary>
        /// main.js vehicleControls(v, dt) minus the camera, tow, jobs and get-out keys: drive(), horn, L lights,
        /// R reset (below 4 m/s), X low range, G winch (findAnchor + toggleWinch), the winch reel on throttle, and the
        /// engine on whenever there is fuel.
        /// </summary>
        public static void Controls(ref BrvState v, in BrvDef d, in BrvWorld w, in BrvInput input, double dt, int self,
                                    NativeArray<BrvState> fleet, NativeArray<BrvDef> defs, int fleetCount, ref BrvControlEvents ev)
        {
            Drive(ref v, d, input.fwd, input.back, input.steer, input.hand, dt);
            v.horn = input.horn;
            for (int i = 0; i < input.lightsPresses; i++) v.lightsOn = !v.lightsOn;
            for (int i = 0; i < input.resetPresses; i++)
            {
                if (v.speed < 4) { v.winch = new BrvWinch { target = -1 }; ResetUpright(ref v, d, w); ev.resets++; }
                else ev.resetRefused++;
            }
            if (!d.simple)
                for (int i = 0; i < input.lowRangePresses; i++) { v.lowRange = !v.lowRange; ev.lowRangeToggled++; }
            for (int i = 0; i < input.winchPresses; i++) ToggleWinch(ref v, d, w, self, fleet, defs, fleetCount, ref ev);
            if (v.winch.active) v.winch.reel = input.fwd > 0.1;
            if (v.fuel <= 0 || !input.ignition) v.engineOn = false;
            else if (!v.engineOn) v.engineOn = true;
        }

        /// <summary>main.js toggleWinch(v).</summary>
        public static void ToggleWinch(ref BrvState v, in BrvDef d, in BrvWorld w, int self, NativeArray<BrvState> fleet,
                                       NativeArray<BrvDef> defs, int fleetCount, ref BrvControlEvents ev)
        {
            if (v.winch.active) { v.winch = new BrvWinch { target = -1 }; ev.winchReleased++; return; }
            if (!FindAnchor(v, d, w, self, fleet, fleetCount, out var ax, out var ay, out var az, out var ar, out int target))
            {
                ev.winchNoAnchor++;
                return;
            }
            // hook the cable round the near side of a tree, not its centre
            double3 hook = WinchHook(v, d);
            double dx = hook.x - ax, dz = hook.z - az, dl = Hypot(dx, dz);
            if (dl == 0) dl = 1;
            var pt = new double3(ax + (dx / dl) * ar, ay, az + (dz / dl) * ar);
            AttachWinch(ref v, d, pt, target, fleet);
            ev.winchHooked++;
        }

        /// <summary>
        /// main.js findAnchor(v): the best standing tree, rock, lamp or post within 30 m of the hook (preferring what is
        /// ahead of the bumper), or another vehicle of the fleet.
        /// </summary>
        public static bool FindAnchor(in BrvState v, in BrvDef d, in BrvWorld w, int self, NativeArray<BrvState> fleet, int fleetCount,
                                      out double bx, out double by, out double bz, out double br, out int btarget)
        {
            double3 hook = WinchHook(v, d);
            double fx = v.ez.x, fz = v.ez.z, fl = Hypot(fx, fz);
            if (fl == 0) fl = 1;
            bool found = false;
            double bestScore = double.PositiveInfinity;
            bx = by = bz = br = 0; btarget = -1;
            if (w.HasColliders)
            {
                var buf = new FixedList4096Bytes<int>();
                QueryColliders(w, hook.x, hook.z, 32, ref buf);
                for (int i = 0; i < buf.Length; i++)
                {
                    int ci = buf[i];
                    var c = w.colliders[ci];
                    bool down = w.treeDown.IsCreated && w.treeDown[ci] != 0;
                    double cr = down ? 0 : c.r;
                    if (c.type != 0 || cr <= 0) continue;
                    if (c.kind == 0 && (c.tree < 0 || down)) continue;
                    if (c.kind > 3) continue; // tree, rock, lamp, post
                    double cy1 = down ? c.y0 : c.y1;
                    Consider(hook, fx, fz, fl, c.x, Math.Min(cy1, c.y0 + 1.6), c.z, cr, -1, ref found, ref bestScore, ref bx, ref by, ref bz, ref br, ref btarget);
                }
            }
            for (int o = 0; o < fleetCount; o++)
            {
                if (o == self) continue;
                var p = fleet[o].pos;
                Consider(hook, fx, fz, fl, p.x, p.y, p.z, 1.2, o, ref found, ref bestScore, ref bx, ref by, ref bz, ref br, ref btarget);
            }
            return found;
        }

        static void Consider(double3 hook, double fx, double fz, double fl, double x, double y, double z, double r, int target,
                             ref bool found, ref double bestScore, ref double bx, ref double by, ref double bz, ref double br, ref int btarget)
        {
            double dx = x - hook.x, dz = z - hook.z;
            double dd = Hypot(dx, dz) - r;
            if (dd > 30 || dd < 1) return;
            double ahead = (dx * fx + dz * fz) / (fl * Math.Max(dd, 0.1));
            double score = dd - ahead * 6;
            if (score < bestScore) { bestScore = score; found = true; bx = x; by = y; bz = z; br = r; btarget = target; }
        }
    }
}
