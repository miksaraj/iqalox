# Plan: getting to 0.2

> **Note (added in `0.3` Phase 6):** this log refers to files that are no longer kept in the tree
> — `docs/LANGUAGE-0.1.md`, `docs/LANGUAGE-POC.md`, `langspec/versions/<n>/`, `langspec/archived/` —
> by their old paths. Git tags preserve them; see [`EARLIER-VERSIONS.md`](EARLIER-VERSIONS.md).

This is the `0.2` counterpart to `docs/PLAN-0.1.md`: a living plan for the
next feature set on top of `0.1`'s compiler frontend (F#, `compiler/`) and
bytecode VM backend (C++23, `vm/`). Like the `0.1` plan, this document
tracks resolved decisions, flags open ones rather than guessing at them
(per `CLAUDE.md`), and keeps a running status table and sequencing
checklist as work lands.

`0.1-poc` (`poc/`) is **not** getting a `0.2`. It stays frozen at its own
feature set as a historical reference implementation (`CLAUDE.md`); every
feature below targets `compiler/`+`vm/` only. See §2 item 6 for what that
implied for `langspec/examples/` and the (since-retired, Phase 7) Phase 9
conformance suite.

## 0. Scope: what "0.2" means here

Per `ROADMAP.md`'s `0.2` entry (formerly `0.2-poc`, retired as a separate
PoC stage and folded in as the natural home for "class system
completeness" work):

- Matrix support (alongside existing vector/array support)
- Array-manipulation standard library, including list comprehensions and
  Julia-inspired array manipulation
- Anonymous function literals (lambdas) — distinct from the named
  nested-function closures `0.1` already has
- Variadic unpacking (`...`)
- Getters and setters — resolved this round as **`pub`/`mut` visibility
  and mutability modifiers**, not custom accessor bodies (a real
  mid-review redesign — see §1)
- Mixin support (`with`) and trait support (`trait`/`use`) — resolved this
  round as a PHP-style/Scala-style split (§1)

## 1. Design decisions (resolved)

Resolved across this planning round and a PR review pass on the first
draft (2026-07-06 to 2026-07-07) — recorded with the reasoning, not just
the answer, per this project's convention. A few items below are real
revisions to what an earlier draft of this document said (one outright
reversal, one full redesign, one genuinely new extension) rather than
incremental refinements — each says so explicitly rather than silently
overwriting the earlier text.

1. **Lambda syntax is `(a, b) -> expr`** — arrow syntax, single-expression
   body only (no block body, no `return`; anything needing statements
   stays a named `fun`). No new keyword; `Minus` immediately followed by
   `Greater` scans as a new `Arrow` token, the same two-character-lookahead
   pattern `Scanner.fs` already uses for `->`'s siblings (`??`, `?:`,
   `++`, `--`).
2. **Cons (`[item | list]`) and list comprehensions share `[expr |`, told
   apart by lookahead on the generator marker.** After `|`, if what
   follows matches a generator clause (`x <- xs`), it's a comprehension;
   a bare expression makes it cons. `[x | xs]` conses `x` onto `xs`;
   `[x | x <- xs]` comprehends. `xs` in the cons case must be a vector at
   runtime (a runtime type error otherwise, matching how every other
   vector operation reports a bad-type argument).
3. **List comprehensions ship with a single generator, no guards, for
   `0.2`.** `[expr | pattern <- iterable]` only. Multiple comma-separated
   generators (nested/Cartesian iteration) and boolean guards are the
   *eventual* shape (the original `[x + y | x <- xs, y <- ys, x not y]`-
   style sketch in `ROADMAP.md`) but explicitly not this pass — now
   tracked as its own line item on `ROADMAP.md`'s `0.3` entry rather than
   just a mention here, so it doesn't quietly fall off the plan.
4. **The generator token is `<-`** (Haskell-style), not `in`. Unambiguous
   since nothing else in the grammar uses it; a new `LeftArrow` token.
5. **Matrices are nested vectors (`[[1, 2], [3, 4]]`), not a new literal
   syntax.** No Julia-style space-separated-row literal (`[1 2; 3 4]`) —
   that would make whitespace significant inside `[...]` for the first
   time in the grammar, a much bigger change than the feature needs. A
   matrix is a vector of vectors with dedicated stdlib functions (multiply,
   transpose, elementwise ops — exact list still open, §2) recognizing the
   shape; Julia-flavored *API*, not Julia-flavored *literal syntax*.
