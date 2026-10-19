// builders.h: C++ engine builders under fictional ids (PLAN 3.2 "If you say no", 3.10 [C2]).
//
//   f4_boxer_a  a flat-four, line-by-line port of an engine-sim example script (probes\p02\src\ej25.cpp; validated
//               against the real engine: 229 N m at 4,500 rpm, 170 hp at 6,500 rpm, research 02 6.2)
//   v8_cross_a  a cross-plane V8 rebuilt from an engine-sim example (probes\p01\bench.cpp specLsV8, 01 App. A.1)
//   i4_synth    the synthetic inline four of the same probe (specInline(4))
// Builders create the Engine, Vehicle and Transmission only; the Instance makes the simulator (UnitySim).
// Every name string here is a fictional id; real names stay in the vendor tree and internal docs.
#pragma once

#include <string>

class Engine;
class Vehicle;
class Transmission;

namespace esp {

struct EngineBundle {
    Engine *engine = nullptr;
    Vehicle *vehicle = nullptr;
    Transmission *transmission = nullptr;  // read for its ratios only; the simulator gets a UnityTransmission
    std::string name;
    double peakTorqueHint = 0.0;  // N m, for the SLAVED_CRANK clamp default; 0 = unknown
    double idleRpmHint = 800.0;
    int sourceKind = 1;           // 1 builtin, 2 script
};

// Builds a builtin. esDir: the es library folder (for sound-library\*.wav). False + err on an unknown name.
bool buildBuiltin(const std::string &name, const std::string &esDir, EngineBundle &out, std::string &err);
// "f4_boxer_a,v8_cross_a,i4_synth"
const char *builtinNameList();

// Frees what a builder or a script made (Functions, camshafts and valvetrains leak as upstream does, 01 7.3).
void destroyBundle(EngineBundle &b);

// Builders (one file each).
bool buildF4BoxerA(const std::string &esDir, EngineBundle &out);
bool buildV8CrossA(const std::string &esDir, EngineBundle &out);
bool buildI4Synth(const std::string &esDir, EngineBundle &out);

} // namespace esp
