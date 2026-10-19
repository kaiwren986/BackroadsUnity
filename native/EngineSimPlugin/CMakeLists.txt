# EngineSimPlugin: engine-sim inside Unity as one x64 DLL (PLAN 1.6, 3.1).
# Our own CMake: it lists sources from the untouched vendor tree and never add_subdirectory()s upstream (research 01 1.4).
# Build with tools\ci\build-native.ps1 (presets vs2022-x64 -> native\build, vs2022-x64-asan -> native\build-asan).
cmake_minimum_required(VERSION 3.21)
project(EngineSimPlugin LANGUAGES CXX)

if (NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "EngineSimPlugin builds with MSVC for Windows x64 only (PLAN 1.6).")
endif ()

# ---- build identity (tools\ci\build-native.ps1 passes both; esp_build_info() reports them) ----
set(ESP_SOURCE_HASH "unknown" CACHE STRING "SHA-256 over the plugin sources (set by tools/ci/build-native.ps1)")
set(ESP_GIT_COMMIT "unknown" CACHE STRING "git commit of the sources (set by tools/ci/build-native.ps1)")
foreach (v ESP_SOURCE_HASH ESP_GIT_COMMIT)
    if (NOT "${${v}}" MATCHES "^[0-9A-Za-z._-]+$")
        message(FATAL_ERROR "${v} must match [0-9A-Za-z._-]+, got '${${v}}'")
    endif ()
endforeach ()
set(ESP_VENDOR_ENGINESIM 85f7c3b)   # keep in step with vendor/VENDOR.md and tools/ci/vendor-check.ps1
set(ESP_VENDOR_SCS e009f4f)
set(ESP_VENDOR_PIRANHA 432f0b1)

