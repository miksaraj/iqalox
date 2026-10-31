/// The AST `Parser` produces. Mirrors the node shapes
/// `poc/src/expression.py`/`poc/src/statement.py` generate (see
/// `poc/tools/generate_ast.py`), but as plain F# discriminated unions
/// instead of a generated visitor-pattern class hierarchy -- pattern
/// matching replaces the visitor dispatch, with no code-generation step
/// needed.
module Iqalox.Ast

open Iqalox.Token

/// A literal expression's resolved value. Distinct from `Token.Literal`
/// (which only ever holds what the *scanner* attaches to a single NUMBER/
/// STRING token) -- this covers every literal `primary` can produce,
/// including `true`/`false`/`nil`, which the scanner hands back as plain
/// keyword tokens with no literal payload of their own.
type LiteralValue =
    | NilValue
    | BoolValue of bool
    | NumberValue of float
    | StringValue of string

/// Case names ending in `Expr` (`BreakExpr`, `ContinueExpr`, `SelfExpr`,
/// `SuperExpr`) would otherwise collide with `Token.TokenType`'s own
/// `Break`/`Continue`/`Self`/`Super` cases -- both modules are typically
/// opened together, and F# resolves a bare, ambiguous case name to
/// whichever `open` came last, silently breaking the other. Same story
/// for `Stmt` below (`Class`/`Var`/`For`/`Return`, suffixed `Stmt`).
/// Every other case name here is unambiguous and left bare.
type Expr =
    | Assign of name: Token * value: Expr
    | Binary of left: Expr * operator: Token * right: Expr
    | Logical of left: Expr * operator: Token * right: Expr
    | Grouping of expression: Expr
    | Literal of value: LiteralValue
    | Unary of operator: Token * right: Expr
    | Ternary of left: Expr * leftOperator: Token * middle: Expr * rightOperator: Token * right: Expr
    | Vector of values: Expr list
    /// `...expr` inside a vector literal (`docs/PLAN-0.2.md` decision 7)
    /// -- only ever produced by `Parser.fs` as a direct element of a
    /// `Vector`'s `values` list, never as a general expression (`...`
    /// isn't recognized as a prefix anywhere `Primary()`'s ordinary
    /// dispatch runs, so `f(...a)`/`1 + ...a` remain plain parse errors,
    /// matching decision 7's "vector-literal spread only" scope).
    | Spread of expr: Expr * ellipsis: Token
    | Variable of name: Token
    | BreakExpr of keyword: Token
    | ContinueExpr of keyword: Token
    | Ignore
    | Call of callee: Expr * arguments: Expr list
    | Get of obj: Expr * name: Token
    | Set of obj: Expr * name: Token * value: Expr
    | Index of obj: Expr * index: Expr * bracket: Token
    | IndexSet of obj: Expr * index: Expr * value: Expr * bracket: Token
    /// `v[a:b]` (`docs/PLAN-0.3.md` decision 3) -- either bound may be
    /// omitted (`v[:3]`, `v[2:]`, `v[:]`); no `SliceSet` counterpart
    /// exists since slices are read-only this version.
    | Slice of obj: Expr * start: Expr option * stop: Expr option * bracket: Token
    | Lambda of parameters: Token list * arrow: Token * body: Expr
    | Cons of item: Expr * list: Expr * bracket: Token
    /// `docs/PLAN-0.3.md` decision 1 -- `generators` is always non-empty
    /// (comma-separated `x <- xs` clauses, outermost/first-written
    /// first); `guard`, when present, is introduced by a second `|`
    /// after the generator list (`[expr | x <- xs, y <- ys | guard]`).
    /// `0.2` shipped exactly one generator and no guard; both are now
    /// optional/repeatable rather than fixed.
    | ListComprehension of body: Expr * generators: (Token * Expr) list * guard: Expr option * bracket: Token
    /// Internal-only, never produced by `Parser.fs` -- `Resolver.fs`
    /// synthesizes these while desugaring `Cons`/`ListComprehension` into
    /// a call to a synthetic closure (see its doc comment on those
    /// cases). `InternalVectorLength`/`InternalVectorAppend` are the only
    /// two primitives that desugaring needs and that don't already exist
    /// as ordinary surface syntax.
    | InternalVectorLength of vector: Expr
    | InternalVectorAppend of vector: Expr * value: Expr
    | SelfExpr of keyword: Token
    | SuperExpr of keyword: Token * method: Token

