// cosim.cpp: esp_cosim_matrix (PLAN 3.4 "Before ABI v1 freezes"): research 01 3.5's co-simulation, re-run with the
// step lag L = 1-4 and with L sequences replayed from 30/45/60/120 fps frame-time traces, through the real ABI
// (SLAVED_CRANK, step-indexed results, the dT_dw feed-forward). Reports dv, tracking RMS and limiter overshoot.
//
// The reference is 01's monolithic run: everything at 10 kHz, the chassis integrated every engine step, the plugin
// owning crank + clutch + driven wheels (WheelLoad on engine-sim's proxy body). It needs the plugin's internals, so
// this file links the esp_internal library as well as the DLL.
#include "harness.h"

// internals for the reference run
#include "builders/builders.h"
#include "generators.h"
#include "unity_sim.h"
#include "unity_transmission.h"

#include "engine.h"
#include "units.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <mutex>
#include <thread>

namespace esph {

namespace {

constexpr double kMass = 1614.0;
const double kRatios[4] = {2.97, 2.07, 1.43, 1.00};
constexpr double kFinal = 3.42;
constexpr double kJw = 2.0, kJgear = 0.02;

double roadDrag(double v) { return 200.0 + 0.5 * 1.184 * 0.3 * 2.32 * v * v; }

struct Scenario {
    int kind;  // 0: 2nd-gear WOT launch from 8 m/s; 1: 4th-gear 30 % cruise from 20 m/s
    double mu;
    const char *name;
};

const Scenario kScenarios[3] = {{0, 1.0, "launch-mu1.0"}, {0, 0.35, "launch-mu0.35"}, {1, 1.0, "cruise"}};

struct Trace {
    std::vector<double> t, rpm, v;
};

void schedule(double t, int kind, double &thr, double &clutch) {
    thr = 0.0;
    clutch = 0.0;
    if (t >= 2.0 && t < 2.5) {
        thr = 0.25;
        clutch = (t - 2.0) / 0.5;
    } else if (t >= 2.5) {
        thr = kind == 0 ? 1.0 : 0.3;
        clutch = 1.0;
    }
}

// 01 3.5 variant 0: the monolithic reference.
Trace runReference(const Scenario &sc, std::string &err) {
    Trace tr;
    esp::EngineBundle b;
    if (!esp::buildV8CrossA(esDir(), b)) {
        err = "build v8_cross_a";
        return tr;
    }
    auto *trans = new esp::UnityTransmission;
    Transmission::Parameters tp;
    tp.GearCount = 4;
    tp.GearRatios = kRatios;
    tp.MaxClutchTorque = units::torque(500, units::ft_lb);
    trans->initialize(tp);
    auto *sim = new esp::UnitySim;
    Simulator::Parameters sp;
    sim->initialize(sp);
    sim->loadSimulation(b.engine, b.vehicle, trans);
    sim->setFluidSimulationSteps(8);
    sim->setSimulationFrequency(10000);
    sim->attach(false);
    sim->disableSynthesizer();

    const int gear = sc.kind == 0 ? 1 : 3;
    const double g = kRatios[gear] * kFinal;
    esp::WheelLoad &load = sim->wheelLoad();
    load.mode = esp::WheelLoad::TyreCurve;
    load.g = g;
    load.tyre.mu = sc.mu;
    double v = sc.kind == 0 ? 8.0 : 20.0;
    const double ww = v / load.tyre.r;
    trans->changeGear(gear);
    trans->setClutchPressure(0.0);
    trans->update(0.0);
    sim->proxy().I = kJgear + kJw / (g * g);
    sim->proxy().v_theta = -ww * g;
    b.engine->getIgnitionModule()->m_enabled = true;
    sim->m_starterMotor.m_enabled = true;
    const double h = 0.02, hE = 1.0 / 10000.0;
    double nextLog = 0;
    for (double t = 0; t < 6.5 - 1e-9; t += h) {
        if (t >= 1.2) sim->m_starterMotor.m_enabled = false;
        double thr, clutch;
        schedule(t, sc.kind, thr, clutch);
        b.engine->setSpeedControl(thr);
        trans->setClutchPressure(clutch);
        load.v = v;
        sim->startFrame(h);
        while (sim->simulateStep()) {
            const double f = load.force(load.wheelSpeed());
            v += (f - roadDrag(v)) / kMass * hE;
            load.v = v;
        }
        sim->endFrame();
        if (t + 1e-9 >= nextLog) {
            tr.t.push_back(t);
            tr.rpm.push_back(b.engine->getRpm());
            tr.v.push_back(v);
            nextLog += 0.02;
        }
    }
    sim->teardown();
    delete sim;
    esp::destroyBundle(b);
    delete trans;
    return tr;
}

struct CaseSpec {
    std::string name;
    int lag = 2;           // fixed L; 0 = from the frame trace
    bool feedForward = true;
    std::vector<int> stepsPerFrame;  // from a trace (lag = 0)
    bool backstop = true;  // Unity-side fuel cut at the rev limit + 150 rpm (PLAN 3.5 Limiter)
};

struct CaseResult {
    std::string scenario, name;
    double dv = 0, meanAbsDrpm = 0, trackRms = 0, limiterOver = 0, unityOver = 0, ripple = 0, rippleMax = 0;
    double slopeMean = 0, slopeMin = 1e9, slopeMax = -1e9;
    int slopeN = 0;
    double vEnd = 0, rpmEnd = 0;
    int staleSteps = 0;
    bool finite = true, pass = false;
    std::string why;
};

// Unity side of option D (01 3.5 runSlaved) with result lag L in steps, through the ABI.
CaseResult runSlavedCase(const Scenario &sc, const CaseSpec &cs, double jCal, const Trace &ref) {
    CaseResult r;
    r.scenario = sc.name;
    r.name = cs.name;
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 0;
    o.workerThread = 1;
    o.fluidSteps = 8;
    if (!eng.open("v8_cross_a", o, err)) {
        r.finite = false;
        r.why = err;
        return r;
    }
    EspEngine *e = eng.h();
    const double revLimitRpm = eng.info().rev_limit_rad_s * 60.0 / (2 * 3.14159265358979323846);
    const int gear = sc.kind == 0 ? 1 : 3;
    const double g = kRatios[gear] * kFinal;
    const double jw = kJw + kJgear * g * g;
    const double jc = jCal;
    const double tCap = units::torque(500, units::ft_lb);
    esp::TyreLaw ty;
    ty.mu = sc.mu;
    double v = sc.kind == 0 ? 8.0 : 20.0, ww = v / ty.r, wc = 0;
    const double h = 1.0 / 120.0;
    const int sub = 4;
    const int64_t total = std::llround(6.5 / h);
    std::vector<double> tInd(static_cast<size_t>(total), 0.0), slope(static_cast<size_t>(total), 0.0),
        wcEnd(static_cast<size_t>(total), 0.0);
    std::vector<bool> have(static_cast<size_t>(total), false);
    std::vector<double> usedT(static_cast<size_t>(total), 0.0);
    bool slaved = false;
    int64_t slavedFrom = -1;
    double nextLog = 0, sq = 0;
    int nTrack = 0;
    Trace mine;
    // frame bookkeeping for trace-driven L
    size_t frame = 0;
    int leftInFrame = 0, lagNow = cs.lag > 0 ? cs.lag : 2;
    int64_t lastUsed = -1;
    std::vector<double> rpmAfter3;
    for (int64_t k = 0; k < total; ++k) {
        const double t = k * h;
        if (cs.lag == 0) {
            while (leftInFrame == 0 && frame < cs.stepsPerFrame.size()) {
                leftInFrame = cs.stepsPerFrame[frame++];
                lagNow = std::max(2, std::min(4, leftInFrame));  // L = clamp(S, 2, 4), set at the frame boundary
            }
            if (leftInFrame > 0) --leftInFrame;
        }
        double thr, clutch;
        schedule(t, sc.kind, thr, clutch);
        EspControls c = controls(static_cast<float>(thr), true, t < 1.2);
        esp_set_controls(e, &c);
        if (!slaved && t >= 1.5) {
            slaved = true;
            slavedFrom = k;
        }

        // 1) collect T_ind(k - L) (Unity: start of the fixed step, bounded wait; here: always wait)
        double tUse = 0.0;
        if (slaved && k > slavedFrom) {
            int64_t j = k - lagNow;
            if (j < lastUsed) j = lastUsed;  // L grew: use the last torque again
            if (j >= slavedFrom && j >= 0) {
                if (!have[static_cast<size_t>(j)]) {
                    EspStepOut out = stepOut();
                    if (waitStep(e, j, out)) {
                        tInd[static_cast<size_t>(j)] = out.indicated_torque_nm;
                        slope[static_cast<size_t>(j)] = out.dT_dw;
                        r.slopeMean += out.dT_dw;
                        r.slopeMin = std::min(r.slopeMin, out.dT_dw);
                        r.slopeMax = std::max(r.slopeMax, out.dT_dw);
                        ++r.slopeN;
                        have[static_cast<size_t>(j)] = true;
                        // tracking: engine-sim's crank vs the trajectory it was given
                        const double ew = out.crank_speed - wcEnd[static_cast<size_t>(j)];
                        sq += ew * ew;
                        ++nTrack;
                        if (out.rpm - revLimitRpm > r.limiterOver) r.limiterOver = out.rpm - revLimitRpm;
                        if (!std::isfinite(out.rpm)) r.finite = false;
                    } else {
                        ++r.staleSteps;
                    }
                }
                tUse = tInd[static_cast<size_t>(j)];
                if (cs.feedForward) {
                    const double corr = slope[static_cast<size_t>(j)] * (wc - wcEnd[static_cast<size_t>(j)]);
                    tUse += std::max(-200.0, std::min(200.0, corr));
                }
                tUse = std::max(-4 * 720.0, std::min(4 * 720.0, tUse));
                if (cs.backstop && wc * 9.5492966 > revLimitRpm + 150.0) tUse = std::min(tUse, 0.0);  // fuel cut
                lastUsed = j;
            }
        }

        // 2) Unity physics step k (01 runSlaved, 4 substeps, implicit linearised tyre, Coulomb clutch)
        double wcs[sub + 1];
        const double hs = h / sub;
        if (!slaved) {
            for (int s = 0; s < sub; ++s) {
                const double f0 = ty.force(ww, v), eps = 1e-3;
                const double cc = std::max(0.0, (ty.force(ww + eps, v) - ty.force(ww - eps, v)) / (2 * eps));
                const double jwe = jw + hs * cc * ty.r;
                const double ww0 = ww;
                ww += hs * (-f0 * ty.r) / jwe;
                v += hs * ((f0 + cc * (ww - ww0)) - roadDrag(v)) / kMass;
            }
        } else {
            if (k == slavedFrom) {
                EspStepOut out = stepOut();
                if (waitStep(e, k - 1, out)) wc = out.crank_speed;
            }
            wcs[0] = wc;
            for (int s = 0; s < sub; ++s) {
                const double wcStar = wc + hs * tUse / jc;
                const double f0 = ty.force(ww, v), eps = 1e-3;
                const double cc = std::max(0.0, (ty.force(ww + eps, v) - ty.force(ww - eps, v)) / (2 * eps));
                const double jwe = jw + hs * cc * ty.r;
                const double ww0 = ww;
                const double wwStar = ww + hs * (-f0 * ty.r) / jwe;
                const double cap = tCap * clutch * hs;
                double p = 0;
                if (cap > 0) {
                    p = (wcStar - g * wwStar) / (1 / jc + g * g / jwe);
                    p = std::max(-cap, std::min(cap, p));
                }
                wc = wcStar - p / jc;
                ww = wwStar + g * p / jwe;
                v += hs * ((f0 + cc * (ww - ww0)) - roadDrag(v)) / kMass;
                wcs[s + 1] = wc;
            }
            wcEnd[static_cast<size_t>(k)] = wc;
            usedT[static_cast<size_t>(k)] = tUse;
            if (wc * 60 / (2 * 3.14159265358979323846) - revLimitRpm > r.unityOver)
                r.unityOver = wc * 60 / (2 * 3.14159265358979323846) - revLimitRpm;
        }

        // 3) kick step k: engine-sim follows this step's crank trajectory
        EspDrivelineIn in = driveline(slaved ? ESP_MODE_SLAVED_CRANK : ESP_MODE_FREE);
        in.slave_inertia_kgm2 = jc;
        in.slave_fc_hz = 8.0;
        in.slave_torque_limit_nm = 3000.0;
        if (esp_step_begin(e, k, h, &in, slaved ? wcs : nullptr, slaved ? sub + 1 : 0) != ESP_OK) {
            r.finite = false;
            r.why = "step_begin failed";
            break;
        }
        if (!slaved) {  // the engine runs free during start-up; Unity needs its speed when slaving starts
            EspStepOut out = stepOut();
            waitStep(e, k, out);
        }

        const double rpmNow = slaved ? wc * 60 / (2 * 3.14159265358979323846) : 0;
        if (t + 1e-9 >= nextLog) {
            EspStepOut out = stepOut();
            double rpm = rpmNow;
            if (!slaved && esp_step_end(e, k, 0, &out) == ESP_OK) rpm = out.rpm;
            mine.t.push_back(t);
            mine.rpm.push_back(rpm);
            mine.v.push_back(v);
            nextLog += 0.02;
        }
        if (sc.kind == 1 && t > 3.0 && slaved) rpmAfter3.push_back(rpmNow);
        if (!std::isfinite(v) || !std::isfinite(wc)) {
            r.finite = false;
            break;
        }
    }
    r.vEnd = v;
    if (r.slopeN > 0) r.slopeMean /= r.slopeN;
    else r.slopeMin = r.slopeMax = 0;
    r.rpmEnd = wc * 60 / (2 * 3.14159265358979323846);
    r.dv = v - (ref.v.empty() ? 0 : ref.v.back());
    double sumD = 0;
    int cnt = 0;
    for (size_t i = 0; i < std::min(mine.rpm.size(), ref.rpm.size()); ++i) {
        if (mine.t[i] < 2.0) continue;
        sumD += std::fabs(mine.rpm[i] - ref.rpm[i]);
        ++cnt;
    }
    r.meanAbsDrpm = cnt > 0 ? sumD / cnt : 0;
    r.trackRms = nTrack > 0 ? std::sqrt(sq / nTrack) * 60 / (2 * 3.14159265358979323846) : 0;
    {
        char dump[512] = {};
        size_t dl = 0;
        if (getenv_s(&dl, dump, sizeof dump, "ESP_COSIM_DUMP") == 0 && dl > 1) {  // dev: per-case rpm series
            std::string name = std::string(dump) + "/" + sc.name + "_" + cs.name + ".csv";
            for (char &ch : name)
                if (ch == ' ') ch = '_';
            FILE *fp = nullptr;
            if (fopen_s(&fp, name.c_str(), "w") == 0 && fp != nullptr) {
                std::fprintf(fp, "k,t,unity_rpm,t_used,t_ind,dT_dw\n");
                for (int64_t q = 0; q < total; ++q)
                    std::fprintf(fp, "%lld,%.4f,%.2f,%.2f,%.2f,%.3f\n", static_cast<long long>(q), q * h,
                                 wcEnd[static_cast<size_t>(q)] * 9.5492966, usedT[static_cast<size_t>(q)],
                                 tInd[static_cast<size_t>(q)], slope[static_cast<size_t>(q)]);
                std::fclose(fp);
            }
        }
    }
    if (sc.kind == 1 && rpmAfter3.size() > 60) {
        // Cruise oscillation: deviation of Unity's crank rpm (every step after 3 s) from its +-0.25 s moving average,
        // as the amplitude of the equivalent sine (sqrt(2) x RMS; P4: "no rpm oscillation above +-15 rpm"). The
        // maximum is reported too; it is set by engine-sim's random misfires (burn-efficiency randomness: one step
        // at about 40 % torque), which dip the crank by about 20 rpm at any lag and are not a coupling oscillation.
        const size_t w = 30;
        double sq2 = 0;
        size_t nd = 0;
        for (size_t i = w; i + w < rpmAfter3.size(); ++i) {
            double s = 0;
            for (size_t q = i - w; q <= i + w; ++q) s += rpmAfter3[q];
            const double d = rpmAfter3[i] - s / (2 * w + 1);
            r.rippleMax = std::max(r.rippleMax, std::fabs(d));
            sq2 += d * d;
            ++nd;
        }
        if (nd > 0) r.ripple = std::sqrt(2.0 * sq2 / nd);
    }
    // P4 criteria (06 4.5 and 8): no divergence (finite, final speed within 0.25 m/s of the monolithic reference) and no
    // cruise oscillation above +-15 rpm. Tracking RMS and limiter overshoot are reported, not gated.
    r.pass = r.finite && std::fabs(r.dv) <= 0.25 && (sc.kind != 1 || r.ripple <= 15.0);
    if (!r.pass && r.why.empty()) {
        if (!r.finite) r.why = "not finite";
        else if (std::fabs(r.dv) > 0.25) r.why = "dv";
        else r.why = "oscillation";
    }
    drainLog();
    return r;
}

double calibrate(std::string &err) {
    Engine eng;
    CreateOpts o;
    o.audioRate = 0;
    o.workerThread = 1;
    if (!eng.open("v8_cross_a", o, err)) return 0;
    double j = 0;
    if (esp_calibrate_inertia(eng.h(), &j) != ESP_OK) err = "esp_calibrate_inertia failed";
    return j;
}

} // namespace

int runCosimMatrix(const std::string &traceDir, const std::string &outJson, int threads) {
    std::string err;
    const double t0 = nowS();
    const double jCal = calibrate(err);
    if (!err.empty() || !(jCal > 0)) {
        std::printf("FAIL calibration: %s\n", err.c_str());
        return 1;
    }
    std::printf("cosim matrix (v8_cross_a at 10 kHz / 8, 1,614 kg car, 120 Hz x 4 substeps): J_eff = %.4f kg m^2 "
                "(esp_calibrate_inertia; 01 measured 0.4570)\n",
                jCal);

    std::vector<CaseSpec> cases;
    // 01 3.5's configuration (1-step lag, no feed-forward, no backstop), then the M1 design: L = 1-4 with the dT_dw
    // feed-forward and the fuel-cut backstop; then L = 4 without each of them
    cases.push_back({"L1 bare", 1, false, {}, false});
    for (int l = 1; l <= 4; ++l) cases.push_back({fmt("L%d", l), l, true, {}, true});
    cases.push_back({"L4 no-ff", 4, false, {}, true});
    cases.push_back({"L4 no-backstop", 4, true, {}, false});
    const double fpsList[4] = {30, 45, 60, 120};
    std::vector<std::string> traceNotes;
    for (double fps : fpsList) {
        std::vector<double> frames;
        std::string src = "synthetic";
        if (!traceDir.empty()) {
            std::string e2;
            frames = loadFrameTrace(fmt("%s/frames_%dfps.csv", traceDir.c_str(), static_cast<int>(fps)), e2);
            if (!frames.empty()) src = fmt("frames_%dfps.csv", static_cast<int>(fps));
        }
        if (frames.empty()) frames = syntheticFrameTrace(fps, 10.0, 77 + static_cast<uint32_t>(fps));
        const std::vector<int> spf = stepsPerFrame(frames);
        int hist[6] = {};
        for (int s : spf) hist[std::min(5, s)]++;
        traceNotes.push_back(fmt("%3.0f fps (%s, %zu frames): steps/frame 0:%d 1:%d 2:%d 3:%d 4:%d", fps, src.c_str(),
                                 frames.size(), hist[0], hist[1], hist[2], hist[3], hist[4]));
        cases.push_back({fmt("trace %.0f fps", fps), 0, true, spf, true});
    }
    for (const std::string &n : traceNotes) std::printf("  trace %s\n", n.c_str());

    std::vector<Trace> refs(3);
    std::vector<CaseResult> results(3 * cases.size());
    std::vector<std::function<void()>> jobs;
    for (int s = 0; s < 3; ++s) jobs.push_back([&, s] {
        std::string e2;
        refs[s] = runReference(kScenarios[s], e2);
    });
    {
        std::atomic<size_t> next{0};
        std::vector<std::thread> pool;
        for (int i = 0; i < std::max(1, threads); ++i)
            pool.emplace_back([&] {
                for (size_t q; (q = next++) < jobs.size();) jobs[q]();
            });
        for (auto &th : pool) th.join();
    }
    jobs.clear();
    char only[64] = {};
    size_t onlyLen = 0;
    getenv_s(&onlyLen, only, sizeof only, "ESP_COSIM_ONLY");  // dev filter: one scenario name
    for (int s = 0; s < 3; ++s)
        for (size_t c = 0; c < cases.size(); ++c)
            if (onlyLen <= 1 || std::string(only) == kScenarios[s].name) jobs.push_back([&, s, c] { results[s * cases.size() + c] = runSlavedCase(kScenarios[s], cases[c], jCal, refs[s]); });
    {
        std::atomic<size_t> next{0};
        std::vector<std::thread> pool;
        for (int i = 0; i < std::max(1, threads); ++i)
            pool.emplace_back([&] {
                for (size_t q; (q = next++) < jobs.size();) jobs[q]();
            });
        for (auto &th : pool) th.join();
    }

    bool l4ok = true, allTraceOk = true;
    Json j;
    j.obj().str("gate", "esp_cosim_matrix").num("jEff", jCal, 4).arr("traces");
    for (const std::string &n : traceNotes) j.str(nullptr, n);
    j.endArr().arr("scenarios");
    for (int s = 0; s < 3; ++s) {
        const Trace &ref = refs[s];
        std::printf("\n  %-14s reference v(6.5 s) = %.2f m/s, rpm %.0f\n", kScenarios[s].name, ref.v.empty() ? 0 : ref.v.back(),
                    ref.rpm.empty() ? 0 : ref.rpm.back());
        std::printf("  %-16s %8s %10s %10s %11s %11s %8s %6s %22s %s\n", "case", "dv m/s", "|drpm|", "track rms", "limiter +rpm",
                    "unity +rpm", "osc amp", "stale", "dT_dw mean [min,max]", "verdict");
        j.obj().str("scenario", kScenarios[s].name).num("refV", ref.v.empty() ? 0 : ref.v.back(), 3).arr("cases");
        for (size_t c = 0; c < cases.size(); ++c) {
            const CaseResult &r = results[s * cases.size() + c];
            std::printf("  %-16s %+8.3f %10.0f %10.1f %11.0f %11.0f %8.1f %6d %7.2f [%6.2f,%5.2f] %s%s%s\n", r.name.c_str(), r.dv,
                        r.meanAbsDrpm, r.trackRms, r.limiterOver, r.unityOver, r.ripple, r.staleSteps, r.slopeMean, r.slopeMin,
                        r.slopeMax, r.pass ? "pass" : "FAIL", r.why.empty() ? "" : " ", r.why.c_str());
            if (r.name == "L4" && !r.pass) l4ok = false;
            if (r.name.rfind("trace", 0) == 0 && !r.pass) allTraceOk = false;
            j.obj().str("case", r.name).num("dv", r.dv, 4).num("meanAbsDrpm", r.meanAbsDrpm, 1).num("trackRmsRpm", r.trackRms, 2)
                .num("limiterOvershootRpm", r.limiterOver, 1).num("unityOvershootRpm", r.unityOver, 1)
                .num("oscillationAmpRpm", r.ripple, 2).num("oscillationMaxRpm", r.rippleMax, 2).num("dTdwMean", r.slopeMean, 3).integer("stale", r.staleSteps).boolean("finite", r.finite)
                .boolean("pass", r.pass).str("why", r.why).end();
        }
        j.endArr().end();
    }
    const bool pass = l4ok && allTraceOk;
    j.endArr().boolean("l4Pass", l4ok).boolean("tracesPass", allTraceOk).boolean("pass", pass)
        .num("wallS", nowS() - t0, 1).end();
    std::printf("\n  => L = 4 with feed-forward %s; trace-driven L %s; %s (%.1f s wall)\n", l4ok ? "passes" : "FAILS",
                allTraceOk ? "passes" : "FAILS", pass ? "PASS" : "FAIL", nowS() - t0);
    if (!outJson.empty()) j.save(outJson);
    return pass ? 0 : 1;
}

} // namespace esph
