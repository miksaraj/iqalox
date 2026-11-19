# Plan: getting to 0.4

This is the `0.4` counterpart to `docs/PLAN-0.3.md`: a living plan for the
next feature set on top of `0.3`'s compiler frontend (F#, `compiler/`) and
bytecode VM backend (C++23, `vm/`). Like the earlier plans, this document
tracks resolved decisions, flags open ones rather than guessing at them
(per `CLAUDE.md`), and keeps a running status table and sequencing
checklist as work lands.

`0.1-poc` (`poc/`) is **not** getting a `0.4` either. It stays frozen, as it
has since `0.1` (`CLAUDE.md`).

## 0. Scope: what "0.4" means here

Per `ROADMAP.md`'s `0.4` entry (formerly `0.3`, bumped by the `0.2-poc`
renumbering): two language-level models first, then standard library part
1 of 5. The models come first because the libraries are built on them, and
because `1.0` is the stability line: anything that changes what an
existing program means has to ship before it.

- **Numeric model**: a separate, exact, arbitrary-precision **integer**
  type, distinct from floats, with integer division (`//`) and bitwise
  operators. Deliberately a breaking change.
- **String model**: strings are sequences of Unicode **code points**;
  `s[i]` is a one-character string (no `char` type), `length` counts code
  points, and every library position uses the same unit; `ord`/`chr`
  convert between a character and its code.
- **String manipulation standard library**: concatenation, search, split,
  format, regex.
- **Mathematical standard library**: arithmetic, trigonometry, logarithms,
  random numbers, rounding (`floor`/`round` return integers).
- **Second set of compiler optimizations.**

`ROADMAP.md` deliberately left the numeric details open ("how integer vs.
float literals are told apart, what `/` does on two integers, mixed
integer/float arithmetic and equality, printing, and how vector indices
and `length` fit in"). All of them, and every other design question this
version raised, are settled in §1.

## 1. Design decisions (resolved)

Resolved via thirteen `AskUserQuestion` rounds with the repository owner
before this document's first draft. The owner asked for every open
decision to be put to them, however small, so this section records
outright choices rather than "proposed, not separately confirmed"
defaults. The few items that still rest on a proposal are listed in §2.
Two answers changed the plan's shape and needed their own follow-up rounds:

- **Division by zero is a compile-time error.** This wasn't one of the
  offered options. A follow-up round settled what counts as proof (decision 12).
- **`1 == 1.0` is false**, while mixed ordering promotes to float. This
  opened a consistency question that was asked and accepted (decision 7).

### Numeric model

1. **A literal with a decimal point is a float; one without is an
   integer.** `5` is an int; `5.0`, `3.14`, `.5` and `1.` are floats. The
   owner added the leading-dot and trailing-dot forms to the offered option.
   Every literal in existing programs keeps its spelling but changes type
   if it has no decimal point. This is the intended breaking change.
2. **Extra literal forms: hex/binary/octal, digit separators, exponents.**
   `0xFF`, `0b1010`, `0o17` are integers; `1_000_000` and `0xFF_FF` ignore
   their `_`s; `1e9`, `2.5e-3`, `6.02E23` are **always floats**, even with no
   decimal point. The owner chose the **permissive** fine print over a strict
   one:
   - uppercase prefixes `0X`/`0B`/`0O` are accepted;
   - hex digits may be either case, and the exponent marker may be `e` or `E`;
   - `_` may follow a prefix directly (`0x_FF`) and may repeat (`1__000`);
   - `1.e5` is accepted.

   §2 item 1 covers the two `_` placements the answer didn't cover.
3. **`/` always produces a float.** `7 / 2 == 3.5`, `6 / 2 == 3.0`. The
   result type depends only on the operator, never on the values (Python 3).
4. **`//` is floor division, integers only.** `-7 // 2 == -4`, which pairs
   with the existing floored `%` (`-7 % 2 == 1`) so `(a // b) * b + a % b ==
   a` holds. A float operand is a runtime error ("Operands of '//' must be
   integers."); floats use `floor (a / b)`. `%` keeps its current floored
   semantics and returns an int for two ints and a float otherwise.
5. **`^` on two ints is an exact int when the exponent is non-negative,
   otherwise a float.** `2 ^ 100` is exact and `2 ^ -1 == 0.5` (Python). Any
   float operand gives a float.
6. **Mixed int/float arithmetic and ordering promote to float.**
   `1 + 2.5 == 3.5` and `3 < 3.5` is true. An int whose magnitude doesn't
   fit a double raises a runtime error when promoted, whether by `float x`
   or by mixing (`2 ^ 2000 + 0.5`): "Integer too large to convert to
   float." Silently saturating to `inf` was rejected. *Refined at Phase 1
   start (decisions 31-32):* ordering doesn't promote after all, it
   compares exactly; and `/` and a negative `^` on two integers compute
   exactly before rounding once.
7. **`==` compares type and value, so `1 == 1.0` is false**, the same rule
   that already makes `1 == "1"` false. This also fixes `0.5`'s dictionary
   keys: `d[1]` and `d[1.0]` are different keys. **The consequence was asked
   about and accepted**: since ordering compares numeric value (decision 6;
   exactly, per decision 31), `1 <= 1.0` and `1 >= 1.0` are both true while
   `1 == 1.0` is false. This is documented as a
   known quirk of the design, not something to fix. Two related choices:
   - **Floats follow plain IEEE-754.** `nan == nan` is false, every ordering
     involving `nan` is false (test with `std.math.isNan`), and
     `0.0 == -0.0` is true.
   - **Strings compare by code-point sequence**, with no Unicode
     normalisation. This follows from decision 14's model rather than being
     asked separately.
8. **A float always prints with a decimal point.** `print 5` → `5`,
   `print 5.0` → `5.0`, `print 7 / 7` → `1.0`, so the type is visible in
   output (Python). This replaces the "drop a trailing `.0`" rule `0.1`
   inherited from `poc`. It applies everywhere stringification does: inside
   printed vectors, `concat`, `str` and `format`'s `{}`. `-0.0` prints as
   `-0.0`, and `inf`, `-inf` and `nan` print as those words, as today.
   Integers print in full decimal, however large. The decimal point holds
   in scientific notation too (decision 30): `1.0e+16` where Python prints
   `1e+16`.
9. **Vector (and string) indices and slice bounds must be integers.**
   `v[1.0]` is a runtime error ("Vector index must be an integer, got float."),
   even when the float is whole. This catches a `/` result used as an index
   by accident. `length` returns an int.
10. **Bitwise operators are symbols, Lua-style.**
    - The operators are `a & b`, `a | b`, `a ~ b` (xor), `~a` (bitwise not),
      `a << n` and `a >> n`. `^` stays power; `~` is xor when binary and
      bitwise not when unary.
    - **`|` inside a vector literal**: no bare `|` at the top level of a
      vector literal is bitwise OR. That covers any element, generator
      source, guard or cons tail, where `|` keeps its `0.2`
      cons/comprehension meaning; a bitwise OR there needs parentheses:
      `[(a | b), c]`. Parentheses and index/slice brackets (`v[i | 1]`)
      are their own context. Everywhere else `|` is bitwise OR. *Clarified
      at Phase 2 start:* this draft originally said "a vector literal's
      head", which disagreed with the wording the owner had approved
      ("inside a vector literal"); asked again, the owner chose the
      whole-top-level rule over a position-dependent one.
    - **Precedence is Python-like**. From tightest to loosest: unary
      (`! - ~`), multiplicative (`* / // % ^`), additive (`+ -`), shifts
      (`<< >>`), `&`, `~` (xor), `|`, then comparisons. So `x & 1 == 0` means
      `(x & 1) == 0`, avoiding C's well-known pitfall.
    - **Semantics are infinite two's complement** (Python): `~5 == -6`,
      `-8 >> 1 == -4`, `-1 & 0xFF == 255`, for ints of any size.
    - Any float operand is a runtime error, and so is a negative shift count.
11. **Explicit conversions and type tests live in `std.math`.**
    - `int x` truncates toward zero (`int 3.7 == 3`, `int -3.7 == -3`). It
      is an error on `inf`/`nan`, and on a string (use `parseInt`).
    - `float x` converts an int to a float.
    - `isInt x` / `isFloat x` are the only type tests. No general `typeOf`
      is added: Iqalox still has no reflection.
    - `print` stays the only ambient name (`0.3` Phase 6), so none of
      these is ambient.
12. **Division by zero is a compile-time error. A divisor must be proven
    non-zero, or guarded.** The owner's own answer (all three offered
    options made it a runtime error), refined in a follow-up round. Every
    `/`, `//` and `%` is accepted only if its divisor is:
    - **a non-zero constant**, after `Optimizer.fs`-style constant folding
      and global constant propagation, so `n / 2`, `n % width` with
      `var width = 10`, and `n / (4 - 2)` are fine; or
    - **an immutable binding narrowed by an enclosing guard.** The binding
      is an immutable local, parameter or top-level `var`. The guards that
      count are:
      - `d != 0`, and `d == 0` (which narrows the *other* branch);
      - any ordering comparison against a constant that excludes zero
        (`d > 0`, `d >= 1`, `d < -2`, `d <= -1`);
      - combinations of these through `and`, `or` and `!`.

      A guard narrows the then/else branches of a ternary or elvis, and the
      right-hand operand of `and`/`or`: `d != 0 ? n / d : 0`,
      `d == 0 ? 0 : n / d` and `d > 0 and n / d` are all accepted.

    Anything else is rejected with a compile-time error saying how to fix
    it. That covers a mutable variable, a call such as `n / length v`, an
    arbitrary expression, and an unguarded parameter, so `(x) -> 10 / x`
    must become `(x) -> x != 0 ? 10 / x : 0`. The fix is to bind the
    divisor to an immutable `var` and guard it.

    **A float zero divisor follows IEEE and never errors.** `0.0 != 0` is
    true under decision 7, so a `d != 0` guard can't exclude a float zero.
    The follow-up round closed the gap this way: dividing by `0.0` (or
    `-0.0`) gives `inf`/`-inf`/`nan`. Only an **integer** zero divisor is an
    error, and that is exactly what `d != 0` rules out. Ordering guards
    (`d > 0`) exclude both zeros anyway.

    **A constant float zero divisor (`x / 0.0`) is a compile-time
    warning**, not an error: "division by a constant 0.0 always gives inf
    or nan". It's well defined but almost always a mistake, and
    `std.math.inf` exists for intent.

    `vm/` keeps its runtime "Division by zero." check as a backstop for
    bytecode not produced by `iqaloxc`. It is unreachable from compiled
    source.

### Decided at Phase 1 start

Asked when Phase 1 began, alongside §2 items 1 and 3 (resolved below).
These are the details of how exact integers meet floats that decisions
1-9 didn't reach.

30. **Scientific notation keeps the decimal point: `1.0e+16`**, not
    Python's `1e+16` (Ruby's form). It applies to `1.0e-05`, and leaves
    `1.5e+300` as is.
