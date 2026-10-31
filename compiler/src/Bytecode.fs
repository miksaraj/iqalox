/// Bytecode format v3 adds a source-FILE dimension to v2's line table (see
/// "files" below): a multi-file program (`docs/PLAN-0.3.md` Phases 4-5)
/// compiles to ONE chunk whose code came from several source files, so a
/// bare `[line N]` was no longer enough to find the fault. Everything else
/// is unchanged from v2.
///
/// Bytecode format v2: adds a per-instruction source-line table (§"lines"
/// below) so a runtime fault can be blamed on a real `[line N]`, which v1
/// (Phases 1-9) had no room for at all -- a real diagnostics gap found
/// while writing `docs/LANGUAGE.md`'s Phase 10 entry (`docs/PLAN-0.1.md`).
/// Otherwise unchanged from v1, which itself superseded format v0's Phase 1
/// round-trip proof (`vm/src/bytecode.hpp`, `docs/PLAN-0.1.md` Phase 1) now
/// that `Codegen` (Phase 5) produces something worth defining a real format
/// for.
///
/// `Instruction`/`Chunk` are the *structured*, in-memory representation
/// `Codegen` builds and `Disassemble.fs` prints directly -- jump targets
/// are plain instruction-array indices here, not byte offsets, which is
/// what makes both codegen (no manual byte-offset patching) and the
/// disassembler (no re-decoding) simple. `write` is the only place that
/// cares about the actual on-disk byte layout, converting indices to byte
/// offsets (and per-instruction line numbers to per-byte ones -- see
/// "lines" below) in one pass right before serializing.
///
/// On-disk layout (all multi-byte integers little-endian, unchanged
/// framing style from v0/v1):
///
///   offset  size  field
///   0       4     magic: 'I' 'Q' 'B' 'C'
///   4       1     format version (0x03 for v3)
///   5       2     file_count (u16), then for each file a name (u32
///                 length + UTF-8 bytes) -- the source files the program
///                 was compiled from, referred to by index below. A name
///                 is the file's path relative to the entry file's
///                 directory ("math/vector.iqx"); an empty name means
///                 "unknown", and the VM then reports a bare `[line N]`.
///   ...     <chunk>   the top-level "script" chunk (see below), treated
///                     as an implicit zero-arity, zero-upvalue function
///
///   <chunk> serialization:
///     4     constant_count (u32)
///     for each constant:
///       1     tag (0x00 number, 0x01 string, 0x02 function)
///       if number:   8   f64
///       if string:   4+N u32 length, UTF-8 bytes
///       if function: name (u32 length + UTF-8 bytes), 1 arity (u8),
///                    2 local_count (u16), 2 upvalue_count (u16),
///                    upvalue_count * (1 from_enclosing_local (u8) +
///                    2 index (u16)), then a nested <chunk>
///     4     code_length (u32) -- byte length of the instruction stream
///     N     instruction bytes
///     N*2   lines -- one u16 per byte of the instruction stream above
///           (yes, one per *byte*, not per instruction -- redundant, but
///           it means the VM can look up `lines[ip]` directly with no
///           opcode-width decoding of its own at load time, the same
///           tradeoff `clox` itself makes)
///     N*2   files -- same shape as lines: one u16 per byte of the
///           instruction stream, an index into the file table above
///

///   Instruction encoding: 1 opcode byte, then a fixed operand width
///   determined entirely by the opcode (no variable-width operands except
///   CLOSURE's trailing per-upvalue list, whose own length is always
///   computable from its upvalue_count operand):
///     no operand                                  : 1 byte total
///     one u16 operand (index/slot/count)          : 3 bytes total
///     one u16 byte-offset operand (jumps)         : 3 bytes total
///     CLOSURE <u16 function index> <u16 upvalue
///       count> <upvalue count * 3 bytes>          : 5 + 3*N bytes
module Iqalox.Bytecode

open System.IO
open System.Text
open Iqalox.Bound

[<Literal>]
let FormatVersion = 3uy

