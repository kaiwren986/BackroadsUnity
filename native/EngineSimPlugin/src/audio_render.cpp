// audio_render.cpp: see audio_render.h.
#include "audio_render.h"

#include "platform.h"
#include "synthesizer.h"

#include <algorithm>
#include <cmath>

namespace esp {

// ---------------------------------------------------------------------------------------------------------------
// AudioOutput (consumer side)

void AudioOutput::init(size_t ringFrames, int sampleRate, int initialTargetFrames) {
    ring.init(ringFrames);
    targetFrames.store(initialTargetFrames);
    m_scratch.assign(1024, 0.0f);
    // Hold and decay over 2 ms: e^-4 (about -35 dB) after 2 ms.
    m_decay = static_cast<float>(std::exp(-4.0 / (0.002 * sampleRate)));
    // Fade back in over about 1 ms after an underrun.
    m_fadeStep = static_cast<float>(1.0 / (0.001 * sampleRate));
    m_fadeGain = 1.0f;
    m_last = 0.0f;
    m_primed = false;
    m_everPrimed = false;
    m_inUnderrun = false;
}

int32_t AudioOutput::read(float *dst, int32_t frames, int32_t channels) {
    if (frames <= 0 || channels <= 0 || dst == nullptr) return 0;
    const int32_t target = targetFrames.load(std::memory_order_relaxed);
    if (!m_primed) {
        const size_t need = static_cast<size_t>(m_everPrimed ? std::max(1, target / 2) : std::max(1, target));
        if (ring.fill() >= need) {
            m_primed = true;
            m_everPrimed = true;
        }
    }

    int32_t got = 0;
    if (m_primed) {
        while (got < frames) {
            const size_t want = std::min(static_cast<size_t>(frames - got), m_scratch.size());
            const size_t n = ring.pop(m_scratch.data(), want);
            for (size_t k = 0; k < n; ++k) {
                float s = m_scratch[k];
                if (m_fadeGain < 1.0f) {
                    s *= m_fadeGain;
                    m_fadeGain = std::min(1.0f, m_fadeGain + m_fadeStep);
                }
                m_last = s;
                float *o = dst + static_cast<size_t>(got + static_cast<int32_t>(k)) * channels;
                for (int32_t c = 0; c < channels; ++c) o[c] = s;
            }
            got += static_cast<int32_t>(n);
            if (n < want) break;
        }
    }

    if (got < frames) {
        const bool active = producerActive.load(std::memory_order_relaxed);
        if (m_primed) {  // ran dry
            m_primed = false;
            if (active && !m_inUnderrun) {
                lastUnderrunQpc.store(qpcNow(), std::memory_order_relaxed);
                underruns.fetch_add(1, std::memory_order_relaxed);
            }
            m_inUnderrun = active;
        }
        if (active && m_everPrimed) underrunFrames.fetch_add(frames - got, std::memory_order_relaxed);
        for (int32_t i = got; i < frames; ++i) {
            m_last *= m_decay;
            float *o = dst + static_cast<size_t>(i) * channels;
            for (int32_t c = 0; c < channels; ++c) o[c] = m_last;
        }
        m_fadeGain = 0.0f;
    } else {
        m_inUnderrun = false;
    }
    consumed.fetch_add(got, std::memory_order_relaxed);
    return got;
}

// ---------------------------------------------------------------------------------------------------------------
// FillController (worker side)

void FillController::init(int sampleRate, int dspBufferFrames, int initialTargetFrames, bool trimEnabled) {
    m_rate = sampleRate;
    m_dsp = dspBufferFrames;
    m_target = initialTargetFrames;
    m_trim = trimEnabled;
    m_eps = 0.0;
    m_frame = INT32_MIN;
    m_frameDts.assign(256, 0.0);
    m_frameWorks.assign(256, 0.0);
    m_scratch.assign(256, 0.0);
    m_next = m_count = 0;
    m_lastRecompute = -1.0;
}

void FillController::onCommand(int32_t frameIndex, double frameDtS, double workerSeconds, double nowS) {
    if (frameDtS <= 0.0) return;  // no frame information (tools, tests): keep the initial target
    if (frameIndex != m_frame) {
        if (m_frame != INT32_MIN) {
            m_frameDts[m_next] = std::min(m_frameDt, 0.25);
            m_frameWorks[m_next] = m_frameWork;
            m_next = (m_next + 1) % m_frameDts.size();
            if (m_count < m_frameDts.size()) ++m_count;
        }
        m_frame = frameIndex;
        m_frameDt = frameDtS;
        m_frameWork = 0.0;
    }
    m_frameWork += workerSeconds;
    if (m_lastRecompute < 0.0) m_lastRecompute = nowS;
    if (nowS - m_lastRecompute >= 2.0 || (m_p95FrameMs == 0.0 && m_count >= 16)) {  // first target after 16 frames
        recompute(nowS);
    }
}

namespace {

// p95 of the first n values of v, using scratch (same size as v) so the worker never allocates here.
double p95(const std::vector<double> &v, size_t n, std::vector<double> &scratch) {
    n = std::min(n, std::min(v.size(), scratch.size()));
    if (n == 0) return 0.0;
    std::copy(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(n), scratch.begin());
    const size_t k = static_cast<size_t>(std::ceil(0.95 * static_cast<double>(n))) - 1;
    const auto end = scratch.begin() + static_cast<std::ptrdiff_t>(n);
    std::nth_element(scratch.begin(), scratch.begin() + static_cast<std::ptrdiff_t>(k), end);
    return scratch[k];
}

} // namespace

void FillController::recompute(double nowS) {
    m_lastRecompute = nowS;
    if (m_count < 8) return;
    m_p95FrameMs = 1000.0 * p95(m_frameDts, m_count, m_scratch);
    m_workerFrameMs = 1000.0 * p95(m_frameWorks, m_count, m_scratch);
    double targetMs = m_p95FrameMs + m_workerFrameMs + 1000.0 * m_dsp / m_rate + 5.0;
    targetMs = std::max(10.0, std::min(250.0, targetMs));
    const int32_t t = static_cast<int32_t>(std::lround(targetMs * m_rate / 1000.0));
    if (t > m_target) {  // raise at once
        m_target = t;
        m_lowerVotes = 0;
    } else if (t < m_target * 0.9) {  // lower only when two periods in a row agree
        if (++m_lowerVotes >= 2) {
            m_target = t;
            m_lowerVotes = 0;
        }
    } else {
        m_lowerVotes = 0;
    }
}

double FillController::onPushed(size_t fillFrames) {
    if (!m_trim || m_target <= 0) {
        m_eps = 0.0;
        return 0.0;
    }
    const double err = (static_cast<double>(fillFrames) - m_target) / m_target;
    const double raw = std::max(-0.005, std::min(0.005, 0.01 * err));
    m_eps += 0.05 * (raw - m_eps);
    return m_eps;
}

// ---------------------------------------------------------------------------------------------------------------
// AudioTapBuffer

void AudioTapBuffer::append(const float *x, size_t n) {
    if (!active || data.size() >= want) return;
    const size_t m = std::min(n, want - data.size());
    data.insert(data.end(), x, x + m);
}

// ---------------------------------------------------------------------------------------------------------------
// AudioRenderer (worker side)

void AudioRenderer::init(Synthesizer *s, int sampleRate, const std::vector<std::vector<float>> &irs) {
    m_s = s;
    m_rate = sampleRate;
    m_irs = irs;
    m_fir.assign(irs.size(), Fir());
    for (size_t c = 0; c < irs.size(); ++c) m_fir[c].init(irs[c].data(), static_cast<int>(irs[c].size()));
    m_block.reserve(static_cast<size_t>(sampleRate));
}

void AudioRenderer::setFirCap(int maxTaps) {
    for (size_t c = 0; c < m_irs.size(); ++c) {
        const int n = std::min(static_cast<int>(m_irs[c].size()), maxTaps > 0 ? maxTaps : 10000);
        m_fir[c].init(m_irs[c].data(), n);
    }
}

int AudioRenderer::firTaps(int channel) const {
    return channel >= 0 && channel < static_cast<int>(m_fir.size()) ? m_fir[channel].taps() : 0;
}

size_t AudioRenderer::prepare() {
    Synthesizer &s = *m_s;
    if (s.m_inputChannelCount <= 0) return 0;
    const size_t n = std::min(s.m_inputChannels[0].data.size(), static_cast<size_t>(s.m_inputBufferSize));
    for (int c = 0; c < s.m_inputChannelCount; ++c) s.m_inputChannels[c].data.read(n, s.m_inputChannels[c].transferBuffer);
    s.m_inputSamplesRead = static_cast<int>(n);  // endFrame() -> endInputBlock() removes them; latency stays 0
    for (int c = 0; c < s.m_inputChannelCount; ++c) {
        s.m_filters[c].airNoiseLowPass.setCutoffFrequency(s.m_audioParameters.airNoiseFrequencyCutoff, s.m_audioSampleRate);
        s.m_filters[c].jitterFilter.setJitterScale(s.m_audioParameters.inputSampleNoise);
    }
    return n;
}

void AudioRenderer::discardInput() {
    Synthesizer &s = *m_s;
    if (s.m_inputChannelCount > 0) s.m_inputSamplesRead = static_cast<int>(s.m_inputChannels[0].data.size());
}

float AudioRenderer::renderSample(int i) {
    Synthesizer &s = *m_s;
    const float airNoise = s.m_audioParameters.airNoise;
    const float dF_F_mix = s.m_audioParameters.dF_F_mix;
    const float convAmount = s.m_audioParameters.convolution;

    float signal = 0;
    for (int c = 0; c < s.m_inputChannelCount; ++c) {
        Synthesizer::ProcessingFilters &f = s.m_filters[c];
        const float jittered = f.jitterFilter.fast_f(s.m_inputChannels[c].transferBuffer[i]);
        const float fIn = jittered;
        const float fDc = f.inputDcFilter.fast_f(fIn);
        const float x = fIn - fDc;
        const float fP = f.derivative.f(fIn);

        // xorshift64* noise in [-1, 1) (upstream uses rand(); the statistics are the same)
        m_rng ^= m_rng >> 12;
        m_rng ^= m_rng << 25;
        m_rng ^= m_rng >> 27;
        const float noise = static_cast<float>((m_rng * 0x2545F4914F6CDD1Dull) >> 40) * (2.0f / 16777216.0f) - 1.0f;
        const float r = s.m_filters[0].airNoiseLowPass.fast_f(noise);  // upstream uses filter 0 for every channel
        const float rMixed = airNoise * r + (1 - airNoise);

        float vIn = fP * dF_F_mix + x * rMixed * (1 - dF_F_mix);
        if (std::fpclassify(vIn) == FP_SUBNORMAL) vIn = 0;
        const float v = convAmount * m_fir[c].process(vIn) + (1 - convAmount) * vIn;
        signal += v;
    }

    signal = s.m_antialiasing.fast_f(signal);
    s.m_levelingFilter.p_target = s.m_audioParameters.levelerTarget;
    float leveled = s.m_levelingFilter.f(signal) * s.m_audioParameters.volume;
    if (leveled > 32767.0f) {
        leveled = 32767.0f;
        ++m_clipped;
    } else if (leveled < -32768.0f) {
        leveled = -32768.0f;
        ++m_clipped;
    }
    return leveled * (1.0f / 32768.0f);
}

size_t AudioRenderer::renderSync(AudioOutput &out, AudioTapBuffer *tap) {
    const size_t n = prepare();
    if (n == 0) return 0;
    m_block.resize(n);
    for (size_t i = 0; i < n; ++i) m_block[i] = renderSample(static_cast<int>(i));
    const size_t pushed = out.ring.push(m_block.data(), n);
    m_dropped += static_cast<int64_t>(n - pushed);
    m_produced += static_cast<int64_t>(n);
    if (tap != nullptr) tap->append(m_block.data(), n);
    return n;
}

size_t AudioRenderer::renderInto(std::vector<float> &dst) {
    const size_t n = prepare();
    for (size_t i = 0; i < n; ++i) dst.push_back(renderSample(static_cast<int>(i)));
    m_produced += static_cast<int64_t>(n);
    return n;
}

} // namespace esp
