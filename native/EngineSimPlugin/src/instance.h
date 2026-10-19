// instance.h: one engine (PLAN 3.4). Owns the engine objects, the UnitySim, the worker thread, the step-indexed
// command and result rings, the fractional step accumulator, coast mode and the audio path.
//
// Threads: the control thread (Unity main) calls everything except readAudio(); the worker runs process() and
// coasting; the audio thread calls readAudio() only. m_simLock serialises the worker against offline calls (sweeps);
// the hot paths (commands, results, audio) are lock-free.
#pragma once

#include "EngineSimPlugin.h"
#include "audio_render.h"
#include "builders/builders.h"
#include "spsc_ring.h"
#include "unity_sim.h"
#include "unity_transmission.h"

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace esp {

struct Command {
    int64_t stepIndex;
    double dt;
    EspControls controls;
    EspDrivelineIn driveline;
    double crank[ESP_MAX_CRANK_SAMPLES];
    int32_t nCrank;
    int64_t kickQpc;
};

struct ResultSlot {
    std::atomic<int64_t> step{-1};  // step published here; -2 while being written
    EspStepOut out;
};

class Instance {
public:
    static constexpr uint32_t kMagic = 0x31505345u;  // "ESP1"

    Instance(const EspCreateDesc &desc, const std::string &esDir, const std::string &tempDir);
    ~Instance();
    Instance(const Instance &) = delete;
    Instance &operator=(const Instance &) = delete;

    bool valid() const { return m_magic == kMagic; }

    int32_t loadBuiltin(const std::string &name, std::string &err);
    int32_t loadScript(const std::string &path, const std::string &topNode, std::string &err);
    int32_t getInfo(EspEngineInfo &out) const;

    int32_t setControls(const EspControls &c);
    int32_t setAudioParams(const EspAudioParams &p);
    int32_t setQuality(int32_t fluidSteps, int32_t firMaxTaps);
    int32_t setWorkerOptions(const EspWorkerOptions &o);

    int32_t step(int64_t index, double dt, const EspDrivelineIn *in, const double *crank, int32_t n, EspStepOut *out);
    int32_t stepBegin(int64_t index, double dt, const EspDrivelineIn *in, const double *crank, int32_t n);
    int32_t stepEnd(int64_t index, int32_t timeoutUs, EspStepOut *out);
    int32_t readAudio(float *dst, int32_t frames, int32_t channels) { return m_out.read(dst, frames, channels); }

    int32_t getStats(EspStats &out);
    int32_t debugTap(const char *pathUtf8, double seconds);

    // offline.cpp
    int32_t dynoSweep(double rpmFrom, double rpmTo, int32_t points, float throttle, double settleS, double *torque,
                      double *power);
    int32_t calibrateInertia(double *jEff);
    int32_t renderOffline(double rpm, float throttle, double seconds, float *dst, int32_t frames);

private:
    struct StepAcc;

    int32_t finishLoad(EngineBundle &bundle, std::string &err);
    std::vector<std::vector<float>> loadImpulseResponses(std::string &warnings);
    void startWorker();
    void stopWorker();

    // Command processing (worker, or the caller with worker_thread = 0); m_simLock held.
    void applyPending();
    void applyControls(const EspControls &c);
    int32_t modeOf(const Command &c) const;
    void enterMode(const Command &c);
    void runSteps(int64_t n, const Command &c, StepAcc &acc);
    void process(const Command &c, bool afterCoast);
    void coastBatch();
    void fillOut(const Command &c, const StepAcc &acc, int64_t n, double cpuS, double wallS, int32_t flags,
                 EspStepOut &o);
    void publish(const EspStepOut &o);
    void updateSlope(double omega, double torque, double dt);

    void workerMain();
    // Worker wait for the next command or for `seconds`, whichever comes first, on a high-resolution waitable timer
    // (independent of the process timer resolution); falls back to WaitForSingleObject's millisecond timeout.
    void workerWait(double seconds);
    // Underrun diagnostics (worker): every produced batch is noted; a new underrun logs the batches before it.
    void notePush(double startS, double wallS, double cpuS, double kickLatencyS, size_t fillBefore, size_t pushed,
                  bool coast, int64_t step);
    void logWorkerSummary();

    // offline helpers (m_simLock held)
    void offlineFrames(double seconds, double frameDt, bool render, std::vector<float> *audio,
                       double *torqueSum, int *torqueN);

    uint32_t m_magic = kMagic;
    EspCreateDesc m_desc{};
    std::string m_esDir, m_tempDir;

    // engine
    EngineBundle m_bundle;
    UnitySim *m_sim = nullptr;
    UnityTransmission *m_trans = nullptr;
    bool m_loaded = false;
    int m_freq = 10000;
    int m_fluid = 8;
    bool m_audio = false;
    int m_rate = 0;
    double m_crankJ = 0.0;
    double m_revLimit = 0.0;

    // audio
    AudioOutput m_out;
    AudioRenderer m_renderer;
    FillController m_fill;
    AudioTapBuffer m_tap;
    std::mutex m_tapLock;
    bool m_tapPending = false;
    AudioTapBuffer m_tapRequest;

    // control thread
    EspControls m_controls{};
    int64_t m_lastKicked = INT64_MIN;
    bool m_anyKick = false;

    // rings
    SpscQueue<Command, ESP_COMMAND_SLOTS> m_cmds;
    ResultSlot m_results[ESP_RESULT_SLOTS];
    std::atomic<int64_t> m_lastPublished{INT64_MIN};

    // worker
    std::thread m_worker;
    void *m_wake = nullptr;  // auto-reset event
    void *m_timer = nullptr; // high-resolution waitable timer for the worker's timed waits (null: not available)
    bool m_lowWaterCoast = true;  // coast as soon as the ring would run dry before the next command (ESP_COAST_LOWWATER=0: off)
    std::atomic<bool> m_stop{false};
    std::atomic<int64_t> m_lastKickQpc{0};
    std::atomic<int32_t> m_coastMaxMs{300};
    std::atomic<bool> m_offline{false};
    std::mutex m_simLock;

    // processing state (worker side)
    double m_sumDtF = 0.0;
    int64_t m_target = 0;
    int64_t m_executed = 0;
    double m_simTime = 0.0;
    int32_t m_mode = -1;
    double m_followPrev = 0.0;
    double m_simSpeed = 1.0;
    Command m_lastCmd{};
    bool m_haveLastCmd = false;
    bool m_coasting = false;
    double m_coastStartS = 0.0, m_coastNextS = 0.0;
    double m_lastProduceS = 0.0;

    // underrun diagnostics (worker only)
    struct PushNote {
        double t, gapMs, fillBeforeMs, fillAfterMs, wallMs, cpuMs, kickLatencyMs;
        int64_t step;
        bool coast;
    };
    struct WorkerDiag {
        PushNote hist[24] = {};
        int next = 0, count = 0;
        double t0 = 0.0, lastPushS = 0.0;
        double maxGapMs = 0.0, maxGapAtS = 0.0, minFillBeforeMs = 1e9, minFillAtS = 0.0;
        double maxKickLatencyMs = 0.0, maxDeschedMs = 0.0, maxWallMs = 0.0;
        int64_t pushes = 0, lowFill[4] = {};  // fill before a push below 2, 5, 10, 15 ms
        int32_t underrunsSeen = 0, underrunsLogged = 0, lowWaterCoasts = 0;
    } m_diag;
    double m_slope = 0.0, m_mx = 0.0, m_my = 0.0, m_sxx = 0.0, m_sxy = 0.0;
    bool m_slopeInit = false;
    double m_peakHint = 0.0, m_idleHint = 800.0;
    bool m_warnedSteps = false;

    // pending settings (control thread -> worker)
    std::mutex m_pendingLock;
    std::atomic<bool> m_pendingDirty{false};
    bool m_pendingAudio = false;
    EspAudioParams m_pendingAudioParams{};
    int32_t m_pendingFluid = 0, m_pendingFir = 0;

    // stats
    mutable std::mutex m_statsLock;
    struct Stats {
        int64_t commands = 0, stepsRun = 0, stepsTarget = 0, borrowed = 0, coastSteps = 0, offlineSteps = 0;
        int32_t coastEvents = 0;
        double cpuEma = 0.0, wallEma = 0.0;
        double simSeconds = 0.0, wallSeconds = 0.0, cpuSeconds = 0.0;
        int64_t pCommands = 0;
    } m_stats;
    std::atomic<int32_t> m_stale{0};
    double m_waitTotalMs = 0.0, m_waitMaxMs = 0.0;  // control thread only
};

} // namespace esp
