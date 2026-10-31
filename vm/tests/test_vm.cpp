#include <catch2/catch_test_macros.hpp>

#include <iostream>
#include <sstream>
#include <string>
#include <vector>

#include "bytecode.hpp"
#include "chunk_builder.hpp"
#include "vm.hpp"

using namespace iqalox;
using bytecode::OpCode;
using iqalox::testing::ChunkBuilder;

TEST_CASE("arithmetic evaluates and a global holds the result", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    // result = 1 + 2 * 3
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emit(OpCode::Multiply);
    b.emit(OpCode::Add);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(vm.getGlobal("result") != nullptr);
    REQUIRE(asNumber(*vm.getGlobal("result")) == 7.0);
}

TEST_CASE("modulo and power match Python's floored-modulo semantics, not C's fmod", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t negOne = b.addNumberConstant(-1);
    uint16_t four = b.addNumberConstant(4);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, negOne);
    b.emitU16(OpCode::Constant, four);
    b.emit(OpCode::Modulo);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 3.0);  // Python: -1 % 4 == 3, not fmod's -1
}

TEST_CASE("comparisons and equality produce booleans", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emit(OpCode::Less);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(isBool(*vm.getGlobal("result")));
    REQUIRE(asBool(*vm.getGlobal("result")) == true);
}

TEST_CASE("only nil and false are falsy -- 0 is truthy", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t zero = b.addNumberConstant(0);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, zero);
    b.emit(OpCode::Not);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asBool(*vm.getGlobal("result")) == false);  // !0 is false: 0 is truthy
}

TEST_CASE("strings compare equal by content, not identity", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t a = b.addStringConstant("hi");
    uint16_t bConst = b.addStringConstant("hi");  // a second, distinct ObjString with the same content
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, a);
    b.emitU16(OpCode::Constant, bConst);
    b.emit(OpCode::Equal);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asBool(*vm.getGlobal("result")) == true);
}

TEST_CASE("vectors compare equal element-wise and recursively", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);  // [1, 2]
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 2);  // [1, 3]
    b.emit(OpCode::NotEqual);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asBool(*vm.getGlobal("result")) == true);  // [1, 2] != [1, 3]
}

TEST_CASE("indexed get/set read and write vector elements", "[vm]") {
    // docs/PLAN-0.2.md Phase 1: v[i] read/write, 0-based, bounds-checked.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t ten = b.addNumberConstant(10);
    uint16_t twenty = b.addNumberConstant(20);
    uint16_t ninetyNine = b.addNumberConstant(99);
    uint16_t one = b.addNumberConstant(1);
    uint16_t vName = b.addStringConstant("v");
    uint16_t resultName = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, ten);
    b.emitU16(OpCode::Constant, twenty);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::DefineGlobal, vName);  // v = [10, 20]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, ninetyNine);
    b.emit(OpCode::SetIndex);  // v[1] = 99 -- leaves 99 on the stack (assignment is an expression)
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::GetIndex);
    b.emitU16(OpCode::DefineGlobal, resultName);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 99.0);
}

TEST_CASE("indexing out of range is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t five = b.addNumberConstant(5);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);  // [1] -- valid indices are just 0
    b.emitU16(OpCode::Constant, five);
    b.emit(OpCode::GetIndex);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a valid negative index reads from the end of the vector", "[vm]") {
    // docs/PLAN-0.3.md decision 2: v[-1] is the last element, translated
    // to length + index -- no longer an error, unlike 0.2.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t ten = b.addNumberConstant(10);
    uint16_t twenty = b.addNumberConstant(20);
    uint16_t thirty = b.addNumberConstant(30);
    uint16_t negOne = b.addNumberConstant(-1);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, ten);
    b.emitU16(OpCode::Constant, twenty);
    b.emitU16(OpCode::Constant, thirty);
    b.emitU16(OpCode::BuildVector, 3);
    b.emitU16(OpCode::Constant, negOne);
    b.emit(OpCode::GetIndex);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 30.0);
}

TEST_CASE("a valid negative index also writes to the end of the vector", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t ten = b.addNumberConstant(10);
    uint16_t twenty = b.addNumberConstant(20);
    uint16_t ninetyNine = b.addNumberConstant(99);
    uint16_t negOne = b.addNumberConstant(-1);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, ten);
    b.emitU16(OpCode::Constant, twenty);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::DefineGlobal, vName);  // v = [10, 20]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, negOne);
    b.emitU16(OpCode::Constant, ninetyNine);
    b.emit(OpCode::SetIndex);  // v[-1] = 99 -- v is now [10, 99]
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, negOne);
    b.emit(OpCode::GetIndex);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 99.0);
}

