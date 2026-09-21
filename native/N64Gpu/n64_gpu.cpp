#include "n64_gpu.h"
#include "rdp_device.hpp"
#include "context.hpp"
#include "global_managers_init.hpp"
#include "filesystem.hpp"
#include "thread_group.hpp"
#include "aligned_alloc.hpp"
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <stdexcept>
#include <vector>

#if __BYTE_ORDER__ != __ORDER_LITTLE_ENDIAN__
#error "ABI 1 currently supports little-endian Linux hosts only"
#endif

namespace {
constexpr uint32_t ram_size = ED_N64_GPU_RAM_SIZE, hidden_size = ED_N64_GPU_HIDDEN_SIZE;
uint32_t le32(const uint8_t *p) { return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24; }
void require(bool condition, const char *message) { if (!condition) throw std::invalid_argument(message); }
void copy_string(char *dst, uint32_t capacity, const char *src) {
    if (dst && capacity) std::snprintf(dst, capacity, "%s", src);
}
void swap_words(uint8_t *dst, const uint8_t *src, uint32_t size) {
    for (uint32_t i = 0; i < size; i += 4) {
        uint32_t value; std::memcpy(&value, src + i, 4); value = __builtin_bswap32(value); std::memcpy(dst + i, &value, 4);
    }
}
void swap_halfwords(uint8_t *dst, const uint8_t *src, uint32_t size) {
    for (uint32_t i = 0; i < size; i += 2) { dst[i] = src[i + 1]; dst[i + 1] = src[i]; }
}
void patch_be(uint8_t *dst, uint32_t offset, const uint8_t *src, uint32_t size) {
    while (size && (offset & 3)) { dst[offset++ ^ 3] = *src++; size--; }
    uint32_t aligned = size & ~3u;
    swap_words(dst + offset, src, aligned); offset += aligned; src += aligned; size -= aligned;
    while (size--) dst[offset++ ^ 3] = *src++;
}

// Granite's managers are thread-local. Preserve the embedding caller's context
// and bind this instance on every entry, including a managed finalizer thread.
struct RestoreTls {
    Granite::Global::GlobalManagersHandle previous = Granite::Global::create_thread_context();
    ~RestoreTls() { Granite::Global::set_thread_context(*previous); }
};
struct Managers {
    Granite::Global::GlobalManagersHandle state;
    Managers() {
        Granite::Global::clear_thread_context();
        try {
            Granite::Global::init(Granite::Global::MANAGER_FEATURE_FILESYSTEM_BIT | Granite::Global::MANAGER_FEATURE_THREAD_GROUP_BIT, 2);
            state = Granite::Global::create_thread_context();
        } catch (...) { Granite::Global::deinit(); throw; }
    }
    void bind() const { Granite::Global::set_thread_context(*state); }
    ~Managers() { bind(); Granite::Global::deinit(); }
};
struct InstanceFactory : Vulkan::InstanceFactory {
    bool validate;
    explicit InstanceFactory(bool enabled) : validate(enabled) {}
    VkInstance create_instance(const VkInstanceCreateInfo *input) override {
        VkInstanceCreateInfo info = *input;
        std::vector<const char *> extensions;
        if (input->enabledExtensionCount)
            extensions.assign(input->ppEnabledExtensionNames, input->ppEnabledExtensionNames + input->enabledExtensionCount);
        VkValidationFeatureEnableEXT sync = VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
        VkValidationFeaturesEXT features{};
        if (validate) {
            bool found = false;
            for (unsigned i = 0; i < input->enabledLayerCount; i++) found |= std::strcmp(input->ppEnabledLayerNames[i], "VK_LAYER_KHRONOS_validation") == 0;
            if (!found) throw std::runtime_error("Validation requested: require Khronos layer and GRANITE_VULKAN_NO_VALIDATION=0");
            features.sType = VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT;
            features.pNext = info.pNext; features.enabledValidationFeatureCount = 1; features.pEnabledValidationFeatures = &sync;
            extensions.push_back(VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME);
            info.enabledExtensionCount = uint32_t(extensions.size()); info.ppEnabledExtensionNames = extensions.data(); info.pNext = &features;
        }
        VkInstance instance = VK_NULL_HANDLE;
        if (vkCreateInstance(&info, nullptr, &instance) != VK_SUCCESS) throw std::runtime_error("vkCreateInstance failed");
        return instance;
    }
};
struct AlignedFree { void operator()(uint8_t *ptr) const { Util::memalign_free(ptr); } };
// Conservative RDRAM read AND write bounds for the pinned, unscaled renderer.
// update_deduced_height() clips against the 12-bit quarter-pixel scissor, so
// no render pass exceeds 1024 rows. Include the entire rectangular pass, both
// attachments even with depth disabled, I4's byte accesses, and word padding.
// Shader addresses wrap at RDRAM_SIZE; state changes can expose pending data.
struct FramebufferRanges {
    uint32_t color = 0, depth = 0, width = 0, pixel_bytes = 0;
    bool have_depth = false;
    void command(uint32_t op, uint32_t w0, uint32_t w1) {
        if (op == 0x3f) {
            color = w1 & 0xffffff; width = (w0 & 1023) + 1;
            pixel_bytes = 1u << std::max(0, int((w0 >> 19) & 3) - 1);
        } else if (op == 0x3e) { depth = w1 & 0xffffff; have_depth = true; }
    }
    static bool overlaps(uint32_t low, uint32_t high, uint32_t address, uint32_t bytes) {
        uint32_t start = address & (ram_size - 4), end = start + bytes + 4;
        if (end <= ram_size) return low < end && high > start;
        return high > start || low < end - ram_size;
    }
    bool disjoint(uint32_t low, uint32_t high) const {
        return width && have_depth && !overlaps(low, high, color, width * 1024 * pixel_bytes)
            && !overlaps(low, high, depth, width * 1024 * 2);
    }
};
struct Context : RDP::ValidationInterface {
    Managers managers;
    std::atomic<uint64_t> errors{0};
    InstanceFactory factory;
    Vulkan::Context vulkan;
    std::unique_ptr<Vulkan::Device> device;
    std::unique_ptr<uint8_t, AlignedFree> ram;
    std::unique_ptr<RDP::CommandProcessor> gpu;
    ed_n64_gpu_stats stats{};
    bool batch_state_writes;
    bool defer_disjoint_writes;
    FramebufferRanges framebuffers;

