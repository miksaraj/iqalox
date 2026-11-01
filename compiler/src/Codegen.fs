/// `BoundStmt`/`BoundExpr` -> `Bytecode.Chunk`. Phase 5's compiler backend,
/// per `docs/PLAN-0.1.md`.
///
/// A local variable's declaration needs no dedicated "store" instruction:
/// its initializer's pushed value already sits in the stack slot `Resolver`
/// assigned it, since slots are handed out in strict push order. Only
/// re-assignment needs an explicit (non-popping, since assignment is itself
/// an expression) `SetLocal`/`SetUpvalue`. Globals are the mirror image --
/// not stack-resident, so declaration pops via `DefineGlobal` but
/// assignment still doesn't pop, for the same "assignment is an expression"
/// reason.
///
/// `??`, the comma operator, and elvis (`?:`) all fix real `poc` bugs
/// (evaluates to `nil` always; doesn't short-circuit; double-evaluates its
/// condition -- see `docs/PLAN-0.1-POC.md`'s running list) rather than
/// carrying them forward: comma is `compile left; Pop; compile right` with
/// no dedicated opcode, `??` short-circuits via the peek-based
/// `JumpIfNotNil`, and elvis is told apart from a full ternary by
/// `leftOperator.Type` (`QuestionMarkColon` vs `QuestionMark` --
/// `Parser.fs` sets both `Ternary.left`/`.middle` to the very same node for
/// elvis, so `middle` can't be used to tell them apart) and evaluates its
/// condition exactly once, reusing its pushed value for the truthy branch.
///
/// `super.method()` codegen needs `self`'s binding as well as `super`'s,
/// resolved independently (mirroring `clox`) so a super-call from inside a
/// closure nested *within* a method still finds the right `self` --
/// `Bound.BSuper`/`Resolver.fs`'s `SuperExpr` case carry/compute both.
///
/// `BIndex`/`BIndexSet` (`docs/PLAN-0.2.md` Phase 1) compile like
/// `BGet`/`BSet`, just with the index pushed as an ordinary runtime value
/// instead of `AddStringConstant`-ing a compile-time-known property name
/// -- `GetIndex`/`SetIndex` take no operand at all, unlike
/// `GetProperty`/`SetProperty`'s `nameIndex`.
///
/// `BLambda` (`docs/PLAN-0.2.md` Phase 2) needs no new opcode at all --
/// `Resolver.fs`'s `Lambda` case already desugars it to an ordinary
/// (nameless) `BoundFunctionDecl` with a single implicit `return`, so
/// `CompileExpr` just hands it to the existing `CompileFunctionValue`
/// (previously only ever called from `BFunctionStmt`/`CompileClass`),
/// which pushes a `Closure` the same way any named function or method
/// already does.
///
/// `FunctionState.StackDepth` is `Codegen`'s own running counter (distinct
/// from `Resolver`'s already-computed slot numbers) of how many values are
/// currently pushed, kept in sync by routing every emission through
/// `Emit`. It lets block/loop/function exits know how many `PopN` to emit,
/// and lets `break`/`continue` unwind the right number of slots inline at
/// the jump site rather than sharing (and double-popping via) a common
/// cleanup path. The one place a purely sequential counter isn't enough is
/// a `for` loop's condition check: the "falsy condition" landing point and
/// the "truthy, fell into the body" path both flow from the same
/// `JumpIfFalse`, but only the truthy path has already popped the
/// condition value by the time the counter reaches that point textually --
/// see `CompileFor`'s explicit reset.
module Iqalox.Codegen

open System
open Iqalox.Token
open Iqalox.Ast
open Iqalox.Bound
open Iqalox.Bytecode

type CodegenError = { Message: string; Line: int }