TEST_CASE("an out-of-range negative index is still a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t tooNegative = b.addNumberConstant(-100);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);  // [1] -- valid indices are 0 and -1 only
    b.emitU16(OpCode::Constant, tooNegative);
    b.emit(OpCode::GetIndex);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("indexing with a non-integer index is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t half = b.addNumberConstant(0.5);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Constant, half);
    b.emit(OpCode::GetIndex);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("indexing with a non-number index is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);
    b.emit(OpCode::True);  // index = true, not a number
    b.emit(OpCode::GetIndex);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("indexing a non-vector value is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);  // receiver = 1, not a vector
    b.emitU16(OpCode::Constant, zero);
    b.emit(OpCode::GetIndex);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("assigning into a non-vector value via index is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);  // receiver = 1, not a vector
    b.emitU16(OpCode::Constant, zero);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::SetIndex);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

// docs/PLAN-0.3.md decision 3: v[a:b], end-inclusive slices.

namespace {

// Builds v = [10, 20, 30, 40] as a global named "v" and returns the
// vector's own element constants for reuse by callers that need to
// assert against specific values.
void buildFourElementVector(Vm& vm, ChunkBuilder& b, uint16_t vName) {
    uint16_t ten = b.addNumberConstant(10);
    uint16_t twenty = b.addNumberConstant(20);
    uint16_t thirty = b.addNumberConstant(30);
    uint16_t forty = b.addNumberConstant(40);
    b.emitU16(OpCode::Constant, ten);
    b.emitU16(OpCode::Constant, twenty);
    b.emitU16(OpCode::Constant, thirty);
    b.emitU16(OpCode::Constant, forty);
    b.emitU16(OpCode::BuildVector, 4);
    b.emitU16(OpCode::DefineGlobal, vName);
}

}  // namespace

TEST_CASE("a basic slice is end-inclusive", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    buildFourElementVector(vm, b, vName);  // v = [10, 20, 30, 40]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emit(OpCode::GetSlice);  // v[1:2] -- end-inclusive, so [20, 30]
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    auto* vec = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(vec->elements.size() == 2);
    REQUIRE(asNumber(vec->elements[0]) == 20.0);
    REQUIRE(asNumber(vec->elements[1]) == 30.0);
}

TEST_CASE("an omitted slice bound defaults to start/end", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");
    uint16_t two = b.addNumberConstant(2);
    buildFourElementVector(vm, b, vName);  // v = [10, 20, 30, 40]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emit(OpCode::Nil);  // omitted start -- defaults to 0
    b.emitU16(OpCode::Constant, two);
    b.emit(OpCode::GetSlice);  // v[:2] -- [10, 20, 30]
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    auto* vec = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(vec->elements.size() == 3);
    REQUIRE(asNumber(vec->elements[2]) == 30.0);
}

TEST_CASE("both slice bounds omitted copies the whole vector", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");
    buildFourElementVector(vm, b, vName);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emit(OpCode::Nil);
    b.emit(OpCode::Nil);
    b.emit(OpCode::GetSlice);  // v[:]
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    auto* vec = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(vec->elements.size() == 4);
}

TEST_CASE("negative slice bounds count from the end", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");
    uint16_t negThree = b.addNumberConstant(-3);
    uint16_t negOne = b.addNumberConstant(-1);
    buildFourElementVector(vm, b, vName);  // v = [10, 20, 30, 40]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, negThree);
    b.emitU16(OpCode::Constant, negOne);
    b.emit(OpCode::GetSlice);  // v[-3:-1] -- [20, 30, 40]
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    auto* vec = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(vec->elements.size() == 3);
    REQUIRE(asNumber(vec->elements[0]) == 20.0);
    REQUIRE(asNumber(vec->elements[2]) == 40.0);
}

TEST_CASE("a slice with start after stop is an empty vector, not an error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");
    uint16_t three = b.addNumberConstant(3);
    uint16_t one = b.addNumberConstant(1);
    buildFourElementVector(vm, b, vName);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::GetSlice);  // v[3:1]
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    auto* vec = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(vec->elements.empty());
}

TEST_CASE("an out-of-range slice bound clamps instead of erroring", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");
    uint16_t zero = b.addNumberConstant(0);
    uint16_t hundred = b.addNumberConstant(100);
    buildFourElementVector(vm, b, vName);  // v = [10, 20, 30, 40]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, zero);
    b.emitU16(OpCode::Constant, hundred);
    b.emit(OpCode::GetSlice);  // v[0:100] -- clamps to the whole vector
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    auto* vec = static_cast<ObjVector*>(asObj(*vm.getGlobal("result")));
    REQUIRE(vec->elements.size() == 4);
}

TEST_CASE("a slice is a copy, not a view -- mutating it doesn't mutate the source", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    uint16_t resultName = b.addStringConstant("result");
    uint16_t zero = b.addNumberConstant(0);
    uint16_t one = b.addNumberConstant(1);
    uint16_t ninetyNine = b.addNumberConstant(99);
    buildFourElementVector(vm, b, vName);  // v = [10, 20, 30, 40]

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, zero);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::GetSlice);  // result = v[0:1] = [10, 20]
    b.emitU16(OpCode::DefineGlobal, resultName);

    b.emitU16(OpCode::GetGlobal, resultName);
    b.emitU16(OpCode::Constant, zero);
    b.emitU16(OpCode::Constant, ninetyNine);
    b.emit(OpCode::SetIndex);  // result[0] = 99 -- must not touch v
    b.emit(OpCode::Pop);

    vm.interpret(b.build());

    auto* original = static_cast<ObjVector*>(asObj(*vm.getGlobal("v")));
    REQUIRE(asNumber(original->elements[0]) == 10.0);
}