31. **Ordering between an integer and a float is exact**, as in Python. It
    neither rounds the integer nor raises "too large":
    `2 ^ 53 + 1 > 9007199254740992.0` is true, and `2 ^ 2000 < 1.5` is
    false. Arithmetic still promotes (decision 6).
32. **`/` on two integers is computed exactly and rounded once** (Python),
    so `(2 ^ 2000) / (2 ^ 1999) == 2.0`; only a result beyond the float
    range is an error. **Integer `^` with a negative exponent works the
    same way**: `(2 ^ 2000) ^ -1 == 0.0`, never "too large to convert".
33. **A float literal beyond the double range is a compile-time error**
    ("Float literal is too large.", as in Go, Java and Swift). One below
    it, like `1e-400`, is `0.0`.
34. **Exact integers have a size limit: 2^26 bits** (about 20 million
    digits). A larger result is a runtime error ("Integer result too
    large."), checked before computing wherever the size is predictable
    (`*`, `^`). Without it, `2 ^ 10000000000` would exhaust memory and
    abort the VM with no Iqalox error. The same limit applies to integer
    literals at compile time. It can be raised later without breaking
    programs.
35. **Integer `0 ^ -1` gets the same prove-or-guard compile-time rule as
    division** (§2 item 3, resolved). That follows exact-integer
    languages (Python, Ruby, Scheme, Haskell's `^`, JS `BigInt`) rather
    than float-only ones (JS `Number`, Lua, C `pow`), which give `inf`.
    - A `^` is accepted if its base is a non-zero constant (`2 ^ n`) or
      guarded (`x != 0`), or its exponent is a non-negative constant
      (`x ^ 2`) or guarded (`n >= 0`).
    - Only `x ^ n`, with both unknown, needs a guard.
    - A float `0.0` base gives `inf` under the IEEE rule, so the
      `x != 0` guard stays sound.
    - Implemented in Phase 3.

### Decided at Phase 3 start

Asked when Phase 3 began. These settle what decision 12's "constant" and
"guard" reach.

36. **An immutable local with a constant initializer is a constant
    divisor**, like a top-level one: `var size = 10` inside a function
    makes `n % size` fine without a guard.
37. **An imported immutable constant is a constant divisor too.** With
    `pub var size = 4` in `dep`, `import dep.size` makes `n / size` fine
    in the importer, and `pub var size = 0` is a compile-time error there.
38. **`d == c` for a non-zero constant `c` narrows `d`** in its true
    branch: `d == 5 ? n / d : 0` is accepted. (`d != c` narrows nothing.)
39. **A constant divisor that isn't a number compiles** (`1 / "a"`,
    `1 / nil`). The check is only about zero; the VM's runtime type error
    ("Operands must be numbers.") reports the rest, as before.

### String model

13. **Strings are immutable.** Index assignment on a string (`s[0] = "x"`)
    is a runtime error, and a compile-time one where the target is visibly
    a string literal. New strings come from library functions. Immutability
    also keeps strings safe as `0.5` dictionary keys.
14. **Strings index exactly like vectors.** The roadmap fixed the core: a
    string is a sequence of code points, `s[i]` is a one-character string,
    and `length` counts code points. On top of that, the owner chose:
    - `s[-1]` is the last character;
    - `s[a:b]` is an end-inclusive slice returning a new string, with the
      same omitted-bound, clamping and empty-when-inverted rules as vector
      slices (`docs/PLAN-0.3.md` decision 3);
    - an out-of-range `s[i]` is the same kind of runtime error as for a
      vector.
15. **Strings are iterable.** A comprehension generator over a string binds
    each one-character string in turn (`[c | c <- s]`), and spread splits a
    string into characters (`[..."abc"] == ["a", "b", "c"]`). A
    scanner-style loop needs no conversion step.
16. **`<`, `<=`, `>`, `>=` on two strings compare lexicographically by code
    point.** `"Z" < "a"`, `"ab" < "abc"`. The comparison is
    locale-independent. Ordering a string against a number stays a runtime
    error. `std.vector.sort` now sorts strings.
17. **Unicode escapes `\u{...}` and `\x..`.** `\u{1F600}` takes 1-6 hex
    digits in braces (Rust / modern JavaScript) and covers every code
    point. `\x41` takes exactly 2 hex digits and must be ≤ `0x7F`. A
    surrogate or a value above `0x10FFFF` in `\u{...}` is a compile-time
    scan error.
18. **Raw string literals: `r"..."` / `r'...'`.** Backslashes are literal
    and no escapes are processed, so a regex reads `r"\d+\.\d+"`. A raw
    string can't contain its own quote character. **Whitespace decides
    between a raw string and a call**, as `0.2` decision 6 already does for
    `v[i]` vs `f [i]`:
    - `r"x"` (no space) is a raw string;
    - `r "x"` remains a call to a function named `r`.
19. **Source files are UTF-8; identifiers stay ASCII.**
    `[A-Za-z_][A-Za-z0-9_]*` as today. Non-ASCII characters may appear in
    strings and comments only, which avoids normalisation and
    confusable-character questions.
20. **Still no string `+`.** `"a" + "b"` stays a runtime error, keeping the
    `0.1-poc` design. Concatenation is `concat [a, b]` (stringifies each
    element) plus the new `join sep, v`.
21. **Case mapping is full Unicode, via ICU.** `upper "straße" ==
    "STRASSE"`, `upper "äö" == "ÄÖ"`. This makes ICU a new third-party
    dependency of `vm/` and of every release build; see §7 for that risk.
    Because ICU is linked anyway, it also provides the regex engine
    (decision 28) and the character classification for `isLetter` and
    friends.

### Standard library

22. **Conventions shared by all three new modules**:
    - **camelCase** for multi-word names (`startsWith`, `parseInt`,
      `maxOf`), matching existing example code (`ifEqualOr`, `checkPin`).
    - **The subject comes first** in every string and regex function
      (`split s, ","`, `match s, pattern`).
    - **`nil` means "not found" or "didn't parse"** (`find`, `search`,
      `match`, `parseInt`, ...). It works with `??` (`parseInt s ?? 0`). It
      also tests correctly as a condition: `0` is truthy in Iqalox, so a
      `-1` convention would break `find s, "x" ? ...` at index 0, while
      `nil` doesn't.
23. **One native, several modules: same-origin imports never conflict.**
    `length`, `reverse` and `concat` become polymorphic (vectors and
    strings, or for `concat` a vector of anything). Each is **one** native
    exported by both `std.vector` and `std.string`. Importing it through
    both modules is not an "already declared" error: the no-silent-shadowing
    rule is about one name meaning two *different* things, and here both
    imports name the same binding. Importing two *different* things under
    one name stays an error. **`concat` stays in `std.vector` and is also
    exported from `std.string`.** The owner chose this over moving it, so
    no existing import breaks. The new `join` lives only in `std.string`.
24. **`std.string`**, approved as proposed:

    | Group | Functions |
    |---|---|
    | Shared with `std.vector` | `length s`, `reverse s`, `concat v` |
    | Building | `join sep, v`, `repeat s, n`, `str x` (stringify like `print`), `format tpl, values`, `padStart s, width, fill`, `padEnd s, width, fill` |
    | Searching (code-point positions, `nil` when absent) | `find s, sub`, `findLast s, sub`, `contains s, sub`, `startsWith s, p`, `endsWith s, p`, `count s, sub` (non-overlapping) |
    | Transforming | `replace s, old, new` (all occurrences, literal), `split s, sep` (`sep == ""` is an error; use `[...s]`), `lines s`, `trim s`, `trimStart s`, `trimEnd s`, `upper s`, `lower s` |
    | Characters | `ord c`, `chr n`, `isDigit c`, `isLetter c`, `isWhitespace c`, `isUpper c`, `isLower c` |
    | Parsing (`nil` on failure) | `parseInt s`, `parseFloat s`: same syntax as literals, including `0x`/`0b`/`0o` prefixes and `_` separators |

    `ord` on a string that isn't exactly one character, and `chr` of a
    surrogate or of a value above `0x10FFFF`, are runtime errors. Character
    classification (`isLetter`, ...) and trimming use Unicode properties via
    ICU.
25. **`format` uses `{}` placeholders with Python-style specs.** Values are
    passed as a vector, since there are no variadics:
    `format "{} + {} = {:.2f}", [a, b, c]`. The template syntax is:
    - `{}` takes the next value, and `{0}` takes one by index;
    - specs follow a colon (`{:>5}`, `{:05d}`, `{:.2f}`);
    - `{{` and `}}` are literal braces.

    Too few or too many values is a runtime error. The exact spec subset is
    §2 item 2. String interpolation syntax in literals was considered and
    not chosen.
26. **`std.math`**, approved as proposed:

    | Group | Functions |
    |---|---|
    | Types & conversion | `int x`, `float x`, `isInt x`, `isFloat x` (decision 11) |
    | Basic | `abs x`, `sign x`, `clamp x, lo, hi`, `min a, b`, `max a, b`, `minOf v`, `maxOf v` (empty vector: runtime error) |
    | Rounding (return ints; `inf`/`nan` is an error) | `floor x`, `ceil x`, `round x`, `trunc x` |
    | Powers & logs (return floats) | `sqrt x`, `exp x`, `log x` (natural), `log2 x`, `log10 x` |
    | Trigonometry (radians) | `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2 y, x` |
    | Integer | `gcd a, b`, `lcm a, b`, `isqrt n`, `modPow b, e, m` |
    | Float specials | `isNan x`, `isInf x` |
    | Constants | `pi`, `e`, `tau`, `inf`, `nan` |
    | Random | `random()` (float in `[0, 1)`), `randomInt lo, hi` (int in `[lo, hi]`, inclusive), `seed n` |

    Three behaviours here were separate questions:
    - **`round` breaks ties half to even** (banker's rounding, Python 3 and
      the IEEE default): `round 2.5 == 2`, `round 3.5 == 4`. The owner chose
      this over half-away-from-zero.
    - **`min`/`max` come in both forms**: two arguments, and a vector form
      (`minOf`/`maxOf`).
    - **The random generator is seeded from entropy**, so each run
      differs, and `seed n` makes it repeatable. The algorithm is pinned so
      the same seed gives the same sequence on every platform (§2 item 4).
27. **`std.regex` is its own module**, separate from `std.string`, so the
    plain-substring `replace`/`split` and their regex counterparts need no
    suffixed names. Approved as proposed:
    - `match s, pattern`: the first match, or `nil`.
    - `fullMatch s, pattern`: a match only if the whole string matches,
      else `nil`.
    - `matchAll s, pattern`: a vector of match vectors (`[]` if none).
    - `search s, pattern`: the code-point index of the first match, or
      `nil`.
    - `test s, pattern`: `true`/`false`.
    - `replace s, pattern, replacement`: replaces every match. `$1`, `$2`
      refer to groups and `$$` is a literal `$`.
    - `split s, pattern`: a vector of the pieces.
    - `escape s`: a pattern that matches `s` literally.

    A **match** is a vector `[whole, group1, group2, ...]`, and a group that
    didn't participate is `nil`. Named groups are legal in patterns but
    reachable only by index until `0.5` adds maps. Flags are inline only
    (`(?i)`, `(?m)`, `(?s)`). An invalid pattern is a runtime error naming
    the position. Compiled patterns are cached inside the VM; there is no
    regex object type.
28. **Regex syntax is ICU's (Perl/Java-like)**: `\d \w (?:...)
    (?<name>...)`, lookaround, and Unicode properties such as `\p{L}`. It
    reuses the ICU engine decision 21 already links. All positions are
    reported in code points, converted from ICU's UTF-16 indices, to agree
    with string indexing. RE2 (linear time, but another dependency and no
    lookaround) and ECMAScript `std::regex` (not code-point-aware, slow)
    were the alternatives.

### Compiler optimizations

29. **The second set is loop-invariant code motion plus strength
    reduction**, measured with `0.3` Phase 7's harness (`bench/run.sh`,
    `scripts/optimizer-differential.sh`), with behaviour unchanged.
    - **LICM** hoists pure expressions over immutable bindings out of `for`
      loops. Iqalox's immutable-by-default `var` makes far more expressions
      provably invariant than in most dynamic languages.
    - **Strength reduction** replaces costly operations with cheaper ones
      where the new numeric model keeps the result identical (`x ^ 2` →
      `x * x`, ...).

    Common subexpression elimination was not chosen this time. Neither was
    pulling `1.0`'s globals-as-slots VM work forward; it stays a `1.0` item.

## 2. Open questions (flagged, not decided)

Small items not fully covered by the answers above. Each will be put to
the repository owner when its phase starts, the way `0.3`'s open question
1 was settled at Phase 6, not silently resolved.

1. **RESOLVED at Phase 1 start.** A trailing `_` (`1_`) and a `_` next to
   the decimal point (`1_.5`, `1._5`) are both compile-time scan errors, as
   proposed. Phase 1 also treats a `_` next to the exponent marker (`1_e5`,
   `1e_5`) the same way, since it's the same case. A leading `_` (`_1`) is
   necessarily an identifier, as before.
2. **The exact `format` spec subset.** Proposed:
   `[[fill]align][sign][0][width][.precision][type]`, with
   - aligns `< > ^`;
   - signs `+ - space`;
   - types `d` (int), `f` / `e` / `g` / `%` (float), `x` / `X` / `b` / `o`
     (int bases) and `s`.

   A type that doesn't match the value is a runtime error (`{:d}` on a
   float, say). Python's thousands-grouping `,` and `_` are left out unless
   wanted. To confirm at Phase 6 start.
3. **RESOLVED at Phase 1 start: decision 35.** The owner asked what other
   languages do before deciding, and chose a compile-time prove-or-guard
   rule like division's over the `inf` originally proposed here.
4. **Engineering choices, recorded here, not design questions**
   (`CLAUDE.md`: engineering is fair game):
   - **Arbitrary-precision integers**: an `int64_t` fast path held inline
     in `Value`, overflowing into a heap `ObjBigInt` backed by a
     **vendored mini-gmp** (single-file C, LGPL-3/GPL-2 dual-licensed and
     GPLv3-compatible). This avoids a second system dependency next to
     ICU.
   - **String representation**: UTF-8 storage plus an "all ASCII" flag,
     which gives O(1) indexing for the common case, and a lazily built
     code-point offset table for non-ASCII strings.
   - **RNG**: a pinned algorithm (xoshiro256\*\* or PCG64) with a
     hand-written uniform mapping, since `std::uniform_*_distribution` is
     not specified identically across standard libraries.
   - **Regex backtracking**: patterns run with ICU's `uregex_setTimeLimit`
     guard.
   - **ICU linking per platform**: settled in Phase 0. ICU is built from
     source with CMake and linked statically on all three platforms, with
     no data file (see Phase 0 in §5).
5. **Three regex features need ICU data that Phase 0's build leaves out:**
   - `\X` (grapheme clusters, which needs break iteration);
   - `\N{LATIN SMALL LETTER A}` (character names);
   - the emoji and layout properties (`\p{Emoji}`, `\p{InPC}`).

   They currently fail to compile as patterns, and
   `vm/tests/test_dependencies.cpp` pins that. Decision 28 chose ICU's
   syntax but didn't say whether every ICU feature must work, so this is
   to be put to the owner at Phase 7 start:
   - **Leave them out** and document them as unsupported. This is the
     current state and costs nothing.
   - **Add them back** by building a small custom data package with only
     the needed pieces, rather than ICU's 32 MB data file. Character
     names are about 340 KB, emoji properties about 16 KB, and `\X`
     needs break iteration switched back on plus its rules data.

## 3. Grammar and architecture additions (overview)

- **Scanner (`Scanner.fs`)**:
  - new numeric literal forms (decisions 1-2): an `IntLiteral` carrying a
    `bigint` next to today's `NumberLiteral`, which becomes the float
    literal;
  - leading-dot floats (`.5`), which only apply where a `.` can't be
    member access, i.e. when the previous token can't end an expression;
  - new tokens `//`, `&`, `~`, `<<`, `>>`, reusing `VerticalBar` for `|`;
  - `\u{...}`/`\x..` escapes;
  - `r"..."` raw strings, using the no-space rule.

  Disambiguation needs a real check during implementation, as `0.3`'s
  slice syntax did:
  - `1.` vs `1..`/`1.method`: there are no methods on numbers, so `1.` is
    always a float;
  - `...5` spread vs `..` `.5`: longest match keeps `...` a token;
  - `<<` vs `<-` / `<#`;
  - `//` vs `/`.
- **Parser (`Parser.fs`)**:
  - four new precedence levels between comparison and additive (decision
    10), plus `~` as a unary operator;
  - the vector-literal head keeps a top-level `|` as cons/comprehension,
    so an unparenthesised `|` inside a vector head is never bitwise OR.
- **Resolver (`Resolver.fs`)**:
  - the **division-by-zero narrowing pass** (decision 12). This is a new,
    flow-sensitive "known non-zero" fact set carried through ternary,
    elvis, `and`/`or` and `!`, keyed by immutable binding. It reuses
    Optimizer.fs's constant evaluation for the "non-zero constant" case.
    It landed as a Resolver.fs extension (Phase 3), so `--no-optimize`
    gives the same diagnostics;
  - the same-origin import rule (decision 23);
  - the compile-time error for index-assigning a string literal.
- **Bytecode (`Bytecode.fs`), format v4**:
  - an `IntConstant` (small) and `BigIntConstant` (sign plus magnitude
    bytes) next to today's `NumberConstant`, which stays as the float
    constant;
  - new opcodes `IntDivide`, `BitAnd`, `BitOr`, `BitXor`, `BitNot`,
    `ShiftLeft`, `ShiftRight`.

  Strings keep their UTF-8 constant encoding.
- **VM (`vm/`)**:
  - `Value` gains an `int64_t` alternative;
  - `ObjBigInt` (mini-gmp);
  - every arithmetic and comparison opcode follows decisions 3-10;
  - `stringify` follows decision 8;
  - `GetIndex`/`GetSlice` accept strings (decision 14);
  - the generator and spread opcodes accept strings (decision 15);
  - `SetIndex` rejects strings (decision 13);
  - ICU-backed natives for `std.string`/`std.regex`; plain C++ natives
    for `std.math`.
- **Standard library (`Stdlib.fs`, `vm/src/natives.cpp`)**: three new
  built-in modules. Natives may be listed under more than one module
  (decision 23). Any Iqalox-source half must pass decision 12's check
  itself.
- **Optimizer (`Optimizer.fs`)**:
  - constant folding learns the new numeric semantics in Phase 1. Ints
    fold exactly, and `1 == 1.0` folds to `false`;
  - LICM and strength reduction are added in Phase 8.

## 4. Feature checklist (parity target)

Every row starts "not started" and is ticked off as `compiler/`+`vm/`
land it, verified by new tests (§6).

| Feature | Design decision(s) | Status |
|---|---|---|
| Integer type: literals, exact arbitrary precision, `/`, `^`, mixed promotion, `==`, printing | §1.1-9, §1.30-34 | Done |
| Hex/binary/octal, separators, exponent literals | §1.2 | Done |
| Integer-only indices and slice bounds; `length` returns int | §1.9 | Done |
| `//` integer division | §1.4 | Done |
| Bitwise operators `& \| ~ << >>` | §1.10 | Done |
| Compile-time division-by-zero check (incl. `0 ^ -1`); IEEE float zero | §1.12, §1.35-39 | Done |
| Code-point strings: indexing, negative, slices, immutability | §1.13-14 | Not started |
| String iteration (generators, spread) and ordering | §1.15-16 | Not started |
| `\u{...}` / `\x..` escapes; raw strings `r"..."` | §1.17-18 | Not started |
| `std.math` | §1.11, §1.26 | Not started |
| `std.string` (incl. `format`, shared natives) | §1.20-25 | Not started |
| `std.regex` | §1.27-28 | Not started |
| Second set of compiler optimizations (LICM, strength reduction) | §1.29 | Not started |

## 5. Suggested sequencing

A proposed order, not a commitment; reorder freely. The numeric model comes
first because nearly everything later depends on it (indices, `length`,
`std.math`, constant folding). The new ICU dependency is de-risked in its
own phase before any feature relies on it.

**Phase 0 — Dependency plumbing — done.** No language change. Verified
on Linux, macOS (Apple clang, libc++) and Windows (MSVC) by CI on PR #48:
the full VM suite passes on all three, and the release-configured binary
passes the smoke test on all three.

*mini-gmp* is vendored unmodified from GMP 6.3.0 in
`vm/third_party/mini-gmp/`. `README.iqalox.md` there records the file
hashes and the license choice (LGPL v3+).

*ICU 78.3* (Unicode 17) comes in through `vm/cmake/IqaloxIcu.cmake`:
- `FetchContent` downloads upstream's release tarball, as mirrored by the
  Ubuntu archive, and refuses it unless its SHA-256 matches the pinned
  one.
- CMake builds ICU's `common` and `i18n` libraries as two static
  libraries, listing the sources from ICU's own per-library
  `sources.txt`.
- **One build path for all three platforms.** ICU's own build is
  autotools on Unix and Visual Studio projects on Windows; driving it
  from CMake avoids that split. The release binary needs no ICU installed
  on the user's machine.

The data question, which this phase existed to answer, came out simpler
than planned:
- **ICU's 32 MB data file isn't trimmed; it isn't linked at all.** ICU's
  empty `stubdata` library stands in for it.
- ICU compiles its general character properties, full case mapping
  (including one-to-many mappings such as `ß` → `SS`), property names,
  bidi and NFC tables into its code. That is everything decisions 21, 24
  and 28 need.
- The rest is switched off with ICU's `UCONFIG_NO_*` switches: legacy
  converters, formatting, collation, transliteration, break iteration,
  IDNA, services and file-based data loading. Most of ICU then compiles
  to nothing.
- Three regex features need data and are left out for now (§2 item 5).

`vm/tests/test_dependencies.cpp` (9 cases, 77 assertions) pins this
configuration down. It covers:
- mini-gmp: exact arithmetic past 64 bits, detecting when a result fits
  back in 64 bits, floored division, and infinite two's-complement
  bitwise operations;
- ICU case mapping under the root locale;
- character classes;
- regex: Unicode properties, scripts, lookahead and full case folding
  under `(?i)`;
- match positions converted from UTF-8 byte offsets to code points across
  non-BMP characters;
- the three unsupported patterns failing.

The plan called for a throwaway test; it is kept instead, so an ICU or
mini-gmp upgrade can't change behaviour silently.

`iqaloxvm --version` reports both library versions. It is the cheapest
way to prove the libraries are linked into the shipped binary itself, not
only into the test suite.

Measured on Linux unless noted:

| Measurement | Result |
|---|---|
| ICU build time (8 jobs) | ~25 s; a whole CI `vm/` job takes 1-4 min |
| Release `iqaloxvm` size | 192 KB → 479 KB (only `--version` uses ICU so far); 448 KiB on macOS, 704 KiB on Windows |
| Stripped binary using regex + case mapping + character properties | ~1.4 MB, the realistic size once Phases 6-7 land |
| ICU data file the stub approach avoids | 32 MB |

Verified:
- every `langspec/examples/*.iqx` gives byte-identical output and exit
  codes on `main`'s VM and on the new Debug and Release VMs;
- the full Catch2 suite passes under GCC 13 and Clang 18 locally, and in
  CI on all three platforms;
- a clean configure downloads and verifies the tarball.

Build, CI and release changes:
- `vm/CMakeLists.txt` now builds an `iqaloxvm_core` library that both
  `iqaloxvm` and the tests link, instead of the tests recompiling
  `src/*.cpp`.
- It compiles with `/utf-8` on MSVC (UTF-8 literals in the tests) and
  needs CMake 3.24.
- It collects ICU's and mini-gmp's license texts into
  `third-party-licenses/` in the build directory. `release.yml` ships
  that directory, which static linking requires.
- CI's `vm/` job is now a Linux/macOS/Windows matrix, with Catch2 from
  apt, Homebrew and vcpkg.
- A new `vm-release` matrix job builds `iqaloxvm` exactly as `release.yml`
  does and runs `scripts/vm-release-smoke-test.sh`, which `release.yml`
  also runs. The script checks:
  - the binary reports the pinned versions;
  - ICU and GMP are not shared-library dependencies (`ldd`/`otool`);
  - the license files are present.
- No new actions were added; every `uses:` is one already SHA-pinned in
  the repo.

**Phase 1 — The integer type — done.** Decisions 1-3, 5-9 and 30-35.
§2 items 1 and 3 were put to the owner first. Four more questions came
up while settling them and became decisions 30-34: how scientific floats
print, exact mixed comparison, exact-then-round `/` and negative `^`,
`1e400`, and the size limit.

*Compiler.*
- **Scanner.** `Scanner.fs` scans every literal form, with permissive `_`
  rules and one targeted error per bad literal (`Invalid digit 'G' in
  hexadecimal literal.`). A `.5` float needs a non-expression character
  before it, so `x.5` stays member access.
- **Literals through the pipeline.** `Token.Literal` and
  `Ast.LiteralValue` split into `IntLiteral`/`IntValue` (a `bigint`) and
  `FloatLiteral`/`FloatValue`, renamed from `Number*`; `Bytecode.Constant`
  gains `IntConstant`.
- **Format v4.** Bytecode now encodes an integer as an i64, or as a sign
  plus little-endian magnitude when it doesn't fit.
- **Compiler-generated numbers are ints.** `++`/`--` add the integer `1`,
  and comprehension desugaring starts its index at the integer `0`.
- **Constant folding** (`Optimizer.fs`) follows the new semantics exactly.
  - Integers fold exactly, and `==` checks type (so `1 == 1.0` folds to
    `false`).
  - A float or mixed case folds only when every integer involved converts
    to a double exactly (at most 2^53). Larger ones are left to the VM,
    which rounds big integers itself.
  - `%` and `^` are still never folded. (Since Phase 3 a constant
    `0 ^ -1` is a compile-time error.)

*VM.*
- **`Value`** gains an inline `int64_t`, and a new `ObjBigInt` holds
  anything larger. The split is canonical: an `ObjBigInt` never holds a
  value that fits in 64 bits, so equality and hashing never need to
  compare across the two forms.
- **All arithmetic rules live in the new `vm/src/numeric.cpp`**,
  computed on a plain RAII `BigInt` the GC never sees. Only the final
  result becomes a heap object, through one allocation, so no
  intermediate value can be collected mid-operation.
- **Correct rounding.** Exact-then-round (`/`, negative `^`, big integer
  to float) uses one routine that rounds a ratio of big integers to the
  nearest double. It handles subnormal results with a single rounding;
  tests pin ties, subnormals and overflow.
- **Exact comparison** compares an integer against a float's integer part
  exactly, then lets the fraction break a tie.
- **Portable int64 conversion.** int64 values cross into mini-gmp through
  `mpz_import`/`mpz_export`, because its `long`-based setters are only
  32 bits on Windows.

*A pre-existing GC bug, found and fixed.* `natives.cpp`'s `buildMatrix`
(from `0.2`) allocated each row while the result vector wasn't reachable
from any GC root. A collection triggered by a row allocation could free
the half-built result. Matrix arithmetic can now allocate big integers
too, which makes that far more likely.
- **The fix** is a new `Vm::TempRoot` guard. It keeps a native's
  in-progress values reachable, here the result vector and the running
  sum in `multiply`.
- **Regression test.** A stress-GC test multiplies big integer matrices.
  It fails under AddressSanitizer with a heap-use-after-free when the old
  unrooted code is put back, and passes with the fix.

*A performance trap, found and fixed.* The first version pushed every
arithmetic result through a small inline `try`/`catch` helper inside
`Vm::run()`.
- **Symptom.** Benchmarks ran 30-60% slower than on `main`, although
  callgrind counted only 6-13% more instructions.
- **Cause.** Exception landing pads in the dispatch loop cost register
  allocation for the whole loop.
- **Fix.** Every slow path moved into one out-of-line function,
  `numericSlowPath`, the only place with a `try`. `run()` keeps inline
  fast paths for two int64s (overflow-checked) and two doubles.
- **Result.** Measured interleaved and pinned to one CPU, 9 runs each,
  every benchmark is now within noise of `main` or faster:

  | Benchmarks | Change vs `main` |
  |---|---|
  | `loop_global`, `loop_local` | -9% to -12% (int `%` is cheaper than `fmod`) |
  | `closures`, `fib`, `methods` | +2% to +4% |
  | `collatz` | +8% (see below) |

*Benchmark corpus.*
- **`collatz.iqx`** halves with `/`, which now gives a float. `==` never
  equates a float with an int, so the old loop would never end, and
  integer division `//` doesn't exist until Phase 2. For this phase it
  runs on floats explicitly (exact: every value stays below 2^53).
  **Phase 2 must switch it back to integers with `//`.**
- **`timeconv.iqx`** runs as is; its checksum is now a float, which is
  correct under decision 3. Phase 2 should move it to `//` as well.
- No other example or standard-library source divides or uses a float
  literal. Every `langspec/examples/*.iqx` therefore prints exactly what
  it did on `main`, since an integer prints like the whole float it
  replaces.

*Verification.*
- **Tests.**
  - xUnit: 356, up from 338. New cases cover every literal form and
    error, folding under the new semantics, and both v4 constant
    encodings.
  - Catch2: 136, up from 125. A new `test_numeric.cpp` covers overflow
    into a big integer and normalisation back; floored `%`; correctly
    rounded `/`, including ties and subnormals; every `^` case; the size
    limit; exact mixed ordering; and type-strict equality, integer-only
    indices and the stress-GC test.
- **Sanitizers.** The full VM suite is clean under AddressSanitizer and
  UndefinedBehaviorSanitizer.
- **Optimizer differential.** `scripts/optimizer-differential.sh` passes
  on 244 programs. Its fuzz atoms gained `2.0`, `.25`, `1e300`, 2^53 + 1
  and 2^64, and unary minus, so folding is exercised exactly where it
  must defer to the VM.
- **Example.** A new `langspec/examples/numbers.iqx` covers the phase end
  to end.
- **Docs.** `docs/LANGUAGE.md` §2, §3, §5, §8, §9, §13 and §15 (new item
  13), `langspec/SYNTAX_GRAMMAR.md` (now titled `0.4`) and the root
  README's lexical grammar are updated.

*Known limitation, documented (`docs/LANGUAGE.md` §15 item 13).*
mini-gmp has only quadratic multiplication.
- Squaring a 1.6-million-bit integer takes about 2.3 s (Python: 0.17 s).
- Printing a 47,713-digit number takes about 0.6 s.
- Integers that fit in 64 bits never touch mini-gmp. Full GMP has the
  same API, so swapping it in would be a contained change if `1.2`'s
  measurements call for it.

**Phase 2 — `//` and bitwise operators — done.** Decisions 4 and 10.
One question came up at the start: the plan's "vector literal's head"
wording for `|` disagreed with what the owner had approved, and the owner
chose the whole-top-level rule (decision 10, now reworded).

*Compiler.*
- **Tokens.** New tokens `//`, `&`, `~`, `<<`, `>>`; `|` reuses
  `VerticalBar`. Longest match keeps `|>`, `<-`, `<=` and `/` intact.
- **Precedence.** Four parser levels sit between `Comparison` and
  `Addition` (`BitOr` → `BitXor` → `BitAnd` → `Shift`). `//` joins
  `Multiplication`, and `~` joins `Unary`.
- **`~` and calls.** Like `-`, `~` can't start a paren-free call
  argument, so `f ~x` is xor; that follows the existing documented rule
  rather than being a new decision.
- **The `|` rule** uses a `barAsOperator` flag, mirroring the existing
  `commaAsOperator`. It is off at the top level of `[...]` and back on
  inside parentheses and index/slice brackets. A stray `|` where a vector
  literal expects `]` gets a targeted error: "A bare '|' inside [...]
  isn't bitwise OR (it marks cons or a comprehension); write (a | b)."
- **Opcodes.** Seven new ones, `IntDivide`, `BitAnd`, `BitOr`, `BitXor`,
  `BitNot`, `ShiftLeft` and `ShiftRight` (`0x38`-`0x3E`). They're purely
  additive, so the format stays v4, as when `0.2` added opcodes.
- **Folding.** Constant folding handles all seven on integer literals.
  .NET's `BigInteger` already uses infinite two's complement for
  `&`/`|`/xor/`>>`, and `//` floors explicitly. A zero divisor, a float
  operand, a negative shift count or a result past the size limit is left
  for the VM to report.

*VM.*
- **Semantics.** `numeric.cpp` gains `intDivide`, `bitAnd`/`bitOr`/
  `bitXor`/`bitNot` and `shiftLeft`/`shiftRight`, over mini-gmp for big
  integers (`mpz_fdiv_q`, `mpz_and`/`ior`/`xor`/`com`, `mpz_mul_2exp`,
  `mpz_fdiv_q_2exp`).
- **Fast paths.** `run()` gets inline int64 fast paths for every case that
  can't overflow: a positive `//` divisor, all of `&`/`|`/xor/`~`, a
  `<<` whose result stays below 2^62, and `>>`.
- **Slow path.** Everything else goes through `numericSlowPath`, keeping
  `try` out of the dispatch loop (the Phase 1 lesson). `INT64_MIN // -1`
  becomes a big integer.
- **Errors.** `Operands of '//' must be integers.` (and the same per
  bitwise operator), `Negative shift count.`, and `Integer result too
  large.` for a `<<` past 2^26 bits.

*Benchmarks.*
- `bench/realistic/collatz.iqx` is back on integers with `//`, and
  `timeconv.iqx`'s three hand-rolled `(a - a % b) / b` divisions are now
  `a // b`. Both print exactly what they printed before `0.4` (checked on
  the pre-Phase-1 toolchain).
- Measured against `main` (Phase 1), interleaved and pinned, 9 runs each:

  | Benchmarks | Change vs `main` |
  |---|---|
  | `collatz` | -14% |
  | `timeconv` | -34% |
  | everything else | within ±6% (noise) |

*Verification.*
- **Tests.**
  - xUnit: 367, up from 356. New cases cover the tokens, precedence
    (`x & 1 == 0`, the full level order, left-associative `//`), unary
    vs binary `~` including `f ~x`, the `|` rule in every position
    including the parse errors, codegen opcodes, and folding plus what
    must stay unfolded.
  - Catch2: 140, up from 136. They cover floored `//` (including the
    `(a // b) * b + a % b == a` identity and `INT64_MIN // -1`), two's
    complement on negative and big integers with results shrinking back
    to int64, shift edges (floored `>>`, huge counts, negative counts, the
    size limit), and every opcode on both the fast and slow paths.
- **Sanitizers.** Clean under ASan/UBSan.
- **Optimizer differential.** `scripts/optimizer-differential.sh` now
  fuzzes `//`, `&`, `|`, `~` (binary and unary), `<<` and `>>` too, and
  passes on 246 programs.
- **Examples.** A new `langspec/examples/bitwise.iqx`, and
  `numbers.iqx` gains `//`.
- **Docs.** `docs/LANGUAGE.md` §3 and §5, `langspec/SYNTAX_GRAMMAR.md`
  (productions and notes) and the README's precedence table are updated.

**Phase 3 — Compile-time division-by-zero check — done.** Decisions 12,
35 and 36-39 (the last four asked at the start of this phase).

*Compiler.*
- **Placement.** The check is a `Resolver.fs` extension, not its own
  pass: the resolver already knows each binding's mutability and scope,
  and running before `Optimizer.fs` gives `--no-optimize` the same
  diagnostics.
- **Shared folding.** `Optimizer.fs`'s literal folding (`foldBinary` and
  helpers) moved to a new `compiler/src/ConstantFolding.fs`. Its
  `evaluate` folds an unresolved `Ast.Expr` with a lookup for named
  constants. It also folds `%`, `^` (while the result stays under 2^16
  bits, so `(2 ^ 2000) / (2 ^ 1999)` is a constant divisor), `??`, and
  `and`/`or`/ternary with a constant condition, as `Optimizer.fs` does.
