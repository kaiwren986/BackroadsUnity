// esp_cli: command-line front end of EngineSimPlugin.dll (PLAN 1.6). Uses the public C ABI only, except `cosim`,
// whose reference run needs the plugin internals.
//
//   esp_cli info                                       build info and builtins
//   esp_cli load <engine|file.mr> [--top node]         load and print EspEngineInfo
//   esp_cli start [engine]                             crank + idle gate (default f4_boxer_a)
//   esp_cli sweep [engine] [--from 2000 --to 6500 --points 10 --throttle 1 --settle 2] [--gate] [--json f]
//   esp_cli follow [engine]                            FOLLOW_RPM 1000 -> 4000 rpm ramp gate
//   esp_cli accounting [--timescale 1] [--seconds 600] step accounting with two hitches
//   esp_cli pitch [engine] [--wav prefix]              rendered WAV pitch at 25/60/100 % pedal
//   esp_cli render-wav <engine> --out f.wav [--rpm 3000 --throttle 0.5 --seconds 5]   esp_render_offline
//   esp_cli calibrate [engine]                         esp_calibrate_inertia
//   esp_cli cosim [--traces dir] [--threads 8]         esp_cosim_matrix report
//   esp_cli bench [engine] [--affinity P|E] [--seconds 20] [--quality ultra|high|low]   real-time factor
//   esp_cli soak [engine] [--minutes 10] [--freq Hz --fluid n --taps n] [--m1-baseline]   real-time soak (underruns,
//                                                      memory growth; --m1-baseline: M1's worker scheduling and timers)
//   esp_cli all-engines [--threads 8] [--soft]         every upstream script through piranha
//   esp_cli sanitise <in.mr> <out.mr> <fictional_id>   mr_sanitise
//   esp_cli sanitise-all [--threads 8]                 mr_sanitise every upstream script; the copies must still load
//   esp_cli bigblock                                   v8_bigblock_a vs the upstream script it ports (dyno, start)
//   esp_cli roster                                     every builtin: crank, idle, WOT peak (report)
//   esp_cli follow-hitch [engine] [--repay-all]        FOLLOW_RPM through 100 and 250 ms hitches, real time (underruns)
//   esp_cli selftest                                   start + dyno + follow gates
// Every report also takes --json <file>.
#include "harness/harness.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using namespace esph;

static int usage() {
    std::printf("usage: esp_cli info | load | start | sweep | follow | accounting | pitch | render-wav | calibrate |\n"
                "               cosim | bench | soak | all-engines | sanitise | selftest   (see tools/esp_cli.cpp)\n");
    return 2;
}