TEST_CASE("slicing a non-vector value is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);  // receiver = 1, not a vector
    b.emit(OpCode::Nil);
    b.emitU16(OpCode::Constant, zero);
    b.emit(OpCode::GetSlice);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a non-number slice bound is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t vName = b.addStringConstant("v");
    buildFourElementVector(vm, b, vName);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emit(OpCode::Nil);
    b.emit(OpCode::True);  // stop bound = true, not a number
    b.emit(OpCode::GetSlice);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("reading an Undef local is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    b.emit(OpCode::Undef);           // slot 0, a `mut`-without-initializer
    b.emitU16(OpCode::GetLocal, 0);  // reading it before assignment

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("reading an undefined global is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t name = b.addStringConstant("neverDeclared");
    b.emitU16(OpCode::GetGlobal, name);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("assigning to an undeclared global is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t name = b.addStringConstant("neverDeclared");
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::SetGlobal, name);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("division by zero is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, zero);
    b.emit(OpCode::Divide);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a runtime error reports the source line the faulting instruction came from", "[vm]") {
    // Regression test for the diagnostics gap found while writing
    // docs/LANGUAGE.md's Phase 10 entry: runtime errors used to carry no
    // line number at all. `Chunk::lines` is one u16 per *byte* of `code`
    // (docs/PLAN-0.1.md's Phase 10 entry) -- hand-populated here since
    // ChunkBuilder, unlike the real compiler, has no source lines of its
    // own to derive them from.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);  // bytes 0-2
    b.emitU16(OpCode::Constant, zero);  // bytes 3-5
    b.emit(OpCode::Divide);  // byte 6

    auto* fn = b.build();
    fn->chunk.lines.assign(fn->chunk.code.size(), 1);
    fn->chunk.lines[6] = 42;  // Divide's own opcode byte

    try {
        vm.interpret(fn);
        FAIL("expected a RuntimeError");
    } catch (const RuntimeError& error) {
        REQUIRE(std::string(error.what()).find("[line 42]") != std::string::npos);
    }
}

TEST_CASE("adding a non-number is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::True);
    b.emit(OpCode::Add);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("calling a non-callable value is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 0);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("calling a closure with the wrong argument count is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder fnBuilder(vm);
    fnBuilder.emit(OpCode::Nil);
    fnBuilder.emit(OpCode::Return);
    ObjFunction* fn = fnBuilder.build(1, "f");  // expects 1 argument

    ChunkBuilder b(vm);
    uint16_t fnIndex = b.addFunctionConstant(fn);
    b.emitClosure(fnIndex, {});
    b.emitU16(OpCode::Call, 0);  // called with none

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a jump-based conditional (elvis-style) selects the correct branch", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t thenVal = b.addNumberConstant(1);
    uint16_t elseVal = b.addNumberConstant(2);
    uint16_t result = b.addStringConstant("result");

    b.emit(OpCode::False);
    size_t jumpToElse = b.emitJump(OpCode::JumpIfFalse);
    b.emit(OpCode::Pop);
    b.emitU16(OpCode::Constant, thenVal);
    size_t jumpToEnd = b.emitJump(OpCode::Jump);
    b.patch(jumpToElse);
    b.emit(OpCode::Pop);
    b.emitU16(OpCode::Constant, elseVal);
    b.patch(jumpToEnd);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 2.0);
}

TEST_CASE("a function call passes arguments and returns a value", "[vm]") {
    Vm vm;
    // fun addOne(a) { return a + 1; }
    ChunkBuilder fnBuilder(vm);
    uint16_t one = fnBuilder.addNumberConstant(1);
    fnBuilder.emitU16(OpCode::GetLocal, 0);
    fnBuilder.emitU16(OpCode::Constant, one);
    fnBuilder.emit(OpCode::Add);
    fnBuilder.emit(OpCode::Return);
    ObjFunction* fn = fnBuilder.build(1, "addOne");

    ChunkBuilder b(vm);
    uint16_t fnIndex = b.addFunctionConstant(fn);
    uint16_t five = b.addNumberConstant(5);
    uint16_t result = b.addStringConstant("result");

    b.emitClosure(fnIndex, {});
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 6.0);
}

TEST_CASE("a closure captures an enclosing local and outlives the frame that declared it", "[vm]") {
    Vm vm;
    // fun makeGetter() { var x mut = 42; fun get() { return x; } return get; }
    ChunkBuilder getBuilder(vm);
    getBuilder.emitU16(OpCode::GetUpvalue, 0);
    getBuilder.emit(OpCode::Return);
    ObjFunction* getFn = getBuilder.build(0, "get");

    ChunkBuilder makeGetterBuilder(vm);
    uint16_t fortyTwo = makeGetterBuilder.addNumberConstant(42);
    uint16_t getIndex = makeGetterBuilder.addFunctionConstant(getFn);
    makeGetterBuilder.emitU16(OpCode::Constant, fortyTwo);  // slot 0: x = 42
    makeGetterBuilder.emitClosure(getIndex, {{true, 0}});   // captures slot 0 directly
    makeGetterBuilder.emit(OpCode::Return);                 // returns the closure; x's slot is torn down
    ObjFunction* makeGetterFn = makeGetterBuilder.build(0, "makeGetter");

    ChunkBuilder b(vm);
    uint16_t makeGetterIndex = b.addFunctionConstant(makeGetterFn);
    uint16_t result = b.addStringConstant("result");

    b.emitClosure(makeGetterIndex, {});
    b.emitU16(OpCode::Call, 0);  // -> the `get` closure
    b.emitU16(OpCode::Call, 0);  // -> 42, read back after makeGetter's frame is long gone
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 42.0);
}

TEST_CASE("two closures over the same local share one upvalue -- a write through one is visible to the other",
          "[vm]") {
    Vm vm;
    // fun increment() { x = x + 1; return x; }  -- reads/writes the shared upvalue
    ChunkBuilder incBuilder(vm);
    uint16_t one = incBuilder.addNumberConstant(1);
    incBuilder.emitU16(OpCode::GetUpvalue, 0);
    incBuilder.emitU16(OpCode::Constant, one);
    incBuilder.emit(OpCode::Add);
    incBuilder.emitU16(OpCode::SetUpvalue, 0);
    incBuilder.emit(OpCode::Pop);
    incBuilder.emitU16(OpCode::GetUpvalue, 0);
    incBuilder.emit(OpCode::Return);
    ObjFunction* incFn = incBuilder.build(0, "increment");

    // fun makeCounter() { var x mut = 0; fun increment() {...}; return increment; }
    ChunkBuilder makeCounterBuilder(vm);
    uint16_t zero = makeCounterBuilder.addNumberConstant(0);
    uint16_t incIndex = makeCounterBuilder.addFunctionConstant(incFn);
    makeCounterBuilder.emitU16(OpCode::Constant, zero);  // slot 0: x = 0
    makeCounterBuilder.emitClosure(incIndex, {{true, 0}});
    makeCounterBuilder.emit(OpCode::Return);
    ObjFunction* makeCounterFn = makeCounterBuilder.build(0, "makeCounter");

    ChunkBuilder b(vm);
    uint16_t makeCounterIndex = b.addFunctionConstant(makeCounterFn);
    uint16_t result = b.addStringConstant("result");

    b.emitClosure(makeCounterIndex, {});
    b.emitU16(OpCode::Call, 0);      // slot 0: the `increment` closure, a top-level local
    b.emitU16(OpCode::GetLocal, 0);
    b.emitU16(OpCode::Call, 0);      // first call -> 1 (discarded)
    b.emit(OpCode::Pop);
    b.emitU16(OpCode::GetLocal, 0);
    b.emitU16(OpCode::Call, 0);      // second call -> 2 (kept): sees the first call's write
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 2.0);
}

