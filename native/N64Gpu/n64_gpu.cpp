#include "n64_gpu.h"
#include "n64_gpu_state.hpp"
#include "rdp_device.hpp"
#include "context.hpp"
#include "global_managers_init.hpp"
#include "filesystem.hpp"
#include "thread_group.hpp"
#include "thread_id.hpp"
#include "aligned_alloc.hpp"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <stdexcept>
#include <vector>
#if N64_GPU_PROFILE
#include <chrono>
#endif

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
    void bind() const {
        Granite::Global::set_thread_context(*state);
        // Managed callers may enter on a different .NET thread than the one
        // which created the Vulkan device. The ABI mutex serializes all such
        // entries, so they share Granite's main-thread command-buffer index.
        Util::register_thread_index(0);
    }
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
    uint32_t color = 0, depth = 0, width = 0, pixel_bytes = 0, scissor_yhi = 0;
    bool have_depth = false, have_scissor = false;
    void command(uint32_t op, uint32_t w0, uint32_t w1) {
        if (op == 0x3f) {
            color = w1 & 0xffffff; width = (w0 & 1023) + 1;
            pixel_bytes = 1u << std::max(0, int((w0 >> 19) & 3) - 1);
        } else if (op == 0x3e) { depth = w1 & 0xffffff; have_depth = true; }
        else if (op == 0x2d) { scissor_yhi = w1 & 4095; have_scissor = true; }
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
    bool disjoint_narrow(uint32_t low, uint32_t high) const {
        if (!width || !have_depth || !have_scissor || !scissor_yhi) return false;
        uint32_t height = (scissor_yhi + 3) / 4;
        return !overlaps(low, high, color, width * height * pixel_bytes)
            && !overlaps(low, high, depth, width * height * 2);
    }
};
// Narrow read bounds for the pinned renderer's texture uploads.
// load_tile_iteration() keeps Block S/T in full pixels (not quarter pixels),
// rounds width to four pixels, and sets upload height to one. With a matching
// 16-bit non-YUV tile and zero tile stride, update_tmem_16() reads that linear
// span; DXT only permutes halfwords within each eight-byte group. Other tile
// layouts and out-of-installed-RAM reads retain the barrier. LoadTile and
// single-row TLUT have their own source bounds below.
struct LoadBlockRange {
    uint32_t address = 0, width = 0, size = 0;
    struct Tile { uint32_t size = 0, format = 0, stride = 0; bool known = false; } tiles[8];
    void command(uint32_t op, uint32_t w0, uint32_t w1) {
        if (op == 0x3d) {
            address = w1 & 0xffffff; width = (w0 & 1023) + 1; size = (w0 >> 19) & 3;
        } else if (op == 0x35) {
            tiles[(w1 >> 24) & 7] = { (w0 >> 19) & 3, (w0 >> 21) & 7, (w0 >> 9) & 511, true };
        }
    }
    bool block_range(uint32_t w0, uint32_t w1, uint32_t &begin, uint32_t &end) const {
        const auto &tile = tiles[(w1 >> 24) & 7];
        if (!width || size != 2 || !tile.known || tile.size != 2 || tile.format == 1 || tile.format > 4 || tile.stride) return false;
        uint32_t s = (w0 >> 12) & 4095, t = w0 & 4095;
        uint32_t pixels = (((w1 >> 12) & 4095) - s + 1) & 4095;
        if (!pixels || pixels > 2048) return false;
        begin = address + 2 * (s + width * t); end = begin + 2 * ((pixels + 3) & ~3u);
        if ((begin & 1) || end > ram_size) return false;
        begin &= ~3u; end = (end + 3) & ~3u;
        return true;
    }
    bool disjoint(uint32_t low, uint32_t high, uint32_t w0, uint32_t w1) const {
        uint32_t begin, end;
        if (!block_range(w0, w1, begin, end)) return false;
        return low >= end || high <= begin;
    }
    bool tlut_range(uint32_t w0, uint32_t w1, uint32_t &begin, uint32_t &end) const {
        // Pinned load_tile_iteration() gives 16-bit TLUT uploads an effective
        // width of exactly pixel_count. update_tmem_lut() rejects every source
        // index beyond it, regardless of the destination's TMEM wraparound.
        // The RGBA destination tile can be 4/8/16-bit: all three shader cases
        // bound their source index by the same 16-bit effective width.
        // Keep the proof to aligned, single-row loads. Other source sizes,
        // tile layouts and reads outside installed RAM still wait.
        const auto &tile = tiles[(w1 >> 24) & 7];
        if (!width || size != 2 || !tile.known || tile.size > 2 || tile.format != 0 || tile.stride) return false;
        uint32_t s = ((w0 >> 12) & 4095) >> 2, t = (w0 & 4095) >> 2;
        if (((w1 & 4095) >> 2) != t) return false;
        uint32_t pixels = (((((w1 >> 12) & 4095) >> 2) - s) + 1) & 4095;
        if (!pixels || pixels > 256) return false;
        begin = address + 2 * (s + width * t); end = begin + 2 * pixels;
        if ((begin & 1) || end > ram_size) return false;
        begin &= ~3u; end = (end + 3) & ~3u;
        return true;
    }
    bool disjoint_tlut(uint32_t low, uint32_t high, uint32_t w0, uint32_t w1) const {
        uint32_t begin, end;
        if (!tlut_range(w0, w1, begin, end)) return false;
        return low >= end || high <= begin;
    }
    bool disjoint_tile(uint32_t low, uint32_t high, uint32_t w0, uint32_t w1) const {
        // For matching 8/16-bit source/tile sizes, load_tile_iteration()
        // rounds each source row to eight bytes. update_tmem_16() bounds
        // upload_y by height - 1 and upload_x by that rounded width, even
        // when TMEM rows overlap or wrap. Enclose all source rows; unknown
        // layouts and wrapped coordinates retain the barrier.
        const auto &tile = tiles[(w1 >> 24) & 7];
        if (!width || (size != 1 && size != 2) || !tile.known || tile.size != size
            || tile.format == 1 || tile.format > 4) return false;
        uint32_t s = ((w0 >> 12) & 4095) >> 2, t = (w0 & 4095) >> 2;
        uint32_t last_s = ((w1 >> 12) & 4095) >> 2;
        uint32_t last_t = (w1 & 4095) >> 2;
        if (last_t < t || last_s < s) return false;
        uint32_t pixel_bytes = 1u << (size - 1);
        uint32_t begin = address + pixel_bytes * (s + width * t);
        uint32_t end = begin + pixel_bytes * width * (last_t - t)
            + (((last_s - s + 1) * pixel_bytes + 7) & ~7u);
        if ((begin & 1) || end > ram_size) return false;
        begin &= ~3u; end = (end + 3) & ~3u;
        return low >= end || high <= begin;
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
    bool defer_disjoint_load_blocks;
    bool narrow_triangle_writes;
    bool track_texture_reads;
    bool triangle_epoch_safe = false, triangle_epoch_has_draw = false;
    uint32_t triangle_read_low = ram_size, triangle_read_high = 0;
    FramebufferRanges framebuffers;
    LoadBlockRange texture;
    uint32_t vi_registers[14] = {};
    // Derived readback metadata, not checkpoint state. The live caller owns
    // CPU writes in its RDRAM; only GPU-written pages need to be returned.
    static constexpr uint32_t page_size = 4096, page_count = ram_size / page_size;
    std::array<uint64_t, page_count / 64> gpu_dirty_pages{};
    bool gpu_targets_marked = false, live_readback_initialized = false;
#if N64_GPU_PROFILE
    std::array<uint64_t, 65> profile_barrier_count{};
    std::array<uint64_t, 65> profile_idle_ns{};
    std::array<uint64_t, 65> profile_begin_read_ns{};
    uint64_t profile_full_sync_disjoint_count = 0;
    uint64_t profile_full_sync_disjoint_idle_ns = 0;
    uint64_t profile_full_sync_disjoint_writes = 0;
    uint64_t profile_full_sync_total_writes = 0;
    uint64_t profile_submit_ns = 0, profile_validation_ns = 0, profile_enqueue_ns = 0;
    uint64_t profile_tracking_ns = 0, profile_final_flush_ns = 0;
    uint64_t profile_readback_wait_ns = 0, profile_readback_begin_ns = 0;
    uint64_t profile_readback_copy_ns = 0, profile_readback_hidden_ns = 0;
    uint64_t profile_readback_frame_ns = 0;
    uint64_t profile_triangle_narrow_defers = 0;
    uint64_t profile_triangle_reject_unsafe = 0;
    uint64_t profile_triangle_reject_oversize = 0;
    uint64_t profile_triangle_reject_overlap = 0;
    std::array<uint64_t, 65> profile_triangle_unsafe_causes{};
    std::array<uint64_t, 65> profile_triangle_unsafe_rejects{};
    unsigned profile_triangle_unsafe_cause = 0;
#endif

    void mark_page_range(uint32_t begin, uint32_t end) {
        uint32_t page = begin / page_size, limit = (end + page_size - 1) / page_size;
        while (page < limit) {
            uint32_t bit = page & 63, count = std::min(limit - page, 64u - bit);
            gpu_dirty_pages[page >> 6] |= (UINT64_MAX >> (64 - count)) << bit;
            page += count;
        }
    }
    void mark_target(uint32_t address, uint32_t bytes) {
        uint32_t begin = address & (ram_size - 4), end = begin + bytes + 4;
        mark_page_range(begin, std::min(end, ram_size));
        if (end > ram_size) mark_page_range(0, end - ram_size);
    }
    void mark_draw_targets() {
        if (gpu_targets_marked) return;
        if (!framebuffers.width || !framebuffers.have_depth) gpu_dirty_pages.fill(UINT64_MAX);
        else {
            mark_target(framebuffers.color, framebuffers.width * 1024 * framebuffers.pixel_bytes);
            mark_target(framebuffers.depth, framebuffers.width * 1024 * 2);
        }
        gpu_targets_marked = true;
    }
    uint32_t copy_gpu_pages(uint8_t *destination) const {
        uint32_t copied = 0, page = 0;
        while (page < page_count) {
            if (!gpu_dirty_pages[page >> 6]) { page = (page + 64) & ~63u; continue; }
            if (!(gpu_dirty_pages[page >> 6] & (uint64_t(1) << (page & 63)))) { page++; continue; }
            uint32_t begin = page++;
            while (page < page_count && (gpu_dirty_pages[page >> 6] & (uint64_t(1) << (page & 63)))) page++;
            uint32_t offset = begin * page_size, size = (page - begin) * page_size;
            swap_words(destination + offset, ram.get() + offset, size); copied += size;
        }
        return copied;
    }
    uint32_t copy_hidden_pages(uint8_t *destination, const uint8_t *source) const {
        uint32_t copied = 0, page = 0;
        while (page < page_count) {
            if (!gpu_dirty_pages[page >> 6]) { page = (page + 64) & ~63u; continue; }
            if (!(gpu_dirty_pages[page >> 6] & (uint64_t(1) << (page & 63)))) { page++; continue; }
            uint32_t begin = page++;
            while (page < page_count && (gpu_dirty_pages[page >> 6] & (uint64_t(1) << (page & 63)))) page++;
            uint32_t offset = begin * (page_size / 2), size = (page - begin) * (page_size / 2);
            swap_halfwords(destination + offset, source + offset, size); copied += size;
        }
        return copied;
    }

    Context(uint32_t flags, const uint8_t *initial_ram, const uint8_t *initial_hidden)
        : factory((flags & ED_N64_GPU_VALIDATE) != 0),
          batch_state_writes((flags & (ED_N64_GPU_BATCH_STATE_WRITES | ED_N64_GPU_DEFER_DISJOINT_WRITES | ED_N64_GPU_DEFER_DISJOINT_LOAD_BLOCKS)) != 0),
          defer_disjoint_writes((flags & (ED_N64_GPU_DEFER_DISJOINT_WRITES | ED_N64_GPU_DEFER_DISJOINT_LOAD_BLOCKS)) != 0),
          defer_disjoint_load_blocks((flags & ED_N64_GPU_DEFER_DISJOINT_LOAD_BLOCKS) != 0),
          narrow_triangle_writes((flags & ED_N64_GPU_NARROW_TRIANGLE_WRITES) != 0),
          track_texture_reads((flags & ED_N64_GPU_TRACK_TEXTURE_READS) != 0) {
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
        triangle_epoch_safe = true;
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
#if N64_GPU_PROFILE
        std::fprintf(stderr, "gpuNativeProfile submitMs=%.3f validateMs=%.3f enqueueMs=%.3f trackingMs=%.3f finalFlushMs=%.3f\n",
            profile_submit_ns / 1000000.0,
            profile_validation_ns / 1000000.0, profile_enqueue_ns / 1000000.0,
            profile_tracking_ns / 1000000.0, profile_final_flush_ns / 1000000.0);
        std::fprintf(stderr, "gpuNativeReadback waitMs=%.3f beginMs=%.3f copyMs=%.3f hiddenMs=%.3f frameMs=%.3f\n",
            profile_readback_wait_ns / 1000000.0, profile_readback_begin_ns / 1000000.0,
            profile_readback_copy_ns / 1000000.0, profile_readback_hidden_ns / 1000000.0,
            profile_readback_frame_ns / 1000000.0);
        for (unsigned op = 0; op < profile_barrier_count.size(); op++)
            if (profile_barrier_count[op])
                std::fprintf(stderr, "gpuNativeBarrier cause=%u count=%llu idleMs=%.3f beginReadMs=%.3f\n",
                    op, static_cast<unsigned long long>(profile_barrier_count[op]),
                    profile_idle_ns[op] / 1000000.0, profile_begin_read_ns[op] / 1000000.0);
        std::fprintf(stderr, "gpuNativeFullSyncDisjoint count=%llu idleMs=%.3f writes=%llu totalWrites=%llu\n",
            static_cast<unsigned long long>(profile_full_sync_disjoint_count),
            profile_full_sync_disjoint_idle_ns / 1000000.0,
            static_cast<unsigned long long>(profile_full_sync_disjoint_writes),
            static_cast<unsigned long long>(profile_full_sync_total_writes));
        std::fprintf(stderr, "gpuNativeTriangleNarrowDefers count=%llu\n",
            static_cast<unsigned long long>(profile_triangle_narrow_defers));
        std::fprintf(stderr, "gpuNativeTriangleNarrowReject unsafeEpoch=%llu oversizedBatch=%llu targetOverlap=%llu\n",
            static_cast<unsigned long long>(profile_triangle_reject_unsafe),
            static_cast<unsigned long long>(profile_triangle_reject_oversize),
            static_cast<unsigned long long>(profile_triangle_reject_overlap));
        for (unsigned op = 0; op < profile_triangle_unsafe_causes.size(); op++)
            if (profile_triangle_unsafe_causes[op] || profile_triangle_unsafe_rejects[op])
                std::fprintf(stderr, "gpuNativeTriangleUnsafe cause=%u transitions=%llu rejectedTriangles=%llu\n",
                    op, static_cast<unsigned long long>(profile_triangle_unsafe_causes[op]),
                    static_cast<unsigned long long>(profile_triangle_unsafe_rejects[op]));
#endif
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
// FULL_SYNC and public submit boundaries always flush writes. The separate
// Texture-load whitelists must prove the read disjoint before deferring it.
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
        require(abi == ED_N64_GPU_ABI && (flags & ~127u) == 0, "Unsupported ABI or flags");
        require(!(flags & ED_N64_GPU_TRACK_TEXTURE_READS) || (flags & ED_N64_GPU_NARROW_TRIANGLE_WRITES),
            "Texture-read tracking requires narrow triangle writes");
        require(handle && ram && hidden && size == ram_size && hidden_bytes == hidden_size, "Invalid initial memory buffers");
        require(next_handle <= uint64_t(INT64_MAX), "GPU handle space exhausted");
        auto context = std::make_unique<Context>(flags, ram, hidden);
        uint64_t id = next_handle++; contexts.emplace(id, std::move(context)); *handle = id;
    });
}
int ed_n64_gpu_submit(uint64_t handle, const uint8_t *bytes, uint32_t size, uint64_t *timeline, char *error, uint32_t capacity) {
    if (timeline) *timeline = 0;
    return boundary(error, capacity, [&] {
        auto &ctx = get(handle); require(timeline, "Missing timeline output");
#if N64_GPU_PROFILE
        auto submit_start = std::chrono::steady_clock::now();
#endif
        validate_batch(bytes, size); ctx.healthy();
#if N64_GPU_PROFILE
        ctx.profile_validation_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - submit_start).count();
#endif
        struct Patch { uint32_t address, size; const uint8_t *data; };
        std::vector<Patch> pending;
        uint32_t pending_low = ram_size, pending_high = 0;
        auto flush = [&](unsigned cause) {
            if (pending.empty()) return;
#if N64_GPU_PROFILE
            // Diagnostic upper bound only: framebuffer disjointness does not
            // prove that earlier texture reads or unknown commands are safe.
            bool full_sync_disjoint = cause == 0x29 &&
                std::all_of(pending.begin(), pending.end(), [&](const Patch &patch) {
                    return ctx.framebuffers.disjoint(patch.address, patch.address + patch.size);
                });
            if (cause == 0x29) {
                ctx.profile_full_sync_total_writes += pending.size();
                if (full_sync_disjoint) {
                    ctx.profile_full_sync_disjoint_count++;
                    ctx.profile_full_sync_disjoint_writes += pending.size();
                }
            }
            auto idle_start = std::chrono::steady_clock::now();
#else
            (void)cause;
#endif
            ctx.gpu->idle();
#if N64_GPU_PROFILE
            ctx.profile_barrier_count[cause]++;
            ctx.profile_idle_ns[cause] += std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - idle_start).count();
            if (full_sync_disjoint)
                ctx.profile_full_sync_disjoint_idle_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
                    std::chrono::steady_clock::now() - idle_start).count();
            idle_start = std::chrono::steady_clock::now();
#endif
            ctx.gpu->begin_read_rdram();
#if N64_GPU_PROFILE
            ctx.profile_begin_read_ns[cause] += std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - idle_start).count();
