using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // Bus arbiter : chips:315-5308 (headless version, no UI)
    //----------------------------------------------------------------
    internal class md_bus
    {
        // ------------------------------------------------------------
        // READ
        // ------------------------------------------------------------
         public static MegaDriveBus? Current { get; set; }


        public byte read8(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            // 0x000000–0x3FFFFF  | ROM / cart
            if (in_address <= 0x3FFFFF)
                return md_m68k.read8(in_address);

            // 0xFF0000–0xFFFFFF  | Work RAM (mirrors)
            if (in_address >= 0xFF0000)
                return md_m68k.read8(in_address);

            // 0xC00000–0xDFFFFF  | VDP space
            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
                return md_main.g_md_vdp.read8(in_address);

            // 0xA10000–0xA10FFF  | I/O (controllers, etc)
            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
                return md_main.g_md_io != null ? md_main.g_md_io.read8(in_address) : (byte)0xFF;

            // 0xA04000            | YM2612 (read)
            // Om/ när ljudet kopplas in: exponera wrappers i md_music istället
            if (in_address == 0xA04000)
                return 0xFF; // placeholder tills md_music/ym2612 är på plats

                // 0xA11000–0xA1FFFF  | Control
                if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
                    return md_main.g_md_control != null ? md_main.g_md_control.read8(in_address) : (byte)0xFF;

            // 0xA00000–0xA0FFFF  | Z80 bus
            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
                return md_main.g_md_z80.read8(in_address);

            // Okänt område → "open bus"
            Debug.WriteLine($"[BUS] read8 @0x{in_address:X6} (open)");
            return 0xFF;
        }

        public ushort read16(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (in_address <= 0x3FFFFF)
                return md_m68k.read16(in_address);

            if (in_address >= 0xFF0000)
                return md_m68k.read16(in_address);

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
                return md_main.g_md_vdp.read16(in_address);

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
                return md_main.g_md_io != null ? md_main.g_md_io.read16(in_address) : (ushort)0xFFFF;

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
                return md_main.g_md_control != null ? md_main.g_md_control.read16(in_address) : (ushort)0xFFFF;

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
                return md_main.g_md_z80.read16(in_address);

            Debug.WriteLine($"[BUS] read16 @0x{in_address:X6} (open)");
            return 0xFFFF;
        }

        public uint read32(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (in_address <= 0x3FFFFF)
                return md_m68k.read32(in_address);

            if (in_address >= 0xFF0000)
                return md_m68k.read32(in_address);

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
                return md_main.g_md_vdp.read32(in_address);

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
                return md_main.g_md_io != null ? md_main.g_md_io.read32(in_address) : 0xFFFF_FFFF;

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
                return md_main.g_md_control != null ? md_main.g_md_control.read32(in_address) : 0xFFFF_FFFF;

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
                return md_main.g_md_z80.read32(in_address);

            Debug.WriteLine($"[BUS] read32 @0x{in_address:X6} (open)");
            return 0xFFFF_FFFF;
        }

        // ------------------------------------------------------------
        // WRITE
        // ------------------------------------------------------------
        public void write8(uint in_address, byte in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (in_address >= 0xFF0000)
            {
                md_m68k.write8(in_address, in_data);
                return;
            }

            // 0xC00010/11 SN76489 (PSG) – tills ljud kopplas in, ignorera
            if (in_address == 0xC00010 || in_address == 0xC00011)
            {
                // TODO: md_music.WritePSG(in_data);
                return;
            }

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
            {
                md_main.g_md_vdp.write8(in_address, in_data);
                return;
            }

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
            {
                if (md_main.g_md_io != null) md_main.g_md_io.write8(in_address, in_data);
                return;
            }

            // 0xA04000–0xA04003 YM2612 – tills ljud kopplas in, ignorera
            if (in_address >= 0xA04000 && in_address <= 0xA04003)
            {
                // TODO: md_music.WriteYM2612(in_address, in_data);
                return;
            }

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
            {
                if (md_main.g_md_control != null) md_main.g_md_control.write8(in_address, in_data);
                return;
            }

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
            {
                md_main.g_md_z80.write8(in_address, in_data);
                return;
            }

            // Övrigt: no-op
            Debug.WriteLine($"[BUS] write8 @0x{in_address:X6} = 0x{in_data:X2} (ignored)");
        }

        public void write16(uint in_address, ushort in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (in_address >= 0xFF0000)
            {
                md_m68k.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
            {
                md_main.g_md_vdp.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
            {
                if (md_main.g_md_io != null) md_main.g_md_io.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
            {
                if (md_main.g_md_control != null) md_main.g_md_control.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
            {
                md_main.g_md_z80.write16(in_address, in_data);
                return;
            }

            Debug.WriteLine($"[BUS] write16 @0x{in_address:X6} = 0x{in_data:X4} (ignored)");
        }

        public void write32(uint in_address, uint in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (in_address >= 0xFF0000)
            {
                md_m68k.write32(in_address, in_data);
                return;
            }

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
            {
                md_main.g_md_vdp.write32(in_address, in_data);
                return;
            }

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
            {
                // (debug kvar från originalet)
                // if (in_address == 0xA01FFC || in_address == 0xA01FFD ||
                //     in_address == 0xA01FFE || in_address == 0xA01FFF) { }

                md_main.g_md_z80.write32(in_address, in_data);
                return;
            }

            if (in_address == 0xA14000)
            {
                // TMSS – ignorera tills vidare
                return;
            }

            Debug.WriteLine($"[BUS] write32 @0x{in_address:X6} = 0x{in_data:X8} (ignored)");
        }
    }
}
