# iqaloxvm

The `0.1` bytecode VM backend, in C++23. Loads and executes the bytecode
format `compiler/` emits (format v1 -- see `src/bytecode.hpp` for the exact
layout, mirroring `compiler/src/Bytecode.fs`'s own doc comment). See
`docs/PLAN-0.1.md` for the full plan.

A stack-based interpreter (`src/vm.hpp`/`.cpp`) over a tagged-union `Value`
(`src/value.hpp`/`.cpp`) and a small heap-object hierarchy -- strings,
vectors, functions, closures, upvalues, native functions, classes,
instances, bound methods (`src/object.hpp`) -- collected by a mark-sweep
tracing garbage collector built into `Vm` itself. Covers every
`0.1-poc`-equivalent expression/statement, functions and closures, a
native standard library (`src/natives.hpp`/`.cpp`: `print`, `concat`)
defined as globals before any program runs, and classes/inheritance/
`self`/`super` with full dynamic dispatch -- see `docs/PLAN-0.1.md`'s
Phase 8 entry for the calling-convention details.

## Build

```
cmake -S . -B build -DCMAKE_BUILD_TYPE=Debug
cmake --build build -j
```

Needs CMake 3.24+ and a C++23 compiler. Tests also need Catch2 3 (off with
`-DIQALOX_BUILD_TESTS=OFF`).

### Third-party dependencies

Both are linked statically, so `iqaloxvm` needs nothing installed to run.
`build/third-party-licenses/` collects their license texts, which ship in
release bundles.

- **ICU 78.3** (Unicode case mapping, character properties, regex; from
  `0.4`, `docs/PLAN-0.4.md` decisions 21 and 28). `cmake/IqaloxIcu.cmake`
  downloads ICU's source tarball the first time you configure, checks it
  against a pinned SHA-256, and builds it with CMake. It uses no ICU data
  file (about 32 MB); everything Iqalox uses runs on tables compiled into ICU
  itself. `tests/test_dependencies.cpp` pins down what that does and doesn't
  cover. To build offline, extract the tarball yourself and configure with
  `-DFETCHCONTENT_SOURCE_DIR_ICU=<the extracted icu/ directory>`.
- **mini-gmp 6.3.0** (big integers), vendored in `third_party/mini-gmp/`.
  See `README.iqalox.md` there.

`build/iqaloxvm --version` reports both versions.

## Test

```
cd build && ctest --output-on-failure
```

## Run

```
build/iqaloxvm <bytecode file>
```