#endif
            for (auto &patch : pending) patch_be(ctx.ram.get(), patch.address, patch.data, patch.size);
            ctx.gpu->end_write_rdram(); ctx.stats.write_barriers++; pending.clear();
            pending_low = ram_size; pending_high = 0;
            // The idle completed every previously queued GPU access. A new
            // triangle-only epoch can now establish narrower write bounds.
            ctx.triangle_epoch_safe = true;
            ctx.triangle_epoch_has_draw = false;
            ctx.triangle_read_low = ram_size;
            ctx.triangle_read_high = 0;
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
                    || (ctx.defer_disjoint_writes && draw && ctx.framebuffers.disjoint(pending_low, pending_high))
                    || (ctx.defer_disjoint_load_blocks && op == 0x33
                        && ctx.texture.disjoint(pending_low, pending_high, le32(data), le32(data + 4)))
                    || (ctx.defer_disjoint_load_blocks && op == 0x30
                        && ctx.texture.disjoint_tlut(pending_low, pending_high, le32(data), le32(data + 4)))
                    || (ctx.defer_disjoint_load_blocks && op == 0x34
                        && ctx.texture.disjoint_tile(pending_low, pending_high, le32(data), le32(data + 4))));
                // The cheap enclosing interval can straddle a texture even
                // though none of the actual CPU patches touches it. Reuse the
                // same source proof for every patch in a small pending list.
                // Cap the scan so large or unproven batches keep a cheap wait.
                if (!defer && ctx.defer_disjoint_load_blocks && !pending.empty() && pending.size() <= 4096
                    && (op == 0x30 || op == 0x33 || op == 0x34)) {
                    defer = std::all_of(pending.begin(), pending.end(), [&](const Patch &patch) {
                        uint32_t high = patch.address + patch.size, w0 = le32(data), w1 = le32(data + 4);
                        if (op == 0x30) return ctx.texture.disjoint_tlut(patch.address, high, w0, w1);
                        if (op == 0x33) return ctx.texture.disjoint(patch.address, high, w0, w1);
                        return ctx.texture.disjoint_tile(patch.address, high, w0, w1);
                    });
                }
                // A queued texture upload may still read its source after CPU
                // patches were staged. The tracked interval is conservative;
                // drain before applying any patch that could touch it.
                if (ctx.track_texture_reads && defer && ctx.triangle_read_low != ram_size)
                    for (const Patch &patch : pending)
                        if (patch.address < ctx.triangle_read_high
                            && patch.address + patch.size > ctx.triangle_read_low) {
                            defer = false;
                            break;
                        }
                if (!defer && ctx.narrow_triangle_writes && op == 0x0f && !pending.empty()) {
#if N64_GPU_PROFILE
                    if (!ctx.triangle_epoch_safe) {
                        ctx.profile_triangle_reject_unsafe++;
                        ctx.profile_triangle_unsafe_rejects[ctx.profile_triangle_unsafe_cause]++;
                    }
                    else if (pending.size() > 4096) ctx.profile_triangle_reject_oversize++;
#endif
                }
                if (!defer && ctx.narrow_triangle_writes && op == 0x0f
                    && ctx.triangle_epoch_safe && pending.size() <= 4096) {
                    defer = std::all_of(pending.begin(), pending.end(), [&](const Patch &patch) {
                        uint32_t high = patch.address + patch.size;
                        return ctx.framebuffers.disjoint_narrow(patch.address, high)
                            && (!ctx.track_texture_reads || ctx.triangle_read_low == ram_size
                                || patch.address >= ctx.triangle_read_high || high <= ctx.triangle_read_low);
                    });
#if N64_GPU_PROFILE
                    if (defer && !pending.empty()) ctx.profile_triangle_narrow_defers++;
                    else if (!defer && !pending.empty()) ctx.profile_triangle_reject_overlap++;
#endif
                }
                if (!defer) flush(op);
                if (kind == 2) {
                    uint32_t words[44];
                    for (uint32_t i = 0; i < length / 4; i++) words[i] = le32(data + i * 4);
                    if (draw) ctx.mark_draw_targets();
                    // State, texture-upload and sync commands do not introduce
                    // RDRAM writes. Preserve a full readback for unknown commands.
                    else if (!state_only(op) && op != 0x29 && op != 0x30 && op != 0x33 && op != 0x34)
                        ctx.gpu_dirty_pages.fill(UINT64_MAX);
#if N64_GPU_PROFILE
                    auto phase_start = std::chrono::steady_clock::now();
#endif
                    ctx.gpu->enqueue_command(length / 4, words); ctx.stats.commands++;
#if N64_GPU_PROFILE
                    ctx.profile_enqueue_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
                        std::chrono::steady_clock::now() - phase_start).count();
                    phase_start = std::chrono::steady_clock::now();
