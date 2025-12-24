namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_vdp
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
            uint a = addr & 0x00FF_FFFF;
            uint port = a & 0x00000E;

            if (port == 0x04)
            {
                // Register write: 1vvvvvvv dddddddd
                if ((val & 0x8000) != 0)
                {
                    uint reg = (uint)((val >> 8) & 0x1F);
                    byte data = (byte)(val & 0xFF);
                    set_vdp_register(reg, data);
                }
            }
        }

        public void write32(uint addr, uint val)
        {
            write16(addr, (ushort)(val >> 16));
            write16(addr + 2, (ushort)(val & 0xFFFF));
        }

    }
}
