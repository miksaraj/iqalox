// docs/PLAN-0.4.md Phase 4: the string model -- UTF-8 storage, code-point
// indexing and slicing, immutability, iteration and ordering.
#include <catch2/catch_test_macros.hpp>

#include <optional>
#include <string>
#include <vector>

#include "bytecode.hpp"
#include "chunk_builder.hpp"
#include "utf8.hpp"
#include "vm.hpp"

using namespace iqalox;
using bytecode::OpCode;
using iqalox::testing::ChunkBuilder;

namespace {

std::string asText(const Value& v) {
    REQUIRE(isObj(v));
    REQUIRE(asObj(v)->type == ObjType::String);
    return static_cast<ObjString*>(asObj(v))->value;
}

// Runs `receiver <op> operand` (or `receiver[operand]` and friends) and
// returns the result, stored through a global.
// The result is a one-character or ASCII string, or a number, so it
// outlives its Vm only as a copy: callers read it through `asText` before
// the next call. A static Vm keeps it alive until then.
Value binary(OpCode op, const std::string& receiver, Value operand) {
    static std::optional<Vm> vm;
    vm.emplace();
    ChunkBuilder b(*vm);
    uint16_t r = b.addStringConstant(receiver);
    uint16_t o = b.addValueConstant(operand);
    uint16_t result = b.addStringConstant("result");
    b.emitU16(OpCode::Constant, r);
    b.emitU16(OpCode::Constant, o);
    b.emit(op);
    b.emitU16(OpCode::DefineGlobal, result);
    vm->interpret(b.build());
    return *vm->getGlobal("result");
}

Value charAt(const std::string& s, int64_t i) { return binary(OpCode::GetIndex, s, smallIntValue(i)); }

// `s[start:stop]`; an omitted bound is nil.
std::string slice(const std::string& s, std::optional<int64_t> start, std::optional<int64_t> stop) {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t r = b.addStringConstant(s);
    uint16_t result = b.addStringConstant("result");
    b.emitU16(OpCode::Constant, r);
    if (start) b.emitU16(OpCode::Constant, b.addIntConstant(*start)); else b.emit(OpCode::Nil);
    if (stop) b.emitU16(OpCode::Constant, b.addIntConstant(*stop)); else b.emit(OpCode::Nil);
    b.emit(OpCode::GetSlice);
    b.emitU16(OpCode::DefineGlobal, result);
    vm.interpret(b.build());
    return asText(*vm.getGlobal("result"));
}

bool ordered(OpCode op, const std::string& a, const std::string& bText) {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t left = b.addStringConstant(a);
    uint16_t right = b.addStringConstant(bText);
    uint16_t result = b.addStringConstant("result");
    b.emitU16(OpCode::Constant, left);
    b.emitU16(OpCode::Constant, right);
    b.emit(op);
    b.emitU16(OpCode::DefineGlobal, result);
    vm.interpret(b.build());
    return asBool(*vm.getGlobal("result"));
}

}  // namespace

TEST_CASE("utf8::isValid accepts well-formed text and rejects every malformed kind", "[strings]") {
    REQUIRE(utf8::isValid(""));
    REQUIRE(utf8::isValid("plain ascii"));
    REQUIRE(utf8::isValid("h\xC3\xA9llo \xF0\x9F\x98\x80"));     // héllo 😀
    REQUIRE(utf8::isValid("\xF4\x8F\xBF\xBF"));                 // U+10FFFF
    REQUIRE_FALSE(utf8::isValid("\xE4"));                       // Latin-1 ä
    REQUIRE_FALSE(utf8::isValid("\xC3"));                       // truncated
    REQUIRE_FALSE(utf8::isValid("\x80"));                       // stray continuation
    REQUIRE_FALSE(utf8::isValid("\xC0\xAF"));                   // overlong '/'
    REQUIRE_FALSE(utf8::isValid("\xED\xA0\x80"));               // surrogate U+D800
    REQUIRE_FALSE(utf8::isValid("\xF4\x90\x80\x80"));           // above U+10FFFF
    REQUIRE_FALSE(utf8::isValid("\xF8\x88\x80\x80\x80"));       // 5-byte form
}

TEST_CASE("a string's length counts code points, not bytes", "[strings]") {
    Vm vm;
    REQUIRE(ObjString("").length == 0);
    REQUIRE(ObjString("abc").length == 3);
    REQUIRE(ObjString("abc").isAscii());
    ObjString mixed("h\xC3\xA9llo \xF0\x9F\x98\x80");
    REQUIRE(mixed.length == 7);
    REQUIRE_FALSE(mixed.isAscii());
    REQUIRE(mixed.codePoints(1, 1) == "\xC3\xA9");
    REQUIRE(mixed.codePoints(6, 6) == "\xF0\x9F\x98\x80");
    REQUIRE(mixed.codePoints(0, 6) == mixed.value);
}

