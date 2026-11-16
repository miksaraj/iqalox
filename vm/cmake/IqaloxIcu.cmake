# ICU for iqaloxvm (docs/PLAN-0.4.md decisions 21 and 28, Phase 0).
#
# ICU is built from its pinned source tarball, with CMake, as two static
# libraries linked straight into iqaloxvm. The same path is used on every
# platform (ICU's own build is autotools on Unix and Visual Studio projects
# on Windows), and the release binaries need no ICU installed on the user's
# machine.
#
# No ICU data file is linked. ICU's "stubdata" library stands in for the
# ~32 MB data package. Everything 0.4 uses from ICU (character properties,
# full case mapping, property names, regex) runs on the tables ICU compiles
# into its code. The features that would need the data file are switched
# off below or fail with a missing-resource error: legacy charset
# converters, formatting, collation, transliteration, break iteration
# (regex `\X` and `\b{w}`), character names (`\N{...}`), and the emoji and
# layout properties. `vm/tests/test_dependencies.cpp` pins down which ones
# work.
#
# Offline builds: extract the tarball yourself and pass
# -DFETCHCONTENT_SOURCE_DIR_ICU=<the extracted `icu` directory, which holds source/>.

include(FetchContent)

set(IQALOX_ICU_VERSION 78.3)

# The source is ICU4C 78.3's upstream release tarball, as mirrored by the
# Ubuntu archive (it carries upstream's detached .asc signature next to it).
# The hash is what pins it: CMake refuses anything else, so any host serving
# these exact bytes would do. To upgrade, change the version, the URL and the
# hash together, then re-run vm/tests/test_dependencies.cpp.
FetchContent_Declare(icu
    URL "https://archive.ubuntu.com/ubuntu/pool/main/i/icu/icu_${IQALOX_ICU_VERSION}.orig.tar.gz"
    URL_HASH SHA256=3a2e7a47604ba702f345878308e6fefeca612ee895cf4a5f222e7955fabfe0c0
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    # ICU has no CMakeLists.txt, so this only downloads and extracts.
    SOURCE_SUBDIR source
)
FetchContent_MakeAvailable(icu)

set(_icu_src "${icu_SOURCE_DIR}/source")
if(NOT EXISTS "${_icu_src}/common/sources.txt")
    message(FATAL_ERROR "ICU source tree not found at ${_icu_src}")
endif()

# ICU keeps one file name per line in each library's sources.txt.
function(_iqalox_icu_sources out dir)
    file(STRINGS "${_icu_src}/${dir}/sources.txt" names REGEX "\\.cpp$")
    list(TRANSFORM names PREPEND "${_icu_src}/${dir}/")
    set(${out} ${names} PARENT_SCOPE)
endfunction()

_iqalox_icu_sources(_icu_common_sources common)
_iqalox_icu_sources(_icu_i18n_sources i18n)

# Feature switches. PUBLIC, so code that includes ICU's headers sees the same
# configuration the libraries were built with.
set(_icu_public_definitions
    U_STATIC_IMPLEMENTATION
    U_USING_ICU_NAMESPACE=0
    U_CHARSET_IS_UTF8=1
    U_HIDE_OBSOLETE_UTF_OLD_H=1
    UCONFIG_NO_FILE_IO=1
    UCONFIG_NO_CONVERSION=1
    UCONFIG_NO_LEGACY_CONVERSION=1
    UCONFIG_NO_FORMATTING=1
    UCONFIG_NO_MF2=1
    UCONFIG_NO_COLLATION=1
    UCONFIG_NO_TRANSLITERATION=1
    UCONFIG_NO_BREAK_ITERATION=1
    UCONFIG_NO_IDNA=1
    UCONFIG_NO_SERVICE=1
)

add_library(iqalox_icuuc STATIC ${_icu_common_sources} "${_icu_src}/stubdata/stubdata.cpp")
add_library(iqalox_icui18n STATIC ${_icu_i18n_sources})

target_include_directories(iqalox_icuuc SYSTEM PUBLIC "${_icu_src}/common")
target_include_directories(iqalox_icuuc PRIVATE "${_icu_src}/stubdata")
target_include_directories(iqalox_icui18n SYSTEM PUBLIC "${_icu_src}/i18n")
target_compile_definitions(iqalox_icuuc PUBLIC ${_icu_public_definitions} PRIVATE U_COMMON_IMPLEMENTATION)
target_compile_definitions(iqalox_icui18n PRIVATE U_I18N_IMPLEMENTATION)
target_link_libraries(iqalox_icui18n PUBLIC iqalox_icuuc)

find_package(Threads REQUIRED)
target_link_libraries(iqalox_icuuc PUBLIC Threads::Threads ${CMAKE_DL_LIBS})

foreach(_icu_lib iqalox_icuuc iqalox_icui18n)
    # Third-party code: keep its warnings out of our build output.
    set_target_properties(${_icu_lib} PROPERTIES CXX_STANDARD 17 CXX_EXTENSIONS OFF)
    target_compile_definitions(${_icu_lib} PRIVATE U_ATTRIBUTE_DEPRECATED=)
    if(MSVC)
        # The same switches ICU's own Visual Studio projects use.
        target_compile_options(${_icu_lib} PRIVATE /utf-8 /W0)
        target_compile_definitions(${_icu_lib} PRIVATE _CRT_SECURE_NO_DEPRECATE)
    else()
        target_compile_options(${_icu_lib} PRIVATE -w)
    endif()
endforeach()
if(WIN32)
    # Only the common library gets this in ICU's own projects (common.vcxproj).
    target_compile_definitions(iqalox_icuuc PRIVATE U_PLATFORM_USES_ONLY_WIN32_API=1)
    target_link_libraries(iqalox_icuuc PUBLIC advapi32)
endif()

add_library(Iqalox::ICU ALIAS iqalox_icui18n)

# ICU's license must ship with any binary that links it statically.
set(IQALOX_ICU_LICENSE_FILE "${icu_SOURCE_DIR}/LICENSE")