/// Shared between `Stmt.FunctionStmt` (a top-level `fun` declaration) and
/// `Stmt.ClassStmt`'s `methods` field -- `poc`'s `Class.methods` is typed
/// as `List[Function]`, a Python type hint with no runtime enforcement;
/// here it's a real, checked type, since a class's methods are never
/// anything but function declarations. `IsPub` (`docs/PLAN-0.2.md`
/// decision 11) is only meaningful for a method (a class-body function);
/// `Parser.fs` always sets it `false` for a top-level `fun`, which has no
/// visibility concept of its own to carry.
type FunctionDecl = { Name: Token; Parameters: Token list; Body: Stmt list; IsPub: bool }

/// A class-body property declaration (`var name [pub] [mut]`,
/// `docs/PLAN-0.2.md` decision 8) -- no initializer and no body, unlike
/// `VarStmt`: a property's first value always comes from whatever
/// assignment (commonly, but not necessarily, inside `init`) happens to
/// set it first (decision 9).
and PropertyDecl = { Name: Token; IsPub: bool; IsMutable: bool }

/// One `a.b.name [as alias]` entry of an `import` statement
/// (`docs/PLAN-0.3.md` decision 5): `Module` is every dotted segment but
/// the last (`a.b`), `Name` is the member being imported (`name`), and
/// `Alias`, when present, is the local name it's bound to instead.
and ImportItem = { Module: Token list; Name: Token; Alias: Token option }

and Stmt =
    | Block of statements: Stmt list
    | ExpressionStmt of expression: Expr
    | VarStmt of name: Token * initializer: Expr option * isMutable: bool
    | ForStmt of initializer: Stmt option * condition: Expr option * increment: Expr option * body: Stmt
    | FunctionStmt of FunctionDecl
    | ReturnStmt of keyword: Token * value: Expr option
    /// `docs/PLAN-0.2.md` decision 12: `mixins` is the `with M1, M2`
    /// header list (Scala-style, resolved/composed at runtime -- each
    /// entry is an ordinary `Variable` reference, mirroring `superclass`,
    /// since a mixin is a real, independently-instantiable class);
    /// `usedTraits` is every `use A, B` found anywhere in the class body
    /// (PHP-style, flattened across possibly-multiple `use` clauses,
    /// composed entirely at compile time -- see `Resolver.fs`, which
    /// inlines a used trait's own members directly into `properties`/
    /// `methods` and never lets a `TraitStmt` reach `Bound.fs` at all).
    | ClassStmt of
        name: Token *
        superclass: Expr option *
        mixins: Expr list *
        properties: PropertyDecl list *
        methods: FunctionDecl list *
        usedTraits: Token list
    /// `trait T { ... }` (decision 12) -- grammared identically to a class
    /// body (`langspec/SYNTAX_GRAMMAR.md`: properties, nested `use`, and
    /// methods), but never instantiable and never given a runtime
    /// representation at all: `Resolver.fs` consumes every `TraitStmt`
    /// entirely at compile time (inlining its members into whichever
    /// class(es) `use` it) and filters it out before `Bound.fs` ever sees
    /// the statement list, so there's no `BTraitStmt`/opcode/runtime
    /// object anywhere below this.
    | TraitStmt of name: Token * properties: PropertyDecl list * methods: FunctionDecl list * usedTraits: Token list
    /// `import a.b.name [as alias], ...` (`docs/PLAN-0.3.md` decision 5).
    /// Only meaningful at the top level of a file; consumed entirely by the
    /// module linker (`Modules.fs`) and never reaches `Bound.fs`.
    | ImportStmt of keyword: Token * items: ImportItem list
    /// `pub fun`/`pub var`/`pub class`/`pub trait` at the top level
    /// (decision 6) -- wraps the declaration it exports. The resolver
    /// unwraps it up front; a `pub` anywhere but the top level is an error.
    | PubStmt of keyword: Token * declaration: Stmt
