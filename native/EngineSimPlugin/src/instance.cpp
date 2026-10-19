// instance.cpp: loading, controls, step-indexed commands and results, statistics (see instance.h).
#include "instance.h"

#include "crash_guard.h"
#include "esp_build_config.h"
#include "esp_log.h"
#include "platform.h"
#include "resample.h"
#include "script_loader.h"
#include "wav_io.h"

#include "engine.h"
#include "function.h"
#include "ignition_module.h"
#include "units.h"
#include "vehicle.h"

#include <windows.h>
#include <intrin.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>

namespace esp {

namespace {

std::once_flag g_warm;

// Function::DefaultGaussianFilter is created lazily by the first Function constructor; make it once, here, before
// any engine is built (on any thread) so concurrent builds never race on it (research 01 6.1, defect 11).
void warmStatics() {
    std::call_once(g_warm, [] { Function warm; });
}

struct IgnitionPeek : IgnitionModule {
    static double revLimit(const IgnitionModule &m) { return m.*(&IgnitionPeek::m_revLimit); }
    static double limiterTimer(const IgnitionModule &m) { return m.*(&IgnitionPeek::m_revLimitTimer); }
};

double clampd(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

std::string fmtLine(const char *f, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, f);
    const int n = vsnprintf(buf, sizeof buf, f, ap);
    va_end(ap);
    return n > 0 ? std::string(buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof buf) - 1))) : std::string();
}

size_t roundUpPow2(size_t v) {
    size_t p = 1;
    while (p < v) p <<= 1;
    return p;
}

} // namespace

struct Instance::StepAcc {
    double wStart = 0.0, wEnd = 0.0;
    double sumPd = 0.0, sumDyno = 0.0, sumClutch = 0.0;
    int64_t n = 0;
    bool revLimited = false;
    size_t fillBefore = 0, pushed = 0;  // audio ring fill before this batch's push, frames pushed
};

// ---------------------------------------------------------------------------------------------------------------
// lifetime

Instance::Instance(const EspCreateDesc &desc, const std::string &esDir, const std::string &tempDir)
    : m_desc(desc), m_esDir(esDir), m_tempDir(tempDir) {
    warmStatics();
    while (!m_esDir.empty() && (m_esDir.back() == '/' || m_esDir.back() == '\\')) m_esDir.pop_back();
    m_rate = desc.audio_sample_rate > 0 ? desc.audio_sample_rate : 0;
    m_audio = m_rate > 0;
    size_t ring = desc.audio_ring_frames > 0 ? static_cast<size_t>(desc.audio_ring_frames) : 16384;
    ring = roundUpPow2(std::max<size_t>(1024, std::min<size_t>(ring, size_t(1) << 20)));
    const int rate = m_audio ? m_rate : 48000;
    const int dsp = desc.audio_dsp_buffer_frames > 0 ? desc.audio_dsp_buffer_frames : 256;
    const int target = desc.audio_target_frames > 0 ? desc.audio_target_frames : rate / 20;  // 50 ms
    m_out.init(ring, rate, target);
    m_fill.init(rate, dsp, target, (desc.flags & ESP_CREATE_NO_DRIFT_TRIM) == 0);
    m_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+): sub-millisecond timed waits whatever the process timer
    // resolution is (a console or background process can be held at 15.6 ms on Windows 11).
    m_timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */,
                                     TIMER_ALL_ACCESS);
    {
        // A/B switches for the soak (esp_cli soak --m1-baseline). The process environment, not the CRT's: the DLL has its
        // own static CRT, whose copy of the environment is taken when the DLL loads.
        char v[8] = {};
        if (GetEnvironmentVariableA("ESP_COAST_LOWWATER", v, sizeof v) > 0 && v[0] == '0') m_lowWaterCoast = false;
        if (GetEnvironmentVariableA("ESP_HIRES_TIMER", v, sizeof v) > 0 && v[0] == '0' && m_timer != nullptr) {
            CloseHandle(static_cast<HANDLE>(m_timer));
            m_timer = nullptr;
        }
    }
    m_controls.struct_size = sizeof(EspControls);
    m_controls.sim_speed = 1.0f;
    if (desc.flags & ESP_CREATE_NO_COAST) m_coastMaxMs.store(0);
}

Instance::~Instance() {
    stopWorker();
    if (m_sim != nullptr) {
        m_sim->teardown();
        delete m_sim;
        m_sim = nullptr;
    }
    destroyBundle(m_bundle);
    delete m_trans;
    m_trans = nullptr;
    if (m_wake != nullptr) CloseHandle(static_cast<HANDLE>(m_wake));
    if (m_timer != nullptr) CloseHandle(static_cast<HANDLE>(m_timer));
    m_magic = 0;
}

void Instance::startWorker() {
    m_stop.store(false);
    m_worker = start_guarded_thread([this] { workerMain(); });
}

void Instance::stopWorker() {
    if (!m_worker.joinable()) return;
    m_stop.store(true);
    SetEvent(static_cast<HANDLE>(m_wake));
    m_worker.join();
    logWorkerSummary();
}

// ---------------------------------------------------------------------------------------------------------------
// loading

int32_t Instance::loadBuiltin(const std::string &name, std::string &err) {
    if (m_loaded) {
        err = "this engine handle already holds an engine; create a new handle";
        return ESP_ERR_STATE;
    }
    EngineBundle b;
    if (!buildBuiltin(name, m_esDir, b, err)) return ESP_ERR_ARG;
    return finishLoad(b, err);
}

int32_t Instance::loadScript(const std::string &path, const std::string &topNode, std::string &err) {
    if (m_loaded) {
        err = "this engine handle already holds an engine; create a new handle";
        return ESP_ERR_STATE;
    }
#if ESP_WITH_PIRANHA
    EngineBundle b;
    const int32_t r = compileEngineScript(path, topNode, m_esDir, m_tempDir, b, err);
    if (r != ESP_OK) return r;
    return finishLoad(b, err);
#else
    (void)path;
    (void)topNode;
    err = "this EngineSimPlugin.dll was built without piranha (ESP_WITH_PIRANHA=OFF); only builtins load";
    return ESP_ERR_STATE;
#endif
}

