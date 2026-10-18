// probe.exe - headless engine-sim benchmark (no delta-studio renderer, no piranha scripting).
//
//   probe.exe [--quick] [--parts ABCDEF]
//     A  start -> idle -> 25/60/100 % pedal against a fixed load, audio ON, writes probe_out.wav
//     B  wide-open-throttle dyno-hold sweep 2000..6500 rpm (sanity vs. real EJ25 figures)
//     C  single-thread CPU cost of several configurations (P-core / E-core pinned)
//     D  N engines in parallel threads (N = 1,2,4,8,16)
//     E  1 "local" full-quality engine + N "remote" lightweight engines in parallel
//     F  audio clips of the lightweight configurations (probe_out_10k4.wav, probe_out_5k2.wav)
//
// CPU time is measured with QueryThreadCycleTime (cycle-exact) converted with a TSC rate that is
// calibrated at start-up; GetThreadTimes is NOT used because it is tick-sampled (15.6 ms).

#include "ej25.h"
#include "wav.h"

#include "engine.h"
#include "piston_engine_simulator.h"
#include "units.h"

#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <thread>
#include <vector>

#ifndef ES_ROOT_DIR
#define ES_ROOT_DIR "D:/Backroads-ref/engine-sim"
#endif

namespace {

// ------------------------------------------------------------------ timing helpers
unsigned long long threadCycles() {
    ULONG64 cycles = 0;
    QueryThreadCycleTime(GetCurrentThread(), &cycles);
    return cycles;
}

double wallSeconds() {
    using namespace std::chrono;
    return duration<double>(steady_clock::now().time_since_epoch()).count();
}

double g_tscHz = 3.0e9;

void calibrateTsc() {
    double best = 0;
    for (int i = 0; i < 5; ++i) {
        const double w0 = wallSeconds();
        const unsigned long long c0 = threadCycles();
        volatile double x = 0;
        while (wallSeconds() - w0 < 0.1) x = x + 1.0;
        const double w1 = wallSeconds();
        const unsigned long long c1 = threadCycles();
        best = std::max(best, (c1 - c0) / (w1 - w0));
    }
    g_tscHz = best;
}

// ------------------------------------------------------------------ hybrid CPU (P/E core) helpers
struct CpuTopology {
    std::map<std::pair<WORD, BYTE>, BYTE> efficiencyClass;   // (group, lp) -> class
    DWORD_PTR pMask = 0, eMask = 0;
    int pCount = 0, eCount = 0;
    BYTE maxClass = 0;

    void load() {
        ULONG len = 0;
        GetSystemCpuSetInformation(nullptr, 0, &len, GetCurrentProcess(), 0);
        std::vector<char> buf(len);
        if (!GetSystemCpuSetInformation((PSYSTEM_CPU_SET_INFORMATION)buf.data(), len, &len, GetCurrentProcess(), 0)) return;
        for (int pass = 0; pass < 2; ++pass) {
            for (ULONG off = 0; off < len;) {
                auto *info = (PSYSTEM_CPU_SET_INFORMATION)(buf.data() + off);
                if (info->Type == CpuSetInformation) {
                    const auto &cs = info->CpuSet;
                    if (pass == 0) {
                        maxClass = std::max(maxClass, cs.EfficiencyClass);
                        efficiencyClass[{ cs.Group, cs.LogicalProcessorIndex }] = cs.EfficiencyClass;
                    }
                    else if (cs.Group == 0) {
                        if (cs.EfficiencyClass == maxClass) { pMask |= (DWORD_PTR)1 << cs.LogicalProcessorIndex; ++pCount; }
                        else { eMask |= (DWORD_PTR)1 << cs.LogicalProcessorIndex; ++eCount; }
                    }
                }
                off += info->Size;
            }
        }
    }

    bool onPCore() const {
        PROCESSOR_NUMBER pn;
        GetCurrentProcessorNumberEx(&pn);
        auto it = efficiencyClass.find({ pn.Group, pn.Number });
        return it != efficiencyClass.end() && it->second == maxClass;
    }

