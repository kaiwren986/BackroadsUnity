// p01_generic.h: the generic piston-engine builder of research 01's probe (probes/p01/bench.cpp buildEngine), with
// geometry and flow numbers from an upstream cross-plane V8 example script (per cylinder). v8_cross_a and i4_synth
// are two specs of it.
#pragma once

#include "builders.h"

#include <string>
#include <vector>

namespace esp {
namespace p01 {

struct CylSpec {
    int bank;
    int journal;
    double firingDeg;
    double primaryLen;  // m
    int exhaust;
};

struct Spec {
    std::string name;
    std::vector<double> bankAnglesDeg;
    std::vector<double> journalAnglesDeg;
    std::vector<CylSpec> cyl;  // grouped by bank, in bank order
    double tdcDeg = 0;
    std::vector<double> exhaustLengthIn;
    double simFreq = 10000;
    double redlineRpm = 6500, revLimitRpm = 6800;
};

Spec specInline(int n, double simFreq);
Spec specCrossPlaneV8(double simFreq);

// Engine + the probe's vehicle (1,614 kg) and 6-speed transmission.
bool build(const Spec &spec, const std::string &esDir, EngineBundle &out);

} // namespace p01
} // namespace esp