option(ESP_ASAN "Build with /fsanitize=address (preset vs2022-x64-asan, for soaks)" OFF)
option(ESP_WITH_PIRANHA "Build piranha and es_script for .mr scripts (needs generated/piranha, U3c)" OFF)
option(ESP_LINK_WHOLE_CORE "Link every scs and enginesim_core object into the DLL (M0 link proof; off since T1.6 calls the core)" OFF)
if (ESP_WITH_PIRANHA AND NOT EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/generated/piranha/parser.auto.cpp")
    message(FATAL_ERROR "ESP_WITH_PIRANHA needs native/EngineSimPlugin/generated/piranha (U3c, PLAN 3.2).")
endif ()
if (ESP_WITH_PIRANHA)
    set(ESP_WITH_PIRANHA_01 1)
else ()
    set(ESP_WITH_PIRANHA_01 0)
endif ()

# ---- flags (research 01 7.1): /MT, NDEBUG always, /O2 /fp:fast, C++17 ----
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")   # static CRT in every configuration: no VC++ redistributable
add_compile_definitions(NDEBUG)                    # upstream Debug does not compile (research 02 8)
add_compile_options(/O2 /fp:fast /MP /Zi)
if (ESP_ASAN)
    add_compile_options(/fsanitize=address)
    add_link_options(/INCREMENTAL:NO)
endif ()

# Our sources: /W4, but not /permissive-: it implies /Zc:externConstexpr, which gives engine-sim's header constants
# ("extern constexpr" in units.h and constants.h) external linkage, so every TU that includes them would define them.
# C4324 (padding from alignas on the rings) is intended. Vendor headers are SYSTEM includes (/external:W0).
set(ESP_OWN_FLAGS /W4 /utf-8 /wd4324 /external:W0)

set(VENDOR_ES "${CMAKE_CURRENT_SOURCE_DIR}/vendor/engine-sim")
set(VENDOR_SCS "${VENDOR_ES}/dependencies/submodules/simple-2d-constraint-solver")
set(VENDOR_PIRANHA "${VENDOR_ES}/dependencies/submodules/piranha")

# ---- scs: simple-2d-constraint-solver, the same 29 sources as its own CMakeLists (without gtest) ----
set(SCS_SOURCES
    clutch_constraint conjugate_gradient_sle_solver constant_rotation_constraint constant_speed_motor constraint
    euler_ode_solver fixed_position_constraint force_generator gauss_seidel_sle_solver gaussian_elimination_sle_solver
    generic_rigid_body_system gravity_force_generator line_constraint link_constraint matrix nsv_ode_solver ode_solver
    optimized_nsv_rigid_body_system rigid_body rigid_body_system rk4_ode_solver rolling_constraint
    rotation_friction_constraint sle_solver sparse_matrix spring static_force_generator system_state utilities)
list(LENGTH SCS_SOURCES n)
if (NOT n EQUAL 29)
    message(FATAL_ERROR "scs: expected 29 sources, listed ${n}")
endif ()
list(TRANSFORM SCS_SOURCES PREPEND "${VENDOR_SCS}/src/")
list(TRANSFORM SCS_SOURCES APPEND ".cpp")
add_library(scs STATIC ${SCS_SOURCES})
target_include_directories(scs SYSTEM PUBLIC "${VENDOR_SCS}/include")
target_compile_options(scs PRIVATE /W1)

# ---- enginesim_core: exactly the 41 sources of upstream add_library(engine-sim ...) (CMakeLists.txt:47-141) ----
set(CORE_SOURCES
    audio_buffer camshaft crankshaft combustion_chamber connecting_rod convolution_filter cylinder_bank cylinder_head
    delay_filter derivative_filter direct_throttle_linkage dynamometer engine exhaust_system feedback_comb_filter filter
    fuel function gas_system gaussian_filter governor ignition_module impulse_response intake jitter_filter
    leveling_filter low_pass_filter part piston piston_engine_simulator simulator standard_valvetrain starter_motor
    synthesizer throttle transmission utilities valvetrain vehicle vehicle_drag_constraint vtec_valvetrain)
list(LENGTH CORE_SOURCES n)
if (NOT n EQUAL 41)
    message(FATAL_ERROR "enginesim_core: expected 41 sources, listed ${n}")
endif ()
list(TRANSFORM CORE_SOURCES PREPEND "${VENDOR_ES}/src/")
list(TRANSFORM CORE_SOURCES APPEND ".cpp")
add_library(enginesim_core STATIC ${CORE_SOURCES})
# shim/ first: it satisfies the unused <delta-studio/...> includes of include/delta.h (research 01 1.3).
target_include_directories(enginesim_core SYSTEM BEFORE PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/shim")
target_include_directories(enginesim_core SYSTEM PUBLIC "${VENDOR_ES}/include" "${VENDOR_ES}/dependencies/submodules")
target_link_libraries(enginesim_core PUBLIC scs)
target_compile_options(enginesim_core PRIVATE /W1)

# ---- piranha + es_script (PLAN 3.2): the generated scanner and parser are committed (generated/piranha), so no
# flex or bison runs here. path.cpp is replaced by our std::filesystem src/piranha_path_std.cpp (no Boost). ----
if (ESP_WITH_PIRANHA)
    set(PIRANHA_SOURCES
        assembly channel_node channel_type compilation_error compiler error_list float_conversions fundamental_types
        int_conversions ir_attribute ir_attribute_definition ir_attribute_definition_list ir_attribute_list
        ir_binary_operator ir_compilation_unit ir_context_tree ir_import_statement ir_node ir_node_definition
        ir_parser_structure ir_unary_operator ir_value language_rules memory_tracker node node_allocator node_container
        node_graph node_output node_program no_op_node standard_allocator string_conversions vector_constructor)
    list(LENGTH PIRANHA_SOURCES n)
    if (NOT n EQUAL 34)
        message(FATAL_ERROR "piranha: expected 34 sources (upstream's 35 minus path.cpp), listed ${n}")
    endif ()
    list(TRANSFORM PIRANHA_SOURCES PREPEND "${VENDOR_PIRANHA}/src/")
    list(TRANSFORM PIRANHA_SOURCES APPEND ".cpp")
    add_library(piranha STATIC ${PIRANHA_SOURCES}
        src/piranha_path_std.cpp
        generated/piranha/parser.auto.cpp
        generated/piranha/scanner.auto.cpp)
    target_include_directories(piranha SYSTEM PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/generated/piranha"
        "${VENDOR_PIRANHA}/dependencies/libraries/flex/include"
        "${VENDOR_PIRANHA}/include"
        "${VENDOR_ES}/dependencies/submodules")
    target_compile_options(piranha PRIVATE /W1 /wd4996)

    add_library(es_script STATIC
        "${VENDOR_ES}/scripting/src/channel_types.cpp"
        "${VENDOR_ES}/scripting/src/compiler.cpp"
        "${VENDOR_ES}/scripting/src/engine_context.cpp"
        "${VENDOR_ES}/scripting/src/language_rules.cpp")
    target_include_directories(es_script SYSTEM PUBLIC "${VENDOR_ES}")  # #include "scripting/include/..." (piranha has a compiler.h too)
    target_compile_definitions(es_script PUBLIC ATG_ENGINE_SIM_PIRANHA_ENABLED)
    target_link_libraries(es_script PUBLIC piranha enginesim_core)
    target_compile_options(es_script PRIVATE /W1)
endif ()

# ---- esp_internal: the plugin implementation (everything but the exported entry points) ----
configure_file(cmake/esp_build_config.h.in "${CMAKE_CURRENT_BINARY_DIR}/generated/esp_build_config.h" @ONLY)
set(ESP_INTERNAL_SOURCES
    src/audio_render.cpp src/crash_guard.cpp src/esp_log.cpp src/fir_avx2.cpp src/fir_sse2.cpp src/instance.cpp
    src/offline.cpp src/platform.cpp src/resample.cpp src/unity_sim.cpp src/wav_io.cpp src/worker.cpp
    src/builders/builders.cpp src/builders/f4_boxer_a.cpp src/builders/i4_synth.cpp src/builders/p01_generic.cpp
    src/builders/v8_cross_a.cpp)
if (ESP_WITH_PIRANHA)
    list(APPEND ESP_INTERNAL_SOURCES src/script_loader.cpp src/mr_sanitise.cpp)
endif ()
add_library(esp_internal STATIC ${ESP_INTERNAL_SOURCES})
# /arch:AVX2 on the AVX2 kernel only; the dispatch in fir_sse2.cpp calls it after a CPUID check (01 7.1).
set_source_files_properties(src/fir_avx2.cpp PROPERTIES COMPILE_OPTIONS "/arch:AVX2")
target_include_directories(esp_internal PUBLIC src "${CMAKE_CURRENT_BINARY_DIR}/generated")
target_compile_definitions(esp_internal PUBLIC ESP_BUILDING_DLL WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(esp_internal PRIVATE ${ESP_OWN_FLAGS})
target_link_libraries(esp_internal PUBLIC enginesim_core dbghelp)
if (ESP_WITH_PIRANHA)
    target_link_libraries(esp_internal PUBLIC es_script)
endif ()

# ---- EngineSimPlugin.dll: the C ABI (src/EngineSimPlugin.h) ----
add_library(EngineSimPlugin SHARED src/EngineSimPlugin.h src/esp_api.cpp)
target_compile_options(EngineSimPlugin PRIVATE ${ESP_OWN_FLAGS})
target_link_libraries(EngineSimPlugin PRIVATE esp_internal)
target_link_options(EngineSimPlugin PRIVATE /DEBUG /OPT:REF /OPT:ICF)
if (ESP_LINK_WHOLE_CORE)
    target_link_options(EngineSimPlugin PRIVATE
        "/WHOLEARCHIVE:$<TARGET_FILE:enginesim_core>" "/WHOLEARCHIVE:$<TARGET_FILE:scs>")
endif ()

# ---- tools: the harness (public ABI only), the co-simulation (also internals), esp_cli ----
add_library(esp_harness STATIC
    tools/harness/harness.cpp tools/harness/gates.cpp tools/harness/allengines.cpp)
target_include_directories(esp_harness PUBLIC src tools)
target_compile_definitions(esp_harness PUBLIC WIN32_LEAN_AND_MEAN NOMINMAX "ESP_VENDOR_DIR=\"${VENDOR_ES}\"")
target_compile_options(esp_harness PRIVATE ${ESP_OWN_FLAGS})
target_link_libraries(esp_harness PUBLIC EngineSimPlugin winmm psapi)

add_library(esp_cosim STATIC tools/harness/cosim.cpp)
target_compile_options(esp_cosim PRIVATE ${ESP_OWN_FLAGS})
target_link_libraries(esp_cosim PUBLIC esp_harness esp_internal)

add_executable(esp_cli tools/esp_cli.cpp)
target_compile_options(esp_cli PRIVATE ${ESP_OWN_FLAGS})
target_link_libraries(esp_cli PRIVATE esp_harness esp_cosim)
target_link_options(esp_cli PRIVATE /DEBUG)

# ---- tests (ctest). Labels: none = quick (merge tier), heavy = CPU-heavy reports (machine.lock), soak = ASan soak ----
enable_testing()
set(ESP_REPORTS "${CMAKE_CURRENT_BINARY_DIR}/reports")
file(MAKE_DIRECTORY "${ESP_REPORTS}")

foreach (t esp_smoke esp_crash_guard)
    add_executable(${t} tests/${t}.cpp)
    target_include_directories(${t} PRIVATE src)   # constants and types only; the DLL is loaded with LoadLibrary
    target_compile_definitions(${t} PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(${t} PRIVATE ${ESP_OWN_FLAGS})
    target_link_options(${t} PRIVATE /DEBUG)
    add_dependencies(${t} EngineSimPlugin)
endforeach ()
foreach (t esp_abi esp_ring_stress)
    add_executable(${t} tests/${t}.cpp)
    target_compile_options(${t} PRIVATE ${ESP_OWN_FLAGS})
    target_link_libraries(${t} PRIVATE esp_harness)
    target_link_options(${t} PRIVATE /DEBUG)
endforeach ()
target_include_directories(esp_ring_stress PRIVATE src)
add_executable(esp_dsp tests/esp_dsp.cpp)
target_compile_options(esp_dsp PRIVATE ${ESP_OWN_FLAGS})
target_link_libraries(esp_dsp PRIVATE esp_internal)
target_link_options(esp_dsp PRIVATE /DEBUG)

if (ESP_ASAN)
    # The ASan runtime DLL sits next to cl.exe; copy it next to the test executables and the plugin.
    get_filename_component(_cl_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
    set(_asan_dll "${_cl_dir}/clang_rt.asan_dynamic-x86_64.dll")
    if (NOT EXISTS "${_asan_dll}")
        message(FATAL_ERROR "ASan runtime not found: ${_asan_dll}")
    endif ()
    add_custom_command(TARGET esp_smoke POST_BUILD
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${_asan_dll}" "$<TARGET_FILE_DIR:esp_smoke>")
endif ()

# esp_smoke: the DLL loads and esp_version() == 1 (M0 headline gate).
add_test(NAME esp_smoke COMMAND esp_smoke "$<TARGET_FILE:EngineSimPlugin>")
# esp_crash_guard: a fault on a plugin thread leaves a minidump. Not under ASan, which reports the fault itself.
if (NOT ESP_ASAN)
    add_test(NAME esp_crash_guard
        COMMAND esp_crash_guard "$<TARGET_FILE:EngineSimPlugin>" "${CMAKE_CURRENT_BINARY_DIR}/crash_test")
endif ()
add_test(NAME esp_abi COMMAND esp_abi)
add_test(NAME esp_dsp COMMAND esp_dsp "${VENDOR_ES}/es" "${CMAKE_CURRENT_BINARY_DIR}")
add_test(NAME esp_ring_stress COMMAND esp_ring_stress)
# M1 headline gates (PLAN 6.1 M1)
add_test(NAME esp_start COMMAND esp_cli start f4_boxer_a --json "${ESP_REPORTS}/esp_start.json")
add_test(NAME esp_dyno COMMAND esp_cli sweep f4_boxer_a --gate --json "${ESP_REPORTS}/esp_dyno.json")
add_test(NAME esp_follow COMMAND esp_cli follow f4_boxer_a --json "${ESP_REPORTS}/esp_follow.json")
add_test(NAME esp_step_accounting
    COMMAND esp_cli accounting --timescale 1 --seconds 600 --json "${ESP_REPORTS}/esp_step_accounting.json")
add_test(NAME esp_step_accounting_ts05
    COMMAND esp_cli accounting --timescale 0.5 --seconds 600 --json "${ESP_REPORTS}/esp_step_accounting_ts05.json")
# M1 lane gates
add_test(NAME esp_pitch COMMAND esp_cli pitch f4_boxer_a --wav "${ESP_REPORTS}/esp_pitch_f4_boxer_a"
    --json "${ESP_REPORTS}/esp_pitch.json")
add_test(NAME esp_cosim_matrix COMMAND esp_cli cosim --traces "${CMAKE_CURRENT_SOURCE_DIR}/tests/traces" --threads 8
    --json "${ESP_REPORTS}/esp_cosim_matrix.json")
add_test(NAME esp_bench_ultra_e COMMAND esp_cli bench f4_boxer_a --affinity E --quality ultra --seconds 20
    --json "${ESP_REPORTS}/esp_bench_ultra_e.json")
add_test(NAME esp_bench_ultra_p COMMAND esp_cli bench f4_boxer_a --affinity P --quality ultra --seconds 20
    --json "${ESP_REPORTS}/esp_bench_ultra_p.json")
set_tests_properties(esp_cosim_matrix esp_bench_ultra_e esp_bench_ultra_p PROPERTIES LABELS heavy)
set_tests_properties(esp_cosim_matrix PROPERTIES TIMEOUT 900)
if (ESP_WITH_PIRANHA)
    add_test(NAME esp_all_engines COMMAND esp_cli all-engines --threads 8 --json "${ESP_REPORTS}/esp_all_engines.json")
    set_tests_properties(esp_all_engines PROPERTIES LABELS heavy TIMEOUT 1200)
    add_test(NAME esp_mr_sanitise COMMAND esp_cli sanitise-all --threads 8 --json "${ESP_REPORTS}/esp_mr_sanitise.json")
endif ()
# Soaks, real time (PLAN 6.1 M1 lane gate): under ASan, engine-sim runs about 10x slower, so the ASan soak uses the
# light i4_synth configuration that still keeps real time there; the release soak runs f4_boxer_a at full quality.
if (ESP_ASAN)
    add_test(NAME esp_soak COMMAND esp_cli soak i4_synth --minutes 10 --freq 5000 --fluid 2 --taps 256
        --json "${ESP_REPORTS}/esp_soak_asan.json")
    set_tests_properties(esp_soak PROPERTIES LABELS soak TIMEOUT 900)
else ()
    add_test(NAME esp_soak_release COMMAND esp_cli soak f4_boxer_a --minutes 10 --json "${ESP_REPORTS}/esp_soak_release.json")
    set_tests_properties(esp_soak_release PROPERTIES LABELS heavy TIMEOUT 900)
endif ()
set_tests_properties(esp_step_accounting esp_step_accounting_ts05 esp_pitch PROPERTIES TIMEOUT 600)
set_property(DIRECTORY PROPERTY TESTS_TIMEOUT 300)
