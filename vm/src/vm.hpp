#pragma once

#include <cstdint>
#include <deque>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

#include "object.hpp"
#include "value.hpp"

namespace iqalox {

// An Iqalox-level runtime fault (a failed arithmetic type check, division
// by zero, an undefined/uninitialized variable, calling a non-callable
// value, ...) -- as opposed to a malformed bytecode *file*, which
// `bytecode::load` reports as a plain `std::runtime_error` instead. `main`
// tells the two apart to pick an exit code, mirroring
// `poc/src/iqalox.py`'s sysexits split (65 for a bad source/file, 70 for a
// runtime fault in an otherwise-valid program).
class RuntimeError : public std::runtime_error {
public:
    explicit RuntimeError(const std::string& message) : std::runtime_error(message) {}
};

struct CallFrame {
    ObjClosure* closure;
    size_t ip = 0;
    // The byte offset `ip` had at the start of whatever instruction is
    // currently executing -- captured once per `run()` loop iteration,
    // before any operand bytes are read, so `runtimeError` can still
    // find it after `ip` has advanced past the whole instruction (or a
    // callee it invoked pushed further frames of its own). Indexes
    // `closure->function->chunk.lines` to report `[line N]`.
    size_t currentInstructionIp = 0;
    // Stack index of this frame's slot 0. Note this is *not* clox's
    // convention of reserving slot 0 for the callee itself -- Resolver.fs
    // starts a plain function's parameters at slot 0 directly (see
    // `Codegen.fs`'s `CompileFunctionValue`). A *method* call is the one
    // place that convention still applies: `self` occupies slot 0 (see
    // `ResolveFunction`'s `isMethod` branch), so `stackBase` for a method
    // frame points at the receiver itself, not one slot above it.
    size_t stackBase = 0;
    // Where `Return` truncates the stack back to and pushes the call's
    // result -- `stackBase - 1` for a plain function/native call (the
    // callee's own slot, one below its parameters), or `stackBase` itself
    // for a method call (there's no separate callee slot below `self`;
    // see `Vm::callMethod`).
    size_t resultIndex = 0;
    // `init`, invoked via constructing an instance (`Vm::callClass`),
    // always yields the instance regardless of what it explicitly
    // `return`s (or falls off the end with) -- mirroring `poc`'s
    // `IqaloxClass.call`, which discards `initializer.bind(instance).call(...)`'s
    // own return value and returns `instance` unconditionally. Calling
    // `.init()` directly as an ordinary method (not via construction)
    // does *not* get this treatment in `poc` either, so this is only ever
    // set for the constructor-invoked call, never a plain `GetProperty`
    // lookup of "init".
    bool isInitializer = false;
};

// The stack-based bytecode interpreter (docs/PLAN-0.1.md Phase 6) plus its
// mark-sweep tracing garbage collector (decision 7). Owns every
// GC-managed `Obj` ever allocated -- including those built while loading
// a chunk (see `bytecode::load`, which takes a `Vm&` for exactly this) --
// so construct the `Vm` before loading anything into it.
class Vm {
public:
    // Defines the native stdlib (`print`, `concat` -- Phase 7) as globals
    // before anything else runs, mirroring `poc/src/interpreter.py`'s
    // `Interpreter.__init__`, which defines both in the same environment
    // chain user code runs in before any user statement executes. Safe to
    // allocate here even though `gcEnabled` is still false: exactly like
    // objects `bytecode::load` builds, these become reachable the moment
    // `interpret` anchors the script's closure, via the `globals` map
    // itself, which is always scanned as a root.
    Vm();
    ~Vm();

    // The loaded program's source-file names (`bytecode::load` fills this
    // from the format-v3 file table); `Chunk::files` indexes into it so a
    // runtime error can say which file it came from. Empty for a
    // hand-built program.
    std::vector<std::string> sourceFiles;

    Vm(const Vm&) = delete;
    Vm& operator=(const Vm&) = delete;

    // Runs `script` (the top-level chunk, treated as an implicit
    // zero-arity, zero-upvalue function per `Bytecode.fs`'s format doc
    // comment) to completion. Throws `RuntimeError` on an Iqalox-level
    // fault.
    void interpret(ObjFunction* script);

    // Allocates and registers a new GC-tracked object, running a
    // collection first if the heap has grown past its current threshold
    // (or always, if `stressGc` is set -- a testing aid, see
    // `docs/PLAN-0.1.md`'s Phase 6 entry and `vm/tests/test_vm.cpp`).
    // Safe to call before `interpret` starts (e.g. from the loader): with
    // an empty stack/globals/frame list, a triggered collection simply
    // finds nothing reachable yet to free, since every object allocated
    // so far is reachable transitively from whatever will become the
    // script's own top-level `ObjFunction` once it exists.
    template <typename T, typename... Args>
    T* allocate(Args&&... args) {
        // `gcEnabled` stays false until `interpret` has safely anchored
        // the loaded program's top-level closure on the stack. Before
        // that -- for every allocation `bytecode::load` makes while
        // building the constant pool, and for `interpret`'s own first
        // allocation (the closure wrapping the script, which isn't
        // reachable from any root until the very next line pushes it) --
        // nothing is rooted yet, so a collection here would find the
        // entire freshly-loaded program unreachable and free all of it
        // out from under itself before it ever runs.
        if (gcEnabled && (stressGc || bytesAllocated > nextGc)) {
            collectGarbage();
        }
        T* obj = new T(std::forward<Args>(args)...);
        obj->next = objects;
        objects = obj;
        bytesAllocated += sizeof(T);
        return obj;
    }

