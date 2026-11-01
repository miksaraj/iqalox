module StdlibTests

open System.IO
open Xunit
open Iqalox.Ast
open Iqalox.Scanner
open Iqalox.Parser
open Iqalox.Bound
open Iqalox.Codegen
open Iqalox.Modules
open Iqalox.Stdlib

// The standard library (docs/PLAN-0.3.md Phase 6): `print` is the only
// ambient name; everything else lives in a built-in `std.*` module that has
// to be imported. These tests exist partly because the library's Iqalox
// source is a plain string literal -- invisible to the F# compiler -- so
// nothing else would notice a syntax mistake in it.

let private root = Path.GetFullPath "/proj"
let private mainPath = Path.Combine(root, "main.iqx")

let private parseText (source: string) : Stmt list =
    let tokens, scanErrors = scanTokens (if source.EndsWith "\n" then source else source + "\n")
    Assert.Empty scanErrors
    let stmts, parseErrors = parse tokens
    Assert.Empty parseErrors
    stmts

let private builtin (name: string) =
    tryFind name |> Option.map (fun m -> Ok(parseText m.Source), m.Natives)

/// Links a single user program (`main.iqx`) against the real built-in modules.
let private linkMain (source: string) : LinkedProgram =
    let stmts = parseText source
    let parseFile _ = Ok(stmts, importedModuleNames stmts)
    match loadModulesWith builtin mainPath parseFile with
    | Ok modules -> linkModules modules
    | Error e -> failwith $"%A{e}"

let private mainErrors (linked: LinkedProgram) =
    (linked.Diagnostics |> List.find (fun d -> d.IsEntry)).Errors |> List.map (fun e -> e.Message)

let private mainWarnings (linked: LinkedProgram) =
    (linked.Diagnostics |> List.find (fun d -> d.IsEntry)).Warnings |> List.map (fun e -> e.Message)

let private globalNamesReferenced (linked: LinkedProgram) =
    let rec walk (e: BoundExpr) =
        match e with
        | BVariable(GlobalBinding n, _) -> [ n ]
        | BCall(c, args) -> walk c @ List.collect walk args
        | _ -> []
    linked.Bound
    |> List.collect (function
        | BExpressionStmt e -> walk e
        | BVarStmt(_, _, Some e) -> walk e
        | _ -> [])

[<Fact>]
let ``each built-in module's source parses and exports exactly the pub funs it lists`` () =
    for m in modules do
        let stmts = parseText m.Source
        let exported =
            stmts
            |> List.choose (function
                | PubStmt(_, FunctionStmt decl) -> Some decl.Name.Lexeme
                | PubStmt _ -> failwith $"{m.Name}: only pub funs expected"
                | other -> failwith $"{m.Name}: unexpected top-level statement %A{other}")
        Assert.Equal<string list>(List.sort m.SourceExports, List.sort exported)

[<Fact>]
let ``no name is exported by two built-in modules, or as both a native and source`` () =
    let names = modules |> List.collect (fun m -> m.Natives @ m.SourceExports)
    Assert.Equal(names.Length, List.distinct names |> List.length)

[<Fact>]
let ``import hints cover every exported name`` () =
    for m in modules do
        for n in m.Natives @ m.SourceExports do
            Assert.Equal(m.Name, importHints.[n])

[<Fact>]
let ``every built-in module resolves cleanly when imported`` () =
    let imports =
        modules
        |> List.collect (fun m -> (m.Natives @ m.SourceExports) |> List.map (fun n -> $"import {m.Name}.{n}"))
    let linked = linkMain (String.concat "\n" imports)
    for d in linked.Diagnostics |> List.filter (fun d -> d.IsBuiltin) do
        Assert.Empty d.Errors
    Assert.Empty(mainErrors linked)

[<Fact>]
let ``print is the only ambient name, so redeclaring it is still an error`` () =
    let linked = linkMain "var print = 1"
    Assert.Contains("Variable 'print' already declared.", mainErrors linked)

[<Fact>]
let ``a program may declare its own sort, max, add and length`` () =
    let source =
        "fun sort(x) {\n    return x\n}\nfun add(a, b) {\n    return a + b\n}\nvar max = 3\nvar length = 4\nprint sort(1)\nprint add 1, 2\nprint max\nprint length"
    let linked = linkMain source
    Assert.Empty(mainErrors linked)
    let referenced = globalNamesReferenced linked
    Assert.Contains("main::sort", referenced)
    Assert.Contains("main::max", referenced)

[<Fact>]
let ``using a standard-library name without importing it is a compile error that says what to import`` () =
    let errors = mainErrors (linkMain "var v = []\npush v, 1\nprint sort (a, b) -> a < b, v")
    Assert.Contains("'push' is part of the standard library and isn't in scope; add 'import std.vector.push'.", errors)
    Assert.Contains("'sort' is part of the standard library and isn't in scope; add 'import std.vector.sort'.", errors)