TEST_CASE("aggressive stress-mode collection doesn't corrupt a running program", "[vm][gc]") {
    Vm vm;
    vm.stressGc = true;

    // Allocates a handful of strings, functions/closures, and vectors --
    // one of everything the GC has to trace -- interleaved with ordinary
    // arithmetic, with a collection forced before *every single*
    // allocation. If any of them isn't correctly rooted, this either
    // crashes or produces a wrong final answer.
    ChunkBuilder fnBuilder(vm);
    fnBuilder.emitU16(OpCode::GetUpvalue, 0);
    fnBuilder.emit(OpCode::Return);
    ObjFunction* getFn = fnBuilder.build(0, "get");

    ChunkBuilder b(vm);
    uint16_t fortyTwo = b.addNumberConstant(42);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t getIndex = b.addFunctionConstant(getFn);
    uint16_t label = b.addStringConstant("a label, just to allocate a string");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, fortyTwo);         // slot 0: x = 42
    b.emitClosure(getIndex, {{true, 0}});          // a closure over it
    b.emitU16(OpCode::Call, 0);                    // -> 42
    b.emitU16(OpCode::Constant, label);
    b.emit(OpCode::Pop);                           // just to allocate + discard a string
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);             // [1, 2], also discarded
    b.emit(OpCode::Pop);
    b.emit(OpCode::Add);                           // 42 (from the closure call) + x (slot 0)
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 84.0);
}

TEST_CASE("print and concat are present as globals without any user declaration", "[vm][natives]") {
    Vm vm;
    REQUIRE(vm.getGlobal("print") != nullptr);
    REQUIRE(vm.getGlobal("concat") != nullptr);
}

TEST_CASE("print writes stringify(value) plus a newline to stdout and returns nil", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t printName = b.addStringConstant("print");
    uint16_t hello = b.addStringConstant("hello");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::GetGlobal, printName);
    b.emitU16(OpCode::Constant, hello);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, result);

    std::ostringstream captured;
    std::streambuf* oldBuf = std::cout.rdbuf(captured.rdbuf());
    vm.interpret(b.build());
    std::cout.rdbuf(oldBuf);

    REQUIRE(captured.str() == "hello\n");
    REQUIRE(isNil(*vm.getGlobal("result")));
}

TEST_CASE("concat joins stringify of each element with no separator", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t concatName = b.addStringConstant("concat");
    uint16_t one = b.addNumberConstant(1);
    uint16_t a = b.addStringConstant("a");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::GetGlobal, concatName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, a);
    b.emit(OpCode::True);
    b.emitU16(OpCode::BuildVector, 3);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultValue = vm.getGlobal("result");
    REQUIRE(isObj(*resultValue));
    REQUIRE(static_cast<ObjString*>(asObj(*resultValue))->value == "1atrue");
}

TEST_CASE("concat with a non-vector argument is a clean runtime error, not a crash", "[vm][natives]") {
    // Regression test for a real poc bug (docs/PLAN-0.1-POC.md's running
    // list): poc's concat(5) raises an uncaught Python TypeError instead
    // of a clean IqaloxRuntimeError.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t concatName = b.addStringConstant("concat");
    uint16_t five = b.addNumberConstant(5);

    b.emitU16(OpCode::GetGlobal, concatName);
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("push/pop/length/reverse are present as globals without any user declaration", "[vm][natives]") {
    // docs/PLAN-0.2.md Phase 5: true natives (need direct ObjVector
    // access), unlike map/filter/reduce/sort which are ordinary Iqalox
    // source (compiler/src/Prelude.fs) prepended to every program.
    Vm vm;
    REQUIRE(vm.getGlobal("push") != nullptr);
    REQUIRE(vm.getGlobal("pop") != nullptr);
    REQUIRE(vm.getGlobal("length") != nullptr);
    REQUIRE(vm.getGlobal("reverse") != nullptr);
}

TEST_CASE("push appends in place and returns nil", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pushName = b.addStringConstant("push");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t vName = b.addStringConstant("v");
    uint16_t pushResult = b.addStringConstant("pushResult");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::DefineGlobal, vName);  // v = [1]

    b.emitU16(OpCode::GetGlobal, pushName);
    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Call, 2);  // push(v, 2) -- mutates v, returns nil
    b.emitU16(OpCode::DefineGlobal, pushResult);

    vm.interpret(b.build());

    REQUIRE(isNil(*vm.getGlobal("pushResult")));
    const Value* vVal = vm.getGlobal("v");
    auto& elements = static_cast<ObjVector*>(asObj(*vVal))->elements;
    REQUIRE(elements.size() == 2);
    REQUIRE(asNumber(elements[0]) == 1.0);
    REQUIRE(asNumber(elements[1]) == 2.0);
}

TEST_CASE("push on a non-vector is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pushName = b.addStringConstant("push");
    uint16_t one = b.addNumberConstant(1);

    b.emitU16(OpCode::GetGlobal, pushName);
    b.emitU16(OpCode::Constant, one);  // receiver = 1, not a vector
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 2);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("pop removes and returns the last element in place", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t popName = b.addStringConstant("pop");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t vName = b.addStringConstant("v");
    uint16_t popResult = b.addStringConstant("popResult");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::DefineGlobal, vName);  // v = [1, 2]

    b.emitU16(OpCode::GetGlobal, popName);
    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, popResult);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("popResult")) == 2.0);
    const Value* vVal = vm.getGlobal("v");
    auto& elements = static_cast<ObjVector*>(asObj(*vVal))->elements;
    REQUIRE(elements.size() == 1);
    REQUIRE(asNumber(elements[0]) == 1.0);
}