std::vector<std::vector<float>> Instance::loadImpulseResponses(std::string &warnings) {
    Engine *e = m_bundle.engine;
    const int channels = e->getExhaustSystemCount();
    std::vector<std::vector<float>> irs(static_cast<size_t>(channels));
    std::map<std::string, std::vector<float>> cache;
    for (int i = 0; i < channels; ++i) {
        ImpulseResponse *r = e->getExhaustSystem(i)->getImpulseResponse();
        if (r == nullptr || r->getFilename().empty()) {
            irs[i] = {1.0f};
            warnings += "exhaust " + std::to_string(i) + " has no impulse response (dry); ";
            continue;
        }
        const std::string key = r->getFilename() + "|" + std::to_string(r->getVolume());
        auto it = cache.find(key);
        if (it != cache.end()) {
            irs[i] = it->second;
            continue;
        }
        WavData w;
        std::string err;
        if (!readWav16(r->getFilename(), w, err)) {
            irs[i] = {1.0f};
            warnings += err + " (dry); ";
            continue;
        }
        const std::vector<int16_t> mono = firstChannel(w);
        // Synthesizer::initializeImpulseResponse: clip after the last |x| > 100, at most 10,000 taps (source rate).
        size_t clipped = 0;
        for (size_t k = 0; k < mono.size(); ++k)
            if (std::abs(mono[k]) > 100) clipped = k + 1;
        const size_t count = std::min<size_t>(10000, clipped);
        std::vector<float> taps(count);
        const float volume = static_cast<float>(r->getVolume());
        for (size_t k = 0; k < count; ++k) taps[k] = volume * mono[k] / INT16_MAX;
        if (w.sampleRate != m_rate && !taps.empty()) {
            // Resample once at load; scale so the convolution keeps its gain at the new rate.
            std::vector<float> rs = resampleSinc(taps.data(), taps.size(), w.sampleRate, m_rate);
            const float g = static_cast<float>(static_cast<double>(w.sampleRate) / m_rate);
            for (float &v : rs) v *= g;
            taps.swap(rs);
        }
        cache[key] = taps;
        irs[i] = std::move(taps);
    }
    return irs;
}

int32_t Instance::finishLoad(EngineBundle &bundle, std::string &err) {
    m_bundle = bundle;
    bundle = EngineBundle();
    Engine *e = m_bundle.engine;
    if (e == nullptr) {
        err = "no engine was built";
        return ESP_ERR_COMPILE;
    }
    if (m_bundle.vehicle == nullptr) {  // scripts without a vehicle node: upstream's default (loadScript)
        Vehicle::Parameters vp;
        vp.mass = units::mass(1597, units::kg);
        vp.diffRatio = 3.42;
        vp.tireRadius = units::distance(10, units::inch);
        vp.dragCoefficient = 0.25;
        vp.crossSectionArea = units::distance(6.0, units::foot) * units::distance(6.0, units::foot);
        vp.rollingResistance = 2000.0;
        m_bundle.vehicle = new Vehicle;
        m_bundle.vehicle->initialize(vp);
    }

    m_freq = m_desc.sim_frequency > 0 ? m_desc.sim_frequency : static_cast<int>(std::lround(e->getSimulationFrequency()));
    if (m_freq <= 0) m_freq = 10000;
    m_fluid = m_desc.fluid_steps > 0 ? m_desc.fluid_steps : 8;

    m_trans = new UnityTransmission;
    {
        static const double kOne[1] = {1.0};
        Transmission::Parameters tp;
        tp.GearCount = 1;
        tp.GearRatios = kOne;
        tp.MaxClutchTorque = units::torque(1000.0, units::ft_lb);
        if (m_bundle.transmission != nullptr && TransmissionPeek::gearCount(*m_bundle.transmission) > 0) {
            tp.GearCount = TransmissionPeek::gearCount(*m_bundle.transmission);
            tp.GearRatios = TransmissionPeek::gearRatios(*m_bundle.transmission);
            tp.MaxClutchTorque = TransmissionPeek::maxClutchTorque(*m_bundle.transmission);
        }
        m_trans->initialize(tp);
    }

    m_sim = new UnitySim;
    Simulator::Parameters sp;
    sp.systemType = Simulator::SystemType::NsvOptimized;
    m_sim->initialize(sp);
    m_sim->loadSimulation(e, m_bundle.vehicle, m_trans);
    m_sim->setFluidSimulationSteps(m_fluid);
    m_sim->setSimulationFrequency(m_freq);
    m_sim->setSimulationSpeed(1.0);
    m_sim->attach(m_audio);
    m_trans->neutral();

    Synthesizer::AudioParameters ap;
    ap.inputSampleNoise = static_cast<float>(e->getInitialJitter());
    ap.airNoise = static_cast<float>(e->getInitialNoise());
    ap.dF_F_mix = static_cast<float>(e->getInitialHighFrequencyGain());
    ap.volume = 0.5f;  // 02 6.7: volume 1.0 clips 0.4-3 % under load
    std::string warn;
    if (m_audio) {
        m_sim->reinitSynthesizer(m_rate, ap);
        m_renderer.init(&m_sim->synthesizer(), m_rate, loadImpulseResponses(warn));
        m_renderer.setFirCap(m_desc.fir_max_taps > 0 ? m_desc.fir_max_taps : 10000);
    } else {
        m_sim->disableSynthesizer();
    }
    if (!warn.empty()) logf(LogLevel::Warn, "%s: %s", m_bundle.name.c_str(), warn.c_str());

    e->calculateDisplacement();  // scripts do not (upstream calls it in EngineSimApplication::loadEngine)
    m_crankJ = e->getOutputCrankshaft()->getMomentOfInertia();
    m_revLimit = IgnitionPeek::revLimit(*e->getIgnitionModule());
    m_peakHint = m_bundle.peakTorqueHint;
    m_idleHint = m_bundle.idleRpmHint > 0 ? m_bundle.idleRpmHint : 800.0;
    e->getIgnitionModule()->m_enabled = false;
    m_sim->m_starterMotor.m_enabled = false;
    e->setSpeedControl(0.0);
    m_sim->m_dyno.m_enabled = false;
    m_sim->m_dyno.m_hold = false;

    m_loaded = true;
    logf(LogLevel::Info, "loaded %s: %d cyl, %d Hz, %d gas substeps, audio %s, FIR %s", m_bundle.name.c_str(),
         e->getCylinderCount(), m_freq, m_fluid, m_audio ? "on" : "off", firKernelName());
    if (m_desc.worker_thread) startWorker();
    return ESP_OK;
}

