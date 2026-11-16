// docs/PLAN-0.4.md Phase 1: the numeric model -- exact integers (inline
// int64, heap ObjBigInt beyond it) next to IEEE doubles. Tests call
// numeric:: directly; test_vm.cpp covers the opcodes that dispatch to it.

#include <catch2/catch_test_macros.hpp>

#include <cmath>
#include <compare>
#include <cstdint>
#include <limits>
#include <string>

#include "chunk_builder.hpp"
#include "numeric.hpp"
#include "object.hpp"
#include "vm.hpp"

using namespace iqalox;
using iqalox::bytecode::OpCode;
using iqalox::testing::ChunkBuilder;

namespace {

// 2^n as an exact integer Value.
Value powerOfTwo(Vm& vm, unsigned long n) {
    numeric::BigInt big;
    mpz_setbit(big.get(), n);  // instant even for huge n, unlike mpz_ui_pow_ui
    return numeric::integerValue(vm, big);
}

Value big(Vm& vm, const char* decimal) {
    numeric::BigInt n;
    mpz_set_str(n.get(), decimal, 10);
    return numeric::integerValue(vm, n);
}

Value i(int64_t n) { return smallIntValue(n); }
Value f(double d) { return doubleValue(d); }

std::string show(const Value& v) { return stringify(v); }

bool sameDouble(const Value& v, double expected) {
    if (!isDouble(v)) return false;
    double actual = asDouble(v);
    // Exact equality, but also tell 0.0 and -0.0 apart.
    return actual == expected && std::signbit(actual) == std::signbit(expected);
}

constexpr int64_t kMax = std::numeric_limits<int64_t>::max();
constexpr int64_t kMin = std::numeric_limits<int64_t>::min();

}  // namespace

TEST_CASE("int64 overflow becomes a big integer, and a result that fits comes back inline", "[numeric]") {
    Vm vm;
    Value overflowed = numeric::add(vm, i(kMax), i(1));
    REQUIRE_FALSE(isSmallInt(overflowed));
    REQUIRE(show(overflowed) == "9223372036854775808");
    REQUIRE(typeName(overflowed) == "int");

    Value back = numeric::subtract(vm, overflowed, i(1));
    REQUIRE(isSmallInt(back));
    REQUIRE(asSmallInt(back) == kMax);

    REQUIRE(show(numeric::multiply(vm, i(kMax), i(kMax))) == "85070591730234615847396907784232501249");
    REQUIRE(show(numeric::subtract(vm, i(kMin), i(1))) == "-9223372036854775809");

    // Negating INT64_MIN needs a big integer; negating that comes back.
    Value negatedMin = numeric::negate(vm, i(kMin));
    REQUIRE(show(negatedMin) == "9223372036854775808");
    Value renegated = numeric::negate(vm, negatedMin);
    REQUIRE(isSmallInt(renegated));
    REQUIRE(asSmallInt(renegated) == kMin);
}

TEST_CASE("integer arithmetic is exact at any size", "[numeric]") {
    Vm vm;
    Value a = powerOfTwo(vm, 200);
    REQUIRE(show(numeric::multiply(vm, a, a)) == show(powerOfTwo(vm, 400)));
    REQUIRE(show(numeric::add(vm, big(vm, "99999999999999999999"), i(1))) == "100000000000000000000");
}

TEST_CASE("% is floored for ints of any size and for floats", "[numeric]") {
    Vm vm;
    REQUIRE(asSmallInt(numeric::modulo(vm, i(-7), i(2))) == 1);
    REQUIRE(asSmallInt(numeric::modulo(vm, i(7), i(-2))) == -1);
    REQUIRE(asSmallInt(numeric::modulo(vm, i(kMin), i(-1))) == 0);
    REQUIRE(show(numeric::modulo(vm, numeric::negate(vm, powerOfTwo(vm, 100)), i(3))) == "2");
    REQUIRE(sameDouble(numeric::modulo(vm, f(7.5), i(2)), 1.5));
    REQUIRE(sameDouble(numeric::modulo(vm, f(-1.0), f(4.0)), 3.0));
    REQUIRE_THROWS_AS(numeric::modulo(vm, i(1), i(0)), RuntimeError);
    REQUIRE_THROWS_AS(numeric::modulo(vm, powerOfTwo(vm, 100), i(0)), RuntimeError);
}