    Context(uint32_t flags, const uint8_t *initial_ram, const uint8_t *initial_hidden)
        : factory((flags & ED_N64_GPU_VALIDATE) != 0),
          batch_state_writes((flags & (ED_N64_GPU_BATCH_STATE_WRITES | ED_N64_GPU_DEFER_DISJOINT_WRITES)) != 0),
          defer_disjoint_writes((flags & ED_N64_GPU_DEFER_DISJOINT_WRITES) != 0) {
        if (!Vulkan::Context::init_loader(nullptr)) throw std::runtime_error("Cannot load Vulkan");
        vulkan.set_instance_factory(&factory);
        vulkan.set_notification_callback([this](const char *) { errors++; });
        Vulkan::Context::SystemHandles handles{};
        handles.filesystem = GRANITE_FILESYSTEM(); handles.thread_group = GRANITE_THREAD_GROUP();
        handles.timeline_trace_file = handles.thread_group->get_timeline_trace_file();
        vulkan.set_system_handles(handles);
        if (!vulkan.init_instance_and_device(nullptr, 0, nullptr, 0)) throw std::runtime_error("Cannot initialize Vulkan device");
        device = std::make_unique<Vulkan::Device>(); device->set_context(vulkan);
        if ((flags & ED_N64_GPU_REQUIRE_DISCRETE) && device->get_gpu_properties().deviceType != VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)
            throw std::runtime_error("A discrete GPU was requested");
        // This first ABI deliberately requires coherent external host memory.
        // The backend's separate copy/mask fallback needs its own validation.
        auto &features = device->get_device_features();
        if (!features.supports_external_memory_host) throw std::runtime_error("VK_EXT_external_memory_host required by ABI 1");
        size_t alignment = std::max<size_t>(64 * 1024, features.host_memory_properties.minImportedHostPointerAlignment);
        if (alignment > ram_size || (ram_size % alignment)) throw std::runtime_error("Unsupported imported-host-memory alignment");
        const char *external = std::getenv("PARALLEL_RDP_ALLOW_EXTERNAL_HOST");
        if (external && std::strcmp(external, "0") == 0) throw std::runtime_error("External host memory disabled by environment");
        ram.reset(static_cast<uint8_t *>(Util::memalign_calloc(alignment, ram_size)));
        if (!ram) throw std::bad_alloc();
        gpu = std::make_unique<RDP::CommandProcessor>(*device, ram.get(), 0, ram_size, hidden_size,
            RDP::COMMAND_PROCESSOR_FLAG_HOST_VISIBLE_HIDDEN_RDRAM_BIT | RDP::COMMAND_PROCESSOR_FLAG_HOST_VISIBLE_TMEM_BIT);
        if (!gpu->device_is_supported()) throw std::runtime_error("GPU lacks paraLLEl-RDP capabilities");
        gpu->set_validation_interface(this);
        gpu->idle();
        // Detect import failure even when the extension is advertised. The
        // fallback exposes a different mapping (or no CPU mapping).
        if (gpu->begin_read_rdram() != ram.get()) throw std::runtime_error("Host memory import failed; copy fallback is not supported yet");
        swap_words(ram.get(), initial_ram, ram_size); gpu->end_write_rdram();
        swap_halfwords(static_cast<uint8_t *>(gpu->begin_read_hidden_rdram()), initial_hidden, hidden_size); gpu->end_write_hidden_rdram();
        healthy();
    }
    void report_rdp_crash(RDP::ValidationError, const char *message) override {
        errors++; std::fprintf(stderr, "[N64 GPU] RDP validation: %s\n", message);
    }
    void healthy() const { if (errors.load()) throw std::runtime_error("Vulkan or RDP validation failed; discard this context"); }
    void wait(uint64_t timeline) {
        require(timeline > 0 && timeline <= stats.last_timeline, "Invalid or future timeline");
        if (timeline > stats.completed_timeline) { gpu->wait_for_timeline(timeline); stats.completed_timeline = timeline; }
        healthy();
    }
    ~Context() {
        managers.bind();
        if (gpu) gpu->idle();
        if (device) device->wait_idle();
        gpu.reset(); device.reset();
        vulkan.set_notification_callback({});
    }
};

std::mutex api_mutex;
std::map<uint64_t, std::unique_ptr<Context>> contexts;
uint64_t next_handle = 1;
Context &get(uint64_t handle) {
    auto it = contexts.find(handle); require(it != contexts.end(), "Invalid or disposed GPU handle");
    it->second->managers.bind(); return *it->second;
}
template<typename F> int boundary(char *error, uint32_t capacity, F &&fn) noexcept {
    copy_string(error, capacity, "");
    try { std::lock_guard<std::mutex> lock(api_mutex); RestoreTls restore; fn(); return ED_N64_GPU_OK; }
    catch (const std::invalid_argument &e) { copy_string(error, capacity, e.what()); return ED_N64_GPU_ARGUMENT; }
    catch (const std::exception &e) { copy_string(error, capacity, e.what()); return ED_N64_GPU_BACKEND; }
    catch (...) { copy_string(error, capacity, "Unknown native GPU failure"); return ED_N64_GPU_BACKEND; }
}
uint32_t command_words(uint32_t op) {
    return op >= 8 && op <= 15 ? 8 + ((op & 4) ? 16 : 0) + ((op & 2) ? 16 : 0) + ((op & 1) ? 4 : 0)
        : op == 0x24 || op == 0x25 ? 4 : 2;
}
void validate_batch(const uint8_t *bytes, uint32_t size) {
    require(bytes && size && size <= 64u * 1024 * 1024, "Invalid batch buffer or length");
    for (uint32_t at = 0; at < size;) {
        require(size - at >= 8, "Truncated batch record");
        uint32_t kind = le32(bytes + at), length = le32(bytes + at + 4); at += 8;
        require(length <= size - at, "Batch payload exceeds buffer"); auto *data = bytes + at;
        if (kind == 1) {
            require(length > 4, "Empty RDRAM patch");
            require(le32(data) < ram_size && uint64_t(le32(data)) + length - 4 <= ram_size, "RDRAM patch out of range");
        } else if (kind == 2) {
            require(length >= 8 && length % 4 == 0, "Invalid command payload");
            require(length == command_words((le32(data) >> 24) & 63) * 4, "Incomplete command");
        } else if (kind == 3) require(length == 56, "Invalid VI payload");
        else throw std::invalid_argument("Unknown batch record kind");
        at += length;
    }
}
// Exhaustive whitelist of state-only commands in the pinned CommandProcessor.
// Unknown commands take the barrier path. Draws can additionally pass only
// when DEFER_DISJOINT_WRITES proves their entire memory bounds disjoint.
// Texture loads, FULL_SYNC and public submit boundaries always flush writes.
bool state_only(uint32_t op) {
    switch (op) {
    case 0x26: case 0x27: case 0x28:
    case 0x2a: case 0x2b: case 0x2c: case 0x2d: case 0x2e: case 0x2f:
    case 0x32: case 0x35: case 0x37: case 0x38: case 0x39: case 0x3a:
    case 0x3b: case 0x3c: case 0x3d: case 0x3e: case 0x3f: return true;
    default: return false;
    }
}
}