#endif
                    ctx.framebuffers.command(op, words[0], words[1]);
                    if (ctx.narrow_triangle_writes && ctx.triangle_epoch_safe) {
                        if (op == 0x0f) ctx.triangle_epoch_has_draw = true;
                        else if ((op == 0x3f || op == 0x3e || op == 0x2d) && ctx.triangle_epoch_has_draw) {
                            ctx.triangle_epoch_safe = false;
#if N64_GPU_PROFILE
                            ctx.profile_triangle_unsafe_cause = op;
                            ctx.profile_triangle_unsafe_causes[op]++;
#endif
                        }
                        else if ((op == 0x30 || op == 0x33) && ctx.track_texture_reads) {
                            uint32_t begin, end;
                            bool known = op == 0x33
                                ? ctx.texture.block_range(words[0], words[1], begin, end)
                                : ctx.texture.tlut_range(words[0], words[1], begin, end);
                            if (known) {
                                ctx.triangle_read_low = std::min(ctx.triangle_read_low, begin);
                                ctx.triangle_read_high = std::max(ctx.triangle_read_high, end);
                            } else {
                                ctx.triangle_epoch_safe = false;
#if N64_GPU_PROFILE
                                ctx.profile_triangle_unsafe_cause = op;
                                ctx.profile_triangle_unsafe_causes[op]++;
#endif
                            }
                        }
                        else if (!state_only(op) && op != 0x29) {
                            ctx.triangle_epoch_safe = false;
#if N64_GPU_PROFILE
                            ctx.profile_triangle_unsafe_cause = op;
                            ctx.profile_triangle_unsafe_causes[op]++;
#endif
                        }
                    }
                    if (op == 0x3f || op == 0x3e) ctx.gpu_targets_marked = false;
                    ctx.texture.command(op, words[0], words[1]);
