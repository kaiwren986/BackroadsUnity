// worker.cpp: the engine worker thread and coast mode (PLAN 3.4).
//
// The worker processes queued commands in order. When the queue is empty it waits for the next kick. It coasts (keeps
// stepping with the last command, one physics step's worth of engine time per physics-step interval of wall time, for
// at most coast_max_ms, so the sound continues) when either
//   - no command has arrived for max(10 ms, 2 physics steps of wall time, 1.5 x the last frame time): a real hitch, not
//     the normal gap between Unity's bursts of fixed steps (ADR-002, item 6); or
//   - (M2, card T2.4) the audio ring would run dry before the next command could refill it: the fill has fallen to the
//     low-water mark of one DSP buffer + one coast batch of worker time + 3 ms. In steady play the fill before a push
//     never gets there (it sits near the target minus one frame); it is the last line against an underrun when a frame
//     comes late but not late enough to count as a hitch. Never while borrowed steps are still being repaid by commands
//     that run no steps (FREE, SLAVED_CRANK): coasting then would only borrow again (measured in the M2 bench demo after
//     a 300 ms screenshot hitch: coast and repay alternated and the debt never cleared). FOLLOW_RPM repays only from
//     spare audio (instance.cpp process()), so its commands keep producing and the low-water coast stays available.
// Coasted steps are borrowed; process() subtracts them from the following commands (never below zero), restoring
// round(sum(dt) * f), and re-enters the mode (the SLAVED_CRANK PD integral resets).
// Timed waits use a high-resolution waitable timer, so a deadline is met within about 0.5 ms whatever the process timer
// resolution is (Windows 11 keeps background and console processes at 15.6 ms; a 12 ms wait could last 31 ms).
#include "instance.h"

#include "esp_log.h"
#include "platform.h"

#include <windows.h>

#include <algorithm>
#include <cmath>

