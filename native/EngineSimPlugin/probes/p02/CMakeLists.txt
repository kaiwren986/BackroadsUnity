# Headless build of the engine-sim simulation core (no delta-studio, no piranha,
# no gtest download). Sources are compiled straight out of the untouched
# D:/Backroads-ref/engine-sim tree; the only patched file is synthesizer.cpp
# (copy in ./patched) which drops a stray #include of delta-studio headers.
cmake_minimum_required(VERSION 3.20)
project(enginesim_headless_probe LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(ES_ROOT "D:/Backroads-ref/engine-sim" CACHE PATH "engine-sim checkout (read-only)")
set(SCS_ROOT "${ES_ROOT}/dependencies/submodules/simple-2d-constraint-solver")
set(CSV_ROOT "${ES_ROOT}/dependencies/submodules/csv-io")

option(PROBE_FAST_MATH "Build with /fp:fast /arch:AVX2 (lets MSVC vectorise the convolution reverb)" OFF)
if (MSVC)
    add_compile_options(/W1 /MP)
    if (PROBE_FAST_MATH)
        add_compile_options(/fp:fast /arch:AVX2)
        add_compile_definitions(PROBE_FAST_MATH_BUILD)
    endif ()
endif ()

# ---- simple-2d-constraint-solver (same source list as its own CMakeLists) ----
file(GLOB SCS_SOURCES "${SCS_ROOT}/src/*.cpp")
add_library(simple-2d-constraint-solver STATIC ${SCS_SOURCES})
target_include_directories(simple-2d-constraint-solver PUBLIC "${SCS_ROOT}/include")

# ---- csv-io (linked by upstream engine-sim target; not actually used by the core) ----
add_library(csv-io STATIC "${CSV_ROOT}/src/information.cpp" "${CSV_ROOT}/src/csv_data.cpp")
target_compile_definitions(csv-io PRIVATE PROJECT_NAMESPACE=atg_csv)
target_include_directories(csv-io PUBLIC "${CSV_ROOT}/include")

# ---- engine-sim core: identical list to upstream add_library(engine-sim ...) ----
set(ES_CORE_SOURCES
    audio_buffer camshaft crankshaft combustion_chamber connecting_rod
    convolution_filter cylinder_bank cylinder_head delay_filter derivative_filter
    direct_throttle_linkage dynamometer engine exhaust_system feedback_comb_filter
    filter fuel function gas_system gaussian_filter governor ignition_module
    impulse_response intake jitter_filter leveling_filter low_pass_filter part
    piston piston_engine_simulator simulator standard_valvetrain starter_motor
    throttle transmission utilities valvetrain vehicle vehicle_drag_constraint
    vtec_valvetrain)
set(ES_CORE_FILES "${CMAKE_CURRENT_SOURCE_DIR}/patched/synthesizer.cpp")
foreach (s ${ES_CORE_SOURCES})
    list(APPEND ES_CORE_FILES "${ES_ROOT}/src/${s}.cpp")
endforeach ()

add_library(engine-sim STATIC ${ES_CORE_FILES})
target_include_directories(engine-sim PUBLIC "${ES_ROOT}/include" "${ES_ROOT}/dependencies/submodules")
target_link_libraries(engine-sim PUBLIC simple-2d-constraint-solver csv-io)

# ---- headless probe ----
add_executable(probe src/probe.cpp src/ej25.cpp src/wav.cpp)
target_link_libraries(probe PRIVATE engine-sim)
target_compile_definitions(probe PRIVATE ES_ROOT_DIR="${ES_ROOT}")
