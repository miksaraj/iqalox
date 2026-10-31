module BytecodeTests

open System
open System.IO
open Xunit
open Iqalox.Bound
open Iqalox.Bytecode

let private tempPath () =
    Path.Combine(Path.GetTempPath(), $"iqaloxc_test_{Guid.NewGuid()}.iqbc")

let private writeAndRead (chunk: Chunk) : byte[] =
    let path = tempPath ()
    try
        write chunk path
        File.ReadAllBytes path
    finally
        File.Delete path

let private pushU32 (bytes: ResizeArray<byte>) (value: uint32) = bytes.AddRange(BitConverter.GetBytes(value))
let private pushU16 (bytes: ResizeArray<byte>) (value: uint16) = bytes.AddRange(BitConverter.GetBytes(value))

/// One u16 line entry per byte of `code`, matching how `write` expands
/// `chunk.Lines` (one entry per instruction) into the on-disk, one-entry-
/// per-byte form -- see `Bytecode.fs`'s own doc comment.
let private pushLines (bytes: ResizeArray<byte>) (code: Instruction[]) (lines: int[]) =
    for i in 0 .. code.Length - 1 do
        for _ in 1 .. instructionByteLength code.[i] do
            pushU16 bytes (uint16 lines.[i])
    // ...followed by the equally per-byte file indices (format v3), which
    // every test chunk here leaves at file 0.
    for i in 0 .. code.Length - 1 do
        for _ in 1 .. instructionByteLength code.[i] do
            pushU16 bytes 0us

[<Fact>]
let ``instructionByteLength is 1 for every no-operand instruction`` () =
    Assert.Equal(1, instructionByteLength Nil)
    Assert.Equal(1, instructionByteLength Pop)
    Assert.Equal(1, instructionByteLength Add)
    Assert.Equal(1, instructionByteLength Return)
    Assert.Equal(1, instructionByteLength Inherit)

[<Fact>]
let ``instructionByteLength is 3 for every single u16-operand instruction`` () =
    Assert.Equal(3, instructionByteLength (Constant 0))
    Assert.Equal(3, instructionByteLength (GetLocal 1))
    Assert.Equal(3, instructionByteLength (Jump 5))
    Assert.Equal(3, instructionByteLength (Call 2))

[<Fact>]
let ``instructionByteLength for Closure grows by 3 bytes per upvalue`` () =
    Assert.Equal(5, instructionByteLength (Closure(0, [])))
    Assert.Equal(8, instructionByteLength (Closure(0, [ { FromEnclosingLocal = true; Index = 0 } ])))

    Assert.Equal(
        11,
        instructionByteLength (
            Closure(0, [ { FromEnclosingLocal = true; Index = 0 }; { FromEnclosingLocal = false; Index = 1 } ])
        )
    )

[<Fact>]
let ``an empty chunk writes just the header and zero counts`` () =
    let bytes = writeAndRead { Constants = [||]; Code = [||]; Lines = [||]; Files = [||] }

    let expected = ResizeArray<byte>()
    expected.AddRange("IQBC"B)
    expected.Add(FormatVersion)
    pushU16 expected 0us // file_count (format v3)
    pushU32 expected 0u // constant_count
    pushU32 expected 0u // code_length
    // no code bytes, so no lines bytes either

    Assert.Equal<byte[]>(expected.ToArray(), bytes)

[<Fact>]
let ``number and string constants encode with their tag, then payload`` () =
    let code = [| Constant 0; Constant 1; Return |]
    let lines = [| 10; 10; 11 |]

    let bytes =
        writeAndRead
            { Constants = [| NumberConstant 1.5; StringConstant "hi" |]
              Code = code
              Lines = lines; Files = Array.zeroCreate lines.Length }

    let expected = ResizeArray<byte>()
    expected.AddRange("IQBC"B)
    expected.Add(FormatVersion)
    pushU16 expected 0us // file_count (format v3)
    pushU32 expected 2u // constant_count
    expected.Add(0x00uy) // number tag
    expected.AddRange(BitConverter.GetBytes(1.5))
    expected.Add(0x01uy) // string tag
    pushU32 expected 2u
    expected.AddRange("hi"B)

    let codeBytes = ResizeArray<byte>()
    codeBytes.Add(0x01uy) // CONSTANT
    pushU16 codeBytes 0us
    codeBytes.Add(0x01uy) // CONSTANT
    pushU16 codeBytes 1us
    codeBytes.Add(0x23uy) // RETURN

    pushU32 expected (uint32 codeBytes.Count)
    expected.AddRange codeBytes
    pushLines expected code lines

    Assert.Equal<byte[]>(expected.ToArray(), bytes)