TEST_CASE("pop from an empty vector is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t popName = b.addStringConstant("pop");

    b.emitU16(OpCode::GetGlobal, popName);
    b.emitU16(OpCode::BuildVector, 0);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("pop on a non-vector is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t popName = b.addStringConstant("pop");
    uint16_t one = b.addNumberConstant(1);

    b.emitU16(OpCode::GetGlobal, popName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("length returns the element count, first user-facing exposure of VectorLength's own logic", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t lengthName = b.addStringConstant("length");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::GetGlobal, lengthName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 3);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 3.0);
}

TEST_CASE("length on a non-vector is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t lengthName = b.addStringConstant("length");
    uint16_t one = b.addNumberConstant(1);

    b.emitU16(OpCode::GetGlobal, lengthName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("reverse returns a new vector, leaving the original untouched", "[vm][natives]") {
    // docs/PLAN-0.2.md Phase 5: push/pop mutate (classic stack ops);
    // reverse reads as a pure transformation, so it returns new instead.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t reverseName = b.addStringConstant("reverse");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t vName = b.addStringConstant("v");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 3);
    b.emitU16(OpCode::DefineGlobal, vName);  // v = [1, 2, 3]

    b.emitU16(OpCode::GetGlobal, reverseName);
    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    auto& reversedElements = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(reversedElements.size() == 3);
    REQUIRE(asNumber(reversedElements[0]) == 3.0);
    REQUIRE(asNumber(reversedElements[1]) == 2.0);
    REQUIRE(asNumber(reversedElements[2]) == 1.0);

    const Value* vVal = vm.getGlobal("v");
    auto& originalElements = static_cast<ObjVector*>(asObj(*vVal))->elements;
    REQUIRE(originalElements.size() == 3);
    REQUIRE(asNumber(originalElements[0]) == 1.0);
    REQUIRE(asNumber(originalElements[1]) == 2.0);
    REQUIRE(asNumber(originalElements[2]) == 3.0);
}

TEST_CASE("reverse on a non-vector is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t reverseName = b.addStringConstant("reverse");
    uint16_t one = b.addNumberConstant(1);

    b.emitU16(OpCode::GetGlobal, reverseName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("transpose/multiply/add/subtract are present as globals without any user declaration", "[vm][natives]") {
    // docs/PLAN-0.2.md Phase 6: none of these four call back into user
    // code, so (unlike elementwise, compiler/src/Prelude.fs) they're
    // true natives too, matching push/pop/length/reverse's pattern.
    Vm vm;
    REQUIRE(vm.getGlobal("transpose") != nullptr);
    REQUIRE(vm.getGlobal("multiply") != nullptr);
    REQUIRE(vm.getGlobal("add") != nullptr);
    REQUIRE(vm.getGlobal("subtract") != nullptr);
}

TEST_CASE("transpose swaps rows and columns, returning a new matrix", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t transposeName = b.addStringConstant("transpose");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t four = b.addNumberConstant(4);
    uint16_t five = b.addNumberConstant(5);
    uint16_t six = b.addNumberConstant(6);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::GetGlobal, transposeName);
    // m = [[1, 2, 3], [4, 5, 6]], built directly as the call's own argument.
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 3);
    b.emitU16(OpCode::Constant, four);
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::Constant, six);
    b.emitU16(OpCode::BuildVector, 3);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    REQUIRE(isObj(*resultVal));
    auto& rows = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(rows.size() == 3);  // 2x3 transposed is 3x2
    auto& row0 = static_cast<ObjVector*>(asObj(rows[0]))->elements;
    REQUIRE(row0.size() == 2);
    REQUIRE(asNumber(row0[0]) == 1.0);
    REQUIRE(asNumber(row0[1]) == 4.0);
    auto& row2 = static_cast<ObjVector*>(asObj(rows[2]))->elements;
    REQUIRE(asNumber(row2[0]) == 3.0);
    REQUIRE(asNumber(row2[1]) == 6.0);
}

TEST_CASE("transpose on a non-vector is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t transposeName = b.addStringConstant("transpose");
    uint16_t one = b.addNumberConstant(1);

    b.emitU16(OpCode::GetGlobal, transposeName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("transpose on a ragged (non-rectangular) matrix is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t transposeName = b.addStringConstant("transpose");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);

    b.emitU16(OpCode::GetGlobal, transposeName);
    // [[1, 2], [1]] -- rows of different lengths
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("multiply computes the true matrix product, not an elementwise one", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t multiplyName = b.addStringConstant("multiply");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t four = b.addNumberConstant(4);
    uint16_t five = b.addNumberConstant(5);
    uint16_t six = b.addNumberConstant(6);
    uint16_t seven = b.addNumberConstant(7);
    uint16_t eight = b.addNumberConstant(8);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::GetGlobal, multiplyName);
    // a = [[1, 2], [3, 4]]
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::Constant, four);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 2);
    // b = [[5, 6], [7, 8]]
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::Constant, six);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Constant, seven);
    b.emitU16(OpCode::Constant, eight);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    // [[1,2],[3,4]] * [[5,6],[7,8]] = [[1*5+2*7, 1*6+2*8], [3*5+4*7, 3*6+4*8]] = [[19,22],[43,50]]
    const Value* resultVal = vm.getGlobal("result");
    REQUIRE(isObj(*resultVal));
    auto& rows = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(rows.size() == 2);
    auto& row0 = static_cast<ObjVector*>(asObj(rows[0]))->elements;
    REQUIRE(asNumber(row0[0]) == 19.0);
    REQUIRE(asNumber(row0[1]) == 22.0);
    auto& row1 = static_cast<ObjVector*>(asObj(rows[1]))->elements;
    REQUIRE(asNumber(row1[0]) == 43.0);
    REQUIRE(asNumber(row1[1]) == 50.0);
}

