#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <random>
#include <vector>

#include "bytecode.hpp"
#include "vm.hpp"

using namespace iqalox;

namespace {

void pushU16(std::vector<uint8_t>& bytes, uint16_t value) {
    bytes.push_back(static_cast<uint8_t>(value & 0xFF));
    bytes.push_back(static_cast<uint8_t>((value >> 8) & 0xFF));
}

void pushU32(std::vector<uint8_t>& bytes, uint32_t value) {
    bytes.push_back(static_cast<uint8_t>(value & 0xFF));
    bytes.push_back(static_cast<uint8_t>((value >> 8) & 0xFF));
    bytes.push_back(static_cast<uint8_t>((value >> 16) & 0xFF));
    bytes.push_back(static_cast<uint8_t>((value >> 24) & 0xFF));
}

void pushF64(std::vector<uint8_t>& bytes, double value) {
    uint8_t raw[8];
    std::memcpy(raw, &value, 8);
    bytes.insert(bytes.end(), raw, raw + 8);
}

void pushString(std::vector<uint8_t>& bytes, const std::string& s) {
    pushU32(bytes, static_cast<uint32_t>(s.size()));
    bytes.insert(bytes.end(), s.begin(), s.end());
}

// Writes `bytes` to a fresh temp file and returns its path -- each test gets
// its own file (a random suffix) so parallel/interleaved test runs can't
// collide on the same path.
std::filesystem::path writeTempFile(const std::vector<uint8_t>& bytes) {
    static std::mt19937 rng(std::random_device{}());
    auto path = std::filesystem::temp_directory_path() / ("iqaloxvm_test_" + std::to_string(rng()) + ".iqbc");
    std::ofstream file(path, std::ios::binary);
    file.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    file.close();
    return path;
}

std::vector<uint8_t> header() {
    // magic, version, then an empty (u16 count = 0) source-file table
    return {'I', 'Q', 'B', 'C', bytecode::kFormatVersion, 0, 0};
}

struct TempFile {
    std::filesystem::path path;
    explicit TempFile(const std::vector<uint8_t>& bytes) : path(writeTempFile(bytes)) {}
    ~TempFile() { std::filesystem::remove(path); }
};

}  // namespace

TEST_CASE("an empty top-level chunk loads as a zero-arity script function", "[bytecode]") {
    auto bytes = header();
    pushU32(bytes, 0);  // constant_count
    pushU32(bytes, 0);  // code_length
    TempFile file(bytes);

    Vm vm;
    ObjFunction* script = bytecode::load(file.path, vm);

    REQUIRE(script->arity == 0);
    REQUIRE(script->chunk.constants.empty());
    REQUIRE(script->chunk.code.empty());
}

TEST_CASE("number and string constants decode to their runtime Values", "[bytecode]") {
    auto bytes = header();
    pushU32(bytes, 2);  // constant_count
    bytes.push_back(0x00);
    pushF64(bytes, 1.5);
    bytes.push_back(0x01);
    pushString(bytes, "hi");

    std::vector<uint8_t> code{0x01, 0, 0, 0x01, 1, 0, 0x23};  // CONSTANT 0, CONSTANT 1, RETURN
    pushU32(bytes, static_cast<uint32_t>(code.size()));
    bytes.insert(bytes.end(), code.begin(), code.end());
    for (size_t i = 0; i < code.size(); ++i) {
        pushU16(bytes, 1);  // one line entry per code byte
    }
    for (size_t i = 0; i < code.size(); ++i) {
        pushU16(bytes, 0);  // one file entry per code byte
    }
    TempFile file(bytes);

    Vm vm;
    ObjFunction* script = bytecode::load(file.path, vm);

    REQUIRE(script->chunk.constants.size() == 2);
    REQUIRE(isNumber(script->chunk.constants[0]));
    REQUIRE(asNumber(script->chunk.constants[0]) == 1.5);
    REQUIRE(isObj(script->chunk.constants[1]));
    REQUIRE(static_cast<ObjString*>(asObj(script->chunk.constants[1]))->value == "hi");
    REQUIRE(script->chunk.code == code);
    REQUIRE(script->chunk.lines.size() == code.size());
}

