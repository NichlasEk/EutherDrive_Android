namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        // Bus-åtkomst (tills riktiga registerlogiken är portad)
        public byte read8(uint addr)
        {
            ushort w = read16(addr & 0x00FF_FFFE);
            return (byte)(w & 0xFF);
        }

        public ushort read16(uint addr)
        {
            uint a = addr & 0x00FF_FFFF;
            uint port = a & 0x00000E;

            if (port == 0x04)
            {
                ushort status = get_vdp_status();
                g_vdp_status_7_vinterrupt = 0; // ack on status read
                md_m68k.g_interrupt_V_req = false;
                return status;
            }

            if (port == 0x08)
                return get_vdp_hvcounter();

            return 0xFFFF;
        }

        public uint read32(uint addr)
        {
            ushort hi = read16(addr);
            ushort lo = read16(addr + 2);
            return ((uint)hi << 16) | lo;
        }

        public void write8(uint addr, byte val) { }

        public void write16(uint addr, ushort val)
        {
            // Always log ALL VDP writes for debugging
            Console.WriteLine($"[VDP-WRITE16-ALL] addr=0x{addr:X8} val=0x{val:X4}");
            
            uint a = addr & 0x00FF_FFFF;
            uint port = a & 0x00000E;

            if (port == 0x04)
            {
                Console.WriteLine($"[VDP-CONTROL-PORT] addr=0x{addr:X8} val=0x{val:X4}");
                
                // Register write: 1vvvvvvv dddddddd
                if ((val & 0x8000) != 0)
                {
                    uint reg = (uint)((val >> 8) & 0x1F);
                    byte data = (byte)(val & 0xFF);
                    
                    // Log ALL register writes to DMA source/length registers for Predator 2 debugging
                    if (reg >= 0x13 && reg <= 0x17) // Registers 19-23
                    {
                        Console.WriteLine($"[VDP-CTRL-RAW] raw=0x{val:X4} reg={reg} data=0x{data:X2}");
                    }
                    
                    set_vdp_register(reg, data);
                }
            }
        }

        public void write32(uint addr, uint val)
        {
            // On 68000, 32-bit write to 16-bit port does TWO writes to SAME address
            // First upper word, then lower word (both to same address)
            ushort hi = (ushort)(val >> 16);
            ushort lo = (ushort)(val & 0xFFFF);
            
            // Log for Predator 2 debugging
            Console.WriteLine($"[VDP-WRITE32] addr=0x{addr:X8} val=0x{val:X8} hi=0x{hi:X4} lo=0x{lo:X4}");
            
            write16(addr, hi);  // Upper word
            write16(addr, lo);  // Lower word to SAME address
        }

    }
}
