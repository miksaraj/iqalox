module ParserTests

open Xunit
open Iqalox.Token
open Iqalox.Ast
open Iqalox.Scanner
open Iqalox.Parser

let private parseSource (source: string) : Stmt list =
    let source = if source.EndsWith "\n" then source else source + "\n"
    let tokens, _ = scanTokens source
    let stmts, _ = parse tokens
    stmts

let private parseWithErrors (source: string) : Stmt list * ParseError list =
    let source = if source.EndsWith "\n" then source else source + "\n"
    let tokens, _ = scanTokens source
    parse tokens

let private singleExpr (source: string) : Expr =
    match parseSource source with
    | [ ExpressionStmt expr ] -> expr
    | stmts -> failwith $"expected exactly one ExpressionStmt, got %A{stmts}"

[<Fact>]
let ``true, false, and nil are real literal values`` () =
    Assert.Equal(Literal(BoolValue false), singleExpr "false")
    Assert.Equal(Literal(BoolValue true), singleExpr "true")
    Assert.Equal(Literal NilValue, singleExpr "nil")

[<Fact>]
let ``logical or and and precedence`` () =
    match singleExpr "a and b or c and d" with
    | Logical(left, operator, right) ->
        Assert.Equal(Or, operator.Type)
        match left with
        | Logical(_, op, _) -> Assert.Equal(And, op.Type)
        | e -> failwith $"expected Logical, got %A{e}"
        match right with
        | Logical(_, op, _) -> Assert.Equal(And, op.Type)
        | e -> failwith $"expected Logical, got %A{e}"
    | e -> failwith $"expected Logical, got %A{e}"

[<Fact>]
let ``null-coalescing binds looser than logical and tighter than ternary`` () =
    // `??` sits between `logic_or` and `ternary`: logical operators group
    // first (`a ?? (b or c)`), and `??` itself groups before the ternary
    // gets to see it (`(a ?? b) ? c : d`).
    match singleExpr "a ?? b or c" with
    | Binary(_, operator, right) ->
        Assert.Equal(DoubleQuestionMark, operator.Type)
        match right with
        | Logical(_, op, _) -> Assert.Equal(Or, op.Type)
        | e -> failwith $"expected Logical, got %A{e}"
    | e -> failwith $"expected Binary, got %A{e}"

    match singleExpr "a ?? b ? c : d" with
    | Ternary(left, _, _, _, _) ->
        match left with
        | Binary(_, op, _) -> Assert.Equal(DoubleQuestionMark, op.Type)
        | e -> failwith $"expected Binary, got %A{e}"
    | e -> failwith $"expected Ternary, got %A{e}"

[<Fact>]
let ``logical sits between ternary and equality`` () =
    // `a == b and c == d` should group as `(a == b) and (c == d)`, and the
    // whole thing should still be usable as a ternary condition without
    // needing its own wrapping parens.
    match singleExpr "a == b and c == d ? 1 : 2" with
    | Ternary(left, _, _, _, _) ->
        match left with
        | Logical _ -> ()
        | e -> failwith $"expected Logical, got %A{e}"
    | e -> failwith $"expected Ternary, got %A{e}"

[<Fact>]
let ``break and continue are expressions`` () =
    match singleExpr "break" with
    | BreakExpr _ -> ()
    | e -> failwith $"expected BreakExpr, got %A{e}"
    match singleExpr "continue" with
    | ContinueExpr _ -> ()
    | e -> failwith $"expected ContinueExpr, got %A{e}"

[<Fact>]
let ``break and continue are usable as ternary branches`` () =
    match singleExpr "a ? continue : b ? break : c" with
    | Ternary(_, _, middle, _, right) ->
        match middle with
        | ContinueExpr _ -> ()
        | e -> failwith $"expected ContinueExpr, got %A{e}"
        match right with
        | Ternary(_, _, innerMiddle, _, _) ->
            match innerMiddle with
            | BreakExpr _ -> ()
            | e -> failwith $"expected BreakExpr, got %A{e}"
        | e -> failwith $"expected Ternary, got %A{e}"
    | e -> failwith $"expected Ternary, got %A{e}"

[<Fact>]
let ``for statement parses all clauses`` () =
    match parseSource "for (var i mut = 0; i < 5; ++i) { print i; }" with
    | [ ForStmt(initializer, condition, increment, _) ] ->
        Assert.True(initializer.IsSome)
        Assert.True(condition.IsSome)
        Assert.True(increment.IsSome)
    | stmts -> failwith $"expected one ForStmt, got %A{stmts}"

[<Fact>]
let ``for statement clauses are optional`` () =
    match parseSource "for (;;) { break; }" with
    | [ ForStmt(initializer, condition, increment, _) ] ->
        Assert.True(initializer.IsNone)
        Assert.True(condition.IsNone)
        Assert.True(increment.IsNone)
    | stmts -> failwith $"expected one ForStmt, got %A{stmts}"

