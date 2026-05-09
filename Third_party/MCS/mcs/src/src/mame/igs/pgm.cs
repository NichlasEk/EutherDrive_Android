// license:BSD-3-Clause
// copyright-holders:Edward Fast
// Ported from MAME pgm.cpp

using System;

using device_type = mame.emu.detail.device_type_impl_base;
using u8 = System.Byte;
using u32 = System.UInt32;
using uint32_t = System.UInt32;

using static mame.emucore_global;
using static mame.gamedrv_global;
using static mame.hash_global;
using static mame.ioport_global;
using static mame.ioport_ioport_type_helper;
using static mame.romentry_global;
using static mame.screen_global;

namespace mame
{
    class pgm_state : driver_device
    {
        const int ScreenWidth = 448;
        const int ScreenHeight = 224;

        public pgm_state(machine_config mconfig, device_type type, string tag)
            : base(mconfig, type, tag)
        {
        }

        public void pgm(machine_config config)
        {
            screen_device screen = SCREEN(config, "screen", SCREEN_TYPE_RASTER);
            screen.set_screen_update(screen_update);
            screen.set_refresh_hz(60);
            screen.set_size(ScreenWidth, ScreenHeight);
            screen.set_visarea(0, ScreenWidth - 1, 0, ScreenHeight - 1);
        }

        uint32_t screen_update(screen_device screen, bitmap_rgb32 bitmap, rectangle cliprect)
        {
            int minY = Math.Max(cliprect.min_y, 0);
            int maxY = Math.Min(cliprect.max_y, ScreenHeight - 1);
            int minX = Math.Max(cliprect.min_x, 0);
            int maxX = Math.Min(cliprect.max_x, ScreenWidth - 1);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    u8 r = (u8)(0x20 + (x * 0x70 / ScreenWidth));
                    u8 g = (u8)(0x18 + (y * 0x60 / ScreenHeight));
                    u8 b = ((x / 32 + y / 32) & 1) == 0 ? (u8)0x30 : (u8)0x58;
                    bitmap.pix(y, x)[0] = (uint)((r << 16) | (g << 8) | b);
                }
            }

