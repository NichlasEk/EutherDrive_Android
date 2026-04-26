// license:BSD-3-Clause
// copyright-holders:Aaron Giles

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using s32 = System.Int32;
using tilemap_memory_index = System.UInt32;
using u32 = System.UInt32;
using uint8_t = System.Byte;
using uint32_t = System.UInt32;
using offs_t = System.UInt32;

using static mame.attotime_global;
using static mame.diexec_global;
using static mame.digfx_global;
using static mame.disound_global;
using static mame.drawgfx_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.emupal_global;
using static mame.gamedrv_global;
using static mame.hash_global;
using static mame.inputcode_global;
using static mame.ioport_global;
using static mame.ioport_input_string_helper;
using static mame.ioport_ioport_type_helper;
using static mame.midway_global;
using static mame.nvram_global;
using static mame.romentry_global;
using static mame.screen_global;
using static mame.speaker_global;
using static mame.tilemap_global;
using static mame.timer_global;
using static mame.watchdog_global;
using static mame.z80_global;
using static mame.z80ctc_global;


namespace mame
{
    partial class mcr3_state : mcr_state
    {
        static readonly XTAL MASTER_CLOCK = new XTAL(20_000_000);

        required_device<screen_device> m_screen;


        public mcr3_state(machine_config mconfig, device_type type, string tag) :
            base(mconfig, type, tag)
        {
            m_screen = new required_device<screen_device>(this, "screen");
        }


        void mcrmono_control_port_w(uint8_t data)
        {
            machine().bookkeeping().coin_counter_w(0, (data >> 0) & 1);
            m_mcr_cocktail_flip = (uint8_t)((data >> 6) & 1);
        }


        uint8_t rampage_ip4_r()
        {
            uint32_t input = (uint32_t)ioport("MONO.IP4").read();
            uint32_t sound_status = (uint32_t)(m_sounds_good.op0.read() & 1);
            return (uint8_t)(input | (sound_status << 7));
        }


        void rampage_op6_w(uint8_t data)
        {
            m_sounds_good.op0.reset_write(((data ^ 0xff) >> 5) & 1);
            m_sounds_good.op0.write(data);
        }


        void mcrmono_map(address_map map, device_t device)
        {
            map.unmap_value_high();
            map.op(0x0000, 0xdfff).rom();
            map.op(0xe000, 0xe7ff).ram().share("nvram");
            map.op(0xe800, 0xe9ff).ram().share("spriteram");
            map.op(0xea00, 0xebff).ram();
            map.op(0xec00, 0xec7f).mirror(0x0380).w(mcr_paletteram9_w).share("paletteram");
            map.op(0xf000, 0xf7ff).ram().w(mcr3_videoram_w).share("videoram");
            map.op(0xf800, 0xffff).rom();
        }


        void mcrmono_portmap(address_map map, device_t device)
        {
            map.unmap_value_high();
            map.global_mask(0xff);
            map.op(0x00, 0x00).mirror(0x78).portr("MONO.IP0");
            map.op(0x01, 0x01).mirror(0x78).portr("MONO.IP1");
            map.op(0x02, 0x02).mirror(0x78).portr("MONO.IP2");
            map.op(0x03, 0x03).mirror(0x78).portr("MONO.IP3");
            map.op(0x04, 0x04).mirror(0x78).r(rampage_ip4_r);
            map.op(0x05, 0x05).mirror(0x78).w(mcrmono_control_port_w);
            map.op(0x06, 0x06).mirror(0x78).w(rampage_op6_w);
            map.op(0x07, 0x07).mirror(0x78).w("watchdog", (data) => { ((watchdog_timer_device)subdevice("watchdog")).reset_w(data); });
            map.op(0xf0, 0xf3).mirror(0x0c).rw(m_ctc, (offset) => { return m_ctc.op0.read(offset); }, (offset, data) => { m_ctc.op0.write(offset, data); });
        }


        void mcrmono_get_bg_tile_info(tilemap_t tilemap, ref tile_data tileinfo, tilemap_memory_index tile_index)
        {
            int index = (int)tile_index;
            int data = m_videoram[index * 2].op | (m_videoram[index * 2 + 1].op << 8);
            int code = (data & 0x3ff) | ((data >> 4) & 0x400);
            int color = ((data >> 12) & 3) ^ 3;
            tileinfo.set(0, (uint32_t)code, (uint32_t)color, TILE_FLIPYX(data >> 10));
        }


