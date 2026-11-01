# The Iqalox Language (0.3)

> **Older versions** (`0.1-poc`, `0.1`, `0.2`) are preserved by git tags
> rather than as separate files — see
> [`docs/EARLIER-VERSIONS.md`](EARLIER-VERSIONS.md) for which tag holds
> each one's reference (this document as it stood at the `0.2` release is
> `git show 0.2.0:docs/LANGUAGE.md`). This file describes the current,
> current version, and marks what is **new for `0.3`** on top of `0.2`'s
> language. (At the `0.3` release it is `git show 0.3.0:docs/LANGUAGE.md`.)

This is the complete reference for Iqalox `0.3` — `0.2`'s language plus
the `0.3` additions (each marked "new for `0.3`") — implemented as a
compiler frontend (`compiler/`, F#) that compiles `.iqx` source to a
bytecode file, executed by a stack-based virtual machine (`vm/`, C++23).
It documents what the language **actually does today**, with runnable
examples and an explicit list of known limitations. `0.2` is, by design,
everything `0.1` had — same syntax, same semantics, same core language —
plus a large batch of additions (§4, §9, §11, §12, §13, and noted
throughout): vector indexing, lambdas, cons/list-comprehension vector
forms, vector-literal spread, an array-manipulation standard library, a
matrix standard library, `pub`/`mut` property visibility and mutability,
method `pub`/private visibility, and mixin (`with`)/trait (`trait`/`use`)
class composition. See `ROADMAP.md` for what's planned beyond this
milestone, `docs/PLAN-0.2.md` for the day-to-day `0.2` implementation
log, and `docs/PLAN-0.1.md`/`docs/PLAN-0.1-POC.md` for the earlier
versions' logs.

Every code sample below is valid Iqalox and, unless marked otherwise,
compiles and runs successfully. In a release bundle, run a program in one
step with `iqalox program.iqx` (the launcher for `iqaloxc run program.iqx`,
which compiles to a temporary bytecode file and runs `iqaloxvm` on it; the
exit status is the VM's). The two-step form — `iqaloxc program.iqx out.iqbc`,
then `iqaloxvm out.iqbc` — remains available. `iqaloxc` also runs a small
optimizer (constant folding, propagation of never-assigned top-level
`var`s with literal initializers, dead-code removal) that never changes a
program's behavior; `--no-optimize` turns it off. Longer, complete
programs live under `langspec/examples/*.iqx` — this version's
active-target fixture set (see `langspec/README.md`).
**Since `0.3` the standard library must be imported** ([§13](#13-the-standard-library)):
samples below that call a library function (`push`, `concat`, `map`, ...) leave
the `import std....` line out for brevity unless the sample is about imports.
`scripts/conformance-test.sh` (`docs/PLAN-0.1.md` Phase 9), which used to
diff fixture output against `poc/` byte-for-byte, was retired during
`0.2` Phase 7 (`docs/PLAN-0.2.md`): decisions 8-11's breaking changes to
the object model mean `compiler/`+`vm/` can no longer run `poc/`-era
class fixtures at all, and the repository owner's explicit call was to
drop cross-implementation conformance testing entirely rather than
maintain it — pre-`1.0`, backward compatibility with an earlier version
isn't a goal.

## Table of contents

1. [Introduction and classification](#1-introduction-and-classification)
2. [Lexical structure](#2-lexical-structure)
3. [Values and types](#3-values-and-types)
4. [Variables and mutability](#4-variables-and-mutability)
5. [Operators and precedence](#5-operators-and-precedence)
6. [Control flow](#6-control-flow)
7. [Functions, closures, and lambdas](#7-functions-closures-and-lambdas)
8. [The call syntax](#8-the-call-syntax-no-parentheses)
9. [Vectors: indexing, cons, comprehensions, and spread](#9-vectors-indexing-cons-comprehensions-and-spread)
10. [The pipe operator](#10-the-pipe-operator)
11. [Classes and objects](#11-classes-and-objects)
12. [Mixins and traits](#12-mixins-and-traits)
13. [The standard library](#13-the-standard-library)
14. [Errors](#14-errors)
15. [Known limitations](#15-known-limitations)
16. [Grammar and precedence reference](#16-grammar-and-precedence-reference)
17. [Modules and imports](#17-modules-and-imports-new-for-03)

## 1. Introduction and classification

Iqalox is Lox — the small teaching language from Bob Nystrom's *Crafting
Interpreters* — mutated and extended by the repository owner into a
personal language project. `0.2` builds directly on `0.1`'s compiler
frontend plus bytecode virtual machine (see `ROADMAP.md`'s architecture
note), adding real vector manipulation and a first, deliberately-scoped
pass at multiple-inheritance-style class composition.

Classifying `0.2` along the axes commonly used to describe programming
languages — all statements below describe this implementation as it
exists today, not a permanent commitment for later Iqalox versions:

- **Paradigm**: primarily **imperative/procedural**, with **class-based
  object orientation** (`class`, `extends`, `with`, `trait`/`use`, `self`,
  `super`) and a meaningful vein of **functional-language influence** —
  functions and lambdas are first-class values, closures capture their
  defining environment, list comprehensions and cons build vectors
  declaratively, and the pipe operator (`|>`) encourages composing calls
  left-to-right the way functional languages do. There is no logic- or
  constraint-programming aspect.
- **Typing discipline**: **dynamically typed**. There is no type
  declaration syntax anywhere in the grammar — variables, parameters, and
  properties hold whatever value is assigned, and type errors (e.g.
  `"a" + 1`) surface as runtime errors, not at compile time. Within that,
  Iqalox leans **strong**: operators check operand types and raise a
  runtime error rather than silently coercing between them (no
  string-to-number or number-to-string coercion happens for `+`, `-`,
  `*`, `/`, `%`, `^`, or the comparison operators).
- **Memory management**: fully **automatic**, via a real **mark-sweep
  tracing garbage collector** built into the VM (`vm/src/vm.cpp`), which
  allocates and traces every heap object (strings, vectors, functions,
  closures, classes, instances) itself. There is no manual memory
  management, no pointers, and no language-level concept of ownership or
  lifetimes exposed to Iqalox source code.
- **Execution model**: **ahead-of-time compiled to bytecode, then
  interpreted by a stack-based virtual machine** — source is scanned,
  parsed into an AST (`compiler/src/Ast.fs`), resolved into a bound tree
  with every binding/scope/slot already determined
  (`compiler/src/Bound.fs`), and lowered to a structured bytecode format
  (`compiler/src/Bytecode.fs`, `compiler/src/Codegen.fs`) that the VM
  (`vm/src/vm.cpp`) loads and executes via a fetch-decode-execute loop
  over an explicit value stack and call-frame stack. There is no
  tree-walking, no JIT, and no further optimization pass on the bytecode
  itself. **New for `0.2`/`0.3`**: the standard library is a set of built-in `std.*`
modules (`compiler/src/Stdlib.fs`, see [§13](#13-the-standard-library)),
imported like any other module and compiled into a program only when
something imports them; the one name available without an import is
`print`.
- **Evaluation strategy**: **eager (strict)** evaluation everywhere except
  the short-circuiting operators — `and`/`or`, `??`, and the
  ternary/elvis forms — which compile to conditional jumps rather than
  always evaluating every operand (see [§5](#5-operators-and-precedence),
  [§6](#6-control-flow)). Function and lambda arguments are fully
  evaluated before a call executes (**call-by-value** for the argument
  slots themselves; since the values passed may be references to heap
  objects, mutable values like vectors are shared by reference).
- **Scoping**: **lexical (static) scoping**, resolved entirely at compile
  time. `compiler/src/Resolver.fs` assigns every local variable a fixed
  stack slot and every non-local reference an upvalue, following the same
  scope/slot/upvalue algorithm as `clox` (*Crafting Interpreters*); the VM
  never does a name-based environment lookup at runtime for locals.
  Closures (both named functions and lambdas) capture the variables
  active at the point they're *declared*, not where they're *called* —
  implemented as `ObjUpvalue`s that point directly at a stack slot until
  that slot goes out of scope, then hold a closed-over copy. `super`
  resolution is **lexical** — it resolves to the class the calling method
  is *written in*, never to the calling instance's actual runtime class
  (see [§11](#11-classes-and-objects)); mixin composition (`with`) does
  **not** extend `super`'s chain at all (see
  [§12](#12-mixins-and-traits)).
- **Name-binding / mutability default**: **immutable by default**,
  enforced at **compile time** for local variables and function/lambda
  parameters — `var x = 1` followed by `x = 2` is a compile-time error;
  `var x mut = 1` opts in to reassignment. **New for `0.2`**: object
  properties now have their own, separate mutability model — a property
  is declared once, in the class body, as `var name`, `var name mut`,
  `var name pub`, or `var name pub mut` (no initializer expression; see
  [§11](#11-classes-and-objects)). Without `mut`, a property may be
  assigned **at most once** (typically inside `init`) — a second
  assignment is a runtime error, the property-level equivalent of local
  variables' compile-time immutability check, but necessarily a runtime
  one since which assignment is "the first" can depend on control flow.
  This replaces `0.1`'s model, where fields sprang into existence on
  first assignment with no immutability concept and no declaration
  requirement at all (see [§15](#15-known-limitations)).
- **Object model**: **class-based** (not prototype-based), with **single
  inheritance** via `extends`, plus, **new for `0.2`**, two additional
  composition mechanisms: **traits** (`trait`/`use`, PHP-style static
  copying of a trait's members into a class at compile time — a trait has
  no runtime representation of its own) and **mixins** (`with`,
  Scala-style composition of a real class's already-compiled
  methods/properties at runtime, via a dedicated `Mixin` opcode). Method
  dispatch remains **nominal** — a call resolves against the instance's
  actual runtime class (`ObjClass`'s `methods`/`publicMethods` maps,
  populated by copying superclass and mixin members in at
  `extends`/`with`-time), with no structural/duck-typed interface
  concept. **New for `0.2`**: declared properties and methods now carry
  **visibility** (`pub`/private) — see [§11](#11-classes-and-objects) for
  the full internal-vs-external access model, including the
  protected-like treatment of a subclass's own methods. The built-in
  value types (numbers, strings, booleans) remain plain values with no
  methods of their own — there's no `"hello".length()`-style access on
  primitives — but vectors are no longer purely literal-only data:
  indexing (`v[i]`), a growing set of array-manipulation functions, and a
  small matrix-manipulation library now operate on them (all as ordinary
  global functions, not vector methods — see
  [§9](#9-vectors-indexing-cons-comprehensions-and-spread) and
  [§13](#13-the-standard-library)).
- **Concurrency model**: **none**. Iqalox is single-threaded with no
  concurrency primitives, no `async`/`await`, and no scheduling model of
  any kind at this stage.
- **Error-handling model**: internally, the compiler front-end
  accumulates scan/parse/resolve/codegen errors as data (not
  exceptions), and the VM signals runtime faults via a C++ exception
  (`RuntimeError`) caught once at the top of `main`, but **none of this
  is exposed to Iqalox source code** — there is no `try`/`catch`/`throw`
  construct. A runtime error anywhere aborts the whole program (see
  [§14](#14-errors)).
- **Metaprogramming/reflection**: **none**. No `eval`, no reflection API,
  no macros.
- **Call syntax**: the standout, deliberately unusual axis for this
  language — see [§8](#8-the-call-syntax-no-parentheses). No function
  call, builtin or user-defined, takes parentheses; calls are formed by
  **fixed-arity juxtaposition** (`f x`, `f x, y`) reminiscent of
  ML-family languages, but *without* currying or partial application — a
  call's arity is fixed by the callee's declaration, not inferred from
  how many arguments happen to be written. **New for `0.2`**: postfix
  indexing (`v[0]`) is told apart from a paren-free call whose sole
  argument is a vector literal (`f [0]`) purely by **whitespace** — a `[`
  with no space before it is always indexing, a `[` with a space is
  always the pre-existing call form. This is the only place whitespace is
  significant anywhere in Iqalox's grammar (`langspec/SYNTAX_GRAMMAR.md`).
- **Syntax style**: brace-delimited blocks (`{ ... }`, not
  indentation-sensitive) combined with **implicit statement termination**
  on a newline (à la Python/JavaScript ASI) — either a real `;` or a
  newline ends a statement. Comments come in a shell-style line form
  (`# ...`) and a Pascal/ML-style block form (`<# ... #>`), not C-style
  (`//`, `/* */`).

## 2. Lexical structure

```
NUMBER      → DIGIT+ ( "." DIGIT+ )?
STRING      → '"' <any char except '"'>* '"' | "'" <any char except "'">* "'"
IDENTIFIER  → ALPHA ( ALPHA | DIGIT )*
ALPHA       → 'a'...'z' | 'A'...'Z' | '_'
DIGIT       → '0'...'9'
```

- **Numbers** are always floating-point — there is no separate integer
  type (`5` and `5.0` are the same value). Printing drops a trailing
  `.0`, so `print 5` prints `5`, not `5.0` (see
  [§13](#13-the-standard-library)).
- **Strings** may use single or double quotes interchangeably. Escape
  sequences are processed inside string literals — `\n` `\t` `\r` `\\`
  `\'` `\"` `\0` are recognized; any other character after a backslash is
  a compile-time scan error.
- **Comments**: `# ...` runs to end of line; `<# ... #>` is a block
  comment and may span multiple lines. Neither nests.
- **Implicit semicolons**: a newline is scanned as a statement
  terminator, exactly like a literal `;`. A blank line (or a
  comment-only line) becomes a harmless empty statement, not an error.
  The scanner turns *every* newline into an implicit `;` unconditionally,
  with no awareness of bracket/paren depth — see
  [§15](#15-known-limitations) item 9 for the resulting limitation on
  multi-line vector literals and grouping expressions.
- **Keywords** (cannot be used as identifiers): `and`, `class`, `false`,
  `fun`, `for`, `nil`, `or`, `return`, `super`, `self`, `true`, `var`,
  `extends`, `break`, `continue`, `mut`, `pub`, `with`, `trait`, `use`.
  (`module` is still reserved for future module support but has no
  grammar or meaning yet.) `print` and `concat`, and the whole
  array/matrix standard library (`push`, `pop`, `length`, `reverse`,
  `map`, `filter`, `reduce`, `sort`, `transpose`, `multiply`, `add`,
  `subtract`, `elementwise`), are **not** keywords — see
  [§13](#13-the-standard-library).

## 3. Values and types

Iqalox has five kinds of user-visible runtime value:

| Type | Literal syntax | Example |
|---|---|---|
| Number (always float) | digits, optional decimal point | `42`, `3.14` |
| String | single- or double-quoted | `"hi"`, `'hi'` |
| Boolean | `true` / `false` | `true` |
| `nil` | `nil` | `nil` |
| Vector | `[ ... ]`, comma-separated, cons, comprehension, or spread | `[1, 2, "three"]` |

Plus two callable/object kinds introduced by functions and classes:
function values (user-defined, lambdas, or native builtins) and class
instances — see [§7](#7-functions-closures-and-lambdas) and
[§11](#11-classes-and-objects).

There is also an internal `undef` value (distinct from `nil`), used to
mark a mutable local variable, or a non-`mut` property, that hasn't been
assigned yet. It is never actually observable in a running program —
every read checks for `undef` first and raises a runtime error
immediately, so it can't be printed, compared, passed to a function, or
stored anywhere (see [§4](#4-variables-and-mutability),
[§11](#11-classes-and-objects)).

**Truthiness**: only `nil` and `false` are falsy. Every other value —
`0`, `""` (empty string), and `[]` (empty vector) included — is truthy:

```
print (0 ? "truthy" : "falsy")     # truthy
print ("" ? "truthy" : "falsy")    # truthy
print (nil ? "truthy" : "falsy")   # falsy
```

**Equality** (`==`/`!=`) compares values directly and never raises —
comparing a number to a string, for instance, is a well-defined `false`,
not an error. **Ordering** comparisons (`>`, `>=`, `<`, `<=`) and
**arithmetic** (`+`, `-`, `*`, `/`, `%`, `^`) require both operands to be
numbers and raise a runtime error otherwise — there is no string
comparison or string `+` concatenation (use `concat`,
[§13](#13-the-standard-library)).

**New for `0.2`**: vectors are no longer literals-only data. `[1, 2, 3]`
still produces a value the same way it always has, but that value now
supports **indexing** (read and write), **cons** and **list-comprehension**
literal forms, **spread** inside an ordinary literal, and a real
array/matrix **standard library** — see
[§9](#9-vectors-indexing-cons-comprehensions-and-spread) and
[§13](#13-the-standard-library) for the full details. A matrix is not a
distinct type: it's simply a vector of vectors, indexed one level at a
time (`matrix[row][col]`) — see [§9](#9-vectors-indexing-cons-comprehensions-and-spread).

## 4. Variables and mutability

```
var x = 1          # immutable -- must be initialized
var y mut = 1      # mutable -- may be reassigned
y = 2              # OK
x = 2              # compile-time error: 'x' is immutable
var z mut          # OK -- mutable declarations may omit the initializer,
                    # but z starts as undef, not nil (see below)
```

An immutable (`var x`, no `mut`) local declaration **must** have an
initializer — `var x` with no `mut` and no `= ...` is a compile-time
error, since there would be no way to ever give it a value. A mutable
declaration may omit the initializer, in which case the variable starts
as `undef`.

Reassigning a local variable that wasn't declared `mut` is caught at
**compile time** — `compiler/src/Resolver.fs` tracks each binding's
mutability and reports an error before any bytecode is even generated.
Function and lambda parameters are immutable inside the body by the same
rule, with no per-parameter `mut` syntax to opt out.

Reading a mutable local variable before it's ever been assigned (`var z
mut` then using `z` with no assignment in between) is a **runtime**
error — `undef` is a distinct value from `nil`, and every variable read
checks for it first. This is deliberately a runtime check, not a
compile-time one: unlike immutability (which depends only on static
structure — was there an initializer, is there a later assignment), a
`var mut` binding's assignedness can depend on control flow the resolver
doesn't model (e.g. a variable assigned inside one branch of a ternary
but not another).

**Object properties (`self.x`) follow a related but distinct model, new
for `0.2`.** Unlike `0.1`, where fields sprang into existence on first
assignment with no declaration and no immutability concept at all, every
property a class uses must now be **declared** in the class body:

```
class Counter {
    var count mut   # declared, private, mutable
    ...
}
```

A property declared **without** `mut` may be assigned **at most once**
externally-invisible internal state is exactly what backs `init`-only
initialization — assigning it a second time is a **runtime** error (the
same `undef`-sentinel mechanism `var mut` locals use for
must-assign-before-read, reused here for write-once instead). A property
declared **with** `mut` may be reassigned freely, the same as a `var mut`
local. See [§11](#11-classes-and-objects) for the full property model,
including the separate `pub` visibility axis (whether a property is even
*readable*/*writable* from outside the class at all) and how the two
combine.

## 5. Operators and precedence

From loosest-binding to tightest (see
[§16](#16-grammar-and-precedence-reference) for the full grammar, and the
root `README.md` for the same chain presented tightest-first):

| Operator(s) | Meaning | Notes |
|---|---|---|
| `=` | assignment | right-associative; also assigns through a trailing `[i]` index (see [§9](#9-vectors-indexing-cons-comprehensions-and-spread)) |
| `\|>` | pipe | desugars to a call, see [§10](#10-the-pipe-operator) |
| `,` | comma | left-associative, sequences expressions |
| `? :` / `?:` | ternary / elvis | see [§6](#6-control-flow) |
| `??` | null-coalescing | `a ?? b` is `a` unless `a` is `nil`, else `b` |
| `or` | logical or | short-circuits |
| `and` | logical and | short-circuits |
| `==` `!=` | equality | never raises |
| `>` `>=` `<` `<=` | comparison | numbers only |
| `-` `+` | additive | numbers only, no string concat |
| `/` `*` `%` `^` | multiplicative | includes modulo and power, numbers only |
| `++` `--` | prefix increment/decrement | mutating, target must be a variable |
| `!` `-` | unary not / negate | |

```
print (1 + 2 * 3)      # 7
print (2 ^ 10)         # 1024
print (7 % 3)          # 1
print (nil ?? "fallback")   # fallback
print (!false)         # true

var x mut = 1
print (++x)            # 2 -- mutates x and evaluates to the new value
print (--x)            # 1
```

Every short-circuiting operator here (`??`, `and`, `or`, ternary/elvis)
compiles to a conditional jump in the emitted bytecode — the right-hand
side's bytecode is only reached, and thus only ever executed, when it's
actually needed. Elvis (`a ?: b`) in particular evaluates its condition
exactly once even though it's also the "then" value, via a jump that
reuses the already-computed value rather than emitting the condition's
bytecode twice.

**New syntax that is not part of this table.** This table (and the root
`README.md`'s copy of it) covers infix/prefix *operators* specifically —
it has never listed member access (`.`), since that's postfix call-chain
syntax rather than an operator with a precedence level relative to the
others. `0.2` adds more syntax in that same postfix/structural category,
which for the same reason doesn't appear as a table row either:
**indexing** (`v[i]`) chains onto a call exactly like `.` does and binds
tighter than any operator above it; **lambdas** (`(params) -> expr`),
**cons** (`[item | list]`), **list comprehensions**
(`[expr | x <- xs]`), and **vector-literal spread** (`[...v]`) are all
literal/primary-expression forms, not operators applied to an existing
expression — see [§9](#9-vectors-indexing-cons-comprehensions-and-spread)
for indexing/cons/comprehension/spread and
[§7](#7-functions-closures-and-lambdas) for lambdas.

## 6. Control flow

**There is no `if`/`else` statement and no `while` loop.** Both are
deliberately absent from the language; `while` was removed outright.

The chainable ternary operator replaces `if`/`else` entirely, including
for statement-like branches (side-effecting calls, `break`, `continue`):

```
var category = (n < 0) ? "negative" : (n == 0) ? "zero" : "positive"

for (var i mut = 0; i < 10; ++i) {
    (i == 3) ? continue : (i == 7) ? break : print i
}
```

`?:` is the **elvis** short form: `a ?: b` means "`a` if `a` is truthy,
else `b`" — it reuses the condition as the "then" value instead of
writing it twice:

```
var name mut = false
print (name ?: "anonymous")   # anonymous
name = "Waddles"
print (name ?: "anonymous")   # Waddles
```

`break` and `continue` are **expressions**, not statements — they're
parsed the same way `_` (see below) is, which is exactly what makes them
legal ternary branches. Each compiles to a jump out of (`break`) or back
to the top of (`continue`) the nearest enclosing `for` loop's bytecode;
using either outside of a loop is reported as a compile-time codegen
error rather than crashing anything.

`for` is the only loop construct, with the full three-clause C-style
form, every clause optional:

```
for (var i mut = 0; i < 5; ++i) { print i; }
for (;;) { break; }   # infinite loop, only escape is a break somewhere inside
```

The **ignore operator** `_` is an expression that evaluates to `nil` with
no side effect — useful as an explicit "do nothing" ternary branch:

```
var flag = true
flag ? _ : print "only prints when flag is false"
```

## 7. Functions, closures, and lambdas

```
fun add(a, b) { return a + b; }
var result = add 1, 2   # 3
```

Functions are first-class values. A bare function name with nothing
recognizable as an argument after it is a *reference* to the function,
not a call — this is what lets functions be passed around:

```
fun makeFive() { return 5; }
fun apply(func) { return func(); }   # explicit () -- see §8
print (apply makeFive)               # 5
```

Closures capture their **defining** environment, and that capture is
live (mutations to captured `mut` variables are visible across calls) —
the VM implements this with `ObjUpvalue`, which points directly at the
variable's stack slot for as long as that slot is live, then transitions
to holding a closed-over copy once the enclosing call returns:

```
fun createCounter() {
    var c mut = 0
    fun counter() { c = c + 1; return c; }
    return counter
}
var count = createCounter()
print count()   # 1
print count()   # 2
```

Recursion works via the normal name-binding mechanism (a function's own
name is bound in its enclosing scope before its body runs):

```
fun fact(n) { return (n == 1) ? n : (n * fact (n - 1)); }
print (fact 5)   # 120
```

A function called with the wrong number of arguments, or a non-callable
value called at all, is a runtime error.

**New for `0.2`: lambdas.** `(params) -> expr` is an anonymous function
value — a single expression body (no `{ ... }`, no `return`), otherwise a
closure exactly like any `fun`:

```
var double = (x) -> x * 2
print (double 21)         # 42

var makeAdder = (n) -> (x) -> x + n   # lambdas can return lambdas
var addFive = makeAdder 5
print (addFive 10)        # 15
```

A parenthesized parameter list is only read as a lambda's parameters if
the very next token after the closing `)` is `->` — otherwise the same
`(a, b)` shape is an ordinary grouped/comma expression (see
[§5](#5-operators-and-precedence)'s comma row). This is resolved by
lookahead **past** the closing paren, not by any other syntactic marker:

```
var pair = (1, 2)          # comma expression: evaluates to 2
var addPair = (a, b) -> a + b   # lambda: the -> disambiguates it
```

Lambdas are used throughout `0.2`'s standard library — `map`, `filter`,
`reduce`, and `sort` (see [§13](#13-the-standard-library)) all take a
lambda as their first argument.

## 8. The call syntax (no parentheses)

This is Iqalox's most distinctive departure from Lox (and from most
C-family languages): **no function call — builtin or user-defined — ever
takes parentheses to mark its argument list.** This was a deliberate,
non-negotiable design decision, not an oversight.

- **A single simple argument needs nothing**: `add5 1`, `Duck "Waddles"`,
  `math.square 3`.
- **Multiple arguments are comma-separated, with no wrapping parens**:
  `ifEqualOr 2, 5`. That comma is an argument separator, not the general
  comma operator (the same way the comma operator is suppressed inside a
  `[...]` vector literal).
- **A compound (non-primary) argument needs grouping parens** to mark
  where it ends: `fact (n - 1)`. Without the parens, `fact n - 1` would
  parse as `fact n` (a call) minus `1` — a binary subtraction, not one
  argument.
- **Zero-argument calls keep explicit `()`**: `count()`, `duck.quack()`.
  This is what lets a bare name mean "the function value itself" —
  without it, there'd be no way to distinguish "call `count` with no
  arguments" from "refer to `count` without calling it."
- **Nested calls need no extra parens**, because a fully-parsed call is
  itself just another primary value: `print concat [1, 2]` parses as
  `print(concat([1, 2]))`, with `concat [1, 2]` recognized as one complete
  call that becomes `print`'s single argument.
- Parentheses are **still used** for: grouping a compound argument
  (above), the explicit zero-arg marker (above), function
  *declarations'* parameter lists (`fun f(a, b) { ... }` is unchanged),
  and, **new for `0.2`**, lambda parameter lists (`(a, b) -> ...`, see
  [§7](#7-functions-closures-and-lambdas)) — only *call sites* lost their
  parentheses.

`print` and `concat`, and every array/matrix stdlib function, are
ordinary functions under this same grammar (see
[§13](#13-the-standard-library)) — there's no special
`printStmt`/`concatStmt` grammar rule the way Lox has a dedicated `print`
statement.

**Known ambiguity**: chaining `.method()` directly onto a call that
itself takes an argument binds to the argument, not the outer call — see
[§15](#15-known-limitations).

**New for `0.2`: indexing shares call syntax's paren-free juxtaposition
space, disambiguated by whitespace.** `v[0]` (no space before `[`) reads
as postfix indexing on `v`; `f [0]` (a space before `[`) reads as the
pre-existing call form — `f` called with the vector literal `[0]` as its
sole argument, exactly as `concat [1, 2]` above already does. This is the
only place whitespace is significant anywhere in Iqalox's grammar — see
[§9](#9-vectors-indexing-cons-comprehensions-and-spread).

## 9. Vectors: indexing, cons, comprehensions, and spread

`0.1`'s vectors were literals only — `[1, 2, 3]` produced a value with no
way to read an element back out, mutate it, or build one up
programmatically. `0.2` adds all of that.

### Indexing

```
var v = [10, 20, 30]
print v[0]        # 10
print v[2]        # 30

v[1] = 99
print v[1]        # 99
```

- 0-based, **bounds-checked** — reading or writing past either end of the
  vector is a runtime error (`Vector index N out of range.`), not
  silently `nil` or a resize.
- Indexing composes with anything that produces a vector, including a
  call's own result, and nests for matrix-style access:

```
fun makeVector() { return [1, 2, 3]; }
print makeVector()[0]   # 1

var grid = [[1, 2], [3, 4]]
print grid[0][1]        # 2
grid[1][0] = 100
print grid[1][0]        # 100
```

A matrix is not a distinct type — it's simply a vector of vectors,
indexed one level at a time. See [§13](#13-the-standard-library) for the
matrix-manipulation standard library that operates on this shape.

- **As an assignment target**, `v[i] = x` follows the same parsing
  pattern `self.x = value` already uses: parse an ordinary indexing
  *read*, then peel the trailing `[expr]` off specifically when it's
  immediately followed by `=`, and treat that step as the mutable
  target — not a general "any expression is assignable" rule.
  `langspec/SYNTAX_GRAMMAR.md`'s `assignment` production has the exact
  grammar.

**New for `0.3`: negative indices.** `v[-1]` reads (or writes) the last
element, `v[-2]` the second-to-last, and so on — a negative index needed
no new grammar at all, since `-1` already parses as an ordinary unary-minus
expression; only the runtime bounds check changed, to accept `-length..-1`
as valid (translated internally to `length + index`) alongside the
existing `0..length-1`:

```
var v = [10, 20, 30]
print v[-1]   # 30
v[-1] = 99
print v[-1]   # 99
```

An index outside `-length..length-1` either direction is still the same
"Vector index N out of range" runtime error as before.

### Slices

**New for `0.3`.** `v[a:b]` returns a **new** vector containing the
elements from index `a` through index `b`, **inclusive** of both ends —
unlike many other languages' half-open slices. Either bound may be
omitted (`v[:3]`, `v[2:]`, `v[:]`), defaulting to the start/end of the
vector respectively; either bound may be negative, using the same
from-the-end convention as single-index access:

```
var v = [10, 20, 30, 40, 50]
print v[1:3]    # [20, 30, 40] -- inclusive of index 3
print v[:2]     # [10, 20, 30]
print v[3:]     # [40, 50]
print v[:]      # [10, 20, 30, 40, 50] -- a full copy
print v[-2:-1]  # [40, 50]
```

Unlike single-index access, a slice is deliberately more lenient: an
out-of-range bound **clamps** into the vector's own extent rather than
raising a runtime error, and a `start` that resolves after `stop`
produces an **empty vector** rather than an error:

```
print v[0:100]  # [10, 20, 30, 40, 50] -- clamped, not an error
print v[3:1]    # [] -- start after stop, not an error
```

A slice always allocates a fresh vector — mutating the result never
mutates the source it was sliced from. There is no slice-assignment in
`0.3` (`v[a:b] = ...` is a parse error); slices are read-only this
version.

### Cons and list comprehensions

```
var tail = [2, 3, 4]
print [1 | tail]              # [1, 2, 3, 4]

var xs = [1, 2, 3, 4]
print [x * 2 | x <- xs]       # [2, 4, 6, 8]
```

Both forms share the same `[expr |` opening syntax and are told apart by
lookahead on the token immediately after `|`: a generator marker `<-`
means a list comprehension (`[expr | name <- source]`, evaluating `expr`
once per element of `source` with `name` bound to it, collecting the
results into a new vector); anything else means **cons**
(`[item | list]`, producing a new vector with `item` prepended onto a
copy of `list`).

**New for `0.3`: multiple generators and a guard clause.** Comma-separate
more `name <- source` clauses for a Cartesian product (desugars to nested
loops, the first-written generator outermost), and optionally follow the
generator list with a **second** `|` and a guard expression:

```
print [[x, y] | x <- [1, 2], y <- ["a", "b"]]   # [[1, 'a'], [1, 'b'], [2, 'a'], [2, 'b']]
print [n | n <- [1, 2, 3, 4, 5, 6] | n % 2 == 0] # [2, 4, 6]
```

A later generator's source expression may reference an earlier
generator's bound name — it's re-evaluated fresh on every iteration of
the outer loop(s), not just once up front:

```
fun upTo(n) { var r = []; for (var i mut = 0; i < n; ++i) { push r, i; } return r; }
print [[x, y] | x <- [1, 2, 3], y <- upTo(x)]
# [[1, 0], [2, 0], [2, 1], [3, 0], [3, 1], [3, 2]]
```

The guard is just an ordinary boolean expression (`==`, `!=`, `and`,
`or`, `!`, ...) — there's no dedicated filter-clause syntax beyond the
second `|`, and it sees every generator's bound name already in scope.
The bound name in each generator clause is a single identifier, not a
destructuring pattern.

Both forms desugar entirely at compile time into ordinary (nested, for a
multi-generator comprehension) loops inside a synthetic closure — there
is no dedicated cons/comprehension runtime representation, just the two
VM opcodes (`VectorLength`, `VectorAppend`) that closure's loop body
compiles down to, plus a conditional (compiled exactly like any other
ternary) around the append when a guard is present.

### Vector-literal spread

```
var a = [1, 2]
var b = [4, 5]
print [...a, ...b]              # [1, 2, 4, 5]
print [0, ...a, 2.5, ...b, 5]   # [0, 1, 2, 2.5, 4, 5, 5]
```

`...expr` inside a plain, comma-separated vector literal splices every
element of `expr` (which must itself be a vector) into the result in
place, and can be freely mixed with ordinary items in any order. Spread
only applies inside this comma-separated literal form — it does not
combine with cons or comprehension syntax (`[...a | b]` isn't meaningful
under `0.2`'s scope for this feature).

## 10. The pipe operator

`a |> f` desugars, entirely at parse time, to the call `f(a)` — there's
no separate runtime representation for a pipe at all, it's just sugar for
a call written in the other order. It chains left-associatively:

```
fun square(n) { return n * n; }
5 |> square |> print   # prints 25, equivalent to print(square(5))
```

The right-hand side of `|>` must be a bare function name (a variable
reference) — `a |> 1` is a compile-time parse error, since `1` isn't
something that can be called.

## 11. Classes and objects

```
class Duck {
    var name
    var species pub

    init(name, species) {
        self.name = name
        self.species = species
    }

    pub quack() {
        print self.name
    }
}

var duck = Duck "Waddles", "Mallard"   # calling the class constructs an instance
duck.quack()                            # Waddles
print duck.species                      # Mallard
```

- **`self`**, not `this`, is the self-reference keyword.
- A class with no `init` method has arity zero (`Math()`, no arguments).
  A class's construction arity is always derived from its `init`
  method's arity.

### Properties: declaration, visibility, and mutability

**New for `0.2`.** `0.1` let a method assign `self.x = ...` to any name
at all, springing the field into existence on first assignment, with no
declaration and no immutability concept. `0.2` requires every property a
class uses to be **declared** in the class body, with two independent
modifiers:

```
class Duck {
    var name              # private, immutable -- set once, from init
    var energy mut        # private, mutable -- self only
    var species pub       # public, immutable -- readable from outside
    var quacking pub mut  # public, mutable -- readable and writable from outside
    ...
}
```

- **`pub`** controls **external** visibility: whether code *outside* the
  class (`duck.species`) can even see the property exists. Without
  `pub`, an external read or write is a runtime error.
- **`mut`** controls **mutability**: without it, a property may be
  assigned at most once (a second assignment is a runtime error, the
  same `undef`-sentinel write-once mechanism [§4](#4-variables-and-mutability)
  describes); with it, a property may be freely reassigned.
- These two axes are independent and combine as all four
  `pub`/`mut` presence combinations — private+immutable (the default,
  neither modifier), private+mutable, public+immutable, public+mutable.
- A property declaration takes **no initializer expression** — `var x
  pub = value` is not valid syntax. A property's actual value comes from
  whatever assignment (typically inside `init`) happens to run first.

Property declarations have no initializer syntax, and there's no
getter/setter accessor-body concept either — `pub`/`mut` replace that
category of feature entirely for `0.2`'s scope (no custom get/set logic,
no computed properties).

### Methods: visibility

**New for `0.2`.** A method is declared `pub method(...)  { ... }` or
just `method(...) { ... }` (private, the default):

```
class Vault {
    var contents mut

    init() { self.contents = 0 }

    checkPin(pin) { return pin == 1234; }   # private -- self-only

    pub deposit(amount, pin) {
        self.checkPin pin ? self.contents = self.contents + amount : print "Wrong PIN."
    }

    pub balance() { return self.contents; }
}

var vault = Vault()
vault.deposit 100, 1234
print vault.balance()     # 100
# vault.checkPin 1234     # runtime error -- checkPin has no pub
```

`init` is **always** externally callable regardless of any `pub`
annotation (or lack of one) — the visibility system never blocks
constructing an instance.

### Internal vs. external access, and protected-like subclassing

The property/method visibility rules above apply only to **external**
access — code reaching a member through some other expression
(`vault.checkPin`, `duck.species`). **Internal** access — a method
reading or calling `self.x`/`self.method()` on itself — always bypasses
every `pub`/`mut` gate; this is purely syntactic (is the access written
as exactly `self.x`, not "does this code happen to be inside the same
class"), which is what gives Iqalox a **protected-like** access model for
free: a subclass's own method calling `self.inheritedPrivateMethod()`
counts as internal access relative to *that* method's own body, the same
as C++/Java/C#'s `protected` — not stricter, and with zero extra
bookkeeping needed to support it.

```
class LoggingVault extends Vault {
    pub depositLogged(amount, pin) {
        self.checkPin pin ? print "PIN accepted." : print "PIN rejected."
        self.deposit amount, pin
    }
}

var logging = LoggingVault()
logging.depositLogged 50, 1234   # reaches Vault's private checkPin fine
```

### Inheritance

**Single inheritance** via `extends`. The VM copies the superclass's
whole method table into the subclass once, at `extends`-time (`ObjClass`'s
`methods` map) — Iqalox has no syntax for adding methods to a class after
it's declared, so there's no need for a live pointer-chain walk instead:

```
class A { pub greet() { print "A"; } }
class B extends A { }
B().greet()   # A -- B has no override, inherits A's
```

- **`super.method()`** calls up to the superclass method, resolved
  **lexically** — i.e. relative to the class the calling method is
  *written in*, not the actual runtime class of the calling instance. A
  three-level hierarchy where only the middle class overrides and calls
  `super` demonstrates this: the grandchild still ends up invoking the
  grandparent's method through the middle class's `super` call,
  regardless of which subclass instance it's called on. Ordinary
  (non-`super`) method calls, in contrast, dispatch dynamically against
  the receiver's actual runtime class — so an inherited method calling
  `self.method()` correctly reaches a subclass's override.
- **Extending a non-class value** (`class B extends NotAClass { }` where
  `NotAClass` isn't a class) is a runtime error, checked when the `class`
  statement executes.
- A class can reference itself by name from inside its own methods — the
  resolver defines the class's own name in scope before resolving its
  methods, so a method can look itself, or another instance of the same
  class, up by name.
- A subclass **redeclaring** a property already declared by an ancestor
  is a compile-time error — properties have no override semantics
  (methods do; see [§12](#12-mixins-and-traits) for how this extends to
  `with`-mixed-in and `use`-traited members too).
- Printing a class value itself shows `<class Duck>`; printing an
  instance shows `<Duck instance>`.

## 12. Mixins and traits

**New for `0.2`.** Beyond single inheritance, a class can compose members
from other sources two different ways, split by keyword: **traits**
(`trait`/`use`) and **mixins** (`with`). They look similar on the surface
but work completely differently underneath, and that difference is
deliberate and visible.

### Traits (`trait`/`use`): compile-time static copying

```
trait Flyable {
    pub fly() { print "Flying!"; }
}

trait Swimmable {
    pub swim() { print "Swimming!"; }
}

class Duck {
    use Flyable, Swimmable

    pub quack() { print "Quack!"; }
}

var duck = Duck()
duck.fly()    # Flying!
duck.swim()   # Swimming!
duck.quack()  # Quack!
```

A `trait` is grammared like a class body (properties, methods, and even
nested `use` of other traits) but has **zero runtime representation** —
`use Flyable, Swimmable` statically inlines the named traits' members
into `Duck` entirely at **compile time** (`compiler/src/Resolver.fs`),
the same way PHP's `trait`/`use` works. A trait-provided method's `self`
and `super` resolve relative to the *using* class, not the trait itself.
Because a trait never exists at runtime, it can never be the target of
`extends` or `with` — only a real class can be mixed in that way (see
below).

### Mixins (`with`): runtime composition

```
class Named {
    pub greet() { print "Hello!"; }
}

class Robot with Named {
    pub beep() { print "Beep boop."; }
}

var robot = Robot()
robot.greet()   # Hello!
robot.beep()    # Beep boop.
```

`with` composes a **real, independently-instantiable class**'s already-
compiled methods and properties into the composing class at **runtime**,
via a dedicated `Mixin` opcode (mirroring how `extends`/`Inherit` copies
a superclass's members in, except the mixin's own value is discarded
afterward — it's never needed again). `with` can combine with `extends`
on the same class:

```
class Vehicle { pub honk() { print "Honk!"; } }
class Winged { pub fly() { print "Zoom!"; } }

class FlyingCar extends Vehicle with Winged {
    pub drive() { print "Driving."; }
}

var car = FlyingCar()
car.honk()    # Honk!
car.fly()     # Zoom!
car.drive()   # Driving.
```

`0.2` ships a **simplified, non-C3-linearized** composition algorithm for
`with` — multiple mixins compose independently rather than through a
full C3 method-resolution-order chain, and `super` does **not** chain
through a `with`-mixin the way it does through `extends`. A full
Scala-style linearization is deliberately deferred; see `ROADMAP.md`'s
"Language feature ideas under consideration" for the reasoning.

### Conflict resolution

Two composed sources — two used traits, a used trait and the class's own
superclass, two `with`-mixins, or a mixin and the superclass — sharing a
member name is a **compile-time error**. There is no `insteadof`/`as`-style
disambiguation syntax (PHP has one; `0.2`'s grammar doesn't); the only
ways out are renaming one of the conflicting members, or declaring the
member directly on the composing class, which **always silently wins**
over anything composed in:

- For **methods**, the composing class's own literal declaration
  overriding a composed one is ordinary polymorphism — no error, no
  special syntax needed.
- For **properties**, redeclaring one already provided by a composed
  source is **still a compile-time error** — properties have no override
  semantics at all (see [§11](#11-classes-and-objects)), so "the class's
  own body wins" doesn't extend to silently accepting a property
  redeclaration the way it does for methods.

```
trait A { pub greet() { print "A"; } }
trait B { pub greet() { print "B"; } }

class C {
    use A, B   # compile-time error: 'greet' is provided by both A and B
}
```

## 13. The standard library

**New for `0.3`: the standard library is import-gated.** `print` is the
only name available in every program without an import. Everything else
lives in a built-in module under `std` and must be imported by name,
exactly like names from your own modules ([§17](#17-modules-and-imports-new-for-03)):

```
import std.vector.{push, map}
import std.matrix.transpose
```

- **You can declare your own `sort`, `max`, `add`, `length`, ...** — none
  of them is reserved. (`print` is: declaring your own `print` is an
  "already declared" error.)
- **No silent shadowing, here too.** If you import `std.vector.sort` *and*
  declare your own `sort` in the same file, that's the usual
  `Variable 'sort' already declared.` error; rename the import with `as`
  (`import std.vector.sort as stdSort`).
- **Forgetting an import is a compile-time error** that says what to
  add: `'push' is part of the standard library and isn't in scope; add
  'import std.vector.push'.`
- An unused standard-library import gets the usual unused-import warning
  ([§14](#14-errors)).
- A built-in module is only compiled into your program if something
  imports it, so a program that uses none of it carries none of it.
- `std` is reserved: a file of yours at `std/vector.iqx` that would
  collide with a built-in module is a compile-time error, not a silent
  override.

The modules:

- **`std.vector`** — `push`, `pop`, `length`, `reverse`, `concat`, `map`,
  `filter`, `reduce`, `sort`
- **`std.matrix`** — `transpose`, `multiply`, `add`, `subtract`,
  `elementwise`

### Core

- **`print value`** — stringifies `value` (see [§3](#3-values-and-types)'s
  stringification rules: `nil` → `nil`, floats drop a trailing `.0`,
  booleans lowercase) and writes it followed by a newline. Returns `nil`.
  Always available, no import needed.

### Array manipulation (`std.vector`)

`push`, `pop`, `length`, `reverse` and `concat` are true natives
(`vm/src/natives.cpp`) — they operate directly on an `ObjVector`'s
storage and never call back into user code:

- **`push vector, value`** — appends `value` to `vector` **in place**.
  Returns `nil`.
- **`pop vector`** — removes and returns the last element of `vector`
  **in place**. A runtime error on an empty vector.
- **`length vector`** — returns the element count as a number.
- **`reverse vector`** — returns a **new** vector with elements in
  reverse order; does not mutate the original.
- **`concat vector`** — stringifies every element of `vector` and joins
  them with no separator, returning the resulting string. This is
  currently the *only* way to build a string from non-string values or to
  join strings together, since `+` doesn't work on strings
  ([§3](#3-values-and-types)). A non-vector argument is a runtime error.

```
concat ["a", 1, "b"]   # "a1b"
```

`map`, `filter`, `reduce`, and `sort` are not natives — they're ordinary
Iqalox `fun` declarations (`compiler/src/Stdlib.fs`), compiled like any
other module when imported, using the same paren-free call syntax and
lambda syntax any user program has. They take
a lambda as their first argument and never mutate their vector
argument(s):

- **`map fn, vector`** — returns a new vector of `fn(element)` applied to
  each element.
- **`filter fn, vector`** — returns a new vector of only the elements for
  which `fn(element)` is truthy.
- **`reduce fn, vector, initial`** — folds `vector` left-to-right via
  `acc = fn(acc, element)`, starting from `initial`, returning the final
  accumulator.
- **`sort fn, vector`** — returns a new, sorted vector; `fn(a, b)` should
  return truthy when `a` belongs before `b` (e.g. `(a, b) -> a < b` for
  ascending, `(a, b) -> a > b` for descending).

```
import std.vector.{map, filter, reduce, sort}

var v = [3, 1, 4, 1, 5]
print (map (x) -> x * 2, v)              # [6, 2, 8, 2, 10]
print (filter (x) -> x % 2 == 0, v)      # [4]
print (reduce (a, b) -> a + b, v, 0)     # 14
print (sort (a, b) -> a < b, v)          # [1, 1, 3, 4, 5]
```

### Matrix manipulation (`std.matrix`)

A matrix is a vector of vectors, indexed one level at a time (see
[§9](#9-vectors-indexing-cons-comprehensions-and-spread)). `transpose`,
`multiply`, `add`, and `subtract` are true natives that validate their
argument(s) are a genuinely **rectangular** matrix (every row the same
length) and raise a dedicated, clearly-worded runtime error on a shape
mismatch (e.g. multiplying a 2x3 by a 4x2 matrix):

- **`transpose matrix`** — returns a new MxN → NxM transposed matrix.
- **`multiply a, b`** — matrix product; `a`'s column count must equal
  `b`'s row count.
- **`add a, b`** / **`subtract a, b`** — elementwise sum/difference;
  `a` and `b` must be the same shape.

`elementwise fn, a, b` is, like `map`/`filter`/`reduce`/`sort`, ordinary
Iqalox source (`compiler/src/Stdlib.fs`) rather than a native — it
applies `fn(a[i][j], b[i][j])` across every position and returns the
resulting matrix, but **deliberately does not validate** that `a` and `b`
are the same shape the way the four natives above do: since there's no
`try`/`throw` construct for library-level Iqalox code to raise a
dedicated error with, a shape mismatch here just falls through to the
ordinary "vector index out of range" error a step later — a real,
non-crashing runtime error either way, just not a custom-worded one.

```
import std.matrix.{transpose, multiply, add, subtract, elementwise}

var a = [[1, 2], [3, 4]]
var b = [[5, 6], [7, 8]]

print transpose a                        # [[1, 3], [2, 4]]
print multiply a, b                      # [[19, 22], [43, 50]]
print add a, b                           # [[6, 8], [10, 12]]
print subtract a, b                      # [[-4, -4], [-4, -4]]
print elementwise (x, y) -> x * y, a, b  # [[5, 12], [21, 32]]
```

Everything else commonly found in a language's standard library — string
manipulation beyond `concat`, math beyond the operators in
[§5](#5-operators-and-precedence), I/O, collections beyond vectors — is
**not implemented yet**; see `ROADMAP.md`'s "Standard library vision"
section for what's planned and roughly when.

## 14. Errors

Iqalox distinguishes two error phases, both of which currently **abort
the whole program** — there is no way for Iqalox source code to catch or
recover from either kind:

- **Compile-time errors** — scan errors (e.g. an unterminated string, an
  invalid escape sequence, an unexpected character), parse errors (e.g. a
  malformed ternary), resolve errors (e.g. reassigning an immutable
  variable, using `super` outside a subclass, an undeclared property, a
  trait/mixin member conflict), and codegen errors (e.g. `break` outside
  a loop). `iqaloxc` reports each as `[line N] Error: message` to stderr
  and exits with status `65`. Compilation never reaches the VM at all if
  any stage reports an error.
- **Runtime errors** — e.g. wrong arity, dividing by zero, a non-number
  operand to an arithmetic/comparison operator, an undefined
  variable/property, reading a `var mut` (or non-`mut` property) before
  its first assignment, reassigning a non-`mut` property a second time,
  an out-of-range vector index, extending/mixing-in a non-class,
  external access to a non-`pub` member, calling a non-callable value.
  `iqaloxvm` reports the message plus `[file, line N]` to stderr and
  exits with status `70`, after whatever output was already produced —
  `file` is the faulting instruction's source file, relative to the
  entry file's directory (`math/vector.iqx`; `main.iqx` for the entry
  file itself; `<std.vector>`/`<std.matrix>` for a fault inside the standard-library
  functions written in Iqalox, [§13](#13-the-standard-library)). The
  location comes from per-instruction-byte line and file tables the
  bytecode format carries alongside the instructions themselves — every
  emitted instruction is stamped with whichever source token
  `Codegen.fs` most recently had in view, the same way `clox` stamps
  every byte it emits with the parser's current line. Every runtime
  fault carries one, including operand type errors and errors raised by
  built-in functions (`pop` on an empty vector, ...). (Compile-time
  messages are unchanged: `[line N]` for the entry file, and
  `[module, line N]` for another module.)

**Remaining, smaller gap versus `0.1-poc`**: `0.1-poc` additionally shows
*which token* triggered the error (`Error at 'x': ...`) plus a caret-
underlined source excerpt, for both compile-time and runtime errors.
`0.2` has real line numbers for both, but not yet the token text or
source excerpt — the VM in particular has no access to the original
source text at all (it only ever receives a compiled `.iqbc` file), so
reproducing `0.1-poc`'s excerpt there would need embedding source text in
the bytecode format, not just a line table. See
[§15](#15-known-limitations) item 8.

There is no `try`/`catch`/`throw` construct in the language itself.

### Compile-time warnings (new for `0.3`)

Unlike the two error phases above, a **warning** never aborts compilation
or affects `iqaloxc`'s exit status — a program with only warnings still
compiles and runs exactly as before, they're printed to stderr as
`[line N] Warning: message` purely for the programmer's benefit.

The only warning `0.3` produces is **unused-variable detection**: a
declared local variable, function/lambda parameter, top-level `var`/`fun`,
non-`pub` class property/method, or `import` that's never actually used
gets flagged.

```
fun f(a, b) {
    return a       # 'b' is declared but never used
}
```

```
[line 1] Warning: Variable 'b' is never used.
```

A few rules shape what counts as "used":

- For a local variable, parameter, or top-level `var`, only a **read**
  counts — assigning to it (`x = 1`) without ever reading it back does
  **not** count as using it, and still warns. Capturing it into a nested
  closure always counts as a use, regardless of whether that closure
  reads or writes it.
- A top-level `fun` counts as used if it's called anywhere in the file,
  including from inside another function declared earlier or later in
  the same file (declaration order doesn't matter, same as any other
  top-level name). A top-level `class` is never checked at all — only
  `var`/`fun` declarations at the top level are covered.
- For a private class property or method, a `self.`-qualified reference
  counts **either way** — both `self.x` (read) and `self.x = value`
  (write) mark a property used, and `self.method()` marks a method used.
  This is a deliberate difference from the local-variable rule above: a
  property that's only ever written from `init` and never read elsewhere
  is still meaningful ("this needs to be set before it's used"), unlike a
  local that's assigned and then dropped.
- A `pub` property or method is never warned about, whether or not
  anything in the current program happens to call it — that's the point
  of marking something `pub`. `init` is likewise always exempt, since
  it's always externally callable regardless of its own `pub`/private
  annotation ([§11](#11-classes-and-objects)).
- A name starting with `_` is exempt from the warning entirely, at any of
  these positions — the same "explicitly don't care about this"
  convention the standalone `_` ignore-operator already uses
  ([§6](#6-control-flow)):

```
fun f(_unused, keep) {
    return keep
}
```

- The check only tracks `self.`-qualified references anywhere in the
  program, not whether an external caller happens to invoke a `pub`
  method — an external call alone doesn't suppress the warning for a
  non-`pub` member (and such a call would fail at runtime regardless,
  since a non-`pub` member can't be accessed from outside its class in
  the first place).
- The standard-library functions [§13](#13-the-standard-library) provides
  as ordinary top-level `fun` declarations under the hood (`map`,
  `filter`, `reduce`, `sort`, `elementwise`) are never warned about,
  whether or not the current program happens to call all five of them —
  a program that only calls `map` isn't told that `sort` is "unused"
  just because it was always declared for it.

## 15. Known limitations

These are documented tradeoffs and open items, not undiscovered bugs —
see `docs/PLAN-0.2.md`, `docs/PLAN-0.1.md`, and `docs/PLAN-0.1-POC.md`
for the full history of how each was arrived at.

1. **No string concatenation via `+`.** `"a" + "b"` is a runtime error
   (arithmetic `+` requires both operands to be numbers); use `concat
   ["a", "b"]` instead.
2. **Chaining `.method()` straight onto a call that itself takes an
   argument is ambiguous, and currently binds to the argument, not the
   outer call.** `B "Bea".greet()` parses as `B(("Bea").greet())`, not
   `(B("Bea")).greet()` — there's no parenthesis marking where a
   paren-free call's argument list ends, unlike Lox's `f(x).y`. The same
   ambiguity applies to indexing: `f x[0]` binds `[0]` to `x`, not to
   `f`'s result. Workaround: bind the call's result to a variable first,
   or wrap the whole call in its own grouping parens (`(B "Bea").greet()`).
3. **No built-in methods on primitive values.** Numbers, strings, and
   booleans have no methods of their own (`"hi".length()` doesn't
   exist); vectors gain real standard-library functions in `0.2`
   ([§13](#13-the-standard-library)), but as ordinary global functions,
   not vector *methods* — `v.length()` still doesn't exist, only
   `length v`.
4. **No concurrency, no exception handling, no reflection/metaprogramming**
   exist at the language level (see [§1](#1-introduction-and-classification)).
5. **`with`-mixin composition is simplified, not full C3 linearization.**
   Multiple mixins compose independently rather than through a real
   method-resolution-order chain, and `super` doesn't chain through a
   `with`-mixin the way it does through `extends`. Deferred deliberately
   (see [§12](#12-mixins-and-traits)); a full C3 algorithm is logged in
   `ROADMAP.md` as a future idea, not committed scope.
6. **No trait member-conflict disambiguation syntax.** PHP's `insteadof`/
   `as` have no equivalent here — a conflict between two composed sources
   is always a hard compile-time error; the only ways out are renaming or
   redeclaring the member directly on the composing class
   ([§12](#12-mixins-and-traits)).
7. **No token text or source excerpt on errors, unlike `0.1-poc`.**
   `0.1-poc` reports both compile-time and runtime errors as `[line N]
   Error at 'x': message` plus a caret-underlined single-line source
   excerpt. `0.2` reports a real `[line N]` for every error kind
   (including runtime errors, via a per-instruction-byte line table in
   the bytecode format itself), but not yet the offending token's text or
   a source excerpt. The excerpt/token-text part remains open, and for
   the VM specifically would need embedding source text in the bytecode
   format (it only ever receives a compiled `.iqbc` file, never the
   original source), not just a line table.
8. **A vector literal or grouping expression can't span multiple lines.**
   The scanner turns *every* newline into an implicit `;`
   unconditionally, with no awareness of bracket/paren depth — so
   `print (\n1 + 2\n)` or a `[...]` literal split across lines both fail
   with `Expect expression.` at the line following the opening bracket,
   the newline having already terminated the "statement" right after it.
   Not fixed here — making the scanner bracket-depth-aware is a real
   scanner change with its own test surface.
9. **A non-identifier callee can't be called at all, with or without
   parentheses.** The "am I followed by something that looks like an
   argument" check only ever runs for a bare `IDENTIFIER` (or,
   indirectly, a property/method access), so a grouped expression never
   gets that treatment — neither `(f) 5` nor even `(f)()` parses; the
   callee has to be bound to a name first. This makes an
   immediately-invoked lambda expression currently inexpressible: `((x)
   -> x + 1) 5` doesn't parse, even though `(x) -> x + 1` is a perfectly
   valid lambda value once bound to a variable.
10. **A one-line block whose last statement is a bare `return <value>`
    needs an explicit trailing `;` before the closing `}`.** ASI only
    ever fires on an actual newline, so `fun f(x) { return x }`
    (everything on one line) fails to parse — the closing `}` doesn't
    itself imply a statement terminator. Writing the body across multiple
    lines (or an explicit `; }`) works fine. Not fixed here — a real
    scanner change (teaching ASI that `}` also implies a terminator,
    mirroring how many C-like languages' "automatic semicolon insertion"
    rules work) with its own test surface.
11. **`elementwise`'s shape-mismatch behavior is generic, not
    dedicated.** Unlike `transpose`/`multiply`/`add`/`subtract` (true
    natives with clean, custom-worded shape-validation errors),
    `elementwise` is ordinary Iqalox source with no `throw`/`raise`
    construct available to it — a shape mismatch surfaces as the
    ordinary "vector index out of range" error a step later, not a
    dedicated shape-mismatch message. See [§13](#13-the-standard-library).
12. **A `pub trait`'s method bodies resolve in the *importing* module's
    name space.** Traits are inlined into whichever class `use`s them
    (compile time, no runtime representation), so a trait method that
    calls a private, module-local function of its own file won't find it
    when the trait is used from another module. (A `pub class`'s methods
    don't have this problem — they stay compiled in their own module.)
    See [§17](#17-modules-and-imports-new-for-03).

The following limitations from earlier versions are **resolved**:
vectors being literal-only with no indexing/mutation/length (now fixed as
of `0.2`, see `git show 0.1.0:docs/LANGUAGE.md` §13 for the original write-up;
[§9](#9-vectors-indexing-cons-comprehensions-and-spread),
[§13](#13-the-standard-library)), object fields having no immutability or
declaration concept at all (now fixed as of `0.2`, `git show
0.1.0:docs/LANGUAGE.md` §13; [§11](#11-classes-and-objects) — properties must be declared and
default to write-once), list comprehensions supporting only a single
generator with no guard clause (now fixed as of `0.3`, see
`git show 0.2.0:docs/LANGUAGE.md` §15 for the original write-up;
[§9](#9-vectors-indexing-cons-comprehensions-and-spread) covers the
multi-generator/guarded form), and runtime errors reporting only a
bare `[line N]` with no file — which mattered once a program could span
several files, and left a fault inside a standard-library function
pointing at a line number in text the user never wrote (now `[file, line
N]` for every runtime fault, [§14](#14-errors)).

## 16. Grammar and precedence reference

The authoritative, up-to-date grammar lives in
`langspec/SYNTAX_GRAMMAR.md`; the precedence table (same information,
presented tightest-to-loosest) is in the root `README.md`. Both describe
the language independent of any one implementation — refer to those files
directly rather than duplicating them here, since a third copy would only
be one more place for the three to drift out of sync.

## 17. Modules and imports (new for `0.3`)

A program can span several files. You compile only the **entry file**;
the compiler follows its `import` statements to find the rest and still
produces **one** bytecode file, so the VM never knows more than one source
file existed.

```
iqaloxc langspec/examples/modules/main.iqx out.iqbc
```

**Modules are files.** A file's module name is its path relative to the
entry file's directory, dotted, without the extension: `math/vector.iqx`
is module `math.vector`. There is no `module` declaration.

**`pub` exports.** Prefix a top-level `fun`, `var`, `class` or `trait`
with `pub` to make it importable from other files. Anything without
`pub` is private to its own file — though a `pub` function can still use
the file's private helpers freely. `pub` anywhere other than the top
level of a file is a compile error.

```
# math/vector.iqx
var offset = 100                    # private to this file
fun shifted(n) { return n + offset }  # private

pub fun double(n) {                 # importable
    return shifted(n) - offset + n
}
pub var origin = [0, 0]             # importable (add `mut` for pub var x mut = ...)
```

**`import`** brings specific names in, unqualified. The last dotted
segment is the name; everything before it is the module path. Several
names from one module can be grouped in braces so the module path isn't
repeated, `as` binds one under another name, and entries are
comma-separated:

```
import math.vector.double              # one name
import math.vector.origin as zero      # renamed
import shapes.{Shape, Named}           # several from one module
import std.matrix.{transpose, multiply as mul, add}, math.vector.double
```

A brace group may span lines (newlines inside the braces are ignored),
which keeps a long list readable. It needs at least one name, and a
trailing comma is not allowed. The braces are only a shorthand: they mean
exactly what the same names written out one by one would.

There is deliberately no wildcard (`import std.matrix.*`): once `1.0`
starts the compatibility promise, adding a function to a library module
would otherwise break any program that wildcard-imports it *and* declares
the same name itself. Named imports are immune to that.

- `import` is only allowed at the top level of a file (not inside a
  function, class or block), and may appear anywhere there.
- **Imported names are read-only.** You can read an imported `var` but
  never assign to it, even if its exporter declared it `mut`.
- **No silent shadowing.** Importing a name that collides with a
  declaration in the importing file, another import, a built-in or a
  standard-library name (`map`, `print`, ...) is a compile-time
  `Variable 'x' already declared.` error; `as` is how you resolve it.
- Importing a name that isn't `pub`, or doesn't exist, is a compile-time
  error naming the module and the name.
- An import that is never used gets the same kind of warning as an unused
  variable (`Import 'x' is never used.`, [§14](#14-errors)); naming the
  local binding with a leading `_` (`import a.b.x as _x`) exempts it.
- Two different modules may each declare a top-level `helper`; they don't
  clash, because each file's declarations are its own.
- Circular imports (including back to the entry file) are a compile-time
  error.
- Imported classes and traits compose like local ones: a class can
  `extends` or `with` an imported class and `use` an imported trait, and
  a subclass's `self.x` is checked against the imported class's members
  as usual (limitation: [§15](#15-known-limitations) item 12 for traits).

`langspec/examples/modules/` has a complete three-file program.