/// The source line a runtime fault at this expression should be blamed on
/// -- whichever token is closest at hand, recursing into a handful of
/// cases with no token of their own (a grouping/vector's own line is
/// whichever its first sub-expression's is; a bare literal/`_`/`break`/
/// `continue` has none at all, so `None` here means "leave `CurrentLine`
/// exactly as it already was," not "reset to line 0"). Mirrors `clox`'s
/// own approach of stamping every emitted byte with "whatever the parser's
/// `previous` token's line was most recently," just computed from the
/// bound tree instead of a live parser cursor.
let rec private lineOfExpr (expr: BoundExpr) : int option =
    match expr with
    | BAssign(_, name, _) -> Some name.Line
    | BBinary(_, operator, _) -> Some operator.Line
    | BLogical(_, operator, _) -> Some operator.Line
    | BGrouping inner -> lineOfExpr inner
    | BLiteral _ -> None
    | BUnary(operator, _) -> Some operator.Line
    | BTernary(_, leftOperator, _, _, _) -> Some leftOperator.Line
    | BVector values -> values |> List.tryPick lineOfExpr
    | BSpread(_, ellipsis) -> Some ellipsis.Line
    | BVariable(_, name) -> Some name.Line
    | BBreak keyword -> Some keyword.Line
    | BContinue keyword -> Some keyword.Line
    | BIgnore -> None
    | BCall(callee, _) -> lineOfExpr callee
    | BGet(_, name) -> Some name.Line
    | BSet(_, name, _) -> Some name.Line
    | BIndex(_, _, bracket) -> Some bracket.Line
    | BIndexSet(_, _, _, bracket) -> Some bracket.Line
    | BSlice(_, _, _, bracket) -> Some bracket.Line
    | BLambda decl -> Some decl.Name.Line
    | BVectorLengthInternal vector -> lineOfExpr vector
    | BVectorAppendInternal(vector, _) -> lineOfExpr vector
    | BSelf(_, keyword) -> Some keyword.Line
    | BSuper(_, _, keyword, _) -> Some keyword.Line

let rec private lineOfStmt (stmt: BoundStmt) : int option =
    match stmt with
    | BBlock statements -> statements |> List.tryPick lineOfStmt
    | BExpressionStmt expr -> lineOfExpr expr
    | BVarStmt(_, name, _) -> Some name.Line
    | BForStmt(initializer, condition, _, body) ->
        [ initializer |> Option.bind lineOfStmt
          condition |> Option.bind lineOfExpr
          lineOfStmt body ]
        |> List.tryPick id
    | BFunctionStmt(_, decl) -> Some decl.Name.Line
    | BReturnStmt(keyword, _) -> Some keyword.Line
    | BClassStmt(_, name, _, _, _, _) -> Some name.Line

/// Net stack effect of one instruction -- the only input `FunctionState`
/// needs to keep `StackDepth` accurate as instructions are emitted.
let private stackEffect (instr: Instruction) : int =
    match instr with
    | Constant _
    | Nil
    | True
    | False
    | Undef -> 1
    | Pop -> -1
    | PopN n -> -n
    | GetLocal _
    | GetUpvalue _
    | GetGlobal _ -> 1
    | SetLocal _
    | SetUpvalue _
    | SetGlobal _ -> 0
    | DefineGlobal _ -> -1
    | Add
    | Subtract
    | Multiply
    | Divide
    | Modulo
    | Power -> -1
    | Negate
    | Not -> 0
    | Equal
    | NotEqual
    | Greater
    | GreaterEqual
    | Less
    | LessEqual -> -1
    | Jump _
    | JumpIfFalse _
    | JumpIfNotNil _ -> 0
    | BuildVector count -> 1 - count
    | Call argCount -> -argCount
    | Closure _ -> 1
    | Return -> -1
    | Class _ -> 1
    | Method _
    | MethodPub _ -> -1
    | Inherit -> 0
    | Mixin -> -1
    | GetProperty _
    | GetPropertySelf _ -> 0
    | SetProperty _
    | SetPropertySelf _ -> -1
    | GetSuper _ -> -1
    | PropertyPrivate _
    | PropertyPrivateMut _
    | PropertyPub _
    | PropertyPubMut _ -> 0
    // Pops obj + index, pushes the element: net -1. SetIndex additionally
    // pops the value being assigned, then pushes it back (assignment is
    // an expression) -- net -2.
    | GetIndex -> -1
    | SetIndex -> -2
    | VectorLength -> 0
    | VectorAppend -> -2
    // Pops source + target, pushes target back: net -1.
    | VectorExtend -> -1
    // Pops obj + start + stop, pushes the new sliced vector: net -2.
    | GetSlice -> -2

type private LoopContext =
    { BreakTargetDepth: int
      ContinueTargetDepth: int
      BreakJumps: ResizeArray<int>
      ContinueJumps: ResizeArray<int> }

