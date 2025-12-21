namespace EutherDrive.Core.MdTracerCore
{
    // OBS: måste vara samma "shape" som övriga md_io- partials:
    // - INTE static
    // - partial så den kan fortsätta ligga i flera filer (md_io_pad.cs osv)
    internal partial class md_io
    {
        // Global pekare (som md_bus.Current)
        public static md_io? Current { get; set; }

        // Om overlay / debugkod vill läsa kontroller statiskt:
        // md_io.Pad1 / md_io.Pad2
        public static MdPadState Pad1 => Current?._pad1 ?? default;
        public static MdPadState Pad2 => Current?._pad2 ?? default;

        // Interna states (fylls typiskt i md_io_pad.cs)
        internal MdPadState _pad1;
        internal MdPadState _pad2;

        // ------------------------------------------------------------
        // READ
        // ------------------------------------------------------------
        public byte read8(uint in_address)
        {
            // TODO: implementera riktig IO-map om/när du vill.
            // Just nu: returnera "ingen data"
            return 0;
        }

        public ushort read16(uint in_address)
        {
            // Big-endian 16-bit read via två 8-bit (om du vill hålla det enkelt)
            byte hi = read8(in_address);
            byte lo = read8(in_address + 1);
            return (ushort)((hi << 8) | lo);
        }

        public uint read32(uint in_address)
        {
            ushort hi = read16(in_address);
            ushort lo = read16(in_address + 2);
            return ((uint)hi << 16) | lo;
        }

        // ------------------------------------------------------------
        // WRITE
        // ------------------------------------------------------------
        public void write8(uint in_address, byte in_val)
        {
            // TODO: riktig IO-write routing.
        }

        public void write16(uint in_address, ushort in_val)
        {
            // Big-endian split
            write8(in_address, (byte)(in_val >> 8));
            write8(in_address + 1, (byte)(in_val & 0xFF));
        }

        public void write32(uint in_address, uint in_val)
        {
            write16(in_address, (ushort)(in_val >> 16));
            write16(in_address + 2, (ushort)(in_val & 0xFFFF));
        }
    }
}
