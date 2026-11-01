/// The built-in standard library (`docs/PLAN-0.3.md` Phase 6). Nothing in
/// it is ambient except `print` (`Resolver.fs`'s `nativeGlobals`): every
/// other name lives in a built-in module a program must `import`
/// (`import std.vector.push, std.vector.map`), so a user program may
/// freely declare its own `sort` or `max` -- the repository owner's
/// "no silent shadowing" principle applies to the library too, and an
/// import colliding with a user declaration is the usual "already
/// declared" error.
///
/// A built-in module is a pair: **natives** (`vm/src/natives.cpp` -- they
/// need direct access to an `ObjVector`'s storage and never call back into
/// user code, so they stay registered in the VM under their bare names,
/// and an import of one binds straight to that bare runtime name) and
/// **source** (ordinary Iqalox compiled like any user module, only when
/// something imports it). `map`/`filter`/`reduce`/`sort`/`elementwise` are
/// source rather than natives because each must call back into a user
/// lambda and a native has no way to do that: `Vm::callNative` invokes the
/// C++ function synchronously and expects a `Value` back, while calling a
/// closure (`Vm::call`) only queues a `CallFrame` for the *existing*
/// dispatch loop -- there is no "call this closure and get its result"
/// primitive for native code. Writing them as Iqalox needs nothing new in
/// the VM.
///
/// `elementwise` deliberately does NOT validate that `a`/`b` are the same
/// shape the way `add`/`subtract`/`multiply` do (the repository owner's
/// explicit choice, `docs/PLAN-0.2.md` Phase 6): there's no `throw`/`raise`
/// construct in Iqalox itself, so a hand-written shape check here could
/// only ever produce the ordinary "Vector index N out of range" error a
/// step later anyway.
///
/// While a built-in module's own source runs, the natives are visible to it
/// as plain bare-named globals (it is compiled with them as root globals),
/// which is why it never has to import them from itself.
///
/// Call convention (confirmed empirically against the real parser, not
/// just by reading its grammar): a callee immediately followed by `(`
/// with a comma inside -- `fn(a, b)` -- is *not* a 2-argument call. It's
/// a 1-argument call whose single argument is the parenthesized
/// comma-operator expression `(a, b)` (`Parser.fs`'s comma-as-operator
/// default, only ever suppressed while parsing a call's own *unparenthesized*
/// argument list). Every multi-argument call below is written the
/// no-parens way instead (`fn a, b`), matching `push v, x`'s own existing
/// surface syntax.
module Iqalox.Stdlib

/// `std.vector`'s Iqalox-source half.
let private vectorSource =
    """
pub fun map(fn, v) {
    var result = []
    for (var i mut = 0; i < length(v); ++i) {
        push result, fn(v[i])
    }
    return result
}

pub fun filter(fn, v) {
    var result = []
    for (var i mut = 0; i < length(v); ++i) {
        fn(v[i]) ? push result, v[i] : nil
    }
    return result
}

pub fun reduce(fn, v, initial) {
    var acc mut = initial
    for (var i mut = 0; i < length(v); ++i) {
        acc = fn acc, v[i]
    }
    return acc
}

pub fun sort(fn, v) {
    var result = [...v]
    var n = length(result)
    for (var i mut = 1; i < n; ++i) {
        var key = result[i]
        var j mut = i - 1
        for (; j >= 0 and fn key, result[j]; --j) {
            result[j + 1] = result[j]
        }
        result[j + 1] = key
    }
    return result
}
"""

/// `std.matrix`'s Iqalox-source half.
let private matrixSource =
    """
pub fun elementwise(fn, a, b) {
    var result = []
    for (var i mut = 0; i < length(a); ++i) {
        var row = []
        for (var j mut = 0; j < length(a[i]); ++j) {
            push row, fn a[i][j], b[i][j]
        }
        push result, row
    }
    return result
}
"""

/// A built-in module: `Natives` are VM-registered natives it exports under
/// their bare runtime names; `Source` is Iqalox compiled only when imported.
///
/// `SourceExports` lists the `pub fun`s in `Source` by hand (checked against
/// the parsed source by a test) -- it exists so the compiler can tell a user
/// who forgot an import which module a name lives in without parsing every
/// built-in module just to find out.
type BuiltinModule =
    { Name: string
      Natives: string list
      SourceExports: string list
      Source: string }

let modules: BuiltinModule list =
    [ { Name = "std.vector"
        Natives = [ "concat"; "push"; "pop"; "length"; "reverse" ]
        SourceExports = [ "map"; "filter"; "reduce"; "sort" ]
        Source = vectorSource }
      { Name = "std.matrix"
        Natives = [ "transpose"; "multiply"; "add"; "subtract" ]
        SourceExports = [ "elementwise" ]
        Source = matrixSource } ]

/// Every built-in native, across all modules.
let allNatives: string list = modules |> List.collect (fun m -> m.Natives)

let tryFind (name: string) : BuiltinModule option = modules |> List.tryFind (fun m -> m.Name = name)

/// Every name a built-in module exports, mapped to the module that does --
/// what `Resolver.fs` uses to turn "you used `push` without importing it"
/// from a silent runtime success (the VM registers natives under their bare
/// names regardless) into a compile-time error that says what to import.
let importHints: Map<string, string> =
    modules
    |> List.collect (fun m -> (m.Natives @ m.SourceExports) |> List.map (fun n -> n, m.Name))
    |> Map.ofList
