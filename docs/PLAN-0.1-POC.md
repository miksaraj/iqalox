# Plan: getting to 0.1-poc

> **Note (added in `0.3` Phase 6):** this log refers to files that are no longer kept in the tree
> — `docs/LANGUAGE-0.1.md`, `docs/LANGUAGE-POC.md`, `langspec/versions/<n>/`, `langspec/archived/` —
> by their old paths. Git tags preserve them; see [`EARLIER-VERSIONS.md`](EARLIER-VERSIONS.md).

> **Note (post-`0.1` repo reorg):** `0.1-poc`'s code moved to `poc/` (was
> the repository root) as part of `docs/PLAN-0.1.md`'s Phase 0. The `src/`/
> `tests/`/`tools/` paths throughout this document describe the
> implementation *as it was built*, before that move — a historical
> record, left as-is rather than retroactively rewritten. Read `src/` etc.
> below as `poc/src/` etc. for where things actually live today.

Status snapshot based on reading `src/` against the `0.1-poc` feature list in
`ROADMAP.md` and the example scripts in `langspec/examples/*.iqx`. This is a
living document — update the status table and the open-questions list as work
lands or decisions get made. See `CLAUDE.md` for why the open questions are
listed rather than answered here.

## 1. Design decisions (resolved)

These aren't engineering choices; each one changes how the language behaves,
so they were routed to the repo owner per `CLAUDE.md` rather than guessed at.
Resolved 2026-07-05:

1. **Ternary fully replaces `if`/`else`, including for statement-like
   branches.** `continue`, `break`, and calls with side effects (`print k`)
   must all be usable as ternary branches, e.g.:
   ```
   (k == 0 or k == 5) ? continue : (k == 10) ? break : print k
   ```
   Engineering consequence: the ternary's middle/right branches can't be
   restricted to the `Expr` hierarchy the way `Ternary` is defined today —
   they need to accept statement-like constructs too (at minimum `break`/
   `continue` control-flow signals, plus ordinary expression-statements now
   that `print`/`concat` are calls rather than dedicated statements — see
   decision 4). The cleanest fit is probably: parse ternary branches via a
   grammar rule that covers both plain expressions and `break`/`continue`;
   evaluate `break`/`continue` branches by raising the same control-flow
   signal a loop body would use (see §4 step 4), letting it propagate up
   through ternary evaluation to the enclosing `for`. A ternary with a
   `break`/`continue` branch has no meaningful "value" on that arm — that's
   fine, since taking that arm never falls through to produce one.

2. **Prefix `++`/`--` uses standard mutating semantics.** `++x` assigns
   `x + 1` back to `x` and evaluates to the new value (matching every example
   that uses it: `++i` in `for` headers, `++c |> print` in `createCounter`).
   Reassigning an immutable (no `mut`) target should **preferably** be a
   compile-time error. Note: there is currently no static analysis/resolution
   pass in this codebase (no Lox-style `Resolver`) — one doesn't exist until
   the "disallow unused variables" work in 0.3/0.5 introduces it. Recommended
   approach for 0.1-poc: implement immutable-target rejection as a **runtime**
   error for now (reusing the existing `Environment.assign` mechanism, which
   already does exactly this for plain `=` assignment), and fold true
   compile-time enforcement into whatever static-analysis pass 0.3 introduces
   rather than building a one-off resolver just for this. Flagging this as a
   deliberate interim tradeoff, not a silent downgrade of the requirement.
   Also: `increment()` in the parser must reject non-assignable operands
   (only `Variable`, and later property-access, targets — not arbitrary
   expressions like `++5`).

3. **The self-reference keyword is `self`, not `this`.** `src/token.py`'s
   `TokenType.SELF = 'self'` is canonical; every `this` reference in current
   (non-archived) langspec docs/examples was outdated and has been corrected
   (see `langspec/examples/classes.iqx`, `langspec/SYNTAX_GRAMMAR.md`).
   Archived snapshots under `langspec/archived/` are left as-is (historical
   record).

4. **No function call — builtin or user-defined — takes parentheses.**
   `print`/`concat` are promoted to ordinary (builtin) function values, not
   statement keywords, and called the same way every other function is:
   without parens (`print x`, `concat [a, b]`, `add5 1`, `math.square 3`).
   This generalizes what was originally asked only for `print`/`concat` to
   *all* calls, resolving the "4b" exploration from an earlier draft of this
   doc, using the fixed-arity-juxtaposition option that was flagged there as
   the recommendation. Finalized shape, worked out from the earlier
   tradeoffs:
   - **Fixed-arity juxtaposition, no currying.** `fun f(a, b) {...}` keeps
     its existing declaration form; a call site provides exactly that many
     arguments, space- (and, for 2+, comma-) separated, immediately after
     the callee — see the grammar sketch below.
   - **Compound (non-primary) arguments need grouping parens.**
     `fact(n - 1)` becomes `fact (n - 1)` — parens here mean "group this
     sub-expression into one argument," not "this is the argument list."
     Simple single-token arguments need nothing (`add5 1`, `Duck "Waddles"`).
   - **Zero-argument calls keep explicit `()`** — `count()`, `B()`,
     `duck.quack()` — so a bare name can still mean "the function value
     itself, don't call it" (`print fact`, `return adder`, `test count`).
     Without this, first-class function values (already used throughout
     `functions.iqx`) would have no way to be passed around unevaluated.
   - **2+ arguments are comma-separated without an enclosing pair**:
     `ifEqualOr 2, 5`. The comma here is an argument separator, not the
     general comma operator — scoped out exactly like
     `Parser.comma_as_operator` already gets toggled off while parsing a
     `[...]` vector literal, just triggered by "currently parsing a bare
     call's argument list" instead.
   - **Nested calls need no extra parens**, because a call is itself a
     `primary`-level unit once fully parsed: `print concat [self.name, "quacks"]`
     parses as `concat` immediately followed by the vector-literal primary
     `[self.name, "quacks"]` — forming the complete call `concat([...])` —
     which then becomes `print`'s one argument, i.e.
     `print(concat([self.name, "quacks"]))`. No comma appears between
     `print` and `concat`, so `print` never tries to treat `concat` and the
     vector as two separate arguments.

   Rough grammar sketch (to be refined when functions/calls are actually
   implemented — see §4 step 5):
   ```
   call       → IDENTIFIER arguments? ( "." IDENTIFIER arguments? )*
   arguments  → "(" ")"                     ; explicit zero-arg call
              | argument ( "," argument )*  ; 1+ args, no wrapping parens
   argument   → "(" expression ")"          ; grouped/compound argument
              | primary
              | call                        ; nested call, e.g. `concat [...]`
   ```
   `src/statement.py`'s `Print`/`Concat` statement nodes need to go away
   once function calls exist and `print`/`concat` are registered as builtin
   function values in the global environment instead (§4 step 6).

