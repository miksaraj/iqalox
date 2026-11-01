/// Multi-file compilation infrastructure (`docs/PLAN-0.3.md` Phase 4,
/// decisions 4, 7 and 8): module discovery from an entry file, path-to-
/// module-name mapping, cycle detection, and linking several separately
/// resolved files into one `BoundStmt` list for a single `Codegen` run --
/// so `iqaloxc` still emits exactly one `.iqbc` file and `vm/` never
/// learns a program came from more than one source file.
///
/// A module's dependencies come from its top-level `import` statements
/// (Phase 5; `loadModules`' `parse` callback reports them). The built-in
/// `std.*` modules (`Stdlib.fs`, Phase 6) are found by name instead of by
/// path, and a user file that would shadow one is an error.
///
/// Name isolation: each file is resolved *on its own* (`Resolver.
/// resolveUnit`), so two modules may each declare a top-level `helper`
/// without an "already declared" clash. Linking then qualifies every
/// global a module declared (`math.vector::helper`) by rewriting the
/// already-resolved `Bound` tree -- safe precisely because locals are
/// slots by then, so there is no shadowing to get wrong. Since Phase 6
/// that includes the *entry* module too: the runtime's global table is
/// flat and the natives live in it under their bare names, so a user
/// program's own `sort` or `push` must never collide with one.
module Iqalox.Modules

open System.Collections.Generic
open System.IO
open Iqalox.Ast
open Iqalox.Bound
open Iqalox.Resolver

/// `File` is the module's path relative to the entry file's directory
/// ("math/vector.iqx"), with `/` separators on every platform -- what
/// runtime errors report (`Bytecode.fs`' file table) -- `<std.vector>` for
/// a built-in module. `IsBuiltin` modules additionally export `Natives`
/// (VM-registered, bare runtime names) alongside their source's `pub`s.
type ModuleSource =
    { Name: string
      Path: string
      File: string
      Stmts: Stmt list
      IsBuiltin: bool
      Natives: string list }

/// `math/vector.iqx` (relative to `rootDir`) -> `math.vector`.
let moduleNameOfPath (rootDir: string) (filePath: string) : string =
    let relative = Path.GetRelativePath(rootDir, filePath)
    Path.ChangeExtension(relative, null)
        .Replace(Path.DirectorySeparatorChar, '.')
        .Replace(Path.AltDirectorySeparatorChar, '.')

/// Inverse of `moduleNameOfPath`: `math.vector` -> `<rootDir>/math/vector.iqx`.
let modulePathOfName (rootDir: string) (moduleName: string) : string =
    Path.Combine(rootDir, moduleName.Replace('.', Path.DirectorySeparatorChar) + ".iqx")

/// Loads `entryPath` and, transitively, every module it depends on
/// (resolved relative to the entry file's own directory -- decision 8).
/// `parse` turns a file path into its statements plus the dotted module
/// names it depends on, or into ready-to-print error lines. Result is in
/// dependency order (a module always appears after everything it depends
/// on; the entry module is last). Each file is parsed once even if
/// several modules depend on it; a circular dependency is an error, not
/// an infinite loop.
let loadModulesWith
    (builtin: string -> (Result<Stmt list, string list> * string list) option)
    (entryPath: string)
    (parse: string -> Result<Stmt list * string list, string list>)
    : Result<ModuleSource list, string list> =
    let entryFull = Path.GetFullPath entryPath
    let rootDir = Path.GetDirectoryName entryFull
    let finished = Dictionary<string, ModuleSource>()
    let order = ResizeArray<ModuleSource>()
    let errors = ResizeArray<string>()

    // `stack` is the chain of modules currently being visited, innermost
    // first -- seeing `name` in it again means a cycle.
    let rec visit (stack: string list) (name: string) (path: string) =
        if finished.ContainsKey name then
            ()
        elif List.contains name stack then
            let inner = stack |> List.takeWhile (fun s -> s <> name)
            let chain = (List.rev (inner @ [ name ])) @ [ name ]
            let joined = String.concat " -> " chain
            errors.Add $"Circular module dependency: {joined}."
        else
          match builtin name with
          | Some(parsed, natives) ->
            // A built-in module is found by name; a user file at the same
            // path would silently shadow it, which the language forbids.
            if File.Exists path then
                errors.Add $"Module '{name}' is built in; the file '{path}' would shadow it."
            else
                match parsed with
                | Error lines -> errors.AddRange lines
                | Ok stmts ->
                    let source =
                        { Name = name
                          Path = ""
                          File = $"<{name}>"
                          Stmts = stmts
                          IsBuiltin = true
                          Natives = natives }
                    finished.[name] <- source
                    order.Add source
          | None ->
            match parse path with
            | Error lines -> errors.AddRange lines
            | Ok(stmts, imports) ->
                for imported in imports do
                    visit (name :: stack) imported (modulePathOfName rootDir imported)
                let file = Path.GetRelativePath(rootDir, Path.GetFullPath path).Replace('\\', '/')
                let source =
                    { Name = name
                      Path = path
                      File = file
                      Stmts = stmts
                      IsBuiltin = false
                      Natives = [] }
                finished.[name] <- source
                order.Add source

    visit [] (moduleNameOfPath rootDir entryFull) entryFull
    if errors.Count > 0 then Error(List.ofSeq errors) else Ok(List.ofSeq order)

