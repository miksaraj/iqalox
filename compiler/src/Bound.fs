/// The output of `Resolver`: a tree shaped exactly like `Ast.Expr`/
/// `Ast.Stmt`, except every variable reference/assignment/`self`/`super`
/// now carries a `VariableBinding` saying exactly where it lives (a
/// compile-time-known stack slot, a closed-over upvalue, or a
/// name-addressed global), and every declaration carries a
/// `DeclaredBinding` saying where *it* creates a slot. `Codegen` (Phase 5)
/// consumes this directly instead of re-deriving scope information from
/// the raw parsed AST.
module Iqalox.Bound

open Iqalox.Token
open Iqalox.Ast

/// Where a variable *reference* (a read, or the target of an assignment)
/// resolves to.
type VariableBinding =
    /// A stack slot in the current function's own frame.
    | LocalBinding of slot: int
    /// A variable closed over from an enclosing function -- `index` is
    /// this function's own upvalue list index, not the enclosing
    /// function's local slot (see `UpvalueDescriptor`).
    | UpvalueBinding of index: int
    /// Not found as a local or upvalue anywhere up the enclosing-function
    /// chain -- resolved dynamically by name at runtime, exactly like
    /// every variable in `poc`.
    | GlobalBinding of name: string

/// Where a variable *declaration* (`var`, a top-level `fun`/`class` name)
/// creates its binding. Never `UpvalueBinding` -- a declaration always
/// creates something new in the current function's locals or in the
/// global table, never closes over an existing one.
type DeclaredBinding =
    | DeclaredLocal of slot: int
    | DeclaredGlobal of name: string

/// How a closure captures one upvalue when it's created at runtime:
/// either directly from a slot in the *immediately* enclosing function's
/// frame (`FromEnclosingLocal = true`), or from an upvalue the enclosing
/// function itself already captured (`FromEnclosingLocal = false`) -- the
/// same two-case design `clox` uses (Crafting Interpreters ch. 25) to let
/// a doubly (or more) nested closure capture a variable from further up
/// the chain than its immediate parent.
type UpvalueDescriptor = { FromEnclosingLocal: bool; Index: int }

type BoundExpr =
    // Mutually recursive with `BoundFunctionDecl`/`BoundStmt` below (via
    // `BLambda`, which needs `BoundFunctionDecl` -- itself needing
    // `BoundStmt` for `Body`, which in turn contains `BoundExpr`s).
    | BAssign of binding: VariableBinding * name: Token * value: BoundExpr
    | BBinary of left: BoundExpr * operator: Token * right: BoundExpr
    | BLogical of left: BoundExpr * operator: Token * right: BoundExpr
    | BGrouping of expression: BoundExpr
    | BLiteral of value: LiteralValue
    | BUnary of operator: Token * right: BoundExpr
    | BTernary of left: BoundExpr * leftOperator: Token * middle: BoundExpr * rightOperator: Token * right: BoundExpr
    | BVector of values: BoundExpr list
    /// `docs/PLAN-0.2.md` Phase 4 -- only ever appears as a direct element
    /// of a `BVector`'s `values` list (see `Ast.Spread`'s doc comment).
    | BSpread of expr: BoundExpr * ellipsis: Token
    | BVariable of binding: VariableBinding * name: Token
    | BBreak of keyword: Token
    | BContinue of keyword: Token
    | BIgnore
    | BCall of callee: BoundExpr * arguments: BoundExpr list
    | BGet of obj: BoundExpr * name: Token
    | BSet of obj: BoundExpr * name: Token * value: BoundExpr
    | BIndex of obj: BoundExpr * index: BoundExpr * bracket: Token
    | BIndexSet of obj: BoundExpr * index: BoundExpr * value: BoundExpr * bracket: Token
    /// `docs/PLAN-0.3.md` decision 3 -- `start`/`stop` are `None` when
    /// the corresponding bound was omitted (`v[:3]`, `v[2:]`, `v[:]`).
    | BSlice of obj: BoundExpr * start: BoundExpr option * stop: BoundExpr option * bracket: Token
    /// Reuses `BoundFunctionDecl` wholesale -- a lambda resolves through
    /// exactly the same scope/slot/upvalue machinery as a nested named
    /// function (`docs/PLAN-0.2.md` §3), just with a synthetic `Name` (a
    /// lambda has no name to bind) and a body desugared to a single
    /// implicit `return` (`Resolver.fs`'s `Lambda` case).
    | BLambda of decl: BoundFunctionDecl
    /// Internal-only primitives (`docs/PLAN-0.2.md` Phase 3) with no
    /// surface syntax of their own -- `Resolver.fs` synthesizes them only
    /// while desugaring `Cons`/`ListComprehension` (see its doc comment
    /// on those cases for why: both need a real runtime loop over a
    /// vector of unknown-at-compile-time length, which they get by
    /// desugaring to a call of a synthetic closure built from ordinary
    /// `var`/`for`/`return` statements -- these two are the only
    /// primitives that don't already exist in that vocabulary). Wraps
    /// `VectorLength`/`VectorAppend` directly; `BVectorAppendInternal`
    /// evaluates to `Nil` (the opcode itself pushes nothing back) purely
    /// so it satisfies the "every expression pushes one value" contract
    /// when used as a bare `ExpressionStmt`.
    | BVectorLengthInternal of vector: BoundExpr * iterable: bool
    | BVectorAppendInternal of vector: BoundExpr * value: BoundExpr
    | BSelf of binding: VariableBinding * keyword: Token
    | BSuper of selfBinding: VariableBinding * binding: VariableBinding * keyword: Token * method: Token