type Instruction =
    | Constant of index: int
    | Nil
    | True
    | False
    | Undef
    | Pop
    | PopN of count: int
    | GetLocal of slot: int
    | SetLocal of slot: int
    | GetUpvalue of index: int
    | SetUpvalue of index: int
    | GetGlobal of nameIndex: int
    | SetGlobal of nameIndex: int
    | DefineGlobal of nameIndex: int
    | Add
    | Subtract
    | Multiply
    | Divide
    | Modulo
    | Power
    | Negate
    | Not
    | Equal
    | NotEqual
    | Greater
    | GreaterEqual
    | Less
    | LessEqual
    /// Target is an index into the *same* instruction array, resolved to
    /// a byte offset only when serialized.
    | Jump of target: int
    /// Peeks (doesn't pop) the top of stack; jumps if it's falsy.
    | JumpIfFalse of target: int
    /// Peeks (doesn't pop) the top of stack; jumps if it's anything other
    /// than `nil` -- the primitive `??`'s short-circuiting is built from.
    | JumpIfNotNil of target: int
    | BuildVector of count: int
    | Call of argCount: int
    | Closure of functionIndex: int * upvalues: UpvalueDescriptor list
    | Return
    | Class of nameIndex: int
    /// `docs/PLAN-0.2.md` decision 12's `with M1, M2` composition -- one
    /// per mixin, emitted right after `Inherit` (if any) and before the
    /// class's own `Property*`/`Method*` opcodes. Mirrors `Inherit`'s
    /// "peek the class, copy members in" mechanics, but pops its operand
    /// (the mixin value) rather than leaving it on the stack: unlike the
    /// superclass value, which must persist as the synthetic `super`
    /// local's backing slot for the rest of the class declaration, a
    /// mixin's value is never needed again once its members are copied
    /// in -- `super` does not chain through `with`-mixins under this
    /// version's simplified (non-C3) approximation of decision 12, open
    /// question 2. No operand of its own: like `Inherit`, it operates
    /// purely on the two values already on the stack.
    | Mixin
    /// A private (no `pub`) method declaration -- `docs/PLAN-0.2.md`
    /// decision 11. Only reachable internally (`self.method()`, or via
    /// `super`) once bound; `MethodPub` below is the externally-callable
    /// counterpart. Both just insert into `ObjClass.methods`, same as
    /// before this phase; `MethodPub` additionally records the name in
    /// `ObjClass.publicMethods`, which `GetProperty`'s external variant
    /// checks.
    | Method of nameIndex: int
    | MethodPub of nameIndex: int
    | Inherit
    /// External property/method access (`instance.x`) -- gated by the
    /// declared `pub` (and, for `SetProperty`, `mut`) modifiers.
    /// `GetPropertySelf`/`SetPropertySelf` below are `self.x`'s own
    /// variants, which skip that gating entirely (decision 10: any method
    /// of the class -- or, per the now-resolved open question 3, a
    /// subclass -- can always read/write `self.x` regardless of
    /// `pub`/`mut`). `Codegen.fs` picks the `*Self` opcode whenever the
    /// `Get`/`Set`'s own object expression is exactly `BSelf` -- purely a
    /// compile-time, syntactic choice, needing no new `Resolver.fs`
    /// bookkeeping.
    | GetProperty of nameIndex: int
    | GetPropertySelf of nameIndex: int
    | SetProperty of nameIndex: int
    | SetPropertySelf of nameIndex: int
    | GetSuper of nameIndex: int
    /// One of decision 8's four `var name [pub] [mut]` property
    /// declaration forms -- emitted once per `BoundPropertyDecl` inside
    /// `CompileClass`, exactly like `Method`/`MethodPub` are emitted once
    /// per method, and reusing the exact same "class value already on top
    /// of the stack" convention: pops nothing, pushes nothing, just
    /// records `nameIndex`'s `pub`/`mut` metadata into the peeked class's
    /// own `properties` map. Four separate opcodes (matching each of
    /// decision 8's four bullets one-for-one) rather than one opcode with
    /// boolean operands, so no new operand-width class is needed -- every
    /// one of these is still a plain "one u16 operand" instruction, same
    /// bucket as `Class`/`Method`/`GetProperty`.
    | PropertyPrivate of nameIndex: int
    | PropertyPrivateMut of nameIndex: int
    | PropertyPub of nameIndex: int
    | PropertyPubMut of nameIndex: int
    /// Indexed vector read/write (`v[i]`, `v[i] = x` -- `docs/PLAN-0.2.md`
    /// Phase 1). Unlike `GetProperty`/`SetProperty`, the "name" (here, the
    /// index) is a runtime value already on the stack, not a compile-time
    /// constant, so neither opcode takes an operand at all.
    | GetIndex
    | SetIndex
    /// Pops a vector, pushes its element count as a number -- a runtime
    /// type error if the popped value isn't a vector. `docs/PLAN-0.2.md`
    /// Phase 3: both `Cons` and list comprehensions need to loop a
    /// runtime-determined number of times (the source vector's length
    /// isn't known at compile time), which a fixed-operand `BuildVector`
    /// can't express. Only ever emitted from inside the synthetic
    /// closures `Resolver.fs` desugars `Cons`/`ListComprehension` into
    /// (via `Bound.BVectorLengthInternal`) -- no surface syntax reaches it
    /// directly.
    | VectorLength
    /// Pops a value, then pops a vector, appends the value to the
    /// vector's own element list, and pushes nothing back. Since a
    /// vector is a heap reference (`Obj*`), any other copy of the same
    /// pointer already sitting elsewhere on the stack (e.g. an
    /// accumulator local slot) observes the mutation immediately -- no
    /// need to re-store anything after appending. Same synthetic-closure-
    /// only reach as `VectorLength` (`Bound.BVectorAppendInternal`).
    | VectorAppend
    /// Pops a source vector, then pops a target vector, and appends every
    /// element of the source onto the target's own element list (a
    /// user-facing runtime type error if the source isn't a vector --
    /// unlike `VectorAppend`, this one's reachable directly from surface
    /// syntax: `docs/PLAN-0.2.md` Phase 4's vector-literal spread,
    /// `[...a, ...b]`). Pushes the target back, unlike `VectorAppend` --
    /// `Codegen.fs`'s spread-flattening `BVector` compilation chains
    /// several of these (and `BuildVector`) purely on the stack, with no
    /// hidden local slots at all, sidestepping the exact mid-expression
    /// stack-corruption bug Phase 3's `Cons`/`ListComprehension` hit and
    /// fixed by moving to an isolated closure frame instead -- spread
    /// never needs that isolation since it never declares a *named*
    /// local, only ever chains ordinary stack-relative pushes/pops.
    | VectorExtend
    /// Pops stop, then start, then the vector (`docs/PLAN-0.3.md`
    /// decision 3), pushes a new vector -- both bounds are numbers or
    /// `nil` (an omitted bound, `v[:3]`/`v[2:]`/`v[:]`; `Codegen.fs`
    /// emits a plain `Nil` for whichever side is missing rather than
    /// giving this opcode its own optional-operand encoding). End-
    /// inclusive, negative-from-the-end, always a fresh copy, out-of-
    /// range/`start > stop` bounds clamp to an empty result rather than
    /// erroring -- see `vm/src/vm.cpp`'s handler for the exact resolution
    /// rules.
    | GetSlice