/// `loadModulesWith` with no built-in modules.
let loadModules
    (entryPath: string)
    (parse: string -> Result<Stmt list * string list, string list>)
    : Result<ModuleSource list, string list> =
    loadModulesWith (fun _ -> None) entryPath parse

// --- qualifying a resolved module's own globals ---

let private qualifiedName (moduleName: string) (name: string) = moduleName + "::" + name

let private qualifyBinding (moduleName: string) (own: Set<string>) (binding: VariableBinding) =
    match binding with
    | GlobalBinding name when own.Contains name -> GlobalBinding(qualifiedName moduleName name)
    | other -> other

let private qualifyDeclared (moduleName: string) (own: Set<string>) (binding: DeclaredBinding) =
    match binding with
    | DeclaredGlobal name when own.Contains name -> DeclaredGlobal(qualifiedName moduleName name)
    | other -> other

let rec private qualifyExpr (m: string) (own: Set<string>) (expr: BoundExpr) : BoundExpr =
    let q = qualifyExpr m own
    let qo = Option.map q
    match expr with
    | BAssign(binding, name, value) -> BAssign(qualifyBinding m own binding, name, q value)
    | BBinary(l, op, r) -> BBinary(q l, op, q r)
    | BLogical(l, op, r) -> BLogical(q l, op, q r)
    | BGrouping e -> BGrouping(q e)
    | BLiteral _ | BBreak _ | BContinue _ | BIgnore -> expr
    | BUnary(op, r) -> BUnary(op, q r)
    | BTernary(l, lo, mid, ro, r) -> BTernary(q l, lo, q mid, ro, q r)
    | BVector values -> BVector(List.map q values)
    | BSpread(e, ellipsis) -> BSpread(q e, ellipsis)
    | BVariable(binding, name) -> BVariable(qualifyBinding m own binding, name)
    | BCall(callee, args) -> BCall(q callee, List.map q args)
    | BGet(obj, name) -> BGet(q obj, name)
    | BSet(obj, name, value) -> BSet(q obj, name, q value)
    | BIndex(obj, index, bracket) -> BIndex(q obj, q index, bracket)
    | BIndexSet(obj, index, value, bracket) -> BIndexSet(q obj, q index, q value, bracket)
    | BSlice(obj, start, stop, bracket) -> BSlice(q obj, qo start, qo stop, bracket)
    | BLambda decl -> BLambda(qualifyFunction m own decl)
    | BVectorLengthInternal v -> BVectorLengthInternal(q v)
    | BVectorAppendInternal(v, value) -> BVectorAppendInternal(q v, q value)
    | BSelf(binding, keyword) -> BSelf(qualifyBinding m own binding, keyword)
    | BSuper(selfBinding, binding, keyword, method) ->
        BSuper(qualifyBinding m own selfBinding, qualifyBinding m own binding, keyword, method)

and private qualifyFunction (m: string) (own: Set<string>) (decl: BoundFunctionDecl) : BoundFunctionDecl =
    { decl with Body = List.map (qualifyStmt m own) decl.Body }

and private qualifyStmt (m: string) (own: Set<string>) (stmt: BoundStmt) : BoundStmt =
    let qe = qualifyExpr m own
    let qs = qualifyStmt m own
    match stmt with
    | BBlock statements -> BBlock(List.map qs statements)
    | BExpressionStmt e -> BExpressionStmt(qe e)
    | BVarStmt(binding, name, init) -> BVarStmt(qualifyDeclared m own binding, name, Option.map qe init)
    | BForStmt(init, cond, incr, body) -> BForStmt(Option.map qs init, Option.map qe cond, Option.map qe incr, qs body)
    | BFunctionStmt(binding, decl) -> BFunctionStmt(qualifyDeclared m own binding, qualifyFunction m own decl)
    | BReturnStmt(keyword, value) -> BReturnStmt(keyword, Option.map qe value)
    | BClassStmt(binding, name, superclass, mixins, properties, methods) ->
        BClassStmt(
            qualifyDeclared m own binding,
            name,
            superclass |> Option.map (fun (b, t) -> qualifyBinding m own b, t),
            mixins |> List.map (fun (b, t) -> qualifyBinding m own b, t),
            properties,
            List.map (qualifyFunction m own) methods
        )

/// Qualifies every global in `statements` that is in `declaredGlobals`
/// (the names the module itself declared) with `moduleName::`.
let qualifyModule (moduleName: string) (declaredGlobals: Set<string>) (statements: BoundStmt list) : BoundStmt list =
    statements |> List.map (qualifyStmt moduleName declaredGlobals)

