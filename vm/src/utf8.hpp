#pragma once

#include <cstddef>
#include <cstdint>
#include <string_view>

// UTF-8 helpers for the string model (docs/PLAN-0.4.md Phase 4). Every
// `ObjString` holds valid UTF-8: the loader checks string constants with
// `isValid`, and every operation that builds a string cuts only at
// code-point boundaries.
namespace iqalox::utf8 {

inline bool isContinuation(unsigned char byte) { return (byte & 0xC0) == 0x80; }

// The number of code points in valid UTF-8: every byte that isn't a
// continuation byte starts one.
inline size_t countCodePoints(std::string_view text) {
    size_t count = 0;
    for (unsigned char byte : text) count += isContinuation(byte) ? 0 : 1;
    return count;
}

// Strict validation: no overlong forms, no surrogates, nothing above
// U+10FFFF, no truncated sequences.
bool isValid(std::string_view text);

}  // namespace iqalox::utf8
