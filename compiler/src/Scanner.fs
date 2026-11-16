/// Scanner: source text -> `Token` list.
///
/// Ports `poc/src/scanner.py`'s design, including its two proven-correct
/// post-release fixes (accurate line/column tracking via a `lineStart`
/// offset; coalescing a run of unrecognized characters into a single
/// error instead of one per character), plus escape sequences in string
/// literals (new for `0.1`, see `docs/PLAN-0.1.md` decision 5).
///
/// This is a fresh implementation, not a line-for-line port, because
/// writing it carefully surfaced several `poc` bugs that were never
/// "proven correct" and so aren't carried forward:
///   - Decimal number literals never worked in `poc` at all (`number()`
///     compares `self.peek()` against a digit check that also defaults to
///     `self.peek()`, i.e. it always compares the same '.' character to
///     itself and never looks at what follows it) -- `3.14` scans as
///     `NUMBER("3")`, `DOT`, `NUMBER("14")`. Fixed here by checking the
///     character *after* the dot.
///   - A leading-underscore identifier (`_foo`) misscans as a bare `_`
///     (the ignore operator) followed by a separate `foo` identifier,
///     since `_` is checked as its own token before the identifier
///     dispatch runs. Fixed here by checking whether an alphanumeric
///     character follows before deciding `_` is standalone.
///   - The `...` (ellipsis) token under-consumes by one character (`poc`'s
///     generic compound-matching only ever advances one extra character
///     regardless of the compound's actual length) -- moot in `poc` since
///     `...` has no grammar yet, but worth not carrying forward. Fixed
///     here by a longest-match search that consumes exactly the matched
///     lexeme's length.
///   - A bare `#>` outside a block comment is silently swallowed with no
///     token or error (an accident of treating comment markers as just
///     another entry in the general operator-compound table). Fixed here
///     by giving comments their own explicit dispatch instead.
///   - An unterminated block comment (`<#` with no matching `#>` before
///     EOF) produces no error at all in `poc`. Fixed here to report one,
///     matching how an unterminated string is already handled.
module Iqalox.Scanner

open System.Text
open Iqalox.Token

type ScanError = { Message: string; Line: int; Column: int }

/// Multi- and single-character punctuation, longest lexeme first so a
/// longest-match search finds e.g. `...` before `.`, or `??` before `?`.
/// Comment markers (`#`, `<#`, `#>`) and string quotes are deliberately
/// not here -- `Scanner`'s main dispatch handles those explicitly instead
/// of through this generic table (see the module doc comment).
let private operatorCandidates: (string * TokenType) list =
    [ "...", Ellipsis
      "!=", BangEqual
      "==", EqualEqual
      ">=", GreaterEqual
      "<=", LessEqual
      "--", MinusMinus
      "->", Arrow
      "<-", LeftArrow
      "++", PlusPlus
      "?:", QuestionMarkColon
      "??", DoubleQuestionMark
      "|>", Pipe
      "(", LeftParen
      ")", RightParen
      "{", LeftBrace
      "}", RightBrace
      "[", LeftBracket
      "]", RightBracket
      ",", Comma
      ";", Semicolon
      "/", Slash
      "\\", Backslash
      "*", Star
      "%", Percent
      "^", Power
      "!", Bang
      "=", Equal
      ">", Greater
      "<", Less
      "-", Minus
      "+", Plus
      ".", Dot
      "?", QuestionMark
      ":", Colon
      "|", VerticalBar ]
    |> List.sortByDescending (fun (lexeme, _) -> lexeme.Length)

let private operatorStartChars: Set<char> =
    operatorCandidates |> List.map (fun (lexeme, _) -> lexeme.[0]) |> Set.ofList

