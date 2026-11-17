#include "vm.hpp"

#include <algorithm>
#include <cmath>

#include "bytecode.hpp"
#include "natives.hpp"

namespace iqalox {

namespace {

// A global's key in the table is module-qualified by the compiler
// (`main::x`, `math.vector::helper` -- `compiler/src/Modules.fs`); the user
// only ever wrote `x`/`helper`, so error messages show what follows the last
// `::`. A bare name (a native like `print`) is returned unchanged.
std::string displayGlobalName(const std::string& qualified) {
    auto pos = qualified.rfind("::");
    return pos == std::string::npos ? qualified : qualified.substr(pos + 2);
}

constexpr size_t kMaxFrames = 1024;
constexpr size_t kGcHeapGrowFactor = 2;


}  // namespace

Vm::Vm() { defineNatives(); }

void Vm::defineNatives() {
    // Every native is registered here under its bare name, but `compiler/`
    // only lets a program *see* one after importing it from a `std.*` module
    // (docs/PLAN-0.3.md Phase 6) -- only `print` is ambient. Keep this list
    // in sync with compiler/src/Stdlib.fs and Resolver.fs's `nativeGlobals`.
    globals["print"] = objValue(allocate<ObjNativeFunction>("print", 1, nativePrint));
    globals["concat"] = objValue(allocate<ObjNativeFunction>("concat", 1, nativeConcat));
    // docs/PLAN-0.2.md Phase 5: push/pop/length/reverse need direct access
    // to an ObjVector's own element list, so they're true natives; map/
    // filter/reduce/sort don't (see compiler/src/Stdlib.fs) -- they're
    // ordinary Iqalox functions in the built-in std.* modules.
    globals["push"] = objValue(allocate<ObjNativeFunction>("push", 2, nativePush));
    globals["pop"] = objValue(allocate<ObjNativeFunction>("pop", 1, nativePop));
    globals["length"] = objValue(allocate<ObjNativeFunction>("length", 1, nativeLength));
    globals["reverse"] = objValue(allocate<ObjNativeFunction>("reverse", 1, nativeReverse));
    // docs/PLAN-0.2.md Phase 6: none of these four call back into user
    // code, so (unlike elementwise, compiler/src/Stdlib.fs) they're
    // true natives too.
    globals["transpose"] = objValue(allocate<ObjNativeFunction>("transpose", 1, nativeTranspose));
    globals["multiply"] = objValue(allocate<ObjNativeFunction>("multiply", 2, nativeMultiply));
    globals["add"] = objValue(allocate<ObjNativeFunction>("add", 2, nativeAdd));
    globals["subtract"] = objValue(allocate<ObjNativeFunction>("subtract", 2, nativeSubtract));
}

Vm::~Vm() {
    Obj* obj = objects;
    while (obj != nullptr) {
        Obj* next = obj->next;
        delete obj;
        obj = next;
    }
}

Value Vm::pop() {
    Value value = stack.back();
    truncateStack(stack.size() - 1);
    return value;
}

const Value& Vm::peek(int distanceFromTop) const { return stack[stack.size() - 1 - static_cast<size_t>(distanceFromTop)]; }

void Vm::truncateStack(size_t newSize) {
    while (openUpvalues != nullptr && openUpvalues->stackIndex >= newSize) {
        ObjUpvalue* upvalue = openUpvalues;
        upvalue->closed = stack[upvalue->stackIndex];
        upvalue->open = false;
        openUpvalues = upvalue->nextOpen;
    }
    stack.resize(newSize);
}

void Vm::writeUpvalue(ObjUpvalue* upvalue, const Value& value) {
    if (upvalue->open) {
        stack[upvalue->stackIndex] = value;
    } else {
        upvalue->closed = value;
    }
}

void Vm::runtimeError(const std::string& message) {
    // `frames` is only ever empty here for the top-level script's own
    // `call()` inside `interpret()`, before `run()`'s loop -- and that
    // call's arity always matches (0 == 0), so it can never actually
    // throw. Guarded anyway rather than assumed.
    if (frames.empty()) {
        throw RuntimeError(message);
    }
    const CallFrame& frame = frames.back();
    const auto& chunk = frame.closure->function->chunk;
    const auto& lines = chunk.lines;
    if (frame.currentInstructionIp < lines.size()) {
        // `[file, line N]` when the faulting instruction's file is known,
        // a bare `[line N]` otherwise (hand-built chunks, empty file name).
        std::string where = "line " + std::to_string(lines[frame.currentInstructionIp]);
        if (frame.currentInstructionIp < chunk.files.size()) {
            uint16_t file = chunk.files[frame.currentInstructionIp];
            if (file < sourceFiles.size() && !sourceFiles[file].empty()) {
                where = sourceFiles[file] + ", " + where;
            }
        }
        throw RuntimeError(message + "\n[" + where + "]");
    }
    throw RuntimeError(message);
}

void Vm::checkNotUndef(const Value& value, const std::string& globalName) {
    if (!isUndef(value)) return;
    if (globalName.empty()) {
        runtimeError("Variable accessed before being assigned a value.");
    } else {
        runtimeError("Variable '" + displayGlobalName(globalName) + "' accessed before being assigned a value.");
    }
}

void Vm::checkPropertyNotUndef(const Value& value, const std::string& propName) {
    if (!isUndef(value)) return;
    runtimeError("Property '" + propName + "' accessed before being assigned a value.");
}

size_t Vm::checkVectorIndex(const Value& receiver, const Value& indexValue) {
    if (!isObj(receiver) || asObj(receiver)->type != ObjType::Vector) {
        runtimeError("Only vectors can be indexed, got " + typeName(receiver) + ".");
    }
    // docs/PLAN-0.4.md decision 9: only an integer indexes, even a whole
    // float like `1.0` is refused.
    numeric::Index index = numeric::toIndex(indexValue);
    if (!index.isInteger) {
        runtimeError("Vector index must be an integer, got " + typeName(indexValue) + ".");
    }
    auto* vector = static_cast<ObjVector*>(asObj(receiver));
    auto length = static_cast<int64_t>(vector->elements.size());
    int64_t resolved = index.value < 0 ? index.value + length : index.value;
    if (resolved < 0 || resolved >= length) {
        runtimeError("Vector index " + stringify(indexValue) + " out of range for vector of length " +
                      std::to_string(vector->elements.size()) + ".");
    }
    return static_cast<size_t>(resolved);
}

Value Vm::getSlice(const Value& receiver, const Value& startValue, const Value& stopValue) {
    if (!isObj(receiver) || asObj(receiver)->type != ObjType::Vector) {
        runtimeError("Only vectors can be sliced, got " + typeName(receiver) + ".");
    }
    auto* vector = static_cast<ObjVector*>(asObj(receiver));
    auto length = static_cast<int64_t>(vector->elements.size());

    auto resolveBound = [&](const Value& boundValue, int64_t defaultValue, const char* label) {
        if (isNil(boundValue)) return defaultValue;
        numeric::Index bound = numeric::toIndex(boundValue);
        if (!bound.isInteger) {
            runtimeError(std::string("Slice ") + label + " bound must be an integer, got " + typeName(boundValue) + ".");
        }
        return bound.value < 0 ? bound.value + length : bound.value;
    };

    // Out-of-range/start-after-stop bounds clamp to an empty result
    // rather than erroring, unlike single-index access -- decision 3's
    // deliberately more lenient treatment of a degenerate slice range.
    int64_t start = std::clamp(resolveBound(startValue, 0, "start"), int64_t{0}, length);
    int64_t stop = std::clamp(resolveBound(stopValue, length - 1, "stop"), int64_t{-1}, length - 1);

    auto* result = allocate<ObjVector>();
    if (start <= stop) {
        auto startIt = vector->elements.begin() + static_cast<std::ptrdiff_t>(start);
        auto stopIt = vector->elements.begin() + static_cast<std::ptrdiff_t>(stop) + 1;
        result->elements.assign(startIt, stopIt);
    }
    return objValue(result);
}

void Vm::numericSlowPath(bytecode::OpCode op, const Value& a, const Value& b) {
    using bytecode::OpCode;
    Value result;
    try {
        switch (op) {
            case OpCode::Add: result = numeric::add(*this, a, b); break;
            case OpCode::Subtract: result = numeric::subtract(*this, a, b); break;
            case OpCode::Multiply: result = numeric::multiply(*this, a, b); break;
            case OpCode::Divide: result = numeric::divide(*this, a, b); break;
            case OpCode::Modulo: result = numeric::modulo(*this, a, b); break;
            case OpCode::Power: result = numeric::power(*this, a, b); break;
            case OpCode::Negate: result = numeric::negate(*this, a); break;
            case OpCode::IntDivide: result = numeric::intDivide(*this, a, b); break;
            case OpCode::BitAnd: result = numeric::bitAnd(*this, a, b); break;
            case OpCode::BitOr: result = numeric::bitOr(*this, a, b); break;
            case OpCode::BitXor: result = numeric::bitXor(*this, a, b); break;
            case OpCode::BitNot: result = numeric::bitNot(*this, a); break;
            case OpCode::ShiftLeft: result = numeric::shiftLeft(*this, a, b); break;
            case OpCode::ShiftRight: result = numeric::shiftRight(*this, a, b); break;
            case OpCode::Greater: result = boolValue(numeric::compare(a, b) > 0); break;
            case OpCode::GreaterEqual: result = boolValue(numeric::compare(a, b) >= 0); break;
            case OpCode::Less: result = boolValue(numeric::compare(a, b) < 0); break;
            case OpCode::LessEqual: result = boolValue(numeric::compare(a, b) <= 0); break;
            default: throw std::logic_error("numericSlowPath: not a numeric opcode");
        }
    } catch (const RuntimeError& error) {
        runtimeError(error.what());
    }
    push(result);
}

uint8_t Vm::readByte(CallFrame& frame) { return frame.closure->function->chunk.code[frame.ip++]; }

uint16_t Vm::readU16(CallFrame& frame) {
    const auto& code = frame.closure->function->chunk.code;
    uint16_t value = static_cast<uint16_t>(code[frame.ip]) | (static_cast<uint16_t>(code[frame.ip + 1]) << 8);
    frame.ip += 2;
    return value;
}

Value& Vm::constantAt(CallFrame& frame, uint16_t index) { return frame.closure->function->chunk.constants[index]; }

ObjString* Vm::stringConstantAt(CallFrame& frame, uint16_t index) {
    return static_cast<ObjString*>(asObj(constantAt(frame, index)));
}

void Vm::callValue(const Value& callee, int argCount) {
    if (isObj(callee)) {
        Obj* obj = asObj(callee);
        switch (obj->type) {
            case ObjType::Closure:
                call(static_cast<ObjClosure*>(obj), argCount);
                return;
            case ObjType::NativeFunction:
                callNative(static_cast<ObjNativeFunction*>(obj), argCount);
                return;
            case ObjType::Class:
                callClass(static_cast<ObjClass*>(obj), argCount);
                return;
            case ObjType::BoundMethod: {
                auto* bound = static_cast<ObjBoundMethod*>(obj);
                size_t calleeIndex = stack.size() - static_cast<size_t>(argCount) - 1;
                stack[calleeIndex] = bound->receiver;
                callMethod(bound->method, argCount, /*isInitializer=*/false);
                return;
            }
            default:
                break;
        }
    }
    runtimeError(typeName(callee) + " value is not callable.");
}

void Vm::callNative(ObjNativeFunction* native, int argCount) {
    if (argCount != native->arity) {
        runtimeError("Expected " + std::to_string(native->arity) + " argument(s) but got " +
                     std::to_string(argCount) + ".");
    }

    // Copied into a plain `std::vector` for the native function's own
    // simple ABI (`deque` doesn't offer a contiguous view to hand out
    // instead). Safe even though `native->function` may itself allocate
    // (`concat` builds its result string): the arguments stay right where
    // they are on `stack` -- still scanned as a GC root -- until
    // `truncateStack` removes them *after* the call returns.
    std::vector<Value> args(stack.end() - argCount, stack.end());
    // A native reports a fault by throwing a bare `RuntimeError`, with no
    // idea where in the user's program it was called from -- re-raised
    // here through `runtimeError` so it carries the call site's
    // `[file, line N]` like every other runtime fault.
    Value result;
    try {
        result = native->function(*this, args);
    } catch (const RuntimeError& error) {
        runtimeError(error.what());
    }
    truncateStack(stack.size() - static_cast<size_t>(argCount) - 1);
    push(result);
}

void Vm::call(ObjClosure* closure, int argCount) {
    if (argCount != closure->function->arity) {
        runtimeError("Expected " + std::to_string(closure->function->arity) + " argument(s) but got " +
                     std::to_string(argCount) + ".");
    }
    if (frames.size() >= kMaxFrames) {
        runtimeError("Stack overflow.");
    }

    CallFrame frame;
    frame.closure = closure;
    frame.ip = 0;
    frame.stackBase = stack.size() - static_cast<size_t>(argCount);
    frame.resultIndex = frame.stackBase - 1;
    frames.push_back(frame);
}

void Vm::callMethod(ObjClosure* method, int argCount, bool isInitializer) {
    if (argCount != method->function->arity) {
        runtimeError("Expected " + std::to_string(method->function->arity) + " argument(s) but got " +
                     std::to_string(argCount) + ".");
    }
    if (frames.size() >= kMaxFrames) {
        runtimeError("Stack overflow.");
    }

    CallFrame frame;
    frame.closure = method;
    frame.ip = 0;
    // `self` occupies slot 0 (already placed there by the caller -- see
    // `callValue`'s BoundMethod case and `callClass`), so, unlike a plain
    // call, there's no separate callee slot *below* stackBase here.
    frame.stackBase = stack.size() - static_cast<size_t>(argCount) - 1;
    frame.resultIndex = frame.stackBase;
    frame.isInitializer = isInitializer;
    frames.push_back(frame);
}

void Vm::callClass(ObjClass* klass, int argCount) {
    size_t calleeIndex = stack.size() - static_cast<size_t>(argCount) - 1;
    auto* instance = allocate<ObjInstance>(klass);
    // Every declared property (own class's plus every ancestor's, already
    // flattened into `klass->properties` by `Inherit`) gets a field slot
    // up front, starting `Undef` -- docs/PLAN-0.2.md decision 8's
    // addendum retires the old "springs into existence on assignment"
    // model (see `ObjInstance`'s doc comment).
    for (const auto& entry : klass->properties) {
        instance->fields[entry.first] = UndefValue;
    }
    // Overwrites the class value's own slot -- mirrors `poc`'s
    // `IqaloxClass.call`, which always returns the freshly created
    // instance regardless of what (if anything) `init` itself returns.
    stack[calleeIndex] = objValue(instance);

    auto it = klass->methods.find("init");
    if (it == klass->methods.end()) {
        if (argCount != 0) {
            runtimeError("Expected 0 argument(s) but got " + std::to_string(argCount) + ".");
        }
        // No frame to push -- `stack[calleeIndex]` already holds the
        // finished call's result (the instance), and there are no
        // leftover arguments to discard.
        return;
    }
    callMethod(it->second, argCount, /*isInitializer=*/true);
}

ObjUpvalue* Vm::captureUpvalue(size_t stackIndex) {
    ObjUpvalue* prev = nullptr;
    ObjUpvalue* current = openUpvalues;
    while (current != nullptr && current->stackIndex > stackIndex) {
        prev = current;
        current = current->nextOpen;
    }
    if (current != nullptr && current->stackIndex == stackIndex) {
        return current;
    }

    ObjUpvalue* created = allocate<ObjUpvalue>(stackIndex);
    created->nextOpen = current;
    if (prev == nullptr) {
        openUpvalues = created;
    } else {
        prev->nextOpen = created;
    }
    return created;
}

Value Vm::bindMethod(ObjClass* klass, const Value& receiver, const std::string& name, bool internal) {
    auto it = klass->methods.find(name);
    if (it == klass->methods.end()) {
        runtimeError("Undefined property '" + name + "'.");
    }
    // decision 11: `init` is always externally callable regardless of any
    // `pub` annotation; every other non-`pub` method reads as genuinely
    // undefined from outside, exactly like a nonexistent property does --
    // matching decision 10's "invisible" framing for properties.
    if (!internal && name != "init" && !klass->publicMethods.contains(name)) {
        runtimeError("Undefined property '" + name + "'.");
    }
    return objValue(allocate<ObjBoundMethod>(receiver, it->second));
}

void Vm::interpret(ObjFunction* script) {
    // This allocation itself must run before `gcEnabled` flips on --
    // `script` isn't reachable from any root yet, only from this local
    // variable (see `allocate`'s doc comment).
    auto* closure = allocate<ObjClosure>(script);
    push(objValue(closure));
    gcEnabled = true;
    call(closure, 0);
    run();
}

void Vm::run() {
    using bytecode::OpCode;

    for (;;) {
        CallFrame& frame = frames.back();
        // Every chunk `compiler/` emits ends in a `Return` (see
        // `Codegen.fs`'s unconditional trailing `Nil; Return`), which
        // always exits this loop or pops back to a caller before `ip`
        // could run off the end -- this check only exists to fail
        // cleanly on a truncated/malformed *bytecode file* that lacks
        // one, rather than reading past the end of `code`.
        if (frame.ip >= frame.closure->function->chunk.code.size()) {
            runtimeError("Malformed chunk: ran off the end without a RETURN.");
        }
        // Captured before reading the opcode/operand bytes below, so
        // `runtimeError` can still find this instruction's own line after
        // `ip` has advanced past it (or after a callee this instruction
        // invoked has pushed further frames of its own -- `frames.back()`
        // during a callee's own arity/stack-overflow check is still this
        // frame, since that check runs before the new one is pushed).
        frame.currentInstructionIp = frame.ip;
        auto op = static_cast<OpCode>(readByte(frame));

        switch (op) {
            case OpCode::Constant:
                push(constantAt(frame, readU16(frame)));
                break;
            case OpCode::Nil:
                push(NilValue);
                break;
            case OpCode::True:
                push(boolValue(true));
                break;
            case OpCode::False:
                push(boolValue(false));
                break;
            case OpCode::Undef:
                push(UndefValue);
                break;
            case OpCode::Pop:
                pop();
                break;
            case OpCode::PopN:
                truncateStack(stack.size() - readU16(frame));
                break;
            case OpCode::GetLocal: {
                Value value = stack[frame.stackBase + readU16(frame)];
                checkNotUndef(value, "");
                push(value);
                break;
            }
            case OpCode::SetLocal:
                stack[frame.stackBase + readU16(frame)] = peek(0);
                break;
            case OpCode::GetUpvalue: {
                Value value = readUpvalue(frame.closure->upvalues[readU16(frame)]);
                checkNotUndef(value, "");
                push(value);
                break;
            }
            case OpCode::SetUpvalue:
                writeUpvalue(frame.closure->upvalues[readU16(frame)], peek(0));
                break;
            case OpCode::GetGlobal: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                auto it = globals.find(name->value);
                if (it == globals.end()) {
                    runtimeError("Undefined variable '" + displayGlobalName(name->value) + "'.");
                }
                checkNotUndef(it->second, name->value);
                push(it->second);
                break;
            }
            case OpCode::SetGlobal: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                auto it = globals.find(name->value);
                if (it == globals.end()) {
                    runtimeError("Undefined variable '" + displayGlobalName(name->value) + "'.");
                }
                it->second = peek(0);
                break;
            }
            case OpCode::DefineGlobal: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                globals[name->value] = pop();
                break;
            }
            // Arithmetic (docs/PLAN-0.4.md decisions 3-6): numeric.cpp has the
            // rules. Operands are popped first; each operation reads them
            // fully before it allocates its result, so a collection that
            // allocation triggers can't free an operand still in use.
            case OpCode::Add: {
                Value b = pop(), a = pop();
                int64_t r;
                if (isSmallInt(a) && isSmallInt(b) && !numeric::addOverflows(asSmallInt(a), asSmallInt(b), r)) {
                    push(smallIntValue(r));
                } else if (isDouble(a) && isDouble(b)) {
                    push(doubleValue(asDouble(a) + asDouble(b)));
                } else {
                    numericSlowPath(OpCode::Add, a, b);
                }
                break;
            }
            case OpCode::Subtract: {
                Value b = pop(), a = pop();
                int64_t r;
                if (isSmallInt(a) && isSmallInt(b) && !numeric::subtractOverflows(asSmallInt(a), asSmallInt(b), r)) {
                    push(smallIntValue(r));
                } else if (isDouble(a) && isDouble(b)) {
                    push(doubleValue(asDouble(a) - asDouble(b)));
                } else {
                    numericSlowPath(OpCode::Subtract, a, b);
                }
                break;
            }
            case OpCode::Multiply: {
                Value b = pop(), a = pop();
                int64_t r;
                if (isSmallInt(a) && isSmallInt(b) && !numeric::multiplyOverflows(asSmallInt(a), asSmallInt(b), r)) {
                    push(smallIntValue(r));
                } else if (isDouble(a) && isDouble(b)) {
                    push(doubleValue(asDouble(a) * asDouble(b)));
                } else {
                    numericSlowPath(OpCode::Multiply, a, b);
                }
                break;
            }
            case OpCode::Divide: {
                Value b = pop(), a = pop();
                numericSlowPath(OpCode::Divide, a, b);
                break;
            }
            case OpCode::Modulo: {
                Value b = pop(), a = pop();
                // Floored, like numeric::modulo; y == -1 and 0 take the slow
                // path (INT64_MIN % -1 is undefined, and 0 is an error).
                if (isSmallInt(a) && isSmallInt(b) && asSmallInt(b) > 0) {
                    int64_t r = asSmallInt(a) % asSmallInt(b);
                    push(smallIntValue(r < 0 ? r + asSmallInt(b) : r));
                } else {
                    numericSlowPath(OpCode::Modulo, a, b);
                }
                break;
            }
            case OpCode::Power: {
                Value b = pop(), a = pop();
                numericSlowPath(OpCode::Power, a, b);
                break;
            }
            case OpCode::Negate: {
                Value a = pop();
                numericSlowPath(OpCode::Negate, a, a);
                break;
            }
            // docs/PLAN-0.4.md Phase 2. Inline fast paths for two int64s
            // where the result can't overflow; everything else (big
            // integers, errors) goes through numericSlowPath.
            case OpCode::IntDivide: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b) && asSmallInt(b) > 0) {
                    int64_t x = asSmallInt(a), y = asSmallInt(b);
                    int64_t q = x / y;
                    push(smallIntValue((x % y != 0 && x < 0) ? q - 1 : q));  // floor, y > 0
                } else {
                    numericSlowPath(OpCode::IntDivide, a, b);
                }
                break;
            }
            case OpCode::BitAnd: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(smallIntValue(asSmallInt(a) & asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::BitAnd, a, b);
                }
                break;
            }
            case OpCode::BitOr: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(smallIntValue(asSmallInt(a) | asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::BitOr, a, b);
                }
                break;
            }
            case OpCode::BitXor: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(smallIntValue(asSmallInt(a) ^ asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::BitXor, a, b);
                }
                break;
            }
            case OpCode::BitNot: {
                Value a = pop();
                if (isSmallInt(a)) {
                    push(smallIntValue(~asSmallInt(a)));
                } else {
                    numericSlowPath(OpCode::BitNot, a, a);
                }
                break;
            }
            case OpCode::ShiftLeft: {
                Value b = pop(), a = pop();
                // Safe when |a| < 2^(62 - b): the result stays below 2^62.
                if (isSmallInt(a) && isSmallInt(b) && asSmallInt(b) >= 0 && asSmallInt(b) < 62) {
                    int64_t x = asSmallInt(a), limit = int64_t{1} << (62 - asSmallInt(b));
                    if (x > -limit && x < limit) {
                        push(smallIntValue(x * (int64_t{1} << asSmallInt(b))));
                        break;
                    }
                }
                numericSlowPath(OpCode::ShiftLeft, a, b);
                break;
            }
            case OpCode::ShiftRight: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b) && asSmallInt(b) >= 0) {
                    // C++20 `>>` on a negative signed value is arithmetic.
                    int64_t x = asSmallInt(a), n = asSmallInt(b);
                    push(smallIntValue(n >= 63 ? (x < 0 ? -1 : 0) : x >> n));
                } else {
                    numericSlowPath(OpCode::ShiftRight, a, b);
                }
                break;
            }
            case OpCode::Not:
                push(boolValue(!isTruthy(pop())));
                break;
            case OpCode::Equal: {
                Value b = pop(), a = pop();
                push(boolValue(valuesEqual(a, b)));
                break;
            }
            case OpCode::NotEqual: {
                Value b = pop(), a = pop();
                push(boolValue(!valuesEqual(a, b)));
                break;
            }
            case OpCode::Greater: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(boolValue(asSmallInt(a) > asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::Greater, a, b);
                }
                break;
            }
            case OpCode::GreaterEqual: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(boolValue(asSmallInt(a) >= asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::GreaterEqual, a, b);
                }
                break;
            }
            case OpCode::Less: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(boolValue(asSmallInt(a) < asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::Less, a, b);
                }
                break;
            }
            case OpCode::LessEqual: {
                Value b = pop(), a = pop();
                if (isSmallInt(a) && isSmallInt(b)) {
                    push(boolValue(asSmallInt(a) <= asSmallInt(b)));
                } else {
                    numericSlowPath(OpCode::LessEqual, a, b);
                }
                break;
            }
            case OpCode::Jump:
                frame.ip = readU16(frame);
                break;
            case OpCode::JumpIfFalse: {
                uint16_t target = readU16(frame);
                if (!isTruthy(peek(0))) frame.ip = target;
                break;
            }
            case OpCode::JumpIfNotNil: {
                uint16_t target = readU16(frame);
                if (!isNil(peek(0))) frame.ip = target;
                break;
            }
            case OpCode::BuildVector: {
                uint16_t count = readU16(frame);
                auto* vector = allocate<ObjVector>();
                vector->elements.assign(stack.end() - count, stack.end());
                truncateStack(stack.size() - count);
                push(objValue(vector));
                break;
            }
            case OpCode::Call: {
                uint16_t argCount = readU16(frame);
                callValue(peek(argCount), argCount);
                break;
            }
            case OpCode::Closure: {
                uint16_t functionIndex = readU16(frame);
                uint16_t upvalueCount = readU16(frame);
                auto* function = static_cast<ObjFunction*>(asObj(constantAt(frame, functionIndex)));

                std::vector<ObjUpvalue*> upvalues;
                upvalues.reserve(upvalueCount);
                for (uint16_t i = 0; i < upvalueCount; ++i) {
                    bool fromEnclosingLocal = readByte(frame) != 0;
                    uint16_t index = readU16(frame);
                    upvalues.push_back(fromEnclosingLocal ? captureUpvalue(frame.stackBase + index)
                                                           : frame.closure->upvalues[index]);
                }

                auto* closure = allocate<ObjClosure>(function);
                closure->upvalues = std::move(upvalues);
                push(objValue(closure));
                break;
            }
            case OpCode::Return: {
                Value result = pop();
                if (frame.isInitializer) {
                    // Always the instance, regardless of what `init`
                    // itself returned -- still sitting at `resultIndex`,
                    // where `callClass` placed it before this frame ever
                    // started running (see `CallFrame::isInitializer`).
                    result = stack[frame.resultIndex];
                }
                size_t resultIndex = frame.resultIndex;
                truncateStack(resultIndex);
                frames.pop_back();
                if (frames.empty()) {
                    return;
                }
                push(result);
                break;
            }
            case OpCode::Class: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                push(objValue(allocate<ObjClass>(name->value)));
                break;
            }
            case OpCode::Method: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                auto* closure = static_cast<ObjClosure*>(asObj(pop()));
                auto* klass = static_cast<ObjClass*>(asObj(peek(0)));
                klass->methods[name->value] = closure;
                // An override (own or inherited name) is private unless
                // *this* declaration says otherwise -- clears any
                // publicMethods entry Inherit may have copied in.
                klass->publicMethods.erase(name->value);
                break;
            }
            case OpCode::MethodPub: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                auto* closure = static_cast<ObjClosure*>(asObj(pop()));
                auto* klass = static_cast<ObjClass*>(asObj(peek(0)));
                klass->methods[name->value] = closure;
                klass->publicMethods.insert(name->value);
                break;
            }
            case OpCode::Inherit: {
                const Value& superclassValue = peek(1);
                if (!isObj(superclassValue) || asObj(superclassValue)->type != ObjType::Class) {
                    runtimeError("Superclass must be a class.");
                }
                auto* superclass = static_cast<ObjClass*>(asObj(superclassValue));
                auto* subclass = static_cast<ObjClass*>(asObj(peek(0)));
                // Copies the superclass's methods/properties in *now*,
                // before any of the subclass's own Method*/Property*
                // opcodes run -- so a same-named subclass method
                // naturally overrides the inherited entry, and `find`-by-
                // name never has to walk a superclass chain at all (see
                // `ObjClass`'s doc comment). `Resolver.fs`'s
                // `preRegisterClasses` already rejects a property
                // redeclared anywhere up a class's own ancestor chain at
                // compile time, so `properties` is never overwritten here
                // the way `methods` intentionally can be.
                subclass->methods = superclass->methods;
                subclass->publicMethods = superclass->publicMethods;
                subclass->properties = superclass->properties;
                break;
            }
            case OpCode::Mixin: {
                // docs/PLAN-0.2.md Phase 8, decision 12's `with M1, M2`
                // (this version's simplified, non-C3 approximation of
                // open question 2): merges the mixin's own methods/
                // properties into the class being declared, exactly like
                // Inherit -- except the mixin value itself is popped, not
                // left on the stack, since (unlike a superclass) nothing
                // needs it again afterward. `Resolver.fs`'s
                // `effectiveClassMembers` already rejects any name shared
                // between two composed sources (the superclass and each
                // mixin) at compile time, so plain insertion here can
                // never silently clobber something the class cares about
                // -- only the class's own later Property*/Method* opcodes
                // are expected to overwrite an entry copied in here, and
                // that's the intended "own declaration always wins" rule,
                // not a conflict.
                Value mixinValue = pop();
                if (!isObj(mixinValue) || asObj(mixinValue)->type != ObjType::Class) {
                    runtimeError("Mixin must be a class.");
                }
                auto* mixin = static_cast<ObjClass*>(asObj(mixinValue));
                auto* klass = static_cast<ObjClass*>(asObj(peek(0)));
                for (const auto& entry : mixin->methods) {
                    klass->methods[entry.first] = entry.second;
                }
                for (const auto& name : mixin->publicMethods) {
                    klass->publicMethods.insert(name);
                }
                for (const auto& entry : mixin->properties) {
                    klass->properties[entry.first] = entry.second;
                }
                break;
            }
            case OpCode::PropertyPrivate: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                static_cast<ObjClass*>(asObj(peek(0)))->properties[name->value] = PropertyMeta{false, false};
                break;
            }
            case OpCode::PropertyPrivateMut: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                static_cast<ObjClass*>(asObj(peek(0)))->properties[name->value] = PropertyMeta{false, true};
                break;
            }
            case OpCode::PropertyPub: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                static_cast<ObjClass*>(asObj(peek(0)))->properties[name->value] = PropertyMeta{true, false};
                break;
            }
            case OpCode::PropertyPubMut: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                static_cast<ObjClass*>(asObj(peek(0)))->properties[name->value] = PropertyMeta{true, true};
                break;
            }
            case OpCode::GetProperty: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                Value receiver = pop();
                if (!isObj(receiver) || asObj(receiver)->type != ObjType::Instance) {
                    runtimeError("Only instances have properties.");
                }
                auto* instance = static_cast<ObjInstance*>(asObj(receiver));
                auto fieldIt = instance->fields.find(name->value);
                if (fieldIt != instance->fields.end()) {
                    // Every `fields` entry was pre-populated straight from
                    // `klass->properties` (`Vm::callClass`), so a
                    // corresponding metadata entry always exists here.
                    const PropertyMeta& meta = instance->klass->properties.at(name->value);
                    if (!meta.isPub) {
                        // Reads as genuinely undefined from outside,
                        // matching decision 10's "invisible" framing --
                        // the same wording a nonexistent property/method
                        // gets, not a distinct "it's private" message.
                        runtimeError("Undefined property '" + name->value + "'.");
                    }
                    checkPropertyNotUndef(fieldIt->second, name->value);
                    push(fieldIt->second);
                } else {
                    push(bindMethod(instance->klass, receiver, name->value, /*internal=*/false));
                }
                break;
            }
            case OpCode::GetPropertySelf: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                Value receiver = pop();
                if (!isObj(receiver) || asObj(receiver)->type != ObjType::Instance) {
                    runtimeError("Only instances have properties.");
                }
                auto* instance = static_cast<ObjInstance*>(asObj(receiver));
                auto fieldIt = instance->fields.find(name->value);
                if (fieldIt != instance->fields.end()) {
                    checkPropertyNotUndef(fieldIt->second, name->value);
                    push(fieldIt->second);
                } else {
                    push(bindMethod(instance->klass, receiver, name->value, /*internal=*/true));
                }
                break;
            }
            case OpCode::SetProperty: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                Value value = pop();
                Value receiver = pop();
                if (!isObj(receiver) || asObj(receiver)->type != ObjType::Instance) {
                    runtimeError("Only instances have fields.");
                }
                auto* instance = static_cast<ObjInstance*>(asObj(receiver));
                auto propIt = instance->klass->properties.find(name->value);
                if (propIt == instance->klass->properties.end() || !propIt->second.isPub) {
                    runtimeError("Undefined property '" + name->value + "'.");
                }
                if (!propIt->second.isMut) {
                    runtimeError("Property '" + name->value + "' is not externally mutable.");
                }
                instance->fields[name->value] = value;
                push(value);
                break;
            }
            case OpCode::SetPropertySelf: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                Value value = pop();
                Value receiver = pop();
                if (!isObj(receiver) || asObj(receiver)->type != ObjType::Instance) {
                    runtimeError("Only instances have fields.");
                }
                auto* instance = static_cast<ObjInstance*>(asObj(receiver));
                auto propIt = instance->klass->properties.find(name->value);
                if (propIt == instance->klass->properties.end()) {
                    // Unreachable for a genuinely-compiled `self.x = value`
                    // -- Resolver.fs's decision-8-addendum check already
                    // rejects an undeclared property at compile time.
                    // Kept as a defensive runtime error rather than an
                    // assumption.
                    runtimeError("Undefined property '" + name->value + "'.");
                }
                if (!propIt->second.isMut) {
                    Value& field = instance->fields[name->value];
                    if (!isUndef(field)) {
                        runtimeError("Property '" + name->value +
                                     "' already assigned; immutable properties can only be set once.");
                    }
                }
                instance->fields[name->value] = value;
                push(value);
                break;
            }
            case OpCode::GetSuper: {
                ObjString* name = stringConstantAt(frame, readU16(frame));
                auto* superclass = static_cast<ObjClass*>(asObj(pop()));
                Value self = pop();
                push(bindMethod(superclass, self, name->value, /*internal=*/true));
                break;
            }
            case OpCode::GetIndex: {
                Value indexValue = pop();
                Value receiver = pop();
                size_t index = checkVectorIndex(receiver, indexValue);
                push(static_cast<ObjVector*>(asObj(receiver))->elements[index]);
                break;
            }
            case OpCode::SetIndex: {
                Value value = pop();
                Value indexValue = pop();
                Value receiver = pop();
                size_t index = checkVectorIndex(receiver, indexValue);
                static_cast<ObjVector*>(asObj(receiver))->elements[index] = value;
                push(value);
                break;
            }
            case OpCode::GetSlice: {
                Value stopValue = pop();
                Value startValue = pop();
                Value receiver = pop();
                push(getSlice(receiver, startValue, stopValue));
                break;
            }
            case OpCode::VectorLength: {
                Value receiver = pop();
                if (!isObj(receiver) || asObj(receiver)->type != ObjType::Vector) {
                    runtimeError("Only vectors have a length, got " + typeName(receiver) + ".");
                }
                push(smallIntValue(static_cast<int64_t>(static_cast<ObjVector*>(asObj(receiver))->elements.size())));
                break;
            }
            case OpCode::VectorAppend: {
                Value value = pop();
                Value receiver = pop();
                // Only ever emitted inside the synthetic closures
                // Cons/ListComprehension desugar into (docs/PLAN-0.2.md
                // Phase 3), appending onto an accumulator built with
                // BuildVector just before -- never reachable with a
                // non-vector receiver from any user-written program, so
                // this is an internal-consistency check, not a
                // user-facing type error.
                if (!isObj(receiver) || asObj(receiver)->type != ObjType::Vector) {
                    runtimeError("Internal error: VectorAppend on a non-vector.");
                }
                static_cast<ObjVector*>(asObj(receiver))->elements.push_back(value);
                break;
            }
            case OpCode::VectorExtend: {
                // docs/PLAN-0.2.md Phase 4: `[..., ...source, ...]`
                // -- unlike VectorAppend, `source` is a user-written
                // expression, so a non-vector source is a real,
                // user-facing runtime error, not an internal-consistency
                // check. Pushes the target back (unlike VectorAppend):
                // Codegen.fs's spread-flattening chains several of these
                // purely on the stack, with no accumulator local slot to
                // re-fetch it from in between.
                Value source = pop();
                Value target = pop();
                if (!isObj(source) || asObj(source)->type != ObjType::Vector) {
                    runtimeError("Can only spread a vector, got " + typeName(source) + ".");
                }
                auto& targetElements = static_cast<ObjVector*>(asObj(target))->elements;
                auto& sourceElements = static_cast<ObjVector*>(asObj(source))->elements;
                targetElements.insert(targetElements.end(), sourceElements.begin(), sourceElements.end());
                push(target);
                break;
            }
        }
    }
}

