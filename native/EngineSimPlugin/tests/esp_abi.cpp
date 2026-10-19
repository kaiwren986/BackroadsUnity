// esp_abi: the frozen ABI v1 contract (ADR-002): struct sizes, version, build info, argument checks, the builtins
// under fictional ids, step-indexed results (ESP_PENDING, too old, never kicked), crash folder and log calls.
#include "harness/harness.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>

using namespace esph;

static int g_fail = 0;
#define CHECK(c)                                                              \
    do {                                                                      \
        if (!(c)) {                                                           \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c);          \
            ++g_fail;                                                         \
        }                                                                     \
    } while (0)

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);
    CHECK(esp_version() == static_cast<int32_t>(ESP_ABI_VERSION));
    static_assert(sizeof(EspCreateDesc) == ESP_SIZEOF_CREATE_DESC, "ABI v1 struct size");
    static_assert(sizeof(EspEngineInfo) == ESP_SIZEOF_ENGINE_INFO, "ABI v1 struct size");
    static_assert(sizeof(EspControls) == ESP_SIZEOF_CONTROLS, "ABI v1 struct size");
    static_assert(sizeof(EspDrivelineIn) == ESP_SIZEOF_DRIVELINE_IN, "ABI v1 struct size");
    static_assert(sizeof(EspStepOut) == ESP_SIZEOF_STEP_OUT, "ABI v1 struct size");
    static_assert(sizeof(EspAudioParams) == ESP_SIZEOF_AUDIO_PARAMS, "ABI v1 struct size");
    static_assert(sizeof(EspStats) == ESP_SIZEOF_STATS, "ABI v1 struct size");
    static_assert(sizeof(EspWorkerOptions) == ESP_SIZEOF_WORKER_OPTIONS, "ABI v1 struct size");

    char info[2048];
    const int32_t need = esp_build_info(nullptr, 0);
    CHECK(need > 0);
    CHECK(esp_build_info(info, need + 1) == need);
    CHECK(std::strstr(info, "\"abi\":1") != nullptr);
    CHECK(std::strstr(info, "\"fir\":\"") != nullptr);
    CHECK(esp_build_info(info, 4) == ESP_ERR_ARG && info[0] == '\0');

    // argument checks never crash
    CHECK(esp_create(nullptr, nullptr, nullptr) == nullptr);
    EspCreateDesc small{};
    small.struct_size = 8;
    CHECK(esp_create(&small, "", "") == nullptr);
    CHECK(esp_load_builtin(nullptr, "f4_boxer_a", nullptr, 0) == ESP_ERR_ARG);
    CHECK(esp_step_begin(nullptr, 0, 0.01, nullptr, nullptr, 0) == ESP_ERR_ARG);
    float audio[64];
    CHECK(esp_read_audio(nullptr, audio, 32, 2) == ESP_ERR_ARG);
    esp_destroy(nullptr);
    CHECK(esp_debug_crash_test(0) == ESP_ERR_ARG);
    CHECK(esp_set_crash_dir("") == ESP_OK);

    // builtins: fictional ids, sane info
    const char *names[4] = {"f4_boxer_a", "v8_cross_a", "i4_synth", "v8_bigblock_a"};
    const int cylinders[4] = {4, 8, 4, 8};
    for (int i = 0; i < 4; ++i) {
        Engine eng;
        std::string err;
        CreateOpts o;
        o.audioRate = 48000;
        CHECK(eng.open(names[i], o, err));
        if (!eng.h()) {
            std::printf("  %s\n", err.c_str());
            continue;
        }
        const EspEngineInfo &inf = eng.info();
        CHECK(std::strcmp(inf.name, names[i]) == 0);
        CHECK(inf.cylinders == cylinders[i]);
        CHECK(inf.sim_frequency > 0 && inf.ir_taps > 0 && inf.source_kind == 1);
        CHECK(inf.rev_limit_rad_s > inf.redline_rad_s * 0.9);
        char e[256];
        CHECK(esp_load_builtin(eng.h(), names[i], e, sizeof e) == ESP_ERR_STATE);  // one engine per handle
        std::printf("  %s: %d cyl, %d Hz, %.3f L, IR %d taps\n", inf.name, inf.cylinders, inf.sim_frequency,
                    inf.displacement_m3 * 1000, inf.ir_taps);
    }
    {
        Engine eng;
        std::string err;
        CreateOpts o;
        CHECK(!eng.open("not_an_engine", o, err));
        CHECK(err.find("unknown builtin") != std::string::npos);
    }

    // step-indexed results
    {
        Engine eng;
        std::string err;
        CreateOpts o;
        o.audioRate = 0;
        CHECK(eng.open("i4_synth", o, err));
        EspEngine *e = eng.h();
        EspStepOut out = stepOut();
        CHECK(esp_step_end(e, 0, 0, &out) == ESP_ERR_ARG);  // never kicked
        EspDrivelineIn in = driveline(ESP_MODE_FREE);
        EspDrivelineIn bad = driveline(ESP_MODE_PROXY);
        CHECK(esp_step_begin(e, 0, 1.0 / 120, &bad, nullptr, 0) == ESP_ERR_ARG);  // not in this build
        for (int64_t k = 0; k < 200; ++k) CHECK(esp_step_begin(e, k, 1.0 / 120, &in, nullptr, 0) == ESP_OK);
        CHECK(esp_step_begin(e, 100, 1.0 / 120, &in, nullptr, 0) == ESP_ERR_ARG);  // indices are monotonic
        CHECK(waitStep(e, 199, out));
        // 10 kHz / 120 Hz with the fractional accumulator: 83 or 84 steps per command, round(sum) in total
        CHECK(out.step_index == 199 && out.steps == std::llround(200 * 10000.0 / 120) - std::llround(199 * 10000.0 / 120));
        CHECK(esp_step_end(e, 199 - ESP_RESULT_SLOTS, 0, &out) == ESP_ERR_ARG);  // too old
        CHECK(esp_step_end(e, 199 - ESP_RESULT_SLOTS + 1, 0, &out) == ESP_OK);
        const EspStats s = stats(e);
        CHECK(s.commands == 200);
        CHECK(s.steps_run == std::llround(200 * 10000.0 / 120));
        CHECK(s.steps_target == s.steps_run);
        CHECK(s.last_step_index == 199);
        std::printf("  200 commands: %lld steps (round(200 x 10000 / 120) = %lld), RTF %.2f\n",
                    static_cast<long long>(s.steps_run), std::llround(200 * 10000.0 / 120), s.rtf);
        // offline calls refuse while commands are queued, run when idle
        double t[1];
        CHECK(esp_dyno_sweep(e, 2000, 2000, 1, 1.0f, 0.1, t, nullptr) == ESP_OK);
        CHECK(t[0] > 0);
    }
    // synchronous mode
    {
        Engine eng;
        std::string err;
        CreateOpts o;
        o.audioRate = 0;
        o.workerThread = 0;
        CHECK(eng.open("i4_synth", o, err));
        EspDrivelineIn in = driveline(ESP_MODE_FREE);
        EspStepOut out = stepOut();
        CHECK(esp_step(eng.h(), 0, 1.0 / 60, &in, nullptr, 0, &out) == ESP_OK);
        CHECK(out.steps == 167);
        CHECK(esp_step(eng.h(), 1, 1.0 / 60, &in, nullptr, 0, &out) == ESP_OK);
        CHECK(out.steps == 166);  // round(2 x 166.67) - 167
    }
    char log[8192];
    while (esp_poll_log(log, sizeof log) > 0) {
    }
    std::printf("esp_abi: %s (%d failures)\n", g_fail == 0 ? "PASS" : "FAIL", g_fail);
    return g_fail == 0 ? 0 : 1;
}
