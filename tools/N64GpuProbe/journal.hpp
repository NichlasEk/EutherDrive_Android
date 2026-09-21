// Included inside the probe namespace. NRDPJ001 is documented in README.md.
// This conservative replay waits before external writes; it is not a speed test.
struct Journal : RDP::CommandInterface {
    static constexpr uint32_t ram_size = 8u << 20, hidden_size = 4u << 20;
    std::ifstream file;
    std::vector<uint8_t> ram, hidden;
    bool from_reset = false, ended = false, need_checkpoint = false, at_checkpoint = false;
    uint64_t sequence = 0, commands = 0, patches = 0, patch_bytes = 0;
    uint32_t frames = 0;
    void set_command_interface(RDP::CommandListenerInterface *) override {}
    size_t get_rdram_size() const override { return ram_size; }
    size_t get_hidden_rdram_size() const override { return hidden_size; }
    static uint32_t u32(const uint8_t *p) {
        return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24;
    }
    static uint64_t u64(const uint8_t *p) { return u32(p) | uint64_t(u32(p + 4)) << 32; }
    std::vector<uint8_t> read(size_t size) {
        std::vector<uint8_t> data(size);
        if (!file.read(reinterpret_cast<char *>(data.data()), std::streamsize(size)))
            throw std::runtime_error("Truncated journal");
        return data;
    }
    explicit Journal(const char *path) : file(path, std::ios::binary) {
        auto magic = read(8);
        if (std::string(magic.begin(), magic.end()) != "NRDPJ001") throw std::runtime_error("Invalid journal magic");
        auto header = read(12);
        if (u32(header.data()) > 1 || u32(header.data() + 4) != ram_size || u32(header.data() + 8) != hidden_size)
            throw std::runtime_error("Unsupported journal header");
        from_reset = u32(header.data()) == 1;
        read(32); // SHA256 of the software start.bin, checked by managed replay.
        auto bytes = read(ram_size); ram.resize(ram_size);
        for (uint32_t i = 0; i < ram_size; i++) ram[i ^ 3] = bytes[i];
        bytes = read(hidden_size); hidden.resize(hidden_size);
        for (uint32_t i = 0; i < hidden_size; i++) hidden[i ^ 1] = bytes[i];
    }
    struct Record { uint8_t kind; std::vector<uint8_t> data; };
    Record next() {
        if (ended) throw std::runtime_error("Journal already ended");
        auto header = read(13);
        uint8_t kind = header[0]; uint32_t size = u32(header.data() + 1);
        if (size > ram_size + 4 || u64(header.data() + 5) != ++sequence)
            throw std::runtime_error("Invalid journal envelope");
        auto data = read(size); auto *p = data.data();
        if (need_checkpoint && kind != 4) throw std::runtime_error("Missing FULL_SYNC checkpoint");
        switch (kind) {
        case 1:
            if (size <= 4 || u32(p) >= ram_size || uint64_t(u32(p)) + size - 4 > ram_size)
                throw std::runtime_error("Invalid RDRAM patch");
            patches++; patch_bytes += size - 4; break;
        case 2: {
            if (size < 8 || size % 4) throw std::runtime_error("Invalid command length");
            uint32_t op = (u32(p) >> 24) & 63;
            uint32_t words = op >= 8 && op <= 15 ? 8 + ((op & 4) ? 16 : 0) + ((op & 2) ? 16 : 0) + ((op & 1) ? 4 : 0)
                : op == 0x24 || op == 0x25 ? 4 : 2;
            if (size != words * 4) throw std::runtime_error("Incomplete RDP command");
            commands++; need_checkpoint = op == 0x29; break;
        }
        case 3: if (size != 56) throw std::runtime_error("Invalid VI observation"); break;
        case 4:
            if (!need_checkpoint || size != 152 || u32(p) != ++frames) throw std::runtime_error("Invalid checkpoint");
            need_checkpoint = false; break;
        case 5:
            if (!at_checkpoint || size != 28 || u32(p) != frames || u64(p + 4) != commands || u64(p + 12) != patches
                || u64(p + 20) != patch_bytes || file.peek() != std::char_traits<char>::eof())
                throw std::runtime_error("Invalid journal end or trailing bytes");
            ended = true; break;
        default: throw std::runtime_error("Unknown journal record");
        }
        at_checkpoint = kind == 4;
        return {kind, std::move(data)};
    }
};

