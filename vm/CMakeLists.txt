cmake_minimum_required(VERSION 3.20)
project(iqaloxvm CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Off for a release build (no Catch2 dependency needed just to produce the
# `iqaloxvm` binary users actually want) -- on by default so local/CI dev
# builds keep testing exactly as before.
option(IQALOX_BUILD_TESTS "Build the Catch2 test suite (tests/)" ON)

add_executable(iqaloxvm
    src/main.cpp
    src/bytecode.cpp
    src/value.cpp
    src/vm.cpp
    src/natives.cpp
)

target_include_directories(iqaloxvm PRIVATE src)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

if(MSVC)
    target_compile_options(iqaloxvm PRIVATE /W4)
else()
    target_compile_options(iqaloxvm PRIVATE -Wall -Wextra)
endif()

if(IQALOX_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
