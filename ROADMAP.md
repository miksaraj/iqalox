# Iqalox Roadmap

This supersedes the ad-hoc version notes previously kept outside the repo.
It reflects one renumbering decision made when this document was written:

> **`0.2-poc` is retired as a separate proof-of-concept stage.** Its content
> (matrix support, array-manipulation stdlib, list comprehensions, ...) now
> ships as plain **`0.2`** — a real release, not a second PoC. Every version
> that used to come after `0.2-poc` is bumped up by one to make room. The
> `langspec/archived/` snapshots (0.1, 0.2, 0.3) are **not** renumbered —
> they're a historical record of planning-as-it-happened, not documentation
> of the current plan. Use the mapping table below to translate old notes.

## Old → new version mapping

| Old label     | New label     |
|---------------|---------------|
| `0.1-poc`     | `0.1-poc`     (unchanged) |
| `0.1`         | `0.1`         (unchanged) |
| `0.2-poc`     | `0.2`         |
| `0.2`         | `0.3`         |
| `0.3`         | `0.4`         |
| `0.4`         | `0.5`         |
| `0.5`         | `0.6`         |
| `0.6`         | `0.7`         |
| `0.7` / `1.0` | `0.8` / `1.0` |

`1.0` stays the name of the eventual stable release regardless of how many
pre-1.0 minor versions it takes to get there.

## Versions

### 0.1-poc — *done*

The original PoC target: address the most glaring gaps in Lox, in Python, as
a tree-walk interpreter. See `docs/PLAN-0.1-POC.md` for the full
implementation log. Now superseded by `0.1` below as the primary
implementation, but kept in the repo (`poc/`) as a frozen, working reference
— see `CLAUDE.md`.

New/changed vs. Lox:

- Array support (vector literals — kept in 0.1-poc even though the original
  notes marked this for `0.2-poc+`, since basic vector literals already
  exist in the code; the full array-manipulation stdlib, list comprehensions,
  and matrices stay in 0.2)
- Block comments (`<# ... #>`)
- Implicit semicolons
- `continue` and `break` statements — usable as ternary branches (see below)
- Prefix increment/decrement operators (`++` / `--`) with standard
  mutating semantics; reassigning an immutable (`mut`-less) target is an
  error, ideally caught at compile time (see `docs/PLAN-0.1-POC.md`)
- `extends` instead of `<`/`>` for inheritance
- Chainable ternary operator (`? :` / elvis `?:`) **completely replaces**
  `if`/`else` — including for statement-like branches (`continue`, `break`,
  function calls with side effects). There is no `if` statement.
- `while` does not exist — `for` is the only loop construct
- **No function call — builtin or user-defined — takes parentheses.**
  `print x`, `concat [a, b]`, `add5 1`, `math.square 3`, `Duck "Waddles"`,
  `fact (n - 1)`. Parentheses still show up in three narrower, unrelated
  roles: grouping a compound (non-primary) argument expression
  (`fact (n - 1)`), the explicit zero-argument call marker (`count()`,
  `B()` — needed so a bare name can still mean "the function value itself,"
  e.g. `print fact`, `return adder`, `test count`), and multiple arguments
  are comma-separated without an enclosing pair (`ifEqualOr 2, 5`) — the
  comma there is an argument separator, not the general comma operator,
  exactly like the comma operator is already suppressed inside `[...]`
  vector literals. Function *declarations* keep their existing
  `fun f(a, b) { ... }` parameter-list parens; this only changes call sites.
  See `docs/PLAN-0.1-POC.md` for the full grammar sketch.
- No string concatenation via `+` (arrays/strings use array-manipulation
  facilities/`concat` instead)
- Pipe operator (`|>`)
- Ignore operator (`_`)
- Nullable infix operator (`??`)
- Comma operator
- Modulo (`%`) and power (`^`) arithmetic operators, at the same precedence
  level as `*`/`/`
- Immutability by default (`mut` keyword to opt in to mutability)
- The self-reference keyword is `self` (not `this`)

Lox baseline mechanics the examples assume and that 0.1-poc therefore also
covers (not "new" features, but not yet implemented either — see the plan doc):

