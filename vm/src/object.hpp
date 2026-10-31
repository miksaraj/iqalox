#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "value.hpp"

namespace iqalox {

class Vm;

enum class ObjType : uint8_t {
    String,
    Vector,
    Function,
    Closure,
    Upvalue,
    NativeFunction,
    Class,
    Instance,
    BoundMethod,
};

// Base of every heap-allocated, GC-managed value. `next` is the intrusive
// singly-linked list of *every* live allocation -- see `Vm::allocate`/
// `Vm::sweep` -- distinct from `ObjUpvalue::nextOpen`'s separate list of
// just the currently-*open* upvalues.
struct Obj {
    ObjType type;
    bool marked = false;
    Obj* next = nullptr;

    explicit Obj(ObjType t) : type(t) {}
    // Virtual so `delete` through a base `Obj*` (all the GC's sweep phase
    // ever holds) correctly runs the derived destructor.
    virtual ~Obj() = default;
};

struct ObjString : Obj {
    std::string value;
    explicit ObjString(std::string v) : Obj(ObjType::String), value(std::move(v)) {}
};

struct ObjVector : Obj {
    std::vector<Value> elements;
    ObjVector() : Obj(ObjType::Vector) {}
};

// How a closure captures one of its upvalues when it's created at
// runtime -- mirrors `compiler/src/Bound.fs`'s `UpvalueDescriptor` exactly
// (see `compiler/src/Bytecode.fs`'s format doc comment for the on-disk
// encoding this is read from).
struct UpvalueDescriptor {
    bool fromEnclosingLocal;
    uint16_t index;
};

// The structured, in-memory form of one `compiler/src/Bytecode.fs` chunk:
// a raw instruction byte stream plus its constant pool, already resolved
// to runtime `Value`s (numbers/strings/nested functions) at load time --
// see `bytecode::load`. Unlike the F# compiler side, there's no need for a
// separate structured `Instruction` representation here: the VM decodes
// opcodes directly off `code` as it executes, the same way `clox` does.
struct Chunk {
    std::vector<uint8_t> code;
    std::vector<Value> constants;
    // `lines[i]` is the source line `code[i]` came from -- one entry per
    // *byte*, not per instruction (redundant, but lets a runtime fault
    // look up its line with a direct `lines[ip]`, no opcode-width
    // decoding needed at the point of the fault). See
    // `compiler/src/Bytecode.fs`'s doc comment for the on-disk layout this
    // mirrors, and `Vm::runtimeError` for the lookup itself.
    std::vector<uint16_t> lines;
    // `files[i]` indexes `Vm::sourceFiles` for the source file `code[i]`
    // came from -- same one-entry-per-byte shape as `lines`. May be empty
    // (a hand-built chunk); `Vm::runtimeError` then reports a bare line.
    std::vector<uint16_t> files;
};

struct ObjFunction : Obj {
    int arity = 0;
    // Not load-bearing for execution (slot indices are always relative to
    // a frame's own base, and the instruction stream already encodes
    // exactly how many slots to push/pop) -- kept for fidelity to the
    // format and future diagnostics.
    int localCount = 0;
    std::string name;
    std::vector<UpvalueDescriptor> upvalues;
    Chunk chunk;

    ObjFunction() : Obj(ObjType::Function) {}
};

// While open, `stackIndex` names the VM stack slot this upvalue aliases
// (so a write through the upvalue is visible to the local, and vice
// versa); `Vm::closeUpvalues` copies that slot's value into `closed` and
// flips `open` to false once the slot is about to be reclaimed, so the
// captured value survives independently of the stack after that.
//
// An index rather than a `Value*` into the stack deliberately: the VM's
// stack is a `std::deque` (see `vm.hpp` for why), and comparing pointers
// into two different elements of a non-contiguous container with
// `<`/`>=` -- which capturing/closing both need, to find/close every
// upvalue at or above a given stack position -- is unspecified behavior
// unless they alias the same underlying array. Plain size_t comparisons
// have no such hazard.
struct ObjUpvalue : Obj {
    size_t stackIndex;
    bool open = true;
    Value closed = NilValue;
    ObjUpvalue* nextOpen = nullptr;

    explicit ObjUpvalue(size_t index) : Obj(ObjType::Upvalue), stackIndex(index) {}
};

struct ObjClosure : Obj {
    ObjFunction* function;
    std::vector<ObjUpvalue*> upvalues;

