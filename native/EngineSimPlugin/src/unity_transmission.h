// unity_transmission.h: engine-sim's Transmission with the clutch torque readable (research 01 3.2).
//
// The plugin always builds its own UnityTransmission and passes it to loadSimulation(); a script's transmission is
// only read for its gear ratios (EspEngineInfo). In ABI v1 engine-sim's transmission stays in neutral: FREE and
// FOLLOW_RPM need no driveline, and in SLAVED_CRANK Unity owns the clutch (PLAN 3.5).
#pragma once

#include "transmission.h"

namespace esp {

class UnityTransmission : public Transmission {
public:
    double clutchTorque() const { return m_clutchConstraint.F_t[0][0]; }  // on the crank, positive while driving
    // Neutral with clutch limits 0, applied now instead of one step late (research 01 3.3, defect 6).
    void neutral() {
        changeGear(-1);
        setClutchPressure(0.0);
        update(0.0);
    }
};

// Read-only access to a script Transmission's protected ratios, through pointers to members named via a derived class
// (well-formed C++: the pointer has type `T Transmission::*` and applying it re-checks no access).
struct TransmissionPeek : Transmission {
    static int gearCount(const Transmission &t) { return t.*(&TransmissionPeek::m_gearCount); }
    static const double *gearRatios(const Transmission &t) { return t.*(&TransmissionPeek::m_gearRatios); }
    static double maxClutchTorque(const Transmission &t) { return t.*(&TransmissionPeek::m_maxClutchTorque); }
};

} // namespace esp
