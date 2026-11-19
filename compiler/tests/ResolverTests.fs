module ResolverTests

open Xunit
open Iqalox.Ast
open Iqalox.Scanner
open Iqalox.Parser
open Iqalox.Bound
open Iqalox.Resolver

let private resolveSource (source: string) : BoundStmt list * ResolveError list =
    let source = if source.EndsWith "\n" then source else source + "\n"
    let tokens, _ = scanTokens source
    let stmts, _ = parse tokens
    let bound, errors, _ = resolve stmts
    bound, errors

/// Like `resolveSource`, but also surfaces `docs/PLAN-0.3.md` decision 4's
/// unused-variable/-parameter/-private-member warnings -- kept as a
/// separate helper (rather than changing `resolveSource`'s own shape) so
/// every existing test call site keeps destructuring a plain 2-tuple.
let private resolveSourceWithWarnings (source: string) : BoundStmt list * ResolveError list * ResolveError list =
    let source = if source.EndsWith "\n" then source else source + "\n"
    let tokens, _ = scanTokens source
    let stmts, _ = parse tokens
    resolve stmts

[<Fact>]
let ``a top-level var resolves as a global`` () =
    let bound, errors = resolveSource "var x = 1\nx"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "x", _, _); BExpressionStmt(BVariable(GlobalBinding "x", _)) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a local var inside a block resolves to a stack slot`` () =
    let bound, errors = resolveSource "{ var x = 1\nx; }"
    Assert.Empty errors
    match bound with
    | [ BBlock [ BVarStmt(DeclaredLocal 0, _, _); BExpressionStmt(BVariable(LocalBinding 0, _)) ] ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``function parameters resolve as locals`` () =
    let bound, errors = resolveSource "fun f(a, b) { return a + b; }"
    Assert.Empty errors
    match bound with
    | [ BFunctionStmt(_, decl) ] ->
        match decl.Body with
        | [ BReturnStmt(_, Some(BBinary(BVariable(LocalBinding aSlot, _), _, BVariable(LocalBinding bSlot, _)))) ] ->
            Assert.Equal(0, aSlot)
            Assert.Equal(1, bSlot)
        | body -> failwith $"unexpected body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a nested function capturing an enclosing local resolves as an upvalue`` () =
    let source = "fun outer() {\n    var x mut = 1\n    fun inner() { return x; }\n    return inner\n}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ BFunctionStmt(_, outerDecl) ] ->
        match outerDecl.Body with
        | [ BVarStmt(DeclaredLocal xSlot, _, _); BFunctionStmt(_, innerDecl); _ ] ->
            Assert.Equal<UpvalueDescriptor list>([ { FromEnclosingLocal = true; Index = xSlot } ], innerDecl.Upvalues)
            match innerDecl.Body with
            | [ BReturnStmt(_, Some(BVariable(UpvalueBinding 0, _))) ] -> ()
            | body -> failwith $"unexpected inner body: %A{body}"
        | body -> failwith $"unexpected outer body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a doubly nested closure captures a grandparent local via an upvalue of an upvalue`` () =
    let source =
        "fun a() {\n"
        + "    var x mut = 1\n"
        + "    fun b() {\n"
        + "        fun c() { return x; }\n"
        + "        return c\n"
        + "    }\n"
        + "    return b\n"
        + "}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ BFunctionStmt(_, aDecl) ] ->
        match aDecl.Body with
        | [ BVarStmt(DeclaredLocal xSlot, _, _); BFunctionStmt(_, bDecl); _ ] ->
            // b captures x directly from a's locals.
            Assert.Equal<UpvalueDescriptor list>([ { FromEnclosingLocal = true; Index = xSlot } ], bDecl.Upvalues)
            match bDecl.Body with
            | [ BFunctionStmt(_, cDecl); _ ] ->
                // c captures x through b's own upvalue, not directly.
                Assert.Equal<UpvalueDescriptor list>([ { FromEnclosingLocal = false; Index = 0 } ], cDecl.Upvalues)
            | body -> failwith $"unexpected b body: %A{body}"
        | body -> failwith $"unexpected a body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``assigning to an immutable local is a compile error`` () =
    let _, errors = resolveSource "{ var x = 1\nx = 2; }"
    Assert.Single errors |> ignore
    Assert.Contains("immutable", errors.[0].Message)

[<Fact>]
let ``assigning to a mutable local has no error`` () =
    let _, errors = resolveSource "{ var x mut = 1\nx = 2; }"
    Assert.Empty errors

[<Fact>]
let ``assigning to an immutable global is a compile error regardless of declaration order`` () =
    // useX textually comes before x's own declaration -- the
    // pre-registration pass must still catch this.
    let _, errors = resolveSource "fun useX() { x = 2; }\nvar x = 1\n"
    Assert.Single errors |> ignore
    Assert.Contains("immutable", errors.[0].Message)

[<Fact>]
let ``redeclaring a variable in the same scope is an error`` () =
    let _, errors = resolveSource "{ var x = 1\nvar x = 2; }"
    Assert.Single errors |> ignore
    Assert.Contains("already declared", errors.[0].Message)

[<Fact>]
let ``shadowing in a nested scope is not an error`` () =
    let _, errors = resolveSource "{ var x = 1\n{ var x = 2\nx; } }"
    Assert.Empty errors

[<Fact>]
let ``redeclaring a global is an error`` () =
    let _, errors = resolveSource "var x = 1\nvar x = 2\n"
    Assert.Single errors |> ignore
    Assert.Contains("already declared", errors.[0].Message)

[<Fact>]
let ``a class can reference its own name from inside its own methods`` () =
    let bound, errors = resolveSource "class Duck {\n    quack() { return Duck; }\n}"
    Assert.Empty errors
    match bound with
    | [ BClassStmt(DeclaredGlobal "Duck", _, _, _, _, [ quackDecl ]) ] ->
        match quackDecl.Body with
        | [ BReturnStmt(_, Some(BVariable(GlobalBinding "Duck", _))) ] -> ()
        | body -> failwith $"unexpected method body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``self resolves inside a method`` () =
    let bound, errors = resolveSource "class Duck {\n    quack() { return self; }\n}"
    Assert.Empty errors
    match bound with
    | [ BClassStmt(_, _, _, _, _, [ quackDecl ]) ] ->
        match quackDecl.Body with
        | [ BReturnStmt(_, Some(BSelf(LocalBinding 0, _))) ] -> ()
        | body -> failwith $"unexpected method body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``self used outside a method is a compile error`` () =
    let _, errors = resolveSource "self"
    Assert.Single errors |> ignore
    Assert.Contains("outside of a method", errors.[0].Message)

[<Fact>]
let ``super resolves inside a method of a class with a superclass`` () =
    let source = "class A { greet() { return 1; } }\nclass B extends A {\n    greet() { return super.greet(); }\n}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ _; BClassStmt(_, _, Some(GlobalBinding "A", _), _, _, [ greetDecl ]) ] ->
        match greetDecl.Body with
        | [ BReturnStmt(_, Some(BCall(BSuper(LocalBinding 0, UpvalueBinding 0, _, methodName), []))) ] ->
            Assert.Equal("greet", methodName.Lexeme)
        | body -> failwith $"unexpected method body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``super used in a class without a superclass is a compile error`` () =
    let _, errors = resolveSource "class A {\n    greet() { return super.greet(); }\n}"
    Assert.Single errors |> ignore
    Assert.Contains("superclass", errors.[0].Message)

[<Fact>]
let ``super used outside any class is a compile error`` () =
    let _, errors = resolveSource "super.greet()"
    Assert.Single errors |> ignore
    Assert.Contains("superclass", errors.[0].Message)

[<Fact>]
let ``assigning to a function's own name is a compile error`` () =
    let _, errors = resolveSource "fun f() { }\nf = 1\n"
    Assert.Single errors |> ignore
    Assert.Contains("immutable", errors.[0].Message)

[<Fact>]
let ``assigning to a class's own name is a compile error`` () =
    let _, errors = resolveSource "class C { }\nC = 1\n"
    Assert.Single errors |> ignore
    Assert.Contains("immutable", errors.[0].Message)

[<Fact>]
let ``calling a native global like print needs no declaration and is not an error`` () =
    let bound, errors = resolveSource "print(1)"
    Assert.Empty errors
    match bound with
    | [ BExpressionStmt(BCall(BVariable(GlobalBinding "print", _), [ _ ])) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``assigning to a native global like print is a compile error`` () =
    let _, errors = resolveSource "print = 1"
    Assert.Single errors |> ignore
    Assert.Contains("immutable", errors.[0].Message)

[<Fact>]
let ``redeclaring the ambient print is a compile error`` () =
    let _, errors = resolveSource "var print = 1"
    Assert.Single errors |> ignore
    Assert.Contains("already declared", errors.[0].Message)

[<Fact>]
let ``a library native like concat is not ambient, so declaring it is fine`` () =
    // docs/PLAN-0.3.md Phase 6: only `print` is reserved; the rest of the
    // natives live in std.* modules a program must import.
    let _, errors = resolveSource "var concat = 1\nprint concat"
    Assert.Empty errors

[<Fact>]
let ``LocalCount includes self plus every declared local`` () =
    let bound, errors = resolveSource "class Duck {\n    quack(a) { var b = 1\nreturn a; }\n}"
    Assert.Empty errors
    match bound with
    | [ BClassStmt(_, _, _, _, _, [ quackDecl ]) ] -> Assert.Equal(3, quackDecl.LocalCount) // self, a, b
    | _ -> failwith $"unexpected shape: %A{bound}"

// docs/PLAN-0.2.md Phase 7: property/method visibility (`pub`/`mut`).

[<Fact>]
let ``property declarations resolve with their pub/mut flags carried through`` () =
    let bound, errors = resolveSource "class Duck {\n    var name\n    var energy mut\n    var species pub\n    var quacking pub mut\n}"
    Assert.Empty errors
    match bound with
    | [ BClassStmt(_, _, _, _, [ name; energy; species; quacking ], []) ] ->
        Assert.Equal("name", name.Name.Lexeme)
        Assert.False(name.IsPub)
        Assert.False(name.IsMutable)
        Assert.False(energy.IsPub)
        Assert.True(energy.IsMutable)
        Assert.True(species.IsPub)
        Assert.False(species.IsMutable)
        Assert.True(quacking.IsPub)
        Assert.True(quacking.IsMutable)
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a pub method's IsPub flag is carried into the bound method`` () =
    let bound, errors = resolveSource "class Duck {\n    pub quack() { return 1; }\n}"
    Assert.Empty errors
    match bound with
    | [ BClassStmt(_, _, _, _, _, [ quackDecl ]) ] -> Assert.True(quackDecl.IsPub)
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``self.x = value targeting an undeclared property is a compile-time error`` () =
    let _, errors = resolveSource "class Duck {\n    init() { self.name = \"x\"; }\n}"
    Assert.Single errors |> ignore
    Assert.Contains("not declared", errors.[0].Message)

[<Fact>]
let ``self.x = value targeting a property declared by an ancestor is not an error (protected-like)`` () =
    let source =
        "class Animal {\n    var name\n    init(n) { self.name = n; }\n}\nclass Dog extends Animal {\n    rename(n) { self.name = n; }\n}"
    let _, errors = resolveSource source
    Assert.Empty errors

[<Fact>]
let ``self.x = value targeting a method name is a distinct compile-time error`` () =
    let _, errors = resolveSource "class Duck {\n    quack() { return 1; }\n    init() { self.quack = 1; }\n}"
    Assert.Single errors |> ignore
    Assert.Contains("method, not a property", errors.[0].Message)

[<Fact>]
let ``a property and a method sharing the same name in one class is a compile-time error`` () =
    let _, errors = resolveSource "class Duck {\n    var quack\n    quack() { return 1; }\n}"
    Assert.Single errors |> ignore
    Assert.Contains("both a property and a method", errors.[0].Message)

[<Fact>]
let ``redeclaring the same property twice in one class is a compile-time error`` () =
    let _, errors = resolveSource "class Duck {\n    var name\n    var name mut\n}"
    Assert.Single errors |> ignore
    Assert.Contains("already declared", errors.[0].Message)

[<Fact>]
let ``redeclaring a property already declared by an ancestor is a compile-time error`` () =
    let source = "class Animal {\n    var name\n}\nclass Dog extends Animal {\n    var name mut\n}"
    let _, errors = resolveSource source
    Assert.Single errors |> ignore
    Assert.Contains("already declared by an ancestor", errors.[0].Message)

[<Fact>]
let ``external instance.x = value has no compile-time declared-property check`` () =
    // Unlike `self.x = value`, an external `instance.x = value` has no
    // statically-known class to check against -- deferred to a runtime
    // check instead (`vm/src/vm.cpp`'s SetProperty).
    let source = "class Duck {}\nvar d = Duck()\nd.name = 1"
    let _, errors = resolveSource source
    Assert.Empty errors

[<Fact>]
let ``indexing resolves the object and index sub-expressions`` () =
    let bound, errors = resolveSource "var v = [1, 2]\nv[0]"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "v", _, _)
        BExpressionStmt(BIndex(BVariable(GlobalBinding "v", _), BLiteral(IntValue(Is 0I)), _)) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``indexed assignment resolves obj, index, and value sub-expressions`` () =
    let bound, errors = resolveSource "var v = [1, 2]\nv[0] = 9"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "v", _, _)
        BExpressionStmt(BIndexSet(BVariable(GlobalBinding "v", _), BLiteral(IntValue(Is 0I)), BLiteral(IntValue(Is 9I)), _)) ] ->
        ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a slice with both bounds resolves the object and both bound sub-expressions`` () =
    // docs/PLAN-0.3.md decision 3.
    let bound, errors = resolveSource "var v = [1, 2, 3]\nv[1:2]"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "v", _, _)
        BExpressionStmt(BSlice(BVariable(GlobalBinding "v", _), Some(BLiteral(IntValue(Is 1I))), Some(BLiteral(IntValue(Is 2I))), _)) ] ->
        ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a slice with an omitted bound resolves it as None, not a placeholder expression`` () =
    let bound, errors = resolveSource "var v = [1, 2, 3]\nv[:2]"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "v", _, _)
        BExpressionStmt(BSlice(BVariable(GlobalBinding "v", _), None, Some(BLiteral(IntValue(Is 2I))), _)) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a lambda's parameters resolve as locals, same as a named function's`` () =
    let bound, errors = resolveSource "(a, b) -> a + b"
    Assert.Empty errors
    match bound with
    | [ BExpressionStmt(BLambda decl) ] ->
        Assert.Equal(2, decl.LocalCount) // a, b
        match decl.Body with
        | [ BReturnStmt(_, Some(BBinary(BVariable(LocalBinding aSlot, _), _, BVariable(LocalBinding bSlot, _)))) ] ->
            Assert.Equal(0, aSlot)
            Assert.Equal(1, bSlot)
        | body -> failwith $"unexpected lambda body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a lambda closes over an enclosing local exactly like a nested named function does`` () =
    let source = "fun outer() {\n    var x mut = 1\n    return (y) -> x + y\n}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ BFunctionStmt(_, outerDecl) ] ->
        match outerDecl.Body with
        | [ BVarStmt(DeclaredLocal xSlot, _, _); BReturnStmt(_, Some(BLambda lambdaDecl)) ] ->
            Assert.Equal<UpvalueDescriptor list>([ { FromEnclosingLocal = true; Index = xSlot } ], lambdaDecl.Upvalues)
            match lambdaDecl.Body with
            | [ BReturnStmt(_, Some(BBinary(BVariable(UpvalueBinding 0, _), _, BVariable(LocalBinding 0, _)))) ] -> ()
            | body -> failwith $"unexpected lambda body: %A{body}"
        | body -> failwith $"unexpected outer body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a lambda parameter is immutable, matching a named function's parameters`` () =
    let _, errors = resolveSource "(x) -> x = 1"
    Assert.Single errors |> ignore
    Assert.Contains("immutable", errors.[0].Message)