[<Fact>]
let ``importing a native binds straight to its bare runtime name`` () =
    let linked = linkMain "import std.vector.push\nvar v = []\npush v, 1\nprint v"
    Assert.Empty(mainErrors linked)
    Assert.Contains("push", globalNamesReferenced linked)

[<Fact>]
let ``importing a source-defined function binds to the module-qualified name`` () =
    let linked = linkMain "import std.vector.map\nprint map (x) -> x, [1]"
    Assert.Empty(mainErrors linked)
    Assert.Contains("std.vector::map", globalNamesReferenced linked)

[<Fact>]
let ``a standard-library import can be renamed with as`` () =
    let linked = linkMain "import std.vector.length as len\nprint len [1]"
    Assert.Empty(mainErrors linked)
    Assert.Contains("length", globalNamesReferenced linked)

[<Fact>]
let ``importing a std name and declaring the same name yourself is an already-declared error`` () =
    let linked = linkMain "import std.vector.sort\nfun sort(x) {\n    return x\n}\nprint 1"
    Assert.Contains("Variable 'sort' already declared.", mainErrors linked)

[<Fact>]
let ``importing a name a std module doesn't export is an error`` () =
    let linked = linkMain "import std.vector.nope"
    Assert.Contains("Module 'std.vector' has no top-level 'nope'.", mainErrors linked)

[<Fact>]
let ``an unused standard-library import is warned about like any other`` () =
    let linked = linkMain "import std.vector.push\nprint 1"
    Assert.Equal<string list>([ "Import 'push' is never used." ], mainWarnings linked)

[<Fact>]
let ``a built-in module is only compiled in when something imports it`` () =
    let none = linkMain "print 1"
    Assert.Equal<string list>([ "main.iqx" ], none.Segments |> List.map fst)
    let onlyMatrix = linkMain "import std.matrix.transpose\nprint transpose [[1]]"
    Assert.Equal<string list>([ "<std.matrix>"; "main.iqx" ], onlyMatrix.Segments |> List.map fst)

[<Fact>]
let ``built-in diagnostics are flagged so the CLI can keep them out of the user's output`` () =
    let linked = linkMain "import std.vector.map\nprint map (x) -> x, [1]"
    let std = linked.Diagnostics |> List.find (fun d -> d.Module = "std.vector")
    Assert.True std.IsBuiltin
    Assert.False (linked.Diagnostics |> List.find (fun d -> d.IsEntry)).IsBuiltin

[<Fact>]
let ``a user file that would shadow a built-in module is an error, not a silent override`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"iqalox_std_{System.Guid.NewGuid()}")
    Directory.CreateDirectory(Path.Combine(dir, "std")) |> ignore
    File.WriteAllText(Path.Combine(dir, "std", "vector.iqx"), "pub fun map(f, v) {\n    return v\n}\n")
    try
        let entry = Path.Combine(dir, "main.iqx")
        let stmts = parseText "import std.vector.map\nprint 1"
        let result = loadModulesWith builtin entry (fun _ -> Ok(stmts, importedModuleNames stmts))
        match result with
        | Error [ msg ] -> Assert.Contains("is built in", msg)
        | other -> failwith $"unexpected: %A{other}"
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``the whole library compiles to a single chunk`` () =
    let source =
        "import std.vector.push, std.vector.pop, std.vector.length, std.vector.reverse, std.vector.concat\nimport std.vector.map, std.vector.filter, std.vector.reduce, std.vector.sort\nimport std.matrix.elementwise, std.matrix.transpose, std.matrix.multiply, std.matrix.add, std.matrix.subtract\nprint map (x) -> x, [1]"
    let linked = linkMain source
    Assert.All(linked.Diagnostics, fun d -> Assert.Empty d.Errors)
    let _, fileNames, errors = compileSegments linked.Segments
    Assert.Empty errors
    Assert.Contains("<std.vector>", fileNames)
    Assert.Contains("<std.matrix>", fileNames)

[<Fact>]
let ``a brace group of std imports binds every name, aliases included`` () =
    let linked = linkMain "import std.vector.{push, length as len}\nvar v = []\npush v, 1\nprint len v"
    Assert.Empty(mainErrors linked)
    let referenced = globalNamesReferenced linked
    Assert.Contains("push", referenced)
    Assert.Contains("length", referenced)

[<Fact>]
let ``a name imported through a group collides like any other import`` () =
    let linked = linkMain "import std.vector.{sort}\nfun sort(x) {\n    return x\n}\nprint 1"
    Assert.Contains("Variable 'sort' already declared.", mainErrors linked)