5. **Mixins and traits are deferred out of 0.1-poc**, moved to `0.2` in
   `ROADMAP.md`. The PHP-vs-Scala-style implementation question from the
   original notes is unresolved and doesn't need to be until that work
   starts; `module`/`trait`/`use`/`with`/`extends` tokens already exist but
   have no grammar or semantics yet, and none should be built for 0.1-poc.

6. **"Support for standard methods" means baseline Lox method dispatch** —
   plain method definitions on classes, called via `instance.method(...)`.
   No built-in methods on primitive values (e.g. `.length()` on a string) are
   in scope for 0.1-poc; that's stdlib territory for a later version.

Additionally confirmed by re-reading history: `while` was **deliberately
removed** from the language (commit `1a22361`, "Removed while loop from
examples... since while was removed from the language specification").
`for` is the only loop construct. `langspec/SYNTAX_GRAMMAR.md` still listed
`whileStmt` and `ifStmt` (superseded by decision 1) — both removed from the
grammar draft as part of this update.

**Flagging, not deciding: function parameters are immutable by default.**
There's no grammar for marking an individual parameter mutable (`fun f(mut x)`
doesn't exist), so implementing `fun f(a) {...}` had to pick *something* for
whether `a` can be reassigned inside the body. Went with immutable — matching
the already-decided default for `var` — since no existing example reassigns
a parameter and it's the more conservative/consistent choice, not because
it's obviously the only right answer. If you want mutable-by-default
parameters, or a `mut` marker in the parameter list, this is a one-line
change (`is_mutable=False` in `IqaloxFunction.call()`, `src/callable.py`)
plus a small grammar addition for the marker case.

**Flagging, not deciding: instance fields (`self.x = ...`) are freely
reassignable, with no immutability enforcement at all.** Unlike function
parameters, there isn't even a `var`-shaped declaration step to hang a
default on — `self.x = value` is simultaneously "declare and assign" the
first time and "reassign" every time after, dynamically, so there's no
clean way to apply the `var`-style "immutable unless `mut`" default without
inventing a new mechanism (e.g. deciding what a first-vs-later assignment
even means when it depends on runtime state, or requiring fields to be
pre-declared somewhere). Went with plain, always-mutable fields — closer to
how Python/Lox objects behave by default — since no example needs anything
stricter and this avoids inventing a field-declaration syntax that isn't
in any spec anywhere. Worth an explicit decision later if field
immutability turns out to matter.

**Known limitation, not a decision: classes can't reference themselves by
name from inside their own methods.** jlox's book handles this by
`environment.define(name, null)` *before* building methods, then
`environment.assign(name, klass)` after — a placeholder-then-patch pattern.
Our `Environment` doesn't support that cleanly: `define()` errors on
redeclaration, and `assign()` errors on immutable targets, and class
bindings are (deliberately, matching function bindings) immutable — so
there's no clean placeholder step. Skipped rather than reworking
`Environment`'s API for a case no example needs (methods reference `self`/
`super`, never their own enclosing class by name).

**Known limitation, not a decision: chaining `.method()` directly onto a
call's result is ambiguous when that call takes an argument, and currently
resolves in favor of the argument.** `f x.y` correctly parses as `f(x.y)`
(the dot binds to the argument) — but by the same rule, `f x .y` variants
like `B "Bea".greet()` *also* bind the dot to the last argument, parsing as
`B(("Bea").greet())` instead of `(B("Bea")).greet()`. This is inherent to a
grammar with no parentheses marking where a call's argument list ends
(Lox's `f(x).y` has no such ambiguity, because `)` marks the boundary
unambiguously). Workaround, and what every actual example already does
anyway: bind the call's result to a variable first (`var b = B "Bea"` then
`b.greet()`), or wrap the whole call in its own grouping parens
(`(B "Bea").greet()`) if chaining inline is really wanted. Not fixed because
it would need a real grammar decision (e.g. requiring explicit grouping
around any call used as a chain target) rather than a parser bug fix.

## 2. Status of 0.1-poc feature list

Legend: ✅ done · 🟡 partial/buggy · ⛔ not started