        protected override void video_start()
        {
            m_bg_tilemap = machine().tilemap().create(m_gfxdecode.op0, mcrmono_get_bg_tile_info, tilemap_standard_mapper.TILEMAP_SCAN_ROWS, 16,16, 32,30);
        }


        void mcr3_videoram_w(offs_t offset, uint8_t data)
        {
            m_videoram[offset].op = data;
            m_bg_tilemap.mark_tile_dirty(offset / 2);
        }


        void mcr3_update_sprites(screen_device screen, bitmap_ind16 bitmap, rectangle cliprect, int color_mask, int code_xor, int dx, int dy, int interlaced)
        {
            screen.priority().fill(1, cliprect);

            for (int offs = (int)m_spriteram.bytes() - 4; offs >= 0; offs -= 4)
            {
                if (m_spriteram[offs].op == 0)
                    continue;

                int flags = m_spriteram[offs + 1].op;
                int code = m_spriteram[offs + 2].op + 256 * ((flags >> 3) & 0x01);
                int color = (~flags) & color_mask;
                int flipx = flags & 0x10;
                int flipy = flags & 0x20;
                int sx = (m_spriteram[offs + 3].op - 3) * 2;
                int sy = 241 - m_spriteram[offs].op;

                if (interlaced == 1)
                    sy *= 2;

                code ^= code_xor;
                sx += dx;
                sy += dy;

                if (m_mcr_cocktail_flip == 0)
                {
                    m_gfxdecode.op0.gfx(1).prio_transmask(bitmap, cliprect, (u32)code, (u32)color, flipx, flipy, sx, sy,
                        screen.priority(), 0x00, 0x0101);
                    m_gfxdecode.op0.gfx(1).prio_transmask(bitmap, cliprect, (u32)code, (u32)color, flipx, flipy, sx, sy,
                        screen.priority(), 0x02, 0xfeff);
                }
                else
                {
                    int cflipx = flipx != 0 ? 0 : 1;
                    int cflipy = flipy != 0 ? 0 : 1;
                    m_gfxdecode.op0.gfx(1).prio_transmask(bitmap, cliprect, (u32)code, (u32)color, cflipx, cflipy, 480 - sx, 452 - sy,
                        screen.priority(), 0x00, 0x0101);
                    m_gfxdecode.op0.gfx(1).prio_transmask(bitmap, cliprect, (u32)code, (u32)color, cflipx, cflipy, 480 - sx, 452 - sy,
                        screen.priority(), 0x02, 0xfeff);
                }
            }
        }


        uint32_t screen_update_mcr3(screen_device screen, bitmap_ind16 bitmap, rectangle cliprect)
        {
            m_bg_tilemap.set_flip(m_mcr_cocktail_flip != 0 ? (TILEMAP_FLIPX | TILEMAP_FLIPY) : 0);
            m_bg_tilemap.draw(screen, bitmap, cliprect, 0, 0);
            mcr3_update_sprites(screen, bitmap, cliprect, 0x03, 0, 0, 0, 1);
            return 0;
        }


        static readonly gfx_decode_entry [] gfx_mcr3 =
        {
            GFXDECODE_SCALE( "gfx1", 0, mcr_bg_layout,     0, 4, 2, 2 ),
            GFXDECODE_ENTRY( "gfx2", 0, mcr_sprite_layout, 0, 4 ),
        };


        public void mcrmono(machine_config config)
        {
            Z80(config, m_maincpu, MASTER_CLOCK / 4);
            m_maincpu.op0.z80daisy.set_daisy_config(mcr_daisy_chain);
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, mcrmono_map);
            m_maincpu.op0.memory().set_addrmap(AS_IO, mcrmono_portmap);

            TIMER(config, "scantimer").configure_scanline(mcr_interrupt, "screen", 0, 1);

            Z80CTC(config, m_ctc, MASTER_CLOCK / 4);
            m_ctc.op0.intr_callback().set_inputline(m_maincpu, INPUT_LINE_IRQ0).reg();
            m_ctc.op0.zc_callback<int_const_0>().set(m_ctc, (int state) => { m_ctc.op0.trg1(state); }).reg();

            WATCHDOG_TIMER(config, "watchdog").set_vblank_count("screen", 16);
            NVRAM(config, "nvram", nvram_device.default_value.DEFAULT_ALL_0);

            SPEAKER(config, "speaker").front_center();