TEST_CASE("indexing a string gives a one-character string by code point", "[strings]") {
    REQUIRE(asText(charAt("abc", 0)) == "a");
    REQUIRE(asText(charAt("abc", -1)) == "c");
    REQUIRE(asText(charAt("h\xC3\xA9llo", 1)) == "\xC3\xA9");
    REQUIRE(asText(charAt("h\xC3\xA9llo", 2)) == "l");
    REQUIRE(asText(charAt("a\xF0\x9F\x98\x80", -1)) == "\xF0\x9F\x98\x80");
}

TEST_CASE("indexing out of range or with a non-integer is a runtime error", "[strings]") {
    REQUIRE_THROWS_AS(charAt("abc", 3), RuntimeError);
    REQUIRE_THROWS_AS(charAt("abc", -4), RuntimeError);
    REQUIRE_THROWS_AS(charAt("", 0), RuntimeError);
    REQUIRE_THROWS_AS(binary(OpCode::GetIndex, "abc", doubleValue(1.0)), RuntimeError);
}

TEST_CASE("assigning into a string is a runtime error", "[strings]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t s = b.addStringConstant("abc");
    uint16_t zero = b.addIntConstant(0);
    uint16_t x = b.addStringConstant("x");
    b.emitU16(OpCode::Constant, s);
    b.emitU16(OpCode::Constant, zero);
    b.emitU16(OpCode::Constant, x);
    b.emit(OpCode::SetIndex);
    try {
        vm.interpret(b.build());
        FAIL("expected a runtime error");
    } catch (const RuntimeError& e) {
        REQUIRE(std::string(e.what()).find("Strings are immutable") != std::string::npos);
    }
}

TEST_CASE("slicing a string follows the vector slice rules, by code point", "[strings]") {
    const std::string s = "h\xC3\xA9llo";  // héllo
    REQUIRE(slice(s, 1, 3) == "\xC3\xA9ll");
    REQUIRE(slice(s, std::nullopt, 1) == "h\xC3\xA9");
    REQUIRE(slice(s, -2, std::nullopt) == "lo");
    REQUIRE(slice(s, std::nullopt, std::nullopt) == s);
    REQUIRE(slice(s, 3, 1) == "");
    REQUIRE(slice(s, -100, 100) == s);
    REQUIRE(slice("", std::nullopt, std::nullopt) == "");
}

TEST_CASE("strings order lexicographically by code point", "[strings]") {
    REQUIRE(ordered(OpCode::Less, "Z", "a"));
    REQUIRE(ordered(OpCode::Less, "ab", "abc"));
    REQUIRE(ordered(OpCode::Less, "", "a"));
    REQUIRE(ordered(OpCode::LessEqual, "b", "b"));
    REQUIRE_FALSE(ordered(OpCode::Greater, "b", "b"));
    REQUIRE(ordered(OpCode::Greater, "\xC3\xA9", "z"));                 // é > z
    // U+1F600 sorts after U+FFFF by code point (UTF-16 code-unit order
    // would put it before).
    REQUIRE(ordered(OpCode::Greater, "\xF0\x9F\x98\x80", "\xEF\xBF\xBF"));
    REQUIRE(ordered(OpCode::GreaterEqual, "b", "a"));
}

TEST_CASE("ordering a string against a number is a runtime error", "[strings]") {
    REQUIRE_THROWS_AS(binary(OpCode::Less, "a", smallIntValue(1)), RuntimeError);
}

TEST_CASE("IterableLength counts a string's code points; VectorLength refuses a string", "[strings]") {
    REQUIRE(asSmallInt([] {
        Vm vm;
        ChunkBuilder b(vm);
        uint16_t s = b.addStringConstant("a\xF0\x9F\x98\x80" "b");
        uint16_t result = b.addStringConstant("result");
        b.emitU16(OpCode::Constant, s);
        b.emit(OpCode::IterableLength);
        b.emitU16(OpCode::DefineGlobal, result);
        vm.interpret(b.build());
        return *vm.getGlobal("result");
    }()) == 3);

    Vm vm;
    ChunkBuilder b(vm);
    uint16_t s = b.addStringConstant("ab");
    b.emitU16(OpCode::Constant, s);
    b.emit(OpCode::VectorLength);
    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("spreading a string splits it into one-character strings", "[strings]") {
    Vm vm;
    vm.stressGc = true;  // the spread allocates while holding the target
    ChunkBuilder b(vm);
    uint16_t s = b.addStringConstant("x\xC3\xA9y");
    uint16_t result = b.addStringConstant("result");
    b.emitU16(OpCode::BuildVector, 0);
    b.emitU16(OpCode::Constant, s);
    b.emit(OpCode::VectorExtend);
    b.emitU16(OpCode::DefineGlobal, result);
    vm.interpret(b.build());
    vm.stressGc = false;
    auto* v = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(v->elements.size() == 3);
    REQUIRE(asText(v->elements[0]) == "x");
    REQUIRE(asText(v->elements[1]) == "\xC3\xA9");
    REQUIRE(asText(v->elements[2]) == "y");
}