- **Constants.** Each `LocalVar` carries a declaration id and its constant
  value, if it is immutable with a constant initializer (decision 36). A
  pre-pass over the top-level statements does the same for globals, and
  `ExportVar` carries the constant to importing modules (decision 37).
- **Guards.** A `Facts` map, keyed by declaration id or global name,
  records `NotIntZero` and `NotNegativeInt`. `conditionFacts` turns a
  condition into facts for its true and false outcomes:
  - comparisons with a numeric constant on either side, `d == c` for a
    non-zero `c` included (decision 38);
  - `!` swaps the two;
  - `and`/`or` combine them by union and intersection.

  They are pushed into a ternary's branches (the middle only for `?`, not
  elvis `?:`) and into the right operand of `and`/`or`, which covers
  lambdas written there and a comprehension's own guard (desugared to a
  ternary). Mutable bindings never get facts.
- **Diagnostics.**
  - "Division by zero: the divisor is the constant 0."
  - "Can't prove this divisor isn't zero. Divide by a constant, or by an
    immutable variable checked first: d != 0 ? n / d : 0." The example
    uses the operator actually written.
  - "Division by zero: 0 raised to a negative power." for a constant
    `0 ^ -1`.
  - "Can't prove this '^' doesn't raise 0 to a negative power...", with
    both fixes.
  - The warning "Division by a constant 0.0 always gives inf or nan."
    (not for `//`, which rejects floats anyway).

  Elvis resolves its condition twice, so checks are deduplicated by
  token position. A constant that isn't a number compiles (decision 39).
