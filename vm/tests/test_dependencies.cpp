// Phase 0 of docs/PLAN-0.4.md: proves the two new third-party dependencies
// build, link and behave as later phases need, on every platform CI builds
// on. Nothing here is Iqalox-level behaviour yet. These are direct calls into
// mini-gmp (Phase 1's big integers) and ICU (Phases 4, 6 and 7: case mapping,
// character classes, regex), under the exact configuration
// vm/cmake/IqaloxIcu.cmake builds ICU with (stub data, no data file).

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <memory>
#include <string>

#include <unicode/uchar.h>
#include <unicode/uregex.h>
#include <unicode/ustring.h>
#include <unicode/utext.h>

#include "dependencies.hpp"
#include "mini-gmp.h"

namespace {

// --- mini-gmp ----------------------------------------------------------------

struct Mpz {
    mpz_t value;
    Mpz() { mpz_init(value); }
    explicit Mpz(long n) { mpz_init_set_si(value, n); }
    explicit Mpz(const char* decimal) { mpz_init_set_str(value, decimal, 10); }
    ~Mpz() { mpz_clear(value); }
    Mpz(const Mpz&) = delete;
    Mpz& operator=(const Mpz&) = delete;

    std::string str() const {
        std::unique_ptr<char, void (*)(void*)> text(mpz_get_str(nullptr, 10, value), [](void* p) {
            void (*freeFunc)(void*, size_t);
            mp_get_memory_functions(nullptr, nullptr, &freeFunc);
            freeFunc(p, 0);
        });
        return text.get();
    }
};

// --- ICU -----------------------------------------------------------------------

std::u16string toUtf16(const std::string& utf8) {
    UErrorCode status = U_ZERO_ERROR;
    int32_t length = 0;
    u_strFromUTF8(nullptr, 0, &length, utf8.data(), static_cast<int32_t>(utf8.size()), &status);
    std::u16string result(static_cast<size_t>(length), u'\0');
    status = U_ZERO_ERROR;
    u_strFromUTF8(result.data(), length, nullptr, utf8.data(), static_cast<int32_t>(utf8.size()), &status);
    REQUIRE(U_SUCCESS(status));
    return result;
}

std::string toUtf8(const std::u16string& utf16) {
    UErrorCode status = U_ZERO_ERROR;
    int32_t length = 0;
    u_strToUTF8(nullptr, 0, &length, utf16.data(), static_cast<int32_t>(utf16.size()), &status);
    std::string result(static_cast<size_t>(length), '\0');
    status = U_ZERO_ERROR;
    u_strToUTF8(result.data(), length, nullptr, utf16.data(), static_cast<int32_t>(utf16.size()), &status);
    REQUIRE(U_SUCCESS(status));
    return result;
}

// Full (one-to-many) case mapping under the root locale, so the result never
// depends on the machine's default locale (Turkish dotless i and the like).
enum class Case { Upper, Lower };
std::string mapCase(Case which, const std::string& utf8) {
    std::u16string source = toUtf16(utf8);
    std::u16string result(source.size() * 3 + 1, u'\0');
    UErrorCode status = U_ZERO_ERROR;
    const auto map = which == Case::Upper ? u_strToUpper : u_strToLower;
    int32_t length = map(result.data(), static_cast<int32_t>(result.size()), source.data(),
                         static_cast<int32_t>(source.size()), "root", &status);
    REQUIRE(U_SUCCESS(status));
    result.resize(static_cast<size_t>(length));
    return toUtf8(result);
}

// Number of code points in the first `bytes` bytes of a UTF-8 string -- how
// ICU's native (UTF-8 byte) match positions become Iqalox string indices.
int64_t codePointsBefore(const std::string& utf8, int64_t bytes) {
    int64_t count = 0;
    for (int64_t i = 0; i < bytes; ++i) {
        if ((static_cast<unsigned char>(utf8[static_cast<size_t>(i)]) & 0xC0) != 0x80) ++count;
    }
    return count;
}

struct RegexResult {
    UErrorCode compileStatus = U_ZERO_ERROR;
    bool found = false;
    std::string match;
    int64_t start = -1;  // code points
};

// Compile `pattern` and search `subject` once, both given as UTF-8 and
// searched directly over UTF-8 through UText (no UTF-16 copy of the subject).
RegexResult search(const std::string& pattern, const std::string& subject) {
    RegexResult result;
    UParseError parseError;
    UErrorCode status = U_ZERO_ERROR;
    UText* patternText =
        utext_openUTF8(nullptr, pattern.data(), static_cast<int64_t>(pattern.size()), &status);
    REQUIRE(U_SUCCESS(status));
    URegularExpression* regex = uregex_openUText(patternText, 0, &parseError, &result.compileStatus);
    utext_close(patternText);
    if (U_FAILURE(result.compileStatus)) return result;
    UText* text = utext_openUTF8(nullptr, subject.data(), static_cast<int64_t>(subject.size()), &status);
    uregex_setUText(regex, text, &status);
    result.found = uregex_findNext(regex, &status);
    if (result.found) {
        int64_t begin = uregex_start64(regex, 0, &status);
        int64_t end = uregex_end64(regex, 0, &status);
        result.match = subject.substr(static_cast<size_t>(begin), static_cast<size_t>(end - begin));
        result.start = codePointsBefore(subject, begin);
    }
    REQUIRE(U_SUCCESS(status));
    uregex_close(regex);
    utext_close(text);
    return result;
}

}  // namespace

