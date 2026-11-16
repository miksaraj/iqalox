#include "dependencies.hpp"

#include <unicode/uchar.h>
#include <unicode/uversion.h>

#include "mini-gmp.h"

namespace iqalox {

namespace {

std::string formatVersion(const UVersionInfo version) {
    char text[U_MAX_VERSION_STRING_LENGTH];
    u_versionToString(version, text);
    return text;
}

}  // namespace

std::string dependencyVersions() {
    UVersionInfo icu;
    u_getVersion(icu);
    UVersionInfo unicode;
    u_getUnicodeVersion(unicode);
    return "ICU " + formatVersion(icu) + " (Unicode " + formatVersion(unicode) + "), mini-gmp " +
           IQALOX_MINI_GMP_VERSION;
}

}  // namespace iqalox
