#include "numeric.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <limits>

#include "object.hpp"
#include "vm.hpp"

namespace iqalox::numeric {

namespace {

[[noreturn]] void fail(const std::string& message) { throw RuntimeError(message); }

bool isBigInt(const Value& v) { return isObj(v) && asObj(v)->type == ObjType::BigInt; }

const BigInt& bigOf(const Value& v) { return static_cast<ObjBigInt*>(asObj(v))->value; }

// mini-gmp's `long`-based setters are only 32 bits wide on Windows, so
// 64-bit values go through mpz_import/mpz_export instead.
void setInt64(mpz_ptr z, int64_t n) {
    uint64_t magnitude = n < 0 ? uint64_t{0} - static_cast<uint64_t>(n) : static_cast<uint64_t>(n);
    mpz_import(z, 1, -1, sizeof magnitude, 0, 0, &magnitude);
    if (n < 0) mpz_neg(z, z);
}

uint64_t bitsOf(mpz_srcptr z) { return mpz_sgn(z) == 0 ? 0 : mpz_sizeinbase(z, 2); }

void checkSize(uint64_t bits) {
    if (bits > kMaxIntegerBits) fail("Integer result too large.");
}

constexpr int64_t kExactInDouble = int64_t{1} << 53;

bool exactlyConvertible(int64_t n) { return n >= -kExactInDouble && n <= kExactInDouble; }

// The double nearest to num/den (den > 0), rounding half to even, with a
// single rounding even for subnormal results. Returns +-inf if the result
// is beyond the double range; callers decide what that means.
//
// Method: compute q = floor(|num| * 2^-r / den) with a resolution exponent
// r at least two bits finer than the result's last place, remember whether
// the division was exact ("sticky"), then round q to the result's real
// precision by hand.
double ratioToDouble(const BigInt& num, const BigInt& den) {
    int sign = mpz_sgn(num.get());
    if (sign == 0) return 0.0;

    BigInt a;
    mpz_abs(a.get(), num.get());
    BigInt b = den;
    auto k = static_cast<long>(bitsOf(a.get())) - static_cast<long>(bitsOf(b.get()));  // a/b in (2^(k-1), 2^(k+1))
    long r = std::max(k - 56, -1076L);
    if (r < 0) {
        mpz_mul_2exp(a.get(), a.get(), static_cast<mp_bitcnt_t>(-r));
    } else {
        mpz_mul_2exp(b.get(), b.get(), static_cast<mp_bitcnt_t>(r));
    }
    BigInt q, rem;
    mpz_tdiv_qr(q.get(), rem.get(), a.get(), b.get());
    bool sticky = mpz_sgn(rem.get()) != 0;
    if (mpz_sgn(q.get()) == 0) {
        // Below 2^r <= 2^-1076: less than half the smallest subnormal.
        return sign < 0 ? -0.0 : 0.0;
    }

    long topExponent = r + static_cast<long>(bitsOf(q.get())) - 1;  // floor(log2(|num/den|))
    long ulp = std::max(topExponent - 52, -1074L);
    auto drop = static_cast<mp_bitcnt_t>(ulp - r);  // always >= 2

    BigInt low, half;
    mpz_fdiv_r_2exp(low.get(), q.get(), drop);
    mpz_fdiv_q_2exp(q.get(), q.get(), drop);
    mpz_setbit(half.get(), drop - 1);
    int vsHalf = mpz_cmp(low.get(), half.get());
    if (vsHalf > 0 || (vsHalf == 0 && (sticky || mpz_odd_p(q.get())))) {
        mpz_add_ui(q.get(), q.get(), 1);
    }
    // q <= 2^53 now, so this conversion is exact; ldexp only scales.
    double result = std::ldexp(mpz_get_d(q.get()), static_cast<int>(ulp));
    return sign < 0 ? -result : result;
}

void requireNumbers(const Value& a, const Value& b) {
    if (!isNumber(a) || !isNumber(b)) fail("Operands must be numbers.");
}

double floatOperand(const Value& v) { return toDouble(v); }

// `%` for floats, as Python and every earlier Iqalox version do it: the
// result takes the divisor's sign (`-1 % 4 == 3`), unlike C++'s `fmod`.
double flooredModulo(double a, double b) {
    double result = std::fmod(a, b);
    if (result != 0.0 && ((result < 0.0) != (b < 0.0))) {
        result += b;
    }
    return result;
}

}  // namespace

BigInt::BigInt(int64_t n) {
    mpz_init(value_);
    setInt64(value_, n);
}

bool BigInt::fitsInt64() const {
    uint64_t bits = bitLength();
    if (bits < 64) return true;
    // -2^63 is the one 64-bit magnitude that fits.
    return bits == 64 && mpz_sgn(value_) < 0 && mpz_scan1(value_, 0) == 63;
}

int64_t BigInt::toInt64() const {
    uint64_t magnitude = 0;
    mpz_export(&magnitude, nullptr, -1, sizeof magnitude, 0, 0, value_);
    return mpz_sgn(value_) < 0 ? static_cast<int64_t>(uint64_t{0} - magnitude) : static_cast<int64_t>(magnitude);
}

std::string BigInt::toString() const {
    char* text = mpz_get_str(nullptr, 10, value_);
    std::string result(text);
    void (*freeFunction)(void*, size_t);
    mp_get_memory_functions(nullptr, nullptr, &freeFunction);
    freeFunction(text, result.size() + 1);
    return result;
}

bool isInteger(const Value& v) { return isSmallInt(v) || isBigInt(v); }
bool isFloat(const Value& v) { return isDouble(v); }
bool isNumber(const Value& v) { return isInteger(v) || isFloat(v); }

BigInt toBigInt(const Value& v) { return isSmallInt(v) ? BigInt(asSmallInt(v)) : bigOf(v); }

Value integerValue(Vm& vm, BigInt n) {
    checkSize(n.bitLength());
    if (n.fitsInt64()) return smallIntValue(n.toInt64());
    return objValue(vm.allocate<ObjBigInt>(std::move(n)));
}

double toDouble(const Value& v) {
    if (isDouble(v)) return asDouble(v);
    if (isSmallInt(v)) return static_cast<double>(asSmallInt(v));
    double result = ratioToDouble(bigOf(v), BigInt(1));
    if (std::isinf(result)) fail("Integer too large to convert to float.");
    return result;
}

Value add(Vm& vm, const Value& a, const Value& b) {
    if (isSmallInt(a) && isSmallInt(b)) {
        int64_t result;
        if (!addOverflows(asSmallInt(a), asSmallInt(b), result)) return smallIntValue(result);
    }
    if (isDouble(a) && isDouble(b)) return doubleValue(asDouble(a) + asDouble(b));
    requireNumbers(a, b);
    if (isInteger(a) && isInteger(b)) {
        BigInt result;
        mpz_add(result.get(), toBigInt(a).get(), toBigInt(b).get());
        return integerValue(vm, std::move(result));
    }
    return doubleValue(floatOperand(a) + floatOperand(b));
}

Value subtract(Vm& vm, const Value& a, const Value& b) {
    if (isSmallInt(a) && isSmallInt(b)) {
        int64_t result;
        if (!subtractOverflows(asSmallInt(a), asSmallInt(b), result)) return smallIntValue(result);
    }
    if (isDouble(a) && isDouble(b)) return doubleValue(asDouble(a) - asDouble(b));
    requireNumbers(a, b);
    if (isInteger(a) && isInteger(b)) {
        BigInt result;
        mpz_sub(result.get(), toBigInt(a).get(), toBigInt(b).get());
        return integerValue(vm, std::move(result));
    }
    return doubleValue(floatOperand(a) - floatOperand(b));
}

Value multiply(Vm& vm, const Value& a, const Value& b) {
    if (isSmallInt(a) && isSmallInt(b)) {
        int64_t result;
        if (!multiplyOverflows(asSmallInt(a), asSmallInt(b), result)) return smallIntValue(result);
    }
    if (isDouble(a) && isDouble(b)) return doubleValue(asDouble(a) * asDouble(b));
    requireNumbers(a, b);
    if (isInteger(a) && isInteger(b)) {
        BigInt x = toBigInt(a), y = toBigInt(b);
        // The product has at most this many bits; refuse before computing.
        if (mpz_sgn(x.get()) != 0 && mpz_sgn(y.get()) != 0) checkSize(x.bitLength() + y.bitLength() - 1);
        BigInt result;
        mpz_mul(result.get(), x.get(), y.get());
        return integerValue(vm, std::move(result));
    }
    return doubleValue(floatOperand(a) * floatOperand(b));
}

Value divide(Vm&, const Value& a, const Value& b) {
    requireNumbers(a, b);
    if (isInteger(a) && isInteger(b)) {
        // Exact, then rounded once (the Phase 1 follow-up to decision 3).
        if (isSmallInt(a) && isSmallInt(b) && exactlyConvertible(asSmallInt(a)) && exactlyConvertible(asSmallInt(b))) {
            if (asSmallInt(b) == 0) fail("Division by zero.");
            // Both convert exactly, and IEEE division rounds once.
            return doubleValue(static_cast<double>(asSmallInt(a)) / static_cast<double>(asSmallInt(b)));
        }
        BigInt num = toBigInt(a), den = toBigInt(b);
        if (mpz_sgn(den.get()) == 0) fail("Division by zero.");
        if (mpz_sgn(den.get()) < 0) {
            mpz_neg(num.get(), num.get());
            mpz_neg(den.get(), den.get());
        }
        double result = ratioToDouble(num, den);
        if (std::isinf(result)) fail("Result too large to represent as a float.");
        return doubleValue(result);
    }
    double divisor = floatOperand(b);
    double dividend = floatOperand(a);
    // docs/PLAN-0.4.md Phase 3 makes a float zero divisor follow IEEE; until
    // then every zero divisor is an error, as it always has been.
    if (divisor == 0.0) fail("Division by zero.");
    return doubleValue(dividend / divisor);
}

Value modulo(Vm& vm, const Value& a, const Value& b) {
    requireNumbers(a, b);
    if (isInteger(a) && isInteger(b)) {
        if (isSmallInt(a) && isSmallInt(b)) {
            int64_t x = asSmallInt(a), y = asSmallInt(b);
            if (y == 0) fail("Division by zero.");
            if (y == -1) return smallIntValue(0);  // INT64_MIN % -1 is undefined in C++
            int64_t result = x % y;
            if (result != 0 && ((result < 0) != (y < 0))) result += y;
            return smallIntValue(result);
        }
        BigInt x = toBigInt(a), y = toBigInt(b);
        if (mpz_sgn(y.get()) == 0) fail("Division by zero.");
        BigInt result;
        mpz_fdiv_r(result.get(), x.get(), y.get());
        return integerValue(vm, std::move(result));
    }
    return doubleValue(flooredModulo(floatOperand(a), floatOperand(b)));
}

Value power(Vm& vm, const Value& a, const Value& b) {
    requireNumbers(a, b);
    if (!(isInteger(a) && isInteger(b))) {
        return doubleValue(std::pow(floatOperand(a), floatOperand(b)));
    }

    // Decision 5: an exact int for a non-negative exponent, a float (computed
    // exactly, then rounded once) for a negative one.
    BigInt base = toBigInt(a), exponent = toBigInt(b);
    int baseSign = mpz_sgn(base.get());
    bool baseIsUnit = base.bitLength() == 1;  // +1 or -1
    bool exponentOdd = mpz_odd_p(exponent.get());

    if (mpz_sgn(exponent.get()) >= 0) {
        if (baseSign == 0) return smallIntValue(mpz_sgn(exponent.get()) == 0 ? 1 : 0);
        if (baseIsUnit) return smallIntValue(baseSign < 0 && exponentOdd ? -1 : 1);
        // |base| >= 2, so the result has at least `exponent` bits.
        if (exponent.bitLength() > 32) fail("Integer result too large.");
        auto e = static_cast<uint64_t>(exponent.toInt64());
        if ((base.bitLength() - 1) * e > kMaxIntegerBits) fail("Integer result too large.");
        BigInt result;
        mpz_pow_ui(result.get(), base.get(), static_cast<unsigned long>(e));
        return integerValue(vm, std::move(result));
    }

    // Negative exponent: base^-m == 1 / base^m.
    if (baseSign == 0) fail("Division by zero.");  // docs/PLAN-0.4.md §2 item 3; Phase 3 checks it at compile time
    bool negativeResult = baseSign < 0 && exponentOdd;
    if (baseIsUnit) return doubleValue(negativeResult ? -1.0 : 1.0);
    BigInt magnitude;
    mpz_neg(magnitude.get(), exponent.get());
    // |base|^m >= 2^((bits-1)*m); past 2^1100 the result rounds to zero, so
    // skip computing an enormous denominator.
    if (magnitude.bitLength() > 32 || (base.bitLength() - 1) * static_cast<uint64_t>(magnitude.toInt64()) > 1100) {
        return doubleValue(negativeResult ? -0.0 : 0.0);
    }
    BigInt denominator, absoluteBase;
    mpz_abs(absoluteBase.get(), base.get());
    mpz_pow_ui(denominator.get(), absoluteBase.get(), static_cast<unsigned long>(magnitude.toInt64()));
    return doubleValue(ratioToDouble(BigInt(negativeResult ? -1 : 1), denominator));
}

Value negate(Vm& vm, const Value& a) {
    if (isSmallInt(a) && asSmallInt(a) != std::numeric_limits<int64_t>::min()) return smallIntValue(-asSmallInt(a));
    if (isDouble(a)) return doubleValue(-asDouble(a));
    if (!isNumber(a)) fail("Operand must be a number.");
    BigInt result = toBigInt(a);
    mpz_neg(result.get(), result.get());
    return integerValue(vm, std::move(result));
}

namespace {

// Exact ordering of an integer against a finite or infinite double.
std::partial_ordering compareIntegerToDouble(const Value& integer, double d) {
    if (std::isnan(d)) return std::partial_ordering::unordered;
    if (std::isinf(d)) return d > 0 ? std::partial_ordering::less : std::partial_ordering::greater;
    if (isSmallInt(integer) && exactlyConvertible(asSmallInt(integer))) {
        return static_cast<double>(asSmallInt(integer)) <=> d;
    }
    // Compare against d's integer part exactly, then let the fraction break
    // a tie.
    BigInt whole;
    mpz_set_d(whole.get(), std::trunc(d));
    int c = mpz_cmp(toBigInt(integer).get(), whole.get());
    if (c != 0) return c < 0 ? std::partial_ordering::less : std::partial_ordering::greater;
    double fraction = d - std::trunc(d);
    if (fraction == 0.0) return std::partial_ordering::equivalent;
    return fraction > 0 ? std::partial_ordering::less : std::partial_ordering::greater;
}

}  // namespace

std::partial_ordering compare(const Value& a, const Value& b) {
    if (isSmallInt(a) && isSmallInt(b)) return asSmallInt(a) <=> asSmallInt(b);
    if (isDouble(a) && isDouble(b)) return asDouble(a) <=> asDouble(b);
    requireNumbers(a, b);
    if (isInteger(a) && isInteger(b)) {
        int c = mpz_cmp(toBigInt(a).get(), toBigInt(b).get());
        return c < 0 ? std::partial_ordering::less
                     : (c > 0 ? std::partial_ordering::greater : std::partial_ordering::equivalent);
    }
    if (isInteger(a)) return compareIntegerToDouble(a, asDouble(b));
    // a is the float: flip the integer-vs-float result.
    std::partial_ordering flipped = compareIntegerToDouble(b, asDouble(a));
    if (flipped == std::partial_ordering::less) return std::partial_ordering::greater;
    if (flipped == std::partial_ordering::greater) return std::partial_ordering::less;
    return flipped;
}

Index toIndexSlow(const Value& v) {
    if (isBigInt(v)) {
        return {true, mpz_sgn(bigOf(v).get()) < 0 ? std::numeric_limits<int64_t>::min()
                                                  : std::numeric_limits<int64_t>::max()};
    }
    return {false, 0};
}

std::string formatInteger(const Value& v) {
    return isSmallInt(v) ? std::to_string(asSmallInt(v)) : bigOf(v).toString();
}

// Python's `repr(float)` digits -- the shortest decimal that round-trips --
// in fixed notation for ordinary magnitudes and scientific outside them,
// switching at CPython's thresholds (exponent < -4 or >= 16), plus Iqalox's
// own rule that a float always shows a decimal point (docs/PLAN-0.4.md
// decision 8): `5.0`, and `1.0e+16` where Python prints `1e+16`.
std::string formatFloat(double v) {
    if (std::isnan(v)) return "nan";
    if (std::isinf(v)) return v < 0 ? "-inf" : "inf";

    char buf[64];
    auto result = std::to_chars(buf, buf + sizeof(buf), v, std::chars_format::scientific);
    std::string sci(buf, result.ptr);

    size_t ePos = sci.find('e');
    int exponent = std::stoi(sci.substr(ePos + 1));

    // `std::to_chars`' scientific form already matches Python's spelling
    // (lowercase `e`, sign, at least two exponent digits).
    if (exponent < -4 || exponent >= 16) {
        if (sci.find('.') == std::string::npos) sci.insert(ePos, ".0");
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
            out += digits;
            out.append(pointPos - digits.size(), '0');
            out += ".0";
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

}  // namespace iqalox::numeric
