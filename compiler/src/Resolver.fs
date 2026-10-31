/// Resolver: `Stmt` list -> `BoundStmt` list. The compile-time
/// lexical-scope/variable-slot pass `docs/PLAN-0.1.md`'s Phase 4 calls
/// for -- locals get compile-time-known stack slots instead of runtime
/// hashmap lookups, closures get upvalues, and this is where compile-time
/// immutability enforcement and self-referencing-class-name binding both
/// live, per that plan.
///
/// The scope/slot/upvalue algorithm (`findLocal`/`resolveUpvalue`/
/// `addUpvalue`, and the "bump scope depth by one before declaring
/// parameters" trick that keeps a function's own parameters from being
/// mistaken for globals) is `clox`'s, from *Crafting Interpreters* Part
/// III -- `poc` has no resolver at all to port from for this phase, so
/// this is new, not a port.
///
/// `self`/`super` are resolved through the exact same local/upvalue/
/// global machinery as any other name, by looking up the synthetic names
/// "self"/"super" -- a method's function context gets an implicit `self`
/// local at slot 0, and a class with a superclass gets a synthetic
/// `super` local in a scope wrapping all of its methods (mirroring `clox`
/// again, and mirroring `poc`'s own extra-`Environment`-layer trick for
/// `super`, just done at compile time instead of at class-declaration
/// time). Falling through all the way to an unresolved global for either
/// name is exactly the "used outside a method" / "no superclass" error
/// case, since neither name can ever be a real user-declared identifier
/// (both are reserved keywords -- `var self = 1` doesn't parse).
///
/// Global declaration order doesn't matter for compile-time immutability
/// checking: top-level `var`/`fun`/`class` names are pre-registered in a
/// first pass (`preRegisterGlobals`) before the real resolution pass
/// walks any bodies, so a function that references a global declared
/// later in the same file (ordinary, expected mutual-recursion style code)
/// still gets a fully-informed immutability check, not a guess. Nested
/// (non-top-level) declarations have no such ambiguity to begin with --
/// they must be declared before use in their own scope, same as `poc`'s
/// dynamic `Environment` already requires.
module Iqalox.Resolver

open System.Collections.Generic
open Iqalox.Token
open Iqalox.Ast
open Iqalox.Bound

type ResolveError = { Message: string; Token: Token }

/// `NameToken`/`Used` (`docs/PLAN-0.3.md` decision 4 -- unused-variable
/// warnings) are `None`/pre-`true` for the synthetic `self`/`super`
/// locals (never user-declared, never warned about) and `Some`/`false`
/// for everything `declareVariable` actually declares. Mutable so
/// `resolveReference`/`resolveUpvalue` can flip it in place the moment a
/// real read is seen, without a second pass over the tree.
type private LocalVar =
    { Name: string
      Slot: int
      IsMutable: bool
      Depth: int
      NameToken: Token option
      mutable Used: bool }
type private UpvalueEntry = { Name: string; Descriptor: UpvalueDescriptor; IsMutable: bool }

type private FunctionContext(enclosing: FunctionContext option) =
    member val ScopeDepth = 0 with get, set
    member val Locals = ResizeArray<LocalVar>() with get
    member val Upvalues = ResizeArray<UpvalueEntry>() with get
    member _.Enclosing = enclosing

let private findLocal (context: FunctionContext) (name: string) : LocalVar option =
    let mutable result = None
    let mutable i = context.Locals.Count - 1
    while result.IsNone && i >= 0 do
        if context.Locals.[i].Name = name then
            result <- Some context.Locals.[i]
        i <- i - 1
    result

let rec private addUpvalue (context: FunctionContext) (name: string) (descriptor: UpvalueDescriptor) (isMutable: bool) : int =
    match context.Upvalues |> Seq.tryFindIndex (fun u -> u.Descriptor = descriptor) with
    | Some i -> i
    | None ->
        context.Upvalues.Add { Name = name; Descriptor = descriptor; IsMutable = isMutable }
        context.Upvalues.Count - 1

and private resolveUpvalue (context: FunctionContext) (name: string) : (int * bool) option =
    match context.Enclosing with
    | None -> None
    | Some enclosing ->
        match findLocal enclosing name with
        | Some local ->
            // Capturing a local from an enclosing scope into a closure is
            // always treated as a real use (`docs/PLAN-0.3.md` decision
            // 4), regardless of whether the closure itself only reads or
            // only writes it -- distinguishing those would need threading
            // a read/write flag through every recursive hop here too, for
            // a case narrow enough not to be worth it.
            local.Used <- true
            let index = addUpvalue context name { FromEnclosingLocal = true; Index = local.Slot } local.IsMutable
            Some(index, local.IsMutable)
        | None ->
            match resolveUpvalue enclosing name with
            | Some(enclosingUpvalueIndex, isMutable) ->
                let index = addUpvalue context name { FromEnclosingLocal = false; Index = enclosingUpvalueIndex } isMutable
                Some(index, isMutable)
            | None -> None

/// Pre-registers every top-level `var`/`fun`/`class` name (with its
/// mutability) before any body is resolved, so a compile-time
/// immutability check against a global doesn't depend on where in the
/// file that global happens to be declared. Also where top-level
/// redeclaration is caught (`var x = 1 ... var x = 2`, matching `poc`'s
/// `Environment.define`'s "already declared" rule, just moved to compile
/// time here). `globalDecls` additionally records every top-level `var`/
/// `fun`'s own declaration token, keyed by name, for the unused-variable
/// warning (`docs/PLAN-0.3.md` decision 4) -- `class` is deliberately
/// left out (a top-level class's *members* get their own, separate
/// unused-property/-method check; "this class itself is never
/// instantiated anywhere in the file" wasn't part of what was asked for).
let private preRegisterGlobals
    (globals: Dictionary<string, bool>)
    (globalDecls: Dictionary<string, Token * string>)
    (errors: ResizeArray<ResolveError>)
    (stmts: Stmt list)
    =
    let register (name: Token) (isMutable: bool) (kind: string option) =
        if globals.ContainsKey name.Lexeme then
            errors.Add { Message = $"Variable '{name.Lexeme}' already declared."; Token = name }
        else
            globals.[name.Lexeme] <- isMutable
            match kind with
            | Some k -> globalDecls.[name.Lexeme] <- (name, k)
            | None -> ()

    for stmt in stmts do
        match stmt with
        | VarStmt(name, _, isMutable) -> register name isMutable (Some "Variable")
        | FunctionStmt decl -> register decl.Name false (Some "Function")
        | ClassStmt(name, _, _, _, _, _) -> register name false None
        | _ -> ()

/// A class or trait's own property/method declarations, flattened into
/// name-keyed maps -- the unit `checkNoConflicts`/`overrideWith`/
/// `mergeAll` below operate on, for `docs/PLAN-0.2.md` decision 12's
/// compile-time composition (traits via `use`, mixins via `with`).
type MemberSet = { Properties: Map<string, PropertyDecl>; Methods: Map<string, FunctionDecl> }

let private emptyMemberSet: MemberSet = { Properties = Map.empty; Methods = Map.empty }

