using System.Diagnostics;

using System.Threading;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // Bus arbiter : chips:315-5308 (headless version, no UI)
    //----------------------------------------------------------------
    internal class md_bus
    {
        // ------------------------------------------------------------
        // READ
        // ------------------------------------------------------------
         public static MegaDriveBus? Current { get; set; }
        private bool _z80BusGranted = true;
        private bool _z80ForceGrant = true;
        private bool _z80Reset;
        private long _z80BusReqWriteCount;
        private long _z80BusReqToggleCount;
        private long _z80ResetWriteCount;
        private long _z80ResetToggleCount;
        private int _z80RegReadLogRemaining = 32;
        private int _z80RegWriteLogRemaining = 32;
        private int _z80WinLogRemaining = 64;
        private bool _z80WinWarned;
        private int _ymWriteLogRemaining = 64;
        private static readonly bool TraceZ80Win =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN"), "1", StringComparison.Ordinal);
        private static bool _ymEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYm =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM"), "1", StringComparison.Ordinal);

        private static bool IsZ80BusReq(uint addr) => (addr & 0xFFFFFE) == 0xA11100;
        private static bool IsZ80Reset(uint addr) => (addr & 0xFFFFFE) == 0xA11200;

        public void Reset()
        {
            _z80BusGranted = true;
            _z80ForceGrant = true;
            _z80Reset = false;
            _z80RegReadLogRemaining = 32;
            _z80RegWriteLogRemaining = 32;
            _z80WinLogRemaining = 64;
            _z80WinWarned = false;
            _z80BusReqWriteCount = 0;
            _z80BusReqToggleCount = 0;
            _z80ResetWriteCount = 0;
            _z80ResetToggleCount = 0;
        }

        internal bool Z80BusGranted => _z80BusGranted;
        internal bool Z80Reset => _z80Reset;
        internal void ConsumeZ80SignalStats(out long busReqWrites, out long busReqToggles, out long resetWrites, out long resetToggles)
        {
            busReqWrites = Interlocked.Exchange(ref _z80BusReqWriteCount, 0);
            busReqToggles = Interlocked.Exchange(ref _z80BusReqToggleCount, 0);
            resetWrites = Interlocked.Exchange(ref _z80ResetWriteCount, 0);
            resetToggles = Interlocked.Exchange(ref _z80ResetToggleCount, 0);
        }

        internal static void SetYmEnabled(bool enabled)
        {
            _ymEnabled = enabled;
        }


        public byte read8(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                // Force grant only until the first busreq write, then reflect state.
                byte val = _z80ForceGrant ? (byte)0x00 : (_z80BusGranted ? (byte)0x00 : (byte)0x01);
                LogZ80RegRead(in_address, val);
                return val;
            }

            if (IsZ80Reset(in_address))
            {
                byte val = _z80Reset ? (byte)0x00 : (byte)0x01;
                LogZ80RegRead(in_address, val);
                return val;
            }

            // 0x000000–0x3FFFFF  | ROM / cart
            if (in_address <= 0x3FFFFF)
                return md_m68k.read8(in_address);

            // 0xFF0000–0xFFFFFF  | Work RAM (mirrors)
            if (in_address >= 0xFF0000)
                return md_m68k.read8(in_address);

            // 0xC00000–0xDFFFFF  | VDP space
            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
                return md_main.g_md_vdp != null ? md_main.g_md_vdp.read8(in_address) : (byte)0xFF;

            // 0xA10000–0xA10FFF  | I/O (controllers, etc)
            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
                return md_main.g_md_io != null ? md_main.g_md_io.read8(in_address) : (byte)0xFF;

            // 0xA04000–0xA04003   | YM2612 (read)
            if (in_address >= 0xA04000 && in_address <= 0xA04003)
                return _ymEnabled && md_main.g_md_music != null
                    ? md_main.g_md_music.g_md_ym2612.read8(in_address)
                    : (byte)0xFF;

            // 0xA11000–0xA1FFFF  | Control
            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
                return md_main.g_md_control != null ? md_main.g_md_control.read8(in_address) : (byte)0xFF;

            // 0xA00000–0xA0FFFF  | Z80 bus
            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
                return md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address) : (byte)0xFF;

            // Okänt område → "open bus"
            Debug.WriteLine($"[BUS] read8 @0x{in_address:X6} (open)");
            return 0xFF;
        }

        public ushort read16(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                // Force grant only until the first busreq write, then reflect state.
                ushort val = _z80ForceGrant ? (ushort)0x0000 : (_z80BusGranted ? (ushort)0x0000 : (ushort)0x0001);
                LogZ80RegRead(in_address, val);
                return val;
            }

            if (IsZ80Reset(in_address))
            {
                ushort val = _z80Reset ? (ushort)0x0000 : (ushort)0x0001;
                LogZ80RegRead(in_address, val);
                return val;
            }

            if (in_address <= 0x3FFFFF)
                return md_m68k.read16(in_address);

            if (in_address >= 0xFF0000)
                return md_m68k.read16(in_address);

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
                return md_main.g_md_vdp != null ? md_main.g_md_vdp.read16(in_address) : (ushort)0xFFFF;

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
                return md_main.g_md_io != null ? md_main.g_md_io.read16(in_address) : (ushort)0xFFFF;

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
                return md_main.g_md_control != null ? md_main.g_md_control.read16(in_address) : (ushort)0xFFFF;

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
                return md_main.g_md_z80 != null ? md_main.g_md_z80.read16(in_address) : (ushort)0xFFFF;

            Debug.WriteLine($"[BUS] read16 @0x{in_address:X6} (open)");
            return 0xFFFF;
        }

        public uint read32(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                // Force grant only until the first busreq write, then reflect state.
                uint val = _z80ForceGrant ? 0x0000_0000u : (_z80BusGranted ? 0x0000_0000u : 0x0000_0001u);
                LogZ80RegRead(in_address, val);
                return val;
            }

            if (IsZ80Reset(in_address))
            {
                uint val = _z80Reset ? 0x0000_0000u : 0x0000_0001u;
                LogZ80RegRead(in_address, val);
                return val;
            }

            if (in_address <= 0x3FFFFF)
                return md_m68k.read32(in_address);

            if (in_address >= 0xFF0000)
                return md_m68k.read32(in_address);

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
                return md_main.g_md_vdp != null ? md_main.g_md_vdp.read32(in_address) : 0xFFFF_FFFFu;

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
                return md_main.g_md_io != null ? md_main.g_md_io.read32(in_address) : 0xFFFF_FFFF;

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
                return md_main.g_md_control != null ? md_main.g_md_control.read32(in_address) : 0xFFFF_FFFF;

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
                return md_main.g_md_z80 != null ? md_main.g_md_z80.read32(in_address) : 0xFFFF_FFFFu;

            Debug.WriteLine($"[BUS] read32 @0x{in_address:X6} (open)");
            return 0xFFFF_FFFF;
        }

        // ------------------------------------------------------------
        // WRITE
        // ------------------------------------------------------------
        public void write8(uint in_address, byte in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                // 1 = request bus (grant), 0 = release (no grant)
                bool prev = _z80BusGranted;
                bool next = (in_data & 0x01) != 0;
                _z80BusGranted = next;
                _z80ForceGrant = false;
                if (md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                Interlocked.Increment(ref _z80BusReqWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80BusReqToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80BUSREQ] write addr=0x{in_address:X6} value=0x{in_data:X2}");
                return;
            }

            if (IsZ80Reset(in_address))
            {
                bool prev = _z80Reset;
                bool next = (in_data & 0x01) == 0;
                _z80Reset = next;
                if (prev != next && md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !next;
                Interlocked.Increment(ref _z80ResetWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80ResetToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80RESET]  write addr=0x{in_address:X6} value=0x{in_data:X2}");
                return;
            }

            if (in_address >= 0xFF0000)
            {
                md_m68k.write8(in_address, in_data);
                return;
            }

            // 0xC00010/11 SN76489 (PSG) – tills ljud kopplas in, ignorera
            if (in_address == 0xC00010 || in_address == 0xC00011)
            {
                md_main.g_md_music?.g_md_sn76489.write8(in_data);
                md_psg_trace.TraceWrite("68K", in_address, in_data, md_m68k.g_reg_PC);
                return;
            }

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
            {
                md_main.g_md_vdp?.write8(in_address, in_data);
                return;
            }

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
            {
                if (md_main.g_md_io != null) md_main.g_md_io.write8(in_address, in_data);
                return;
            }

            // 0xA04000–0xA04003 YM2612
            if (in_address >= 0xA04000 && in_address <= 0xA04003)
            {
                if (_ymEnabled)
                {
                    md_main.g_md_music?.g_md_ym2612.write8(in_address, in_data);
                    if (TraceYm && _ymWriteLogRemaining > 0)
                    {
                        _ymWriteLogRemaining--;
                        Console.WriteLine($"[YMTRACE] 68K pc=0x{md_m68k.g_reg_PC:X6} addr=0x{in_address:X6} val=0x{in_data:X2}");
                    }
                }
                return;
            }

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
            {
                if (md_main.g_md_control != null) md_main.g_md_control.write8(in_address, in_data);
                return;
            }

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
            {
                md_main.g_md_z80?.write8(in_address, in_data);
                if (TraceZ80Win && _z80WinLogRemaining > 0)
                {
                    _z80WinLogRemaining--;
                    uint z80Index = in_address & 0x1FFF;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN] W8 addr=0x{in_address:X6} -> z80[{z80Index:X4}]=0x{in_data:X2} busReq={busReq} reset={reset}");
                }
                if (TraceZ80Win && !_z80BusGranted && !_z80WinWarned)
                {
                    _z80WinWarned = true;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X2} busReq={busReq} reset={reset}");
                }
                return;
            }

            // Övrigt: no-op
            Debug.WriteLine($"[BUS] write8 @0x{in_address:X6} = 0x{in_data:X2} (ignored)");
        }

        public void write16(uint in_address, ushort in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                bool prev = _z80BusGranted;
                bool next = (in_data & 0x0100) != 0;
                _z80BusGranted = next;
                _z80ForceGrant = false;
                if (md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                Interlocked.Increment(ref _z80BusReqWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80BusReqToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80BUSREQ] write addr=0x{in_address:X6} value=0x{in_data:X4}");
                return;
            }

            if (IsZ80Reset(in_address))
            {
                bool prev = _z80Reset;
                bool next = (in_data & 0x0100) == 0;
                _z80Reset = next;
                if (prev != next && md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !next;
                Interlocked.Increment(ref _z80ResetWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80ResetToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80RESET]  write addr=0x{in_address:X6} value=0x{in_data:X4}");
                return;
            }

            if (in_address >= 0xFF0000)
            {
                md_m68k.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
            {
                md_main.g_md_vdp?.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xA10000 && in_address <= 0xA10FFF)
            {
                if (md_main.g_md_io != null) md_main.g_md_io.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xA11000 && in_address <= 0xA1FFFF)
            {
                if (md_main.g_md_control != null) md_main.g_md_control.write16(in_address, in_data);
                return;
            }

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
            {
                md_main.g_md_z80?.write16(in_address, in_data);
                if (TraceZ80Win && _z80WinLogRemaining > 0)
                {
                    _z80WinLogRemaining--;
                    uint z80Index = in_address & 0x1FFF;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN] W16 addr=0x{in_address:X6} -> z80[{z80Index:X4}]=0x{in_data:X4} busReq={busReq} reset={reset}");
                }
                if (TraceZ80Win && !_z80BusGranted && !_z80WinWarned)
                {
                    _z80WinWarned = true;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X4} busReq={busReq} reset={reset}");
                }
                return;
            }

            Debug.WriteLine($"[BUS] write16 @0x{in_address:X6} = 0x{in_data:X4} (ignored)");
        }

        public void write32(uint in_address, uint in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                bool prev = _z80BusGranted;
                bool next = (in_data & 0x0100_0000u) != 0;
                _z80BusGranted = next;
                _z80ForceGrant = false;
                if (md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                Interlocked.Increment(ref _z80BusReqWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80BusReqToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80BUSREQ] write addr=0x{in_address:X6} value=0x{in_data:X8}");
                return;
            }

            if (IsZ80Reset(in_address))
            {
                bool prev = _z80Reset;
                bool next = (in_data & 0x0100_0000u) == 0;
                _z80Reset = next;
                if (prev != next && md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !next;
                Interlocked.Increment(ref _z80ResetWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80ResetToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80RESET]  write addr=0x{in_address:X6} value=0x{in_data:X8}");
                return;
            }

            if (in_address >= 0xFF0000)
            {
                md_m68k.write32(in_address, in_data);
                return;
            }

            if (in_address >= 0xC00000 && in_address <= 0xDFFFFF)
            {
                md_main.g_md_vdp?.write32(in_address, in_data);
                return;
            }

            if (in_address >= 0xA00000 && in_address <= 0xA0FFFF)
            {
                // (debug kvar från originalet)
                // if (in_address == 0xA01FFC || in_address == 0xA01FFD ||
                //     in_address == 0xA01FFE || in_address == 0xA01FFF) { }

                md_main.g_md_z80?.write32(in_address, in_data);
                if (TraceZ80Win && _z80WinLogRemaining > 0)
                {
                    _z80WinLogRemaining--;
                    uint z80Index = in_address & 0x1FFF;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN] W32 addr=0x{in_address:X6} -> z80[{z80Index:X4}]=0x{in_data:X8} busReq={busReq} reset={reset}");
                }
                if (TraceZ80Win && !_z80BusGranted && !_z80WinWarned)
                {
                    _z80WinWarned = true;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X8} busReq={busReq} reset={reset}");
                }
                return;
            }

            if (in_address == 0xA14000)
            {
                // TMSS – ignorera tills vidare
                return;
            }

            Debug.WriteLine($"[BUS] write32 @0x{in_address:X6} = 0x{in_data:X8} (ignored)");
        }

        private void LogZ80RegRead(uint addr, uint val)
        {
            if (_z80RegReadLogRemaining <= 0)
                return;
            _z80RegReadLogRemaining--;
            MdLog.WriteLine($"[md_bus] Z80 reg read 0x{addr:X6} -> 0x{val:X}");
        }

        private void LogZ80RegWrite(uint addr, uint val)
        {
            if (_z80RegWriteLogRemaining <= 0)
                return;
            _z80RegWriteLogRemaining--;
            MdLog.WriteLine($"[md_bus] Z80 reg write 0x{addr:X6} <- 0x{val:X}");
        }
    }
}
