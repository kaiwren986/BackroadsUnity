// builder_util.h: helpers shared by the C++ engine builders. The math mirrors the piranha nodes in
// vendor\engine-sim\scripting\include (FunctionNode, GenerateHarmonicCamLobeNode) and es\utilities\utilities.mr.
#pragma once

#include "constants.h"
#include "function.h"
#include "gas_system.h"
#include "units.h"

#include <cmath>
#include <utility>
#include <vector>

namespace esp {
namespace build {

using Samples = std::vector<std::pair<double, double>>;

// es_script::FunctionNode::generate()
inline Function *makeFunction(double filterRadius, const Samples &samples) {
    Function *f = new Function;
    f->initialize(static_cast<int>(samples.size()), filterRadius);
    for (const auto &s : samples) f->addSample(s.first, s.second);
    return f;
}

// es/part-library/parts/heads.mr: add_flow_sample(lift, flow) = add_sample(lift * thou, k_28inH2O(flow))
inline Function *makeFlowFunction(const Samples &liftThouToCfm, double filterRadius = 50 * units::thou) {
    Samples s;
    for (const auto &p : liftThouToCfm) s.push_back({p.first * units::thou, GasSystem::k_28inH2O(p.second)});
    return makeFunction(filterRadius, s);
}

// es_script::GenerateHarmonicCamLobeNode::_evaluate() (scripting/include/actions.h)
inline Function *harmonicCamLobe(double durationAt50Thou, double gamma, double lift, int steps) {
    const double angle = durationAt50Thou / 4;
    const double s = std::pow(2 * units::distance(50, units::thou) / lift, 1 / gamma) - 1;
    const double k = std::acos(s) / angle;
    const double extents = constants::pi / k;
    const double step = extents / (steps - 5.0);
    Samples samples;
    for (int i = 0; i < steps; ++i) {
        if (i == 0) {
            samples.push_back({0.0, lift});
        } else {
            const double x = i * step;
            const double l = (x >= extents) ? 0.0 : lift * std::pow(0.5 + 0.5 * std::cos(k * x), gamma);
            samples.push_back({x, l});
            samples.push_back({-x, l});
        }
    }
    return makeFunction(step, samples);
}

inline double diskMoment(double mass, double radius) { return 0.5 * mass * radius * radius; }  // utilities.mr
inline double rodMoment(double mass, double length) { return (1 / 12.0) * mass * length * length; }

} // namespace build
} // namespace esp