/// `LocalCount` is how many stack slots the VM must reserve for this
/// function's frame (including its own implicit `self` slot for methods);
/// `Upvalues` tells the VM how to populate each captured variable when a
/// closure over this function is created. `IsPub` (`docs/PLAN-0.2.md`
/// decision 11) only matters for a method; carried through unchanged from
/// `Ast.FunctionDecl` for every other case (always `false`).
and BoundFunctionDecl =
    { Name: Token
      Parameters: Token list
      Body: BoundStmt list
      LocalCount: int
      Upvalues: UpvalueDescriptor list
      IsPub: bool }

/// Carries `Ast.PropertyDecl`'s `pub`/`mut` flags through resolution
/// unchanged -- a property declaration needs no scope/slot/upvalue
/// resolution of its own (`Codegen.fs`'s `CompileClass` just needs the
/// flags to pick which `Property*` opcode to emit).
and BoundPropertyDecl = { Name: Token; IsPub: bool; IsMutable: bool }

and BoundStmt =
    | BBlock of statements: BoundStmt list
    | BExpressionStmt of expression: BoundExpr
    | BVarStmt of binding: DeclaredBinding * name: Token * initializer: BoundExpr option
    | BForStmt of initializer: BoundStmt option * condition: BoundExpr option * increment: BoundExpr option * body: BoundStmt
    | BFunctionStmt of binding: DeclaredBinding * decl: BoundFunctionDecl
    | BReturnStmt of keyword: Token * value: BoundExpr option
    | BClassStmt of
        binding: DeclaredBinding *
        name: Token *
        superclass: (VariableBinding * Token) option *
        /// `docs/PLAN-0.2.md` decision 12's `with M1, M2` list, each
        /// resolved exactly like `superclass` -- an ordinary variable
        /// reference to a real, independently-instantiable class,
        /// composed at runtime (`Codegen.fs` emits one `Mixin` opcode
        /// per entry). `usedTraits` (PHP-style `use`) needs no field
        /// here at all: `Resolver.fs` fully consumes it at compile time,
        /// inlining a used trait's members directly into `properties`/
        /// `methods` below, so by the time a `BClassStmt` exists there's
        /// nothing left of `use` to represent.
        mixins: (VariableBinding * Token) list *
        properties: BoundPropertyDecl list *
        methods: BoundFunctionDecl list