| Feature | Status | Notes |
|---|---|---|
| Array support (vectors) | 🟡 | `Vector` expression + `visit_vector_expr` work for literals (`[1, 2, 3]`). No indexing, no mutation, no stdlib methods. Matrices/manipulation stdlib are 0.2 scope. |
| Block comments `<# ... #>` | ✅ | Fixed — see §3. |
| Implicit semicolons | ✅ | Newline → `SEMICOLON` token in `Scanner.scan_token`. A stray/empty semicolon (blank line, comment-only line) is now a harmless no-op statement instead of a hard parse error — see §3. |
| `continue` / `break` statements | ✅ | Implemented as zero-field `Expr` nodes (`Break`/`Continue`), not statements — see decision 1's engineering note. Usable anywhere an expression is (bare, or as a ternary branch); raise `BreakSignal`/`ContinueSignal` in the interpreter, caught by the enclosing `for`. |
| Prefix `++`/`--` | ✅ | Mutates via `Environment.assign` (so the existing immutability check applies), evaluates to the new value. Parser rejects non-`Variable` targets (`++5` is a parse error). |
| `extends` for inheritance | ✅ | `Class` statement's optional `superclass` (a `Variable` reference, resolved at class-declaration time); `IqaloxClass.find_method` walks the superclass chain; `super.method(...)` resolves lexically to the defining class's superclass, not the calling instance's actual class — see §2 status row for classes and §1's `super`-scoping note. |
| Chainable ternary (elvis `?:` too) instead of if/else | ✅ | `Ternary` node, both `? :` and `?:` forms parse and evaluate; nesting gives chaining. `break`/`continue` work as branches because they're expressions now (see above), not because `Ternary` itself changed. |
| Support for standard methods | ✅ | Plain method dispatch via `instance.method(...)` — see the `Classes` row below. |
| No `+` string concat | 🟡 | Unchanged from before — `+` on two strings raises a runtime error today only as a side effect of numeric type-checking, not an intentional string-specific rule. |
| `print` / `concat` as builtin functions | ✅ | Registered as `NativeFunction` instances (arity 1 each) in the global environment at `Interpreter.__init__`; `print`/`concat` are no longer keywords at all (removed from `_keywords`, scan as plain `IDENTIFIER`) — ordinary, shadowable bindings, called through the exact same `Call` grammar as user-defined functions. |
| Pipe operator `\|>` | ✅ | Desugars directly to a `Call` at parse time (`a \|> f` → `f(a)`, chains left-associatively) — no separate AST node or interpreter support needed. Scanner fix from §3 was a prerequisite (`\|` alone wasn't tokenizable). |
| Ignore operator `_` | ✅ | Zero-field `Ignore` expr (same pattern as `Break`/`Continue`), usable anywhere an expression is; evaluates to `nil` with no side effect. |
| Nullable infix `??` | ✅ | Own `null_coalescing()` precedence level, between `ternary` and `logic_or` (just above the conditional operator, below logical OR/AND — see §3). |
| Comma operator | ✅ | `comma()` in parser, precedence matches the table in the root `README.md`. Also fixed: see §3's vector-literal bugs — a stray comma-suppression bug in this same function silently corrupted multi-element vector literals. |
| Modulo `%` / power `^` | ✅ | Predates this project's Python implementation; sat at the same precedence level as `*`/`/` in `multiplication()`, but was unreachable from source text until the sixth-batch scanner fix (see §3) — never had a test or example exercising it before this pass. |
| Immutability by default (`mut`) | ✅ | `VariableData.is_mutable`, enforced in `Environment.assign`. The `var IDENTIFIER mut? = expr` parse path had a double-`advance()` bug that made `mut` declarations unparseable in practice — fixed, see §3. Function parameters are immutable by default too (no grammar yet for a `mut` parameter) — an assumption, not a design decision, flagged in §1. |
| `for` loops | ✅ | Full grammar (initializer/condition/increment all optional, per the drafted grammar minus the removed `whileStmt`). Loop-scoped `Environment` wraps the initializer; body executes via the normal `Block` mechanics. |
| Logical `and`/`or` | ✅ | `Logical` expr node, short-circuit evaluation, sits between `ternary` and `equality` in precedence (`ternary → logic_or → logic_and → equality`). |
| Functions (`fun`, calls, `return`, closures) | ✅ | `Function`/`Call`/`Return` AST nodes; `IqaloxFunction`/`NativeFunction` runtime callables in `src/callable.py`; closures work via capturing `self.environment` at declaration time (same object the function's name gets `define`d into, so recursion works); `Call` uses the paren-free grammar from decision 4 directly — no parenthesized form was ever built. |
| Classes (`class`, methods, instances, `init`, `super`, `self`) | ✅ | `Class`/`Get`/`Set`/`Self`/`Super` AST nodes; `IqaloxClass`/`IqaloxInstance` in `src/callable.py`; `.` member-access chaining added to `Parser.call()` (`call_head()` + `finish_property_access()`). Constructing an instance is just calling the class value (`Duck "Waddles"`, `Math()`) — arity comes from `init`'s arity (0 if no `init`). Fields (`self.x = ...`) are freely mutable (§1). Classes can't reference themselves by name from their own methods, and chaining `.method()` straight onto a call with arguments is ambiguous (both §1, known limitations, not bugs). |

Mixin support and trait support are **out of scope for 0.1-poc** (deferred to
0.2, decision 5) and are intentionally not in this table. Getters/setters
are also 0.2 scope — plain field access (`self.x`, `instance.x`) is all
0.1-poc supports, no forced accessor methods.

With classes done, every 0.1-poc feature and every Lox-baseline mechanic the
example scripts need is now implemented — see §4 for what's left (grammar
doc sync, more test coverage) versus what's genuinely still open.

## 3. Known bugs

### Fixed as part of implementing steps 1-4 (2026-07-05 batch)

Writing the first real tests against this interpreter (see §5) surfaced
several bugs well beyond the one originally documented here — the honest
takeaway is that essentially no multi-statement `.iqx` source had ever been
successfully run end-to-end before this batch, since several of these compound
to make basically any real script fail. All are plain correctness bugs, not
design questions, so fixed directly rather than routed for sign-off:

1. **Block comment terminator** (the one originally documented here).
   `Scanner.scan_token`'s block-comment loop exited as soon as *either*
   `peek() == '#'` *or* `peek_next() == '>'` individually, not when the
   two-character sequence `#>` was actually next. Fixed to advance while
   **not** (`peek() == '#' and peek_next() == '>'`); also now tracks
   newlines inside block comments so line numbers stay accurate afterward.
2. **`TokenType` didn't mix in `str`,** so every `some_char == TokenType.X`
   comparison against a plain scanned character was silently always `False`
   (an `Enum` member never equals a raw string unless it also subclasses
   `str`). This broke, in order of how badly: implicit-semicolon insertion
   (`c == TokenType.NEWLINE` never matched — newlines were reported as
   "unexpected character" instead of becoming semicolons), line comments
   (`token == TokenType.COMMENT` never matched, so `# ...` text leaked into
   the token stream as if it were code), and block comments (same issue for
   `TokenType.BLOCK_COMMENT_START`, meaning fix #1 above was previously dead
   code — the branch containing it was never entered). Fixed by declaring
   `class TokenType(str, Enum)`; verified no two members share a value
   (which would otherwise alias under the `str` mixin).
3. **Scanner's number/identifier dispatch tested the wrong character.**
   `scan_token()` decides whether to start scanning a number or identifier
   via `self.is_digit()`/`self.is_alpha()`, but those check `self.peek()` —
   the *next* character — not `c`, the one `scan_token` had just consumed.
   This is correct inside `number()`/`identifier()`'s own "keep consuming"
   loops (peeking ahead before advancing), but wrong at the initial dispatch
   site, where it needs to test `c` itself. Net effect: any single-character
   identifier or number adjacent to a delimiter (`i`, `n`, `x`, `0`...`9`
   alone) was misscanned as "unexpected character" — i.e. most loop counters
   and small integer literals in real programs. Fixed by giving
   `is_digit`/`is_alpha`/`is_alpha_numeric` an optional explicit-character
   parameter (defaulting to `peek()` for existing call sites) and passing
   `c` at the dispatch site.
4. **`true`/`false`/`nil` literals stored the `TokenType` enum member as
   their value**, not the Python value it stands for (`Literal(TokenType.FALSE)`
   instead of `Literal(False)`, etc.). Combined with bug #2's fix, this
   would have made `false` and `nil` evaluate as *truthy* (`is_truthy`
   checks `isinstance(obj, bool)`/`obj is None`, neither of which match a
   `TokenType` member). Fixed in `Parser.primary()` to use real `False`/
   `True`/`None`.
5. **`var` declarations' `mut` handling double-advanced.**
   `if self.match(TokenType.MUTABLE): is_mutable = True; self.advance()` —
   `match()` already consumes the `mut` token on success, so the extra
   `self.advance()` silently ate the *next* token too (typically `=`),
   making every `mut` declaration fail to parse. Fixed by dropping the
   redundant `advance()`.
6. **A blank or comment-only line aborted the entire program.** Every
   newline becomes a semicolon (by design), so a blank line produces a
   semicolon with no preceding expression. `expression_statement()`
   unconditionally tried to parse an expression there and failed, which
   (a) got "recovered" via `synchronize()` but still set `Iqalox.had_error`,
   and `Iqalox.run()` refuses to interpret anything at all once that flag is
   set — so *any* script with a blank line between statements (i.e. every
   realistic script, including every example in `langspec/examples/`) never
   actually ran; and (b) left a `None` in the parsed statement list from the
   failed/recovered declaration, which then crashed the interpreter with a
   raw `AttributeError` instead of a clean error. Fixed by having
   `Parser.statement()` treat a leading `SEMICOLON` as a no-op (returns
   `None` without raising), and having `Parser.parse()`/`Parser.block()`
   filter `None` out of the collected statement list (covering both this
   case and ordinary parse-error recovery).
7. **`src/iqalox.py` called `main(argv[1:])` unconditionally at module
   level**, with no `if __name__ == '__main__':` guard. Since `scanner.py`
   and `parser.py` both do a lazy `import iqalox` (to call back into
   `Iqalox.error(...)` without a circular top-level import), simply
   *importing* the scanner or parser module for any reason — including from
   a test suite — silently started a whole REPL session as a side effect.
   Fixed with the standard guard.
8. **Runtime errors were caught and silently discarded.**
   `Interpreter.interpret()` had `except IqaloxRuntimeError: return` with no
   call to `Iqalox.runtime_error(...)` — the method existed and was clearly
   intended for this, just never wired up. This mattered concretely for
   decision 2: an unenforced-looking immutability error is not the same
   thing as an enforced one. Fixed to call `Iqalox.runtime_error(error)`
   (and added minimal, non-crashing reporting for `break`/`continue` used
   outside of a loop, which is a plain `Exception` internally and has no
   token to build a "proper" error from).

### Resolved 2026-07-05 (second batch)

**`??` precedence.** Decided: put it where most languages put it (JS, C# —
just above the conditional operator, below logical OR). Moved out of
`multiplication()` into its own `null_coalescing()` level, inserted between
`ternary` and `logic_or`: `ternary → null_coalescing → logic_or → logic_and →
equality → ...`, left-associative like every other binary operator in this
parser. `README.md`'s precedence table updated to include it.

### Fixed as part of implementing steps 5-6 (third batch, functions/print-concat)

More of the same pattern as the first batch: implementing and actually
testing functions/calls surfaced more bugs that had nothing to do with
functions themselves.

1. **Multi-element vector literals were broken.** `comma()`'s handling of
   `self.comma_as_operator = False` (set while parsing `[...]`) did
   `self.advance()` unconditionally and then re-parsed and *discarded* the
   previously-parsed element, rather than just leaving the separating comma
   alone for the vector loop to consume. `[1, 2, 3]` silently dropped `1`
   and then failed outright on the comma before `3` ("Expect expression").
   Fixed by making `comma()` just delegate straight to `ternary()` when
   disabled, and having the vector-literal loop in `primary()` explicitly
   consume `element ("," element)*` itself.
2. **Single-element (and any) vector literals were *also* broken**, separately:
   the loop `while not self.match(TokenType.RIGHT_BRACKET): ...` already
   consumes the closing `]` as its own exit condition, but the code then did
   an unconditional `self.consume(TokenType.RIGHT_BRACKET, ...)` right after
   — trying to consume a second `]` that was never there, erroring on
   whatever token actually followed the vector. Fixed alongside #1 by
   checking for the empty-vector case up front and consuming `]` exactly
   once at the end.
3. **Running `iqalox.py` directly as the entry point split `Iqalox`'s error
   state into two independent copies.** `scanner.py`/`parser.py`/
   `interpreter.py` each do a lazy `import iqalox` to call back into
   `Iqalox.error()`/`runtime_error()`. When `iqalox.py` is launched with
   `python3 iqalox.py ...`, Python registers the running file as `__main__`,
   *not* as `iqalox` — so that lazy import doesn't find an already-loaded
   module and re-executes the whole file under the separate name `iqalox`,
   creating a second `Iqalox` class with its own independent `had_error`/
   `had_runtime_error`/`interpreter`. Error *messages* still printed fine
   (that just needs a `print` call), but `had_error` on the copy `run_file()`
   actually checks never got updated — so scripts with scan/parse errors
   kept partially interpreting instead of aborting, and the process always
   exited `0` instead of `65`/`70`. This is exactly the mechanism decision
   2's immutability enforcement (and now arity/"not callable" errors) relies
   on to actually stop a program, so it mattered concretely here, not just
   cosmetically. Fixed with `sys.modules.setdefault('iqalox', sys.modules[__name__])`
   near the top of `iqalox.py`, so both names resolve to the one module
   regardless of how it was launched. Note: this bug is invisible to the
   pytest suite, since tests never run `iqalox.py` as `__main__` — worth
   remembering if a future bug seems to only reproduce "from the CLI."
4. **The pipe operator token can't be scanned at all**, discovered (but not
   fixed — out of scope until step 7) while checking why `functions.iqx`/
   `control_flow.iqx`/`loops.iqx` still don't run: `scan_token()`'s dispatch
   requires the *single* character just consumed to itself be an element of
   `ONE_OR_MORE_CHARACTER_TOKENS` before it looks for a longer match, but
   `|` alone isn't listed there (only the full `'|>'` is) — every other
   multi-char operator works because its first character is *also* listed
   standalone (e.g. both `'-'` and `'--'` are present). `|` alone hits
   "Unexpected character" every time. Whoever picks up step 7 will need this
   fixed first, or `|>` can never be tokenized.