type Constant =
    | NumberConstant of float
    | StringConstant of string
    | FunctionConstant of FunctionProto

and FunctionProto =
    { Name: string
      Arity: int
      LocalCount: int
      Upvalues: UpvalueDescriptor list
      Chunk: Chunk }

/// `Lines.[i]` is the source line `Codegen` had "in view" when it emitted
/// `Code.[i]` -- parallel to `Code`, one entry per structured instruction
/// (not one per serialized byte; `write` expands it to the latter, since
/// that's what a byte-offset-indexed VM needs -- see its own doc comment).
///
/// `Files.[i]` is the index (into the program's file table, written once
/// at the top of the file) of the source file `Code.[i]` came from.
and Chunk =
    { Constants: Constant[]
      Code: Instruction[]
      Lines: int[]
      Files: int[] }

let private opcodeOf =
    function
    | Constant _ -> 0x01uy
    | Nil -> 0x02uy
    | True -> 0x03uy
    | False -> 0x04uy
    | Undef -> 0x05uy
    | Pop -> 0x06uy
    | PopN _ -> 0x07uy
    | GetLocal _ -> 0x08uy
    | SetLocal _ -> 0x09uy
    | GetUpvalue _ -> 0x0Auy
    | SetUpvalue _ -> 0x0Buy
    | GetGlobal _ -> 0x0Cuy
    | SetGlobal _ -> 0x0Duy
    | DefineGlobal _ -> 0x0Euy
    | Add -> 0x0Fuy
    | Subtract -> 0x10uy
    | Multiply -> 0x11uy
    | Divide -> 0x12uy
    | Modulo -> 0x13uy
    | Power -> 0x14uy
    | Negate -> 0x15uy
    | Not -> 0x16uy
    | Equal -> 0x17uy
    | NotEqual -> 0x18uy
    | Greater -> 0x19uy
    | GreaterEqual -> 0x1Auy
    | Less -> 0x1Buy
    | LessEqual -> 0x1Cuy
    | Jump _ -> 0x1Duy
    | JumpIfFalse _ -> 0x1Euy
    | JumpIfNotNil _ -> 0x1Fuy
    | BuildVector _ -> 0x20uy
    | Call _ -> 0x21uy
    | Closure _ -> 0x22uy
    | Return -> 0x23uy
    | Class _ -> 0x24uy
    | Method _ -> 0x25uy
    | Inherit -> 0x26uy
    | GetProperty _ -> 0x27uy
    | SetProperty _ -> 0x28uy
    | GetSuper _ -> 0x29uy
    | GetIndex -> 0x2Auy
    | SetIndex -> 0x2Buy
    | VectorLength -> 0x2Cuy
    | VectorAppend -> 0x2Duy
    | VectorExtend -> 0x2Euy
    | MethodPub _ -> 0x2Fuy
    | GetPropertySelf _ -> 0x30uy
    | SetPropertySelf _ -> 0x31uy
    | PropertyPrivate _ -> 0x32uy
    | PropertyPrivateMut _ -> 0x33uy
    | PropertyPub _ -> 0x34uy
    | PropertyPubMut _ -> 0x35uy
    | Mixin -> 0x36uy
    | GetSlice -> 0x37uy