[<Fact>]
let ``a for loop with an empty statement body does not crash`` () =
    // Bug fix vs. poc: poc's for_statement() assigns `body = self.statement()`
    // with no None-check, but `statement()` returns None for a bare `;`
    // body -- constructing a For node with a None body, which crashes the
    // interpreter later (`AttributeError` on `None.accept`). Treated here
    // as an empty block instead.
    match parseSource "for (;;) ;" with
    | [ ForStmt(_, _, _, Block []) ] -> ()
    | stmts -> failwith $"expected a ForStmt with an empty Block body, got %A{stmts}"

[<Fact>]
let ``increment requires an assignable target`` () =
    // `++5` isn't a variable, so it shouldn't parse as an increment.
    Assert.Empty(parseSource "++5")

[<Fact>]
let ``increment on a variable parses`` () =
    match singleExpr "++x" with
    | Unary(operator, right) ->
        Assert.Equal(PlusPlus, operator.Type)
        match right with
        | Variable _ -> ()
        | e -> failwith $"expected Variable, got %A{e}"
    | e -> failwith $"expected Unary, got %A{e}"

[<Fact>]
let ``mut declaration parses`` () =
    match parseSource "var x mut = 1" with
    | [ VarStmt(_, _, isMutable) ] -> Assert.True isMutable
    | stmts -> failwith $"expected one VarStmt, got %A{stmts}"

[<Fact>]
let ``immutable declaration parses`` () =
    match parseSource "var x = 1" with
    | [ VarStmt(_, _, isMutable) ] -> Assert.False isMutable
    | stmts -> failwith $"expected one VarStmt, got %A{stmts}"

[<Fact>]
let ``blank lines between statements do not error`` () =
    Assert.Equal(2, (parseSource "var x = 1\n\n\nprint x").Length)

[<Fact>]
let ``function declaration parses params and body`` () =
    match parseSource "fun add(a, b) { return a + b; }" with
    | [ FunctionStmt decl ] ->
        Assert.Equal("add", decl.Name.Lexeme)
        Assert.Equal<string list>([ "a"; "b" ], decl.Parameters |> List.map (fun p -> p.Lexeme))
        Assert.Single(decl.Body) |> ignore
        match decl.Body.[0] with
        | ReturnStmt _ -> ()
        | s -> failwith $"expected ReturnStmt, got %A{s}"
    | stmts -> failwith $"expected one FunctionStmt, got %A{stmts}"

[<Fact>]
let ``zero-arg call requires empty parens`` () =
    match singleExpr "f()" with
    | Call(Variable name, arguments) ->
        Assert.Equal("f", name.Lexeme)
        Assert.Empty(arguments)
    | e -> failwith $"expected Call, got %A{e}"

[<Fact>]
let ``a bare identifier is not a call`` () =
    // No `()` and nothing recognizable as an argument follows -- `f` alone
    // is a value reference, not an invocation (needed so functions can be
    // passed around without being called, e.g. `return adder`).
    match singleExpr "f" with
    | Variable _ -> ()
    | e -> failwith $"expected Variable, got %A{e}"

[<Fact>]
let ``single-arg call needs no parens`` () =
    match singleExpr "f 1" with
    | Call(_, [ Literal(NumberValue 1.0) ]) -> ()
    | e -> failwith $"expected Call with one NumberValue 1.0 argument, got %A{e}"

[<Fact>]
let ``multi-arg call is comma-separated without wrapping parens`` () =
    match singleExpr "f 1, 2, 3" with
    | Call(_, arguments) ->
        let values =
            arguments
            |> List.map (function
                | Literal(NumberValue n) -> n
                | e -> failwith $"expected NumberValue, got %A{e}")
        Assert.Equal<float list>([ 1.0; 2.0; 3.0 ], values)
    | e -> failwith $"expected Call, got %A{e}"

[<Fact>]
let ``a compound argument needs grouping parens`` () =
    // `f - 1` is subtraction (f minus 1), never a call to f with a bare
    // unary/binary expression as its argument.
    match singleExpr "f - 1" with
    | Binary(_, operator, _) -> Assert.Equal(Minus, operator.Type)
    | e -> failwith $"expected Binary, got %A{e}"

    match singleExpr "f (n - 1)" with
    | Call(_, [ Grouping _ ]) -> ()
    | e -> failwith $"expected Call with one Grouping argument, got %A{e}"

[<Fact>]
let ``a nested call needs no extra parens`` () =
    // `concat` immediately followed by a vector literal is itself a
    // complete call, which then becomes print's one argument.
    match singleExpr "print concat [1, 2]" with
    | Call(Variable printName, [ Call(Variable concatName, [ Vector _ ]) ]) ->
        Assert.Equal("print", printName.Lexeme)
        Assert.Equal("concat", concatName.Lexeme)
    | e -> failwith $"expected nested Call, got %A{e}"

[<Fact>]
let ``vector literal with multiple elements`` () =
    match singleExpr "[1, 2, 3]" with
    | Vector values ->
        let numbers =
            values
            |> List.map (function
                | Literal(NumberValue n) -> n
                | e -> failwith $"expected NumberValue, got %A{e}")
        Assert.Equal<float list>([ 1.0; 2.0; 3.0 ], numbers)
    | e -> failwith $"expected Vector, got %A{e}"

