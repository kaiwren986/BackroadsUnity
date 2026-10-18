#pragma once

#include <string>

class Engine;
class Vehicle;
class Transmission;
class Simulator;

struct EngineRig {
    Engine *engine = nullptr;
    Vehicle *vehicle = nullptr;
    Transmission *transmission = nullptr;
    Simulator *simulator = nullptr;
    int impulseResponseSamples = 0;
};

struct RigOptions {
    int simulationFrequency = 0;   // 0 = engine's own value from the .mr script (20000 Hz for the EJ25)
    int fluidSimulationSteps = 8;  // Engine::createSimulator() default
    bool loadImpulseResponse = true;
};

// Hand-built C++ equivalent of assets/engines/atg-video-2/01_subaru_ej25_eh.mr
// (the default engine loaded by assets/main.mr), following the exact construction
// order of es_script::EngineNode::buildEngine() and the es/*.mr library defaults.
EngineRig buildSubaruEj25(const std::string &engineSimRoot, const RigOptions &options);
void destroyRig(EngineRig *rig);