TEST_CASE("dependency versions are the pinned ones", "[dependencies]") {
    std::string versions = iqalox::dependencyVersions();
    CHECK(versions.starts_with("ICU 78.3 (Unicode 17"));
    CHECK(versions.ends_with("mini-gmp 6.3.0"));
}

TEST_CASE("mini-gmp: exact arithmetic beyond 64 bits", "[dependencies][mini-gmp]") {
    Mpz power;
    mpz_ui_pow_ui(power.value, 2, 200);
    CHECK(power.str() == "1606938044258990275541962092341162602522202993782792835301376");

    Mpz a("123456789012345678901234567890");
    Mpz product;
    mpz_mul(product.value, a.value, a.value);
    CHECK(product.str() == "15241578753238836750495351562536198787501905199875019052100");

    // A result that shrinks back into 64 bits must be detectable as such,
    // so Phase 1 can return to the inline int64 fast path.
    Mpz quotient;
    mpz_tdiv_q(quotient.value, product.value, a.value);
    CHECK(mpz_cmp(quotient.value, a.value) == 0);
    Mpz small(42);
    CHECK(mpz_fits_slong_p(small.value));
    CHECK_FALSE(mpz_fits_slong_p(power.value));
}

TEST_CASE("mini-gmp: floored division and remainder (decision 4)", "[dependencies][mini-gmp]") {
    Mpz n(-7), d(2), q, r;
    mpz_fdiv_qr(q.value, r.value, n.value, d.value);
    CHECK(q.str() == "-4");
    CHECK(r.str() == "1");
}

TEST_CASE("mini-gmp: infinite two's complement bitwise ops (decision 10)", "[dependencies][mini-gmp]") {
    Mpz five(5), result;
    mpz_com(result.value, five.value);
    CHECK(result.str() == "-6");

    Mpz minusOne(-1), mask(255);
    mpz_and(result.value, minusOne.value, mask.value);
    CHECK(result.str() == "255");

    Mpz minusEight(-8);
    mpz_fdiv_q_2exp(result.value, minusEight.value, 1);  // -8 >> 1
    CHECK(result.str() == "-4");

    Mpz one(1);
    mpz_mul_2exp(result.value, one.value, 100);  // 1 << 100
    CHECK(result.str() == "1267650600228229401496703205376");

    Mpz x(0b1100), y(0b1010);
    mpz_xor(result.value, x.value, y.value);
    CHECK(result.str() == "6");
    mpz_ior(result.value, x.value, y.value);
    CHECK(result.str() == "14");
}

TEST_CASE("ICU: full Unicode case mapping without a data file (decision 21)", "[dependencies][icu]") {
    CHECK(mapCase(Case::Upper, "straße") == "STRASSE");
    CHECK(mapCase(Case::Upper, "äö päivä") == "ÄÖ PÄIVÄ");
    CHECK(mapCase(Case::Lower, "ÄÖ") == "äö");
    CHECK(mapCase(Case::Lower, "I") == "i");  // root locale: no Turkish dotless i
    CHECK(mapCase(Case::Upper, "ﬁ") == "FI");  // ligature expands
}

TEST_CASE("ICU: character classification without a data file (decision 24)", "[dependencies][icu]") {
    CHECK(u_isalpha(U'ä'));
    CHECK(u_isalpha(U'Ж'));
    CHECK_FALSE(u_isalpha(U'1'));
    CHECK(u_isdigit(U'٣'));  // ARABIC-INDIC DIGIT THREE
    CHECK(u_isUWhiteSpace(U' '));
    CHECK(u_isUWhiteSpace(U'　'));
    CHECK(u_isupper(U'Ä'));
    CHECK(u_islower(U'ß'));
}

TEST_CASE("ICU regex: Unicode properties, positions in code points (decisions 27-28)", "[dependencies][icu]") {
    RegexResult letters = search(R"(\p{L}+)", "1 päivä 🙂 x");
    REQUIRE(letters.found);
    CHECK(letters.match == "päivä");
    CHECK(letters.start == 2);

    // A position after a non-BMP character counts it once.
    RegexResult afterEmoji = search("x", "🙂🙂x");
    REQUIRE(afterEmoji.found);
    CHECK(afterEmoji.start == 2);

    RegexResult groups = search(R"((\d+)\.(\d+))", "v 12.34");
    REQUIRE(groups.found);
    CHECK(groups.match == "12.34");

    RegexResult script = search(R"(\p{Script=Cyrillic}+)", "abc Жук");
    REQUIRE(script.found);
    CHECK(script.match == "Жук");

    RegexResult lookahead = search(R"(\w+(?=!))", "hi there!");
    REQUIRE(lookahead.found);
    CHECK(lookahead.match == "there");
}

TEST_CASE("ICU regex: case-insensitive matching uses full case folding", "[dependencies][icu]") {
    RegexResult folded = search("(?i)straße", "STRASSE");
    REQUIRE(folded.found);
    CHECK(folded.match == "STRASSE");

    CHECK(search("(?i)PÄIVÄ", "päivä").found);
}

TEST_CASE("ICU regex: what the stub-data build does not support", "[dependencies][icu]") {
    // These need ICU's data file, which iqaloxvm doesn't link (see
    // vm/cmake/IqaloxIcu.cmake). Pinned here so docs/LANGUAGE.md can list
    // them in Phase 7, and so an ICU upgrade can't change them silently.
    CHECK(U_FAILURE(search(R"(\X)", "a").compileStatus));                      // grapheme clusters
    CHECK(U_FAILURE(search(R"(\N{LATIN SMALL LETTER A})", "a").compileStatus));  // character names
    CHECK(U_FAILURE(search(R"(\p{Emoji})", "🙂").compileStatus));                // emoji properties
}