[<Fact>]
let ``empty and single-element vector literals`` () =
    Assert.Equal(Vector [], singleExpr "[]")
    Assert.Equal(Vector [ Literal(NumberValue 1.0) ], singleExpr "[1]")

[<Fact>]
let ``a vector literal parse error does not leave the comma operator disabled`` () =
    // Bug fix vs. poc: poc's primary() restores comma_as_operator with a
    // plain assignment *after* parsing a vector literal's elements, so an
    // error partway through (here, a bare '+' isn't a valid element) skips
    // that restore -- since the exception unwinds straight past it --
    // leaving the comma operator permanently disabled for the rest of the
    // file. The next line's `a, b` would then fail to parse too.
    let stmts, _ = parseWithErrors "[1, 2, +]\na, b\n"
    match stmts with
    | [ ExpressionStmt(Binary(Variable a, operator, Variable b)) ] ->
        Assert.Equal(Comma, operator.Type)
        Assert.Equal("a", a.Lexeme)
        Assert.Equal("b", b.Lexeme)
    | _ -> failwith $"expected the second line to parse as one comma-expression statement, got %A{stmts}"

[<Fact>]
let ``pipe desugars to a call`` () =
    match singleExpr "a |> f" with
    | Call(Variable f, [ Variable a ]) ->
        Assert.Equal("f", f.Lexeme)
        Assert.Equal("a", a.Lexeme)
    | e -> failwith $"expected Call, got %A{e}"

[<Fact>]
let ``pipe chains left-associatively`` () =
    // `a |> f |> g` is `g(f(a))`.
    match singleExpr "a |> f |> g" with
    | Call(Variable g, [ Call(Variable f, [ Variable a ]) ]) ->
        Assert.Equal("g", g.Lexeme)
        Assert.Equal("f", f.Lexeme)
        Assert.Equal("a", a.Lexeme)
    | e -> failwith $"expected nested Call, got %A{e}"

[<Fact>]
let ``pipe requires a function reference on the right`` () =
    // `a |> 1` doesn't make sense -- the right side must be a bare name.
    Assert.Empty(parseSource "a |> 1")

[<Fact>]
let ``pipe binds looser than comma and ternary`` () =
    // A ternary as the whole pipe input needs no extra grouping.
    match singleExpr "(a ? b : c) |> f" with
    | Call(_, [ Grouping(Ternary _) ]) -> ()
    | e -> failwith $"expected Call with a grouped Ternary argument, got %A{e}"

[<Fact>]
let ``the ignore operator is an expression`` () =
    Assert.Equal(Ignore, singleExpr "_")

[<Fact>]
let ``the ignore operator is usable as a ternary branch`` () =
    match singleExpr "a ? _ : b" with
    | Ternary(_, _, middle, _, _) -> Assert.Equal(Ignore, middle)
    | e -> failwith $"expected Ternary, got %A{e}"

[<Fact>]
let ``class declaration parses methods`` () =
    match parseSource "class Duck { quack() { return 1; } }" with
    | [ ClassStmt(name, superclass, [], [], [ method_ ], []) ] ->
        Assert.Equal("Duck", name.Lexeme)
        Assert.True(superclass.IsNone)
        Assert.Equal("quack", method_.Name.Lexeme)
        Assert.False(method_.IsPub)
    | stmts -> failwith $"expected one ClassStmt with one method, got %A{stmts}"

[<Fact>]
let ``class declaration parses a superclass`` () =
    match parseSource "class B extends A {}" with
    | [ ClassStmt(_, Some(Variable superName), [], [], [], []) ] -> Assert.Equal("A", superName.Lexeme)
    | stmts -> failwith $"expected one ClassStmt with a Variable superclass, got %A{stmts}"

[<Fact>]
let ``class body tolerates blank lines`` () =
    match parseSource "class Duck {\n\n    quack() { return 1; }\n\n}" with
    | [ ClassStmt(_, _, [], [], [ _ ], []) ] -> ()
    | stmts -> failwith $"expected one ClassStmt with one method, got %A{stmts}"

[<Fact>]
let ``pub method is parsed with IsPub true`` () =
    match parseSource "class Duck { pub quack() { return 1; } }" with
    | [ ClassStmt(_, _, [], [], [ method_ ], []) ] ->
        Assert.Equal("quack", method_.Name.Lexeme)
        Assert.True(method_.IsPub)
    | stmts -> failwith $"expected one ClassStmt with one pub method, got %A{stmts}"

[<Fact>]
let ``property declaration parses all four pub/mut combinations`` () =
    match
        parseSource
            "class Duck { var a; var b mut; var c pub; var d pub mut; init() { self.a = 1; self.b = 1; self.c = 1; self.d = 1; } }"
    with
    | [ ClassStmt(_, _, [], [ a; b; c; d ], [ _ ], []) ] ->
        Assert.Equal("a", a.Name.Lexeme)
        Assert.False(a.IsPub)
        Assert.False(a.IsMutable)
        Assert.Equal("b", b.Name.Lexeme)
        Assert.False(b.IsPub)
        Assert.True(b.IsMutable)
        Assert.Equal("c", c.Name.Lexeme)
        Assert.True(c.IsPub)
        Assert.False(c.IsMutable)
        Assert.Equal("d", d.Name.Lexeme)
        Assert.True(d.IsPub)
        Assert.True(d.IsMutable)
    | stmts -> failwith $"expected one ClassStmt with four properties, got %A{stmts}"

