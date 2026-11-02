#pragma once

#include <string>
#include <variant>

namespace iqalox {

struct Obj;

// Distinct from `nil`: the initial value of a `mut`-without-initializer
// `var` (ROADMAP.md's 0.1 scope, docs/PLAN-0.1.md decision 6). Reading a
// slot still holding this is a runtime error -- see `Vm::checkNotUndef` --
// it never appears as an ordinary, user-visible value.
struct UndefTag {};

// A tagged union to start, per docs/PLAN-0.1.md's Phase 6 scope --
// NaN-boxing is a later optimization, not required here.
using Value = std::variant<std::monostate, UndefTag, bool, double, Obj*>;

inline const Value NilValue{};
inline const Value UndefValue{UndefTag{}};

inline bool isNil(const Value& v) { return std::holds_alternative<std::monostate>(v); }
inline bool isUndef(const Value& v) { return std::holds_alternative<UndefTag>(v); }
inline bool isBool(const Value& v) { return std::holds_alternative<bool>(v); }
inline bool isNumber(const Value& v) { return std::holds_alternative<double>(v); }
inline bool isObj(const Value& v) { return std::holds_alternative<Obj*>(v); }

inline bool asBool(const Value& v) { return std::get<bool>(v); }
inline double asNumber(const Value& v) { return std::get<double>(v); }
inline Obj* asObj(const Value& v) { return std::get<Obj*>(v); }

inline Value boolValue(bool b) { return Value{b}; }
inline Value numberValue(double n) { return Value{n}; }
inline Value objValue(Obj* o) { return Value{o}; }

// Lox/Iqalox truthiness: only `nil` and `false` are falsy -- 0, "", and
// empty vectors are all truthy, matching `poc/src/interpreter.py`'s
// `is_truthy`.
bool isTruthy(const Value& v);

// `==`/`!=` structural equality: numbers/bools by value, strings by
// content, vectors element-wise and recursively, everything else
// (functions, closures) by identity -- mirrors Python's default `==`,
// which is all `poc`'s `is_equal` (`return a == b`) actually relies on.
bool valuesEqual(const Value& a, const Value& b);

// Short, human-readable type name for runtime error messages. No source
// location is available at this layer -- format v1 has no debug-info side
// table yet (docs/PLAN-0.1.md's Phase 5 entry notes this as deferred).
std::string typeName(const Value& v);

// Human-readable rendering of a value for the `print`/`concat` natives
// (Phase 7) -- matches `poc/src/interpreter.py`'s `stringify` (numbers
// drop a trailing ".0", strings are unquoted, booleans render lowercase).
// A vector prints as `[e1, e2, ...]` with each element rendered by this
// same function, so `[1, "a"]` prints as `[1, a]`.
std::string stringify(const Value& v);

}  // namespace iqalox