int32_t Instance::getInfo(EspEngineInfo &out) const {
    if (!m_loaded) return ESP_ERR_STATE;
    EspEngineInfo o;
    std::memset(&o, 0, sizeof o);
    o.struct_size = sizeof o;
    Engine *e = m_bundle.engine;
    strncpy_s(o.name, sizeof o.name, m_bundle.name.c_str(), _TRUNCATE);
    o.cylinders = e->getCylinderCount();
    o.banks = e->getCylinderBankCount();
    o.exhaust_systems = e->getExhaustSystemCount();
    o.intakes = e->getIntakeCount();
    o.sim_frequency = m_freq;
    o.displacement_m3 = e->getDisplacement();
    o.redline_rad_s = e->getRedline();
    o.starter_torque_nm = e->getStarterTorque();
    o.starter_speed_rad_s = e->getStarterSpeed();
    o.crank_inertia_kgm2 = m_crankJ;
    o.fluid_steps = m_fluid;
    if (m_bundle.transmission != nullptr) {
        const int n = TransmissionPeek::gearCount(*m_bundle.transmission);
        o.script_gear_count = std::min(n, 12);
        const double *r = TransmissionPeek::gearRatios(*m_bundle.transmission);
        for (int i = 0; i < o.script_gear_count && r != nullptr; ++i) o.script_gear_ratios[i] = r[i];
        o.script_max_clutch_nm = TransmissionPeek::maxClutchTorque(*m_bundle.transmission);
    }
    if (m_bundle.vehicle != nullptr) {
        o.script_final_drive = m_bundle.vehicle->getDiffRatio();
        o.script_tire_radius_m = m_bundle.vehicle->getTireRadius();
    }
    o.rev_limit_rad_s = m_revLimit;
    o.ir_taps = m_audio ? m_renderer.firTaps(0) : 0;
    o.source_kind = m_bundle.sourceKind;
    const size_t n = std::min<size_t>(out.struct_size, sizeof o);
    const uint32_t keep = out.struct_size;
    std::memcpy(&out, &o, n);
    out.struct_size = keep;
    return ESP_OK;
}

// ---------------------------------------------------------------------------------------------------------------
// control thread

int32_t Instance::setControls(const EspControls &c) {
    m_controls = c;
    m_controls.struct_size = sizeof(EspControls);
    return ESP_OK;
}

int32_t Instance::setAudioParams(const EspAudioParams &p) {
    std::lock_guard<std::mutex> lk(m_pendingLock);
    m_pendingAudio = true;
    m_pendingAudioParams = p;
    m_pendingDirty.store(true, std::memory_order_release);
    return ESP_OK;
}

int32_t Instance::setQuality(int32_t fluidSteps, int32_t firMaxTaps) {
    if (fluidSteps < 0 || fluidSteps > 64 || firMaxTaps < 0) return ESP_ERR_ARG;
    std::lock_guard<std::mutex> lk(m_pendingLock);
    m_pendingFluid = fluidSteps;
    m_pendingFir = firMaxTaps;
    m_pendingDirty.store(true, std::memory_order_release);
    return ESP_OK;
}

int32_t Instance::setWorkerOptions(const EspWorkerOptions &o) {
    m_coastMaxMs.store(std::max(0, o.coast_max_ms));
    if (!m_worker.joinable()) return ESP_OK;
    HANDLE h = static_cast<HANDLE>(m_worker.native_handle());
    static const int kPriorities[5] = {THREAD_PRIORITY_LOWEST, THREAD_PRIORITY_BELOW_NORMAL, THREAD_PRIORITY_NORMAL,
                                       THREAD_PRIORITY_ABOVE_NORMAL, THREAD_PRIORITY_HIGHEST};
    const int p = std::max(-2, std::min(2, o.priority));
    bool ok = true;
    if (o.priority >= 3) {  // audio class: the worker joins MMCSS itself (it must be the calling thread)
        m_wantPriority.store(3, std::memory_order_relaxed);
        m_priorityDirty.store(true, std::memory_order_release);
        SetEvent(static_cast<HANDLE>(m_wake));
    } else {
        if (m_mmcss != nullptr || m_wantPriority.load(std::memory_order_relaxed) >= 3) {
            m_wantPriority.store(p, std::memory_order_relaxed);  // the worker leaves MMCSS, then takes this priority
            m_priorityDirty.store(true, std::memory_order_release);
            SetEvent(static_cast<HANDLE>(m_wake));
        } else {
            ok = SetThreadPriority(h, kPriorities[p + 2]) != 0;
            m_wantPriority.store(p, std::memory_order_relaxed);
        }
    }
    if (o.power_throttling_off) {
        THREAD_POWER_THROTTLING_STATE s = {};
        s.Version = THREAD_POWER_THROTTLING_CURRENT_VERSION;
        s.ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED;
        s.StateMask = 0;
        ok = SetThreadInformation(h, ThreadPowerThrottling, &s, sizeof s) != 0 && ok;
    }
    if (o.affinity_mask != 0) ok = SetThreadAffinityMask(h, static_cast<DWORD_PTR>(o.affinity_mask)) != 0 && ok;
    return ok ? ESP_OK : ESP_ERR_INTERNAL;
}

