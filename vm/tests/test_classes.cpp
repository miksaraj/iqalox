#include <catch2/catch_test_macros.hpp>

#include <string>

#include "chunk_builder.hpp"
#include "object.hpp"
#include "vm.hpp"

using namespace iqalox;
using bytecode::OpCode;
using iqalox::testing::ChunkBuilder;

namespace {

std::string asString(const Value& v) {
    REQUIRE(isObj(v));
    REQUIRE(asObj(v)->type == ObjType::String);
    return static_cast<ObjString*>(asObj(v))->value;
}

}  // namespace

TEST_CASE("a class with no init constructs a bare instance, and fields get/set", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t pName = b.addStringConstant("p");
    uint16_t xName = b.addStringConstant("x");
    uint16_t five = b.addIntConstant(5);
    uint16_t resultName = b.addStringConstant("result");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);  // temp re-fetch
    // `pub mut`, so the external get/set below (docs/PLAN-0.2.md Phase 7)
    // is actually allowed -- an undeclared/private/read-only property
    // would each be a runtime error instead.
    b.emitU16(OpCode::PropertyPubMut, xName);
    b.emit(OpCode::Pop);  // discard temp re-fetch

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::SetProperty, xName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::GetProperty, xName);
    b.emitU16(OpCode::DefineGlobal, resultName);

    vm.interpret(b.build());

    REQUIRE(asSmallInt(*vm.getGlobal("result")) == 5);
}

TEST_CASE("init runs with self bound and its own return value is discarded for the instance", "[vm][classes]") {
    // class Point { init(x, y) { self.x = x; self.y = y; } sum() { return self.x + self.y; } }
    // var p = Point(3, 4); var result = p.sum();
    Vm vm;

    ChunkBuilder initBuilder(vm);
    uint16_t xName1 = initBuilder.addStringConstant("x");
    uint16_t yName1 = initBuilder.addStringConstant("y");
    initBuilder.emitU16(OpCode::GetLocal, 0);  // self
    initBuilder.emitU16(OpCode::GetLocal, 1);  // x
    initBuilder.emitU16(OpCode::SetPropertySelf, xName1);
    initBuilder.emit(OpCode::Pop);
    initBuilder.emitU16(OpCode::GetLocal, 0);  // self
    initBuilder.emitU16(OpCode::GetLocal, 2);  // y
    initBuilder.emitU16(OpCode::SetPropertySelf, yName1);
    initBuilder.emit(OpCode::Pop);
    ObjFunction* initFn = initBuilder.build(2, "init");

    ChunkBuilder sumBuilder(vm);
    uint16_t xName2 = sumBuilder.addStringConstant("x");
    uint16_t yName2 = sumBuilder.addStringConstant("y");
    sumBuilder.emitU16(OpCode::GetLocal, 0);
    sumBuilder.emitU16(OpCode::GetPropertySelf, xName2);
    sumBuilder.emitU16(OpCode::GetLocal, 0);
    sumBuilder.emitU16(OpCode::GetPropertySelf, yName2);
    sumBuilder.emit(OpCode::Add);
    sumBuilder.emit(OpCode::Return);
    ObjFunction* sumFn = sumBuilder.build(0, "sum");

    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t initIndex = b.addFunctionConstant(initFn);
    uint16_t sumIndex = b.addFunctionConstant(sumFn);
    uint16_t initName = b.addStringConstant("init");
    uint16_t sumName = b.addStringConstant("sum");
    uint16_t xDeclName = b.addStringConstant("x");
    uint16_t yDeclName = b.addStringConstant("y");
    uint16_t three = b.addIntConstant(3);
    uint16_t four = b.addIntConstant(4);
    uint16_t pName = b.addStringConstant("p");
    uint16_t resultName = b.addStringConstant("result");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);  // temp re-fetch
    // Private (no external access needed by this test) but `mut`, so
    // `init` can freely set both on every fresh instance.
    b.emitU16(OpCode::PropertyPrivateMut, xDeclName);
    b.emitU16(OpCode::PropertyPrivateMut, yDeclName);
    b.emitClosure(initIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emitClosure(sumIndex, {});
    // `sum` is called externally (`p.sum()` below), so it needs `pub`
    // (docs/PLAN-0.2.md decision 11) -- `init` doesn't, since it's always
    // externally callable regardless of any `pub` annotation.
    b.emitU16(OpCode::MethodPub, sumName);
    b.emit(OpCode::Pop);  // discard temp re-fetch

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Constant, three);
    b.emitU16(OpCode::Constant, four);
    b.emitU16(OpCode::Call, 2);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::GetProperty, sumName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, resultName);

    vm.interpret(b.build());

    REQUIRE(asSmallInt(*vm.getGlobal("result")) == 7);
}