let private isDigit (c: char) = c >= '0' && c <= '9'
let private isAlpha (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_'
let private isAlphaNumeric (c: char) = isAlpha c || isDigit c

/// Characters that can end an expression when directly adjacent (no
/// whitespace) to what follows: an identifier or number, a closing
/// bracket, or a string's closing quote.
let private endsExpression (c: char) =
    isAlphaNumeric c || c = ')' || c = ']' || c = '}' || c = '"' || c = '\''

/// The largest exact integer Iqalox computes, in bits: 2^26 bits, about 20
/// million decimal digits (`docs/PLAN-0.4.md` Phase 1). A literal beyond it
/// is a compile-time error; the VM enforces the same limit on results.
let maxIntegerBits = 1 <<< 26

/// Recognized escape sequences (decision 5, docs/PLAN-0.1.md): the common
/// C-family set. Anything else after a backslash is a hard error -- no
/// silent fallback to a literal backslash-plus-character.
let private escapeSequences: Map<char, char> =
    Map.ofList [ 'n', '\n'; 't', '\t'; 'r', '\r'; '\\', '\\'; '\'', '\''; '"', '"'; '0', '\000' ]

type private ScannerState(source: string) =
    let tokens = ResizeArray<Token>()
    let errors = ResizeArray<ScanError>()
    let mutable start = 0
    let mutable current = 0
    let mutable line = 1
    let mutable lineStart = 0
    // Line/column of the token currently being scanned, captured once per
    // token (mirrors `poc`'s `start_line`/`start_column`) so a token that
    // itself spans multiple lines (a multi-line string, a block comment)
    // is still reported at the position it *started*, not wherever
    // scanning ended up.
    let mutable startLine = 1
    let mutable startColumn = 1

    let isAtEnd () = current >= source.Length

    let peek () = if isAtEnd () then '\000' else source.[current]

    let peekNext () =
        if current + 1 >= source.Length then '\000' else source.[current + 1]

    let advance () =
        let c = source.[current]
        current <- current + 1
        c

    let columnAt (offset: int) = offset - lineStart + 1

    /// Call right after `advance()` has consumed a '\n', so `current`
    /// already points just past it.
    let advanceNewline () =
        line <- line + 1
        lineStart <- current

    let currentLexeme () = source.Substring(start, current - start)

    let addToken (tokenType: TokenType) (literal: Literal) =
        tokens.Add(
            { Type = tokenType
              Lexeme = currentLexeme ()
              Literal = literal
              Line = startLine
              Column = startColumn }
        )

    let addError (message: string) (errLine: int) (errColumn: int) =
        errors.Add { Message = message; Line = errLine; Column = errColumn }

    let isRecognized (c: char) =
        c = '\n'
        || c = ' '
        || c = '\t'
        || c = '\r'
        || c = '#'
        || c = '\''
        || c = '"'
        || isDigit c
        || isAlpha c
        || Set.contains c operatorStartChars

    let scanUnrecognizedRun () =
        while not (isAtEnd ()) && not (isRecognized (peek ())) do
            advance () |> ignore
        let lexeme = currentLexeme ()
        let suffix = if lexeme.Length > 1 then "s" else ""
        addError $"Unexpected character{suffix}: {lexeme}" startLine startColumn

    let tryMatchOperator () : TokenType option =
        let remaining = source.Length - start
        operatorCandidates
        |> List.tryFind (fun (lexeme, _) -> lexeme.Length <= remaining && source.Substring(start, lexeme.Length) = lexeme)
        |> Option.map (fun (lexeme, tokenType) ->
            current <- start + lexeme.Length
            tokenType)

    let scanLineComment () =
        while peek () <> '\n' && not (isAtEnd ()) do
            advance () |> ignore

    let scanBlockComment () =
        // `current` is already past "<#" by the time this is called.
        let mutable closed = false
        while not closed && not (isAtEnd ()) do
            if peek () = '#' && peekNext () = '>' then
                advance () |> ignore
                advance () |> ignore
                closed <- true
            elif peek () = '\n' then
                advance () |> ignore
                advanceNewline ()
            else
                advance () |> ignore
        if not closed then
            addError "Unterminated block comment." startLine startColumn

    let scanString (quote: char) =
        let value = StringBuilder()
        let mutable finished = false
        // Distinct from `finished`: on a bad escape we keep scanning to
        // find the real closing quote (so scanning resumes at the right
        // place afterward) but stop producing a String token, and only
        // report the first bad escape in a given string rather than one
        // per occurrence -- same "don't spam" philosophy as coalescing a
        // run of unrecognized characters elsewhere in this scanner.
        let mutable hadError = false
        while not finished do
            if isAtEnd () then
                addError "Unterminated string." startLine startColumn
                finished <- true
            elif peek () = quote then
                advance () |> ignore
                finished <- true
            elif peek () = '\\' then
                let backslashColumn = columnAt (current)
                advance () |> ignore // consume the backslash
                if isAtEnd () then
                    addError "Unterminated string." startLine startColumn
                    finished <- true
                else
                    let escapeChar = advance ()
                    match Map.tryFind escapeChar escapeSequences with
                    | Some resolved -> value.Append(resolved) |> ignore
                    | None ->
                        if not hadError then
                            addError $"Unrecognized escape sequence: \\{escapeChar}" line backslashColumn
                        hadError <- true
            elif peek () = '\n' then
                value.Append('\n') |> ignore
                advance () |> ignore
                advanceNewline ()
            else
                value.Append(advance ()) |> ignore
        if not hadError then
            addToken String (StringLiteral(value.ToString()))

    /// Number literals (`docs/PLAN-0.4.md` decisions 1-2 and §2 item 1).
    /// `first` is the already-consumed first character: a digit, or the
    /// `.` of a leading-dot float (`.5`).
    ///
    /// - Integers: `42`, `1_000`, `0xFF`, `0b1010`, `0o17` (prefixes in
    ///   either case). Exact, any size up to `maxIntegerBits`.
    /// - Floats: anything with a decimal point (`3.14`, `.5`, `1.`) or an
    ///   exponent (`1e9`, `2.5e-3`, `1.e5`).
    /// - `_` separators are permissive: repeated (`1__000`) and right after a
    ///   radix prefix (`0x_FF`) are fine. A `_` at the end of a literal, next
    ///   to the decimal point, or next to the exponent marker is an error.
    ///
    /// A bad literal reports one error and produces no token, like a string
    /// with a bad escape.
    let scanNumber (first: char) =
        let mutable error: string option = None
        let fail message =
            if error.IsNone then
                error <- Some message

        let peekAt (offset: int) =
            if current + offset >= source.Length then '\000' else source.[current + offset]

        /// Consumes digits and `_` separators; returns what was consumed.
        let digitRun () =
            let runStart = current
            while isDigit (peek ()) || peek () = '_' do
                advance () |> ignore
            source.Substring(runStart, current - runStart)

        let isRadixPrefix (c: char) =
            c = 'x' || c = 'X' || c = 'b' || c = 'B' || c = 'o' || c = 'O'

        if first = '0' && isRadixPrefix (peek ()) then
            let prefix = System.Char.ToLowerInvariant(advance ())
            let radix, name =
                match prefix with
                | 'x' -> 16, "hexadecimal"
                | 'b' -> 2, "binary"
                | _ -> 8, "octal"
            // Take every alphanumeric character, so `0b102` and `0xFG` are
            // one bad literal rather than a literal followed by an identifier.
            let bodyStart = current
            while isAlphaNumeric (peek ()) do
                advance () |> ignore
            let body = source.Substring(bodyStart, current - bodyStart)
            let digits = body.Replace("_", "")
            let digitValue (c: char) =
                if isDigit c then int c - int '0'
                elif c >= 'a' && c <= 'z' then int c - int 'a' + 10
                elif c >= 'A' && c <= 'Z' then int c - int 'A' + 10
                else 99
            if digits = "" then
                fail $"A {name} literal needs at least one digit."
            elif body.EndsWith "_" then
                fail "A number literal can't end with '_'."
            else
                match digits |> Seq.tryFind (fun c -> digitValue c >= radix) with
                | Some bad -> fail $"Invalid digit '{bad}' in {name} literal."
                | None ->
                    let value = digits |> Seq.fold (fun acc c -> acc * bigint radix + bigint (digitValue c)) 0I
                    if error.IsNone then
                        if value.GetBitLength() > int64 maxIntegerBits then
                            fail "Integer literal is too large."
                        else
                            addToken Number (IntLiteral value)
        else
            let mutable isFloat = false
            // The digit run most recently consumed: a `_` at its end is only
            // legal if more digits follow, which they never do once we stop.
            let mutable lastRun = ""
            if first = '.' then
                isFloat <- true
                lastRun <- digitRun ()
            else
                lastRun <- string first + digitRun ()
                // `1.` is a float; `1..` is left alone so `...` stays a token.
                if peek () = '.' && peekNext () <> '.' then
                    if lastRun.EndsWith "_" then
                        fail "'_' can't be next to the decimal point."
                    advance () |> ignore
                    isFloat <- true
                    if peek () = '_' then
                        fail "'_' can't be next to the decimal point."
                    lastRun <- digitRun ()

            let isExponentMarker (c: char) = c = 'e' || c = 'E'
            let exponentFollows =
                isExponentMarker (peek ())
                && (isDigit (peekAt 1)
                    || peekAt 1 = '_'
                    || ((peekAt 1 = '+' || peekAt 1 = '-') && isDigit (peekAt 2)))
            if exponentFollows then
                if lastRun.EndsWith "_" then
                    fail "'_' can't be next to the exponent."
                advance () |> ignore // the 'e'
                if peek () = '+' || peek () = '-' then
                    advance () |> ignore
                if peek () = '_' then
                    fail "'_' can't be next to the exponent."
                isFloat <- true
                lastRun <- digitRun ()

            if lastRun.EndsWith "_" then
                fail "A number literal can't end with '_'."

            if error.IsNone then
                let text = currentLexeme().Replace("_", "")
                if isFloat then
                    let value =
                        System.Double.Parse(
                            text,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                    if System.Double.IsInfinity value then
                        fail "Float literal is too large."
                    else
                        addToken Number (FloatLiteral value)
                else
                    let value = System.Numerics.BigInteger.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
                    if value.GetBitLength() > int64 maxIntegerBits then
                        fail "Integer literal is too large."
                    else
                        addToken Number (IntLiteral value)

        match error with
        | Some message -> addError message startLine startColumn
        | None -> ()

    let scanIdentifierOrKeyword () =
        while isAlphaNumeric (peek ()) do
            advance () |> ignore
        let text = currentLexeme ()
        match Map.tryFind text keywords with
        | Some tokenType -> addToken tokenType NoLiteral
        | None -> addToken Identifier NoLiteral

    let scanToken () =
        let c = advance ()
        if c = '\n' then
            addToken Semicolon NoLiteral
            advanceNewline ()
        elif c = ' ' || c = '\t' || c = '\r' then
            ()
        elif c = '#' then
            scanLineComment ()
        elif c = '<' && peek () = '#' then
            advance () |> ignore
            scanBlockComment ()
        elif c = '\'' || c = '"' then
            scanString c
        elif isDigit c then
            scanNumber c
        // `.5` is a float, but `x.5` is member access (an error later), so a
        // leading-dot float can't follow directly after something that ends
        // an expression. `...` is never affected: its second '.' isn't a digit.
        elif c = '.' && isDigit (peek ()) && not (start > 0 && endsExpression source.[start - 1]) then
            scanNumber c
        elif c = '_' then
            if isAlphaNumeric (peek ()) then
                scanIdentifierOrKeyword ()
            else
                addToken Underscore NoLiteral
        elif isAlpha c then
            scanIdentifierOrKeyword ()
        else
            match tryMatchOperator () with
            | Some tokenType -> addToken tokenType NoLiteral
            | None -> scanUnrecognizedRun ()

    member _.ScanTokens() : Token list * ScanError list =
        while not (isAtEnd ()) do
            start <- current
            startLine <- line
            startColumn <- columnAt start
            scanToken ()

        tokens.Add(
            { Type = Eof
              Lexeme = ""
              Literal = NoLiteral
              Line = line
              Column = columnAt current }
        )

        List.ofSeq tokens, List.ofSeq errors

/// Scans `source` into a list of tokens plus any scan errors. Scanning
/// never throws -- a bad character or an unterminated string/comment is
/// recorded as a `ScanError` and scanning continues, matching `poc`'s
/// "report and keep going" approach (mirroring parse-error recovery, not
/// an exception-per-error design).
let scanTokens (source: string) : Token list * ScanError list =
    ScannerState(source).ScanTokens()
