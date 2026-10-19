using System;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Backroads.EngineSim
{
    /// <summary>
    /// How many fixed steps Unity will run in the current frame, computed before them in EarlyUpdate (PLAN 3.4 [T1]):
    /// S = floor((Time.time - Time.fixedTime) / Time.fixedDeltaTime), at most 4 (maximumDeltaTime = 4/120). The result lag
    /// for the frame is L = clamp(S, 2, 4); it changes only here, at a frame boundary.
    /// </summary>
    public static class FixedStepFrame
    {
        public const int MinLag = 2, MaxLag = 4;

        /// <summary>Fixed steps Unity will run this frame (0-4).</summary>
        public static int StepsThisFrame { get; private set; }

        /// <summary>L for this frame: step k uses the result of step k - L.</summary>
        public static int Lag { get; private set; } = MinLag;

        public static int Frame { get; private set; } = -1;

        /// <summary>This frame's unscaled delta time, read before the fixed steps (inside FixedUpdate, Time.unscaledDeltaTime
        /// is the fixed step, not the frame). Feeds EspControls.frame_dt_s: the ring target and the coast threshold.</summary>
        public static float FrameDt { get; private set; }

        public static int LagFor(int stepsThisFrame) => Math.Max(MinLag, Math.Min(MaxLag, stepsThisFrame));

        public static int StepsFor(double time, double fixedTime, double fixedDeltaTime)
        {
            if (fixedDeltaTime <= 0) return 0;
            var s = (int)Math.Floor((time - fixedTime) / fixedDeltaTime + 1e-6);
            return Math.Max(0, Math.Min(MaxLag, s));
        }

        struct Hook { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Install()
        {
            StepsThisFrame = 0;
            Lag = MinLag;
            Frame = -1;
            FrameDt = 0f;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (Insert(ref loop)) PlayerLoop.SetPlayerLoop(loop);
        }

        static bool Insert(ref PlayerLoopSystem root)
        {
            if (root.subSystemList == null) return false;
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type != typeof(EarlyUpdate)) continue;
                var early = root.subSystemList[i];
                var subs = early.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                foreach (var s in subs) if (s.type == typeof(Hook)) return false;  // already there (no domain reload)
                var list = new PlayerLoopSystem[subs.Length + 1];
                list[0] = new PlayerLoopSystem { type = typeof(Hook), updateDelegate = Tick };
                Array.Copy(subs, 0, list, 1, subs.Length);
                early.subSystemList = list;
                root.subSystemList[i] = early;
                return true;
            }
            return false;
        }

        static void Tick()
        {
            Frame = Time.frameCount;
            FrameDt = Time.unscaledDeltaTime;
            StepsThisFrame = StepsFor(Time.timeAsDouble, Time.fixedTimeAsDouble, Time.fixedDeltaTime);
            Lag = LagFor(StepsThisFrame);
        }
    }
}
