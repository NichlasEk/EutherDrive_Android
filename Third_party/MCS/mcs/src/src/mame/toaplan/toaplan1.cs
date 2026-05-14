// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Minimal Toaplan 1 Out Zone registration for Euther Drive MCS bring-up.

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using offs_t = System.UInt32;
using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.diexec_global;
using static mame.emucore_global;
using static mame.emumem_global;
using static mame.gamedrv_global;
using static mame.hash_global;
using static mame.ioport_global;
using static mame.ioport_input_string_helper;
using static mame.ioport_ioport_type_helper;
using static mame.m68000_global;
using static mame.romentry_global;
using static mame.screen_global;
using static mame.z80_global;


namespace mame
{
    class toaplan1_state : driver_device
    {
        const int SharedRamSize = 0x800;
        const int TileOffsetWords = 0x4;
        const int BcuLayerCount = 4;
        const int BcuLayerWords = 0x1000;
        const int SpriteRamWords = 0x800;
        const int SpriteSizeRamWords = 0x80;
        const int PaletteWords = 0x1000;
        const int ScreenWidth = 320;
        const int ScreenHeight = 240;
        static readonly XTAL MasterClock = new XTAL(28_000_000);
        static readonly XTAL PixelClock = MasterClock / 4;
        const int HTotal = 450;
        const int HBEnd = 0;
        const int HBStart = 320;
        const int VTotal55 = 282;
        const int VBEnd = 0;
        const int VBStart = 240;

        readonly required_device<m68000_device> m_maincpu;
        readonly required_device<z80_device> m_audiocpu;
        readonly u8 [] m_sharedram = new u8[SharedRamSize];
        readonly u32 [,] m_bcu_vram = new u32[BcuLayerCount, BcuLayerWords];
        readonly u16 [] m_bcu_scrollx = new u16[BcuLayerCount];
        readonly u16 [] m_bcu_scrolly = new u16[BcuLayerCount];
        readonly u16 [] m_spriteram = new u16[SpriteRamWords];
        readonly u16 [] m_spritesizeram = new u16[SpriteSizeRamWords];
        readonly u16 [,] m_paletteram = new u16[2, PaletteWords];
        readonly u16 [] m_tile_offsets = new u16[TileOffsetWords];
        u16 m_bcu_ram_offs;
        u16 m_spriteram_offs;
        u8 m_fcu_flipscreen;
        u8 m_bcu_flipscreen;
        u8 m_vctrl_intenable;
        int m_frame_counter;


        public toaplan1_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
            m_maincpu = new required_device<m68000_device>(this, "maincpu");
            m_audiocpu = new required_device<z80_device>(this, "audiocpu");
        }


        public void outzone(machine_config config)
        {
            M68000(config, m_maincpu, new XTAL(10_000_000));
            m_maincpu.op0.memory().set_addrmap(AS_PROGRAM, outzone_main_map);

            Z80(config, m_audiocpu, MasterClock / 8);
            m_audiocpu.op0.memory().set_addrmap(AS_PROGRAM, sound_map);
            m_audiocpu.op0.memory().set_addrmap(AS_IO, outzone_sound_io_map);

            config.set_maximum_quantum(attotime.from_hz(600));

            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_screen_update(screen_update);
            screen.set_raw(PixelClock, HTotal, HBEnd, HBStart, VTotal55, VBEnd, VBStart);
            screen.screen_vblank().set((write_line_delegate)screen_vblank).reg();
        }


        void outzone_main_map(address_map map, device_t device)
        {
            map.op(0x000000, 0x03ffff).rom();
            map.op(0x100000, 0x100007).rw((read16_delegate)fcu_host_r, (write16_delegate)fcu_host_w);
            map.op(0x140000, 0x140fff).rw((read16_delegate)shared_r, (write16_delegate)shared_w);
            map.op(0x200000, 0x20001f).rw((read16_delegate)bcu_host_r, (write16_delegate)bcu_host_w);
            map.op(0x240000, 0x243fff).ram();
            map.op(0x300000, 0x307fff).rw((read16_delegate)vctrl_r, (write16_delegate)vctrl_w);
            map.op(0x340000, 0x340007).rw((read16_delegate)tile_offset_r, (write16_delegate)tile_offset_w);
        }


