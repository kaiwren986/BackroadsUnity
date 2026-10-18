// crash_guard.cpp: see crash_guard.h. Nothing here allocates once a fault is being handled.
#include "crash_guard.h"

#include <windows.h>
#include <dbghelp.h>

#include <cwchar>

#ifndef ESP_CRASH_COMPANY
#define ESP_CRASH_COMPANY L"BRDev"      // GameInfo placeholder (PLAN T0.5) until U18
#endif
#ifndef ESP_CRASH_PRODUCT
#define ESP_CRASH_PRODUCT L"Backroads"
#endif

namespace esp {
namespace {

constexpr DWORD kSetThreadNameException = 0x406D1388;  // MSVC thread naming; handled by debuggers, never a crash
constexpr DWORD kDbgPrintExceptionWide = 0x4001000A;

INIT_ONCE g_dirOnce = INIT_ONCE_STATIC_INIT;
wchar_t g_dir[MAX_PATH * 2] = {};
volatile LONG g_dumped = 0;

BOOL CALLBACK initDir(PINIT_ONCE, PVOID, PVOID *) {
    DWORD n = GetEnvironmentVariableW(L"BR_CRASH_DIR", g_dir, ARRAYSIZE(g_dir));
    if (n > 0 && n < ARRAYSIZE(g_dir)) return TRUE;
    wchar_t tmp[MAX_PATH + 1];
    n = GetTempPathW(ARRAYSIZE(tmp), tmp);  // ends with a backslash
    if (n == 0 || n > MAX_PATH) {
        g_dir[0] = L'\0';
        return TRUE;
    }
    _snwprintf_s(g_dir, _TRUNCATE, L"%ls%ls\\%ls\\Crashes", tmp, ESP_CRASH_COMPANY, ESP_CRASH_PRODUCT);
    return TRUE;
}

// Creates every missing folder of path.
bool ensureDir(const wchar_t *path) {
    wchar_t buf[ARRAYSIZE(g_dir)];
    if (wcscpy_s(buf, path) != 0) return false;
    for (wchar_t *p = buf + 1; *p; ++p) {
        if ((*p == L'\\' || *p == L'/') && p[-1] != L':' && p[-1] != L'\\') {
            const wchar_t c = *p;
            *p = L'\0';
            CreateDirectoryW(buf, nullptr);
            *p = c;
        }
    }
    return CreateDirectoryW(buf, nullptr) || GetLastError() == ERROR_ALREADY_EXISTS;
}

struct DumpJob {
    EXCEPTION_POINTERS *ep;
    DWORD threadId;
};

DWORD WINAPI writeDump(LPVOID param) {
    const DumpJob *job = static_cast<const DumpJob *>(param);
    if (g_dir[0] == L'\0' || !ensureDir(g_dir)) return 1;
    SYSTEMTIME t;
    GetLocalTime(&t);
    wchar_t path[ARRAYSIZE(g_dir) + 64];
    _snwprintf_s(path, _TRUNCATE, L"%ls\\esp-%04u%02u%02u-%02u%02u%02u-%lu-%lu.dmp", g_dir, t.wYear, t.wMonth, t.wDay,
                 t.wHour, t.wMinute, t.wSecond, GetCurrentProcessId(), job->threadId);
    HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return 1;
    MINIDUMP_EXCEPTION_INFORMATION mei = {};
    mei.ThreadId = job->threadId;
    mei.ExceptionPointers = job->ep;
    mei.ClientPointers = FALSE;
    const auto type = static_cast<MINIDUMP_TYPE>(MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithThreadInfo |
                                                 MiniDumpWithUnloadedModules);
    const BOOL ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), f, type, &mei, nullptr, nullptr);
    CloseHandle(f);
    return ok ? 0 : 1;
}

LONG filter(EXCEPTION_POINTERS *ep) {
    const DWORD code = ep->ExceptionRecord->ExceptionCode;
    if (code == kSetThreadNameException || code == DBG_PRINTEXCEPTION_C || code == kDbgPrintExceptionWide) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    if (InterlockedCompareExchange(&g_dumped, 1, 0) == 0) {  // one dump per process
        DumpJob job{ep, GetCurrentThreadId()};
        // Written from a helper thread with its own stack, so a stack overflow can still be dumped.
        HANDLE h = CreateThread(nullptr, 256 * 1024, writeDump, &job, 0, nullptr);
        if (h != nullptr) {
            WaitForSingleObject(h, 30000);
            CloseHandle(h);
        } else {
            writeDump(&job);
        }
    }
    return EXCEPTION_CONTINUE_SEARCH;  // the host's own crash handling runs next
}

} // namespace

const wchar_t *crash_dir() {
    InitOnceExecuteOnce(&g_dirOnce, initDir, nullptr, nullptr);
    return g_dir;
}

void run_guarded(void (*fn)(void *), void *ctx) {
    crash_dir();  // resolve the folder before anything can fault
    __try {
        fn(ctx);
    } __except (filter(GetExceptionInformation())) {
    }
}

} // namespace esp
