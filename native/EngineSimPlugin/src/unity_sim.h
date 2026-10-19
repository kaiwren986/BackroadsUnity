// unity_sim.h: UnitySim, engine-sim's PistonEngineSimulator as the plugin needs it (PLAN 3.1, research 01 7.3, 8).
//
// Every fix lives here, never in the vendored tree:
//   - writeToSynthesizer() without the shared, out-of-bounds `static double lastValveLift[8]` (defect 1; engines with
//     9-12 cylinders); skipped entirely when the engine has no audio.
//   - the exhaust delay filters re-initialised at the engine's own frequency (upstream hard-codes 10 kHz, defect 7).
//   - the synthesizer re-initialised at the output rate (48 kHz) with input latency 0 (defects 5, 8): the step count
//     per frame is then exactly round(dt * f), and renderSync() drains its input before endFrame().
//   - engine-sim's own road load (m_vehicleDrag) removed; SlavedCrankPD and WheelLoad attached (disabled).
#pragma once

#include "generators.h"

#include "piston_engine_simulator.h"

namespace esp {

class UnitySim final : public PistonEngineSimulator {
public:
    // Call once, right after loadSimulation() and setSimulationFrequency().
    void attach(bool audio);
    // Synthesizer at the output rate; input rate is set per frame by startFrame()/the drift trim.
    void reinitSynthesizer(int outputRate, const Synthesizer::AudioParameters &params);
    // No audio (sweeps, tests): frees the synthesizer and keeps its input latency at 0, so startFrame() never adds
    // or removes steps.
    void disableSynthesizer();
    // Frees what upstream's destroy() forgets (synthesizer buffers), then destroys the simulation.
    void teardown();

    Engine *engine() const { return m_engine; }
    atg_scs::RigidBody &proxy() { return m_vehicleMass; }
    atg_scs::RigidBodySystem *system() { return m_system; }
    SlavedCrankPD &pd() { return m_pd; }
    WheelLoad &wheelLoad() { return m_wheelLoad; }
    bool audio() const { return m_audio; }

protected:
    void writeToSynthesizer() override;

private:
    void freeSynthesizerBuffers();

    SlavedCrankPD m_pd;
    WheelLoad m_wheelLoad;
    bool m_audio = true;
    bool m_attached = false;
};

} // namespace esp
