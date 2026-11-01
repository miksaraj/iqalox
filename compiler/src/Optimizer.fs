/// `docs/PLAN-0.3.md` Phase 7: the first set of compiler optimizations, run
/// on the linked, resolved `Bound` tree between `Modules.linkModules` and
/// `Codegen`. Every rewrite here must leave a program's observable
/// behavior -- output, runtime errors and their `[file, line N]` -- exactly
/// as it was, so each is deliberately conservative:
///
/// * **Constant folding** of arithmetic/comparison/equality on literals
///   (`+ - * /` only when the result is finite and, for `/`, the divisor is
///   non-zero; `% ^` are never folded -- their VM semantics are not worth
///   duplicating here), unary `-`/`!`, `??` with a literal left side, and
///   `and`/`or`/ternary on a `nil`/`bool` literal condition.
/// * **Global constant propagation**: a top-level `var x = <literal>` that
///   is never assigned or incremented anywhere in the program has every read
///   *textually after* its declaration replaced by the literal. "After"
///   matters: a read that could run before the declaration would raise
///   "Undefined variable" in the unoptimized program, and must keep doing
///   so. Function bodies defined after the declaration can only run after
///   it too. The declaration itself is kept.
/// * **Dead-code elimination**: statements after an unconditional `return`
///   in the same block, and expression statements that are bare literals
///   (what a folded-away `debug ? print x : nil` leaves behind).
///
/// Locals are not propagated: a `GetLocal` and a `Constant` cost the same.
module Iqalox.Optimizer

open System
open System.Collections.Generic
open Iqalox.Token
open Iqalox.Ast
open Iqalox.Bound

/// Hooks applied bottom-up by the generic traversal below: `Expr` to every
/// expression after its children were rewritten, `StmtList` to every
/// statement list (block, function body) after its statements were.
type private Hooks =
    { Expr: BoundExpr -> BoundExpr
      StmtList: BoundStmt list -> BoundStmt list }

let rec private mapExpr (h: Hooks) (e: BoundExpr) : BoundExpr =
    let m = mapExpr h
    let rebuilt =
        match e with
        | BAssign(b, n, v) -> BAssign(b, n, m v)
        | BBinary(l, o, r) -> BBinary(m l, o, m r)
        | BLogical(l, o, r) -> BLogical(m l, o, m r)
        | BGrouping x -> BGrouping(m x)
        // The operand of `++`/`--` is an assignment target, not a value.
        | BUnary(o, r) when o.Type = PlusPlus || o.Type = MinusMinus -> e
        | BUnary(o, r) -> BUnary(o, m r)
        | BTernary(l, lo, mid, ro, r) -> BTernary(m l, lo, m mid, ro, m r)
        | BVector values -> BVector(List.map m values)
        | BSpread(x, t) -> BSpread(m x, t)
        | BCall(c, args) -> BCall(m c, List.map m args)
        | BGet(o, n) -> BGet(m o, n)
        | BSet(o, n, v) -> BSet(m o, n, m v)
        | BIndex(o, i, t) -> BIndex(m o, m i, t)
        | BIndexSet(o, i, v, t) -> BIndexSet(m o, m i, m v, t)
        | BSlice(o, a, b, t) -> BSlice(m o, Option.map m a, Option.map m b, t)
        | BLambda d -> BLambda(mapDecl h d)
        | BVectorLengthInternal v -> BVectorLengthInternal(m v)
        | BVectorAppendInternal(v, x) -> BVectorAppendInternal(m v, m x)
        | BLiteral _
        | BVariable _
        | BBreak _
        | BContinue _
        | BIgnore
        | BSelf _
        | BSuper _ -> e
    h.Expr rebuilt

and private mapDecl (h: Hooks) (d: BoundFunctionDecl) : BoundFunctionDecl =
    { d with Body = h.StmtList(List.map (mapStmt h) d.Body) }

and private mapStmt (h: Hooks) (s: BoundStmt) : BoundStmt =
    match s with
    | BBlock l -> BBlock(h.StmtList(List.map (mapStmt h) l))
    | BExpressionStmt e -> BExpressionStmt(mapExpr h e)
    | BVarStmt(b, n, i) -> BVarStmt(b, n, Option.map (mapExpr h) i)
    | BForStmt(init, cond, inc, body) ->
        BForStmt(Option.map (mapStmt h) init, Option.map (mapExpr h) cond, Option.map (mapExpr h) inc, mapStmt h body)
    | BFunctionStmt(b, d) -> BFunctionStmt(b, mapDecl h d)
    | BReturnStmt(k, v) -> BReturnStmt(k, Option.map (mapExpr h) v)
    | BClassStmt(b, n, sup, mixins, props, methods) -> BClassStmt(b, n, sup, mixins, props, List.map (mapDecl h) methods)

// ---------------------------------------------------------------------------
// Folding
// ---------------------------------------------------------------------------

let private finite (x: float) = not (Double.IsNaN x || Double.IsInfinity x)

let private numberResult (x: float) =
    if finite x then Some(NumberValue x) else None

/// Only `nil` and `false` are decided here; any other literal's truthiness
/// is left to the VM at runtime.
let private literalTruthiness (v: LiteralValue) : bool option =
    match v with
    | NilValue -> Some false
    | BoolValue b -> Some b
    | _ -> None