/// One function's (or the top-level script's) in-progress chunk. `Codegen`
/// swaps `state` to a fresh one per nested function, mirroring
/// `Resolver.fs`'s own `context <- FunctionContext(...)` pattern.
type private FunctionState(initialStackDepth: int) =
    let instructions = ResizeArray<Instruction>()
    let lines = ResizeArray<int>()
    let files = ResizeArray<int>()
    let constants = ResizeArray<Constant>()

    member val StackDepth = initialStackDepth with get, set
    member val Loop: LoopContext option = None with get, set
    /// The source line the *next* emitted instruction gets stamped with --
    /// updated from whichever token `CompileExpr`/`CompileStmt` most
    /// recently had in view (see `lineOfExpr`/`lineOfStmt`), not reset per
    /// instruction, so a token-less case (a literal, `_`) just inherits
    /// whatever line was already current.
    member val CurrentLine = 0 with get, set
    /// Index into the program's file table of the source file the next
    /// emitted instruction belongs to (see `Codegen.CompileSegments`).
    member val CurrentFile = 0 with get, set

    member _.Here = instructions.Count

    member this.Emit(instr: Instruction) : int =
        instructions.Add instr
        lines.Add this.CurrentLine
        files.Add this.CurrentFile
        this.StackDepth <- this.StackDepth + stackEffect instr
        instructions.Count - 1

    member _.PatchJump(index: int, target: int) =
        instructions.[index] <-
            match instructions.[index] with
            | Jump _ -> Jump target
            | JumpIfFalse _ -> JumpIfFalse target
            | JumpIfNotNil _ -> JumpIfNotNil target
            | other -> failwith $"unreachable: patching a non-jump instruction %A{other}"

    member _.AddNumberConstant(n: float) : int =
        match constants |> Seq.tryFindIndex (function
            | NumberConstant existing -> BitConverter.DoubleToInt64Bits existing = BitConverter.DoubleToInt64Bits n
            | _ -> false) with
        | Some i -> i
        | None ->
            constants.Add(NumberConstant n)
            constants.Count - 1

    member _.AddStringConstant(s: string) : int =
        match constants |> Seq.tryFindIndex (function
            | StringConstant existing -> existing = s
            | _ -> false) with
        | Some i -> i
        | None ->
            constants.Add(StringConstant s)
            constants.Count - 1

    member _.AddFunctionConstant(proto: FunctionProto) : int =
        constants.Add(FunctionConstant proto)
        constants.Count - 1

    member _.ToChunk() : Chunk =
        { Constants = constants.ToArray()
          Code = instructions.ToArray()
          Lines = lines.ToArray()
          Files = files.ToArray() }

