/// Token types and the `Token` record produced by `Scanner`. Mirrors the
/// token set `poc/src/token.py` defines (including the keywords reserved
/// for future mixin/trait/module support, which have no grammar yet), but
/// as an idiomatic F# discriminated union rather than a `str`-backed enum
/// -- there's no need here for TokenType members that only ever existed in
/// the Python version to double as raw-character dispatch keys (comment
/// markers, whitespace, string-quote characters, EOF-as-empty-string):
/// Scanner.fs matches on plain `char`/`string` literals for those instead.
module Iqalox.Token

type TokenType =
    // Single-character tokens.
    | LeftParen
    | RightParen
    | LeftBrace
    | RightBrace
    | LeftBracket
    | RightBracket
    | Comma
    | Semicolon
    | Slash
    | Backslash
    | Star
    | Underscore
    | Percent
    | Power

    // One- or two-plus-character tokens.
    | Bang
    | BangEqual
    | Equal
    | EqualEqual
    | Greater
    | GreaterEqual
    | Less
    | LessEqual
    | Minus
    | MinusMinus
    | Arrow
    | Plus
    | PlusPlus
    | Dot
    | Ellipsis
    | QuestionMark
    | Colon
    | QuestionMarkColon
    | DoubleQuestionMark
    | Pipe
    | VerticalBar
    | LeftArrow

    // Keywords.
    | And
    | Class
    | False
    | Fun
    | For
    | Nil
    | Or
    | Return
    | Super
    | Self
    | True
    | Var
    | With
    | Module
    | Trait
    | Extends
    | Break
    | Continue
    | Use
    | Mutable
    | Pub
    | Import
    | As

    // Literals.
    | Identifier
    | String
    | Number

    | Eof

/// A token's literal value, if it has one. `NoLiteral` covers every
/// non-literal token (punctuation, keywords, identifiers, EOF).
type Literal =
    | NoLiteral
    | NumberLiteral of float
    | StringLiteral of string

type Token =
    { Type: TokenType
      Lexeme: string
      Literal: Literal
      Line: int
      /// 1-indexed column of the lexeme's first character within its line.
      Column: int }

/// Keywords, per `poc/src/token.py`'s `_keywords` tuple. `with`/`module`/
/// `trait`/`use` are reserved for future mixin/trait/module support (0.2)
/// and scan as keywords here too, even though they have no grammar yet --
/// matching `poc`'s behavior, so an identifier can't accidentally shadow
/// one before that grammar lands.
let keywords: Map<string, TokenType> =
    Map.ofList [
        "and", And
        "class", Class
        "false", False
        "fun", Fun
        "for", For
        "nil", Nil
        "or", Or
        "return", Return
        "super", Super
        "self", Self
        "true", True
        "var", Var
        "with", With
        "module", Module
        "trait", Trait
        "extends", Extends
        "break", Break
        "continue", Continue
        "use", Use
        "mut", Mutable
        "pub", Pub
        "import", Import
        "as", As
    ]
