// offline.cpp: blocking tools for EngineBake, tests and the Engine Bench's sweep engine (PLAN 2.5, 3.5).
// They run on the caller's thread with m_simLock held; the worker neither steps nor coasts meanwhile. Their steps are
// outside the step accounting (stats.offline_steps).
#include "instance.h"

#include "esp_log.h"

#include "engine.h"
#include "units.h"

#include <algorithm>
#include <cmath>

namespace esp {

namespace {

// Restores the control state an offline call changed.
struct SavedState {
    double throttle;
    bool ignition, starter, dynoEnabled, dynoHold, pdEnabled;
    double dynoSpeed, dynoMax;
};

SavedState save(Engine *e, UnitySim *sim) {
    return {e->getSpeedControl(),          e->getIgnitionModule()->m_enabled, sim->m_starterMotor.m_enabled,
            sim->m_dyno.m_enabled,         sim->m_dyno.m_hold,                sim->pd().enabled,
            sim->m_dyno.m_rotationSpeed,   sim->m_dyno.m_maxTorque};
}

void restore(Engine *e, UnitySim *sim, const SavedState &s) {
    e->setSpeedControl(s.throttle);
    e->getIgnitionModule()->m_enabled = s.ignition;
    sim->m_starterMotor.m_enabled = s.starter;
    sim->m_dyno.m_enabled = s.dynoEnabled;
    sim->m_dyno.m_hold = s.dynoHold;
    sim->m_dyno.m_rotationSpeed = s.dynoSpeed;
    sim->m_dyno.m_maxTorque = s.dynoMax;
    sim->pd().enabled = s.pdEnabled;
}

} // namespace

void Instance::offlineFrames(double seconds, double frameDt, bool render, std::vector<float> *audio, double *torqueSum,
                             int *torqueN) {
    const int64_t frames = std::max<int64_t>(1, std::llround(seconds / frameDt));
    const int64_t perFrame = std::max<int64_t>(1, std::llround(frameDt * m_freq));
    int64_t steps = 0;
    for (int64_t f = 0; f < frames; ++f) {
        m_sim->startFrame(static_cast<double>(perFrame) / m_freq);
        if (m_audio) m_sim->synthesizer().setInputSampleRate(static_cast<double>(m_freq));
        for (int64_t k = 0; k < perFrame; ++k) {
            if (!m_sim->simulateStep()) break;
            ++steps;
        }
        if (torqueSum != nullptr) {
            *torqueSum += std::fabs(m_sim->getFilteredDynoTorque());
            ++*torqueN;
        }
        if (m_audio) {
            if (render && audio != nullptr) m_renderer.renderInto(*audio);
            else m_renderer.discardInput();
        }
        m_sim->endFrame();
    }
    m_simTime += static_cast<double>(steps) / m_freq;
    std::lock_guard<std::mutex> lk(m_statsLock);
    m_stats.offlineSteps += steps;
}

int32_t Instance::dynoSweep(double rpmFrom, double rpmTo, int32_t points, float throttle, double settleS,
                            double *torque, double *power) {
    if (!m_loaded) return ESP_ERR_STATE;
    if (points < 1 || points > 512 || torque == nullptr || !(rpmFrom > 0) || !(rpmTo > 0) || !(settleS >= 0))
        return ESP_ERR_ARG;
    if (m_cmds.size() > 0) return ESP_ERR_STATE;  // never while stepping
    m_offline.store(true, std::memory_order_release);
    {
        std::lock_guard<std::mutex> lk(m_simLock);
        Engine *e = m_bundle.engine;
        const SavedState s = save(e, m_sim);
        e->setSpeedControl(std::max(0.0f, std::min(1.0f, throttle)));
        e->getIgnitionModule()->m_enabled = true;
        m_sim->m_starterMotor.m_enabled = false;
        m_sim->pd().enabled = false;
        Dynamometer &dyno = m_sim->m_dyno;
        atg_scs::RigidBody &crank = e->getOutputCrankshaft()->m_body;
        if (crank.v_theta >= 0.0) crank.v_theta = -1e-3;  // the dyno holds the current direction (see runSteps)
        for (int32_t p = 0; p < points; ++p) {
            const double rpm = points == 1 ? rpmFrom : rpmFrom + (rpmTo - rpmFrom) * p / (points - 1);
            dyno.m_enabled = true;
            dyno.m_hold = true;
            dyno.m_rotationSpeed = units::rpm(rpm);
            dyno.m_maxTorque = units::torque(10000.0, units::ft_lb);
            offlineFrames(settleS, 1.0 / 60.0, false, nullptr, nullptr, nullptr);
            double sum = 0.0;
            int n = 0;
            offlineFrames(1.0, 1.0 / 60.0, false, nullptr, &sum, &n);  // average over 1 s, as research 02 Part B
            torque[p] = n > 0 ? sum / n : 0.0;
            if (power != nullptr) power[p] = torque[p] * units::rpm(rpm);
        }
        restore(e, m_sim, s);
        m_mode = -1;  // the next command re-enters its mode
    }
    m_offline.store(false, std::memory_order_release);
    return ESP_OK;
}

int32_t Instance::renderOffline(double rpm, float throttle, double seconds, float *dst, int32_t frames) {
    if (!m_loaded) return ESP_ERR_STATE;
    if (!m_audio) return ESP_ERR_STATE;
    if (dst == nullptr || frames <= 0 || !(seconds > 0) || !(rpm > 0)) return ESP_ERR_ARG;
    if (m_cmds.size() > 0) return ESP_ERR_STATE;
    std::vector<float> audio;
    audio.reserve(static_cast<size_t>(seconds * m_rate) + 4096);
    m_offline.store(true, std::memory_order_release);
    {
        std::lock_guard<std::mutex> lk(m_simLock);
        Engine *e = m_bundle.engine;
        const SavedState s = save(e, m_sim);
        e->setSpeedControl(std::max(0.0f, std::min(1.0f, throttle)));
        e->getIgnitionModule()->m_enabled = true;
        m_sim->m_starterMotor.m_enabled = false;
        if (e->getOutputCrankshaft()->m_body.v_theta >= 0.0) e->getOutputCrankshaft()->m_body.v_theta = -1e-3;
        m_sim->m_dyno.m_enabled = true;
        m_sim->m_dyno.m_hold = true;
        m_sim->m_dyno.m_rotationSpeed = units::rpm(rpm);
        m_sim->m_dyno.m_maxTorque = units::torque(10000.0, units::ft_lb);
        offlineFrames(1.0, 1.0 / 60.0, false, nullptr, nullptr, nullptr);  // settle
        offlineFrames(seconds, 1.0 / 60.0, true, &audio, nullptr, nullptr);
        restore(e, m_sim, s);
        m_mode = -1;
    }
    m_offline.store(false, std::memory_order_release);
    const int32_t n = static_cast<int32_t>(std::min<size_t>(audio.size(), static_cast<size_t>(frames)));
    std::copy(audio.begin(), audio.begin() + n, dst);
    return n;
}

int32_t Instance::calibrateInertia(double *jEff) {
    if (!m_loaded) return ESP_ERR_STATE;
    if (jEff == nullptr) return ESP_ERR_ARG;
    if (m_cmds.size() > 0) return ESP_ERR_STATE;
    m_offline.store(true, std::memory_order_release);
    double result = 0.0;
    {
        std::lock_guard<std::mutex> lk(m_simLock);
        Engine *e = m_bundle.engine;
        const SavedState s = save(e, m_sim);
        e->getIgnitionModule()->m_enabled = false;  // motoring only: ramps through 2000 rpm, research 01 3.5
        e->setSpeedControl(0.0);
        m_sim->m_starterMotor.m_enabled = false;
        m_sim->m_dyno.m_enabled = false;
        SlavedCrankPD &pd = m_sim->pd();
        pd.configure(m_crankJ > 0 ? m_crankJ : 0.3, 8.0, 3000.0);
        pd.reset();
        pd.enabled = true;
        atg_scs::RigidBody &crank = e->getOutputCrankshaft()->m_body;
        double cmd = -crank.v_theta;
        const double h = 1.0 / m_freq;
        const double alpha = units::rpm(2000) / 0.5;  // rad/s^2
        double tUp = 0, tDn = 0;
        int nUp = 0, nDn = 0;
        int64_t steps = 0;
        auto run = [&](double rate, double dur, int tag) {
            const int64_t total = std::llround(dur * m_freq);
            const int64_t perFrame = std::max<int64_t>(1, m_freq / 100);
            for (int64_t done = 0; done < total;) {
                const int64_t n = std::min(perFrame, total - done);
                m_sim->startFrame(static_cast<double>(n) / m_freq);
                for (int64_t k = 0; k < n; ++k) {
                    cmd += rate * h;
                    pd.cmd = cmd;
                    if (!m_sim->simulateStep()) break;
                    pd.afterStep(-crank.v_theta, h);
                    const double rpm = units::toRpm(cmd);
                    if (rpm > 1800 && rpm < 2200) {
                        if (tag == 1) {
                            tUp += pd.lastT;
                            ++nUp;
                        } else if (tag == 2) {
                            tDn += pd.lastT;
                            ++nDn;
                        }
                    }
                    ++steps;
                }
                if (m_audio) m_renderer.discardInput();
                m_sim->endFrame();
                done += n;
            }
        };
        run(units::rpm(1000) / 0.5, 0.5, 0);  // 0 -> 1000 rpm
        run(0, 0.5, 0);
        run(alpha, 1.0, 1);                    // 1000 -> 3000 rpm
        run(0, 0.5, 0);
        run(-alpha, 1.0, 2);                   // 3000 -> 1000 rpm
        result = (tUp / std::max(1, nUp) - tDn / std::max(1, nDn)) / (2 * alpha);
        pd.enabled = false;
        pd.reset();
        restore(e, m_sim, s);
        m_mode = -1;
        m_simTime += static_cast<double>(steps) / m_freq;
        std::lock_guard<std::mutex> lk2(m_statsLock);
        m_stats.offlineSteps += steps;
    }
    m_offline.store(false, std::memory_order_release);
    *jEff = result;
    return ESP_OK;
}

} // namespace esp
