/// The `iqaloxc` CLI: `.iqx` source -> bytecode file, running every Phase
/// 2-5 stage in sequence (scan, parse, resolve, generate code) and
/// stopping at the first stage that reports errors. Exit codes follow
/// `poc/src/iqalox.py`'s existing sysexits.h convention (64 usage, 65
/// source/compile error).
///
/// `docs/PLAN-0.2.md` Phase 5: `Prelude.source`'s `map`/`filter`/`reduce`/
/// `sort` are scanned and parsed here too, once per invocation, and their
/// statements are prepended to the user's own before resolving/compiling
/// -- one combined program, one `Chunk`, so `Resolver.fs`'s ordinary
/// global pre-registration sees both without any special-casing. Scanned
/// and parsed *separately* from the user's source purely so a failure in
/// each can be attributed to the right one -- an error inside
/// `Prelude.source` itself would mean a real bug in this compiler, not
/// anything the user wrote, so it's reported distinctly rather than
/// folded into the user's own error list.
module Iqalox.Program

open System
open Iqalox.Scanner
open Iqalox.Parser
open Iqalox.Resolver
open Iqalox.Modules
open Iqalox.Codegen
open Iqalox.Bytecode

/// Scans and parses one module file. Phase 4 (`docs/PLAN-0.3.md`) has no
/// `import` statement yet, so every file reports no dependencies; Phase 5
/// will read them out of the AST here. Errors come back as ready-to-print
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
                Ok(stmts, [])

[<EntryPoint>]
let main argv =
    match argv with
    | [| inputPath; outputPath |] ->
        let preludeTokens, preludeScanErrors = scanTokens Prelude.source

        if not preludeScanErrors.IsEmpty then
            for e in preludeScanErrors do
                eprintfn "internal error: prelude failed to scan [line %d]: %s" e.Line e.Message
            70
        else
            let preludeStmts, preludeParseErrors = parse preludeTokens

            if not preludeParseErrors.IsEmpty then
                for e in preludeParseErrors do
                    eprintfn "internal error: prelude failed to parse [line %d]: %s" e.Token.Line e.Message
                70
            else
                // The prelude is resolved as its own unit, once, and its
                // declared names seed every user module as ordinary
                // immutable globals (`docs/PLAN-0.3.md` Phase 4).
                let prelude = resolveUnit [] preludeStmts

                if not prelude.Errors.IsEmpty then
                    for e in prelude.Errors do
                        eprintfn "internal error: prelude failed to resolve [line %d]: %s" e.Token.Line e.Message
                    70
                else
                    let entryFull = IO.Path.GetFullPath inputPath
                    let rootDir = IO.Path.GetDirectoryName entryFull

                    let parseFile (path: string) =
                        parseModule entryFull (moduleNameOfPath rootDir (IO.Path.GetFullPath path)) path

                    match loadModules inputPath parseFile with
                    | Error lines ->
                        for line in lines do
                            eprintfn "%s" line
                        65
                    | Ok modules ->
                        let linked = linkModules prelude modules

                        let label (d: UnitDiagnostics) (line: int) =
                            if d.IsEntry then $"[line {line}]" else $"[{d.Module}, line {line}]"

                        // Warnings first and unconditionally -- they never
                        // affect the exit status (`docs/PLAN-0.3.md`
                        // decision 4).
                        for d in linked.Diagnostics do
                            for w in d.Warnings do
                                eprintfn "%s Warning: %s" (label d w.Token.Line) w.Message

                        let hasErrors = linked.Diagnostics |> List.exists (fun d -> not d.Errors.IsEmpty)

                        if hasErrors then
                            for d in linked.Diagnostics do
                                for e in d.Errors do
                                    eprintfn "%s Error: %s" (label d e.Token.Line) e.Message
                            65
                        else
                            let chunk, codegenErrors = compile linked.Bound

                            if not codegenErrors.IsEmpty then
                                for e in codegenErrors do
                                    eprintfn "[line %d] Error: %s" e.Line e.Message
                                65
                            else
                                write chunk outputPath
                                0
    | _ ->
        eprintfn "Usage: iqaloxc <input .iqx file> <output bytecode file>"
        64