namespace esp {

void Instance::workerWait(double seconds) {
    HANDLE wake = static_cast<HANDLE>(m_wake);
    const double start = nowSeconds();
    const double deadline = start + std::max(1e-4, seconds);
    DWORD r = WAIT_TIMEOUT;
    bool timed = false;
    if (m_timer != nullptr) {
        HANDLE timer = static_cast<HANDLE>(m_timer);
        LARGE_INTEGER due;
        due.QuadPart = -static_cast<LONGLONG>(std::max(1e-4, seconds) * 1e7);  // relative, 100 ns units
        if (SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) {
            HANDLE both[2] = {wake, timer};
            r = WaitForMultipleObjects(2, both, FALSE, INFINITE);
            CancelWaitableTimer(timer);
            timed = true;
        }
    }
    if (!timed) {
        const DWORD ms = static_cast<DWORD>(std::max(1.0, std::ceil(seconds * 1000.0)));
        r = WaitForSingleObject(wake, ms);
    }
    // diagnostics: a wake by the deadline (timer or timeout) that came late
    const double woke = nowSeconds();
    WorkerDiag &d = m_diag;
    d.lastWaitDeadlineS = deadline;
    d.lastWaitWokeS = woke;
    d.lastWaitByEvent = (r == WAIT_OBJECT_0);
    d.lastWaitTimed = !d.lastWaitByEvent;
    if (!d.lastWaitByEvent && d.pushes > 0 && woke - d.t0 >= 10.0) {
        const double lateMs = 1000.0 * (woke - deadline);
        if (lateMs > d.maxWakeLateMs) {
            d.maxWakeLateMs = lateMs;
            d.maxWakeLateAtS = woke - d.t0;
        }
        if (lateMs > 5.0) ++d.wakesLate5;
        if (lateMs > 20.0) ++d.wakesLate20;
    }
}

// The audio class (EspWorkerOptions.priority 3, M2) is applied here, on the worker itself: MMCSS registers the calling
// thread. Measured in the M2 release soaks: once in 10 minutes, every thread at or below above-normal priority in the
// process (the harness's frame loop and the worker) stopped for about 50 ms while the time-critical audio consumer kept
// running; the ring (33 ms) ran dry before the worker could coast. In the audio class the worker keeps running then.
void Instance::applyWorkerPriority() {
    if (!m_priorityDirty.exchange(false, std::memory_order_acq_rel)) return;
    const int p = m_wantPriority.load(std::memory_order_relaxed);
    if (p >= 3) {
        if (m_mmcss == nullptr) {
            const char *how = "failed";
            m_mmcss = enterAudioClass(&how);
            m_schedClass = how;
            logf(LogLevel::Info, "%s: worker in the audio class (%s)", m_bundle.name.c_str(), how);
        }
        return;
    }
    if (m_mmcss != nullptr) {
        leaveAudioClass(m_mmcss);
        m_mmcss = nullptr;
    }
    static const int kPriorities[5] = {THREAD_PRIORITY_LOWEST, THREAD_PRIORITY_BELOW_NORMAL, THREAD_PRIORITY_NORMAL,
                                       THREAD_PRIORITY_ABOVE_NORMAL, THREAD_PRIORITY_HIGHEST};
    SetThreadPriority(GetCurrentThread(), kPriorities[std::max(-2, std::min(2, p)) + 2]);
    m_schedClass = "priority";
}

void Instance::workerMain() {
    setCurrentThreadName(L"esp-worker");
    bool coastSpent = false;  // coasted to the limit; wait for a command before coasting again
    for (;;) {
        if (m_stop.load(std::memory_order_acquire)) break;
        applyWorkerPriority();

        Command c;
        if (m_cmds.pop(c)) {
            std::lock_guard<std::mutex> lk(m_simLock);
            const bool afterCoast = m_coasting;
            m_coasting = false;
            coastSpent = false;
            m_out.producerActive.store(true, std::memory_order_relaxed);
            process(c, afterCoast);
            continue;
        }

        const double now = nowSeconds();
        double waitS = 0.05;
        const int coastMaxMs = m_coastMaxMs.load(std::memory_order_relaxed);
        if (m_audio && m_haveLastCmd && coastMaxMs > 0 && !coastSpent && !m_offline.load(std::memory_order_acquire)) {
            const double lastKick = qpcSeconds(m_lastKickQpc.load(std::memory_order_acquire));
            const double ts = m_simSpeed > 0 ? m_simSpeed : 1.0;
            const double stepWall = m_lastCmd.dt / ts;
            const double threshold = std::max(0.010, std::max(2.0 * stepWall, 1.5 * m_lastCmd.controls.frame_dt_s));
            if (!m_coasting && stepWall > 0.0) {
                // low-water mark: one DSP buffer + the worker time of one coast batch + 3 ms
                const double rate = m_rate > 0 ? m_rate : 48000.0;
                const double fillS = static_cast<double>(m_out.ring.fill()) / rate;
                const double batchS = m_stats.wallEma > 0 ? 1.5 * m_stats.wallEma / 1000.0 : stepWall;
                const double lowS = static_cast<double>(m_fill.dspFrames()) / rate + std::min(batchS, stepWall) + 0.003;
                // borrowed steps being repaid with commands that run no steps (FOLLOW_RPM keeps producing while in debt)
                const bool inDebt = m_executed > m_target && !(m_followSpareRepay && modeOf(m_lastCmd) == ESP_MODE_FOLLOW_RPM);
                m_out.lowWaterFrames.store(m_lowWaterCoast && !inDebt ? static_cast<int32_t>(lowS * rate) : 0,
                                           std::memory_order_relaxed);
                const bool low = m_lowWaterCoast && !inDebt && m_out.producerActive.load(std::memory_order_relaxed) && fillS <= lowS &&
                                 now - lastKick >= 0.5 * stepWall;
                if (now - lastKick >= threshold || low) {
                    m_coasting = true;
                    m_coastStartS = now;
                    m_coastNextS = now;
                    if (low && now - lastKick < threshold) ++m_diag.lowWaterCoasts;
                    std::lock_guard<std::mutex> lk(m_statsLock);
                    m_stats.coastEvents += 1;
                } else {
                    waitS = std::min(waitS, lastKick + threshold - now);
                    if (m_lowWaterCoast && !inDebt && fillS > lowS) waitS = std::min(waitS, std::max(0.0005, fillS - lowS));
                    else if (m_lowWaterCoast && !inDebt) waitS = std::min(waitS, std::max(0.0005, lastKick + 0.5 * stepWall - now));
                }
            }
            if (m_coasting) {
                if (now - m_coastStartS > coastMaxMs / 1000.0) {
                    m_coasting = false;
                    coastSpent = true;
                } else if (now >= m_coastNextS) {
                    {
                        std::lock_guard<std::mutex> lk(m_simLock);
                        coastBatch();
                    }
                    m_coastNextS += stepWall;
                    if (m_coastNextS < now - 0.05) m_coastNextS = now;  // never try to catch up a long stall
                    continue;
                } else {
                    waitS = std::min(waitS, m_coastNextS - now);
                }
            }
        }
        if (!m_coasting && now - m_lastProduceS > 0.1) m_out.producerActive.store(false, std::memory_order_relaxed);
        workerWait(waitS);
    }
    if (m_mmcss != nullptr) {
        leaveAudioClass(m_mmcss);
        m_mmcss = nullptr;
    }
}

} // namespace esp
