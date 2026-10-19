// v8_cross_a: a 5.33 L cross-plane V8 at 10 kHz, research 01's C++ rebuild of an upstream V8 example (01 App. A.1,
// probes/p01/bench.cpp specLsV8). It runs hot (about 715 N m at 4,000 rpm); the piranha run settles script vs
// port (01 9.2, PLAN 3.10).
#include "p01_generic.h"

namespace esp {

bool buildV8CrossA(const std::string &esDir, EngineBundle &out) {
    if (!p01::build(p01::specCrossPlaneV8(10000), esDir, out)) return false;
    out.peakTorqueHint = 720.0;
    out.idleRpmHint = 750.0;
    return true;
}

} // namespace esp