#if N64_GPU_PROFILE
                    ctx.profile_tracking_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
                        std::chrono::steady_clock::now() - phase_start).count();
#endif
                } else {
                    if (ctx.narrow_triangle_writes) {
#if N64_GPU_PROFILE
                        if (ctx.triangle_epoch_safe) {
                            ctx.profile_triangle_unsafe_cause = 64;
                            ctx.profile_triangle_unsafe_causes[64]++;
                        }
#endif
                        ctx.triangle_epoch_safe = false;
                    }
                    for (unsigned reg = 0; reg < 14; reg++) {
                        auto *p = data + reg * 4;
                        uint32_t value = uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
                        ctx.gpu->set_vi_register(RDP::VIRegister(reg), value);
                        ctx.vi_registers[reg] = value;
                    }
                }
            }
            at += length;
        }
        flush(64);
#if N64_GPU_PROFILE
        auto final_flush_start = std::chrono::steady_clock::now();
#endif
        ctx.gpu->flush(); *timeline = ctx.gpu->signal_timeline();
#if N64_GPU_PROFILE
        ctx.profile_final_flush_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - final_flush_start).count();
        ctx.profile_submit_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - submit_start).count();
#endif
        ctx.stats.last_timeline = *timeline; ctx.stats.submissions++; ctx.healthy();
    });
}
int ed_n64_gpu_wait(uint64_t handle, uint64_t timeline, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] { get(handle).wait(timeline); });
}
static int readback(uint64_t handle, uint64_t timeline, uint8_t *ram, uint32_t size,
    uint8_t *hidden, uint32_t hidden_bytes, uint8_t *tmem, uint32_t tmem_bytes, bool live, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] {
        auto &ctx = get(handle);
        require(ram && hidden && tmem && size == ram_size && hidden_bytes == hidden_size && tmem_bytes == 4096, "Invalid readback buffers");
        require(timeline == ctx.stats.last_timeline && timeline != 0, "Readback requires latest submitted timeline");
#if N64_GPU_PROFILE
        auto phase_start = std::chrono::steady_clock::now();
#endif
        ctx.wait(timeline);
#if N64_GPU_PROFILE
        ctx.profile_readback_wait_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - phase_start).count();
        phase_start = std::chrono::steady_clock::now();
