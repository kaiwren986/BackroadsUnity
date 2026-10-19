using Backroads.Core;
using UnityEngine;

namespace Backroads.Vehicles
{
    /// <summary>
    /// Feature flags of the vehicle model (until Core has a FeatureFlags type, PLAN 1.5). Everything behind a flag is off
    /// by default; with every flag off the vehicles are exactly BRV V0 (the golden traces do not change).
    /// </summary>
    public static class BrvFeatures
    {
        public const string V2Flag = "-br-brv-v2";

        static bool? s_V2;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_V2 = null;

        /// <summary>
        /// BRV v2 driveline (06 sections 3-4: shafts and PGS rows for wheel spin, engine, clutch, gearbox, differential).
        /// M2 ships only the skeleton (T2.3); nothing in V0 reads it. Off unless -br-brv-v2 is given or a test sets it.
        /// </summary>
        public static bool V2Driveline
        {
            get => s_V2 ??= CommandLine.Has(V2Flag);
            set => s_V2 = value;
        }
    }
}
