/// The `iqaloxc` CLI: `.iqx` source -> bytecode file, running every Phase
/// 2-5 stage in sequence (scan, parse, resolve, generate code) and
/// stopping at the first stage that reports errors. Exit codes follow
/// `poc/src/iqalox.py`'s existing sysexits.h convention (64 usage, 65
/// source/compile error).
///
/// `docs/PLAN-0.3.md` Phase 6: there is no textually-merged prelude any
/// more. The standard library (`Stdlib.fs`) is a set of built-in `std.*`
/// modules, scanned and parsed here on demand (only when something imports
/// them) and handed to the module loader by name. A failure while scanning,
/// parsing or resolving one would mean a bug in this compiler, not in
/// anything the user wrote, so it is reported distinctly as an internal
/// error.
module Iqalox.Program

open System
open Iqalox.Scanner
open Iqalox.Parser
open Iqalox.Resolver
open Iqalox.Modules
open Iqalox.Codegen
open Iqalox.Bytecode

/// Scans and parses one module file, reporting the modules its top-level
/// `import`s depend on (`docs/PLAN-0.3.md` Phase 5). Errors come back as ready-to-print
/// lines: the entry file keeps the original `[line N]` format, every
/// other module is attributed to its own module name.
let private parseModule (entryFull: string) (moduleName: string) (path: string) : Result<Ast.Stmt list * string list, string list> =
    if not (IO.File.Exists path) then
        Error [ $"Error: cannot read module file '{path}'." ]
    else
        let label line =
            if IO.Path.GetFullPath path = entryFull then $"[line {line}]" else $"[{moduleName}, line {line}]"
        let tokens, scanErrors = scanTokens (IO.File.ReadAllText path)

        if not scanErrors.IsEmpty then
            Error [ for e in scanErrors -> $"{label e.Line} Error: {e.Message}" ]
        else
            let stmts, parseErrors = parse tokens

            if not parseErrors.IsEmpty then
                Error [ for e in parseErrors -> $"{label e.Token.Line} Error: {e.Message}" ]
            else
                Ok(stmts, importedModuleNames stmts)

/// Scans and parses a built-in module's embedded source.
let private parseBuiltin (name: string) (source: string) : Result<Ast.Stmt list, string list> =
    let tokens, scanErrors = scanTokens source

    if not scanErrors.IsEmpty then
        Error [ for e in scanErrors -> $"internal error: {name} failed to scan [line {e.Line}]: {e.Message}" ]
    else
        let stmts, parseErrors = parse tokens

        if not parseErrors.IsEmpty then
            Error [ for e in parseErrors -> $"internal error: {name} failed to parse [line {e.Token.Line}]: {e.Message}" ]
        else
            Ok stmts

[<EntryPoint>]
let main argv =
    match argv with
    | [| inputPath; outputPath |] ->
        let entryFull = IO.Path.GetFullPath inputPath
        let rootDir = IO.Path.GetDirectoryName entryFull

        let parseFile (path: string) =
            parseModule entryFull (moduleNameOfPath rootDir (IO.Path.GetFullPath path)) path

        let builtin (name: string) =
            Stdlib.tryFind name |> Option.map (fun m -> parseBuiltin m.Name m.Source, m.Natives)

        match loadModulesWith builtin inputPath parseFile with
        | Error lines ->
            for line in lines do
                eprintfn "%s" line
            65
        | Ok modules ->
            let linked = linkModules modules

            let label (d: UnitDiagnostics) (line: int) =
                if d.IsEntry then $"[line {line}]" else $"[{d.Module}, line {line}]"

            // A built-in module's own diagnostics are never the user's
            // business: warnings are dropped, and an error means a bug in
            // this compiler.
            let builtinErrors = linked.Diagnostics |> List.filter (fun d -> d.IsBuiltin && not d.Errors.IsEmpty)

            if not builtinErrors.IsEmpty then
                for d in builtinErrors do
                    for e in d.Errors do
                        eprintfn "internal error: %s failed to resolve [line %d]: %s" d.Module e.Token.Line e.Message
                70
            else
                // Warnings first and unconditionally -- they never affect
                // the exit status (`docs/PLAN-0.3.md` decision 4).
                for d in linked.Diagnostics |> List.filter (fun d -> not d.IsBuiltin) do
                    for w in d.Warnings do
                        eprintfn "%s Warning: %s" (label d w.Token.Line) w.Message

                let hasErrors = linked.Diagnostics |> List.exists (fun d -> not d.Errors.IsEmpty)

                if hasErrors then
                    for d in linked.Diagnostics do
                        for e in d.Errors do
                            eprintfn "%s Error: %s" (label d e.Token.Line) e.Message
                    65
                else
                    let chunk, fileNames, codegenErrors = compileSegments linked.Segments

                    if not codegenErrors.IsEmpty then
                        for e in codegenErrors do
                            eprintfn "[line %d] Error: %s" e.Line e.Message
                        65
                    else
                        writeWithFiles fileNames chunk outputPath
                        0
    | _ ->
        eprintfn "Usage: iqaloxc <input .iqx file> <output bytecode file>"
        64