// --- linking ---

/// One unit's diagnostics, kept separate per module so the caller can
/// attribute each message to the right file.
type UnitDiagnostics =
    { Module: string
      IsEntry: bool
      IsBuiltin: bool
      Errors: ResolveError list
      Warnings: ResolveError list }

/// `Bound` is every statement in execution order; `Segments` is the same
/// statements grouped by the source file each came from (a built-in module
/// is `"<std.vector>"` etc.), for `Codegen.compileSegments`.
type LinkedProgram =
    { Bound: BoundStmt list
      Segments: (string * BoundStmt list) list
      Diagnostics: UnitDiagnostics list }

/// The dotted module names `stmts`' top-level `import` statements depend
/// on, in first-mention order without repeats -- what `Program.fs`'s parse
/// callback hands `loadModules` as a file's dependencies.
let importedModuleNames (stmts: Stmt list) : string list =
    stmts
    |> List.collect (function
        | ImportStmt(_, items) ->
            items |> List.map (fun item -> item.Module |> List.map (fun t -> t.Lexeme) |> String.concat ".")
        | _ -> [])
    |> List.distinct

/// Turns one module's `import` statements into validated bindings (or
/// errors), looking each `a.b.name` up in the already-resolved module `a.b`.
/// Dependency order guarantees every imported module is in `resolved`.
let private bindImports
    (resolved: Dictionary<string, ResolvedUnit>)
    (natives: Dictionary<string, string list>)
    (stmts: Stmt list)
    : ImportBinding list * ResolveError list =
    let bindings = ResizeArray<ImportBinding>()
    let errors = ResizeArray<ResolveError>()
    for stmt in stmts do
        match stmt with
        | ImportStmt(_, items) ->
            for item in items do
                let moduleName = item.Module |> List.map (fun t -> t.Lexeme) |> String.concat "."
                let memberName = item.Name.Lexeme
                match resolved.TryGetValue moduleName with
                | false, _ ->
                    errors.Add { Message = $"Unknown module '{moduleName}'."; Token = item.Module.Head }
                | true, exporter when
                    (match natives.TryGetValue moduleName with
                     | true, names -> List.contains memberName names
                     | false, _ -> false)
                    ->
                    // A built-in module's native: binds straight to the
                    // VM's bare runtime name, not a module-qualified one.
                    bindings.Add
                        { Local = defaultArg item.Alias item.Name
                          Qualified = memberName
                          Kind = ExportFun }
                | true, exporter ->
                    match exporter.Exports |> List.tryFind (fun e -> e.Name = memberName) with
                    | Some symbol ->
                        bindings.Add
                            { Local = defaultArg item.Alias item.Name
                              Qualified = qualifiedName moduleName memberName
                              Kind = symbol.Kind }
                    | None when exporter.DeclaredGlobals.Contains memberName ->
                        errors.Add
                            { Message = $"'{memberName}' in module '{moduleName}' is not 'pub', so it can't be imported."
                              Token = item.Name }
                    | None ->
                        errors.Add { Message = $"Module '{moduleName}' has no top-level '{memberName}'."; Token = item.Name }
        | _ -> ()
    List.ofSeq bindings, List.ofSeq errors

/// Resolves every module (each in isolation, seeded with whatever its own
/// `import`s bring in from already-resolved dependencies -- plus, for a
/// built-in module, the natives, visible to its own source as bare root
/// globals), qualifies every module's own globals (the entry module's
/// too), and concatenates the results in dependency order, entry last.
/// `modules` must be in `loadModules`' order.
let linkModules (modules: ModuleSource list) : LinkedProgram =
    let lastIndex = List.length modules - 1
    let resolvedByName = Dictionary<string, ResolvedUnit>()
    let nativesByModule = Dictionary<string, string list>()
    for m in modules do
        if m.IsBuiltin then
            nativesByModule.[m.Name] <- m.Natives
    let resolved =
        modules
        |> List.mapi (fun i m ->
            let imports, importErrors = bindImports resolvedByName nativesByModule m.Stmts
            // A built-in module sees the natives as bare root globals and
            // needs no hints; every user module gets the hint table.
            let rootGlobals, hints =
                if m.IsBuiltin then Iqalox.Stdlib.allNatives, Map.empty else [], Iqalox.Stdlib.importHints
            let unit = resolveUnitWithHints rootGlobals hints imports m.Stmts
            resolvedByName.[m.Name] <- unit
            let bound = qualifyModule m.Name unit.DeclaredGlobals unit.Bound
            (m.File, bound),
            { Module = m.Name
              IsEntry = (i = lastIndex)
              IsBuiltin = m.IsBuiltin
              Errors = importErrors @ unit.Errors
              Warnings = unit.Warnings })
    let segments = resolved |> List.map fst
    { Bound = segments |> List.collect snd
      Segments = segments
      Diagnostics = resolved |> List.map snd }
