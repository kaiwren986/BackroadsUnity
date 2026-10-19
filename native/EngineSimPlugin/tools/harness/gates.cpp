// gates.cpp: the M1 native gates (PLAN 6.1 M1 headline and lane gates), shared by esp_cli and the ctests.
#include "harness.h"

#include <windows.h>
#include <intrin.h>
#include <psapi.h>
#include <timeapi.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <thread>

namespace esph {

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kHpW = 745.699872;  // mechanical horsepower in watts

double rpmToRad(double rpm) { return rpm * 2 * kPi / 60.0; }

struct Window {
    double sum = 0, mn = 1e30, mx = -1e30;
    int n = 0;
    void add(double v) {
        sum += v;
        mn = std::min(mn, v);
        mx = std::max(mx, v);
        ++n;
    }
    double mean() const { return n > 0 ? sum / n : 0; }
};

} // namespace

// ---------------------------------------------------------------------------------------------------------------
// EJ25 (f4_boxer_a) cranks and idles at 700-900 rpm within 3 s of ignition (PLAN 6.1 M1 headline).
int runStartGate(const std::string &engine, const std::string &outJson) {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 48000;
    if (!eng.open(engine, o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    Driver d{eng.h()};
    const double starterRpm = eng.info().starter_speed_rad_s * 60.0 / (2 * kPi);
    EspDrivelineIn free = driveline(ESP_MODE_FREE);
    // Like a driver: hold the starter until the engine runs on its own (rpm > 1.3 x starter speed for 0.25 s).
    EspControls c = controls(0.0f, true, true);
    esp_set_controls(eng.h(), &c);
    double above = 0, tRelease = -1;
    while (d.t < 5.0) {
        if (!d.step(free)) break;
        if (d.last.rpm > 1.3 * starterRpm) above += d.dt;
        else above = 0;
        if (above >= 0.25) {
            tRelease = d.t;
            break;
        }
    }
    c = controls(0.0f, true, false);
    esp_set_controls(eng.h(), &c);
    Window idle;  // the 2 s after t = 3 s
    double rpmAt3 = 0;
    while (d.t < 5.0 + 1e-9) {
        if (!d.step(free)) break;
        if (std::fabs(d.t - 3.0) < d.dt / 2) rpmAt3 = d.last.rpm;
        if (d.t > 3.0) idle.add(d.last.rpm);
    }
    const bool started = tRelease > 0 && tRelease <= 3.0;
    const bool inRange = idle.n > 0 && idle.mean() >= 700 && idle.mean() <= 900 && idle.mn > 500;
    const bool pass = started && inRange;
    std::printf("start %s: starter released at %.2f s (rpm > %.0f for 0.25 s); rpm at 3 s %.0f; idle 3-5 s mean %.0f "
                "(min %.0f, max %.0f) -> %s (gate: running by 3 s, idle 700-900 rpm)\n",
                engine.c_str(), tRelease, 1.3 * starterRpm, rpmAt3, idle.mean(), idle.mn, idle.mx, pass ? "PASS" : "FAIL");
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_start").str("engine", engine).num("releaseS", tRelease, 3).num("rpmAt3s", rpmAt3, 1)
            .num("idleMeanRpm", idle.mean(), 1).num("idleMinRpm", idle.mn, 1).num("idleMaxRpm", idle.mx, 1)
            .boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// WOT dyno: 229 +- 7 N m at 4,500 +- 500 rpm and 170 +- 6 hp at 6,500 rpm (02 6.2).
int runDynoGate(const std::string &engine, const std::string &outJson, bool gate) {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 0;
    o.workerThread = 1;
    if (!eng.open(engine, o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    const int points = 10;
    double torque[points] = {}, power[points] = {};
    const double t0 = nowS();
    const int32_t r = esp_dyno_sweep(eng.h(), 2000, 6500, points, 1.0f, 2.0, torque, power);
    const double wall = nowS() - t0;
    if (r != ESP_OK) {
        std::printf("FAIL esp_dyno_sweep returned %d\n", r);
        drainLog();
        return 1;
    }
    int peakI = 0;
    std::printf("dyno %s (WOT, 2 s settle + 1 s average per point, %.1f s wall):\n  %8s %8s %8s\n", engine.c_str(), wall,
                "rpm", "N m", "hp");
    for (int i = 0; i < points; ++i) {
        const double rpm = 2000 + 500.0 * i;
        std::printf("  %8.0f %8.1f %8.1f\n", rpm, torque[i], power[i] / kHpW);
        if (torque[i] > torque[peakI]) peakI = i;
    }
    const double peakRpm = 2000 + 500.0 * peakI;
    const double hp6500 = power[points - 1] / kHpW;
    const bool pass = !gate || (std::fabs(torque[peakI] - 229) <= 7 && std::fabs(peakRpm - 4500) <= 500 &&
                                std::fabs(hp6500 - 170) <= 6);
    std::printf("  peak %.1f N m at %.0f rpm; %.1f hp at 6500 rpm -> %s%s\n", torque[peakI], peakRpm, hp6500,
                pass ? "PASS" : "FAIL", gate ? " (gate: 229 +- 7 N m at 4500 +- 500 rpm, 170 +- 6 hp at 6500 rpm)" : "");
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_dyno").str("engine", engine).num("peakTorqueNm", torque[peakI], 2)
            .num("peakRpm", peakRpm, 0).num("hpAt6500", hp6500, 2).num("wallS", wall, 2).arr("points");
        for (int i = 0; i < points; ++i)
            j.obj().num("rpm", 2000 + 500.0 * i, 0).num("torqueNm", torque[i], 2).num("hp", power[i] / kHpW, 2).end();
        j.endArr().boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// FOLLOW_RPM tracks a 1,000 -> 4,000 rpm ramp with < 30 rpm RMS.
int runFollowGate(const std::string &engine, const std::string &outJson) {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 48000;
    if (!eng.open(engine, o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    Driver d{eng.h()};
    EspDrivelineIn f = driveline(ESP_MODE_FOLLOW_RPM);
    EspControls c = controls(0.2f, true, false);
    esp_set_controls(eng.h(), &c);
    // settle at 1000 rpm, ramp to 4000 over 3 s, hold 1 s
    double sq = 0, mx = 0, holdTorque = 0, holdMap = 0;
    int n = 0, nHold = 0;
    for (int k = 0; k < 120 * 5; ++k) {
        const double t = k / 120.0;
        double target = 1000;
        if (t >= 1.0) target = 1000 + 3000 * std::min(1.0, (t - 1.0) / 3.0);
        f.follow_rpm = target;
        if (!d.step(f)) break;
        if (t >= 1.0) {
            const double e = d.last.rpm - target;
            sq += e * e;
            mx = std::max(mx, std::fabs(e));
            ++n;
        }
        if (t >= 4.25) {  // the engine fires against the hold: positive brake torque, part-throttle MAP
            holdTorque += d.last.dyno_torque_nm;
            holdMap += d.last.manifold_pressure_pa;
            ++nHold;
        }
    }
    holdTorque /= std::max(1, nHold);
    holdMap /= std::max(1, nHold);
    const double rms = n > 0 ? std::sqrt(sq / n) : 1e9;
    const bool pass = rms < 30.0;
    std::printf("follow %s: 1000 -> 4000 rpm in 3 s, then 1 s hold: RMS %.2f rpm, max %.1f rpm -> %s (gate < 30 rpm RMS); "
                "at the 4000 rpm hold, 20 %% pedal: %.1f N m brake torque, MAP %.1f kPa\n",
                engine.c_str(), rms, mx, pass ? "PASS" : "FAIL", holdTorque, holdMap / 1000);
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_follow").str("engine", engine).num("rmsRpm", rms, 3).num("maxRpm", mx, 2)
            .num("holdTorqueNm", holdTorque, 2).num("holdMapKpa", holdMap / 1000, 2)
            .boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// Step accounting over `seconds` of game time = round(sum(dt) * f) +- 1, at a time scale, with two real hitches
// (coast mode + borrowed steps) on the way. Frames follow a jittered 60 fps trace in wall time.
int runStepAccounting(double timeScale, double seconds, const std::string &outJson) {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 48000;
    o.simFrequency = 5000;  // accounting is frequency-independent; 5 kHz/2 keeps 600 s of game time quick
    o.fluidSteps = 2;
    o.firMaxTaps = 64;
    o.flags = ESP_CREATE_NO_DRIFT_TRIM;
    if (!eng.open("i4_synth", o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    EspEngine *e = eng.h();
    const double f = eng.info().sim_frequency;
    // audio consumer: drains whatever is there, like a very fast audio thread
    std::atomic<bool> run{true};
    std::atomic<int64_t> drained{0};
    std::thread consumer([&] {
        std::vector<float> buf(4096 * 2);
        while (run.load()) {
            const int32_t got = esp_read_audio(e, buf.data(), 4096, 2);
            if (got > 0) drained += got;
            else Sleep(0);
        }
    });

    const std::vector<double> frames = syntheticFrameTrace(60.0, seconds / timeScale, 1234);
    std::vector<double> gameFrames(frames.size());
    for (size_t i = 0; i < frames.size(); ++i) gameFrames[i] = frames[i] * timeScale;
    const std::vector<int> steps = stepsPerFrame(gameFrames);
    const double dt = 1.0 / 120.0;
    double sumDt = 0;
    int64_t k = 0;
    EspDrivelineIn in = driveline(ESP_MODE_FREE);
    in.load_torque_nm = 40.0;
    const double hitchAt[2] = {seconds / 3, 2 * seconds / 3};
    int hitches = 0;
    const double t0 = nowS();
    int64_t collected = 0;
    for (size_t fi = 0; fi < frames.size() && sumDt < seconds; ++fi) {
        const bool starting = sumDt < 2.0;
        EspControls c = controls(starting ? 0.0f : 0.3f, true, sumDt < 1.5, static_cast<float>(timeScale),
                                 static_cast<float>(frames[fi]), static_cast<int>(fi));
        esp_set_controls(e, &c);
        for (int s = 0; s < steps[fi]; ++s) {
            if (esp_step_begin(e, k, dt, &in, nullptr, 0) != ESP_OK) {
                std::printf("FAIL esp_step_begin at step %lld\n", static_cast<long long>(k));
                run = false;
                consumer.join();
                return 1;
            }
            sumDt += dt;
            ++k;
        }
        // keep the worker within a few frames (like Unity collecting step k - L), never far behind
        while (collected < k - 8) {
            EspStepOut out = stepOut();
            if (waitStep(e, collected, out)) ++collected;
            else break;
        }
        if (hitches < 2 && sumDt >= hitchAt[hitches]) {
            // let the worker finish what it has, then go quiet for 150 ms of wall time: a real hitch
            EspStepOut out = stepOut();
            waitStep(e, k - 1, out);
            collected = k;
            Sleep(150);
            ++hitches;
        }
    }
    // The run ends here: no coasting after the last command (it would be borrowed with nothing left to repay it).
    EspWorkerOptions wo{};
    wo.struct_size = sizeof wo;
    wo.coast_max_ms = 0;
    esp_set_worker_options(e, &wo);
    EspStepOut last = stepOut();
    const bool lr = waitStep(e, k - 1, last);
    const double wall = nowS() - t0;
    const EspStats s = stats(e);
    Sleep(50);
    run = false;
    consumer.join();
    const int64_t expect = std::llround(sumDt * f);
    const bool stepsOk = lr && std::llabs(s.steps_run - expect) <= 1 && std::llabs(s.steps_target - expect) <= 1;
    const bool coastOk = s.coast_events >= 2 && s.steps_borrowed == 0;
    const double expectFrames = sumDt / timeScale * 48000.0;
    // coasted steps replace the repaid ones, so the audio still matches sum(dt) / timeScale
    const double audioRatio = static_cast<double>(s.audio_frames_produced) / expectFrames;
    const bool audioOk = std::fabs(audioRatio - 1.0) < 0.01;
    const bool pass = stepsOk && coastOk && audioOk;
    std::printf("step accounting at timeScale %.2f over %.0f s of game time (%lld commands, %.1f s wall):\n"
                "  steps run %lld, target %lld, round(sum(dt) * f) = %lld -> %s (+-1)\n"
                "  hitches 2 x 150 ms: coast events %d, coast steps %lld, borrowed left %lld -> %s\n"
                "  audio frames %lld vs sum(dt) / timeScale x 48 kHz = %.0f: ratio %.5f -> %s (+-1 %%)\n"
                "  => %s\n",
                timeScale, sumDt, static_cast<long long>(k), wall, static_cast<long long>(s.steps_run),
                static_cast<long long>(s.steps_target), static_cast<long long>(expect), stepsOk ? "ok" : "MISMATCH",
                s.coast_events, static_cast<long long>(s.coast_steps), static_cast<long long>(s.steps_borrowed),
                coastOk ? "ok" : "BAD", static_cast<long long>(s.audio_frames_produced), expectFrames,
                audioRatio, audioOk ? "ok" : "BAD", pass ? "PASS" : "FAIL");
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_step_accounting").num("timeScale", timeScale, 2).num("gameSeconds", sumDt, 3)
            .integer("commands", k).integer("stepsRun", s.steps_run).integer("stepsTarget", s.steps_target)
            .integer("expected", expect).integer("coastEvents", s.coast_events).integer("coastSteps", s.coast_steps)
            .integer("borrowedLeft", s.steps_borrowed).num("audioRatio", audioRatio, 6).boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// Rendered WAV pitch at 25/60/100 % pedal against 02's fixed load (02 6.7), per the PLAN 1.9 rule; clipping.
int runPitchGate(const std::string &engine, const std::string &outJson, const std::string &wavPrefix) {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 48000;
    o.flags = ESP_CREATE_NO_DRIFT_TRIM;
    if (!eng.open(engine, o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    EspEngine *e = eng.h();
    const int cyl = eng.info().cylinders;
    Driver d{e};
    const std::string wav = (wavPrefix.empty() ? outDir() + "/esp_pitch_" + engine : wavPrefix) + ".wav";
    const double segS = 10.0, startS = 5.0;
    const double pedals[3] = {0.25, 0.60, 1.00};
    const double total = startS + 3 * segS;
    esp_debug_tap(e, wav.c_str(), total + 1.0);
    std::vector<double> rpmAt;  // per step
    EspDrivelineIn in = driveline(ESP_MODE_FREE);
    const double wRef = rpmToRad(5000);
    for (int k = 0; d.t < total - 1e-9; ++k) {
        const double t = d.t;
        float pedal = 0.0f;
        bool starter = t < 2.0;
        if (t >= startS) pedal = static_cast<float>(pedals[std::min(2, static_cast<int>((t - startS) / segS))]);
        EspControls c = controls(pedal, true, starter);
        esp_set_controls(e, &c);
        const double w = d.last.crank_speed;
        in.load_torque_nm = t >= startS ? 200.0 * (w / wRef) * (w / wRef) : 0.0;  // T = 200 N m (rpm/5000)^2
        if (!d.step(in)) break;
        rpmAt.push_back(d.last.rpm);
    }
    esp_debug_tap(e, nullptr, 0);   // stop and flush ...
    d.step(in);                      // ... at the next render
    eng.close();

    // read back the tap (16-bit mono)
    std::vector<float> x;
    {
        FILE *fp = nullptr;
        if (fopen_s(&fp, wav.c_str(), "rb") != 0 || fp == nullptr) {
            std::printf("FAIL no tap written: %s\n", wav.c_str());
            return 1;
        }
        std::fseek(fp, 44, SEEK_SET);
        int16_t s;
        while (std::fread(&s, 2, 1, fp) == 1) x.push_back(s / 32768.0f);
        std::fclose(fp);
    }
    const int rate = 48000;
    bool pass = true;
    Json j;
    j.obj().str("gate", "esp_pitch").str("engine", engine).str("wav", wav).arr("segments");
    std::printf("pitch %s (%d cyl), tap %s (%.1f s), last 3 s of each 10 s segment against T = 200 N m (rpm/5000)^2:\n",
                engine.c_str(), cyl, wav.c_str(), x.size() / 48000.0);
    for (int p = 0; p < 3; ++p) {
        for (int w = 0; w < 3; ++w) {
            const double a = startS + p * segS + 7.0 + w, b = a + 1.0;
            const size_t i0 = static_cast<size_t>(a * rate), i1 = std::min(x.size(), static_cast<size_t>(b * rate));
            double rpm = 0;
            int n = 0;
            for (size_t k = static_cast<size_t>(a * 120); k < static_cast<size_t>(b * 120) && k < rpmAt.size(); ++k) {
                rpm += rpmAt[k];
                ++n;
            }
            rpm = n > 0 ? rpm / n : 0;
            if (i1 <= i0) {
                pass = false;
                continue;
            }
            const PitchCheck pc = checkPitch(&x[i0], i1 - i0, rate, rpm, cyl);
            const double clip = clipFraction(&x[i0], i1 - i0);
            const double db = rmsDb(&x[i0], i1 - i0);
            if (!pc.pass()) pass = false;
            std::printf("  %3.0f %% %4.1f-%4.1f s: %s | RMS %.1f dBFS, clipped %.3f %%\n", pedals[p] * 100, a, b,
                        pc.describe().c_str(), db, clip * 100);
            j.obj().num("pedal", pedals[p], 2).num("fromS", a, 1).num("rpm", rpm, 1).num("firingHz", pc.expectedFiringHz, 2)
                .num("acfHz", pc.acfHz, 2).num("peakHz", pc.peakHz, 2).boolean("pass", pc.pass()).num("rmsDb", db, 2)
                .num("clipPct", clip * 100, 4).end();
        }
    }
    const double clipAll = clipFraction(x.data(), x.size());
    const bool clipOk = clipAll < 0.001;
    pass = pass && clipOk && !x.empty();
    std::printf("  clipped samples over the whole render: %.4f %% -> %s (< 0.1 %%)\n  => %s\n", clipAll * 100,
                clipOk ? "ok" : "TOO MANY", pass ? "PASS" : "FAIL");
    j.endArr().num("clipPctAll", clipAll * 100, 4).boolean("pass", pass).end();
    if (!outJson.empty()) j.save(outJson);
    drainLog();
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// Real-time factor per tier and core class (PLAN 3.4 [T7]). Steady 60 % pedal against 02's fixed load.
int runBench(const std::string &engine, const std::string &affinity, double seconds, const std::string &quality,
             const std::string &outJson) {
    std::string note;
    const uint64_t mask = setProcessAffinityClass(affinity, note);
    if (!note.empty()) std::printf("bench: %s\n", note.c_str());
    CreateOpts o;
    o.audioRate = 48000;
    o.flags = ESP_CREATE_NO_DRIFT_TRIM | ESP_CREATE_NO_COAST;
    if (quality == "high") o.fluidSteps = 4;
    else if (quality == "low") {
        o.fluidSteps = 4;
        o.firMaxTaps = 2000;
    } else {
        o.fluidSteps = 8;
        o.firMaxTaps = 10000;
    }
    Engine eng;
    std::string err;
    if (!eng.open(engine, o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    EspEngine *e = eng.h();
    std::atomic<bool> run{true};
    std::thread consumer([&] {  // drain the ring so the renderer always works on fresh audio
        std::vector<float> buf(4096 * 2);
        while (run.load())
            if (esp_read_audio(e, buf.data(), 4096, 2) <= 0) Sleep(1);
    });
    EspDrivelineIn in = driveline(ESP_MODE_FREE);
    const double wRef = rpmToRad(5000);
    const double dt = 1.0 / 120;
    int64_t k = 0;
    double w = 0;
    auto runFor = [&](double simS, float pedal, bool starter) {
        const int64_t n = std::llround(simS / dt);
        for (int64_t i = 0; i < n; ++i) {
            EspControls c = controls(pedal, true, starter);
            esp_set_controls(e, &c);
            in.load_torque_nm = pedal > 0 ? 200.0 * (w / wRef) * (w / wRef) : 0.0;
            esp_step_begin(e, k, dt, &in, nullptr, 0);
            if (k >= 8) {  // keep 8 commands in flight so the worker never idles
                EspStepOut out = stepOut();
                if (waitStep(e, k - 8, out)) w = out.crank_speed;
            }
            ++k;
        }
    };
    runFor(1.5, 0.0f, true);
    runFor(2.0, 0.6f, false);  // warm-up
    const EspStats s0 = stats(e);
    runFor(seconds, 0.6f, false);
    EspStepOut out = stepOut();
    waitStep(e, k - 1, out);
    const EspStats s1 = stats(e);
    run = false;
    consumer.join();
    // RTF over the timed part only: simulated seconds / worker wall seconds
    const double simS = static_cast<double>(s1.steps_run - s0.steps_run) / s1.sim_frequency;
    const double rtfAll = s1.rtf;
    const double usPerStep = rtfAll > 0 ? 1e6 / (rtfAll * s1.sim_frequency) : 0.0;
    const bool isBoxer = engine == "f4_boxer_a";
    const bool gate = isBoxer && affinity == "E" && quality == "ultra";
    const bool pass = !gate || rtfAll >= 1.5;
    std::printf("bench %s, quality %s, affinity %s: %.1f s simulated; RTF %.2f (wall), %.2f (CPU); %.1f us/step; "
                "worker %.3f ms per 1/120 s command; P-core share %.0f %% -> %s%s\n",
                engine.c_str(), quality.c_str(), affinity.empty() ? "unpinned" : affinity.c_str(), simS, rtfAll,
                s1.rtf_cpu, usPerStep, s1.worker_ms_ema, s1.p_core_share * 100, pass ? "PASS" : "FAIL",
                gate ? " (gate: Ultra, E-pinned, RTF >= 1.5)" : "");
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_bench").str("engine", engine).str("quality", quality).str("affinity", affinity)
            .str("affinityMask", fmt("0x%llx", static_cast<unsigned long long>(mask))).num("rtf", rtfAll, 3)
            .num("rtfCpu", s1.rtf_cpu, 3).num("usPerStep", usPerStep, 2).num("workerMsPerCommand", s1.worker_ms_ema, 4)
            .num("pCoreShare", s1.p_core_share, 3).integer("simFrequency", s1.sim_frequency).boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

// ---------------------------------------------------------------------------------------------------------------
// Real-time soak (ASan preset): 60 fps commands paced by the wall clock, a 48 kHz consumer pulling 256 frames every
// 5.33 ms, a slow rev cycle. Gates: 0 steady-state underruns, < 1 MB private-memory growth after the first minute.
int runSoak(const std::string &engine, double minutes, const std::string &outJson, int simFrequency, int fluidSteps,
            int firMaxTaps) {
    Engine eng;
    std::string err;
    CreateOpts o;
    o.audioRate = 48000;
    o.simFrequency = simFrequency;  // under ASan (about 10x slower) a lighter configuration keeps real time
    o.fluidSteps = fluidSteps;
    o.firMaxTaps = firMaxTaps;
    if (!eng.open(engine, o, err)) {
        std::printf("FAIL %s\n", err.c_str());
        return 1;
    }
    EspEngine *e = eng.h();
    std::atomic<bool> run{true};
    std::thread audio([&] {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        std::vector<float> buf(256 * 2);
        const double period = 256.0 / 48000.0;
        double next = nowS();
        while (run.load()) {
            esp_read_audio(e, buf.data(), 256, 2);
            next += period;
            const double wait = next - nowS();
            if (wait > 0.002) Sleep(static_cast<DWORD>((wait - 0.001) * 1000));
            while (nowS() < next) _mm_pause();
            if (nowS() - next > 0.1) next = nowS();  // the process was descheduled for a long time
        }
    });
    auto privateBytes = [] {
        PROCESS_MEMORY_COUNTERS_EX pmc{};
        GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&pmc), sizeof pmc);
        return static_cast<double>(pmc.PrivateUsage);
    };
    timeBeginPeriod(1);
    const double frame = 1.0 / 60.0;
    const double dt = 1.0 / 120.0;
    EspDrivelineIn in = driveline(ESP_MODE_FREE);
    int64_t k = 0;
    int fi = 0;
    const double t0 = nowS();
    double next = t0;
    double mem1 = 0, memEnd = 0;
    int underrunsAt10 = -1, stale = 0;
    double w = 0;
    const double wRef = rpmToRad(5000);
    while (true) {
        const double t = nowS() - t0;
        if (t >= minutes * 60.0) break;
        // rev cycle: 20 s period between idle and 70 % pedal against the road load
        const double phase = std::fmod(t, 20.0);
        const float pedal = t < 3 ? 0.0f : (phase < 10 ? static_cast<float>(0.7 * phase / 10) : static_cast<float>(0.7 * (20 - phase) / 10));
        EspControls c = controls(pedal, true, t < 1.5, 1.0f, static_cast<float>(frame), fi);
        esp_set_controls(e, &c);
        in.load_torque_nm = 200.0 * (w / wRef) * (w / wRef);
        for (int s = 0; s < 2; ++s, ++k) {
            esp_step_begin(e, k, dt, &in, nullptr, 0);
            if (k >= 2) {
                EspStepOut out = stepOut();
                if (esp_step_end(e, k - 2, 1000, &out) == ESP_OK) w = out.crank_speed;
                else ++stale;
            }
        }
        ++fi;
        if (underrunsAt10 < 0 && t >= 10.0) underrunsAt10 = stats(e).audio_underruns;
        if (mem1 == 0 && t >= 60.0) mem1 = privateBytes();
        next += frame;
        const double wait = next - nowS();
        if (wait > 0.002) Sleep(static_cast<DWORD>((wait - 0.001) * 1000));
        while (nowS() < next) _mm_pause();
    }
    memEnd = privateBytes();
    timeEndPeriod(1);
    EspStepOut last = stepOut();
    waitStep(e, k - 1, last);
    const EspStats s = stats(e);
    run = false;
    audio.join();
    const int steadyUnderruns = s.audio_underruns - std::max(0, underrunsAt10);
    const double growthMb = mem1 > 0 ? (memEnd - mem1) / (1024.0 * 1024.0) : 0.0;
    const bool pass = steadyUnderruns == 0 && growthMb < 1.0;
    std::printf("soak %s for %.1f min: underruns %d (after 10 s: %d), underrun frames %d, stale collects %d, coast events %d, "
                "RTF %.2f; private bytes %.1f MB at 1 min, %.1f MB at the end (growth %.3f MB) -> %s\n",
                engine.c_str(), minutes, s.audio_underruns, steadyUnderruns, s.audio_underrun_frames, stale, s.coast_events,
                s.rtf, mem1 / 1048576.0, memEnd / 1048576.0, growthMb, pass ? "PASS" : "FAIL");
    if (!outJson.empty()) {
        Json j;
        j.obj().str("gate", "esp_soak").str("engine", engine).num("minutes", minutes, 2)
            .integer("simFrequency", eng.info().sim_frequency).integer("fluidSteps", eng.info().fluid_steps)
            .integer("irTaps", eng.info().ir_taps)
            .integer("underrunsSteady", steadyUnderruns).integer("underrunsAll", s.audio_underruns)
            .integer("staleCollects", stale).integer("coastEvents", s.coast_events).num("rtf", s.rtf, 3)
            .num("memGrowthMb", growthMb, 4).boolean("pass", pass).end();
        j.save(outJson);
    }
    drainLog();
    return pass ? 0 : 1;
}

} // namespace esph
