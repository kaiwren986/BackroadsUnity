/* EngineSimPlugin.h: C ABI v1 of EngineSimPlugin.dll (PLAN 3.3, research 01 7.1-7.2, ADR-002).
 *
 * ABI v1 freezes at M1 (ADR-002). Changing anything here needs an ADR and updated tests in the same commit
 * (PLAN 1.10). New calls and new trailing struct fields may be added in a compatible way; struct_size tells which.
 *
 * Rules for every function: extern "C", __cdecl, returns an int32_t (a status code unless stated otherwise), and never
 * lets a C++ exception escape. Strings are UTF-8 and NUL-terminated. Every struct starts with uint32_t struct_size,
 * which the caller sets to sizeof(the struct it was compiled with); the plugin accepts struct_size >= the v1 size.
 * All structs are packed (#pragma pack(1)) and laid out without padding, so C# [StructLayout(Sequential, Pack = 1)]
 * matches them byte for byte; the ESP_SIZEOF_* constants below are asserted on both sides.
 *
 * Thread contract:
 *   - Calls on one engine handle come from one control thread at a time (Unity's main thread for live engines; a
 *     background thread may own a separate handle, for example a dyno sweep, PLAN 3.5). esp_read_audio is the only
 *     call made from the audio thread; it is lock-free and allocation-free.
 *   - esp_create, esp_load_builtin and esp_load_script may run on any thread; script compiles are serialised by one
 *     global mutex inside the plugin.
 *   - esp_poll_log, esp_build_info, esp_version, esp_set_crash_dir: any thread.
 *
 * Time and steps (PLAN 3.3 [T16], 3.4 [T1]):
 *   - dt passed to esp_step* is physics (game) time. The plugin keeps a fractional accumulator: after the command for
 *     physics step k, the engine has run round(sum(dt) * f) steps in total, f = the engine's simulation frequency,
 *     less any borrowed coast steps still to be repaid (see coast mode).
 *   - EspControls.sim_speed (Time.timeScale) never changes the step count; it only scales the synthesizer's input rate
 *     to f * sim_speed * (1 + drift trim), so slow motion keeps the sound real-time (with lower pitch).
 *   - Step-indexed results: each command carries the caller's physics step index (a monotonic int64, e.g. SimTick).
 *     esp_step_end(e, k, timeout_us, out) returns the result of step k, waiting at most timeout_us, or ESP_PENDING.
 *     The caller decides the lag L: at step k it collects step k - L (L = clamp(steps this frame, 2, 4) in Unity).
 *     Results stay available for the last ESP_RESULT_SLOTS steps; an older step returns ESP_ERR_ARG.
 *   - Every command is queued (the command ring holds ESP_COMMAND_SLOTS steps); none is ever skipped or dropped.
 *   - Coast mode: when no command has arrived for max(2 physics steps of wall time, 1.5 x the last frame time), the
 *     worker keeps stepping with the last command for up to coast_max_ms (default 300 ms) so the sound continues.
 *     Those steps are borrowed: when commands resume, they are subtracted from the step counts of the next commands
 *     (never below zero), so engine time realigns with physics time. The PD integral resets and the event is counted.
 */
#pragma once
#include <stdint.h>

#if defined(ESP_BUILDING_DLL)
#define ESP_EXPORT __declspec(dllexport)
#elif defined(ESP_STATIC)
#define ESP_EXPORT
#else
#define ESP_EXPORT __declspec(dllimport)
#endif
#ifdef __cplusplus
#define ESP_API extern "C" ESP_EXPORT
#else
#define ESP_API ESP_EXPORT
#endif
#define ESP_CALL __cdecl

#define ESP_ABI_VERSION 1

/* ---- status codes ---- */
enum {
    ESP_OK = 0,
    ESP_ERR_ARG = -1,       /* bad argument, NULL handle, too-small struct_size, unknown name, step index too old */
    ESP_ERR_COMPILE = -2,   /* script failed to compile or build; err text says why */
    ESP_ERR_STATE = -3,     /* wrong state: not loaded, already loaded, offline call while stepping, not built in */
    ESP_ERR_TIMEOUT = -4,   /* the command ring stayed full (the worker is far behind) */
    ESP_ERR_INTERNAL = -5,  /* a C++ exception or allocation failure inside the plugin */
    ESP_PENDING = 1         /* esp_step_end: the result of that step is not there yet */
};

