module OptimizerTests

open Xunit
open Iqalox.Scanner
open Iqalox.Parser
open Iqalox.Resolver
open Iqalox.Bytecode
open Iqalox.Codegen

let private compileWith (optimize: bool) (source: string) : Chunk =
    let source = if source.EndsWith "\n" then source else source + "\n"
    let tokens, _ = scanTokens source
    let stmts, parseErrors = parse tokens
    Assert.Empty parseErrors
    let bound, resolveErrors, _ = resolve stmts
    Assert.Empty resolveErrors
    let segments = [ "", bound ]
    let segments = if optimize then Iqalox.Optimizer.optimize segments else segments
    let chunk, _, errors = compileSegmentsWith optimize segments
    Assert.Empty errors
    chunk

let private opt = compileWith true
let private plain = compileWith false

let private functionNamed (name: string) (chunk: Chunk) : FunctionProto =
    chunk.Constants
    |> Array.pick (function
        | FunctionConstant p when p.Name = name -> Some p
        | _ -> None)

let private has (pred: Instruction -> bool) (chunk: Chunk) = chunk.Code |> Array.exists pred

[<Fact>]
let ``arithmetic on literals is folded to one constant`` () =
    let chunk = opt "var x mut = 1 + 2 * 3\nx = x"
    Assert.Contains(IntConstant 7I, chunk.Constants)
    Assert.False(has ((=) Add) chunk)
    Assert.False(has ((=) Multiply) chunk)
    Assert.True(has ((=) Add) (plain "var x mut = 1 + 2 * 3\nx = x"))

[<Fact>]
let ``division by a literal zero is never folded, so it still raises at runtime`` () =
    Assert.True(has ((=) Divide) (opt "var x mut = 1 / 0\nx = x"))

[<Fact>]
let ``modulo and power are never folded`` () =
    let chunk = opt "var x mut = 7 % 3\nvar y mut = 2 ^ 3\nx = x\ny = y"
    Assert.True(has ((=) Modulo) chunk)
    Assert.True(has ((=) Power) chunk)

[<Fact>]
let ``a float result that overflows to infinity is left for the vm`` () =
    Assert.True(has ((=) Multiply) (opt "var a = 1000000000000.0\nvar x mut = a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a\nx = x"))

[<Fact>]
let ``the same product of integers folds exactly instead`` () =
    let chunk = opt "var a = 1000000000000\nvar x mut = a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a\nx = x"
    Assert.False(has ((=) Multiply) chunk)
    Assert.Contains(IntConstant(bigint.Pow(1000000000000I, 26)), chunk.Constants)

[<Fact>]
let ``integer negative zero is just zero`` () =
    let chunk = opt "var a mut = 0\nvar b mut = -0\na = b"
    Assert.Equal(1, chunk.Constants |> Array.filter (fun c -> c = IntConstant 0I) |> Array.length)

[<Fact>]
let ``float negative zero keeps its own constant slot`` () =
    let chunk = opt "var a mut = 0.0\nvar b mut = -0.0\na = b"
    let zeros =
        chunk.Constants
        |> Array.choose (function
            | FloatConstant n when n = 0.0 -> Some(System.BitConverter.DoubleToInt64Bits n)
            | _ -> None)
    Assert.Equal(2, zeros.Length)
    Assert.NotEqual(zeros.[0], zeros.[1])

[<Fact>]
let ``comparisons and equality of literals fold`` () =
    let chunk = opt "var a mut = 1 < 2\nvar b mut = \"x\" == \"y\"\na = a\nb = b"
    Assert.False(has ((=) Less) chunk)
    Assert.False(has ((=) Equal) chunk)
    Assert.True(has ((=) True) chunk)
    Assert.True(has ((=) False) chunk)

[<Fact>]
let ``an unassigned literal global is propagated into later code`` () =
    let chunk = opt "var a = 5\nvar b mut = a + 1\nb = b"
    Assert.Contains(IntConstant 6I, chunk.Constants)
    Assert.False(has ((=) Add) chunk)

[<Fact>]
let ``propagation chains through folded initializers`` () =
    let chunk = opt "var a = 2\nvar b = a * 3\nvar c mut = b + 1\nc = c"
    Assert.Contains(IntConstant 7I, chunk.Constants)

[<Fact>]
let ``a global that is assigned anywhere is never propagated`` () =
    let chunk = opt "var a mut = 5\nfun bump() {\n    a = 6\n}\nvar b mut = a + 1\nb = b"
    Assert.True(has ((=) Add) chunk)

[<Fact>]
let ``a global that is incremented is never propagated`` () =
    let chunk = opt "var a mut = 5\n++a\nvar b mut = a + 1\nb = b"
    Assert.True(has ((=) Add) chunk)