int main(int argc, char **argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    if (argc < 2) return usage();
    const std::string cmd = argv[1];
    Args a(argc, argv, 2);
    const std::string json = a.get("json");
    auto engineArg = [&](const char *def) { return a.pos.empty() ? std::string(def) : a.pos[0]; };

    if (cmd == "info") {
        char buf[2048];
        esp_build_info(buf, sizeof buf);
        std::printf("esp_version %d\n%s\nes library: %s\nengines: %s\n", esp_version(), buf, esDir().c_str(),
                    enginesDir().c_str());
        return 0;
    }
    if (cmd == "load") {
        if (a.pos.empty()) return usage();
        Engine eng;
        std::string err;
        CreateOpts o;
        if (!eng.open(a.pos[0], o, err, a.get("top"))) {
            std::printf("FAIL %s\n", err.c_str());
            drainLog();
            return 1;
        }
        const EspEngineInfo &i = eng.info();
        std::printf("name %s | %d cyl, %d banks, %d exhausts, %d intakes | %d Hz, %d gas substeps | %.3f L | redline %.0f rpm, "
                    "rev limit %.0f rpm | starter %.0f N m at %.0f rpm | crank I %.4f kg m^2 | IR taps %d | gears %d\n",
                    i.name, i.cylinders, i.banks, i.exhaust_systems, i.intakes, i.sim_frequency, i.fluid_steps,
                    i.displacement_m3 * 1000, i.redline_rad_s * 9.5492966, i.rev_limit_rad_s * 9.5492966, i.starter_torque_nm,
                    i.starter_speed_rad_s * 9.5492966, i.crank_inertia_kgm2, i.ir_taps, i.script_gear_count);
        drainLog();
        return 0;
    }
    if (cmd == "start") return runStartGate(engineArg("f4_boxer_a"), json);
    if (cmd == "sweep") {
        const std::string engine = engineArg("f4_boxer_a");
        if (!a.has("from") && !a.has("to") && !a.has("points")) return runDynoGate(engine, json, a.has("gate"));
        Engine eng;
        std::string err;
        CreateOpts o;
        o.audioRate = 0;
        if (!eng.open(engine, o, err)) {
            std::printf("FAIL %s\n", err.c_str());
            return 1;
        }
        const int n = static_cast<int>(a.num("points", 10));
        std::vector<double> t(n), p(n);
        const double from = a.num("from", 1000), to = a.num("to", 6000);
        const int32_t r = esp_dyno_sweep(eng.h(), from, to, n, static_cast<float>(a.num("throttle", 1.0)),
                                         a.num("settle", 2.0), t.data(), p.data());
        if (r != ESP_OK) {
            std::printf("FAIL esp_dyno_sweep %d\n", r);
            return 1;
        }
        std::printf("rpm,torque_nm,power_kw,power_hp\n");
        for (int i = 0; i < n; ++i) {
            const double rpm = n == 1 ? from : from + (to - from) * i / (n - 1);
            std::printf("%.0f,%.2f,%.2f,%.2f\n", rpm, t[i], p[i] / 1000, p[i] / 745.699872);
        }
        return 0;
    }
    if (cmd == "follow") return runFollowGate(engineArg("f4_boxer_a"), json);
    if (cmd == "accounting") return runStepAccounting(a.num("timescale", 1.0), a.num("seconds", 600), json);
    if (cmd == "pitch") return runPitchGate(engineArg("f4_boxer_a"), json, a.get("wav"));
    if (cmd == "render-wav") {
        const std::string engine = engineArg("f4_boxer_a");
        Engine eng;
        std::string err;
        CreateOpts o;
        o.workerThread = 1;
        if (!eng.open(engine, o, err)) {
            std::printf("FAIL %s\n", err.c_str());
            return 1;
        }
        const double seconds = a.num("seconds", 5.0);
        std::vector<float> x(static_cast<size_t>(seconds * 48000) + 48000);
        const int32_t n = esp_render_offline(eng.h(), a.num("rpm", 3000), static_cast<float>(a.num("throttle", 0.5)), seconds,
                                             x.data(), static_cast<int32_t>(x.size()));
        if (n <= 0) {
            std::printf("FAIL esp_render_offline %d\n", n);
            return 1;
        }
        x.resize(static_cast<size_t>(n));
        const std::string out = a.get("out", "render.wav");
        writeWav(out, x, 48000);
        const PitchCheck pc = checkPitch(x.data() + x.size() / 2, x.size() / 2, 48000, a.num("rpm", 3000), eng.info().cylinders);
        std::printf("wrote %s (%.2f s); %s; RMS %.1f dBFS; clipped %.3f %%\n", out.c_str(), n / 48000.0, pc.describe().c_str(),
                    rmsDb(x.data(), x.size()), 100 * clipFraction(x.data(), x.size()));
        return 0;
    }
    if (cmd == "calibrate") {
        const std::string engine = engineArg("v8_cross_a");
        Engine eng;
        std::string err;
        CreateOpts o;
        o.audioRate = 0;
        if (!eng.open(engine, o, err)) {
            std::printf("FAIL %s\n", err.c_str());
            return 1;
        }
        double j = 0;
        const double t0 = nowS();
        const int32_t r = esp_calibrate_inertia(eng.h(), &j);
        std::printf("%s: J_eff %.4f kg m^2 (crankshaft I %.4f), %.2f s CPU wall, status %d\n", engine.c_str(), j,
                    eng.info().crank_inertia_kgm2, nowS() - t0, r);
        return r == ESP_OK ? 0 : 1;
    }
    if (cmd == "cosim") return runCosimMatrix(a.get("traces"), json, static_cast<int>(a.num("threads", 8)));
    if (cmd == "bench")
        return runBench(engineArg("f4_boxer_a"), a.get("affinity"), a.num("seconds", 20), a.get("quality", "ultra"), json);
    if (cmd == "soak")
        return runSoak(engineArg("f4_boxer_a"), a.num("minutes", 10), json, static_cast<int>(a.num("freq", 0)),
                       static_cast<int>(a.num("fluid", 0)), static_cast<int>(a.num("taps", 0)), a.has("m1-baseline"));
    if (cmd == "all-engines") return runAllEngines(json, static_cast<int>(a.num("threads", 8)), !a.has("soft"));
    if (cmd == "sanitise-all") return runSanitiseAll(json, static_cast<int>(a.num("threads", 8)));
    if (cmd == "sanitise") {
        if (a.pos.size() < 3) return usage();
        char err[1024] = {};
        const int32_t r = esp_mr_sanitise(a.pos[0].c_str(), a.pos[1].c_str(), a.pos[2].c_str(), err, sizeof err);
        std::printf("%s: %s\n", r == ESP_OK ? "ok" : "FAIL", err);
        return r == ESP_OK ? 0 : 1;
    }
    if (cmd == "bigblock") return runBigBlockGate(json);
    if (cmd == "roster") return runRosterReport(json);
    if (cmd == "follow-hitch") return runFollowHitch(engineArg("v8_bigblock_a"), json, a.has("repay-all"));
    if (cmd == "selftest") {
        int fails = 0;
        fails += runStartGate("f4_boxer_a", "") != 0;
        fails += runDynoGate("f4_boxer_a", "", true) != 0;
        fails += runFollowGate("f4_boxer_a", "") != 0;
        std::printf("selftest: %s\n", fails == 0 ? "PASS" : "FAIL");
        return fails == 0 ? 0 : 1;
    }
    return usage();
}
