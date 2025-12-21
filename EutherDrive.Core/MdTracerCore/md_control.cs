    // Minimal stub tills riktiga kontrollregistret finns
    internal class md_control
    {
        public byte   read8(uint addr)    => 0xFF;
        public ushort read16(uint addr)   => 0xFFFF;
        public uint   read32(uint addr)   => 0xFFFF_FFFF;

        public void write8(uint addr, byte val) { }
        public void write16(uint addr, ushort val) { }
        public void write32(uint addr, uint val) { }
    }