TEST_CASE("calling a class with the wrong argument count for init is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder initBuilder(vm);
    ObjFunction* initFn = initBuilder.build(1, "init");  // expects 1 argument

    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t initIndex = b.addFunctionConstant(initFn);
    uint16_t initName = b.addStringConstant("init");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitClosure(initIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);  // no arguments given

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("calling a class with no init and any arguments is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t one = b.addIntConstant(1);

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("reading an undefined property is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t pName = b.addStringConstant("p");
    uint16_t missing = b.addStringConstant("missing");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::GetProperty, missing);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("getting a property on a non-instance is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t five = b.addIntConstant(5);
    uint16_t xName = b.addStringConstant("x");

    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::GetProperty, xName);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("setting a property on a non-instance is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t five = b.addIntConstant(5);
    uint16_t one = b.addIntConstant(1);
    uint16_t xName = b.addStringConstant("x");

    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::SetProperty, xName);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("extending a non-class value is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t notAClassName = b.addStringConstant("NotAClass");
    uint16_t five = b.addIntConstant(5);
    uint16_t dogName = b.addStringConstant("Dog");

    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::DefineGlobal, notAClassName);

    b.emitU16(OpCode::Class, dogName);
    b.emitU16(OpCode::DefineGlobal, dogName);
    b.emitU16(OpCode::GetGlobal, notAClassName);
    b.emitU16(OpCode::GetGlobal, dogName);
    b.emit(OpCode::Inherit);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("calling a non-callable instance is a runtime error", "[vm][classes]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t pName = b.addStringConstant("p");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::Call, 0);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("inheritance: overriding, dynamic dispatch through self, and an explicit super call", "[vm][classes]") {
    // class Animal { speak() { return "..."; } describe() { return self.speak(); } }
    // class Dog extends Animal { speak() { return "Woof"; } describeSuper() { return super.speak(); } }
    // var d = Dog();
    // r1 = d.speak();          -- "Woof" (overridden)
    // r2 = d.describe();       -- "Woof" (inherited method, but dynamic dispatch on `self` finds Dog's override)
    // r3 = d.describeSuper();  -- "..." (explicit `super` bypasses the override)
    Vm vm;

    ChunkBuilder animalSpeakBuilder(vm);
    uint16_t dots = animalSpeakBuilder.addStringConstant("...");
    animalSpeakBuilder.emitU16(OpCode::Constant, dots);
    animalSpeakBuilder.emit(OpCode::Return);
    ObjFunction* animalSpeakFn = animalSpeakBuilder.build(0, "speak");

    ChunkBuilder describeBuilder(vm);
    uint16_t speakName1 = describeBuilder.addStringConstant("speak");
    describeBuilder.emitU16(OpCode::GetLocal, 0);  // self
    describeBuilder.emitU16(OpCode::GetPropertySelf, speakName1);
    describeBuilder.emitU16(OpCode::Call, 0);
    describeBuilder.emit(OpCode::Return);
    ObjFunction* describeFn = describeBuilder.build(0, "describe");

    ChunkBuilder dogSpeakBuilder(vm);
    uint16_t woof = dogSpeakBuilder.addStringConstant("Woof");
    dogSpeakBuilder.emitU16(OpCode::Constant, woof);
    dogSpeakBuilder.emit(OpCode::Return);
    ObjFunction* dogSpeakFn = dogSpeakBuilder.build(0, "speak");

    // describeSuper(): self is slot 0, `super` is captured as upvalue 0
    // (mirrors how a method referencing `super` always resolves it -- see
    // Bound.BSuper / Resolver.fs).
    ChunkBuilder describeSuperBuilder(vm);
    uint16_t speakName2 = describeSuperBuilder.addStringConstant("speak");
    describeSuperBuilder.emitU16(OpCode::GetLocal, 0);     // self
    describeSuperBuilder.emitU16(OpCode::GetUpvalue, 0);   // super (== Animal)
    describeSuperBuilder.emitU16(OpCode::GetSuper, speakName2);
    describeSuperBuilder.emitU16(OpCode::Call, 0);
    describeSuperBuilder.emit(OpCode::Return);
    ObjFunction* describeSuperFn = describeSuperBuilder.build(0, "describeSuper");

    ChunkBuilder b(vm);
    uint16_t animalName = b.addStringConstant("Animal");
    uint16_t dogName = b.addStringConstant("Dog");
    uint16_t speakName = b.addStringConstant("speak");
    uint16_t describeName = b.addStringConstant("describe");
    uint16_t describeSuperName = b.addStringConstant("describeSuper");
    uint16_t animalSpeakIndex = b.addFunctionConstant(animalSpeakFn);
    uint16_t describeIndex = b.addFunctionConstant(describeFn);
    uint16_t dogSpeakIndex = b.addFunctionConstant(dogSpeakFn);
    uint16_t describeSuperIndex = b.addFunctionConstant(describeSuperFn);
    uint16_t dName = b.addStringConstant("d");
    uint16_t r1Name = b.addStringConstant("r1");
    uint16_t r2Name = b.addStringConstant("r2");
    uint16_t r3Name = b.addStringConstant("r3");

    // class Animal { speak() {...} describe() {...} }
    // `speak` is only ever called internally (via `self.speak()` inside
    // `describe`, and via `super.speak()` bypassing dispatch entirely --
    // neither needs `pub`); `describe` is called externally below (and
    // inherited by Dog, which never overrides it), so it needs `pub`.
    b.emitU16(OpCode::Class, animalName);
    b.emitU16(OpCode::DefineGlobal, animalName);
    b.emitU16(OpCode::GetGlobal, animalName);
    b.emitClosure(animalSpeakIndex, {});
    b.emitU16(OpCode::Method, speakName);
    b.emitClosure(describeIndex, {});
    b.emitU16(OpCode::MethodPub, describeName);
    b.emit(OpCode::Pop);

    // class Dog extends Animal { speak() {...} describeSuper() {...} }
    // Dog is global, so the synthetic `super` local lives in the
    // top-level script's own frame (slot 0, since nothing else has taken
    // a local slot there yet) -- describeSuperFn captures it as upvalue 0
    // via FromEnclosingLocal=true, index=0.
    //
    // Both of Dog's own methods are called externally below, so both need
    // their own `pub` -- an override always starts private regardless of
    // what the superclass declared (`Vm`'s `Method` handler clears any
    // inherited `publicMethods` entry), so Dog's `speak` override can't
    // just inherit Animal's (private) visibility here either way.
    b.emitU16(OpCode::Class, dogName);
    b.emitU16(OpCode::DefineGlobal, dogName);
    b.emitU16(OpCode::GetGlobal, animalName);  // superclass
    b.emitU16(OpCode::GetGlobal, dogName);     // temp re-fetch
    b.emit(OpCode::Inherit);
    b.emitClosure(dogSpeakIndex, {});
    b.emitU16(OpCode::MethodPub, speakName);
    b.emitClosure(describeSuperIndex, {{true, 0}});
    b.emitU16(OpCode::MethodPub, describeSuperName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, dogName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, dName);

    b.emitU16(OpCode::GetGlobal, dName);
    b.emitU16(OpCode::GetProperty, speakName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, r1Name);

    b.emitU16(OpCode::GetGlobal, dName);
    b.emitU16(OpCode::GetProperty, describeName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, r2Name);

    b.emitU16(OpCode::GetGlobal, dName);
    b.emitU16(OpCode::GetProperty, describeSuperName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, r3Name);

    vm.interpret(b.build());

    REQUIRE(asString(*vm.getGlobal("r1")) == "Woof");
    REQUIRE(asString(*vm.getGlobal("r2")) == "Woof");
    REQUIRE(asString(*vm.getGlobal("r3")) == "...");
}

TEST_CASE("super.method() with an undefined method name is a runtime error", "[vm][classes]") {
    Vm vm;

    ChunkBuilder callSuperBuilder(vm);
    uint16_t missing = callSuperBuilder.addStringConstant("missing");
    callSuperBuilder.emitU16(OpCode::GetLocal, 0);
    callSuperBuilder.emitU16(OpCode::GetUpvalue, 0);
    callSuperBuilder.emitU16(OpCode::GetSuper, missing);
    ObjFunction* callSuperFn = callSuperBuilder.build(0, "callSuper");

    ChunkBuilder b(vm);
    uint16_t animalName = b.addStringConstant("Animal");
    uint16_t dogName = b.addStringConstant("Dog");
    uint16_t callSuperName = b.addStringConstant("callSuper");
    uint16_t callSuperIndex = b.addFunctionConstant(callSuperFn);
    uint16_t dName = b.addStringConstant("d");

    b.emitU16(OpCode::Class, animalName);
    b.emitU16(OpCode::DefineGlobal, animalName);

    b.emitU16(OpCode::Class, dogName);
    b.emitU16(OpCode::DefineGlobal, dogName);
    b.emitU16(OpCode::GetGlobal, animalName);
    b.emitU16(OpCode::GetGlobal, dogName);
    b.emit(OpCode::Inherit);
    b.emitClosure(callSuperIndex, {{true, 0}});
    // `pub`, since it's called externally below -- otherwise the runtime
    // error this test expects would come from the (also correct, but not
    // what this test is meant to exercise) "callSuper is private" check
    // instead of the intended "missing" super-lookup failure.
    b.emitU16(OpCode::MethodPub, callSuperName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, dogName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, dName);
    b.emitU16(OpCode::GetGlobal, dName);
    b.emitU16(OpCode::GetProperty, callSuperName);
    b.emitU16(OpCode::Call, 0);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("aggressive stress-mode collection doesn't corrupt classes, instances, or bound methods", "[vm][classes][gc]") {
    Vm vm;
    vm.stressGc = true;

    ChunkBuilder getXBuilder(vm);
    uint16_t xName = getXBuilder.addStringConstant("x");
    getXBuilder.emitU16(OpCode::GetLocal, 0);
    getXBuilder.emitU16(OpCode::GetPropertySelf, xName);
    getXBuilder.emit(OpCode::Return);
    ObjFunction* getXFn = getXBuilder.build(0, "getX");

    ChunkBuilder initBuilder(vm);
    uint16_t xName2 = initBuilder.addStringConstant("x");
    initBuilder.emitU16(OpCode::GetLocal, 0);
    initBuilder.emitU16(OpCode::GetLocal, 1);
    initBuilder.emitU16(OpCode::SetPropertySelf, xName2);
    initBuilder.emit(OpCode::Pop);
    ObjFunction* initFn = initBuilder.build(1, "init");

    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t initIndex = b.addFunctionConstant(initFn);
    uint16_t getXIndex = b.addFunctionConstant(getXFn);
    uint16_t initName = b.addStringConstant("init");
    uint16_t getXName = b.addStringConstant("getX");
    uint16_t xDeclName = b.addStringConstant("x");
    uint16_t fortyTwo = b.addIntConstant(42);
    uint16_t pName = b.addStringConstant("p");
    uint16_t resultName = b.addStringConstant("result");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::PropertyPrivateMut, xDeclName);
    b.emitClosure(initIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emitClosure(getXIndex, {});
    // Called externally (`p.getX()` below), so `pub`.
    b.emitU16(OpCode::MethodPub, getXName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Constant, fortyTwo);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::GetProperty, getXName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, resultName);

    vm.interpret(b.build());

    REQUIRE(asSmallInt(*vm.getGlobal("result")) == 42);
}

// docs/PLAN-0.2.md Phase 7: property/method visibility (`pub`/`mut`,
// decisions 8-11) and the now-resolved open question 3 (subclass access
// to a superclass's private members is protected-like, not stricter).

TEST_CASE("a private (no pub) property is invisible to external access", "[vm][classes][visibility]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t xName = b.addStringConstant("x");
    uint16_t pName = b.addStringConstant("p");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::PropertyPrivateMut, xName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::GetProperty, xName);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a pub (non-mut) property is externally readable but not writable", "[vm][classes][visibility]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t xName = b.addStringConstant("x");
    uint16_t pName = b.addStringConstant("p");
    uint16_t nine = b.addIntConstant(9);

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::PropertyPub, xName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::Constant, nine);
    b.emitU16(OpCode::SetProperty, xName);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("an immutable property can be set internally exactly once, from anywhere", "[vm][classes][visibility]") {
    // class Point { var x; init(v) { self.x = v; self.x = v; } }
    Vm vm;
    ChunkBuilder initBuilder(vm);
    uint16_t xName1 = initBuilder.addStringConstant("x");
    initBuilder.emitU16(OpCode::GetLocal, 0);
    initBuilder.emitU16(OpCode::GetLocal, 1);
    initBuilder.emitU16(OpCode::SetPropertySelf, xName1);
    initBuilder.emit(OpCode::Pop);
    initBuilder.emitU16(OpCode::GetLocal, 0);
    initBuilder.emitU16(OpCode::GetLocal, 1);
    initBuilder.emitU16(OpCode::SetPropertySelf, xName1);  // second assignment -- must fail
    initBuilder.emit(OpCode::Pop);
    ObjFunction* initFn = initBuilder.build(1, "init");

    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t initIndex = b.addFunctionConstant(initFn);
    uint16_t initName = b.addStringConstant("init");
    uint16_t xDeclName = b.addStringConstant("x");
    uint16_t one = b.addIntConstant(1);

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::PropertyPrivate, xDeclName);  // immutable
    b.emitClosure(initIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Constant, one);
    b.emitU16(OpCode::Call, 1);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("a mut property allows unlimited internal writes", "[vm][classes][visibility]") {
    // class Counter { var n mut; init() { self.n = 1; self.n = 2; self.n = 3; } getN() { return self.n; } }
    Vm vm;
    ChunkBuilder initBuilder(vm);
    uint16_t nName1 = initBuilder.addStringConstant("n");
    for (int64_t v : {1, 2, 3}) {
        initBuilder.emitU16(OpCode::GetLocal, 0);
        uint16_t constIdx = initBuilder.addIntConstant(v);
        initBuilder.emitU16(OpCode::Constant, constIdx);
        initBuilder.emitU16(OpCode::SetPropertySelf, nName1);
        initBuilder.emit(OpCode::Pop);
    }
    ObjFunction* initFn = initBuilder.build(0, "init");

    ChunkBuilder getNBuilder(vm);
    uint16_t nName2 = getNBuilder.addStringConstant("n");
    getNBuilder.emitU16(OpCode::GetLocal, 0);
    getNBuilder.emitU16(OpCode::GetPropertySelf, nName2);
    getNBuilder.emit(OpCode::Return);
    ObjFunction* getNFn = getNBuilder.build(0, "getN");

    ChunkBuilder b(vm);
    uint16_t counterName = b.addStringConstant("Counter");
    uint16_t initIndex = b.addFunctionConstant(initFn);
    uint16_t getNIndex = b.addFunctionConstant(getNFn);
    uint16_t initName = b.addStringConstant("init");
    uint16_t getNName = b.addStringConstant("getN");
    uint16_t nDeclName = b.addStringConstant("n");
    uint16_t cName = b.addStringConstant("c");
    uint16_t resultName = b.addStringConstant("result");

    b.emitU16(OpCode::Class, counterName);
    b.emitU16(OpCode::DefineGlobal, counterName);
    b.emitU16(OpCode::GetGlobal, counterName);
    b.emitU16(OpCode::PropertyPrivateMut, nDeclName);
    b.emitClosure(initIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emitClosure(getNIndex, {});
    b.emitU16(OpCode::MethodPub, getNName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, counterName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, cName);

    b.emitU16(OpCode::GetGlobal, cName);
    b.emitU16(OpCode::GetProperty, getNName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, resultName);

    vm.interpret(b.build());

    REQUIRE(asSmallInt(*vm.getGlobal("result")) == 3);
}

TEST_CASE("reading an immutable property before it's ever been assigned is a runtime error", "[vm][classes][visibility]") {
    // class Point { var x; getX() { return self.x; } } -- init never runs (no args given a no-init class).
    Vm vm;
    ChunkBuilder getXBuilder(vm);
    uint16_t xName1 = getXBuilder.addStringConstant("x");
    getXBuilder.emitU16(OpCode::GetLocal, 0);
    getXBuilder.emitU16(OpCode::GetPropertySelf, xName1);
    getXBuilder.emit(OpCode::Return);
    ObjFunction* getXFn = getXBuilder.build(0, "getX");

    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t getXIndex = b.addFunctionConstant(getXFn);
    uint16_t getXName = b.addStringConstant("getX");
    uint16_t xDeclName = b.addStringConstant("x");
    uint16_t pName = b.addStringConstant("p");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::PropertyPrivate, xDeclName);
    b.emitClosure(getXIndex, {});
    b.emitU16(OpCode::MethodPub, getXName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    b.emitU16(OpCode::GetGlobal, pName);
    b.emitU16(OpCode::GetProperty, getXName);
    b.emitU16(OpCode::Call, 0);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("calling a private (no pub) method from outside its class is a runtime error", "[vm][classes][visibility]") {
    Vm vm;
    ChunkBuilder secretBuilder(vm);
    ObjFunction* secretFn = secretBuilder.build(0, "secret");

    ChunkBuilder b(vm);
    uint16_t vaultName = b.addStringConstant("Vault");
    uint16_t secretIndex = b.addFunctionConstant(secretFn);
    uint16_t secretName = b.addStringConstant("secret");
    uint16_t vName = b.addStringConstant("v");

    b.emitU16(OpCode::Class, vaultName);
    b.emitU16(OpCode::DefineGlobal, vaultName);
    b.emitU16(OpCode::GetGlobal, vaultName);
    b.emitClosure(secretIndex, {});
    b.emitU16(OpCode::Method, secretName);  // private
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, vaultName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, vName);

    b.emitU16(OpCode::GetGlobal, vName);
    b.emitU16(OpCode::GetProperty, secretName);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("init is always externally callable regardless of any pub annotation", "[vm][classes][visibility]") {
    Vm vm;
    ChunkBuilder initBuilder(vm);
    ObjFunction* initFn = initBuilder.build(0, "init");  // no pub -- construction must still work

    ChunkBuilder b(vm);
    uint16_t pointName = b.addStringConstant("Point");
    uint16_t initIndex = b.addFunctionConstant(initFn);
    uint16_t initName = b.addStringConstant("init");
    uint16_t pName = b.addStringConstant("p");

    b.emitU16(OpCode::Class, pointName);
    b.emitU16(OpCode::DefineGlobal, pointName);
    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitClosure(initIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, pointName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, pName);

    vm.interpret(b.build());

    REQUIRE(isObj(*vm.getGlobal("p")));
}

TEST_CASE("a subclass method can internally reach a superclass's private property and method (protected-like)",
          "[vm][classes][visibility]") {
    // class Animal { var name; init(n) { self.name = n; } secret() { return "shh"; } }
    // class Dog extends Animal { pub reveal() { return concat [self.name, self.secret()]; } }
    Vm vm;

    ChunkBuilder animalInitBuilder(vm);
    uint16_t nameName1 = animalInitBuilder.addStringConstant("name");
    animalInitBuilder.emitU16(OpCode::GetLocal, 0);
    animalInitBuilder.emitU16(OpCode::GetLocal, 1);
    animalInitBuilder.emitU16(OpCode::SetPropertySelf, nameName1);
    animalInitBuilder.emit(OpCode::Pop);
    ObjFunction* animalInitFn = animalInitBuilder.build(1, "init");

    ChunkBuilder secretBuilder(vm);
    uint16_t shh = secretBuilder.addStringConstant("shh");
    secretBuilder.emitU16(OpCode::Constant, shh);
    secretBuilder.emit(OpCode::Return);
    ObjFunction* secretFn = secretBuilder.build(0, "secret");  // private

    // Dog.reveal(): self.name (inherited private property) + self.secret()
    // (inherited private method) -- both reached via `self`, so both are
    // internal access regardless of where up the hierarchy they were
    // declared (the now-resolved open question 3's protected-like
    // reading).
    ChunkBuilder revealBuilder(vm);
    uint16_t nameName2 = revealBuilder.addStringConstant("name");
    uint16_t secretName2 = revealBuilder.addStringConstant("secret");
    revealBuilder.emitU16(OpCode::GetLocal, 0);
    revealBuilder.emitU16(OpCode::GetPropertySelf, nameName2);
    revealBuilder.emitU16(OpCode::GetLocal, 0);
    revealBuilder.emitU16(OpCode::GetPropertySelf, secretName2);
    revealBuilder.emitU16(OpCode::Call, 0);
    revealBuilder.emit(OpCode::Return);
    ObjFunction* revealFn = revealBuilder.build(0, "reveal");

    ChunkBuilder b(vm);
    uint16_t animalName = b.addStringConstant("Animal");
    uint16_t dogName = b.addStringConstant("Dog");
    uint16_t nameDeclName = b.addStringConstant("name");
    uint16_t initName = b.addStringConstant("init");
    uint16_t secretName = b.addStringConstant("secret");
    uint16_t revealName = b.addStringConstant("reveal");
    uint16_t animalInitIndex = b.addFunctionConstant(animalInitFn);
    uint16_t secretIndex = b.addFunctionConstant(secretFn);
    uint16_t revealIndex = b.addFunctionConstant(revealFn);
    uint16_t rex = b.addStringConstant("Rex");
    uint16_t dName = b.addStringConstant("d");

    b.emitU16(OpCode::Class, animalName);
    b.emitU16(OpCode::DefineGlobal, animalName);
    b.emitU16(OpCode::GetGlobal, animalName);
    b.emitU16(OpCode::PropertyPrivate, nameDeclName);
    b.emitClosure(animalInitIndex, {});
    b.emitU16(OpCode::Method, initName);
    b.emitClosure(secretIndex, {});
    b.emitU16(OpCode::Method, secretName);  // private
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::Class, dogName);
    b.emitU16(OpCode::DefineGlobal, dogName);
    b.emitU16(OpCode::GetGlobal, animalName);
    b.emitU16(OpCode::GetGlobal, dogName);
    b.emit(OpCode::Inherit);
    b.emitClosure(revealIndex, {});
    b.emitU16(OpCode::MethodPub, revealName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, dogName);
    b.emitU16(OpCode::Constant, rex);
    b.emitU16(OpCode::Call, 1);
    b.emitU16(OpCode::DefineGlobal, dName);

    b.emitU16(OpCode::GetGlobal, dName);
    b.emitU16(OpCode::GetProperty, revealName);
    b.emitU16(OpCode::Call, 0);

    // Doesn't need concat -- just confirms both internal reaches succeeded
    // without throwing (RuntimeError would propagate out of interpret()).
    REQUIRE_NOTHROW(vm.interpret(b.build()));
}

// docs/PLAN-0.2.md Phase 8: `with`-mixin composition (decision 12, this
// version's simplified/non-C3 approximation of open question 2).

TEST_CASE("a with-mixin's methods and properties are copied into the class", "[vm][classes][mixins]") {
    // class Named { var label pub mut; pub greet() { return "hi"; } }
    // class Robot with Named {}
    // var r = Robot(); r.label = "R2"; result = r.greet() + r.label
    Vm vm;
    ChunkBuilder greetBuilder(vm);
    uint16_t hi = greetBuilder.addStringConstant("hi");
    greetBuilder.emitU16(OpCode::Constant, hi);
    greetBuilder.emit(OpCode::Return);
    ObjFunction* greetFn = greetBuilder.build(0, "greet");

    ChunkBuilder b(vm);
    uint16_t namedName = b.addStringConstant("Named");
    uint16_t robotName = b.addStringConstant("Robot");
    uint16_t labelName = b.addStringConstant("label");
    uint16_t greetName = b.addStringConstant("greet");
    uint16_t greetIndex = b.addFunctionConstant(greetFn);
    uint16_t rName = b.addStringConstant("r");
    uint16_t hiValue = b.addStringConstant("hello");

    b.emitU16(OpCode::Class, namedName);
    b.emitU16(OpCode::DefineGlobal, namedName);
    b.emitU16(OpCode::GetGlobal, namedName);
    b.emitU16(OpCode::PropertyPubMut, labelName);
    b.emitClosure(greetIndex, {});
    b.emitU16(OpCode::MethodPub, greetName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::Class, robotName);
    b.emitU16(OpCode::DefineGlobal, robotName);
    b.emitU16(OpCode::GetGlobal, robotName);  // temp re-fetch
    b.emitU16(OpCode::GetGlobal, namedName);  // the mixin
    b.emit(OpCode::Mixin);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::GetGlobal, robotName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, rName);

    // r.label = "hello" (external -- pub mut, so allowed)
    b.emitU16(OpCode::GetGlobal, rName);
    b.emitU16(OpCode::Constant, hiValue);
    b.emitU16(OpCode::SetProperty, labelName);
    b.emit(OpCode::Pop);

    // r.greet() (external -- pub, so allowed)
    b.emitU16(OpCode::GetGlobal, rName);
    b.emitU16(OpCode::GetProperty, greetName);
    b.emitU16(OpCode::Call, 0);
    uint16_t resultName = b.addStringConstant("result");
    b.emitU16(OpCode::DefineGlobal, resultName);

    vm.interpret(b.build());

    REQUIRE(asString(*vm.getGlobal("result")) == "hi");
}

TEST_CASE("mixing in a non-class value is a runtime error", "[vm][classes][mixins]") {
    Vm vm;
    ChunkBuilder b(vm);
    uint16_t notAClassName = b.addStringConstant("NotAClass");
    uint16_t five = b.addIntConstant(5);
    uint16_t robotName = b.addStringConstant("Robot");

    b.emitU16(OpCode::Constant, five);
    b.emitU16(OpCode::DefineGlobal, notAClassName);

    b.emitU16(OpCode::Class, robotName);
    b.emitU16(OpCode::DefineGlobal, robotName);
    b.emitU16(OpCode::GetGlobal, robotName);
    b.emitU16(OpCode::GetGlobal, notAClassName);
    b.emit(OpCode::Mixin);

    REQUIRE_THROWS_AS(vm.interpret(b.build()), RuntimeError);
}

TEST_CASE("extends and with compose together: superclass and mixin methods both land on the subclass",
          "[vm][classes][mixins]") {
    // class Base { pub baseM() { return "base"; } }
    // class Mix { pub mixM() { return "mix"; } }
    // class Combo extends Base with Mix {}
    Vm vm;
    ChunkBuilder baseMBuilder(vm);
    uint16_t baseStr = baseMBuilder.addStringConstant("base");
    baseMBuilder.emitU16(OpCode::Constant, baseStr);
    baseMBuilder.emit(OpCode::Return);
    ObjFunction* baseMFn = baseMBuilder.build(0, "baseM");

    ChunkBuilder mixMBuilder(vm);
    uint16_t mixStr = mixMBuilder.addStringConstant("mix");
    mixMBuilder.emitU16(OpCode::Constant, mixStr);
    mixMBuilder.emit(OpCode::Return);
    ObjFunction* mixMFn = mixMBuilder.build(0, "mixM");

    ChunkBuilder b(vm);
    uint16_t baseName = b.addStringConstant("Base");
    uint16_t mixName = b.addStringConstant("Mix");
    uint16_t comboName = b.addStringConstant("Combo");
    uint16_t baseMName = b.addStringConstant("baseM");
    uint16_t mixMName = b.addStringConstant("mixM");
    uint16_t baseMIndex = b.addFunctionConstant(baseMFn);
    uint16_t mixMIndex = b.addFunctionConstant(mixMFn);
    uint16_t cName = b.addStringConstant("c");

    b.emitU16(OpCode::Class, baseName);
    b.emitU16(OpCode::DefineGlobal, baseName);
    b.emitU16(OpCode::GetGlobal, baseName);
    b.emitClosure(baseMIndex, {});
    b.emitU16(OpCode::MethodPub, baseMName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::Class, mixName);
    b.emitU16(OpCode::DefineGlobal, mixName);
    b.emitU16(OpCode::GetGlobal, mixName);
    b.emitClosure(mixMIndex, {});
    b.emitU16(OpCode::MethodPub, mixMName);
    b.emit(OpCode::Pop);

    b.emitU16(OpCode::Class, comboName);
    b.emitU16(OpCode::DefineGlobal, comboName);
    b.emitU16(OpCode::GetGlobal, baseName);  // superclass
    b.emitU16(OpCode::GetGlobal, comboName); // temp re-fetch
    b.emit(OpCode::Inherit);
    b.emitU16(OpCode::GetGlobal, mixName);   // the mixin
    b.emit(OpCode::Mixin);
    b.emit(OpCode::Pop);
    b.emit(OpCode::Pop);  // discards the synthetic `super` local (Combo has a superclass)

    b.emitU16(OpCode::GetGlobal, comboName);
    b.emitU16(OpCode::Call, 0);
    b.emitU16(OpCode::DefineGlobal, cName);

    b.emitU16(OpCode::GetGlobal, cName);
    b.emitU16(OpCode::GetProperty, baseMName);
    b.emitU16(OpCode::Call, 0);
    uint16_t r1Name = b.addStringConstant("r1");
    b.emitU16(OpCode::DefineGlobal, r1Name);

    b.emitU16(OpCode::GetGlobal, cName);
    b.emitU16(OpCode::GetProperty, mixMName);
    b.emitU16(OpCode::Call, 0);
    uint16_t r2Name = b.addStringConstant("r2");
    b.emitU16(OpCode::DefineGlobal, r2Name);

    vm.interpret(b.build());

    REQUIRE(asString(*vm.getGlobal("r1")) == "base");
    REQUIRE(asString(*vm.getGlobal("r2")) == "mix");
}
