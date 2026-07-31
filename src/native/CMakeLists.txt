cmake_minimum_required(VERSION 3.25)

project(CodexQuotaTaskbarNative LANGUAGES CXX)

include(CTest)

function(cqtb_configure_cpp_target target_name)
  if(MSVC)
    target_compile_options(${target_name} PRIVATE
      /EHsc
      /W4
      /WX
      /permissive-
      /utf-8
    )
  endif()
endfunction()

add_library(cq_bridge_lifecycle STATIC
  taskbar-bridge/bridge_runtime.cpp
  taskbar-bridge/probe_capsule.cpp
  taskbar-bridge/probe_safety.cpp
  taskbar-bridge/xaml_taskbar_probe.cpp
)
target_compile_features(cq_bridge_lifecycle PUBLIC cxx_std_23)
target_include_directories(cq_bridge_lifecycle PUBLIC
  "${CMAKE_CURRENT_SOURCE_DIR}/common"
  "${CMAKE_CURRENT_SOURCE_DIR}/taskbar-bridge"
)
target_link_libraries(cq_bridge_lifecycle PUBLIC windowsapp)
cqtb_configure_cpp_target(cq_bridge_lifecycle)

add_library(CodexQuotaTaskbar.Bridge SHARED
  taskbar-bridge/bridge_exports.def
  taskbar-bridge/dllmain.cpp
)
target_compile_features(CodexQuotaTaskbar.Bridge PRIVATE cxx_std_23)
target_compile_definitions(CodexQuotaTaskbar.Bridge PRIVATE
  CQTB_BRIDGE_BUILD
  UNICODE
  _UNICODE
)
target_include_directories(CodexQuotaTaskbar.Bridge PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}/common"
  "${CMAKE_CURRENT_SOURCE_DIR}/taskbar-bridge"
)
target_link_libraries(CodexQuotaTaskbar.Bridge PRIVATE
  cq_bridge_lifecycle
  kernel32
  ole32
  oleaut32
  runtimeobject
  user32
  windowsapp
)
set_target_properties(CodexQuotaTaskbar.Bridge PROPERTIES
  OUTPUT_NAME "CodexQuotaTaskbar.Bridge"
)
cqtb_configure_cpp_target(CodexQuotaTaskbar.Bridge)
if(MSVC)
  target_compile_options(CodexQuotaTaskbar.Bridge PRIVATE /guard:cf)
  target_link_options(CodexQuotaTaskbar.Bridge PRIVATE /guard:cf)
endif()

if(BUILD_TESTING)
  add_executable(probe_safety_tests
    tests/probe_safety_tests.cpp
  )
  target_compile_features(probe_safety_tests PRIVATE cxx_std_23)
  target_include_directories(probe_safety_tests PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/taskbar-bridge"
  )
  target_link_libraries(probe_safety_tests PRIVATE cq_bridge_lifecycle)
  cqtb_configure_cpp_target(probe_safety_tests)

  add_executable(xaml_taskbar_probe_tests
    tests/xaml_taskbar_probe_tests.cpp
  )
  target_compile_features(xaml_taskbar_probe_tests PRIVATE cxx_std_23)
  target_include_directories(xaml_taskbar_probe_tests PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/taskbar-bridge"
  )
  target_link_libraries(xaml_taskbar_probe_tests PRIVATE cq_bridge_lifecycle)
  cqtb_configure_cpp_target(xaml_taskbar_probe_tests)

  add_executable(bridge_lifecycle_tests
    tests/bridge_lifecycle_tests.cpp
  )
  target_compile_features(bridge_lifecycle_tests PRIVATE cxx_std_23)
  target_include_directories(bridge_lifecycle_tests PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/taskbar-bridge"
  )
  target_link_libraries(bridge_lifecycle_tests PRIVATE cq_bridge_lifecycle)
  cqtb_configure_cpp_target(bridge_lifecycle_tests)

  add_test(NAME xaml_taskbar_probe_tests COMMAND xaml_taskbar_probe_tests)
  add_test(NAME bridge_lifecycle_tests COMMAND bridge_lifecycle_tests)
  add_test(NAME probe_safety_tests COMMAND probe_safety_tests)
endif()