[<Fact>]
let ``jump targets serialize as byte offsets, not instruction-array indices`` () =
    // index 0: Nil (offset 0, length 1); index 1: JumpIfFalse -> index 2
    // (offset 1, length 3); index 2: Pop (offset 4, length 1) -- so the
    // jump's target index (2) must serialize as byte offset 4, not 2.
    let code = [| Nil; JumpIfFalse 2; Pop |]
    let lines = [| 1; 1; 1 |]
    let bytes = writeAndRead { Constants = [||]; Code = code; Lines = lines; Files = Array.zeroCreate lines.Length }

    let codeBytes = ResizeArray<byte>()
    codeBytes.Add(0x02uy) // NIL
    codeBytes.Add(0x1Euy) // JUMP_IF_FALSE
    pushU16 codeBytes 4us
    codeBytes.Add(0x06uy) // POP

    let expected = ResizeArray<byte>()
    expected.AddRange("IQBC"B)
    expected.Add(FormatVersion)
    pushU16 expected 0us // file_count (format v3)
    pushU32 expected 0u
    pushU32 expected (uint32 codeBytes.Count)
    expected.AddRange codeBytes
    pushLines expected code lines

    Assert.Equal<byte[]>(expected.ToArray(), bytes)

[<Fact>]
let ``a jump targeting one past the end of the code uses the total code length`` () =
    // index 0: Jump -> index 1 (one past the end); index 1 doesn't exist,
    // so the target must be the total serialized byte length instead.
    let code = [| Jump 1 |]
    let lines = [| 1 |]
    let bytes = writeAndRead { Constants = [||]; Code = code; Lines = lines; Files = Array.zeroCreate lines.Length }

    let codeBytes = ResizeArray<byte>()
    codeBytes.Add(0x1Duy) // JUMP
    pushU16 codeBytes 3us // the whole (only) instruction is 3 bytes long

    let expected = ResizeArray<byte>()
    expected.AddRange("IQBC"B)
    expected.Add(FormatVersion)
    pushU16 expected 0us // file_count (format v3)
    pushU32 expected 0u
    pushU32 expected (uint32 codeBytes.Count)
    expected.AddRange codeBytes
    pushLines expected code lines

    Assert.Equal<byte[]>(expected.ToArray(), bytes)

[<Fact>]
let ``each instruction's line is repeated once per byte of its own serialized length`` () =
    // Regression test for the lines-expansion step itself: a 3-byte
    // instruction (Constant) must repeat its line 3 times, not once --
    // otherwise the VM's byte-offset-indexed lookup would misalign against
    // every instruction after the first one wider than a single byte.
    let code = [| Constant 0; Pop |]
    let lines = [| 7; 9 |]
    let bytes = writeAndRead { Constants = [| NumberConstant 1.0 |]; Code = code; Lines = lines; Files = Array.zeroCreate lines.Length }

    // Skip past the header/constant section to the code_length + code +
    // lines tail, which is all this test cares about.
    let codeBytes = ResizeArray<byte>()
    codeBytes.Add(0x01uy) // CONSTANT
    pushU16 codeBytes 0us
    codeBytes.Add(0x06uy) // POP

    let expectedLines = ResizeArray<byte>()
    pushLines expectedLines code lines // 7, 7, 7, 9 (Constant is 3 bytes, Pop is 1)

    let tail = bytes.[bytes.Length - codeBytes.Count - expectedLines.Count ..]
    let expectedTail = ResizeArray<byte>()
    expectedTail.AddRange codeBytes
    expectedTail.AddRange expectedLines

    Assert.Equal<byte[]>(expectedTail.ToArray(), tail)