- **Compliance.** No change needed. Every existing divisor in
  `Stdlib.fs`, `langspec/examples/` and `bench/` was already a literal or
  an immutable constant. Four xUnit tests that divided by `var mut`s or a
  literal zero were rewritten.

*VM.*
- **IEEE.** `numeric.cpp`'s `/` divides floats directly, so a float zero
  divisor gives `inf`/`-inf`/`nan`; `%` already gave `nan`. An integer
  zero divisor still raises "Division by zero." as a backstop.

*Verification.*
- **Tests.**
  - xUnit: 387, up from 367. New cases cover every guard form and its
    negative twin, constant chains (global, local, imported, large
    powers, short-circuit), mutable and call divisors, lambdas,
    comprehensions, elvis (one error, not two), non-number divisors, the
    `0.0` warning and the `^` rules.
  - Catch2: still 140; the "float zero divisor is an error" test became
    an IEEE one (`inf`, `-inf`, `-0.0`, `nan`, `%`).
- **Sanitizers.** Clean under ASan/UBSan.
- **Optimizer differential.** Its integer-zero case became a float-zero
  case plus a `//` type error. Random divisors the check can't prove now
  fail to compile in both modes, so half the time the generator picks a
  non-zero constant divisor instead. That keeps 166 of 210 random
  programs running, against 132 without the change. It passes on
  203 programs (246 before this phase).
