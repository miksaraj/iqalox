/// `docs/PLAN-0.3.md` Phase 7: the first set of compiler optimizations, run
/// on the linked, resolved `Bound` tree between `Modules.linkModules` and
/// `Codegen`. Every rewrite here must leave a program's observable
/// behavior -- output, runtime errors and their `[file, line N]` -- exactly
/// as it was, so each is deliberately conservative:
///
/// * **Constant folding** of arithmetic/comparison/equality on literals
///   (`+ - * /` only when the result is finite and, for `/`, the divisor is
///   non-zero; `% ^` are never folded -- their VM semantics are not worth
///   duplicating here). Integers fold exactly; anything mixing in an
///   integer too large to convert to a double exactly is left to the VM
///   (`docs/PLAN-0.4.md` Phase 1). Also unary `-`/`!`, `??` with a literal left side, and
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
    if finite x then Some(FloatValue x) else None

/// Integers at most this large in magnitude convert to a double exactly, so
/// .NET's arithmetic on the converted values is exactly what the VM does.
/// Anything larger is left to the VM, which rounds big integers itself.
let private exactInDouble = 9007199254740992I // 2^53

let private toExactDouble (n: bigint) : float option =
    if bigint.Abs n <= exactInDouble then Some(float n) else None

/// An exact integer result, unless it's beyond the VM's size limit (the VM
/// then raises the error at runtime, so it must stay unfolded).
let private intResult (n: bigint) =
    if n.GetBitLength() <= int64 Scanner.maxIntegerBits then Some(IntValue n) else None

/// The two operands as doubles when at least one is a float and the other
/// converts exactly (decision 6's promotion); None for int/int pairs.
let private promoted (l: LiteralValue) (r: LiteralValue) : (float * float) option =
    match l, r with
    | FloatValue a, FloatValue b -> Some(a, b)
    | IntValue a, FloatValue b -> toExactDouble a |> Option.map (fun a -> a, b)
    | FloatValue a, IntValue b -> toExactDouble b |> Option.map (fun b -> a, b)
    | _ -> None

/// Only `nil` and `false` are decided here; any other literal's truthiness
/// is left to the VM at runtime.
let private literalTruthiness (v: LiteralValue) : bool option =
    match v with
    | NilValue -> Some false
    | BoolValue b -> Some b
    | _ -> None

/// Folds one binary operator over two literals, following `docs/PLAN-0.4.md`
/// decisions 3-7 exactly. Anything whose VM result could differ (a runtime
/// error, an integer too big to convert exactly, `%`, `^`) is left unfolded.
let private foldBinary (op: Token) (l: LiteralValue) (r: LiteralValue) : LiteralValue option =
    match op.Type, l, r with
    // Exact integer arithmetic.
    | Plus, IntValue a, IntValue b -> intResult (a + b)
    | Minus, IntValue a, IntValue b -> intResult (a - b)
    | Star, IntValue a, IntValue b -> intResult (a * b)
    // `/` on two ints: exact, then rounded once. Two doubles of at most 2^53
    // divide with a single rounding, so that case folds exactly.
    | Slash, IntValue a, IntValue b when not b.IsZero ->
        match toExactDouble a, toExactDouble b with
        | Some x, Some y -> numberResult (x / y)
        | _ -> None
    | (Greater | GreaterEqual | Less | LessEqual), IntValue a, IntValue b ->
        let c = compare a b
        Some(
            BoolValue(
                match op.Type with
                | Greater -> c > 0
                | GreaterEqual -> c >= 0
                | Less -> c < 0
                | _ -> c <= 0
            )
        )
    // Floats, and int/float mixes whose int converts exactly. Exact
    // comparison (decision 7's follow-up) agrees with promotion here.
    | Plus, _, _ -> promoted l r |> Option.bind (fun (a, b) -> numberResult (a + b))
    | Minus, _, _ -> promoted l r |> Option.bind (fun (a, b) -> numberResult (a - b))
    | Star, _, _ -> promoted l r |> Option.bind (fun (a, b) -> numberResult (a * b))
    | Slash, _, _ ->
        promoted l r
        |> Option.bind (fun (a, b) -> if b <> 0.0 then numberResult (a / b) else None)
    | Greater, _, _ -> promoted l r |> Option.map (fun (a, b) -> BoolValue(a > b))
    | GreaterEqual, _, _ -> promoted l r |> Option.map (fun (a, b) -> BoolValue(a >= b))
    | Less, _, _ -> promoted l r |> Option.map (fun (a, b) -> BoolValue(a < b))
    | LessEqual, _, _ -> promoted l r |> Option.map (fun (a, b) -> BoolValue(a <= b))
    // `==` compares type and value (decision 7): `1 == 1.0` is false.
    | EqualEqual, IntValue a, IntValue b -> Some(BoolValue(a = b))
    | BangEqual, IntValue a, IntValue b -> Some(BoolValue(a <> b))
    | EqualEqual, FloatValue a, FloatValue b -> Some(BoolValue(a = b))
    | BangEqual, FloatValue a, FloatValue b -> Some(BoolValue(a <> b))
    | EqualEqual, (IntValue _ | FloatValue _), (IntValue _ | FloatValue _) -> Some(BoolValue false)
    | BangEqual, (IntValue _ | FloatValue _), (IntValue _ | FloatValue _) -> Some(BoolValue true)
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
    | BUnary(op, BLiteral(FloatValue n)) when op.Type = Minus -> BLiteral(FloatValue(-n))
    | BUnary(op, BLiteral(IntValue n)) when op.Type = Minus -> BLiteral(IntValue(-n))
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
