// platform.h: Windows timing, CPU topology and thread helpers used by the worker and the tools.
#pragma once

#include <cstdint>

namespace esp {

// Wall clock (QueryPerformanceCounter).
int64_t qpcNow();
double qpcSeconds(int64_t ticks);  // ticks -> seconds
double nowSeconds();

// CPU time of the calling thread (QueryThreadCycleTime, cycle-exact) in seconds; the TSC rate is calibrated once.
double threadCpuSeconds();

// Hybrid CPUs: logical processors of group 0 with the highest efficiency class are performance cores.
struct CpuTopology {
    uint64_t pMask = 0, eMask = 0;
    int pCount = 0, eCount = 0;
    bool hybrid = false;
};
const CpuTopology &cpuTopology();
bool onPerformanceCore();  // the processor the calling thread runs on right now

void setCurrentThreadName(const wchar_t *name);
// priority -2..2; powerThrottlingOff: EcoQoS off for this thread. Returns false if Windows refused.
bool setCurrentThreadScheduling(int priority, bool powerThrottlingOff);
bool setCurrentThreadAffinity(uint64_t mask);

} // namespace esp
