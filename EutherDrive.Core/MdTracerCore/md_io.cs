namespace EutherDrive.Core.MdTracerCore
{
    // OBS: måste vara samma "shape" som övriga md_io- partials:
    // - INTE static
    // - partial så den kan fortsätta ligga i flera filer (md_io_pad.cs osv)
    internal partial class md_io
    {
        private bool _pad1Th = true;
        private bool _pad2Th = true;

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
            uint addr = in_address & 0xFFFFFF;
            switch (addr)
            {
                case 0xA10000:
                    return 0x00;
                case 0xA10001:
                    return 0xA0; // Version register (NTSC, rev 0)
                case 0xA10002:
                case 0xA10003:
                    return ReadPadData(_pad1, _pad1Th);
                case 0xA10004:
                case 0xA10005:
                    return ReadPadData(_pad2, _pad2Th);
                case 0xA10008:
                case 0xA10009:
                    return (byte)(_pad1Th ? 0x40 : 0x00);
                case 0xA1000A:
                case 0xA1000B:
                    return (byte)(_pad2Th ? 0x40 : 0x00);
                default:
                    return 0x00;
            }
        }

        public ushort read16(uint in_address)
        {
            // Big-endian 16-bit read via två 8-bit (om du vill hålla det enkelt)
            uint addr = in_address & 0xFFFFFF;
            switch (addr)
            {
                case 0xA10002:
                case 0xA10003:
                    return (ushort)(0xFF00 | ReadPadData(_pad1, _pad1Th));
                case 0xA10004:
                case 0xA10005:
                    return (ushort)(0xFF00 | ReadPadData(_pad2, _pad2Th));
                case 0xA10008:
                case 0xA10009:
                    return (ushort)(0xFF00 | (_pad1Th ? 0x40 : 0x00));
                case 0xA1000A:
                case 0xA1000B:
                    return (ushort)(0xFF00 | (_pad2Th ? 0x40 : 0x00));
                default:
                {
                    byte hi = read8(in_address);
                    byte lo = read8(in_address + 1);
                    return (ushort)((hi << 8) | lo);
                }
            }
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
            uint addr = in_address & 0xFFFFFF;
            switch (addr)
            {
                case 0xA10003:
                case 0xA10008:
                case 0xA10009:
                    _pad1Th = (in_val & 0x40) != 0;
                    break;
                case 0xA10005:
                case 0xA1000A:
                case 0xA1000B:
                    _pad2Th = (in_val & 0x40) != 0;
                    break;
                default:
                    break;
            }
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

        private static byte ReadPadData(MdPadState pad, bool thHigh)
        {
            byte v = 0xFF; // active-low

            if (pad.Up) v &= 0xFE;
            if (pad.Down) v &= 0xFD;
            if (pad.Left) v &= 0xFB;
            if (pad.Right) v &= 0xF7;

            if (thHigh)
            {
                if (pad.B) v &= 0xEF;     // bit 4
                if (pad.C) v &= 0xDF;     // bit 5
                v |= 0x40;                // TH = 1
            }
            else
            {
                if (pad.Start) v &= 0xEF; // bit 4
                if (pad.A) v &= 0xDF;     // bit 5
                v &= 0xBF;                // TH = 0
            }

            return v;
        }
    }
}