/* ---- driveline coupling modes (PLAN 3.5, research 01 3.6) ---- */
enum EspMode {
    ESP_MODE_FREE = 0,         /* crank free (neutral, menu, Engine Bench); optional absorber load_torque_nm on the crank */
    ESP_MODE_PROXY = 1,        /* A/B: plugin integrates crank + clutch + driven-wheel proxy (M13; not in this build) */
    ESP_MODE_KINEMATIC = 2,    /* C: Unity imposes driveline speed (not in this build) */
    ESP_MODE_SLAVED_CRANK = 3, /* D: Unity owns the crank; the plugin follows crank_samples and returns T_ind (M4) */
    ESP_MODE_FOLLOW_RPM = 4    /* remote/AI and M2: dyno hold at follow_rpm (ramped across the step), sound only */
};

#define ESP_COMMAND_SLOTS 32     /* queued commands per engine */
#define ESP_RESULT_SLOTS 64      /* step results kept per engine */
#define ESP_MAX_CRANK_SAMPLES 17 /* crank speed samples per SLAVED_CRANK step (substeps + 1) */

#pragma pack(push, 1)

/* esp_create. */
typedef struct EspCreateDesc {
    uint32_t struct_size;
    int32_t audio_sample_rate;       /* AudioSettings.outputSampleRate (48000); 0 = no audio (sweeps, tests) */
    int32_t audio_ring_frames;       /* float ring, power of two; 0 = 16384 */
    int32_t audio_target_frames;     /* first fill target before frame times are known; 0 = 50 ms */
    int32_t audio_dsp_buffer_frames; /* AudioSettings.GetDSPBufferSize length (256 at Best latency); 0 = 256 */
    int32_t sim_frequency;           /* 0 = the engine's own simulation frequency (never a quality knob, PLAN 3.8) */
    int32_t fluid_steps;             /* gas substeps; 0 = 8 */
    int32_t worker_thread;           /* 1 = dedicated worker (esp_step_begin/end); 0 = steps run on the caller's thread */
    int32_t fir_max_taps;            /* impulse-response length cap at the output rate; 0 = 10000 */
    int32_t flags;                   /* ESP_CREATE_* */
} EspCreateDesc;
#define ESP_SIZEOF_CREATE_DESC 40
#define ESP_CREATE_NO_DRIFT_TRIM 0x1 /* tests: keep the synthesizer input rate at exactly f * sim_speed */
#define ESP_CREATE_NO_COAST 0x2      /* tests: never coast */

/* esp_get_info: what was loaded. */
typedef struct EspEngineInfo {
    uint32_t struct_size;
    char name[64];                   /* builtins: the fictional id; scripts: the script's name field (Dev builds only) */
    int32_t cylinders, banks, exhaust_systems, intakes, sim_frequency;
    double displacement_m3, redline_rad_s, starter_torque_nm, starter_speed_rad_s;
    double crank_inertia_kgm2;       /* output crankshaft moment of inertia (J_eff is baked by EngineBake, M4) */
    int32_t script_gear_count;
    int32_t fluid_steps;
    double script_gear_ratios[12], script_final_drive, script_tire_radius_m, script_max_clutch_nm;
    double rev_limit_rad_s;          /* the ignition module's spark-cut limit */
    int32_t ir_taps;                 /* impulse-response taps used by the first exhaust (0 = no audio) */
    int32_t source_kind;             /* 1 = builtin, 2 = script */
} EspEngineInfo;
#define ESP_SIZEOF_ENGINE_INFO 272

/* esp_set_controls: latched; copied into every following command. */
typedef struct EspControls {
    uint32_t struct_size;
    float throttle;                  /* 0..1 -> Engine::setSpeedControl */
    float clutch;                    /* 0..1 engagement (engine-sim's own transmission stays in neutral in v1) */
    int32_t ignition;                /* IgnitionModule::m_enabled */
    int32_t starter;                 /* StarterMotor::m_enabled */
    float sim_speed;                 /* Time.timeScale: scales the synthesizer input rate only */
    float frame_dt_s;                /* unscaled duration of the frame that issues the next commands (fill target, coast) */
    int32_t frame_index;             /* Time.frameCount of that frame (groups commands into frames) */
} EspControls;
#define ESP_SIZEOF_CONTROLS 32

