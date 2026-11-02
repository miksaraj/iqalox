# Iqalox #

Iqalox is a programming language based on Lox, from Bob Nystrom's
[Crafting Interpreters](http://craftinginterpreters.com/), mutated and extended to suit the author's
preferences and interests. "*Iqalox*" is a play on the Kalaallisut word for Arctic char, *Eqaluk*, and *Lox*.

`0.3` is the current, primary implementation: a compiler frontend (F#, `compiler/`) that compiles `.iqx`
source to a bytecode file, executed by a stack-based virtual machine (modern C++23, `vm/`). This repository
also keeps `poc/` around — the original `0.1-poc` proof of concept, a Python tree-walk interpreter — as a
frozen, working reference implementation. Earlier versions are preserved by git tags; see
`docs/EARLIER-VERSIONS.md` for where to read each one.

## Getting started

**Option 1: download a release.** Grab the archive for your platform from the
[Releases page](https://github.com/miksaraj/iqalox.poc/releases) — each one bundles both binaries
(`iqaloxc`, `iqaloxvm`), a one-command launcher (`iqalox`), and the example `.iqx` scripts, and needs nothing else installed.

**Option 2: build from source.** You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download)
and a C++23 compiler (GCC 13+ or Clang 18+) plus [CMake](https://cmake.org/) 3.20+:

```sh
# Build the compiler
dotnet build compiler/src/Iqaloxc.fsproj

# Build the VM
cmake -S vm -B vm/build -DCMAKE_BUILD_TYPE=Release
cmake --build vm/build --target iqaloxvm -j
```

In a release bundle, run a program with one command:

```sh
./iqalox examples/classes.iqx        # iqalox.cmd on Windows
```

`iqalox` is a thin launcher for `iqaloxc run`, which compiles to a temporary bytecode file and runs
`iqaloxvm` on it (found next to `iqaloxc`, on the `PATH`, or via the `IQALOX_VM` environment variable).
The two steps are also available separately — compile to bytecode, then run it:

```sh
iqaloxc langspec/examples/classes.iqx classes.iqbc
iqaloxvm classes.iqbc
```

`iqaloxc` runs a small optimizer by default (constant folding, constant propagation of
unchanged top-level `var`s, dead-code removal); pass `--no-optimize` before the paths to skip it.

(If you built from source instead of downloading a release, replace `iqaloxc` with
`dotnet run --project compiler/src/Iqaloxc.fsproj --` and `iqaloxvm` with `vm/build/iqaloxvm`.)

See `docs/LANGUAGE.md` for the full language reference (with plenty of runnable examples and a list
of known limitations), `ROADMAP.md` for the version plan, and `docs/EARLIER-VERSIONS.md` for the `0.1`/`0.1-poc`/`0.2`
references. You'll find the **syntax grammar** in `langspec/`; the
**lexical grammar** and **precedence rules** are presented below.

## Lexical Grammar ##

    NUMBER          → DIGIT+ ( "." DIGIT+ )? ;
    STRING          → '"' <any char except '"'>* '"' | "'" <any char except "'">* "'" ;
    IDENTIFIER      → ALPHA ( ALPHA | DIGIT )* ;
    ALPHA           → 'a' ... 'z' | 'A' ... 'Z' | '_' ;
    DIGIT           → '0' ... '9' ;
    

## Precedence Rules ##

|       Name       |       Operators       | Associates |
|:----------------:|:---------------------:|:----------:|
|      Unary       |        `!` `-`        |   Right    |
|    Increment     |       `++` `--`       |   Right    |
|  Multiplication  |    `/` `*` `%` `^`    |    Left    |
|     Addition     |        `-` `+`        |    Left    |
|    Comparison    |  `>` `>=` `<` `<=`    |    Left    |
|     Equality     |       `==` `!=`       |    Left    |
|   Logical AND    |         `and`         |    Left    |
|    Logical OR    |         `or`          |    Left    |
| Null-coalescing  |          `??`          |    Left    |
|     Ternary      |    `? :` / `?:`       |   Right    |
|      Comma       |          `,`          |    Left    |
|       Pipe       |         `\|>`         |    Left    |
|    Assignment    |          `=`          |   Right    |

Listed tightest-binding (evaluated first) to loosest. `%`/`^` share
`*`/`/`'s precedence level rather than binding tighter, unlike some other
languages. `?:` is the elvis short form of the ternary (`a ?: b` ≡
`a ? a : b`) at the same precedence as the full `? :` form.

This table covers infix/prefix *operators* specifically — it has never
listed member access (`.`), since that's postfix call-chain syntax, not
an operator with a precedence level relative to the others. `0.2` adds
more syntax in that same postfix/structural category, which for the same
reason doesn't appear as a table row either: indexing (`v[i]`) chains
onto `call` exactly like `.` does and binds tighter than any operator
above; lambdas (`(params) -> expr`), cons (`[item | list]`), list
comprehensions (`[expr | x <- xs]`), and vector-literal spread
(`[...v]`) are all literal/primary-expression forms, not operators
applied to an existing expression. See `langspec/SYNTAX_GRAMMAR.md`'s
`call`/`primary`/`vector` productions for exactly how each of those
parses.