[<Fact>]
let ``class declaration parses a with-mixin list`` () =
    match parseSource "class Robot with Named, Steel {}" with
    | [ ClassStmt(_, None, [ Variable m1; Variable m2 ], [], [], []) ] ->
        Assert.Equal("Named", m1.Lexeme)
        Assert.Equal("Steel", m2.Lexeme)
    | stmts -> failwith $"expected one ClassStmt with two mixins, got %A{stmts}"

[<Fact>]
let ``class declaration parses extends combined with with`` () =
    match parseSource "class FlyingCar extends Vehicle with Flyable {}" with
    | [ ClassStmt(_, Some(Variable superName), [ Variable mixinName ], [], [], []) ] ->
        Assert.Equal("Vehicle", superName.Lexeme)
        Assert.Equal("Flyable", mixinName.Lexeme)
    | stmts -> failwith $"expected one ClassStmt with a superclass and a mixin, got %A{stmts}"

[<Fact>]
let ``class body parses a use clause`` () =
    match parseSource "class Duck { use Flyable, Swimmable\n quack() { return 1; } }" with
    | [ ClassStmt(_, _, [], [], [ method_ ], [ t1; t2 ]) ] ->
        Assert.Equal("quack", method_.Name.Lexeme)
        Assert.Equal("Flyable", t1.Lexeme)
        Assert.Equal("Swimmable", t2.Lexeme)
    | stmts -> failwith $"expected one ClassStmt with two used traits, got %A{stmts}"

[<Fact>]
let ``trait declaration parses properties, methods, and nested use`` () =
    match parseSource "trait Flyable { use Winged\n var altitude mut\n pub fly() { return 1; } }" with
    | [ TraitStmt(name, [ altitude ], [ fly ], [ winged ]) ] ->
        Assert.Equal("Flyable", name.Lexeme)
        Assert.Equal("altitude", altitude.Name.Lexeme)
        Assert.True(altitude.IsMutable)
        Assert.Equal("fly", fly.Name.Lexeme)
        Assert.True(fly.IsPub)
        Assert.Equal("Winged", winged.Lexeme)
    | stmts -> failwith $"expected one TraitStmt, got %A{stmts}"

[<Fact>]
let ``property access is a Get expression`` () =
    match singleExpr "duck.name" with
    | Get(Variable objName, name) ->
        Assert.Equal("name", name.Lexeme)
        Assert.Equal("duck", objName.Lexeme)
    | e -> failwith $"expected Get, got %A{e}"

[<Fact>]
let ``property call with zero args`` () =
    match singleExpr "duck.quack()" with
    | Call(Get(_, name), []) -> Assert.Equal("quack", name.Lexeme)
    | e -> failwith $"expected zero-arg Call on a Get, got %A{e}"

[<Fact>]
let ``property call with args needs no parens`` () =
    match singleExpr "math.square 3" with
    | Call(Get(_, name), [ Literal(NumberValue 3.0) ]) -> Assert.Equal("square", name.Lexeme)
    | e -> failwith $"expected Call on a Get, got %A{e}"

[<Fact>]
let ``property assignment is a Set expression`` () =
    match singleExpr "self.name = value" with
    | Set(SelfExpr _, name, Variable value) ->
        Assert.Equal("name", name.Lexeme)
        Assert.Equal("value", value.Lexeme)
    | e -> failwith $"expected Set, got %A{e}"

[<Fact>]
let ``super method call`` () =
    match singleExpr "super.test()" with
    | Call(SuperExpr(_, method_), []) -> Assert.Equal("test", method_.Lexeme)
    | e -> failwith $"expected zero-arg Call on a SuperExpr, got %A{e}"

[<Fact>]
let ``self is an expression`` () =
    match singleExpr "self" with
    | SelfExpr _ -> ()
    | e -> failwith $"expected SelfExpr, got %A{e}"

[<Fact>]
let ``no-space bracket after an identifier is indexing`` () =
    match singleExpr "v[0]" with
    | Index(Variable objName, Literal(NumberValue 0.0), _) -> Assert.Equal("v", objName.Lexeme)
    | e -> failwith $"expected Index, got %A{e}"

[<Fact>]
let ``space before bracket after an identifier is still a vector-literal call argument`` () =
    // docs/PLAN-0.2.md decision 6's whitespace-adjacency resolution: this
    // must keep meaning exactly what it meant before indexing existed.
    match singleExpr "concat [1, 2]" with
    | Call(Variable name, [ Vector [ Literal(NumberValue 1.0); Literal(NumberValue 2.0) ] ]) ->
        Assert.Equal("concat", name.Lexeme)
    | e -> failwith $"expected Call with a Vector argument, got %A{e}"

[<Fact>]
let ``chained indexing`` () =
    match singleExpr "grid[0][1]" with
    | Index(Index(Variable objName, Literal(NumberValue 0.0), _), Literal(NumberValue 1.0), _) ->
        Assert.Equal("grid", objName.Lexeme)
    | e -> failwith $"expected nested Index, got %A{e}"