            return 0;
        }
    }

    public class pgm : construct_ioport_helper
    {
        const u32 ROM_GROUPWORD = 0x100;
        const u32 ROMREGION_16BIT = 0x100;

        static readonly pgm m_pgm = new pgm();

        static tiny_rom_entry ROM_REGION16_LE(u32 length, string tag, u32 flags)
        {
            return ROM_REGION(length, tag, flags | ROMREGION_16BIT | ROMREGION_LE);
        }

        static tiny_rom_entry ROM_LOAD16_WORD_SWAP(string name, u32 offset, u32 length, string hash)
        {
            return ROMX_LOAD(name, offset, length, hash, ROM_GROUPWORD | ROM_REVERSE);
        }

        static readonly tiny_rom_entry [] rom_pgm =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),

            ROM_REGION(0x280000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),

            ROM_REGION(0x200000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),

            ROM_REGION16_LE(0x1000000, "igs023:sprcol", ROMREGION_ERASEFF),
            ROM_REGION16_LE(0x1000000, "igs023:sprmask", ROMREGION_ERASEFF),

            ROM_END,
        };

        static readonly tiny_rom_entry [] rom_kov =
        {
            ROM_REGION(0x600000, "maincpu", 0),
            ROM_LOAD16_WORD_SWAP("pgm_p01s.u20", 0x000000, 0x020000, CRC("e42b166e") + SHA1("2a9df9ec746b14b74fae48b1a438da14973702ea")),
            ROM_LOAD16_WORD_SWAP("p0600.117", 0x100000, 0x400000, CRC("c4d19fe6") + SHA1("14ef31539bfbc665e76c9703ee01b12228344052")),

            ROM_REGION(0x4000, "prot", ROMREGION_ERASEFF),

            ROM_REGION(0xa00000, "igs023", 0),
            ROM_LOAD("pgm_t01s.rom", 0x000000, 0x200000, CRC("1a7123a0") + SHA1("cc567f577bfbf45427b54d6695b11b74f2578af3")),
            ROM_LOAD("t0600.rom", 0x180000, 0x800000, CRC("4acc1ad6") + SHA1("0668dbd5e856c2406910c6b7382548b37c631780")),

            ROM_REGION16_LE(0x2000000, "igs023:sprcol", 0),
            ROM_LOAD("a0600.rom", 0x0000000, 0x0800000, CRC("d8167834") + SHA1("fa55a99629d03b2ea253392352f70d2c8639a991")),
            ROM_LOAD("a0601.rom", 0x0800000, 0x0800000, CRC("ff7a4373") + SHA1("7def9fca7513ad5a117da230bebd2e3c78679041")),
            ROM_LOAD("a0602.rom", 0x1000000, 0x0800000, CRC("e7a32959") + SHA1("3d0ed684dc5b269238890836b2ce7ef46aa5265b")),
            ROM_LOAD("a0603.rom", 0x1800000, 0x0400000, CRC("ec31abda") + SHA1("ee526655369bae63b0ef0730e9768b765c9950fc")),

            ROM_REGION16_LE(0x1000000, "igs023:sprmask", 0),
            ROM_LOAD("b0600.rom", 0x0000000, 0x0800000, CRC("7d3cd059") + SHA1("00cf994b63337e0e4ebe96453daf45f24192af1c")),
            ROM_LOAD("b0601.rom", 0x0800000, 0x0400000, CRC("a0bb1c2f") + SHA1("0542348c6e27779e0a98de16f04f9c18158f2b28")),

            ROM_REGION(0x800000, "ics", 0),
            ROM_LOAD("pgm_m01s.rom", 0x000000, 0x200000, CRC("45ae7159") + SHA1("d3ed3ff3464557fd0df6b069b2e431528b0ebfa8")),
            ROM_LOAD("m0600.rom", 0x400000, 0x400000, CRC("3ada4fd6") + SHA1("4c87adb25d31cbd41f04fbffe31f7bc37173da76")),

            ROM_END,
        };

        static void pgm_state_pgm(machine_config config, device_t device) { ((pgm_state)device).pgm(config); }
        static device_t device_creator_pgm_state(emu.detail.device_type_impl_base type, machine_config mconfig, string tag, device_t owner, u32 clock) { return new pgm_state(mconfig, (device_type)type, tag); }

        void construct_ioport_pgm(device_t owner, ioport_list portlist, ref string errorbuf)
        {
            INPUT_PORTS_START(owner, portlist, ref errorbuf);

            PORT_START("P1");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(1); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(1);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(1);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(1);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_START1);

            PORT_START("P2");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_JOYSTICK_UP); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_JOYSTICK_DOWN); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_JOYSTICK_LEFT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x08, IP_ACTIVE_LOW, IPT_JOYSTICK_RIGHT); PORT_PLAYER(2); PORT_8WAY();
            PORT_BIT(0x10, IP_ACTIVE_LOW, IPT_BUTTON1); PORT_PLAYER(2);
            PORT_BIT(0x20, IP_ACTIVE_LOW, IPT_BUTTON2); PORT_PLAYER(2);
            PORT_BIT(0x40, IP_ACTIVE_LOW, IPT_BUTTON3); PORT_PLAYER(2);
            PORT_BIT(0x80, IP_ACTIVE_LOW, IPT_START2);

            PORT_START("SYSTEM");
            PORT_BIT(0x01, IP_ACTIVE_LOW, IPT_COIN1);
            PORT_BIT(0x02, IP_ACTIVE_LOW, IPT_COIN2);
            PORT_BIT(0x04, IP_ACTIVE_LOW, IPT_SERVICE1);
            PORT_BIT(0xf8, IP_ACTIVE_LOW, IPT_UNUSED);
        }

        public static readonly game_driver driver_pgm = GAME(device_creator_pgm_state, rom_pgm, "1997", "pgm", "0", pgm_state_pgm, m_pgm.construct_ioport_pgm, driver_device.empty_init, ROT0, "IGS", "PGM (Polygame Master) System BIOS", MACHINE_IS_BIOS_ROOT | MACHINE_IS_SKELETON);
        public static readonly game_driver driver_kov = GAME(device_creator_pgm_state, rom_kov, "1999", "kov", "pgm", pgm_state_pgm, m_pgm.construct_ioport_pgm, driver_device.empty_init, ROT0, "IGS", "Knights of Valour / Sanguo Zhan Ji / Sangoku Senki (ver. 117, Hong Kong)", MACHINE_IS_SKELETON | MACHINE_IMPERFECT_SOUND | MACHINE_UNEMULATED_PROTECTION);
    }
}
