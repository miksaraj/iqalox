/// Human-readable listing of a `Bytecode.Chunk` -- the primary way
/// `Codegen`'s tests verify output, per `docs/PLAN-0.1.md`'s Phase 1
/// testing-strategy note ("the frontend's tests can assert against the
/// bytecode it produces... no C++ VM needed yet"). Operates on the
/// structured, index-based `Instruction` form directly, not the
/// serialized byte format `Bytecode.write` produces.
module Iqalox.Disassembler

open System.Text
open Iqalox.Bound
open Iqalox.Bytecode

let private describeConstant (constant: Constant) : string =
    match constant with
    | IntConstant n -> string n
    // Always with a decimal point, so a float constant is never mistaken
    // for an int one.
    | FloatConstant n ->
        let text = n.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if System.Double.IsFinite n && not (text.Contains '.' || text.Contains 'E') then text + ".0" else text
    | StringConstant s -> $"\"{s}\""
    | FunctionConstant proto -> $"<fun {proto.Name}>"

let private describeUpvalues (upvalues: UpvalueDescriptor list) : string =
    upvalues
    |> List.map (fun u -> if u.FromEnclosingLocal then $"local {u.Index}" else $"upvalue {u.Index}")
    |> String.concat ", "

let disassembleInstruction (chunk: Chunk) (index: int) : string =
    let instr = chunk.Code.[index]
    let at (i: int) = sprintf "%04d" i

    let simple name = $"{at index} {name}"
    let withIndex name (i: int) = $"{at index} {name} {i}"
    let withConstant name (i: int) = $"{at index} {name} {i} ({describeConstant chunk.Constants.[i]})"
    let withJump name (target: int) = $"{at index} {name} -> {at target}"

    match instr with
    | Constant i -> withConstant "CONSTANT" i
    | Nil -> simple "NIL"
    | True -> simple "TRUE"
    | False -> simple "FALSE"
    | Undef -> simple "UNDEF"
    | Pop -> simple "POP"
    | PopN n -> withIndex "POPN" n
    | GetLocal slot -> withIndex "GET_LOCAL" slot
    | SetLocal slot -> withIndex "SET_LOCAL" slot
    | GetUpvalue i -> withIndex "GET_UPVALUE" i
    | SetUpvalue i -> withIndex "SET_UPVALUE" i
    | GetGlobal i -> withConstant "GET_GLOBAL" i
    | SetGlobal i -> withConstant "SET_GLOBAL" i
    | DefineGlobal i -> withConstant "DEFINE_GLOBAL" i
    | Add -> simple "ADD"
    | Subtract -> simple "SUBTRACT"
    | Multiply -> simple "MULTIPLY"
    | Divide -> simple "DIVIDE"
    | Modulo -> simple "MODULO"
    | Power -> simple "POWER"
    | Negate -> simple "NEGATE"
    | Not -> simple "NOT"
    | Equal -> simple "EQUAL"
    | NotEqual -> simple "NOT_EQUAL"
    | Greater -> simple "GREATER"
    | GreaterEqual -> simple "GREATER_EQUAL"
    | Less -> simple "LESS"
    | LessEqual -> simple "LESS_EQUAL"
    | Jump target -> withJump "JUMP" target
    | JumpIfFalse target -> withJump "JUMP_IF_FALSE" target
    | JumpIfNotNil target -> withJump "JUMP_IF_NOT_NIL" target
    | BuildVector count -> withIndex "BUILD_VECTOR" count
    | Call argCount -> withIndex "CALL" argCount
    | Closure(functionIndex, upvalues) ->
        let desc = describeConstant chunk.Constants.[functionIndex]
        let upvalueDesc = if upvalues.IsEmpty then "" else $" [{describeUpvalues upvalues}]"
        $"{at index} CLOSURE {functionIndex} ({desc}){upvalueDesc}"
    | Return -> simple "RETURN"
    | Class i -> withConstant "CLASS" i
    | Method i -> withConstant "METHOD" i
    | MethodPub i -> withConstant "METHOD_PUB" i
    | Inherit -> simple "INHERIT"
    | Mixin -> simple "MIXIN"
    | GetProperty i -> withConstant "GET_PROPERTY" i
    | GetPropertySelf i -> withConstant "GET_PROPERTY_SELF" i
    | SetProperty i -> withConstant "SET_PROPERTY" i
    | SetPropertySelf i -> withConstant "SET_PROPERTY_SELF" i
    | GetSuper i -> withConstant "GET_SUPER" i
    | PropertyPrivate i -> withConstant "PROPERTY_PRIVATE" i
    | PropertyPrivateMut i -> withConstant "PROPERTY_PRIVATE_MUT" i
    | PropertyPub i -> withConstant "PROPERTY_PUB" i
    | PropertyPubMut i -> withConstant "PROPERTY_PUB_MUT" i
    | GetIndex -> simple "GET_INDEX"
    | SetIndex -> simple "SET_INDEX"
    | VectorLength -> simple "VECTOR_LENGTH"
    | VectorAppend -> simple "VECTOR_APPEND"
    | VectorExtend -> simple "VECTOR_EXTEND"
    | GetSlice -> simple "GET_SLICE"

let rec disassembleChunk (name: string) (chunk: Chunk) : string =
    let sb = StringBuilder()
    sb.AppendLine($"== {name} ==") |> ignore

    for i in 0 .. chunk.Code.Length - 1 do
        sb.AppendLine(disassembleInstruction chunk i) |> ignore

    for constant in chunk.Constants do
        match constant with
        | FunctionConstant proto ->
            sb.AppendLine() |> ignore
            let protoName = if proto.Name = "" then "script" else proto.Name
            sb.Append(disassembleChunk protoName proto.Chunk) |> ignore
        | _ -> ()

    sb.ToString()