static void makeCommand(Command &c, int64_t index, double dt, const EspControls &controls, const EspDrivelineIn *in,
                        const double *crank, int32_t n) {
    std::memset(&c, 0, sizeof c);
    c.stepIndex = index;
    c.dt = dt;
    c.controls = controls;
    if (in != nullptr) {
        const size_t m = std::min<size_t>(in->struct_size, sizeof(EspDrivelineIn));
        std::memcpy(&c.driveline, in, m);
    }
    c.driveline.struct_size = sizeof(EspDrivelineIn);
    c.nCrank = std::max(0, std::min(n, ESP_MAX_CRANK_SAMPLES));
    for (int32_t i = 0; i < c.nCrank; ++i) c.crank[i] = crank[i];
    c.kickQpc = qpcNow();
}

static int32_t checkStep(double dt, const EspDrivelineIn *in, const double *crank, int32_t n) {
    if (!(dt >= 0.0) || !std::isfinite(dt)) return ESP_ERR_ARG;
    if (in != nullptr && in->struct_size < ESP_SIZEOF_DRIVELINE_IN) return ESP_ERR_ARG;
    const int32_t mode = in != nullptr ? in->mode : ESP_MODE_FREE;
    if (mode != ESP_MODE_FREE && mode != ESP_MODE_FOLLOW_RPM && mode != ESP_MODE_SLAVED_CRANK) return ESP_ERR_ARG;
    if (n < 0 || n > ESP_MAX_CRANK_SAMPLES || (n > 0 && crank == nullptr)) return ESP_ERR_ARG;
    if (mode == ESP_MODE_SLAVED_CRANK && n < 1) return ESP_ERR_ARG;
    return ESP_OK;
}

int32_t Instance::step(int64_t index, double dt, const EspDrivelineIn *in, const double *crank, int32_t n,
                       EspStepOut *out) {
    if (!m_loaded) return ESP_ERR_STATE;
    if (m_desc.worker_thread) return ESP_ERR_STATE;  // use esp_step_begin/end with a worker
    const int32_t r = stepBegin(index, dt, in, crank, n);
    if (r != ESP_OK) return r;
    return stepEnd(index, 0, out);
}

int32_t Instance::stepBegin(int64_t index, double dt, const EspDrivelineIn *in, const double *crank, int32_t n) {
    if (!m_loaded) return ESP_ERR_STATE;
    const int32_t chk = checkStep(dt, in, crank, n);
    if (chk != ESP_OK) return chk;
    if (m_anyKick && index <= m_lastKicked) return ESP_ERR_ARG;  // step indices are monotonic
    Command c;
    makeCommand(c, index, dt, m_controls, in, crank, n);
    if (!m_desc.worker_thread) {
        std::lock_guard<std::mutex> lk(m_simLock);
        m_out.producerActive.store(true, std::memory_order_relaxed);
        process(c, false);
    } else {
        const int64_t t0 = qpcNow();
        while (!m_cmds.push(c)) {  // never dropped: wait for the worker, at most 50 ms
            if (qpcSeconds(qpcNow() - t0) > 0.05) return ESP_ERR_TIMEOUT;
            SetEvent(static_cast<HANDLE>(m_wake));
            Sleep(0);
        }
        m_lastKickQpc.store(c.kickQpc, std::memory_order_release);
        SetEvent(static_cast<HANDLE>(m_wake));
    }
    m_lastKicked = index;
    m_anyKick = true;
    return ESP_OK;
}

int32_t Instance::stepEnd(int64_t index, int32_t timeoutUs, EspStepOut *out) {
    if (!m_loaded) return ESP_ERR_STATE;
    if (out == nullptr || out->struct_size < ESP_SIZEOF_STEP_OUT) return ESP_ERR_ARG;
    if (!m_anyKick || index > m_lastKicked) return ESP_ERR_ARG;  // never kicked
    const int64_t t0 = qpcNow();
    ResultSlot &slot = m_results[static_cast<uint64_t>(index) & (ESP_RESULT_SLOTS - 1)];
    for (int spins = 0;; ++spins) {
        // Load the newest published index first: if it is >= index, the slot load below sees index or a later value.
        const int64_t last = m_lastPublished.load(std::memory_order_acquire);
        const int64_t s1 = slot.step.load(std::memory_order_acquire);
        if (s1 == index) {
            EspStepOut tmp = slot.out;
            std::atomic_thread_fence(std::memory_order_acquire);
            if (slot.step.load(std::memory_order_relaxed) != index) return ESP_ERR_ARG;  // overwritten while copying
            const uint32_t keep = out->struct_size;
            std::memcpy(out, &tmp, std::min<size_t>(keep, sizeof tmp));
            out->struct_size = keep;
            if (spins > 0) {
                const double ms = 1000.0 * qpcSeconds(qpcNow() - t0);
                m_waitTotalMs += ms;
                m_waitMaxMs = std::max(m_waitMaxMs, ms);
            }
            return ESP_OK;
        }
        if (last != INT64_MIN && last >= index) return ESP_ERR_ARG;  // published and since overwritten: too old
        const double us = 1e6 * qpcSeconds(qpcNow() - t0);
        if (us >= timeoutUs) {
            if (timeoutUs > 0) {
                m_stale.fetch_add(1, std::memory_order_relaxed);
                const double ms = us / 1000.0;
                m_waitTotalMs += ms;
                m_waitMaxMs = std::max(m_waitMaxMs, ms);
            }
            return ESP_PENDING;
        }
        for (int k = 0; k < 16; ++k) _mm_pause();
    }
}

