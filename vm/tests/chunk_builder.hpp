#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "bytecode.hpp"
#include "object.hpp"
#include "value.hpp"
#include "vm.hpp"

namespace iqalox::testing {

// A tiny test-only "assembler" for hand-building `Chunk`s -- mirrors what
// `compiler/src/Codegen.fs` emits, but built directly rather than compiled
// from source, per this plan's own testing strategy (docs/PLAN-0.1.md §7:
// "vm/'s tests can hand-assemble small bytecode fixtures directly, no F#
// frontend needed yet"). Shared by test_vm.cpp and test_classes.cpp.
class ChunkBuilder {
public:
    explicit ChunkBuilder(Vm& vm) : vm_(vm) {}

    uint16_t addIntConstant(int64_t n) {
        constants_.push_back(smallIntValue(n));
        return static_cast<uint16_t>(constants_.size() - 1);
    }

    uint16_t addFloatConstant(double n) {
        constants_.push_back(doubleValue(n));
        return static_cast<uint16_t>(constants_.size() - 1);
    }

    // Any already-built Value, e.g. a big integer from numeric::integerValue.
    uint16_t addValueConstant(const Value& v) {
        constants_.push_back(v);
        return static_cast<uint16_t>(constants_.size() - 1);
    }

    uint16_t addStringConstant(const std::string& s) {
        constants_.push_back(objValue(vm_.allocate<ObjString>(s)));
        return static_cast<uint16_t>(constants_.size() - 1);
    }

    uint16_t addFunctionConstant(ObjFunction* fn) {
        constants_.push_back(objValue(fn));
        return static_cast<uint16_t>(constants_.size() - 1);
    }

    void emit(bytecode::OpCode op) { code_.push_back(static_cast<uint8_t>(op)); }

    void emitU16(bytecode::OpCode op, uint16_t operand) {
        code_.push_back(static_cast<uint8_t>(op));
        code_.push_back(static_cast<uint8_t>(operand & 0xFF));
        code_.push_back(static_cast<uint8_t>((operand >> 8) & 0xFF));
    }

    void emitClosure(uint16_t functionIndex, const std::vector<UpvalueDescriptor>& upvalues) {
        code_.push_back(static_cast<uint8_t>(bytecode::OpCode::Closure));
        code_.push_back(static_cast<uint8_t>(functionIndex & 0xFF));
        code_.push_back(static_cast<uint8_t>((functionIndex >> 8) & 0xFF));
        auto count = static_cast<uint16_t>(upvalues.size());
        code_.push_back(static_cast<uint8_t>(count & 0xFF));
        code_.push_back(static_cast<uint8_t>((count >> 8) & 0xFF));
        for (const auto& uv : upvalues) {
            code_.push_back(uv.fromEnclosingLocal ? 1 : 0);
            code_.push_back(static_cast<uint8_t>(uv.index & 0xFF));
            code_.push_back(static_cast<uint8_t>((uv.index >> 8) & 0xFF));
        }
    }

    // Emits a jump with a placeholder target; returns a handle `patch`
    // backfills once the real target is known.
    size_t emitJump(bytecode::OpCode op) {
        code_.push_back(static_cast<uint8_t>(op));
        size_t operandOffset = code_.size();
        code_.push_back(0);
        code_.push_back(0);
        return operandOffset;
    }

    void patch(size_t operandOffset) { patchTo(operandOffset, here()); }

    void patchTo(size_t operandOffset, size_t target) {
        code_[operandOffset] = static_cast<uint8_t>(target & 0xFF);
        code_[operandOffset + 1] = static_cast<uint8_t>((target >> 8) & 0xFF);
    }

    size_t here() const { return code_.size(); }

    void emitJumpTo(bytecode::OpCode op, size_t target) {
        code_.push_back(static_cast<uint8_t>(op));
        code_.push_back(static_cast<uint8_t>(target & 0xFF));
        code_.push_back(static_cast<uint8_t>((target >> 8) & 0xFF));
    }

    // Always appends a trailing `Nil; Return`, matching
    // `Codegen.fs`'s `CompileFunctionValue`/`CompileProgram`, which do the
    // same unconditionally (even after a body's own explicit `return`) --
    // every real chunk ends this way, so every hand-assembled test chunk
    // should too, rather than each test having to remember it.
    ObjFunction* build(int arity = 0, const std::string& name = "script") {
        emit(bytecode::OpCode::Nil);
        emit(bytecode::OpCode::Return);

        auto* fn = vm_.allocate<ObjFunction>();
        fn->name = name;
        fn->arity = arity;
        fn->chunk.code = code_;
        fn->chunk.constants = constants_;
        return fn;
    }

private:
    Vm& vm_;
    std::vector<uint8_t> code_;
    std::vector<Value> constants_;
};

}  // namespace iqalox::testing