[<Fact>]
let ``indexing a zero-arg call result needs no extra parens`` () =
    match singleExpr "makeVector()[0]" with
    | Index(Call(Variable name, []), Literal(NumberValue 0.0), _) -> Assert.Equal("makeVector", name.Lexeme)
    | e -> failwith $"expected Index over a Call, got %A{e}"

[<Fact>]
let ``no-space bracket after a property access is indexing, not a call`` () =
    match singleExpr "obj.method[0]" with
    | Index(Get(_, name), Literal(NumberValue 0.0), _) -> Assert.Equal("method", name.Lexeme)
    | e -> failwith $"expected Index over a Get, got %A{e}"

[<Fact>]
let ``a negative index needs no new grammar -- it's ordinary indexing with a unary-minus expression`` () =
    // docs/PLAN-0.3.md decision 2.
    match singleExpr "v[-1]" with
    | Index(Variable objName, Unary(operator, Literal(NumberValue 1.0)), _) ->
        Assert.Equal("v", objName.Lexeme)
        Assert.Equal(Minus, operator.Type)
    | e -> failwith $"expected Index over a Unary, got %A{e}"

[<Fact>]
let ``a colon inside brackets is a slice, both bounds present`` () =
    // docs/PLAN-0.3.md decision 3.
    match singleExpr "v[1:2]" with
    | Slice(Variable objName, Some(Literal(NumberValue 1.0)), Some(Literal(NumberValue 2.0)), _) ->
        Assert.Equal("v", objName.Lexeme)
    | e -> failwith $"expected Slice with both bounds, got %A{e}"

[<Fact>]
let ``a slice's start bound may be omitted`` () =
    match singleExpr "v[:2]" with
    | Slice(Variable _, None, Some(Literal(NumberValue 2.0)), _) -> ()
    | e -> failwith $"expected Slice with no start bound, got %A{e}"

[<Fact>]
let ``a slice's stop bound may be omitted`` () =
    match singleExpr "v[2:]" with
    | Slice(Variable _, Some(Literal(NumberValue 2.0)), None, _) -> ()
    | e -> failwith $"expected Slice with no stop bound, got %A{e}"

[<Fact>]
let ``a slice may omit both bounds`` () =
    match singleExpr "v[:]" with
    | Slice(Variable _, None, None, _) -> ()
    | e -> failwith $"expected Slice with neither bound, got %A{e}"

[<Fact>]
let ``a slice bound may itself be a negative index`` () =
    match singleExpr "v[-3:-1]" with
    | Slice(Variable _, Some(Unary(startOp, _)), Some(Unary(stopOp, _)), _) ->
        Assert.Equal(Minus, startOp.Type)
        Assert.Equal(Minus, stopOp.Type)
    | e -> failwith $"expected Slice with negative bounds, got %A{e}"

[<Fact>]
let ``a ternary used as a plain index is not mistaken for a slice`` () =
    // The lookahead that tells indexing apart from a slice must skip past
    // a ternary's own '?'/':' pair rather than treating the ternary's ':'
    // as the slice separator.
    match singleExpr "v[cond ? 1 : 2]" with
    | Index(Variable _, Ternary(Variable condName, _, _, _, _), _) -> Assert.Equal("cond", condName.Lexeme)
    | e -> failwith $"expected Index over a Ternary, got %A{e}"

[<Fact>]
let ``assigning to a slice is a parse error`` () =
    let _, errors = parseWithErrors "v[1:2] = x"
    Assert.NotEmpty errors

[<Fact>]
let ``space before bracket after a property access is still a vector-literal call argument`` () =
    match singleExpr "obj.method [0]" with
    | Call(Get(_, name), [ Vector [ Literal(NumberValue 0.0) ] ]) -> Assert.Equal("method", name.Lexeme)
    | e -> failwith $"expected Call with a Vector argument, got %A{e}"

[<Fact>]
let ``indexing is a valid assignment target`` () =
    match singleExpr "v[0] = 1" with
    | IndexSet(Variable objName, Literal(NumberValue 0.0), Literal(NumberValue 1.0), _) ->
        Assert.Equal("v", objName.Lexeme)
    | e -> failwith $"expected IndexSet, got %A{e}"

[<Fact>]
let ``a single-parameter lambda parses as a Lambda expression`` () =
    match singleExpr "(n) -> n * n" with
    | Lambda([ param ], _, Binary(Variable left, _, Variable right)) ->
        Assert.Equal("n", param.Lexeme)
        Assert.Equal("n", left.Lexeme)
        Assert.Equal("n", right.Lexeme)
    | e -> failwith $"expected Lambda, got %A{e}"

[<Fact>]
let ``a multi-parameter lambda parses both parameter names`` () =
    match singleExpr "(a, b) -> a + b" with
    | Lambda([ a; b ], _, _) ->
        Assert.Equal("a", a.Lexeme)
        Assert.Equal("b", b.Lexeme)
    | e -> failwith $"expected Lambda, got %A{e}"

