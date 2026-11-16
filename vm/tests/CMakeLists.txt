find_package(Catch2 3 REQUIRED)

add_executable(iqaloxvm_tests
    test_bytecode.cpp
    test_vm.cpp
    test_value.cpp
    test_classes.cpp
    test_dependencies.cpp
)

target_link_libraries(iqaloxvm_tests PRIVATE iqaloxvm_core Catch2::Catch2WithMain)

include(CTest)
include(Catch)
catch_discover_tests(iqaloxvm_tests)
