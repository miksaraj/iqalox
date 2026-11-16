#include <catch2/catch_test_macros.hpp>

#include <cmath>
#include <cstdint>

#include "numeric.hpp"
#include "object.hpp"
#include "value.hpp"
#include "vm.hpp"

using namespace iqalox;

TEST_CASE("stringify: nil, undef, and booleans", "[value]") {
    REQUIRE(stringify(NilValue) == "nil");
    REQUIRE(stringify(UndefValue) == "undef");
    REQUIRE(stringify(boolValue(true)) == "true");
    REQUIRE(stringify(boolValue(false)) == "false");
}

TEST_CASE("stringify: a float always shows a decimal point (docs/PLAN-0.4.md decision 8)", "[value]") {
    REQUIRE(stringify(doubleValue(1.0)) == "1.0");
    REQUIRE(stringify(doubleValue(0.0)) == "0.0");
    REQUIRE(stringify(doubleValue(-0.0)) == "-0.0");
    REQUIRE(stringify(doubleValue(-5.5)) == "-5.5");
    REQUIRE(stringify(doubleValue(3.14)) == "3.14");
    REQUIRE(stringify(doubleValue(0.1 + 0.2)) == "0.30000000000000004");
}

TEST_CASE("stringify: an integer prints exactly, with no decimal point", "[value]") {
    Vm vm;
    REQUIRE(stringify(smallIntValue(1)) == "1");
    REQUIRE(stringify(smallIntValue(-42)) == "-42");
    REQUIRE(stringify(smallIntValue(INT64_MIN)) == "-9223372036854775808");
    numeric::BigInt big;
    mpz_ui_pow_ui(big.get(), 2, 100);
    REQUIRE(stringify(numeric::integerValue(vm, big)) == "1267650600228229401496703205376");
}

TEST_CASE("stringify: inf and nan print as words", "[value]") {
    REQUIRE(stringify(doubleValue(INFINITY)) == "inf");
    REQUIRE(stringify(doubleValue(-INFINITY)) == "-inf");
    REQUIRE(stringify(doubleValue(NAN)) == "nan");
}

TEST_CASE("stringify: fixed vs. scientific notation follows Python's threshold, not std::to_chars' own",
          "[value]") {
    // std::to_chars' own "general" format switches to scientific whenever
    // it's *shorter* (e.g. rendering 1e8 as "1e+08"), which disagrees with
    // Python/poc for plenty of ordinary values.
    REQUIRE(stringify(doubleValue(100000000.0)) == "100000000.0");
    REQUIRE(stringify(doubleValue(1e15)) == "1000000000000000.0");
    REQUIRE(stringify(doubleValue(0.0001)) == "0.0001");

    // Python's actual switch points: exponent < -4 or >= 16. Iqalox adds
    // ".0" to a whole mantissa so the decimal point is never lost.
    REQUIRE(stringify(doubleValue(1e16)) == "1.0e+16");
    REQUIRE(stringify(doubleValue(0.00001)) == "1.0e-05");
    REQUIRE(stringify(doubleValue(1.5e300)) == "1.5e+300");
    REQUIRE(stringify(doubleValue(1e-10)) == "1.0e-10");
    REQUIRE(stringify(doubleValue(-2.5e-7)) == "-2.5e-07");
}

TEST_CASE("stringify: a string prints unquoted", "[value]") {
    Vm vm;
    auto* s = vm.allocate<ObjString>("hello");
    REQUIRE(stringify(objValue(s)) == "hello");
}

TEST_CASE("stringify: a vector's elements print exactly as they do on their own", "[value]") {
    Vm vm;
    auto* vec = vm.allocate<ObjVector>();
    vec->elements = {smallIntValue(1), doubleValue(2.0), doubleValue(2.5), objValue(vm.allocate<ObjString>("a"))};

    // Same rules as a bare top-level value: ints plain, floats with a
    // decimal point, no quotes on strings.
    REQUIRE(stringify(objValue(vec)) == "[1, 2.0, 2.5, a]");
}

TEST_CASE("stringify: nested vectors recurse", "[value]") {
    Vm vm;
    auto* inner1 = vm.allocate<ObjVector>();
    inner1->elements = {smallIntValue(1), smallIntValue(2)};
    auto* inner2 = vm.allocate<ObjVector>();
    inner2->elements = {smallIntValue(3), smallIntValue(4)};
    auto* outer = vm.allocate<ObjVector>();
    outer->elements = {objValue(inner1), objValue(inner2)};

    REQUIRE(stringify(objValue(outer)) == "[[1, 2], [3, 4]]");
}

TEST_CASE("typeName reports a sensible name for each kind of value", "[value]") {
    Vm vm;
    REQUIRE(typeName(NilValue) == "nil");
    REQUIRE(typeName(UndefValue) == "undef");
    REQUIRE(typeName(boolValue(true)) == "bool");
    REQUIRE(typeName(smallIntValue(1)) == "int");
    REQUIRE(typeName(doubleValue(1.0)) == "float");
    REQUIRE(typeName(objValue(vm.allocate<ObjString>("s"))) == "string");
    REQUIRE(typeName(objValue(vm.allocate<ObjVector>())) == "vector");
}
