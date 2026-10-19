// fir_avx2.cpp: AVX2 + FMA dot product for Fir. This file alone is compiled with /arch:AVX2 (PLAN 1.6); Fir only
// calls it after cpuHasAvx2Fma() said yes. Four independent accumulators over 32 taps per iteration (01 App. A.5).
#include "fir.h"

#include <immintrin.h>

namespace esp {

float firDotAvx2(const float *h, const float *x, int n) {
    __m256 a0 = _mm256_setzero_ps(), a1 = _mm256_setzero_ps(), a2 = _mm256_setzero_ps(), a3 = _mm256_setzero_ps();
    for (int i = 0; i < n; i += 32) {
        a0 = _mm256_fmadd_ps(_mm256_loadu_ps(h + i), _mm256_loadu_ps(x + i), a0);
        a1 = _mm256_fmadd_ps(_mm256_loadu_ps(h + i + 8), _mm256_loadu_ps(x + i + 8), a1);
        a2 = _mm256_fmadd_ps(_mm256_loadu_ps(h + i + 16), _mm256_loadu_ps(x + i + 16), a2);
        a3 = _mm256_fmadd_ps(_mm256_loadu_ps(h + i + 24), _mm256_loadu_ps(x + i + 24), a3);
    }
    const __m256 s = _mm256_add_ps(_mm256_add_ps(a0, a1), _mm256_add_ps(a2, a3));
    __m128 lo = _mm256_castps256_ps128(s);
    const __m128 hi = _mm256_extractf128_ps(s, 1);
    lo = _mm_add_ps(lo, hi);
    lo = _mm_add_ps(lo, _mm_movehl_ps(lo, lo));
    lo = _mm_add_ss(lo, _mm_shuffle_ps(lo, lo, 0x55));
    return _mm_cvtss_f32(lo);
}

} // namespace esp
