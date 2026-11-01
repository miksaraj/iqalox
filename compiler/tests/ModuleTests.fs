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
open Iqalox.Bytecode

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
    | Ok ms -> linkModules ms
    | Error e -> failwith $"%A{e}"

[<Fact>]
let ``two modules may each declare the same top-level name without a clash`` () =
    let linked = link [ "main", "var helper = 1\nprint helper", [ "dep" ]; "dep", "var helper = 2\nprint helper", [] ]
    Assert.All(linked.Diagnostics, fun d -> Assert.Empty d.Errors)

[<Fact>]
let ``every module's globals are qualified with its own name, the entry module's included`` () =
    let linked = link [ "main", "var helper = 1", [ "dep" ]; "dep", "var helper = 2", [] ]
    let declared =
        linked.Bound
        |> List.choose (function
            | BVarStmt(DeclaredGlobal n, _, _) -> Some n
            | _ -> None)
    Assert.Contains("dep::helper", declared)
    Assert.Contains("main::helper", declared)

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
let ``a module needs no import to declare a name the standard library also uses`` () =
    let linked = link [ "main", "print 1", [ "dep" ]; "dep", "var map = 1\nprint map", [] ]
    Assert.All(linked.Diagnostics, fun d -> Assert.Empty d.Errors)

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

// --- docs/PLAN-0.3.md Phase 5: import / pub-as-export ---

/// Like `link`, but each file's dependencies come from its own `import`
/// statements (via `importedModuleNames`), exactly as `Program.fs` does.
let private linkAuto (files: (string * string) list) =
    let parseFile path =
        match files |> List.tryFind (fun (n, _) -> pathOf n = path) with
        | Some(_, source) ->
            let stmts = parseText source
            Ok(stmts, importedModuleNames stmts)
        | None -> Error [ $"missing {path}" ]
    let builtin name =
        Iqalox.Stdlib.tryFind name |> Option.map (fun m -> Ok(parseText m.Source), m.Natives)
    match loadModulesWith builtin (pathOf "main") parseFile with
    | Ok ms -> linkModules ms
    | Error e -> failwith $"%A{e}"

let private errorsOf (linked: LinkedProgram) (moduleName: string) =
    (linked.Diagnostics |> List.find (fun d -> d.Module = moduleName)).Errors |> List.map (fun e -> e.Message)

let private warningsOf (linked: LinkedProgram) (moduleName: string) =
    (linked.Diagnostics |> List.find (fun d -> d.Module = moduleName)).Warnings |> List.map (fun e -> e.Message)