    // hard affinity (SetThreadSelectedCpuSets turned out to be only a preference)
    void pinCurrentThread(int pin) const {
        if (pin == 1 && pMask) SetThreadAffinityMask(GetCurrentThread(), pMask);
        if (pin == 2 && eMask) SetThreadAffinityMask(GetCurrentThread(), eMask);
    }
};

CpuTopology g_topology;

std::string cpuName() {
    char name[256] = {};
    DWORD size = sizeof(name);
    RegGetValueA(HKEY_LOCAL_MACHINE, "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
        "ProcessorNameString", RRF_RT_REG_SZ, nullptr, name, &size);
    return name;
}

// ------------------------------------------------------------------ runner
enum class LoadMode { None, Quadratic, DynoHold };

struct Runner {
    EngineRig rig;
    Simulator *sim = nullptr;
    Engine *engine = nullptr;
    bool renderAudio = true;
    std::vector<int16_t> *wav = nullptr;

    LoadMode load = LoadMode::None;
    double loadRefTorque = 200.0;                 // N*m at loadRefSpeed
    double loadRefSpeed = units::rpm(5000);

    // accounting
    double simTime = 0;
    long long steps = 0;
    double physicsWall = 0, audioWall = 0;
    unsigned long long physicsCycles = 0, audioCycles = 0;
    long long frames = 0, framesOnPCore = 0;
    double sumRpm = 0, sumTorque = 0;

    std::vector<int16_t> scratch;

    void attach(const EngineRig &r) {
        rig = r;
        sim = r.simulator;
        engine = r.engine;
        // Synthesizer::initialize() pre-fills 1 s of silence into the output buffer; discard it.
        drainAudio(nullptr);
    }

    void drainAudio(std::vector<int16_t> *out) {
        Synthesizer &s = sim->synthesizer();
        const int n = (int)s.m_audioBuffer.size();
        if (n <= 0) return;
        scratch.resize(n);
        sim->readAudioOutput(n, scratch.data());
        if (out != nullptr) out->insert(out->end(), scratch.begin(), scratch.end());
    }

    // Single-threaded replacement for Synthesizer::audioRenderingThread(): render every pending
    // input sample (convolution + filters) right after the physics frame. Never blocks because we
    // only call renderAudio() when its wait-predicate is already true.
    void pumpAudio() {
        Synthesizer &s = sim->synthesizer();
        for (;;) {
            drainAudio(wav);
            if (s.m_inputChannels[0].data.size() == 0) break;
            s.renderAudio();
            drainAudio(wav);
            s.endInputBlock();
        }
    }

    void discardAudioInput() {
        Synthesizer &s = sim->synthesizer();
        for (int i = 0; i < s.m_inputChannelCount; ++i) {
            s.m_inputChannels[i].data.removeBeginning(s.m_inputChannels[i].data.size());
        }
    }

    void applyLoad() {
        if (load == LoadMode::Quadratic) {
            const double w = engine->getSpeed();
            sim->m_dyno.m_enabled = true;
            sim->m_dyno.m_hold = false;
            sim->m_dyno.m_rotationSpeed = 0.0;
            sim->m_dyno.m_maxTorque = loadRefTorque * (w / loadRefSpeed) * (w / loadRefSpeed);
        }
    }

    void setDynoHold(double speed) {
        load = LoadMode::DynoHold;
        sim->m_dyno.m_enabled = true;
        sim->m_dyno.m_hold = true;
        sim->m_dyno.m_rotationSpeed = speed;
        sim->m_dyno.m_maxTorque = units::torque(10000.0, units::ft_lb);
    }

    void setNoLoad() {
        load = LoadMode::None;
        sim->m_dyno.m_enabled = false;
        sim->m_dyno.m_hold = false;
    }

