/// Shared by every test module (auto-opened: top-level modules are in the
/// global namespace).
[<AutoOpen>]
module TestHelpers

/// F# can't pattern-match on a `bigint` literal, so an integer literal in a
/// pattern is written `IntValue(Is 3I)` instead of `IntValue 3I`.
let (|Is|_|) (expected: bigint) (actual: bigint) =
    if actual = expected then Some() else None