        void sound_map(address_map map, device_t device)
        {
            map.op(0x0000, 0x7fff).rom();
            map.op(0x8000, 0x87ff).rw((read8sm_delegate)shared_sound_r, (write8sm_delegate)shared_sound_w);
        }


        void outzone_sound_io_map(address_map map, device_t device)
        {
            map.global_mask(0xff);
            map.op(0x00, 0x01).rw((read8sm_delegate)ym3812_stub_r, (write8sm_delegate)ym3812_stub_w);
            map.op(0x04, 0x04).w((write8sm_delegate)coin_w);
            map.op(0x08, 0x08).r((read8sm_delegate)dswa_r);
            map.op(0x0c, 0x0c).r((read8sm_delegate)dswb_r);
            map.op(0x10, 0x10).r((read8sm_delegate)system_r);
            map.op(0x14, 0x14).r((read8sm_delegate)p1_r);
            map.op(0x18, 0x18).r((read8sm_delegate)p2_r);
            map.op(0x1c, 0x1c).r((read8sm_delegate)tjump_r);
        }


        u16 fcu_host_r(address_space space, offs_t offset, u16 mem_mask)
        {
            switch ((offset << 1) & 0x6)
            {
                case 0x0:
                    return 1;
                case 0x2:
                    return m_spriteram_offs;
                case 0x4:
                    return m_spriteram[m_spriteram_offs & (SpriteRamWords - 1)];
                case 0x6:
                    return m_spritesizeram[m_spriteram_offs & (SpriteSizeRamWords - 1)];
                default:
                    return 0;
            }
        }


