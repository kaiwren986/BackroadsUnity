// audio_render.h: renderSync on the engine worker, the float ring to the audio thread, the frame-sized fill target,
// the drift trim and underrun handling (PLAN 3.6, research 01 4.2-4.4).
//
// Producer (worker), per command, after the engine steps and before endFrame():
//   AudioRenderer::renderSync() reads every pending synthesizer input sample and renders it with engine-sim's own
//   per-sample chain (jitter, DC removal, derivative mix, air noise, leveler, volume) but our CPUID-dispatched FIR in
//   place of the scalar ConvolutionFilter (Synthesizer's members are public, so nothing is forked).
// Consumer (audio thread): AudioOutput::read() is lock-free and allocation-free; on underrun it holds and decays the
//   last sample over 2 ms, counts the event, and waits for half the target before fading back in.
// FillController (worker): target = p95 frame time + worker time for one frame's commands + one DSP buffer + 5 ms,
//   re-computed every 2 s with hysteresis; drift trim epsilon = clamp(k (fill - target) / target, +-0.5 %), smoothed,
//   applied as setInputSampleRate(f * sim_speed * (1 + epsilon)).
#pragma once

#include "fir.h"
#include "spsc_ring.h"

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

class Synthesizer;

namespace esp {

class AudioOutput {
public:
    void init(size_t ringFrames, int sampleRate, int initialTargetFrames);
    // Audio thread.
    int32_t read(float *dst, int32_t frames, int32_t channels);

    SpscFloatRing ring;
    std::atomic<int32_t> targetFrames{2400};
    std::atomic<bool> producerActive{false};  // the worker produced recently (underruns count only then)
    std::atomic<int32_t> underruns{0};
    std::atomic<int32_t> underrunFrames{0};
    std::atomic<int64_t> consumed{0};

private:
    std::vector<float> m_scratch;
    float m_last = 0.0f;
    float m_decay = 0.98f;   // per sample, 2 ms to about -35 dB
    float m_fadeStep = 0.02f;
    float m_fadeGain = 1.0f;
    bool m_primed = false;    // false until the ring first reaches the target (start) or half of it (after underrun)
    bool m_inUnderrun = false;
    bool m_everPrimed = false;
};

class FillController {
public:
    void init(int sampleRate, int dspBufferFrames, int initialTargetFrames, bool trimEnabled);
    // Per command: the frame it belongs to, that frame's duration (s, 0 = unknown) and the worker wall time spent.
    void onCommand(int32_t frameIndex, double frameDtS, double workerSeconds, double nowS);
    // After pushing a batch: returns the new epsilon.
    double onPushed(size_t fillFrames);
    int32_t targetFrames() const { return m_target; }
    double epsilon() const { return m_eps; }
    double frameP95Ms() const { return m_p95FrameMs; }
    double workerFrameMs() const { return m_workerFrameMs; }

private:
    void recompute(double nowS);

    int m_rate = 48000;
    int m_dsp = 256;
    int32_t m_target = 2400;
    bool m_trim = true;
    double m_eps = 0.0;
    int32_t m_frame = INT32_MIN;
    double m_frameDt = 0.0, m_frameWork = 0.0;
    std::vector<double> m_frameDts, m_frameWorks;  // ring of the last 256 frames
    std::vector<double> m_scratch;                // p95 work space, allocated once (no steady-state allocation)
    size_t m_next = 0, m_count = 0;
    double m_lastRecompute = -1.0;
    double m_p95FrameMs = 0.0, m_workerFrameMs = 0.0;
    int m_lowerVotes = 0;
};

// What the engine produced, teed before the ring (esp_debug_tap). Worker only.
struct AudioTapBuffer {
    std::string path;
    std::vector<float> data;
    size_t want = 0;
    bool active = false;
    void append(const float *x, size_t n);
    bool full() const { return active && data.size() >= want; }
};

class AudioRenderer {
public:
    // irs[c]: taps for exhaust channel c at the output rate (empty = dry).
    void init(Synthesizer *s, int sampleRate, const std::vector<std::vector<float>> &irs);
    void setFirCap(int maxTaps);
    // Renders all pending input; pushes into the ring; tees into tap. Returns frames rendered.
    size_t renderSync(AudioOutput &out, AudioTapBuffer *tap);
    // Offline: renders into dst instead of the ring (esp_render_offline). Returns frames rendered.
    size_t renderInto(std::vector<float> &dst);
    // Drops pending input (offline steps without audio).
    void discardInput();

    int64_t produced() const { return m_produced; }
    int64_t dropped() const { return m_dropped; }
    int64_t clipped() const { return m_clipped; }
    int firTaps(int channel) const;

private:
    size_t prepare();             // reads the input block into transfer buffers, returns n
    float renderSample(int i);    // Synthesizer::renderAudio(int) with our FIR, as a float in [-1, 1]

    Synthesizer *m_s = nullptr;
    int m_rate = 48000;
    std::vector<std::vector<float>> m_irs;
    std::vector<Fir> m_fir;
    std::vector<float> m_block;
    uint64_t m_rng = 0x9E3779B97F4A7C15ull;
    int64_t m_produced = 0, m_dropped = 0, m_clipped = 0;
};

} // namespace esp