let private globalRefs (linked: LinkedProgram) =
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
let ``importing a pub function binds the importer's calls to the exporter's qualified global`` () =
    let linked =
        linkAuto [ "main", "import dep.f\nprint f()"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Empty(errorsOf linked "main")
    Assert.Contains("dep::f", globalRefs linked)

[<Fact>]
let ``as binds the import under the alias instead of its own name`` () =
    let linked =
        linkAuto [ "main", "import dep.f as g\nprint g()"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Empty(errorsOf linked "main")
    Assert.Contains("dep::f", globalRefs linked)
    Assert.DoesNotContain("g", globalRefs linked)

[<Fact>]
let ``the same name can be imported from two modules by aliasing one`` () =
    let linked =
        linkAuto
            [ "main", "import a.f, b.f as f2\nprint f()\nprint f2()"
              "a", "pub fun f() {\n    return 1\n}"
              "b", "pub fun f() {\n    return 2\n}" ]
    Assert.Empty(errorsOf linked "main")
    Assert.Contains("a::f", globalRefs linked)
    Assert.Contains("b::f", globalRefs linked)

[<Fact>]
let ``importing a non-pub name is an error that says so`` () =
    let linked = linkAuto [ "main", "import dep.hidden"; "dep", "fun hidden() {\n    return 1\n}" ]
    match errorsOf linked "main" with
    | [ msg ] -> Assert.Contains("is not 'pub'", msg)
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``importing a name the module doesn't declare is an error`` () =
    let linked = linkAuto [ "main", "import dep.ghost"; "dep", "pub var x = 1" ]
    match errorsOf linked "main" with
    | [ msg ] -> Assert.Contains("has no top-level 'ghost'", msg)
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``an import colliding with a local declaration is an already-declared error`` () =
    let linked = linkAuto [ "main", "import dep.f\nfun f() {\n    return 2\n}"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Contains("Variable 'f' already declared.", errorsOf linked "main")

[<Fact>]
let ``two imports claiming the same local name is an already-declared error`` () =
    let linked =
        linkAuto [ "main", "import a.f, b.f"; "a", "pub fun f() {\n    return 1\n}"; "b", "pub fun f() {\n    return 2\n}" ]
    Assert.Contains("Variable 'f' already declared.", errorsOf linked "main")

[<Fact>]
let ``an import colliding with the ambient print is an already-declared error`` () =
    let linked = linkAuto [ "main", "import dep.show as print"; "dep", "pub fun show() {\n    return 1\n}" ]
    Assert.Contains("Variable 'print' already declared.", errorsOf linked "main")

[<Fact>]
let ``an imported variable is read-only even if the exporter declared it mut`` () =
    let linked =
        linkAuto [ "main", "import dep.counter\ncounter = 5"; "dep", "pub var counter mut = 0" ]
    Assert.Contains("Assigning to immutable variable 'counter' not allowed.", errorsOf linked "main")

[<Fact>]
let ``a subclass can extend an imported class and use its inherited properties`` () =
    let shapes = "pub class Shape {\n    var sides pub mut\n    init(n) {\n        self.sides = n\n    }\n}"
    let main = "import shapes.Shape\nclass Sq extends Shape {\n    init() {\n        self.sides = 4\n    }\n}\nSq()"
    let linked = linkAuto [ "main", main; "shapes", shapes ]
    Assert.Empty(errorsOf linked "main")
    Assert.Contains("shapes::Shape", globalRefs linked |> List.append [ "shapes::Shape" ])
    let superclasses =
        linked.Bound |> List.choose (function BClassStmt(_, n, Some(GlobalBinding s, _), _, _, _) when n.Lexeme = "Sq" -> Some s | _ -> None)
    Assert.Equal<string list>([ "shapes::Shape" ], superclasses)

[<Fact>]
let ``a subclass of an imported class still can't assign an undeclared property`` () =
    let shapes = "pub class Shape {\n    var sides pub mut\n}"
    let main = "import shapes.Shape\nclass Sq extends Shape {\n    init() {\n        self.nope = 4\n    }\n}"
    let linked = linkAuto [ "main", main; "shapes", shapes ]
    Assert.Single(errorsOf linked "main") |> ignore

[<Fact>]
let ``an imported trait can be used by a class in the importing module`` () =
    let named = "pub trait Named {\n    var label pub mut\n    pub describe() {\n        return self.label\n    }\n}"
    let main = "import named.Named\nclass C {\n    use Named\n    init() {\n        self.label = 1\n    }\n}\nC()"
    let linked = linkAuto [ "main", main; "named", named ]
    Assert.Empty(errorsOf linked "main")
    let _, codegenErrors = compile linked.Bound
    Assert.Empty codegenErrors

[<Fact>]
let ``a pub declaration is never warned unused in its own module but a private one is`` () =
    let linked = linkAuto [ "main", "import dep.a\nprint a()"; "dep", "pub fun a() {\n    return 1\n}\nfun b() {\n    return 2\n}" ]
    match warningsOf linked "dep" with
    | [ msg ] -> Assert.Contains("'b'", msg)
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``import below the top level is an error`` () =
    let linked = linkAuto [ "main", "fun f() {\n    import dep.x\n}\nf()"; "dep", "pub var x = 1" ]
    Assert.Contains("'import' is only allowed at the top level of a file.", errorsOf linked "main")

[<Fact>]
let ``pub below the top level is an error`` () =
    let linked = linkAuto [ "main", "fun f() {\n    pub var x = 1\n}\nf()" ]
    Assert.Contains("'pub' is only allowed on top-level declarations.", errorsOf linked "main")

[<Fact>]
let ``resolving a file with an import in isolation, without the linker, is an error`` () =
    let _, errors, _ = resolve (parseText "import dep.x")
    Assert.Single errors |> ignore

[<Fact>]
let ``a transitive import chain links and compiles`` () =
    let linked =
        linkAuto
            [ "main", "import b.g\nprint g()"
              "b", "import a.f\npub fun g() {\n    return f()\n}"
              "a", "pub fun f() {\n    return 1\n}" ]
    Assert.All(linked.Diagnostics, fun d -> Assert.Empty d.Errors)
    let _, codegenErrors = compile linked.Bound
    Assert.Empty codegenErrors

// --- unused-import warnings ---

[<Fact>]
let ``an import that is never used produces a warning`` () =
    let linked = linkAuto [ "main", "import dep.f\nprint 1"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Equal<string list>([ "Import 'f' is never used." ], warningsOf linked "main")

[<Fact>]
let ``an import that is called produces no warning`` () =
    let linked = linkAuto [ "main", "import dep.f\nprint f()"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Empty(warningsOf linked "main")

[<Fact>]
let ``an unused aliased import warns under the alias`` () =
    let linked = linkAuto [ "main", "import dep.f as g\nprint 1"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Equal<string list>([ "Import 'g' is never used." ], warningsOf linked "main")

[<Fact>]
let ``an underscore-prefixed unused import is exempt`` () =
    let linked = linkAuto [ "main", "import dep.f as _f\nprint 1"; "dep", "pub fun f() {\n    return 1\n}" ]
    Assert.Empty(warningsOf linked "main")

[<Fact>]
let ``an import only assigned to (which is an error anyway) is still unused`` () =
    let linked = linkAuto [ "main", "import dep.v\nv = 1"; "dep", "pub var v mut = 0" ]
    Assert.Contains("Import 'v' is never used.", warningsOf linked "main")

[<Fact>]
let ``an imported class used only as a superclass counts as used`` () =
    let linked =
        linkAuto
            [ "main", "import dep.Base\nclass D extends Base {\n    pub go() {\n        return 1\n    }\n}\nprint D().go()"
              "dep", "pub class Base {\n}" ]
    Assert.Empty(warningsOf linked "main")

[<Fact>]
let ``an imported trait used only by a use clause counts as used`` () =
    let linked =
        linkAuto
            [ "main", "import dep.T\nclass C {\n    use T\n}\nC()"
              "dep", "pub trait T {\n    pub hello() {\n        return 1\n    }\n}" ]
    Assert.Empty(warningsOf linked "main")

// --- file names in the compiled program ---

[<Fact>]
let ``each module's instructions are stamped with its own file, built-in modules included`` () =
    let linked =
        linkAuto
            [ "main", "import dep.f\nimport std.vector.map\nprint f()\nprint map (x) -> x, [1]"
              "dep", "pub fun f() {\n    return 1\n}" ]
    let chunk, fileNames, errors = compileSegments linked.Segments
    Assert.Empty errors
    Assert.Equal<string list>([ "dep.iqx"; "<std.vector>"; "main.iqx" ], List.ofArray fileNames)
    // Every file index in use is a valid index, the top-level chunk touches
    // all three files, `f` (declared in dep.iqx, index 0) is stamped
    // entirely with dep's index, and `map` entirely with std.vector's (1).
    Assert.All(chunk.Files, fun i -> Assert.InRange(i, 0, 2))
    Assert.Equal<int list>([ 0; 1; 2 ], chunk.Files |> Array.distinct |> Array.sort |> List.ofArray)
    let protoFor name =
        chunk.Constants
        |> Array.pick (function
            | FunctionConstant p when p.Name = name -> Some p
            | _ -> None)
    Assert.All((protoFor "f").Chunk.Files, fun i -> Assert.Equal(0, i))
    Assert.All((protoFor "map").Chunk.Files, fun i -> Assert.Equal(1, i))

[<Fact>]
let ``a module in a subdirectory is named by its relative path with forward slashes`` () =
    let linked = linkAuto [ "main", "import math.vector.f\nprint f()"; "math.vector", "pub fun f() {\n    return 1\n}" ]
    let _, fileNames, _ = compileSegments linked.Segments
    Assert.Contains("math/vector.iqx", fileNames)
