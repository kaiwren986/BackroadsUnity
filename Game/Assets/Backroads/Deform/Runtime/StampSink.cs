using UnityEngine;

namespace Backroads.Deform
{
    /// <summary>
    /// M2 contract between the vehicles (lane V, which call it) and the deformation system (lane W, which implements it).
    /// Arguments are exactly the browser game's world.js calls, in JS world coordinates (x, z as in world.js; not
    /// flipped), so CanonData/v1/reference/deform/calls.json replays through the same entry points:
    ///   rut(x, z, radius, depth, rate, wearAdd, berm = 0.45, fx = 0, fz = 0)   world.js:137
    ///   brush(x, z, radius, amount)                                          world.js:187
    ///   relax(x, z, radius, iters = 2, maxDiff = 0.36)                       world.js:370
    /// The implementation quantises to RutStamp (06 §5.6, 18 B) before applying, so every machine applies the same
    /// integers. Calls come from the simulation thread in step order.
    /// </summary>
    public interface IStampSink
    {
        void Rut(float x, float z, float radius, float depth, float rate, float wearAdd, float berm = 0.45f, float fx = 0f, float fz = 0f);
        void Brush(float x, float z, float radius, float amount);
        void Relax(float x, float z, float radius, int iters = 2, float maxDiff = 0.36f);
    }

    /// <summary>The active sink; null (no deformation) until lane W's system registers one.</summary>
    public static class StampSinks
    {
        public static IStampSink Current { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;
    }
}