TEST_CASE("/ always gives a float, computed exactly and rounded once", "[numeric]") {
    Vm vm;
    REQUIRE(sameDouble(numeric::divide(vm, i(7), i(2)), 3.5));
    REQUIRE(sameDouble(numeric::divide(vm, i(6), i(2)), 3.0));
    REQUIRE(sameDouble(numeric::divide(vm, i(1), i(3)), 1.0 / 3.0));
    REQUIRE(sameDouble(numeric::divide(vm, i(-1), i(3)), -1.0 / 3.0));
    // Operands too big for a double, result not.
    REQUIRE(sameDouble(numeric::divide(vm, powerOfTwo(vm, 2000), powerOfTwo(vm, 1999)), 2.0));
    REQUIRE(sameDouble(numeric::divide(vm, powerOfTwo(vm, 2000), numeric::negate(vm, powerOfTwo(vm, 1998))), -4.0));
    // Correct rounding where converting the operands first would be off:
    // (2^60 + 1) / 3 rounds once, not after (2^60 + 1) became 2^60.
    Value numerator = numeric::add(vm, powerOfTwo(vm, 60), i(1));
    REQUIRE(sameDouble(numeric::divide(vm, numerator, i(3)), 384307168202282325.6666666666666667));
    // Ties go to even: 2^54 + 2 has its last bit exactly halfway.
    REQUIRE(sameDouble(numeric::divide(vm, numeric::add(vm, powerOfTwo(vm, 54), i(2)), i(1)), 18014398509481984.0));
    REQUIRE(sameDouble(numeric::divide(vm, numeric::add(vm, powerOfTwo(vm, 54), i(6)), i(1)), 18014398509481992.0));
    // Subnormal results round once, correctly.
    REQUIRE(sameDouble(numeric::divide(vm, i(1), powerOfTwo(vm, 1074)), std::ldexp(1.0, -1074)));
    REQUIRE(sameDouble(numeric::divide(vm, i(1), powerOfTwo(vm, 1075)), 0.0));  // a tie, rounds to even 0
    REQUIRE(sameDouble(numeric::divide(vm, i(3), powerOfTwo(vm, 1076)), std::ldexp(1.0, -1074)));
    REQUIRE(sameDouble(numeric::divide(vm, i(-1), powerOfTwo(vm, 1200)), -0.0));
    // Mixed int/float promotes.
    REQUIRE(sameDouble(numeric::divide(vm, i(1), f(4.0)), 0.25));

    REQUIRE_THROWS_AS(numeric::divide(vm, powerOfTwo(vm, 1100), i(1)), RuntimeError);  // result too large
    REQUIRE_THROWS_AS(numeric::divide(vm, i(1), i(0)), RuntimeError);
    REQUIRE_THROWS_AS(numeric::divide(vm, f(1.0), f(0.0)), RuntimeError);  // until Phase 3
}

TEST_CASE("^ gives an exact int for a non-negative exponent, a rounded float otherwise", "[numeric]") {
    Vm vm;
    REQUIRE(show(numeric::power(vm, i(2), i(100))) == "1267650600228229401496703205376");
    REQUIRE(asSmallInt(numeric::power(vm, i(0), i(0))) == 1);
    REQUIRE(asSmallInt(numeric::power(vm, i(0), i(5))) == 0);
    REQUIRE(asSmallInt(numeric::power(vm, i(-1), i(7))) == -1);
    REQUIRE(asSmallInt(numeric::power(vm, i(-1), powerOfTwo(vm, 100))) == 1);
    REQUIRE(asSmallInt(numeric::power(vm, i(1), powerOfTwo(vm, 100))) == 1);
    REQUIRE(show(numeric::power(vm, i(-3), i(3))) == "-27");

    REQUIRE(sameDouble(numeric::power(vm, i(2), i(-1)), 0.5));
    REQUIRE(sameDouble(numeric::power(vm, i(-2), i(-3)), -0.125));
    REQUIRE(sameDouble(numeric::power(vm, i(10), i(-2)), 0.01));
    REQUIRE(sameDouble(numeric::power(vm, i(-1), i(-3)), -1.0));
    // Beyond the double range: exact, so zero, never "too large".
    REQUIRE(sameDouble(numeric::power(vm, powerOfTwo(vm, 2000), i(-1)), 0.0));
    REQUIRE(sameDouble(numeric::power(vm, i(-2), i(-2001)), -0.0));
    REQUIRE(sameDouble(numeric::power(vm, i(10), numeric::negate(vm, powerOfTwo(vm, 80))), 0.0));

    // Any float operand: plain pow.
    REQUIRE(sameDouble(numeric::power(vm, f(2.0), i(10)), 1024.0));
    REQUIRE(sameDouble(numeric::power(vm, i(4), f(0.5)), 2.0));

    // docs/PLAN-0.4.md §2 item 3: integer 0 to a negative power.
    REQUIRE_THROWS_AS(numeric::power(vm, i(0), i(-1)), RuntimeError);
    // The size limit, checked before computing anything.
    REQUIRE_THROWS_AS(numeric::power(vm, i(3), i(10000000000)), RuntimeError);
    REQUIRE_THROWS_AS(numeric::power(vm, i(2), powerOfTwo(vm, 100)), RuntimeError);
}

