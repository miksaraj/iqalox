#include "value.hpp"


#include "numeric.hpp"
#include "object.hpp"

namespace iqalox {

bool isTruthy(const Value& v) {
    if (isNil(v) || isUndef(v)) return false;
    if (isBool(v)) return asBool(v);
    return true;
}

namespace {

bool objectsEqual(Obj* a, Obj* b) {
    if (a == b) return true;
    if (a->type != b->type) return false;

    switch (a->type) {
        case ObjType::String:
            return static_cast<ObjString*>(a)->value == static_cast<ObjString*>(b)->value;
        case ObjType::Vector: {
            auto& left = static_cast<ObjVector*>(a)->elements;
            auto& right = static_cast<ObjVector*>(b)->elements;
            if (left.size() != right.size()) return false;
            for (size_t i = 0; i < left.size(); ++i) {
                if (!valuesEqual(left[i], right[i])) return false;
            }
            return true;
        }
        // Functions/closures/native functions/upvalues/classes/instances/
        // bound methods have no `poc` equivalent with structural equality
        // -- Python's default `==` (what `is_equal` relies on for
        // everything it doesn't special-case) falls back to identity,
        // already handled by the `a == b` check above.
        // Canonical representation (numeric.hpp): equal big integers are
        // both ObjBigInts, never one inline int64 and one ObjBigInt.
        case ObjType::BigInt:
            return mpz_cmp(static_cast<ObjBigInt*>(a)->value.get(), static_cast<ObjBigInt*>(b)->value.get()) == 0;
        case ObjType::Function:
        case ObjType::Closure:
        case ObjType::NativeFunction:
        case ObjType::Upvalue:
        case ObjType::Class:
        case ObjType::Instance:
        case ObjType::BoundMethod:
            return false;
    }
    return false;
}

}  // namespace

bool valuesEqual(const Value& a, const Value& b) {
    if (a.index() != b.index()) return false;
    if (isNil(a) || isUndef(a)) return true;
    if (isBool(a)) return asBool(a) == asBool(b);
    if (isSmallInt(a)) return asSmallInt(a) == asSmallInt(b);
    // IEEE: `nan == nan` is false, `0.0 == -0.0` is true.
    if (isDouble(a)) return asDouble(a) == asDouble(b);
    return objectsEqual(asObj(a), asObj(b));
}

std::string typeName(const Value& v) {
    if (isNil(v)) return "nil";
    if (isUndef(v)) return "undef";
    if (isBool(v)) return "bool";
    if (isSmallInt(v)) return "int";
    if (isDouble(v)) return "float";
    switch (asObj(v)->type) {
        case ObjType::String: return "string";
        case ObjType::Vector: return "vector";
        case ObjType::Function: return "function";
        case ObjType::Closure: return "function";
        case ObjType::NativeFunction: return "function";
        case ObjType::Upvalue: return "upvalue";
        case ObjType::Class: return "class";
        case ObjType::Instance: return "instance";
        case ObjType::BoundMethod: return "function";
        case ObjType::BigInt: return "int";
    }
    return "value";
}


std::string stringify(const Value& v) {
    if (isNil(v)) return "nil";
    if (isUndef(v)) return "undef";
    if (isBool(v)) return asBool(v) ? "true" : "false";
    if (isSmallInt(v)) return numeric::formatInteger(v);
    if (isDouble(v)) return numeric::formatFloat(asDouble(v));

    switch (asObj(v)->type) {
        case ObjType::String:
            return static_cast<ObjString*>(asObj(v))->value;
        case ObjType::Vector: {
            const auto& elements = static_cast<ObjVector*>(asObj(v))->elements;
            std::string out = "[";
            for (size_t i = 0; i < elements.size(); ++i) {
                if (i > 0) out += ", ";
                out += stringify(elements[i]);
            }
            out += "]";
            return out;
        }
        case ObjType::Function:
            return "<fun " + static_cast<ObjFunction*>(asObj(v))->name + ">";
        case ObjType::Closure:
            return "<fun " + static_cast<ObjClosure*>(asObj(v))->function->name + ">";
        case ObjType::NativeFunction:
            return "<native fun " + static_cast<ObjNativeFunction*>(asObj(v))->name + ">";
        case ObjType::Upvalue:
            return "<upvalue>";
        case ObjType::Class:
            return "<class " + static_cast<ObjClass*>(asObj(v))->name + ">";
        case ObjType::Instance:
            return "<" + static_cast<ObjInstance*>(asObj(v))->klass->name + " instance>";
        case ObjType::BoundMethod:
            return "<fun " + static_cast<ObjBoundMethod*>(asObj(v))->method->function->name + ">";
        case ObjType::BigInt:
            return numeric::formatInteger(v);
    }
    return "<value>";
}

}  // namespace iqalox
