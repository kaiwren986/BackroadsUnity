// crash_guard.h: SEH guard for every plugin thread (PLAN 1.6, 1.9 [S5]).
//
// A structured exception (access violation, stack overflow, an uncaught C++ exception, ...) that reaches the guard gets
// one minidump written to crash_dir(), then continues its search, so the host process (Unity, a test) handles the
// crash exactly as it would without the plugin. CrashCollector copies the dumps into the playtest crash report.
#pragma once

#include <thread>
#include <utility>

namespace esp {

// Runs fn(ctx) inside the guard. Use it as the body of every thread the plugin starts.
void run_guarded(void (*fn)(void *), void *ctx);

// Folder for plugin minidumps: %BR_CRASH_DIR% if set (tests), else %TEMP%\<company>\<product>\Crashes, the folder
// Unity uses for its own crash dumps. company/product default to the GameInfo placeholders BRDev / Backroads.
const wchar_t *crash_dir();

// std::thread whose body runs under run_guarded.
template <class F>
std::thread start_guarded_thread(F f) {
    return std::thread([f = std::move(f)]() mutable {
        run_guarded([](void *p) { (*static_cast<F *>(p))(); }, &f);
    });
}

} // namespace esp
