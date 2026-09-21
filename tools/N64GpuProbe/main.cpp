#include "conformance_utils.hpp"
#include "global_managers_init.hpp"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstdio>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

// Test executable only. Angrylion is an independent rendering oracle here,
// not an emulator dependency. The production backend boundary is still open.
namespace {
struct ValidationInstanceFactory : Vulkan::InstanceFactory {
    VkInstance create_instance(const VkInstanceCreateInfo *input) override {
        bool validate = !std::getenv("GRANITE_VULKAN_NO_VALIDATION")
            || std::string(std::getenv("GRANITE_VULKAN_NO_VALIDATION")) != "1";
        VkInstanceCreateInfo info = *input;
        std::vector<const char *> extensions(input->ppEnabledExtensionNames,
                                             input->ppEnabledExtensionNames + input->enabledExtensionCount);
        VkValidationFeatureEnableEXT sync = VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
        VkValidationFeaturesEXT features{};
        features.sType = VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT;
        features.pNext = info.pNext;
        features.enabledValidationFeatureCount = 1;
        features.pEnabledValidationFeatures = &sync;
        if (validate) {
            bool layer = false;
            for (unsigned i = 0; i < input->enabledLayerCount; i++)
                layer |= std::string(input->ppEnabledLayerNames[i]) == "VK_LAYER_KHRONOS_validation";
            if (!layer) throw std::runtime_error("Khronos validation layer required for this run");
            extensions.push_back(VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME);
            info.ppEnabledExtensionNames = extensions.data();
            info.enabledExtensionCount = unsigned(extensions.size());
            info.pNext = &features;
        }
        VkInstance instance = VK_NULL_HANDLE;
        if (vkCreateInstance(&info, nullptr, &instance) != VK_SUCCESS)
            throw std::runtime_error("vkCreateInstance failed");
        std::cout << "khronosValidation=" << validate << " synchronizationValidation=" << validate << '\n';
        return instance;
    }
};

struct Fixture : RDP::CommandListenerInterface {
    std::vector<uint8_t> rdram, hidden;
    std::vector<std::vector<uint32_t>> commands;
    bool complete = false, ended = false;
    void set_vi_register(RDP::VIRegister, uint32_t) override { throw std::runtime_error("VI is outside this probe"); }
    void end_frame() override { throw std::runtime_error("VI scanout is outside this probe"); }
    void signal_complete() override { complete = true; }
    void eof() override { ended = true; }
    void command(RDP::Op op, uint32_t count, const uint32_t *words) override {
        unsigned id = unsigned(op);
        unsigned expected = id >= 8 && id <= 15 ? 8 + ((id & 4) ? 16 : 0) + ((id & 2) ? 16 : 0) + ((id & 1) ? 4 : 0)
            : id == 0x24 || id == 0x25 ? 4 : 2;
        if (count != expected || ((words[0] >> 24) & 63) != id || complete)
            throw std::runtime_error("Invalid command in fixture");
        commands.emplace_back(words, words + count);
    }
    void update_rdram(const void *data, size_t size, size_t offset) override { update(rdram, data, size, offset); }
    void update_hidden_rdram(const void *data, size_t size, size_t offset) override { update(hidden, data, size, offset); }
    void update(std::vector<uint8_t> &dst, const void *data, size_t size, size_t offset) {
        if (!commands.empty() || offset != 0 || (size != (8u << 20) && size != (4u << 20)))
            throw std::runtime_error("Only complete initial memory uploads are supported");
        auto *bytes = static_cast<const uint8_t *>(data);
        dst.assign(bytes, bytes + size);
    }
};

size_t differences(const std::vector<uint8_t> &a, const std::vector<uint8_t> &b) {
    if (a.size() != b.size()) throw std::runtime_error("Oracle sizes differ");
    size_t count = 0;
    for (size_t i = 0; i < a.size(); i++) count += a[i] != b[i];
    return count;
}
struct Result { std::vector<uint8_t> rdram, hidden, tmem; };
Result snapshot(RDP::ReplayerDriver &driver) {
    driver.idle();
    driver.invalidate_caches();
    auto *ram = driver.get_rdram(), *hidden = driver.get_hidden_rdram(), *tmem = driver.get_tmem();
    return {{ram, ram + driver.get_rdram_size()}, {hidden, hidden + driver.get_hidden_rdram_size()}, {tmem, tmem + 4096}};
}
double replay(RDP::ReplayerDriver &driver, const Fixture &fixture) {
    driver.update_rdram(fixture.rdram.data(), fixture.rdram.size(), 0);
    driver.update_hidden_rdram(fixture.hidden.data(), fixture.hidden.size(), 0);
    driver.idle();
    auto start = std::chrono::steady_clock::now();
    for (auto &words : fixture.commands)
        driver.command(RDP::Op((words[0] >> 24) & 63), unsigned(words.size()), words.data());
    driver.signal_complete();
    driver.idle(); // Time includes enqueue, GPU completion and the host wait.
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
}
void save_frame(const std::filesystem::path &path, const Result &result, const RDP::Interface &iface) {
    unsigned width = iface.fb.width, bytes = iface.fb.size == 2 ? 2 : iface.fb.size == 3 ? 4 : 0;
    if (!bytes || !width || uint64_t(iface.fb.addr) + uint64_t(width) * 240 * bytes > result.rdram.size())
        throw std::runtime_error("Unsupported output framebuffer");
    std::ofstream out(path, std::ios::binary);
    out.exceptions(std::ios::failbit | std::ios::badbit);
    out << "P6\n" << width << " 240\n255\n";
    for (unsigned pixel = 0; pixel < width * 240; pixel++) {
        unsigned address = iface.fb.addr + pixel * bytes;
        if (bytes == 2) {
            uint16_t color = uint16_t(result.rdram[address ^ 3]) << 8 | result.rdram[(address + 1) ^ 3];
            for (unsigned shift : {11u, 6u, 1u}) {
                unsigned channel = (color >> shift) & 31;
                out.put(char((channel << 3) | (channel >> 2)));
            }
        } else for (unsigned c = 0; c < 3; c++) out.put(char(result.rdram[(address + c) ^ 3]));
    }
}

#include "journal.hpp"

int run(int argc, char **argv) {
    if (argc < 3 || argc > 4) throw std::runtime_error("Usage: n64-gpu-probe frame.rdp|journal.bin NEW_OUTPUT [--bench|--negative-control|--validate-journal|--journal-reference]");
    bool bench = argc == 4 && std::string(argv[3]) == "--bench";
    bool negative = argc == 4 && std::string(argv[3]) == "--negative-control";
    bool parse_only = argc == 4 && std::string(argv[3]) == "--validate-journal";
    bool reference_only = argc == 4 && std::string(argv[3]) == "--journal-reference";
    if (argc == 4 && !bench && !negative && !parse_only && !reference_only) throw std::runtime_error("Unknown option");
    std::filesystem::path output(argv[2]);
    if (std::filesystem::exists(output)) throw std::runtime_error("Use a new output directory");
    std::ifstream input(argv[1], std::ios::binary);
    char magic[8] = {};
    input.read(magic, 8);
    if (parse_only || reference_only || std::string(magic, 8) == "NRDPJ001") {
        if (bench) throw std::runtime_error("Ordered journal replay is a correctness check, not a benchmark");
        return run_journal(argv[1], output, negative, parse_only, reference_only);
    }
    RDP::DumpPlayer player;
    Fixture fixture;
    if (!player.load_dump(argv[1])) throw std::runtime_error("Invalid RDPDUMP2 file");
    player.set_command_interface(&fixture);
    while (player.iterate()) {}
    if (!fixture.ended || !fixture.complete || fixture.commands.empty() || fixture.rdram.empty() || fixture.hidden.empty()
        || ((fixture.commands.back()[0] >> 24) & 63) != 0x29)
        throw std::runtime_error("Incomplete fixture");

    std::atomic<unsigned> validationErrors{0};
    ValidationInstanceFactory instanceFactory;
    RDP::ReplayerState state;
    state.context.set_instance_factory(&instanceFactory);
    if (!state.init_common()) throw std::runtime_error("Cannot initialize Vulkan");
    state.context.set_notification_callback([&](const char *) { validationErrors++; });
    auto &properties = state.device->get_gpu_properties();
    if (properties.deviceType != VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)
        throw std::runtime_error("Probe requires a discrete GPU; refusing software-device timings");
    auto gpu = RDP::create_replayer_driver_parallel(*state.device, player, state.iface);
    std::filesystem::create_directories(output);
    std::cout << "device=" << properties.deviceName << " commands=" << fixture.commands.size() << '\n';
    double firstMs = replay(*gpu, fixture);
    auto actual = snapshot(*gpu);
    save_frame(output / "gpu.ppm", actual, state.iface);
    std::cout << "coldFrameMs=" << firstMs << " (includes first-use shader compilation)\n";
    if (bench) {
        std::vector<double> times, transferTimes;
        for (int i = -10; i < 40; i++) {
            auto transferStart = std::chrono::steady_clock::now();
            double ms = replay(*gpu, fixture);
            auto next = snapshot(*gpu);
            double transferMs = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - transferStart).count();
            if (differences(actual.rdram, next.rdram) || differences(actual.hidden, next.hidden) || differences(actual.tmem, next.tmem))
                throw std::runtime_error("GPU replay is not deterministic across restored memory");
            state.device->next_frame_context();
            if (i >= 0) { times.push_back(ms); transferTimes.push_back(transferMs); }
        }
        std::sort(times.begin(), times.end());
        std::sort(transferTimes.begin(), transferTimes.end());
        std::cout << "gpuFrameRuns=40 medianMs=" << (times[19] + times[20]) * .5
                  << " minMs=" << times.front() << " maxMs=" << times.back()
                  << " memoryRestoreAndHashOutsideTimer=true deterministic=true\n";
        std::cout << "fixtureRestoreRenderReadbackMedianMs=" << (transferTimes[19] + transferTimes[20]) * .5
                  << " includesInitialMemoryUploadsAndOutputCopies=true excludesManagedByteSwapAndVI=true\n";
        state.device->wait_idle();
        std::cout << "vulkanValidationErrors=" << validationErrors.load() << '\n';
        state.context.set_notification_callback({});
        return validationErrors.load() ? 3 : 0;
    }
    auto reference = RDP::create_replayer_driver_angrylion(player, state.iface);
    replay(*reference, fixture);
    auto expected = snapshot(*reference);
    save_frame(output / "angrylion.ppm", expected, state.iface);
    size_t dramDiff = differences(expected.rdram, actual.rdram), hiddenDiff = differences(expected.hidden, actual.hidden), tmemDiff = differences(expected.tmem, actual.tmem);
    std::cout << "rdramMismatchBytes=" << dramDiff << " hiddenMismatchBytes=" << hiddenDiff
              << " tmemMismatchBytes=" << tmemDiff << '\n';
    if (dramDiff || hiddenDiff || tmemDiff) return 2;
    if (negative) {
        expected.rdram.at(state.iface.fb.addr ^ 3) ^= 1;
        if (differences(expected.rdram, actual.rdram) != 1) throw std::runtime_error("Negative control missed injected error");
        std::cout << "negativeControl=passed mismatchBytes=1\n";
    }
    std::cout << "gpuAngrylionConformance=passed bytes=" << actual.rdram.size() + actual.hidden.size() + actual.tmem.size() << '\n';
    state.device->wait_idle();
    std::cout << "vulkanValidationErrors=" << validationErrors.load() << '\n';
    state.context.set_notification_callback({});
    return validationErrors.load() ? 3 : 0;
}
}

int main(int argc, char **argv) {
    Granite::Global::init();
    int result;
    try { result = run(argc, argv); }
    catch (const std::exception &e) { std::cerr << e.what() << '\n'; result = 1; }
    Granite::Global::deinit();
    return result;
}