int32_t Instance::debugTap(const char *pathUtf8, double seconds) {
    if (!m_audio) return ESP_ERR_STATE;
    std::lock_guard<std::mutex> lk(m_tapLock);
    m_tapRequest = AudioTapBuffer();
    if (pathUtf8 != nullptr && pathUtf8[0] != '\0' && seconds > 0.0) {
        m_tapRequest.path = pathUtf8;
        m_tapRequest.want = static_cast<size_t>(std::llround(seconds * m_rate));
        m_tapRequest.data.reserve(m_tapRequest.want);
        m_tapRequest.active = true;
    }
    m_tapPending = true;
    return ESP_OK;
}

int32_t Instance::getStats(EspStats &out) {
    EspStats o;
    std::memset(&o, 0, sizeof o);
    o.struct_size = sizeof o;
    {
        std::lock_guard<std::mutex> lk(m_statsLock);
        o.commands = m_stats.commands;
        o.steps_run = m_stats.stepsRun;
        o.steps_target = m_stats.stepsTarget;
        o.steps_borrowed = m_stats.borrowed;
        o.coast_steps = m_stats.coastSteps;
        o.coast_events = m_stats.coastEvents;
        o.offline_steps = m_stats.offlineSteps;
        o.cpu_ms_ema = m_stats.cpuEma;
        o.worker_ms_ema = m_stats.wallEma;
        o.rtf = m_stats.wallSeconds > 0 ? m_stats.simSeconds / m_stats.wallSeconds : 0.0;
        o.rtf_cpu = m_stats.cpuSeconds > 0 ? m_stats.simSeconds / m_stats.cpuSeconds : 0.0;
        o.p_core_share = m_stats.commands > 0 ? static_cast<double>(m_stats.pCommands) / m_stats.commands : 0.0;
        o.audio_frames_produced = m_renderer.produced();
        o.audio_frames_dropped = m_renderer.dropped();
        o.clipped_samples = m_renderer.clipped();
        o.drift_trim = m_fill.epsilon();
        o.frame_p95_ms = m_fill.frameP95Ms();
        o.worker_frame_ms = m_fill.workerFrameMs();
    }
    o.sim_frequency = m_freq;
    o.commands_pending = static_cast<int32_t>(m_cmds.size());
    const int64_t last = m_lastPublished.load(std::memory_order_acquire);
    o.last_step_index = last == INT64_MIN ? -1 : last;
    o.stale_steps = m_stale.load(std::memory_order_relaxed);
    o.ring_capacity_frames = static_cast<int32_t>(m_out.ring.capacity());
    o.collect_wait_ms_total = m_waitTotalMs;
    o.collect_wait_ms_max = m_waitMaxMs;
    o.audio_underruns = m_out.underruns.load(std::memory_order_relaxed);
    o.audio_underrun_frames = m_out.underrunFrames.load(std::memory_order_relaxed);
    o.ring_fill_frames = static_cast<int32_t>(m_out.ring.fill());
    o.ring_target_frames = m_out.targetFrames.load(std::memory_order_relaxed);
    o.audio_frames_consumed = m_out.consumed.load(std::memory_order_relaxed);
    const uint32_t keep = out.struct_size;
    std::memcpy(&out, &o, std::min<size_t>(keep, sizeof o));
    out.struct_size = keep;
    return ESP_OK;
}

// ---------------------------------------------------------------------------------------------------------------
// processing (worker, or the caller with worker_thread = 0); m_simLock held

void Instance::applyPending() {
    if (!m_pendingDirty.load(std::memory_order_acquire)) return;
    std::lock_guard<std::mutex> lk(m_pendingLock);
    m_pendingDirty.store(false, std::memory_order_relaxed);
    if (m_pendingAudio && m_audio) {
        Synthesizer::AudioParameters ap = m_sim->synthesizer().getAudioParameters();
        const EspAudioParams &p = m_pendingAudioParams;
        ap.volume = p.volume;
        ap.convolution = static_cast<float>(clampd(p.convolution, 0.0, 1.0));
        ap.dF_F_mix = p.hf_gain;
        ap.airNoise = p.low_freq_noise;
        ap.inputSampleNoise = p.high_freq_noise;
        if (p.leveler_target > 0) ap.levelerTarget = p.leveler_target;
        m_sim->synthesizer().setAudioParameters(ap);
    }
    m_pendingAudio = false;
    if (m_pendingFluid > 0) {
        m_fluid = m_pendingFluid;
        m_sim->setFluidSimulationSteps(m_fluid);
    }
    if (m_pendingFir > 0 && m_audio) m_renderer.setFirCap(m_pendingFir);
    m_pendingFluid = m_pendingFir = 0;
}

void Instance::applyControls(const EspControls &c) {
    Engine *e = m_bundle.engine;
    e->setSpeedControl(clampd(c.throttle, 0.0, 1.0));
    e->getIgnitionModule()->m_enabled = c.ignition != 0;
    m_sim->m_starterMotor.m_enabled = c.starter != 0;
    m_simSpeed = c.sim_speed > 0.0f ? clampd(c.sim_speed, 0.05, 4.0) : 1.0;
}

int32_t Instance::modeOf(const Command &c) const { return c.driveline.mode; }

void Instance::enterMode(const Command &c) {
    const int32_t mode = modeOf(c);
    Dynamometer &dyno = m_sim->m_dyno;
    dyno.m_enabled = false;
    dyno.m_hold = false;
    dyno.m_rotationSpeed = 0.0;
    dyno.m_maxTorque = units::torque(10000.0, units::ft_lb);
    SlavedCrankPD &pd = m_sim->pd();
    pd.enabled = false;
    pd.reset();
    if (mode == ESP_MODE_FOLLOW_RPM) {
        m_followPrev = m_bundle.engine->getRpm();
        dyno.m_enabled = true;
        dyno.m_hold = true;
    } else if (mode == ESP_MODE_SLAVED_CRANK) {
        pd.enabled = true;
    }
    m_mode = mode;
}

