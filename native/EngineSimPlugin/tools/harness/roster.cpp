// roster.cpp: M2 engine gates (PLAN 3.10, 6.1 M2; card T2.4).
//
//   esp_bigblock  v8_bigblock_a, the C++ port of upstream's gas big-block truck script, against the script itself
//                 (both through this DLL): WOT dyno 1,000-5,500 rpm within 5 % at every point, and it cranks on its
//                 starter and idles on its own. The script path carries a real name, so it stays in this internal tool.
//   esp_cli roster  every builtin roster engine: cranks, idles (mean over 2 s), WOT dyno peak, as a report table (the
//                 numbers behind EngineCatalog's idle and peak fields).
#include "harness.h"

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <thread>

namespace esph {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kHpW = 745.699872;

struct Sweep {
    std::vector<double> rpm, torque, power;
    std::string err;
    double wallS = 0;
};

Sweep dynoSweep(const std::string &name, const std::string &top, double from, double to, int points) {
    Sweep s;
    Engine eng;
    CreateOpts o;
    o.audioRate = 0;
    if (!eng.open(name, o, s.err, top)) return s;
    s.rpm.resize(points);
    s.torque.resize(points);
    s.power.resize(points);
    for (int i = 0; i < points; ++i) s.rpm[i] = points == 1 ? from : from + (to - from) * i / (points - 1);
    const double t0 = nowS();
    const int32_t r = esp_dyno_sweep(eng.h(), from, to, points, 1.0f, 2.0, s.torque.data(), s.power.data());
    s.wallS = nowS() - t0;
    if (r != ESP_OK) s.err = fmt("esp_dyno_sweep %d", r);
    return s;
}

struct StartResult {
    double releaseS = -1, idleMean = 0, idleMin = 0, idleMax = 0;
    bool started = false;
    std::string err;
};

// Like a driver: hold the starter until the engine runs on its own (rpm > 1.3 x starter speed for 0.25 s, at most
// 5 s), release, then the idle is the mean over the 2 s that start 1.5 s after the release.
StartResult crankAndIdle(const std::string &name, const std::string &top) {
    StartResult r;
    Engine eng;
    CreateOpts o;
    o.audioRate = 48000;
    o.flags = ESP_CREATE_NO_COAST;  // lockstep driver (see runFollowGate)
    if (!eng.open(name, o, r.err, top)) return r;
    Driver d{eng.h()};
    const double starterRpm = std::max(150.0, eng.info().starter_speed_rad_s * 60.0 / (2 * kPi));
    EspDrivelineIn free = driveline(ESP_MODE_FREE);
    EspControls c = controls(0.0f, true, true);
    esp_set_controls(eng.h(), &c);
    double above = 0;
    while (d.t < 5.0) {
        if (!d.step(free)) return r;
        above = d.last.rpm > 1.3 * starterRpm ? above + d.dt : 0;
        if (above >= 0.25) {
            r.releaseS = d.t;
            break;
        }
    }
    c = controls(0.0f, true, false);
    esp_set_controls(eng.h(), &c);
    const double from = (r.releaseS > 0 ? r.releaseS : d.t) + 1.5;
    double sum = 0, mn = 1e30, mx = 0;
    int n = 0;
    while (d.t < from + 2.0) {
        if (!d.step(free)) break;
        if (d.t >= from) {
            sum += d.last.rpm;
            mn = std::min(mn, d.last.rpm);
            mx = std::max(mx, d.last.rpm);
            ++n;
        }
    }
    r.idleMean = n > 0 ? sum / n : 0;
    r.idleMin = n > 0 ? mn : 0;
    r.idleMax = mx;
    r.started = r.releaseS > 0 && r.idleMin > 0.5 * starterRpm;
    return r;
}

} // namespace