[<Fact>]
let ``cons desugars to a call of a synthetic two-parameter closure`` () =
    let bound, errors = resolveSource "[1 | xs]"
    Assert.Empty errors
    match bound with
    | [ BExpressionStmt(BCall(BLambda decl, [ BLiteral(IntValue(Is 1I)); _ ])) ] ->
        Assert.Equal("cons", decl.Name.Lexeme)
        Assert.Equal(2, decl.Parameters.Length)
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``cons's arguments are resolved in the enclosing scope, not the synthetic closure's`` () =
    // `xs` is an enclosing global, evaluated once and passed as the
    // closure's second argument -- not looked up from inside the
    // synthetic closure's own body.
    let bound, errors = resolveSource "var xs = [2, 3]\n[1 | xs]"
    Assert.Empty errors
    match bound with
    | [ _; BExpressionStmt(BCall(BLambda _, [ _; BVariable(GlobalBinding "xs", _) ])) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``list comprehension desugars to a call of a synthetic one-parameter closure`` () =
    let bound, errors = resolveSource "[x * 2 | x <- xs]"
    Assert.Empty errors
    match bound with
    | [ BExpressionStmt(BCall(BLambda decl, [ _ ])) ] ->
        Assert.Equal("comprehension", decl.Name.Lexeme)
        Assert.Equal(1, decl.Parameters.Length)
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a list comprehension's body captures an enclosing local as an upvalue, exactly like a lambda`` () =
    let source = "fun outer() {\n    var factor mut = 10\n    return [x + factor | x <- [1, 2, 3]]\n}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ BFunctionStmt(_, outerDecl) ] ->
        match outerDecl.Body with
        | [ BVarStmt(DeclaredLocal factorSlot, _, _)
            BReturnStmt(_, Some(BCall(BLambda comprehensionDecl, [ _ ]))) ] ->
            Assert.Equal<UpvalueDescriptor list>(
                [ { FromEnclosingLocal = true; Index = factorSlot } ],
                comprehensionDecl.Upvalues
            )
        | body -> failwith $"unexpected outer body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a list comprehension's bound variable shadows an outer variable of the same name without corrupting it`` () =
    let bound, errors = resolveSource "var n = 0\nvar shadow = [n | n <- [5, 6, 7]]\nn"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "n", _, _); BVarStmt(DeclaredGlobal "shadow", _, _); BExpressionStmt(BVariable(GlobalBinding "n", _)) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a later generator may reference an earlier generator's bound name in its own source`` () =
    // docs/PLAN-0.3.md decision 1 -- resolved as a direct consequence of
    // the nested-loop desugaring, not a separately implemented feature.
    let bound, errors = resolveSource "[y | x <- [1, 2], y <- [x]]"
    Assert.Empty errors

