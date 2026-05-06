// license:BSD-3-Clause
// copyright-holders:Carlos A. Lozano, Rob Rosenbrock, Phil Stroffolino
// Ported from MAME xain.cpp

using System;

using ioport_value = System.UInt32;
using offs_t = System.UInt32;
using PointerU8 = mame.Pointer<System.Byte>;
using s32 = System.Int32;
using u8 = System.Byte;
using u32 = System.UInt32;
using uint8_t = System.Byte;

using static mame.diexec_global;
using static mame.digfx_global;
using static mame.device_global;
using static mame.drawgfx_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.emupal_global;
using static mame.gamedrv_global;
using static mame.gen_latch_global;
using static mame.hash_global;
using static mame.ioport_global;
using static mame.ioport_input_string_helper;
using static mame.ioport_ioport_type_helper;
using static mame.m6809_global;
using static mame.romentry_global;
using static mame.screen_global;
using static mame.speaker_global;
using static mame.taito68705_global;
using static mame.timer_global;
using static mame.ymopn_global;

namespace mame
{
    partial class xain_state : driver_device
    {
        static readonly XTAL MASTER_CLOCK = XTAL_global.op("12_MHz_XTAL");
        static readonly XTAL CPU_CLOCK   = MASTER_CLOCK / 8;
        static readonly XTAL MCU_CLOCK   = MASTER_CLOCK / 4;
        static readonly XTAL PIXEL_CLOCK = MASTER_CLOCK / 2;


        int scanline_to_vcount(int scanline)
        {
            int vcount = scanline + 8;
            if (vcount < 0x100)
                return vcount;
            else
                return (vcount - 0x18) | 0x100;
        }


        void scanline(timer_device timer, object ptr, s32 param)
        {
            int scanline = param;
            int screen_height = (int)m_screen.op0.height();
            int vcount_old = scanline_to_vcount((scanline == 0) ? screen_height - 1 : scanline - 1);
            int vcount = scanline_to_vcount(scanline);

            if (scanline > 0)
                m_screen.op0.update_partial(scanline - 1);

            if ((vcount_old & 8) == 0 && (vcount & 8) != 0)
                m_maincpu.op0.set_input_line(M6809_FIRQ_LINE, ASSERT_LINE);

            if (vcount == 0xf8)
                m_maincpu.op0.set_input_line(INPUT_LINE_NMI, ASSERT_LINE);

            if (vcount >= 248 - 1)
                m_vblank = 1;
            else
                m_vblank = 0;
        }


        void cpuA_bankswitch_w(uint8_t data)
        {
            m_pri = (u8)(data & 0x7);
            m_rom_banks_0.op0.set_entry((data >> 3) & 1);
        }


        void cpuB_bankswitch_w(uint8_t data)
        {
            m_rom_banks_1.op0.set_entry(data & 1);
        }


        void main_irq_w(offs_t offset, uint8_t data)
        {
            switch (offset)
            {
            case 0:
                m_maincpu.op0.set_input_line(INPUT_LINE_NMI, CLEAR_LINE);
                break;
            case 1:
                m_maincpu.op0.set_input_line(M6809_FIRQ_LINE, CLEAR_LINE);
                break;
            case 2:
                m_maincpu.op0.set_input_line(M6809_IRQ_LINE, CLEAR_LINE);
                break;
            case 3:
                m_subcpu.op0.set_input_line(M6809_IRQ_LINE, ASSERT_LINE);
                break;
            }
        }


        void irqA_assert_w(uint8_t data)
        {
            m_maincpu.op0.set_input_line(M6809_IRQ_LINE, ASSERT_LINE);
        }


        void irqB_clear_w(uint8_t data)
        {
            m_subcpu.op0.set_input_line(M6809_IRQ_LINE, CLEAR_LINE);
        }


        public int vblank_r()
        {
            return m_vblank;
        }

        uint8_t vblank_port_r()
        {
            uint8_t data = (uint8_t)ioport("VBLANK").read();

            if (m_trace_status && data != m_trace_last_vblank_port && m_trace_mcu_count < 256)
            {
                Console.Error.WriteLine($"[XAIN] vblank_port_r pc=0x{m_maincpu.op0.debug_pc():X4} data=0x{data:X2} vblank={m_vblank}");
                m_trace_last_vblank_port = data;
                m_trace_mcu_count++;
            }

            return data;
        }


        public int mcu_status_r()
        {
            int result = ((m_mcu.found() && (CLEAR_LINE != m_mcu.op0.mcu_semaphore_r())) ? 0x00 : 0x01) |
                         ((m_mcu.found() && (CLEAR_LINE != m_mcu.op0.host_semaphore_r())) ? 0x00 : 0x02);

            if (m_trace_status && result != m_trace_last_mcu_status && m_trace_mcu_count < 256)
            {
                Console.Error.WriteLine($"[XAIN] mcu_status_r result=0x{result:X2} mcu={(m_mcu.found() ? m_mcu.op0.mcu_semaphore_r() : -1)} host={(m_mcu.found() ? m_mcu.op0.host_semaphore_r() : -1)}");
                m_trace_last_mcu_status = result;
                m_trace_mcu_count++;
            }

            return result;
        }


        uint8_t mcu_comm_reset_r()
        {
            if (m_trace_status && m_trace_mcu_count < 256)
            {
                Console.Error.WriteLine("[XAIN] mcu_comm_reset_r");
                m_trace_mcu_count++;
            }

            if (m_mcu.found() && !machine().side_effects_disabled())
            {
                m_mcu.op0.reset_w(ASSERT_LINE);
                m_mcu.op0.reset_w(CLEAR_LINE);
            }
            return 0xff;
        }


        void scrollx_w_0(offs_t offset, uint8_t data)
        {
            if (offset == 0) m_scrollx_0_0 = data;
            if (offset == 1) m_scrollx_0_1 = data;
            m_bg_tilemap_0.set_scrollx(0, (int)(m_scrollx_0_0 | (m_scrollx_0_1 << 8)));
        }


        void scrollx_w_1(offs_t offset, uint8_t data)
        {
            if (offset == 0) m_scrollx_1_0 = data;
            if (offset == 1) m_scrollx_1_1 = data;
            m_bg_tilemap_1.set_scrollx(0, (int)(m_scrollx_1_0 | (m_scrollx_1_1 << 8)));
        }


