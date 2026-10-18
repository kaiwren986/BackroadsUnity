cmake_minimum_required(VERSION 3.20)
project(esprobe CXX)
set(CMAKE_CXX_STANDARD 17)

# Probe build: engine-sim core WITHOUT delta-studio, piranha, discord, SDL, boost.
set(ES "D:/Backroads-ref/engine-sim")
option(ES_FAST_MATH "Compile with /fp:fast /arch:AVX2" OFF)

if (MSVC)
    add_compile_options(/W1 /MP)
    if (ES_FAST_MATH)
        add_compile_options(/fp:fast /arch:AVX2)
    endif()
endif()

file(GLOB SCS_SRC "${ES}/dependencies/submodules/simple-2d-constraint-solver/src/*.cpp")
add_library(scs STATIC ${SCS_SRC})

set(CORE
    audio_buffer camshaft crankshaft combustion_chamber connecting_rod convolution_filter
    cylinder_bank cylinder_head delay_filter derivative_filter direct_throttle_linkage
    dynamometer engine exhaust_system feedback_comb_filter filter fuel function gas_system
    gaussian_filter governor ignition_module impulse_response intake jitter_filter
    leveling_filter low_pass_filter part piston piston_engine_simulator simulator
    standard_valvetrain starter_motor synthesizer throttle transmission utilities
    valvetrain vehicle vehicle_drag_constraint vtec_valvetrain)
set(CORE_SRC "")
foreach(f ${CORE})
    list(APPEND CORE_SRC "${ES}/src/${f}.cpp")
endforeach()

add_library(escore STATIC ${CORE_SRC})
# shim/ must come first: it satisfies <delta-studio/...> from include/delta.h (pulled in only by synthesizer.cpp, unused)
target_include_directories(escore PUBLIC "${CMAKE_SOURCE_DIR}/shim" "${ES}/include" "${ES}/dependencies/submodules")
target_link_libraries(escore PUBLIC scs)

add_executable(bench bench.cpp)
target_link_libraries(bench escore)

# Compile-check of the Boost-free piranha Path replacement (piranha itself still needs generated parser files)
add_library(piranha_path_shim STATIC piranha_path_std.cpp)
target_include_directories(piranha_path_shim PRIVATE "${ES}/dependencies/submodules")

# Compile-check: piranha (minus Boost path.cpp and the flex/bison-dependent ir_compilation_unit.cpp) + engine-sim-script-interpreter
set(PIR "${ES}/dependencies/submodules/piranha")
file(GLOB PIR_SRC "${PIR}/src/*.cpp")
list(REMOVE_ITEM PIR_SRC "${PIR}/src/path.cpp" "${PIR}/src/ir_compilation_unit.cpp")
add_library(piranha_check STATIC ${PIR_SRC} piranha_path_std.cpp
    "${ES}/scripting/src/channel_types.cpp" "${ES}/scripting/src/compiler.cpp"
    "${ES}/scripting/src/engine_context.cpp" "${ES}/scripting/src/language_rules.cpp")
target_include_directories(piranha_check PRIVATE "${PIR}/include" "${PIR}/dependencies/libraries/flex/include"
    "${CMAKE_SOURCE_DIR}/shim" "${ES}/include" "${ES}/dependencies/submodules")
