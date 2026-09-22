#include "n64_gpu_state.hpp"
#include "rdp_device.hpp"
#include <array>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <type_traits>

namespace RDP {
namespace {
void check(bool value) {
    if (!value) throw std::invalid_argument("Invalid or unsupported RDP checkpoint");
}

// Scalars are explicit little-endian u32s, including small enums and booleans.
// No C++ padding, pointers, compiler layout, or Vulkan resources enter a save.
struct Archive {
    std::array<uint8_t, 16384> output{};
    size_t written = 0;
    const uint8_t *input = nullptr;
    size_t remaining = 0;
    template<class T> void operator()(T &value) {
        static_assert(std::is_integral_v<T> || std::is_enum_v<T>);
        uint32_t word = uint32_t(value);
        if (input) {
            check(remaining >= 4);
            word = uint32_t(input[0]) | uint32_t(input[1]) << 8 | uint32_t(input[2]) << 16 | uint32_t(input[3]) << 24;
            input += 4; remaining -= 4;
            if constexpr (std::is_same_v<T, bool>) check(word <= 1);
            else if constexpr (sizeof(T) < 4) check(word < (uint32_t(1) << (8 * sizeof(T))));
            value = T(word);
        } else {
            check(written <= output.size() - 4);
            for (unsigned shift = 0; shift < 32; shift += 8) output[written++] = uint8_t(word >> shift);
        }
    }
    void bytes(uint8_t *data, size_t size) {
        if (input) {
            check(remaining >= size); std::memcpy(data, input, size); input += size; remaining -= size;
        } else {
            check(size <= output.size() - written);
            std::memcpy(output.data() + written, data, size); written += size;
        }
    }
};
void fields(Archive &a, ScissorState &s) { a(s.xlo); a(s.ylo); a(s.xhi); a(s.yhi); }
void fields(Archive &a, StaticRasterizationState &s) {
    for (auto &c : s.combiner) {
        a(c.rgb.muladd); a(c.rgb.mulsub); a(c.rgb.mul); a(c.rgb.add);
        a(c.alpha.muladd); a(c.alpha.mulsub); a(c.alpha.mul); a(c.alpha.add);
        check(uint32_t(c.rgb.muladd) <= 15 && uint32_t(c.rgb.mulsub) <= 15 && uint32_t(c.rgb.mul) <= 31 && uint32_t(c.rgb.add) <= 7);
        check(uint32_t(c.alpha.muladd) <= 7 && uint32_t(c.alpha.mulsub) <= 7 && uint32_t(c.alpha.mul) <= 7 && uint32_t(c.alpha.add) <= 7);
    }
    a(s.flags); a(s.dither); a(s.texture_size); a(s.texture_fmt);
    check(s.dither <= 15 && s.texture_size <= 3 && s.texture_fmt <= 7);
}
void fields(Archive &a, DepthBlendState &s) {
    for (auto &c : s.blend_cycles) {
        a(c.blend_1a); a(c.blend_1b); a(c.blend_2a); a(c.blend_2b);
        check(uint32_t(c.blend_1a) <= 3 && uint32_t(c.blend_1b) <= 3 && uint32_t(c.blend_2a) <= 3 && uint32_t(c.blend_2b) <= 3);
    }
    a(s.flags); a(s.coverage_mode); a(s.z_mode);
    check(uint32_t(s.coverage_mode) <= 3 && uint32_t(s.z_mode) <= 3);
}
void fields(Archive &a, TileInfo &t) {
    a(t.size.slo); a(t.size.shi); a(t.size.tlo); a(t.size.thi);
    auto &m = t.meta;
    a(m.offset); a(m.stride); a(m.fmt); a(m.size); a(m.palette);
    a(m.mask_s); a(m.shift_s); a(m.mask_t); a(m.shift_t); a(m.flags);
    check(m.offset <= 4095 && m.stride <= 4095 && uint32_t(m.fmt) <= 7 && uint32_t(m.size) <= 3);
    check(m.palette <= 15 && m.mask_s <= 15 && m.shift_s <= 15 && m.mask_t <= 15 && m.shift_t <= 15 && m.flags <= 15);
}
struct State {
    ScissorState scissor{}, render_scissor{};
    StaticRasterizationState raster{}, render_raster{};
    DepthBlendState depth{}, render_depth{};
    uint32_t texture_address = 0, texture_width = 0;
    TextureFormat texture_format{};
    TextureSize texture_size{};
    uint32_t color_address = 0, depth_address = 0, width = 0;
    FBFormat format{};
    uint32_t primitive_index = 0;
    TileInfo tiles[8]{};
    uint32_t blend_color = 0, fog_color = 0, env_color = 0, primitive_color = 0, fill_color = 0;
    uint8_t min_level = 0, prim_lod_frac = 0;
    int32_t prim_depth = 0;
    uint16_t prim_dz = 0, convert[6]{}, key_width[3]{};
    uint8_t key_center[3]{}, key_scale[3]{};
    bool use_prim_depth = false;
    std::array<uint8_t, 4096> tmem{};
};
void fields(Archive &a, State &s) {
    uint32_t magic = 0x31504452, revision = 1;
    a(magic); a(revision); check(magic == 0x31504452 && revision == 1);
    fields(a, s.scissor); fields(a, s.raster); fields(a, s.depth);
    fields(a, s.render_scissor); fields(a, s.render_raster); fields(a, s.render_depth);
    a(s.texture_address); a(s.texture_width); a(s.texture_format); a(s.texture_size);
    a(s.color_address); a(s.depth_address); a(s.width); a(s.format); a(s.primitive_index);
    check(s.texture_address <= 0xffffff && s.texture_width <= 1024 && uint32_t(s.texture_format) <= 7 && uint32_t(s.texture_size) <= 3);
    check(s.color_address <= 0xffffff && s.depth_address <= 0xffffff && s.width <= 1024 && uint32_t(s.format) <= 4);
    for (auto &tile : s.tiles) fields(a, tile);
    a(s.blend_color); a(s.fog_color); a(s.env_color); a(s.primitive_color); a(s.fill_color);
    a(s.min_level); a(s.prim_lod_frac); a(s.prim_depth); a(s.prim_dz);
    for (auto &v : s.convert) a(v);
    for (auto &v : s.key_width) a(v);
    for (auto &v : s.key_center) a(v);
    for (auto &v : s.key_scale) a(v);
    a(s.use_prim_depth); a.bytes(s.tmem.data(), s.tmem.size());
}
struct RendererLock {
    Renderer &renderer;
    explicit RendererLock(Renderer &r) : renderer(r) { r.lock_command_processing(); }
    ~RendererLock() { renderer.unlock_command_processing(); }
};
}

// The bridge never exposes VI scanout or upscaling. After idle(), all queued
// primitives and TMEM uploads have completed. Stream caches, fences and
// pipeline objects can therefore be rebuilt on a fresh Vulkan context.
std::vector<uint8_t> EutherCheckpoint::save(CommandProcessor &p) {
    check(p.get_rdram_size() == 8 * 1024 * 1024 && p.get_hidden_rdram_size() == 4 * 1024 * 1024);
    check(p.is_host_coherent && p.renderer.get_scaling_factor() == 1);
    p.idle();
    Renderer &r = p.renderer;
    RendererLock lock(r);
    check(r.stream.triangle_setup.empty() && r.stream.tmem_upload_infos.empty() && !r.stream.cmd);
    check(r.fb.deduced_height == 0 && !r.fb.color_write_pending && !r.fb.depth_write_pending);
    State s;
    s.scissor = p.scissor_state; s.raster = p.static_state; s.depth = p.depth_blend;
    s.render_scissor = r.stream.scissor_state; s.render_raster = r.stream.static_raster_state; s.render_depth = r.stream.depth_blend_state;
    s.texture_address = p.texture_image.addr; s.texture_width = p.texture_image.width;
    s.texture_format = p.texture_image.fmt; s.texture_size = p.texture_image.size;
    s.color_address = r.fb.addr; s.depth_address = r.fb.depth_addr; s.width = r.fb.width; s.format = r.fb.fmt;
    // Noise and dithering depend on this counter, even across empty draws.
    s.primitive_index = r.base_primitive_index;
    std::copy(std::begin(r.tiles), std::end(r.tiles), std::begin(s.tiles));
#define COPY_CONSTANT(name) s.name = r.constants.name
    COPY_CONSTANT(blend_color); COPY_CONSTANT(fog_color); COPY_CONSTANT(env_color);
    COPY_CONSTANT(primitive_color); COPY_CONSTANT(fill_color); COPY_CONSTANT(min_level);
    COPY_CONSTANT(prim_lod_frac); COPY_CONSTANT(prim_depth); COPY_CONSTANT(prim_dz); COPY_CONSTANT(use_prim_depth);
#undef COPY_CONSTANT
#define COPY_ARRAY(name) std::copy(std::begin(r.constants.name), std::end(r.constants.name), std::begin(s.name))
    COPY_ARRAY(convert); COPY_ARRAY(key_width); COPY_ARRAY(key_center); COPY_ARRAY(key_scale);
#undef COPY_ARRAY
    const void *mapped = p.device.map_host_buffer(*p.tmem, Vulkan::MEMORY_ACCESS_READ_BIT);
    std::memcpy(s.tmem.data(), mapped, s.tmem.size());
    p.device.unmap_host_buffer(*p.tmem, Vulkan::MEMORY_ACCESS_READ_BIT);
    Archive archive; fields(archive, s); return {archive.output.begin(), archive.output.begin() + archive.written};
}

void EutherCheckpoint::load(CommandProcessor &p, const uint8_t *bytes, size_t size) {
    check(bytes && size <= 16384);
    State s;
    Archive archive; archive.input = bytes; archive.remaining = size;
    fields(archive, s); check(archive.remaining == 0);
    check(p.is_host_coherent && p.renderer.get_scaling_factor() == 1);
    p.idle();
    Renderer &r = p.renderer;
    RendererLock lock(r);
    check(r.stream.triangle_setup.empty() && r.stream.tmem_upload_infos.empty() && !r.stream.cmd);
    p.scissor_state = s.scissor; p.static_state = s.raster; p.depth_blend = s.depth;
    r.stream.scissor_state = s.render_scissor; r.stream.static_raster_state = s.render_raster; r.stream.depth_blend_state = s.render_depth;
    p.texture_image.addr = s.texture_address; p.texture_image.width = s.texture_width;
    p.texture_image.fmt = s.texture_format; p.texture_image.size = s.texture_size;
    r.fb.addr = s.color_address; r.fb.depth_addr = s.depth_address; r.fb.width = s.width; r.fb.fmt = s.format;
    r.base_primitive_index = s.primitive_index;
    std::copy(std::begin(s.tiles), std::end(s.tiles), std::begin(r.tiles));
#define COPY_CONSTANT(name) r.constants.name = s.name
    COPY_CONSTANT(blend_color); COPY_CONSTANT(fog_color); COPY_CONSTANT(env_color);
    COPY_CONSTANT(primitive_color); COPY_CONSTANT(fill_color); COPY_CONSTANT(min_level);
    COPY_CONSTANT(prim_lod_frac); COPY_CONSTANT(prim_depth); COPY_CONSTANT(prim_dz); COPY_CONSTANT(use_prim_depth);
#undef COPY_CONSTANT
#define COPY_ARRAY(name) std::copy(std::begin(s.name), std::end(s.name), std::begin(r.constants.name))
    COPY_ARRAY(convert); COPY_ARRAY(key_width); COPY_ARRAY(key_center); COPY_ARRAY(key_scale);
#undef COPY_ARRAY
    void *mapped = p.device.map_host_buffer(*p.tmem, Vulkan::MEMORY_ACCESS_WRITE_BIT);
    std::memcpy(mapped, s.tmem.data(), s.tmem.size());
    p.device.unmap_host_buffer(*p.tmem, Vulkan::MEMORY_ACCESS_WRITE_BIT);
}
}
