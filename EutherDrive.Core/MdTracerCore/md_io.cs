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
        private PadType _pad1Type = PadType.ThreeButton;
        private PadType _pad2Type = PadType.ThreeButton;
        private readonly ConsoleIdentity _identity = new ConsoleIdentity();
        private ConsoleRegion? _romRegionHint;
        private int _ioReadLogRemaining = 64;
        private long _ioReadLastTicks;
        private static readonly bool TraceIo =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_IO"), "1", StringComparison.Ordinal);
        private static readonly bool TracePadIo =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PAD_IO"), "1", StringComparison.Ordinal);
        private const byte VersionBits = 0x20;

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
                    result = ReadIoVersion();
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
            if (TracePadIo)
                MaybeLogPadIoRead(addr, result, 8);
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
            if (TracePadIo)
                MaybeLogPadIoRead(addr, result, 16);

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
            if (TracePadIo)
                MaybeLogPadIoWrite(addr, in_val);
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

        internal void SetRomRegionHint(ConsoleRegion? hint)
        {
            _romRegionHint = hint;
        }

        internal void SetRegionOverride(ConsoleRegion region)
        {
            _identity.RegionOverride = region;
        }

        private byte ReadIoVersion()
        {
            ConsoleRegion effective = GetEffectiveRegion();
            byte value = VersionBits;
            // bit7=overseas (US/EU), bit6=PAL (EU)
            if (effective == ConsoleRegion.US || effective == ConsoleRegion.EU)
                value |= 0x80;
            if (effective == ConsoleRegion.EU)
                value |= 0x40;
            return value;
        }

        private ConsoleRegion GetEffectiveRegion()
        {
            ConsoleRegion? envOverride = ParseRegionOverrideEnv();
            ConsoleRegion overrideRegion = envOverride ?? _identity.RegionOverride;
            if (overrideRegion != ConsoleRegion.Auto)
                return overrideRegion;
            if (_romRegionHint.HasValue)
                return _romRegionHint.Value;
            return ConsoleRegion.US;
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
            string region = addr == 0xA10001 ? $" region={GetEffectiveRegion()}" : string.Empty;
            Console.WriteLine($"[IOREAD] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{addr:X6} val=0x{val} w={widthBits}{region}");
        }

        private void MaybeLogPadIoRead(uint addr, uint value, int widthBits)
        {
            if (addr != 0xA10003 && addr != 0xA10005 && addr != 0xA10009 && addr != 0xA1000B &&
                addr != 0xA10002 && addr != 0xA10004 && addr != 0xA10008 && addr != 0xA1000A &&
                addr != 0xA10001 && addr != 0xA10000)
                return;

            uint pc = md_m68k.g_reg_PC;
            string pad1 = FormatPadState(_pad1);
            string pad2 = FormatPadState(_pad2);
            string val = widthBits == 8 ? value.ToString("X2") : value.ToString("X4");
            Console.WriteLine(
                $"[PAD-IO-READ] pc=0x{pc:X6} addr=0x{addr:X6} val=0x{val} " +
                $"p1TH={(_pad1Th ? 1 : 0)} p1Stage={_pad1Handshake.Stage} p1Type={_pad1Type} p1={pad1} " +
                $"p2TH={(_pad2Th ? 1 : 0)} p2Stage={_pad2Handshake.Stage} p2Type={_pad2Type} p2={pad2}");
        }

        private void MaybeLogPadIoWrite(uint addr, byte value)
        {
            if (addr != 0xA10003 && addr != 0xA10005 && addr != 0xA10009 && addr != 0xA1000B &&
                addr != 0xA10002 && addr != 0xA10004 && addr != 0xA10008 && addr != 0xA1000A)
                return;

            uint pc = md_m68k.g_reg_PC;
            string pad1 = FormatPadState(_pad1);
            string pad2 = FormatPadState(_pad2);
            Console.WriteLine(
                $"[PAD-IO-WRITE] pc=0x{pc:X6} addr=0x{addr:X6} val=0x{value:X2} " +
                $"p1TH={(_pad1Th ? 1 : 0)} p1Stage={_pad1Handshake.Stage} p1Type={_pad1Type} p1={pad1} " +
                $"p2TH={(_pad2Th ? 1 : 0)} p2Stage={_pad2Handshake.Stage} p2Type={_pad2Type} p2={pad2}");
        }

        private static string FormatPadState(in MdPadState pad)
        {
            Span<char> s = stackalloc char[12];
            int i = 0;
            s[i++] = pad.Up ? 'U' : '-';
            s[i++] = pad.Down ? 'D' : '-';
            s[i++] = pad.Left ? 'L' : '-';
            s[i++] = pad.Right ? 'R' : '-';
            s[i++] = pad.A ? 'A' : '-';
            s[i++] = pad.B ? 'B' : '-';
            s[i++] = pad.C ? 'C' : '-';
            s[i++] = pad.Start ? 'S' : '-';
            s[i++] = pad.X ? 'X' : '-';
            s[i++] = pad.Y ? 'Y' : '-';
            s[i++] = pad.Z ? 'Z' : '-';
            s[i++] = pad.Mode ? 'M' : '-';
            return new string(s);
        }

        private static ConsoleRegion? ParseRegionOverrideEnv()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_REGION");
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "jp":
                case "japan":
                    return ConsoleRegion.JP;
                case "us":
                case "usa":
                    return ConsoleRegion.US;
                case "eu":
                case "europe":
                    return ConsoleRegion.EU;
                case "auto":
                    return null;
                default:
                    return null;
            }
        }

        private static byte ReadPadData(MdPadState pad, bool thHigh, ref PadHandshake handshake, PadType padType)
        {
            byte v = 0xFF; // active-low
            bool sixButton = padType == PadType.SixButton;

            if (!sixButton)
            {
                handshake.Stage = 0;
                handshake.LastThHigh = thHigh;
            }
            else
            {
                if (handshake.LastThHigh != thHigh && handshake.Stage < 6)
                    handshake.Stage++;
                handshake.LastThHigh = thHigh;
            }

            if (thHigh)
            {
                if (sixButton && handshake.Stage >= 6)
                {
                    if (pad.C) v &= 0xDF;    // bit 5
                    if (pad.B) v &= 0xEF;    // bit 4
                    if (pad.Mode) v &= 0xF7; // bit 3
                    if (pad.X) v &= 0xFB;    // bit 2
                    if (pad.Y) v &= 0xFD;    // bit 1
                    if (pad.Z) v &= 0xFE;    // bit 0
                }
                else
                {
                    if (pad.Up) v &= 0xFE;
                    if (pad.Down) v &= 0xFD;
                    if (pad.Left) v &= 0xFB;
                    if (pad.Right) v &= 0xF7;
                    if (pad.B) v &= 0xEF;     // bit 4
                    if (pad.C) v &= 0xDF;     // bit 5
                }

                v |= 0x40; // TH = 1
                return v;
            }

            int stage = sixButton ? handshake.Stage : 0;
            bool specialLow = sixButton && stage >= 5;

            if (!specialLow)
            {
                if (pad.Up) v &= 0xFE;
                if (pad.Down) v &= 0xFD;
            }

            if (pad.A) v &= 0xEF;     // bit 4
            if (pad.Start) v &= 0xDF; // bit 5

            if (specialLow)
                v &= 0xF0; // bits 3-0 = 0
            else
                v &= 0xF3; // bits 3-2 = 0

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