            SCREEN(config, m_screen, SCREEN_TYPE_RASTER);
            m_screen.op0.set_video_attributes(VIDEO_UPDATE_BEFORE_VBLANK);
            m_screen.op0.set_refresh_hz(30);
            m_screen.op0.set_vblank_time(ATTOSECONDS_IN_USEC(2500));
            m_screen.op0.set_size(32*16, 30*16);
            m_screen.op0.set_visarea(0*16, 32*16-1, 0*16, 30*16-1);
            m_screen.op0.set_screen_update(screen_update_mcr3);
            m_screen.op0.set_palette(m_palette);

            GFXDECODE(config, m_gfxdecode, m_palette, gfx_mcr3);
            PALETTE(config, m_palette).set_entries(64);
        }


        public void mono_sg(machine_config config)
        {
            mcrmono(config);
            MIDWAY_SOUNDS_GOOD(config, m_sounds_good).disound.add_route(ALL_OUTPUTS, "speaker", 0.75);
        }


        void mcr_common_init()
        {
            save_item(NAME(new { m_input_mux }));
            save_item(NAME(new { m_last_op4 }));
        }


        public void init_rampage()
        {
            mcr_common_init();
        }
    }


    public partial class mcr3 : construct_ioport_helper
    {
        void construct_ioport_rampage(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("MONO.IP0");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_COIN1 );
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_COIN2 );
            PORT_BIT( 0x0c, IP_ACTIVE_LOW, IPT_UNUSED );
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_TILT );
            PORT_SERVICE( 0x20, IP_ACTIVE_LOW );
            PORT_BIT( 0x40, IP_ACTIVE_LOW, IPT_SERVICE1 );
            PORT_BIT( 0x80, IP_ACTIVE_LOW, IPT_UNUSED );

            PORT_START("MONO.IP1");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_8WAY(); PORT_PLAYER(1);
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_8WAY(); PORT_PLAYER(1);
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_8WAY(); PORT_PLAYER(1);
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_8WAY(); PORT_PLAYER(1);
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 ); PORT_PLAYER(1);
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 ); PORT_PLAYER(1);
            PORT_BIT( 0xc0, IP_ACTIVE_LOW, IPT_UNUSED );

            PORT_START("MONO.IP2");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_8WAY(); PORT_PLAYER(2);
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_8WAY(); PORT_PLAYER(2);
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_8WAY(); PORT_PLAYER(2);
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_8WAY(); PORT_PLAYER(2);
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 ); PORT_PLAYER(2);
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 ); PORT_PLAYER(2);
            PORT_BIT( 0xc0, IP_ACTIVE_LOW, IPT_UNUSED );

            PORT_START("MONO.IP3");
            PORT_DIPNAME( 0x03, 0x03, DEF_STR( Difficulty ) );
            PORT_DIPSETTING(    0x02, DEF_STR( Easy ) );
            PORT_DIPSETTING(    0x03, DEF_STR( Normal ) );
            PORT_DIPSETTING(    0x01, DEF_STR( Hard ) );
            PORT_DIPSETTING(    0x00, DEF_STR( Free_Play ) );
            PORT_DIPNAME( 0x04, 0x04, "Score Option" );
            PORT_DIPSETTING(    0x04, "Keep score when continuing" );
            PORT_DIPSETTING(    0x00, "Lose score when continuing" );
            PORT_DIPNAME( 0x08, 0x08, DEF_STR( Coin_A ) );
            PORT_DIPSETTING(    0x00, DEF_STR( _2C_1C ) );
            PORT_DIPSETTING(    0x08, DEF_STR( _1C_1C ) );
            PORT_DIPNAME( 0x70, 0x70, DEF_STR( Coin_B ) );
            PORT_DIPSETTING(    0x00, DEF_STR( _3C_1C ) );
            PORT_DIPSETTING(    0x10, DEF_STR( _2C_1C ) );
            PORT_DIPSETTING(    0x70, DEF_STR( _1C_1C ) );
            PORT_DIPSETTING(    0x60, DEF_STR( _1C_2C ) );
            PORT_DIPSETTING(    0x50, DEF_STR( _1C_3C ) );
            PORT_DIPSETTING(    0x40, DEF_STR( _1C_4C ) );
            PORT_DIPSETTING(    0x30, DEF_STR( _1C_5C ) );
            PORT_DIPSETTING(    0x20, DEF_STR( _1C_6C ) );
            PORT_DIPNAME( 0x80, 0x80, "Rack Advance (Cheat)" ); PORT_CODE(KEYCODE_F1);
            PORT_DIPSETTING(    0x80, DEF_STR( Off ) );
            PORT_DIPSETTING(    0x00, DEF_STR( On ) );

            PORT_START("MONO.IP4");
            PORT_BIT( 0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP ); PORT_8WAY(); PORT_PLAYER(3);
            PORT_BIT( 0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT ); PORT_8WAY(); PORT_PLAYER(3);
            PORT_BIT( 0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN ); PORT_8WAY(); PORT_PLAYER(3);
            PORT_BIT( 0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT ); PORT_8WAY(); PORT_PLAYER(3);
            PORT_BIT( 0x10, IP_ACTIVE_LOW, IPT_BUTTON1 ); PORT_PLAYER(3);
            PORT_BIT( 0x20, IP_ACTIVE_LOW, IPT_BUTTON2 ); PORT_PLAYER(3);
            PORT_BIT( 0x40, IP_ACTIVE_LOW, IPT_UNUSED );
            PORT_BIT( 0x80, IP_ACTIVE_HIGH, IPT_CUSTOM );

            INPUT_PORTS_END();
        }


        static readonly tiny_rom_entry [] rom_rampage =
        {
            ROM_REGION( 0x10000, "maincpu", 0 ),
            ROM_LOAD( "pro-0_3b_rev_3_8-27-86.3b", 0x00000, 0x8000, CRC("2f7ca03c") + SHA1("1e3a1f213fd67938adf14ea0d04dab687ea8f4ef") ),
            ROM_LOAD( "pro-1_5b_rev_3_8-27-86.5b", 0x08000, 0x8000, CRC("d89bd9a4") + SHA1("3531464ffe49dfaf2755d9e2dc1aea23819b3a5d") ),
            ROM_FILL(                              0x0e000, 0x2000, 0xff ),

            ROM_REGION( 0x40000, "sg:cpu", 0 ),
            ROM_LOAD16_BYTE( "u-7_rev.2_8-14-86.u7",   0x00000, 0x8000, CRC("cffd7fa5") + SHA1("7c5cecce1d428f847fea37d53eb09c6f62055c6f") ),
            ROM_LOAD16_BYTE( "u-17_rev.2_8-14-86.u17", 0x00001, 0x8000, CRC("e92c596b") + SHA1("4e2d87398f2e7b637cbad6cb16d832dfa8f8288c") ),
            ROM_LOAD16_BYTE( "u-8_rev.2_8-14-86.u8",   0x10000, 0x8000, CRC("11f787e4") + SHA1("1fa195bf9169608099d17be5801738a4e17bec3d") ),
            ROM_LOAD16_BYTE( "u-18_rev.2_8-14-86.u18", 0x10001, 0x8000, CRC("6b8bf5e1") + SHA1("aa8c0260dcd19a795bfc23197cd87348a685d20b") ),

            ROM_REGION( 0x08000, "gfx1", ROMREGION_INVERT ),
            ROM_LOAD( "bg-0_u15_7-23-86.15a", 0x00000, 0x04000, CRC("c0d8b7a5") + SHA1("692219388a3124fb48db7e35c4127b0fe066a289") ),
            ROM_LOAD( "bg-1_u14_7-23-86.14b", 0x04000, 0x04000, CRC("2f6e3aa1") + SHA1("ae86ce90bb6bf660e38c0f91e7ce90d44be82d60") ),

            ROM_REGION( 0x40000, "gfx2", 0 ),
            ROM_LOAD( "fg-0_8e_6-30-86.8e",   0x00000, 0x10000, CRC("0974be5d") + SHA1("be347faaa345383dc6e5c2b3789c372d6bd25905") ),
            ROM_LOAD( "fg-1_6e_6-30-86.6e",   0x10000, 0x10000, CRC("8728532b") + SHA1("327df92db7e3506b827d497859980cd2de51f45d") ),
            ROM_LOAD( "fg-2_5e_6-30-86.5e",   0x20000, 0x10000, CRC("9489f714") + SHA1("df17a45cdc6a9310856d64f89954be79bbeac12e") ),
            ROM_LOAD( "fg-3_4e_6-30-86.4e",   0x30000, 0x10000, CRC("81e1de40") + SHA1("7e7818792845ec3687b3202eeade60a298ef513e") ),

            ROM_REGION( 0x0001, "sg:pal", 0 ),
            ROM_LOAD( "e36a31axnaxqd.u15.bin", 0x0000, 0x0001, NO_DUMP),

            ROM_END,
        };


        static readonly tiny_rom_entry [] rom_rampage2 =
        {
            ROM_REGION( 0x10000, "maincpu", 0 ),
            ROM_LOAD( "pro-0_3b_rev_2_8-4-86.3b", 0x00000, 0x8000, CRC("3f1d0293") + SHA1("d68f04b9b3fc377b9e57b823db4e7f9cfedbcf99") ),
            ROM_LOAD( "pro-1_5b_rev_2_8-4-86.5b", 0x08000, 0x8000, CRC("58523d75") + SHA1("5cd512864568ec7793bda0164f21e7d72a7ea817") ),
            ROM_FILL(                             0x0e000, 0x2000, 0xff ),

            ROM_REGION( 0x40000, "sg:cpu", 0 ),
            ROM_LOAD16_BYTE( "u-7_rev.2_8-14-86.u7",   0x00000, 0x8000, CRC("cffd7fa5") + SHA1("7c5cecce1d428f847fea37d53eb09c6f62055c6f") ),
            ROM_LOAD16_BYTE( "u-17_rev.2_8-14-86.u17", 0x00001, 0x8000, CRC("e92c596b") + SHA1("4e2d87398f2e7b637cbad6cb16d832dfa8f8288c") ),
            ROM_LOAD16_BYTE( "u-8_rev.2_8-14-86.u8",   0x10000, 0x8000, CRC("11f787e4") + SHA1("1fa195bf9169608099d17be5801738a4e17bec3d") ),
            ROM_LOAD16_BYTE( "u-18_rev.2_8-14-86.u18", 0x10001, 0x8000, CRC("6b8bf5e1") + SHA1("aa8c0260dcd19a795bfc23197cd87348a685d20b") ),

            ROM_REGION( 0x08000, "gfx1", ROMREGION_INVERT ),
            ROM_LOAD( "bg-0_u15_7-23-86.15a", 0x00000, 0x04000, CRC("c0d8b7a5") + SHA1("692219388a3124fb48db7e35c4127b0fe066a289") ),
            ROM_LOAD( "bg-1_u14_7-23-86.14b", 0x04000, 0x04000, CRC("2f6e3aa1") + SHA1("ae86ce90bb6bf660e38c0f91e7ce90d44be82d60") ),

            ROM_REGION( 0x40000, "gfx2", 0 ),
            ROM_LOAD( "fg-0_8e_6-30-86.8e",   0x00000, 0x10000, CRC("0974be5d") + SHA1("be347faaa345383dc6e5c2b3789c372d6bd25905") ),
            ROM_LOAD( "fg-1_6e_6-30-86.6e",   0x10000, 0x10000, CRC("8728532b") + SHA1("327df92db7e3506b827d497859980cd2de51f45d") ),
            ROM_LOAD( "fg-2_5e_6-30-86.5e",   0x20000, 0x10000, CRC("9489f714") + SHA1("df17a45cdc6a9310856d64f89954be79bbeac12e") ),
            ROM_LOAD( "fg-3_4e_6-30-86.4e",   0x30000, 0x10000, CRC("81e1de40") + SHA1("7e7818792845ec3687b3202eeade60a298ef513e") ),

            ROM_REGION( 0x0001, "sg:pal", 0 ),
            ROM_LOAD( "e36a31axnaxqd.u15.bin", 0x0000, 0x0001, NO_DUMP),

            ROM_END,
        };


        static void mcr3_state_mono_sg(machine_config config, device_t device) { mcr3_state mcr3_state = (mcr3_state)device; mcr3_state.mono_sg(config); }
        static void mcr3_state_init_rampage(device_t owner) { mcr3_state mcr3_state = (mcr3_state)owner; mcr3_state.init_rampage(); }

        static mcr3 m_mcr3 = new mcr3();

        static device_t device_creator_rampage(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new mcr3_state(mconfig, type, tag); }
        static device_t device_creator_rampage2(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new mcr3_state(mconfig, type, tag); }


        public static readonly game_driver driver_rampage  = GAME(device_creator_rampage,  rom_rampage,  "1986", "rampage",  "0",       mcr3_state_mono_sg, m_mcr3.construct_ioport_rampage, mcr3_state_init_rampage, ROT0, "Bally Midway", "Rampage (Rev 3, 8/27/86)", MACHINE_SUPPORTS_SAVE);
        public static readonly game_driver driver_rampage2 = GAME(device_creator_rampage2, rom_rampage2, "1986", "rampage2", "rampage", mcr3_state_mono_sg, m_mcr3.construct_ioport_rampage, mcr3_state_init_rampage, ROT0, "Bally Midway", "Rampage (Rev 2, 8/4/86)",  MACHINE_SUPPORTS_SAVE);
    }
}
