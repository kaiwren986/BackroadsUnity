// harness.cpp: see harness.h (paths, engines, driver, analysis, traces, JSON, helpers).
#include "harness.h"

#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstring>
#include <fstream>
#include <random>
#include <sstream>

#ifndef ESP_VENDOR_DIR
#define ESP_VENDOR_DIR "vendor/engine-sim"
#endif

namespace esph {

namespace {

std::string envOr(const char *name, const std::string &fallback) {
    char buf[1024];
    size_t len = 0;
    if (getenv_s(&len, buf, sizeof buf, name) == 0 && len > 1) return std::string(buf);
    return fallback;
}

constexpr double kPi = 3.14159265358979323846;

} // namespace

std::string esDir() { return envOr("ESP_ES_DIR", std::string(ESP_VENDOR_DIR) + "/es"); }
std::string enginesDir() { return envOr("ESP_ENGINES_DIR", std::string(ESP_VENDOR_DIR) + "/assets/engines"); }
std::string tempDir() {
    char tmp[MAX_PATH + 1];
    const DWORD n = GetTempPathA(sizeof tmp, tmp);
    std::string t = (n > 0 && n < sizeof tmp) ? std::string(tmp) : std::string(".\\");
    t += "esp-harness";
    CreateDirectoryA(t.c_str(), nullptr);
    return t;
}
std::string outDir() { return envOr("ESP_OUT", "."); }

// ---------------------------------------------------------------------------------------------------------------

Engine::~Engine() { close(); }

void Engine::close() {
    if (m_e != nullptr) esp_destroy(m_e);
    m_e = nullptr;
}

bool Engine::open(const std::string &name, const CreateOpts &o, std::string &err, const std::string &topNode) {
    close();
    EspCreateDesc d{};
    d.struct_size = sizeof d;
    d.audio_sample_rate = o.audioRate;
    d.audio_ring_frames = o.ringFrames;
    d.audio_target_frames = o.targetFrames;
    d.audio_dsp_buffer_frames = 256;
    d.sim_frequency = o.simFrequency;
    d.fluid_steps = o.fluidSteps;
    d.worker_thread = o.workerThread;
    d.fir_max_taps = o.firMaxTaps;
    d.flags = o.flags;
    m_e = esp_create(&d, esDir().c_str(), tempDir().c_str());
    if (m_e == nullptr) {
        err = "esp_create failed";
        drainLog();
        return false;
    }
    char e[2048] = {};
    int32_t r;
    const bool script = name.size() > 3 && name.compare(name.size() - 3, 3, ".mr") == 0;
    if (script) r = esp_load_script(m_e, name.c_str(), topNode.empty() ? nullptr : topNode.c_str(), e, sizeof e);
    else r = esp_load_builtin(m_e, name.c_str(), e, sizeof e);
    if (r != ESP_OK) {
        err = fmt("load %s: %d %s", name.c_str(), r, e);
        close();
        return false;
    }
    m_info = EspEngineInfo{};
    m_info.struct_size = sizeof m_info;
    esp_get_info(m_e, &m_info);
    return true;
}

EspControls controls(float throttle, bool ignition, bool starter, float simSpeed, float frameDt, int frameIndex) {
    EspControls c{};
    c.struct_size = sizeof c;
    c.throttle = throttle;
    c.ignition = ignition ? 1 : 0;
    c.starter = starter ? 1 : 0;
    c.sim_speed = simSpeed;
    c.frame_dt_s = frameDt;
    c.frame_index = frameIndex;
    return c;
}

EspDrivelineIn driveline(int mode) {
    EspDrivelineIn d{};
    d.struct_size = sizeof d;
    d.mode = mode;
    return d;
}

EspStepOut stepOut() {
    EspStepOut o{};
    o.struct_size = sizeof o;
    return o;
}

EspStats stats(EspEngine *e) {
    EspStats s{};
    s.struct_size = sizeof s;
    esp_get_stats(e, &s);
    return s;
}

void drainLog(FILE *to) {
    char buf[8192];
    for (;;) {
        const int32_t n = esp_poll_log(buf, sizeof buf);
        if (n <= 0) break;
        std::istringstream in(std::string(buf, static_cast<size_t>(n)));
        std::string line;
        while (std::getline(in, line))
            if (!line.empty()) std::fprintf(to, "  %s\n", line.c_str());
    }
}

bool waitStep(EspEngine *e, int64_t k, EspStepOut &out, double maxS) {
    const double t0 = nowS();
    for (;;) {
        out = stepOut();
        const int32_t r = esp_step_end(e, k, 0, &out);
        if (r == ESP_OK) return true;
        if (r != ESP_PENDING || nowS() - t0 > maxS) return false;
        Sleep(0);
    }
}

bool Driver::step(const EspDrivelineIn &in, const double *crank, int nCrank) {
    const int32_t r = esp_step_begin(e, k, dt, &in, crank, nCrank);
    if (r != ESP_OK) {
        std::fprintf(stderr, "  driver: esp_step_begin(step %lld) returned %d\n", static_cast<long long>(k), r);
        drainLog();
        return false;
    }
    const bool ok = waitStep(e, k, last);
    if (!ok) {
        EspStepOut o = stepOut();
        std::fprintf(stderr, "  driver: no result for step %lld (esp_step_end %d)\n", static_cast<long long>(k),
                     esp_step_end(e, k, 0, &o));
        drainLog();
    }
    ++k;
    t += dt;
    return ok;
}

// ---------------------------------------------------------------------------------------------------------------
// audio analysis

std::string PitchCheck::describe() const {
    return fmt("rpm %.0f firing %.1f Hz | acf %.1f Hz (order %.2f) %s | peak %.1f Hz (order %.2f) %s", rpm,
               expectedFiringHz, acfHz, acfOrder, acfPass ? "ok" : "--", peakHz, peakOrder, peakPass ? "ok" : "--");
}

PitchCheck checkPitch(const float *x, size_t n, int rate, double rpm, int cylinders, const std::vector<double> &orders,
                      double tol) {
    PitchCheck pc;
    pc.rpm = rpm;
    pc.expectedFiringHz = rpm / 60.0 * cylinders / 2.0;
    if (n < 1024 || rate <= 0 || rpm <= 0) return pc;

    // Decimate to about 12 kHz (the engine signal is band-limited below 2 kHz by engine-sim's anti-alias filter).
    const int d = std::max(1, rate / 12000);
    const double r2 = static_cast<double>(rate) / d;
    std::vector<double> y(n / static_cast<size_t>(d));
    for (size_t i = 0; i < y.size(); ++i) {
        double s = 0;
        for (int k = 0; k < d; ++k) s += x[i * d + k];
        y[i] = s / d;
    }
    double mean = 0;
    for (double v : y) mean += v;
    mean /= static_cast<double>(y.size());
    for (double &v : y) v -= mean;
    const size_t m = y.size();

    // Normalised autocorrelation over 20..1000 Hz; the first maximum after the zero-lag lobe has decayed.
    const int lagMin = std::max(2, static_cast<int>(r2 / 1000.0));
    const int lagMax = std::min(static_cast<int>(m / 2), static_cast<int>(r2 / 20.0));
    std::vector<double> r(static_cast<size_t>(lagMax + 2), 0.0);
    for (int lag = 1; lag <= lagMax + 1; ++lag) {
        double s = 0, e0 = 0, e1 = 0;
        const size_t len = m - static_cast<size_t>(lag);
        for (size_t i = 0; i < len; ++i) {
            s += y[i] * y[i + lag];
            e0 += y[i] * y[i];
            e1 += y[i + lag] * y[i + lag];
        }
        r[lag] = (e0 > 0 && e1 > 0) ? s / std::sqrt(e0 * e1) : 0.0;
    }
    int start = lagMin;
    while (start < lagMax && r[start] > 0.0) ++start;  // leave the zero-lag lobe
    int best = -1;
    double bestV = -2.0;
    for (int lag = start; lag <= lagMax; ++lag)
        if (r[lag] > bestV) {
            bestV = r[lag];
            best = lag;
        }
    if (best > 0) {
        double lag = best;
        if (best > 1 && best <= lagMax) {
            const double a = r[best - 1], b = r[best], c = r[best + 1];
            const double den = a - 2 * b + c;
            if (den < 0) lag += 0.5 * (a - c) / den;
        }
        pc.acfHz = r2 / lag;
    }

    // Spectral peak 20..1000 Hz (Hann window, 0.25 Hz steps).
    {
        double bestP = 0;
        for (double f = 20.0; f <= 1000.0; f += 0.25) {
            double re = 0, im = 0;
            const double w = 2 * kPi * f / r2;
            for (size_t i = 0; i < m; ++i) {
                const double win = 0.5 - 0.5 * std::cos(2 * kPi * i / (m - 1));
                re += y[i] * win * std::cos(w * i);
                im -= y[i] * win * std::sin(w * i);
            }
            const double p = re * re + im * im;
            if (p > bestP) {
                bestP = p;
                pc.peakHz = f;
            }
        }
    }

    const double cycle = rpm / 120.0;
    pc.acfOrder = pc.acfHz / cycle;
    pc.peakOrder = pc.peakHz / cycle;
    auto isNear = [&](double order, double want) { return want > 0 && std::fabs(order - want) <= tol * want; };
    std::vector<double> firing = orders;
    if (firing.empty())
        for (int k : {1, 2, 4, 8}) firing.push_back(static_cast<double>(cylinders) / k);
    for (double o : firing)
        if (isNear(pc.acfOrder, o)) pc.acfPass = true;
    if (orders.empty()) {
        const double h = std::round(pc.peakOrder);
        if (h >= 1 && h <= 2.0 * cylinders && isNear(pc.peakOrder, h)) pc.peakPass = true;
    } else {
        for (double o : orders)
            if (isNear(pc.peakOrder, o)) pc.peakPass = true;
    }
    return pc;
}

double rmsDb(const float *x, size_t n) {
    if (n == 0) return -200.0;
    double s = 0;
    for (size_t i = 0; i < n; ++i) s += static_cast<double>(x[i]) * x[i];
    const double r = std::sqrt(s / static_cast<double>(n));
    return r > 0 ? 20.0 * std::log10(r) : -200.0;
}

double clipFraction(const float *x, size_t n, float limit) {
    if (n == 0) return 0.0;
    size_t c = 0;
    for (size_t i = 0; i < n; ++i)
        if (std::fabs(x[i]) >= limit) ++c;
    return static_cast<double>(c) / static_cast<double>(n);
}

bool writeWav(const std::string &path, const std::vector<float> &x, int rate) {
    std::ofstream f(path, std::ios::binary);
    if (!f) return false;
    auto w32 = [&](uint32_t v) { f.write(reinterpret_cast<const char *>(&v), 4); };
    auto w16 = [&](uint16_t v) { f.write(reinterpret_cast<const char *>(&v), 2); };
    const uint32_t bytes = static_cast<uint32_t>(x.size() * 2);
    f.write("RIFF", 4);
    w32(36 + bytes);
    f.write("WAVE", 4);
    f.write("fmt ", 4);
    w32(16);
    w16(1);
    w16(1);
    w32(static_cast<uint32_t>(rate));
    w32(static_cast<uint32_t>(rate * 2));
    w16(2);
    w16(16);
    f.write("data", 4);
    w32(bytes);
    for (float v : x) {
        const float c = std::max(-1.0f, std::min(1.0f, v));
        const int16_t s = static_cast<int16_t>(std::lround(c * 32767.0f));
        f.write(reinterpret_cast<const char *>(&s), 2);
    }
    return static_cast<bool>(f);
}

// ---------------------------------------------------------------------------------------------------------------
// frame traces

std::vector<double> loadFrameTrace(const std::string &path, std::string &err) {
    std::vector<double> out;
    std::ifstream f(path);
    if (!f) {
        err = "cannot open " + path;
        return out;
    }
    std::string line;
    int col = -1;
    bool first = true;
    while (std::getline(f, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (line.empty() || line[0] == '#') continue;
        const std::vector<std::string> cells = split(line, ',');
        if (first) {
            first = false;
            for (size_t i = 0; i < cells.size(); ++i)
                if (cells[i] == "frame_ms") col = static_cast<int>(i);
            if (col >= 0) continue;  // header of a frametimes.csv
            col = static_cast<int>(cells.size()) - 1;
        }
        if (col < static_cast<int>(cells.size())) {
            const double ms = std::atof(cells[static_cast<size_t>(col)].c_str());
            if (ms > 0) out.push_back(ms / 1000.0);
        }
    }
    if (out.empty()) err = "no frame times in " + path;
    return out;
}

std::vector<double> syntheticFrameTrace(double fps, double seconds, uint32_t seed) {
    std::mt19937 rng(seed);
    std::normal_distribution<double> jitter(0.0, 0.04);
    std::uniform_real_distribution<double> u(0.0, 1.0);
    std::vector<double> out;
    const double base = 1.0 / fps;
    for (double t = 0; t < seconds;) {
        double f = base * (1.0 + jitter(rng));
        if (u(rng) < 0.01) f *= 1.8;  // an occasional long frame (GC, streaming)
        f = std::max(0.25 * base, f);
        out.push_back(f);
        t += f;
    }
    return out;
}

std::vector<int> stepsPerFrame(const std::vector<double> &frames, double fixedDt, double maxDelta) {
    std::vector<int> s;
    s.reserve(frames.size());
    double time = 0, fixedTime = 0;
    for (double f : frames) {
        time += std::min(f, maxDelta);
        int n = 0;
        while (fixedTime + fixedDt <= time + 1e-9) {
            fixedTime += fixedDt;
            ++n;
        }
        s.push_back(n);
    }
    return s;
}

// ---------------------------------------------------------------------------------------------------------------
// JSON

void Json::pre(const char *k) {
    if (!m_need.empty()) {
        if (m_need.back()) m_s += ',';
        m_need.back() = true;
    }
    if (k != nullptr) {
        m_s += '"';
        m_s += k;
        m_s += "\":";
    }
}
Json &Json::obj(const char *k) {
    pre(k);
    m_s += '{';
    m_need.push_back(false);
    return *this;
}
Json &Json::end() {
    m_s += '}';
    m_need.pop_back();
    return *this;
}
Json &Json::arr(const char *k) {
    pre(k);
    m_s += '[';
    m_need.push_back(false);
    return *this;
}
Json &Json::endArr() {
    m_s += ']';
    m_need.pop_back();
    return *this;
}
Json &Json::num(const char *k, double v, int prec) {
    pre(k);
    m_s += std::isfinite(v) ? fmt("%.*f", prec, v) : std::string("null");
    return *this;
}
Json &Json::integer(const char *k, int64_t v) {
    pre(k);
    m_s += std::to_string(v);
    return *this;
}
Json &Json::str(const char *k, const std::string &v) {
    pre(k);
    m_s += '"';
    for (char ch : v) {
        if (ch == '"' || ch == '\\') {
            m_s += '\\';
            m_s += ch;
        } else if (static_cast<unsigned char>(ch) < 0x20) {
            m_s += fmt("\\u%04x", static_cast<unsigned char>(ch));
        } else {
            m_s += ch;
        }
    }
    m_s += '"';
    return *this;
}
Json &Json::boolean(const char *k, bool v) {
    pre(k);
    m_s += v ? "true" : "false";
    return *this;
}
Json &Json::raw(const char *k, const std::string &v) {
    pre(k);
    m_s += v;
    return *this;
}
bool Json::save(const std::string &path) const {
    std::ofstream f(path, std::ios::binary);
    f << m_s << '\n';
    return static_cast<bool>(f);
}

// ---------------------------------------------------------------------------------------------------------------

double nowS() {
    using namespace std::chrono;
    return duration<double>(steady_clock::now().time_since_epoch()).count();
}

std::string fmt(const char *f, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, f);
    std::vsnprintf(buf, sizeof buf, f, ap);
    va_end(ap);
    return buf;
}

std::vector<std::string> split(const std::string &s, char sep) {
    std::vector<std::string> out;
    std::string cur;
    for (char c : s) {
        if (c == sep) {
            out.push_back(cur);
            cur.clear();
        } else {
            cur += c;
        }
    }
    out.push_back(cur);
    return out;
}

Args::Args(int argc, char **argv, int first) {
    for (int i = first; i < argc; ++i) {
        std::string a = argv[i];
        if (a.size() > 2 && a[0] == '-' && a[1] == '-') {
            const std::string k = a.substr(2);
            if (i + 1 < argc && !(argv[i + 1][0] == '-' && argv[i + 1][1] == '-')) kv[k] = argv[++i];
            else kv[k] = "1";
        } else {
            pos.push_back(a);
        }
    }
}
std::string Args::get(const std::string &k, const std::string &d) const {
    auto it = kv.find(k);
    return it == kv.end() ? d : it->second;
}
double Args::num(const std::string &k, double d) const {
    auto it = kv.find(k);
    return it == kv.end() ? d : std::atof(it->second.c_str());
}

uint64_t setProcessAffinityClass(const std::string &cls, std::string &note) {
    if (cls.empty()) return 0;
    ULONG len = 0;
    GetSystemCpuSetInformation(nullptr, 0, &len, GetCurrentProcess(), 0);
    std::vector<char> buf(len);
    uint64_t pMask = 0, eMask = 0;
    BYTE maxClass = 0;
    if (len > 0 && GetSystemCpuSetInformation(reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buf.data()), len, &len,
                                              GetCurrentProcess(), 0)) {
        for (int pass = 0; pass < 2; ++pass)
            for (ULONG off = 0; off < len;) {
                auto *info = reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buf.data() + off);
                if (info->Type == CpuSetInformation && info->CpuSet.Group == 0 && info->CpuSet.LogicalProcessorIndex < 64) {
                    const auto &cs = info->CpuSet;
                    if (pass == 0) maxClass = std::max(maxClass, cs.EfficiencyClass);
                    else if (cs.EfficiencyClass == maxClass) pMask |= 1ull << cs.LogicalProcessorIndex;
                    else eMask |= 1ull << cs.LogicalProcessorIndex;
                }
                off += info->Size;
            }
    }
    const uint64_t mask = (cls == "P" || cls == "p") ? pMask : eMask;
    if (mask == 0) {
        note = "no " + cls + "-cores found; affinity unchanged";
        return 0;
    }
    if (!SetProcessAffinityMask(GetCurrentProcess(), static_cast<DWORD_PTR>(mask))) {
        note = "SetProcessAffinityMask failed";
        return 0;
    }
    note = fmt("process pinned to %s-cores, mask 0x%llx", cls.c_str(), static_cast<unsigned long long>(mask));
    return mask;
}

} // namespace esph