TEST_CASE("add computes the elementwise sum and doesn't mutate either argument", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t addName = b.addStringConstant("add");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t four = b.addNumberConstant(4);
    uint16_t aName = b.addStringConstant("a");
    uint16_t bName = b.addStringConstant("b");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::DefineGlobal, aName);  // a = [[1, 2]]

    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::Constant, four);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::DefineGlobal, bName);  // b = [[3, 4]]

    b.emitU16(OpCode::GetGlobal, addName);
    b.emitU16(OpCode::GetGlobal, aName);
    b.emitU16(OpCode::GetGlobal, bName);
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    auto& resultRow = static_cast<ObjVector*>(asObj(static_cast<ObjVector*>(asObj(*resultVal))->elements[0]))->elements;
    REQUIRE(asNumber(resultRow[0]) == 4.0);
    REQUIRE(asNumber(resultRow[1]) == 6.0);

    // Neither original argument was mutated.
    const Value* aVal = vm.getGlobal("a");
    auto& aRow = static_cast<ObjVector*>(asObj(static_cast<ObjVector*>(asObj(*aVal))->elements[0]))->elements;
    REQUIRE(asNumber(aRow[0]) == 1.0);
    REQUIRE(asNumber(aRow[1]) == 2.0);
}

TEST_CASE("add with mismatched shapes is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t addName = b.addStringConstant("add");
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);

    b.emitU16(OpCode::GetGlobal, addName);
    // a = [[1, 2]] (1x2), b = [[1]] (1x1)
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Call, 2);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("subtract computes the elementwise difference, a minus b", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t subtractName = b.addStringConstant("subtract");
    uint16_t five = b.addNumberConstant(5);
    uint16_t two = b.addNumberConstant(2);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::GetGlobal, subtractName);
    // a = [[5]], b = [[2]]
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    auto& row = static_cast<ObjVector*>(asObj(static_cast<ObjVector*>(asObj(*resultVal))->elements[0]))->elements;
    REQUIRE(asNumber(row[0]) == 3.0);  // 5 - 2
}

TEST_CASE("multiply with incompatible dimensions is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t multiplyName = b.addStringConstant("multiply");
    uint16_t one = b.addNumberConstant(1);

    b.emitU16(OpCode::GetGlobal, multiplyName);
    // a = [[1, 1]] (1x2), b = [[1, 1]] (1x2) -- a's cols (2) != b's rows (1)
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::BuildVector, 1);
    b.emitU16(OpCode::Call, 2);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("VectorLength pushes the element count of a vector", "[vm]") {
    // docs/PLAN-0.2.md Phase 3: an internal-only primitive, never emitted
    // by any surface syntax -- only Codegen.fs's desugared Cons/
    // ListComprehension loop condition uses it. Exercised directly here
    // since no user-written Iqalox program can reach it any other way.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 3);  // [1, 2, 3]
    b.emit(OpCode::VectorLength);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("result")) == 3.0);
}

TEST_CASE("VectorLength on a non-vector is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::VectorLength);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("VectorAppend mutates the vector in place and pushes nothing back", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t vName = b.addStringConstant("v");
    uint16_t lengthName = b.addStringConstant("length");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);  // [1]
    b.emitU16(OpCode::DefineGlobal, vName);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::Constant, two);
    b.emit(OpCode::VectorAppend);  // v.push_back(2), pushes nothing

    b.emitU16(OpCode::GetGlobal, vName);
    b.emit(OpCode::VectorLength);
    b.emitU16(OpCode::DefineGlobal, lengthName);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("length")) == 2.0);
    const Value* vVal = vm.getGlobal("v");
    REQUIRE(static_cast<ObjVector*>(asObj(*vVal))->elements.size() == 2);
    REQUIRE(asNumber(static_cast<ObjVector*>(asObj(*vVal))->elements[1]) == 2.0);
}

TEST_CASE("VectorAppend's mutation is visible through every other reference to the same vector", "[vm]") {
    // Vectors are heap-allocated reference types -- appending through one
    // copy of the pointer is immediately visible through any other, which
    // is exactly what lets Codegen.fs's desugared loop mutate `$result`
    // without ever needing to store it back.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t aliasLength = b.addStringConstant("aliasLength");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);  // [1], left on the stack as slot 0 ("v")
    b.emitU16(OpCode::GetLocal, 0);     // a second reference to the same ObjVector ("alias")

    b.emitU16(OpCode::GetLocal, 0);  // "v" again, as VectorAppend's receiver
    b.emitU16(OpCode::Constant, two);
    b.emit(OpCode::VectorAppend);  // mutates through "v"

    b.emitU16(OpCode::GetLocal, 1);  // "alias", a different stack slot, same underlying vector
    b.emit(OpCode::VectorLength);
    b.emitU16(OpCode::DefineGlobal, aliasLength);

    vm.interpret(b.build());

    REQUIRE(asNumber(*vm.getGlobal("aliasLength")) == 2.0);
}