[<Fact>]
let ``a guard clause resolves without error and still desugars to a one-parameter closure call`` () =
    let bound, errors = resolveSource "[x | x <- xs | x > 0]"
    Assert.Empty errors
    match bound with
    | [ BExpressionStmt(BCall(BLambda decl, [ _ ])) ] -> Assert.Equal(1, decl.Parameters.Length)
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a spread element resolves to BSpread wrapping the resolved inner expression`` () =
    let bound, errors = resolveSource "var a = [1, 2]\n[...a]"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "a", _, _); BExpressionStmt(BVector [ BSpread(BVariable(GlobalBinding "a", _), _) ]) ] -> ()
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``spread and plain elements resolve independently within the same vector`` () =
    let bound, errors = resolveSource "var a = [1]\n[0, ...a, 2]"
    Assert.Empty errors
    match bound with
    | [ _; BExpressionStmt(BVector [ BLiteral(IntValue(Is 0I)); BSpread(BVariable(GlobalBinding "a", _), _); BLiteral(IntValue(Is 2I)) ]) ] ->
        ()
    | other -> failwith $"unexpected shape: %A{other}"

[<Fact>]
let ``vector length and append are internal-only primitives usable directly`` () =
    let bound, errors = resolveSource "var v = [1, 2]\n[3 | v]"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "v", _, _); BExpressionStmt(BCall(BLambda decl, _)) ] ->
        // The synthetic cons body's for-loop condition calls
        // InternalVectorLength on the closure's own $list parameter, and
        // its body calls InternalVectorAppend on $result.
        let rec containsInternalOps (stmts: BoundStmt list) =
            stmts
            |> List.exists (function
                | BForStmt(_, Some(BBinary(_, _, BVectorLengthInternal _)), _, body) -> containsInternalOps [ body ]
                | BExpressionStmt(BVectorAppendInternal _) -> true
                | BBlock inner -> containsInternalOps inner
                | _ -> false)
        Assert.True(containsInternalOps decl.Body)
    | _ -> failwith $"unexpected shape: %A{bound}"