void Instance::runSteps(int64_t n, const Command &c, StepAcc &acc) {
    Engine *e = m_bundle.engine;
    atg_scs::RigidBody &crank = e->getOutputCrankshaft()->m_body;
    acc.wStart = -crank.v_theta;
    acc.wEnd = acc.wStart;
    if (n <= 0) return;

    const int32_t mode = modeOf(c);
    const double h = 1.0 / m_freq;
    SlavedCrankPD &pd = m_sim->pd();
    Dynamometer &dyno = m_sim->m_dyno;
    if (mode == ESP_MODE_FREE) {
        const double load = c.driveline.load_torque_nm;
        dyno.m_enabled = load > 0.0;
        dyno.m_hold = false;
        dyno.m_rotationSpeed = 0.0;
        dyno.m_maxTorque = load > 0.0 ? load : 0.0;
    } else if (mode == ESP_MODE_SLAVED_CRANK) {
        const double j = c.driveline.slave_inertia_kgm2 > 0 ? c.driveline.slave_inertia_kgm2 : m_crankJ;
        const int cyl = e->getCylinderCount();
        const double idleFiring = m_idleHint / 60.0 * cyl / 2.0;
        const double fc = c.driveline.slave_fc_hz > 0 ? c.driveline.slave_fc_hz : std::min(8.0, idleFiring / 4.0);
        const double limit = c.driveline.slave_torque_limit_nm > 0 ? c.driveline.slave_torque_limit_nm
                                                                 : (m_peakHint > 0 ? 4.0 * m_peakHint : 3000.0);
        pd.configure(j, fc, limit);
    }

    m_sim->startFrame(static_cast<double>(n) / m_freq);
    if (m_sim->getFrameIterationCount() != n && !m_warnedSteps) {
        m_warnedSteps = true;
        logf(LogLevel::Warn, "startFrame planned %d steps for %lld (synthesizer latency not 0?)",
             m_sim->getFrameIterationCount(), static_cast<long long>(n));
    }
    if (m_audio) m_sim->synthesizer().setInputSampleRate(m_freq * m_simSpeed * (1.0 + m_fill.epsilon()));

    const double followTarget = c.driveline.follow_rpm;
    // engine-sim's dynamometer holds the speed in the direction the crank already turns; from rest it would spin
    // the engine backwards, so give it a nudge forward (forward is v_theta < 0)
    if (mode == ESP_MODE_FOLLOW_RPM && crank.v_theta >= 0.0) crank.v_theta = -1e-3;
    const int nc = c.nCrank;
    for (int64_t k = 0; k < n; ++k) {
        if (mode == ESP_MODE_FOLLOW_RPM) {
            const double rpm = m_followPrev + (followTarget - m_followPrev) * static_cast<double>(k + 1) / n;
            dyno.m_rotationSpeed = units::rpm(std::max(0.0, rpm));
        } else if (mode == ESP_MODE_SLAVED_CRANK) {
            double cmd = c.crank[0];
            if (nc > 1) {
                const double u = (static_cast<double>(k) + 0.5) / n * (nc - 1);
                const int i = std::min(nc - 2, static_cast<int>(u));
                cmd = c.crank[i] + (c.crank[i + 1] - c.crank[i]) * (u - i);
            }
            pd.cmd = cmd;
        }
        if (!m_sim->simulateStep()) break;
        if (mode == ESP_MODE_SLAVED_CRANK) {
            pd.afterStep(-crank.v_theta, h);
            acc.sumPd += pd.lastT;
        }
        if (dyno.m_enabled) acc.sumDyno += dyno.getTorque();
        acc.sumClutch += m_trans->clutchTorque();
        ++acc.n;
    }
    if (mode == ESP_MODE_FOLLOW_RPM) m_followPrev = followTarget;
    acc.revLimited = IgnitionPeek::limiterTimer(*e->getIgnitionModule()) > 0.0;

    if (m_audio) {
        AudioTapBuffer *tap = nullptr;
        {
            std::unique_lock<std::mutex> lk(m_tapLock, std::try_to_lock);
            if (lk.owns_lock() && m_tapPending) {
                if (m_tap.active && !m_tap.data.empty()) {
                    std::string err;
                    writeWav16Mono(m_tap.path, m_tap.data.data(), m_tap.data.size(), m_rate, err);
                }
                m_tap = std::move(m_tapRequest);
                m_tapRequest = AudioTapBuffer();
                m_tapPending = false;
            }
        }
        if (m_tap.active) tap = &m_tap;
        acc.fillBefore = m_out.ring.fill();
        acc.pushed = m_renderer.renderSync(m_out, tap);
        if (m_tap.full()) {
            std::string err;
            if (!writeWav16Mono(m_tap.path, m_tap.data.data(), m_tap.data.size(), m_rate, err))
                logf(LogLevel::Warn, "debug tap: %s", err.c_str());
            else
                logf(LogLevel::Info, "debug tap written: %s (%.1f s)", m_tap.path.c_str(),
                     static_cast<double>(m_tap.data.size()) / m_rate);
            m_tap = AudioTapBuffer();
        }
    }
    m_sim->endFrame();
    m_simTime += static_cast<double>(acc.n) * h;
    acc.wEnd = -crank.v_theta;
}

void Instance::updateSlope(double omega, double torque, double dt) {
    // Exponentially weighted least squares of T_ind against omega over about 0.3 s; held and decayed when omega
    // barely moves, clamped to a plausible range (06 4.5: |dT/dw| < 0.5 except near the limiter).
    if (!m_slopeInit) {
        m_mx = omega;
        m_my = torque;
        m_sxx = m_sxy = 0.0;
        m_slopeInit = true;
        return;
    }
    const double a = 1.0 - std::exp(-dt / 0.3);
    const double dx = omega - m_mx, dy = torque - m_my;
    m_mx += a * dx;
    m_my += a * dy;
    m_sxx = (1 - a) * (m_sxx + a * dx * dx);
    m_sxy = (1 - a) * (m_sxy + a * dx * dy);
    if (m_sxx > 4.0) m_slope = m_sxy / m_sxx;  // omega spread above about 2 rad/s
    else m_slope *= 0.98;
    m_slope = clampd(m_slope, -20.0, 5.0);
}

