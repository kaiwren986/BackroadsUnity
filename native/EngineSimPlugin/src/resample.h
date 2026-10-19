// resample.h: band-limited resampling of impulse responses, done once at load (44.1 kHz IRs -> the output rate,
// usually 48 kHz; PLAN 3.1, 3.6; research 01 4.2).
#pragma once

#include <cstddef>
#include <vector>

namespace esp {

// Kaiser-windowed sinc, 32 zero crossings per side, cutoff 0.95 x the lower Nyquist frequency. The output keeps the
// input's duration: n * outRate / inRate samples (rounded).
std::vector<float> resampleSinc(const float *in, size_t n, double inRate, double outRate);

} // namespace esp