TEST_CASE("VectorAppend on a non-vector is a runtime error", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    b.emitU16(OpCode::Constant, one);  // receiver = 1, not a vector
    b.emitU16(OpCode::Constant, two);
    b.emit(OpCode::VectorAppend);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("end-to-end: a cons call builds [item, ...list] via a synthetic closure", "[vm]") {
    // Mirrors exactly what Codegen.fs emits for `[1 | list]` -- verified
    // against CodegenTests.fs's `cons compiles to a call of a synthetic
    // closure...` test. Exercises the whole synthetic-closure desugaring
    // end-to-end at the VM level: BuildVector for the accumulator,
    // VectorLength/VectorAppend/GetIndex in a real loop, and Return
    // extracting the final accumulator out from under the closure's own
    // now-discarded locals ($index, and the loop's own transient values).
    Vm vm;

    // fun cons($item, $list) {
    //     var $result = [$item]
    //     var $index mut = 0
    //     for (; $index < len($list); ++$index) { $result.append($list[$index]); }
    //     return $result
    // }
    ChunkBuilder consBuilder(vm);
    uint16_t zero = consBuilder.addNumberConstant(0);
    uint16_t one = consBuilder.addNumberConstant(1);
    consBuilder.emitU16(OpCode::GetLocal, 0);  // $item
    consBuilder.emitU16(OpCode::BuildVector, 1);  // $result (slot 2) = [$item]
    consBuilder.emitU16(OpCode::Constant, zero);  // $index (slot 3) = 0
    size_t loopStart = consBuilder.here();
    consBuilder.emitU16(OpCode::GetLocal, 3);  // $index
    consBuilder.emitU16(OpCode::GetLocal, 1);  // $list
    consBuilder.emit(OpCode::VectorLength);
    consBuilder.emit(OpCode::Less);
    size_t exitJump = consBuilder.emitJump(OpCode::JumpIfFalse);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitU16(OpCode::GetLocal, 2);  // $result
    consBuilder.emitU16(OpCode::GetLocal, 1);  // $list
    consBuilder.emitU16(OpCode::GetLocal, 3);  // $index
    consBuilder.emit(OpCode::GetIndex);
    consBuilder.emit(OpCode::VectorAppend);
    consBuilder.emit(OpCode::Nil);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitU16(OpCode::GetLocal, 3);
    consBuilder.emitU16(OpCode::Constant, one);
    consBuilder.emit(OpCode::Add);
    consBuilder.emitU16(OpCode::SetLocal, 3);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitJumpTo(OpCode::Jump, loopStart);
    consBuilder.patch(exitJump);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitU16(OpCode::GetLocal, 2);  // return $result
    consBuilder.emit(OpCode::Return);
    ObjFunction* consFn = consBuilder.build(2, "cons");

    ChunkBuilder b(vm);
    uint16_t consIndex = b.addFunctionConstant(consFn);
    uint16_t itemVal = b.addNumberConstant(1);
    uint16_t tailA = b.addNumberConstant(2);
    uint16_t tailB = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    b.emitClosure(consIndex, {});
    b.emitU16(OpCode::Constant, itemVal);
    b.emitU16(OpCode::Constant, tailA);
    b.emitU16(OpCode::Constant, tailB);
    b.emitU16(OpCode::BuildVector, 2);  // list = [2, 3]
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    REQUIRE(isObj(*resultVal));
    auto& elements = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(elements.size() == 3);
    REQUIRE(asNumber(elements[0]) == 1.0);
    REQUIRE(asNumber(elements[1]) == 2.0);
    REQUIRE(asNumber(elements[2]) == 3.0);
}

TEST_CASE("end-to-end: consing onto an empty list produces a single-element vector", "[vm]") {
    // Regression test for the exact bug the synthetic-closure redesign
    // fixed: `[1 | []]` used to corrupt local-slot addressing when the
    // cons's hidden locals were declared directly in the enclosing scope
    // instead of an isolated closure frame.
    Vm vm;

    ChunkBuilder consBuilder(vm);
    uint16_t zero = consBuilder.addNumberConstant(0);
    uint16_t one = consBuilder.addNumberConstant(1);
    consBuilder.emitU16(OpCode::GetLocal, 0);
    consBuilder.emitU16(OpCode::BuildVector, 1);
    consBuilder.emitU16(OpCode::Constant, zero);
    size_t loopStart = consBuilder.here();
    consBuilder.emitU16(OpCode::GetLocal, 3);
    consBuilder.emitU16(OpCode::GetLocal, 1);
    consBuilder.emit(OpCode::VectorLength);
    consBuilder.emit(OpCode::Less);
    size_t exitJump = consBuilder.emitJump(OpCode::JumpIfFalse);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitU16(OpCode::GetLocal, 2);
    consBuilder.emitU16(OpCode::GetLocal, 1);
    consBuilder.emitU16(OpCode::GetLocal, 3);
    consBuilder.emit(OpCode::GetIndex);
    consBuilder.emit(OpCode::VectorAppend);
    consBuilder.emit(OpCode::Nil);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitU16(OpCode::GetLocal, 3);
    consBuilder.emitU16(OpCode::Constant, one);
    consBuilder.emit(OpCode::Add);
    consBuilder.emitU16(OpCode::SetLocal, 3);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitJumpTo(OpCode::Jump, loopStart);
    consBuilder.patch(exitJump);
    consBuilder.emit(OpCode::Pop);
    consBuilder.emitU16(OpCode::GetLocal, 2);
    consBuilder.emit(OpCode::Return);
    ObjFunction* consFn = consBuilder.build(2, "cons");

    ChunkBuilder b(vm);
    uint16_t consIndex = b.addFunctionConstant(consFn);
    uint16_t itemVal = b.addNumberConstant(1);
    uint16_t result = b.addStringConstant("result");

    // print(item) as a preceding call, matching the original failing
    // repro (`print [1 | []]`) -- the callee occupying a stack slot ahead
    // of the cons call's own arguments is exactly what the old hidden-
    // local-slot design got wrong.
    b.emitClosure(consIndex, {});
    b.emitU16(OpCode::Constant, itemVal);
    b.emitU16(OpCode::BuildVector, 0);  // list = []
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    REQUIRE(isObj(*resultVal));
    auto& elements = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(elements.size() == 1);
    REQUIRE(asNumber(elements[0]) == 1.0);
}

TEST_CASE("VectorExtend appends every element of the source onto the target and pushes the target back", "[vm]") {
    // docs/PLAN-0.2.md Phase 4: `[...a, ...b]` -- unlike VectorAppend,
    // this one's reachable directly from spread syntax, so the target is
    // pushed back onto the stack (no accumulator local slot to refetch it
    // from between chained spreads/plain elements).
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::BuildVector, 0);  // target = []
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 3);  // source = [1, 2, 3]
    b.emit(OpCode::VectorExtend);       // target.extend(source), target pushed back
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    REQUIRE(isObj(*resultVal));
    auto& elements = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(elements.size() == 3);
    REQUIRE(asNumber(elements[0]) == 1.0);
    REQUIRE(asNumber(elements[1]) == 2.0);
    REQUIRE(asNumber(elements[2]) == 3.0);
}