void Instance::fillOut(const Command &c, const StepAcc &acc, int64_t n, double cpuS, double wallS, int32_t flags,
                       EspStepOut &o) {
    Engine *e = m_bundle.engine;
    std::memset(&o, 0, sizeof o);
    o.struct_size = sizeof o;
    o.steps = static_cast<int32_t>(n);
    o.step_index = c.stepIndex;
    o.sim_time_s = m_simTime;
    o.rpm = e->getRpm();
    o.crank_speed = acc.wEnd;
    const int32_t mode = modeOf(c);
    double tInd = 0.0;
    if (acc.n > 0) {
        const double span = static_cast<double>(acc.n) / m_freq;
        const double j = (mode == ESP_MODE_SLAVED_CRANK && c.driveline.slave_inertia_kgm2 > 0)
                             ? c.driveline.slave_inertia_kgm2
                             : m_crankJ;
        const double jAlpha = j * (acc.wEnd - acc.wStart) / span;
        if (mode == ESP_MODE_SLAVED_CRANK) tInd = jAlpha - acc.sumPd / acc.n;
        else tInd = jAlpha + acc.sumDyno / acc.n;  // FREE (absorber load) and FOLLOW_RPM (dyno hold)
        updateSlope(acc.wEnd, tInd, span);
        o.clutch_torque_nm = acc.sumClutch / acc.n;
        o.intake_flow_mol_s = e->getIntakeFlowRate();
    }
    o.indicated_torque_nm = tInd;
    o.dT_dw = m_slope;
    o.dyno_torque_nm = m_sim->getFilteredDynoTorque();
    o.manifold_pressure_pa = e->getManifoldPressure();
    o.throttle_open = 1.0 - e->getThrottle();
    o.afr = e->getIntakeAfr();
    o.fuel_used_l = e->getTotalVolumeFuelConsumed() / units::L;
    o.cpu_ms = 1000.0 * cpuS;
    o.audio_frames_buffered = static_cast<int32_t>(m_out.ring.fill());
    o.audio_underruns = m_out.underruns.load(std::memory_order_relaxed);
    o.rev_limited_hint = acc.revLimited ? 1 : 0;
    o.flags = flags;
    (void)wallS;
}

void Instance::publish(const EspStepOut &o) {
    ResultSlot &slot = m_results[static_cast<uint64_t>(o.step_index) & (ESP_RESULT_SLOTS - 1)];
    slot.step.store(-2, std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_release);
    slot.out = o;
    slot.step.store(o.step_index, std::memory_order_release);
    m_lastPublished.store(o.step_index, std::memory_order_release);
}

void Instance::process(const Command &c, bool afterCoast) {
    const double w0 = nowSeconds();
    const double c0 = threadCpuSeconds();
    applyPending();

    int32_t flags = afterCoast ? ESP_OUT_AFTER_COAST : 0;
    double dt = c.dt;
    if (dt > 0.25) {
        dt = 0.25;
        flags |= ESP_OUT_CAPPED;
    }
    applyControls(c.controls);
    if (modeOf(c) != m_mode || afterCoast) enterMode(c);

    const int64_t prevTarget = m_target;
    m_sumDtF += dt * m_freq;
    m_target = std::llround(m_sumDtF);
    if (m_executed > prevTarget) flags |= ESP_OUT_REPAID;
    int64_t n = m_target - m_executed;  // borrowed coast steps are repaid here, never below zero
    if (n < 0) n = 0;

    StepAcc acc;
    runSteps(n, c, acc);
    m_executed += acc.n;

    const double cpuS = threadCpuSeconds() - c0;
    const double now = nowSeconds();
    const double wallS = now - w0;
    EspStepOut o;
    fillOut(c, acc, acc.n, cpuS, wallS, flags, o);
    if (m_audio) {
        m_fill.onCommand(c.controls.frame_index, c.controls.frame_dt_s, wallS, now);
        m_fill.onPushed(m_out.ring.fill());
        m_out.targetFrames.store(m_fill.targetFrames(), std::memory_order_relaxed);
    }
    publish(o);

    const bool onP = onPerformanceCore();
    {
        std::lock_guard<std::mutex> lk(m_statsLock);
        m_stats.commands += 1;
        m_stats.stepsRun += acc.n;
        m_stats.stepsTarget = m_target;
        m_stats.borrowed = std::max<int64_t>(0, m_executed - m_target);
        m_stats.cpuEma = m_stats.commands == 1 ? 1000.0 * cpuS : m_stats.cpuEma + 0.02 * (1000.0 * cpuS - m_stats.cpuEma);
        m_stats.wallEma = m_stats.commands == 1 ? 1000.0 * wallS : m_stats.wallEma + 0.02 * (1000.0 * wallS - m_stats.wallEma);
        m_stats.simSeconds += static_cast<double>(acc.n) / m_freq;
        m_stats.wallSeconds += wallS;
        m_stats.cpuSeconds += cpuS;
        if (onP) m_stats.pCommands += 1;
    }
    m_lastCmd = c;
    m_haveLastCmd = true;
    m_lastProduceS = now;
    if (m_audio && m_desc.worker_thread)
        notePush(w0, wallS, cpuS, w0 - qpcSeconds(c.kickQpc), acc.fillBefore, acc.pushed, false, c.stepIndex);
}

