// harness.h: shared code of esp_cli and the ctest executables. Everything here uses the public C ABI
// (EngineSimPlugin.h) only, so the tests exercise exactly what Unity calls.
#pragma once

#include "EngineSimPlugin.h"

#include <cstdint>
#include <cstdio>
#include <functional>
#include <map>
#include <string>
#include <vector>

namespace esph {

// ---- paths (ESP_ES_DIR / ESP_ENGINES_DIR override; default: the vendored tree next to this source) ----
std::string esDir();       // vendor/engine-sim/es
std::string enginesDir();  // vendor/engine-sim/assets/engines
std::string tempDir();     // %TEMP%\esp-harness
std::string outDir();      // ESP_OUT or the current directory

// ---- engines ----
struct CreateOpts {
    int audioRate = 48000;  // 0 = no audio
    int workerThread = 1;
    int simFrequency = 0;
    int fluidSteps = 0;
    int firMaxTaps = 0;
    int flags = 0;
    int ringFrames = 0;
    int targetFrames = 0;
};

class Engine {
public:
    Engine() = default;
    ~Engine();
    Engine(const Engine &) = delete;
    Engine &operator=(const Engine &) = delete;

    // name: a builtin id, or a path ending in .mr (topNode for scripts without main).
    bool open(const std::string &name, const CreateOpts &o, std::string &err, const std::string &topNode = "");
    EspEngine *h() const { return m_e; }
    const EspEngineInfo &info() const { return m_info; }
    void close();

private:
    EspEngine *m_e = nullptr;
    EspEngineInfo m_info{};
};

EspControls controls(float throttle, bool ignition, bool starter, float simSpeed = 1.0f, float frameDt = 0.0f,
                     int frameIndex = 0);
EspDrivelineIn driveline(int mode);
EspStepOut stepOut();
EspStats stats(EspEngine *e);

// Drains the plugin log to stderr (prefix "  ").
void drainLog(FILE *to = stderr);

// Polls esp_step_end (timeout 0) and yields between polls until step k is there; false after maxS seconds.
bool waitStep(EspEngine *e, int64_t k, EspStepOut &out, double maxS = 10.0);

// ---- a synchronous driver for worker engines: kick step k, collect step k (waiting), like a fork-join ----
struct Driver {
    EspEngine *e = nullptr;
    int64_t k = 0;
    double dt = 1.0 / 120.0;
    double t = 0.0;
    EspStepOut last{};
    // Runs one physics step and returns its result (waits up to 2 s).
    bool step(const EspDrivelineIn &in, const double *crank = nullptr, int nCrank = 0);
};

// ---- audio analysis (PLAN 1.9 pitch rule [T12]) ----
struct PitchCheck {
    double rpm = 0;
    double expectedFiringHz = 0;  // rpm/60 * cyl/2
    double acfHz = 0;             // dominant period of the autocorrelation, as a frequency
    double acfOrder = 0;          // acfHz / (rpm/120)
    double peakHz = 0;            // strongest spectral peak 20-1000 Hz
    double peakOrder = 0;
    bool acfPass = false, peakPass = false;
    bool pass() const { return acfPass || peakPass; }
    std::string describe() const;
};
// orders: accepted cycle orders (frequency / (rpm/120)); empty = the firing rule k in {1,2,4,8} for cyl cylinders
// plus any integer cycle harmonic. tol: relative tolerance (0.03).
PitchCheck checkPitch(const float *x, size_t n, int rate, double rpm, int cylinders,
                      const std::vector<double> &orders = {}, double tol = 0.03);
double rmsDb(const float *x, size_t n);
double clipFraction(const float *x, size_t n, float limit = 0.999f);
bool writeWav(const std::string &path, const std::vector<float> &x, int rate);

// ---- frame traces (cosim, step accounting, soak) ----
// Frame durations in seconds. A file has one number per line (ms), or a frametimes.csv with a "frame_ms" column.
std::vector<double> loadFrameTrace(const std::string &path, std::string &err);
std::vector<double> syntheticFrameTrace(double fps, double seconds, uint32_t seed);
// Unity's fixed-step loop (PLAN 1.3: fixedDeltaTime 1/120, maximumDeltaTime 4/120): steps run in each frame.
std::vector<int> stepsPerFrame(const std::vector<double> &frames, double fixedDt = 1.0 / 120, double maxDelta = 4.0 / 120);

// ---- tiny JSON writer ----
class Json {
public:
    Json &obj(const char *key = nullptr);
    Json &end();
    Json &arr(const char *key = nullptr);
    Json &endArr();
    Json &num(const char *k, double v, int prec = 4);
    Json &integer(const char *k, int64_t v);
    Json &str(const char *k, const std::string &v);
    Json &boolean(const char *k, bool v);
    Json &raw(const char *k, const std::string &v);
    std::string text() const { return m_s; }
    bool save(const std::string &path) const;

private:
    void pre(const char *k);
    std::string m_s;
    std::vector<bool> m_need;
};

// ---- small helpers ----
double nowS();
std::string fmt(const char *f, ...);
// Splits "a,b,c".
std::vector<std::string> split(const std::string &s, char sep);
// Command-line flags: --name value, --flag.
struct Args {
    std::vector<std::string> pos;
    std::map<std::string, std::string> kv;
    Args(int argc, char **argv, int first);
    std::string get(const std::string &k, const std::string &d = "") const;
    double num(const std::string &k, double d) const;
    bool has(const std::string &k) const { return kv.count(k) > 0; }
};

// Process-wide affinity to the performance or efficiency cores ("P", "E", "" = unchanged). Returns the mask used.
uint64_t setProcessAffinityClass(const std::string &cls, std::string &note);

// ---- reports shared by esp_cli and the ctests ----
// Each prints a human table to stdout, writes <out>.json when outJson is set, returns 0 if the gate passed.
int runStartGate(const std::string &engine, const std::string &outJson);
int runDynoGate(const std::string &engine, const std::string &outJson, bool gate);
int runFollowGate(const std::string &engine, const std::string &outJson);
int runStepAccounting(double timeScale, double seconds, const std::string &outJson);
int runPitchGate(const std::string &engine, const std::string &outJson, const std::string &wavPrefix);
int runCosimMatrix(const std::string &traceDir, const std::string &outJson, int threads);
int runBench(const std::string &engine, const std::string &affinity, double seconds, const std::string &quality,
             const std::string &outJson);
int runSoak(const std::string &engine, double minutes, const std::string &outJson, int simFrequency = 0, int fluidSteps = 0,
            int firMaxTaps = 0);
int runAllEngines(const std::string &outJson, int threads, bool hardRoster);
int runSanitiseAll(const std::string &outJson, int threads);

} // namespace esph