        void scrolly_w_0(offs_t offset, uint8_t data)
        {
            if (offset == 0) m_scrolly_0_0 = data;
            if (offset == 1) m_scrolly_0_1 = data;
            m_bg_tilemap_0.set_scrolly(0, (int)(m_scrolly_0_0 | (m_scrolly_0_1 << 8)));
        }


        void scrolly_w_1(offs_t offset, uint8_t data)
        {
            if (offset == 0) m_scrolly_1_0 = data;
            if (offset == 1) m_scrolly_1_1 = data;
            m_bg_tilemap_1.set_scrolly(0, (int)(m_scrolly_1_0 | (m_scrolly_1_1 << 8)));
        }

        void xain_postload()
        {
            m_bg_tilemap_0.set_scrollx(0, (int)(m_scrollx_0_0 | (m_scrollx_0_1 << 8)));
            m_bg_tilemap_0.set_scrolly(0, (int)(m_scrolly_0_0 | (m_scrolly_0_1 << 8)));
            m_bg_tilemap_1.set_scrollx(0, (int)(m_scrollx_1_0 | (m_scrollx_1_1 << 8)));
            m_bg_tilemap_1.set_scrolly(0, (int)(m_scrolly_1_0 | (m_scrolly_1_1 << 8)));
            m_char_tilemap.mark_all_dirty();
            m_bg_tilemap_0.mark_all_dirty();
            m_bg_tilemap_1.mark_all_dirty();
        }


        void bootleg_map(address_map map, device_t device)
        {
            map.op(0x0000, 0x1fff).ram().share("share1");
            map.op(0x2000, 0x27ff).ram().w(charram_w).share(m_charram);
            map.op(0x2800, 0x2fff).ram().w(bgram_w_1).share(m_bgram[1]);
            map.op(0x3000, 0x37ff).ram().w(bgram_w_0).share(m_bgram[0]);
            map.op(0x3800, 0x397f).ram().share(m_spriteram);
            map.op(0x3a00, 0x3a00).portr("P1");
            map.op(0x3a00, 0x3a01).w(scrollx_w_1);
            map.op(0x3a01, 0x3a01).portr("P2");
            map.op(0x3a02, 0x3a02).portr("DSW0");
            map.op(0x3a02, 0x3a03).w(scrolly_w_1);
            map.op(0x3a03, 0x3a03).portr("DSW1");
            map.op(0x3a04, 0x3a05).w(scrollx_w_0);
            map.op(0x3a05, 0x3a05).r(vblank_port_r);
            map.op(0x3a06, 0x3a07).w(scrolly_w_0);
            map.op(0x3a08, 0x3a08).w(m_soundlatch, (data) => {
                if (m_trace_status && m_trace_sound_count < 256)
                {
                    Console.Error.WriteLine($"[XAIN] soundlatch_w pc=0x{m_maincpu.op0.debug_pc():X4} data=0x{data:X2}");
                    m_trace_sound_count++;
                }
                m_soundlatch.op0.write(data);
            });
            map.op(0x3a09, 0x3a0c).w(main_irq_w);
            map.op(0x3a0d, 0x3a0d).w(flipscreen_w);
            map.op(0x3a0f, 0x3a0f).w(cpuA_bankswitch_w);
            map.op(0x3c00, 0x3dff).w(m_palette, (offset, data) => { m_palette.op0.write8(offset, data); }).share("palette");
            map.op(0x3e00, 0x3fff).w(m_palette, (offset, data) => { m_palette.op0.write8_ext(offset, data); }).share("palette_ext");
            map.op(0x4000, 0x7fff).bankr(m_rom_banks_0);
            map.op(0x8000, 0xffff).rom();
        }


        void main_map(address_map map, device_t device)
        {
            bootleg_map(map, device);
            map.op(0x3a04, 0x3a04).r(m_mcu, () => {
                u8 data = m_mcu.op0.data_r();
                if (m_trace_status && m_trace_mcu_count < 256)
                {
                    Console.Error.WriteLine($"[XAIN] mcu_data_r pc=0x{m_maincpu.op0.debug_pc():X4} data=0x{data:X2}");
                    m_trace_mcu_count++;
                }
                return data;
            });
            map.op(0x3a06, 0x3a06).r(mcu_comm_reset_r);
            map.op(0x3a0e, 0x3a0e).w(m_mcu, (data) => {
                if (m_trace_status && m_trace_mcu_count < 256)
                {
                    Console.Error.WriteLine($"[XAIN] mcu_data_w pc=0x{m_maincpu.op0.debug_pc():X4} data=0x{data:X2}");
                    m_trace_mcu_count++;
                }
                m_mcu.op0.data_w(data);
            });
        }


        void cpu_map_B(address_map map, device_t device)
        {
            map.op(0x0000, 0x1fff).ram().share("share1");
            map.op(0x2000, 0x2000).w(irqA_assert_w);
            map.op(0x2800, 0x2800).w(irqB_clear_w);
            map.op(0x3000, 0x3000).w(cpuB_bankswitch_w);
            map.op(0x4000, 0x7fff).bankr(m_rom_banks_1);
            map.op(0x8000, 0xffff).rom();
        }


        void sound_map(address_map map, device_t device)
        {
            map.op(0x0000, 0x07ff).ram();
            map.op(0x1000, 0x1000).r(m_soundlatch, () => {
                u8 data = m_soundlatch.op0.read();
                if (m_trace_status && m_trace_sound_count < 256)
                {
                    Console.Error.WriteLine($"[XAIN] soundlatch_r pc=0x{m_audiocpu.op0.debug_pc():X4} data=0x{data:X2}");
                    m_trace_sound_count++;
                }
                return data;
            });
            map.op(0x2800, 0x2801).w("ym1", (offset, data) => { ((ym2203_device)subdevice("ym1")).write(offset, data); });
            map.op(0x3000, 0x3001).w("ym2", (offset, data) => { ((ym2203_device)subdevice("ym2")).write(offset, data); });
            map.op(0x4000, 0xffff).rom();
        }
    }


    public partial class xain : construct_ioport_helper
    {
        void construct_ioport_xsleena(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            xain_state state = (xain_state)owner;

            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("P1");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_8WAY();
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_8WAY();
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_8WAY();
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_8WAY();
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 );
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 );
            PORT_BIT( 0x40, IP_ACTIVE_LOW, IPT_START1 );
            PORT_BIT( 0x80, IP_ACTIVE_LOW, IPT_START2 );

