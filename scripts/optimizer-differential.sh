#!/usr/bin/env bash
# Differential test for the Phase 7 optimizer (docs/PLAN-0.3.md): every
# program must behave identically -- stdout, stderr (runtime errors and
# their [file, line N]) and exit status -- whether compiled with or without
# --no-optimize. Covers langspec/examples, bench/, a deterministic corpus of
# randomly generated constant expressions, and hand-written error cases.
#
# Usage: scripts/optimizer-differential.sh [vm-binary] [iqaloxc-command...]
set -u
VM="${1:-vm/build-rel/iqaloxvm}"
if [ $# -gt 1 ]; then shift; CC=("$@"); else CC=(dotnet compiler/src/bin/Release/net10.0/Iqaloxc.dll); fi
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail=0; checked=0

check() { # src
  local src="$1" a b
  "${CC[@]}" "$src" "$TMP/o.iqbc" >/dev/null 2>&1; local ca=$?
  "${CC[@]}" --no-optimize "$src" "$TMP/n.iqbc" >/dev/null 2>&1; local cb=$?
  if [ $ca -ne $cb ]; then echo "FAIL (compile status differs): $src"; fail=1; return; fi
  [ $ca -ne 0 ] && return
  a=$(timeout 120 "$VM" "$TMP/o.iqbc" 2>&1; echo "exit=$?")
  b=$(timeout 120 "$VM" "$TMP/n.iqbc" 2>&1; echo "exit=$?")
  checked=$((checked+1))
  if [ "$a" != "$b" ]; then echo "FAIL (behavior differs): $src"; diff <(echo "$a") <(echo "$b") | head -5; fail=1; fi
}

for f in langspec/examples/*.iqx langspec/examples/modules/*.iqx bench/micro/*.iqx bench/realistic/*.iqx; do
  [ -f "$f" ] && check "$f"
done

# Hand-written cases: errors must keep their messages and lines.
mkdir -p "$TMP/cases"
# An integer zero divisor is a compile-time error since docs/PLAN-0.4.md
# Phase 3, so these exercise a float zero (IEEE) and a runtime type error.
printf 'var a = 0.0\nprint 1\nprint 10 / a\nprint 10 %% a\n' > "$TMP/cases/floatzero.iqx"
printf 'var a = 2.5\nprint 1\nprint 10 // a\n' > "$TMP/cases/divtype.iqx"
printf 'fun f() {\n    return limit\n}\nprint f()\nvar limit = 5\n' > "$TMP/cases/undefined_before.iqx"
printf 'var base = 2\nvar out = [1,\n    base + 1,\n    "x" - base]\n' > "$TMP/cases/multiline.iqx"
printf 'var debug = false\nfun f() {\n    debug ? print "dbg" : nil\n    return 1\n    print "never"\n}\nprint f()\n' > "$TMP/cases/deadcode.iqx"
for f in "$TMP"/cases/*.iqx; do check "$f"; done

# Random constant expressions (fixed seeds so failures reproduce). A runtime
# error stops a program early, so besides 12-statement programs every
# expression is also run alone.
python3 - "$TMP/fuzz" <<'PY'
import random, sys, os
random.seed(20260101)
out = sys.argv[1]; os.makedirs(out)
ops = ['+','-','*','/','%','^','<','<=','>','>=','==','!=','and','or','??',
       '//','&','|','~','<<','>>']
# Integers and floats side by side (docs/PLAN-0.4.md Phase 1), including an
# int just past 2^53 and one past 64 bits, where folding must defer to the VM.
atoms = ['0','1','2','3','7','0.5','100','true','false','nil','"a"','"b"',
         '2.0','.25','1e300','9007199254740993','18446744073709551616']
def expr(d):
    if d == 0 or random.random() < 0.25: return random.choice(atoms)
    r = random.random()
    if r < 0.1: return '(!' + expr(d-1) + ')'
    if r < 0.15: return '(-' + expr(d-1) + ')'
    if r < 0.18: return '(~' + expr(d-1) + ')'
    if r < 0.2: return '((' + expr(d-1) + ') ? (' + expr(d-1) + ') : (' + expr(d-1) + '))'
    op = random.choice(ops)
    return '(' + expr(d-1) + ' ' + op + ' ' + (divisor(d-1) if op in divs else expr(d-1)) + ')'
# Since docs/PLAN-0.4.md Phase 3 a divisor (or `^` operand pair) the
# compiler can't prove non-zero is a compile-time error in both modes, so
# half the time the right operand is a constant it can.
divs = {'/', '//', '%', '^'}
def divisor(d):
    if random.random() < 0.5: return random.choice(['1','2','3','7','0.5','100','2.0','.25','1e300'])
    return expr(d)
for i in range(60):
    with open(f"{out}/f{i}.iqx", "w") as fh:
        for j in range(12):
            fh.write(f"var c{j} = {expr(3)}\nprint c{j}\n")
for i in range(150):
    with open(f"{out}/g{i}.iqx", "w") as fh:
        fh.write(f"print {expr(3)}\n")
PY
for f in "$TMP"/fuzz/*.iqx; do check "$f"; done

echo "checked $checked programs"
exit $fail
