// worker.cpp: the engine worker thread and coast mode (PLAN 3.4).
//
// The worker processes queued commands in order. When the queue is empty it waits for the next kick. If no command
// has arrived for max(2 physics steps of wall time, 1.5 x the last frame time) (a real hitch, not the normal gap
// between Unity's bursts of fixed steps), it coasts: it keeps stepping with the last command, one physics step's worth
// of engine time per physics-step interval of wall time, for at most coast_max_ms, so the sound continues. Coasted
// steps are borrowed; process() subtracts them from the following commands (never below zero), restoring
// round(sum(dt) * f), and re-enters the mode (the SLAVED_CRANK PD integral resets).
#include "instance.h"

#include "platform.h"

#include <windows.h>

#include <algorithm>
#include <cmath>

namespace esp {

void Instance::workerMain() {
    setCurrentThreadName(L"esp-worker");
    bool coastSpent = false;  // coasted to the limit; wait for a command before coasting again
    for (;;) {
        if (m_stop.load(std::memory_order_acquire)) break;

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
            if (!m_coasting) {
                if (now - lastKick >= threshold && stepWall > 0.0) {
                    m_coasting = true;
                    m_coastStartS = now;
                    m_coastNextS = now;
                    std::lock_guard<std::mutex> lk(m_statsLock);
                    m_stats.coastEvents += 1;
                } else {
                    waitS = std::min(waitS, lastKick + threshold - now);
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
        const DWORD ms = static_cast<DWORD>(std::max(1.0, std::ceil(waitS * 1000.0)));
        WaitForSingleObject(static_cast<HANDLE>(m_wake), ms);
    }
}

} // namespace esp
