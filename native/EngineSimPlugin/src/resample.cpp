// resample.cpp: see resample.h.
#include "resample.h"

#include <cmath>

namespace esp {
namespace {

constexpr double kPi = 3.14159265358979323846;

// Zeroth-order modified Bessel function of the first kind (series), for the Kaiser window.
double besselI0(double x) {
    double sum = 1.0, term = 1.0;
    const double q = x * x / 4.0;
    for (int k = 1; k < 64; ++k) {
        term *= q / (static_cast<double>(k) * k);
        sum += term;
        if (term < 1e-17 * sum) break;
    }
    return sum;
}

} // namespace

std::vector<float> resampleSinc(const float *in, size_t n, double inRate, double outRate) {
    std::vector<float> out;
    if (n == 0 || !(inRate > 0) || !(outRate > 0)) return out;
    if (inRate == outRate) return std::vector<float>(in, in + n);

    const double ratio = outRate / inRate;                     // output samples per input sample
    const double cutoff = 0.95 * (ratio < 1.0 ? ratio : 1.0);  // relative to the input Nyquist frequency
    const int zeros = 32;
    const double halfWidth = zeros / cutoff;                   // in input samples
    const double beta = 8.6;                                   // about 90 dB stop-band

    // Kaiser window tabulated over |r| in [0, 1], linear interpolation.
    const int kTable = 4096;
    std::vector<double> window(kTable + 2);
    const double i0Beta = besselI0(beta);
    for (int i = 0; i <= kTable + 1; ++i) {
        const double r = static_cast<double>(i) / kTable;
        window[i] = r >= 1.0 ? 0.0 : besselI0(beta * std::sqrt(1.0 - r * r)) / i0Beta;
    }

    const size_t m = static_cast<size_t>(std::llround(static_cast<double>(n) * ratio));
    out.resize(m);
    for (size_t j = 0; j < m; ++j) {
        const double t = static_cast<double>(j) / ratio;  // position in input samples
        const long k0 = static_cast<long>(std::ceil(t - halfWidth));
        const long k1 = static_cast<long>(std::floor(t + halfWidth));
        double acc = 0.0;
        for (long k = k0 < 0 ? 0 : k0; k <= k1 && k < static_cast<long>(n); ++k) {
            const double d = t - static_cast<double>(k);
            const double r = std::fabs(d) / halfWidth;
            if (r >= 1.0) continue;
            const double p = r * kTable;
            const int i = static_cast<int>(p);
            const double w = window[i] + (window[i + 1] - window[i]) * (p - i);
            const double x = kPi * cutoff * d;
            const double s = (std::fabs(x) < 1e-12) ? 1.0 : std::sin(x) / x;
            acc += in[k] * cutoff * s * w;
        }
        out[j] = static_cast<float>(acc);
    }
    return out;
}

} // namespace esp
