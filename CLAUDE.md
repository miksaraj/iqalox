# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.

## What this repo is

Iqalox is a programming language: Lox (from Bob Nystrom's *Crafting Interpreters*),
heavily mutated and extended. This repository holds **multiple implementations
across Iqalox's versions**:

- `poc/` — the **0.1-poc proof-of-concept implementation**: a tree-walking
  interpreter written in Python. `0.1` reached feature parity with it as of
  `docs/PLAN-0.1.md` Phase 9, so `poc/` is now **frozen/reference** — a
  working reference implementation kept in the repo, not the primary
  target for new language work. `0.2`'s breaking changes to the object
  model (`docs/PLAN-0.2.md` decisions 8-11) mean `poc/` can no longer stay
  conformant with `langspec/examples/` at all — see the "Retirement of
  cross-implementation conformance testing" note under Engineering
  conventions below.
- `compiler/` — the **compiler frontend**, written in F#. Scanner → parser
  → AST → resolver → bytecode codegen. Feature-complete for `0.1`
  (`docs/PLAN-0.1.md`, Phases 1-9) and `0.2` (`docs/PLAN-0.2.md`, Phases
  1-9) — the current, primary implementation going forward.
- `vm/` — the **bytecode VM backend**, written in modern C++ (C++23).
  Loads and executes the bytecode `compiler/` emits. Feature-complete for
  `0.1` (`docs/PLAN-0.1.md`, Phases 1-9) and `0.2` (`docs/PLAN-0.2.md`,
  Phases 1-9) — the current, primary implementation going forward.

Implementation-agnostic material stays at the repository root:

- `langspec/` — the language specification: syntax grammar and runnable
  examples, for whichever version is the **current active target** (right
  now, `0.3` — see `docs/PLAN-0.3.md`). "Active target" can run ahead of
  what's actually implemented in `compiler/`+`vm/` during a version's
  phased rollout; check that version's `docs/PLAN-0.X.md` §4 feature
  checklist for what has actually landed. `langspec/README.md` explains
  the directory's own layout in full. **Earlier versions are preserved by
  git tags, not by frozen copies in the tree** (decided during `0.3`
  Phase 6 — see `docs/EARLIER-VERSIONS.md` for which tag and path holds
  what): the only frozen thing still in the tree is
  `langspec/versions/0.1/examples/`, kept because CI's `poc/` smoke test and
  the `poc/` release bundle read it. Tag every release. `langspec/examples/*.iqx` are
  cross-implementation conformance fixtures — same input, same expected
  output, regardless of which implementation runs them, *when* more than
  one implementation can run them at all (see the "Example scripts" bullet
  below for what that means while a version's features are still landing).
- `ROADMAP.md` — the version roadmap (0.1-poc onward).
- `docs/` — implementation planning docs (`docs/PLAN-0.1-POC.md` for
  `0.1-poc`, `docs/PLAN-0.1.md` for `0.1`, `docs/PLAN-0.2.md` for `0.2`,
  `docs/PLAN-0.3.md` for `0.3`, currently being implemented), plus
  `docs/LANGUAGE.md`, the language reference for the current version (it
  marks what is new in `0.3`). Older references (`0.1-poc`, `0.1`, `0.2`)
  are not kept as separate files; `docs/EARLIER-VERSIONS.md` says which tag
  to read them from.

## Language design authority

**All language design decisions belong to the repository owner.** This includes
syntax, keywords, operator choice and precedence, semantics of any construct,
standard library API shape, and breaking changes between versions.

- Do not invent, guess, or "helpfully" resolve a language design question on
  your own — even a small one (e.g. whether `++x` mutates its operand, whether
  the self-reference keyword is `this` or `self`, whether `concat` is a
  statement or a stdlib function). If an implementation task depends on a
  design question that hasn't been settled, stop and ask rather than picking
  an answer.
- `docs/PLAN-0.1-POC.md` maintains a running list of open design questions
  blocking 0.1-poc work. Check it before implementing anything in that scope,
  and add to it (rather than silently resolving) when you find a new one.
- Engineering decisions — file layout, whether/how to test, tooling, internal
  refactors that don't change observable language behavior — are fair game to
  make and don't need sign-off.

## Implementation language

Only the **0.1-poc** (and any other explicitly-labeled `-poc` milestone) is
implemented in Python. The real, non-PoC implementation starting at `0.1` is
a **compiler frontend in F#** plus a **bytecode VM backend in modern C++
(C++23)** — see `docs/PLAN-0.1.md` for the full plan. Don't assume this
extends to hypothetical future versions beyond `0.1` unprompted; that
hasn't been decided.

## Architecture (`poc/`, 0.1-poc)

Straight out of *Crafting Interpreters*' tree-walk interpreter design:

1. **Scanner** (`poc/src/scanner.py`, `poc/src/token.py`) — source text →
   `Token` list. Handles implicit semicolons (newline → `;`), line comments
   (`#`), block comments (`<# ... #>`), string/number literals, keywords.
2. **Parser** (`poc/src/parser.py`) — recursive-descent, produces an AST of
   `Expr`/`Stmt` nodes (`poc/src/expression.py`, `poc/src/statement.py`).
3. **Interpreter** (`poc/src/interpreter.py`) — visitor-pattern tree-walking
   evaluator, over `poc/src/environment.py` for variable scoping and mutability.
4. **Errors** (`poc/src/error.py`) — `ParseError`, `IqaloxRuntimeError`.
5. **Entry point** (`poc/src/iqalox.py`) — REPL and file-runner (`Iqalox`
   class), mirrors the `jlox`/error-reporting structure from the book.
6. **`poc/src/ast_printer.py`** — debug helper for visualizing the AST.

`poc/src/expression.py` and `poc/src/statement.py` are **generated files**.
To add or change an AST node, edit the `EXPRESSIONS` / `STATEMENTS` dict in
`poc/tools/generate_ast.py` and regenerate:

```
python poc/tools/generate_ast.py poc/src
```

Don't hand-edit the structure of the generated classes directly — the next
regeneration will silently discard the change. (Bug fixes to
`generate_ast.py`'s templating are fine, just regenerate afterward.)

`langspec/SYNTAX_GRAMMAR.md` tracked `poc/src/parser.py` while `0.1-poc` was
the active target; as of `0.2` planning, the top-level `langspec/` describes
the **active-target spec**, which is written ahead of `compiler/`+`vm/`
actually implementing it; once a version ships it is preserved by its git
tag (see the `langspec/` bullet above and `langspec/README.md`). Don't treat a target-spec grammar doc as
authorization to implement something differently than what's actually
decided elsewhere, and don't assume everything it describes already works —
check the relevant `docs/PLAN-0.X.md` §4 feature checklist first.

## Architecture (`compiler/` + `vm/`, 0.1)

See `docs/PLAN-0.1.md` for the full plan — the compiler frontend (`compiler/`,
F#) and bytecode VM backend (`vm/`, C++23), decoupled through a versioned
bytecode file. `vm/src/bytecode.hpp` still implements format v0 (a minimal
container just big enough to represent "push a string constant, print it,
halt") — `compiler/` has moved on to format v1 as of Phase 5 (see below);
rebuilding `vm/` to read v1 is Phase 6's job, one phase behind by design.
Phase 1 (toolchain scaffolding and
an end-to-end round-trip proof) is done. Phase 2 (the scanner) is also
done: `compiler/src/Token.fs` (an idiomatic `TokenType` discriminated
union) and `compiler/src/Scanner.fs` (`Scanner.scanTokens`) — see
`docs/PLAN-0.1.md`'s Phase 2 entry for the several `poc` scanner bugs this
surfaced and fixed rather than carried forward (decimal literals,
leading-underscore identifiers, and others). Phase 3 (the parser) is also
done: `compiler/src/Ast.fs` (an idiomatic `Expr`/`Stmt` discriminated
union) and `compiler/src/Parser.fs` (`Parser.parse`) — see
`docs/PLAN-0.1.md`'s Phase 3 entry for a real naming collision between
`TokenType` and `Ast` case names (fixed by suffixing the colliding `Ast`
cases) and two more `poc` bugs found and fixed rather than carried
forward. Phase 4 (the resolver) is also done: `compiler/src/Bound.fs`
(`BoundExpr`/`BoundStmt`, the same shape as `Ast.fs` but with every
variable reference/declaration/`self`/`super` carrying its resolved
binding) and `compiler/src/Resolver.fs` (`Resolver.resolve`), implementing
`clox`'s compile-time scope/slot/upvalue algorithm — see
`docs/PLAN-0.1.md`'s Phase 4 entry for how compile-time immutability
enforcement, self-referencing classes, and `self`/`super` scoping all work.
Phase 5 (code generation) is also done: `compiler/src/Bytecode.fs` now
defines bytecode format v1 (a structured, index-based `Instruction`/
`Chunk`/`Constant`/`FunctionProto` representation, serialized to actual
bytes only in `Bytecode.write`), `compiler/src/Disassembler.fs`
pretty-prints a `Chunk` (the primary way tests verify codegen output, no
C++ VM needed), and `compiler/src/Codegen.fs` (`Codegen.compile`) lowers
`Bound.fs`'s tree to it — see `docs/PLAN-0.1.md`'s Phase 5 entry for the
stack-depth-tracking model, the three more `poc` bugs (comma, `??`,
elvis) fixed here, a real gap found in already-merged Phase 4 code
(`Bound.BSuper` needed `self`'s binding alongside `super`'s to support a
super-call from inside a closure nested within a method) and fixed in
both `Bound.fs`/`Resolver.fs`, and the class/method and `for`-loop
`break`/`continue` codegen sequences. `compiler/src/Program.fs` is now a
real `iqaloxc` CLI (source path + output path, scan → parse → resolve →
codegen → write).

Phase 6 (VM core) is also done: `vm/src/value.hpp` defines `Value` as a
tagged `std::variant` (`nil`/`undef`/`bool`/`double`/`Obj*`);
`vm/src/object.hpp` defines the heap-object hierarchy (`ObjString`,
`ObjVector`, `ObjFunction`, `ObjClosure`, `ObjUpvalue`); `vm/src/bytecode.cpp`
loads format v1 (superseding Phase 1's v0 loader); `vm/src/vm.hpp`/`.cpp`
is the stack-based interpreter (`Vm::run`) plus its mark-sweep tracing
garbage collector (decision 7), both on one `Vm` class. Covers every
`0.1-poc`-equivalent expression/statement plus functions and closures —
see `docs/PLAN-0.1.md`'s Phase 6 entry for three notable design points
(a calling convention that deliberately differs from `clox`'s, since
`Resolver.fs` doesn't reserve frame slot 0 for the callee; no dedicated
"close upvalue" opcode, handled instead by a universal stack-shrink choke
point in `Vm`; and why `ObjUpvalue` addresses its stack slot by index
rather than by pointer, to sidestep undefined behavior comparing pointers
into different blocks of the stack's underlying `std::deque`) and a real
bug caught before it shipped (the GC mustn't run at all until the loaded
program's top-level closure is anchored on the stack, or it frees the
whole program before it starts). Classes/`self`/`super` were recognized by
the loader and the VM's opcode dispatch at this point but raised a clear
"not yet supported" error if actually executed until Phase 8 (below).

Phase 7 (native standard library) is also done: `vm/src/object.hpp` adds
`ObjNativeFunction` (a name, arity, and a plain C++ function pointer, no
bytecode frame involved in calling one), and `vm/src/natives.hpp`/`.cpp`
implement `print`/`concat`, both defined as globals by the `Vm`
constructor — mirroring `poc/src/interpreter.py`'s `Interpreter.__init__`,
which defines both the same way before any user statement runs. See
`docs/PLAN-0.1.md`'s Phase 7 entry for `stringify`'s float-formatting
work (matching Python's exact fixed/scientific notation thresholds, which
`std::to_chars`' own formatting doesn't), the vector-nested-element
`repr`-vs-`stringify` distinction, a real `poc` bug found (`concat` on a
non-vector argument crashes with an uncaught Python exception instead of
a clean error) and not carried forward, and a `Resolver.fs` fix so
reassigning/redeclaring `print`/`concat` is a compile-time error like any
other global instead of silently succeeding. `scripts/phase7-run-smoke-test.sh`
(replacing Phase 5's compile-only script, now that `vm/` can actually
execute a program and produce real output) compiles *and runs* every
`langspec/examples/*.iqx` fixture — a hand-verified spot check during this
phase found the four non-class examples already produce byte-for-byte
identical output to `poc`. Classes/`self`/`super` still raised a "not
yet supported" error at that point in time.

Phase 8 (classes & OOP) is also done: `vm/src/object.hpp` adds
`ObjClass` (a name plus a `methods` map, populated by *copying* the
superclass's methods at `Inherit` time — clox's static-copy approach,
not `poc`'s live `superclass` pointer chain walked at every lookup;
behaviorally equivalent since Iqalox has no runtime method-adding
syntax), `ObjInstance` (a `klass` pointer plus a `fields` map that
springs entries into existence on first assignment, exactly like
`poc/src/callable.py`'s `IqaloxInstance` — 0.1's new compile-time
immutability is scoped to `var` bindings only, never fields), and
`ObjBoundMethod` (mirrors `poc`'s `IqaloxFunction.bind`). Method calls
need a different calling convention from plain function calls, since
`Resolver.fs` reserves frame slot 0 for `self` on methods but not on
plain functions: `vm/src/vm.cpp` adds a `callMethod` path alongside the
existing `call`, and `CallFrame` gains `resultIndex` (generalizing what
used to be hardcoded as `stackBase - 1` in `Return`'s handler, since it
differs between the two conventions) and `isInitializer` (lets `Return`
substitute the pre-seeded instance for whatever `init` explicitly
returns, matching `poc`'s `IqaloxClass.call` discarding `init`'s own
return value — a direct `.init()` call on an existing instance, not via
construction, does *not* get this treatment, also matching `poc`).
Dynamic dispatch (`GetProperty`) always resolves against the receiver's
actual runtime `klass`, so an inherited method's `self.method()` call
correctly finds a subclass's override; `super.method()` (`GetSuper`)
bypasses dispatch entirely, resolving directly against the specific
superclass value captured at class-declaration time. See
`docs/PLAN-0.1.md`'s Phase 8 entry for the full opcode-by-opcode stack
shapes, exact error-message porting from `poc`, and a real bug found and
fixed in already-merged Phase 5 code: `Codegen.fs`'s `CompileClass`
never popped the synthetic `super` local's stack slot once a class's
own methods were compiled, so a *second*, unrelated `extends`
declaration later in the same script reused the same slot per
`Resolver.fs`'s (correct) scoping but collided with the first class's
still-resident value at runtime — surfaced only by real end-to-end
script validation (`inheritance.iqx` vs `poc`'s output), not by any unit
test, and fixed with one added `Pop`. All six `langspec/examples/*.iqx`
fixtures, including the two class-based ones, now produce byte-for-byte
identical output to `poc`.

## Architecture (`compiler/` + `vm/`, 0.2)

See `docs/PLAN-0.2.md` for the full plan, decision log, and phase-by-phase
implementation writeups — this section is a summary, not a replacement.
`0.2` builds directly on top of `0.1`'s compiler frontend and VM (the
section above), across eight feature phases plus a ninth, docs-only wrap-up
phase (this one).

Phase 1 (indexing) added `Ast.fs`'s `Index`/`IndexSet` nodes and
`Parser.fs`'s postfix `[expr]` parsing, disambiguated from the pre-existing
paren-free call syntax (`concat [1, 2]`) purely by **whitespace** — a `[`
with no space before it is always indexing, a `[` with a space is always a
call whose sole argument is a vector literal. This is the only place
whitespace is significant anywhere in Iqalox's grammar. `Bound.fs`/
`Resolver.fs` gained `BIndex`/`BIndexSet`; `Bytecode.fs`/`vm/` gained
`GetIndex`/`SetIndex` opcodes with runtime bounds-checking (`Vector index N
out of range.`).

Phase 2 (lambdas) added `Token.fs`'s `Arrow` token and `Ast.fs`/`Parser.fs`'s
`Lambda` node (`(params) -> expr`, single-expression body only). A
parenthesized parameter list is only read as a lambda's parameters via
lookahead **past** the closing `)` — only if the very next token is `->` —
since `(a, b)` is already a valid grouped comma expression in `0.1`'s
grammar and the two share the identical opening shape. `Bound.fs`/
`Resolver.fs`/`Codegen.fs` reuse the existing `BoundFunctionDecl`/
`CompileFunctionValue` machinery rather than adding a parallel lambda-specific
path, since a lambda is, after resolution, just an ordinary closure with an
implicit `return`.

Phase 3 (cons and list comprehensions) added `Token.fs`'s `VerticalBar`/
`LeftArrow` tokens and `Ast.fs`/`Parser.fs`'s `Cons`/`ListComprehension`
nodes, both sharing the `[expr | ...]` opening and disambiguated by
lookahead on the generator marker `<-` immediately after `|`. Both desugar
entirely at compile time (`Resolver.fs`) into a loop inside a synthetic
closure — there is no dedicated runtime representation for either — backed
by two new `vm/` opcodes, `VectorLength`/`VectorAppend`, that the desugared
loop body compiles down to. `0.2`'s comprehensions support exactly one
generator clause and no guard/filter clause; a richer form is `ROADMAP.md`
future scope.

Phase 4 (vector-literal spread) added `Ast.fs`/`Parser.fs`'s `Spread` node
(`[...a, ...b]`), applying only inside the plain comma-separated item-list
form of a vector literal (not combined with cons/comprehension syntax).
`Codegen.fs` compiles a spread-containing literal via stack-only
flattening; `Bytecode.fs`/`vm/` gained one new opcode, `VectorExtend`.

Phase 5 (array-manipulation standard library) split its eight functions two
ways. `push`/`pop`/`length`/`reverse` are true natives (`vm/src/natives.cpp`,
pre-registered in `Resolver.fs`'s `nativeGlobals`) since they need direct
access to an `ObjVector`'s storage and never call back into user code.
`map`/`filter`/`reduce`/`sort` do need to call back into a caller-supplied
lambda, and the VM's native-calling convention (`Vm::callNative`) has no
"call this closure and synchronously get its result" primitive — rather
than build one, these four are ordinary Iqalox `fun` declarations
(`compiler/src/Prelude.fs`), textually prepended to every program's own
source (`Program.fs`) and resolved/compiled as part of the same program,
with no special-casing in `Resolver.fs`/`Codegen.fs` at all. A real
`Parser.fs` bug was found and fixed while writing the prelude's own source
(a lambda swallowing a sibling call argument, and a grouping-expression
comma-scope bug) — see `docs/PLAN-0.2.md`'s Phase 5 entry. Two known,
accepted limitations were logged rather than solved: a one-line function
body ending in a bare `return x` needs an explicit `;` before `}` (ASI only
fires on a real newline), and a runtime error raised from inside a prelude
function's own body reports a `[line N]` relative to `Prelude.fs`'s own
source text, not the user's file (no multi-file source-position tracking
exists in this pipeline).

Phase 6 (matrix standard library) added no new literal syntax at all — a
matrix is simply a vector of vectors, indexed one level at a time via
Phase 1's indexing. `transpose`/`multiply`/`add`/`subtract` are true
natives (`vm/src/natives.cpp`) that validate their argument(s) are a
genuinely rectangular matrix and raise a dedicated, clearly-worded runtime
error on a shape mismatch. `elementwise` (a combining function applied
positionally across two matrices) is, like Phase 5's four, ordinary
Prelude.fs Iqalox source rather than a native, since it needs to call a
caller-supplied lambda — and, per the repository owner's explicit choice,
it deliberately does **not** validate that its two arguments are the same
shape the way the four natives do, since Prelude-level Iqalox code has no
`throw`/`raise` construct available to signal a dedicated error with; a
shape mismatch there just falls through to the ordinary out-of-range index
error a step later.

Phase 7 (property/method `pub`/`mut` visibility) is the largest single
`Resolver.fs` change of `0.2`. Properties must now be declared in the class
body (`var name`, `var name mut`, `var name pub`, `var name pub mut`, no
initializer expression) rather than springing into existence on first
assignment; a non-`mut` property may be assigned at most once, enforced via
the same `Undef`-sentinel mechanism `0.1`'s `var mut` locals already use for
must-assign-before-read. Methods gained a `pub`/private modifier the same
way. **Internal** access (`self.x`/`self.method()`) always bypasses every
visibility/mutability gate — a purely syntactic check (is the access
written as exactly `self.x`) rather than "is this code inside the same
class," which gives Iqalox a protected-like access model for free: a
subclass's own method reaching a superclass's private member via `self`
counts as internal, with zero extra bookkeeping needed. New opcodes:
`PropertyPrivate`/`PropertyPrivateMut`/`PropertyPub`/`PropertyPubMut` (one
per modifier combination), `MethodPub` (private is the existing `Method`),
and `GetPropertySelf`/`SetPropertySelf` (internal, ungated) alongside the
existing `GetProperty`/`SetProperty` (now external, gated). `vm/`'s
`ObjClass` gained `properties`/`publicMethods` metadata and `ObjInstance`
now pre-populates every declared field with `Undef` at construction rather
than letting fields spring into existence. **A mid-phase discovery**: these
breaking changes to the object model (an undeclared `self.x = ...` is now a
compile error) made `compiler/`+`vm/` permanently unable to run `poc/`-era
class fixtures at all — surfaced by `langspec/versions/0.1/examples/
classes.iqx` failing to compile. Raised to the repository owner, who chose
to retire cross-implementation conformance testing entirely rather than
patch around it (pre-`1.0`, backward compatibility with an earlier version
isn't a goal): `scripts/conformance-test.sh` and
`scripts/phase7-run-smoke-test.sh`, plus their `.github/workflows/ci.yml`
jobs, were deleted outright. See `docs/PLAN-0.2.md`'s Phase 7 entry and
open question 6's resolution for the full narrative.

Phase 8 (mixins and traits) added two composition mechanisms split by
keyword. **Traits** (`trait`/`use`) are pure compile-time inlining with
zero runtime representation — `Resolver.fs` recursively flattens a trait's
own members (memoized, cycle-detected) and statically merges them into
whichever class(es) `use` it, PHP-style; a trait-provided method's
`self`/`super` resolve relative to the *using* class. **Mixins** (`with`)
compose a real, already-compiled class's methods/properties at *runtime*
instead, via a new `Mixin` opcode that mirrors `Inherit`'s "peek class,
copy members" mechanics (but pops its operand, since a mixin's value is
never needed again — `super` does not chain through a `with`-mixin under
this deliberately simplified, non-C3-linearized approximation; see
`ROADMAP.md`'s deferred-ideas list for the full C3 alternative). Any two
composed sources (two used traits, a trait vs. the superclass, two mixins,
a mixin vs. the superclass) sharing a member name is a compile-time error
— no `insteadof`/`as`-style disambiguation exists in the grammar; the
class's own **literal** body always wins silently for methods (ordinary
polymorphism), but a property redeclaration is still an error even against
composed sources (properties have no override semantics under Phase 7's
write-once model). The underlying `Resolver.fs` machinery is a `MemberSet`
algebra (`Properties`/`Methods` maps plus `overrideWith`/`mergeAll`/
`checkNoConflicts`/`buildOwnMemberSet` helpers, shared with Phase 7's own
collision detection) and a `ClassInfo` split into `OwnLiteral` (the class's
own body, exempt from trait-conflict checks) vs. `TraitContributed`
(post-trait-merge, checked against composed superclass/mixin sources for
*any* conflict) — this split was a genuine architectural correction found
via test-driven discovery mid-phase, not the original design. This phase
also involved two rounds of `AskUserQuestion`: the repository owner first
chose full C3 linearization for `with`, then, after a follow-up question
laying out C3's real architectural cost (per-class MRO, live
MRO-walking dispatch, runtime-resolved `super`), reconsidered and chose to
ship the simplified approximation now with C3 deferred — see
`docs/PLAN-0.2.md`'s Phase 8 entry for the full writeup.

Phase 9 (this phase) is docs-only: `docs/LANGUAGE.md` forked into a frozen
`docs/LANGUAGE-0.1.md` (since removed — releases are preserved by git tags now,
see `docs/EARLIER-VERSIONS.md`) plus a new, current `docs/LANGUAGE.md` describing
`0.2` in full (the same fork-not-addendum pattern `0.1`'s own Phase 10
used, and this section mirrors for `CLAUDE.md` itself); every top-level
`langspec/examples/*.iqx` fixture verified end-to-end through the real
`compiler/`+`vm/` toolchain; `ROADMAP.md` marks `0.2` delivered and moves
the active-target goalposts to `0.3`.

## Architecture (`compiler/` + `vm/`, 0.3, in progress)

See `docs/PLAN-0.3.md` (the decision log and per-phase writeups). Phases 1-7
have landed: negative indices and slices; multi-generator/guarded list
comprehensions; unused-variable/-import warnings; multi-file compilation
(`compiler/src/Modules.fs`: path-derived module names, an `import` statement,
`pub` as the export marker, one `.iqbc` out, no `vm/` involvement beyond a
file table in bytecode format v3 so runtime errors name their source file);
an import-gated standard library; and a first set of compiler
optimizations (`compiler/src/Optimizer.fs`: constant folding, global constant
propagation, dead code; `iqaloxc --no-optimize` skips it). **Phase 6 supersedes the `0.2`
description above of a textually-prepended prelude**: `compiler/src/Prelude.fs`
is now `Stdlib.fs`, a set of built-in `std.*` modules (`std.vector`,
`std.matrix`) a program must `import`; `print` is the only ambient name; every
module (the entry file too) has its globals qualified (`main::x`) so a user's
own `sort`/`push` can never clobber a native in the VM's flat global table.

## Engineering conventions

- **`poc/` Python style**: match what's there — 4-space indents, type hints
  on public methods, `ABC`/visitor pattern for AST nodes, no
  docstrings/comments beyond the occasional `# TODO [#n]: ...` marker.
- **`poc/` testing**: standardizes on **pytest**. Run with `cd poc && pytest`
  (`poc/pytest.ini` sets `pythonpath = src`; dev dependencies are in
  `poc/requirements-dev.txt`). When adding non-trivial interpreter behavior,
  add tests under `poc/tests/` rather than relying on manual `.iqx` script
  runs alone. Note: `poc/src/token.py` shadows the stdlib `token` module —
  `poc/tests/conftest.py` works around this by evicting it from
  `sys.modules` before importing project modules; see
  `docs/PLAN-0.1-POC.md` §5 if that ever needs revisiting.
- **`compiler/`/`vm/` testing**: xUnit (F#) and Catch2 (C++) respectively,
  per `docs/PLAN-0.1.md` §7. `scripts/conformance-test.sh` and
  `scripts/phase7-run-smoke-test.sh` (0.1's Phase 9 cross-implementation
  diff against `poc/`, and the compiler/+vm/-only "does every 0.1 fixture
  still run" smoke test) were both **retired during `0.2` Phase 7**
  (`docs/PLAN-0.2.md`): decisions 8-11's breaking changes to the object
  model mean `compiler/`+`vm/` can no longer run `poc/`-era class
  fixtures at all, and the repository owner's explicit call was to drop
  cross-implementation/backward-compatibility testing entirely rather
  than maintain it going forward — pre-`1.0`, an earlier version's
  fixtures are historical artifacts, not a compatibility target.
- **`compiler/` F# style**: targets the current F# language version (F# 10
  as of .NET 10) — prefer newer idioms where they cleanly simplify existing
  code (e.g. a discriminated union's auto-generated `.IsCaseName` property,
  F# 9, over a two-arm `match` that only needed a case check, not its
  payload) rather than defaulting to older patterns out of habit. Don't
  force-fit a newer feature where it doesn't clearly help, though — e.g.
  nullable reference types (F# 9) have no real application here, since this
  codebase doesn't interoperate with null-returning APIs.
- **Example scripts**: live in `langspec/examples/*.iqx` (current
  active-target version); earlier versions' examples are in their git
  tags (`docs/EARLIER-VERSIONS.md`), except `langspec/versions/0.1/examples/`
  which stays in the tree for CI. These are cross-implementation-*capable*
  fixtures in principle (same input,
  same expected output, wherever more than one implementation can parse
  the syntax at all) — but there's no automated CI enforcing that anymore;
  see the `compiler/`/`vm/` testing bullet above for why
  cross-implementation/backward-compatibility conformance testing was
  retired entirely during `0.2` Phase 7. While a version is still being
  phased into `compiler/`+`vm/` (`docs/PLAN-0.X.md` §5), the top-level
  `examples/` describe that version's target spec and are expected to be
  ahead of what currently runs; verifying they actually run once their
  features land is a manual, per-phase step now, not a CI job. If an
  example depends on an unresolved design question, leave a note rather
  than changing the example to match a guess.
- **Commit style**: short, lowercase, imperative summaries (see `git log`) —
  no enforced conventional-commits format.

## GitHub Actions

**Any GitHub Actions workflow added to this repo must pin every `uses:` step
to a full commit SHA, never a floating tag or branch** (not `actions/checkout@v4`,
but `actions/checkout@<full-40-char-sha>`), including for first-party GitHub
actions. This is a supply-chain security requirement, not a style preference —
don't relax it for convenience.

Two workflows, two different jobs: `.github/workflows/ci.yml` runs on every
push/PR (build + test both toolchains, plus `poc/`'s own standalone test
suite — the cross-implementation conformance job and the 0.1-fixture smoke
test were both retired during `0.2` Phase 7, see the `compiler/`/`vm/`
testing bullet above); `.github/workflows/release.yml` fires only when a
GitHub Release is
published (`on: release: types: [published]`, not on tag push) and builds
`iqaloxc`/`iqaloxvm` for Linux/macOS/Windows, attaching each platform's
archive as a release asset. Release notes are written by hand against the
tag first (same process the `0.1.x-poc` releases used) — this workflow's
only job is producing the binaries, not the notes. `vm/CMakeLists.txt`'s
`IQALOX_BUILD_TESTS` option (default `ON`) is what lets `release.yml`
configure with `-DIQALOX_BUILD_TESTS=OFF`, so a release build of `iqaloxvm`
needs no Catch2 dependency at all.

## License

GPLv3 (`LICENSE`). No file header convention is currently in use — don't add
license headers to source files unless asked.