TEST_CASE("the integer size limit is a runtime error, not an abort", "[numeric]") {
    Vm vm;
    Value huge = powerOfTwo(vm, (1u << 25) + 1);  // just over half the limit
    REQUIRE_THROWS_AS(numeric::multiply(vm, huge, huge), RuntimeError);
}

TEST_CASE("mixed int/float arithmetic promotes; an int beyond the float range is an error", "[numeric]") {
    Vm vm;
    REQUIRE(sameDouble(numeric::add(vm, i(1), f(2.5)), 3.5));
    REQUIRE(sameDouble(numeric::multiply(vm, f(0.5), i(4)), 2.0));
    REQUIRE(sameDouble(numeric::add(vm, powerOfTwo(vm, 100), f(0.0)), std::ldexp(1.0, 100)));
    REQUIRE_THROWS_AS(numeric::add(vm, powerOfTwo(vm, 2000), f(0.5)), RuntimeError);
    // Converting a big integer rounds to nearest, ties to even.
    REQUIRE(numeric::toDouble(numeric::add(vm, powerOfTwo(vm, 64), i(1))) == std::ldexp(1.0, 64));
    REQUIRE(numeric::toDouble(numeric::add(vm, powerOfTwo(vm, 64), i(6144))) == std::ldexp(1.0, 64) + 8192.0);
}

TEST_CASE("ordering is exact across ints and floats; nan is unordered", "[numeric]") {
    Vm vm;
    using std::partial_ordering;
    REQUIRE(numeric::compare(i(1), f(1.0)) == partial_ordering::equivalent);
    REQUIRE(numeric::compare(i(3), f(3.5)) == partial_ordering::less);
    // 2^53 + 1 vs the double 2^53: promotion would call them equal.
    Value justAbove = numeric::add(vm, i(int64_t{1} << 53), i(1));
    REQUIRE(numeric::compare(justAbove, f(9007199254740992.0)) == partial_ordering::greater);
    REQUIRE(numeric::compare(f(9007199254740992.0), justAbove) == partial_ordering::less);
    // An int beyond the float range compares fine, no error.
    REQUIRE(numeric::compare(powerOfTwo(vm, 2000), f(1.5)) == partial_ordering::greater);
    REQUIRE(numeric::compare(numeric::negate(vm, powerOfTwo(vm, 2000)), f(-1e300)) == partial_ordering::less);
    REQUIRE(numeric::compare(powerOfTwo(vm, 2000), f(INFINITY)) == partial_ordering::less);
    // Fractions break ties against the integer part, on both signs.
    REQUIRE(numeric::compare(big(vm, "-3"), f(-2.5)) == partial_ordering::less);
    REQUIRE(numeric::compare(big(vm, "-2"), f(-2.5)) == partial_ordering::greater);
    REQUIRE(numeric::compare(numeric::add(vm, powerOfTwo(vm, 60), i(0)), f(std::ldexp(1.0, 60) + 512.0)) ==
            partial_ordering::less);
    REQUIRE(numeric::compare(i(1), f(NAN)) == partial_ordering::unordered);
    REQUIRE(numeric::compare(f(NAN), f(NAN)) == partial_ordering::unordered);
    REQUIRE(numeric::compare(powerOfTwo(vm, 100), powerOfTwo(vm, 99)) == partial_ordering::greater);
    REQUIRE_THROWS_AS(numeric::compare(i(1), boolValue(true)), RuntimeError);
}