- **Examples.** `numbers.iqx` gains a constant divisor, a guarded one, a
  comprehension guard, and a guarded `inverse` that returns `inf` for
  `0.0`. The constant-`0.0` warning is left out, since examples compile
  without diagnostics.
- **Docs.** `docs/LANGUAGE.md` §3 (the rule, with examples) and its error
  list.

**Phase 4 — The string model.** Decisions 13-19:
- the UTF-8 + ASCII-flag representation;
- code-point indexing, negative indices and slices on strings;
- immutability errors;
- generator and spread iteration;
- string ordering;
- the `\u{...}`/`\x..` escapes and `r"..."` raw strings.

**Phase 5 — `std.math`.** Decisions 11 and 26, plus `0.3`-Phase-6-style
import-gating, with "forgot an import" errors that name the module.

**Phase 6 — `std.string`.** Decisions 20-25:
- the same-origin import rule (decision 23), with `length`/`reverse`/
  `concat` made polymorphic;
- the function list;
- ICU case mapping and character classes;
- `format`.

At the phase's start, put §2 item 2 to the owner.

**Phase 7 — `std.regex`.** Decisions 27-28: ICU regex,
UTF-16 → code-point position mapping, the pattern cache, and the time limit.

**Phase 8 — Second set of compiler optimizations.** Decision 29: LICM and
strength reduction in `Optimizer.fs`. This includes before/after numbers
on `bench/` written up as in `0.3` Phase 7, plus differential testing with
`scripts/optimizer-differential.sh`.

