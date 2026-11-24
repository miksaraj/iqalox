#include "utf8.hpp"

namespace iqalox::utf8 {

bool isValid(std::string_view text) {
    size_t i = 0;
    const size_t n = text.size();
    while (i < n) {
        auto byte = static_cast<unsigned char>(text[i]);
        if (byte < 0x80) {
            ++i;
            continue;
        }
        size_t extra;
        uint32_t codePoint;
        uint32_t minimum;
        if ((byte & 0xE0) == 0xC0) {
            extra = 1, codePoint = byte & 0x1F, minimum = 0x80;
        } else if ((byte & 0xF0) == 0xE0) {
            extra = 2, codePoint = byte & 0x0F, minimum = 0x800;
        } else if ((byte & 0xF8) == 0xF0) {
            extra = 3, codePoint = byte & 0x07, minimum = 0x10000;
        } else {
            return false;
        }
        if (n - i <= extra) return false;
        for (size_t k = 1; k <= extra; ++k) {
            auto next = static_cast<unsigned char>(text[i + k]);
            if (!isContinuation(next)) return false;
            codePoint = (codePoint << 6) | (next & 0x3F);
        }
        if (codePoint < minimum || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF)) return false;
        i += extra + 1;
    }
    return true;
}

}  // namespace iqalox::utf8