// docs/PLAN-0.2.md Phase 8: mixins (`with`) and traits (`trait`/`use`).

[<Fact>]
let ``a used trait's method is inlined and resolves self relative to the using class`` () =
    let source = "trait Flyable {\n    pub fly() { return self; }\n}\nclass Duck {\n    use Flyable\n}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ BClassStmt(_, _, _, _, _, [ flyDecl ]) ] ->
        Assert.Equal("fly", flyDecl.Name.Lexeme)
        Assert.True(flyDecl.IsPub)
        match flyDecl.Body with
        | [ BReturnStmt(_, Some(BSelf(LocalBinding 0, _))) ] -> ()
        | body -> failwith $"unexpected inlined method body: %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a trait declaration produces no BoundStmt of its own`` () =
    let bound, errors = resolveSource "trait Flyable {\n    pub fly() { return 1; }\n}\nvar x = 1"
    Assert.Empty errors
    match bound with
    | [ BVarStmt(DeclaredGlobal "x", _, _) ] -> ()
    | _ -> failwith $"expected the trait to vanish entirely, got %A{bound}"

[<Fact>]
let ``two used traits declaring the same method name is a compile-time error`` () =
    let source =
        "trait A {\n    pub go() { return 1; }\n}\ntrait B {\n    pub go() { return 2; }\n}\nclass C {\n    use A, B\n}"
    let _, errors = resolveSource source
    Assert.Single errors |> ignore
    Assert.Contains("declared by both", errors.[0].Message)