[<Fact>]
let ``a read that textually precedes the declaration is left alone`` () =
    let chunk = opt "fun f() {\n    return a\n}\nvar a = 5\nf()"
    let f = functionNamed "f" chunk
    Assert.True(f.Chunk.Code |> Array.exists (function GetGlobal _ -> true | _ -> false))

[<Fact>]
let ``a function declared after the constant reads it as a literal`` () =
    let chunk = opt "var a = 5\nfun f() {\n    return a\n}\nf()"
    let f = functionNamed "f" chunk
    Assert.False(f.Chunk.Code |> Array.exists (function GetGlobal _ -> true | _ -> false))

[<Fact>]
let ``a ternary on a constant condition keeps only the live branch`` () =
    let chunk = opt "var debug = false\nvar r mut = debug ? 1 : 2\nr = r"
    Assert.False(has (function JumpIfFalse _ -> true | _ -> false) chunk)
    Assert.Contains(IntConstant 2I, chunk.Constants)
    Assert.DoesNotContain(IntConstant 1I, chunk.Constants)

[<Fact>]
let ``and and or with a literal left side collapse`` () =
    let chunk = opt "var a mut = false and 1\nvar b mut = true or 1\nvar c mut = nil ?? 3\na = a\nb = b\nc = c"
    Assert.False(has (function JumpIfFalse _ -> true | _ -> false) chunk)
    Assert.False(has (function JumpIfNotNil _ -> true | _ -> false) chunk)

[<Fact>]
let ``a folded-away statement leaves no code behind`` () =
    let chunk = opt "var debug = false\ndebug ? print 1 : nil\n"
    Assert.Equal<Instruction[]>([| False; DefineGlobal 0; Nil; Return |], chunk.Code)

[<Fact>]
let ``statements after a return are dropped`` () =
    let f = opt "fun f() {\n    return 1\n    print 2\n}\nf()" |> functionNamed "f"
    Assert.Equal<Instruction[]>([| Constant 0; Return |], f.Chunk.Code)

[<Fact>]
let ``the nil-return epilogue is kept when the body does not end in a return`` () =
    let f = opt "fun f() {\n    var x = 1\n}\nf()" |> functionNamed "f"
    Assert.Equal<Instruction[]>([| Constant 0; Nil; Return |], f.Chunk.Code)

[<Fact>]
let ``the epilogue is only elided when optimizing`` () =
    let f = plain "fun f() {\n    return 1\n}\nf()" |> functionNamed "f"
    Assert.Equal<Instruction[]>([| Constant 0; Return; Nil; Return |], f.Chunk.Code)

// --- docs/PLAN-0.4.md Phase 1: folding under the integer/float model --------

/// The single constant a one-variable program folds `expr` to.
let private foldedValue (expr: string) : Constant =
    let chunk = opt $"var r mut = {expr}\nr = r"
    let values = chunk.Constants |> Array.filter (function StringConstant _ -> false | _ -> true)
    Assert.Single(values)

[<Fact>]
let ``integer arithmetic folds exactly`` () =
    Assert.Equal(IntConstant 7I, foldedValue "1 + 2 * 3")
    Assert.Equal(IntConstant(bigint.Pow(2I, 64)), foldedValue "18446744073709551615 + 1")
    Assert.Equal(IntConstant -5I, foldedValue "-5")

[<Fact>]
let ``slash on two integers folds to a float`` () =
    Assert.Equal(FloatConstant 3.5, foldedValue "7 / 2")
    Assert.Equal(FloatConstant 3.0, foldedValue "6 / 2")

[<Fact>]
let ``mixed integer and float arithmetic folds to a float`` () =
    Assert.Equal(FloatConstant 3.5, foldedValue "1 + 2.5")

[<Fact>]
let ``equality folds by type and value`` () =
    Assert.Equal(1, (opt "var r mut = 1 == 1.0\nr = r").Code |> Array.filter ((=) False) |> Array.length)
    Assert.Equal(1, (opt "var r mut = 1 == 1\nr = r").Code |> Array.filter ((=) True) |> Array.length)
    Assert.Equal(1, (opt "var r mut = 1 < 1.5\nr = r").Code |> Array.filter ((=) True) |> Array.length)

[<Fact>]
let ``anything the vm might round or reject is left unfolded`` () =
    // An int too large to convert exactly, mixed with a float.
    Assert.True(has ((=) Add) (opt "var r mut = 9007199254740993 + 0.5\nr = r"))
    // Integer division by zero: the vm reports it (Phase 3 makes it a compile error).
    Assert.True(has ((=) Divide) (opt "var r mut = 1 / 0\nr = r"))
    // `%` and `^` are never folded.
    Assert.True(has ((=) Power) (opt "var r mut = 0 ^ -1\nr = r"))