    // One 60 Hz game frame, exactly like EngineSimApplication::process() minus UI.
    void frame(double dt = 1.0 / 60.0) {
        const unsigned long long y0 = threadCycles();
        const double w0 = wallSeconds();

        sim->startFrame(dt);
        const int n = sim->getFrameIterationCount();
        while (sim->simulateStep()) {
            applyLoad();
        }
        sim->endFrame();

        const unsigned long long y1 = threadCycles();
        const double w1 = wallSeconds();

        if (renderAudio) pumpAudio();
        else discardAudioInput();

        const unsigned long long y2 = threadCycles();
        const double w2 = wallSeconds();

        physicsWall += w1 - w0; audioWall += w2 - w1;
        physicsCycles += y1 - y0; audioCycles += y2 - y1;

        steps += n;
        simTime += n * sim->getTimestep();
        ++frames;
        if (g_topology.onPCore()) ++framesOnPCore;
        sumRpm += rpm();
        sumTorque += torqueNm();
    }

    void resetCounters() {
        simTime = 0; steps = 0; physicsWall = audioWall = 0;
        physicsCycles = audioCycles = 0; frames = framesOnPCore = 0;
        sumRpm = sumTorque = 0;
    }

    double rpm() const { return engine->getRpm(); }
    double torqueNm() const { return std::fabs(sim->getFilteredDynoTorque()); }
    double powerKw() const { return std::fabs(sim->getDynoPower()) / 1000.0; }
    double mapKpa() const { return engine->getManifoldPressure() / 1000.0; }

    // crank until the engine fires and sustains, then release the starter
    bool start(double maxCrankSeconds, bool verbose) {
        engine->setSpeedControl(0.0);
        sim->m_starterMotor.m_enabled = true;
        const double t0 = simTime;
        double aboveTime = 0;
        while (simTime - t0 < maxCrankSeconds) {
            frame();
            if (rpm() > 1.3 * units::toRpm(engine->getStarterSpeed())) aboveTime += 1.0 / 60.0;
            if (aboveTime > 0.25) break;
        }
        sim->m_starterMotor.m_enabled = false;
        if (verbose) std::printf("  starter released after %.2f s simulated, rpm=%.0f\n", simTime - t0, rpm());
        return aboveTime > 0.25;
    }

