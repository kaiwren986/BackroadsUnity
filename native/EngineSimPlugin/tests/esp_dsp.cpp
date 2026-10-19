// esp_dsp: the plugin's own DSP pieces against references. FIR (AVX2, SSE2, scalar) against engine-sim's
// ConvolutionFilter; 44.1 -> 48 kHz resampling keeps frequency and level; WAV reader round trip.
#include "fir.h"
#include "resample.h"
#include "wav_io.h"

#include "convolution_filter.h"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <random>
#include <string>
#include <vector>

static int g_fail = 0;
#define CHECK(c)                                                              \
    do {                                                                      \
        if (!(c)) {                                                           \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #c);          \
            ++g_fail;                                                         \
        }                                                                     \
    } while (0)

static void firMatches(int taps) {
    std::mt19937 rng(taps);
    std::uniform_real_distribution<float> u(-1.0f, 1.0f);
    std::vector<float> h(static_cast<size_t>(taps));
    for (float &v : h) v = u(rng) * 0.01f;
    ConvolutionFilter ref;
    ref.initialize(taps);
    for (int i = 0; i < taps; ++i) ref.getImpulseResponse()[i] = h[static_cast<size_t>(i)];
    esp::Fir a, s, c;
    a.init(h.data(), taps, esp::cpuHasAvx2Fma() ? esp::firDotAvx2 : esp::firDotSse2);
    s.init(h.data(), taps, esp::firDotSse2);
    c.init(h.data(), taps, esp::firDotScalar);
    double maxErr = 0, maxRef = 0;
    for (int n = 0; n < 3 * taps + 1000; ++n) {
        const float x = u(rng);
        const float yr = ref.f(x), ya = a.process(x), ys = s.process(x), yc = c.process(x);
        maxRef = std::max(maxRef, static_cast<double>(std::fabs(yr)));
        maxErr = std::max({maxErr, static_cast<double>(std::fabs(ya - yr)), static_cast<double>(std::fabs(ys - yr)),
                           static_cast<double>(std::fabs(yc - yr))});
    }
    ref.destroy();
    const double rel = maxErr / std::max(1e-9, maxRef);
    std::printf("  FIR %5d taps: max |kernel - ConvolutionFilter| %.2e (relative %.2e)\n", taps, maxErr, rel);
    CHECK(rel < 1e-4);
}

static void firSpeed() {
    const int taps = 10000;
    std::vector<float> h(taps, 0.0001f);
    ConvolutionFilter ref;
    ref.initialize(taps);
    for (int i = 0; i < taps; ++i) ref.getImpulseResponse()[i] = h[static_cast<size_t>(i)];
    esp::Fir f;
    f.init(h.data(), taps);
    const int n = 48000;
    float acc = 0;
    auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < n; ++i) acc += f.process(std::sin(i * 0.1f));
    auto t1 = std::chrono::steady_clock::now();
    for (int i = 0; i < n / 4; ++i) acc += ref.f(std::sin(i * 0.1f));
    auto t2 = std::chrono::steady_clock::now();
    ref.destroy();
    const double us = std::chrono::duration<double, std::micro>(t1 - t0).count() / n;
    const double usRef = std::chrono::duration<double, std::micro>(t2 - t1).count() / (n / 4);
    std::printf("  FIR 10000 taps, %s: %.3f us/sample (ConvolutionFilter %.3f, %.1fx) (acc %g)\n", esp::firKernelName(), us,
                usRef, usRef / us, acc);
    CHECK(us < usRef);
}

static void resampler() {
    // 1 kHz sine at 44.1 kHz -> 48 kHz: frequency (zero crossings) and RMS preserved
    const int n = 44100;
    std::vector<float> x(n);
    for (int i = 0; i < n; ++i) x[i] = 0.5f * static_cast<float>(std::sin(2 * 3.14159265358979 * 1000.0 * i / 44100.0));
    const std::vector<float> y = esp::resampleSinc(x.data(), x.size(), 44100, 48000);
    CHECK(y.size() == 48000);
    int crossings = 0;
    double sq = 0;
    for (size_t i = 2000; i + 2000 < y.size(); ++i) {
        if ((y[i - 1] < 0) != (y[i] < 0)) ++crossings;
        sq += static_cast<double>(y[i]) * y[i];
    }
    const double secs = (y.size() - 4000) / 48000.0;
    const double freq = crossings / 2.0 / secs;
    const double rms = std::sqrt(sq / (y.size() - 4000));
    std::printf("  resample 44.1 -> 48 kHz: %zu samples, tone %.2f Hz (1000), RMS %.4f (%.4f)\n", y.size(), freq, rms,
                0.5 / std::sqrt(2.0));
    CHECK(std::fabs(freq - 1000.0) < 1.0);
    CHECK(std::fabs(rms - 0.5 / std::sqrt(2.0)) < 0.005);
}

static void wavRoundTrip(const std::string &dir) {
    std::vector<float> x(4800);
    for (size_t i = 0; i < x.size(); ++i) x[i] = static_cast<float>(std::sin(i * 0.05)) * 0.8f;
    const std::string path = dir + "/esp_dsp_roundtrip.wav";
    std::string err;
    CHECK(esp::writeWav16Mono(path, x.data(), x.size(), 48000, err));
    esp::WavData w;
    CHECK(esp::readWav16(path, w, err));
    CHECK(w.sampleRate == 48000 && w.channels == 1 && w.samples.size() == x.size());
    double maxErr = 0;
    for (size_t i = 0; i < x.size() && i < w.samples.size(); ++i)
        maxErr = std::max(maxErr, std::fabs(w.samples[i] / 32767.0 - x[i]));
    CHECK(maxErr < 1.0 / 16000);
    std::remove(path.c_str());
}

static void irFiles(const std::string &es) {
    // every shipped impulse response reads, 44.1 kHz mono 16-bit (research 01 4.1)
    const char *files[] = {"sound-library/new/minimal_muffling_02.wav", "sound-library/smooth/smooth_39.wav",
                           "sound-library/sharp/sharp_01.wav", "sound-library/new/mild_exhaust.wav"};
    for (const char *f : files) {
        esp::WavData w;
        std::string err;
        const bool ok = esp::readWav16(es + "/" + f, w, err);
        CHECK(ok);
        if (ok) std::printf("  IR %s: %d Hz, %d ch, %zu frames\n", f, w.sampleRate, w.channels, w.frames());
        else std::printf("  %s\n", err.c_str());
    }
}

int main(int argc, char **argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    const std::string es = argc > 1 ? argv[1] : "vendor/engine-sim/es";
    const std::string tmp = argc > 2 ? argv[2] : ".";
    std::printf("  CPU AVX2+FMA: %s; Fir uses %s\n", esp::cpuHasAvx2Fma() ? "yes" : "no", esp::firKernelName());
    for (int taps : {1, 31, 32, 33, 1893, 6907, 10000}) firMatches(taps);
    firSpeed();
    resampler();
    wavRoundTrip(tmp);
    irFiles(es);
    std::printf("esp_dsp: %s (%d failures)\n", g_fail == 0 ? "PASS" : "FAIL", g_fail);
    return g_fail == 0 ? 0 : 1;
}
