cmake_minimum_required(VERSION 3.16)
project(n64-gpu-probe LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 17)
set(PARALLEL_RDP_SOURCE "" CACHE PATH "Pinned paraLLEl-RDP checkout with submodules")
if (NOT EXISTS "${PARALLEL_RDP_SOURCE}/parallel-rdp/rdp_device.hpp")
    message(FATAL_ERROR "Set PARALLEL_RDP_SOURCE to the prepared external checkout")
endif()
set(GRANITE_RENDERER OFF CACHE BOOL "" FORCE)
set(GRANITE_TOOLS OFF CACHE BOOL "" FORCE)
set(GRANITE_AUDIO OFF CACHE BOOL "" FORCE)
set(GRANITE_PLATFORM headless CACHE STRING "" FORCE)
set(GRANITE_VULKAN_FOSSILIZE OFF CACHE BOOL "" FORCE)
add_subdirectory("${PARALLEL_RDP_SOURCE}" upstream EXCLUDE_FROM_ALL)
target_compile_definitions(granite-vulkan PUBLIC VULKAN_DEBUG)
# The pinned glslang relied on a transitive cstdint include removed by GCC 15.
# Supply the standard header without modifying the external checkout.
target_compile_options(SPIRV PRIVATE -include cstdint)
target_compile_options(granite-util PUBLIC "$<$<COMPILE_LANGUAGE:CXX>:-include>" "$<$<COMPILE_LANGUAGE:CXX>:cstdint>")
# rdp-utils includes Angrylion solely as the offline validation oracle. This
# executable and its dependency graph must not be linked into the emulator.
add_granite_offline_tool(n64-gpu-probe main.cpp)
target_link_libraries(n64-gpu-probe PRIVATE rdp-utils)
target_compile_options(n64-gpu-probe PRIVATE -Wall -Wextra -Wno-unused-function)