            PORT_START("P2");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_8WAY(); PORT_COCKTAIL();
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_8WAY(); PORT_COCKTAIL();
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_8WAY(); PORT_COCKTAIL();
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_8WAY(); PORT_COCKTAIL();
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 ); PORT_COCKTAIL();
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 ); PORT_COCKTAIL();
            PORT_BIT( 0x40, IP_ACTIVE_LOW, IPT_COIN1 );
            PORT_BIT( 0x80, IP_ACTIVE_LOW, IPT_COIN2 );

            PORT_START("DSW0");
            PORT_DIPNAME( 0x03, 0x03, DEF_STR( Coin_B ) );       PORT_DIPLOCATION("SW1:1,2");
            PORT_DIPSETTING(    0x00, DEF_STR( _2C_1C ) );
            PORT_DIPSETTING(    0x03, DEF_STR( _1C_1C ) );
            PORT_DIPSETTING(    0x02, DEF_STR( _1C_2C ) );
            PORT_DIPSETTING(    0x01, DEF_STR( _1C_3C ) );
            PORT_DIPNAME( 0x0c, 0x0c, DEF_STR( Coin_A ) );       PORT_DIPLOCATION("SW1:3,4");
            PORT_DIPSETTING(    0x00, DEF_STR( _2C_1C ) );
            PORT_DIPSETTING(    0x0c, DEF_STR( _1C_1C ) );
            PORT_DIPSETTING(    0x08, DEF_STR( _1C_2C ) );
            PORT_DIPSETTING(    0x04, DEF_STR( _1C_3C ) );
            PORT_DIPNAME( 0x10, 0x10, DEF_STR( Demo_Sounds ) );  PORT_DIPLOCATION("SW1:5");
            PORT_DIPSETTING(    0x00, DEF_STR( Off ) );
            PORT_DIPSETTING(    0x10, DEF_STR( On ) );
            PORT_DIPNAME( 0x20, 0x20, DEF_STR( Allow_Continue ) );   PORT_DIPLOCATION("SW1:6");
            PORT_DIPSETTING(    0x00, DEF_STR( No ) );
            PORT_DIPSETTING(    0x20, DEF_STR( Yes ) );
            PORT_DIPNAME( 0x40, 0x00, DEF_STR( Cabinet ) );      PORT_DIPLOCATION("SW1:7");
            PORT_DIPSETTING(    0x00, DEF_STR( Upright ) );
            PORT_DIPSETTING(    0x40, DEF_STR( Cocktail ) );
            PORT_DIPNAME( 0x80, 0x00, DEF_STR( Flip_Screen ) );  PORT_DIPLOCATION("SW1:8");
            PORT_DIPSETTING(    0x00, DEF_STR( Off ) );
            PORT_DIPSETTING(    0x80, DEF_STR( On ) );

            PORT_START("DSW1");
            PORT_DIPNAME( 0x03, 0x03, DEF_STR( Difficulty ) );   PORT_DIPLOCATION("SW2:1,2");
            PORT_DIPSETTING(    0x03, DEF_STR( Easy ) );
            PORT_DIPSETTING(    0x02, DEF_STR( Normal ) );
            PORT_DIPSETTING(    0x01, DEF_STR( Hard ) );
            PORT_DIPSETTING(    0x00, DEF_STR( Hardest ) );
            PORT_DIPNAME( 0x0c, 0x0c, DEF_STR( Game_Time ) );    PORT_DIPLOCATION("SW2:3,4");
            PORT_DIPSETTING(    0x0c, "Slow" );
            PORT_DIPSETTING(    0x08, DEF_STR( Normal ) );
            PORT_DIPSETTING(    0x04, "Fast" );
            PORT_DIPSETTING(    0x00, "Very Fast" );
            PORT_DIPNAME( 0x30, 0x30, DEF_STR( Bonus_Life ) );   PORT_DIPLOCATION("SW2:5,6");
            PORT_DIPSETTING(    0x30, "20k 70k and every 70k" );
            PORT_DIPSETTING(    0x20, "30k 80k and every 80k" );
            PORT_DIPSETTING(    0x10, "20k and 80k" );
            PORT_DIPSETTING(    0x00, "30k and 80k" );
            PORT_DIPNAME( 0xc0, 0xc0, DEF_STR( Lives ) );        PORT_DIPLOCATION("SW2:7,8");
            PORT_DIPSETTING(    0xc0, "3" );
            PORT_DIPSETTING(    0x80, "4" );
            PORT_DIPSETTING(    0x40, "6" );
            PORT_DIPSETTING(    0x00, "Infinite (Cheat)");

            PORT_START("VBLANK");
            PORT_BIT( 0x03, IP_ACTIVE_LOW,  IPT_UNUSED );
            PORT_BIT( 0x04, IP_ACTIVE_LOW,  IPT_COIN3 );
            PORT_BIT( 0x18, IP_ACTIVE_HIGH, IPT_CUSTOM ); PORT_CUSTOM_MEMBER(DEVICE_SELF, () => (ioport_value)state.mcu_status_r());
            PORT_BIT( 0x20, IP_ACTIVE_HIGH, IPT_CUSTOM ); PORT_READ_LINE_MEMBER(() => state.vblank_r());
            PORT_BIT( 0xc0, IP_ACTIVE_LOW,  IPT_UNUSED );

            INPUT_PORTS_END();
        }
    }

    partial class xain_state : driver_device
    {
        static readonly gfx_layout charlayout = new gfx_layout(
            8,8,
            RGN_FRAC(1,1),
            4,
            new u32[] { 0, 2, 4, 6 },
            new u32[] { 1, 0, 65, 64, 129, 128, 193, 192 },
            new u32[] { 0, 8, 16, 24, 32, 40, 48, 56 },
            32*8
        );

        static readonly gfx_layout tilelayout = new gfx_layout(
            16,16,
            RGN_FRAC(1,2),
            4,
            new u32[] { RGN_FRAC(1,2)+0, RGN_FRAC(1,2)+4, 0, 4 },
            new u32[] { 3, 2, 1, 0, 131, 130, 129, 128, 259, 258, 257, 256, 387, 386, 385, 384 },
            new u32[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120 },
            64*8
        );

        static readonly gfx_decode_entry [] gfx_xain =
        {
            GFXDECODE_ENTRY( "gfx1", 0, charlayout,   0, 8 ),
            GFXDECODE_ENTRY( "gfx2", 0, tilelayout, 256, 8 ),
            GFXDECODE_ENTRY( "gfx3", 0, tilelayout, 384, 8 ),
            GFXDECODE_ENTRY( "gfx4", 0, tilelayout, 128, 8 ),
        };


        protected override void machine_start()
        {
            m_rom_banks_0.op0.configure_entries(0, 2, new PointerU8(memregion("maincpu").base_(), 0x4000), 0xc000);
            m_rom_banks_1.op0.configure_entries(0, 2, new PointerU8(memregion("sub").base_(), 0x4000), 0xc000);
            m_rom_banks_0.op0.set_entry(0);
            m_rom_banks_1.op0.set_entry(0);

            save_item(NAME(new { m_vblank }));
            machine().save().save_item_ref(this, name(), tag(), 0, "m_vblank", () => m_vblank, value => m_vblank = value);
        }


        public void xsleena(machine_config config)
        {
            MC6809E(config, m_maincpu, CPU_CLOCK);
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, main_map);

            TIMER(config, "scantimer").configure_scanline(scanline, "screen", 0, 1);

            MC6809E(config, m_subcpu, CPU_CLOCK);
            m_subcpu.op0.memory().set_addrmap(AS_PROGRAM, cpu_map_B);

            MC6809(config, m_audiocpu, (u32)PIXEL_CLOCK.dvalue());
            m_audiocpu.op0.memory().set_addrmap(AS_PROGRAM, sound_map);

            TAITO68705_MCU(config, m_mcu, MCU_CLOCK);

            SCREEN(config, m_screen, SCREEN_TYPE_RASTER);
            m_screen.op0.set_raw(PIXEL_CLOCK, 384, 0, 256, 272, 8, 248);
            m_screen.op0.set_screen_update(screen_update);
            m_screen.op0.set_palette(m_palette);

            GFXDECODE(config, m_gfxdecode, m_palette, gfx_xain);
            PALETTE(config, m_palette).set_format(palette_device.xbgr_444_t.xBGR_444, 512);

            SPEAKER(config, "mono").front_center();

            GENERIC_LATCH_8(config, m_soundlatch).data_pending_callback().set_inputline(m_audiocpu, M6809_IRQ_LINE).reg();

            ym2203_device ym1 = YM2203(config, "ym1", (u32)MCU_CLOCK.dvalue());
            ym1.irq_handler().set_inputline(m_audiocpu, M6809_FIRQ_LINE).reg();
            ym1.add_route(0, "mono", 0.50f);
            ym1.add_route(1, "mono", 0.50f);
            ym1.add_route(2, "mono", 0.50f);
            ym1.add_route(3, "mono", 0.40f);

            ym2203_device ym2 = YM2203(config, "ym2", (u32)MCU_CLOCK.dvalue());
            ym2.add_route(0, "mono", 0.50f);
            ym2.add_route(1, "mono", 0.50f);
            ym2.add_route(2, "mono", 0.50f);
            ym2.add_route(3, "mono", 0.40f);

            if (Environment.GetEnvironmentVariable("EUTHERDRIVE_MCS_XAIN_PERFECT_QUANTUM") == "1")
                config.set_perfect_quantum(m_maincpu);
        }


        public void xsleenab(machine_config config)
        {
            xsleena(config);
            config.device_remove("mcu");
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, bootleg_map);
        }
    }

    partial class xain : construct_ioport_helper
    {
        static readonly tiny_rom_entry [] rom_xsleena =
        {
            ROM_REGION( 0x14000, "maincpu", 0 ),
            ROM_LOAD( "p9-08.ic66",   0x08000, 0x8000, CRC("5179ae3f") + SHA1("9e4e2825e56b090aa759b0da39ccb17ccd77ede2") ),
            ROM_LOAD( "pa-09.ic65",   0x04000, 0x4000, CRC("10a7c800") + SHA1("f19201fe1414faed649b8e49416025aae44bcb6c") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x14000, "sub", 0 ),
            ROM_LOAD( "p1-0.ic29",    0x08000, 0x8000, CRC("a1a860e2") + SHA1("fb2b152bfafc44608039774436ddf3b17eed979c") ),
            ROM_LOAD( "p0-0.ic15",    0x04000, 0x4000, CRC("948b9757") + SHA1("3ea840cc47ae6a66f3e5f6a2f3e88475dcfe1840") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x10000, "audiocpu", 0 ),
            ROM_LOAD( "p2-0.ic49",     0x8000, 0x8000, CRC("a5318cb8") + SHA1("35fb28c5598e39f22552bb036ae356b78422f080") ),

            ROM_REGION( 0x800, "mcu:mcu", 0 ),
            ROM_LOAD( "pz-0.113",      0x0000, 0x0800, CRC("a432a907") + SHA1("4708a40e3a82dec2c5a64bc5da884a37d503cb6b") ),

            ROM_REGION( 0x08000, "gfx1", 0 ),
            ROM_LOAD( "pb-0.ic24",   0x00000, 0x8000, CRC("83c00dd8") + SHA1("8e9b19281039b63072270c7a63d9fb30cda570fd") ),

            ROM_REGION( 0x40000, "gfx2", 0 ),
            ROM_LOAD( "pk-0.ic136",   0x00000, 0x8000, CRC("11eb4247") + SHA1("5d2f1fa07b8fb1c6bebfdb02c39282d29813791b") ),
            ROM_LOAD( "pl-0.ic135",   0x08000, 0x8000, CRC("422b536e") + SHA1("d5985c0bd1c840cb6f0da6b177a2caaff6db5a04") ),
            ROM_LOAD( "pm-0.ic134",   0x10000, 0x8000, CRC("828c1b0c") + SHA1("cb9b64073b0ade3885f61545191db4c445e3066b") ),
            ROM_LOAD( "pn-0.ic133",   0x18000, 0x8000, CRC("d37939e0") + SHA1("301d9f6720857c64a4e070444a07a38138ddd4ef") ),
            ROM_LOAD( "pc-0.ic114",   0x20000, 0x8000, CRC("8f0aa1a7") + SHA1("be3fdb6204b77dba28b14c5b880d65d7c1d6a161") ),
            ROM_LOAD( "pd-0.ic113",   0x28000, 0x8000, CRC("45681910") + SHA1("60c3eb4bc08bf11bf09bcd27549c6427fafbb1fb") ),
            ROM_LOAD( "pe-0.ic112",   0x30000, 0x8000, CRC("a8eeabc8") + SHA1("e5dc31df0b223b65144af3602be5bcb2ff9eebbd") ),
            ROM_LOAD( "pf-0.ic111",   0x38000, 0x8000, CRC("e59a2f27") + SHA1("4643cea85f8613c36b416f46f9d1753fa9839237") ),

            ROM_REGION( 0x40000, "gfx3", 0 ),
            ROM_LOAD( "p5-0.ic44",    0x00000, 0x8000, CRC("5c6c453c") + SHA1("68c0028d15da8f5e53f09e3d154d18cd9f219601") ),
            ROM_LOAD( "p4-0.ic45",    0x08000, 0x8000, CRC("59d87a9a") + SHA1("f23cb9a9d6c6249a8a1f8e2acbc235086b008c7b") ),
            ROM_LOAD( "p3-0.ic46",    0x10000, 0x8000, CRC("84884a2e") + SHA1("5087010a72226e91a084a61b5089c110dba7e933") ),
            ROM_LOAD( "p6-0.ic43",    0x20000, 0x8000, CRC("8d637639") + SHA1("301a7893de8f1bb526f5075e2af8203b8af4b0d3") ),
            ROM_LOAD( "p7-0.ic42",    0x28000, 0x8000, CRC("71eec4e6") + SHA1("3417c52a39a6fc43c51ad707168180f54153177a") ),
            ROM_LOAD( "p8-0.ic41",    0x30000, 0x8000, CRC("7fc9704f") + SHA1("b6f353fb7fec58f68b9e28be2aa29146ac64ffd4") ),

            ROM_REGION( 0x40000, "gfx4", 0 ),
            ROM_LOAD( "po-0.ic131",   0x00000, 0x8000, CRC("252976ae") + SHA1("534c9148d33e453f3541543a8c0eb4afc59c7de8") ),
            ROM_LOAD( "pp-0.ic130",   0x08000, 0x8000, CRC("e6f1e8d5") + SHA1("2ee0227361d1f1358f5b5964dab7e691243cd9ae") ),
            ROM_LOAD( "pq-0.ic129",   0x10000, 0x8000, CRC("785381ed") + SHA1("95bf4eb29830c589a9793a4138e645e5b77f0c06") ),
            ROM_LOAD( "pr-0.ic128",   0x18000, 0x8000, CRC("59754e3d") + SHA1("d1781dbc83965fc84492f7282d6813507ba1e81b") ),
            ROM_LOAD( "pg-0.ic109",   0x20000, 0x8000, CRC("4d977f33") + SHA1("30b446ddb2f32354334ea780c435f2407d128808") ),
            ROM_LOAD( "ph-0.ic108",   0x28000, 0x8000, CRC("3f3b62a0") + SHA1("ab7e8f0ff707771401e679b6151ad0ea85cfc792") ),
            ROM_LOAD( "pi-0.ic107",   0x30000, 0x8000, CRC("76641ee3") + SHA1("8fba0fa6639e7bdfb3f7be5e945a55b64411d242") ),
            ROM_LOAD( "pj-0.ic106",   0x38000, 0x8000, CRC("37671f36") + SHA1("1494eec4ecde9ae1f1101aa13eb301b3f3d06602") ),

            ROM_REGION( 0x0100, "proms", 0 ),
            ROM_LOAD( "pt-0.ic59",    0x00000, 0x0100, CRC("fed32888") + SHA1("4e9330456b20f7198c1e27ca1ae7200f25595599") ),

            ROM_END,
        };


        static readonly tiny_rom_entry [] rom_xsleenaj =
        {
            ROM_REGION( 0x14000, "maincpu", 0 ),
            ROM_LOAD( "p9-01.ic66",   0x08000, 0x8000, CRC("370164be") + SHA1("65c9951cac7dc3943fa4d5f9919ebb4c4f29b3ae") ),
            ROM_LOAD( "pa-0.ic65",    0x04000, 0x4000, CRC("d22bf859") + SHA1("9edb159bef2eba2c5d93c03c15fbcb87eea52236") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x14000, "sub", 0 ),
            ROM_LOAD( "p1-0.ic29",    0x08000, 0x8000, CRC("a1a860e2") + SHA1("fb2b152bfafc44608039774436ddf3b17eed979c") ),
            ROM_LOAD( "p0-0.ic15",    0x04000, 0x4000, CRC("948b9757") + SHA1("3ea840cc47ae6a66f3e5f6a2f3e88475dcfe1840") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x10000, "audiocpu", 0 ),
            ROM_LOAD( "p2-0.ic49",     0x8000, 0x8000, CRC("a5318cb8") + SHA1("35fb28c5598e39f22552bb036ae356b78422f080") ),

            ROM_REGION( 0x800, "mcu:mcu", 0 ),
            ROM_LOAD( "pz-0.113",      0x0000, 0x0800, CRC("a432a907") + SHA1("4708a40e3a82dec2c5a64bc5da884a37d503cb6b") ),

            ROM_REGION( 0x08000, "gfx1", 0 ),
            ROM_LOAD( "pb-0.ic24",   0x00000, 0x8000, CRC("83c00dd8") + SHA1("8e9b19281039b63072270c7a63d9fb30cda570fd") ),

            ROM_REGION( 0x40000, "gfx2", 0 ),
            ROM_LOAD( "pk-0.ic136",   0x00000, 0x8000, CRC("11eb4247") + SHA1("5d2f1fa07b8fb1c6bebfdb02c39282d29813791b") ),
            ROM_LOAD( "pl-0.ic135",   0x08000, 0x8000, CRC("422b536e") + SHA1("d5985c0bd1c840cb6f0da6b177a2caaff6db5a04") ),
            ROM_LOAD( "pm-0.ic134",   0x10000, 0x8000, CRC("828c1b0c") + SHA1("cb9b64073b0ade3885f61545191db4c445e3066b") ),
            ROM_LOAD( "pn-0.ic133",   0x18000, 0x8000, CRC("d37939e0") + SHA1("301d9f6720857c64a4e070444a07a38138ddd4ef") ),
            ROM_LOAD( "pc-0.ic114",   0x20000, 0x8000, CRC("8f0aa1a7") + SHA1("be3fdb6204b77dba28b14c5b880d65d7c1d6a161") ),
            ROM_LOAD( "pd-0.ic113",   0x28000, 0x8000, CRC("45681910") + SHA1("60c3eb4bc08bf11bf09bcd27549c6427fafbb1fb") ),
            ROM_LOAD( "pe-0.ic112",   0x30000, 0x8000, CRC("a8eeabc8") + SHA1("e5dc31df0b223b65144af3602be5bcb2ff9eebbd") ),
            ROM_LOAD( "pf-0.ic111",   0x38000, 0x8000, CRC("e59a2f27") + SHA1("4643cea85f8613c36b416f46f9d1753fa9839237") ),

            ROM_REGION( 0x40000, "gfx3", 0 ),
            ROM_LOAD( "p5-0.ic44",    0x00000, 0x8000, CRC("5c6c453c") + SHA1("68c0028d15da8f5e53f09e3d154d18cd9f219601") ),
            ROM_LOAD( "p4-0.ic45",    0x08000, 0x8000, CRC("59d87a9a") + SHA1("f23cb9a9d6c6249a8a1f8e2acbc235086b008c7b") ),
            ROM_LOAD( "p3-0.ic46",    0x10000, 0x8000, CRC("84884a2e") + SHA1("5087010a72226e91a084a61b5089c110dba7e933") ),
            ROM_LOAD( "p6-0.ic43",    0x20000, 0x8000, CRC("8d637639") + SHA1("301a7893de8f1bb526f5075e2af8203b8af4b0d3") ),
            ROM_LOAD( "p7-0.ic42",    0x28000, 0x8000, CRC("71eec4e6") + SHA1("3417c52a39a6fc43c51ad707168180f54153177a") ),
            ROM_LOAD( "p8-0.ic41",    0x30000, 0x8000, CRC("7fc9704f") + SHA1("b6f353fb7fec58f68b9e28be2aa29146ac64ffd4") ),

            ROM_REGION( 0x40000, "gfx4", 0 ),
            ROM_LOAD( "po-0.ic131",   0x00000, 0x8000, CRC("252976ae") + SHA1("534c9148d33e453f3541543a8c0eb4afc59c7de8") ),
            ROM_LOAD( "pp-0.ic130",   0x08000, 0x8000, CRC("e6f1e8d5") + SHA1("2ee0227361d1f1358f5b5964dab7e691243cd9ae") ),
            ROM_LOAD( "pq-0.ic129",   0x10000, 0x8000, CRC("785381ed") + SHA1("95bf4eb29830c589a9793a4138e645e5b77f0c06") ),
            ROM_LOAD( "pr-0.ic128",   0x18000, 0x8000, CRC("59754e3d") + SHA1("d1781dbc83965fc84492f7282d6813507ba1e81b") ),
            ROM_LOAD( "pg-0.ic109",   0x20000, 0x8000, CRC("4d977f33") + SHA1("30b446ddb2f32354334ea780c435f2407d128808") ),
            ROM_LOAD( "ph-0.ic108",   0x28000, 0x8000, CRC("3f3b62a0") + SHA1("ab7e8f0ff707771401e679b6151ad0ea85cfc792") ),
            ROM_LOAD( "pi-0.ic107",   0x30000, 0x8000, CRC("76641ee3") + SHA1("8fba0fa6639e7bdfb3f7be5e945a55b64411d242") ),
            ROM_LOAD( "pj-0.ic106",   0x38000, 0x8000, CRC("37671f36") + SHA1("1494eec4ecde9ae1f1101aa13eb301b3f3d06602") ),

            ROM_REGION( 0x0100, "proms", 0 ),
            ROM_LOAD( "pt-0.ic59",    0x00000, 0x0100, CRC("fed32888") + SHA1("4e9330456b20f7198c1e27ca1ae7200f25595599") ),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_solrwarr =
        {
            ROM_REGION( 0x14000, "maincpu", 0 ),
            ROM_LOAD( "p9-02.ic66",   0x08000, 0x8000, CRC("8ff372a8") + SHA1("0fc396e662419fb9cb5bea11748aa8e0e8d072e6") ),
            ROM_LOAD( "pa-03.ic65",   0x04000, 0x4000, CRC("154f946f") + SHA1("25b776eb9c494e5302795ae79e494cbfc7c104b1") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x14000, "sub", 0 ),
            ROM_LOAD( "p1-02.ic29",   0x08000, 0x8000, CRC("f5f235a3") + SHA1("9f57dd7c5e514afa750edc6da6d263bf1e913c14") ),
            ROM_LOAD( "p0-02.ic133",  0x04000, 0x4000, CRC("51ae95ae") + SHA1("e03f7ccb0b33b05547577c60a7f92dc75e24b4d6") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x10000, "audiocpu", 0 ),
            ROM_LOAD( "p2-0.ic49",     0x8000, 0x8000, CRC("a5318cb8") + SHA1("35fb28c5598e39f22552bb036ae356b78422f080") ),

            ROM_REGION( 0x800, "mcu:mcu", 0 ),
            ROM_LOAD( "pz-0.113",      0x0000, 0x0800, CRC("a432a907") + SHA1("4708a40e3a82dec2c5a64bc5da884a37d503cb6b") ),

            ROM_REGION( 0x08000, "gfx1", 0 ),
            ROM_LOAD( "pb-0.ic24",   0x00000, 0x8000, CRC("83c00dd8") + SHA1("8e9b19281039b63072270c7a63d9fb30cda570fd") ),

            ROM_REGION( 0x40000, "gfx2", 0 ),
            ROM_LOAD( "pk-0.ic136",   0x00000, 0x8000, CRC("11eb4247") + SHA1("5d2f1fa07b8fb1c6bebfdb02c39282d29813791b") ),
            ROM_LOAD( "pl-0.ic135",   0x08000, 0x8000, CRC("422b536e") + SHA1("d5985c0bd1c840cb6f0da6b177a2caaff6db5a04") ),
            ROM_LOAD( "pm-0.ic134",   0x10000, 0x8000, CRC("828c1b0c") + SHA1("cb9b64073b0ade3885f61545191db4c445e3066b") ),
            ROM_LOAD( "pn-02.ic133",  0x18000, 0x8000, CRC("d2ed6f94") + SHA1("155a0d1d978f07517400d0c602fc40657f8569dc") ),
            ROM_LOAD( "pc-0.ic114",   0x20000, 0x8000, CRC("8f0aa1a7") + SHA1("be3fdb6204b77dba28b14c5b880d65d7c1d6a161") ),
            ROM_LOAD( "pd-0.ic113",   0x28000, 0x8000, CRC("45681910") + SHA1("60c3eb4bc08bf11bf09bcd27549c6427fafbb1fb") ),
            ROM_LOAD( "pe-0.ic112",   0x30000, 0x8000, CRC("a8eeabc8") + SHA1("e5dc31df0b223b65144af3602be5bcb2ff9eebbd") ),
            ROM_LOAD( "pf-02.ic111",  0x38000, 0x8000, CRC("6e627a77") + SHA1("1d16031acd53c9e691ae7eac8a6f1ae3954fac8c") ),

            ROM_REGION( 0x40000, "gfx3", 0 ),
            ROM_LOAD( "p5-0.ic44",    0x00000, 0x8000, CRC("5c6c453c") + SHA1("68c0028d15da8f5e53f09e3d154d18cd9f219601") ),
            ROM_LOAD( "p4-0.ic45",    0x08000, 0x8000, CRC("59d87a9a") + SHA1("f23cb9a9d6c6249a8a1f8e2acbc235086b008c7b") ),
            ROM_LOAD( "p3-0.ic46",    0x10000, 0x8000, CRC("84884a2e") + SHA1("5087010a72226e91a084a61b5089c110dba7e933") ),
            ROM_LOAD( "p6-0.ic43",    0x20000, 0x8000, CRC("8d637639") + SHA1("301a7893de8f1bb526f5075e2af8203b8af4b0d3") ),
            ROM_LOAD( "p7-0.ic42",    0x28000, 0x8000, CRC("71eec4e6") + SHA1("3417c52a39a6fc43c51ad707168180f54153177a") ),
            ROM_LOAD( "p8-0.ic41",    0x30000, 0x8000, CRC("7fc9704f") + SHA1("b6f353fb7fec58f68b9e28be2aa29146ac64ffd4") ),

            ROM_REGION( 0x40000, "gfx4", 0 ),
            ROM_LOAD( "po-0.ic131",   0x00000, 0x8000, CRC("252976ae") + SHA1("534c9148d33e453f3541543a8c0eb4afc59c7de8") ),
            ROM_LOAD( "pp-0.ic130",   0x08000, 0x8000, CRC("e6f1e8d5") + SHA1("2ee0227361d1f1358f5b5964dab7e691243cd9ae") ),
            ROM_LOAD( "pq-0.ic129",   0x10000, 0x8000, CRC("785381ed") + SHA1("95bf4eb29830c589a9793a4138e645e5b77f0c06") ),
            ROM_LOAD( "pr-0.ic128",   0x18000, 0x8000, CRC("59754e3d") + SHA1("d1781dbc83965fc84492f7282d6813507ba1e81b") ),
            ROM_LOAD( "pg-0.ic109",   0x20000, 0x8000, CRC("4d977f33") + SHA1("30b446ddb2f32354334ea780c435f2407d128808") ),
            ROM_LOAD( "ph-0.ic108",   0x28000, 0x8000, CRC("3f3b62a0") + SHA1("ab7e8f0ff707771401e679b6151ad0ea85cfc792") ),
            ROM_LOAD( "pi-0.ic107",   0x30000, 0x8000, CRC("76641ee3") + SHA1("8fba0fa6639e7bdfb3f7be5e945a55b64411d242") ),
            ROM_LOAD( "pj-0.ic106",   0x38000, 0x8000, CRC("37671f36") + SHA1("1494eec4ecde9ae1f1101aa13eb301b3f3d06602") ),

            ROM_REGION( 0x0100, "proms", 0 ),
            ROM_LOAD( "pt-0.ic59",    0x00000, 0x0100, CRC("fed32888") + SHA1("4e9330456b20f7198c1e27ca1ae7200f25595599") ),

            ROM_END,
        };


        static readonly tiny_rom_entry [] rom_xsleenab =
        {
            ROM_REGION( 0x14000, "maincpu", 0 ),
            ROM_LOAD( "1.rom",        0x08000, 0x8000, CRC("79f515a7") + SHA1("e61f18e3639dd9afe16c7bcb90fa7be31905e2c6") ),
            ROM_LOAD( "pa-0.ic65",    0x04000, 0x4000, CRC("d22bf859") + SHA1("9edb159bef2eba2c5d93c03c15fbcb87eea52236") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x14000, "sub", 0 ),
            ROM_LOAD( "p1-0.ic29",    0x08000, 0x8000, CRC("a1a860e2") + SHA1("fb2b152bfafc44608039774436ddf3b17eed979c") ),
            ROM_LOAD( "p0-0.ic15",    0x04000, 0x4000, CRC("948b9757") + SHA1("3ea840cc47ae6a66f3e5f6a2f3e88475dcfe1840") ),
            ROM_CONTINUE(             0x10000, 0x4000 ),

            ROM_REGION( 0x10000, "audiocpu", 0 ),
            ROM_LOAD( "p2-0.ic49",     0x8000, 0x8000, CRC("a5318cb8") + SHA1("35fb28c5598e39f22552bb036ae356b78422f080") ),

            ROM_REGION( 0x08000, "gfx1", 0 ),
            ROM_LOAD( "pb-0.ic24",   0x00000, 0x8000, CRC("83c00dd8") + SHA1("8e9b19281039b63072270c7a63d9fb30cda570fd") ),

            ROM_REGION( 0x40000, "gfx2", 0 ),
            ROM_LOAD( "pk-0.ic136",   0x00000, 0x8000, CRC("11eb4247") + SHA1("5d2f1fa07b8fb1c6bebfdb02c39282d29813791b") ),
            ROM_LOAD( "pl-0.ic135",   0x08000, 0x8000, CRC("422b536e") + SHA1("d5985c0bd1c840cb6f0da6b177a2caaff6db5a04") ),
            ROM_LOAD( "pm-0.ic134",   0x10000, 0x8000, CRC("828c1b0c") + SHA1("cb9b64073b0ade3885f61545191db4c445e3066b") ),
            ROM_LOAD( "pn-0.ic133",   0x18000, 0x8000, CRC("d37939e0") + SHA1("301d9f6720857c64a4e070444a07a38138ddd4ef") ),
            ROM_LOAD( "pc-0.ic114",   0x20000, 0x8000, CRC("8f0aa1a7") + SHA1("be3fdb6204b77dba28b14c5b880d65d7c1d6a161") ),
            ROM_LOAD( "pd-0.ic113",   0x28000, 0x8000, CRC("45681910") + SHA1("60c3eb4bc08bf11bf09bcd27549c6427fafbb1fb") ),
            ROM_LOAD( "pe-0.ic112",   0x30000, 0x8000, CRC("a8eeabc8") + SHA1("e5dc31df0b223b65144af3602be5bcb2ff9eebbd") ),
            ROM_LOAD( "pf-0.ic111",   0x38000, 0x8000, CRC("e59a2f27") + SHA1("4643cea85f8613c36b416f46f9d1753fa9839237") ),

            ROM_REGION( 0x40000, "gfx3", 0 ),
            ROM_LOAD( "p5-0.ic44",    0x00000, 0x8000, CRC("5c6c453c") + SHA1("68c0028d15da8f5e53f09e3d154d18cd9f219601") ),
            ROM_LOAD( "p4-0.ic45",    0x08000, 0x8000, CRC("59d87a9a") + SHA1("f23cb9a9d6c6249a8a1f8e2acbc235086b008c7b") ),
            ROM_LOAD( "p3-0.ic46",    0x10000, 0x8000, CRC("84884a2e") + SHA1("5087010a72226e91a084a61b5089c110dba7e933") ),
            ROM_LOAD( "p6-0.ic43",    0x20000, 0x8000, CRC("8d637639") + SHA1("301a7893de8f1bb526f5075e2af8203b8af4b0d3") ),
            ROM_LOAD( "p7-0.ic42",    0x28000, 0x8000, CRC("71eec4e6") + SHA1("3417c52a39a6fc43c51ad707168180f54153177a") ),
            ROM_LOAD( "p8-0.ic41",    0x30000, 0x8000, CRC("7fc9704f") + SHA1("b6f353fb7fec58f68b9e28be2aa29146ac64ffd4") ),

            ROM_REGION( 0x40000, "gfx4", 0 ),
            ROM_LOAD( "po-0.ic131",   0x00000, 0x8000, CRC("252976ae") + SHA1("534c9148d33e453f3541543a8c0eb4afc59c7de8") ),
            ROM_LOAD( "pp-0.ic130",   0x08000, 0x8000, CRC("e6f1e8d5") + SHA1("2ee0227361d1f1358f5b5964dab7e691243cd9ae") ),
            ROM_LOAD( "pq-0.ic129",   0x10000, 0x8000, CRC("785381ed") + SHA1("95bf4eb29830c589a9793a4138e645e5b77f0c06") ),
            ROM_LOAD( "pr-0.ic128",   0x18000, 0x8000, CRC("59754e3d") + SHA1("d1781dbc83965fc84492f7282d6813507ba1e81b") ),
            ROM_LOAD( "pg-0.ic109",   0x20000, 0x8000, CRC("4d977f33") + SHA1("30b446ddb2f32354334ea780c435f2407d128808") ),
            ROM_LOAD( "ph-0.ic108",   0x28000, 0x8000, CRC("3f3b62a0") + SHA1("ab7e8f0ff707771401e679b6151ad0ea85cfc792") ),
            ROM_LOAD( "pi-0.ic107",   0x30000, 0x8000, CRC("76641ee3") + SHA1("8fba0fa6639e7bdfb3f7be5e945a55b64411d242") ),
            ROM_LOAD( "pj-0.ic106",   0x38000, 0x8000, CRC("37671f36") + SHA1("1494eec4ecde9ae1f1101aa13eb301b3f3d06602") ),

            ROM_REGION( 0x0100, "proms", 0 ),
            ROM_LOAD( "pt-0.ic59",    0x00000, 0x0100, CRC("fed32888") + SHA1("4e9330456b20f7198c1e27ca1ae7200f25595599") ),

            ROM_END,
        };
    }


    partial class xain : construct_ioport_helper
    {
        static void xain_state_xsleena(machine_config config, device_t device) { xain_state state = (xain_state)device; state.xsleena(config); }
        static void xain_state_xsleenab(machine_config config, device_t device) { xain_state state = (xain_state)device; state.xsleenab(config); }


        static xain m_xain = new xain();


        static device_t device_creator_xain_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new xain_state(mconfig, type, tag); }


        //                                                                 creator,                     rom,            YEAR,   NAME,       PARENT,     MACHINE,                INPUT,                          INIT,                       MONITOR, COMPANY,                            FULLNAME,                        FLAGS
        public static readonly game_driver driver_xsleena   = GAME( device_creator_xain_state, rom_xsleena,   "1986", "xsleena",  "0",        xain_state_xsleena,     m_xain.construct_ioport_xsleena,   driver_device.empty_init,   ROT0,   "Technos Japan (Taito license)",            "Xain'd Sleena (World)",             MACHINE_SUPPORTS_SAVE);
        public static readonly game_driver driver_xsleenaj  = GAME( device_creator_xain_state, rom_xsleenaj,  "1986", "xsleenaj", "xsleena",  xain_state_xsleena,     m_xain.construct_ioport_xsleena,   driver_device.empty_init,   ROT0,   "Technos Japan",                            "Xain'd Sleena (Japan)",             MACHINE_SUPPORTS_SAVE);
        public static readonly game_driver driver_solrwarr  = GAME( device_creator_xain_state, rom_solrwarr,  "1986", "solrwarr", "xsleena",  xain_state_xsleena,     m_xain.construct_ioport_xsleena,   driver_device.empty_init,   ROT0,   "Technos Japan (Taito / Memetron license)", "Solar-Warrior (US)",                MACHINE_SUPPORTS_SAVE);
        public static readonly game_driver driver_xsleenab  = GAME( device_creator_xain_state, rom_xsleenab,  "1986", "xsleenab", "xsleena",  xain_state_xsleenab,    m_xain.construct_ioport_xsleena,   driver_device.empty_init,   ROT0,   "bootleg",                                  "Xain'd Sleena (bootleg, set 1)",   MACHINE_SUPPORTS_SAVE);
    }
}
