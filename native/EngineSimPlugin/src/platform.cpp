// platform.cpp: see platform.h.
#include "platform.h"

#include <windows.h>
#include <intrin.h>

#include <mutex>
#include <vector>

namespace esp {
namespace {

double g_qpcPeriod = 0.0;
std::once_flag g_qpcOnce;

void initQpc() {
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_qpcPeriod = 1.0 / static_cast<double>(f.QuadPart);
}

double g_tscHz = 0.0;
std::once_flag g_tscOnce;

// QueryThreadCycleTime counts TSC ticks on invariant-TSC CPUs. Calibrate __rdtsc against QPC over 20 ms (once).
void calibrateTsc() {
    std::call_once(g_qpcOnce, initQpc);
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    const uint64_t t0 = __rdtsc();
    do {
        QueryPerformanceCounter(&b);
    } while ((b.QuadPart - a.QuadPart) * g_qpcPeriod < 0.02);
    const uint64_t t1 = __rdtsc();
    g_tscHz = static_cast<double>(t1 - t0) / ((b.QuadPart - a.QuadPart) * g_qpcPeriod);
    if (!(g_tscHz > 1e8)) g_tscHz = 3.0e9;
}

CpuTopology g_topo;
std::vector<BYTE> g_classOfLp;  // group 0 logical processor -> efficiency class
BYTE g_maxClass = 0;
std::once_flag g_topoOnce;

void loadTopology() {
    ULONG len = 0;
    GetSystemCpuSetInformation(nullptr, 0, &len, GetCurrentProcess(), 0);
    if (len == 0) return;
    std::vector<char> buf(len);
    auto *first = reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buf.data());
    if (!GetSystemCpuSetInformation(first, len, &len, GetCurrentProcess(), 0)) return;
    for (int pass = 0; pass < 2; ++pass) {
        for (ULONG off = 0; off < len;) {
            auto *info = reinterpret_cast<PSYSTEM_CPU_SET_INFORMATION>(buf.data() + off);
            if (info->Type == CpuSetInformation && info->CpuSet.Group == 0 && info->CpuSet.LogicalProcessorIndex < 64) {
                const auto &cs = info->CpuSet;
                if (pass == 0) {
                    if (cs.EfficiencyClass > g_maxClass) g_maxClass = cs.EfficiencyClass;
                    if (g_classOfLp.size() <= cs.LogicalProcessorIndex) g_classOfLp.resize(cs.LogicalProcessorIndex + 1, 0);
                    g_classOfLp[cs.LogicalProcessorIndex] = cs.EfficiencyClass;
                } else if (cs.EfficiencyClass == g_maxClass) {
                    g_topo.pMask |= 1ull << cs.LogicalProcessorIndex;
                    ++g_topo.pCount;
                } else {
                    g_topo.eMask |= 1ull << cs.LogicalProcessorIndex;
                    ++g_topo.eCount;
                }
            }
            off += info->Size;
        }
    }
    g_topo.hybrid = g_topo.eCount > 0 && g_topo.pCount > 0;
}

} // namespace

int64_t qpcNow() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

double qpcSeconds(int64_t ticks) {
    std::call_once(g_qpcOnce, initQpc);
    return static_cast<double>(ticks) * g_qpcPeriod;
}

double nowSeconds() { return qpcSeconds(qpcNow()); }

double threadCpuSeconds() {
    std::call_once(g_tscOnce, calibrateTsc);
    ULONG64 cycles = 0;
    QueryThreadCycleTime(GetCurrentThread(), &cycles);
    return static_cast<double>(cycles) / g_tscHz;
}

const CpuTopology &cpuTopology() {
    std::call_once(g_topoOnce, loadTopology);
    return g_topo;
}

bool onPerformanceCore() {
    const CpuTopology &t = cpuTopology();
    if (!t.hybrid) return true;
    PROCESSOR_NUMBER pn;
    GetCurrentProcessorNumberEx(&pn);
    if (pn.Group != 0 || pn.Number >= g_classOfLp.size()) return false;
    return g_classOfLp[pn.Number] == g_maxClass;
}

void setCurrentThreadName(const wchar_t *name) {
    SetThreadDescription(GetCurrentThread(), name);
}

bool setCurrentThreadScheduling(int priority, bool powerThrottlingOff) {
    static const int kPriorities[5] = {THREAD_PRIORITY_LOWEST, THREAD_PRIORITY_BELOW_NORMAL, THREAD_PRIORITY_NORMAL,
                                       THREAD_PRIORITY_ABOVE_NORMAL, THREAD_PRIORITY_HIGHEST};
    if (priority < -2) priority = -2;
    if (priority > 2) priority = 2;
    bool ok = SetThreadPriority(GetCurrentThread(), kPriorities[priority + 2]) != 0;
    THREAD_POWER_THROTTLING_STATE s = {};
    s.Version = THREAD_POWER_THROTTLING_CURRENT_VERSION;
    s.ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED;
    s.StateMask = powerThrottlingOff ? 0 : THREAD_POWER_THROTTLING_EXECUTION_SPEED;
    if (powerThrottlingOff) {
        ok = SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, &s, sizeof s) != 0 && ok;
    }
    return ok;
}

bool setCurrentThreadAffinity(uint64_t mask) {
    if (mask == 0) return true;
    return SetThreadAffinityMask(GetCurrentThread(), static_cast<DWORD_PTR>(mask)) != 0;
}

} // namespace esp