TEST_CASE("VectorExtend chains: several extends in a row build up the same target vector", "[vm]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t three = b.addNumberConstant(3);
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::BuildVector, 0);  // target = []
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::BuildVector, 1);  // [1]
    b.emit(OpCode::VectorExtend);       // target = [1], pushed back
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::BuildVector, 2);  // [2, 3]
    b.emit(OpCode::VectorExtend);       // target = [1, 2, 3], pushed back
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    auto& elements = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(elements.size() == 3);
    REQUIRE(asNumber(elements[0]) == 1.0);
    REQUIRE(asNumber(elements[1]) == 2.0);
    REQUIRE(asNumber(elements[2]) == 3.0);
}

TEST_CASE("VectorExtend with a non-vector source is a user-facing runtime error", "[vm]") {
    // Unlike VectorAppend's non-vector *receiver* (an internal-consistency
    // check, never reachable from real syntax), a non-vector *source* here
    // is exactly what `[...5]` produces -- a real, user-facing type error.
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t five = b.addNumberConstant(5);

    b.emitU16(OpCode::BuildVector, 0);  // target = []
    b.emitU16(OpCode::Constant, five);  // source = 5, not a vector
    b.emit(OpCode::VectorExtend);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("end-to-end: a vector literal with a spread element flattens purely on the stack, no locals needed", "[vm]") {
    // Mirrors exactly what Codegen.fs emits for `[0, ...a, 5]` -- verified
    // against CodegenTests.fs's own instruction-sequence assertion for the
    // same source. Confirms the whole spread-flattening strategy actually
    // executes correctly end to end, including composing safely as a call
    // argument (no hidden local slots to corrupt, unlike Phase 3's
    // Cons/ListComprehension before their synthetic-closure fix).
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t zero = b.addNumberConstant(0);
    uint16_t one = b.addNumberConstant(1);
    uint16_t two = b.addNumberConstant(2);
    uint16_t five = b.addNumberConstant(5);
    uint16_t aName = b.addStringConstant("a");
    uint16_t result = b.addStringConstant("result");

    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, two);
    b.emitU16(OpCode::BuildVector, 2);
    b.emitU16(OpCode::DefineGlobal, aName);  // a = [1, 2]

    b.emitU16(OpCode::BuildVector, 0);  // accumulator = []
    b.emitU16(OpCode::Constant, zero);
    b.emitU16(OpCode::BuildVector, 1);  // [0]
    b.emit(OpCode::VectorExtend);       // acc.extend([0])
    b.emitU16(OpCode::GetGlobal, aName);
    b.emit(OpCode::VectorExtend);  // acc.extend(a)
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::BuildVector, 1);  // [5]
    b.emit(OpCode::VectorExtend);       // acc.extend([5])
    b.emitU16(OpCode::DefineGlobal, result);

    vm.interpret(b.build());

    const Value* resultVal = vm.getGlobal("result");
    REQUIRE(isObj(*resultVal));
    auto& elements = static_cast<ObjVector*>(asObj(*resultVal))->elements;
    REQUIRE(elements.size() == 4);
    REQUIRE(asNumber(elements[0]) == 0.0);
    REQUIRE(asNumber(elements[1]) == 1.0);
    REQUIRE(asNumber(elements[2]) == 2.0);
    REQUIRE(asNumber(elements[3]) == 5.0);
}

TEST_CASE("calling print with the wrong argument count is a runtime error", "[vm][natives]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t printName = b.addStringConstant("print");

    b.emitU16(OpCode::GetGlobal, printName);
    b.emitU16(OpCode::Call, 0);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a runtime error names the source file when the program has a file table", "[vm]") {
    Vm vm;
    vm.sourceFiles = {"main.iqx", "math/vector.iqx"};
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, zero);
    b.emit(OpCode::Divide);  // byte 6

    auto* fn = b.build();
    fn->chunk.lines.assign(fn->chunk.code.size(), 1);
    fn->chunk.lines[6] = 7;
    fn->chunk.files.assign(fn->chunk.code.size(), 0);
    fn->chunk.files[6] = 1;  // the faulting Divide came from the second file

    try {
        vm.interpret(fn);
        FAIL("expected a RuntimeError");
    } catch (const RuntimeError& error) {
        REQUIRE(std::string(error.what()).find("[math/vector.iqx, line 7]") != std::string::npos);
    }
}

TEST_CASE("an empty file name falls back to a bare line number", "[vm]") {
    Vm vm;
    vm.sourceFiles = {""};
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    uint16_t zero = b.addNumberConstant(0);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Constant, zero);
    b.emit(OpCode::Divide);

    auto* fn = b.build();
    fn->chunk.lines.assign(fn->chunk.code.size(), 9);
    fn->chunk.files.assign(fn->chunk.code.size(), 0);

    try {
        vm.interpret(fn);
        FAIL("expected a RuntimeError");
    } catch (const RuntimeError& error) {
        REQUIRE(std::string(error.what()).find("[line 9]") != std::string::npos);
    }
}

TEST_CASE("a type-check fault (non-number operand) also reports its source location", "[vm]") {
    // These used to be thrown as bare RuntimeErrors with no `[line N]` at all.
    Vm vm;
    vm.sourceFiles = {"main.iqx"};
    ChunkBuilder b(vm);
    uint16_t one = b.addNumberConstant(1);
    b.emitU16(OpCode::Constant, one);
    b.emit(OpCode::Nil);
    b.emit(OpCode::Subtract);  // byte 4: number - nil

    auto* fn = b.build();
    fn->chunk.lines.assign(fn->chunk.code.size(), 3);
    fn->chunk.files.assign(fn->chunk.code.size(), 0);

    try {
        vm.interpret(fn);
        FAIL("expected a RuntimeError");
    } catch (const RuntimeError& error) {
        std::string what = error.what();
        REQUIRE(what.find("Operands must be numbers.") != std::string::npos);
        REQUIRE(what.find("[main.iqx, line 3]") != std::string::npos);
    }
}
