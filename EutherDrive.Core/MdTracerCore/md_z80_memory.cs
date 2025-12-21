using System.Diagnostics;
using static EutherDrive.Core.MdTracerCore.md_m68k;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        private byte[] g_ram;
        private uint g_bank_register; // 68k-fönstrets basadress (maskad)

        //----------------------------------------------------------------
        // read
        //----------------------------------------------------------------
        public byte read8(uint in_address)
        {
            byte w_out = 0;
            ushort a = (ushort)(in_address & 0xFFFF);

            if (a < 0x4000)
            {
                // 8 KB Z80 RAM (0x0000..0x1FFF) speglad över 0x0000..0x3FFF
                w_out = g_ram[(ushort)(a & 0x1FFF)];
            }
            else if (a <= 0x5FFF)
            {
                // YM2612
                w_out = md_main.g_md_music.g_md_ym2612.read8(a);
            }
            else if (a >= 0x6000 && a <= 0x7EFF)
            {
                // I/O/UB – returnera “öppet bussvärde”
                w_out = 0xFF;
            }
            else if (a >= 0x8000)
            {
                // 68k bankfönster, 0x8000..0xFFFF => 32 KB
                // Maskera alltid till 32KB offset och OR:a med bankbasen.
                uint m68kAddr = g_bank_register | (uint)(a & 0x7FFF);
                w_out = md_m68k.read8(m68kAddr);
            }
            else
            {
                MessageBox.Show("md_z80_memory.read8", "error");
            }

            return w_out;
        }

        public ushort read16(uint in_address)
        {
            // Läs via read8 så MMIO-sidoeffekter och wrapping blir korrekt
            ushort a = (ushort)(in_address & 0xFFFF);
            byte hi = read8(a);
            byte lo = read8((ushort)(a + 1));
            return (ushort)((hi << 8) | lo);
        }

        public uint read32(uint in_address)
        {
            ushort a = (ushort)(in_address & 0xFFFF);
            byte b3 = read8(a);
            byte b2 = read8((ushort)(a + 1));
            byte b1 = read8((ushort)(a + 2));
            byte b0 = read8((ushort)(a + 3));
            return (uint)((b3 << 24) | (b2 << 16) | (b1 << 8) | b0);
        }

        //----------------------------------------------------------------
        // write
        //----------------------------------------------------------------
        public void write8(uint in_address, byte in_data)
        {
            ushort a = (ushort)(in_address & 0xFFFF);

            if (a < 0x4000)
            {
                // Z80 RAM (speglad)
                g_ram[(ushort)(a & 0x1FFF)] = in_data;
            }
            else if (a >= 0x4000 && a <= 0x5FFF)
            {
                // YM2612
                md_main.g_md_music.g_md_ym2612.write8(a, in_data);
            }
            else if (a >= 0x6000 && a <= 0x60FF)
            {
                // Z80 bank register till 68k-bussen:
                // Standard: 32KB fönster; bankbas = (in_data << 15), maskad till 0x00FF8000
                // (dvs bit 0 på in_data => 0x00008000, bit 7 => 0x00400000)
                g_bank_register = (uint)(in_data << 15) & 0x00FF8000;
            }
            else if (a >= 0x6100 && a <= 0x7EFF)
            {
                // “nothing”
            }
            else if (a == 0x7F11)
            {
                // SN76489 PSG
                md_main.g_md_music.g_md_sn76489.write8(in_data);
            }
            else if (a >= 0x8000)
            {
                // 68k bankfönster (32KB)
                uint m68kAddr = g_bank_register | (uint)(a & 0x7FFF);
                md_m68k.write8(m68kAddr, in_data);
            }
            else
            {
                MessageBox.Show("md_z80_memory.write8", "error");
            }
        }

        public void write16(uint in_address, ushort in_data)
        {
            // Skriv via write8 så MMIO hanteras korrekt och wrapping funkar
            ushort a = (ushort)(in_address & 0xFFFF);
            write8(a,     (byte)((in_data >> 8) & 0xFF));
            write8((ushort)(a + 1), (byte)(in_data & 0xFF));
        }

        public void write32(uint in_address, uint in_data)
        {
            ushort a = (ushort)(in_address & 0xFFFF);
            write8(a,                     (byte)((in_data >> 24) & 0xFF));
            write8((ushort)(a + 1),       (byte)((in_data >> 16) & 0xFF));
            write8((ushort)(a + 2),       (byte)((in_data >> 8)  & 0xFF));
            write8((ushort)(a + 3),       (byte)( in_data        & 0xFF));
        }
    }
}