[<Fact>]
let ``a used trait conflicting with the class's own superclass is a compile-time error`` () =
    let source =
        "class Base {\n    pub go() { return 1; }\n}\ntrait Helper {\n    pub go() { return 2; }\n}\nclass C extends Base {\n    use Helper\n}"
    let _, errors = resolveSource source
    Assert.Single errors |> ignore
    Assert.Contains("declared by both", errors.[0].Message)

[<Fact>]
let ``the class's own method overriding a used trait's is not a conflict`` () =
    let source = "trait Flyable {\n    pub fly() { return 1; }\n}\nclass Duck {\n    use Flyable\n    pub fly() { return 2; }\n}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ BClassStmt(_, _, _, _, _, [ flyDecl ]) ] ->
        match flyDecl.Body with
        | [ BReturnStmt(_, Some(BLiteral(IntValue(Is 2I)))) ] -> ()
        | body -> failwith $"expected the class's own override to win, got %A{body}"
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``a circular trait use is a compile-time error, not an infinite loop`` () =
    let source = "trait A {\n    use B\n}\ntrait B {\n    use A\n}\nclass C {\n    use A\n}"
    let _, errors = resolveSource source
    Assert.NotEmpty errors
    Assert.Contains(errors, fun e -> e.Message.Contains "Circular trait composition")