#endif
        ctx.gpu->begin_read_rdram();
#if N64_GPU_PROFILE
        ctx.profile_readback_begin_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - phase_start).count();
        phase_start = std::chrono::steady_clock::now();
#endif
        uint32_t copied = ram_size;
        if (live && ctx.live_readback_initialized) copied = ctx.copy_gpu_pages(ram);
        else swap_words(ram, ctx.ram.get(), ram_size);
#if N64_GPU_PROFILE
        ctx.profile_readback_copy_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - phase_start).count();
        phase_start = std::chrono::steady_clock::now();
#endif
        const auto *hidden_source = static_cast<const uint8_t *>(ctx.gpu->begin_read_hidden_rdram());
        uint32_t hidden_copied = hidden_size;
        if (live && ctx.live_readback_initialized)
            hidden_copied = ctx.copy_hidden_pages(hidden, hidden_source);
        else swap_halfwords(hidden, hidden_source, hidden_size);
        std::memcpy(tmem, ctx.gpu->get_tmem(), 4096);
#if N64_GPU_PROFILE
        ctx.profile_readback_hidden_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - phase_start).count();
        phase_start = std::chrono::steady_clock::now();
#endif
        ctx.stats.readback_bytes += copied + hidden_copied + 4096;
        ctx.gpu->begin_frame_context(); ctx.healthy();
