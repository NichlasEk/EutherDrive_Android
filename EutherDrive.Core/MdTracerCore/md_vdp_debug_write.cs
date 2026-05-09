namespace EutherDrive.Core.MdTracerCore
{
    public partial class md_vdp
    {
        public void DebugWriteVramWord(int address, ushort value)
        {
            vram_write_w(address, value);
            pattern_chk(address, (byte)(value >> 8));
            pattern_chk(address ^ 1, (byte)value);
        }
    }
}