    void runFor(double seconds) {
        const double t0 = simTime;
        while (simTime - t0 < seconds) frame();
    }
};

void printHeader() {
    std::printf("  %6s %7s %8s %8s %8s %8s %8s\n", "t[s]", "pedal", "rpm", "T[Nm]", "T[lbft]", "P[kW]", "MAP[kPa]");
}

void printRow(Runner &r, double t, double pedal) {
    std::printf("  %6.1f %6.0f%% %8.0f %8.1f %8.1f %8.1f %8.1f\n", t, pedal * 100, r.rpm(), r.torqueNm(),
        r.torqueNm() / units::ft_lb, r.powerKw(), r.mapKpa());
}

struct BenchResult {
    double simSeconds = 0;
    double physicsCpuMs = 0, audioCpuMs = 0;      // per simulated second
    double physicsWallMs = 0, audioWallMs = 0;    // per simulated second
    double pCoreFraction = 0, meanRpm = 0, meanTorque = 0, wotTorque4000 = 0;
    bool started = false;
    double totalCpuMs() const { return physicsCpuMs + audioCpuMs; }
    double totalWallMs() const { return physicsWallMs + audioWallMs; }
};

BenchResult collect(const Runner &r) {
    BenchResult b;
    b.simSeconds = r.simTime;
    const double s = std::max(1e-9, r.simTime);
    b.physicsCpuMs = 1000.0 * (r.physicsCycles / g_tscHz) / s;
    b.audioCpuMs = 1000.0 * (r.audioCycles / g_tscHz) / s;
    b.physicsWallMs = 1000.0 * r.physicsWall / s;
    b.audioWallMs = 1000.0 * r.audioWall / s;
    b.pCoreFraction = r.frames > 0 ? (double)r.framesOnPCore / r.frames : 0;
    b.meanRpm = r.frames > 0 ? r.sumRpm / r.frames : 0;
    b.meanTorque = r.frames > 0 ? r.sumTorque / r.frames : 0;
    return b;
}

// One self-contained benchmark job: build, start, warm up at 60 % pedal on the fixed load,
// time `seconds` of simulated running, optionally finish with a WOT 4000 rpm dyno-hold check.
BenchResult benchJob(const std::string &root, const RigOptions &opt, bool audio, double seconds,
    int pin /*0 none,1 P,2 E*/, bool fidelityCheck, std::atomic<int> *startGate = nullptr, int gateCount = 0)
{
    g_topology.pinCurrentThread(pin);

    EngineRig rig = buildSubaruEj25(root, opt);
    Runner r;
    r.attach(rig);
    r.renderAudio = audio;
    const bool started = r.start(4.0, false);
    r.load = LoadMode::Quadratic;
    r.engine->setSpeedControl(0.6);
    r.runFor(2.0);

    if (startGate != nullptr) {   // line all threads up so the timed sections overlap
        startGate->fetch_add(1);
        while (startGate->load() < gateCount) std::this_thread::yield();
    }

    r.resetCounters();
    r.runFor(seconds);
    BenchResult b = collect(r);
    b.started = started;

    if (fidelityCheck) {
        r.engine->setSpeedControl(1.0);
        r.setDynoHold(units::rpm(4000));
        r.runFor(2.0);
        double sumT = 0; int n = 0;
        const double t0 = r.simTime;
        while (r.simTime - t0 < 1.0) { r.frame(); sumT += r.torqueNm(); ++n; }
        b.wotTorque4000 = sumT / n;
    }

    destroyRig(&rig);
    return b;
}

void printBenchHeader() {
    std::printf("  %-36s %9s %9s %9s %9s %7s %6s %6s %7s %9s\n",
        "configuration", "phys cpu", "audio cpu", "total cpu", "wall", "RTx", "Pcore", "rpm", "T[Nm]", "WOT@4k Nm");
    std::printf("  %-36s %9s %9s %9s %9s\n", "", "ms/sim-s", "ms/sim-s", "ms/sim-s", "ms/sim-s");
}

void printBench(const char *label, const BenchResult &b) {
    std::printf("  %-36s %9.1f %9.1f %9.1f %9.1f %7.2f %5.0f%% %6.0f %7.1f %9.1f%s\n",
        label, b.physicsCpuMs, b.audioCpuMs, b.totalCpuMs(), b.totalWallMs(),
        1000.0 / std::max(1e-9, b.totalWallMs()), 100 * b.pCoreFraction, b.meanRpm, b.meanTorque,
        b.wotTorque4000, b.started ? "" : "  (DID NOT START)");
}

void writeClip(const std::string &root, const RigOptions &opt, const std::string &path) {
    EngineRig rig = buildSubaruEj25(root, opt);
    std::vector<int16_t> wav;
    Runner r;
    r.attach(rig);
    r.wav = &wav;
    r.renderAudio = true;
    r.start(4.0, false);
    r.runFor(2.0);
    r.load = LoadMode::Quadratic;
    r.engine->setSpeedControl(0.6);
    r.runFor(4.0);
    r.engine->setSpeedControl(1.0);
    r.runFor(4.0);
    r.setNoLoad();
    r.engine->setSpeedControl(0.0);
    r.runFor(2.0);
    writeWav16Mono(path, wav, 44100);
    std::printf("  wrote %s (%.1f s) [sim %d Hz, %d fluid steps]\n", path.c_str(), wav.size() / 44100.0,
        rig.simulator->getSimulationFrequency(), static_cast<PistonEngineSimulator *>(rig.simulator)->getFluidSimulationSteps());
    destroyRig(&rig);
}

} // namespace

int main(int argc, char **argv) {
    bool quick = false;
    std::string parts = "ABCDEF";
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--quick") == 0) quick = true;
        else if (std::strcmp(argv[i], "--parts") == 0 && i + 1 < argc) parts = argv[++i];
    }
    auto want = [&](char p) { return parts.find(p) != std::string::npos; };
    setvbuf(stdout, nullptr, _IONBF, 0);

    const std::string root = ES_ROOT_DIR;
    g_topology.load();
    calibrateTsc();
    std::printf("engine-sim headless probe\n");
    std::printf("CPU: %s | logical processors: %u | P-cores: %d | E-cores: %d | TSC %.3f GHz\n",
        cpuName().c_str(), std::thread::hardware_concurrency(), g_topology.pCount, g_topology.eCount, g_tscHz * 1e-9);