[<Fact>]
let ``a zero-parameter lambda parses with an empty parameter list`` () =
    match singleExpr "() -> 1" with
    | Lambda([], _, Literal(NumberValue 1.0)) -> ()
    | e -> failwith $"expected zero-parameter Lambda, got %A{e}"

[<Fact>]
let ``a grouped comma expression with no arrow after it is not a lambda`` () =
    // docs/PLAN-0.2.md decision 1's lookahead-past-')' disambiguation:
    // (a, b) alone is still 0.1's comma operator wrapped in a Grouping.
    match singleExpr "(a, b)" with
    | Grouping(Binary(Variable a, operator, Variable b)) ->
        Assert.Equal(Comma, operator.Type)
        Assert.Equal("a", a.Lexeme)
        Assert.Equal("b", b.Lexeme)
    | e -> failwith $"expected a Grouping around a comma Binary, got %A{e}"

[<Fact>]
let ``a lambda is a valid call argument with no extra wrapping parens`` () =
    match singleExpr "applyTwice square, (x) -> x + 1" with
    | Call(Variable name, [ Variable squareName; Lambda([ param ], _, _) ]) ->
        Assert.Equal("applyTwice", name.Lexeme)
        Assert.Equal("square", squareName.Lexeme)
        Assert.Equal("x", param.Lexeme)
    | e -> failwith $"expected Call with a Lambda argument, got %A{e}"

[<Fact>]
let ``a curried lambda's body is itself a lambda`` () =
    match singleExpr "(x) -> (y) -> x + y" with
    | Lambda([ x ], _, Lambda([ y ], _, _)) ->
        Assert.Equal("x", x.Lexeme)
        Assert.Equal("y", y.Lexeme)
    | e -> failwith $"expected a Lambda whose body is another Lambda, got %A{e}"

[<Fact>]
let ``a lambda argument in a non-last position does not swallow the arguments after it`` () =
    // Bug found while writing docs/PLAN-0.2.md Phase 5's array-stdlib
    // prelude (`map fn, v`): a lambda's own body is parsed via the full
    // Expression() chain, which -- unless suppressed -- treats a bare
    // comma as its own operator, so `map (x) -> x * 2, v` used to have
    // the lambda's unparenthesized body swallow `, v` entirely, leaving
    // `map` a 1-argument call. Fixed in Argument().
    match singleExpr "map (x) -> x * 2, v" with
    | Call(Variable mapName, [ Lambda([ x ], _, Binary(Variable bodyX, _, Literal(NumberValue 2.0))); Variable vName ]) ->
        Assert.Equal("map", mapName.Lexeme)
        Assert.Equal("x", x.Lexeme)
        Assert.Equal("x", bodyX.Lexeme)
        Assert.Equal("v", vName.Lexeme)
    | e -> failwith $"expected a 2-argument Call with a Lambda first argument, got %A{e}"

[<Fact>]
let ``a three-argument call with a non-last lambda still parses all three arguments`` () =
    // The same bug, exercised with reduce's actual shape (fn, v, initial).
    match singleExpr "reduce (a, b) -> a + b, v, 0" with
    | Call(Variable reduceName, [ Lambda([ a; b ], _, _); Variable vName; Literal(NumberValue 0.0) ]) ->
        Assert.Equal("reduce", reduceName.Lexeme)
        Assert.Equal("a", a.Lexeme)
        Assert.Equal("b", b.Lexeme)
        Assert.Equal("v", vName.Lexeme)
    | e -> failwith $"expected a 3-argument Call, got %A{e}"

[<Fact>]
let ``a parenthesized comma expression still works as a single call argument`` () =
    // The fix above must not break the pre-existing, legitimate case: a
    // *parenthesized* comma-operator expression, passed as one argument.
    // Grouping's own explicit parens always re-enable the comma operator
    // for their own contents, regardless of the outer context.
    match singleExpr "f (a, b)" with
    | Call(Variable fName, [ Grouping(Binary(Variable a, operator, Variable b)) ]) ->
        Assert.Equal("f", fName.Lexeme)
        Assert.Equal(Comma, operator.Type)
        Assert.Equal("a", a.Lexeme)
        Assert.Equal("b", b.Lexeme)
    | e -> failwith $"expected a 1-argument Call with a grouped comma Binary, got %A{e}"

[<Fact>]
let ``a parenthesized comma expression inside a vector literal still works`` () =
    // Same fix, exercised inside `[...]`'s own comma-suppressed context --
    // Grouping's parens must re-enable the comma operator there too.
    match singleExpr "[(1, 2), 3]" with
    | Vector [ Grouping(Binary(Literal(NumberValue 1.0), operator, Literal(NumberValue 2.0))); Literal(NumberValue 3.0) ] ->
        Assert.Equal(Comma, operator.Type)
    | e -> failwith $"expected a Vector with a grouped comma Binary first element, got %A{e}"

[<Fact>]
let ``a bare vertical bar with no generator marker is a cons`` () =
    match singleExpr "[1 | xs]" with
    | Cons(Literal(NumberValue 1.0), Variable list_, _) -> Assert.Equal("xs", list_.Lexeme)
    | e -> failwith $"expected Cons, got %A{e}"