void Instance::coastBatch() {
    Command c = m_lastCmd;
    if (modeOf(c) == ESP_MODE_FOLLOW_RPM) c.driveline.follow_rpm = m_followPrev;  // hold, no ramp
    if (modeOf(c) == ESP_MODE_SLAVED_CRANK && c.nCrank > 0) {
        const double last = c.crank[c.nCrank - 1];
        for (int i = 0; i < c.nCrank; ++i) c.crank[i] = last;
    }
    const double w0 = nowSeconds();
    const double c0 = threadCpuSeconds();
    const int64_t n = std::max<int64_t>(1, std::llround(c.dt * m_freq));
    StepAcc acc;
    runSteps(n, c, acc);
    m_executed += acc.n;
    const double cpuS = threadCpuSeconds() - c0;
    const double now = nowSeconds();
    std::lock_guard<std::mutex> lk(m_statsLock);
    m_stats.stepsRun += acc.n;
    m_stats.coastSteps += acc.n;
    m_stats.borrowed = std::max<int64_t>(0, m_executed - m_target);
    m_stats.simSeconds += static_cast<double>(acc.n) / m_freq;
    m_stats.wallSeconds += now - w0;
    m_stats.cpuSeconds += cpuS;
    m_lastProduceS = now;
    if (m_audio) notePush(w0, now - w0, cpuS, 0.0, acc.fillBefore, acc.pushed, true, c.stepIndex);
}

// ---------------------------------------------------------------------------------------------------------------
// underrun diagnostics (worker only; card T2.4: "log which step stalled")

void Instance::notePush(double startS, double wallS, double cpuS, double kickLatencyS, size_t fillBefore, size_t pushed,
                        bool coast, int64_t step) {
    WorkerDiag &d = m_diag;
    const double now = startS + wallS;
    const double toMs = 1000.0 / (m_rate > 0 ? m_rate : 48000);
    if (d.pushes == 0) {
        d.t0 = startS;
        d.lastPushS = now;
    }
    PushNote &n = d.hist[d.next];
    n.t = now - d.t0;
    n.gapMs = 1000.0 * (now - d.lastPushS);
    n.fillBeforeMs = fillBefore * toMs;
    n.fillAfterMs = (fillBefore + pushed) * toMs;
    n.wallMs = 1000.0 * wallS;
    n.cpuMs = 1000.0 * cpuS;
    n.kickLatencyMs = 1000.0 * kickLatencyS;
    n.step = step;
    n.coast = coast;
    d.next = (d.next + 1) % 24;
    if (d.count < 24) ++d.count;
    // steady-state statistics from 10 s on (load, priming and the first frames are not the steady state)
    if (now - d.t0 >= 10.0) {
        if (n.gapMs > d.maxGapMs) {
            d.maxGapMs = n.gapMs;
            d.maxGapAtS = n.t;
        }
        if (n.fillBeforeMs < d.minFillBeforeMs) {
            d.minFillBeforeMs = n.fillBeforeMs;
            d.minFillAtS = n.t;
        }
        d.maxKickLatencyMs = std::max(d.maxKickLatencyMs, n.kickLatencyMs);
        d.maxDeschedMs = std::max(d.maxDeschedMs, n.wallMs - n.cpuMs);
        d.maxWallMs = std::max(d.maxWallMs, n.wallMs);
        if (n.fillBeforeMs < 2) ++d.lowFill[0];
        if (n.fillBeforeMs < 5) ++d.lowFill[1];
        if (n.fillBeforeMs < 10) ++d.lowFill[2];
        if (n.fillBeforeMs < 15) ++d.lowFill[3];
    }
    ++d.pushes;
    d.lastPushS = now;

    const int32_t u = m_out.underruns.load(std::memory_order_relaxed);
    if (u == d.underrunsSeen) return;
    d.underrunsSeen = u;
    if (d.underrunsLogged >= 8) return;  // a bad run logs its first few only
    ++d.underrunsLogged;
    const double at = qpcSeconds(m_out.lastUnderrunQpc.load(std::memory_order_relaxed)) - d.t0;
    std::string lines = fmtLine("underrun #%d at %.3f s (ring target %.1f ms); the batches before it (t s, gap ms, fill "
                                "before -> after ms, wall/cpu ms, kick->start ms, step):",
                                u, at, m_out.targetFrames.load(std::memory_order_relaxed) * toMs);
    for (int i = 0; i < d.count; ++i) {
        const PushNote &h = d.hist[(d.next - d.count + i + 24) % 24];
        lines += fmtLine("\n    %s %.3f gap %.1f fill %.1f -> %.1f wall %.2f cpu %.2f kick %.2f step %lld", h.coast ? "coast" : "cmd  ",
                         h.t, h.gapMs, h.fillBeforeMs, h.fillAfterMs, h.wallMs, h.cpuMs, h.kickLatencyMs,
                         static_cast<long long>(h.step));
    }
    log(LogLevel::Warn, lines);
}

void Instance::logWorkerSummary() {
    if (!m_audio || m_diag.pushes == 0) return;
    const WorkerDiag &d = m_diag;
    logf(LogLevel::Info,
         "worker summary %s: %lld batches; steady state (from 10 s): longest gap between pushes %.1f ms (at %.1f s), "
         "lowest fill before a push %.1f ms (at %.1f s), pushes with fill < 2/5/10/15 ms: %lld/%lld/%lld/%lld, "
         "longest batch %.2f ms, most descheduled %.2f ms, longest kick->start %.2f ms; low-water coasts %d; "
         "underruns %d; timer %s, low-water coast %s, worker %s",
         m_bundle.name.c_str(), static_cast<long long>(d.pushes), d.maxGapMs, d.maxGapAtS, d.minFillBeforeMs < 1e8 ? d.minFillBeforeMs : 0.0,
         d.minFillAtS, static_cast<long long>(d.lowFill[0]), static_cast<long long>(d.lowFill[1]),
         static_cast<long long>(d.lowFill[2]), static_cast<long long>(d.lowFill[3]), d.maxWallMs, d.maxDeschedMs,
         d.maxKickLatencyMs, d.lowWaterCoasts, m_out.underruns.load(), m_timer != nullptr ? "high-resolution" : "ms",
         m_lowWaterCoast ? "on" : "off", m_schedClass);
}

} // namespace esp