int export_journal_reference(Journal &journal, const std::filesystem::path &output) {
    if (!journal.from_reset) throw std::runtime_error("Reference export requires capture from reset");
    RDP::Interface iface;
    auto reference = RDP::create_replayer_driver_angrylion(journal, iface);
    reference->update_rdram(journal.ram.data(), journal.ram.size(), 0);
    reference->update_hidden_rdram(journal.hidden.data(), journal.hidden.size(), 0);
    std::filesystem::create_directories(output);
    while (!journal.ended) {
        auto record = journal.next(); auto &data = record.data;
        if (record.kind == 1) {
            auto *ram = reference->get_rdram(); uint32_t offset = Journal::u32(data.data());
            for (size_t i = 4; i < data.size(); i++) ram[(offset + i - 4) ^ 3] = data[i];
        } else if (record.kind == 2) {
            std::vector<uint32_t> words(data.size() / 4);
            for (size_t i = 0; i < words.size(); i++) words[i] = Journal::u32(data.data() + 4 * i);
            reference->command(RDP::Op((words[0] >> 24) & 63), unsigned(words.size()), words.data());
        } else if (record.kind == 3) {
            for (unsigned reg = 0; reg < 14; reg++) {
                auto *p = data.data() + reg * 4;
                reference->set_vi_register(RDP::VIRegister(reg), uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3]);
            }
        } else if (record.kind == 4) {
            auto memory = snapshot(*reference);
            char name[32]; std::snprintf(name, sizeof(name), "frame-%04u.bin", journal.frames);
            std::ofstream file(output / name, std::ios::binary); file.exceptions(std::ios::failbit | std::ios::badbit);
            for (auto *bytes : {&memory.rdram, &memory.hidden, &memory.tmem})
                file.write(reinterpret_cast<const char *>(bytes->data()), std::streamsize(bytes->size()));
        }
    }
    std::cout << "journalReferenceExport=passed frames=" << journal.frames << " commands=" << journal.commands << '\n';
    return 0;
}