TEST_CASE("== compares type and value: an int never equals a float", "[numeric][value]") {
    Vm vm;
    REQUIRE_FALSE(valuesEqual(i(1), f(1.0)));
    REQUIRE(valuesEqual(i(1), i(1)));
    REQUIRE(valuesEqual(f(0.0), f(-0.0)));
    REQUIRE_FALSE(valuesEqual(f(NAN), f(NAN)));
    REQUIRE(valuesEqual(powerOfTwo(vm, 100), numeric::multiply(vm, powerOfTwo(vm, 50), powerOfTwo(vm, 50))));
    REQUIRE_FALSE(valuesEqual(powerOfTwo(vm, 100), powerOfTwo(vm, 101)));
}

TEST_CASE("only an integer indexes a vector or bounds a slice", "[numeric][vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addIntConstant(1);
    uint16_t wholeFloat = b.addFloatConstant(0.0);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Constant, wholeFloat);  // v[0.0]: refused even though whole
    b.emit(OpCode::GetIndex);
    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);

    Vm vm2;
    ChunkBuilder s(vm2);
    uint16_t sOne = s.addIntConstant(1);
    uint16_t sFloat = s.addFloatConstant(1.0);
    s.emitU16(OpCode::Constant, sOne);
    s.emitU16(OpCode::BuildVector, 1);
    s.emitU16(OpCode::Constant, sFloat);
    s.emit(OpCode::Nil);
    s.emit(OpCode::GetSlice);  // v[1.0:]
    REQUIRE_THROWS_AS(vm2.interpret(s.build()), RuntimeError);

    // A big integer index is simply out of range.
    Vm vm3;
    ChunkBuilder g(vm3);
    uint16_t gOne = g.addIntConstant(1);
    uint16_t hugeIndex = g.addValueConstant(powerOfTwo(vm3, 100));
    g.emitU16(OpCode::Constant, gOne);
    g.emitU16(OpCode::BuildVector, 1);
    g.emitU16(OpCode::Constant, hugeIndex);
    g.emit(OpCode::GetIndex);
    REQUIRE_THROWS_AS(vm3.interpret(g.build()), RuntimeError);
}

TEST_CASE("big integer arithmetic and matrix natives survive stress-mode GC", "[numeric][gc]") {
    // Every allocation collects. Big integer results are heap objects, so a
    // missing root (an operand, a running sum, a half-built matrix) frees
    // something still in use and this crashes or computes garbage.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t multiplyName = b.addStringConstant("multiply");
    uint16_t big1 = b.addValueConstant(powerOfTwo(vm, 100));
    uint16_t big2 = b.addValueConstant(powerOfTwo(vm, 70));
    uint16_t three = b.addIntConstant(3);
    uint16_t result = b.addStringConstant("result");
    uint16_t product = b.addStringConstant("product");

    // product = (2^100 * 2^70) * 3
    b.emitU16(OpCode::Constant, big1);
    b.emitU16(OpCode::Constant, big2);
    b.emit(OpCode::Multiply);
    b.emitU16(OpCode::Constant, three);
    b.emit(OpCode::Multiply);
    b.emitU16(OpCode::DefineGlobal, product);

    // result = multiply [[2^100, 2^70], [3, 2^100]], [[2^70, 3], [2^100, 2^70]]
    b.emitU16(OpCode::GetGlobal, multiplyName);
    b.emitU16(OpCode::Constant, big1);
    b.emitU16(OpCode::Constant, big2);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::Constant, big1);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Constant, big2);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Constant, big1);
    b.emitU16(OpCode::Constant, big2);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.stressGc = true;
    vm.interpret(b.build());
    // The expectations below are built from unrooted temporaries; they're
    // test scaffolding, not what this test is about, so stop collecting.
    vm.stressGc = false;

    REQUIRE(valuesEqual(*vm.getGlobal("product"), numeric::multiply(vm, powerOfTwo(vm, 170), i(3))));
    // [[2^170 + 2^170, 3*2^100 + 2^170], [3*2^70 + 2^200, 9 + 2^170]]
    Value expected00 = numeric::add(vm, powerOfTwo(vm, 170), powerOfTwo(vm, 170));
    Value expected11 = numeric::add(vm, i(9), powerOfTwo(vm, 170));
    const auto& rows = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")))->elements;
    const auto& row0 = static_cast<ObjVector*>(asObj(rows[0]))->elements;
    const auto& row1 = static_cast<ObjVector*>(asObj(rows[1]))->elements;
    REQUIRE(valuesEqual(row0[0], expected00));
    REQUIRE(valuesEqual(row1[1], expected11));
}