let private foldBinary (op: Token) (l: LiteralValue) (r: LiteralValue) : LiteralValue option =
    match op.Type, l, r with
    | Plus, NumberValue a, NumberValue b -> numberResult (a + b)
    | Minus, NumberValue a, NumberValue b -> numberResult (a - b)
    | Star, NumberValue a, NumberValue b -> numberResult (a * b)
    | Slash, NumberValue a, NumberValue b when b <> 0.0 -> numberResult (a / b)
    | Greater, NumberValue a, NumberValue b -> Some(BoolValue(a > b))
    | GreaterEqual, NumberValue a, NumberValue b -> Some(BoolValue(a >= b))
    | Less, NumberValue a, NumberValue b -> Some(BoolValue(a < b))
    | LessEqual, NumberValue a, NumberValue b -> Some(BoolValue(a <= b))
    | EqualEqual, NumberValue a, NumberValue b -> Some(BoolValue(a = b))
    | BangEqual, NumberValue a, NumberValue b -> Some(BoolValue(a <> b))
    | EqualEqual, BoolValue a, BoolValue b -> Some(BoolValue(a = b))
    | BangEqual, BoolValue a, BoolValue b -> Some(BoolValue(a <> b))
    | EqualEqual, StringValue a, StringValue b -> Some(BoolValue(a = b))
    | BangEqual, StringValue a, StringValue b -> Some(BoolValue(a <> b))
    | EqualEqual, NilValue, NilValue -> Some(BoolValue true)
    | BangEqual, NilValue, NilValue -> Some(BoolValue false)
    | _ -> None

let private simplify (constants: Dictionary<string, LiteralValue>) (e: BoundExpr) : BoundExpr =
    match e with
    | BVariable(GlobalBinding name, _) when constants.ContainsKey name -> BLiteral constants.[name]
    | BGrouping(BLiteral v) -> BLiteral v
    | BUnary(op, BLiteral(NumberValue n)) when op.Type = Minus -> BLiteral(NumberValue(-n))
    | BUnary(op, BLiteral v) when op.Type = Bang ->
        match literalTruthiness v with
        | Some t -> BLiteral(BoolValue(not t))
        | None -> e
    | BBinary(BLiteral NilValue, op, right) when op.Type = DoubleQuestionMark -> right
    | BBinary(BLiteral v, op, _) when op.Type = DoubleQuestionMark && v <> NilValue -> BLiteral v
    | BBinary(BLiteral l, op, BLiteral r) ->
        match foldBinary op l r with
        | Some v -> BLiteral v
        | None -> e
    | BLogical(BLiteral l, op, right) ->
        match literalTruthiness l, op.Type with
        | Some false, And -> BLiteral l
        | Some true, And -> right
        | Some false, Or -> right
        | Some true, Or -> BLiteral l
        | _ -> e
    | BTernary(BLiteral c, op, middle, _, right) when op.Type <> QuestionMarkColon ->
        match literalTruthiness c with
        | Some true -> middle
        | Some false -> right
        | None -> e
    | _ -> e

// ---------------------------------------------------------------------------
// Dead code
// ---------------------------------------------------------------------------

let rec private terminates (s: BoundStmt) : bool =
    match s with
    | BReturnStmt _ -> true
    | BBlock l ->
        match List.tryLast l with
        | Some last -> terminates last
        | None -> false
    | _ -> false

/// Drops bare-literal expression statements and everything after a
/// statement that always returns.
let private eliminateDead (stmts: BoundStmt list) : BoundStmt list =
    let result = ResizeArray<BoundStmt>()
    let mutable dead = false
    for s in stmts do
        if not dead then
            match s with
            | BExpressionStmt(BLiteral _) -> ()
            | _ ->
                result.Add s
                if terminates s then dead <- true
    List.ofSeq result

// ---------------------------------------------------------------------------
// Driver
// ---------------------------------------------------------------------------

/// Names of every global that is assigned, `++`'d or `--`'d anywhere.
let private assignedGlobals (segments: (string * BoundStmt list) list) : HashSet<string> =
    let assigned = HashSet<string>()
    let record (e: BoundExpr) =
        match e with
        | BAssign(GlobalBinding name, _, _) -> assigned.Add name |> ignore
        | BUnary(op, BVariable(GlobalBinding name, _)) when op.Type = PlusPlus || op.Type = MinusMinus ->
            assigned.Add name |> ignore
        | _ -> ()
        e
    let hooks = { Expr = record; StmtList = id }
    for (_, stmts) in segments do
        for s in stmts do
            mapStmt hooks s |> ignore
    assigned

/// Optimizes a linked program's segments (in execution order), returning
/// the same shape.
let optimize (segments: (string * BoundStmt list) list) : (string * BoundStmt list) list =
    let assigned = assignedGlobals segments
    let constants = Dictionary<string, LiteralValue>()
    let hooks =
        { Expr = simplify constants
          StmtList = eliminateDead }

    segments
    |> List.map (fun (file, stmts) ->
        let result = ResizeArray<BoundStmt>()
        let mutable dead = false
        for s in stmts do
            if not dead then
                let s' = mapStmt hooks s
                match s' with
                | BExpressionStmt(BLiteral _) -> ()
                | _ ->
                    result.Add s'
                    if terminates s' then dead <- true
                    // Registered only after the statement itself was
                    // rewritten, so its own initializer never sees it,
                    // and only statements textually after can.
                    match s' with
                    | BVarStmt(DeclaredGlobal name, _, Some(BLiteral v)) when not (assigned.Contains name) ->
                        constants.[name] <- v
                    | _ -> ()
        file, List.ofSeq result)