/* esp_step / esp_step_begin: per physics step. */
typedef struct EspDrivelineIn {
    uint32_t struct_size;
    int32_t mode;                    /* EspMode */
    double ratio;                    /* gearbox-input speed / driven-wheel speed; 0 = neutral; < 0 reverse (PROXY) */
    double max_clutch_torque_nm;
    double input_inertia;            /* PROXY: kg m^2 at the gearbox input */
    double wheel_inertia;            /* PROXY: kg m^2 at the driven wheels */
    double wheel_speed;              /* PROXY/KINEMATIC: rad/s */
    int32_t resync_wheel_speed;
    int32_t tyre_model;
    double load_torque_nm;           /* FREE: absorber torque on the crank (a brake, like 02's dyno load); PROXY: tyre */
    double load_slope;
    double mf_B, mf_C, mf_D_n, mf_E, contact_speed_ms, tyre_radius_m;
    double brake_torque_nm;
    double follow_rpm;               /* FOLLOW_RPM: target at the end of this step, ramped from the previous one */
    double slave_fc_hz;              /* SLAVED_CRANK: PD corner; 0 = min(8 Hz, idle firing frequency / 4) */
    double slave_inertia_kgm2;       /* SLAVED_CRANK: J used for T_ind and the PD gains; 0 = crank inertia */
    double slave_torque_limit_nm;    /* SLAVED_CRANK: PD clamp; 0 = 4 x the engine's peak torque estimate */
} EspDrivelineIn;
#define ESP_SIZEOF_DRIVELINE_IN 160

/* esp_step / esp_step_end: the result of one physics step. Torques and speeds are forward-positive. */
typedef struct EspStepOut {
    uint32_t struct_size;
    int32_t steps;                   /* engine steps run for this command (after borrowed-step repayment) */
    int64_t step_index;              /* the command's physics step index */
    double sim_time_s;               /* engine time after this command */
    double rpm;
    double crank_speed;              /* rad/s at the end of the step */
    double indicated_torque_nm;      /* SLAVED_CRANK: J*dw/h - mean(T_pd); FOLLOW_RPM: dyno torque; FREE: J*dw/h + load */
    double dT_dw;                    /* estimate of dT_ind/domega, N m s/rad, for the lag feed-forward (PLAN 3.4) */
    double clutch_torque_nm;
    double dyno_torque_nm;           /* cycle-averaged dynamometer torque (FOLLOW_RPM and sweeps) */
    double wheel_speed;
    double wheel_force_mean_n;
    double manifold_pressure_pa;
    double intake_flow_mol_s;
    double throttle_open;            /* 0 closed .. 1 open */
    double afr;
    double fuel_used_l;
    double cpu_ms;                   /* worker CPU time spent on this command */
    int32_t audio_frames_buffered;   /* audio ring fill after this command */
    int32_t audio_underruns;         /* underrun events so far */
    int32_t rev_limited_hint;        /* 1 while the spark-cut limiter is (probably) active */
    int32_t flags;                   /* ESP_OUT_* */
} EspStepOut;
#define ESP_SIZEOF_STEP_OUT 152
#define ESP_OUT_AFTER_COAST 0x1      /* the worker coasted before this command */
#define ESP_OUT_REPAID 0x2           /* borrowed steps were subtracted from this command */
#define ESP_OUT_CAPPED 0x4           /* dt was larger than 0.25 s; the extra time was dropped */

/* esp_set_audio_params: Synthesizer::AudioParameters subset. */
typedef struct EspAudioParams {
    uint32_t struct_size;
    float volume;                    /* output gain after the leveler; default 0.5 (02 6.7 clipping) */
    float convolution;               /* impulse-response wet mix 0..1 */
    float hf_gain;                   /* dF_F_mix */
    float low_freq_noise;            /* airNoise */
    float high_freq_noise;           /* inputSampleNoise (jitter) */
    float leveler_target;            /* default 30000 */
} EspAudioParams;
#define ESP_SIZEOF_AUDIO_PARAMS 28

