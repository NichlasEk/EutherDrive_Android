// license:BSD-3-Clause
// copyright-holders:Carlos A. Lozano, Rob Rosenbrock, Phil Stroffolino
// Ported from MAME xain.h

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using required_memory_bank = mame.memory_bank_finder<mame.bool_const_true>;
using uint8_t = System.Byte;


namespace mame
{
    partial class xain_state : driver_device
    {
        required_device<mc6809e_device> m_maincpu;
        required_device<mc6809e_device> m_subcpu;
        required_device<mc6809e_device> m_audiocpu;
        optional_device<taito68705_mcu_device> m_mcu;
        required_device<gfxdecode_device> m_gfxdecode;
        required_device<screen_device> m_screen;
        required_device<palette_device> m_palette;
        required_device<generic_latch_8_device> m_soundlatch;

        required_shared_ptr<uint8_t> m_charram;
        required_shared_ptr_array<uint8_t, u32_const_2> m_bgram;
        required_shared_ptr<uint8_t> m_spriteram;

        required_memory_bank m_rom_banks_0;
        required_memory_bank m_rom_banks_1;

        int m_vblank;
        uint8_t m_pri;
        uint8_t m_scrollx_0_0;
        uint8_t m_scrollx_0_1;
        uint8_t m_scrolly_0_0;
        uint8_t m_scrolly_0_1;
        uint8_t m_scrollx_1_0;
        uint8_t m_scrollx_1_1;
        uint8_t m_scrolly_1_0;
        uint8_t m_scrolly_1_1;

        tilemap_t m_char_tilemap;
        tilemap_t m_bg_tilemap_0;
        tilemap_t m_bg_tilemap_1;

        bool m_trace_status;
        int m_trace_screen_count;
        int m_trace_mcu_count;
        int m_trace_ram_count;
        int m_trace_last_mcu_status;
        int m_trace_last_vblank_port;


        public xain_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<mc6809e_device>(this, "maincpu");
            m_subcpu = new required_device<mc6809e_device>(this, "sub");
            m_audiocpu = new required_device<mc6809e_device>(this, "audiocpu");
            m_mcu = new optional_device<taito68705_mcu_device>(this, "mcu");
            m_gfxdecode = new required_device<gfxdecode_device>(this, "gfxdecode");
            m_screen = new required_device<screen_device>(this, "screen");
            m_palette = new required_device<palette_device>(this, "palette");
            m_soundlatch = new required_device<generic_latch_8_device>(this, "soundlatch");

            m_charram = new required_shared_ptr<uint8_t>(this, "charram");
            m_bgram = new required_shared_ptr_array<uint8_t, u32_const_2>(this, "bgram_{0}", 1U);
            m_spriteram = new required_shared_ptr<uint8_t>(this, "spriteram");

            m_rom_banks_0 = new required_memory_bank(this, "mainbank");
            m_rom_banks_1 = new required_memory_bank(this, "subbank");

            m_trace_status = Environment.GetEnvironmentVariable("EUTHERDRIVE_XAIN_STATUS") == "1";
            m_trace_screen_count = 0;
            m_trace_mcu_count = 0;
            m_trace_ram_count = 0;
            m_trace_last_mcu_status = -1;
            m_trace_last_vblank_port = -1;
        }
    }
}