- `for` loops
- Functions, calls, `return`, and closures
- Logical `and` / `or`
- Classes: declarations, fields, methods, constructors (`init`), single
  inheritance via `extends`, and `super` — i.e. baseline Lox method dispatch,
  nothing fancier (no forced getters/setters, no mixins/traits — see 0.2)

Explicitly **deferred out of 0.1-poc** (see 0.2): mixin support
(`class A extends B with C`, `A with B`) and trait support
(`trait A {...}` / `class B { use A }`) — the PHP-vs-Scala-style
implementation question is real but doesn't need answering yet.

### 0.1 — *done*

The first real implementation — a compiler frontend (F#) plus a bytecode
VM backend (C++23), superseding the `0.1-poc` tree-walk interpreter. See
`docs/PLAN-0.1.md` for the full implementation log (all 10 phases done,
including a cross-implementation conformance suite proving `compiler/`+
`vm/` match `poc/`'s output byte-for-byte on every `langspec/examples/`
fixture). Everything targeted for `0.1-poc`, actually complete and
hardened, plus:

- Accessing an uninitialized variable is a **runtime error** (implicit
  `undef`, not implicit `nil`) — a variable must be explicitly assigned
  something (including `nil`) before it can be read.
- Escape sequences in string literals.
- Immutability enforced at compile time, not runtime-only.
- Classes can reference themselves by name from inside their own methods.

See `docs/LANGUAGE-0.1.md` for the full `0.1` language reference (frozen
as of `0.2`'s own Phase 9 fork, `docs/PLAN-0.2.md` §5), including a
diagnostics-quality regression flagged there (§13) as a candidate for a
follow-up phase rather than something silently left undocumented.

### 0.2 *(formerly `0.2-poc`)* — *done*

Everything originally marked "push to `0.2-poc+`" in the source planning
notes that didn't get pulled forward into 0.1-poc, plus mixin/trait support
(deferred out of 0.1-poc — see above; bundled here as the natural home for
"class system completeness" work, alongside the other items below that were
already headed for this version). See `docs/PLAN-0.2.md` for the full
implementation log (all 9 phases done) — design decisions, open questions,
and phased sequencing:

- Matrix support (alongside existing vector/array support) — as nested
  vectors plus a dedicated stdlib, not new literal syntax
- Array-manipulation standard library, including indexing (`v[i]`),
  cons (`[item | list]`), and list comprehensions (`[expr | x <- xs]`,
  single-generator for this version)
- Anonymous function literals ("lambdas": `(a, b) -> expr`) — distinct
  from the named nested-function closures already in 0.1-poc
- Variadic unpacking (`...` operator) — vector-literal spread
  (`[...a, ...b]`) only for this version
- Getters and setters replaced by **`pub`/`mut` visibility and mutability
  modifiers**, private and immutable by default (`var name`, `var name
  mut`, `var name pub`, `var name pub mut`) — resolves the private-by-default
  property question raised for the old v0.3 (now 0.4, see below), and
  foreshadows `0.3`'s module support directly (`pub` is expected to apply
  to classes themselves then, defining what a module exports)
- Mixin support (`class A extends B with C`, `A with B`) and trait support
  (`trait A {...}` / `class B { use A }`) — resolved as a split by which
  keyword is used: `trait`/`use` is **PHP-style static copying** (matching
  PHP's own `trait`/`use` keywords), `with`-only mixins are **Scala-style
  composition** — shipped as a deliberately **simplified, non-C3-linearized**
  approximation rather than full Scala-style dynamic linearization (see
  "Language feature ideas under consideration" below for the deferred full
  version)

See `docs/LANGUAGE.md` for the full `0.2` language reference, and
`docs/LANGUAGE-0.1.md` for `0.1`'s own reference, frozen when this version's
Phase 9 forked it.

### 0.3 *(formerly `0.2`)* — *active target*

See `docs/PLAN-0.3.md` for the full plan — design decisions, open
questions, and phased sequencing:

- Disallow unused variables — **compile-time warning**
- Array-manipulation standard library improvements: negative indices and
  slice syntax (`v[a:b]`, end-**inclusive**), deferred from `0.2`
- Full list comprehensions: multiple comma-separated generators (nested/
  Cartesian iteration) and a boolean guard clause — `0.2` ships a
  single-generator, no-guard form only. **Corrects a typo in this
  document's own original sketch while resolving it**: the guard is
  introduced by its own `|`, not folded into the generator list with a
  comma — `[x + y | x <- xs, y <- ys | x != y]`, not `[x + y | x <- xs, y
  <- ys, x not y]` as originally written here. `not` was never a real
  keyword; it was informal shorthand for `!=` — the guard reuses ordinary
  boolean expressions already in the grammar (`docs/PLAN-0.3.md` decision 1).
- **Module support** — brought forward from `0.5` (see below), resolved
  as **file-based** (a file's path is its module name, no `module { }`
  block syntax), an explicit **`import`** statement bringing specific
  names into unqualified scope, single-entry-point transitive file
  discovery (`iqaloxc`'s CLI shape is unchanged), and **compile-time-only**
  resolution — `vm/` needs no changes at all, `iqaloxc` still emits one
  `.iqbc` file (`docs/PLAN-0.3.md` decisions 4-8). Module-scoped mixin/
  trait composition, flagged here as needing revisiting once real
  `module` declarations existed, turned out to need no new design work
  under this shape — a trait or class is just an ordinary importable
  top-level declaration like any other (`docs/PLAN-0.3.md` decision 9).
- **Array-manipulation standard library moved behind imports — resolved
  (`docs/PLAN-0.3.md` Phase 6).** `0.2` shipped it as flat, always-injected
  globals; with module support in place the repository owner decided that
  users must be able to define their own `sort`/`max`/`add`, that "no
  silent shadowing" is language-wide, and that an import line is acceptable
  ceremony. So the whole library is import-gated under built-in `std.*`
  modules (`std.vector`, `std.matrix`, with `std.string`, `std.math`, ...
  to come in `0.4`-`0.8`), and `print` is the only name available without
  an import. Names are imported bare (`import std.vector.map`), not
  qualified — the `Vector.map` spelling this entry originally floated was
  dropped along with qualified references generally.
- First set of compiler optimizations (see below) — proposed as constant
  propagation plus dead code elimination (`docs/PLAN-0.3.md` §2 item 2),
  not yet separately confirmed.

Comma-as-no-parens-multi-argument-call-separator (`push v, 4`) was also
raised for reconsideration while reviewing Phase 5's array-stdlib
examples (it's `0.1-poc`'s original design, `poc/src/parser.py`'s
`call_head()`, not something that crept in during `0.2`) — decided to
leave as-is permanently, not revisited here or in any later version; see
`docs/PLAN-0.2.md`'s Phase 5 entry for the full exchange.

### 0.4 *(formerly `0.3`)*

Standard library, part 1 of 5 (see "Standard library vision" below for
the full category breakdown and how it maps onto `0.4`-`0.8`):

- String manipulation standard library — concatenation, search, split,
  format, regex
- Mathematical operations standard library — arithmetic, trigonometry,
  logarithms, random number generation, rounding
- Second set of compiler optimizations

### 0.5 *(formerly `0.4`)*

Standard library, part 2 of 5:

- Data structures standard library, the rest of it — lists,
  dictionaries/maps, stacks, queues, sets (arrays/vectors already shipped
  `0.2`)
- I/O standard library — file and stream I/O, console interaction
- Disallow unused variables — **compile-time error** (upgraded from
  `0.3`'s warning)
- Third set of compiler optimizations

### 0.6 *(formerly `0.5`)*

Standard library, part 3 of 5:

- Date and time standard library
- Error handling standard library — raising/handling exceptions, logging,
  error reporting
- Fourth set of compiler optimizations

### 0.7 *(formerly `0.6`)*

Standard library, part 4 of 5:

- Networking standard library — HTTP requests, TCP/IP, UDP, sockets, URL
  parsing
- File system standard library — create/delete/copy/move files and
  directories, metadata retrieval
- Fifth set of compiler optimizations

### 0.8 / 1.0 *(formerly `0.7` / `1.0`)*

Standard library, part 5 of 5 (the last of it — every category in the
vision below is allocated to a version by this point):

- Concurrency and threading standard library — threads, locks,
  semaphores, synchronization primitives
- System interaction standard library — executing system commands,
  environment variables, OS interaction, subprocesses
- Sixth (and possibly final) set of compiler optimizations

## Language feature ideas under consideration

Raised while planning `0.2` (`docs/PLAN-0.2.md`), deliberately not
committed to a specific version yet:

- **Variadic parameters** (`fun f(...args)`, extra call-site arguments
  collect into a vector) and **call-site spread** (`f(...someVector)`,
  unpacking a vector's elements as separate positional arguments). `0.2`
  ships only vector-*literal* spread (`[...a, ...b]`) — these two are a
  bigger step, since variadic parameters are in real tension with `0.1`'s
  stated design ("a call's arity is fixed by the callee's declaration, not
  inferred from how many arguments happen to be written"). That tension
  needs its own resolution before either lands, not just a version slot.
- **Full C3 linearization for `with`-mixins** (real Scala-style multiple
  inheritance: a computed per-class Method Resolution Order, live
  MRO-walking dispatch instead of a flat copied method table, and `super`
  resolving relative to the runtime receiver's actual MRO position rather
  than a single value captured once at class-declaration time, enabling
  genuine `super`-chaining across a mixin graph). `0.2` Phase 8 ships a
  simplified, non-C3 approximation instead (`with` composes exactly like
  `use`'s flat copy, just from a real class's runtime table; `super` never
  chains through a mixin) — raised as an option during Phase 8 and
  explicitly deferred after a dedicated scope-check found the real
  implementation cost far larger than open question 2's own framing
  suggested: it's a foundational redesign of dispatch/`super` for every
  `with`-using class, not an incremental addition on top of what Phase 8
  already ships. See `docs/PLAN-0.2.md` §2 item 2 and Phase 8's own entry
  in §5 for the full reasoning.

## Compiler optimization concepts under consideration

To be scheduled across the 0.3–0.8 optimization passes above, roughly in
order of expected impact/ease, but not committed to any specific version yet:

- Constant propagation
- Common subexpression elimination
- Loop-invariant code motion
- Global value numbering
- Strength reduction
- Scalar replacement of aggregates
- Dead code elimination
- Loop unrolling

See also: <https://en.wikipedia.org/wiki/Optimizing_compiler>

## Architecture note for the eventual bytecode implementation

The project moves past tree-walking for `0.1`: a compiler frontend written
in **F#** and a bytecode VM backend written in **C++23** — see
`docs/PLAN-0.1.md` for the full plan. The split — per *Crafting
Interpreters* — is:

- **Frontend** (`compiler/`, F#): scanner, compiler (source → bytecode)
- **Backend** (`vm/`, C++23): VM, chunk (bytecode representation),
  debug tooling

## Standard library vision

The categories the standard library is expected to eventually cover, in
the order they land — every one now allocated to a version (`0.2` for
arrays; `0.4`-`0.8` for the rest, two categories per version, roughly
ordered strings/math early to concurrency/networking/system-level much
later):

1. **Data structures** — arrays/vectors (`0.2`, already shipped); the
   rest (lists, dictionaries/maps, stacks, queues, sets) in `0.5`
2. **String manipulation** — concatenation, search, split, format, regex
   (`0.4`)
3. **Mathematical operations** — arithmetic, trigonometry, logarithms,
   random number generation, rounding (`0.4`)
4. **Input/Output** — file and stream I/O, console interaction (`0.5`)
5. **Date and time** — dates, times, timestamps, time zones, formatting,
   parsing (`0.6`)
6. **Error handling** — raising/handling exceptions, logging, error
   reporting (`0.6`)
7. **Networking** — HTTP requests, TCP/IP, UDP, sockets, URL parsing
   (`0.7`)
8. **File system operations** — create/delete/copy/move files and
   directories, metadata retrieval (`0.7`)
9. **Concurrency and threading** — threads, locks, semaphores,
   synchronization primitives (`0.8`)
10. **System interaction** — executing system commands, environment
    variables, OS interaction, subprocesses (`0.8`)

Module support itself (`0.3`, brought forward from `0.5` — see above) is
infrastructure this buildout depends on, not a category of its own.