TEST_CASE("a nested FunctionConstant decodes its name/arity/locals/upvalues and its own chunk", "[bytecode]") {
    auto bytes = header();
    pushU32(bytes, 1);  // constant_count
    bytes.push_back(0x02);  // function tag
    pushString(bytes, "f");
    bytes.push_back(2);    // arity
    pushU16(bytes, 3);     // local_count
    pushU16(bytes, 1);     // upvalue_count
    bytes.push_back(1);    // upvalue 0: from enclosing local
    pushU16(bytes, 0);     // upvalue 0: index
    // nested <chunk>: no constants, one RETURN instruction
    pushU32(bytes, 0);
    pushU32(bytes, 1);
    bytes.push_back(0x23);
    pushU16(bytes, 5);  // RETURN's line
    pushU16(bytes, 0);  // RETURN's file

    pushU32(bytes, 0);  // top-level code_length
    TempFile file(bytes);

    Vm vm;
    ObjFunction* script = bytecode::load(file.path, vm);

    REQUIRE(script->chunk.constants.size() == 1);
    auto* function = static_cast<ObjFunction*>(asObj(script->chunk.constants[0]));
    REQUIRE(function->name == "f");
    REQUIRE(function->arity == 2);
    REQUIRE(function->localCount == 3);
    REQUIRE(function->upvalues.size() == 1);
    REQUIRE(function->upvalues[0].fromEnclosingLocal == true);
    REQUIRE(function->upvalues[0].index == 0);
    REQUIRE(function->chunk.constants.empty());
    REQUIRE(function->chunk.code == std::vector<uint8_t>{0x23});
    REQUIRE(function->chunk.lines == std::vector<uint16_t>{5});
}

TEST_CASE("a bad magic number is rejected", "[bytecode]") {
    auto bytes = std::vector<uint8_t>{'N', 'O', 'P', 'E', 0x01};
    pushU32(bytes, 0);
    pushU32(bytes, 0);
    TempFile file(bytes);

    Vm vm;
    REQUIRE_THROWS_AS(bytecode::load(file.path, vm), std::runtime_error);
}

TEST_CASE("an unsupported format version is rejected", "[bytecode]") {
    auto bytes = std::vector<uint8_t>{'I', 'Q', 'B', 'C', 99};
    pushU32(bytes, 0);
    pushU32(bytes, 0);
    TempFile file(bytes);

    Vm vm;
    REQUIRE_THROWS_AS(bytecode::load(file.path, vm), std::runtime_error);
}

TEST_CASE("a truncated file is rejected rather than reading out of bounds", "[bytecode]") {
    auto bytes = header();
    pushU32(bytes, 1);  // claims one constant, but the file ends here
    TempFile file(bytes);

    Vm vm;
    REQUIRE_THROWS_AS(bytecode::load(file.path, vm), std::runtime_error);
}

TEST_CASE("an unknown constant tag is rejected", "[bytecode]") {
    auto bytes = header();
    pushU32(bytes, 1);
    bytes.push_back(0x77);  // not a valid tag
    TempFile file(bytes);

    Vm vm;
    REQUIRE_THROWS_AS(bytecode::load(file.path, vm), std::runtime_error);
}

TEST_CASE("the source-file table and a chunk's per-byte file indices load", "[bytecode]") {
    std::vector<uint8_t> bytes{'I', 'Q', 'B', 'C', bytecode::kFormatVersion};
    pushU16(bytes, 2);  // file_count
    pushString(bytes, "main.iqx");
    pushString(bytes, "math/vector.iqx");
    pushU32(bytes, 0);  // constant_count
    std::vector<uint8_t> code{0x02, 0x23};  // NIL, RETURN
    pushU32(bytes, static_cast<uint32_t>(code.size()));
    bytes.insert(bytes.end(), code.begin(), code.end());
    pushU16(bytes, 3);  // lines
    pushU16(bytes, 4);
    pushU16(bytes, 0);  // files: NIL in file 0, RETURN in file 1
    pushU16(bytes, 1);
    TempFile file(bytes);

    Vm vm;
    ObjFunction* script = bytecode::load(file.path, vm);

    REQUIRE(vm.sourceFiles == std::vector<std::string>{"main.iqx", "math/vector.iqx"});
    REQUIRE(script->chunk.files == std::vector<uint16_t>{0, 1});
}