type private Codegen(elideEpilogue: bool) =
    let errors = ResizeArray<CodegenError>()
    let mutable state = FunctionState(0)
    // The program's source-file table (`Bytecode.fs`' "files"), and the
    // index of the file whose statements are being compiled right now --
    // a nested function is always entirely inside one file, so it just
    // inherits this when its own `FunctionState` is created.
    let fileNames = ResizeArray<string>()
    let mutable currentFile = 0

    let error (message: string) =
        errors.Add { Message = message; Line = state.CurrentLine }

    member this.Errors = List.ofSeq errors

    member private this.CompileGetBinding(binding: VariableBinding) : unit =
        match binding with
        | LocalBinding slot -> state.Emit(GetLocal slot) |> ignore
        | UpvalueBinding index -> state.Emit(GetUpvalue index) |> ignore
        | GlobalBinding name -> state.Emit(GetGlobal(state.AddStringConstant name)) |> ignore

    member private this.CompileSetBinding(binding: VariableBinding) : unit =
        match binding with
        | LocalBinding slot -> state.Emit(SetLocal slot) |> ignore
        | UpvalueBinding index -> state.Emit(SetUpvalue index) |> ignore
        | GlobalBinding name -> state.Emit(SetGlobal(state.AddStringConstant name)) |> ignore

    member private this.CompileDeclareBinding(binding: DeclaredBinding) : unit =
        match binding with
        | DeclaredLocal _ -> ()
        | DeclaredGlobal name -> state.Emit(DefineGlobal(state.AddStringConstant name)) |> ignore

    member private this.CompileBinaryOp(operator: Token) : unit =
        // `TokenType` and `Instruction` both have `Power`/`Greater`/
        // `GreaterEqual`/`Less`/`LessEqual` cases -- qualified explicitly
        // on both sides here to avoid the same open-order ambiguity
        // `Ast.fs` already ran into (see its own doc comment).
        let opcode =
            match operator.Type with
            | TokenType.Plus -> Instruction.Add
            | TokenType.Minus -> Instruction.Subtract
            | TokenType.Star -> Instruction.Multiply
            | TokenType.Slash -> Instruction.Divide
            | TokenType.Percent -> Instruction.Modulo
            | TokenType.Power -> Instruction.Power
            | TokenType.EqualEqual -> Instruction.Equal
            | TokenType.BangEqual -> Instruction.NotEqual
            | TokenType.Greater -> Instruction.Greater
            | TokenType.GreaterEqual -> Instruction.GreaterEqual
            | TokenType.Less -> Instruction.Less
            | TokenType.LessEqual -> Instruction.LessEqual
            | other -> failwith $"unreachable: not a binary operator token: %A{other}"
        state.Emit opcode |> ignore

    member private this.CompileJumpOut
        (getTargetDepth: LoopContext -> int)
        (getJumpList: LoopContext -> ResizeArray<int>)
        (name: string)
        : unit =
        match state.Loop with
        | None -> error $"Can't use '{name}' outside of a loop."
        | Some loop ->
            let popCount = state.StackDepth - getTargetDepth loop
            if popCount > 0 then
                state.Emit(PopN popCount) |> ignore
            let jumpIndex = state.Emit(Jump -1)
            (getJumpList loop).Add jumpIndex
            // Dead code from here on (an unconditional jump never falls
            // through), but Codegen still walks over anything that
            // textually follows -- push a placeholder so that walk's own
            // stack-depth bookkeeping stays self-consistent.
            state.Emit Nil |> ignore

    member private this.CompileExpr(expr: BoundExpr) : unit =
        lineOfExpr expr |> Option.iter (fun line -> state.CurrentLine <- line)
        match expr with
        | BLiteral NilValue -> state.Emit Nil |> ignore
        | BLiteral(BoolValue true) -> state.Emit True |> ignore
        | BLiteral(BoolValue false) -> state.Emit False |> ignore
        | BLiteral(NumberValue n) -> state.Emit(Constant(state.AddNumberConstant n)) |> ignore
        | BLiteral(StringValue s) -> state.Emit(Constant(state.AddStringConstant s)) |> ignore
        | BGrouping inner -> this.CompileExpr inner
        | BVariable(binding, _) -> this.CompileGetBinding binding
        | BSelf(binding, _) -> this.CompileGetBinding binding
        | BIgnore -> state.Emit Nil |> ignore
        | BBreak _ -> this.CompileJumpOut (fun l -> l.BreakTargetDepth) (fun l -> l.BreakJumps) "break"
        | BContinue _ -> this.CompileJumpOut (fun l -> l.ContinueTargetDepth) (fun l -> l.ContinueJumps) "continue"
        | BAssign(binding, _, value) ->
            this.CompileExpr value
            this.CompileSetBinding binding
        | BUnary(operator, right) ->
            match operator.Type with
            | Bang ->
                this.CompileExpr right
                state.Emit Not |> ignore
            | Minus ->
                this.CompileExpr right
                state.Emit Negate |> ignore
            | PlusPlus
            | MinusMinus ->
                match right with
                | BVariable(binding, _) ->
                    this.CompileGetBinding binding
                    state.Emit(Constant(state.AddNumberConstant 1.0)) |> ignore
                    state.Emit(if operator.Type = PlusPlus then Add else Subtract) |> ignore
                    this.CompileSetBinding binding
                | other -> failwith $"unreachable: increment/decrement target is always a variable, got %A{other}"
            | other -> failwith $"unreachable: not a unary operator token: %A{other}"
        | BBinary(left, operator, right) ->
            match operator.Type with
            | Comma ->
                this.CompileExpr left
                state.Emit Pop |> ignore
                this.CompileExpr right
            | DoubleQuestionMark ->
                this.CompileExpr left
                let jumpIfNotNil = state.Emit(JumpIfNotNil -1)
                state.Emit Pop |> ignore
                this.CompileExpr right
                state.PatchJump(jumpIfNotNil, state.Here)
            | _ ->
                this.CompileExpr left
                this.CompileExpr right
                this.CompileBinaryOp operator
        | BLogical(left, operator, right) ->
            match operator.Type with
            | Or ->
                this.CompileExpr left
                let jumpIfFalse = state.Emit(JumpIfFalse -1)
                let jumpToEnd = state.Emit(Jump -1)
                state.PatchJump(jumpIfFalse, state.Here)
                state.Emit Pop |> ignore
                this.CompileExpr right
                state.PatchJump(jumpToEnd, state.Here)
            | And ->
                this.CompileExpr left
                let jumpIfFalse = state.Emit(JumpIfFalse -1)
                state.Emit Pop |> ignore
                this.CompileExpr right
                state.PatchJump(jumpIfFalse, state.Here)
            | other -> failwith $"unreachable: not a logical operator token: %A{other}"
        | BTernary(left, leftOp, middle, _, right) ->
            match leftOp.Type with
            | QuestionMarkColon ->
                // Elvis: `middle` is the very same (already-resolved) node
                // as `left` -- Parser.fs reuses it verbatim -- so it's
                // deliberately ignored here in favor of evaluating `left`
                // exactly once and reusing its truthy value directly.
                this.CompileExpr left
                let jumpIfFalse = state.Emit(JumpIfFalse -1)
                let jumpToEnd = state.Emit(Jump -1)
                state.PatchJump(jumpIfFalse, state.Here)
                state.Emit Pop |> ignore
                this.CompileExpr right
                state.PatchJump(jumpToEnd, state.Here)
            | _ ->
                this.CompileExpr left
                let jumpToElse = state.Emit(JumpIfFalse -1)
                state.Emit Pop |> ignore
                this.CompileExpr middle
                let jumpToEnd = state.Emit(Jump -1)
                state.PatchJump(jumpToElse, state.Here)
                state.Emit Pop |> ignore
                this.CompileExpr right
                state.PatchJump(jumpToEnd, state.Here)
        | BVector values ->
            let hasSpread =
                values
                |> List.exists (function
                    | BSpread _ -> true
                    | _ -> false)
            if not hasSpread then
                for value in values do
                    this.CompileExpr value
                state.Emit(BuildVector(List.length values)) |> ignore
            else
                // docs/PLAN-0.2.md Phase 4: a spread element's source
                // length isn't known at compile time, so this can't be a
                // single fixed-operand BuildVector. Built up purely on the
                // stack instead -- no hidden local slots at all, unlike
                // Phase 3's Cons/ListComprehension (which needed an
                // isolated closure frame to avoid corrupting the
                // enclosing expression's own stack addressing): each step
                // here just pops+pushes the accumulator in place, so it
                // composes safely anywhere an expression can, including
                // mid-expression, with no such risk.
                state.Emit(BuildVector 0) |> ignore // accumulator = []
                for value in values do
                    match value with
                    | BSpread(inner, _) ->
                        this.CompileExpr inner
                        state.Emit VectorExtend |> ignore
                    | plain ->
                        this.CompileExpr plain
                        state.Emit(BuildVector 1) |> ignore
                        state.Emit VectorExtend |> ignore
        | BCall(callee, arguments) ->
            this.CompileExpr callee
            for argument in arguments do
                this.CompileExpr argument
            state.Emit(Call(List.length arguments)) |> ignore
        | BGet(obj, name) ->
            this.CompileExpr obj
            let nameIndex = state.AddStringConstant name.Lexeme
            // `docs/PLAN-0.2.md` decision 10: `self.x` (obj is exactly
            // `BSelf`) is internal access, skipping the `pub` gate
            // entirely; every other object expression -- including
            // `instance.x` on some other, non-`self` reference to an
            // instance of the very same class -- is external, per
            // decision 10's own literal wording ("self.x" vs "instance.x
            // from outside"). A purely syntactic check, needing no
            // Resolver.fs-level class-hierarchy bookkeeping.
            match obj with
            | BSelf _ -> state.Emit(GetPropertySelf nameIndex) |> ignore
            | _ -> state.Emit(GetProperty nameIndex) |> ignore
        | BSet(obj, name, value) ->
            this.CompileExpr obj
            this.CompileExpr value
            let nameIndex = state.AddStringConstant name.Lexeme
            match obj with
            | BSelf _ -> state.Emit(SetPropertySelf nameIndex) |> ignore
            | _ -> state.Emit(SetProperty nameIndex) |> ignore
        | BIndex(obj, index, _) ->
            this.CompileExpr obj
            this.CompileExpr index
            state.Emit GetIndex |> ignore
        | BIndexSet(obj, index, value, _) ->
            this.CompileExpr obj
            this.CompileExpr index
            this.CompileExpr value
            state.Emit SetIndex |> ignore
        | BSlice(obj, start, stop, _) ->
            this.CompileExpr obj
            match start with
            | Some s -> this.CompileExpr s
            | None -> state.Emit Nil |> ignore
            match stop with
            | Some s -> this.CompileExpr s
            | None -> state.Emit Nil |> ignore
            state.Emit GetSlice |> ignore
        | BLambda decl -> this.CompileFunctionValue(decl, isMethod = false)
        | BSpread _ ->
            // `Ast.Spread`'s doc comment: `Parser.fs` only ever produces
            // this as a direct element of a `Vector`'s own `values` list,
            // handled entirely within `BVector`'s case above -- never
            // reached through this top-level dispatch.
            failwith "unreachable: BSpread only ever appears inside BVector's values list"
        | BVectorLengthInternal vector ->
            this.CompileExpr vector
            state.Emit VectorLength |> ignore
        | BVectorAppendInternal(vector, value) ->
            this.CompileExpr vector
            this.CompileExpr value
            state.Emit VectorAppend |> ignore
            state.Emit Nil |> ignore // VectorAppend itself pushes nothing; this expression still needs a value
        | BSuper(selfBinding, binding, _, method) ->
            this.CompileGetBinding selfBinding
            this.CompileGetBinding binding
            state.Emit(GetSuper(state.AddStringConstant method.Lexeme)) |> ignore

    member private this.CompileStmt(stmt: BoundStmt) : unit =
        lineOfStmt stmt |> Option.iter (fun line -> state.CurrentLine <- line)
        match stmt with
        | BExpressionStmt expr ->
            this.CompileExpr expr
            state.Emit Pop |> ignore
        | BBlock statements ->
            let depthBefore = state.StackDepth
            for s in statements do
                this.CompileStmt s
            let popCount = state.StackDepth - depthBefore
            if popCount > 0 then
                state.Emit(PopN popCount) |> ignore
        | BVarStmt(binding, _, initializer) ->
            match initializer with
            | Some init -> this.CompileExpr init
            | None -> state.Emit Undef |> ignore
            this.CompileDeclareBinding binding
        | BReturnStmt(_, value) ->
            match value with
            | Some expr -> this.CompileExpr expr
            | None -> state.Emit Nil |> ignore
            state.Emit Return |> ignore
        | BFunctionStmt(binding, decl) ->
            this.CompileFunctionValue(decl, isMethod = false)
            this.CompileDeclareBinding binding
        | BClassStmt(binding, name, superclass, mixins, properties, methods) ->
            this.CompileClass(binding, name, superclass, mixins, properties, methods)
        | BForStmt(initializer, condition, increment, body) -> this.CompileFor(initializer, condition, increment, body)

    member private this.CompileFunctionValue(decl: BoundFunctionDecl, isMethod: bool) : unit =
        let enclosing = state
        let initialDepth = List.length decl.Parameters + (if isMethod then 1 else 0)
        state <- FunctionState(initialDepth)
        state.CurrentFile <- currentFile

        for s in decl.Body do
            this.CompileStmt s
        // Every function implicitly returns `nil` if control falls off the
        // end without an explicit `return` -- matching poc's
        // `IqaloxFunction.call`, which returns `None` when no
        // `ReturnSignal` was ever raised.
        // With `elideEpilogue` (`docs/PLAN-0.3.md` Phase 7), a body whose
        // last statement is itself a `return` needs no fall-off-the-end
        // `nil; return` after it: nothing can jump past that final Return.
        let endsInReturn =
            match List.tryLast decl.Body with
            | Some(BReturnStmt _) -> true
            | _ -> false
        if not (elideEpilogue && endsInReturn) then
            state.Emit Nil |> ignore
            state.Emit Return |> ignore

        let chunk = state.ToChunk()
        state <- enclosing

        let proto =
            { Name = decl.Name.Lexeme
              Arity = List.length decl.Parameters
              LocalCount = decl.LocalCount
              Upvalues = decl.Upvalues
              Chunk = chunk }

        let functionIndex = state.AddFunctionConstant proto
        state.Emit(Closure(functionIndex, decl.Upvalues)) |> ignore

    member private this.CompileClass
        (
            binding: DeclaredBinding,
            name: Token,
            superclass: (VariableBinding * Token) option,
            mixins: (VariableBinding * Token) list,
            properties: BoundPropertyDecl list,
            methods: BoundFunctionDecl list
        ) : unit =
        state.Emit(Class(state.AddStringConstant name.Lexeme)) |> ignore
        this.CompileDeclareBinding binding

        superclass |> Option.iter (fun (superBinding, _) -> this.CompileGetBinding superBinding)

        // A temporary re-fetch of the class value, so `Method`/`Property*`/
        // `Mixin` can reliably peek "the class" at a fixed relative stack
        // position underneath each compiled method closure (or, for a
        // property declaration or mixin, with nothing else pushed at all
        // once its own operand is popped), regardless of what (if
        // anything) is sitting below it -- mirrors `clox`'s own approach.
        match binding with
        | DeclaredLocal slot -> this.CompileGetBinding(LocalBinding slot)
        | DeclaredGlobal globalName -> this.CompileGetBinding(GlobalBinding globalName)

        if superclass.IsSome then
            state.Emit Inherit |> ignore

        // `docs/PLAN-0.2.md` decision 12: each `with`-listed mixin is
        // pushed then immediately consumed by `Mixin`, unlike the
        // superclass (which stays on the stack as the synthetic `super`
        // local's backing slot) -- see `Bytecode.fs`'s doc comment on
        // `Mixin`. Order doesn't affect correctness (`Resolver.fs`
        // already guarantees no two composed sources share a member
        // name), only determinism.
        for mixinBinding, _ in mixins do
            this.CompileGetBinding mixinBinding
            state.Emit Mixin |> ignore

        // `docs/PLAN-0.2.md` decision 8: one of four opcodes per property,
        // matching exactly which of `var name`/`var name mut`/
        // `var name pub`/`var name pub mut` was declared. Pushes/pops
        // nothing -- see `Bytecode.fs`'s doc comment on `PropertyPrivate`.
        for propertyDecl in properties do
            let nameIndex = state.AddStringConstant propertyDecl.Name.Lexeme
            let opcode =
                match propertyDecl.IsPub, propertyDecl.IsMutable with
                | false, false -> PropertyPrivate nameIndex
                | false, true -> PropertyPrivateMut nameIndex
                | true, false -> PropertyPub nameIndex
                | true, true -> PropertyPubMut nameIndex
            state.Emit opcode |> ignore

        for methodDecl in methods do
            this.CompileFunctionValue(methodDecl, isMethod = true)
            let nameIndex = state.AddStringConstant methodDecl.Name.Lexeme
            // decision 11: `pub` methods are externally callable;
            // everything else (including `init`, which the VM's own
            // `callClass` always exempts from the check regardless of how
            // it was declared) is emitted as the private opcode.
            if methodDecl.IsPub then
                state.Emit(MethodPub nameIndex) |> ignore
            else
                state.Emit(Method nameIndex) |> ignore

        state.Emit Pop |> ignore // discards the temporary re-fetch, not the class itself

        // The synthetic `super` local's scope (Resolver.fs's `beginScope`/
        // `endScope` bracketing the superclass expression and this class's
        // own methods) ends exactly here -- and unlike an ordinary local,
        // this one was never given its own slot-cleanup by a `BBlock`,
        // since a class statement isn't compiled as one. Left un-popped,
        // a *second* class-with-a-superclass declared later in the same
        // script would have its own synthetic `super` local collide with
        // this one at the same slot (Resolver correctly reuses it once
        // this scope ends, but the runtime stack wouldn't have actually
        // freed it) -- any closure that already captured this `super` as
        // an upvalue keeps working regardless, since popping closes it
        // (see `vm/src/vm.cpp`'s `truncateStack`).
        if superclass.IsSome then
            state.Emit Pop |> ignore

    member private this.CompileFor
        (initializer: BoundStmt option, condition: BoundExpr option, increment: BoundExpr option, body: BoundStmt)
        : unit =
        let snapshot = state.StackDepth
        initializer |> Option.iter this.CompileStmt
        let afterInitDepth = state.StackDepth

        let loopStart = state.Here

        let exitJump =
            condition
            |> Option.map (fun cond ->
                this.CompileExpr cond
                let jump = state.Emit(JumpIfFalse -1)
                state.Emit Pop |> ignore // discard the truthy condition before the body runs
                jump)

        let previousLoop = state.Loop

        state.Loop <-
            Some
                { BreakTargetDepth = snapshot
                  ContinueTargetDepth = afterInitDepth
                  BreakJumps = ResizeArray()
                  ContinueJumps = ResizeArray() }

        this.CompileStmt body
        let loop = state.Loop.Value
        state.Loop <- previousLoop

        for continueJump in loop.ContinueJumps do
            state.PatchJump(continueJump, state.Here)

        increment
        |> Option.iter (fun incr ->
            this.CompileExpr incr
            state.Emit Pop |> ignore)

        state.Emit(Jump loopStart) |> ignore

        match exitJump with
        | Some jump ->
            state.PatchJump(jump, state.Here)
            // `JumpIfFalse` only peeks -- the falsy condition value is
            // still sitting here, even though the truthy/fallthrough path
            // (which the counter just followed, textually) already popped
            // its own copy. The counter needs an explicit reset to this
            // landing point's real depth before continuing to track it.
            state.StackDepth <- afterInitDepth + 1
            state.Emit Pop |> ignore
        | None -> ()

        if state.StackDepth > snapshot then
            state.Emit(PopN(state.StackDepth - snapshot)) |> ignore

        for breakJump in loop.BreakJumps do
            state.PatchJump(breakJump, state.Here)
        // Both the condition-false exit above and every `break` (which
        // unwinds inline at its own jump site -- see `CompileJumpOut`)
        // converge here at the loop's pre-initializer depth.
        state.StackDepth <- snapshot

    member this.FileNames = fileNames.ToArray()

    member this.CompileProgram(stmts: BoundStmt list) : Chunk =
        this.CompileSegments [ "", stmts ]

    /// Compiles a program made of several source files' statements,
    /// back to back in the order given (`docs/PLAN-0.3.md` Phases 4-5),
    /// stamping every instruction with the file its statement came from.
    member this.CompileSegments(segments: (string * BoundStmt list) list) : Chunk =
        for (file, stmts) in segments do
            let index =
                match fileNames.IndexOf file with
                | -1 ->
                    fileNames.Add file
                    fileNames.Count - 1
                | existing -> existing
            currentFile <- index
            state.CurrentFile <- index
            for stmt in stmts do
                this.CompileStmt stmt
        state.Emit Nil |> ignore
        state.Emit Return |> ignore
        state.ToChunk()

