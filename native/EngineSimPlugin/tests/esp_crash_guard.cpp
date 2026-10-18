// esp_crash_guard (ctest): proves crash_guard.cpp (PLAN 1.6, 1.9 [S5]).
// The parent runs itself as a child with BR_CRASH_DIR set to the dump folder. The child calls esp_debug_crash_test,
// which faults on a guarded plugin thread. Pass: the child dies with an access violation and the folder holds exactly
// one esp-*.dmp minidump (MDMP signature).
//
// Usage: esp_crash_guard <EngineSimPlugin.dll> <dump folder> [--child]
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "EngineSimPlugin.h"

typedef int32_t(ESP_CALL *CrashTestFn)(int32_t);

static std::vector<std::wstring> dumpsIn(const std::wstring &dir) {
    std::vector<std::wstring> found;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\esp-*.dmp").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return found;
    do {
        found.push_back(dir + L"\\" + fd.cFileName);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return found;
}

static std::wstring fullPath(const wchar_t *p) {
    wchar_t buf[4 * MAX_PATH];
    return GetFullPathNameW(p, 4 * MAX_PATH, buf, nullptr) ? std::wstring(buf) : std::wstring(p);
}

static int child(const std::wstring &dllPath) {
    // No Windows Error Reporting dialog for this deliberate crash.
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    HMODULE dll = LoadLibraryExW(dllPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (dll == nullptr) return 10;
    const auto crashTest = reinterpret_cast<CrashTestFn>(GetProcAddress(dll, "esp_debug_crash_test"));
    if (crashTest == nullptr) return 11;
    crashTest(ESP_CRASH_TEST_MAGIC);
    return 12;  // not reached
}

int wmain(int argc, wchar_t **argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: esp_crash_guard <EngineSimPlugin.dll> <dump folder> [--child]\n");
        return 2;
    }
    const std::wstring dll = fullPath(argv[1]);
    const std::wstring dir = fullPath(argv[2]);
    if (argc > 3 && std::wstring(argv[3]) == L"--child") return child(dll);

    CreateDirectoryW(dir.c_str(), nullptr);
    for (const std::wstring &old : dumpsIn(dir)) DeleteFileW(old.c_str());  // this test's own earlier output
    SetEnvironmentVariableW(L"BR_CRASH_DIR", dir.c_str());

    wchar_t self[4 * MAX_PATH];
    GetModuleFileNameW(nullptr, self, 4 * MAX_PATH);
    std::wstring cmd = L"\"" + std::wstring(self) + L"\" \"" + dll + L"\" \"" + dir + L"\" --child";
    STARTUPINFOW si = {};
    si.cb = sizeof si;
    PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(self, &cmd[0], nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) {
        std::fprintf(stderr, "esp_crash_guard: CreateProcess failed, error %lu\n", GetLastError());
        return 1;
    }
    DWORD code = 0;
    const bool exited = WaitForSingleObject(pi.hProcess, 60000) == WAIT_OBJECT_0;
    if (!exited) TerminateProcess(pi.hProcess, 99);
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    int failures = 0;
    std::printf("esp_crash_guard: child exit code 0x%08lX\n", code);
    if (!exited || code != static_cast<DWORD>(EXCEPTION_ACCESS_VIOLATION)) {
        std::printf("  FAIL  child should die with EXCEPTION_ACCESS_VIOLATION (0xC0000005)\n");
        ++failures;
    } else {
        std::printf("  ok    child died with EXCEPTION_ACCESS_VIOLATION on a plugin thread\n");
    }
    const std::vector<std::wstring> dumps = dumpsIn(dir);
    bool valid = dumps.size() == 1;
    if (valid) {
        char sig[4] = {};
        DWORD read = 0;
        HANDLE f = CreateFileW(dumps[0].c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        LARGE_INTEGER size = {};
        valid = f != INVALID_HANDLE_VALUE && ReadFile(f, sig, 4, &read, nullptr) && read == 4 &&
                std::memcmp(sig, "MDMP", 4) == 0 && GetFileSizeEx(f, &size) && size.QuadPart > 1024;
        if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
        std::printf("  dump: %ls (%lld bytes)\n", dumps[0].c_str(), static_cast<long long>(size.QuadPart));
    }
    std::printf("  %s  exactly one esp-*.dmp minidump in BR_CRASH_DIR (found %zu)\n", valid ? "ok  " : "FAIL", dumps.size());
    if (!valid) ++failures;

    std::printf(failures ? "esp_crash_guard: FAILED\n" : "esp_crash_guard: OK\n");
    return failures ? 1 : 0;
}
