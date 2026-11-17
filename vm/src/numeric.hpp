#pragma once

#include <compare>
#include <cstdint>
#include <optional>
#include <string>
#include <utility>

#include "mini-gmp.h"
#include "value.hpp"

namespace iqalox {

class Vm;

// Iqalox's numbers (docs/PLAN-0.4.md decisions 1-9 and the Phase 1
// follow-ups): exact, arbitrary-precision integers next to IEEE doubles.
//
// An integer is held inline in `Value` as an `int64_t` whenever it fits, and
// otherwise as a heap `ObjBigInt`. The split is canonical: an `ObjBigInt`
// never holds a value that would fit in 64 bits, so two equal integers
// always have the same representation.
//
// Every arithmetic rule lives here, not in the interpreter loop, so the
// opcodes and the natives (matrix arithmetic) share one implementation.
// The functions throw `RuntimeError` on an Iqalox-level fault; callers in
// the VM loop add the `[file, line N]`.
namespace numeric {

// The largest integer computed, in bits: 2^26 bits, about 20 million
// decimal digits. Matches `compiler/src/Scanner.fs`'s `maxIntegerBits`. A
// result that would be larger is a runtime error, checked before computing
// wherever the size can be predicted (`*`, `^`).
inline constexpr uint64_t kMaxIntegerBits = uint64_t{1} << 26;

// An owning wrapper around a mini-gmp integer. Plain C++ value, never
// GC-managed, which is what makes multi-step arithmetic safe: intermediate
// results live here, and only the final one becomes a heap `ObjBigInt`.
class BigInt {
public:
    BigInt() { mpz_init(value_); }
    explicit BigInt(int64_t n);
    BigInt(const BigInt& other) { mpz_init_set(value_, other.value_); }
    BigInt(BigInt&& other) noexcept {
        mpz_init(value_);
        mpz_swap(value_, other.value_);
    }
    BigInt& operator=(BigInt other) noexcept {
        mpz_swap(value_, other.value_);
        return *this;
    }
    ~BigInt() { mpz_clear(value_); }

    mpz_ptr get() { return value_; }
    mpz_srcptr get() const { return value_; }

    // Bits in the magnitude (0 for zero).
    uint64_t bitLength() const { return mpz_sgn(value_) == 0 ? 0 : mpz_sizeinbase(value_, 2); }
    bool fitsInt64() const;
    int64_t toInt64() const;  // precondition: fitsInt64()
    std::string toString() const;

private:
    mpz_t value_;
};

bool isInteger(const Value& v);  // int64 or ObjBigInt
bool isFloat(const Value& v);
bool isNumber(const Value& v);  // any of the above

// A borrowed view of an integer Value as a BigInt (copies the int64 case).
BigInt toBigInt(const Value& v);  // precondition: isInteger(v)

// The canonical Value for an exact integer: inline int64 when it fits,
// otherwise a newly allocated `ObjBigInt` (the only allocation here).
Value integerValue(Vm& vm, BigInt n);

// An integer converted to the nearest double (round half to even). Throws
// "Integer too large to convert to float." beyond the double range.
double toDouble(const Value& v);  // precondition: isNumber(v)

// The arithmetic opcodes. Each throws "Operands must be numbers." for a
// non-numeric operand, plus its own errors (see numeric.cpp).
Value add(Vm& vm, const Value& a, const Value& b);
Value subtract(Vm& vm, const Value& a, const Value& b);
Value multiply(Vm& vm, const Value& a, const Value& b);
Value divide(Vm& vm, const Value& a, const Value& b);
Value modulo(Vm& vm, const Value& a, const Value& b);
Value power(Vm& vm, const Value& a, const Value& b);
Value negate(Vm& vm, const Value& a);

// `//` and the bitwise operators (docs/PLAN-0.4.md decisions 4 and 10).
// Integers only: a float or any other operand is a runtime error. `//`
// floors (`-7 // 2 == -4`); a zero divisor is "Division by zero.". The
// bitwise operators treat integers as infinite two's complement
// (`~5 == -6`, `-8 >> 1 == -4`, `-1 & 0xFF == 255`); a negative shift count
// is an error, and `<<` is subject to the integer size limit.
Value intDivide(Vm& vm, const Value& a, const Value& b);
Value bitAnd(Vm& vm, const Value& a, const Value& b);
Value bitOr(Vm& vm, const Value& a, const Value& b);
Value bitXor(Vm& vm, const Value& a, const Value& b);
Value bitNot(Vm& vm, const Value& a);
Value shiftLeft(Vm& vm, const Value& a, const Value& b);
Value shiftRight(Vm& vm, const Value& a, const Value& b);

// Ordering for `<`, `<=`, `>`, `>=`: exact across ints and floats (no
// rounding, no "too large" error); unordered when either side is `nan`.
// Throws "Operands must be numbers." for a non-numeric operand.
std::partial_ordering compare(const Value& a, const Value& b);

// An integer Value as a vector/string position. `isInteger` is false for a
// float or a non-number (callers report those); a big integer clamps to the
// int64 limits, which are out of range for any real vector anyway.
struct Index {
    bool isInteger;
    int64_t value;
};
Index toIndexSlow(const Value& v);
inline Index toIndex(const Value& v) {
    if (isSmallInt(v)) return {true, asSmallInt(v)};
    return toIndexSlow(v);
}

// Overflow-checked int64 arithmetic for the interpreter's inline fast
// paths (and numeric.cpp's own). True means "overflowed, take the exact
// path"; on MSVC, multiplication is conservative.
inline bool addOverflows(int64_t a, int64_t b, int64_t& result) {
#if defined(__GNUC__) || defined(__clang__)
    return __builtin_add_overflow(a, b, &result);
#else
    if ((b > 0 && a > INT64_MAX - b) || (b < 0 && a < INT64_MIN - b)) return true;
    result = a + b;
    return false;
#endif
}
inline bool subtractOverflows(int64_t a, int64_t b, int64_t& result) {
#if defined(__GNUC__) || defined(__clang__)
    return __builtin_sub_overflow(a, b, &result);
#else
    if ((b < 0 && a > INT64_MAX + b) || (b > 0 && a < INT64_MIN + b)) return true;
    result = a - b;
    return false;
#endif
}
inline bool multiplyOverflows(int64_t a, int64_t b, int64_t& result) {
#if defined(__GNUC__) || defined(__clang__)
    return __builtin_mul_overflow(a, b, &result);
#else
    constexpr int64_t kLimit = int64_t{1} << 31;
    if (a > -kLimit && a < kLimit && b > -kLimit && b < kLimit) {
        result = a * b;
        return false;
    }
    return true;
#endif
}

std::string formatInteger(const Value& v);  // precondition: isInteger(v)
// A float as `print` shows it: shortest round-trip digits, always with a
// decimal point (`5.0`, `1.0e+16`), or `inf`/`-inf`/`nan`.
std::string formatFloat(double v);

}  // namespace numeric
}  // namespace iqalox
