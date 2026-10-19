using System;

namespace Backroads.EngineSim
{
    /// <summary>
    /// The M2 rpm map of PLAN 3.5: the vehicle model's (classic JS engine's) rpm onto an engine-sim engine's rpm range,
    /// rpm_es = idle_es + (rpm_js - idle_js) * (0.97 * redline_es - idle_es) / (redline_js - idle_js).
    /// Below the JS idle (the engine starting or stopping) the map is proportional instead, rpm_es = rpm_js / idle_js *
    /// idle_es, so 0 stays 0 and the two pieces meet at idle. The result never exceeds the engine's rev limit.
    /// </summary>
    public readonly struct RpmMap
    {
        public const float RedlineShare = 0.97f;

        public readonly float IdleJs, RedlineJs, IdleEs, RedlineEs, MaxEs;

        public RpmMap(float idleJs, float redlineJs, float idleEs, float redlineEs, float maxEs = 0f)
        {
            IdleJs = Math.Max(1f, idleJs);
            RedlineJs = Math.Max(IdleJs + 1f, redlineJs);
            IdleEs = Math.Max(1f, idleEs);
            RedlineEs = Math.Max(IdleEs + 1f, redlineEs);
            MaxEs = maxEs > 0f ? maxEs : RedlineEs * 1.05f;
        }

        /// <summary>d rpm_es / d rpm_js above idle.</summary>
        public float Slope => (RedlineShare * RedlineEs - IdleEs) / (RedlineJs - IdleJs);

        public float Map(float rpmJs)
        {
            if (!(rpmJs > 0f)) return 0f;
            float es = rpmJs >= IdleJs ? IdleEs + (rpmJs - IdleJs) * Slope : rpmJs / IdleJs * IdleEs;
            return Math.Min(es, MaxEs);
        }

        public override string ToString() =>
            $"js {IdleJs:F0}-{RedlineJs:F0} rpm -> engine-sim {IdleEs:F0}-{RedlineShare * RedlineEs:F0} rpm (x{Slope:F3})";
    }
}