int run_journal(const char *path, const std::filesystem::path &output, bool negative, bool parse_only, bool reference_only) {
    Journal journal(path);
    if (parse_only) {
        while (!journal.ended) journal.next();
        std::cout << "journalParse=passed frames=" << journal.frames << " commands=" << journal.commands
                  << " patches=" << journal.patches << " patchBytes=" << journal.patch_bytes << '\n';
        return 0;
    }
    if (!journal.from_reset)
        throw std::runtime_error("Native journal replay requires capture from reset; raw warm RDP state import is not implemented");
    if (reference_only) return export_journal_reference(journal, output);
    std::atomic<unsigned> validation_errors{0};
    ValidationInstanceFactory instance_factory;
    RDP::ReplayerState state;
    state.context.set_instance_factory(&instance_factory);
    if (!state.init_common()) throw std::runtime_error("Cannot initialize Vulkan");
    state.context.set_notification_callback([&](const char *) { validation_errors++; });
    if (state.device->get_gpu_properties().deviceType != VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)
        throw std::runtime_error("Journal proof requires a discrete GPU");
    auto gpu = RDP::create_replayer_driver_parallel(*state.device, journal, state.iface);
    auto reference = RDP::create_replayer_driver_angrylion(journal, state.iface);
    std::filesystem::create_directories(output);
    for (auto *driver : {gpu.get(), reference.get()}) {
        driver->update_rdram(journal.ram.data(), journal.ram.size(), 0);
        driver->update_hidden_rdram(journal.hidden.data(), journal.hidden.size(), 0);
    }
    bool writing = false;
    uint8_t *gpu_ram = nullptr, *reference_ram = nullptr;
    uint64_t write_batches = 0;
    while (!journal.ended) {
        auto record = journal.next(); auto &data = record.data;
        if (record.kind == 1) {
            if (!writing) {
                for (auto *driver : {gpu.get(), reference.get()}) { driver->idle(); driver->invalidate_caches(); }
                gpu_ram = gpu->get_rdram(); reference_ram = reference->get_rdram();
                writing = true; write_batches++;
            }
            uint32_t address = Journal::u32(data.data());
            for (size_t i = 4; i < data.size(); i++) {
                size_t offset = (address + i - 4) ^ 3;
                gpu_ram[offset] = data[i]; reference_ram[offset] = data[i];
            }
            continue;
        }
        if (writing) { gpu->flush_caches(); reference->flush_caches(); writing = false; }
        if (record.kind == 2) {
            std::vector<uint32_t> words(data.size() / 4);
            for (size_t i = 0; i < words.size(); i++) words[i] = Journal::u32(data.data() + i * 4);
            for (auto *driver : {gpu.get(), reference.get()})
                driver->command(RDP::Op((words[0] >> 24) & 63), unsigned(words.size()), words.data());
        } else if (record.kind == 3) {
            for (unsigned reg = 0; reg < 14; reg++) {
                auto *p = data.data() + reg * 4;
                uint32_t value = uint32_t(p[0]) << 24 | uint32_t(p[1]) << 16 | uint32_t(p[2]) << 8 | p[3];
                gpu->set_vi_register(RDP::VIRegister(reg), value);
                reference->set_vi_register(RDP::VIRegister(reg), value);
            }
        } else if (record.kind == 4) {
            gpu->signal_complete(); reference->signal_complete();
            auto actual = snapshot(*gpu), expected = snapshot(*reference);
            size_t dram_diff = differences(expected.rdram, actual.rdram), hidden_diff = differences(expected.hidden, actual.hidden), tmem_diff = differences(expected.tmem, actual.tmem);
            std::cout << "journalFrame=" << journal.frames << " commands=" << journal.commands
                      << " rdramMismatchBytes=" << dram_diff << " hiddenMismatchBytes=" << hidden_diff
                      << " tmemMismatchBytes=" << tmem_diff << '\n';
            // Save visible output when a valid framebuffer has been configured.
            if ((state.iface.fb.size == 2 || state.iface.fb.size == 3) && state.iface.fb.width) {
                auto prefix = std::string("frame-") + std::to_string(journal.frames);
                save_frame(output / (prefix + "-gpu.ppm"), actual, state.iface);
                save_frame(output / (prefix + "-angrylion.ppm"), expected, state.iface);
            }
            if (dram_diff || hidden_diff || tmem_diff) throw std::runtime_error("GPU/reference mismatch at FULL_SYNC");
            if (negative) {
                expected.rdram.at(state.iface.fb.addr ^ 3) ^= 1;
                if (differences(expected.rdram, actual.rdram) != 1) throw std::runtime_error("Negative control missed injected error");
            }
            state.device->next_frame_context();
        }
    }
    state.device->wait_idle();
    std::cout << "journalGpuAngrylion=" << (validation_errors.load() ? "failed" : "passed")
              << " frames=" << journal.frames << " commands=" << journal.commands << " patches=" << journal.patches
              << " patchBytes=" << journal.patch_bytes << " writeBatches=" << write_batches
              << " negativeControl=" << (negative ? "passed" : "disabled") << " vulkanValidationErrors=" << validation_errors.load() << '\n';
    state.context.set_notification_callback({});
    return validation_errors.load() ? 3 : 0;
}
