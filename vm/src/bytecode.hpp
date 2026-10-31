#pragma once

#include <cstdint>
#include <filesystem>

#include "object.hpp"

namespace iqalox {
class Vm;
}

namespace iqalox::bytecode {

// Bytecode format v2 -- see `compiler/src/Bytecode.fs`'s module doc
// comment for the authoritative on-disk layout (this mirrors it exactly;
// `compiler/` writes, `vm/` reads). v2 adds a per-instruction-byte source
// line table (see `Chunk::lines`) so a runtime fault can report a real
// `[line N]`, closing a real diagnostics gap v1 (Phases 1-9) left open.
// v3 adds a source-FILE dimension on top (a program-wide file table plus a
// per-instruction-byte file index, `Chunk::files`), since a multi-file
// program compiles to one chunk -- `docs/PLAN-0.3.md` Phases 4-5.
// Supersedes format v0, Phase 1's minimal round-trip proof (a single
// opcode: push a string constant, print it, halt) now that `compiler/`'s
// codegen (Phase 5) actually produces real programs.
[[maybe_unused]] constexpr uint8_t kFormatVersion = 3;

// One-to-one with `compiler/src/Bytecode.fs`'s `opcodeOf` -- same names,
// same byte values.
enum class OpCode : uint8_t {
    Constant = 0x01,
    Nil = 0x02,
    True = 0x03,
    False = 0x04,
    Undef = 0x05,
    Pop = 0x06,
    PopN = 0x07,
    GetLocal = 0x08,
    SetLocal = 0x09,
    GetUpvalue = 0x0A,
    SetUpvalue = 0x0B,
    GetGlobal = 0x0C,
    SetGlobal = 0x0D,
    DefineGlobal = 0x0E,
    Add = 0x0F,
    Subtract = 0x10,
    Multiply = 0x11,
    Divide = 0x12,
    Modulo = 0x13,
    Power = 0x14,
    Negate = 0x15,
    Not = 0x16,
    Equal = 0x17,
    NotEqual = 0x18,
    Greater = 0x19,
    GreaterEqual = 0x1A,
    Less = 0x1B,
    LessEqual = 0x1C,
    Jump = 0x1D,
    JumpIfFalse = 0x1E,
    JumpIfNotNil = 0x1F,
    BuildVector = 0x20,
    Call = 0x21,
    Closure = 0x22,
    Return = 0x23,
    Class = 0x24,
    Method = 0x25,
    Inherit = 0x26,
    GetProperty = 0x27,
    SetProperty = 0x28,
    GetSuper = 0x29,
    GetIndex = 0x2A,
    SetIndex = 0x2B,
    VectorLength = 0x2C,
    VectorAppend = 0x2D,
    VectorExtend = 0x2E,
    // docs/PLAN-0.2.md Phase 7: property/method visibility (`pub`/`mut`).
    // MethodPub = the externally-callable counterpart of Method (private);
    // GetPropertySelf/SetPropertySelf = self.x's internal-access variants
    // of GetProperty/SetProperty, skipping the pub/mut gate entirely.
    MethodPub = 0x2F,
    GetPropertySelf = 0x30,
    SetPropertySelf = 0x31,
    // One opcode per `var name [pub] [mut]` form (compiler/src/Bytecode.fs's
    // doc comment on PropertyPrivate explains why four opcodes rather than
    // one with boolean operands).
    PropertyPrivate = 0x32,
    PropertyPrivateMut = 0x33,
    PropertyPub = 0x34,
    PropertyPubMut = 0x35,
    // docs/PLAN-0.2.md Phase 8: `with M1, M2` mixin composition (decision
    // 12, this version's simplified/non-C3 approximation of open question
    // 2). Mirrors Inherit's "peek the class, copy members in" mechanics,
    // but pops its mixin operand instead of leaving it on the stack --
    // unlike a superclass, a mixin's value is never needed again once its
    // members are copied in (super does not chain through with-mixins
    // this version).
    Mixin = 0x36,
    // docs/PLAN-0.3.md decision 3: `v[a:b]`, end-inclusive, negative
    // bounds and omitted bounds (encoded as `nil`) both supported. See
    // Vm::run's handler for the exact resolution/clamping rules.
    GetSlice = 0x37,
};

// Loads `path` as a format-v1 bytecode file and returns its top-level
// script function -- every object built while decoding (strings, nested
// function protos, ...) is allocated through `vm` so the GC tracks it
// from the moment it exists. Throws `std::runtime_error` on a malformed
// file (bad magic, unsupported version, truncated data, an unrecognized
// constant tag) -- there's no recovery story, matching how early this
// format still is.
ObjFunction* load(const std::filesystem::path& path, Vm& vm);

}  // namespace iqalox::bytecode
