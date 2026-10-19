using System;

namespace Backroads.EngineSim
{
    public enum EngineSource
    {
        Builtin = 1,  // a C++ builder in EngineSimPlugin.dll (fictional id)
        Script = 2,   // a piranha .mr script (Dev builds: the upstream catalogue; later: sanitised shipped scripts)
    }

    /// <summary>
    /// One engine the game can run (PLAN 2.5, 3.10). Roster engines carry fictional ids only; the upstream scripts with
    /// real names appear in Dev builds only (<see cref="EngineCatalog"/>). From M4 EngineBake fills J_eff, the torque map
    /// and the loop bank; the conformance suite checks idle, dyno peak and real-time factor against these fields.
    /// </summary>
    [Serializable]
    public sealed class EngineDef
    {
        public string id;               // fictional id, e.g. "f4_boxer_a" (scripts in Dev builds: the upstream path)
        public string displayName;      // what the UI shows
        public EngineSource source = EngineSource.Builtin;
        public string builtinName;      // Builtin: the plugin's builder id
        public string scriptPath;       // Script: absolute or relative to the engines folder
        public string topNode;          // Script without main(): set_engine(<topNode>())
        public int cylinders;
        public float idleRpmMin = 600, idleRpmMax = 1000;
        public float redlineRpm = 6500;
        public float peakTorqueNm;      // brief or measured peak, for the load model and the conformance check
        public float peakTorqueRpm;
        /// <summary>
        /// Accepted pitch orders for the PLAN 1.9 pitch rule [T12], as multiples of the cycle frequency rpm/120. Engines
        /// with equal firing intervals list cylinders/k for k in {1, 2, 4, 8} (a detected period of k firing intervals);
        /// cross-plane V8s with two exhausts list the integer cycle harmonics their spectrum peaks at (01 4.3).
        /// </summary>
        public float[] pitchOrders = Array.Empty<float>();
        public bool roster;             // PLAN 3.10 roster: shipped in Client builds
        public bool devOnly;            // never in a Client build
        public float jEffKgm2;          // baked by EngineBake (M4); 0 = not baked yet
        public string notes;

        public override string ToString() => $"{id} ({displayName}, {cylinders} cyl, {source})";

        /// <summary>cylinders/k for k in {1, 2, 4, 8}: the firing-interval rule of PLAN 1.9.</summary>
        public static float[] FiringOrders(int cylinders) =>
            new[] { cylinders / 1f, cylinders / 2f, cylinders / 4f, cylinders / 8f };

        /// <summary>Integer cycle harmonics 1..max.</summary>
        public static float[] CycleHarmonics(int max)
        {
            var o = new float[max];
            for (int i = 0; i < max; i++) o[i] = i + 1;
            return o;
        }
    }
}
