// fir.h: direct-form FIR for the exhaust impulse responses (PLAN 3.6, research 01 4.2 and App. A.5).
//
// y[n] = sum_i h[i] * x[n - i], the same sum as engine-sim's ConvolutionFilter::f, computed over a duplicated history
// buffer so the window is contiguous. The dot product is CPUID-dispatched: AVX2+FMA (fir_avx2.cpp, built with
// /arch:AVX2) or SSE2 (fir_sse2.cpp). Taps are zero-padded to a multiple of 32, so the kernels have no tail.
#pragma once

#include <vector>

namespace esp {

using FirDotFn = float (*)(const float *h, const float *x, int n);  // n is a multiple of 32

float firDotAvx2(const float *h, const float *x, int n);
float firDotSse2(const float *h, const float *x, int n);
float firDotScalar(const float *h, const float *x, int n);

bool cpuHasAvx2Fma();
// The kernel used by Fir: AVX2 when the CPU and OS support it, else SSE2. ESP_FIR=sse2|scalar forces one (tests).
FirDotFn firDot();
const char *firKernelName();

class Fir {
public:
    void init(const float *taps, int n, FirDotFn dot = nullptr);
    void clear();  // zero the history
    int taps() const { return m_taps; }
    bool empty() const { return m_n == 0; }

    float process(float x) {
        if (m_n == 0) return 0.0f;
        m_pos = (m_pos == 0) ? m_n - 1 : m_pos - 1;
        m_hist[m_pos] = x;
        m_hist[m_pos + m_n] = x;
        return m_dot(m_h.data(), &m_hist[m_pos], m_n);
    }

private:
    std::vector<float> m_h;     // padded taps
    std::vector<float> m_hist;  // 2 * m_n
    int m_n = 0;                // padded length
    int m_taps = 0;             // real length
    int m_pos = 0;
    FirDotFn m_dot = nullptr;
};

} // namespace esp
