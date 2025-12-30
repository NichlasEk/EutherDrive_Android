namespace EutherDrive.Core.MdTracerCore
{
    internal sealed class md_m68k
    {
        public bool g_interrupt_V_req;
        public bool g_interrupt_H_req;
        public bool g_interrupt_EXT_req;

        public int g_clock_total;
        public int g_clock_now;

        // --- BUS READ STUBS (för VDP-DMA) ---
        public ushort read16(uint address) => 0;
        public ushort read16(int address) => 0;
    }

    internal sealed class md_z80
    {
        public void irq_request(bool _) { }
        public void irq_request(bool _, string __, byte ___) { }
    }
}