[<Fact>]
let ``using an undefined trait is a compile-time error`` () =
    let _, errors = resolveSource "class Duck {\n    use Ghost\n}"
    Assert.Single errors |> ignore
    Assert.Contains("Undefined trait", errors.[0].Message)

[<Fact>]
let ``two with-mixins declaring the same member is a compile-time error`` () =
    let source =
        "class A {\n    pub go() { return 1; }\n}\nclass B {\n    pub go() { return 2; }\n}\nclass C with A, B {}"
    let _, errors = resolveSource source
    Assert.Single errors |> ignore
    Assert.Contains("declared by both", errors.[0].Message)

[<Fact>]
let ``a with-mixin resolves as an ordinary variable reference for the Mixin opcode`` () =
    let source = "class Named {\n    pub greet() { return 1; }\n}\nclass Robot with Named {}"
    let bound, errors = resolveSource source
    Assert.Empty errors
    match bound with
    | [ _; BClassStmt(_, _, None, [ (GlobalBinding "Named", mixinToken) ], [], []) ] -> Assert.Equal("Named", mixinToken.Lexeme)
    | _ -> failwith $"unexpected shape: %A{bound}"

[<Fact>]
let ``self.x = value targeting a property declared only by a with-mixin is not an error`` () =
    let source =
        "class Named {\n    var label\n    init(l) { self.label = l; }\n}\nclass Robot with Named {\n    rename(l) { self.label = l; }\n}"
    let _, errors = resolveSource source
    Assert.Empty errors

// --- docs/PLAN-0.3.md decision 4: unused-variable/-parameter/-private-
// --- member warnings ---

[<Fact>]
let ``an unused local variable produces a warning, not an error`` () =
    let _, errors, warnings = resolveSourceWithWarnings "{\n    var x = 1\n}"
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("'x' is never used", warnings.[0].Message)

[<Fact>]
let ``an unused function parameter produces a warning`` () =
    let _, errors, warnings = resolveSourceWithWarnings "fun f(a, b) {\n    return a\n}\nf(1, 2)"
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("'b' is never used", warnings.[0].Message)

[<Fact>]
let ``a used local variable produces no warning`` () =
    let _, errors, warnings = resolveSourceWithWarnings "{\n    var x = 1\n    print x\n}"
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a variable that is only ever assigned, never read, still warns`` () =
    let _, errors, warnings = resolveSourceWithWarnings "{\n    var x mut = 1\n    x = 2\n}"
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("'x' is never used", warnings.[0].Message)

[<Fact>]
let ``a variable captured by a nested closure counts as used`` () =
    let source = "{\n    var x = 1\n    var f = () -> x\n    print f()\n}"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``an underscore-prefixed unused local or parameter is exempt from the warning`` () =
    let source = "fun f(_ignored) {\n    var _unused = 1\n    return 1\n}\nf(1)"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``an unused private property produces a warning`` () =
    let source = "class Widget {\n    var secret mut\n    init() {}\n}\nWidget()"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("Property 'secret' is never used", warnings.[0].Message)

[<Fact>]
let ``a property only ever assigned via self.x = value, never read, is still not warned about`` () =
    // Unlike a local variable (where a pure write doesn't count as "using"
    // it), `self.x = value` DOES count as usage for the property check --
    // both `Get` and `Set` populate the same `self.`-qualified tracking
    // set, a deliberate difference from the local/parameter check.
    let source = "class Widget {\n    var secret mut\n    init(v) { self.secret = v; }\n}\nWidget(1)"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a pub property is never warned about even if unused`` () =
    let source = "class Widget {\n    var secret pub mut\n    init(v) { self.secret = v; }\n}\nWidget(1)"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a property read only via self.x elsewhere in the class is not warned about`` () =
    let source =
        "class Widget {\n    var secret mut\n    init(v) { self.secret = v; }\n    reveal() { return self.secret; }\n    pub go() { return self.reveal(); }\n}\nprint Widget(1).go()"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``an unused private method produces a warning`` () =
    let source = "class Widget {\n    hidden() { return 1; }\n}\nWidget()"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("Method 'hidden' is never used", warnings.[0].Message)

[<Fact>]
let ``a pub method is never warned about even if unused`` () =
    let source = "class Widget {\n    pub hidden() { return 1; }\n}\nWidget()"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``init is exempt from the unused-method warning even though nothing ever calls it via self`` () =
    let source = "class Widget {\n    init() {}\n}\nWidget()"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a private method only ever called externally (not via self) still warns`` () =
    // Deliberate simplification (docs/PLAN-0.3.md decision 4): the check
    // only tracks `self.`-qualified references, so an external call alone
    // doesn't suppress the warning -- and such a call would fail at
    // runtime anyway, since a non-pub method can't be invoked from outside
    // its class.
    let source = "class Widget {\n    hidden() { return 1; }\n}\nvar w = Widget()\nw.hidden()"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("Method 'hidden' is never used", warnings.[0].Message)