    // When set, every single allocation triggers a full collection --
    // exercises the GC far more aggressively than the size-threshold
    // trigger normally would, to shake out rooting bugs in tests. Only
    // takes effect once `gcEnabled` is also true (see `allocate`).
    bool stressGc = false;

    // Looks up a global by name -- also how `vm/tests/test_vm.cpp`'s
    // hand-assembled fixtures read a result back out without going
    // through `print`, typically by having the test program assign its
    // answer to a conventionally named global. A reasonable building
    // block for a future REPL/debugger too, not purely test scaffolding.
    const Value* getGlobal(const std::string& name) const {
        auto it = globals.find(name);
        return it == globals.end() ? nullptr : &it->second;
    }

private:
    std::deque<Value> stack;
    std::vector<CallFrame> frames;
    std::unordered_map<std::string, Value> globals;
    ObjUpvalue* openUpvalues = nullptr;

    Obj* objects = nullptr;
    size_t bytesAllocated = 0;
    size_t nextGc = 1024 * 1024;
    std::vector<Obj*> grayStack;
    bool gcEnabled = false;

    void run();

    void push(const Value& value) { stack.push_back(value); }
    Value pop();
    const Value& peek(int distanceFromTop = 0) const;
    // The single choke point every stack-shrinking operation (Pop, PopN,
    // a call frame tearing down, BuildVector consuming its operands) goes
    // through -- closes any open upvalues aliasing a slot about to be
    // reclaimed before actually shrinking, so a closure that outlives the
    // scope it captured from still sees a valid, independent value
    // instead of a dangling stack slot. There is no dedicated
    // "close upvalue" opcode in format v1; this is what stands in for one
    // (see docs/PLAN-0.1.md's Phase 6 entry).
    void truncateStack(size_t newSize);

    [[noreturn]] void runtimeError(const std::string& message);
    void checkNotUndef(const Value& value, const std::string& contextForGlobals);
    // A property's own "accessed before being assigned a value" check
    // (docs/PLAN-0.2.md decision 9) -- the same `Undef`-sentinel trick as
    // `checkNotUndef`, just worded for a property instead of a
    // local/upvalue/global, and applying regardless of internal vs.
    // external access (unlike the `pub`/`mut` gate, which only external
    // access needs at all).
    void checkPropertyNotUndef(const Value& value, const std::string& propName);
    void checkNumberOperand(const Value& value, const char* message);
    void checkNumberOperands(const Value& a, const Value& b, const char* message);
    // Shared by GetIndex/SetIndex. Validates `receiver` is a vector and
    // `indexValue` is an in-range integer index, returning the validated
    // (already from-the-end-translated) index. `docs/PLAN-0.3.md`
    // decision 2: negative indices are valid, translated to `length +
    // index`; still a hard error (via `runtimeError`, not a bare
    // `RuntimeError`, so it gets the same `[line N]` treatment
    // `GetProperty`/`SetProperty` already do) when out of range either
    // direction.
    size_t checkVectorIndex(const Value& receiver, const Value& indexValue);
    // `docs/PLAN-0.3.md` decision 3: `v[a:b]`, end-inclusive. `startValue`/
    // `stopValue` are `nil` for an omitted bound (defaulting to 0/
    // length-1 respectively); negative bounds translate the same way
    // `checkVectorIndex` does. Unlike single-index access, an
    // out-of-range or `start > stop` bound clamps to an empty result
    // rather than erroring -- always returns a freshly allocated vector,
    // never a view onto `receiver`.
    Value getSlice(const Value& receiver, const Value& startValue, const Value& stopValue);

    uint8_t readByte(CallFrame& frame);
    uint16_t readU16(CallFrame& frame);
    Value& constantAt(CallFrame& frame, uint16_t index);
    ObjString* stringConstantAt(CallFrame& frame, uint16_t index);

    void defineNatives();

    void callValue(const Value& callee, int argCount);
    void call(ObjClosure* closure, int argCount);
    void callNative(ObjNativeFunction* native, int argCount);
    // Shared by a bound-method call and an `init` invocation from
    // `callClass` -- both need `self`/the instance sitting at the
    // callee's own stack slot (see `CallFrame::stackBase`'s doc comment),
    // which the caller has already arranged before calling this.
    void callMethod(ObjClosure* method, int argCount, bool isInitializer);
    void callClass(ObjClass* klass, int argCount);
    // Looks `name` up in `klass->methods` and returns it wrapped in a
    // fresh `ObjBoundMethod` bound to `receiver` -- shared by `GetProperty`/
    // `GetPropertySelf` (look up on the instance's own class) and
    // `GetSuper` (looks up on a specific superclass value instead).
    // Throws `RuntimeError` ("Undefined property '<name>'.") if `name`
    // isn't found, matching both of poc's identically-worded lookup-miss
    // errors -- and, when `internal` is false (an external `GetProperty`,
    // never `GetPropertySelf`/`GetSuper`), also if `name` names a real but
    // non-`pub`, non-`init` method (docs/PLAN-0.2.md decision 11):
    // reported with the exact same "Undefined property" wording, so a
    // private method reads as genuinely nonexistent from outside, per
    // decision 10's "invisible" framing.
    Value bindMethod(ObjClass* klass, const Value& receiver, const std::string& name, bool internal);
    ObjUpvalue* captureUpvalue(size_t stackIndex);
    Value readUpvalue(ObjUpvalue* upvalue) const { return upvalue->open ? stack[upvalue->stackIndex] : upvalue->closed; }
    void writeUpvalue(ObjUpvalue* upvalue, const Value& value);

    // GC internals.
    void collectGarbage();
    void markRoots();
    void markValue(const Value& value);
    void markObject(Obj* obj);
    void traceReferences();
    void blackenObject(Obj* obj);
    void sweep();
};

}  // namespace iqalox
