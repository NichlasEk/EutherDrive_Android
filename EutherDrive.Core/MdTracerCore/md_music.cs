namespace EutherDrive.Core.MdTracerCore
{
    // Minimal musikstub så md_z80_memory kan kompilera
    internal partial class md_music
    {
        internal readonly ym2612  g_md_ym2612  = new ym2612();
        internal readonly sn76489 g_md_sn76489 = new sn76489();

        public void reset() { }
        public void run(int cycles) { }

        internal class ym2612
        {
            public byte read8(uint addr) => 0xFF;         // placeholder
            public void write8(uint addr, byte data) { }  // placeholder
        }

        internal class sn76489
        {
            public void write8(byte data) { }             // placeholder
        }
    }
}