uint32_t ed_n64_gpu_abi(void) { return ED_N64_GPU_ABI; }
int ed_n64_gpu_create(uint32_t abi, uint32_t flags, const uint8_t *ram, uint32_t size,
    const uint8_t *hidden, uint32_t hidden_bytes, uint64_t *handle, char *error, uint32_t capacity) {
    if (handle) *handle = 0;
    return boundary(error, capacity, [&] {
        require(abi == ED_N64_GPU_ABI && (flags & ~15u) == 0, "Unsupported ABI or flags");
        require(handle && ram && hidden && size == ram_size && hidden_bytes == hidden_size, "Invalid initial memory buffers");
        require(next_handle <= uint64_t(INT64_MAX), "GPU handle space exhausted");
        auto context = std::make_unique<Context>(flags, ram, hidden);
        uint64_t id = next_handle++; contexts.emplace(id, std::move(context)); *handle = id;
    });
}
int ed_n64_gpu_submit(uint64_t handle, const uint8_t *bytes, uint32_t size, uint64_t *timeline, char *error, uint32_t capacity) {
    if (timeline) *timeline = 0;
    return boundary(error, capacity, [&] {
        auto &ctx = get(handle); require(timeline, "Missing timeline output"); validate_batch(bytes, size); ctx.healthy();
        struct Patch { uint32_t address, size; const uint8_t *data; };
        std::vector<Patch> pending;
        uint32_t pending_low = ram_size, pending_high = 0;
        auto flush = [&] {
            if (pending.empty()) return;
            ctx.gpu->idle(); ctx.gpu->begin_read_rdram();
            for (auto &patch : pending) patch_be(ctx.ram.get(), patch.address, patch.data, patch.size);
            ctx.gpu->end_write_rdram(); ctx.stats.write_barriers++; pending.clear();
            pending_low = ram_size; pending_high = 0;
        };
        for (uint32_t at = 0; at < size;) {
            uint32_t kind = le32(bytes + at), length = le32(bytes + at + 4); at += 8; auto *data = bytes + at;
            if (kind == 1) {
                pending.push_back({le32(data), length - 4, data + 4});
                pending_low = std::min(pending_low, le32(data));
                pending_high = std::max(pending_high, le32(data) + length - 4);
                ctx.stats.write_spans++; ctx.stats.write_bytes += length - 4;
            } else {
                uint32_t op = kind == 2 ? (le32(data) >> 24) & 63 : 0;
                bool draw = (op >= 8 && op <= 15) || op == 0x24 || op == 0x25 || op == 0x36;
                bool defer = ctx.batch_state_writes && (kind == 3 || state_only(op)
                    || (ctx.defer_disjoint_writes && draw && ctx.framebuffers.disjoint(pending_low, pending_high)));
                if (!defer) flush();
                if (kind == 2) {
                    uint32_t words[44];
                    for (uint32_t i = 0; i < length / 4; i++) words[i] = le32(data + i * 4);
                    ctx.gpu->enqueue_command(length / 4, words); ctx.stats.commands++;
                    ctx.framebuffers.command(op, words[0], words[1]);
                } else for (unsigned reg = 0; reg < 14; reg++) {
                    auto *p = data + reg * 4;
                    uint32_t value = uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
                    ctx.gpu->set_vi_register(RDP::VIRegister(reg), value);
                }
            }
            at += length;
        }
        flush(); ctx.gpu->flush(); *timeline = ctx.gpu->signal_timeline();
        ctx.stats.last_timeline = *timeline; ctx.stats.submissions++; ctx.healthy();
    });
}
int ed_n64_gpu_wait(uint64_t handle, uint64_t timeline, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] { get(handle).wait(timeline); });
}
int ed_n64_gpu_readback(uint64_t handle, uint64_t timeline, uint8_t *ram, uint32_t size,
    uint8_t *hidden, uint32_t hidden_bytes, uint8_t *tmem, uint32_t tmem_bytes, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] {
        auto &ctx = get(handle);
        require(ram && hidden && tmem && size == ram_size && hidden_bytes == hidden_size && tmem_bytes == 4096, "Invalid readback buffers");
        require(timeline == ctx.stats.last_timeline && timeline != 0, "Readback requires latest submitted timeline");
        ctx.wait(timeline);
        ctx.gpu->begin_read_rdram(); swap_words(ram, ctx.ram.get(), ram_size);
        swap_halfwords(hidden, static_cast<const uint8_t *>(ctx.gpu->begin_read_hidden_rdram()), hidden_size);
        std::memcpy(tmem, ctx.gpu->get_tmem(), 4096);
        ctx.stats.readback_bytes += ram_size + hidden_size + 4096;
        ctx.gpu->begin_frame_context(); ctx.healthy();
    });
}
int ed_n64_gpu_get_stats(uint64_t handle, ed_n64_gpu_stats *stats, uint32_t size, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] {
        auto &ctx = get(handle); require(stats && size == sizeof(*stats), "Invalid stats structure");
        *stats = ctx.stats; stats->validation_errors = ctx.errors.load();
    });
}
int ed_n64_gpu_device_name(uint64_t handle, char *name, uint32_t size, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] {
        auto &ctx = get(handle); require(name && size >= VK_MAX_PHYSICAL_DEVICE_NAME_SIZE, "Device name buffer must hold 256 bytes");
        copy_string(name, size, ctx.device->get_gpu_properties().deviceName);
    });
}
int ed_n64_gpu_destroy(uint64_t handle, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] { get(handle); contexts.erase(handle); });
}
