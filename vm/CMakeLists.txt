cmake_minimum_required(VERSION 3.24)
project(iqaloxvm C CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Off for a release build (no Catch2 dependency needed just to produce the
# `iqaloxvm` binary users actually want) -- on by default so local/CI dev
# builds keep testing exactly as before.
option(IQALOX_BUILD_TESTS "Build the Catch2 test suite (tests/)" ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# Source files (tests especially) contain UTF-8 string literals; MSVC
# otherwise reads source in the system code page.
add_compile_options($<$<CXX_COMPILER_ID:MSVC>:/utf-8>)

# --- Third-party dependencies (docs/PLAN-0.4.md, Phase 0) -------------------

# mini-gmp: arbitrary-precision integers, vendored (third_party/mini-gmp/README.iqalox.md).
add_library(iqalox_mini_gmp STATIC third_party/mini-gmp/mini-gmp.c)
target_include_directories(iqalox_mini_gmp SYSTEM PUBLIC third_party/mini-gmp)
target_compile_definitions(iqalox_mini_gmp INTERFACE IQALOX_MINI_GMP_VERSION="6.3.0")
if(MSVC)
    target_compile_options(iqalox_mini_gmp PRIVATE /W0)
else()
    target_compile_options(iqalox_mini_gmp PRIVATE -w)
endif()

# ICU: Unicode case mapping, character properties and regex, built from a
# pinned source tarball (cmake/IqaloxIcu.cmake).
list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")
include(IqaloxIcu)

# Both are linked statically into iqaloxvm, so their license texts must ship
# with it. Collected into one build-tree directory that the release workflow
# copies into the bundle as-is.
set(IQALOX_LICENSES_DIR "${CMAKE_BINARY_DIR}/third-party-licenses")
configure_file("${IQALOX_ICU_LICENSE_FILE}" "${IQALOX_LICENSES_DIR}/ICU-LICENSE.txt" COPYONLY)
foreach(license COPYING.LESSERv3 COPYINGv2 COPYINGv3)
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/third_party/mini-gmp/${license}"
                   "${IQALOX_LICENSES_DIR}/mini-gmp-${license}.txt" COPYONLY)
endforeach()

# --- The VM ------------------------------------------------------------------

# Everything but main(), shared by iqaloxvm and the test suite.
add_library(iqaloxvm_core STATIC
    src/bytecode.cpp
    src/value.cpp
    src/vm.cpp
    src/natives.cpp
    src/numeric.cpp
    src/dependencies.cpp
)
target_include_directories(iqaloxvm_core PUBLIC src)
target_link_libraries(iqaloxvm_core PUBLIC Iqalox::ICU iqalox_mini_gmp)

add_executable(iqaloxvm src/main.cpp)
target_link_libraries(iqaloxvm PRIVATE iqaloxvm_core)

foreach(target iqaloxvm_core iqaloxvm)
    if(MSVC)
        target_compile_options(${target} PRIVATE /W4)
    else()
        target_compile_options(${target} PRIVATE -Wall -Wextra)
    endif()
endforeach()

if(IQALOX_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