6. **Indexing ships as `v[i]` read/write only, 0-based, no negative
   indices, no slicing.** `v[i]` and `v[i] = x`; out-of-range is a runtime
   error, matching every other bounds/type check's error style. Negative
   indices and slice syntax (`v[a:b]`) are deferred to `ROADMAP.md`'s
   `0.3` entry ("array-manipulation standard library improvements"),
   named explicitly there now rather than just implied. A non-integer or
   negative-*numeric* index (there being no separate integer type,
   `v[1.5]`) is also a runtime error, matching every other arithmetic/
   domain check's error style — an obvious edge case decided the same way
   as every other operator's type checking, not a fresh design axis.

   **Addendum, found during Phase 1 implementation, not caught by this
   document's original grammar draft or `langspec/SYNTAX_GRAMMAR.md`'s
   Phase 0 rewrite: `v[0]` is byte-for-byte ambiguous with `0.1`'s
   existing "bare identifier/property + vector-literal argument" call
   syntax** (`concat [1, 2]`, used throughout every existing example) —
   both parse to the identical token stream (`IDENTIFIER LEFT_BRACKET ...
   RIGHT_BRACKET`), so `Parser.fs` can't tell "index `v` at 0" from "call
   `v` with the one-element vector argument `[0]`" without a tiebreaker.
   Resolved via **whitespace adjacency**: a `[` with no space before it
   (`v[0]`) is always postfix indexing; a `[` preceded by a space
   (`v [0]`, `concat [1, 2]`) is always the pre-existing vector-literal-
   argument call, completely unchanged. Uses `Token.Column`/`Lexeme`
   (already tracked since the column-tracking work predating `0.2`) — no
   new scanner state needed. This is the first place whitespace is
   *ever* significant in Iqalox's grammar; flagged here explicitly since
   it's a real language-design tradeoff, not an implementation detail,
   and the tradeoff was made by necessity (the alternative was a breaking
   change to `0.1`'s call syntax) rather than by preference.
7. **Variadic unpacking (`...`) ships as vector-literal spread only:
   `[...a, ...b]`.** Splices `a`'s and `b`'s elements into the new vector.
   Variadic *parameters* (`fun f(...args)`) and call-site spread
   (`f(...someVector)`) are explicitly **out of scope** for `0.2` — real
   tension with `0.1`'s stated design ("a call's arity is fixed by the
   callee's declaration, not inferred from how many arguments happen to be
   written") that wasn't asked for or resolved this round. Both are now
   logged under `ROADMAP.md`'s new "Language feature ideas under
   consideration" section rather than dropped from sight.
8. **Getters/setters are scrapped in favor of `pub`/`mut` visibility and
   mutability modifiers — a real, mid-review redesign, not an incremental
   tweak to the original forced-accessor idea.** Properties are declared
   in the class body with `var`-style declarations, extending the exact
   same keyword `0.1` already uses for local/global variables into a new
   grammar position (class bodies currently only accept method
   declarations; `0.2` adds property declarations alongside them):
   - `var name` — private, immutable.
   - `var name mut` — private, mutable.
   - `var name pub` — public, immutable.
   - `var name pub mut` — public, mutable.

   No new keyword needed for `mut` (already `Token.fs`'s `Mutable`,
   reused as-is); `pub` is the one genuinely new keyword this decision
   needs. This directly foreshadows `0.3`'s real module support: `pub` is
   expected to apply to classes themselves then too (private-by-default at
   the module level, defining what a module actually exports) — called
   out explicitly on `ROADMAP.md`'s `0.3` entry now so it isn't forgotten.
   (Module support was originally slated for `0.5`; moved forward to `0.3`
   so the string/math/data-structure/I/O stdlib phases starting `0.4` can
   build on it — see `ROADMAP.md`.)

   **Necessary consequence, not separately asked about: `0.1`'s "a field
   springs into existence on first assignment" model is gone.** Decision
   10's access table only makes sense if `self.x`/`instance.x` always
   refers to a *declared* property with known `pub`/`mut` flags to check
   against — an undeclared, implicitly-created field would have no
   visibility/mutability metadata at all, silently bypassing the whole
   mechanism. So every property a `0.2` class uses must have a `var`
   declaration somewhere in the class body; a bare `self.x = value` with
   no matching declaration is a compile-time error, the same category of
   error as assigning to an undeclared local. This is inferred from
   decision 10's own internal logic rather than a separate, explicit
   answer — flag it if declared-only state wasn't actually intended.
9. **Property immutability is enforced at runtime, "assign at most once,
   ever" — not compile-time, and not "once per instance is fine as long as
   it's early."** `0.1`'s local-variable immutability is a compile-time
   check because `Resolver.fs` can statically enumerate every assignment
   site for a given local. A class property assigned via `self.x = value`
   scattered across arbitrarily many methods (and, once inheritance is
   involved, subclass methods too) isn't nearly as tractable to verify
   statically, so this extends the same mechanism `0.1` already has for
   `undef`: an immutable property starts unset, transitions to set on its
   first assignment, and any assignment after that is a runtime error —
   from anywhere, internal or external. This also resolves a real gap in
   reusing `var`'s exact rule literally: an immutable local `var x` with
   no initializer is already a **parse error** in `0.1` ("no way to ever
   give it a value") — but a class property commonly gets its value from
   a constructor parameter, not a fixed literal at the declaration site.
   Property declarations therefore need their own, more permissive rule:
   no initializer required at declaration; the first assignment (wherever
   it happens to occur) sets it, and reading it before that is a runtime
   error, the same shape `0.1`'s `undef` already has for `var mut` locals
   — just without needing `mut` for that leniency.
10. **Access is gated by an internal/external boundary, the same rule for
    both `pub` and `mut`.** Any method belonging to the class — or,
    pending §2.3's confirmation, a subclass — can freely read/write `self.x`
    regardless of `pub`/`mut`, exactly like `0.1`'s fields work internally
    today; only access via an *external* reference (`instance.x` from
    outside the class) is gated by the declared modifiers:

    | Declaration | Internal (`self.x`) | External (`instance.x`) |
    |---|---|---|
    | `var x` | write once, then read-only | invisible |
    | `var x mut` | read/write, unlimited | invisible |
    | `var x pub` | write once, then read-only | read-only |
    | `var x pub mut` | read/write, unlimited | read/write |

    No accessor bodies, no custom logic on get/set — external access to a
    `pub` property is a direct read (or, for `pub mut`, read/write) of the
    same backing value internal access already uses, subject to the
    mutability check above. This is considerably simpler than the
    get/set-block design an earlier draft of this document had; there's
    no design work needed here for "what does a getter return," "can a
    setter have side effects," etc., because there's no user-written
    accessor logic at all.
11. **Methods are private by default too, using the same `pub` modifier —
    a real breaking change to every existing example script, not
    additive.** A method declared without `pub` (`quack() { ... }`) is
    only callable from within the class (or, pending §2.3, a subclass);
    calling it from outside is an error. `init` is always implicitly
    callable from outside regardless of any `pub` annotation — a
    completely unconstructable class isn't a sensible default, and
    nothing in this round's decisions suggested wanting one. Every
    existing `langspec/examples/*.iqx` method call from outside its own
    class (e.g. `duck.quack()`) needs an explicit `pub quack() { ... }`
    from `0.2` onward. Flagged prominently in §7 — this is discovered and
    named now, not found the hard way partway through Phase 7.
12. **Mixins/traits split by *which keyword*, matching each keyword's own
    etymology — corrected from this document's first draft, which had it
    backwards.** `trait T { ... }` composed via `use` is **PHP-style
    static copying** (PHP's own `trait`/`use` keywords do exactly this);
    a separate, plainer `with`-only mixin form (`class C extends Base
    with M1, M2 { }`) is **Scala-style dynamic linearization** (Scala's
    own `with` keyword does exactly that). Concretely: `use`d traits'
    members are copied in once at class-declaration time, extending the
    same static method-table-copy mechanism `0.1`'s `extends` already
    uses, no runtime dispatch chain involved; `with`-listed mixins compose
    via an ordered linearization chain (exact algorithm still open, §2),
    closer to real multiple inheritance. Both forms are usable "anywhere"
    per the original answer, but see §2 for why `0.2` can only actually
    deliver the class-scoped case (module-level composition needs
    `module` itself, which isn't real until `0.3` — now called out
    explicitly on `ROADMAP.md`'s `0.3` entry).
13. **`langspec/` versioned-snapshot subdirectory is `langspec/versions/<version>/`**
    (PR review, resolving the naming-collision question raised in this
    document's first draft). The current version's grammar docs and
    examples stay at `langspec/`'s top level as always; once a version is
    superseded, its snapshot moves to `langspec/versions/<version>/` (e.g.
    `langspec/versions/0.1/`) rather than reusing the bare version number,
    which would collide with (or be confused for) the pre-existing
    `langspec/archived/<version>/` directories — those hold unrelated,
    pre-renumbering *planning-era* snapshots per `ROADMAP.md`'s own
    renumbering note, and keep their existing name/meaning unchanged.

## 2. Open questions (flagged, not decided)

Per `CLAUDE.md`, these block the phases that depend on them (noted per
item) — not the whole plan. Add to this list rather than silently
resolving, the same convention `docs/PLAN-0.1-POC.md` and
`docs/PLAN-0.1.md` §2 already used.

1. ~~Static (`trait`/`use`) conflict resolution.~~ **Resolved by the
   repository owner when Phase 8 began: compile-time error on any
   conflict**, not PHP's `insteadof`/`as` (not in the frozen grammar,
   `langspec/SYNTAX_GRAMMAR.md`) and not silent last-`use`d-wins. Two
   `use`d traits sharing a member name, or a `use`d trait sharing one
   with the class's own superclass, is a compile-time error the user
   must resolve by renaming or by declaring the member directly on the
   composing class (which always silently wins — that's normal
   overriding, not a conflict). See Phase 8's own entry in §5 for the
   implementation (`Resolver.fs`'s `checkNoConflicts`).
2. ~~Dynamic (`with`) linearization algorithm.~~ **Resolved by the
   repository owner when Phase 8 began, after a follow-up on the real
   cost: ship a simplified approximation now, defer full C3.** `with`
   composes via the same kind of flat, compile-time-conflict-checked
   copy as `use` — just from a real class's already-compiled runtime
   method/property table (a new `Mixin` opcode, mirroring `Inherit`)
   rather than from a compile-time-only trait declaration. `super`
   continues to resolve only against the `extends` superclass; it does
   not chain through `with`-mixins under this approximation. Real C3
   (a computed per-class Method Resolution Order, live MRO-walking
   dispatch, and a `super` that resolves relative to the *runtime*
   receiver's actual MRO position rather than a single value captured
   once at class-declaration time) is logged as a genuine future
   direction on `ROADMAP.md`'s "Language feature ideas under
   consideration" section rather than attempted here — its blast radius
   turned out to be much larger than the question's own framing
   suggested (a foundational redesign of dispatch/`super` for every
   `with`-using class, not an incremental addition), confirmed with the
   repository owner via a dedicated scope-check before proceeding either
   way. See Phase 8's own entry in §5 for the implementation.
3. ~~Does privacy (properties *and* methods) extend to subclasses, or stop
   at exactly the declaring class?~~ **Resolved by the repository owner
   when Phase 7 began: protected-like.** A subclass's own methods count as
   internal access to a superclass's private/non-`pub` members, the same
   as C++/Java/C#'s `protected` — not stricter (a subclass is not treated
   as just another external caller). Concretely: `self.x`/`self.method()`
   from inside *any* method, in the declaring class or any descendant, is
   internal access, full stop — `Codegen.fs` implements this as a purely
   syntactic check (is the `Get`/`Set`'s object expression exactly `self`)
   with no new class-hierarchy bookkeeping needed at all, since "internal"
   never depended on *which* class in the hierarchy actually declared the
   member. A private method fully participates in dynamic dispatch/
   overriding, exactly like a `pub` one — it's simply unreachable via an
   *external* reference, never non-virtual. See Phase 7's own entry in §5
   for the implementation.
4. ~~Exact array-manipulation stdlib surface.~~ **Resolved during Phase 5**
   (see its own entry in §5): `length`, `push`, `pop`, `reverse`, `map`,
   `filter`, `reduce`, `sort` — all plain global functions, not methods
   (`0.1`'s object model has no method-dispatch-on-primitives concept at
   all). `push`/`pop` mutate in place; the rest return a new vector.
5. ~~Exact matrix stdlib surface.~~ **Resolved during Phase 6** (see its
   own entry in §5): `transpose`, `multiply`, `add`, `subtract`,
   `elementwise fn, a, b` — matrix-only (exactly 2D), no operator
   overloading, dedicated named functions matching Phase 5's array
   stdlib precedent.
6. ~~What happens to the Phase 9 conformance suite once `langspec/
   examples/` moves to `0.2` syntax.~~ **Resolved during Phase 7, and more
   drastically than this question's own framing anticipated**: it doesn't
   just need re-pointing at `langspec/versions/0.1/` (decision 13) — Phase
   7's decisions 8-11 are a big enough breaking change to the object model
   that `compiler/`+`vm/` can no longer run `poc/`-era *class* fixtures at
   all (confirmed concretely: `langspec/versions/0.1/examples/classes.iqx`
   stopped compiling the moment decision 8's addendum landed, since it
   assigns to an undeclared `self.name` with no `pub` anywhere). Given
   that, the repository owner's explicit call was to **retire
   cross-implementation conformance testing entirely** rather than chase a
   moving target release after release — `scripts/conformance-test.sh` and
   `scripts/phase7-run-smoke-test.sh` (0.1's Phase 9) are both deleted, and
   their two `.github/workflows/ci.yml` jobs removed. Pre-`1.0`, an earlier
   version's fixtures are historical artifacts, not something new work is
   expected to stay compatible with. See `CLAUDE.md`'s `compiler/`/`vm/`
   testing bullet for where this is now documented going forward. No
   longer blocks anything — Phase 9 has one less item on its plate.

## 3. Grammar and architecture additions (overview)

No new implementation stack this time — everything below extends the
existing `compiler/`+`vm/` pipeline from `docs/PLAN-0.1.md` §3.

**New tokens** (`compiler/src/Token.fs`/`Scanner.fs`): `Arrow` (`->`),
`LeftArrow` (`<-`); `Ellipsis` (`...`, already scanned since `0.1-poc`,
just never given grammar until now); a new `pub` keyword. `mut` needs no
new token — `Token.fs`'s existing `Mutable` (already used by `var x mut`)
is reused as-is for property declarations.

**New AST/Bound node shapes** (`Ast.fs`/`Bound.fs`): an `Index`/`IndexSet`
expression pair (parsed as a postfix `[...]` on any primary, distinct from
a vector *literal*'s leading `[...]`); a `Lambda` expression (parameter
list + single body expression, resolved exactly like a named function's
parameter list/closure capture, just with no name to bind); a `Cons`
expression and a `ListComprehension` expression (both `[...]`-delimited,
told apart per decision 2's lookahead); a `PropertyDecl` class-member
shape alongside the existing `FunctionDecl` (name, `pub`/`mut` flags, no
body — contrast with the get/set-block shape an earlier draft of this
document had); `pub` as an optional flag on `FunctionDecl` itself, for
method visibility; `with`-mixin and `trait`/`use` composition on
`ClassStmt`.

**`Resolver.fs`**: lambdas resolve like an anonymous `FunctionDecl` (same
scope/slot/upvalue machinery `0.1` already has for nested named
functions); `self.x` vs `instance.x` needs a genuinely new concept this
version — "is this access happening inside a method of the class (or,
pending §2.3, a subclass) the member belongs to" — to implement decision
10/11's internal/external split, since `0.1`'s `GetProperty`/
`SetProperty`/method-call resolution never distinguished internal from
external access at all; mixin/trait member resolution happens at
class-declaration time -- **as actually built in Phase 8, `use`d traits'
members are inlined entirely at compile time in `Resolver.fs` (a trait
has no runtime representation at all), while `with`-listed mixins extend
the existing superclass-method-table-copy mechanism (`vm/src/vm.cpp`'s
`Inherit` opcode handler) via a new sibling `Mixin` opcode, since a
mixin is a real, independently-instantiable class whose members only
exist once its own bytecode has actually run** (§2.2's algorithm
question resolved as a simplified, non-C3 approximation of "linearized
chain" — see Phase 8's own entry in §5 for the reasoning and the
follow-up scope-check that led there).

**`Codegen.fs`**: new opcodes for indexed get/set (bounds-checked at
runtime, matching the existing `GetProperty`/`SetProperty` error-reporting
style); `Closure` already covers lambdas with no new opcode needed, since
a lambda is just a nameless `FunctionConstant`; cons compiles to
build-a-new-vector-by-prepending (no dedicated opcode either — expands to
existing `BuildVector` plus copying the tail vector's elements); a list
comprehension desugars to a loop that builds a vector via repeated
`BuildVector`-equivalent pushes, closer to how a `for` loop already
compiles than to anything genuinely new; vector-literal spread
(`[...a, ...b]`) is a `BuildVector`-time flattening step; `GetProperty`/
`SetProperty`/method-call opcodes gain a visibility + (for properties)
an assigned-once check, but need no accessor-invocation logic at all,
per decision 10's simplification.

**`vm/`**: `ObjVector` gains bounds-checked indexed get/set (it already
has a `std::vector<Value> elements` — indexing needs no new heap-object
shape, just new opcode handlers); `ObjInstance`'s `fields` map gains a
per-field "has this been assigned yet" bit for immutability enforcement
(decision 9) plus whatever visibility metadata `GetProperty`/`SetProperty`
need to check decision 10's internal/external rule; mixin/trait
composition extends `Vm::run`'s existing `Inherit` handling.

## 4. Feature checklist (parity target)

Every row starts "not started" — ticked off as `compiler/`+`vm/` land each
one, verified via new tests (§6) and, through Phase 6, the cross-
implementation conformance suite (retired during Phase 7 — see §2 item 6).
**Learned from `0.1`'s own Phase 10 audit: keep this table current as work
actually lands, don't let it go stale for nine phases and then need a
special pass to fix it.**

| Feature | Design decision(s) | Status |
|---|---|---|
| Vector indexing (`v[i]` get/set) | §1.6 | Done |
| Lambdas (`(a, b) -> expr`) | §1.1 | Done |
| Cons operator (`[item \| list]`) | §1.2 | Done |
| List comprehensions (single generator) | §1.2-4 | Done |
| Vector-literal spread (`[...a, ...b]`) | §1.7 | Done |
| Array-manipulation stdlib | §2.4 | Done |
| Matrices (nested vectors + stdlib) | §1.5, §2.5 | Done |
| Property `pub`/`mut` modifiers | §1.8-10, §2.3 | Done |
| Method `pub`/private | §1.11, §2.3 | Done |
| Mixins (`with`, simplified non-C3 composition) | §1.12, §2.2 | Done |
| Traits (`trait`/`use`, static copy) | §1.12, §2.1 | Done |

## 5. Suggested sequencing

A proposed order, not a commitment — reorder freely. Indexing first since
almost everything else either needs it directly (matrices, array stdlib)
or benefits from the `[...]`-postfix grammar work being already proven
before cons/comprehensions reuse the same bracket. Properties/methods
visibility and mixins/traits pushed later since they're the largest, most
self-contained (class-system-only) changes and don't block the
array/stdlib work at all — feel free to swap their order if you'd rather
tackle the biggest risk first instead of last.

**Phase 0 — `langspec/` versioning move.** *Done.* Moved `0.1`'s
`langspec/` snapshot (grammar docs + examples) into `langspec/versions/0.1/`
(decision 13) — mirroring `docs/PLAN-0.1.md`'s own Phase 0 (repository
reorganization) pattern of doing the structural move first, separately
from any feature work. A light accuracy pass came first (the pre-move
`langspec/README.md`/`SYNTAX_GRAMMAR.md` still described `0.1-poc`, not
the real, shipped `0.1` — `0.1`'s four additions over `0.1-poc` are
semantic, not grammatical, so the BNF itself needed no changes, just the
prose). Per owner request, this phase also went further than the move
alone: the new top-level `langspec/SYNTAX_GRAMMAR.md`, a repurposed
`langspec/README.md` (now a directory-navigation guide rather than a
version-specific spec), and a full `langspec/examples/*.iqx` set
exercising every decision in §1 were all written now, ahead of Phases
1-8 actually implementing any of it — a deliberate spec-first departure
from this document's original Phase 9 framing ("the current, `0.2`-syntax
top-level examples become a `compiler/`+`vm/`-only check" was written
assuming that wiring would happen at the *end*). To keep CI green through
Phases 1-8, `scripts/conformance-test.sh`, `scripts/phase7-run-smoke-test.sh`,
the CI `poc` job, and `.github/workflows/release.yml`'s example-bundling
step all point at `langspec/versions/0.1/examples/` for now (verified:
both scripts still pass against it) — flipping back to the top-level
`langspec/examples/` once `0.2` fully lands was meant to remain Phase 9's
job, but didn't get the chance to: both scripts were retired outright
during Phase 7 instead, once decisions 8-11 broke `compiler/`+`vm/`'s
ability to run these very `langspec/versions/0.1/examples/` fixtures at
all — see Phase 7's own entry below and §2 item 6's resolution. One
necessary-but-unstated consequence surfaced while writing property
examples: decision 8's addendum (§1) now spells out that undeclared,
implicitly-created fields (`0.1`'s model) can't coexist with the
pub/mut access table, so `0.2` requires every property to be declared.

**Phase 1 — Indexing.** *Done.* `v[i]` read/write, bounds-checked,
0-based, postfix on any primary expression (`f()[0]`, chained `grid[i][j]`,
all compose the same way `.property` access already does) --
`Ast.Index`/`IndexSet` through `Bound.BIndex`/`BIndexSet` to two new
no-operand opcodes, `GetIndex`/`SetIndex` (the index is a runtime stack
value, not a compile-time constant like `GetProperty`'s name). Found and
resolved a real grammar collision along the way that this document's
original draft missed entirely — decision 6's addendum (§1) covers the
whitespace-adjacency disambiguation between `v[0]` (indexing) and `concat
[1, 2]` (the pre-existing vector-literal-argument call syntax, unchanged).
11 new xUnit tests (parser disambiguation/chaining, resolver, codegen
instruction sequences) and 7 new Catch2 tests (happy path, out-of-range,
negative, non-integer, non-number-index, non-vector-receiver for both
`GetIndex`/`SetIndex`). Also surfaced (not fixed — out of scope here) a
real, pre-existing `0.1` scanner limitation while validating
`langspec/examples/matrices.iqx` end to end: every newline scans as an
implicit `;` with no bracket-depth awareness at all, so a multi-line
vector literal (or any multi-line grouping expression) has never actually
worked — logged as `docs/LANGUAGE.md` §13 item 9; `matrices.iqx`'s matrix
literal was rewritten onto one line to work around it.

**Phase 2 — Lambdas.** *Done.* `(a, b) -> expr`, single-expression body,
closing over enclosing scope exactly like a named nested function already
does -- `Ast.Lambda` desugars in `Resolver.fs` to an ordinary, nameless
`FunctionDecl` with a single implicit `return` statement, then resolves
via the *unchanged* `ResolveFunction`, so scope/slot/upvalue capture (and
parameter immutability) come for free. `Codegen.fs` needed no new opcode
at all: `BLambda` just calls the existing `CompileFunctionValue` (already
factored apart from "then bind it to a name" for exactly this kind of
reuse), pushing a `Closure` the same way a named function or method
already does. `Token.fs`/`Scanner.fs` needed one addition (`Arrow`, `->`)
via the scanner's existing longest-match operator table -- no bespoke
lookahead there either.

Disambiguating `(a, b) -> expr` from `0.1`'s pre-existing grouped comma
expression `(a, b)` (same opening token, same shape) needed one new
parser helper, `isLambdaAhead`: a pure lookahead (no backtracking) that
scans for a bare, comma-separated identifier list immediately followed by
`) ->` before committing to either parse path.

Also found, while writing tests, a pre-existing `0.1` call-grammar
limitation unrelated to lambdas themselves: a non-identifier callee can
never be called, with or without parens (`(f) 5` and even `(f)()` both
fail to parse) -- `CallHead()`'s "does an argument-shaped token follow"
check only ever runs for a bare identifier. Not new, not fixed here, but
newly relevant now that lambdas make "produce a callable value inline" a
natural thing to reach for immediately-invoked-function-style; logged as
`docs/LANGUAGE.md` §13 item 10.

18 new xUnit tests: 3 scanner (`Arrow` distinct from `Minus`/`MinusMinus`),
8 parser (single/multi/zero-parameter lambdas, the grouped-comma-expression
non-lambda case, a lambda as a call argument, curried lambdas), 4 resolver
(parameter locals, upvalue capture, parameter immutability), 1 codegen
(instruction-level `Closure`/implicit-`return` shape). No VM/C++ changes
needed at all, so the existing 58 Catch2 tests, the smoke test, and the
conformance suite all stay untouched and green.

**Phase 3 — Cons and list comprehensions.** *Done.* `[item | list]`,
`[expr | pattern <- iterable]` (§1.3's single-generator, no-guards slice).
Both share `Ast.fs`'s pre-existing `[...]` bracket syntax with plain vector
literals and each other, disambiguated in `Parser.fs`'s `Primary()` purely
by lookahead after a bare `|` (decision 2): an identifier immediately
followed by `<-` means a comprehension, anything else means cons.
`Token.fs`/`Scanner.fs` needed two new single/two-character tokens
(`VerticalBar`, `LeftArrow`), both already distinct from their nearest
lookalikes (`Pipe`'s `|>`, `Less`/`LessEqual`) via the scanner's existing
longest-match table -- no bespoke lookahead there either.

Both constructs need a real runtime loop over a vector whose length isn't
known at compile time, which rules out compiling them as a fixed-operand
`BuildVector`. Two new no-operand opcodes support that loop:
`VectorLength` (pop a vector, push its element count, a runtime type error
otherwise) and `VectorAppend` (pop a value and a vector, `push_back` the
value into the vector's own heap-allocated element storage, push nothing
back -- the mutation is visible through every other reference to the same
`ObjVector*` for free, without ever needing to store the vector back
anywhere).

**A real design bug was found and fixed during implementation**, not
caught by this document's original draft: the first attempt had
`Resolver.fs` allocate hidden accumulator/index locals (`$result`,
`$index`) directly in the *enclosing* scope, the same way an ordinary
`var` gets a slot. That works when a `Cons`/`ListComprehension` sits at
statement level (e.g. a `var` initializer) but silently corrupts stack
addressing the moment one appears **mid-expression** -- e.g. as a call
argument (`print [1 | []]`) -- because `Resolver.fs`'s static slot count
has no visibility into transient values `Codegen.fs` pushes mid-expression
(the callee itself, in that example), which already occupy stack positions
the hidden locals' slot numbers didn't account for. Found via manual
end-to-end testing (`var squares = [n * n | n <- ...]` worked; `print [1 |
[]]` corrupted an unrelated global lookup into a runtime type error) rather
than by any unit test, since every test at the time exercised these
constructs only at statement level.

Fixed by discarding the hidden-local design entirely and desugaring both
constructs, in `Resolver.fs`, into a call of a synthetic, nameless closure
-- exactly `Lambda`'s own desugaring (Phase 2), reused wholesale: `item`/
`list` (or `source`, for a comprehension) are evaluated once, in the
*enclosing* scope, and passed in as ordinary call arguments; everything
the loop itself needs lives in the synthetic closure's own fresh
`FunctionState`/`FunctionContext`, whose slot numbering is completely
decoupled from whatever the enclosing expression already has on the stack
-- the same isolation any nested closure already gets. Extracting the
loop's final accumulator value out from under its own now-discarded locals
falls out for free from `Return`'s existing native pop/truncate/push
mechanism, needing no new opcode of its own. (An interim opcode,
`PopNKeepTop`, was added and then fully removed once this became clear --
never shipped, so it left nothing behind to migrate.) Two new internal-only
`Ast.Expr`/`Bound.BoundExpr` node pairs
(`InternalVectorLength`/`InternalVectorAppend` -> `BVectorLengthInternal`/
`BVectorAppendInternal`) wrap `VectorLength`/`VectorAppend` for the
synthetic closure's own body to call -- neither has any surface syntax of
its own; `Resolver.fs` is the only place that ever synthesizes them.

A comprehension's bound variable (`x` in `x <- xs`) is declared fresh
inside the loop body's own block scope each iteration, exactly like any
other block-scoped `var` -- so it correctly shadows an enclosing variable
of the same name without corrupting it, and its slot is correctly reused
iteration to iteration. Both `item`/`body` are resolved *within* the
synthetic closure, so a reference to any other enclosing-scope name
correctly captures it as an upvalue, exactly like a lambda body would
(verified: a comprehension inside a function body correctly closes over
that function's own locals).

14 new xUnit tests (2 scanner: `VerticalBar`/`LeftArrow` distinct from
`Pipe`/`Less`/`LessEqual`; 6 parser: cons vs. comprehension vs. plain
vector-literal disambiguation, nesting, arbitrary comprehension sources; 6
resolver: the desugared `BCall(BLambda(...), args)` shape, enclosing-scope
argument evaluation, upvalue capture from within the synthetic closure's
body, comprehension-variable shadowing, internal-primitive usage) and 2
codegen (instruction-level shape of both desugared calls, including the
full loop body for cons). 7 new Catch2 tests (`VectorLength`/`VectorAppend`
happy path and non-vector runtime errors, cross-reference aliasing, and two
full end-to-end hand-assembled reproductions of the exact bytecode
`Codegen.fs` emits for cons -- including the originally-failing `[1 | []]`
call-argument case, now passing). `langspec/examples/cons_and_comprehensions.iqx`
verified end to end through the real `iqaloxc`+`iqaloxvm` toolchain.

**Phase 4 — Vector-literal spread.** *Done.* `[...a, ...b]` (decision 7).
No new tokens needed -- `Ellipsis` (`...`) has been scanned since
`0.1-poc`, just never given grammar until now. A new `Ast.Spread`/
`Bound.BSpread` expression pair wraps the spread-away inner expression;
`Parser.fs` recognizes a leading `...` on any vector-literal element, and
-- since decision 7 scopes spread to vector literals only -- a *leading*
spread on the very first element unambiguously rules out the existing `|`
lookahead entirely, so `[...a | b]` is a plain syntax error rather than
something ambiguously cons-or-spread-shaped.

Unlike Phase 3's `Cons`/`ListComprehension`, spread needed **no synthetic
closure and no hidden locals at all**. A spread-free vector literal
compiles exactly as before (`BuildVector n`, one fixed operand); once any
element is a spread, `Codegen.fs` instead builds the vector by chaining
pure stack operations: `BuildVector 0` starts an empty accumulator, then
each element either extends it with a spread source directly, or first
wraps a plain value in its own one-element `BuildVector 1` and extends
with that -- a single new opcode, `VectorExtend` (pop a source vector,
pop a target vector, append the source's elements onto the target's own
element list, and, unlike Phase 3's `VectorAppend`, **push the target back
onto the stack** so the next element in the chain can keep extending it).
Because every step both consumes and re-produces the accumulator in place
on the stack -- never in a named local slot -- there's no "declare a new
local here" step for `Resolver.fs`'s slot-counting model to get wrong, so
this sidesteps the exact mid-expression stack-corruption bug Phase 3 hit
and fixed by moving to an isolated closure frame. Verified directly:
`sum([...a, 9])` (a spread inside a call argument, the same shape
`print [1 | []]` broke under the old Phase 3 design) produces the correct
result with no isolation machinery needed at all.

A non-vector spread source (`[...5]`) is a real, user-facing runtime type
error ("Can only spread a vector, got number."), unlike `VectorAppend`'s
non-vector-receiver case (an internal-consistency check only, since that
one's never reachable from surface syntax) -- `VectorExtend`'s target,
by contrast, needs no check at all, since `Codegen.fs` only ever builds it
with `BuildVector` immediately beforehand.

10 new xUnit tests (6 parser: spread parsing, mixed spread/plain
positions, the "no spread at all" case, the leading-spread-rules-out-cons
disambiguation, an arbitrary spread source expression; 2 resolver: the
`BSpread` shape, independent resolution of mixed elements; 3 codegen:
spread-free `BuildVector` unchanged, the full flattening instruction
sequence, an all-spread literal) and 4 new Catch2 tests (`VectorExtend`
happy path, chained extends building up one target, non-vector-source
runtime error, a full end-to-end hand-assembled reproduction matching
`Codegen.fs`'s own emitted sequence for `[0, ...a, 5]`).
`langspec/examples/spread.iqx` verified end to end through the real
`iqaloxc`+`iqaloxvm` toolchain.

**Phase 5 — Array-manipulation standard library.** *Done.* Resolves §2.4:
`length`, `push`, `pop`, `reverse`, `map`, `filter`, `reduce`, `sort`,
decided live (via a design-questions round with the repository owner,
following this project's "ask rather than guess" convention) rather than
guessed from the original candidate list. Key decisions: plain global
functions, not methods on vectors (`0.1`'s object model has no
method-dispatch-on-primitives concept at all, and adding one is a much
bigger change than a stdlib phase); `push`/`pop` mutate their vector
argument in place (matching how `VectorAppend`/spread already treat
vectors as mutable heap objects regardless of the binding's own `mut`
status), the rest return a new vector; argument order is function-first
for the four that take one (`map fn, v`, matching Lisp/mathematical
convention over `push`'s own receiver-first shape); `reduce` always takes
an explicit `initial` (`reduce fn, v, initial`) rather than silently
seeding from the vector's first element; `sort` always takes an explicit
comparator (`sort fn, v`, `fn(a, b)` truthy meaning "`a` sorts before
`b`") rather than assuming a numbers-only default order, since `<`/`<=`
only accept numbers today and teaching them about strings too is out of
scope here.

**A real gap in the first design-questions round, caught by the owner
after the fact, not by this document's own review.** "Plain functions vs.
methods on vectors" was framed as a binary, but that framing silently
conflated two different things: instance-method dispatch on a primitive
value (`v.push(x)`, which really does need a new object-model capability)
and a *namespaced* access style (`Vector.push v, x`), which needs no such
thing but *does* bear directly on `ROADMAP.md`'s planned module support
(`pub` is explicitly meant to "define what a module exports" then) -- a
stdlib shipped now as flat, unconditionally-injected globals is a
different starting point for that migration than one shipped under a
namespace, or gated behind explicit inclusion, would be. Resolved via a
second, correctly-scoped question: keep `0.2`'s array stdlib flat and
always-available (matching `print`/`concat`'s precedent, cheapest now),
explicitly logged as a deliberate stopgap rather than a silent default --
see the bullet under `ROADMAP.md`'s `0.3` entry (module support was
originally slated for `0.5`, moved forward to `0.3` alongside this very
revisit item so both are ready before the `0.4`-onward stdlib phases that
might actually want them), to be revisited once real module support
actually exists to make a namespaced or gated shape meaningful.

**A second question raised during PR review, on the same "ask, don't
guess" grounds**: is comma-separated no-parens multi-arg calling
(`push v, 4`) actually the intended `0.2` design, or did it just carry
forward unquestioned? Checked directly against `poc/src/parser.py`'s
`call_head()` -- it's `0.1-poc`'s own original grammar, not something
introduced by this phase or any `0.2` phase before it, and a
whitespace-only alternative (`push v 4`, no comma) doesn't parse at all
today (verified: `add 1 2` fails with `Expect line break or ';' after
expression.`). Kept as-is for `0.2` -- logged as a real `0.3` revisit
item on `ROADMAP.md` instead of decided inline, since dropping the comma
is a breaking grammar change (needs its own answer for where a
whitespace-only argument list ends) touching every existing example and
the whole call-argument test suite, not something to fold into a
stdlib-functions phase's docs fix.

An architectural split feeds directly from a technical fact checked
against the real VM before deciding anything: `Vm::callNative` invokes a
native function's C++ implementation synchronously and expects a `Value`
back immediately, while calling a closure only ever queues a new
`CallFrame` for the *existing* bytecode dispatch loop to pick up later --
there's no "call this closure and synchronously get its result" primitive
a native could use. `push`/`pop`/`length`/`reverse` need no such thing
(direct `ObjVector` element-list access, implemented as ordinary natives
in `vm/src/natives.cpp`, registered the same way as `print`/`concat` was
in Phase 7). `map`/`filter`/`reduce`/`sort` do need to call back into a
user-supplied lambda -- rather than build a new, untested VM reentrant-call
capability, they're ordinary Iqalox *source* (`compiler/src/Prelude.fs`,
a plain embedded string), textually prepended to every program's own
parsed statements (`Program.fs`) before one combined resolve/codegen
pass -- each one is a completely unremarkable `fun` declaration, using
`for`/lambda-calls/indexing/`push` exactly like any user program could,
needing zero special-casing in `Resolver.fs` or `Codegen.fs` and no new
opcodes at all. `push`/`pop`/`length`/`reverse` *are* added to
`Resolver.fs`'s `nativeGlobals` (pre-registered, immutable, matching
`print`/`concat`); `map`/`filter`/`reduce`/`sort` deliberately are not --
an ordinary top-level `fun` already resolves to a protected global on its
own, so redeclaring any of the eight is an "already declared" compile
error either way.

**A real, pre-existing parser bug was found and fixed** while writing the
prelude's own source, blocking its `map fn, v`-shaped calls specifically
when `fn` is a lambda in *non-last* argument position (verified this is
genuinely pre-existing, not something Phase 1-4 introduced, by reproducing
it against plain user-facing syntax with no prelude involved at all --
`apply (x) -> x * 2, 5` on a fresh test file was already broken before any
Phase 5 code existed). Root cause: a call argument is parsed via
`Argument()`, which (unlike `[...]`'s own element-parsing) never
suppressed the comma operator around itself -- so a lambda argument's own
*unparenthesized* body, parsed through the full `Expression()` chain,
would treat a bare comma as its own operator and silently swallow
whatever argument was meant to follow it (`map (x) -> x * 2, v` produced
a 1-argument call, not 2). Every existing Phase 2 lambda-as-call-argument
test happened to put the lambda *last*, so nothing was ever after it to
swallow. Fixed with the same `commaAsOperator` suppression `[...]`
already uses, applied around `Argument()` itself; `Primary()`'s `Grouping`
branch was given a matching, opposite fix (always *re-enabling* the comma
operator for its own parenthesized contents, restoring the outer state
afterward) so a legitimate parenthesized comma-tuple passed as a single
argument (`f (a, b)`) -- or nested inside a vector literal (`[(a, b), c]`,
previously silently broken the same way, just never noticed) -- keeps
working exactly as before. Verified via the full existing xUnit suite
(no regressions) plus new regression tests for both the bug and the fix's
own boundary (parenthesized comma-tuples still working).

Two known, accepted limitations logged rather than solved (`docs/LANGUAGE.md`
§13 items 11-12): a one-line function body ending in a bare `return x`
needs an explicit `;` before `}` (an ASI gap, found while writing the
prelude in as few lines as reasonable -- ASI only ever fires on a real
newline, ` }` doesn't imply one); and a runtime error raised from *inside*
a prelude function's own body reports a line number relative to
`Prelude.fs`'s own source text, not the user's file, since nothing in
this pipeline has ever needed multi-file source-position tracking before
now.

23 new xUnit tests (5 parser: the swallowed-argument bug and its fix,
the Grouping-comma-scope fix, both standalone and inside a vector
literal; 6 prelude-specific: the embedded source scans/parses/resolves/
compiles cleanly on its own, a user program can call all four as ordinary
globals once merged, redeclaring any of them is an "already declared"
error) and 10 new Catch2 tests (`push`/`pop`/`length`/`reverse` presence,
happy path, mutation-vs-pure-return semantics, and non-vector-argument
runtime errors for each). `langspec/examples/array_stdlib.iqx` verified
end to end through the real `iqaloxc`+`iqaloxvm` toolchain, exercising
all eight functions plus `sort`'s non-mutating contract.

**Phase 6 — Matrices.** *Done.* Nested-vector convention (decision 5, no
new literal grammar) plus a dedicated stdlib, resolving §2.5's exact
surface (raised live with the repository owner, same as Phase 5's array
stdlib): `transpose`, `multiply` (the true matrix product, not
elementwise), `add`, `subtract`, `elementwise fn, a, b`. Matrix-only for
this pass (assume exactly one level of nesting, i.e. real 2D) rather than
generic over any same-shaped nested vectors -- a plain-vector-elementwise
generalization is possible later but wasn't asked for here. No operator
overloading: `+`/`-`/`*` stay numbers-only (verified: already
number-only via `checkNumberOperands`, same constraint `<`/`<=` hit in
Phase 5) -- `add`/`subtract`/`multiply` are dedicated named functions
instead, matching Phase 5's array stdlib precedent rather than reopening
that question.

Same native-vs-prelude split as Phase 5, for the same reason: `transpose`/
`multiply`/`add`/`subtract` never call back into user code, so they're
true natives (`vm/src/natives.cpp`), registered in `Resolver.fs`'s
`nativeGlobals` alongside Phase 5's eight; `elementwise` takes a
user-supplied combining function, so it's ordinary Iqalox source
(`compiler/src/Prelude.fs`), like `map`/`filter`/`reduce`/`sort`.

**A second technical constraint discovered mid-implementation, requiring
its own live decision**: the repository owner's chosen validation
behavior ("clean runtime error on a shape mismatch") turns out to be
*unavailable* to `elementwise` specifically. `docs/LANGUAGE.md` §13
confirms Iqalox has no `throw`/`raise` construct at all -- only native
C++ code can signal a custom-worded `RuntimeError`. `transpose`/
`multiply`/`add`/`subtract` can validate cleanly since they're natives
(dimension-naming messages, e.g. `"multiply: a 2x2 matrix can't be
multiplied by a 3x3 matrix -- the first matrix's column count must equal
the second's row count."`); `elementwise`, forced into the prelude by
needing to call `fn`, has no way to throw a comparably specific message
from Iqalox source. Resolved by letting `elementwise`'s shape mismatch
fall through to whatever error happens to fire first from inside its own
loop (in practice, the ordinary "Vector index N out of range" error a
step later) rather than adding a new `error(message)`-style primitive to
the language now -- a real, if generic, non-crashing runtime error
either way, and inventing a new user-facing error-signaling capability
mid-stdlib-phase was explicitly declined in favor of leaving it to
`ROADMAP.md`'s own dedicated `0.6` "error handling standard library"
entry.

A real bug was also found (and fixed) while manually verifying
end-to-end, unrelated to the constraint above: `elementwise`'s own first
draft called its combining function as `fn(a[i][j], b[i][j])` --
`Argument()`'s own doc comment (Phase 5) already establishes that a
callee immediately followed by `(...)` with a comma inside is a
*1-argument* call whose argument is the parenthesized comma-operator
expression, not 2 arguments. Fixed the same way Phase 5's own prelude
functions already do it: `fn a[i][j], b[i][j]`, no wrapping parens.

9 new xUnit tests (6 prelude-specific, extending Phase 5's `PreludeTests.fs`:
the now-5-function prelude compiles cleanly, resolves/calls correctly
once merged, `transpose`/`multiply`/`add`/`subtract` confirmed as
`nativeGlobals` alongside Phase 5's eight) and 9 new Catch2 tests (all
four natives' presence, happy paths -- including confirming `add`
doesn't mutate either argument -- and shape/type-mismatch runtime
errors). `langspec/examples/matrices.iqx` extended and verified end to
end through the real `iqaloxc`+`iqaloxvm` toolchain, exercising all five
functions.

**Phase 7 — Property and method visibility (`pub`/`mut`) — done.** The
biggest single change to the object model this version, per §7's own risk
assessment — not additive, a real breaking change to `0.1`'s "fields
spring into existence on assignment" model and its unrestricted external
method calls.

Front end: `Token.fs`/`Scanner.fs` gain a `pub` keyword; `Ast.fs`'s
`FunctionDecl` gains `IsPub: bool` (methods only — `Parser.fs` always sets
it `false` for a top-level `fun`, which has no visibility concept), and a
new `PropertyDecl = { Name: Token; IsPub: bool; IsMutable: bool }`;
`ClassStmt` gains a `properties: PropertyDecl list` field alongside
`methods`. `Parser.fs`'s `ClassDeclaration()` now dispatches each class-
body member on `var` (a property) vs. everything else (a method, capturing
a leading `pub` first); a new `PropertyDeclaration()` parses `var name
[pub] [mut]`. `Bound.fs` mirrors this (`BoundFunctionDecl.IsPub`, a new
`BoundPropertyDecl`, `BClassStmt` gaining a `properties` field) — purely
plumbing, no new resolution logic needed for the flags themselves.

The one genuinely new `Resolver.fs` responsibility (decision 8's
addendum): a `self.x = value` targeting a property never declared
anywhere in the current class's own hierarchy is now a *compile-time*
error, the same category as assigning to an undeclared local. This needs
a small class-hierarchy table `Resolver.fs` didn't have before —
`preRegisterClasses` walks every top-level `ClassStmt` (mirroring
`preRegisterGlobals`'s own forward-reference-safe pre-pass) building a
`className -> { SuperclassName; Properties; MethodNames }` map, and a
`currentClassName` field tracks which class's method body is currently
being resolved. Two more compile-time checks fall out of having this
table at all, neither explicitly asked for but both direct, obvious
consequences of decision 8 splitting properties and methods into what are
now effectively two separate declared-name spaces sharing one class body:
a property and a method sharing the same name within one class is an
error, and a property redeclared anywhere up its own ancestor chain
(distinct from redeclaring it *within* the same class, already caught) is
too. An *external* `instance.x = value` gets no such compile-time check —
its `instance` expression has no statically-known class to check against,
so an undeclared external write is deferred to a runtime error instead
(matching how an external call to a nonexistent method already worked).

Runtime model (`vm/src/object.hpp`, `vm/src/vm.cpp`): `ObjClass` gains a
`publicMethods` set (which of `methods`' entries were declared `pub`) and
a `properties` map (`name -> {isPub, isMut}`) — both copied into a
subclass by `Inherit` exactly like `methods` already is, so a subclass
automatically has every ancestor's property/method-visibility metadata
available with no redeclaration needed (the mechanical form the
now-resolved open question 3 takes: inheriting *access*, not just the
name). `ObjInstance.fields` is no longer a springs-into-existence map:
`Vm::callClass` pre-populates one `Undef`-valued entry per declared
property (own + inherited, already flattened by `Inherit`) the moment an
instance is allocated, before `init` ever runs — reusing the exact same
`Undef` sentinel `0.1`'s `var mut` locals already use for "not yet
assigned," including its "accessed before being assigned" runtime error
(`Vm::checkPropertyNotUndef`, the property-flavored sibling of
`checkNotUndef`).

New/changed opcodes, all still plain "one `u16` operand" instructions (no
new operand-width class needed): four `Property*` opcodes
(`PropertyPrivate`/`PropertyPrivateMut`/`PropertyPub`/`PropertyPubMut`,
one per decision 8 bullet, each just recording metadata into the class
value already sitting on top of the stack — chosen over one opcode with
boolean operands specifically to avoid a new operand-width bucket);
`MethodPub` alongside the existing (now implicitly "private") `Method`;
and `GetPropertySelf`/`SetPropertySelf` alongside the existing (now
"external") `GetProperty`/`SetProperty`. `Codegen.fs` picks the `*Self`
variant whenever a `Get`/`Set`'s object expression is exactly `BSelf` —
a purely syntactic, compile-time choice needing no new `Resolver.fs`
bookkeeping, since (per the resolved open question 3) internal access
never depended on *which* class in the hierarchy declared the member,
only on whether the access is spelled `self.x` at all. `super.method()`
(`GetSuper`) needed no changes at all — it was already unconditionally
internal, which is exactly what the protected-like resolution asks for.

Runtime enforcement, all in `vm.cpp`: external `GetProperty` checks the
accessed property's own `isPub` (or, falling through to a method, checks
`publicMethods`/`init`) and reports the exact same "Undefined property"
wording on failure a nonexistent name would — genuinely *invisible* from
outside, per decision 10's own word for it, not a distinct "it's private"
error. External `SetProperty` additionally requires `isMut`, with a
dedicated "not externally mutable" error when a property is visible but
read-only. Both `*Self` variants skip every `pub`/`mut` gate entirely.
Immutability (decision 9) is enforced by both `SetProperty` and
`SetPropertySelf` for a non-`mut` property: the first write (transitioning
away from the `Undef` sentinel) succeeds, any later one is a runtime
error — `mut` properties have no such limit, matching `0.1`'s existing
`var`/`var mut` split reused for exactly this purpose.

A concrete, previously-hypothetical consequence of decisions 8-11 turned
out to be a real compiler/+vm/ regression the moment this phase landed,
not just a documentation risk: `langspec/versions/0.1/examples/classes.iqx`
(frozen `0.1`-era syntax, undeclared `self.name = name`, no `pub` on
`quack()`/`square()`) stopped compiling outright. This is exactly §2 item
6's "conformance-suite fallout," but concretely worse than that question's
own framing assumed (not just a re-pointing exercise — `compiler/`+`vm/`
provably can no longer run `poc/`-era *class* fixtures at all). The
repository owner's call, once this was surfaced: retire cross-
implementation conformance testing entirely rather than keep chasing it
release over release — see item 6's own resolution above and
`CLAUDE.md`'s `compiler/`/`vm/` testing bullet for what that means going
forward (`scripts/conformance-test.sh`/`scripts/phase7-run-smoke-test.sh`
and their CI jobs are gone).

`langspec/examples/classes.iqx`/`inheritance.iqx`/`properties.iqx`/
`visibility.iqx` all already had `pub`/`var`-declared properties from
Phase 0's spec-first pass (written ahead of any implementation existing to
run them) — no editing needed here, just end-to-end verification through
the real toolchain, plus one addition: `visibility.iqx` gained a
`LoggingVault extends Vault` subclass whose own `pub` method calls the
superclass's private `checkPin`, the first fixture anywhere in the repo
that actually exercises the protected-like resolution rather than just
documenting it in a comment. 9 new xUnit tests (property/method `pub`
parsing and all four modifier combinations, the three new compile-time
errors, and two full Codegen instruction-sequence assertions covering
every new opcode) and 9 new Catch2 tests (private-property invisibility,
pub-read/no-external-write, write-once enforcement for both an immutable
and a `mut` property, reading-before-assignment, private-method-call
rejection, `init`'s unconditional external callability, and the
protected-like subclass-reaches-ancestor's-private-members case) — plus 5
existing Catch2 tests updated for the new opcodes (they emit raw bytecode
directly via `ChunkBuilder`, so they needed `Property*`/`MethodPub`/
`*Self` opcodes added by hand, not a compiler-side fix).

Verifying every top-level `langspec/examples/*.iqx` fixture end to end
(not just the four class-related ones) — the closest thing left to the
retired smoke test, now a manual per-phase step per §5's Phase 9 entry —
found one unrelated, pre-existing bug: `lambdas.iqx` (Phase 2) declared
its own `var add`/`add5`, which silently collided with Phase 6's later
`add` native global the moment that landed, an "already declared" compile
error nobody had actually triggered since nothing had run this fixture
since Phase 6 shipped. Renamed to `sum`/`addFive` — an example-authoring
fix, not a language or compiler bug, and not logged in `docs/LANGUAGE.md`
§13 for that reason.

**Phase 8 — Mixins and traits — done.** §2.1/§2.2 resolved at the start
of this phase (compile-time error on any conflict; a simplified,
non-C3 approximation of `with`, full C3 deferred to `ROADMAP.md`'s
language-feature-ideas list) — see their own entries above for the
resolutions and the scope-check round that led to deferring C3
specifically.

Front end: `trait`/`with`/`use` were already reserved keywords with no
grammar (`Token.fs`'s doc comment, since `0.1`); this phase gives them
one. `Ast.fs` gains `TraitStmt(name, properties, methods, usedTraits)`
(grammared identically to a class body per `langspec/SYNTAX_GRAMMAR.md`
-- properties, nested `use`, methods) and extends `ClassStmt` with
`mixins: Expr list` (the `with M1, M2` header list, each an ordinary
`Variable` reference exactly like `superclass`) and `usedTraits: Token
list` (every `use A, B` found anywhere in the class body, however many
separate `use` clauses there are). `Parser.fs`'s `ClassDeclaration`/new
`TraitDeclaration` share one `ClassBody` helper for the `classMember*`
loop (`var` → property, `use` → trait-use, else → method).

The real work is entirely in `Resolver.fs`, which now distinguishes
three kinds of "does this class have member X" question, all answered
from one small algebra (`MemberSet` = a name-keyed `Properties`/
`Methods` map pair; `overrideWith`/`mergeAll`/`checkNoConflicts` compose
them):

1. **Traits are pure compile-time inlining, no runtime representation at
   all.** `preRegisterTraits` builds a `TraitInfo` table; `flattenTrait`
   recursively resolves a trait's own nested `use`s (with cycle
   detection and memoization for the diamond case) into one flattened
   `MemberSet`, applying `checkNoConflicts` at every level -- two
   nested-used traits conflicting is exactly as much an error as two of
   a *class*'s own used traits conflicting. `preRegisterClasses` then
   merges each class's own literal body over its used traits' flattened
   members (`overrideWith` -- the class's own declaration always
   silently wins, matching PHP's real semantics for a class overriding
   what a trait provides) into `InlinedProperties`/`InlinedMethods`,
   which `Resolver.fs`'s `ClassStmt` case resolves directly as if the
   user had written them by hand -- so a trait method's `self`/`super`
   naturally resolve relative to whichever class actually uses it,
   exactly like PHP (a trait has no inheritance identity of its own).
   `resolve` filters every top-level `TraitStmt` out of the statement
   list before the main pass ever runs; no `BTraitStmt`, no opcode, no
   runtime object anywhere below `Resolver.fs` at all.
2. **Mixins compose at runtime, since a mixin is a real, independently-
   instantiable class**, not a compile-time-only declaration -- its
   members only exist once its own `Class`/`Property*`/`Method*`
   opcodes have actually run. One new opcode, `Mixin`, mirrors
   `Inherit`'s "peek the class, copy members in" mechanics but pops its
   operand (the mixin value) rather than leaving it on the stack: unlike
   a superclass, a mixin's value is never needed again once copied in,
   since `super` does not chain through `with`-mixins under this
   version's approximation.
3. **Conflict-checking needed a genuine new recursive computation,
   `effectiveClassMembers`**, walking a class's superclass and every
   mixin (each resolved the same way, transitively, with cycle detection
   for a pathological `extends`/`with` loop) and applying
   `checkNoConflicts` to them as siblings -- open question 1's answer
   applied symmetrically to `with`, since nothing else was specified for
   "what if two mixins disagree." A real subtlety found while writing
   this, not anticipated by either open question's own framing: a
   *trait*-contributed member conflicting with the superclass/a mixin
   needed checking too (open question 1 explicitly names "a trait and
   the class's own superclass" as a conflict scenario) -- but the
   class's own *literal* body must NOT be checked against composed
   sources the same way `use` is (that's just normal overriding). This
   needed splitting `ClassInfo` into `OwnLiteral` (the literal body
   alone) and `TraitContributed` (post-trait-merge, pre-own-override),
   checked against the superclass/mixin set separately: `TraitContributed`
   conflicting there is an error (same rule as any other composed-source
   pair); `OwnLiteral` always wins, silently for methods (ordinary
   polymorphism), but a redeclared *property* stays the compile-time
   error `docs/PLAN-0.2.md` Phase 7 already established for the
   superclass-only case (properties have no override semantics under
   decision 9's write-once model), now extended to mixins for the same
   reason. `effectiveClassMembers` also replaces Phase 7's
   `findDeclaredProperty`/`findDeclaredMethod` ancestor walk outright --
   decision 8's addendum ("declared somewhere in its own body or an
   ancestor's") now also needs to see mixin- and trait-contributed
   properties, which the unified computation already covers for free.

A second real gap found only by actually running every top-level
example end to end (not anticipated by any unit test, since it's a
cross-cutting concern no single test exercises): a plain class with
*no* `use` at all -- true of every class from Phases 1-7 -- must keep
its exact declaration-order property/method sequence in the emitted
bytecode, unchanged from before this phase. The conflict-checked merge
is `Map`-keyed (alphabetical, not declaration-order) by necessity, so
`preRegisterClasses` special-cases `usedTraits.IsEmpty` to skip the
`Map` round-trip entirely and pass a plain class's own `properties`/
`methods` straight through -- only a class that actually composes
traits pays for (and needs) the reordering, new functionality with no
prior ordering to preserve. Caught by two pre-existing Phase 7 Codegen/
Resolver tests failing on exact-order assertions, not by any Phase 8
test.

`langspec/examples/mixins_and_traits.iqx` (already written, Phase 0)
needed one real fix, not just verification: its last section tried
`extends Vehicle with Flyable` where `Flyable` is a *trait* -- but a
trait has no runtime existence at all, so `with` (which resolves its
list as ordinary variable references to real classes) can't reach it,
"Undefined variable 'Flyable'." Fixed by adding a genuine `Winged`
class alongside the existing `Flyable` trait, mixing that in instead --
an example-authoring gap surfaced only by first-time end-to-end
execution, not a language or compiler bug, and a real, useful
illustration of exactly why decision 12 splits `use` and `with` onto
different kinds of entities in the first place.

11 new xUnit tests (trait/mixin/use parsing including nested trait
`use`; the `use`-inlining `self`/`super`-resolves-relative-to-the-using-
class case; four distinct conflict-error scenarios -- trait-vs-trait,
trait-vs-superclass, mixin-vs-mixin, and own-method-overriding-a-trait-
is-*not*-an-error; circular trait `use` and undefined-trait errors; a
mixin resolving as an ordinary variable reference; a with-mixin-only
property satisfying decision 8's declared-property check) and 1 new
Codegen instruction-sequence test (`Mixin`'s exact placement relative to
`Inherit`/`Property*`/`Method*`) plus 3 new Catch2 tests (mixin method/
property copying end-to-end, a non-class `with` target as a runtime
error, and `extends`+`with` composing together on one class).

**Phase 9 — Docs — done.** No conformance-suite split left to resolve —
§2 item 6 was resolved (and the underlying scripts/CI jobs retired
outright) during Phase 7, once decisions 8-11 broke `compiler/`+`vm/`'s
ability to run `poc/`-era class fixtures at all.

All top-level `langspec/examples/*.iqx` fixtures (15 total) were
recompiled and re-run end to end through a freshly-built `compiler/`+`vm/`
— all pass, alongside the full 209-test xUnit suite and 99-assertion
Catch2 suite (both clean, no regressions). `docs/LANGUAGE.md` was forked:
the pre-Phase-9 file (whose own header still said `0.1`, though its known-
limitations list had already accumulated a few `0.2`-era entries from
Phases 5-6 opportunistically noting scanner/parser gaps as they were
found) became `docs/LANGUAGE-0.1.md`, with its header/intro rewritten to
describe `0.1` accurately and one item removed from its own §13 (the
`map`/`filter`/`reduce`/`sort` prelude line-number limitation, since those
functions are `0.2`-only and don't exist in `0.1` — moved to the new
`docs/LANGUAGE.md` instead, where it's actually accurate) — the body
otherwise kept as a verbatim, frozen snapshot per the established
fork-not-rewrite pattern. A new `docs/LANGUAGE.md` was written from
scratch for `0.2`, covering all eight feature phases (indexing, lambdas,
cons/comprehensions, spread, array stdlib, matrix stdlib, property/method
visibility, mixins/traits) across a reorganized 16-section structure (up
from `0.1`'s 14) with a refreshed §13 known-limitations list — most items
9-12 of the old numbering carried forward unchanged (real scanner/parser
gaps, version-independent) plus three new `0.2`-specific limitations
(single-generator comprehensions, simplified non-C3 `with`, no trait
conflict disambiguation syntax).

Cross-references were swept across the repo: root `README.md` (found
stale — its precedence table predated `0.2`'s indexing/lambda/cons/spread
syntax, though on inspection that syntax turns out to belong to the
postfix/literal category the table already excludes `.` member access
from, same as `0.1`'s table did, so a clarifying footnote was added rather
than new rows; its intro text and getting-started example did need
updating from `0.1` to `0.2`), `CLAUDE.md` (the `poc/`/`compiler/`/`vm/`
bullets and `docs/` bullet updated for `0.2`; a new "Architecture
(`compiler/` + `vm/`, 0.2)" section added summarizing all eight phases,
mirroring the existing `0.1` section's style, per-phase, without editing
that section's own historical narrative), `langspec/README.md` and
`langspec/versions/0.1/README.md`/`SYNTAX_GRAMMAR.md` (repointed to
`docs/LANGUAGE-0.1.md` where they meant `0.1`-specific facts, following
the precedent `docs/PLAN-0.1-POC.md` already set when `0.1-poc` was
forked), and one `docs/PLAN-0.1.md` item-number reference (§13 item 7 in
the old numbering, now item 8 in `docs/LANGUAGE-0.1.md`, annotated rather
than left dangling). `ROADMAP.md`'s `0.2` entry is marked *done* (with a
pointer to `docs/LANGUAGE.md`/`docs/LANGUAGE-0.1.md`) and `0.3` is now the
active target.

## 6. Testing strategy

Same split `0.1` already established (`docs/PLAN-0.1.md` §7): xUnit for
`compiler/`, Catch2 for `vm/`. No cross-implementation conformance layer
anymore (§2 item 6, retired Phase 7) and no new testing *infrastructure*
needed otherwise — this version is entirely new language surface on an
already-proven pipeline, not a new implementation to stand up.

## 7. Risks

- ~~Decisions 8-11 (private-by-default properties *and* methods) are a
  real breaking change to `0.1`'s object model, not an additive
  feature~~ **— materialized during Phase 7, exactly as predicted, only
  more severely**: `compiler/`+`vm/` provably can no longer run
  `poc/`-era class fixtures at all (§2 item 6's resolution), not just
  "needs an audited `pub` added here and there." Resolved by retiring
  cross-implementation conformance testing entirely rather than treating
  it as an ongoing constraint — see item 6 and `CLAUDE.md`'s
  `compiler/`/`vm/` testing bullet. Decision 13's `langspec/`
  reorganization still stands on its own merits (versioned spec
  snapshots), independent of this.
- **Four new keyword/token additions in one version** (`->`, `<-`, `...`
  finally given meaning, plus the new `pub` keyword) grows the
  scanner/parser's surface area meaningfully in a single pass — keep test
  density up per new token, the same lesson `0.1`'s own scanner phase
  already demonstrated (five real bugs found there specifically because
  the port was written carefully token-by-token, not copied wholesale).
- ~~Mixin/trait semantics are the least fully specified area of this
  plan~~ (§2.1, §2.2) — **resolved at the start of Phase 8** (compile-time
  error on any composed-source conflict; a simplified, non-C3
  approximation of `with`, real C3 explicitly deferred rather than
  attempted after a dedicated scope-check found its actual blast radius
  much larger than the open question's own framing suggested). Landed as
  one extension of Phase 7's class model, not two independent ones.
- **`langspec/` now needs an ongoing versioning convention it never
  needed before** (decision 13, §2.6) — `langspec/versions/<version>/`
  was chosen specifically to avoid colliding with the pre-existing,
  differently-scoped `langspec/archived/<version>/` planning snapshots;
  don't let Phase 0 casually reuse the bare `langspec/<version>/` form
  instead.
