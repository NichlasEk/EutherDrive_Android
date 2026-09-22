#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

namespace RDP {
class CommandProcessor;
// Access is granted by the two declarations in the prepared, pinned source.
// This format describes persistent RDP state, never Vulkan objects or caches.
class EutherCheckpoint {
public:
    static std::vector<uint8_t> save(CommandProcessor &processor);
    static void load(CommandProcessor &processor, const uint8_t *bytes, size_t size);
};
}