#if N64_GPU_PROFILE
        ctx.profile_readback_frame_ns += std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now() - phase_start).count();
#endif
        if (live) {
            ctx.gpu_dirty_pages.fill(0); ctx.gpu_targets_marked = false;
            ctx.live_readback_initialized = true;
        }
    });
}
int ed_n64_gpu_readback(uint64_t handle, uint64_t timeline, uint8_t *ram, uint32_t size,
    uint8_t *hidden, uint32_t hidden_bytes, uint8_t *tmem, uint32_t tmem_bytes, char *error, uint32_t capacity) {
    return readback(handle, timeline, ram, size, hidden, hidden_bytes, tmem, tmem_bytes, false, error, capacity);
}
int ed_n64_gpu_readback_live(uint64_t handle, uint64_t timeline, uint8_t *ram, uint32_t size,
    uint8_t *hidden, uint32_t hidden_bytes, uint8_t *tmem, uint32_t tmem_bytes, char *error, uint32_t capacity) {
    return readback(handle, timeline, ram, size, hidden, hidden_bytes, tmem, tmem_bytes, true, error, capacity);
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

int ed_n64_gpu_save_state(uint64_t handle, uint8_t *state, uint32_t capacity,
    uint32_t *written, char *error, uint32_t error_capacity) {
    if (written) *written = 0;
    return boundary(error, error_capacity, [&] {
        require(state && written && capacity >= 16384, "Checkpoint buffer must hold 16 KiB");
        auto &ctx = get(handle);
        auto rdp = RDP::EutherCheckpoint::save(*ctx.gpu);
        std::vector<uint8_t> blob;
        auto word = [&](uint32_t value) { for (unsigned shift = 0; shift < 32; shift += 8) blob.push_back(uint8_t(value >> shift)); };
        word(0x31534745); word(uint32_t(rdp.size()));
        blob.insert(blob.end(), rdp.begin(), rdp.end());
        const auto &f = ctx.framebuffers;
        word(f.color); word(f.depth); word(f.width); word(f.pixel_bytes); word(f.have_depth);
        const auto &t = ctx.texture;
        word(t.address); word(t.width); word(t.size);
        for (auto &tile : t.tiles) { word(tile.size); word(tile.format); word(tile.stride); word(tile.known); }
        for (auto value : ctx.vi_registers) word(value);
        require(blob.size() <= capacity, "Checkpoint exceeds buffer");
        ctx.healthy(); std::memcpy(state, blob.data(), blob.size()); *written = uint32_t(blob.size());
    });
}

int ed_n64_gpu_load_state(uint64_t handle, const uint8_t *state, uint32_t size, char *error, uint32_t capacity) {
    return boundary(error, capacity, [&] {
        require(state && size >= 8 && size <= 16384, "Invalid checkpoint size");
        auto &ctx = get(handle);
        require(ctx.stats.submissions == 0 && ctx.stats.commands == 0, "Checkpoint import requires a fresh context");
        uint32_t at = 0;
        auto word = [&]() { require(size - at >= 4, "Truncated checkpoint"); uint32_t value = le32(state + at); at += 4; return value; };
        require(word() == 0x31534745, "Unsupported GPU checkpoint version");
        uint32_t rdp_size = word();
        require(rdp_size <= size - at, "Invalid RDP checkpoint size");
        const uint8_t *rdp = state + at; at += rdp_size;
        FramebufferRanges f;
        f.color = word(); f.depth = word(); f.width = word(); f.pixel_bytes = word();
        uint32_t known = word(); require(known <= 1, "Invalid framebuffer checkpoint flag"); f.have_depth = known != 0;
        require(f.color <= 0xffffff && f.depth <= 0xffffff && f.width <= 1024 && f.pixel_bytes <= 4, "Invalid framebuffer checkpoint");
        LoadBlockRange t;
        t.address = word(); t.width = word(); t.size = word();
        require(t.address <= 0xffffff && t.width <= 1024 && t.size <= 3, "Invalid texture checkpoint");
        for (auto &tile : t.tiles) {
            tile.size = word(); tile.format = word(); tile.stride = word(); known = word();
            require(tile.size <= 3 && tile.format <= 7 && tile.stride <= 511 && known <= 1, "Invalid tile checkpoint");
            tile.known = known != 0;
        }
        uint32_t vi[14]; for (auto &value : vi) value = word();
        require(at == size, "Trailing checkpoint data");
        RDP::EutherCheckpoint::load(*ctx.gpu, rdp, rdp_size);
        ctx.framebuffers = f; ctx.texture = t;
        for (unsigned reg = 0; reg < 14; reg++) {
            ctx.vi_registers[reg] = vi[reg]; ctx.gpu->set_vi_register(RDP::VIRegister(reg), vi[reg]);
        }
        ctx.healthy();
    });
}
