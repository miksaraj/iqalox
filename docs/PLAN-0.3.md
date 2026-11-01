# Plan: getting to 0.3

This is the `0.3` counterpart to `docs/PLAN-0.2.md`: a living plan for the
next feature set on top of `0.2`'s compiler frontend (F#, `compiler/`) and
bytecode VM backend (C++23, `vm/`). Like the `0.1`/`0.2` plans, this
document tracks resolved decisions, flags open ones rather than guessing
at them (per `CLAUDE.md`), and keeps a running status table and
sequencing checklist as work lands.

`0.1-poc` (`poc/`) is **not** getting a `0.3` either — it stays frozen,
same as it did through `0.2` (`CLAUDE.md`).

## 0. Scope: what "0.3" means here

Per `ROADMAP.md`'s `0.3` entry (formerly `0.2`, bumped by the `0.2-poc`
renumbering):

- Disallow unused variables — compile-time **warning** (an error is a
  later version's job)
- Array-manipulation standard library improvements: negative indices and
  slice syntax (`v[a:b]`), deferred from `0.2`
- Full list comprehensions: multiple comma-separated generators and a
  boolean guard clause — `0.2` shipped single-generator, no-guard only
- **Module support**, brought forward from `0.5` so the `0.4`-onward
  standard library buildout has real namespacing to land in from the
  start, rather than after several phases have already shipped as flat
  globals. Also the trigger for revisiting two things `0.2` explicitly
  deferred pending real module support existing: module-scoped mixin/
  trait composition, and whether `0.2`'s array-manipulation stdlib should
  move under a namespace
- First set of compiler optimizations (from `ROADMAP.md`'s "Compiler
  optimization concepts under consideration" list)

## 1. Design decisions (resolved)

Resolved via two `AskUserQuestion` rounds with the repository owner
before this document's first draft — the second round exists because the
first round's module-system answer ("real multi-file compilation") is
substantially bigger in scope than a single multiple-choice option
conveys, the same situation `0.2`'s Phase 8 (`with`-mixin linearization)
ran into; rather than guess at the mechanics, a dedicated follow-up round
pinned down the actual shape before any of this got written down as a
plan.

1. **List comprehension guards reuse ordinary boolean expressions — and
   the guard clause is introduced by its own `|`, not folded into the
   generator list with a comma.** The repository owner corrected a real
   typo in `ROADMAP.md`'s own historical sketch while answering: the
   original text read `[x + y | x <- xs, y <- ys, x not y]` (comma
   before the guard); the actual intended shape is
   `[x + y | x <- xs, y <- ys | x != y]` — generators comma-separated as
   before, then a **second** `|` introduces an optional guard expression.
   `not` was never meant as a real keyword; it was informal shorthand for
   `!=`, and the guard is just any expression already in the grammar
   (`==`, `!=`, `and`, `or`, `!`, ...). Grammar shape:
   `vectorBody → ... | expression "|" generator ("," generator)* ("|" expression)?`
   where `generator → IDENTIFIER "<-" expression`. No new top-level
   ambiguity versus `0.2`'s cons/comprehension split: the second `|` only
   ever appears after a parse has already committed to the comprehension
   branch (seeing at least one `<-`), which cons parsing never reaches.
   **Not separately asked, resolved by direct consequence**: later
   generators may reference earlier generators' bound names in their own
   source expression (`[p | x <- xs, y <- range x]`) — this falls out for
   free from the natural desugaring (nested loops, outermost generator
   first), not a special case needing its own design decision.
2. **Negative indices need no new grammar at all — only a runtime
   semantics change.** `v[-1]` already parses today as ordinary indexing
   with a unary-minus expression inside `[...]`; `0.2`'s `GetIndex`/
   `SetIndex` simply needs their bounds check extended to accept
   `-length..-1` as valid, translating internally to `length + index`.
   Applies to **both** reads and writes (`v[-1] = x` works, same as a
   positive in-range write does today). An out-of-range negative index
   (`v[-100]` on a 3-element vector) is the same "Vector index N out of
   range" runtime error `0.2` already raises, just checked against the
   wider valid range.
3. **Slices are `v[a:b]`, end-**inclusive**, with a dedicated new grammar
   production** (this is genuinely new syntax, unlike negative indexing):
   `slice → "[" expression? ":" expression? "]"`, replacing the plain
   `"[" expression "]"` alternative when a top-level `:` is found before
   the closing `]`. Either bound may be omitted (`v[:3]`, `v[2:]`, `v[:]`
   all valid, defaulting to start/end respectively); either bound may be
   negative, using the exact same from-the-end convention as decision 2.
   A slice always returns a **new** vector (copy semantics, never a
   view) — mutating the result never mutates the source. **No
   slice-assignment in `0.3`** (`v[a:b] = ...` is not valid syntax);
   slices are read-only this version. **Proposed, not separately
   confirmed**: `a > b` after resolving negative bounds (e.g. `v[3:1]`)
   produces an **empty vector**, not a runtime error — consistent with
   how most languages treat a degenerate slice range, and avoids turning
   a common edge case into an error the way an out-of-range single index
   still is. Flag if an error was actually wanted here instead.
4. **Module identity is implicit from file path, not a declared
   header or explicit block.** A file's module name is derived from its
   path relative to the compiled program's root (e.g. `math/vector.iqx`
   → module `math.vector`) — no `module Foo { ... }` block syntax and no
   `module math.vector;`-style header line. Simplest mental model
   (matches Python's own file-is-a-module convention), and needs no new
   top-level declaration grammar at all — every top-level `pub`
   declaration in a file is automatically that file's module's exported
   surface (decision 6 below).
5. **Cross-module references use an explicit `import` statement that
   brings specific names into unqualified scope — a new keyword.**
   `import math.vector.transpose` (dotted module path, then the specific
   member being imported), matching decision 9's *original* sketch
   wording. Multiple imports from possibly-different modules
   comma-separate, mirroring `0.2`'s existing `use A, B` trait-composition
   syntax rather than inventing a brace-grouping form:
   `import math.vector.transpose, math.matrix.multiply`. **Proposed, not
   separately confirmed**: importing a name that collides with an
   existing local/global declaration in the importing file is a
   compile-time "already declared" error, the same category `0.2`
   already raises for redeclaring `print`/a native/a trait/etc. — no
   silent shadowing.
6. **`pub` now also gates module-level visibility, not just
   class-member visibility.** A top-level `fun`/`var`/`class`/`trait`
   declared `pub` is importable from another file; one declared without
   `pub` is module-private (usable anywhere within its own file, but not
   `import`able from anywhere else) — directly foreshadowed by `0.2`'s
   own decision 8. **Necessary consequence, not separately asked about**:
   this makes every existing top-level declaration in every current
   `langspec/examples/*.iqx` fixture implicitly module-private under the
   new rule, same category of breaking change `0.2`'s decision 11 (private
   methods by default) already was — each single-file example still works
   unchanged as long as nothing outside that file needs to `import` from
   it, which is true of every existing fixture today.
7. **Module resolution happens entirely at compile time; `vm/` needs zero
   changes for module support itself.** `iqaloxc` still emits exactly
   **one** `.iqbc` file — `import` resolution, module-path-to-file
   lookup, and `pub`-as-export enforcement all happen inside `compiler/`
   (extending the same textual-merge-then-resolve-together model
   `Program.fs` already uses for `Prelude.fs`, now generalized to a whole
   dependency graph of user files instead of one prelude module). No
   module/symbol-table representation is added to the bytecode format;
   the VM never learns a program came from more than one source file.
   Real separate compilation (recompiling one module without recompiling
   everything that imports it) is explicitly **not** a `0.3` capability —
   logged on `ROADMAP.md` as a future idea if it's ever wanted.
8. **`iqaloxc`'s CLI keeps its current single-entry-point shape — no
   manifest format, no multi-file command line.** `iqaloxc entry.iqx
   out.iqbc` is unchanged; the compiler follows `entry.iqx`'s `import`
   statements to find and parse whatever other files the program
   transitively depends on, resolving file paths relative to `entry.iqx`'s
   own directory (exact multi-root/search-path rules, if ever needed, are
   out of scope until a real use case demands them). No new project/
   package manifest format is introduced this version.
9. **Module-scoped mixin/trait composition needs no new design work —
   it falls out of decisions 4-8 for free.** `0.2`'s open question about
   this (`ROADMAP.md`'s `0.3` entry) assumed real `module` declarations
   might need their own trait/mixin composition rules; under the design
   above, a trait or class is just an ordinary top-level declaration like
   any other, importable via decision 5 the same way a function is —
   `use`/`with` then compose it exactly as `0.2` already does, with no
   awareness that the composed name came from another file at all.

## 2. Open questions (flagged, not decided)

1. **RESOLVED in Phase 6** (see that phase's entry in §5 for the decision
   and its consequences). Original question, kept for the record: **Whether `0.2`'s array-manipulation standard library (`push`, `pop`,
   `length`, `reverse`, `map`, `filter`, `reduce`, `sort`) should move
   under a real module namespace now that one exists, and/or become
   `import`-gated instead of always-injected globals.** `ROADMAP.md`'s
   `0.3` entry explicitly named this as a live decision to make once
   module support's actual shape was known, not something to guess at
   alongside the module-system decisions above — raise this with the
   repository owner specifically when this phase of the sequencing below
   starts, the same way `0.2`'s Phase 8 mixin/trait questions were asked
   right before that phase began rather than during initial `0.2`
   planning.
2. **Exactly which optimizations make up the "first set."**
   `ROADMAP.md`'s "Compiler optimization concepts under consideration"
   list is roughly ordered by expected impact/ease (constant propagation,
   common subexpression elimination, loop-invariant code motion, global
   value numbering, strength reduction, scalar replacement of aggregates,
   dead code elimination, loop unrolling) but not committed to specific
   versions. This is sequencing/scoping, not a language-design question
   (`CLAUDE.md`: engineering decisions don't need sign-off) — §5 below
   proposes constant propagation plus dead code elimination as `0.3`'s
   first set (a natural pairing: DCE finds meaningfully more to remove
   once constants have propagated), flagged here in case a different
   pairing is actually wanted.
3. **Whether `_`-prefixed identifiers suppress the new unused-variable
   warning**, a common convention in languages with similar lints (Rust,
   Go tooling, etc.). Not asked as part of the main design-decision
   rounds since it's a small, reversible detail rather than an
   architectural one — proposed default in §5's relevant phase: yes,
   `_`-prefixed locals/parameters/properties are exempt, mirroring the
   existing `_` ignore-operator's "explicitly don't care about this"
   convention already in the language ([§6](#6-control-flow) of
   `docs/LANGUAGE.md`). Flag if a different convention (or none at all)
   was actually wanted.

## 3. Grammar and architecture additions (overview)

- **Slices**: one new grammar production (decision 3) replacing plain
  `"[" expression "]"` when a top-level `:` appears — `compiler/src/Ast.fs`
  gains a `Slice`/`IndexSet`-adjacent node, `Parser.fs` gains the
  lookahead, `Bytecode.fs`/`vm/` gain a new opcode (`GetSlice`, no
  `SetSlice` per decision 3's read-only rule) rather than overloading
  `GetIndex`.
- **Comprehension guards**: extends `0.2`'s existing `Cons`/
  `ListComprehension` desugaring machinery (`Resolver.fs`) — the
  generator-loop-in-a-synthetic-closure shape gains an extra conditional
  (the guard expression) before the `VectorAppend`, and multiple
  generators desugar to nested loops, outermost first. No new opcodes
  beyond what `0.2`'s `VectorLength`/`VectorAppend` already provide.
- **Unused-variable warnings**: a `Resolver.fs`-only change — track, per
  scope, whether each declared local/parameter/private-property/
  private-method is ever referenced after its declaration; emit a
  warning (not an error — compilation still succeeds, `iqaloxc`'s exit
  status is unaffected) for anything that isn't. No grammar changes, no
  `vm/` changes.
- **Modules**: the largest addition. `compiler/src/Program.fs` grows a
  real dependency-resolution pass — given an entry file, parse it,
  collect its `import` statements, resolve each imported module path to
  a file on disk, recursively repeat for each newly-discovered file
  (cycle detection needed, the same category of problem `0.2`'s
  `flattenTrait` already solved for trait `use`-chains), then feed the
  whole file set through `Resolver.fs` together — extending the existing
  prelude-merge model rather than replacing it. `Resolver.fs` gains
  module-qualified name resolution (mapping `import`ed names to their
  origin file's bindings) and the `pub`-as-module-export check (decision
  6). No `vm/` or bytecode-format changes at all (decision 7).
- **Compiler optimizations**: live entirely inside `Codegen.fs` (or a new
  optimization pass between `Resolver.fs` and `Codegen.fs`, exact
  placement a Phase 7 implementation detail) — no grammar, `Bound.fs`, or
  `vm/` changes; the emitted bytecode's *behavior* must be unchanged,
  only its shape/size/instruction count.

## 4. Feature checklist (parity target)

Every row starts "not started" — ticked off as `compiler/`+`vm/` land
each one, verified via new tests (§6).

| Feature | Design decision(s) | Status |
|---|---|---|
| Negative vector indices (get/set) | §1.2 | Done |
| Slices (`v[a:b]`, end-inclusive, read-only) | §1.3 | Done |
| Multi-generator list comprehensions | §1.1 | Done |
| List comprehension guard clause | §1.1 | Done |
| Unused-variable compile-time warning | §2.3 | Done |
| Module support (file-based, `import`, compile-time-only) | §1.4-9 | Done |
| Array-manipulation stdlib behind imports | §2.1 | Done |
| First set of compiler optimizations | §2.2 | Done |

## 5. Suggested sequencing

A proposed order, not a commitment — reorder freely. Self-contained,
lower-risk items first; modules (the largest, most novel piece of
toolchain work) placed after them so the sequencing checklist has
working, shippable progress even if module support's own scope grows
mid-phase the way `0.2`'s Phase 8 did.

**Phase 0 — `langspec/` versioning move.** *Done.* Moved `0.2`'s
`langspec/` snapshot (grammar docs + examples) into
`langspec/versions/0.2/`, mirroring `docs/PLAN-0.2.md`'s own Phase 0.
Unlike that Phase 0, no CI repointing was needed — the cross-
implementation conformance job that `0.1`→`0.2`'s Phase 0 had to repoint
was already retired outright during `0.2` Phase 7; the one CI job that
still names a `langspec/versions/<n>/examples/` path (`poc/`'s own smoke
run, `.github/workflows/ci.yml`) is intentionally pinned to
`versions/0.1/` specifically (the newest fixture set `poc/` — frozen at
`0.1-poc`-equivalent semantics — can actually still run), not the newest
frozen snapshot in general, so it's correctly left untouched.

**Phase 1 — Negative indices and slices — done.** `vm/src/vm.cpp`'s
`checkVectorIndex` now translates a negative index to `length + index`
before bounds-checking (decision 2) — no grammar change was needed for
this half at all, since `v[-1]` already parsed as ordinary indexing over
a unary-minus expression; only the runtime semantics changed, and the
error message dropped "non-negative" from its wording accordingly (still
a hard error either direction out of range). Slices (`v[a:b]`, decision
3) needed a real new `Ast.Slice`/`Bound.BSlice` node, one new `GetSlice`
opcode (no `SetSlice` — slices are read-only), and a `Parser.fs`
lookahead (`isSliceAhead`) that scans for a bracket-depth-0 `:` before
the closing `]`, explicitly tracking (and skipping past) any ternary
`? :` pair at that same depth so `v[cond ? a : b]` still parses as
ordinary indexing rather than being mistaken for a slice. `Codegen.fs`
emits a plain `Nil` instruction for whichever bound is omitted
(`v[:3]`/`v[2:]`/`v[:]`) rather than giving `GetSlice` its own
optional-operand encoding; `Vm::getSlice` resolves `nil` bounds to
0/`length-1`, translates negative bounds the same way single-index access
does, then **clamps** (rather than erroring) any bound that's still out
of range and returns an **empty vector** rather than erroring when
`start` resolves after `stop` — the specific clamping/empty-on-inverted
behavior proposed in decision 3 and not separately re-confirmed.
`langspec/examples/indexing.iqx` was extended with both features end to
end (verified via the real `compiler/`+`vm/` toolchain), and
`langspec/SYNTAX_GRAMMAR.md`/`docs/LANGUAGE.md` §9 both cover the new
syntax/semantics. New tests: 11 xUnit (3 Parser disambiguation/negative-
index/omitted-bound cases, 2 Resolver, 2 Codegen instruction-sequence,
plus the `Slice` pattern threaded through existing shape-assertion
tests) and 13 Catch2 (negative get/set/out-of-range, and a full slice
matrix: basic, omitted bounds, both omitted, negative bounds, inverted
range, out-of-range clamp, copy-not-view, non-vector receiver, non-number
bound).

**Phase 2 — Full list comprehensions — done.** `Ast.ListComprehension`'s
`variable: Token * source: Expr` pair became `generators: (Token * Expr)
list` plus `guard: Expr option`; `Parser.fs`'s existing `<-`-lookahead
disambiguation (cons vs. comprehension) is unchanged, extended with a
`while matchAny [ Comma ]` loop over more generators and an optional
second `matchAny [ VerticalBar ]` for the guard, both gated behind
`commaAsOperator` already being suppressed inside `[...]` so a
generator's own source expression can't accidentally swallow the comma
separating it from the next generator.

`Resolver.fs`'s desugaring generalizes from one fixed loop to a
recursively built nest of them, one per generator, outermost/first-
written first — the real design decision here, not just mechanical
repetition: only the **first** generator's source is evaluated once, in
the enclosing scope, and passed into the synthetic closure as its sole
parameter, exactly matching `0.2`'s existing shape byte-for-byte (a
dedicated Codegen test asserts the single-generator, no-guard case's
instruction sequence is completely unchanged). Every later generator's
source is instead declared as a fresh local **inside** the previous
generator's loop body, re-evaluated on every outer iteration — required,
not just convenient, since a later generator's source may reference an
earlier generator's already-bound name (`[[x, y] | x <- xs, y <- range
x]`, decision 1's own example), which is only in scope from inside that
enclosing loop. The guard, when present, wraps the existing
`InternalVectorAppend` in a `Ternary` — compiling through the exact same
conditional-jump machinery any other ternary already uses, not a new
opcode. Manually verified end to end (Cartesian product, a dependent
generator via a `range`-style helper, a guard alone, and multi-generator
+ guard together) beyond what the unit tests assert, since the nested-
loop desugaring's runtime behavior isn't fully provable from instruction-
sequence assertions alone. 4 new xUnit Parser tests (multi-generator
parsing, guard parsing, no-guard-stays-`None`), 2 new Resolver tests
(a dependent generator resolves without error, a guard resolves and
still desugars to a one-parameter call), and 2 new Codegen instruction-
sequence tests (the guard's ternary-shaped jumps, and the second
generator's source correctly re-fetched from inside the outer loop
rather than passed as a second parameter). `langspec/examples/
cons_and_comprehensions.iqx` extended with all four new forms.
`docs/LANGUAGE.md` §9 rewritten for the lifted restriction, and §15's old
item 5 (single-generator/no-guard limitation) removed and the list
renumbered — cross-checked against `docs/PLAN-0.3.md`'s own Phase 1 risk
entry, which cited the old item 12 (now item 11) by number.

**Phase 3 — Unused-variable warnings — done.** `Resolver.fs`-only (§3), as
planned — no `Ast.fs`/`Bound.fs`/`Bytecode.fs`/`vm/` changes at all. Scope
turned out to need one more concrete answer than §1's numbered decisions
capture: an early `AskUserQuestion` round (before this document's first
draft) asked how far the warning should reach, and the repository owner's
answer was "also unused private class members," not just locals/
parameters — recorded here rather than under a renumbered §1 decision,
since renumbering would have disturbed every existing cross-reference to
decisions 1-9. Open question 3 (`_`-prefix exemption) is resolved the same
way: proceeded with the proposed default (yes, exempt) rather than asking
again, since it was flagged as a small, reversible detail up front.

`Resolver.fs`'s `LocalVar` record gained `NameToken: Token option` (`None`
for the synthetic `self`/`super` locals, `Some` for everything
`declareVariable` actually declares) and a `mutable Used: bool`, flipped
in place — F# records are reference types, so mutating a `LocalVar`
fetched out of `FunctionContext.Locals` updates the stored instance
directly, needing no second pass over the tree. Only a real **read**
(`resolveReference`'s `Variable` case) marks a local used; a pure
**write** (`Assign`) does not, threaded via a new `markUsed: bool`
parameter added to every one of `resolveReference`'s 8 call sites — a
variable assigned but never subsequently read is exactly the "why does
this exist" case the warning is meant to catch. Capturing a local as an
upvalue (`resolveUpvalue`) always counts as a use regardless of whether
the closure that captures it reads or writes it — a deliberate
simplification rather than threading a read/write flag through every
recursive hop of upvalue resolution for a case narrow enough not to be
worth it. The check itself (`checkUnusedLocals`) runs both from
`endScope()` (the common case) and, separately, from `ResolveFunction`
right before its own top-level scope is discarded — that scope (holding
`self` plus every parameter and top-level body local) never goes through
`endScope()` at all, a real gap only found once `checkUnusedLocals` was
written and function parameters still weren't being checked.

Property/method tracking works differently on purpose: a single global
`usedSelfNames: HashSet<string>` collects every name seen on the
right-hand side of `self.x` or `self.method()` (`Get`) *or* `self.x =
value` (`Set`) anywhere in the whole program — both directions count as a
use here, unlike the local/parameter read/write split above, since
"this property is only ever written, never read" isn't the signal being
hunted for at the class level. Deliberately a flat set of *names*, not
scoped per class, trading a theoretical false negative (an unused private
member in one class sharing a name with a genuinely-used private member
in an unrelated class) for not threading a second, class-scoped
structure through every method resolution — never produces a false
positive, the safer failure mode for a first-pass lint. Checked once,
after the whole program has resolved (`Resolver.CheckUnusedClassMembers`,
called from `resolve` right after the main statement walk), against each
class's own `ClassInfo.OwnLiteral.Properties`/`.Methods` only — trait/
superclass/mixin-contributed members are never checked here, since they
compose into potentially many classes and "unused by this one particular
class" isn't a meaningful signal for them. `pub` members are always
exempt (the point of `pub` is being part of the public surface, callable
whether or not anything in this program happens to call it), and `init`
is always exempt regardless of its own `pub`/private annotation
(`docs/PLAN-0.2.md` decision 11 — externally callable regardless).

`resolve`'s public signature grew a third return value
(`BoundStmt list * ResolveError list * ResolveError list`, errors then
warnings) — the minimal-blast-radius option, given how many indirect call
sites destructure it, was keeping every test helper (`resolveSource` in
`ResolverTests.fs`, `compileSource` in `CodegenTests.fs`, the prelude
helpers in `PreludeTests.fs`) at their existing 2-tuple shape by
discarding the new warnings list internally, and adding one new
`resolveSourceWithWarnings` helper for the tests that actually need it,
rather than touching every existing call site's destructuring pattern.
`Program.fs` prints each warning (`[line N] Warning: ...`) unconditionally
before checking `resolveErrors`, and does **not** fold them into the exit
code — a program with only warnings still compiles and exits 0, per
`ROADMAP.md`'s "a warning, not an error (yet)" framing.

A full sweep of every `langspec/examples/*.iqx` fixture through the real
toolchain surfaced exactly one warning, and it was a real, previously-
unnoticed bug: `functions.iqx`'s `makeAdder`/`adder` closure example had
`adder(i) { return n + 1 }` — ignoring its own parameter `i` entirely, so
`add5 1` and `add5 100` both printed `6` instead of `6`/`105`. Fixed to
`return n + i`; re-verified end to end (compile clean, no warnings, VM
output now correct) — this is exactly the class of bug the feature is
meant to catch, in the same spirit as the several `poc`-era bugs earlier
phases found and fixed rather than carried forward. No dedicated new
`langspec/examples/*.iqx` fixture was added for this phase, unlike
Phases 1-2: those introduced new *syntax* worth demonstrating; this phase
adds a compile-time diagnostic over syntax that already exists, with
nothing new to show running. 14 new xUnit `Resolver.fs` tests: unused
local/parameter warns, a used local doesn't, write-only still warns, an
upvalue-captured variable doesn't warn, `_`-prefix exemption (both local
and parameter), unused private property warns, a pub property never
warns, a property only ever written via `self.x = value` doesn't warn
(the read/write asymmetry versus locals, called out explicitly), unused
private method warns, a pub method never warns, `init` is exempt even
though nothing calls it via `self`, and a private method called only
externally (never via `self`) still warns (documented as the deliberate
`self.`-qualified-only simplification, not a bug — such a call would fail
at runtime anyway, since a non-`pub` method can't be invoked from outside
its class).

**Follow-up correction, same phase**: the first pass of this phase shipped
with top-level (global) `var`/`fun` declarations deliberately excluded
from the check — a scoping call made unilaterally rather than raised as
its own question, unlike the private-class-member scope above, which
*was* explicitly asked and answered. The repository owner caught this
gap directly and it was corrected the same phase rather than deferred:
top-level `var`/`fun` are now checked with the identical read-vs-write
rule locals already use (a global assigned but never read still warns).
`preRegisterGlobals` (`Resolver.fs`) gained a second output,
`globalDecls: Dictionary<string, Token * string>`, recording each
top-level `var`/`fun`'s own declaration token and a message-kind label
("Variable"/"Function") — deliberately **not** populated for top-level
`class` declarations, since "this class is never instantiated in the
file" wasn't part of what was actually asked for or answered, unlike the
class-*member* check. `resolveReference`'s existing global fallthrough
now also populates a new `usedGlobalNames: HashSet<string>` whenever
`markUsed` is true, mirroring `usedSelfNames`'s shape; `CheckUnusedGlobals`
warns on any `globalDecls` entry `usedGlobalNames` never saw, honoring
the same `_`-prefix exemption as everything else.

This surfaced a real problem specific to `compiler/src/Prelude.fs`'s
five stdlib functions (`docs/PLAN-0.2.md` Phase 5): `map`/`filter`/
`reduce`/`sort`/`elementwise` are ordinary top-level `fun` declarations,
textually merged ahead of the user's own source before resolving
(`Program.fs`) — so, naively, a user program that only calls `map` would
now be told `sort`/`filter`/`reduce`/`elementwise` are "unused," even
though the user never declared them and has no way to remove them. Fixed
by splitting `resolve`'s implementation into a private `resolveInternal`
taking an `exemptGlobalNames: Set<string>` and two public entry points:
`resolve` (unchanged 1-argument shape, `exemptGlobalNames = Set.empty` --
every existing call site, including every test helper, keeps compiling
unmodified) and a new `resolveWithExemptGlobals`, which `Program.fs` now
calls instead, passing every one of `preludeStmts`' own `FunctionStmt`
names (derived from the already-parsed prelude AST, not a second
hardcoded name list). A full re-sweep of every `langspec/examples/*.iqx`
fixture after this change produced zero warnings, confirming no example
happens to declare a genuinely dead top-level global either. 8 new xUnit
tests: unused top-level var/fun warn, a read/called one doesn't, write-
only still warns, `_`-prefix exemption, a never-instantiated top-level
class produces no warning (classes are deliberately out of scope), and
two `resolveWithExemptGlobals`-specific tests (an exempted name never
warns; a non-exempted sibling still does).

**Phase 4 — Module system: multi-file compilation infrastructure — done.**
Scoped literally per the plan (confirmed with the repository owner when
the phase started, since `import` — the only thing that can trigger
multi-file loading from real source — is Phase 5): plumbing only, no new
syntax, no `vm/` or bytecode-format changes (decision 7).

New `compiler/src/Modules.fs`: `moduleNameOfPath`/`modulePathOfName`
(`math/vector.iqx` ↔ `math.vector`, relative to the entry file's own
directory — decisions 4 and 8); `loadModules`, a depth-first loader that
takes the entry path plus an injected `parse` callback returning each
file's statements **and the dotted module names it depends on**, yielding
dependency order (entry last), parsing a shared dependency once, and
reporting a circular dependency (`Circular module dependency: a -> b ->
a.`) instead of looping. The callback is the seam Phase 5 plugs `import`
into: until the AST has an import node, `Program.fs` always reports no
dependencies, so every real program is still a single-module program.

Name isolation was the one thing the plan didn't spell out, and it falls
straight out of decisions 4 and 6 (a module's top-level names are its
own): two modules must be able to declare the same top-level `helper`
without an "already declared" clash. Done by resolving **each file as its
own unit** (`Resolver.resolveUnit`, returning a `ResolvedUnit` that also
reports which globals the unit itself declared) and then qualifying those
globals (`dep::helper`) by rewriting the already-resolved `Bound` tree
(`Modules.qualifyModule`) — deliberately on the bound tree rather than the
AST, because by then locals are slots, so there is no local-shadows-global
case to get wrong. The entry module is left unqualified, which is what
keeps single-file output byte-identical. The prelude is now resolved once
as its own unit and its declared names seed every user module as ordinary
immutable root globals (same mechanism as `nativeGlobals`), replacing the
old textual `preludeStmts @ stmts` merge; redeclaring `map` etc. is still
the same "already declared" error, and the prelude exemption added in the
Phase 3 follow-up (`resolveWithExemptGlobals`) stays for callers that
still merge textually. `resolveInternal` now returns a record and `resolve`/
`resolveWithExemptGlobals` keep their old tuple shapes, so no existing
call site changed.

`Program.fs` now drives `loadModules` → `linkModules` (prelude, then
modules in dependency order, entry last — the same execution order the
old merge gave). Diagnostics are attributed per module (`[line N]` for the
entry exactly as before; `[math.vector, line N]` for other modules, not yet
reachable from the CLI). One small behavior change: an unreadable input
file is now a clean `Error: cannot read module file ...` with exit 65
instead of an unhandled exception.

Verification beyond the unit tests: every `langspec/examples/*.iqx`
compiles to **byte-identical** `.iqbc` output versus the pre-change
compiler (checked with `cmp`), with zero warnings; and, since no source
syntax can load two files yet, a two-module program (both declaring
`helper`, one also a function using it) was linked programmatically,
written out, and run on the real VM — each module saw its own `helper`
(`4`, `2`, `1`, dependency first). 14 new xUnit tests (`ModuleTests.fs`):
name↔path mapping, single/ordered/diamond loading (diamond parsed once),
cycle and self-cycle detection, load-error propagation, same-name
globals across modules, qualification of variables/functions/classes and
references to them, prelude names staying unqualified and callable from
any module, redeclaring a prelude name still an error, per-module
diagnostic attribution, and a linked program compiling to one chunk.
Known limitation carried over from the prelude merge: runtime errors report
a line number with no indication of which file it belongs to
(the bytecode line table has no file dimension -- added in Phase 5, format v3).

**Phase 5 — Module system: `import` and `pub`-as-export — done.** The
last syntax details decisions 5 and 6 left open were put to the
repository owner when the phase started, and answered:

- **`pub` is a prefix** on a top-level declaration: `pub fun f()`,
  `pub var x` / `pub var x mut = 1`, `pub class C`, `pub trait T`
  (`pub` anywhere but the top level is a compile error).
- **Imports are read-only bindings.** An importer can read an imported
  `var` but never assign to it, even if the exporter declared it `mut`.
- **A name collision is an error, and `as` renames** (a new `as`
  keyword, beyond what decision 5 originally sketched):
  `import math.vector.transpose as vtranspose`. The collision rule —
  against a local declaration, another import, a native or a prelude
  name — is the same "Variable 'x' already declared." error the language
  already raises elsewhere; there is no silent shadowing.
- **`import` is allowed anywhere at the top level of a file** (not inside
  a function, class or block), in any order relative to other
  declarations — imports are collected before resolution begins.

`import` and `as` are new reserved words (no existing fixture used either
as an identifier). Syntax: `Ast.ImportStmt` (a list of `ImportItem`s: the
dotted module path, the imported name — always the **last** segment, per
decision 5 — and an optional alias) and `Ast.PubStmt`, a wrapper around
the declaration it exports, chosen over adding an `IsPub` field to four
different statement cases so that no existing pattern match had to change.
`Parser.fs` gained `PubDeclaration`/`ImportDeclaration`; `import a` with no
member is a parse error.

`Resolver.resolveUnit` now takes the unit's `ImportBinding`s and returns
its `Exports`. A top-level `PubStmt` is unwrapped up front, recording
which names were exported; `pub` declarations are exempt from the
unused-global warning (they are the module's public surface). An imported
name is seeded into the importer's globals as an immutable global — which
is what gives read-only imports and the collision error for free — and an
`aliases` table makes references to it bind to the exporter's *qualified*
global (`math.vector::double`) rather than to the local spelling.
Classes and traits need more than a name: type-checking `extends`/`with`/
`use` and `self.x` in an importer needs the exporter's member sets, but
the exporter's raw class/trait tables mean nothing in another module's
name space (they refer to names local to the exporter). So a `pub class`
exports its fully **effective** member set (own + inherited + mixed in)
and a `pub trait` its fully **flattened** one, and the importer seeds
synthetic entries from those (`ClassInfo.IsImported`, skipped by the
unused-member check, which belongs to the module that declared the
class). Consequently decision 9 held: `extends`/`with`/`use` over an
imported class or trait work with no further special casing.

`Modules.linkModules` resolves modules in dependency order, so every
`import` is checked against an already-resolved exporter: unknown
member (`Module 'm' has no top-level 'x'.`) and importing a non-`pub`
declaration (`'x' in module 'm' is not 'pub', so it can't be imported.`)
are distinct errors, attributed to the importing file. `Program.fs`'s
parse callback now reports each file's dependencies via
`Modules.importedModuleNames`, so `import` drives Phase 4's loader end
to end; a cycle (including one through the entry module) is the loader's
existing error. `resolve`/`resolveWithExemptGlobals` (no linker) reject
a top-level `import` rather than silently dropping it.

Verification: all 15 existing top-level examples still compile to
**byte-identical** bytecode with zero warnings — this is the audit the
plan called for ("every fixture gets audited for the new default-private
rule"): every existing fixture is a single file that nothing imports
from, so none needed a `pub`. A real three-file program
(`langspec/examples/modules/`: a private helper reached through an
exported function, an exported `var`, an imported class extended by a
local subclass, an imported trait `use`d by it, and an `as` alias) was
compiled through the actual CLI and run on the real VM. Error paths
checked through the CLI as well (non-`pub` import, missing member, name
collision, assignment to an import, nested `import`, circular import).
24 new xUnit tests: 7 parser (import splitting/lists/aliases, malformed
imports, `pub` wrapping all four declaration kinds, bad `pub`, the new
reserved words) and 17 linker (binding to qualified globals, alias,
aliasing around a collision, each error above, read-only imports, extend
an imported class, undeclared-property check still applies to a subclass
of an imported class, `use` an imported trait, `pub` unused-exemption,
nested `import`/`pub`, in-isolation rejection, transitive chains).

**Follow-ups requested by the repository owner, same phase** (after the
first pass of this phase had shipped with both as "known limitations"):

- **Unused imports are warned about** (`Import 'x' is never used.`, at the
  import's local name — the alias if there is one), with the same `_`-prefix
  exemption as every other unused check. "Used" means the name is read
  anywhere, including as an `extends`/`with` operand (those resolve like
  any other read), or appears in a `use` clause — trait use is inlined at
  compile time and never goes through `resolveReference`, so the check
  consults every class's/trait's `use` list separately. An import that is
  only assigned to (itself an error) still counts as unused.
- **Runtime errors name their source file.** This is a bytecode **format
  change**: v3 adds a program-wide file table right after the version
  byte (count + names) and, per chunk, a per-instruction-byte file index
  parallel to v2's line table. `Chunk` gained `Files`; `Codegen` gained
  `compileSegments`, which takes each file's resolved statements as a
  separate segment, stamps every instruction with its segment's file
  (a nested function simply inherits the file current when it is
  compiled — a function never spans files), and returns the file table
  for `Bytecode.writeWithFiles` (`compile`/`write` are unchanged
  wrappers using one unnamed file). `Modules.ModuleSource` gained `File`
  (path relative to the entry file's directory, `/`-separated on every
  platform) and `LinkedProgram` gained `Segments`; the prelude is its own
  segment, `<prelude>`. On the VM, `bytecode::load` fills a new
  `Vm::sourceFiles` and `Chunk::files` indexes into it; `Vm::runtimeError`
  now emits `[math/vector.iqx, line 2]`, falling back to a bare
  `[line N]` for a hand-built chunk or an empty file name. This also
  retires `docs/LANGUAGE.md`'s old §15 item 11 (a fault inside a prelude
  function reported a line number into text the user never wrote — now
  `[<prelude>, line N]`).
  While checking this end to end a **pre-existing gap** turned up that
  would have made it incomplete: operand type errors ("Operands must be
  numbers.") and every error raised by a built-in function (`pop` on an
  empty vector, ...) were thrown as bare `RuntimeError`s that never went
  through `runtimeError`, so they carried no location at all — not even a
  line. The operand checks now call `runtimeError`, and `callNative`
  re-raises a native's `RuntimeError` through it, so the call site's
  `[file, line N]` is attached in one central place. **One output-format
  choice worth flagging**: a *single-file* program's runtime errors now
  also show the file (`[main.iqx, line 3]`, formerly `[line 3]`) — the
  alternative of only naming the file for multi-file programs would have
  made the format depend on how many files happened to be involved.
  Compile-time messages are unchanged (`[line N]` entry, `[module, line
  N]` otherwise). 3 + 1 new Catch2 tests (file table loading, file-aware
  error, bare-line fallback, operand-error location), and 11 new xUnit
  tests (v3 writer layout, per-byte file indices, stamping per module
  including the prelude, relative `/`-separated names, and the
  unused-import cases above).

Known limitations, deliberately not papered over:

- **A used trait's body is inlined into the importing class**
  (decision 12's PHP-style inlining), so it is resolved in the
  *importer's* name space: a `pub trait` whose method calls a private,
  module-local function of its own file will not find it from another
  module. Classes don't have this problem (their methods stay compiled
  in the exporting module). Fixing it needs trait bodies to carry their
  home module's bindings; flagged for the repository owner rather than
  guessed at.

**Phase 6 — Array-manipulation stdlib behind imports — done.** Open
question 1 was the one the repository owner had been dreading and
postponing ("no clearly better answer"), so it was analysed and discussed
before anything was built, and then decided in their words:

- *Users must be able to define their own `sort`* (and `max`, `add`, ...).
- *An import line is acceptable ceremony.*
- *"No silent shadowing" is a language-wide principle* — which rules out
  the Rust-style alternative of an implicit prelude that user code may
  silently shadow.
- *`print` is core and always available* with no import.
- Layout (asked separately): **by category under `std`** —
  `std.vector` (`push`, `pop`, `length`, `reverse`, `concat`, `map`,
  `filter`, `reduce`, `sort`) and `std.matrix` (`transpose`, `multiply`,
  `add`, `subtract`, `elementwise`), with `concat` imported like the rest.
  Later versions' libraries (`std.string`, `std.math`, ...) slot in under
  the same root.

Two things the discussion surfaced that shaped the design. First, the
spelling question was already settled by decisions 5/9 (names are imported
bare; there are no qualified references, and `|>` takes a bare name on its
right), so the original roadmap sketch `Vector.map` is dead: "namespace"
means "lives in a module you import from". Second, the reservation problem
compounds — `add`/`sort`/`length`/... were already unusable as user names,
`max` would have been lost to `0.4`'s math library, and every future ambient
name would have been a breaking change for any program that happened to use
it; import-gated additions never are.

Implementation. `Prelude.fs` became `Stdlib.fs`: a `BuiltinModule` is a
list of **natives** (VM-registered, exported under their bare runtime
names) plus an Iqalox **source** (compiled like any module, but only when
imported — a program that imports nothing no longer carries the five prelude
functions it used to: `print 1` compiles to 106 bytes of bytecode instead of 3,266). The loader
(`Modules.loadModulesWith`) finds a built-in module by name; a user file
that would collide with one (`std/vector.iqx`) is a compile error, not an
override, following the same principle. The Resolver's ambient natives
shrank to just `print`.

Making user-defined `sort` *safe* needed real engineering, not just removing
a reservation. The runtime's global table is flat and natives sit in it under
bare names, so a user's own `push` in an unqualified entry module would have
silently clobbered the native that `std.vector.map` calls. So **every module
is now qualified, the entry module included** (`main::sort`); single-file
programs therefore no longer compile to the same bytecode as before, and
the VM strips the `module::` prefix from the one place a global's name
reaches a user (`Undefined variable 'x'`, `Variable 'x' accessed before
being assigned a value`).

A second consequence: the resolver deliberately leaves an unknown global to
the runtime, and the VM still registers every native, so a program calling
`push` *without* importing it would have compiled and worked — making the
gating decorative. `Stdlib.importHints` (every exported name → its module)
turns that into a compile-time error that says what to do: `'push' is part
of the standard library and isn't in scope; add 'import std.vector.push'.`
Built-in modules' own warnings are dropped and their errors reported as
internal compiler errors; the prelude exemption added in Phase 3's follow-up
(`resolveWithExemptGlobals`) had no remaining purpose and was removed.

Migration (a breaking change, accepted — pre-`1.0`): the seven example
programs that used stdlib names gained `import` lines (the compiler's hint
errors named every missing one), and `docs/LANGUAGE.md` §13 was rewritten
around the import-gated model. Verification beyond the unit tests: the
**old** compiler (from `main`) compiling the **old** examples was diffed
against the new compiler compiling the new examples — identical output for
all 15 — and the multi-file example, user-defined `sort`/`max`, forgotten
imports, import-vs-own-declaration, and the shadowing check were exercised
through the real CLI and VM. New tests: `StdlibTests.fs` (17: built-in
sources parse and match their declared exports, only `print` is ambient,
user-defined library names allowed, the hint error, native vs source
bindings, `as`, collision, unused std import warns, modules compiled only
when imported, built-in diagnostics flagged, file-shadowing error, the whole
library linking and compiling); the Phase 4/5 tests that assumed an
unqualified entry module or a prelude were reworked, and the dead
`resolveWithExemptGlobals` and its two tests removed (net 309 xUnit; Catch2
unchanged at 114, since `vm/` only gained the message-prefix stripping).

**Review follow-up, same phase: grouped imports.** The repository owner
flagged on the PR that importing five names from one module meant writing
`import std.matrix.transpose, std.matrix.multiply, ...`, repeating the
module path every time — "incredibly verbose, and maybe not how a
well-designed language forces the user to write their imports." Options
were put to them and they chose **braces**: `import std.matrix.{transpose,
multiply as mul, add}`, one group per module, several comma-separated, `as`
inside the braces, newlines inside the braces skipped so a long list can
span lines (the scanner emits a `Semicolon` token for every newline, with
lexeme `"\n"` unlike a real `;`, so only the former is swallowed, and only
inside a group). Rejected, with reasons: a **wildcard** (`import m.*`)
would make adding a function to a library module a breaking change for any
program that wildcard-imports it and also declares the same name — fatal
once `1.0` is the stability line; **module-as-namespace** would reverse the
decision that imported names are used bare and break `|>`, whose right side
must be a bare name; and the **colon form** (`import m: a, b`) can only name
one module per statement. A group expands in the parser to one `ImportItem`
per name, so `Resolver`/`Modules` needed no change. 11 new tests (9 parser,
2 linker).

**Review follow-up, same phase: stop keeping frozen copies.** A second
review comment on the PR pointed out that `docs/LANGUAGE.md` had been
edited in place for `0.3` while the plan was still to fork it as the frozen
`0.2` reference at the end — so the fork would not be `0.2` as released —
and asked whether archives are worth keeping at all when git tags exist.
Checked: tags `0.1.0-poc` through `0.1.3-poc`, `0.1.0` and `0.2.0` exist and
each is an exact snapshot (the `0.2.0` tag alone holds the `0.1` and `0.2`
language references, both grammars, the `0.1` snapshot and the pre-renumbering
planning snapshots). The repository owner decided to **rely on tags**. Removed:
`docs/LANGUAGE-0.1.md`, `docs/LANGUAGE-POC.md`, `langspec/archived/`,
`langspec/versions/0.2/`, and `langspec/versions/0.1`'s README and grammar.
Kept: `langspec/versions/0.1/examples/`, because CI's `poc/` smoke test and
the `poc/` release bundle read it, and the plan documents, which are history
in their own right (each older plan got a one-line banner explaining that
paths it mentions now live in tags). New `docs/EARLIER-VERSIONS.md` is the
single table of which tag and path holds what; every live reference
(`CLAUDE.md`, `README.md`, `ROADMAP.md`, `langspec/README.md`,
`docs/LANGUAGE.md`, the grammar) now points there. `docs/LANGUAGE.md` is
retitled "(0.3, in progress)". Consequence worth knowing: `poc/`'s own
language reference is now only reachable via a tag (`0.1.3-poc`), not in
the tree beside the code it documents. Rule going forward: tag every release.

Phase 4/5 entries above describe the module machinery as first built (a
textually merged prelude, an unqualified entry module); read them as history
— Phase 6 is what changed those two things.

**Phase 7 — First set of compiler optimizations — done.** Open question 2's
proposal was adopted as written (constant propagation plus dead code
elimination), and the repository owner scoped the phase to *compiler*
work only: VM-level performance (global-slot resolution, dispatch) was
deliberately left for `1.0`.

*Method.* The benchmark corpus (`bench/micro/`: seven microbenchmarks;
`bench/realistic/`: eight Advent-of-Code-style programs with named
constants, helper functions, grids and DP) and the harness
(`bench/run.sh`) were committed **before** any optimizer code existed, so
the measurement could not be tuned to the result. Wall-clock time is too
noisy here (the min/median spread alone is 10%+), so the headline metric
is executed CPU instructions under `valgrind --tool=callgrind`, which is
deterministic. The VM is built **Release** for all numbers (the CMake
default was Debug, ~15× slower; it is now Release, which is why the
first benchmark attempt reported 43 s for a loop that takes 2.9 s).

*What landed* (`compiler/src/Optimizer.fs`, run between
`Modules.linkModules` and `Codegen`; `iqaloxc --no-optimize` skips it):

- Constant folding on literals: `+ - * /` (not when the result is
  non-finite, nor `/` by zero), comparisons, same-type `==`/`!=`, unary
  `-`/`!`, `??` with a literal left side, and `and`/`or`/ternary whose
  condition is a `nil`/`bool` literal. `%` and `^` are never folded
  (duplicating their VM semantics is not worth the risk); a division by
  zero stays a runtime error.
- Global constant propagation: a top-level `var x = <literal>` that is never
  assigned/`++`/`--`'d anywhere in the program replaces every read that is
  *textually after* the declaration. The "after" rule is what keeps a
  read-before-definition raising "Undefined variable" exactly as before.
  The declaration is kept, and chains work (`var b = a * 3` folds, then
  becomes a constant too). Locals are not propagated: `GetLocal` and
  `Constant` cost the same.
- Dead code: statements after an unconditional `return`, bare-literal
  expression statements (what a folded `debug ? print x : nil` leaves), and
  the `nil; return` epilogue after a body that already ends in `return`
  (`Codegen.compileSegmentsWith`).
- A bug fix this exposed: `FunctionState.AddNumberConstant` deduplicated
  with `=`, which treats `0.0` and `-0.0` as the same constant; it now
  compares bits.

*Results* (callgrind instructions, `--no-optimize` → default):

| program | change |
|---|---|
| timeconv (named unit constants) | −13.6% |
| life (`width`/`height`/`alive`/`dead` constants) | −10.4% |
| sieve | −4.8% |
| matmul | −2.7% |
| queens | −1.8% |
| collatz, knapsack | ≈0 (−0.05%, −0.02%) |
| micro (loops, fib, closures, methods, vector ops) | ≈0 in instruction terms; bytecode 0–2% smaller |

The honest reading: the pass pays off exactly where a program names its
constants (`var width = 60` read in a hot loop), a few percent to ~14%;
it does nothing for code that already uses literals or locals, and it
does not touch what actually dominates the microbenchmarks — every
top-level variable access is a `GET_GLOBAL` string-hash lookup in the VM.
That is the real lever and is `1.0` work. Wall-clock medians moved by
amounts inside the run-to-run noise except where the instruction counts
also moved. One planned side measurement (cost of the `module::name`
qualified global names against the Phase 5 build) was not done.

*Correctness.* `compiler/tests/OptimizerTests.fs` asserts the rewrites and,
as importantly, the non-rewrites (assigned/incremented globals, reads
before the declaration, division by zero, overflow, `-0`).
`scripts/optimizer-differential.sh` compiles every example, benchmark, four
hand-written error cases (division by zero, read-before-define,
multi-line error lines, dead code) and 210 random constant-expression
programs both ways and requires identical stdout, stderr and exit status:
244 programs, no differences.

**Phase 8 — Docs, and a one-command runner — done.** *(Revised during Phase 6: no fork.)* The original plan
was to fork `docs/LANGUAGE.md` into a frozen `docs/LANGUAGE-0.2.md`, but by
then the file had been edited in place seven times for `0.3` — so the fork
would have captured a half-`0.3` document, not `0.2` as released — and the
repository owner pointed out that release tags already preserve exact
snapshots. The project therefore **stopped keeping frozen copies in the
tree** (see `docs/EARLIER-VERSIONS.md`). Phase 8 is now: tag `0.3.0`;
review `docs/LANGUAGE.md` end to end and drop its "in progress" marker;
verify every top-level `langspec/examples/*.iqx` fixture end to end;
`ROADMAP.md` marks `0.3` delivered and moves the active-target goalposts
to `0.4`.

*Added in Phase 8 at the repository owner's request:* running a program
used to take two commands. `iqaloxc run <file.iqx>` now compiles to a
temporary `.iqbc`, runs `iqaloxvm` on it (inheriting stdin/stdout/stderr,
exiting with the VM's status; a compile failure never starts the VM; the
temp file is always deleted), and finds the VM via `IQALOX_VM`, then next
to the compiler executable, then the `PATH`. The bundle ships a two-line
launcher, `iqalox` (sh) / `iqalox.cmd`, so users type `iqalox prog.iqx`.
This is a first pass: no arguments are passed through to the program (the
VM has no command-line-argument support yet; that is `1.0`), and no REPL.
`release.yml` also now bundles the current top-level `langspec/examples/`
(including `modules/`) rather than the stale `0.1` set. Verified by
running all 16 examples through `iqaloxc run`, and an end-to-end check of a
self-contained single-file `iqaloxc` + `iqaloxvm` + launcher laid out like
the bundle, run from another directory.

## 6. Testing strategy

Same split `0.1`/`0.2` established: xUnit (`compiler/tests/`) for scanner/
parser/resolver/codegen-level correctness (the disassembler remains the
primary way to verify codegen output without needing the C++ VM), Catch2
(`vm/tests/`) for VM-level execution and runtime-error behavior, and a
manual, per-phase sweep of `langspec/examples/*.iqx` through the real
`compiler/`+`vm/` toolchain (no automated cross-implementation conformance
suite — retired during `0.2` Phase 7, `docs/PLAN-0.2.md`). Phase 4/5
(modules) additionally need multi-file fixture tests — a new
`langspec/examples/`-adjacent fixture layout (or a dedicated test-only
directory under `compiler/tests/`) for programs that span more than one
`.iqx` file, since every existing fixture is single-file by construction.
Phase 7 (optimizations) additionally needs before/after behavioral
equivalence tests — same program, same observable output, verified
across the optimization pass being on par with the same program compiled
without it — not just new disassembler instruction-sequence assertions.

## 7. Risks

- **Module support is, by a wide margin, the largest single-version
  toolchain change since `0.1`'s original F#/C++ split.** Even with `vm/`
  entirely out of scope (decision 7) and the CLI shape unchanged
  (decision 8), `Program.fs` gains real dependency-graph resolution for
  the first time — cycle detection, multi-file error attribution (an
  error in an imported file needs to report *that* file's own line
  number and path, not just a line number the way `docs/LANGUAGE.md` §15
  item 12 already flags as a gap even for the single-extra-file
  `Prelude.fs` case today), and a meaningfully bigger `Resolver.fs`. If
  Phase 4/5's actual scope turns out larger than this document's
  estimate once implementation starts, the same discovery mid-phase
  `0.2`'s Phase 8 (`with`-mixin C3 scope-check) and Phase 7 (conformance-
  suite retirement) both had, raise it the same way: a direct, concrete
  `AskUserQuestion` laying out the real cost, not a silent scope
  expansion.
- **The multi-file error-attribution gap above isn't new, just wider.**
  `docs/LANGUAGE.md` §15 item 11 (renumbered from item 12 when Phase 2
  resolved item 5, single-generator-only comprehensions) already
  documents that a runtime error
  from inside `Prelude.fs`'s own functions reports a line number relative
  to *that* file, not the user's — a real, disclosed, not-yet-fixed
  limitation. Modules multiply the number of files a compiled program can
  span from two (user file + prelude) to arbitrarily many; whether this
  phase is the right time to finally solve real multi-file source-
  position tracking, versus continuing to log it as a known limitation,
  is worth a direct check-in once Phase 4 is under way rather than
  assuming either answer.
- **Slice grammar (`v[a:b]`) is new punctuation-in-brackets syntax**, the
  first since `0.2`'s indexing/spread/cons work. Low risk on its own (no
  whitespace-adjacency-style ambiguity expected, unlike `0.2` decision
  6's indexing-vs-call collision), but worth a real disambiguation check
  against existing bracket forms (plain indexing, cons, comprehension)
  during Phase 1 implementation rather than assumed clean from this
  document's grammar sketch alone — `0.2`'s own experience (the
  indexing-vs-call collision wasn't caught until Phase 1 implementation,
  not this kind of planning document) is exactly why.
