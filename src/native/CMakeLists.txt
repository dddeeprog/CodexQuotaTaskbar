cmake_minimum_required(VERSION 3.25)

project(CodexQuotaTaskbarNative LANGUAGES CXX)

include(CTest)

if(BUILD_TESTING)
  add_test(NAME native_no_op COMMAND "${CMAKE_COMMAND}" -E true)
endif()
