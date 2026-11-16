#pragma once

#include <string>

namespace iqalox {

// One line naming the third-party libraries linked into this build and their
// versions, for `iqaloxvm --version`, e.g.
// "ICU 78.3 (Unicode 17.0), mini-gmp 6.3.0".
std::string dependencyVersions();

}  // namespace iqalox