[<Fact>]
let ``a vertical bar followed by identifier-arrow is a list comprehension`` () =
    match singleExpr "[x * 2 | x <- xs]" with
    | ListComprehension(Binary(Variable bodyX, _, _), [ variable, Variable source ], None, _) ->
        Assert.Equal("x", bodyX.Lexeme)
        Assert.Equal("x", variable.Lexeme)
        Assert.Equal("xs", source.Lexeme)
    | e -> failwith $"expected ListComprehension, got %A{e}"

[<Fact>]
let ``a plain multi-element vector literal has no vertical bar at all`` () =
    match singleExpr "[1, 2, 3]" with
    | Vector _ -> ()
    | e -> failwith $"expected Vector, got %A{e}"

[<Fact>]
let ``an identifier immediately after the bar without an arrow is still a cons, not a comprehension`` () =
    // Disambiguation is lookahead on the generator marker (docs/PLAN-0.2.md
    // decision 2): `[x | y]` has an identifier right after `|`, but no
    // `<-` follows it, so this is cons -- not a comprehension missing its
    // generator.
    match singleExpr "[x | y]" with
    | Cons(Variable item, Variable list_, _) ->
        Assert.Equal("x", item.Lexeme)
        Assert.Equal("y", list_.Lexeme)
    | e -> failwith $"expected Cons, got %A{e}"

[<Fact>]
let ``cons can nest to build up a list from multiple items`` () =
    match singleExpr "[1 | [2 | []]]" with
    | Cons(Literal(NumberValue 1.0), Cons(Literal(NumberValue 2.0), Vector [], _), _) -> ()
    | e -> failwith $"expected nested Cons, got %A{e}"

[<Fact>]
let ``list comprehension source can be an arbitrary expression, not just an identifier`` () =
    match singleExpr "[x | x <- [1, 2, 3]]" with
    | ListComprehension(_, [ variable, Vector _ ], None, _) -> Assert.Equal("x", variable.Lexeme)
    | e -> failwith $"expected ListComprehension, got %A{e}"

[<Fact>]
let ``multiple comma-separated generators parse in order`` () =
    // docs/PLAN-0.3.md decision 1.
    match singleExpr "[x + y | x <- xs, y <- ys]" with
    | ListComprehension(_, [ (xVar, Variable xs); (yVar, Variable ys) ], None, _) ->
        Assert.Equal("x", xVar.Lexeme)
        Assert.Equal("xs", xs.Lexeme)
        Assert.Equal("y", yVar.Lexeme)
        Assert.Equal("ys", ys.Lexeme)
    | e -> failwith $"expected ListComprehension with two generators, got %A{e}"

[<Fact>]
let ``a guard clause is introduced by a second vertical bar after the generators`` () =
    match singleExpr "[x + y | x <- xs, y <- ys | x != y]" with
    | ListComprehension(_, [ _; _ ], Some(Binary(Variable gx, operator, Variable gy)), _) ->
        Assert.Equal(BangEqual, operator.Type)
        Assert.Equal("x", gx.Lexeme)
        Assert.Equal("y", gy.Lexeme)
    | e -> failwith $"expected ListComprehension with a guard, got %A{e}"

[<Fact>]
let ``a comprehension with no guard leaves it as None`` () =
    match singleExpr "[x | x <- xs]" with
    | ListComprehension(_, [ _ ], None, _) -> ()
    | e -> failwith $"expected ListComprehension with no guard, got %A{e}"

[<Fact>]
let ``a spread element parses as Spread wrapping the inner expression`` () =
    match singleExpr "[...a]" with
    | Vector [ Spread(Variable inner, _) ] -> Assert.Equal("a", inner.Lexeme)
    | e -> failwith $"expected Vector with one Spread element, got %A{e}"

[<Fact>]
let ``spread can be mixed with plain elements in any position`` () =
    match singleExpr "[0, ...a, 2.5, ...b, 5]" with
    | Vector [ Literal(NumberValue 0.0); Spread(Variable a, _); Literal(NumberValue 2.5); Spread(Variable b, _); Literal(NumberValue 5.0) ] ->
        Assert.Equal("a", a.Lexeme)
        Assert.Equal("b", b.Lexeme)
    | e -> failwith $"expected Vector with alternating plain/Spread elements, got %A{e}"

[<Fact>]
let ``a vector literal with no spread elements has no Spread node at all`` () =
    match singleExpr "[1, 2, 3]" with
    | Vector values -> Assert.DoesNotContain(values, (function | Spread _ -> true | _ -> false))
    | e -> failwith $"expected Vector, got %A{e}"

[<Fact>]
let ``a leading spread element rules out cons and list comprehension disambiguation`` () =
    // docs/PLAN-0.2.md decision 7: spread is vector-literal only -- a
    // bare '|' right after a leading spread element is a plain syntax
    // error (expects ',' or ']'), never parsed as cons or a comprehension.
    Assert.Empty(parseSource "[...a | b]")

[<Fact>]
let ``spread's inner expression can be an arbitrary expression, not just an identifier`` () =
    match singleExpr "[...(a)]" with
    | Vector [ Spread(Grouping(Variable inner), _) ] -> Assert.Equal("a", inner.Lexeme)
    | e -> failwith $"expected Vector with one Spread(Grouping) element, got %A{e}"