/// Serialized byte length of one instruction -- see the module doc
/// comment's "Instruction encoding" table.
let instructionByteLength (instruction: Instruction) : int =
    match instruction with
    | Nil
    | True
    | False
    | Undef
    | Pop
    | Add
    | Subtract
    | Multiply
    | Divide
    | Modulo
    | Power
    | Negate
    | Not
    | Equal
    | NotEqual
    | Greater
    | GreaterEqual
    | Less
    | LessEqual
    | Return
    | Inherit
    | Mixin
    | GetIndex
    | SetIndex
    | VectorLength
    | VectorAppend
    | VectorExtend
    | GetSlice -> 1
    | Closure(_, upvalues) -> 1 + 2 + 2 + (3 * List.length upvalues)
    | _ -> 3

/// Byte offset of the start of each instruction, indexed the same as
/// `code` -- the bridge between codegen's index-based jump targets and
/// the file format's byte-offset ones.
let private byteOffsets (code: Instruction[]) : int[] =
    let offsets = Array.zeroCreate code.Length
    let mutable offset = 0
    for i in 0 .. code.Length - 1 do
        offsets.[i] <- offset
        offset <- offset + instructionByteLength code.[i]
    offsets

let rec private writeChunk (writer: BinaryWriter) (chunk: Chunk) : unit =
    writer.Write(uint32 chunk.Constants.Length)
    for constant in chunk.Constants do
        match constant with
        | NumberConstant n ->
            writer.Write(0x00uy)
            writer.Write(n: float)
        | StringConstant s ->
            writer.Write(0x01uy)
            let bytes = Encoding.UTF8.GetBytes(s: string)
            writer.Write(uint32 bytes.Length)
            writer.Write(bytes)
        | FunctionConstant proto ->
            writer.Write(0x02uy)
            let nameBytes = Encoding.UTF8.GetBytes(proto.Name)
            writer.Write(uint32 nameBytes.Length)
            writer.Write(nameBytes)
            writer.Write(byte proto.Arity)
            writer.Write(uint16 proto.LocalCount)
            writer.Write(uint16 (List.length proto.Upvalues))
            for upvalue in proto.Upvalues do
                writer.Write(if upvalue.FromEnclosingLocal then 1uy else 0uy)
                writer.Write(uint16 upvalue.Index)
            writeChunk writer proto.Chunk

    let offsets = byteOffsets chunk.Code

    let targetOffset (index: int) =
        if index < offsets.Length then offsets.[index]
        // A jump targeting one-past-the-end (the common case for "jump to
        // just after the last instruction") has no array entry to read --
        // it's the total code length instead.
        else offsets.[offsets.Length - 1] + instructionByteLength chunk.Code.[chunk.Code.Length - 1]

    use codeStream = new MemoryStream()
    use codeWriter = new BinaryWriter(codeStream)
    for instruction in chunk.Code do
        codeWriter.Write(opcodeOf instruction)
        match instruction with
        | Nil
        | True
        | False
        | Undef
        | Pop
        | Add
        | Subtract
        | Multiply
        | Divide
        | Modulo
        | Power
        | Negate
        | Not
        | Equal
        | NotEqual
        | Greater
        | GreaterEqual
        | Less
        | LessEqual
        | Return
        | Inherit
        | Mixin
        | GetIndex
        | SetIndex
        | VectorLength
        | VectorAppend
        | VectorExtend
        | GetSlice -> ()
        | Constant i
        | PopN i
        | GetLocal i
        | SetLocal i
        | GetUpvalue i
        | SetUpvalue i
        | GetGlobal i
        | SetGlobal i
        | DefineGlobal i
        | BuildVector i
        | Call i
        | Class i
        | Method i
        | MethodPub i
        | GetProperty i
        | GetPropertySelf i
        | SetProperty i
        | SetPropertySelf i
        | GetSuper i
        | PropertyPrivate i
        | PropertyPrivateMut i
        | PropertyPub i
        | PropertyPubMut i -> codeWriter.Write(uint16 i)
        | Jump target
        | JumpIfFalse target
        | JumpIfNotNil target -> codeWriter.Write(uint16 (targetOffset target))
        | Closure(functionIndex, upvalues) ->
            codeWriter.Write(uint16 functionIndex)
            codeWriter.Write(uint16 (List.length upvalues))
            for upvalue in upvalues do
                codeWriter.Write(if upvalue.FromEnclosingLocal then 1uy else 0uy)
                codeWriter.Write(uint16 upvalue.Index)

    let codeBytes = codeStream.ToArray()
    writer.Write(uint32 codeBytes.Length)
    writer.Write(codeBytes)

    // One u16 line number per byte of `codeBytes`, expanded from
    // `chunk.Lines`' one-entry-per-*instruction* form by repeating each
    // instruction's line across every byte of its own serialized length.
    for i in 0 .. chunk.Code.Length - 1 do
        let byteLength = instructionByteLength chunk.Code.[i]
        for _ in 1 .. byteLength do
            writer.Write(uint16 chunk.Lines.[i])

    // Same expansion for the per-instruction file index.
    for i in 0 .. chunk.Code.Length - 1 do
        let byteLength = instructionByteLength chunk.Code.[i]
        for _ in 1 .. byteLength do
            writer.Write(uint16 chunk.Files.[i])

/// Serializes `chunk` (the top-level script) to `path` in bytecode format
/// v3, with `fileNames` as the program's source-file table.
let writeWithFiles (fileNames: string[]) (chunk: Chunk) (path: string) : unit =
    use stream = File.Open(path, FileMode.Create, FileAccess.Write)
    use writer = new BinaryWriter(stream)
    writer.Write(Encoding.ASCII.GetBytes("IQBC"))
    writer.Write(FormatVersion)
    writer.Write(uint16 fileNames.Length)
    for name in fileNames do
        let bytes = Encoding.UTF8.GetBytes name
        writer.Write(uint32 bytes.Length)
        writer.Write(bytes)
    writeChunk writer chunk

/// `writeWithFiles` with an empty file table (every error then reports a
/// bare `[line N]`).
let write (chunk: Chunk) (path: string) : unit = writeWithFiles [||] chunk path