int runBigBlockGate(const std::string &outJson) {
    const std::string script = enginesDir() + "/chevrolet/chev_truck_454.mr";
    const std::string top = "chev_truck_454";
    const int points = 10;
    Sweep port, ref;
    std::thread tRef([&] { ref = dynoSweep(script, top, 1000, 5500, points); });
    port = dynoSweep("v8_bigblock_a", "", 1000, 5500, points);
    tRef.join();
    if (!port.err.empty() || !ref.err.empty()) {
        std::printf("FAIL bigblock: port %s; script %s\n", port.err.c_str(), ref.err.c_str());
        drainLog();
        return 1;
    }
    StartResult sp = crankAndIdle("v8_bigblock_a", "");
    StartResult ss = crankAndIdle(script, top);

    double maxRel = 0;
    int peak = 0, peakRef = 0;
    std::printf("bigblock: v8_bigblock_a (port) vs the upstream script, WOT, 2 s settle + 1 s average per point\n");
    std::printf("  %6s %10s %10s %7s %8s %8s\n", "rpm", "port N m", "script N m", "diff %", "port hp", "scr hp");
    for (int i = 0; i < points; ++i) {
        const double rel = std::fabs(port.torque[i] - ref.torque[i]) / std::max(50.0, std::fabs(ref.torque[i]));
        maxRel = std::max(maxRel, rel);
        if (port.torque[i] > port.torque[peak]) peak = i;
        if (ref.torque[i] > ref.torque[peakRef]) peakRef = i;
        std::printf("  %6.0f %10.1f %10.1f %7.2f %8.1f %8.1f\n", port.rpm[i], port.torque[i], ref.torque[i], 100 * rel,
                    port.power[i] / kHpW, ref.power[i] / kHpW);
    }
    int peakP = 0;
    for (int i = 0; i < points; ++i)
        if (port.power[i] > port.power[peakP]) peakP = i;
    const bool curveOk = maxRel <= 0.05;
    const bool startOk = sp.started && sp.releaseS <= 4.0 && sp.idleMean >= 450 && sp.idleMean <= 1100;
    const bool pass = curveOk && startOk;
    std::printf("  port: peak %.1f N m at %.0f rpm, %.1f hp at %.0f rpm; script: peak %.1f N m at %.0f rpm\n",
                port.torque[peak], port.rpm[peak], port.power[peakP] / kHpW, port.rpm[peakP], ref.torque[peakRef],
                ref.rpm[peakRef]);
    std::printf("  start: port released at %.2f s, idle %.0f rpm (%.0f-%.0f); script released at %.2f s, idle %.0f rpm "
                "(%.0f-%.0f)\n",
                sp.releaseS, sp.idleMean, sp.idleMin, sp.idleMax, ss.releaseS, ss.idleMean, ss.idleMin, ss.idleMax);
    std::printf("  max torque difference %.2f %% -> %s (gate: <= 5 %% at every point; port starts by 4 s and idles "
                "450-1100 rpm)\n",
                100 * maxRel, pass ? "PASS" : "FAIL");
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_bigblock").str("engine", "v8_bigblock_a").num("maxTorqueDiffPct", 100 * maxRel, 3)
            .num("peakTorqueNm", port.torque[peak], 2).num("peakTorqueRpm", port.rpm[peak], 0)
            .num("peakPowerHp", port.power[peakP] / kHpW, 2).num("peakPowerRpm", port.rpm[peakP], 0)
            .num("scriptPeakTorqueNm", ref.torque[peakRef], 2).num("starterReleaseS", sp.releaseS, 3)
            .num("idleMeanRpm", sp.idleMean, 1).num("idleMinRpm", sp.idleMin, 1).num("idleMaxRpm", sp.idleMax, 1)
            .num("scriptIdleMeanRpm", ss.idleMean, 1).arr("points");
        for (int i = 0; i < points; ++i)
            j.obj().num("rpm", port.rpm[i], 0).num("torqueNm", port.torque[i], 2).num("scriptTorqueNm", ref.torque[i], 2)
                .num("hp", port.power[i] / kHpW, 2).end();
        j.endArr().boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

int runRosterReport(const std::string &outJson) {
    const char *names[] = {"v8_bigblock_a", "v8_cross_a", "f4_boxer_a", "i4_synth"};
    Json j;
    j.obj().str("gate", "esp_roster").arr("engines");
    std::printf("roster: %-14s %6s %8s %10s %12s %12s %10s\n", "engine", "freq", "release", "idle rpm", "peak N m",
                "peak hp", "redline");
    for (const char *name : names) {
        Engine eng;
        std::string err;
        CreateOpts o;
        o.audioRate = 0;
        if (!eng.open(name, o, err)) {
            std::printf("FAIL %s: %s\n", name, err.c_str());
            return 1;
        }
        const double redline = eng.info().redline_rad_s * 60 / (2 * kPi);
        const int freq = eng.info().sim_frequency;
        eng.close();
        const StartResult s = crankAndIdle(name, "");
        const int points = 12;
        const Sweep w = dynoSweep(name, "", 1000, std::floor(redline / 500) * 500, points);
        int pk = 0, pp = 0;
        for (int i = 0; i < points && w.err.empty(); ++i) {
            if (w.torque[i] > w.torque[pk]) pk = i;
            if (w.power[i] > w.power[pp]) pp = i;
        }
        std::printf("roster: %-14s %6d %8.2f %10.0f %6.0f@%-5.0f %6.0f@%-5.0f %10.0f\n", name, freq, s.releaseS, s.idleMean,
                    w.torque.empty() ? 0 : w.torque[pk], w.rpm.empty() ? 0 : w.rpm[pk],
                    w.power.empty() ? 0 : w.power[pp] / kHpW, w.rpm.empty() ? 0 : w.rpm[pp], redline);
        j.obj().str("engine", name).integer("simFrequency", freq).num("starterReleaseS", s.releaseS, 3)
            .num("idleMeanRpm", s.idleMean, 1).num("idleMinRpm", s.idleMin, 1).num("idleMaxRpm", s.idleMax, 1)
            .num("redlineRpm", redline, 0);
        if (w.err.empty()) {
            j.num("peakTorqueNm", w.torque[pk], 2).num("peakTorqueRpm", w.rpm[pk], 0).num("peakPowerHp", w.power[pp] / kHpW, 2)
                .num("peakPowerRpm", w.rpm[pp], 0);
        }
        j.end();
    }
    j.endArr().boolean("pass", true).end();
    if (!outJson.empty()) j.save(outJson);
    drainLog();
    return 0;
}

} // namespace esph
