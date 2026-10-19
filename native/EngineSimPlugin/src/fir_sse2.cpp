// fir_sse2.cpp: SSE2 fallback and the scalar reference for Fir, plus the CPUID dispatch.
#include "fir.h"

#include <emmintrin.h>
#include <intrin.h>
#include <immintrin.h>

#include <algorithm>
#include <cstdlib>
#include <cstring>

namespace esp {

float firDotSse2(const float *h, const float *x, int n) {
    __m128 a0 = _mm_setzero_ps(), a1 = _mm_setzero_ps(), a2 = _mm_setzero_ps(), a3 = _mm_setzero_ps();
    for (int i = 0; i < n; i += 16) {
        a0 = _mm_add_ps(a0, _mm_mul_ps(_mm_loadu_ps(h + i), _mm_loadu_ps(x + i)));
        a1 = _mm_add_ps(a1, _mm_mul_ps(_mm_loadu_ps(h + i + 4), _mm_loadu_ps(x + i + 4)));
        a2 = _mm_add_ps(a2, _mm_mul_ps(_mm_loadu_ps(h + i + 8), _mm_loadu_ps(x + i + 8)));
        a3 = _mm_add_ps(a3, _mm_mul_ps(_mm_loadu_ps(h + i + 12), _mm_loadu_ps(x + i + 12)));
    }
    __m128 s = _mm_add_ps(_mm_add_ps(a0, a1), _mm_add_ps(a2, a3));
    s = _mm_add_ps(s, _mm_movehl_ps(s, s));
    s = _mm_add_ss(s, _mm_shuffle_ps(s, s, 0x55));
    return _mm_cvtss_f32(s);
}

float firDotScalar(const float *h, const float *x, int n) {
    float r = 0.0f;
    for (int i = 0; i < n; ++i) r += h[i] * x[i];
    return r;
}

bool cpuHasAvx2Fma() {
    int r[4];
    __cpuid(r, 0);
    if (r[0] < 7) return false;
    __cpuid(r, 1);
    const bool fma = (r[2] & (1 << 12)) != 0;
    const bool osxsave = (r[2] & (1 << 27)) != 0;
    const bool avx = (r[2] & (1 << 28)) != 0;
    if (!fma || !osxsave || !avx) return false;
    if ((_xgetbv(0) & 0x6) != 0x6) return false;  // the OS saves XMM and YMM state
    __cpuidex(r, 7, 0);
    return (r[1] & (1 << 5)) != 0;
}

namespace {

struct Dispatch {
    FirDotFn fn;
    const char *name;
};

Dispatch pick() {
    char force[16] = {};
    size_t len = 0;
    if (getenv_s(&len, force, sizeof force, "ESP_FIR") == 0 && len > 0) {
        if (std::strcmp(force, "sse2") == 0) return {firDotSse2, "sse2"};
        if (std::strcmp(force, "scalar") == 0) return {firDotScalar, "scalar"};
    }
    if (cpuHasAvx2Fma()) return {firDotAvx2, "avx2"};
    return {firDotSse2, "sse2"};
}

const Dispatch &dispatch() {
    static const Dispatch d = pick();
    return d;
}

} // namespace

FirDotFn firDot() { return dispatch().fn; }
const char *firKernelName() { return dispatch().name; }

void Fir::init(const float *taps, int n, FirDotFn dot) {
    m_taps = n > 0 ? n : 0;
    m_n = (m_taps + 31) / 32 * 32;
    m_h.assign(static_cast<size_t>(m_n), 0.0f);
    if (m_taps > 0) std::memcpy(m_h.data(), taps, sizeof(float) * static_cast<size_t>(m_taps));
    m_hist.assign(static_cast<size_t>(2 * m_n), 0.0f);
    m_pos = 0;
    m_dot = dot != nullptr ? dot : firDot();
}

void Fir::clear() {
    std::fill(m_hist.begin(), m_hist.end(), 0.0f);
    m_pos = 0;
}

} // namespace esp