    explicit ObjClosure(ObjFunction* fn) : Obj(ObjType::Closure), function(fn) {}
};

// A stdlib function implemented in C++ rather than compiled Iqalox
// (Phase 7: `print`, `concat`) -- takes the already-popped argument
// values directly (no bytecode frame involved) and returns the call's
// result. Takes `Vm&` so an implementation that needs to allocate (e.g.
// `concat` building its joined result string) can go through the GC's
// own tracked allocator rather than a raw, untracked `new`.
struct ObjNativeFunction : Obj {
    using Fn = Value (*)(Vm&, const std::vector<Value>&);

    std::string name;
    int arity;
    Fn function;

    ObjNativeFunction(std::string n, int a, Fn f)
        : Obj(ObjType::NativeFunction), name(std::move(n)), arity(a), function(f) {}
};

// `methods` already has every inherited method copied in at declaration
// time (see `Vm`'s `Inherit` handler) -- matching `clox`'s own approach,
// not `poc`'s `IqaloxClass`, which instead walks a live `superclass`
// pointer chain at every lookup. Behaviorally equivalent as long as
// methods are never added after the fact, which Iqalox has no syntax for.
// No `superclass` field is kept for the same reason `clox` doesn't need
// one: a `super.method()` call already carries the exact superclass
// value it needs on the stack (captured via the synthetic `super`
// upvalue/local, see `Bound.BSuper`), so nothing here ever has to walk a
// chain to find it.
//
// `docs/PLAN-0.2.md` Phase 7 additions: `publicMethods` names which of
// `methods`' entries were declared `pub` (decision 11) -- checked only by
// `GetProperty`'s *external* variant (`GetPropertySelf` skips the check
// entirely, and `init`/`GetSuper` are exempted at the call site, not
// here). `properties` is declared-property metadata (`pub`/`mut` per
// decision 8), copied into a subclass by `Inherit` exactly like `methods`
// is -- so a subclass automatically has every ancestor's property
// available without redeclaring it, matching the now-resolved open
// question 3's protected-like reading. Neither map is ever consulted for
// override bookkeeping the way `methods` overriding is: `Resolver.fs`'s
// `preRegisterClasses` rejects a property redeclared anywhere up a
// class's own ancestor chain at compile time, so this map is always a
// flat, non-conflicting union by the time any bytecode referencing it
// runs.
struct PropertyMeta {
    bool isPub;
    bool isMut;
};

struct ObjClass : Obj {
    std::string name;
    std::unordered_map<std::string, ObjClosure*> methods;
    std::unordered_set<std::string> publicMethods;
    std::unordered_map<std::string, PropertyMeta> properties;

    explicit ObjClass(std::string n) : Obj(ObjType::Class), name(std::move(n)) {}
};

// Every declared property (own class's plus every ancestor's, both
// already flattened into `klass->properties` by `Inherit`) gets a
// pre-populated `fields` entry the moment an instance is constructed
// (`Vm::callClass`), starting at `UndefValue` -- `docs/PLAN-0.2.md`
// decision 8's addendum retires `0.1`'s "fields spring into existence on
// first assignment" model entirely: every property a `0.2` class ever
// touches must have a `var` declaration, so by the time any bytecode
// runs, `fields` already has exactly one entry per declared property and
// never gains or loses one afterward. `UndefValue` doubles as decision
// 9's "not yet assigned" sentinel, the same trick `0.1` already uses for
// an uninitialized `var mut` local -- reading it before the first
// assignment is a runtime error the same way (`Vm::checkNotUndef`'s
// property counterpart), and for an immutable (`mut`-less) property,
// ever assigning it again after that first, real value is exactly what
// makes decision 9's "assign at most once" enforceable without a
// separate per-field "has this been set" bit alongside it.
struct ObjInstance : Obj {
    ObjClass* klass;
    std::unordered_map<std::string, Value> fields;

    explicit ObjInstance(ObjClass* k) : Obj(ObjType::Instance), klass(k) {}
};

// The result of looking up a method on an instance (`GetProperty`) or via
// `super` (`GetSuper`): a closure paired with the receiver it's bound to,
// so calling it later places `receiver` at the method's own `self` slot
// regardless of how many other values are on the stack by then -- mirrors
// `poc`'s `IqaloxFunction.bind`, which wraps the same closure in a fresh
// environment defining `self`, just without needing a new environment
// per call here.
struct ObjBoundMethod : Obj {
    Value receiver;
    ObjClosure* method;

    ObjBoundMethod(Value r, ObjClosure* m) : Obj(ObjType::BoundMethod), receiver(r), method(m) {}
};

}  // namespace iqalox