/* esp_get_stats (PLAN 3.3). */
typedef struct EspStats {
    uint32_t struct_size;
    int32_t sim_frequency;
    int64_t commands;                /* commands processed */
    int64_t steps_run;               /* engine steps run for commands and coasting */
    int64_t steps_target;            /* round(sum(dt) * f) */
    int64_t steps_borrowed;          /* coast steps not yet repaid */
    int64_t coast_steps;             /* all coast steps so far */
    int32_t coast_events;
    int32_t commands_pending;        /* queued and not yet processed (how far the worker is behind) */
    int64_t last_step_index;         /* newest step whose result is published */
    int32_t stale_steps;             /* esp_step_end calls that waited and still returned ESP_PENDING */
    int32_t ring_capacity_frames;
    double collect_wait_ms_total;    /* time spent waiting inside esp_step_end */
    double collect_wait_ms_max;
    double cpu_ms_ema;               /* worker CPU per command */
    double worker_ms_ema;            /* worker wall time per command */
    double rtf;                      /* simulated seconds per worker wall second, over the whole run */
    double rtf_cpu;                  /* simulated seconds per worker CPU second */
    int32_t audio_underruns;         /* events while the producer was active */
    int32_t audio_underrun_frames;
    int32_t ring_fill_frames;
    int32_t ring_target_frames;
    int64_t audio_frames_produced;
    int64_t audio_frames_consumed;
    int64_t audio_frames_dropped;    /* ring full: newest samples dropped */
    int64_t clipped_samples;
    double drift_trim;               /* current epsilon of the input-rate trim */
    double p_core_share;             /* fraction of commands run on a performance core */
    double frame_p95_ms;             /* p95 of the frame times reported in EspControls */
    double worker_frame_ms;          /* worker wall time for one frame's commands (EMA) */
    int64_t offline_steps;           /* steps run by offline calls (sweeps), outside the accounting */
} EspStats;
#define ESP_SIZEOF_STATS 208

/* esp_set_worker_options (M4 tuning; usable now). */
typedef struct EspWorkerOptions {
    uint32_t struct_size;
    int32_t priority;                /* -2..2 (THREAD_PRIORITY_LOWEST..HIGHEST); default 0 */
    int32_t power_throttling_off;    /* 1 = ask Windows not to throttle the worker (EcoQoS off) */
    int32_t coast_max_ms;            /* default 300; 0 = never coast */
    uint64_t affinity_mask;          /* 0 = leave as is */
} EspWorkerOptions;
#define ESP_SIZEOF_WORKER_OPTIONS 24

#pragma pack(pop)

/* ---- library ---- */
ESP_API int32_t ESP_CALL esp_version(void);
/* One-line JSON describing this build ({"abi":1,"git":...,"sourceHash":...,"piranha":...,"fir":"avx2"|"sse2",...}).
 * Returns the bytes written (excluding NUL). dst == NULL and capacity == 0: returns the length needed.
 * Buffer too small: ESP_ERR_ARG and "" if capacity >= 1. */
ESP_API int32_t ESP_CALL esp_build_info(char *dst_utf8, int32_t capacity);
/* Folder for the plugin's minidumps (%TEMP%\<company>\<product>\Crashes by default; GameInfo names, PLAN 1.9 [S5]).
 * Call before the first engine is created. NULL or "" restores the default. */
ESP_API int32_t ESP_CALL esp_set_crash_dir(const char *dir_utf8);
/* Moves queued log lines (warnings, errors, notes) into dst, one per line. Returns bytes written (0 = nothing).
 * Lines that do not fit stay queued. No callbacks are ever made from native threads. */
ESP_API int32_t ESP_CALL esp_poll_log(char *dst_utf8, int32_t capacity);
/* Test hook (autotest m0.crash.recover, -br-crash-test): with magic == ESP_CRASH_TEST_MAGIC it faults on a plugin
 * thread under the crash guard, which writes a minidump and lets the process crash. Any other value: ESP_ERR_ARG. */
#define ESP_CRASH_TEST_MAGIC 0x43524153 /* "CRAS" */
ESP_API int32_t ESP_CALL esp_debug_crash_test(int32_t magic);

/* ---- engines ---- */
typedef struct EspEngine EspEngine; /* opaque */

/* es_library_dir: folder with engine_sim.mr and sound-library\ (the IRs); temp_dir: scratch folder for script
 * wrappers. Returns NULL on bad arguments (see esp_poll_log). */
ESP_API EspEngine *ESP_CALL esp_create(const EspCreateDesc *desc, const char *es_library_dir_utf8, const char *temp_dir_utf8);
/* Stops the worker and frees everything. Stop the AudioSource first: the ring stays valid until this returns. */
ESP_API void ESP_CALL esp_destroy(EspEngine *e);

