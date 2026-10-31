module ModuleTests

open System.IO
open Xunit
open Iqalox.Ast
open Iqalox.Scanner
open Iqalox.Parser
open Iqalox.Bound
open Iqalox.Resolver
open Iqalox.Codegen
open Iqalox.Modules

// All paths here are fake -- `loadModules` only ever calls the injected
// `parse` callback, never touches the file system itself.
let private root = Path.GetFullPath "/proj"
let private pathOf (name: string) = Path.Combine(root, name.Replace('.', Path.DirectorySeparatorChar) + ".iqx")

let private parseText (source: string) : Stmt list =
    let tokens, _ = scanTokens (if source.EndsWith "\n" then source else source + "\n")
    let stmts, parseErrors = parse tokens
    Assert.Empty parseErrors // a silently-dropped statement would make a test pass vacuously
    stmts

/// `files`: module name -> (source, dotted dependencies it declares).
let private loader (files: (string * string * string list) list) (path: string) =
    match files |> List.tryFind (fun (n, _, _) -> pathOf n = path) with
    | Some(_, source, deps) -> Ok(parseText source, deps)
    | None -> Error [ $"missing {path}" ]

let private names (r: Result<ModuleSource list, string list>) =
    match r with
    | Ok ms -> ms |> List.map (fun m -> m.Name)
    | Error e -> failwith $"expected Ok, got %A{e}"

let private prelude () = resolveUnit [] (parseText Iqalox.Prelude.source)

[<Fact>]
let ``a module's name is its path relative to the root, dotted`` () =
    Assert.Equal("math.vector", moduleNameOfPath root (pathOf "math.vector"))
    Assert.Equal("main", moduleNameOfPath root (pathOf "main"))
    Assert.Equal(pathOf "math.vector", modulePathOfName root "math.vector")

[<Fact>]
let ``a lone entry file loads as a single module`` () =
    let r = loadModules (pathOf "main") (loader [ "main", "print 1", [] ])
    Assert.Equal<string list>([ "main" ], names r)

[<Fact>]
let ``dependencies load before the module that needs them, entry last`` () =
    let files =
        [ "main", "print 1", [ "a"; "b" ]
          "a", "print 2", [ "b" ]
          "b", "print 3", [] ]
    Assert.Equal<string list>([ "b"; "a"; "main" ], names (loadModules (pathOf "main") (loader files)))

[<Fact>]
let ``a module needed by several others is parsed only once`` () =
    let mutable parses = 0
    let files = [ "main", "print 1", [ "a"; "b" ]; "a", "print 2", [ "c" ]; "b", "print 3", [ "c" ]; "c", "print 4", [] ]
    let counting path =
        parses <- parses + 1
        loader files path
    let r = loadModules (pathOf "main") counting
    Assert.Equal<string list>([ "c"; "a"; "b"; "main" ], names r)
    Assert.Equal(4, parses)

[<Fact>]
let ``a circular dependency is reported, not looped on`` () =
    let files = [ "main", "print 1", [ "a" ]; "a", "print 2", [ "b" ]; "b", "print 3", [ "a" ] ]
    match loadModules (pathOf "main") (loader files) with
    | Error [ msg ] -> Assert.Equal("Circular module dependency: a -> b -> a.", msg)
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``depending on the entry module is a cycle`` () =
    let files = [ "main", "print 1", [ "a" ]; "a", "print 2", [ "main" ] ]
    match loadModules (pathOf "main") (loader files) with
    | Error [ msg ] -> Assert.Equal("Circular module dependency: main -> a -> main.", msg)
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``a module that fails to load surfaces the callback's error lines`` () =
    match loadModules (pathOf "main") (loader [ "main", "print 1", [ "ghost" ] ]) with
    | Error [ msg ] -> Assert.Contains("ghost", msg)
    | other -> failwith $"unexpected: %A{other}"

let private link (files: (string * string * string list) list) =
    match loadModules (pathOf "main") (loader files) with
    | Ok ms -> linkModules (prelude ()) ms
    | Error e -> failwith $"%A{e}"

[<Fact>]
let ``two modules may each declare the same top-level name without a clash`` () =
    let linked = link [ "main", "var helper = 1\nprint helper", [ "dep" ]; "dep", "var helper = 2\nprint helper", [] ]
    Assert.All(linked.Diagnostics, fun d -> Assert.Empty d.Errors)

[<Fact>]
let ``a non-entry module's globals are qualified and the entry's are not`` () =
    let linked = link [ "main", "var helper = 1", [ "dep" ]; "dep", "var helper = 2", [] ]
    let declared =
        linked.Bound
        |> List.choose (function
            | BVarStmt(DeclaredGlobal n, _, _) -> Some n
            | _ -> None)
    Assert.Contains("dep::helper", declared)
    Assert.Contains("helper", declared)

[<Fact>]
let ``references inside a module follow its qualified names, including calls and classes`` () =
    let src = "fun f() {\n    return 1\n}\nclass C {}\nprint f()\nvar c = C()"
    let linked = link [ "main", "print 1", [ "dep" ]; "dep", src, [] ]
    let rec globals (e: BoundExpr) =
        match e with
        | BVariable(GlobalBinding n, _) -> [ n ]
        | BCall(c, args) -> globals c @ List.collect globals args
        | _ -> []
    let referenced =
        linked.Bound
        |> List.collect (function
            | BExpressionStmt e -> globals e
            | BVarStmt(_, _, Some e) -> globals e
            | _ -> [])
    Assert.Contains("dep::f", referenced)
    Assert.Contains("dep::C", referenced)
    Assert.DoesNotContain("f", referenced)
    let classes = linked.Bound |> List.choose (function BClassStmt(DeclaredGlobal n, _, _, _, _, _) -> Some n | _ -> None)
    Assert.Equal<string list>([ "dep::C" ], classes)

[<Fact>]
let ``prelude names stay unqualified and are callable from any module`` () =
    let linked = link [ "main", "print 1", [ "dep" ]; "dep", "print map (x) -> x, [1]", [] ]
    Assert.All(linked.Diagnostics, fun d -> Assert.Empty d.Errors)
    let found =
        linked.Bound
        |> List.exists (function
            | BExpressionStmt(BCall(_, [ BCall(BVariable(GlobalBinding "map", _), _) ])) -> true
            | _ -> false)
    Assert.True found

[<Fact>]
let ``redeclaring a prelude name inside a module is still an already-declared error`` () =
    let linked = link [ "main", "print 1", [ "dep" ]; "dep", "var map = 1", [] ]
    let dep = linked.Diagnostics |> List.find (fun d -> d.Module = "dep")
    Assert.Single dep.Errors |> ignore
    Assert.Contains("already declared", dep.Errors.Head.Message)

[<Fact>]
let ``diagnostics are attributed to the module that produced them`` () =
    let linked = link [ "main", "print 1", [ "dep" ]; "dep", "var unused = 1", [] ]
    let dep = linked.Diagnostics |> List.find (fun d -> d.Module = "dep")
    let entry = linked.Diagnostics |> List.find (fun d -> d.IsEntry)
    Assert.Equal("main", entry.Module)
    Assert.Empty entry.Warnings
    Assert.Single dep.Warnings |> ignore

[<Fact>]
let ``a linked multi-module program compiles to a single chunk`` () =
    let linked = link [ "main", "print 1", [ "dep" ]; "dep", "var helper = 2\nprint helper", [] ]
    let _, codegenErrors = compile linked.Bound
    Assert.Empty codegenErrors
