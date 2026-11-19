/// Constant folding shared by `Resolver.fs` (the division-by-zero check,
/// `docs/PLAN-0.4.md` decision 12) and `Optimizer.fs` (folding proper).
/// Lives before both in compile order. `foldBinary`/`foldUnary` follow the
/// VM's semantics exactly and refuse anything whose VM result could differ;
/// `evaluate` builds on them to decide whether a source expression is a
/// compile-time constant at all.
module Iqalox.ConstantFolding

open System
open Iqalox.Token
open Iqalox.Ast

let finite (x: float) = not (Double.IsNaN x || Double.IsInfinity x)

let numberResult (x: float) =
    if finite x then Some(FloatValue x) else None

/// Integers at most this large in magnitude convert to a double exactly, so
/// .NET's arithmetic on the converted values is exactly what the VM does.
/// Anything larger is left to the VM, which rounds big integers itself.
let exactInDouble = 9007199254740992I // 2^53

let toExactDouble (n: bigint) : float option =
    if bigint.Abs n <= exactInDouble then Some(float n) else None

/// An exact integer result, unless it's beyond the VM's size limit (the VM
/// then raises the error at runtime, so it must stay unfolded).
let intResult (n: bigint) =
    if n.GetBitLength() <= int64 Scanner.maxIntegerBits then Some(IntValue n) else None

/// The two operands as doubles when at least one is a float and the other
/// converts exactly (decision 6's promotion); None for int/int pairs.
let promoted (l: LiteralValue) (r: LiteralValue) : (float * float) option =
    match l, r with
    | FloatValue a, FloatValue b -> Some(a, b)
    | IntValue a, FloatValue b -> toExactDouble a |> Option.map (fun a -> a, b)
    | FloatValue a, IntValue b -> toExactDouble b |> Option.map (fun b -> a, b)
    | _ -> None

/// Only `nil` and `false` are decided here; any other literal's truthiness
/// is left to the VM at runtime.
let literalTruthiness (v: LiteralValue) : bool option =
    match v with
    | NilValue -> Some false
    | BoolValue b -> Some b
    | _ -> None

/// Folds one binary operator over two literals, following `docs/PLAN-0.4.md`
/// decisions 3-7 exactly. Anything whose VM result could differ (a runtime
/// error, an integer too big to convert exactly, `%`, `^`) is left unfolded.
let foldBinary (op: Token) (l: LiteralValue) (r: LiteralValue) : LiteralValue option =
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
    // `//` and the bitwise operators (Phase 2): integers only. A zero
    // divisor, a negative shift count, a float operand, or a result past
    // the size limit stays for the VM to report.
    | SlashSlash, IntValue a, IntValue b when not b.IsZero ->
        let quotient = bigint.Divide(a, b)
        // BigInteger division truncates; `//` floors.
        let floored = if not (a % b).IsZero && (a.Sign < 0) <> (b.Sign < 0) then quotient - 1I else quotient
        intResult floored
    | Ampersand, IntValue a, IntValue b -> Some(IntValue(a &&& b))
    | VerticalBar, IntValue a, IntValue b -> Some(IntValue(a ||| b))
    | Tilde, IntValue a, IntValue b -> Some(IntValue(a ^^^ b))
    | LessLess, IntValue a, IntValue b when b.Sign >= 0 ->
        if a.IsZero then Some(IntValue 0I)
        elif b > bigint Scanner.maxIntegerBits then None
        else intResult (a <<< int b)
    | GreaterGreater, IntValue a, IntValue b when b.Sign >= 0 ->
        // Arithmetic (floored) shift, like infinite two's complement.
        if b >= bigint (a.GetBitLength()) then Some(IntValue(if a.Sign < 0 then -1I else 0I))
        else Some(IntValue(a >>> int b))
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

/// Folds a prefix operator over a literal, or None when the VM must decide.
let foldUnary (op: Token) (v: LiteralValue) : LiteralValue option =
    match op.Type, v with
    | Minus, IntValue n -> Some(IntValue(-n))
    | Minus, FloatValue n -> Some(FloatValue(-n))
    | Tilde, IntValue n -> Some(IntValue(-n - 1I))
    | Bang, _ -> literalTruthiness v |> Option.map (fun t -> BoolValue(not t))
    | _ -> None

/// `%` and `^` on integers, for `evaluate` only: the optimizer never folds
/// them (their VM semantics aren't worth duplicating there), but deciding
/// whether `n / (10 % 3)` divides by a constant zero needs them. Kept to
/// cases that are exact and cheap: a non-zero `%` divisor, and powers with
/// a non-negative exponent whose result stays under 2^16 bits.
let private foldIntegerOnly (op: Token) (l: LiteralValue) (r: LiteralValue) : LiteralValue option =
    match op.Type, l, r with
    | Percent, IntValue a, IntValue b when not b.IsZero ->
        let m = bigint.Remainder(a, b)
        Some(IntValue(if not m.IsZero && (m.Sign < 0) <> (b.Sign < 0) then m + b else m))
    | Power, IntValue a, IntValue b when b.Sign >= 0 ->
        if bigint.Abs a <= 1I then
            Some(IntValue(if b.IsZero then 1I elif a.Sign < 0 && not b.IsEven then -1I else bigint.Abs a))
        elif bigint(bigint.Abs(a).GetBitLength()) * b <= 65536I then
            intResult (bigint.Pow(a, int b))
        else
            None
    | _ -> None

/// The compile-time constant value of `expr`, if it has one: literals,
/// groupings, unary/binary operators over constants, `??`, and
/// `and`/`or`/ternary with a constant condition. `lookup` gives the
/// value of a name that is a known constant (an immutable `var` with a
/// constant initializer), or None.
let rec evaluate (lookup: Token -> LiteralValue option) (expr: Expr) : LiteralValue option =
    match expr with
    | Literal v -> Some v
    | Grouping inner -> evaluate lookup inner
    | Variable name -> lookup name
    | Unary(op, operand) when op.Type <> PlusPlus && op.Type <> MinusMinus ->
        evaluate lookup operand |> Option.bind (foldUnary op)
    | Binary(left, op, right) when op.Type = DoubleQuestionMark ->
        match evaluate lookup left with
        | Some NilValue -> evaluate lookup right
        | Some v -> Some v
        | None -> None
    | Binary(left, op, right) ->
        match evaluate lookup left, evaluate lookup right with
        | Some l, Some r ->
            match foldBinary op l r with
            | Some v -> Some v
            | None -> foldIntegerOnly op l r
        | _ -> None
    // The same short-circuit folds as `Optimizer.fs`'s.
    | Logical(left, op, right) ->
        evaluate lookup left
        |> Option.bind (fun l ->
            match literalTruthiness l, op.Type with
            | Some false, And
            | Some true, Or -> Some l
            | Some true, And
            | Some false, Or -> evaluate lookup right
            | _ -> None)
    | Ternary(condition, op, middle, _, right) when op.Type <> QuestionMarkColon ->
        evaluate lookup condition
        |> Option.bind literalTruthiness
        |> Option.bind (fun t -> evaluate lookup (if t then middle else right))
    | _ -> None

