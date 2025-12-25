using System;
using System.Diagnostics;
using EutherDrive.Core;

namespace EutherDrive.Core.MdTracerCore
{
    // OBS: måste vara samma "shape" som övriga md_io- partials:
    // - INTE static
    // - partial så den kan fortsätta ligga i flera filer (md_io_pad.cs osv)
    internal partial class md_io
    {
        private bool _pad1Th = true;
        private bool _pad2Th = true;

        private PadHandshake _pad1Handshake;
        private PadHandshake _pad2Handshake;
        private PadType _pad1Type = PadType.SixButton;
        private PadType _pad2Type = PadType.ThreeButton;
        private int _ioReadLogRemaining = 64;
        private long _ioReadLastTicks;
        private static readonly bool TraceIo =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_IO"), "1", StringComparison.Ordinal);

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
            byte result;
            switch (addr)
            {
                case 0xA10000:
                    result = 0x00;
                    break;
                case 0xA10001:
                    result = 0xA0; // Version register (NTSC, rev 0)
                    break;
                case 0xA10002:
                case 0xA10003:
                    result = ReadPadData(_pad1, _pad1Th, ref _pad1Handshake, _pad1Type);
                    break;
                case 0xA10004:
                case 0xA10005:
                    result = ReadPadData(_pad2, _pad2Th, ref _pad2Handshake, _pad2Type);
                    break;
                case 0xA10008:
                case 0xA10009:
                    result = (byte)(_pad1Th ? 0x40 : 0x00);
                    break;
                case 0xA1000A:
                case 0xA1000B:
                    result = (byte)(_pad2Th ? 0x40 : 0x00);
                    break;
                default:
                    result = 0x00;
                    break;
            }

            MaybeLogIoRead(addr, result, 8);
            return result;
        }

        public ushort read16(uint in_address)
        {
            // Big-endian 16-bit read via två 8-bit (om du vill hålla det enkelt)
            uint addr = in_address & 0xFFFFFF;
            ushort result;
            bool direct = true;
            switch (addr)
            {
                case 0xA10002:
                case 0xA10003:
                    result = (ushort)(0xFF00 | ReadPadData(_pad1, _pad1Th, ref _pad1Handshake, _pad1Type));
                    break;
                case 0xA10004:
                case 0xA10005:
                    result = (ushort)(0xFF00 | ReadPadData(_pad2, _pad2Th, ref _pad2Handshake, _pad2Type));
                    break;
                case 0xA10008:
                case 0xA10009:
                    result = (ushort)(0xFF00 | (_pad1Th ? 0x40 : 0x00));
                    break;
                case 0xA1000A:
                case 0xA1000B:
                    result = (ushort)(0xFF00 | (_pad2Th ? 0x40 : 0x00));
                    break;
                default:
                {
                    byte hi = read8(in_address);
                    byte lo = read8(in_address + 1);
                    result = (ushort)((hi << 8) | lo);
                    direct = false;
                    break;
                }
            }

            if (direct)
                MaybeLogIoRead(addr, result, 16);

            return result;
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

        internal void SetPad1Input(in MdPadState state, PadType padType)
        {
            _pad1 = state;
            if (_pad1Type != padType)
            {
                _pad1Type = padType;
            }

            _pad1Handshake.Stage = 0;
            _pad1Handshake.LastThHigh = _pad1Th;
        }

        internal void SetPad2Input(in MdPadState state, PadType padType)
        {
            _pad2 = state;
            if (_pad2Type != padType)
            {
                _pad2Type = padType;
            }

            _pad2Handshake.Stage = 0;
            _pad2Handshake.LastThHigh = _pad2Th;
        }

        private void MaybeLogIoRead(uint addr, uint value, int widthBits)
        {
            if (!TraceIo)
                return;

            if (addr != 0xA10001 && (addr < 0xA10003 || addr > 0xA1001F))
                return;

            if (_ioReadLogRemaining > 0)
            {
                _ioReadLogRemaining--;
            }
            else
            {
                long now = Stopwatch.GetTimestamp();
                if (now - _ioReadLastTicks < Stopwatch.Frequency)
                    return;
                _ioReadLastTicks = now;
            }

            string val = widthBits == 8 ? value.ToString("X2") : value.ToString("X4");
            Console.WriteLine($"[IOREAD] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{addr:X6} val=0x{val} w={widthBits}");
        }

        private static byte ReadPadData(MdPadState pad, bool thHigh, ref PadHandshake handshake, PadType padType)
        {
            byte v = 0xFF; // active-low

            if (pad.Up) v &= 0xFE;
            if (pad.Down) v &= 0xFD;
            if (pad.Left) v &= 0xFB;
            if (pad.Right) v &= 0xF7;

            if (thHigh)
            {
                if (padType != PadType.SixButton)
                    handshake.Stage = 0;
                handshake.LastThHigh = true;
                if (pad.B) v &= 0xEF;     // bit 4
                if (pad.C) v &= 0xDF;     // bit 5
                v |= 0x40;                // TH = 1
                return v;
            }

            int stage = padType == PadType.SixButton ? handshake.Stage : 0;
            bool advance = padType == PadType.SixButton && handshake.LastThHigh;
            handshake.LastThHigh = false;

            if (padType == PadType.SixButton)
            {
                switch (stage)
                {
                    case 0:
                    case 1:
                        if (pad.A) v &= 0xEF;
                        if (pad.Start) v &= 0xDF;
                        break;
                    case 2:
                        if (pad.X) v &= 0xEF;
                        if (pad.Y) v &= 0xDF;
                        break;
                    default:
                        if (pad.Z) v &= 0xEF;
                        if (pad.Mode) v &= 0xDF;
                        break;
                }

                if (advance)
                {
                    handshake.Stage++;
                    if (handshake.Stage > 3)
                        handshake.Stage = 3;
                }
            }
            else
            {
                if (pad.A) v &= 0xEF;
                if (pad.Start) v &= 0xDF;
            }

            v &= 0xBF; // TH = 0
            return v;
        }

        private struct PadHandshake
        {
            public bool LastThHigh;
            public int Stage;
        }
    }
}
