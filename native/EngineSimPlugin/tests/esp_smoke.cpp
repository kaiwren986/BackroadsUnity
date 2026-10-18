// esp_smoke (ctest): loads EngineSimPlugin.dll the way Unity does (LoadLibrary + GetProcAddress, nothing linked) and
// checks the ABI version, esp_build_info, and that the DLL imports no VC++ runtime DLL (static CRT, PLAN 1.6).
//
// Usage: esp_smoke <EngineSimPlugin.dll> [--info]
//   --info  print esp_build_info() and nothing else (tools\ci\build-native.ps1 writes the version file from it)
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "EngineSimPlugin.h"

typedef int32_t(ESP_CALL *VersionFn)(void);
typedef int32_t(ESP_CALL *BuildInfoFn)(char *, int32_t);
typedef int32_t(ESP_CALL *CrashTestFn)(int32_t);

static int g_failures = 0;

static void check(bool ok, const char *what) {
    std::printf("  %s  %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++g_failures;
}

static bool startsWithI(const char *s, const char *prefix) {
    return _strnicmp(s, prefix, std::strlen(prefix)) == 0;
}

// Names of the DLLs a loaded module imports, read from its import directory.
static std::vector<std::string> importsOf(HMODULE module) {
    std::vector<std::string> names;
    const BYTE *base = reinterpret_cast<const BYTE *>(module);
    const auto *dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);
    const auto *nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(base + dos->e_lfanew);
    const IMAGE_DATA_DIRECTORY &dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (dir.VirtualAddress == 0) return names;
    for (const auto *d = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR *>(base + dir.VirtualAddress); d->Name; ++d) {
        names.emplace_back(reinterpret_cast<const char *>(base + d->Name));
    }
    return names;
}

int wmain(int argc, wchar_t **argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: esp_smoke <EngineSimPlugin.dll> [--info]\n");
        return 2;
    }
    const bool infoOnly = argc > 2 && std::wcscmp(argv[2], L"--info") == 0;

    wchar_t path[4 * MAX_PATH];
    if (GetFullPathNameW(argv[1], 4 * MAX_PATH, path, nullptr) == 0) return 2;
    HMODULE dll = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (dll == nullptr) {
        std::fprintf(stderr, "esp_smoke: LoadLibrary(%ls) failed, error %lu\n", path, GetLastError());
        return 1;
    }
    const auto version = reinterpret_cast<VersionFn>(GetProcAddress(dll, "esp_version"));
    const auto buildInfo = reinterpret_cast<BuildInfoFn>(GetProcAddress(dll, "esp_build_info"));
    const auto crashTest = reinterpret_cast<CrashTestFn>(GetProcAddress(dll, "esp_debug_crash_test"));
    if (version == nullptr || buildInfo == nullptr) {
        std::fprintf(stderr, "esp_smoke: esp_version or esp_build_info is not exported\n");
        return 1;
    }

    const int32_t need = buildInfo(nullptr, 0);
    std::string info;
    if (need > 0) {
        std::vector<char> buf(static_cast<size_t>(need) + 1, 'x');
        const int32_t n = buildInfo(buf.data(), need + 1);
        if (n == need && buf[static_cast<size_t>(n)] == '\0') info.assign(buf.data(), static_cast<size_t>(n));
    }
    if (infoOnly) {
        if (info.empty()) return 1;
        std::printf("%s\n", info.c_str());
        return 0;
    }

    std::printf("esp_smoke: %ls\n", path);
    const int32_t v = version();
    std::printf("  esp_version() = %d\n", v);
    check(v == ESP_ABI_VERSION, "esp_version() == 1");
    check(!info.empty(), "esp_build_info(NULL, 0) gives the length; a buffer of that size gets the whole string");
    check(info.find("\"abi\":1,") != std::string::npos, "build info reports abi 1");
    char small[4] = {'x', 'x', 'x', 'x'};
    check(buildInfo(small, sizeof small) == ESP_ERR_ARG && small[0] == '\0',
          "esp_build_info with a short buffer returns ESP_ERR_ARG and writes \"\"");
    check(buildInfo(nullptr, 8) == ESP_ERR_ARG, "esp_build_info(NULL, 8) returns ESP_ERR_ARG");
    check(crashTest != nullptr && crashTest(0) == ESP_ERR_ARG, "esp_debug_crash_test without the magic does nothing");

    const bool asan = info.find("\"asan\":true") != std::string::npos;
    std::string list;
    bool runtimeDll = false;
    for (const std::string &name : importsOf(dll)) {
        list += (list.empty() ? "" : ", ") + name;
        const char *n = name.c_str();
        if (startsWithI(n, "vcruntime") || startsWithI(n, "msvcp") || startsWithI(n, "api-ms-win-crt-") ||
            startsWithI(n, "ucrtbase") || startsWithI(n, "concrt")) {
            runtimeDll = true;
        }
    }
    std::printf("  imports: %s\n", list.c_str());
    if (asan) {
        std::printf("  skip  static-CRT import check (ASan build uses the ASan runtime DLL)\n");
    } else {
        check(!runtimeDll && info.find("\"crt\":\"static\"") != std::string::npos,
              "static CRT: no VC++ runtime DLL imports (/MT)");
    }
    std::printf("  build info: %s\n", info.c_str());

    FreeLibrary(dll);
    if (g_failures != 0) {
        std::printf("esp_smoke: FAILED (%d checks)\n", g_failures);
        return 1;
    }
    std::printf("esp_smoke: OK\n");
    return 0;
}
