#include "bytecode.hpp"

#include <array>
#include <cstring>
#include <fstream>
#include <stdexcept>

#include "vm.hpp"

namespace iqalox::bytecode {

namespace {

constexpr std::array<char, 4> kMagic{'I', 'Q', 'B', 'C'};

constexpr uint8_t kNumberTag = 0x00;
constexpr uint8_t kStringTag = 0x01;
constexpr uint8_t kFunctionTag = 0x02;

class Reader {
public:
    explicit Reader(std::vector<uint8_t> data) : data_(std::move(data)) {}

    uint8_t readU8() {
        require(1);
        return data_[pos_++];
    }

    uint16_t readU16() {
        require(2);
        uint16_t value = static_cast<uint16_t>(data_[pos_]) | (static_cast<uint16_t>(data_[pos_ + 1]) << 8);
        pos_ += 2;
        return value;
    }

    uint32_t readU32() {
        require(4);
        uint32_t value = static_cast<uint32_t>(data_[pos_]) | (static_cast<uint32_t>(data_[pos_ + 1]) << 8) |
                          (static_cast<uint32_t>(data_[pos_ + 2]) << 16) |
                          (static_cast<uint32_t>(data_[pos_ + 3]) << 24);
        pos_ += 4;
        return value;
    }

    double readF64() {
        require(8);
        double value;
        std::memcpy(&value, &data_[pos_], 8);
        pos_ += 8;
        return value;
    }

    std::string readString(uint32_t length) {
        require(length);
        std::string value(reinterpret_cast<const char*>(&data_[pos_]), length);
        pos_ += length;
        return value;
    }

    std::vector<uint8_t> readBytes(uint32_t length) {
        require(length);
        std::vector<uint8_t> value(data_.begin() + static_cast<long>(pos_),
                                    data_.begin() + static_cast<long>(pos_ + length));
        pos_ += length;
        return value;
    }

private:
    void require(size_t count) const {
        if (pos_ + count > data_.size()) {
            throw std::runtime_error("iqalox bytecode: unexpected end of file");
        }
    }

    std::vector<uint8_t> data_;
    size_t pos_ = 0;
};

Chunk readChunk(Reader& reader, Vm& vm) {
    Chunk chunk;

    uint32_t constantCount = reader.readU32();
    chunk.constants.reserve(constantCount);
    for (uint32_t i = 0; i < constantCount; ++i) {
        uint8_t tag = reader.readU8();
        switch (tag) {
            case kNumberTag:
                chunk.constants.push_back(numberValue(reader.readF64()));
                break;
            case kStringTag: {
                uint32_t length = reader.readU32();
                auto* str = vm.allocate<ObjString>(reader.readString(length));
                chunk.constants.push_back(objValue(str));
                break;
            }
            case kFunctionTag: {
                uint32_t nameLength = reader.readU32();
                std::string name = reader.readString(nameLength);
                uint8_t arity = reader.readU8();
                uint16_t localCount = reader.readU16();
                uint16_t upvalueCount = reader.readU16();

                auto* function = vm.allocate<ObjFunction>();
                function->name = std::move(name);
                function->arity = arity;
                function->localCount = localCount;
                function->upvalues.reserve(upvalueCount);
                for (uint16_t u = 0; u < upvalueCount; ++u) {
                    bool fromEnclosingLocal = reader.readU8() != 0;
                    uint16_t index = reader.readU16();
                    function->upvalues.push_back({fromEnclosingLocal, index});
                }
                function->chunk = readChunk(reader, vm);
                chunk.constants.push_back(objValue(function));
                break;
            }
            default:
                throw std::runtime_error("iqalox bytecode: unknown constant tag " + std::to_string(tag));
        }
    }

    uint32_t codeLength = reader.readU32();
    chunk.code = reader.readBytes(codeLength);

    chunk.lines.reserve(codeLength);
    for (uint32_t i = 0; i < codeLength; ++i) {
        chunk.lines.push_back(reader.readU16());
    }

    chunk.files.reserve(codeLength);
    for (uint32_t i = 0; i < codeLength; ++i) {
        chunk.files.push_back(reader.readU16());
    }

    return chunk;
}

}  // namespace

ObjFunction* load(const std::filesystem::path& path, Vm& vm) {
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        throw std::runtime_error("iqalox bytecode: cannot open '" + path.string() + "'");
    }

    std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
    Reader reader(std::move(bytes));

    for (char expected : kMagic) {
        if (static_cast<char>(reader.readU8()) != expected) {
            throw std::runtime_error("iqalox bytecode: bad magic number in '" + path.string() + "'");
        }
    }

    uint8_t version = reader.readU8();
    if (version != kFormatVersion) {
        throw std::runtime_error("iqalox bytecode: unsupported format version " + std::to_string(version));
    }

    // The program-wide source-file table (format v3), referred to by index
    // from every chunk's `files` -- kept on the Vm so a fault anywhere can
    // name its file.
    uint16_t fileCount = reader.readU16();
    vm.sourceFiles.clear();
    vm.sourceFiles.reserve(fileCount);
    for (uint16_t i = 0; i < fileCount; ++i) {
        uint32_t nameLength = reader.readU32();
        vm.sourceFiles.push_back(reader.readString(nameLength));
    }

    auto* script = vm.allocate<ObjFunction>();
    script->name = "script";
    script->arity = 0;
    script->chunk = readChunk(reader, vm);
    return script;
}

}  // namespace iqalox::bytecode
