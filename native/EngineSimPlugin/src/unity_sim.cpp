// unity_sim.cpp: see unity_sim.h.
#include "unity_sim.h"

#include "units.h"

#include <cmath>
#include <new>

namespace esp {

void UnitySim::attach(bool audio) {
    if (m_attached) return;
    m_attached = true;
    m_audio = audio;

    m_system->removeConstraint(&m_vehicleDrag);  // our load (or none) instead of engine-sim's road model
    m_pd.crank = &m_engine->getOutputCrankshaft()->m_body;
    m_pd.enabled = false;
    m_wheelLoad.proxy = &m_vehicleMass;
    m_wheelLoad.mode = WheelLoad::Off;
    m_system->addForceGenerator(&m_pd);
    m_system->addForceGenerator(&m_wheelLoad);

    // Exhaust delay filters at the engine's own frequency (placeAndInitialize() used 10 kHz). Destroying in place
    // frees the old history (DelayFilter::initialize would leak it).
    const int cylinders = m_engine->getCylinderCount();
    const double f = static_cast<double>(getSimulationFrequency());
    for (int i = 0; i < cylinders; ++i) {
        Piston *piston = m_engine->getChamber(i)->getPiston();
        CylinderHead *head = m_engine->getChamber(i)->getCylinderHead();
        ExhaustSystem *exhaust = head->getExhaustSystem(piston->getCylinderIndex());
        const double length = head->getHeaderPrimaryLength(piston->getCylinderIndex()) + exhaust->getLength();
        const double delay = length / (343.0 * units::m / units::sec);
        m_delayFilters[i].~DelayFilter();
        new (&m_delayFilters[i]) DelayFilter();
        m_delayFilters[i].initialize(delay, f);
    }
}

void UnitySim::freeSynthesizerBuffers() {
    Synthesizer &s = synthesizer();
    if (s.m_inputChannels != nullptr) {
        for (int i = 0; i < s.m_inputChannelCount; ++i) {
            delete[] s.m_inputChannels[i].transferBuffer;  // Synthesizer::destroy() never frees these
            s.m_inputChannels[i].transferBuffer = nullptr;
        }
        s.destroy();
    }
}

void UnitySim::reinitSynthesizer(int outputRate, const Synthesizer::AudioParameters &params) {
    freeSynthesizerBuffers();
    Synthesizer::Parameters p;
    p.inputChannelCount = m_engine->getExhaustSystemCount();
    p.inputBufferSize = outputRate;  // one second of output-rate input; a batch is at most 0.25 s
    p.audioBufferSize = 16;          // engine-sim's int16 output ring is not used (renderSync -> our float ring)
    p.inputSampleRate = static_cast<float>(getSimulationFrequency());
    p.audioSampleRate = static_cast<float>(outputRate);
    p.initialAudioParameters = params;
    synthesizer().initialize(p);
    synthesizer().endInputBlock();   // m_latency is uninitialised until the first endInputBlock() (01 3.3)
    setTargetSynthesizerLatency(0.0);
}

void UnitySim::disableSynthesizer() {
    synthesizer().endInputBlock();  // sets m_latency = 0 (no input yet) before the channels go away
    freeSynthesizerBuffers();
    setTargetSynthesizerLatency(0.0);
}

void UnitySim::teardown() {
    freeSynthesizerBuffers();
    destroy();
}

void UnitySim::writeToSynthesizer() {
    if (!m_audio) return;

    const int exhaustSystemCount = m_engine->getExhaustSystemCount();
    for (int i = 0; i < exhaustSystemCount; ++i) m_exhaustFlowStagingBuffer[i] = 0;

    const double attenuation = std::min(std::abs(filteredEngineSpeed()), 40.0) / 40.0;
    const double attenuation3 = attenuation * attenuation * attenuation;

    // Same as PistonEngineSimulator::writeToSynthesizer (piston_engine_simulator.cpp:370-413) without
    // `static double lastValveLift[8]`, which was written and never read.
    const int cylinderCount = m_engine->getCylinderCount();
    for (int i = 0; i < cylinderCount; ++i) {
        Piston *piston = m_engine->getPiston(i);
        CylinderBank *bank = piston->getCylinderBank();
        CylinderHead *head = m_engine->getHead(bank->getIndex());
        ExhaustSystem *exhaust = head->getExhaustSystem(piston->getCylinderIndex());
        CombustionChamber *chamber = m_engine->getChamber(i);

        const double exhaustLength = head->getHeaderPrimaryLength(piston->getCylinderIndex()) + exhaust->getLength();
        const double exhaustFlow =
            attenuation3 * 1600 *
            (1.0 * (chamber->m_exhaustRunnerAndPrimary.pressure() - units::pressure(1.0, units::atm)) +
             0.1 * chamber->m_exhaustRunnerAndPrimary.dynamicPressure(1.0, 0.0) +
             0.1 * chamber->m_exhaustRunnerAndPrimary.dynamicPressure(-1.0, 0.0));

        const double delayedExhaustPulse = m_delayFilters[i].fast_f(exhaustFlow);
        m_exhaustFlowStagingBuffer[exhaust->getIndex()] +=
            head->getSoundAttenuation(piston->getCylinderIndex()) *
            (exhaust->getAudioVolume() * delayedExhaustPulse / cylinderCount) * (1 / (exhaustLength * exhaustLength));
    }

    synthesizer().writeInput(m_exhaustFlowStagingBuffer);
}

} // namespace esp