let private memberNames (m: MemberSet) : Set<string> =
    Set.union (m.Properties |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (m.Methods |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

/// `winner`'s own declarations take priority over anything `loser`
/// contributes under the same name -- used for "a class's/trait's own
/// body always overrides whatever composition brings in," never itself a
/// conflict (only *sibling* composed sources conflicting with each other
/// is, per `checkNoConflicts`).
let private overrideWith (winner: MemberSet) (loser: MemberSet) : MemberSet =
    { Properties = Map.fold (fun acc k v -> Map.add k v acc) loser.Properties winner.Properties
      Methods = Map.fold (fun acc k v -> Map.add k v acc) loser.Methods winner.Methods }

let private mergeAll (sets: MemberSet list) : MemberSet = sets |> List.fold overrideWith emptyMemberSet

/// docs/PLAN-0.2.md open questions 1-2, both resolved the same way by the
/// repository owner: two composed sources (used traits, a used trait vs.
/// the class's own superclass, `with`-mixins, a mixin vs. the superclass,
/// ...) sharing a member name is a compile-time error -- there is no
/// `insteadof`/`as`-style resolution syntax (not in `langspec/
/// SYNTAX_GRAMMAR.md`), so the only way out is renaming, or declaring the
/// member directly on the composing class/trait itself (which always
/// wins, silently, via `overrideWith` -- never itself flagged here).
let private checkNoConflicts
    (errors: ResizeArray<ResolveError>)
    (anchor: Token)
    (contextDescription: string)
    (sources: (string * MemberSet) list)
    : unit =
    let rec loop =
        function
        | [] -> ()
        | (name1: string, m1: MemberSet) :: rest ->
            for name2, m2 in rest do
                for overlapping in Set.intersect (memberNames m1) (memberNames m2) do
                    errors.Add
                        { Message =
                            $"'{overlapping}' is declared by both '{name1}' and '{name2}' in {contextDescription} -- rename one, or declare '{overlapping}' directly to resolve the conflict."
                          Token = anchor }
            loop rest
    loop sources

/// A single class or trait body's own declarations as a `MemberSet` --
/// also where a duplicate property, duplicate method, or a property/
/// method name collision *within that one body* is caught (unrelated to
/// `checkNoConflicts`, which is about *separate* composed sources
/// conflicting with each other, not one body's own internal duplicates).
let private buildOwnMemberSet
    (errors: ResizeArray<ResolveError>)
    (contextDescription: string)
    (properties: PropertyDecl list)
    (methods: FunctionDecl list)
    : MemberSet =
    let methodMap =
        methods
        |> List.fold
            (fun (acc: Map<string, FunctionDecl>) (m: FunctionDecl) ->
                if acc.ContainsKey m.Name.Lexeme then
                    errors.Add
                        { Message = $"Method '{m.Name.Lexeme}' already declared in {contextDescription}."
                          Token = m.Name }
                    acc
                else
                    acc.Add(m.Name.Lexeme, m))
            Map.empty
    let propMap =
        properties
        |> List.fold
            (fun (acc: Map<string, PropertyDecl>) (p: PropertyDecl) ->
                if acc.ContainsKey p.Name.Lexeme then
                    errors.Add
                        { Message = $"Property '{p.Name.Lexeme}' already declared in {contextDescription}."
                          Token = p.Name }
                    acc
                elif methodMap.ContainsKey p.Name.Lexeme then
                    errors.Add
                        { Message = $"'{p.Name.Lexeme}' is declared as both a property and a method in {contextDescription}."
                          Token = p.Name }
                    acc
                else
                    acc.Add(p.Name.Lexeme, p))
            Map.empty
    { Properties = propMap; Methods = methodMap }

/// One top-level `trait T { ... }`'s own raw declarations, keyed by name
/// -- built by `preRegisterTraits` before any class's `use` is resolved,
/// exactly like `preRegisterClasses` does for classes. A trait has no
/// `extends`/`with` of its own (`langspec/SYNTAX_GRAMMAR.md`'s
/// `traitDecl` grammar), only possibly-nested `use` of other traits.
type TraitInfo =
    { Name: Token
      Properties: PropertyDecl list
      Methods: FunctionDecl list
      UsedTraitNames: Token list }

let private preRegisterTraits (traits: Dictionary<string, TraitInfo>) (errors: ResizeArray<ResolveError>) (stmts: Stmt list) =
    for stmt in stmts do
        match stmt with
        | TraitStmt(name, properties, methods, usedTraits) ->
            if traits.ContainsKey name.Lexeme then
                errors.Add { Message = $"Trait '{name.Lexeme}' already declared."; Token = name }
            else
                traits.[name.Lexeme] <-
                    { Name = name; Properties = properties; Methods = methods; UsedTraitNames = usedTraits }
        | _ -> ()

/// Recursively flattens trait `traitName`'s own members plus everything
/// its own nested `use` clauses pull in, applying `checkNoConflicts` at
/// every level (two of a trait's own nested-used traits conflicting is
/// exactly as much an error as two of a *class*'s used traits
/// conflicting -- same rule, just one level higher). `visiting` turns a
/// `trait A { use B }` / `trait B { use A }` cycle into a clean error
/// instead of unbounded recursion; `cache` means a diamond (two different
/// traits both using a third) only flattens that third trait once.
let rec private flattenTrait
    (traits: Dictionary<string, TraitInfo>)
    (errors: ResizeArray<ResolveError>)
    (cache: Dictionary<string, MemberSet>)
    (visiting: Set<string>)
    (usageToken: Token)
    (traitName: string)
    : MemberSet =
    match cache.TryGetValue traitName with
    | true, cached -> cached
    | false, _ ->
        if visiting.Contains traitName then
            errors.Add { Message = $"Circular trait composition involving '{traitName}'."; Token = usageToken }
            emptyMemberSet
        else
            match traits.TryGetValue traitName with
            | false, _ ->
                errors.Add { Message = $"Undefined trait '{traitName}'."; Token = usageToken }
                emptyMemberSet
            | true, info ->
                let visiting = visiting.Add traitName
                let nested =
                    info.UsedTraitNames
                    |> List.map (fun t -> t.Lexeme, flattenTrait traits errors cache visiting t t.Lexeme)
                checkNoConflicts errors info.Name $"trait '{traitName}'" nested
                let ownSet = buildOwnMemberSet errors $"trait '{traitName}'" info.Properties info.Methods
                let result = overrideWith ownSet (nested |> List.map snd |> mergeAll)
                cache.[traitName] <- result
                result

/// One top-level class's own declared members, keyed by name -- built by
/// `preRegisterClasses` before any method body is resolved, exactly like
/// `preRegisterGlobals` above, so forward/mutual references between
/// classes (and looking up an ancestor's/mixin's declared properties
/// while resolving a *subclass*'s methods) don't depend on file order.
/// `docs/PLAN-0.2.md` decision 8's addendum: every property a class uses
/// must be declared somewhere in its own body, an ancestor's, a `with`
/// mixin's, or a `use`d trait's -- this is the compile-time table that
/// check is answered against. `InlinedProperties`/`InlinedMethods`
/// already have every `use`d trait's own members merged in (per
/// `flattenTrait` above) -- `Resolver.fs`'s `ClassStmt` case resolves
/// these two lists directly and needs no further `use` handling of its
/// own at all; only `SuperclassName`/`MixinNames` remain to walk for
/// `effectiveClassMembers` below, since those two compose at *runtime*
/// (`Inherit`/`Mixin`), not at compile time.
type ClassInfo =
    { Name: Token
      SuperclassName: string option
      MixinNames: string list
      /// The class's own literal body only -- never conflict-checked
      /// against a used trait (a class's own explicit declaration always
      /// silently overrides a trait's, matching PHP's real semantics),
      /// but *does* still need checking against the superclass/mixins in
      /// `effectiveClassMembers` (a property redeclaration is still an
      /// error there; a method override is not).
      OwnLiteral: MemberSet
      /// Everything brought in via `use`, already flattened/conflict-
      /// checked against `OwnLiteral`'s siblings (other used traits) --
      /// kept separate from `OwnLiteral` because a trait-contributed
      /// member conflicting with the superclass/a mixin *is* one of
      /// open question 1's named conflict scenarios ("a trait and the
      /// class's own superclass"), unlike the class's own literal
      /// declarations, which always win silently over anything composed.
      TraitContributed: MemberSet
      /// `OwnLiteral` merged over `TraitContributed` -- exactly what
      /// `Resolver.fs`'s `ClassStmt` case resolves as this class's own
      /// properties/methods, needing no further `use` handling at all.
      InlinedProperties: PropertyDecl list
      InlinedMethods: FunctionDecl list
      /// True for a class brought in via `import` (`docs/PLAN-0.3.md`
      /// Phase 5): its `OwnLiteral` is the exporter's already-computed
      /// *effective* member set, standing in for the whole inheritance
      /// chain, and it's never checked for unused members (those belong to
      /// the module that declared it).
      IsImported: bool }

let private preRegisterClasses
    (classes: Dictionary<string, ClassInfo>)
    (traits: Dictionary<string, TraitInfo>)
    (traitCache: Dictionary<string, MemberSet>)
    (errors: ResizeArray<ResolveError>)
    (stmts: Stmt list)
    =
    for stmt in stmts do
        match stmt with
        | ClassStmt(name, superclassExpr, mixinExprs, properties, methods, usedTraits) ->
            let superclassName =
                superclassExpr
                |> Option.map (function
                    | Variable superName -> superName.Lexeme
                    | other -> failwith $"unreachable: superclass is always a bare Variable, got %A{other}")
            let mixinNames =
                mixinExprs
                |> List.map (function
                    | Variable mixinName -> mixinName.Lexeme
                    | other -> failwith $"unreachable: a mixin is always a bare Variable, got %A{other}")

            let ownLiteral = buildOwnMemberSet errors $"class '{name.Lexeme}'" properties methods

            // No `use` at all is by far the common case (every class
            // before this phase existed) -- skip the `Map`-keyed merge
            // entirely then, so a plain class's property/method order in
            // the emitted bytecode is *exactly* its own declaration
            // order, unchanged from before `use`/`with` existed. Only a
            // class that actually composes traits pays for (and needs)
            // the conflict-checked merge below, whose result is ordered
            // by name (`Map.toList`) rather than by declaration site --
            // a reasonable, deterministic tradeoff for genuinely new
            // functionality with no prior ordering to preserve.
            let traitContributed, inlinedProperties, inlinedMethods =
                if usedTraits.IsEmpty then
                    emptyMemberSet, properties, methods
                else
                    let usedTraitSets =
                        usedTraits
                        |> List.map (fun t -> t.Lexeme, flattenTrait traits errors traitCache Set.empty t t.Lexeme)
                    checkNoConflicts errors name $"class '{name.Lexeme}'" usedTraitSets
                    let traitContributed = usedTraitSets |> List.map snd |> mergeAll
                    let effective = overrideWith ownLiteral traitContributed
                    traitContributed,
                    (effective.Properties |> Map.toList |> List.map snd),
                    (effective.Methods |> Map.toList |> List.map snd)

            classes.[name.Lexeme] <-
                { Name = name
                  SuperclassName = superclassName
                  MixinNames = mixinNames
                  OwnLiteral = ownLiteral
                  TraitContributed = traitContributed
                  InlinedProperties = inlinedProperties
                  InlinedMethods = inlinedMethods
                  IsImported = false }
        | _ -> ()

/// Recursively computes class `className`'s full effective member set --
/// its own body plus its used traits (already flattened into
/// `InlinedProperties`/`InlinedMethods`), plus its superclass's and every
/// `with`-mixin's own effective sets (each resolved the same way,
/// transitively). `checkNoConflicts` applies to the superclass and every
/// mixin as siblings -- decision 12's "closer to real multiple
/// inheritance" without full C3 (docs/PLAN-0.2.md open question 2,
/// deferred) still needs *some* answer for "what if two mixins (or a
/// mixin and the superclass) disagree," and the repository owner's Q1
/// answer (compile error on any trait conflict) is applied symmetrically
/// here rather than picking a silent copy-order winner for mixins alone.
/// The class's own body (already folded into `InlinedProperties`/
/// `InlinedMethods`) always overrides any of this, same as `use`.
let rec private effectiveClassMembers
    (classes: Dictionary<string, ClassInfo>)
    (errors: ResizeArray<ResolveError>)
    (cache: Dictionary<string, MemberSet>)
    (visiting: Set<string>)
    (usageToken: Token)
    (className: string)
    : MemberSet =
    match cache.TryGetValue className with
    | true, cached -> cached
    | false, _ ->
        if visiting.Contains className then
            errors.Add { Message = $"Circular class hierarchy involving '{className}'."; Token = usageToken }
            emptyMemberSet
        else
            match classes.TryGetValue className with
            | false, _ -> emptyMemberSet // an undeclared name here is caught elsewhere (Inherit/Mixin's own runtime "must be a class" check)
            | true, info ->
                let visiting = visiting.Add className
                let superSet =
                    info.SuperclassName
                    |> Option.map (fun s -> s, effectiveClassMembers classes errors cache visiting info.Name s)
                let mixinSets =
                    info.MixinNames |> List.map (fun m -> m, effectiveClassMembers classes errors cache visiting info.Name m)
                let composedSources = (superSet |> Option.toList) @ mixinSets
                checkNoConflicts errors info.Name $"class '{className}'" composedSources
                let composedMerged = composedSources |> List.map snd |> mergeAll

                // `TraitContributed` is checked against the composed
                // (superclass/mixin) set exactly like any other pair of
                // composed sources conflicting -- open question 1 names
                // "a trait and the class's own superclass" as one of its
                // own conflict scenarios, so a trait-provided member is
                // never allowed to silently win over (or lose to) an
                // ancestor/mixin, unlike the class's own literal body.
                checkNoConflicts errors info.Name $"class '{className}'" [ $"class '{className}' (used traits)", info.TraitContributed; "its ancestors/mixins", composedMerged ]

                // The class's own *literal* declarations always win --
                // silently for methods (ordinary polymorphism, unchanged
                // since 0.1), but a redeclared *property* still has no
                // override semantics to speak of (decision 8/9's
                // write-once model has no notion of "which declaration
                // wins"), so it stays the compile-time error
                // `docs/PLAN-0.2.md` Phase 7 originally established for
                // the superclass-only case, now applied to mixins too for
                // the same reason.
                let inheritedMerged = overrideWith info.TraitContributed composedMerged
                for propName in Set.intersect (info.OwnLiteral.Properties |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (memberNames inheritedMerged) do
                    errors.Add
                        { Message = $"Property '{propName}' already declared by an ancestor/mixin of class '{className}'."
                          Token = info.Name }
                let result = overrideWith info.OwnLiteral inheritedMerged
                cache.[className] <- result
                result

/// Runs `effectiveClassMembers` for every registered class once, so every
/// conflict is reported exactly once (subsequent lookups from
/// `findDeclaredProperty`/`findDeclaredMethod` during the main resolve
/// pass just hit this cache, never re-triggering `checkNoConflicts`).
let private computeAllEffectiveClassMembers
    (classes: Dictionary<string, ClassInfo>)
    (errors: ResizeArray<ResolveError>)
    : Dictionary<string, MemberSet> =
    let cache = Dictionary<string, MemberSet>()
    for KeyValue(className, info) in classes do
        effectiveClassMembers classes errors cache Set.empty info.Name className |> ignore
    cache

/// Looks `propName` up in `className`'s full effective member set (own
/// body, used traits, superclass, and mixins, all already flattened by
/// `computeAllEffectiveClassMembers`) -- mirrors decision 10's internal-
/// access rule ("self.x" resolves against whichever source in the
/// composition actually declared `x`), per the now-resolved §2.3
/// protected-like reading extended to mixins/traits the same way.
let private findDeclaredProperty
    (effectiveMembers: Dictionary<string, MemberSet>)
    (className: string)
    (propName: string)
    : PropertyDecl option =
    match effectiveMembers.TryGetValue className with
    | true, m -> m.Properties.TryFind propName
    | false, _ -> None

/// Same lookup, for method names -- used only to give a more specific
/// "that's a method, not a property" diagnostic when a `self.x = value`
/// targets a declared method instead of an undeclared name entirely
/// (decision 8's addendum doesn't cover this case explicitly; a
/// dedicated message is just better engineering, not a new design axis).
let private findDeclaredMethod (effectiveMembers: Dictionary<string, MemberSet>) (className: string) (methodName: string) : bool =
    match effectiveMembers.TryGetValue className with
    | true, m -> m.Methods.ContainsKey methodName
    | false, _ -> false

/// `_`-prefixed names are exempt from every unused check (`docs/PLAN-0.3.md`
/// decision 4, open question 3).
let private isExemptFromUnusedCheckName (lexeme: string) = lexeme.StartsWith "_"

type private Resolver
    (
        globals: Dictionary<string, bool>,
        globalDecls: Dictionary<string, Token * string>,
        aliases: Dictionary<string, string>,
        classes: Dictionary<string, ClassInfo>,
        effectiveMembers: Dictionary<string, MemberSet>
    ) =
    let errors = ResizeArray<ResolveError>()
    let mutable context = FunctionContext(None)
    // The name of the class whose method body is currently being resolved
    // -- `None` outside any method. Used only by `Set(SelfExpr, ...)`'s
    // decision-8-addendum check (`self.x = value` must target a property
    // declared somewhere in this class's own hierarchy); reading `self.x`
    // needs no such check (`Get` is ambiguous between a property read and
    // a bound-method fetch, so it's left to the existing runtime
    // "Undefined property" fallback, same as before this phase).
    let mutable currentClassName: string option = None

    let error (token: Token) (message: string) = errors.Add { Message = message; Token = token }

    // `docs/PLAN-0.3.md` decision 4: a compile-time warning, not an
    // error -- collected separately from `errors` so `resolve`'s caller
    // can print these without affecting `iqaloxc`'s exit status.
    let warnings = ResizeArray<ResolveError>()
    let warn (token: Token) (message: string) = warnings.Add { Message = message; Token = token }

    // Every property/method name seen on the right-hand side of `self.x`
    // (`Get`) or `self.x = value` (`Set`) anywhere in the whole program --
    // deliberately a single global set of *names*, not scoped per class,
    // trading a theoretical false negative (an unused private member in
    // class A named the same as a genuinely-used private member in
    // unrelated class B) for avoiding a second, class-scoped tracking
    // structure threaded through every method resolution. Never produces
    // a false positive, which is the safer failure mode for a first-pass
    // lint. Checked against each class's own literal member declarations
    // in `CheckUnusedClassMembers` once the whole program has resolved.
    let usedSelfNames = HashSet<string>()

    // Every top-level global name (`var`/`fun`) seen on the read side of
    // `resolveReference` anywhere in the program -- same read-vs-write
    // rule as a local (`markUsed` below), so a global that's only ever
    // assigned and never read still warns, same as a local would.
    // Checked in `CheckUnusedGlobals` against `globalDecls` once the
    // whole program has resolved.
    let usedGlobalNames = HashSet<string>()

    let beginScope () = context.ScopeDepth <- context.ScopeDepth + 1

    // A `_`-prefixed name is exempt (decision 4's open question 3's
    // proposed default), mirroring the existing `_` ignore-operator's
    // "explicitly don't care about this" convention.
    let isExemptFromUnusedCheck (lexeme: string) = lexeme.StartsWith "_"

    let checkUnusedLocals (depth: int) =
        for local in context.Locals do
            if local.Depth = depth && not local.Used then
                match local.NameToken with
                | Some token when not (isExemptFromUnusedCheck local.Name) ->
                    warn token $"Variable '{local.Name}' is never used."
                | _ -> () // None marks a synthetic self/super local -- never warned about

    let endScope () =
        checkUnusedLocals context.ScopeDepth
        context.ScopeDepth <- context.ScopeDepth - 1
        while context.Locals.Count > 0 && context.Locals.[context.Locals.Count - 1].Depth > context.ScopeDepth do
            context.Locals.RemoveAt(context.Locals.Count - 1)

    let resolveReference (name: string) (markUsed: bool) : VariableBinding * bool option =
        match findLocal context name with
        | Some local ->
            if markUsed then
                local.Used <- true
            LocalBinding local.Slot, Some local.IsMutable
        | None ->
            match resolveUpvalue context name with
            | Some(index, isMutable) -> UpvalueBinding index, Some isMutable
            | None ->
                match globals.TryGetValue name with
                | true, isMutable ->
                    if markUsed then
                        usedGlobalNames.Add name |> ignore
                    // An imported name binds to the exporting module's
                    // qualified global, not to its local (possibly aliased)
                    // spelling (`docs/PLAN-0.3.md` Phase 5).
                    let target =
                        match aliases.TryGetValue name with
                        | true, qualified -> qualified
                        | false, _ -> name
                    GlobalBinding target, Some isMutable
                | false, _ -> GlobalBinding name, None

    let declareVariable (name: Token) (isMutable: bool) : DeclaredBinding =
        if context.ScopeDepth = 0 then
            // Pre-registered by preRegisterGlobals already -- nothing left
            // to check or insert here.
            DeclaredGlobal name.Lexeme
        else
            let alreadyInScope =
                context.Locals |> Seq.exists (fun l -> l.Depth = context.ScopeDepth && l.Name = name.Lexeme)
            if alreadyInScope then
                error name $"Variable '{name.Lexeme}' already declared in this scope."
            let slot = context.Locals.Count
            context.Locals.Add
                { Name = name.Lexeme
                  Slot = slot
                  IsMutable = isMutable
                  Depth = context.ScopeDepth
                  NameToken = Some name
                  Used = false }
            DeclaredLocal slot

    member this.Errors = List.ofSeq errors
    member this.WasGlobalRead(name: string) = usedGlobalNames.Contains name
    member this.Warnings = List.ofSeq warnings

    /// Warns on any non-`pub` property/method declared directly in a
    /// class's own literal body (`OwnLiteral` -- trait/superclass/mixin-
    /// contributed members are never checked here, since those compose
    /// into potentially many classes and "unused by this one particular
    /// class" isn't a meaningful signal for them) that `usedSelfNames`
    /// never saw a `self.`-qualified reference to anywhere in the whole
    /// program. `init` is always exempt (`docs/PLAN-0.2.md` decision 11 --
    /// externally callable regardless of its own `pub`/private
    /// annotation, so "unused" doesn't apply to it), same as a `_`-prefixed
    /// name. Must run after every `ClassStmt`/method body in the program
    /// has been resolved, so `usedSelfNames` is fully populated first.
    member this.CheckUnusedClassMembers() =
        for KeyValue(_, info) in classes |> Seq.filter (fun kv -> not kv.Value.IsImported) do
            for KeyValue(propName, prop) in info.OwnLiteral.Properties do
                if not prop.IsPub && not (isExemptFromUnusedCheck propName) && not (usedSelfNames.Contains propName) then
                    warn prop.Name $"Property '{propName}' is never used."
            for KeyValue(methodName, meth) in info.OwnLiteral.Methods do
                if
                    not meth.IsPub
                    && methodName <> "init"
                    && not (isExemptFromUnusedCheck methodName)
                    && not (usedSelfNames.Contains methodName)
                then
                    warn meth.Name $"Method '{methodName}' is never used."

    /// Warns on any top-level `var`/`fun` name in `globalDecls` that
    /// `usedGlobalNames` never saw a real read of anywhere in the
    /// program -- same read-vs-write rule as a local (assigning to a
    /// global without ever reading it back still warns). `exemptNames`
    /// lets the caller carve out names that shouldn't be checked at all
    /// even though they're ordinary top-level declarations -- `Program.fs`
    /// uses this for the prelude's own `map`/`filter`/`reduce`/`sort`/
    /// `elementwise`, textually merged in ahead of the user's own source
    /// (`docs/PLAN-0.2.md` Phase 5): a user program that only calls
    /// `map` shouldn't be warned that `sort` is "unused" when it never
    /// asked for `sort` to be declared in the first place. Must run after
    /// the whole program has resolved, same as `CheckUnusedClassMembers`.
    member this.CheckUnusedGlobals(exemptNames: Set<string>) =
        for KeyValue(name, (token, kind)) in globalDecls do
            if
                not (isExemptFromUnusedCheck name)
                && not (exemptNames.Contains name)
                && not (usedGlobalNames.Contains name)
            then
                warn token $"{kind} '{name}' is never used."

    member this.ResolveStmt(stmt: Stmt) : BoundStmt =
        match stmt with
        | Block statements -> BBlock(this.ResolveBlock statements)
        | ExpressionStmt expr -> BExpressionStmt(this.ResolveExpr expr)
        | VarStmt(name, initializer, isMutable) ->
            // Resolve the initializer *before* declaring the name -- `var
            // x = x` must see whatever `x` (if any) already existed in an
            // enclosing scope, matching poc's Environment-based semantics
            // (the initializer is evaluated before `environment.define`
            // runs).
            let boundInit = initializer |> Option.map this.ResolveExpr
            let binding = declareVariable name isMutable
            BVarStmt(binding, name, boundInit)
        | ForStmt(initializer, condition, increment, body) ->
            beginScope ()
            let boundInit = initializer |> Option.map this.ResolveStmt
            let boundCond = condition |> Option.map this.ResolveExpr
            let boundIncr = increment |> Option.map this.ResolveExpr
            let boundBody = this.ResolveStmt body
            endScope ()
            BForStmt(boundInit, boundCond, boundIncr, boundBody)
        | FunctionStmt decl ->
            // The function's own name is declared before its body is
            // resolved (unlike VarStmt) so recursive calls to itself
            // resolve correctly.
            let binding = declareVariable decl.Name false
            BFunctionStmt(binding, this.ResolveFunction(decl, isMethod = false))
        | ReturnStmt(keyword, value) -> BReturnStmt(keyword, value |> Option.map this.ResolveExpr)
        | ClassStmt(name, superclassExpr, mixinExprs, properties, methods, _) ->
            let binding = declareVariable name false

            let boundSuperclass =
                superclassExpr
                |> Option.map (function
                    | Variable superName ->
                        let refBinding, _ = resolveReference superName.Lexeme true
                        refBinding, superName
                    | other -> failwith $"unreachable: superclass is always a bare Variable, got %A{other}")

            let boundMixins: (VariableBinding * Token) list =
                mixinExprs
                |> List.map (function
                    | Variable mixinName ->
                        let refBinding, _ = resolveReference mixinName.Lexeme true
                        refBinding, mixinName
                    | other -> failwith $"unreachable: a mixin is always a bare Variable, got %A{other}")

            if boundSuperclass.IsSome then
                beginScope ()
                // A synthetic `super` local, resolvable (as an upvalue)
                // from every method that references it -- see the module
                // doc comment.
                context.Locals.Add
                    { Name = "super"
                      Slot = context.Locals.Count
                      IsMutable = false
                      Depth = context.ScopeDepth
                      NameToken = None
                      Used = true }

            // `classes.[name.Lexeme]`'s Inlined*/* already has this
            // class's own body merged with every `use`d trait's members
            // (`preRegisterClasses`/`flattenTrait`) -- resolved here
            // exactly as if the user had written them directly, so a
            // trait method's `self`/`super` naturally resolve relative to
            // *this* class, matching PHP's own trait semantics (a trait
            // has no inheritance identity of its own). `preRegisterClasses`
            // only walks *top-level* statements (mirroring
            // `preRegisterGlobals`'s own pre-existing scoping limit), so a
            // nested class -- already an edge case nothing exercises --
            // falls back to its own raw, un-inlined `properties`/`methods`
            // instead of crashing; it simply doesn't get `use`/`with`
            // composition, exactly like it didn't get decision 8's
            // declared-property checking before this phase either.
            let inlinedProperties, inlinedMethods =
                match classes.TryGetValue name.Lexeme with
                | true, info -> info.InlinedProperties, info.InlinedMethods
                | false, _ -> properties, methods
            let enclosingClassName = currentClassName
            currentClassName <- Some name.Lexeme
            let boundMethods = inlinedMethods |> List.map (fun m -> this.ResolveFunction(m, isMethod = true))
            currentClassName <- enclosingClassName

            if boundSuperclass.IsSome then
                endScope ()

            let boundProperties: BoundPropertyDecl list =
                inlinedProperties
                |> List.map (fun p -> ({ Name = p.Name; IsPub = p.IsPub; IsMutable = p.IsMutable }: BoundPropertyDecl))

            BClassStmt(binding, name, boundSuperclass, boundMixins, boundProperties, boundMethods)
        | PubStmt(keyword, declaration) ->
            // A top-level `pub` is unwrapped before resolution ever starts
            // (`resolveInternal`), so reaching one here means it was nested.
            error keyword "'pub' is only allowed on top-level declarations."
            this.ResolveStmt declaration
        | ImportStmt(keyword, _) ->
            // Top-level imports are consumed by the module linker
            // (`Modules.fs`) before this ever runs.
            error keyword "'import' is only allowed at the top level of a file."
            BBlock []
        | TraitStmt(name, _, _, _) ->
            // `resolve` filters every top-level `TraitStmt` out before
            // this ever runs (traits are pre-consumed entirely at compile
            // time -- see the module doc comment on `Ast.TraitStmt`). A
            // *nested* trait declaration (inside a function/block) isn't
            // pre-registered at all, mirroring the same top-level-only
            // scoping limit `preRegisterClasses`/`preRegisterGlobals`
            // already have for nested classes/globals -- reported as a
            // clear compile error here rather than silently doing nothing
            // or crashing.
            error name "Trait declarations are only supported at the top level."
            BBlock []

    member private this.ResolveBlock(statements: Stmt list) : BoundStmt list =
        beginScope ()
        let bound = statements |> List.map this.ResolveStmt
        endScope ()
        bound

    member private this.ResolveFunction(decl: FunctionDecl, isMethod: bool) : BoundFunctionDecl =
        let enclosing = context
        context <- FunctionContext(Some enclosing)
        // Bump depth before declaring anything, so the function's own
        // parameters and top-level body statements are never mistaken
        // for globals (depth 0 means "the script's own top level," full
        // stop -- see the module doc comment).
        beginScope ()

        if isMethod then
            context.Locals.Add
                { Name = "self"
                  Slot = 0
                  IsMutable = false
                  Depth = context.ScopeDepth
                  NameToken = None
                  Used = true }

        // Parameters are always immutable locals, matching poc (no
        // grammar exists for an individual mutable parameter).
        for parameter in decl.Parameters do
            declareVariable parameter false |> ignore

        let boundBody = decl.Body |> List.map this.ResolveStmt

        let result =
            { Name = decl.Name
              Parameters = decl.Parameters
              Body = boundBody
              LocalCount = context.Locals.Count
              Upvalues = context.Upvalues |> Seq.map (fun u -> u.Descriptor) |> List.ofSeq
              IsPub = decl.IsPub }

        // `ResolveBlock`'s `endScope` never runs for this function's own
        // top-level scope (parameters and `self` share it with the body's
        // own top-level locals, deliberately -- see the comment above),
        // so it needs its own unused-variable check here before the whole
        // context is discarded.
        checkUnusedLocals context.ScopeDepth
        context <- enclosing
        result

    member private this.ResolveExpr(expr: Expr) : BoundExpr =
        match expr with
        | Assign(name, value) ->
            let boundValue = this.ResolveExpr value
            // `docs/PLAN-0.3.md` decision 4: a pure write doesn't count as
            // "using" a variable for the unused-variable check -- only a
            // real read does (the `Variable` case below).
            let binding, isMutable = resolveReference name.Lexeme false
            match isMutable with
            | Some false -> error name $"Assigning to immutable variable '{name.Lexeme}' not allowed."
            | Some true -> ()
            | None -> () // an as-yet-unresolved global -- deferred to runtime, same as poc today
            BAssign(binding, name, boundValue)
        | Binary(left, operator, right) -> BBinary(this.ResolveExpr left, operator, this.ResolveExpr right)
        | Logical(left, operator, right) -> BLogical(this.ResolveExpr left, operator, this.ResolveExpr right)
        | Grouping inner -> BGrouping(this.ResolveExpr inner)
        | Literal value -> BLiteral value
        | Unary(operator, right) -> BUnary(operator, this.ResolveExpr right)
        | Ternary(left, leftOp, middle, rightOp, right) ->
            BTernary(this.ResolveExpr left, leftOp, this.ResolveExpr middle, rightOp, this.ResolveExpr right)
        | Vector values -> BVector(values |> List.map this.ResolveExpr)
        | Spread(expr, ellipsis) -> BSpread(this.ResolveExpr expr, ellipsis)
        | Variable name ->
            let binding, _ = resolveReference name.Lexeme true
            BVariable(binding, name)
        | BreakExpr keyword -> BBreak keyword
        | ContinueExpr keyword -> BContinue keyword
        | Ignore -> BIgnore
        | Call(callee, arguments) -> BCall(this.ResolveExpr callee, arguments |> List.map this.ResolveExpr)
        | Get(obj, name) ->
            // `docs/PLAN-0.3.md` decision 4: `self.x`/`self.method()` both
            // parse through here (a method call is `Call(Get(...), ...)`),
            // so this one case covers both property reads and method
            // calls for the unused-private-member check below.
            (match obj with
             | SelfExpr _ -> usedSelfNames.Add name.Lexeme |> ignore
             | _ -> ())
            BGet(this.ResolveExpr obj, name)
        | Set(obj, name, value) ->
            // `docs/PLAN-0.2.md` decision 8's addendum: a bare
            // `self.x = value` with no matching property declaration
            // anywhere in the current class's own hierarchy is a
            // compile-time error, the same category as assigning to an
            // undeclared local -- only checkable here (not for an
            // external `instance.x = value`, whose `instance` expression
            // has no statically-known class to check against).
            (match obj with
             | SelfExpr _ -> usedSelfNames.Add name.Lexeme |> ignore
             | _ -> ())
            match obj, currentClassName with
            | SelfExpr _, Some className when (findDeclaredProperty effectiveMembers className name.Lexeme).IsNone ->
                if findDeclaredMethod effectiveMembers className name.Lexeme then
                    error name $"'{name.Lexeme}' is a method, not a property -- it can't be assigned to."
                else
                    error name $"Property '{name.Lexeme}' is not declared in class '{className}' or any of its superclasses/mixins/traits."
            | _ -> ()
            BSet(this.ResolveExpr obj, name, this.ResolveExpr value)
        | Index(obj, index, bracket) -> BIndex(this.ResolveExpr obj, this.ResolveExpr index, bracket)
        | IndexSet(obj, index, value, bracket) ->
            BIndexSet(this.ResolveExpr obj, this.ResolveExpr index, this.ResolveExpr value, bracket)
        | Slice(obj, start, stop, bracket) ->
            BSlice(this.ResolveExpr obj, start |> Option.map this.ResolveExpr, stop |> Option.map this.ResolveExpr, bracket)
        | Lambda(parameters, arrow, body) ->
            // Desugars to a nameless FunctionDecl with a single implicit
            // `return` statement, then resolves exactly like a nested
            // named function (`ResolveFunction`, unchanged) -- same
            // scope/slot/upvalue machinery, per docs/PLAN-0.2.md §3. The
            // synthetic `Name` token is only ever used for diagnostics/
            // disassembly (`Codegen.fs`'s `FunctionProto.Name`); nothing
            // binds a lambda to it.
            let syntheticDecl: FunctionDecl =
                { Name = { arrow with Type = Identifier; Lexeme = "lambda" }
                  Parameters = parameters
                  Body = [ ReturnStmt(arrow, Some body) ]
                  IsPub = false }
            BLambda(this.ResolveFunction(syntheticDecl, isMethod = false))
        | Cons(item, list, bracket) ->
            // `[item | list]` (docs/PLAN-0.2.md decision 2) needs a real
            // runtime loop -- `list`'s length isn't known at compile
            // time, so this can't be a fixed-operand `BuildVector`. A
            // first attempt allocated hidden local slots directly in the
            // *enclosing* scope for the accumulator/source/index, but
            // that's unsound: a `Cons` can appear anywhere an expression
            // can, including mid-expression (e.g. a call argument), where
            // other transient values the enclosing expression already
            // pushed (like the callee itself) sit above the slot numbers
            // `Resolver.fs`'s ordinary `declareVariable` would compute,
            // corrupting every `GetLocal`/`SetLocal` after it -- found by
            // `[1 | []]` failing as a bare call argument (`print [1 | []]`)
            // while working fine as a `var` initializer. Fixed by
            // desugaring to a call of a synthetic, isolated zero-context
            // closure instead: `item`/`list` are evaluated once in the
            // *enclosing* scope and passed in as ordinary call arguments,
            // and everything the closure's own body needs (`$result`,
            // `$index`) lives in its own fresh frame, entirely decoupled
            // from whatever the enclosing expression already has on the
            // stack -- exactly the same isolation a lambda already gets,
            // and exactly how `Return` already extracts a value out from
            // under a callee's own locals.
            let itemParam = { bracket with Type = Identifier; Lexeme = "$item" }
            let listParam = { bracket with Type = Identifier; Lexeme = "$list" }
            let resultName = { bracket with Type = Identifier; Lexeme = "$result" }
            let indexName = { bracket with Type = Identifier; Lexeme = "$index" }
            let lessOp = { bracket with Type = Less; Lexeme = "<" }
            let incrOp = { bracket with Type = PlusPlus; Lexeme = "++" }
            let syntheticDecl: FunctionDecl =
                { Name = { bracket with Type = Identifier; Lexeme = "cons" }
                  Parameters = [ itemParam; listParam ]
                  Body =
                    [ VarStmt(resultName, Some(Vector [ Variable itemParam ]), false)
                      VarStmt(indexName, Some(Literal(NumberValue 0.0)), true)
                      ForStmt(
                          None,
                          Some(Binary(Variable indexName, lessOp, InternalVectorLength(Variable listParam))),
                          Some(Unary(incrOp, Variable indexName)),
                          ExpressionStmt(
                              InternalVectorAppend(Variable resultName, Index(Variable listParam, Variable indexName, bracket))
                          )
                      )
                      ReturnStmt(bracket, Some(Variable resultName)) ]
                  IsPub = false }
            let boundDecl = this.ResolveFunction(syntheticDecl, isMethod = false)
            let boundItem = this.ResolveExpr item
            let boundList = this.ResolveExpr list
            BCall(BLambda boundDecl, [ boundItem; boundList ])
        | ListComprehension(body, generators, guard, bracket) ->
            // Same runtime-length problem, same synthetic-closure fix as
            // `Cons` above. `docs/PLAN-0.3.md` decision 1 generalizes
            // `0.2`'s single fixed generator into a comma-separated list
            // (desugared to nested loops, outermost/first-written
            // generator first) plus an optional trailing guard.
            //
            // Only the *first* generator's source is evaluated once, in
            // the enclosing scope, and passed in as the synthetic
            // closure's sole parameter -- exactly matching `0.2`'s own
            // shape byte-for-byte when there's only one generator and no
            // guard, so nothing here regresses that already-shipped case.
            // Every later generator's source is instead declared as a
            // fresh local *inside* the previous generator's loop body,
            // re-evaluated on every outer iteration -- required, not just
            // convenient, since a later generator's source expression may
            // reference an earlier generator's bound name (`[p | x <- xs,
            // y <- range x]`, decision 1's own example) and can only see
            // that name in scope from inside the enclosing loop.
            let sourceParam = { bracket with Type = Identifier; Lexeme = "$source" }
            let resultName = { bracket with Type = Identifier; Lexeme = "$result" }
            let indexName = { bracket with Type = Identifier; Lexeme = "$index" }
            let lessOp = { bracket with Type = Less; Lexeme = "<" }
            let incrOp = { bracket with Type = PlusPlus; Lexeme = "++" }
            let questionOp = { bracket with Type = QuestionMark; Lexeme = "?" }
            let colonOp = { bracket with Type = Colon; Lexeme = ":" }
            let appendStmt = ExpressionStmt(InternalVectorAppend(Variable resultName, body))
            let innermost =
                match guard with
                | None -> appendStmt
                | Some g ->
                    ExpressionStmt(
                        Ternary(g, questionOp, InternalVectorAppend(Variable resultName, body), colonOp, Literal NilValue)
                    )
            // Builds generators.[1..], nesting each one's loop inside the
            // previous, with `inner` at the very center. `generators.[0]`
            // is handled separately below since its source comes from the
            // parameter, not a freshly declared local.
            let rec buildInnerLoops (rest: (int * Token * Expr) list) (inner: Stmt) : Stmt list =
                match rest with
                | [] -> [ inner ]
                | (i, variable, source) :: tail ->
                    let sourceName = { bracket with Type = Identifier; Lexeme = $"$source{i}" }
                    let indexN = { bracket with Type = Identifier; Lexeme = $"$index{i}" }
                    [ VarStmt(sourceName, Some source, false)
                      VarStmt(indexN, Some(Literal(NumberValue 0.0)), true)
                      ForStmt(
                          None,
                          Some(Binary(Variable indexN, lessOp, InternalVectorLength(Variable sourceName))),
                          Some(Unary(incrOp, Variable indexN)),
                          Block(
                              VarStmt(variable, Some(Index(Variable sourceName, Variable indexN, bracket)), false)
                              :: buildInnerLoops tail inner
                          )
                      ) ]
            let firstVariable, _ = generators.Head
            let laterGenerators = generators.Tail |> List.mapi (fun i (v, s) -> i + 1, v, s)
            let syntheticDecl: FunctionDecl =
                { Name = { bracket with Type = Identifier; Lexeme = "comprehension" }
                  Parameters = [ sourceParam ]
                  Body =
                    [ VarStmt(resultName, Some(Vector []), false)
                      VarStmt(indexName, Some(Literal(NumberValue 0.0)), true)
                      ForStmt(
                          None,
                          Some(Binary(Variable indexName, lessOp, InternalVectorLength(Variable sourceParam))),
                          Some(Unary(incrOp, Variable indexName)),
                          Block(
                              VarStmt(firstVariable, Some(Index(Variable sourceParam, Variable indexName, bracket)), false)
                              :: buildInnerLoops laterGenerators innermost
                          )
                      )
                      ReturnStmt(bracket, Some(Variable resultName)) ]
                  IsPub = false }
            let boundDecl = this.ResolveFunction(syntheticDecl, isMethod = false)
            let _, firstSource = generators.Head
            let boundSource = this.ResolveExpr firstSource
            BCall(BLambda boundDecl, [ boundSource ])
        | InternalVectorLength vector -> BVectorLengthInternal(this.ResolveExpr vector)
        | InternalVectorAppend(vector, value) -> BVectorAppendInternal(this.ResolveExpr vector, this.ResolveExpr value)
        | SelfExpr keyword ->
            match resolveReference "self" true with
            | GlobalBinding _, None ->
                error keyword "Can't use 'self' outside of a method."
                BSelf(GlobalBinding "self", keyword)
            | binding, _ -> BSelf(binding, keyword)
        | SuperExpr(keyword, method) ->
            match resolveReference "super" true with
            | GlobalBinding _, None ->
                error keyword "Can't use 'super' outside of a class with a superclass."
                BSuper(GlobalBinding "self", GlobalBinding "super", keyword, method)
            | binding, _ ->
                // Resolved independently of `super` itself (mirroring `clox`) so
                // that a `super.method()` call from inside a closure nested
                // *within* a method still finds the right `self` -- e.g. via an
                // upvalue chain of its own -- rather than assuming `self` is
                // always the current function's own slot 0.
                let selfBinding, _ = resolveReference "self" true
                BSuper(selfBinding, binding, keyword, method)

/// Native functions the VM provides without any user declaration (Phase
/// 7: `print`, `concat`; Phase 5 of `docs/PLAN-0.2.md`: `push`, `pop`,
/// `length`, `reverse`; Phase 6: `transpose`, `multiply`, `add`,
/// `subtract`) -- pre-registered as immutable globals, exactly like a
/// user-declared `fun`, so `print = 5` is a compile-time immutability
/// error and `var print = 1` is a compile-time redeclaration error
/// instead of silently succeeding just because the resolver has never
/// heard of the name. Mirrors `poc`'s `Interpreter.__init__`, which
/// defines both in the same environment chain user code runs in, with
/// `is_mutable=False`, before any user statement executes. Keep this in
/// sync with `vm/src/vm.cpp`'s `defineNatives` -- the two toolchains have
/// no shared source of truth for this list. `map`/`filter`/`reduce`/`sort`/
/// `elementwise` are deliberately NOT here -- they're ordinary `fun`
/// declarations (`Prelude.fs`) resolved as part of the same program as
/// any user `fun`, needing no special-casing at all.
let private nativeGlobals =
    [ "print"; "concat"; "push"; "pop"; "length"; "reverse"; "transpose"; "multiply"; "add"; "subtract" ]

/// What a `pub` top-level declaration exports (`docs/PLAN-0.3.md`
/// decision 6). A class or trait exports the member set an importer needs
/// to type-check `extends`/`with`/`use` and `self.x` against -- for a
/// class its full *effective* set (own + inherited + mixed in), for a trait
/// its fully flattened one -- since the exporter's own raw class/trait
/// tables mean nothing in the importer's name space.
type ExportKind =
    | ExportVar
    | ExportFun
    | ExportClass of effective: MemberSet
    | ExportTrait of flattened: MemberSet

type ExportedSymbol = { Name: string; Kind: ExportKind }

/// One name an `import` brings into a module: `Local` is the token for
/// the name as spelled in the importing file (the alias if there is one),
/// `Qualified` the exporting module's qualified global it refers to.
type ImportBinding = { Local: Token; Qualified: string; Kind: ExportKind }

/// The result of resolving one compilation unit (the prelude, or a single
/// module's source file) in isolation. `DeclaredGlobals` is every
/// top-level `var`/`fun`/`class` name the unit itself declared (never
/// the natives, `rootGlobals` or imports it was seeded with) -- `Modules.fs`
/// uses it to qualify exactly those names when linking several units into
/// one program (`docs/PLAN-0.3.md` Phase 4). `Exports` is what the unit's
/// `pub` declarations offer to importers (Phase 5).
type ResolvedUnit =
    { Bound: BoundStmt list
      Errors: ResolveError list
      Warnings: ResolveError list
      DeclaredGlobals: Set<string>
      Exports: ExportedSymbol list }

/// Shared implementation behind `resolve`/`resolveWithExemptGlobals`/
/// `resolveUnit` below. `rootGlobals` are extra immutable global names
/// pre-seeded exactly like `nativeGlobals` (declared by some *other*
/// unit, typically the prelude, that this unit may reference but did not
/// itself declare -- so redeclaring one is the same "already declared"
/// error, and none are ever checked for unused-ness here). `imports` are
/// the already-validated names this unit's `import` statements bring in
/// (`linked` says whether a module linker is driving this call: without
/// one, a top-level `import` can't be satisfied and is an error).
let private resolveInternal
    (linked: bool)
    (rootGlobals: string list)
    (imports: ImportBinding list)
    (stmts: Stmt list)
    (exemptGlobalNames: Set<string>)
    : ResolvedUnit =
    // Unwrap every top-level `pub`, remembering which names it exported,
    // and set aside `import`s (the linker already consumed them).
    let exportedNames = ResizeArray<string>()
    let preErrors = ResizeArray<ResolveError>()
    let declaredName (stmt: Stmt) =
        match stmt with
        | VarStmt(name, _, _) -> Some name.Lexeme
        | FunctionStmt decl -> Some decl.Name.Lexeme
        | ClassStmt(name, _, _, _, _, _) -> Some name.Lexeme
        | TraitStmt(name, _, _, _) -> Some name.Lexeme
        | _ -> None
    let stmts =
        stmts
        |> List.choose (fun stmt ->
            match stmt with
            | PubStmt(_, inner) ->
                declaredName inner |> Option.iter exportedNames.Add
                Some inner
            | ImportStmt(keyword, _) ->
                if not linked then
                    preErrors.Add { Message = "'import' needs the module linker; it can't be resolved in isolation."; Token = keyword }
                None
            | other -> Some other)

    let globals = Dictionary<string, bool>()
    for name in nativeGlobals do
        globals.[name] <- false
    for name in rootGlobals do
        globals.[name] <- false

    let aliases = Dictionary<string, string>()
    let traits = Dictionary<string, TraitInfo>()
    let classes = Dictionary<string, ClassInfo>()
    for import in imports do
        let local = import.Local.Lexeme
        if globals.ContainsKey local then
            preErrors.Add { Message = $"Variable '{local}' already declared."; Token = import.Local }
        else
            globals.[local] <- false // imports are read-only bindings
            aliases.[local] <- import.Qualified
            match import.Kind with
            | ExportClass effective ->
                classes.[local] <-
                    { Name = import.Local
                      SuperclassName = None
                      MixinNames = []
                      OwnLiteral = effective
                      TraitContributed = emptyMemberSet
                      InlinedProperties = []
                      InlinedMethods = []
                      IsImported = true }
            | ExportTrait flattened ->
                traits.[local] <-
                    { Name = import.Local
                      Properties = flattened.Properties |> Map.toList |> List.map snd
                      Methods = flattened.Methods |> Map.toList |> List.map snd
                      UsedTraitNames = [] }
            | ExportVar
            | ExportFun -> ()

    let seeded = Set.ofSeq globals.Keys
    let globalDecls = Dictionary<string, Token * string>()
    preRegisterGlobals globals globalDecls preErrors stmts

    preRegisterTraits traits preErrors stmts
    let traitCache = Dictionary<string, MemberSet>()

    preRegisterClasses classes traits traitCache preErrors stmts
    let effectiveMembers = computeAllEffectiveClassMembers classes preErrors

    // Every `TraitStmt` is fully consumed above (`preRegisterTraits`/
    // `flattenTrait`, inlined into whichever class(es) `use` it) and
    // never given a `BoundStmt` of its own -- filtered out here rather
    // than in `ResolveStmt`, which only exists to catch a *nested* trait
    // declaration (not pre-registered, see its own case) as a clean
    // error instead of silently vanishing.
    let nonTraitStmts = stmts |> List.filter (function TraitStmt _ -> false | _ -> true)

    let resolver = Resolver(globals, globalDecls, aliases, classes, effectiveMembers)
    let bound = nonTraitStmts |> List.map resolver.ResolveStmt
    // Both must run after every top-level/class/method body above has
    // resolved, so `usedSelfNames`/`usedGlobalNames` (populated along the
    // way) are complete before checking them.
    resolver.CheckUnusedClassMembers()
    // A `pub` declaration is part of the module's public surface, used or
    // not within this file -- same reasoning as `pub` class members.
    resolver.CheckUnusedGlobals(Set.union exemptGlobalNames (Set.ofSeq exportedNames))

    // An import is "used" if the name is ever read -- including as an
    // `extends`/`with` operand, which resolves through `resolveReference`
    // like any other read -- or named by a `use` clause (trait use is
    // inlined at compile time and never goes through `resolveReference`).
    let usedTraitNames =
        stmts
        |> List.collect (function
            | ClassStmt(_, _, _, _, _, used) -> used
            | TraitStmt(_, _, _, used) -> used
            | _ -> [])
        |> List.map (fun t -> t.Lexeme)
        |> Set.ofList
    let unusedImportWarnings =
        [ for import in imports do
            let local = import.Local.Lexeme
            if
                not (isExemptFromUnusedCheckName local)
                && not (resolver.WasGlobalRead local)
                && not (usedTraitNames.Contains local)
            then
                yield { Message = $"Import '{local}' is never used."; Token = import.Local } ]

    let exports =
        [ for name in exportedNames |> Seq.distinct do
            let kind =
                stmts
                |> List.tryPick (fun stmt ->
                    match stmt with
                    | VarStmt(n, _, _) when n.Lexeme = name -> Some ExportVar
                    | FunctionStmt d when d.Name.Lexeme = name -> Some ExportFun
                    | ClassStmt(n, _, _, _, _, _) when n.Lexeme = name ->
                        match effectiveMembers.TryGetValue name with
                        | true, members -> Some(ExportClass members)
                        | false, _ -> None
                    | TraitStmt(n, _, _, _) when n.Lexeme = name ->
                        // Re-flattened against throwaway error/cache sinks:
                        // any real conflict was already reported above.
                        Some(ExportTrait(flattenTrait traits (ResizeArray()) (Dictionary()) Set.empty n name))
                    | _ -> None)
            match kind with
            | Some k -> yield { Name = name; Kind = k }
            | None -> () ]

    { Bound = bound
      Errors = (List.ofSeq preErrors) @ resolver.Errors
      Warnings = resolver.Warnings @ unusedImportWarnings
      DeclaredGlobals = globals.Keys |> Seq.filter (fun k -> not (seeded.Contains k)) |> Set.ofSeq
      Exports = exports }

let private asTuple (r: ResolvedUnit) = r.Bound, r.Errors, r.Warnings

/// Resolves `stmts` into a `BoundStmt` list plus any resolution errors
/// (compile-time immutability violations, redeclarations, `self`/`super`
/// used where they can't resolve) and, separately, any unused-variable/
/// -parameter/-private-member/-global warnings (`docs/PLAN-0.3.md`
/// decision 4) -- the caller should print the latter without letting them
/// affect exit status. Never throws -- every error is recorded and
/// resolution continues, since (unlike a syntax error) a semantic error
/// doesn't prevent understanding the rest of the program's structure.
let resolve (stmts: Stmt list) : BoundStmt list * ResolveError list * ResolveError list =
    resolveInternal false [] [] stmts Set.empty |> asTuple

/// Like `resolve`, but never warns about an unused top-level `var`/`fun`
/// whose name is in `exemptGlobalNames`, regardless of whether the
/// program actually reads it (used where the prelude is still textually
/// merged into the same unit as user code).
let resolveWithExemptGlobals (stmts: Stmt list) (exemptGlobalNames: Set<string>) : BoundStmt list * ResolveError list * ResolveError list =
    resolveInternal false [] [] stmts exemptGlobalNames |> asTuple

/// Resolves one compilation unit in isolation (`docs/PLAN-0.3.md` Phases
/// 4-5): `rootGlobals` are names another unit (the prelude) declares that
/// this one may reference as ordinary immutable globals, and `imports` are
/// the names its `import` statements bring in.
let resolveUnit (rootGlobals: string list) (imports: ImportBinding list) (stmts: Stmt list) : ResolvedUnit =
    resolveInternal true rootGlobals imports stmts Set.empty
