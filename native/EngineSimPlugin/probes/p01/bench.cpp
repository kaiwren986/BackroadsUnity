// engine-sim headless probe: builds engines in C++ (no piranha), runs PistonEngineSimulator
// without delta-studio, renders audio synchronously, and measures CPU cost.
#include "engine.h"
#include "piston_engine_simulator.h"
#include "direct_throttle_linkage.h"
#include "standard_valvetrain.h"
#include "camshaft.h"
#include "function.h"
#include "fuel.h"
#include "gas_system.h"
#include "impulse_response.h"
#include "units.h"
#include "constants.h"
#include "transmission.h"
#include "vehicle.h"

#include <chrono>
#include <cstdio>
#include <cstring>
#include <vector>
#include <fstream>
#include <cmath>
#include <string>
#include <algorithm>
#include <utility>

static const char *IR_PATH = "D:/Backroads-ref/engine-sim/es/sound-library/smooth/smooth_39.wav";
static std::string g_outDir = ".";
static bool g_debugClutch = false;

// ---------------------------------------------------------------- WAV I/O
static std::vector<int16_t> loadWav16Mono(const std::string &path) {
    std::ifstream f(path, std::ios::binary);
    std::vector<char> d((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    std::vector<int16_t> out;
    if (d.size() < 12) return out;
    size_t i = 12;
    while (i + 8 <= d.size()) {
        uint32_t sz; std::memcpy(&sz, &d[i + 4], 4);
        if (std::memcmp(&d[i], "data", 4) == 0) {
            out.resize(sz / 2);
            std::memcpy(out.data(), &d[i + 8], (sz / 2) * 2);
            break;
        }
        i += 8 + sz + (sz & 1);
    }
    return out;
}

static void writeWav16Mono(const std::string &path, const std::vector<int16_t> &s, int rate) {
    std::ofstream f(path, std::ios::binary);
    auto w32 = [&](uint32_t v) { f.write((const char *)&v, 4); };
    auto w16 = [&](uint16_t v) { f.write((const char *)&v, 2); };
    f.write("RIFF", 4); w32(36 + (uint32_t)s.size() * 2); f.write("WAVE", 4);
    f.write("fmt ", 4); w32(16); w16(1); w16(1); w32(rate); w32(rate * 2); w16(2); w16(16);
    f.write("data", 4); w32((uint32_t)s.size() * 2); f.write((const char *)s.data(), s.size() * 2);
}

// ---------------------------------------------------------------- helpers
static double deg(double d) { return units::angle(d, units::deg); }

static Function *makeFn(double filterRadius, const std::vector<std::pair<double, double>> &s) {
    Function *f = new Function;
    f->initialize((int)s.size(), filterRadius);
    for (auto &p : s) f->addSample(p.first, p.second);
    return f;
}

// Same math as es_script::GenerateHarmonicCamLobeNode::_evaluate (scripting/include/actions.h)
static Function *harmonicLobe(double dur50, double gamma, double lift, int steps) {
    const double angle = dur50 / 4;
    const double s = std::pow(2 * units::distance(50, units::thou) / lift, 1 / gamma) - 1;
    const double k = std::acos(s) / angle;
    const double extents = constants::pi / k;
    const double step = extents / (steps - 5.0);
    std::vector<std::pair<double, double>> v;
    for (int i = 0; i < steps; ++i) {
        if (i == 0) v.push_back({ 0.0, lift });
        else {
            const double x = i * step;
            const double l = (x >= extents) ? 0.0 : lift * std::pow(0.5 + 0.5 * std::cos(k * x), gamma);
            v.push_back({ x, l });
            v.push_back({ -x, l });
        }
    }
    return makeFn(step, v);
}

struct CylSpec { int bank; int journal; double firingDeg; double primaryLen; int exhaust; };
struct Spec {
    std::string name;
    std::vector<double> bankAnglesDeg;
    std::vector<double> journalAnglesDeg;
    std::vector<CylSpec> cyl;          // grouped by bank, in bank order
    double tdcDeg;
    std::vector<double> exhaustLengthIn;
    double simFreq;
    double redlineRpm, revLimitRpm;
};

// Geometry/flow numbers copied from assets/engines/atg-video-2/07_gm_ls.mr (per cylinder)
static Engine *buildEngine(const Spec &sp) {
    const double stroke = units::distance(3.622, units::inch);
    const double bore = units::distance(3.78, units::inch);
    const double rodLen = units::distance(160, units::mm);
    const double rodMass = units::mass(50, units::g);
    const double compH = units::distance(1.0, units::inch);
    const double crankMass = units::mass(60, units::lb);
    const double flyMass = units::mass(30, units::lb);
    const double flyR = units::distance(8, units::inch);
    const int nCyl = (int)sp.cyl.size();
    const int nBanks = (int)sp.bankAnglesDeg.size();
    const int nExh = (int)sp.exhaustLengthIn.size();

    Engine::Parameters ep;
    ep.name = sp.name;
    ep.cylinderBanks = nBanks;
    ep.cylinderCount = nCyl;
    ep.crankshaftCount = 1;
    ep.exhaustSystemCount = nExh;
    ep.intakeCount = 1;
    ep.starterTorque = units::torque(200, units::ft_lb);
    ep.starterSpeed = units::rpm(200);
    ep.redline = units::rpm(sp.redlineRpm);
    ep.dynoMinSpeed = units::rpm(1000);
    ep.dynoMaxSpeed = ep.redline;
    ep.dynoHoldStep = units::rpm(100);
    DirectThrottleLinkage *thr = new DirectThrottleLinkage;
    DirectThrottleLinkage::Parameters tp; tp.gamma = 2.0; thr->initialize(tp);
    ep.throttle = thr;
    ep.initialSimulationFrequency = sp.simFreq;
    ep.initialHighFrequencyGain = 0.01;
    ep.initialNoise = 1.0;
    ep.initialJitter = 0.6;

    Engine *e = new Engine;
    e->initialize(ep);

    Crankshaft *crank = e->getCrankshaft(0);
    Crankshaft::Parameters cp;
    cp.mass = crankMass;
    cp.flywheelMass = flyMass;
    cp.momentOfInertia = 1.5 * 0.5 * crankMass * stroke * stroke + 0.5 * flyMass * flyR * flyR + 0.5 * 1.0 * 0.01 * 0.01;
    cp.crankThrow = stroke / 2;
    cp.pos_x = 0; cp.pos_y = 0;
    cp.tdc = deg(sp.tdcDeg);
    cp.frictionTorque = units::torque(20.0 * nCyl / 8.0, units::ft_lb);
    cp.rodJournals = (int)sp.journalAnglesDeg.size();
    crank->initialize(cp);
    for (int i = 0; i < cp.rodJournals; ++i) crank->setRodJournalAngle(i, deg(sp.journalAnglesDeg[i]));

    Intake *intake = e->getIntake(0);
    Intake::Parameters ip;
    ip.volume = units::volume(1.325 * nCyl / 8.0, units::L);
    ip.CrossSectionArea = units::area(20.0, units::cm2);
    ip.InputFlowK = GasSystem::k_carb(700.0 * nCyl / 8.0);
    ip.IdleFlowK = GasSystem::k_carb(0.0);
    ip.RunnerFlowRate = GasSystem::k_carb(100.0);
    ip.RunnerLength = units::distance(12.0, units::inch);
    ip.IdleThrottlePlatePosition = 0.996;
    ip.VelocityDecay = 0.5;
    intake->initialize(ip);

    for (int x = 0; x < nExh; ++x) {
        ImpulseResponse *ir = new ImpulseResponse;
        ir->initialize(IR_PATH, 0.001);
        ExhaustSystem::Parameters xp;
        xp.length = units::distance(sp.exhaustLengthIn[x], units::inch);
        xp.collectorCrossSectionArea = constants::pi * units::distance(2.0, units::inch) * units::distance(2.0, units::inch);
        xp.outletFlowRate = GasSystem::k_carb(1000.0);
        xp.primaryTubeLength = units::distance(29.0, units::inch);
        xp.primaryFlowRate = GasSystem::k_carb(500.0);
        xp.velocityDecay = 1.0;
        xp.audioVolume = 4.0;
        xp.impulseResponse = ir;
        e->getExhaustSystem(x)->initialize(xp);
    }

    const std::vector<std::pair<double, double>> inFlow = {
        {0,0},{50,1},{100,103},{150,156},{200,214},{250,249},{300,268},{350,280},{400,280},{450,281} };
    const std::vector<std::pair<double, double>> exFlow = {
        {0,0},{50,1},{100,72},{150,113},{200,160},{250,196},{300,222},{350,235},{400,245},{450,246} };
    auto portFn = [](const std::vector<std::pair<double, double>> &s) {
        std::vector<std::pair<double, double>> v;
        for (auto &p : s) v.push_back({ p.first * units::thou, GasSystem::k_28inH2O(p.second) });
        return makeFn(units::distance(50, units::thou), v);
    };
    Function *inLobe = harmonicLobe(deg(234), 1.1, units::distance(551, units::thou), 256);
    Function *exLobe = harmonicLobe(deg(235), 1.1, units::distance(551, units::thou), 256);

    int g = 0;
    for (int b = 0; b < nBanks; ++b) {
        std::vector<CylSpec> bc;
        for (auto &c : sp.cyl) if (c.bank == b) bc.push_back(c);
        CylinderBank *bank = e->getCylinderBank(b);
        CylinderBank::Parameters bp;
        bp.crankshaft = crank;
        bp.positionX = 0; bp.positionY = 0;
        bp.angle = deg(sp.bankAnglesDeg[b]);
        bp.bore = bore;
        bp.deckHeight = stroke / 2 + rodLen + compH;
        bp.displayDepth = 0;
        bp.cylinderCount = (int)bc.size();
        bp.index = b;
        bank->initialize(bp);

        for (int i = 0; i < (int)bc.size(); ++i) {
            Piston *piston = e->getPiston(g + i);
            ConnectingRod *rod = e->getConnectingRod(g + i);
            Piston::Parameters pp;
            pp.Rod = rod; pp.Bank = bank; pp.CylinderIndex = i;
            pp.BlowbyFlowCoefficient = GasSystem::k_28inH2O(0.0);
            pp.CompressionHeight = compH; pp.WristPinPosition = 0; pp.Displacement = 0;
            pp.mass = units::mass(100, units::g);
            piston->initialize(pp);
            ConnectingRod::Parameters rp;
            rp.mass = rodMass;
            rp.momentOfInertia = (1 / 12.0) * rodMass * rodLen * rodLen;
            rp.centerOfMass = 0; rp.length = rodLen; rp.rodJournals = 0; rp.slaveThrow = 0;
            rp.piston = piston; rp.crankshaft = crank; rp.master = nullptr; rp.journal = bc[i].journal;
            rod->initialize(rp);
        }

        Camshaft *inCam = new Camshaft, *exCam = new Camshaft;
        Camshaft::Parameters cam;
        cam.lobes = (int)bc.size(); cam.advance = 0; cam.crankshaft = crank;
        cam.baseRadius = units::distance(1.0, units::inch);
        cam.lobeProfile = inLobe; inCam->initialize(cam);
        cam.lobeProfile = exLobe; exCam->initialize(cam);
        for (int i = 0; i < (int)bc.size(); ++i) {
            inCam->setLobeCenterline(i, deg(bc[i].firingDeg + 360 + 116));
            exCam->setLobeCenterline(i, deg(bc[i].firingDeg + 360 - 116));
        }
        StandardValvetrain *vt = new StandardValvetrain;
        StandardValvetrain::Parameters vp; vp.intakeCamshaft = inCam; vp.exhaustCamshaft = exCam;
        vt->initialize(vp);

        CylinderHead *head = e->getHead(b);
        CylinderHead::Parameters hp;
        hp.Bank = bank;
        hp.IntakePortFlow = portFn(inFlow);
        hp.ExhaustPortFlow = portFn(exFlow);
        hp.Valvetrain = vt;
        hp.CombustionChamberVolume = units::volume(90, units::cc);
        hp.IntakeRunnerVolume = units::volume(149.6, units::cc);
        hp.IntakeRunnerCrossSectionArea = units::distance(2.2, units::inch) * units::distance(2.2, units::inch);
        hp.ExhaustRunnerVolume = units::volume(50.0, units::cc);
        hp.ExhaustRunnerCrossSectionArea = units::distance(1.75, units::inch) * units::distance(1.75, units::inch);
        hp.FlipDisplay = false;
        head->initialize(hp);
        for (int i = 0; i < (int)bc.size(); ++i) {
            head->setIntake(i, intake);
            head->setExhaustSystem(i, e->getExhaustSystem(bc[i].exhaust));
            head->setSoundAttenuation(i, 1.0);
            head->setHeaderPrimaryLength(i, bc[i].primaryLen);
        }
        g += (int)bc.size();
    }

    Function *timing = makeFn(units::rpm(1000), {
        {units::rpm(0), deg(12)}, {units::rpm(1000), deg(12)}, {units::rpm(2000), deg(20)},
        {units::rpm(3000), deg(30)}, {units::rpm(4000), deg(40)}, {units::rpm(5000), deg(40)},
        {units::rpm(6000), deg(40)}, {units::rpm(7000), deg(40)}, {units::rpm(8000), deg(40)} });
    IgnitionModule::Parameters ig;
    ig.cylinderCount = nCyl; ig.crankshaft = crank; ig.timingCurve = timing;
    ig.revLimit = units::rpm(sp.revLimitRpm); ig.limiterDuration = 0.2;
    e->getIgnitionModule()->initialize(ig);
    {
        int gi = 0;
        for (int b = 0; b < nBanks; ++b)
            for (auto &c : sp.cyl) if (c.bank == b) e->getIgnitionModule()->setFiringOrder(gi++, deg(c.firingDeg));
    }

    std::vector<std::pair<double, double>> tfs = { {0.0, 3.0} };
    for (int i = 1; i <= 9; ++i) { double x = 5.0 * i; tfs.push_back({ x, (i == 1 ? 1.5 : (i == 2 ? 1.75 : 2.0)) * x }); }
    Fuel::Parameters fp;
    fp.maxBurningEfficiency = 1.0;
    fp.maxDilutionEffect = 10.0;     // .mr default (C++ struct default is 50)
    fp.turbulenceToFlameSpeedRatio = makeFn(5.0, tfs);
    e->getFuel()->initialize(fp);

    std::vector<std::pair<double, double>> mps;
    for (int i = 0; i < 30; ++i) mps.push_back({ (double)i, i * 0.5 });
    Function *meanPistonSpeedToTurbulence = makeFn(1.0, mps);

    CombustionChamber::Parameters cc;
    cc.CrankcasePressure = units::pressure(1.0, units::atm);
    cc.Fuel = e->getFuel();
    cc.StartingPressure = units::pressure(1.0, units::atm);
    cc.StartingTemperature = units::celcius(25.0);
    cc.MeanPistonSpeedToTurbulence = meanPistonSpeedToTurbulence;
    for (int i = 0; i < nCyl; ++i) {
        cc.Piston = e->getPiston(i);
        cc.Head = e->getHead(cc.Piston->getCylinderBank()->getIndex());
        e->getChamber(i)->initialize(cc);
    }
    e->calculateDisplacement();
    return e;
}

static Spec specInline(int n, double simFreq) {
    Spec s;
    s.name = "Inline-" + std::to_string(n);
    s.bankAnglesDeg = { 0.0 };
    s.tdcDeg = 90.0;
    for (int i = 0; i < n; ++i) {
        const double phi = i * 720.0 / n;
        s.journalAnglesDeg.push_back(std::fmod(phi, 360.0));
        s.cyl.push_back({ 0, i, phi, units::distance(2.0 * i, units::inch), 0 });
    }
    s.exhaustLengthIn = { 100.0 };
    s.simFreq = simFreq; s.redlineRpm = 6500; s.revLimitRpm = 6800;
    return s;
}

static Spec specLsV8(double simFreq) {   // mirrors 07_gm_ls.mr
    Spec s;
    s.name = "GM LS V8 (C++ rebuild)";
    s.bankAnglesDeg = { -45.0, 45.0 };
    s.journalAnglesDeg = { 0, 270, 90, 180 };
    s.tdcDeg = 45.0;
    const double sp = units::distance(2, units::inch), cm = units::distance(1, units::cm);
    s.cyl = {
        {0,0,  0, 3 * sp + 2 * cm, 0}, {0,1,630, 2 * sp + 1 * cm, 0}, {0,2,450, 1 * sp + 3 * cm, 0}, {0,3,180, 0 * sp + 5 * cm, 0},
        {1,0,270, 3 * sp + 1 * cm, 1}, {1,1,540, 2 * sp + 5 * cm, 1}, {1,2,360, 1 * sp + 7 * cm, 1}, {1,3, 90, 0, 1} };
    s.exhaustLengthIn = { 100.0, 172.0 };
    s.simFreq = simFreq; s.redlineRpm = 6500; s.revLimitRpm = 6800;
    return s;
}

static Spec specV12(double simFreq) {    // 60 deg V12, two interleaved I6 banks
    Spec s;
    s.name = "V12 60deg";
    s.bankAnglesDeg = { -30.0, 30.0 };
    s.journalAnglesDeg = { 0, 120, 240, 240, 120, 0 };
    s.tdcDeg = 60.0;
    const double f0[6] = { 0, 480, 240, 600, 120, 360 };
    for (int i = 0; i < 6; ++i) s.cyl.push_back({ 0, i, f0[i], units::distance(2.0 * i, units::inch), 0 });
    for (int i = 0; i < 6; ++i) s.cyl.push_back({ 1, i, std::fmod(f0[i] - 60 + 720, 720), units::distance(2.0 * i, units::inch), 1 });
    s.exhaustLengthIn = { 100.0, 120.0 };
    s.simFreq = simFreq; s.redlineRpm = 7500; s.revLimitRpm = 7800;
    return s;
}

// ---------------------------------------------------------------- simulator subclass
class ProbeSim : public PistonEngineSimulator {
public:
    atg_scs::RigidBody &vehicleMass() { return m_vehicleMass; }
    atg_scs::RigidBodySystem *system() { return m_system; }
    // Replace engine-sim's built-in road-load model with an external one.
    void detachVehicleDrag() { m_system->removeConstraint(&m_vehicleDrag); }
protected:
    // Same as PistonEngineSimulator::writeToSynthesizer minus `static double lastValveLift[8]`
    // (that static array overflows for engines with more than 8 cylinders and is shared by all instances).
    virtual void writeToSynthesizer() override {
        const int exhaustSystemCount = m_engine->getExhaustSystemCount();
        for (int i = 0; i < exhaustSystemCount; ++i) m_exhaustFlowStagingBuffer[i] = 0;
        const double attenuation = std::min(std::abs(filteredEngineSpeed()), 40.0) / 40.0;
        const double attenuation_3 = attenuation * attenuation * attenuation;
        const int cylinderCount = m_engine->getCylinderCount();
        for (int i = 0; i < cylinderCount; ++i) {
            Piston *piston = m_engine->getPiston(i);
            CylinderBank *bank = piston->getCylinderBank();
            CylinderHead *head = m_engine->getHead(bank->getIndex());
            ExhaustSystem *exhaust = head->getExhaustSystem(piston->getCylinderIndex());
            CombustionChamber *chamber = m_engine->getChamber(i);
            const double exhaustLength = head->getHeaderPrimaryLength(piston->getCylinderIndex()) + exhaust->getLength();
            const double exhaustFlow = attenuation_3 * 1600 * (
                1.0 * (chamber->m_exhaustRunnerAndPrimary.pressure() - units::pressure(1.0, units::atm))
                + 0.1 * chamber->m_exhaustRunnerAndPrimary.dynamicPressure(1.0, 0.0)
                + 0.1 * chamber->m_exhaustRunnerAndPrimary.dynamicPressure(-1.0, 0.0));
            const double delayedExhaustPulse = m_delayFilters[i].fast_f(exhaustFlow);
            m_exhaustFlowStagingBuffer[exhaust->getIndex()] +=
                head->getSoundAttenuation(piston->getCylinderIndex())
                * (exhaust->getAudioVolume() * delayedExhaustPulse / cylinderCount)
                * (1 / (exhaustLength * exhaustLength));
        }
        synthesizer().writeInput(m_exhaustFlowStagingBuffer);
    }
};

// Transmission subclass purely to read the clutch constraint torque (protected member).
class ProbeTransmission : public Transmission {
public:
    double clutchTorque() const { return m_clutchConstraint.F_t[0][0]; }
};

static ProbeSim *createSim(Engine *e, Vehicle *v, Transmission *t) {
    ProbeSim *s = new ProbeSim;
    Simulator::Parameters p; p.systemType = Simulator::SystemType::NsvOptimized;
    s->initialize(p);
    s->loadSimulation(e, v, t);
    s->setFluidSimulationSteps(8);
    s->setSimulationFrequency((int)e->getSimulationFrequency());
    Synthesizer::AudioParameters ap = s->synthesizer().getAudioParameters();
    ap.inputSampleNoise = (float)e->getInitialJitter();
    ap.airNoise = (float)e->getInitialNoise();
    ap.dF_F_mix = (float)e->getInitialHighFrequencyGain();
    s->synthesizer().setAudioParameters(ap);
    for (int i = 0; i < e->getExhaustSystemCount(); ++i) {
        ImpulseResponse *r = e->getExhaustSystem(i)->getImpulseResponse();
        std::vector<int16_t> w = loadWav16Mono(r->getFilename());
        s->synthesizer().initializeImpulseResponse(w.data(), (unsigned)w.size(), (float)r->getVolume(), i);
    }
    s->setTargetSynthesizerLatency(0.0);   // deterministic steps/frame (no latency-driven +-10%)
    s->synthesizer().endInputBlock();      // initialise m_latency (uninitialised in Synthesizer ctor)
    return s;
}

// Synchronous replacement for Synthesizer::renderAudio() (no audio thread, no condition variable).
static int renderSync(Synthesizer &s, std::vector<int16_t> *out) {
    const int n = (int)std::min<size_t>(s.m_inputChannels[0].data.size(), (size_t)s.m_inputBufferSize);
    for (int c = 0; c < s.m_inputChannelCount; ++c) s.m_inputChannels[c].data.read(n, s.m_inputChannels[c].transferBuffer);
    s.m_inputSamplesRead = n;          // endInputBlock() (called by Simulator::endFrame) removes these
    for (int c = 0; c < s.m_inputChannelCount; ++c) {
        s.m_filters[c].airNoiseLowPass.setCutoffFrequency(s.m_audioParameters.airNoiseFrequencyCutoff, s.m_audioSampleRate);
        s.m_filters[c].jitterFilter.setJitterScale(s.m_audioParameters.inputSampleNoise);
    }
    for (int i = 0; i < n; ++i) {
        const int16_t v = s.renderAudio(i);
        if (out) out->push_back(v);
    }
    return n;
}

struct Acc {
    double simSec = 0, synSec = 0; long long steps = 0, samples = 0;
};

static void frame(ProbeSim *s, double dt, Acc &acc, std::vector<int16_t> *audio, bool doAudio = true) {
    s->startFrame(dt);
    auto t0 = std::chrono::steady_clock::now();
    while (s->simulateStep()) {}
    auto t1 = std::chrono::steady_clock::now();
    int n = doAudio ? renderSync(s->synthesizer(), audio) : 0;
    if (!doAudio) { s->synthesizer().m_inputSamplesRead = (int)s->synthesizer().m_inputChannels[0].data.size(); }
    auto t2 = std::chrono::steady_clock::now();
    s->endFrame();
    acc.simSec += std::chrono::duration<double>(t1 - t0).count();
    acc.synSec += std::chrono::duration<double>(t2 - t1).count();
    acc.steps += s->getFrameIterationCount();
    acc.samples += n;
}

static double dominantHz(const std::vector<int16_t> &a, size_t start, size_t len, double rate, double f0, double f1) {
    double best = 0, bestF = 0;
    for (double f = f0; f <= f1; f += 0.5) {
        double re = 0, im = 0;
        for (size_t i = 0; i < len; ++i) {
            const double w = 0.5 - 0.5 * std::cos(2 * constants::pi * i / (len - 1));
            const double x = a[start + i] * w;
            re += x * std::cos(2 * constants::pi * f * i / rate);
            im -= x * std::sin(2 * constants::pi * f * i / rate);
        }
        const double m = re * re + im * im;
        if (m > best) { best = m; bestF = f; }
    }
    return bestF;
}

static void benchEngine(const Spec &sp, double frameDt, bool writeAudio) {
    Engine *e = buildEngine(sp);
    Vehicle *veh = new Vehicle;
    Vehicle::Parameters vp; vp.mass = units::mass(1614, units::kg); vp.dragCoefficient = 0.3;
    vp.crossSectionArea = units::distance(72, units::inch) * units::distance(50, units::inch);
    vp.diffRatio = 3.42; vp.tireRadius = units::distance(10, units::inch); vp.rollingResistance = 200;
    veh->initialize(vp);
    ProbeTransmission *tr = new ProbeTransmission;
    const double ratios[] = { 2.97, 2.07, 1.43, 1.00, 0.71, 0.57 };
    Transmission::Parameters tp; tp.GearCount = 6; tp.GearRatios = ratios; tp.MaxClutchTorque = units::torque(500, units::ft_lb);
    tr->initialize(tp);
    ProbeSim *s = createSim(e, veh, tr);

    std::vector<int16_t> audio;
    Acc warm, idle, dyno;
    e->getIgnitionModule()->m_enabled = true;
    s->m_starterMotor.m_enabled = true;
    e->setSpeedControl(0.0);
    for (double t = 0; t < 1.0; t += frameDt) frame(s, frameDt, warm, &audio);
    s->m_starterMotor.m_enabled = false;
    for (double t = 0; t < 2.0; t += frameDt) frame(s, frameDt, idle, &audio);
    const double idleRpm = e->getRpm();
    const size_t idleAudioEnd = audio.size();

    // WOT on dyno hold at 4000 rpm
    s->m_dyno.m_enabled = true; s->m_dyno.m_hold = true; s->m_dyno.m_rotationSpeed = units::rpm(4000);
    e->setSpeedControl(1.0);
    for (double t = 0; t < 3.0; t += frameDt) frame(s, frameDt, dyno, &audio);
    const double dynoRpm = e->getRpm();
    const double torqueNm = s->getFilteredDynoTorque();
    const double mapKpa = e->getManifoldPressure() / 1000.0;

    const double fps = 1.0 / frameDt;
    auto us = [](const Acc &a) { return 1e6 * a.simSec / std::max<long long>(1, a.steps); };
    auto synUs = [](const Acc &a) { return 1e6 * a.synSec / std::max<long long>(1, a.samples); };
    const double simRtIdle = us(idle) * sp.simFreq / 1e6, simRtDyno = us(dyno) * sp.simFreq / 1e6;
    const double synRt = synUs(dyno) * 44100.0 / 1e6;
    std::printf("%-26s cyl=%2d exh=%d disp=%.2fL f=%5.0fHz | idle %5.0f rpm %.2f us/step (%.1f%% core) | WOT@%4.0frpm T=%6.1fNm MAP=%5.1fkPa %.2f us/step (%.1f%% core) | synth %.3f us/sample (%.1f%% core) | steps/frame=%lld\n",
        sp.name.c_str(), (int)sp.cyl.size(), (int)sp.exhaustLengthIn.size(), e->getDisplacement() * 1000.0, sp.simFreq,
        idleRpm, us(idle), 100 * simRtIdle, dynoRpm, torqueNm, mapKpa, us(dyno), 100 * simRtDyno, synUs(dyno), 100 * synRt,
        dyno.steps / std::max<long long>(1, (long long)std::llround(3.0 * fps)));

    if (writeAudio) {
        // Expected firing frequency at 4000 rpm: rpm/60 * cyl/2
        const double expect = 4000.0 / 60.0 * sp.cyl.size() / 2.0;
        const size_t len = 8192, start = audio.size() - len - 1000;
        const double dom = dominantHz(audio, start, len, 44100.0, 30, 700);
        double rms = 0; for (size_t i = start; i < start + len; ++i) rms += (double)audio[i] * audio[i];
        rms = std::sqrt(rms / len);
        std::printf("    audio: %zu samples (%.2f s), idle-section end %zu, WOT dominant %.1f Hz (firing freq %.1f Hz), RMS %.0f\n",
            audio.size(), audio.size() / 44100.0, idleAudioEnd, dom, expect, rms);
        writeWav16Mono(g_outDir + "/" + std::to_string(sp.cyl.size()) + "cyl.wav", audio, 44100);
    }
}

// Convolution cost in isolation (the synthesizer's dominant cost).
static void benchConvolution() {
    ConvolutionFilter f; f.initialize(10000);
    for (int i = 0; i < 10000; ++i) f.getImpulseResponse()[i] = std::sin(i * 0.01f) * 0.001f;
    const int N = 44100;
    float acc = 0;
    auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < N; ++i) acc += f.f((float)std::sin(i * 0.1));
    auto t1 = std::chrono::steady_clock::now();
    const double sec = std::chrono::duration<double>(t1 - t0).count();
    std::printf("ConvolutionFilter 10000 taps: %.3f us/sample -> %.1f%% of one core per exhaust channel at 44.1 kHz (acc=%g)\n",
        1e6 * sec / N, 100.0 * sec, acc);
    f.destroy();
}

// ---------------------------------------------------------------- SIMD convolution (what a plugin could use)
#include <immintrin.h>
static void benchConvolutionAvx2() {
    const int N = 10000;
    std::vector<float> ir(N), hist(2 * N, 0.0f);   // history stored twice -> contiguous window, no wrap
    for (int i = 0; i < N; ++i) ir[i] = std::sin(i * 0.01f) * 0.001f;
    int pos = 0;
    float acc = 0;
    const int S = 44100;
    auto t0 = std::chrono::steady_clock::now();
    for (int n = 0; n < S; ++n) {
        const float x = (float)std::sin(n * 0.1);
        pos = (pos == 0) ? N - 1 : pos - 1;
        hist[pos] = x; hist[pos + N] = x;
        const float *h = &hist[pos];
        __m256 a0 = _mm256_setzero_ps(), a1 = _mm256_setzero_ps(), a2 = _mm256_setzero_ps(), a3 = _mm256_setzero_ps();
        int i = 0;
        for (; i + 32 <= N; i += 32) {
            a0 = _mm256_fmadd_ps(_mm256_loadu_ps(&ir[i]), _mm256_loadu_ps(h + i), a0);
            a1 = _mm256_fmadd_ps(_mm256_loadu_ps(&ir[i + 8]), _mm256_loadu_ps(h + i + 8), a1);
            a2 = _mm256_fmadd_ps(_mm256_loadu_ps(&ir[i + 16]), _mm256_loadu_ps(h + i + 16), a2);
            a3 = _mm256_fmadd_ps(_mm256_loadu_ps(&ir[i + 24]), _mm256_loadu_ps(h + i + 24), a3);
        }
        __m256 s = _mm256_add_ps(_mm256_add_ps(a0, a1), _mm256_add_ps(a2, a3));
        float t[8]; _mm256_storeu_ps(t, s);
        float r = t[0] + t[1] + t[2] + t[3] + t[4] + t[5] + t[6] + t[7];
        for (; i < N; ++i) r += ir[i] * h[i];
        acc += r;
    }
    auto t1 = std::chrono::steady_clock::now();
    const double sec = std::chrono::duration<double>(t1 - t0).count();
    std::printf("AVX2 direct convolution 10000 taps: %.3f us/sample -> %.2f%% of one core per channel (acc=%g)\n",
        1e6 * sec / S, 100.0 * sec, acc);
}

// Split the per-step cost: rigid-body/constraint solve vs. everything else.
static void benchBreakdown() {
    struct Cfg { int kind; double f; int fluid; };
    for (Cfg cfg : { Cfg{8,10000,8}, Cfg{8,10000,4}, Cfg{8,10000,2}, Cfg{8,5000,4}, Cfg{8,5000,2}, Cfg{4,10000,8}, Cfg{4,5000,2} }) {
        Spec sp = (cfg.kind == 8) ? specLsV8(cfg.f) : specInline(4, cfg.f);
        const int fluid = cfg.fluid;
        Engine *e = buildEngine(sp);
        Vehicle *veh = new Vehicle; Vehicle::Parameters vp; vp.mass = 1614; vp.dragCoefficient = 0.3; vp.crossSectionArea = 2.3;
        vp.diffRatio = 3.42; vp.tireRadius = 0.254; vp.rollingResistance = 200; veh->initialize(vp);
        ProbeTransmission *tr = new ProbeTransmission; const double r[] = { 2.97 };
        Transmission::Parameters tp; tp.GearCount = 1; tp.GearRatios = r; tp.MaxClutchTorque = 700; tr->initialize(tp);
        ProbeSim *s = createSim(e, veh, tr);
        s->setFluidSimulationSteps(fluid);
        Acc a;
        e->getIgnitionModule()->m_enabled = true; s->m_starterMotor.m_enabled = true;
        for (double t = 0; t < 1.0; t += 0.02) frame(s, 0.02, a, nullptr, false);
        s->m_starterMotor.m_enabled = false;
        s->m_dyno.m_enabled = true; s->m_dyno.m_hold = true; s->m_dyno.m_rotationSpeed = units::rpm(4000);
        e->setSpeedControl(1.0);
        for (double t = 0; t < 1.0; t += 0.02) frame(s, 0.02, a, nullptr, false);
        Acc full;
        for (double t = 0; t < 1.0; t += 0.02) frame(s, 0.02, full, nullptr, false);
        const double torque = s->getFilteredDynoTorque();
        // rigid-body system alone (state advances, but only for timing)
        const int K = 5000;
        auto t0 = std::chrono::steady_clock::now();
        for (int k = 0; k < K; ++k) s->system()->process(1.0 / cfg.f, 1);
        auto t1 = std::chrono::steady_clock::now();
        const double rb = 1e6 * std::chrono::duration<double>(t1 - t0).count() / K;
        const double us = 1e6 * full.simSec / full.steps;
        std::printf("%-24s f=%5.0f fluidSteps=%d: full step %.2f us (%.1f%% of a core in real time), rigid-body+constraint solve %.2f us (%d bodies, %d rows), WOT T@4000=%.1f Nm, rpm=%.0f\n",
            sp.name.c_str(), cfg.f, fluid, us, us * cfg.f / 1e4, rb, s->system()->getRigidBodyCount(), s->system()->getFullConstraintCount(), torque, e->getRpm());
    }
}

// ---------------------------------------------------------------- co-simulation experiment
struct Tyre { double mu = 1.0, N = 0.5 * 1614 * 9.81, B = 10.0, C = 1.9, r = 0.254; };
static double tyreForce(double omegaW, double v, const Tyre &t) {
    const double vref = std::max(std::abs(v), 1.0);           // low-speed regularisation
    const double kappa = (omegaW * t.r - v) / vref;
    return t.mu * t.N * std::sin(t.C * std::atan(t.B * kappa));
}
static double roadDrag(double v) { return 200.0 + 0.5 * 1.184 * 0.3 * 2.32 * v * v; }

// External load on engine-sim's driveline proxy body (m_vehicleMass): driven wheels, reflected through G.
class WheelLoad : public atg_scs::ForceGenerator {
public:
    atg_scs::RigidBody *proxy = nullptr;
    double G = 1.0, v = 0.0;
    Tyre tyre;
    int mode = 0;                 // 0: full tyre curve at frozen chassis speed, 1: linearised, 2: constant force
    double F0 = 0, dF = 0, w0 = 0, Fconst = 0;
    virtual void apply(atg_scs::SystemState *s) override {
        const int i = proxy->index;
        const double w = -s->v_theta[i] / G;          // engine spins negative: forward wheel speed
        double F;
        if (mode == 0) F = tyreForce(w, v, tyre);
        else if (mode == 1) F = F0 + dF * (w - w0);
        else F = Fconst;
        s->t[i] += F * tyre.r / G;                   // resists forward (negative) rotation
    }
    double force(double w) const {
        if (mode == 0) return tyreForce(w, v, tyre);
        if (mode == 1) return F0 + dF * (w - w0);
        return Fconst;
    }
};

struct CoSimResult { std::vector<double> t, rpm, v, slip; double chatter = 0, slipAbsMax = 0; };

// variant: 0 = monolithic reference (chassis updated every engine step)
//          1 = co-sim, plugin owns wheel spin, full tyre curve, chassis at hUnity
//          2 = co-sim, plugin owns wheel spin, linearised tyre, chassis at hUnity
//          3 = naive: Unity owns wheel spin (explicit at hUnity), engine-sim gets constant load, proxy speed reset
// scenario: 0 = 2nd-gear WOT launch from 8 m/s (wheelspin), 1 = 4th-gear 30% throttle cruise from 20 m/s (grip)
static CoSimResult runCoSim(int variant, double hUnity, double mu, int scenario) {
    Spec sp = specLsV8(10000);
    Engine *e = buildEngine(sp);
    Vehicle *veh = new Vehicle; Vehicle::Parameters vp; vp.mass = 1614; vp.dragCoefficient = 0.3; vp.crossSectionArea = 2.32;
    vp.diffRatio = 3.42; vp.tireRadius = 0.254; vp.rollingResistance = 200; veh->initialize(vp);
    ProbeTransmission *tr = new ProbeTransmission; const double ratios[] = { 2.97, 2.07, 1.43, 1.00 };
    Transmission::Parameters tp; tp.GearCount = 4; tp.GearRatios = ratios; tp.MaxClutchTorque = units::torque(500, units::ft_lb);
    tr->initialize(tp);
    ProbeSim *s = createSim(e, veh, tr);
    s->detachVehicleDrag();

    const int gear = (scenario == 0) ? 1 : 3;
    const double G = ratios[gear] * 3.42;              // gear * final drive
    const double Jw = 2.0, Jgear = 0.02;               // driven wheels + gearbox/driveshaft (kg m^2)
    const double thrMain = (scenario == 0) ? 1.0 : 0.3;
    Tyre ty; ty.mu = mu;
    WheelLoad load; load.proxy = &s->vehicleMass(); load.G = G; load.tyre = ty;
    load.mode = (variant == 2) ? 1 : (variant == 3 ? 2 : 0);
    s->system()->addForceGenerator(&load);

    double v = (scenario == 0) ? 8.0 : 20.0;            // chassis speed (Unity state)
    double wW = v / ty.r;                               // wheel speed (Unity state, used by variant 3)
    tr->changeGear(gear);
    tr->setClutchPressure(0.0);
    tr->update(0.0);   // Transmission::update runs AFTER process() in simulateStep: prime clutch limits
    s->vehicleMass().I = Jgear + Jw / (G * G);
    s->vehicleMass().v_theta = -wW * G;

    e->getIgnitionModule()->m_enabled = true;
    s->m_starterMotor.m_enabled = true;
    CoSimResult res;
    const double hEngine = 1.0 / 10000.0;
    const double T = 6.5;
    double nextLog = 0, lastSlip = 0; double ch = 0; int chn = 0;
    for (double t = 0; t < T - 1e-9; t += hUnity) {
        if (t >= 1.2) s->m_starterMotor.m_enabled = false;
        double thr = 0.0, clutch = 0.0;
        if (t >= 2.0 && t < 2.5) { thr = 0.25; clutch = (t - 2.0) / 0.5; }
        else if (t >= 2.5) { thr = thrMain; clutch = 1.0; }
        e->setSpeedControl(thr);
        tr->setClutchPressure(clutch);

        // ---- Unity -> plugin
        load.v = v;
        if (variant == 3) {
            s->vehicleMass().v_theta = -wW * G;          // kinematic reset to Unity's wheel speed
            load.Fconst = tyreForce(wW, v, ty);
        }
        else {
            const double w = -s->vehicleMass().v_theta / G;
            load.w0 = w; load.F0 = tyreForce(w, v, ty);
            const double eps = 1e-3;
            load.dF = (tyreForce(w + eps, v, ty) - tyreForce(w - eps, v, ty)) / (2 * eps);
        }

        // ---- plugin step: N engine substeps
        s->startFrame(hUnity);
        double sumF = 0, sumTc = 0; int n = 0;
        while (s->simulateStep()) {
            const double w = -s->vehicleMass().v_theta / G;
            const double F = load.force(w);
            sumF += F; sumTc += tr->clutchTorque(); ++n;   // F_t on the crank is +ve (resisting -ve spin) when driving
            if (variant == 0) {
                v += (F - roadDrag(v)) / 1614.0 * hEngine;
                load.v = v;
            }
        }
        renderSync(s->synthesizer(), nullptr);
        s->endFrame();
        const double Favg = sumF / std::max(1, n);
        const double TclutchAvg = sumTc / std::max(1, n);

        // ---- Unity physics step
        if (variant == 1 || variant == 2) {
            v += (Favg - roadDrag(v)) / 1614.0 * hUnity;
        }
        else if (variant == 3) {
            const double Fw = tyreForce(wW, v, ty);
            wW += (G * TclutchAvg - Fw * ty.r) / Jw * hUnity;   // explicit wheel-spin integration at Unity rate
            v += (Fw - roadDrag(v)) / 1614.0 * hUnity;
        }

        const double w = (variant == 3) ? wW : -s->vehicleMass().v_theta / G;
        const double slip = (w * ty.r - v) / std::max(std::abs(v), 1.0);
        if (t > 3.0) { ch += (slip - lastSlip) * (slip - lastSlip); ++chn; res.slipAbsMax = std::max(res.slipAbsMax, std::abs(slip)); }
        lastSlip = slip;
        if (t + 1e-9 >= nextLog) {
            res.t.push_back(t); res.rpm.push_back(e->getRpm()); res.v.push_back(v); res.slip.push_back(slip);
            nextLog += 0.02;
        }
    }
    res.chatter = std::sqrt(ch / std::max(1, chn));
    return res;
}

// ---------------------------------------------------------------- variant D: Unity owns the crank (report 06 "Option D")
// engine-sim's crank is slaved to Unity's crank-speed trajectory by a PD force generator; engine-sim returns the
// net indicated torque T_ind = J_es*dw/h - mean(T_pd), which Unity applies one physics step later.
class SlavedCrankPD : public atg_scs::ForceGenerator {
public:
    atg_scs::RigidBody *crank = nullptr;
    double kp = 0, kd = 0, cmd = 0, err = 0, lastT = 0, Tmax = 3000;
    bool enabled = false;
    virtual void apply(atg_scs::SystemState *s) override {
        if (!enabled) { lastT = 0; return; }
        const int i = crank->index;
        const double w = -s->v_theta[i];                 // forward-positive crank speed
        double T = -kp * err - kd * (w - cmd);
        T = std::max(-Tmax, std::min(Tmax, T));
        s->t[i] += -T;                                    // engine spins negative
        lastT = T;
    }
};

struct SlavedEngine {
    Engine *e = nullptr; ProbeSim *s = nullptr; ProbeTransmission *tr = nullptr; SlavedCrankPD pd;
};

static SlavedEngine makeSlaved(double J, double fc) {
    SlavedEngine x;
    x.e = buildEngine(specLsV8(10000));
    Vehicle *veh = new Vehicle; Vehicle::Parameters vp; vp.mass = 1614; vp.dragCoefficient = 0.3; vp.crossSectionArea = 2.32;
    vp.diffRatio = 3.42; vp.tireRadius = 0.254; vp.rollingResistance = 200; veh->initialize(vp);
    x.tr = new ProbeTransmission; static const double ratios[] = { 2.97, 2.07, 1.43, 1.00 };
    Transmission::Parameters tp; tp.GearCount = 4; tp.GearRatios = ratios; tp.MaxClutchTorque = units::torque(500, units::ft_lb);
    x.tr->initialize(tp);
    x.s = createSim(x.e, veh, x.tr);
    x.s->detachVehicleDrag();
    x.tr->changeGear(-1); x.tr->setClutchPressure(0.0); x.tr->update(0.0);   // engine-sim's own driveline disconnected
    const double wc = 2 * constants::pi * fc;
    x.pd.crank = &x.e->getOutputCrankshaft()->m_body;
    x.pd.kp = J * wc * wc; x.pd.kd = 2 * 0.7 * wc * J;
    x.s->system()->addForceGenerator(&x.pd);
    return x;
}

// Effective crank-train inertia: ramp up and down through 2000 rpm with ignition off, J = (T_up - T_down) / (2 alpha).
static double calibrateJ() {
    SlavedEngine x = makeSlaved(0.3, 8.0);
    x.pd.enabled = true;
    const double h = 0.01, fs = 10000.0;
    const double alpha = units::rpm(2000) / 0.5;      // rad/s^2
    double tUp = 0, tDn = 0; int nUp = 0, nDn = 0;
    double cmd = 0, t = 0;
    auto run = [&](double rate, double dur, int tag) {
        for (double tt = 0; tt < dur - 1e-9; tt += h) {
            x.s->startFrame(h);
            while (true) {
                cmd += rate / fs; x.pd.cmd = cmd;
                if (!x.s->simulateStep()) break;
                x.pd.err += (-x.pd.crank->v_theta - cmd) / fs;
                const double rpm = units::toRpm(cmd);
                if (tag == 1 && rpm > 1800 && rpm < 2200) { tUp += x.pd.lastT; ++nUp; }
                if (tag == 2 && rpm > 1800 && rpm < 2200) { tDn += x.pd.lastT; ++nDn; }
            }
            renderSync(x.s->synthesizer(), nullptr);
            x.s->endFrame();
            t += h;
        }
    };
    run(units::rpm(1000) / 0.5, 0.5, 0);   // 0 -> 1000 rpm
    run(0, 0.5, 0);
    run(alpha, 1.0, 1);                    // 1000 -> 3000 rpm
    run(0, 0.5, 0);
    run(-alpha, 1.0, 2);                   // 3000 -> 1000 rpm
    const double J = (tUp / std::max(1, nUp) - tDn / std::max(1, nDn)) / (2 * alpha);
    std::printf("  calibrated effective crank-train inertia J_es = %.4f kg m^2 (crankshaft body I = %.4f)\n",
        J, x.e->getOutputCrankshaft()->getMomentOfInertia());
    return J;
}

static CoSimResult runSlaved(double h, int substeps, double mu, int scenario, double J, double *trackRms) {
    SlavedEngine x = makeSlaved(J, 8.0);
    const int gear = (scenario == 0) ? 1 : 3;
    static const double ratios[] = { 2.97, 2.07, 1.43, 1.00 };
    const double G = ratios[gear] * 3.42;
    const double Jw = 2.0 + 0.02 * G * G;          // wheels + gearbox (reflected to wheel), same as the proxy in A/B/C
    const double Jc = J;
    const double Tcap = units::torque(500, units::ft_lb);
    const double thrMain = (scenario == 0) ? 1.0 : 0.3;
    Tyre ty; ty.mu = mu;
    double v = (scenario == 0) ? 8.0 : 20.0, ww = v / ty.r, wc = 0, Tind = 0;
    x.e->getIgnitionModule()->m_enabled = true;
    x.s->m_starterMotor.m_enabled = true;
    CoSimResult res;
    const double fs = 10000.0, T = 6.5;
    double nextLog = 0, lastSlip = 0, ch = 0, tr2 = 0; int chn = 0, trn = 0;
    bool slaved = false;
    for (double t = 0; t < T - 1e-9; t += h) {
        if (t >= 1.2) x.s->m_starterMotor.m_enabled = false;
        double thr = 0.0, clutch = 0.0;
        if (t >= 2.0 && t < 2.5) { thr = 0.25; clutch = (t - 2.0) / 0.5; }
        else if (t >= 2.5) { thr = thrMain; clutch = 1.0; }
        x.e->setSpeedControl(thr);
        if (!slaved && t >= 1.5) { slaved = true; wc = -x.pd.crank->v_theta; x.pd.err = 0; x.pd.enabled = true; Tind = 0; }

        // ---- Unity step k: driveline substeps with T_ind from step k-1
        std::vector<double> wcs(substeps + 1);
        const double hs = h / substeps;
        if (!slaved) {
            // engine free-running inside engine-sim (start-up); Unity integrates only the wheel
            for (int k = 0; k < substeps; ++k) {
                const double F = tyreForce(ww, v, ty), eps = 1e-3;
                const double c = std::max(0.0, (tyreForce(ww + eps, v, ty) - tyreForce(ww - eps, v, ty)) / (2 * eps));
                const double Jwe = Jw + hs * c * ty.r;
                const double ww0 = ww;
                ww += hs * (-F * ty.r) / Jwe;
                v += hs * ((F + c * (ww - ww0)) - roadDrag(v)) / 1614.0;
            }
        }
        else {
            wcs[0] = wc;
            for (int k = 0; k < substeps; ++k) {
                const double wcStar = wc + hs * Tind / Jc;
                const double F0 = tyreForce(ww, v, ty), eps = 1e-3;
                const double c = std::max(0.0, (tyreForce(ww + eps, v, ty) - tyreForce(ww - eps, v, ty)) / (2 * eps));
                const double Jwe = Jw + hs * c * ty.r;               // implicit linearised tyre
                const double ww0 = ww;
                double wwStar = ww + hs * (-F0 * ty.r) / Jwe;
                const double cap = Tcap * clutch * hs;
                double P = 0;
                if (cap > 0) {
                    P = (wcStar - G * wwStar) / (1 / Jc + G * G / Jwe);
                    P = std::max(-cap, std::min(cap, P));
                }
                wc = wcStar - P / Jc;
                ww = wwStar + G * P / Jwe;
                v += hs * ((F0 + c * (ww - ww0)) - roadDrag(v)) / 1614.0;
                wcs[k + 1] = wc;
            }
        }

        // ---- engine-sim follows Unity's crank trajectory for step k (on its own thread in reality)
        x.s->startFrame(h);
        const double w0 = -x.pd.crank->v_theta;
        double sumT = 0; int n = 0;
        const int N = (int)std::lround(h * fs);
        while (true) {
            if (slaved) {
                const double u = (n + 0.5) / N * substeps;
                const int k = std::min(substeps - 1, (int)u);
                x.pd.cmd = wcs[k] + (wcs[k + 1] - wcs[k]) * (u - k);
            }
            if (!x.s->simulateStep()) break;
            if (slaved) {
                x.pd.err += (-x.pd.crank->v_theta - x.pd.cmd) / fs;
                sumT += x.pd.lastT;
                tr2 += (-x.pd.crank->v_theta - x.pd.cmd) * (-x.pd.crank->v_theta - x.pd.cmd); ++trn;
            }
            ++n;
        }
        renderSync(x.s->synthesizer(), nullptr);
        x.s->endFrame();
        if (slaved) Tind = J * (-x.pd.crank->v_theta - w0) / h - sumT / std::max(1, n);   // used at step k+1

        const double slip = (ww * ty.r - v) / std::max(std::abs(v), 1.0);
        if (t > 3.0) { ch += (slip - lastSlip) * (slip - lastSlip); ++chn; res.slipAbsMax = std::max(res.slipAbsMax, std::abs(slip)); }
        lastSlip = slip;
        if (t + 1e-9 >= nextLog) {
            res.t.push_back(t); res.rpm.push_back(x.e->getRpm()); res.v.push_back(v); res.slip.push_back(slip);
            nextLog += 0.02;
        }
    }
    res.chatter = std::sqrt(ch / std::max(1, chn));
    *trackRms = units::toRpm(std::sqrt(tr2 / std::max(1, trn)));
    return res;
}

static void cosimExperiment() {
    const double Jcal = calibrateJ();
    struct Sc { int scenario; double mu; const char *name; };
    for (Sc sc : { Sc{0, 1.0, "2nd-gear WOT launch, mu=1.0"}, Sc{0, 0.35, "2nd-gear WOT launch, mu=0.35 (mud)"},
                   Sc{1, 1.0, "4th-gear 30% cruise, mu=1.0 (grip, clutch locked)"} }) {
        CoSimResult ref = runCoSim(0, 0.02, sc.mu, sc.scenario);
        std::printf("\n%s | reference (all at 10 kHz): v(6.5s)=%.2f m/s rpm=%.0f slip=%.3f slip-chatter=%.4f\n",
            sc.name, ref.v.back(), ref.rpm.back(), ref.slip.back(), ref.chatter);
        struct V { int variant; double h; const char *name; };
        for (V var : { V{1, 0.02, "A: plugin owns wheel spin, full tyre curve, 50 Hz"},
                       V{1, 0.01, "A: plugin owns wheel spin, full tyre curve, 100 Hz"},
                       V{2, 0.02, "B: plugin owns wheel spin, linearised tyre, 50 Hz"},
                       V{3, 0.02, "C: Unity owns wheel spin, explicit, 50 Hz"},
                       V{3, 0.01, "C: Unity owns wheel spin, explicit, 100 Hz"},
                       V{3, 0.002, "C: Unity owns wheel spin, explicit, 500 Hz"} }) {
            CoSimResult r = runCoSim(var.variant, var.h, sc.mu, sc.scenario);
            double sumD = 0; int cnt = 0; bool bad = false;
            for (size_t i = 0; i < std::min(r.rpm.size(), ref.rpm.size()); ++i) {
                if (!std::isfinite(r.rpm[i]) || !std::isfinite(r.v[i])) { bad = true; break; }
                if (r.t[i] < 2.0) continue;
                sumD += std::abs(r.rpm[i] - ref.rpm[i]); ++cnt;
            }
            std::printf("  %-52s v=%7.2f m/s (dv=%+.2f) rpm=%6.0f | mean|drpm| vs ref=%5.0f | slip-chatter=%.4f max|slip|=%.3f %s\n",
                var.name, r.v.back(), r.v.back() - ref.v.back(), r.rpm.back(), cnt ? sumD / cnt : 0, r.chatter, r.slipAbsMax, bad ? "NaN!" : "");
        }
        struct D { double h; int sub; const char *name; };
        for (D d : { D{1.0 / 120, 4, "D: Unity owns crank, PD-slaved es, 120 Hz x4 sub"},
                     D{1.0 / 50, 4, "D: Unity owns crank, PD-slaved es, 50 Hz x4 sub"} }) {
            double track = 0;
            CoSimResult r = runSlaved(d.h, d.sub, sc.mu, sc.scenario, Jcal, &track);
            double sumD = 0; int cnt = 0; bool bad = false;
            for (size_t i = 0; i < std::min(r.rpm.size(), ref.rpm.size()); ++i) {
                if (!std::isfinite(r.rpm[i]) || !std::isfinite(r.v[i])) { bad = true; break; }
                if (r.t[i] < 2.0) continue;
                sumD += std::abs(r.rpm[i] - ref.rpm[i]); ++cnt;
            }
            std::printf("  %-52s v=%7.2f m/s (dv=%+.2f) rpm=%6.0f | mean|drpm| vs ref=%5.0f | slip-chatter=%.4f max|slip|=%.3f | es-vs-Unity crank rms=%.0f rpm %s\n",
                d.name, r.v.back(), r.v.back() - ref.v.back(), r.rpm.back(), cnt ? sumD / cnt : 0, r.chatter, r.slipAbsMax, track, bad ? "NaN!" : "");
        }
    }
}

int main(int argc, char **argv) {
    if (argc > 1) g_outDir = argv[1];
    if (argc > 2 && std::string(argv[2]) == "cosim") { cosimExperiment(); return 0; }
    if (argc > 2 && std::string(argv[2]) == "breakdown") { benchConvolutionAvx2(); benchBreakdown(); return 0; }
    const double dt = 1.0 / 50.0;
    benchConvolution();
    benchConvolutionAvx2();
    benchEngine(specInline(4, 10000), dt, true);
    benchEngine(specInline(6, 10000), dt, true);
    benchEngine(specLsV8(10000), dt, true);
    benchEngine(specV12(10000), dt, true);
    benchEngine(specLsV8(20000), dt, false);
    return 0;
}