**Phase 9 — Docs wrap-up.** `docs/LANGUAGE.md` describes `0.4` in place,
with what's new marked (no fork; `docs/EARLIER-VERSIONS.md` and the
`0.3.0` tag preserve `0.3`). `langspec/SYNTAX_GRAMMAR.md` gets the same
treatment. Every top-level `langspec/examples/*.iqx` is verified end to
end. `ROADMAP.md` marks `0.4` delivered and moves the active target to
`0.5`. `CLAUDE.md` gains an "Architecture (`compiler/` + `vm/`, 0.4)"
summary. Each earlier phase still updates the grammar, reference and
examples for its own feature as it lands, as in `0.3`.

## 6. Testing strategy

Same split `0.1`-`0.3` established:
- **xUnit** (`compiler/tests/`) for scanner, parser, resolver, codegen and
  optimizer correctness. The disassembler remains the primary codegen
  check.
- **Catch2** (`vm/tests/`) for VM execution and runtime errors.
- **A manual per-phase sweep** of `langspec/examples/` through the real
  toolchain.

On top of that, this version needs:

- **Numeric edge tables.** Catch2 cases that cross every operator with
  int/bigint/float/mixed operands, including:
  - the `int64` ↔ bigint overflow boundary in both directions (results
    that shrink back into `int64` must normalise back to the fast path);
  - floored `//`/`%` signs;
  - two's complement on negative bigints;
  - promotion overflow.