// --- Garbage collection (docs/PLAN-0.1.md decision 7: mark-sweep) ---

void Vm::markValue(const Value& value) {
    if (isObj(value)) markObject(asObj(value));
}

void Vm::markObject(Obj* obj) {
    if (obj == nullptr || obj->marked) return;
    obj->marked = true;
    grayStack.push_back(obj);
}

void Vm::markRoots() {
    for (const Value& value : stack) markValue(value);
    for (auto& [name, value] : globals) markValue(value);
    for (const Value& value : tempRoots) markValue(value);
    for (ObjUpvalue* upvalue = openUpvalues; upvalue != nullptr; upvalue = upvalue->nextOpen) markObject(upvalue);
    // Every `frames[i].closure` is also sitting on `stack` at its own call
    // site (see `call`'s calling convention note in vm.hpp), so the stack
    // scan above already keeps every active frame's closure reachable --
    // no separate frame-walk needed.
}

void Vm::blackenObject(Obj* obj) {
    switch (obj->type) {
        case ObjType::String:
            break;
        case ObjType::Vector:
            for (const Value& element : static_cast<ObjVector*>(obj)->elements) markValue(element);
            break;
        case ObjType::Function:
            for (const Value& constant : static_cast<ObjFunction*>(obj)->chunk.constants) markValue(constant);
            break;
        case ObjType::Closure: {
            auto* closure = static_cast<ObjClosure*>(obj);
            markObject(closure->function);
            for (ObjUpvalue* upvalue : closure->upvalues) markObject(upvalue);
            break;
        }
        case ObjType::Upvalue:
            markValue(static_cast<ObjUpvalue*>(obj)->closed);
            break;
        case ObjType::NativeFunction:
            // A raw C++ function pointer plus a name/arity -- nothing
            // GC-owned to trace through.
            break;
        case ObjType::Class:
            for (auto& [name, closure] : static_cast<ObjClass*>(obj)->methods) markObject(closure);
            break;
        case ObjType::Instance: {
            auto* instance = static_cast<ObjInstance*>(obj);
            markObject(instance->klass);
            for (auto& [name, value] : instance->fields) markValue(value);
            break;
        }
        case ObjType::BoundMethod: {
            auto* bound = static_cast<ObjBoundMethod*>(obj);
            markValue(bound->receiver);
            markObject(bound->method);
            break;
        }
        case ObjType::BigInt:
            // Its digits are mini-gmp memory it owns, not GC objects.
            break;
    }
}

void Vm::traceReferences() {
    while (!grayStack.empty()) {
        Obj* obj = grayStack.back();
        grayStack.pop_back();
        blackenObject(obj);
    }
}

void Vm::sweep() {
    Obj* previous = nullptr;
    Obj* obj = objects;
    while (obj != nullptr) {
        if (obj->marked) {
            obj->marked = false;
            previous = obj;
            obj = obj->next;
        } else {
            Obj* unreached = obj;
            obj = obj->next;
            if (previous == nullptr) {
                objects = obj;
            } else {
                previous->next = obj;
            }
            delete unreached;
        }
    }
}

void Vm::collectGarbage() {
    markRoots();
    traceReferences();
    sweep();
    nextGc = bytesAllocated * kGcHeapGrowFactor;
}

}  // namespace iqalox