### Fixed as part of implementing steps 7 and 9 (fourth batch, pipe/ignore)

1. **The previous batch's bug #4 (pipe token unscannable), actually fixed
   this time.** Added `'|'` to
   `ONE_OR_MORE_CHARACTER_TOKENS` so the scanner's dispatch recognizes it as
   the start of a possible `|>`. This introduced a new failure mode worth
   guarding against: `'|'` alone (not followed by `>`) doesn't extend into
   anything and isn't itself a valid `TokenType` value, so the existing
   `self.add_token(TokenType(token))` call would raise an unguarded
   `ValueError` instead of a clean scan error — every other multi-char token
   in this table has its first character *also* independently valid, so this
   case never came up before. Wrapped in `try/except ValueError`, reporting
   "Unexpected character" the same way the rest of the scanner does.
2. **`loops.iqx`'s `print i * i` was a latent regression from the previous
   (functions/print-concat) batch**, not something new: `i * i` is a binary
   expression, not a primary, so once `print` became an ordinary function
   subject to the "compound arguments need grouping" rule, this line should
   have been updated to `print (i * i)` at the same time as `fact (n - 1)`
   and `fib (n - 2)` were — it was simply missed because it doesn't look
   like a call at a glance. Caught by actually running `loops.iqx` after
   implementing pipe (which was the file's last remaining blocker) and
   getting "Operands must be numbers" instead of squares. Fixed.

With this batch, `functions.iqx`, `control_flow.iqx`, and `loops.iqx` all
run to completion via the real CLI for the first time (verified with
`python3 iqalox.py <file>`, exit code 0, sensible output — including the
pre-existing, not-mine-to-fix quirks already in those examples: `fib`
multiplies instead of adding its recursive calls, and `adder(i)` ignores
its own parameter and always returns `n + 1`). `classes.iqx`/`inheritance.iqx`
still correctly fail cleanly (exit 65) pending classes (step 10).

### Noticed, not fixed (out of scope for this batch)

**Identifiers starting with `_` don't scan correctly.** `_` is listed in
`SINGLE_CHARACTER_TOKENS`, checked *before* the alpha/identifier dispatch in
`scan_token()`, so a name like `_foo` tokenizes as a bare `UNDERSCORE`
followed by a separate `foo` identifier, not one `_foo` identifier. This
predates the ignore operator and is unrelated to it (this batch only needed
a *bare*, standalone `_` to scan correctly, which it already did) — no
example or test needs a leading-underscore identifier today, so this is
noted for whenever it actually blocks something, not fixed speculatively.

### Fixed as part of implementing step 10 (fifth batch, classes)

1. **A blank line right after `class Name {` failed to parse**, the same
   class of bug as the original blank-line/empty-statement issue from the
   first batch, just not yet exercised in this specific position:
   `class_declaration()`'s method-collecting loop called
   `function_declaration('method')` directly, which unconditionally expects
   an `IDENTIFIER` next — but the newline right after `{` is a stray
   semicolon (implicit-semicolon insertion doesn't know it followed a `{`
   with nothing on that line), so it errored "Expect method name." instead
   of just skipping the empty statement the way `block()`/top-level
   `declaration()` already do. Fixed by skipping a leading `SEMICOLON` in
   the loop, same fix shape as the original.
2. **`classes.iqx` and `inheritance.iqx` were both missing their trailing
   newline**, unlike every other example file — meaning their last
   statement had no terminator at all (there's no "EOF also terminates a
   statement" rule) and always failed to parse. Not a classes-specific bug
   (any file missing a trailing newline would hit this), just never
   surfaced before because these two files could never even get that far
   until classes existed. Fixed by adding the missing newline to both files.

With both fixed, `classes.iqx` and `inheritance.iqx` run to completion via
the real CLI for the first time — meaning **every example script in
`langspec/examples/` now runs end-to-end successfully** (verified with
`python3 iqalox.py <file>`, exit code 0 for all five).

### Fixed during the final documentation/coverage pass (sixth batch)

1. **`%` (modulo) and `^` (power) could never actually be scanned**, despite
   full parser (`multiplication()`) and interpreter (`visit_binary_expr`)
   support existing since before this project's Python implementation began
   (`bd33b39`, the original scaffolding commit). `token.py`'s
   `SINGLE_CHARACTER_TOKENS` never listed `'%'`/`'^'`, so the scanner's
   dispatch treated both as "unexpected character" — every `a % b`/`a ^ b`
   in the entire history of this codebase would have failed to parse. Found
   while writing `tests/test_operators.py` to close a coverage gap (no
   example or test exercised either operator), not by design review — this
   is a scanner bug, not a design question, since the operators were already
   fully implemented one layer up. Fixed by adding both to
   `SINGLE_CHARACTER_TOKENS`.

### Fixed post-0.1.0-poc: GitHub issues #1 and #2 (seventh batch)

1. **Issue #1, "Improve error reporting to show the user exactly where the
   error is."** `Token` gained a 1-indexed `column` field; `Scanner` tracks
   `line_start`/`start_line`/`start_column` so every token (including ones
   spanning a multi-line string or block comment) reports the position it
   *started* at, not wherever scanning happened to end up. `Iqalox.run()`
   now keeps `source_lines` around, and `Iqalox.report()`/`runtime_error()`
   print the offending source line plus a `^` underline spanning the whole
   lexeme, not just a bare line number. Along the way, fixed a related
   cosmetic wart in the same code path: an implicit semicolon's lexeme is a
   literal `'\n'`, which used to split the `at '...'` error text across two
   lines — now displayed as the word `newline`.
2. **Issue #2, "Handle a run of one or more invalid tokens as a single
   error."** `Scanner.scan_token()`'s fallback branch (genuinely
   unrecognized characters, e.g. `@`) now keeps consuming characters while
   they're *also* unrecognized (`is_recognized()`) before reporting, so
   `@@@` is one error naming the whole run, not three separate
   "Unexpected character" reports.

Both were pre-existing `TODO [#1]`/`TODO [#2]` comments in `iqalox.py`/
`scanner.py` referencing the actual GitHub issue numbers — straightforward
engineering fixes, not design questions, so implemented directly rather
than routed for sign-off. See `tests/test_scanner.py` and the new
`tests/test_error_reporting.py` for coverage.

### Fixed post-repo-reorg, found while writing the 0.1 F# scanner (eighth batch)

Writing `compiler/src/Scanner.fs` (the fresh `0.1` scanner, F#) carefully
rather than as a line-for-line port surfaced five real `scanner.py` bugs
that had never actually been proven correct — none needed design sign-off,
all are straightforward scanning bugs, not language-design questions:

1. **Decimal number literals never worked at all.** `number()`'s
   fractional-part check compared `self.peek()` against a digit test that
   *also* defaulted to `self.peek()` — the same '.' character checked
   against itself, never the character after it. `3.14` scanned as
   `NUMBER("3")`, `DOT`, `NUMBER("14")`, silently, since no test or example
   had ever used a decimal literal. Fixed by checking `self.peek_next()`
   instead, matching jlox's own `isDigit(peekNext())`.
2. **A leading-underscore identifier (`_foo`) misscanned** as a bare `_`
   (the ignore operator) followed by a separate `foo` identifier, since
   `_` was checked as its own token before the identifier dispatch ran — a
   limitation `docs/LANGUAGE-POC.md` §13 had explicitly flagged as
   known-but-deprioritized. Fixed by checking whether an alphanumeric
   character follows before deciding `_` is standalone.
3. **The `...` (ellipsis) token under-consumed by one character** — the
   compound-matching dispatch only ever advanced one extra character
   (`compound[1]`) regardless of a matched compound's actual length. Moot
   today (`...` has no grammar yet, `0.2` scope) but not worth leaving
   broken. Fixed with a longest-match search (candidates sorted by length,
   descending) that verifies and consumes a compound's *entire* remaining
   length.
4. **A bare `#>` outside a block comment was silently swallowed** with no
   token or error — an accident of treating `BLOCK_COMMENT_END` as just
   another entry in the same generic compound table used for operators,
   so a lone `#` immediately followed by `>` matched the `#>` compound and
   then matched neither of the two branches handling `token in
   COMMENT_TOKENS`. Fixed by excluding `BLOCK_COMMENT_END` from the
   compound search entirely — `#>` only has meaning as a terminator
   searched for from *inside* a block comment's own loop, never as a
   token in its own right.
5. **An unterminated block comment produced no error at all** (the loop
   just exited silently at EOF), unlike an unterminated string, which
   already errored correctly. Fixed to report `"Unterminated block
   comment."` at the opening `<#`'s position, the same way `string()`
   already reports at the opening quote.

Also removed `Scanner.match()`, left dead by fix 3's rewrite (nothing else
called it — `Parser.match()` is a separate, still-used method on a
different class). Escape sequences were deliberately *not* added here:
that's a genuinely new `0.1` feature (`docs/PLAN-0.1.md` decision 5), not
a `0.1-poc` bug — `docs/LANGUAGE-POC.md` §13 already documents their absence
as an intentional limitation, and `0.1-poc` should keep behaving exactly
as documented.

Regression tests for all five in `tests/test_scanner.py`. Also exercised
in the example scripts, since these are real, usable language behaviors
(not just error-handling edge cases): `langspec/examples/operators.iqx`
now uses a decimal literal (`var pi = 3.14`), and
`langspec/examples/functions.iqx` now has a function with a
leading-underscore parameter name (`secondOf(_first, second)`), a common
"intentionally unused" naming convention.

### Noticed via the 0.1 F# port, now patched here too

Per-owner direction: these were collected as they turned up across the
`0.1` implementation phases (`docs/PLAN-0.1.md`) rather than patching
`poc` one bug at a time, and fixed all together in one batch once Phase 8
landed (2026-07-06). Each entry below was already fixed in the
corresponding `compiler/`/`vm/` module first (see `docs/PLAN-0.1.md`'s
phase notes for that fix and its tests); the same fix, with the same
reasoning, is now also applied directly to `poc/src/` — regression tests
for all six live in `poc/tests/test_parser.py`, `test_operators.py`, and
`test_functions.py`. All six `langspec/examples/*.iqx` fixtures still
produce byte-for-byte identical output between `poc` and `compiler/`+`vm/`
after the batch (none of the fixtures happened to exercise any of these
bugs).

1. **`Parser.primary()`'s vector-literal handling can permanently disable
   the comma operator.** `comma_as_operator` is toggled off, the elements
   are parsed, then toggled back on with a plain assignment — but a parse
   error partway through the elements (e.g. `[1, 2, bad+]`) raises past
   that restore, leaving `comma_as_operator = False` for the rest of the
   file. Fixed in `compiler/src/Parser.fs`'s `Primary()` with a
   `try`/`finally` (`docs/PLAN-0.1.md` Phase 3).
2. **`Parser.for_statement()` can construct a `For` node with a `None`
   body**, crashing the interpreter later. `body = self.statement()` has
   no `None` check, but `statement()` returns `None` for a bare `;` body
   (`for (;;) ;`). Fixed in `compiler/src/Parser.fs`'s `ForStatement()` by
   treating a `None` body as an empty block (`docs/PLAN-0.1.md` Phase 3).
3. **The comma operator always evaluates to `nil`.** `(1, 2)` returns
   `nil`, not `2` — `Interpreter.visit_binary_expr` never had a
   `TokenType.COMMA` case, so it silently falls through to the method's
   final `return None`. Every other language's comma operator means
   "evaluate both sides, discard the left, return the right" — that's
   what the grammar/precedence docs already imply and what
   `docs/PLAN-0.1-POC.md`'s own status table already claims is done.
   Found while designing `0.1`'s codegen (`docs/PLAN-0.1.md` Phase 5);
   implemented correctly there.
4. **`??` (null-coalescing) is not short-circuiting.** `5 ?? boom()` calls
   `boom()` even though `5` isn't `nil`, because `??` is wired through the
   same generic `Binary` node as `+`/`-`/etc., whose `visit_binary_expr`
   unconditionally evaluates *both* operands before even looking at the
   operator — unlike `and`/`or`, which get their own `Logical` node
   specifically so they can short-circuit. Every language with a `??`
   operator (JS, C#, Kotlin, Swift) only evaluates the right side when the
   left is nil/null. Found and fixed the same way as the comma operator
   above.
5. **Elvis `?:` evaluates its condition twice when it's truthy.** The
   parser reuses the exact same `expr` object for both `Ternary.left` (the
   truthiness check) and `Ternary.middle` (the "value if truthy" reuse),
   but `visit_ternary_expr` calls `self.evaluate()` on each independently
   — so a condition with a side effect (e.g. a function call) runs twice,
   defeating the entire point of the elvis form (reusing the condition's
   value instead of writing/evaluating it twice). `sideEffect() ?: false`
   prints its side effect twice. Found the same way as the other two;
   fixed in codegen by evaluating the condition once and reusing the
   already-computed value for the truthy branch (a jump-based pattern that
   makes double-evaluation structurally impossible, rather than a
   special-cased fix).
6. **`concat` crashes the whole interpreter on a non-vector argument**,
   instead of raising a clean `IqaloxRuntimeError`. `_native_concat` does
   `''.join(interpreter.stringify(value) for value in arguments[0])` with
   no check that `arguments[0]` is actually a list — `concat(5)` raises an
   uncaught Python `TypeError: 'float' object is not iterable` that
   propagates straight out of `Interpreter.interpret`'s exception handling
   (which only catches `IqaloxRuntimeError`/`BreakSignal`/`ContinueSignal`/
   `ReturnSignal`), printing a raw Python traceback instead of a normal
   `[line N] Error: ...` report. Found while implementing `0.1`'s native
   stdlib (`docs/PLAN-0.1.md` Phase 7); the VM's `concat` validates its
   argument's type and raises a normal runtime error instead.

### Still open

1. `error.py`'s `IqaloxRuntimeError.__str__`/`__repr__` just call `super()`,
   i.e. they're no-ops — fine to leave, but not worth keeping if nobody
   relies on the override.

## 4. Suggested sequencing

With §1 resolved, all of the following can proceed without further design
sign-off — flag anything that turns up a *new* design question rather than
deciding it inline (per `CLAUDE.md`).

**All steps except 11 and 12 (grammar doc sync, further test backfill) are
done** (branches `claude/0.1-poc-control-flow`, `claude/0.1-poc-functions`,
`claude/0.1-poc-pipe-ignore`, `claude/0.1-poc-classes`, 2026-07-05). Step 8
(prefix `++`/`--` mutation) was pulled forward from its original position
because `for` loops are untestable — infinite-looping — without a working
increment, and every existing loop example relies on `++i`/`++j`/`++k`. See
§2/§3 for what actually landed and what bugs surfaced along the way, and §5
for the test suite. As of the classes batch, **every example script in
`langspec/examples/` runs to completion end-to-end** for the first time.
Verified every batch via the actual CLI (`iqalox.py`), not just pytest —
see §3 bug #3 (module-duplication) for why that mattered.

1. ~~**Fix known bugs**~~ — done, see §3.
2. ~~**Logical `and`/`or`**~~ — done.
3. ~~**`for` loops**~~ — done.
4. ~~**`break`/`continue`**~~ — done, but *not* via the control-flow-exception-
   from-a-statement-position approach originally sketched here. Simpler
   approach actually used: `Break`/`Continue` are zero-field `Expr` nodes
   (parsed in `primary()`), so they're usable anywhere any expression is —
   bare, as a ternary branch, wherever — with no special-casing of `Ternary`
   needed at all. Evaluating one just raises `BreakSignal`/`ContinueSignal`
   (plain `Exception` subclasses in `interpreter.py`), which propagates
   naturally up through however many nested expression evaluations until
   `visit_for_stmt`'s per-iteration `try/except` catches it.
5. ~~**Functions**~~ — done. `Function`/`Call`/`Return` AST nodes;
   `IqaloxCallable`/`NativeFunction`/`IqaloxFunction` in the new
   `src/callable.py`; `Parser.call()` implements the paren-free grammar
   directly (`call → IDENTIFIER arguments?`, no `.` chaining yet — that's
   classes' job, step 10). `ReturnSignal` (an `Exception`, like
   `BreakSignal`/`ContinueSignal`) carries the return value up to
   `IqaloxFunction.call()`. See §1's flagged assumption on parameter
   mutability.
6. ~~**Promote `print`/`concat` to builtin functions**~~ — done.
   `Print`/`Concat` statement nodes are gone from `tools/generate_ast.py`
   and `parser.py`; `print`/`concat` are no longer keywords at all (removed
   from `token.py`'s `_keywords`, so they scan as plain `IDENTIFIER` and are
   ordinary, shadowable global bindings) — registered as `NativeFunction`
   instances in `Interpreter.__init__`.
7. ~~**Pipe operator `|>`**~~ — done. Desugars straight to a `Call` at parse
   time in the new `Parser.pipe()` (sits between `assignment` and `comma`
   in precedence — looser than everything except assignment) — no separate
   AST node or interpreter support needed. Required the scanner fix from §3
   bug #4 first (`|` wasn't scannable at all), which surfaced a second,
   related scanner bug (unguarded `ValueError` on a bare `|`) — see §3.
8. ~~**Prefix `++`/`--` mutation**~~ — done (pulled forward, see above).
9. ~~**Ignore operator `_`**~~ — done. Zero-field `Ignore` expr, same
   pattern as `Break`/`Continue`; evaluates to `nil`.
10. ~~**Classes**~~ — done. `class`, `extends`, `init`, methods, `super`,
    `self` (not `this`, decision 3); `.` member-access chaining added to
    `Parser.call()` (`call_head()` for the identifier-anchored head,
    `finish_property_access()` shared between `.`-chains and `super.method`,
    since both need the same "does a call immediately follow" check).
    Getters/setters and mixins/traits stay out of scope (0.2, decision 5) —
    plain field access only. See §1 for the field-mutability and
    self-referencing-class limitations, and §2 for the chaining-ambiguity
    limitation.
11. **Sync `langspec/SYNTAX_GRAMMAR.md` and `langspec/README.md`** with
    whatever actually got built. Patched so far: `while`/`if`/`this`
    removals, the real paren-free `call`/`arguments`/`argument` grammar
    (including `.` chaining and `classDecl`/`Get`/`Set`/`self`/`super`),
    `pipe`, and `_` in `primary`. Still stale: the
    `assignment`/`ternary`/logical/`null_coalescing` precedence chain's
    details (`null_coalescing` isn't shown at all; `ternary` doesn't reflect
    the elvis `?:` form) — doesn't block anything, just documentation debt.
12. ~~**Backfill pytest coverage**~~ — extended again this batch (class
    declarations, instances, fields, method dispatch, inheritance, `super`,
    arity-from-`init`, error cases), see §5. Keep extending as remaining
    stdlib/0.2 work lands rather than after the fact.

Steps 2–3 have no dependency on anything else in this list and could start
immediately.

## 5. Test suite

`tests/` (pytest, per `CLAUDE.md`) covers the scanner/parser/interpreter
behavior from every batch so far: implicit semicolons and empty-statement
tolerance, single-character identifier/number scanning, line and block
comments (including the terminator regression), `true`/`false`/`nil` literal
values, logical `and`/`or` short-circuiting and precedence, `??`'s
precedence, `for` loops with all clauses present/omitted, `break`/`continue`
as bare statements and as ternary branches, prefix `++`/`--` mutation plus
the immutable-target runtime error, the full paren-free call grammar (zero/
one/multi-arg, grouped compound arguments, nested calls, bare-reference-vs-
call disambiguation), function declarations/closures/recursion/return,
native `print`/`concat`, arity and not-callable errors, vector-literal
regressions (empty/single/multi-element), the pipe operator (single call,
chaining, non-identifier-target rejection, full-expression left side), the
ignore operator (bare, as a ternary branch, no side effect), and classes
(`tests/test_classes.py`: construction with/without `init`, field get/set
and free reassignment, method dispatch and overriding, inheritance and
`super` — including that `super` resolves lexically rather than by the
calling instance's actual class — arity derived from `init`, and the
undefined-property/non-instance/non-class-superclass error cases), and
arithmetic/comparison/logical operators including `%`, `^`, `??`, elvis
`?:`, `!`, division-by-zero, and non-number-operand errors
(`tests/test_operators.py`, added during the final documentation/coverage
pass — see §3's sixth batch for the scanner bug it turned up), token column
tracking and invalid-character-run coalescing (`tests/test_scanner.py`, see
§3's seventh batch), and source-excerpt/caret error reporting exercised
through the real `Iqalox` class rather than just the scanner/parser
directly (`tests/test_error_reporting.py` — note its `setup_function()`
resets `Iqalox`'s class-level `interpreter`/`had_error`/`had_runtime_error`/
`source_lines` state before each test, since that state is intentionally
shared/static across calls in the real CLI and would otherwise leak
between tests in the same file), and (added in §3's eighth batch)
decimal number literals, leading-underscore identifiers, exact `...`
consumption, a bare `#>` outside a block comment, and an unterminated
block comment's error. Run with `pytest` from the repo root (`pytest.ini`
sets `pythonpath = src`) — as of the post-`0.1` reorg, that's `poc/`, see
the note at the top of this file.

Deliberately **not** covered by this suite: anything requiring the actual
`iqalox.py` CLI *process* (module-duplication bug in §3, exit codes) —
pytest never runs `iqalox.py` as `__main__`, so that class of bug is
invisible to it by construction (`tests/test_error_reporting.py` exercises
the `Iqalox` class's methods directly, which is different from running the
file as a subprocess). Spot-checked manually via `subprocess.run(['python3',
'iqalox.py', ...])` instead; worth doing the same for any future change
that touches `Iqalox`/`main()`/error-flag plumbing specifically.

One infrastructure wrinkle worth knowing about: `src/token.py` shadows the
Python standard library's `token` module. Pytest's own bootstrap imports the
real stdlib `token` before test collection runs, caching it in
`sys.modules`, so `tests/conftest.py` has to explicitly evict that cache
entry (`sys.modules.pop('token', None)`) after inserting `src` onto
`sys.path`, or every project import resolves to the wrong `token` module.
This is a test-harness-only workaround — nothing in `src/` changed for it —
but if this file ever gets renamed for other reasons, the workaround (and
this comment) can go with it.