/// Compiles a fully-resolved program into its top-level `Chunk` -- treated,
/// per `Bytecode.fs`'s own doc comment, as an implicit zero-arity,
/// zero-upvalue function. Never throws; codegen errors so far are limited
/// to `break`/`continue` used outside any loop.
let compile (stmts: BoundStmt list) : Chunk * CodegenError list =
    let codegen = Codegen(false)
    let chunk = codegen.CompileProgram stmts
    chunk, codegen.Errors

/// Like `compile`, for a program assembled from several source files:
/// `segments` pairs each file's name with its resolved statements (in
/// execution order), and the result also carries the file table to hand
/// `Bytecode.writeWithFiles`. An instruction's file is whichever segment
/// its statement came from; the top-level epilogue is attributed to the
/// last one. With `elideEpilogue`, also drops the redundant `nil; return`
/// after a function body that already ends in `return`
/// (`docs/PLAN-0.3.md` Phase 7).
let compileSegmentsWith (elideEpilogue: bool) (segments: (string * BoundStmt list) list) : Chunk * string[] * CodegenError list =
    let codegen = Codegen(elideEpilogue)
    let chunk = codegen.CompileSegments segments
    chunk, codegen.FileNames, codegen.Errors

/// `compileSegmentsWith false`: no optimizations.
let compileSegments (segments: (string * BoundStmt list) list) : Chunk * string[] * CodegenError list =
    compileSegmentsWith false segments
