#include <iostream>
#include <string_view>

#include "bytecode.hpp"
#include "dependencies.hpp"
#include "vm.hpp"

int main(int argc, char** argv) {
    if (argc == 2 && std::string_view(argv[1]) == "--version") {
        std::cout << "iqaloxvm (bytecode format v" << static_cast<int>(iqalox::bytecode::kFormatVersion) << "), "
                  << iqalox::dependencyVersions() << "\n";
        return 0;
    }
    if (argc != 2) {
        std::cerr << "Usage: iqaloxvm <bytecode file>\n";
        return 64;
    }

    iqalox::Vm vm;
    try {
        iqalox::ObjFunction* script = iqalox::bytecode::load(argv[1], vm);
        vm.interpret(script);
        return 0;
    } catch (const iqalox::RuntimeError& error) {
        // An Iqalox-level fault in an otherwise well-formed program --
        // matches poc/src/iqalox.py's sysexits convention (EX_SOFTWARE).
        std::cerr << error.what() << "\n";
        return 70;
    } catch (const std::exception& error) {
        // A malformed bytecode file (bad magic/version/truncated data) --
        // EX_DATAERR, caught separately and second since RuntimeError is
        // itself a std::exception.
        std::cerr << error.what() << "\n";
        return 65;
    }
}
