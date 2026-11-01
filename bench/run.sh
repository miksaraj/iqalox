#!/usr/bin/env bash
# Benchmark harness (docs/PLAN-0.3.md Phase 7).
#
# Usage: bench/run.sh [runs] [vm-binary] [iqaloxc-command...]
#
# For every program under bench/micro and bench/realistic, compiles it twice
# (default, and with --no-optimize once that flag exists), checks both print
# the same output, and reports bytecode size plus median/min/max wall time
# over N runs of the VM. Build the VM in Release first:
#   cmake -S vm -B vm/build-rel -G Ninja -DCMAKE_BUILD_TYPE=Release -DIQALOX_BUILD_TESTS=OFF
set -u
RUNS="${1:-10}"
VM="${2:-vm/build-rel/iqaloxvm}"
if [ $# -gt 2 ]; then shift 2; CC=("$@"); else CC=(dotnet compiler/src/bin/Release/net10.0/Iqaloxc.dll); fi
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

timeit() { # prints seconds
  local s e
  s=$(date +%s.%N); "$VM" "$1" >/dev/null 2>&1; e=$(date +%s.%N)
  echo "$e - $s" | bc
}

printf "%-14s %-9s %8s %8s %8s %8s\n" program mode bytes median min max
for src in bench/micro/*.iqx bench/realistic/*.iqx; do
  name=$(basename "$src" .iqx)
  for mode in optimized baseline; do
    flag=""; [ "$mode" = baseline ] && flag="--no-optimize"
    # shellcheck disable=SC2086
    "${CC[@]}" $flag "$src" "$OUT/$name.$mode.iqbc" 2>/dev/null || { echo "$name $mode: compile failed"; continue; }
  done
  [ -f "$OUT/$name.baseline.iqbc" ] || cp "$OUT/$name.optimized.iqbc" "$OUT/$name.baseline.iqbc"
  a=$("$VM" "$OUT/$name.optimized.iqbc" 2>&1); b=$("$VM" "$OUT/$name.baseline.iqbc" 2>&1)
  [ "$a" = "$b" ] || echo "!! $name: OUTPUT DIFFERS between modes"
  for mode in baseline optimized; do
    times=(); for _ in $(seq "$RUNS"); do times+=("$(timeit "$OUT/$name.$mode.iqbc")"); done
    sorted=($(printf '%s\n' "${times[@]}" | sort -n))
    printf "%-14s %-9s %8d %8.3f %8.3f %8.3f\n" "$name" "$mode" \
      "$(stat -c %s "$OUT/$name.$mode.iqbc")" "${sorted[$((RUNS/2))]}" "${sorted[0]}" "${sorted[$((RUNS-1))]}"
  done
done