- **Division-check acceptance and rejection suites.** xUnit cases for
  every guard form decision 12 lists, plus the near-misses that must stay
  rejected: a mutable variable, a guard on a *different* binding, a guard
  that doesn't dominate the division, and a reassigned global.
- **Unicode cases** for strings:
  - non-BMP code points (emoji), which exercise the UTF-8 offset table;
  - combining marks, which are separate code points with no
    normalisation;
  - `ß` / Turkish-i case mapping;
  - regex positions after non-ASCII text (the UTF-16 → code-point
    mapping).
- **New example fixtures**: `numbers.iqx`, `bitwise.iqx`, `strings.iqx`,
  `string_stdlib.iqx`, `math_stdlib.iqx` and `regex.iqx` in
  `langspec/examples/`.
- **Optimizations**: before/after behavioural equivalence over `bench/`
  and `langspec/examples/`, as in `0.3` Phase 7.

## 7. Risks

- **ICU is the first heavyweight third-party dependency.** It has to build
  and link on Linux, macOS and Windows, in CI and in `release.yml`.
  *Retired by Phase 0:* no data file is linked (about +1.4 MB in total),
  and static linking works on Linux, macOS and Windows, proven by CI on
  every PR from now on. What remains is upgrade risk. A future ICU
  version may need its CMake flags adjusting; `test_dependencies.cpp`
  is there to catch behaviour changes.
