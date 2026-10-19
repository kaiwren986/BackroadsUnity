// i4_synth: the synthetic inline four of research 01's probe (probes/p01/bench.cpp specInline(4, 10000)), the
// hatch's placeholder until a scripted engine ships (PLAN 3.10).
#include "p01_generic.h"

namespace esp {

bool buildI4Synth(const std::string &esDir, EngineBundle &out) {
    if (!p01::build(p01::specInline(4, 10000), esDir, out)) return false;
    out.peakTorqueHint = 360.0;
    out.idleRpmHint = 750.0;
    return true;
}

} // namespace esp
