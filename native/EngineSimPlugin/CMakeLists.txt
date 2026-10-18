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
option(ESP_LINK_WHOLE_CORE "Link every scs and enginesim_core object into the DLL (M0 link proof until T1.6 calls the core)" ON)
if (ESP_WITH_PIRANHA)
    message(FATAL_ERROR "ESP_WITH_PIRANHA needs native/EngineSimPlugin/generated/piranha (U3c, PLAN 3.2); not in M0.")
endif ()
set(ESP_WITH_PIRANHA_01 0)

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

set(VENDOR_ES "${CMAKE_CURRENT_SOURCE_DIR}/vendor/engine-sim")
set(VENDOR_SCS "${VENDOR_ES}/dependencies/submodules/simple-2d-constraint-solver")

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
target_include_directories(scs PUBLIC "${VENDOR_SCS}/include")
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
target_include_directories(enginesim_core BEFORE PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/shim")
target_include_directories(enginesim_core PUBLIC "${VENDOR_ES}/include" "${VENDOR_ES}/dependencies/submodules")
target_link_libraries(enginesim_core PUBLIC scs)
target_compile_options(enginesim_core PRIVATE /W1)

# ---- EngineSimPlugin.dll: the C ABI (src/EngineSimPlugin.h) ----
configure_file(cmake/esp_build_config.h.in "${CMAKE_CURRENT_BINARY_DIR}/generated/esp_build_config.h" @ONLY)
add_library(EngineSimPlugin SHARED
    src/EngineSimPlugin.h
    src/esp_api.cpp
    src/crash_guard.h
    src/crash_guard.cpp)
target_include_directories(EngineSimPlugin PRIVATE src "${CMAKE_CURRENT_BINARY_DIR}/generated")
target_compile_definitions(EngineSimPlugin PRIVATE ESP_BUILDING_DLL WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(EngineSimPlugin PRIVATE /W4 /permissive- /utf-8)
target_link_libraries(EngineSimPlugin PRIVATE enginesim_core dbghelp)
target_link_options(EngineSimPlugin PRIVATE /DEBUG /OPT:REF /OPT:ICF)
if (ESP_LINK_WHOLE_CORE)
    # Nothing calls the core yet, so without this the linker would drop it. With it, all 41 + 29 objects must link
    # into the DLL with no unresolved symbol. T1.6 turns it off once the plugin uses the core.
    target_link_options(EngineSimPlugin PRIVATE
        "/WHOLEARCHIVE:$<TARGET_FILE:enginesim_core>" "/WHOLEARCHIVE:$<TARGET_FILE:scs>")
endif ()

# ---- tests (ctest) ----
enable_testing()
foreach (t esp_smoke esp_crash_guard)
    add_executable(${t} tests/${t}.cpp)
    target_include_directories(${t} PRIVATE src)   # constants and types only; the DLL is loaded with LoadLibrary
    target_compile_definitions(${t} PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(${t} PRIVATE /W4 /permissive- /utf-8)
    target_link_options(${t} PRIVATE /DEBUG)
    add_dependencies(${t} EngineSimPlugin)
endforeach ()

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
set_property(DIRECTORY PROPERTY TESTS_TIMEOUT 120)