// --- docs/PLAN-0.3.md Phase 5: `pub` on top-level declarations and `import` ---

let private lexemes (tokens: Token list) = tokens |> List.map (fun t -> t.Lexeme)

[<Fact>]
let ``import splits the last dotted segment off as the member name`` () =
    match parseSource "import math.vector.transpose" with
    | [ ImportStmt(_, [ item ]) ] ->
        Assert.Equal<string list>([ "math"; "vector" ], lexemes item.Module)
        Assert.Equal("transpose", item.Name.Lexeme)
        Assert.True item.Alias.IsNone
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``import takes a comma-separated list with optional as-aliases`` () =
    match parseSource "import a.b.x, c.y as why, d.e.f as g" with
    | [ ImportStmt(_, [ one; two; three ]) ] ->
        Assert.Equal("x", one.Name.Lexeme)
        Assert.Equal<string list>([ "c" ], lexemes two.Module)
        Assert.Equal("why", two.Alias.Value.Lexeme)
        Assert.Equal("g", three.Alias.Value.Lexeme)
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``an import needs both a module path and a name`` () =
    let _, errors = parseWithErrors "import vector"
    Assert.Single errors |> ignore
    Assert.Contains("module path and a name", errors.[0].Message)

[<Fact>]
let ``import with a dangling as is a parse error`` () =
    let _, errors = parseWithErrors "import a.b as"
    Assert.NotEmpty errors

[<Fact>]
let ``pub wraps a top-level fun, var, class or trait`` () =
    let wrapped =
        parseSource "pub fun f() {\n}\npub var x = 1\npub var y mut = 2\npub class C {\n}\npub trait T {\n}"
    match wrapped with
    | [ PubStmt(_, FunctionStmt _); PubStmt(_, VarStmt(_, _, false)); PubStmt(_, VarStmt(_, _, true))
        PubStmt(_, ClassStmt _); PubStmt(_, TraitStmt _) ] -> ()
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``pub before anything else is a parse error`` () =
    let _, errors = parseWithErrors "pub print 1"
    Assert.NotEmpty errors
    Assert.Contains("after 'pub'", errors.[0].Message)

[<Fact>]
let ``import and as are reserved words now`` () =
    let _, errors = parseWithErrors "var as = 1"
    Assert.NotEmpty errors

// --- brace-grouped imports: import a.b.{x, y as z} ---

let private importItems (source: string) : ImportItem list =
    match parseSource source with
    | [ ImportStmt(_, items) ] -> items
    | other -> failwith $"unexpected: %A{other}"

[<Fact>]
let ``a brace group imports several names from one module`` () =
    let items = importItems "import std.matrix.{transpose, add}"
    Assert.Equal<string list>([ "transpose"; "add" ], items |> List.map (fun i -> i.Name.Lexeme))
    for item in items do
        Assert.Equal<string list>([ "std"; "matrix" ], lexemes item.Module)

[<Fact>]
let ``names inside a brace group can be renamed with as`` () =
    let items = importItems "import std.matrix.{multiply as mul, add}"
    Assert.Equal("mul", items.[0].Alias.Value.Lexeme)
    Assert.True items.[1].Alias.IsNone

[<Fact>]
let ``a brace group may span several lines`` () =
    let items = importItems "import std.vector.{\n    push,\n    pop as take,\n    map\n}"
    Assert.Equal<string list>([ "push"; "pop"; "map" ], items |> List.map (fun i -> i.Name.Lexeme))
    Assert.Equal("take", items.[1].Alias.Value.Lexeme)

[<Fact>]
let ``groups, single names and several modules can be mixed in one import`` () =
    let items = importItems "import std.matrix.{add, transpose}, std.vector.push, util.{a as b}"
    Assert.Equal(4, items.Length)
    Assert.Equal<string list>([ "util" ], lexemes items.[3].Module)
    Assert.Equal<string list>([ "std"; "vector" ], lexemes items.[2].Module)

[<Fact>]
let ``a module path of just one segment is allowed before a brace group`` () =
    let items = importItems "import util.{helper}"
    Assert.Equal<string list>([ "util" ], lexemes items.[0].Module)
    Assert.Equal("helper", items.[0].Name.Lexeme)

[<Fact>]
let ``an empty brace group is a parse error`` () =
    let _, errors = parseWithErrors "import std.matrix.{}"
    Assert.NotEmpty errors
    Assert.Contains("at least one name", errors.[0].Message)

[<Fact>]
let ``an unclosed brace group is a parse error`` () =
    let _, errors = parseWithErrors "import std.matrix.{add, transpose"
    Assert.NotEmpty errors

[<Fact>]
let ``a trailing comma in a brace group is a parse error`` () =
    let _, errors = parseWithErrors "import std.matrix.{add,}"
    Assert.NotEmpty errors

[<Fact>]
let ``a real semicolon inside a brace group is not swallowed`` () =
    let _, errors = parseWithErrors "import std.matrix.{add; transpose}"
    Assert.NotEmpty errors