- **ICU's source tarball is fetched from the Ubuntu archive.** That is the
  copy reachable from the environment Phase 0 was built in; GitHub
  release downloads were blocked there. The pinned hash makes the host
  irrelevant to integrity, but not to availability: if Ubuntu ever drops
  the file, CMake fails and the URL needs updating to another host serving
  the same bytes.
- **The compile-time division check will reject a lot of existing code.**
  Every unguarded division by a parameter, a call result or a mutable
  variable becomes a compile error, in examples, `bench/` programs,
  `Stdlib.fs` and users' code alike. The narrowing analysis must also be
  sound: a guard must really dominate the division, and the guarded
  binding must really be immutable, including through closures capturing
  it. If real programs show the rule is too strict to live with
  (something common that is genuinely safe but unprovable), bring the
  concrete cases to the owner rather than widening the rule on our own.
- **Performance of the new value representation.** *Retired by Phase 1*:
  measured at parity with `main` or faster (see Phase 1).
  - **The lesson for later phases.** Keep `try` blocks and other
    exception scaffolding out of `Vm::run()`; slow paths belong in
    out-of-line helpers. A 30-60% regression came from that alone.
- **Wide churn from the numeric model.** Printing, `length`, indices and
  `/` all change observable behaviour, so nearly every test file and
  fixture changes in Phase 1. Keep that phase's diff mechanical where
  possible, and separate the deliberate output changes from any
  accidental ones.
- **Lexical edge cases.** The main ones:
  - `.5` vs member access;
  - `1.` / `1.e5`;
  - `r"` vs a call to `r`;
  - `|` in vector heads;
  - `~` as both unary and binary.

  Each one is a new place where whitespace or the preceding token decides
  the meaning. As with `0.3`'s slice syntax, each needs a real
  disambiguation test rather than trusting this document's sketch.
- **Regex performance.** A backtracking engine can take exponential time
  on some patterns; the ICU time limit turns that into a runtime error
  rather than a hang. Document it in `docs/LANGUAGE.md`.
