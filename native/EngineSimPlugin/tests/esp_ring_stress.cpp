// esp_ring_stress: the lock-free rings under concurrency. (1) SpscFloatRing and SpscQueue with sequence checks and
// random chunk sizes; (2) the real audio path: the worker renders while an "audio thread" calls esp_read_audio with
// random sizes and channel counts, and the control thread kicks and collects.
#include "harness/harness.h"
#include "spsc_ring.h"

#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <random>
#include <thread>
#include <vector>

using namespace esph;

static int floatRing() {
    esp::SpscFloatRing ring;
    ring.init(4096);
    const uint32_t total = 8u << 20;  // floats stay exact up to 2^24
    std::atomic<bool> bad{false};
    std::thread prod([&] {
        std::mt19937 rng(1);
        std::vector<float> buf(1500);
        uint32_t v = 0;
        while (v < total) {
            const size_t n = std::min<size_t>(1 + rng() % buf.size(), total - v);
            for (size_t i = 0; i < n; ++i) buf[i] = static_cast<float>(v + i);
            const size_t pushed = ring.push(buf.data(), n);
            v += static_cast<uint32_t>(pushed);  // what did not fit is retried (the plugin would drop it)
            if (pushed == 0) std::this_thread::yield();
        }
    });
    std::mt19937 rng(2);
    std::vector<float> buf(2000);
    uint32_t expect = 0;
    while (expect < total) {
        const size_t n = ring.pop(buf.data(), 1 + rng() % buf.size());
        for (size_t i = 0; i < n; ++i) {
            if (buf[i] != static_cast<float>(expect)) bad = true;
            ++expect;
        }
        if (n == 0) std::this_thread::yield();
    }
    prod.join();
    std::printf("  float ring: %u samples, sequence %s\n", total, bad ? "BROKEN" : "intact");
    return bad ? 1 : 0;
}

struct Item {
    uint64_t seq;
    double payload[8];
};

static int queue() {
    esp::SpscQueue<Item, 32> q;
    const uint64_t total = 2000000;
    std::atomic<bool> bad{false};
    std::thread prod([&] {
        for (uint64_t s = 0; s < total;) {
            Item it{s, {}};
            for (int i = 0; i < 8; ++i) it.payload[i] = static_cast<double>(s * 8 + i);
            if (q.push(it)) ++s;
            else std::this_thread::yield();
        }
    });
    uint64_t expect = 0;
    Item it;
    while (expect < total) {
        if (q.pop(it)) {
            if (it.seq != expect || it.payload[7] != static_cast<double>(expect * 8 + 7)) bad = true;
            ++expect;
        } else {
            std::this_thread::yield();
        }
    }
    prod.join();
    std::printf("  command queue: %llu items, sequence %s\n", static_cast<unsigned long long>(total), bad ? "BROKEN" : "intact");
    return bad ? 1 : 0;
}

static int audioPath() {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 48000;
    o.firMaxTaps = 512;
    if (!eng.open("i4_synth", o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    EspEngine *e = eng.h();
    std::atomic<bool> run{true};
    std::atomic<int64_t> got{0};
    std::atomic<bool> bad{false};
    std::thread audio([&] {
        std::mt19937 rng(3);
        std::vector<float> buf(4096 * 8);
        while (run.load()) {
            const int frames = 1 + static_cast<int>(rng() % 4096);
            const int ch = 1 + static_cast<int>(rng() % 8);
            const int32_t n = esp_read_audio(e, buf.data(), frames, ch);
            if (n < 0 || n > frames) bad = true;
            for (int i = 0; i < frames * ch; ++i)
                if (!std::isfinite(buf[i]) || std::fabs(buf[i]) > 1.0001f) bad = true;
            got += n;
            if ((rng() & 7) == 0) Sleep(1);
        }
    });
    EspDrivelineIn in = driveline(ESP_MODE_FREE);
    std::mt19937 rng(4);
    int64_t k = 0;
    const double t0 = nowS();
    while (nowS() - t0 < 4.0) {
        EspControls c = controls(static_cast<float>((rng() % 100) / 100.0), true, k < 120, 1.0f, 1.0f / 60, static_cast<int>(k / 2));
        esp_set_controls(e, &c);
        if (esp_step_begin(e, k, 1.0 / 120, &in, nullptr, 0) != ESP_OK) bad = true;
        EspStepOut out = stepOut();
        if (k >= 3) esp_step_end(e, k - 3, static_cast<int32_t>(rng() % 2000), &out);
        ++k;
        if ((rng() % 16) == 0) Sleep(static_cast<DWORD>(rng() % 4));
    }
    EspStepOut out = stepOut();
    waitStep(e, k - 1, out);
    run = false;
    audio.join();
    const EspStats s = stats(e);
    if (s.audio_frames_consumed > s.audio_frames_produced) bad = true;
    std::printf("  audio path: %lld commands, produced %lld, consumed %lld, dropped %lld, underruns %d -> %s\n",
                static_cast<long long>(k), static_cast<long long>(s.audio_frames_produced),
                static_cast<long long>(s.audio_frames_consumed), static_cast<long long>(s.audio_frames_dropped), s.audio_underruns,
                bad ? "BAD" : "ok");
    drainLog();
    return bad ? 1 : 0;
}

int main() {
    setvbuf(stdout, nullptr, _IONBF, 0);
    int f = 0;
    f += floatRing();
    f += queue();
    f += audioPath();
    std::printf("esp_ring_stress: %s\n", f == 0 ? "PASS" : "FAIL");
    return f == 0 ? 0 : 1;
}