#ifdef PROBE_FAST_MATH_BUILD
    std::printf("build: /fp:fast /arch:AVX2\n");
#else
    std::printf("build: default MSVC Release flags (/O2 /fp:precise, SSE2)\n");
#endif

    // Construct one Function on the main thread first: Function::DefaultGaussianFilter is a lazily
    // created static, which would race if the first engines were built on worker threads.
    { Function warm; }

    const double benchSeconds = quick ? 3.0 : 6.0;

    // =====================================================================================
    // PART A: start -> idle -> 25 / 60 / 100 % pedal against a fixed (quadratic) load, with audio
    // =====================================================================================
    if (want('A')) {
        std::vector<int16_t> wav;
        std::printf("\n=== PART A: Subaru EJ25 (hand-built from 01_subaru_ej25_eh.mr), 20 kHz, 8 fluid steps, audio ON ===\n");
        RigOptions opt;
        EngineRig rig = buildSubaruEj25(root, opt);
        std::printf("  displacement %.3f L, cylinders %d, sim freq %d Hz, fluid steps %d, IR taps %d\n",
            rig.engine->getDisplacement() / units::L, rig.engine->getCylinderCount(),
            rig.simulator->getSimulationFrequency(),
            static_cast<PistonEngineSimulator *>(rig.simulator)->getFluidSimulationSteps(),
            rig.impulseResponseSamples);

        Runner r;
        r.attach(rig);
        r.wav = &wav;
        r.renderAudio = true;

        const bool started = r.start(5.0, true);
        std::printf("  started: %s\n", started ? "yes" : "NO");

        std::printf("  idle (no load, pedal 0):\n");
        printHeader();
        for (int i = 0; i < 3; ++i) { r.runFor(1.0); printRow(r, r.simTime, 0.0); }

        std::printf("  fixed load: T_load = %.0f Nm * (rpm/5000)^2, applied through engine-sim's Dynamometer used as a brake\n", r.loadRefTorque);
        r.load = LoadMode::Quadratic;
        const double pedals[3] = { 0.25, 0.60, 1.00 };
        for (double pedal : pedals) {
            r.engine->setSpeedControl(pedal);
            std::printf("  --- pedal %.0f%% for 10 s ---\n", pedal * 100);
            printHeader();
            double sRpm = 0, sT = 0, sP = 0; int nAvg = 0;
            for (int s = 1; s <= 10; ++s) {
                const double t0 = r.simTime;
                while (r.simTime - t0 < 1.0) {
                    r.frame();
                    if (s > 7) { sRpm += r.rpm(); sT += r.torqueNm(); sP += r.powerKw(); ++nAvg; }
                }
                printRow(r, r.simTime, pedal);
            }
            std::printf("  => steady state (last 3 s mean): %.0f rpm, %.1f Nm (%.1f lb-ft), %.1f kW (%.1f hp)\n",
                sRpm / nAvg, sT / nAvg, sT / nAvg / units::ft_lb, sP / nAvg, sP / nAvg * 1000 / units::hp);
        }
        std::printf("  back to idle for 2 s\n");
        r.setNoLoad();
        r.engine->setSpeedControl(0.0);
        r.runFor(2.0);

        BenchResult b = collect(r);
        b.started = started;
        std::printf("  PART A totals: %.1f s simulated, %lld solver steps\n", b.simSeconds, r.steps);
        printBenchHeader();
        printBench("Part A whole run (audio ON, unpinned)", b);
        destroyRig(&rig);

        const std::string wavPath = "D:/Backroads-ref/probe/probe_out.wav";
        if (writeWav16Mono(wavPath, wav, 44100)) {
            std::printf("  wrote %s (%zu samples = %.1f s @ 44.1 kHz mono 16-bit)\n", wavPath.c_str(), wav.size(), wav.size() / 44100.0);
        }
    }

    // =====================================================================================
    // PART B: dyno-hold sanity sweep at wide-open throttle (compare with real EJ25 figures)
    // =====================================================================================
    if (want('B')) {
        std::printf("\n=== PART B: WOT dyno-hold sweep (Dynamometer m_hold=true), audio OFF ===\n");
        RigOptions opt;
        EngineRig rig = buildSubaruEj25(root, opt);
        Runner r;
        r.attach(rig);
        r.renderAudio = false;
        r.start(5.0, false);
        r.engine->setSpeedControl(1.0);
        std::printf("  %8s %8s %8s %8s %8s\n", "hold rpm", "T[Nm]", "T[lbft]", "P[kW]", "P[hp]");
        for (int rpmHold = 2000; rpmHold <= 6500; rpmHold += 500) {
            r.setDynoHold(units::rpm(rpmHold));
            r.runFor(2.0);
            double sumT = 0; int n = 0;
            const double t0 = r.simTime;
            while (r.simTime - t0 < 1.0) { r.frame(); sumT += r.torqueNm(); ++n; }
            const double T = sumT / n;
            const double P = T * units::rpm(rpmHold);
            std::printf("  %8d %8.1f %8.1f %8.1f %8.1f   (actual rpm %.0f)\n", rpmHold, T, T / units::ft_lb, P / 1000, P / units::hp, r.rpm());
        }
        destroyRig(&rig);
    }

    // =====================================================================================
    // PART C: single-thread CPU cost per simulated second
    // =====================================================================================
    if (want('C')) {
        std::printf("\n=== PART C: single-thread cost, steady 60%% pedal on the fixed load, %.0f s simulated each ===\n", benchSeconds);
        struct Variant { const char *name; int freq; int fluid; bool audio; bool ir; int pin; };
        const Variant variants[] = {
            { "20k/8 audio+IR  unpinned",       0, 8, true, true, 0 },
            { "20k/8 audio+IR  P-core",         0, 8, true, true, 1 },
            { "20k/8 audio+IR  E-core",         0, 8, true, true, 2 },
            { "20k/8 physics only  P-core",     0, 8, false, true, 1 },
            { "20k/8 physics only  E-core",     0, 8, false, true, 2 },
            { "20k/8 audio, 1-tap IR  P-core",  0, 8, true, false, 1 },
            { "20k/4 physics only  P-core",     0, 4, false, true, 1 },
            { "10k/8 physics only  P-core",     10000, 8, false, true, 1 },
            { "10k/4 physics only  P-core",     10000, 4, false, true, 1 },
            { "10k/4 physics only  E-core",     10000, 4, false, true, 2 },
            { "10k/4 audio+IR  P-core",         10000, 4, true, true, 1 },
            { "5k/4 physics only  P-core",      5000, 4, false, true, 1 },
            { "5k/2 physics only  P-core",      5000, 2, false, true, 1 },
            { "5k/2 physics only  E-core",      5000, 2, false, true, 2 },
        };
        printBenchHeader();
        for (const Variant &v : variants) {
            RigOptions opt;
            opt.simulationFrequency = v.freq;
            opt.fluidSimulationSteps = v.fluid;
            opt.loadImpulseResponse = v.ir;
            BenchResult b;
            std::thread t([&] { b = benchJob(root, opt, v.audio, benchSeconds, v.pin, true); });
            t.join();
            printBench(v.name, b);
        }
    }

    // =====================================================================================
    // PART D: N identical full-quality engines in parallel threads, unpinned
    // =====================================================================================
    if (want('D')) {
        const int counts[] = { 1, 2, 4, 8, 16 };
        for (int pass = 0; pass < 2; ++pass) {
            const bool audio = (pass == 0);
            std::printf("\n=== PART D%d: N parallel engines, 20k/8, %s, %.0f s simulated per engine (timed sections overlap) ===\n",
                pass + 1, audio ? "physics + audio (IR convolution)" : "physics only", benchSeconds);
            for (int n : counts) {
                std::vector<BenchResult> results(n);
                std::vector<std::thread> threads;
                std::atomic<int> gate{ 0 };
                for (int i = 0; i < n; ++i) {
                    threads.emplace_back([&, i] {
                        RigOptions opt;
                        results[i] = benchJob(root, opt, audio, benchSeconds, 0, false, &gate, n);
                    });
                }
                for (auto &t : threads) t.join();

                double sumCpu = 0, sumPhys = 0, maxWall = 0, minWall = 1e30, sumP = 0;
                for (const auto &b : results) {
                    sumCpu += b.totalCpuMs();
                    sumPhys += b.physicsCpuMs;
                    maxWall = std::max(maxWall, b.totalWallMs());
                    minWall = std::min(minWall, b.totalWallMs());
                    sumP += b.pCoreFraction;
                }
                std::printf("  N=%2d  per-engine cpu mean %6.1f ms/sim-s (phys %6.1f) | per-engine wall min %6.1f max %6.1f ms/sim-s | slowest RTx %5.2f | P-core %3.0f%% | total %5.2f cores\n",
                    n, sumCpu / n, sumPhys / n, minWall, maxWall, 1000.0 / maxWall, 100 * sumP / n, sumCpu / 1000.0);
            }
        }
    }

    // =====================================================================================
    // PART E: 1 local full-quality engine (20k/8 + audio + IR) + N lightweight remote engines
    // =====================================================================================
    if (want('E')) {
        struct Remote { const char *name; int freq; int fluid; };
        const Remote remotes[] = { { "10k/4 physics only", 10000, 4 }, { "5k/2 physics only", 5000, 2 } };
        const int counts[] = { 3, 7, 15, 23 };
        for (const Remote &rem : remotes) {
            std::printf("\n=== PART E: 1 local (20k/8 audio+IR) + N remote (%s), all unpinned, %.0f s simulated ===\n", rem.name, benchSeconds);
            for (int n : counts) {
                BenchResult local;
                std::vector<BenchResult> results(n);
                std::vector<std::thread> threads;
                std::atomic<int> gate{ 0 };
                threads.emplace_back([&] {
                    RigOptions opt;
                    local = benchJob(root, opt, true, benchSeconds, 0, false, &gate, n + 1);
                });
                for (int i = 0; i < n; ++i) {
                    threads.emplace_back([&, i] {
                        RigOptions opt;
                        opt.simulationFrequency = rem.freq;
                        opt.fluidSimulationSteps = rem.fluid;
                        results[i] = benchJob(root, opt, false, benchSeconds, 0, false, &gate, n + 1);
                    });
                }
                for (auto &t : threads) t.join();
                double sumCpu = 0, maxWall = 0;
                for (const auto &b : results) { sumCpu += b.totalCpuMs(); maxWall = std::max(maxWall, b.totalWallMs()); }
                std::printf("  N=%2d  local: cpu %6.1f wall %6.1f ms/sim-s RTx %5.2f P-core %3.0f%% | remote: cpu mean %5.1f ms/sim-s, slowest wall %6.1f (RTx %5.2f) | all engines %5.2f cores\n",
                    n, local.totalCpuMs(), local.totalWallMs(), 1000.0 / local.totalWallMs(), 100 * local.pCoreFraction,
                    sumCpu / n, maxWall, 1000.0 / maxWall, (sumCpu + local.totalCpuMs()) / 1000.0);
            }
        }
    }

    // =====================================================================================
    // PART F: audio clips of the lightweight settings for listening comparison
    // =====================================================================================
    if (want('F')) {
        std::printf("\n=== PART F: audio clips (idle 2 s, 60%% 4 s, 100%% 4 s, idle 2 s) ===\n");
        RigOptions o10; o10.simulationFrequency = 10000; o10.fluidSimulationSteps = 4;
        writeClip(root, o10, "D:/Backroads-ref/probe/probe_out_10k4.wav");
        RigOptions o5; o5.simulationFrequency = 5000; o5.fluidSimulationSteps = 2;
        writeClip(root, o5, "D:/Backroads-ref/probe/probe_out_5k2.wav");
    }

    std::printf("\nDone.\n");
    return 0;
}
