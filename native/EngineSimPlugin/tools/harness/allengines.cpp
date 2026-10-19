// allengines.cpp: esp_all_engines (PLAN 6.1 M1 lane gate, 3.2): every upstream engine script through piranha
// (compile, crank, idle +-15 % for 5 s, 10 s at WOT with no NaN), as a report table. Hard only for the roster
// engines of PLAN 3.10. Real names appear here only as upstream file paths (internal report, Dev builds).
#include "harness.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <thread>

namespace esph {

namespace {

struct Script {
    const char *path;     // relative to assets/engines
    const char *topNode;  // "" = main()
    bool roster;          // PLAN 3.10 roster engine: its row is a hard gate
    const char *shipId;   // PLAN 3.10 shipped fictional id (roster engines)
};

// The 25 engines (27 .mr files minus the two radial.mr helpers).
const Script kScripts[] = {
    {"atg-video-1/01_honda_trx520.mr", "", false, ""},
    {"atg-video-1/02_kohler_ch750.mr", "", false, ""},
    {"atg-video-1/03_harley_davidson_shovelhead.mr", "", false, ""},
    {"atg-video-1/04_hayabusa.mr", "", false, ""},
    {"atg-video-1/05_honda_vtec.mr", "", true, "i4_vvt_a"},
    {"atg-video-1/06_subaru_ej25.mr", "", false, ""},
    {"atg-video-1/07_audi_i5.mr", "", false, ""},
    {"atg-video-1/08_radial_5.mr", "", false, ""},
    {"atg-video-2/01_subaru_ej25_eh.mr", "", true, "f4_boxer_a"},
    {"atg-video-2/02_subaru_ej25_uh.mr", "", false, ""},
    {"atg-video-2/03_2jz.mr", "", false, ""},
    {"atg-video-2/04_60_degree_v6.mr", "", true, "v6_60_a"},
    {"atg-video-2/05_odd_fire_v6.mr", "", false, ""},
    {"atg-video-2/06_even_fire_v6.mr", "", false, ""},
    {"atg-video-2/07_gm_ls.mr", "", true, "v8_cross_a"},
    {"atg-video-2/08_ferrari_f136_v8.mr", "", true, "v8_flatplane_a"},
    {"atg-video-2/09_radial_9.mr", "", false, ""},
    {"atg-video-2/10_lfa_v10.mr", "", false, ""},
    {"atg-video-2/11_merlin_v12.mr", "", true, "v12_aero_a"},
    {"atg-video-2/12_ferrari_412_t2.mr", "", false, ""},
    {"audi/i5.mr", "audi_i5_2_2L", false, ""},
    {"bmw/M52B28.mr", "M52B28", false, ""},
    {"chevrolet/chev_truck_454.mr", "chev_truck_454", true, "v8_bigblock_a"},
    {"chevrolet/engine_03_for_e1.mr", "engine_03_for_e1", false, ""},
    {"kohler/kohler_ch750.mr", "kohler_ch750", false, ""},
};

struct Row {
    std::string path;
    bool roster = false;
    bool compiled = false, cranked = false, idleOk = false, wotOk = false;
    std::string err;
    int cylinders = 0, freq = 0;
    double startPedal = 0;
    std::string startMethod = "-";
    double compileMs = 0, crankS = 0, idleMean = 0, idleDevPct = 0, wotMaxRpm = 0, rtf = 0, peakAudio = 0;
    bool pass() const { return compiled && cranked && idleOk && wotOk; }
};

Row testOne(const Script &s) {
    Row r;
    r.path = s.path;
    r.roster = s.roster;
    const std::string full = enginesDir() + "/" + s.path;
    Engine eng;
    CreateOpts o;
    o.audioRate = 48000;
    const double t0 = nowS();
    std::string err;
    if (!eng.open(full, o, err, s.topNode)) {
        r.err = err;
        return r;
    }
    r.compileMs = 1000 * (nowS() - t0);
    r.compiled = true;
    EspEngine *e = eng.h();
    r.cylinders = eng.info().cylinders;
    r.freq = eng.info().sim_frequency;
    std::atomic<bool> run{true};
    std::atomic<float> peak{0.0f};
    std::atomic<bool> nanAudio{false};
    std::thread consumer([&] {
        std::vector<float> buf(4096 * 2);
        while (run.load()) {
            const int32_t got = esp_read_audio(e, buf.data(), 4096, 2);
            for (int32_t i = 0; i < got * 2; ++i) {
                if (!std::isfinite(buf[i])) nanAudio = true;
                else if (std::fabs(buf[i]) > peak.load()) peak = std::fabs(buf[i]);
            }
            if (got <= 0) Sleep(1);
        }
    });
    Driver d{e};
    EspDrivelineIn free = driveline(ESP_MODE_FREE);
    const double starterRpm = std::max(150.0, eng.info().starter_speed_rad_s * 60.0 / (2 * 3.14159265358979323846));
    // Crank like a driver: hold the starter until the engine pulls away (rpm > 1.3 x starter speed for 0.25 s) or for
    // at most 3 s, release, and call it started if it then keeps turning on its own for 1.5 s. Engines whose idle sits
    // below their starter speed (the radials) are released after 3 s. A second try holds 20 % pedal, as a driver would
    // with a cold big engine.
    // A third try spins the engine up on the dynamometer (a bump start) for scripts whose starter cannot turn them
    // over at all (the 27 L aero V12's 258 N m starter against twelve 4.4 L cylinders).
    EspControls c;
    for (int attempt = 0; attempt < 3 && !r.cranked; ++attempt) {
        const float pedal = attempt == 0 ? 0.0f : 0.2f;
        c = controls(pedal, true, attempt < 2);
        esp_set_controls(e, &c);
        const double ts0 = d.t;
        double above = 0;
        if (attempt < 2) {
            while (d.t - ts0 < (attempt == 0 ? 5.0 : 3.0)) {
                if (!d.step(free)) break;
                if (d.last.rpm > 1.3 * starterRpm) above += d.dt;
                else above = 0;
                if (above >= 0.25) break;
            }
        } else {
            EspDrivelineIn spin = driveline(ESP_MODE_FOLLOW_RPM);
            spin.follow_rpm = std::max(600.0, 1.5 * starterRpm);
            while (d.t - ts0 < 1.5)
                if (!d.step(spin)) break;
        }
        c = controls(pedal, true, false);
        esp_set_controls(e, &c);
        const double tCrank = ts0;
        const double tRelease = d.t;
        double minRpm = 1e9;
        while (d.t - tRelease < 1.5) {
            if (!d.step(free)) break;
            minRpm = std::min(minRpm, d.last.rpm);
        }
        if (minRpm > std::max(100.0, 0.25 * starterRpm)) {
            r.cranked = true;
            r.crankS = tRelease - tCrank;
            r.startPedal = pedal;
            r.startMethod = attempt < 2 ? "starter" : "dyno";
        }
    }
    if (r.cranked) {
        // settle 2 s, then 5 s of idle: every 0.5 s mean within +-15 % of the 5 s mean
        for (int i = 0; i < 240; ++i) d.step(free);
        std::vector<double> halfMeans;
        double sum = 0, part = 0;
        int n = 0;
        for (int i = 0; i < 600; ++i) {
            if (!d.step(free)) break;
            sum += d.last.rpm;
            part += d.last.rpm;
            ++n;
            if (n % 60 == 0) {
                halfMeans.push_back(part / 60);
                part = 0;
            }
        }
        r.idleMean = n > 0 ? sum / n : 0;
        for (double m : halfMeans)
            r.idleDevPct = std::max(r.idleDevPct, 100.0 * std::fabs(m - r.idleMean) / std::max(1.0, r.idleMean));
        r.idleOk = r.idleMean > 100 && r.idleDevPct <= 15.0;
    }
    // 10 s at WOT, free in neutral (limiter bouncing): no NaN
    c = controls(1.0f, true, false);
    esp_set_controls(e, &c);
    bool finite = true;
    for (int i = 0; i < 1200; ++i) {
        if (!d.step(free)) {
            finite = false;
            break;
        }
        if (!std::isfinite(d.last.rpm) || !std::isfinite(d.last.manifold_pressure_pa)) finite = false;
        r.wotMaxRpm = std::max(r.wotMaxRpm, d.last.rpm);
    }
    run = false;
    consumer.join();
    r.peakAudio = peak.load();
    r.wotOk = finite && !nanAudio.load() && r.wotMaxRpm > 0;
    r.rtf = stats(e).rtf;
    return r;
}

} // namespace

// mr_sanitise on every script (PLAN 3.2 [C2]): the sanitised copy must still compile and run under its fictional
// name; words from the denylist that remain (references into the es library) are listed per engine.
int runSanitiseAll(const std::string &outJson, int threads) {
    const size_t n = sizeof kScripts / sizeof kScripts[0];
    const std::string outDir = tempDir() + "\\sanitised";
    CreateDirectoryA(outDir.c_str(), nullptr);
    struct SRow {
        std::string path, id, status, remaining, loadedName;
        bool loaded = false, idOk = false;
    };
    std::vector<SRow> rows(n);
    std::atomic<size_t> next{0};
    std::vector<std::thread> pool;
    for (int i = 0; i < std::max(1, threads); ++i)
        pool.emplace_back([&] {
            for (size_t q; (q = next++) < n;) {
                const Script &s = kScripts[q];
                SRow &r = rows[q];
                r.path = s.path;
                r.id = s.shipId[0] ? s.shipId : fmt("dev_engine_%02zu", q);
                const std::string src = enginesDir() + "/" + s.path;
                const std::string dir = outDir + "\\" + r.id;
                CreateDirectoryA(dir.c_str(), nullptr);
                const std::string dst = dir + "\\" + r.id + ".mr";
                // local helpers (radial.mr) are copied as they are, next to the copy
                const std::string srcDir = src.substr(0, src.find_last_of('/'));
                CopyFileA((srcDir + "/radial.mr").c_str(), (dir + "\\radial.mr").c_str(), FALSE);
                char err[1024] = {};
                const int32_t rc = esp_mr_sanitise(src.c_str(), dst.c_str(), r.id.c_str(), err, sizeof err);
                r.status = rc == ESP_OK ? "clean" : (rc == ESP_ERR_COMPILE ? "library refs" : fmt("error %d", rc));
                if (rc == ESP_ERR_COMPILE) {
                    const std::string e = err;
                    const size_t colon = e.find(':');
                    r.remaining = colon == std::string::npos ? e : e.substr(colon + 1);
                }
                if (rc != ESP_OK && rc != ESP_ERR_COMPILE) {
                    r.remaining = err;
                    continue;
                }
                // older scripts: the top node is renamed to <id>_n<k>, k = its order among the file's node definitions
                std::string top;
                if (s.topNode[0]) {
                    FILE *fp = nullptr;
                    std::string text;
                    if (fopen_s(&fp, src.c_str(), "rb") == 0 && fp) {
                        char buf[65536];
                        size_t m;
                        while ((m = std::fread(buf, 1, sizeof buf, fp)) > 0) text.append(buf, m);
                        std::fclose(fp);
                    }
                    std::vector<std::string> seen;
                    for (size_t at = 0; (at = text.find("node ", at)) != std::string::npos; at += 5) {
                        if (at > 0 && (std::isalnum(static_cast<unsigned char>(text[at - 1])) || text[at - 1] == '_')) continue;
                        size_t b = at + 5;
                        while (b < text.size() && text[b] == ' ') ++b;
                        size_t e2 = b;
                        while (e2 < text.size() && (std::isalnum(static_cast<unsigned char>(text[e2])) || text[e2] == '_')) ++e2;
                        const std::string name = text.substr(b, e2 - b);
                        if (name.empty() || name == "main" || std::find(seen.begin(), seen.end(), name) != seen.end()) continue;
                        if (name == s.topNode) top = fmt("%s_n%zu", r.id.c_str(), seen.size());
                        seen.push_back(name);
                    }
                }
                Engine eng;
                CreateOpts o;
                o.audioRate = 0;
                std::string lerr;
                if (eng.open(dst, o, lerr, top)) {
                    r.loaded = true;
                    r.loadedName = eng.info().name;
                    r.idOk = r.loadedName == r.id;
                } else {
                    r.loadedName = lerr;
                }
            }
        });
    for (auto &t : pool) t.join();
    drainLog();
    int clean = 0, loaded = 0;
    Json j;
    j.obj().str("gate", "esp_mr_sanitise").str("outDir", outDir).arr("engines");
    std::printf("mr_sanitise on %zu scripts (copies in %s):\n  %-46s %-15s %-13s %-7s %s\n", n, outDir.c_str(), "script",
                "fictional id", "result", "loads", "denylisted words left (es library references)");
    for (const SRow &r : rows) {
        if (r.status == "clean") ++clean;
        if (r.loaded && r.idOk) ++loaded;
        std::printf("  %-46s %-15s %-13s %-7s %s\n", r.path.c_str(), r.id.c_str(), r.status.c_str(),
                    r.loaded ? (r.idOk ? "yes" : "NAME?") : "NO", r.remaining.c_str());
        j.obj().str("script", r.path).str("id", r.id).str("status", r.status).boolean("loads", r.loaded)
            .boolean("nameIsId", r.idOk).str("remaining", r.remaining).end();
    }
    const bool pass = loaded == static_cast<int>(n);
    j.endArr().integer("clean", clean).integer("loadsUnderId", loaded).boolean("pass", pass).end();
    std::printf("  => %d of %zu clean without library changes; %d of %zu sanitised copies compile and load under their id -> %s\n",
                clean, n, loaded, n, pass ? "PASS" : "FAIL");
    if (!outJson.empty()) j.save(outJson);
    return pass ? 0 : 1;
}

int runAllEngines(const std::string &outJson, int threads, bool hardRoster) {
    const size_t n = sizeof kScripts / sizeof kScripts[0];
    std::vector<Row> rows(n);
    std::atomic<size_t> next{0};
    const double t0 = nowS();
    std::vector<std::thread> pool;
    for (int i = 0; i < std::max(1, threads); ++i)
        pool.emplace_back([&] {
            for (size_t q; (q = next++) < n;) rows[q] = testOne(kScripts[q]);
        });
    for (auto &t : pool) t.join();
    drainLog();

    int passed = 0, rosterFail = 0;
    std::printf("esp_all_engines: %zu engine scripts through piranha (%.1f s wall)\n", n, nowS() - t0);
    std::printf("  %-46s %3s %6s %8s %6s %5s %8s %7s %8s %5s %s\n", "script", "cyl", "Hz", "compile", "crank", "start",
                "idle rpm", "+-%", "WOT max", "RTF", "result");
    Json j;
    j.obj().str("gate", "esp_all_engines").arr("engines");
    for (const Row &r : rows) {
        const bool ok = r.pass();
        if (ok) ++passed;
        else if (r.roster) ++rosterFail;
        std::printf("  %-46s %3d %6d %6.0fms %5.2fs %3s%3.0f%% %8.0f %6.1f%% %8.0f %5.2f %s%s%s\n", r.path.c_str(), r.cylinders,
                    r.freq, r.compileMs, r.crankS, r.startMethod == "dyno" ? "dy" : "st", r.startPedal * 100, r.idleMean, r.idleDevPct, r.wotMaxRpm, r.rtf,
                    ok ? "pass" : "FAIL", r.roster ? " (roster)" : "", r.err.empty() ? "" : (" " + r.err).c_str());
        j.obj().str("script", r.path).boolean("roster", r.roster).boolean("compiled", r.compiled)
            .boolean("cranked", r.cranked).boolean("idleOk", r.idleOk).boolean("wotOk", r.wotOk).integer("cylinders", r.cylinders)
            .integer("simFrequency", r.freq).num("compileMs", r.compileMs, 1).num("crankS", r.crankS, 3).num("startPedal", r.startPedal, 2).str("startMethod", r.startMethod)
            .num("idleRpm", r.idleMean, 1).num("idleDevPct", r.idleDevPct, 2).num("wotMaxRpm", r.wotMaxRpm, 1)
            .num("rtf", r.rtf, 3).num("peakAudio", r.peakAudio, 4).boolean("pass", ok).str("error", r.err).end();
    }
    const bool pass = !hardRoster || rosterFail == 0;
    j.endArr().integer("passed", passed).integer("total", static_cast<int64_t>(n)).integer("rosterFailures", rosterFail)
        .boolean("pass", pass).end();
    std::printf("  => %d of %zu pass; roster failures %d -> %s\n", passed, n, rosterFail, pass ? "PASS" : "FAIL");
    if (!outJson.empty()) j.save(outJson);
    return pass ? 0 : 1;
}

} // namespace esph
