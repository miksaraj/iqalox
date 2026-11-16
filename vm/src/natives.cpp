#include "natives.hpp"

#include <functional>
#include <iostream>
#include <string>
#include <utility>

#include "object.hpp"
#include "vm.hpp"

namespace iqalox {

namespace {

bool isVector(const Value& v) { return isObj(v) && asObj(v)->type == ObjType::Vector; }

std::vector<Value>& vectorElements(const Value& v) { return static_cast<ObjVector*>(asObj(v))->elements; }

// docs/PLAN-0.2.md Phase 6: matrices are plain nested vectors (decision
// 5), so "is this actually a matrix" is never enforced by the type
// system -- transpose/multiply/add/subtract validate it themselves
// (the repository owner's explicit choice: a clean runtime error on a
// malformed/mismatched shape, not undefined behavior or a stray
// index-out-of-range from deep inside an implementation). Returns
// (rowCount, columnCount); an empty matrix (`[]`) is 0x0.
std::pair<size_t, size_t> checkMatrixShape(const std::string& fnName, const std::string& argLabel, const Value& m) {
    if (!isVector(m)) {
        throw RuntimeError(fnName + "'s " + argLabel + " argument must be a matrix (a vector of vectors), got " +
                            typeName(m) + ".");
    }
    auto& rows = vectorElements(m);
    if (rows.empty()) {
        return {0, 0};
    }
    if (!isVector(rows[0])) {
        throw RuntimeError(fnName + "'s " + argLabel +
                            " argument must be a matrix (a vector of vectors), got a vector of " +
                            typeName(rows[0]) + ".");
    }
    size_t cols = vectorElements(rows[0]).size();
    for (const Value& row : rows) {
        if (!isVector(row) || vectorElements(row).size() != cols) {
            throw RuntimeError(fnName + "'s " + argLabel +
                                " argument must be a rectangular matrix (every row the same length).");
        }
    }
    return {rows.size(), cols};
}

Value matrixElement(const std::string& fnName, const Value& m, size_t i, size_t j) {
    const Value& element = vectorElements(vectorElements(m)[i])[j];
    if (!numeric::isNumber(element)) {
        throw RuntimeError(fnName + "'s matrix elements must be numbers, got " + typeName(element) + ".");
    }
    return element;
}

// Builds a rows x cols matrix from `at(i, j)`, which may itself allocate
// (exact integer arithmetic can produce a heap ObjBigInt). The result is
// kept rooted, and each row is attached to it before being filled, so a
// collection `at` triggers can't free what has been built so far.
Value buildMatrix(Vm& vm, size_t rows, size_t cols, const std::function<Value(size_t, size_t)>& at) {
    Vm::TempRoot result(vm, objValue(vm.allocate<ObjVector>()));
    auto& resultRows = static_cast<ObjVector*>(asObj(result.get()))->elements;
    resultRows.reserve(rows);
    for (size_t i = 0; i < rows; ++i) {
        auto* row = vm.allocate<ObjVector>();
        resultRows.push_back(objValue(row));
        row->elements.reserve(cols);
        for (size_t j = 0; j < cols; ++j) {
            Value element = at(i, j);
            row->elements.push_back(element);
        }
    }
    return result.get();
}

}  // namespace

Value nativePrint(Vm&, const std::vector<Value>& args) {
    std::cout << stringify(args[0]) << "\n";
    return NilValue;
}

Value nativeConcat(Vm& vm, const std::vector<Value>& args) {
    if (!isObj(args[0]) || asObj(args[0])->type != ObjType::Vector) {
        throw RuntimeError("Argument to 'concat' must be a vector, got " + typeName(args[0]) + ".");
    }

    std::string joined;
    for (const Value& element : static_cast<ObjVector*>(asObj(args[0]))->elements) {
        joined += stringify(element);
    }
    return objValue(vm.allocate<ObjString>(std::move(joined)));
}

Value nativePush(Vm&, const std::vector<Value>& args) {
    if (!isObj(args[0]) || asObj(args[0])->type != ObjType::Vector) {
        throw RuntimeError("Only vectors can be pushed onto, got " + typeName(args[0]) + ".");
    }
    static_cast<ObjVector*>(asObj(args[0]))->elements.push_back(args[1]);
    return NilValue;
}

Value nativePop(Vm&, const std::vector<Value>& args) {
    if (!isObj(args[0]) || asObj(args[0])->type != ObjType::Vector) {
        throw RuntimeError("Only vectors can be popped from, got " + typeName(args[0]) + ".");
    }
    auto& elements = static_cast<ObjVector*>(asObj(args[0]))->elements;
    if (elements.empty()) {
        throw RuntimeError("Cannot pop from an empty vector.");
    }
    Value last = elements.back();
    elements.pop_back();
    return last;
}

Value nativeLength(Vm&, const std::vector<Value>& args) {
    if (!isObj(args[0]) || asObj(args[0])->type != ObjType::Vector) {
        throw RuntimeError("Only vectors have a length, got " + typeName(args[0]) + ".");
    }
    return smallIntValue(static_cast<int64_t>(static_cast<ObjVector*>(asObj(args[0]))->elements.size()));
}

Value nativeReverse(Vm& vm, const std::vector<Value>& args) {
    if (!isObj(args[0]) || asObj(args[0])->type != ObjType::Vector) {
        throw RuntimeError("Only vectors can be reversed, got " + typeName(args[0]) + ".");
    }
    auto* reversed = vm.allocate<ObjVector>();
    const auto& elements = static_cast<ObjVector*>(asObj(args[0]))->elements;
    reversed->elements.assign(elements.rbegin(), elements.rend());
    return objValue(reversed);
}

Value nativeTranspose(Vm& vm, const std::vector<Value>& args) {
    auto [rows, cols] = checkMatrixShape("transpose", "only", args[0]);
    // An MxN matrix's transpose is NxM -- structurally never in-place,
    // since the result's own shape differs from the input's whenever the
    // matrix isn't square.
    return buildMatrix(vm, cols, rows, [&](size_t i, size_t j) { return matrixElement("transpose", args[0], j, i); });
}

Value nativeMultiply(Vm& vm, const std::vector<Value>& args) {
    auto [aRows, aCols] = checkMatrixShape("multiply", "first", args[0]);
    auto [bRows, bCols] = checkMatrixShape("multiply", "second", args[1]);
    if (aCols != bRows) {
        throw RuntimeError("multiply: a " + std::to_string(aRows) + "x" + std::to_string(aCols) +
                            " matrix can't be multiplied by a " + std::to_string(bRows) + "x" +
                            std::to_string(bCols) + " matrix -- the first matrix's column count must equal the " +
                            "second's row count.");
    }
    return buildMatrix(vm, aRows, bCols, [&](size_t i, size_t j) {
        // Exact for integer matrices (docs/PLAN-0.4.md Phase 1). The running
        // sum is rooted: computing the next product may allocate.
        Vm::TempRoot sum(vm, smallIntValue(0));
        for (size_t k = 0; k < aCols; ++k) {
            Value product = numeric::multiply(vm, matrixElement("multiply", args[0], i, k),
                                              matrixElement("multiply", args[1], k, j));
            sum.set(numeric::add(vm, sum.get(), product));
        }
        return sum.get();
    });
}

Value nativeAdd(Vm& vm, const std::vector<Value>& args) {
    auto [aRows, aCols] = checkMatrixShape("add", "first", args[0]);
    auto [bRows, bCols] = checkMatrixShape("add", "second", args[1]);
    if (aRows != bRows || aCols != bCols) {
        throw RuntimeError("add: matrices must be the same shape, got " + std::to_string(aRows) + "x" +
                            std::to_string(aCols) + " and " + std::to_string(bRows) + "x" + std::to_string(bCols) +
                            ".");
    }
    return buildMatrix(vm, aRows, aCols, [&](size_t i, size_t j) {
        return numeric::add(vm, matrixElement("add", args[0], i, j), matrixElement("add", args[1], i, j));
    });
}

Value nativeSubtract(Vm& vm, const std::vector<Value>& args) {
    auto [aRows, aCols] = checkMatrixShape("subtract", "first", args[0]);
    auto [bRows, bCols] = checkMatrixShape("subtract", "second", args[1]);
    if (aRows != bRows || aCols != bCols) {
        throw RuntimeError("subtract: matrices must be the same shape, got " + std::to_string(aRows) + "x" +
                            std::to_string(aCols) + " and " + std::to_string(bRows) + "x" + std::to_string(bCols) +
                            ".");
    }
    return buildMatrix(vm, aRows, aCols, [&](size_t i, size_t j) {
        return numeric::subtract(vm, matrixElement("subtract", args[0], i, j), matrixElement("subtract", args[1], i, j));
    });
}

}  // namespace iqalox