[<Fact>]
let ``Closure encodes its function index, upvalue count, then each upvalue`` () =
    let code =
        [| Closure(0, [ { FromEnclosingLocal = true; Index = 1 }; { FromEnclosingLocal = false; Index = 0 } ]) |]
    let lines = [| 1 |]

    let bytes =
        writeAndRead
            { Constants =
                [| FunctionConstant
                       { Name = "f"
                         Arity = 0
                         LocalCount = 0
                         Upvalues = []
                         Chunk = { Constants = [||]; Code = [||]; Lines = [||]; Files = [||] } } |]
              Code = code
              Lines = lines; Files = Array.zeroCreate lines.Length }

    let codeBytes = ResizeArray<byte>()
    codeBytes.Add(0x22uy) // CLOSURE
    pushU16 codeBytes 0us // function index
    pushU16 codeBytes 2us // upvalue count
    codeBytes.Add(1uy) // from enclosing local
    pushU16 codeBytes 1us
    codeBytes.Add(0uy) // from enclosing upvalue
    pushU16 codeBytes 0us

    let expectedLines = ResizeArray<byte>()
    pushLines expectedLines code lines

    // Only asserting the tail (the code section + its lines) here -- the
    // constant section's own FunctionConstant layout is covered separately
    // below.
    let tail = bytes.[bytes.Length - codeBytes.Count - expectedLines.Count ..]
    let expectedTail = ResizeArray<byte>()
    expectedTail.AddRange codeBytes
    expectedTail.AddRange expectedLines
    Assert.Equal<byte[]>(expectedTail.ToArray(), tail)

[<Fact>]
let ``a FunctionConstant serializes name, arity, local count, upvalues, then its nested chunk`` () =
    let proto =
        { Name = "f"
          Arity = 2
          LocalCount = 3
          Upvalues = [ { FromEnclosingLocal = true; Index = 0 } ]
          Chunk = { Constants = [||]; Code = [| Return |]; Lines = [| 3 |]; Files = [| 0 |] } }

    let bytes = writeAndRead { Constants = [| FunctionConstant proto |]; Code = [||]; Lines = [||]; Files = [||] }

    let expected = ResizeArray<byte>()
    expected.AddRange("IQBC"B)
    expected.Add(FormatVersion)
    pushU16 expected 0us // file_count (format v3)
    pushU32 expected 1u // constant_count
    expected.Add(0x02uy) // function tag
    pushU32 expected 1u // name length
    expected.AddRange("f"B)
    expected.Add(2uy) // arity
    pushU16 expected 3us // local_count
    pushU16 expected 1us // upvalue_count
    expected.Add(1uy) // from enclosing local
    pushU16 expected 0us
    // nested chunk: 0 constants, then its own code, then its own lines
    pushU32 expected 0u
    pushU32 expected 1u // code_length (RETURN is 1 byte)
    expected.Add(0x23uy) // RETURN
    pushU16 expected 3us // RETURN's line
    pushU16 expected 0us // RETURN's file

    pushU32 expected 0u // top-level code_length
    // top-level code is empty, so no top-level lines bytes

    Assert.Equal<byte[]>(expected.ToArray(), bytes)

[<Fact>]
let ``writeWithFiles puts the file table right after the version byte`` () =
    let path = tempPath ()
    try
        writeWithFiles [| "main.iqx"; "math/vector.iqx" |] { Constants = [||]; Code = [||]; Lines = [||]; Files = [||] } path
        let bytes = File.ReadAllBytes path
        let expected = ResizeArray<byte>()
        expected.AddRange("IQBC"B)
        expected.Add(FormatVersion)
        pushU16 expected 2us // file_count
        for name in [ "main.iqx"; "math/vector.iqx" ] do
            let nameBytes = Text.Encoding.UTF8.GetBytes name
            pushU32 expected (uint32 nameBytes.Length)
            expected.AddRange nameBytes
        pushU32 expected 0u // constant_count
        pushU32 expected 0u // code_length
        Assert.Equal<byte[]>(expected.ToArray(), bytes)
    finally
        File.Delete path

[<Fact>]
let ``each instruction's file index is written once per byte after the lines`` () =
    let code = [| Constant 0; Pop |]
    let bytes =
        writeAndRead { Constants = [| NumberConstant 1.0 |]; Code = code; Lines = [| 5; 6 |]; Files = [| 0; 2 |] }
    // tail: 4 bytes of code, 4 u16 lines (5,5,5,6), 4 u16 files (0,0,0,2)
    let files = bytes.[bytes.Length - 8 ..]
    Assert.Equal<byte[]>([| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 2uy; 0uy |], files)
