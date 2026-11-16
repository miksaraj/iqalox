#!/usr/bin/env bash
# Checks a release-configured iqaloxvm build (Release, IQALOX_BUILD_TESTS=OFF,
# as in .github/workflows/release.yml) on any of the three CI platforms:
#
#   - the binary runs and reports the pinned ICU and mini-gmp versions, which
#     proves both were linked into the shipped binary itself, not only into
#     the test suite;
#   - neither is a shared-library dependency (they are linked statically, so
#     the bundle needs nothing installed on the user's machine);
#   - the third-party license files the bundle must ship were collected.
#
# Usage: scripts/vm-release-smoke-test.sh <vm build dir>
set -euo pipefail

build_dir=${1:?usage: $0 <vm build dir>}

bin="$build_dir/iqaloxvm"
if [ -f "$build_dir/Release/iqaloxvm.exe" ]; then
    bin="$build_dir/Release/iqaloxvm.exe"   # MSVC multi-config layout
fi

version=$("$bin" --version)
echo "$version"
grep -q "ICU 78.3 (Unicode 17" <<<"$version"
grep -q "mini-gmp 6.3.0" <<<"$version"

case "$(uname -s)" in
    Linux)
        ldd "$bin"
        if ldd "$bin" | grep -Eiq 'icu|gmp'; then
            echo "error: iqaloxvm links ICU or GMP dynamically" >&2
            exit 1
        fi
        ;;
    Darwin)
        otool -L "$bin"
        if otool -L "$bin" | grep -Eiq 'icu|gmp'; then
            echo "error: iqaloxvm links ICU or GMP dynamically" >&2
            exit 1
        fi
        ;;
esac

for license in ICU-LICENSE.txt mini-gmp-COPYING.LESSERv3.txt mini-gmp-COPYINGv2.txt mini-gmp-COPYINGv3.txt; do
    test -s "$build_dir/third-party-licenses/$license"
done
echo "ok: $(du -k "$bin" | cut -f1) KiB, ICU and mini-gmp linked statically, licenses collected"
