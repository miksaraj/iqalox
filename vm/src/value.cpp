#include "value.hpp"

#include <charconv>
#include <cmath>

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
    if (isNumber(a)) return asNumber(a) == asNumber(b);
    return objectsEqual(asObj(a), asObj(b));
}

std::string typeName(const Value& v) {
    if (isNil(v)) return "nil";
    if (isUndef(v)) return "undef";
    if (isBool(v)) return "bool";
    if (isNumber(v)) return "number";
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
    }
    return "value";
}

namespace {

// Python's `repr(float)`: the shortest decimal that round-trips back to
// the same double, in fixed notation for "ordinary-sized" magnitudes and
// scientific notation outside that range -- switching at the same
// thresholds CPython does (exponent < -4 or >= 16), confirmed empirically
// against `python3 -c "print(repr(v))"` for both boundaries and a range
// of ordinary/huge/tiny values, since libstdc++'s own `to_chars` "general"
// format picks fixed vs. scientific by shortest-output-length instead,
// which disagrees with Python for plenty of everyday values (e.g. it
// renders 100000000.0 as "1e+08"; Python keeps "100000000.0").
std::string formatNumber(double v) {
    if (std::isnan(v)) return "nan";
    if (std::isinf(v)) return v < 0 ? "-inf" : "inf";

    char buf[64];
    auto result = std::to_chars(buf, buf + sizeof(buf), v, std::chars_format::scientific);
    std::string sci(buf, result.ptr);

    size_t ePos = sci.find('e');
    int exponent = std::stoi(sci.substr(ePos + 1));

    // `std::to_chars`' own scientific form already matches Python's exact
    // spelling (lowercase `e`, sign, minimum 2-digit exponent) for every
    // case checked, so reuse it verbatim rather than reassembling it.
    if (exponent < -4 || exponent >= 16) {
        return sci;
    }

    bool negative = sci.front() == '-';
    size_t start = negative ? 1 : 0;
    std::string digits;
    for (size_t i = start; i < ePos; ++i) {
        if (sci[i] != '.') digits.push_back(sci[i]);
    }

    std::string out = negative ? "-" : "";
    if (exponent >= 0) {
        auto pointPos = static_cast<size_t>(exponent) + 1;
        if (digits.size() <= pointPos) {
            // A whole number -- deliberately left with no trailing ".0"
            // here, matching poc's `stringify`, which strips exactly that
            // suffix from Python's own `str(float)`.
            out += digits;
            out.append(pointPos - digits.size(), '0');
        } else {
            out += digits.substr(0, pointPos);
            out += '.';
            out += digits.substr(pointPos);
        }
    } else {
        out += "0.";
        out.append(static_cast<size_t>(-exponent - 1), '0');
        out += digits;
    }
    return out;
}

}  // namespace

std::string stringify(const Value& v) {
    if (isNil(v)) return "nil";
    if (isUndef(v)) return "undef";
    if (isBool(v)) return asBool(v) ? "true" : "false";
    if (isNumber(v)) return formatNumber(asNumber(v));

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
    }
    return "<value>";
}

}  // namespace iqalox