// --- Extending the check to top-level (global) `var`/`fun` declarations,
// --- per repository-owner follow-up (not covered by the original
// --- AskUserQuestion round, which only asked about private class
// --- members) ---

[<Fact>]
let ``an unused top-level var produces a warning`` () =
    let _, errors, warnings = resolveSourceWithWarnings "var x = 1"
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("Variable 'x' is never used", warnings.[0].Message)

[<Fact>]
let ``an unused top-level fun produces a warning`` () =
    let _, errors, warnings = resolveSourceWithWarnings "fun f() {\n    return 1\n}"
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("Function 'f' is never used", warnings.[0].Message)

[<Fact>]
let ``a top-level var that's read elsewhere in the file produces no warning`` () =
    let _, errors, warnings = resolveSourceWithWarnings "var x = 1\nprint x"
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a top-level fun that's called elsewhere in the file produces no warning`` () =
    let _, errors, warnings = resolveSourceWithWarnings "fun f() {\n    return 1\n}\nprint f()"
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a top-level var that's only ever assigned, never read, still warns`` () =
    let _, errors, warnings = resolveSourceWithWarnings "var x mut = 1\nx = 2"
    Assert.Empty errors
    Assert.Single warnings |> ignore
    Assert.Contains("Variable 'x' is never used", warnings.[0].Message)

[<Fact>]
let ``an underscore-prefixed unused top-level var or fun is exempt`` () =
    let source = "var _x = 1\nfun _f() {\n    return 1\n}"
    let _, errors, warnings = resolveSourceWithWarnings source
    Assert.Empty errors
    Assert.Empty warnings

[<Fact>]
let ``a never-instantiated top-level class produces no warning -- classes aren't part of this check`` () =
    let _, errors, warnings = resolveSourceWithWarnings "class Widget {}"
    Assert.Empty errors
    Assert.Empty warnings

// --- docs/PLAN-0.4.md Phase 3: compile-time division-by-zero check ---

let private divisionErrors (source: string) =
    let _, errors, _ = resolveSourceWithWarnings source
    errors |> List.map (fun e -> e.Message)

let private acceptsDivision (source: string) = Assert.Empty(divisionErrors source)

let private rejectsDivision (expected: string) (source: string) =
    let errors = divisionErrors source
    Assert.Single errors |> ignore
    Assert.Contains(expected, errors.Head)

let private unproven = "Can't prove this divisor isn't zero"

[<Fact>]
let ``a non-zero literal divisor is accepted for all three operators`` () =
    acceptsDivision "print (7 / 2)\nprint (7 // 2)\nprint (7 % 2)\nprint (7 / 0.5)"

[<Fact>]
let ``a literal zero divisor is an error for all three operators`` () =
    for op in [ "/"; "//"; "%" ] do
        rejectsDivision "the divisor is the constant 0" $"print (7 {op} 0)"

[<Fact>]
let ``a constant expression folding to zero is an error`` () =
    rejectsDivision "the divisor is the constant 0" "print (7 / (3 - 3))"
    rejectsDivision "the divisor is the constant 0" "print (7 / (2 & 1 << 3))"

[<Fact>]
let ``an immutable global constant divisor is propagated, chains included`` () =
    acceptsDivision "var a = 3\nvar b = a * 2 - 1\nprint (100 // b)"
    rejectsDivision "the divisor is the constant 0" "var a = 3\nvar b = a - 3\nprint (100 // b)"

[<Fact>]
let ``an immutable local constant divisor is propagated`` () =
    acceptsDivision "fun f() {\n    var size = 10\n    return 95 % size\n}\nprint f()"
    rejectsDivision "the divisor is the constant 0" "fun f() {\n    var size = 0\n    return 95 % size\n}\nprint f()"

[<Fact>]
let ``a mutable divisor is never accepted, even guarded or constant-initialized`` () =
    rejectsDivision unproven "var d mut = 3\nprint (10 / d)"
    rejectsDivision unproven "var d mut = 3\nprint (d != 0 ? 10 / d : 0)"

[<Fact>]
let ``an unguarded parameter or call result is not accepted`` () =
    rejectsDivision unproven "fun f(d) {\n    return 10 / d\n}\nprint f(2)"
    rejectsDivision unproven "fun g() {\n    return 2\n}\nprint (10 / g())"

