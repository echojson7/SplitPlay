// SPDX-License-Identifier: MIT
// Minimal test harness: TEST_CASE registers a function, CHECK records a failure and continues, REQUIRE aborts the
// case. Each test executable runs all cases (or those named on the command line) and returns non-zero on failure.
#pragma once

#include <cstdio>
#include <cstring>
#include <exception>
#include <functional>
#include <string>
#include <vector>

namespace sp_test {

struct Case {
  const char* name;
  void (*fn)();
};

inline std::vector<Case>& registry() {
  static std::vector<Case> cases;
  return cases;
}

inline int& failures() {
  static int n = 0;
  return n;
}

struct Registrar {
  Registrar(const char* name, void (*fn)()) { registry().push_back({name, fn}); }
};

struct RequireFailed {};

inline void report(const char* file, int line, const char* expr, const std::string& detail = {}) {
  ++failures();
  std::fprintf(stderr, "%s:%d: CHECK failed: %s%s%s\n", file, line, expr, detail.empty() ? "" : " -- ",
               detail.c_str());
}

inline int run_all(int argc, char** argv) {
  int failed_cases = 0;
  int ran = 0;
  for (const Case& c : registry()) {
    bool selected = argc <= 1;
    for (int i = 1; i < argc; ++i)
      if (std::strcmp(argv[i], c.name) == 0) selected = true;
    if (!selected) continue;
    ++ran;
    const int before = failures();
    std::fprintf(stderr, "[ RUN  ] %s\n", c.name);
    try {
      c.fn();
    } catch (const RequireFailed&) {
    } catch (const std::exception& e) {
      report(__FILE__, __LINE__, "unexpected exception", e.what());
    }
    const bool ok = failures() == before;
    if (!ok) ++failed_cases;
    std::fprintf(stderr, "[ %s ] %s\n", ok ? " OK " : "FAIL", c.name);
  }
  std::fprintf(stderr, "%d case(s), %d failed\n", ran, failed_cases);
  if (ran == 0) return 2;
  return failed_cases == 0 ? 0 : 1;
}

}  // namespace sp_test

#define SP_TEST_CAT2(a, b) a##b
#define SP_TEST_CAT(a, b) SP_TEST_CAT2(a, b)

#define TEST_CASE(name)                                                                \
  static void SP_TEST_CAT(sp_test_fn_, __LINE__)();                                    \
  static ::sp_test::Registrar SP_TEST_CAT(sp_test_reg_, __LINE__)(name, &SP_TEST_CAT(sp_test_fn_, __LINE__)); \
  static void SP_TEST_CAT(sp_test_fn_, __LINE__)()

#define CHECK(expr)                                                     \
  do {                                                                  \
    if (!(expr)) ::sp_test::report(__FILE__, __LINE__, #expr);          \
  } while (0)

#define CHECK_MSG(expr, msg)                                            \
  do {                                                                  \
    if (!(expr)) ::sp_test::report(__FILE__, __LINE__, #expr, (msg));   \
  } while (0)

#define REQUIRE(expr)                                                   \
  do {                                                                  \
    if (!(expr)) {                                                      \
      ::sp_test::report(__FILE__, __LINE__, #expr);                     \
      throw ::sp_test::RequireFailed{};                                 \
    }                                                                   \
  } while (0)

#define REQUIRE_MSG(expr, msg)                                          \
  do {                                                                  \
    if (!(expr)) {                                                      \
      ::sp_test::report(__FILE__, __LINE__, #expr, (msg));              \
      throw ::sp_test::RequireFailed{};                                 \
    }                                                                   \
  } while (0)

#define CHECK_EQ(a, b) CHECK_MSG((a) == (b), std::to_string(a) + " != " + std::to_string(b))

#define SP_TEST_MAIN() \
  int main(int argc, char** argv) { return ::sp_test::run_all(argc, argv); }