/* C++ engines under fictional ids (PLAN 3.10): "f4_boxer_a", "v8_cross_a", "i4_synth", "v8_bigblock_a" (from M2). */
ESP_API int32_t ESP_CALL esp_load_builtin(EspEngine *e, const char *name_utf8, char *err_utf8, int32_t err_capacity);
/* A piranha .mr engine script (builds with ESP_WITH_PIRANHA only; ESP_ERR_STATE otherwise). top_node NULL or "" =
 * call main(), else set_engine(<top_node>()) for older scripts without main. */
ESP_API int32_t ESP_CALL esp_load_script(EspEngine *e, const char *mr_path_utf8, const char *top_node_utf8,
                                         char *err_utf8, int32_t err_capacity);
ESP_API int32_t ESP_CALL esp_get_info(EspEngine *e, EspEngineInfo *out);

ESP_API int32_t ESP_CALL esp_set_controls(EspEngine *e, const EspControls *c);
ESP_API int32_t ESP_CALL esp_set_audio_params(EspEngine *e, const EspAudioParams *p);
/* Tier switch at runtime: gas substeps (never the frequency) and the IR length cap. Applied at the next command. */
ESP_API int32_t ESP_CALL esp_set_quality(EspEngine *e, int32_t fluid_steps, int32_t fir_max_taps);
ESP_API int32_t ESP_CALL esp_set_worker_options(EspEngine *e, const EspWorkerOptions *o);

/* Synchronous step on the caller's thread (worker_thread = 0, tests and tools). */
ESP_API int32_t ESP_CALL esp_step(EspEngine *e, int64_t step_index, double dt, const EspDrivelineIn *in,
                                  const double *crank_samples, int32_t n_samples, EspStepOut *out);
/* Queue the command for physics step step_index (monotonic). Never dropped; ESP_ERR_TIMEOUT only if the command ring
 * stays full for 50 ms. With worker_thread = 0 the step runs here and esp_step_end returns it at once. */
ESP_API int32_t ESP_CALL esp_step_begin(EspEngine *e, int64_t step_index, double dt, const EspDrivelineIn *in,
                                        const double *crank_samples, int32_t n_samples);
/* The result of step step_index: ESP_OK, or ESP_PENDING after waiting at most timeout_us (0 = poll). */
ESP_API int32_t ESP_CALL esp_step_end(EspEngine *e, int64_t step_index, int32_t timeout_us, EspStepOut *out);

/* Audio thread only. Lock-free. Writes frames * channels interleaved floats (mono duplicated). On underrun it holds
 * and decays the last sample over 2 ms and counts the event. Returns the frames that came from the engine. */
ESP_API int32_t ESP_CALL esp_read_audio(EspEngine *e, float *dst, int32_t frames, int32_t channels);

ESP_API int32_t ESP_CALL esp_get_stats(EspEngine *e, EspStats *out);
/* Dev builds: tee what the engine produced (before the ring) into a 16-bit WAV for `seconds` (written when done).
 * path NULL or seconds <= 0 stops and writes what was captured. */
ESP_API int32_t ESP_CALL esp_debug_tap(EspEngine *e, const char *wav_path_utf8, double seconds);

/* ---- offline tools: blocking, never while stepping (EngineBake, tests, the Engine Bench's sweep engine) ---- */
/* WOT-or-given-throttle dyno-hold sweep: per point, hold the speed for settle_s, then average the cycle-averaged
 * dyno torque over 1 s. power_w may be NULL. */
ESP_API int32_t ESP_CALL esp_dyno_sweep(EspEngine *e, double rpm_from, double rpm_to, int32_t points, float throttle,
                                        double settle_s, double *torque_nm, double *power_w);
/* Effective crank-train inertia from ignition-off ramps through 2000 rpm (research 01 3.5). */
ESP_API int32_t ESP_CALL esp_calibrate_inertia(EspEngine *e, double *j_eff_kgm2);
/* Steady dyno-hold render at rpm and throttle into dst (frames mono floats); needs audio. Returns frames written. */
ESP_API int32_t ESP_CALL esp_render_offline(EspEngine *e, double rpm, float throttle, double seconds, float *dst,
                                            int32_t frames);
/* Writes a shippable copy of an .mr file: name: fields and node identifiers rewritten to fictional_id, comments
 * stripped (PLAN 3.2 [C2]). Builds with ESP_WITH_PIRANHA only. */
ESP_API int32_t ESP_CALL esp_mr_sanitise(const char *src_path_utf8, const char *dst_path_utf8, const char *fictional_id,
                                         char *err_utf8, int32_t err_capacity);