        void fcu_host_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            switch ((offset << 1) & 0x6)
            {
                case 0x2:
                    m_spriteram_offs = CombineWord(m_spriteram_offs, data, mem_mask);
                    break;
                case 0x4:
                    m_spriteram[m_spriteram_offs & (SpriteRamWords - 1)] = CombineWord(m_spriteram[m_spriteram_offs & (SpriteRamWords - 1)], data, mem_mask);
                    m_spriteram_offs++;
                    break;
                case 0x6:
                    m_spritesizeram[m_spriteram_offs & (SpriteSizeRamWords - 1)] = CombineWord(m_spritesizeram[m_spriteram_offs & (SpriteSizeRamWords - 1)], data, mem_mask);
                    m_spriteram_offs++;
                    break;
            }
        }


        u16 shared_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int index = (int)(offset & ((SharedRamSize / 2) - 1));
            return (u16)(0xff00 | m_sharedram[index]);
        }


        void shared_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0x00ff) == 0)
                return;

            int index = (int)(offset & ((SharedRamSize / 2) - 1));
            m_sharedram[index] = (u8)(data & 0xff);
        }


        u16 bcu_host_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int reg = (int)(offset << 1);
            if (reg == 0x02)
                return m_bcu_ram_offs;

            if (reg == 0x04 || reg == 0x06)
                return bcu_tileram_r(reg);

            if (reg >= 0x10 && reg <= 0x1f)
                return bcu_scroll_r((reg - 0x10) >> 1);

            return 0;
        }


        void bcu_host_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int reg = (int)(offset << 1);
            if (reg == 0x00)
                return;

            if (reg == 0x02)
            {
                m_bcu_ram_offs = CombineWord(m_bcu_ram_offs, data, mem_mask);
                return;
            }

            if (reg == 0x04 || reg == 0x06)
            {
                bcu_tileram_w(reg, data, mem_mask);
                return;
            }

            if (reg >= 0x10 && reg <= 0x1f)
                bcu_scroll_w((reg - 0x10) >> 1, data, mem_mask);
        }


        u16 vctrl_r(address_space space, offs_t offset, u16 mem_mask)
        {
            int byteOffset = (int)(offset << 1);
            if (byteOffset == 0)
                return 1;

            if (byteOffset >= 0x4000 && byteOffset < 0x6000)
                return m_paletteram[0, (byteOffset - 0x4000) >> 1];

            if (byteOffset >= 0x6000 && byteOffset < 0x8000)
                return m_paletteram[1, (byteOffset - 0x6000) >> 1];

            return 0;
        }


        void vctrl_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int byteOffset = (int)(offset << 1);
            if (byteOffset == 0x0002)
            {
                m_vctrl_intenable = (u8)(data & 0xff);
                return;
            }

            if (byteOffset >= 0x4000 && byteOffset < 0x6000)
            {
                int index = (byteOffset - 0x4000) >> 1;
                m_paletteram[0, index] = CombineWord(m_paletteram[0, index], data, mem_mask);
                return;
            }

            if (byteOffset >= 0x6000 && byteOffset < 0x8000)
            {
                int index = (byteOffset - 0x6000) >> 1;
                m_paletteram[1, index] = CombineWord(m_paletteram[1, index], data, mem_mask);
            }
        }


        u16 tile_offset_r(address_space space, offs_t offset, u16 mem_mask)
        {
            return m_tile_offsets[offset & (TileOffsetWords - 1)];
        }


        void tile_offset_w(address_space space, offs_t offset, u16 data, u16 mem_mask)
        {
            int index = (int)(offset & (TileOffsetWords - 1));
            m_tile_offsets[index] = CombineWord(m_tile_offsets[index], data, mem_mask);

            if (index == 3)
                m_fcu_flipscreen = (u8)(data & 0xff);
        }


        u8 shared_sound_r(offs_t offset)
        {
            return m_sharedram[offset & (SharedRamSize - 1)];
        }


        void shared_sound_w(offs_t offset, u8 data)
        {
            m_sharedram[offset & (SharedRamSize - 1)] = data;
        }


        u8 ym3812_stub_r(offs_t offset) => 0xff;
        void ym3812_stub_w(offs_t offset, u8 data) { }
        void coin_w(offs_t offset, u8 data) { }
        u8 dswa_r(offs_t offset) => (u8)(ioport("DSWA").read() & 0xff);
        u8 dswb_r(offs_t offset) => (u8)(ioport("DSWB").read() & 0xff);
        u8 system_r(offs_t offset) => (u8)(ioport("SYSTEM").read() & 0xff);
        u8 p1_r(offs_t offset) => (u8)(ioport("P1").read() & 0xff);
        u8 p2_r(offs_t offset) => (u8)(ioport("P2").read() & 0xff);
        u8 tjump_r(offs_t offset) => (u8)(ioport("TJUMP").read() & 0xff);


        void screen_vblank(int state)
        {
            if (state != 0 && m_vctrl_intenable != 0)
                m_maincpu.op0.set_input_line(4, HOLD_LINE);
        }


        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            for (int y = cliprect.min_y; y <= cliprect.max_y; y++)
            {
                for (int x = cliprect.min_x; x <= cliprect.max_x; x++)
                {
                    u8 shade = (u8)(((x >> 3) ^ (y >> 3) ^ m_frame_counter) & 0x1f);
                    bitmap.pix(y, x)[0] = 0xff000000U | (uint32_t)(shade << 11) | (uint32_t)(shade << 3);
                }
            }

            m_frame_counter++;
            return 0;
        }


        protected override void machine_reset()
        {
            Array.Clear(m_sharedram, 0, m_sharedram.Length);
            Array.Clear(m_bcu_vram, 0, m_bcu_vram.Length);
            Array.Clear(m_bcu_scrollx, 0, m_bcu_scrollx.Length);
            Array.Clear(m_bcu_scrolly, 0, m_bcu_scrolly.Length);
            Array.Clear(m_spriteram, 0, m_spriteram.Length);
            Array.Clear(m_spritesizeram, 0, m_spritesizeram.Length);
            Array.Clear(m_paletteram, 0, m_paletteram.Length);
            Array.Clear(m_tile_offsets, 0, m_tile_offsets.Length);
            m_bcu_ram_offs = 0;
            m_spriteram_offs = 0;
            m_fcu_flipscreen = 0;
            m_bcu_flipscreen = 0;
            m_vctrl_intenable = 0;
            m_frame_counter = 0;
        }


        u16 bcu_tileram_r(int reg)
        {
            int layer = (m_bcu_ram_offs >> 12) & 3;
            int index = m_bcu_ram_offs & (BcuLayerWords - 1);
            int shift = (reg == 0x04) ? 16 : 0;
            return (u16)((m_bcu_vram[layer, index] >> shift) & 0xffff);
        }


        void bcu_tileram_w(int reg, u16 data, u16 mem_mask)
        {
            int layer = (m_bcu_ram_offs >> 12) & 3;
            int index = m_bcu_ram_offs & (BcuLayerWords - 1);
            int shift = (reg == 0x04) ? 16 : 0;
            u32 mask = (u32)mem_mask << shift;
            m_bcu_vram[layer, index] = (m_bcu_vram[layer, index] & ~mask) | ((u32)(data & mem_mask) << shift);
        }


        u16 bcu_scroll_r(int scrollIndex)
        {
            int layer = scrollIndex >> 1;
            return (scrollIndex & 1) == 0 ? m_bcu_scrollx[layer] : m_bcu_scrolly[layer];
        }


        void bcu_scroll_w(int scrollIndex, u16 data, u16 mem_mask)
        {
            int layer = scrollIndex >> 1;
            if ((scrollIndex & 1) == 0)
                m_bcu_scrollx[layer] = CombineWord(m_bcu_scrollx[layer], data, mem_mask);
            else
                m_bcu_scrolly[layer] = CombineWord(m_bcu_scrolly[layer], data, mem_mask);
        }


        static u16 CombineWord(u16 value, u16 data, u16 mem_mask)
        {
            if ((mem_mask & 0xff00) != 0)
                value = (u16)((value & 0x00ff) | (data & 0xff00));
            if ((mem_mask & 0x00ff) != 0)
                value = (u16)((value & 0xff00) | (data & 0x00ff));
            return value;
        }


        public void init_outzone()
        {
        }
    }


    public class toaplan1 : construct_ioport_helper
    {
        const u32 ROM_GROUPWORD = 0x100;
        static readonly toaplan1 m_toaplan1 = new toaplan1();

        static tiny_rom_entry ROM_LOAD16_WORD(string name, u32 offset, u32 length, string hash)
        {
            return ROMX_LOAD(name, offset, length, hash, ROM_GROUPWORD);
        }

        static readonly tiny_rom_entry [] rom_outzone =
        {
            ROM_REGION(0x040000, "maincpu", 0),
            ROM_LOAD16_BYTE("tp_018_07.6f", 0x000001, 0x020000, CRC("9704db16") + SHA1("3d65c7a24d0a7c62d9f68745a177ea1ab06b8d69")),
            ROM_LOAD16_BYTE("tp_018_08.6f", 0x000000, 0x020000, CRC("127a38d7") + SHA1("7dfe88264c7094c876e00f789e3b3f757c76dcfc")),

            ROM_REGION(0x008000, "audiocpu", 0),
            ROM_LOAD("tp_018_09.3j", 0x000000, 0x008000, CRC("73d8e235") + SHA1("6d19566485318bb036d16eedb38abebbfb320ce1")),

            ROM_REGION(0x100000, "tiles", 0),
            ROM_LOAD16_WORD("tp-018_rom5.19h", 0x000000, 0x080000, CRC("c64ec7b6") + SHA1("690804336ea640d1fd907cbb4b826f1bd3eec396")),
            ROM_LOAD16_WORD("tp-018_rom6.22h", 0x080000, 0x080000, CRC("64b6c5ac") + SHA1("0eba3469c1b06f6b9e2da7d34181f31470461f13")),

            ROM_REGION(0x080000, "sprites", 0),
            ROM_LOAD16_BYTE("tp-018_rom2.1c", 0x000000, 0x020000, CRC("6bb72d16") + SHA1("075b79af804275e7e0679c22d827a2315c98acbc")),
            ROM_LOAD16_BYTE("tp-018_rom1.1e", 0x000001, 0x020000, CRC("0934782d") + SHA1("169e4896d19a632e226b571de9329d5f6f449b08")),
            ROM_LOAD16_BYTE("tp-018_rom3.1d", 0x040000, 0x020000, CRC("ec903c07") + SHA1("6cdf6b92665e6b1c5df1f5a304a83eb8995a9d0f")),
            ROM_LOAD16_BYTE("tp-018_rom4.1b", 0x040001, 0x020000, CRC("50cbf1a8") + SHA1("0133deecd464d9d057c838759e76b48e77202d7e")),

            ROM_REGION(0x000040, "proms", 0),
            ROM_LOAD("tp018_10.rom10.18a", 0x000000, 0x000020, CRC("bc88cced") + SHA1("d59307282bcf24ee8d2bd1b8c6d26343abbac225")),
            ROM_LOAD("tp018_11.rom11.22c", 0x000020, 0x000020, CRC("a1e17492") + SHA1("2ab7fb10672b7ef17271266f760e6e45611549c6")),

            ROM_END,
        };


        static void toaplan1_state_outzone(machine_config config, device_t device) { ((toaplan1_state)device).outzone(config); }
        static void toaplan1_state_init_outzone(device_t owner) { ((toaplan1_state)owner).init_outzone(); }
        static device_t device_creator_toaplan1_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new toaplan1_state(mconfig, (device_type)type, tag); }


        void construct_ioport_outzone(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("DSWA");
            PORT_DIPNAME(0x07, 0x07, DEF_STR(Coin_A)); PORT_DIPLOCATION("SW1:1,2,3");
            PORT_DIPSETTING(0x07, DEF_STR(_1C_1C));
            PORT_DIPNAME(0x38, 0x38, DEF_STR(Coin_B)); PORT_DIPLOCATION("SW1:4,5,6");
            PORT_DIPSETTING(0x38, DEF_STR(_1C_1C));
            PORT_DIPNAME(0x40, 0x40, DEF_STR(Demo_Sounds)); PORT_DIPLOCATION("SW1:7");
            PORT_DIPSETTING(0x00, DEF_STR(Off));
            PORT_DIPSETTING(0x40, DEF_STR(On));
            PORT_SERVICE_DIPLOC(0x80, IP_ACTIVE_LOW, "SW1:8");

            PORT_START("DSWB");
            PORT_DIPNAME(0x03, 0x03, DEF_STR(Difficulty)); PORT_DIPLOCATION("SW2:1,2");
            PORT_DIPSETTING(0x03, DEF_STR(Normal));
            PORT_DIPNAME(0x0c, 0x0c, DEF_STR(Bonus_Life)); PORT_DIPLOCATION("SW2:3,4");
            PORT_DIPSETTING(0x0c, "Every 300k");
            PORT_DIPNAME(0x30, 0x30, DEF_STR(Lives)); PORT_DIPLOCATION("SW2:5,6");
            PORT_DIPSETTING(0x30, "3");
            PORT_DIPNAME(0x40, 0x40, "Invulnerability"); PORT_DIPLOCATION("SW2:7");
            PORT_DIPSETTING(0x40, DEF_STR(Off));
            PORT_DIPSETTING(0x00, DEF_STR(On));
            PORT_DIPNAME(0x80, 0x80, DEF_STR(Allow_Continue)); PORT_DIPLOCATION("SW2:8");
            PORT_DIPSETTING(0x00, DEF_STR(No));
            PORT_DIPSETTING(0x80, DEF_STR(Yes));

            PORT_START("SYSTEM");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_COIN1);
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_COIN2);
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_START1);
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_START2);
            PORT_BIT(0xf0, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("P1");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_UNUSED);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("P2");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(2);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(2);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_UNUSED);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_UNUSED);

            PORT_START("TJUMP");
            PORT_BIT(0xff, IP_ACTIVE_LOW, IPT_UNUSED);
        }


        public static readonly game_driver driver_outzone = GAME(device_creator_toaplan1_state, rom_outzone, "1990", "outzone", "0", toaplan1_state_outzone, m_toaplan1.construct_ioport_outzone, toaplan1_state_init_outzone, ROT270, "Toaplan", "Out Zone", MACHINE_IS_SKELETON);
    }
}
