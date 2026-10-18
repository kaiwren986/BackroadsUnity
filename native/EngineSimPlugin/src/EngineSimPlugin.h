/* EngineSimPlugin.h: C ABI v1 of EngineSimPlugin.dll (PLAN 3.3, research 01 7.1-7.2).
 *
 * M0 subset: esp_version, esp_build_info and the crash-guard test hook. T1.6 adds the engine calls of 01 7.2 with the
 * revision-2 changes of PLAN 3.3; the ABI is frozen at M1 (ADR-002).
 *
 * Rules for every function: extern "C", __cdecl, returns an int32_t (a status code unless stated otherwise), and never
 * lets a C++ exception escape. Strings are UTF-8. Blittable structs start with uint32_t struct_size.
 * Thread contract: every call comes from one control thread (Unity's main thread), except esp_read_audio (audio thread).
 */
#pragma once
#include <stdint.h>

#if defined(ESP_BUILDING_DLL)
#define ESP_EXPORT __declspec(dllexport)
#else
#define ESP_EXPORT __declspec(dllimport)
#endif
#ifdef __cplusplus
#define ESP_API extern "C" ESP_EXPORT
#else
#define ESP_API ESP_EXPORT
#endif
#define ESP_CALL __cdecl

#define ESP_ABI_VERSION 1

enum {
    ESP_OK = 0,
    ESP_ERR_ARG = -1,
    ESP_ERR_COMPILE = -2,
    ESP_ERR_STATE = -3,
    ESP_ERR_TIMEOUT = -4,
    ESP_ERR_INTERNAL = -5,
    ESP_PENDING = 1
};

/* ABI version. C# checks esp_version() == ESP_ABI_VERSION at start and falls back to the torque map otherwise (PLAN 3.3). */
ESP_API int32_t ESP_CALL esp_version(void);

/* One-line JSON object describing this build, for Native.Selftest and EngineSimPlugin.version.json:
 *   {"abi":1,"git":"...","sourceHash":"...","piranha":false,"asan":false,"crt":"static","fp":"fast","msvc":...,
 *    "vendor":{"engine-sim":"85f7c3b","simple-2d-constraint-solver":"e009f4f","piranha":"432f0b1"}}
 * Returns the number of bytes written, excluding the terminating NUL (>= 0).
 * dst_utf8 == NULL and capacity == 0: returns the length needed, excluding the NUL.
 * Buffer too small: returns ESP_ERR_ARG and writes "" if capacity >= 1. */
ESP_API int32_t ESP_CALL esp_build_info(char *dst_utf8, int32_t capacity);

/* Test hook for autotest m0.crash.recover (-br-crash-test, PLAN 1.9 [S5]). With magic == ESP_CRASH_TEST_MAGIC it
 * faults on a plugin thread under the crash guard, which writes a minidump to %TEMP%\<company>\<product>\Crashes\
 * and lets the process crash as usual. It does not return. Any other value: returns ESP_ERR_ARG and does nothing. */
#define ESP_CRASH_TEST_MAGIC 0x43524153 /* "CRAS" */
ESP_API int32_t ESP_CALL esp_debug_crash_test(int32_t magic);
