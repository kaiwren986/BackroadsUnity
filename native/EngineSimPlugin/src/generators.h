// generators.h: force generators that couple engine-sim to Unity without forking (research 01 3.2, 3.6, App. A.4).
//
// SlavedCrankPD (SLAVED_CRANK, option D): a PD on the output crank that makes engine-sim follow Unity's crank-speed
//   trajectory. The worker sets `cmd` per engine step, integrates `err += (w - cmd) / f` after each step and reads
//   `lastT`. Unity receives T_ind = J * dw/h - mean(T_pd). On saturation the integral decays by 50 % per step (06 4.4).
// WheelLoad (PROXY/KINEMATIC, options A-C): a load on the driveline proxy body (engine-sim's m_vehicleMass), the
//   driven wheels reflected through the overall ratio G. Exercised by the co-simulation reference in M1, by PROXY in
//   M13. engine-sim spins negative: forward is v_theta < 0 (research 01 3.3).
#pragma once

#include "scs.h"

#include <algorithm>
#include <cmath>

namespace esp {

class SlavedCrankPD : public atg_scs::ForceGenerator {
public:
    atg_scs::RigidBody *crank = nullptr;
    double kp = 0.0, kd = 0.0;
    double cmd = 0.0;       // commanded crank speed, rad/s forward-positive
    double err = 0.0;       // integrated angle error, rad
    double lastT = 0.0;     // PD torque applied in the last solve, forward-positive
    double tMax = 3000.0;
    bool enabled = false;
    bool saturated = false;

    void configure(double j, double fcHz, double torqueLimit) {
        const double wc = 2.0 * 3.14159265358979323846 * fcHz;
        kp = j * wc * wc;
        kd = 1.4 * wc * j;
        tMax = torqueLimit;
    }
    void reset() {
        err = 0.0;
        lastT = 0.0;
        saturated = false;
    }
    // After each engine step: integrate the angle error (h = 1/f).
    void afterStep(double wEngine, double h) {
        err += (wEngine - cmd) * h;
        if (saturated) err *= 0.5;
    }

    void apply(atg_scs::SystemState *s) override {
        if (!enabled || crank == nullptr) {
            lastT = 0.0;
            saturated = false;
            return;
        }
        const int i = crank->index;
        const double w = -s->v_theta[i];
        double t = -kp * err - kd * (w - cmd);
        saturated = std::fabs(t) > tMax;
        t = std::max(-tMax, std::min(tMax, t));
        s->t[i] += -t;  // the engine spins negative
        lastT = t;
    }
};

// Magic-formula-like tyre used by the co-simulation (01 3.5): F = mu Fz sin(C atan(B kappa)).
struct TyreLaw {
    double mu = 1.0, fz = 0.5 * 1614 * 9.81, b = 10.0, c = 1.9, r = 0.254;
    double force(double omegaWheel, double v) const {
        const double vref = std::max(std::fabs(v), 1.0);  // low-speed regularisation
        const double kappa = (omegaWheel * r - v) / vref;
        return mu * fz * std::sin(c * std::atan(b * kappa));
    }
};

class WheelLoad : public atg_scs::ForceGenerator {
public:
    enum Mode { Off = 0, TyreCurve = 1, Linear = 2, Constant = 3 };
    atg_scs::RigidBody *proxy = nullptr;
    int mode = Off;
    double g = 1.0;          // overall ratio (gear * final drive)
    double v = 0.0;          // frozen chassis speed, m/s
    TyreLaw tyre;
    double f0 = 0.0, dF = 0.0, w0 = 0.0, fConst = 0.0;

    double force(double omegaWheel) const {
        switch (mode) {
        case TyreCurve: return tyre.force(omegaWheel, v);
        case Linear: return f0 + dF * (omegaWheel - w0);
        case Constant: return fConst;
        default: return 0.0;
        }
    }
    double wheelSpeed() const { return proxy != nullptr ? -proxy->v_theta / g : 0.0; }

    void apply(atg_scs::SystemState *s) override {
        if (mode == Off || proxy == nullptr) return;
        const int i = proxy->index;
        const double w = -s->v_theta[i] / g;  // forward wheel speed
        s->t[i] += force(w) * tyre.r / g;     // resists forward (negative) rotation
    }
};

} // namespace esp