[<Fact>]
let ``a != 0 or == 0 guard in a ternary narrows the right branch`` () =
    acceptsDivision "fun f(d) {\n    return d != 0 ? 10 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return 0 != d ? 10 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return d == 0 ? 0 : 10 // d\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d) {\n    return d == 0 ? 10 / d : 0\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d) {\n    return d != 0 ? 0 : 10 / d\n}\nprint f(2)"

[<Fact>]
let ``equality with a non-zero constant narrows, with zero or another value does not`` () =
    acceptsDivision "fun f(d) {\n    return d == 5 ? 1 / d : 0\n}\nprint f(5)"
    rejectsDivision unproven "fun f(d) {\n    return d != 1 ? 10 / d : 0\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d, e) {\n    return e != 0 ? 10 / d : 0\n}\nprint f(2, 3)"

[<Fact>]
let ``orderings narrow when they exclude zero`` () =
    acceptsDivision "fun f(d) {\n    return d > 0 ? 1 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return d < 0 ? 1 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return d >= 1 ? 1 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return 0 < d ? 1 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return d <= 0 ? 0 : 1 / d\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d) {\n    return d >= 0 ? 1 / d : 0\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d) {\n    return d > -1 ? 1 / d : 0\n}\nprint f(2)"

[<Fact>]
let ``and, or and ! combine guards`` () =
    acceptsDivision "fun f(d) {\n    return d > 0 and 10 % d\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return d == 0 or 10 % d\n}\nprint f(2)"
    acceptsDivision "fun f(d) {\n    return !(d == 0) ? 1 / d : 0\n}\nprint f(2)"
    acceptsDivision "fun f(d, e) {\n    return d != 0 and e != 0 ? 1 / d + 1 / e : 0\n}\nprint f(2, 3)"
    acceptsDivision "fun f(d) {\n    return d < 0 or d > 0 ? 1 / d : 0\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d) {\n    return d != 0 or 10 / d\n}\nprint f(2)"
    rejectsDivision unproven "fun f(d, e) {\n    return d != 0 or e != 0 ? 1 / d : 0\n}\nprint f(2, 3)"

[<Fact>]
let ``a guard narrows into a lambda and a comprehension's own guard narrows its element`` () =
    acceptsDivision "fun f(d) {\n    return d != 0 ? ((x) -> x / d) : nil\n}\nprint f(2)"
    acceptsDivision "var xs = [1, 0, 4]\nprint [12 / x | x <- xs | x != 0]"
    rejectsDivision unproven "var xs = [1, 0, 4]\nprint [12 / x | x <- xs]"

[<Fact>]
let ``the elvis operator is never a guard, and its error is reported once`` () =
    rejectsDivision unproven "fun f(d) {\n    return (d != 0 ?: 10 / d)\n}\nprint f(2)"

[<Fact>]
let ``a non-number constant divisor compiles and is left to the vm`` () =
    acceptsDivision "print (1 / \"a\")\nprint (1 / nil)"

[<Fact>]
let ``a constant float zero divisor is a warning, not an error`` () =
    let _, errors, warnings = resolveSourceWithWarnings "print (1 / 0.0)\nprint (1 % 0.0)"
    Assert.Empty errors
    Assert.Equal(2, warnings.Length)
    Assert.All(warnings, fun w -> Assert.Contains("constant 0.0", w.Message))

[<Fact>]
let ``power is accepted with a non-zero base or a non-negative exponent`` () =
    acceptsDivision "fun f(x) {\n    return x ^ 2\n}\nprint f(3)"
    acceptsDivision "fun f(n) {\n    return 2 ^ n\n}\nprint f(3)"
    acceptsDivision "fun f(x, n) {\n    return n >= 0 ? x ^ n : 1\n}\nprint f(0, 3)"
    acceptsDivision "fun f(x, n) {\n    return x != 0 ? x ^ n : 0\n}\nprint f(2, -1)"
    rejectsDivision "doesn't raise 0 to a negative power" "fun f(x, n) {\n    return x ^ n\n}\nprint f(2, 3)"
    rejectsDivision "0 raised to a negative power" "print (0 ^ -1)"

[<Fact>]
let ``large constant powers are evaluated for the check`` () =
    acceptsDivision "print ((2 ^ 2000) / (2 ^ 1999))"
    rejectsDivision "the divisor is the constant 0" "print (1 / (0 ^ 5000000))"
    acceptsDivision "print (1 / ((-1) ^ 5000001))"

[<Fact>]
let ``short-circuit and ternary constants are evaluated for the check`` () =
    acceptsDivision "print (1 / (nil ?? 2))\nprint (1 / (true ? 3 : 0))\nprint (1 / (false or 4))"
    rejectsDivision "the divisor is the constant 0" "print (1 / (false ? 3 : 0))"
    rejectsDivision "the divisor is the constant 0" "print (1 / (true and 0))"
