using System.Diagnostics;
using System.Text;
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
        private bool _z80BusGranted = false;
        private bool _z80ForceGrant = false;
        private bool _z80Reset;
        private long _z80BusReqWriteCount;
        private long _z80BusReqToggleCount;
        private long _z80ResetWriteCount;
        private long _z80ResetToggleCount;
        private int _z80BusAckLogState = -1;
        private int _z80BusAckLogState8 = -1;
        private int _z80RegReadLogRemaining = 32;
        private int _z80RegWriteLogRemaining = 32;
        private int _z80WinLogRemaining = 64;
        private int _z80BankRegLogRemaining = 16;
        private int _mbx68kLogRemaining = 128;
        private int _mbx68kReadLogRemaining = 64;
        private readonly byte[] _mbx68kLast = new byte[0x10];
        private bool _z80Win68kLogged;
        private bool _z80WinWarned;
        private int _ymWriteLogRemaining = 64;
        private static readonly bool TraceZ80Win =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Mbx =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX"), "1", StringComparison.Ordinal);
        private static bool MapZ80OddReadToNext => ReadEnvDefaultOn("EUTHERDRIVE_Z80_ODD_READ_TO_NEXT");
        private static bool TraceZ80Sig => MdLog.TraceZ80Sig;
        private static bool MirrorZ80Mailbox => ReadEnvDefaultOn("EUTHERDRIVE_MBX_MIRROR");
        private static bool _ymEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYm =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM"), "1", StringComparison.Ordinal);
        private static bool IsZ80BusReq(uint addr) => (addr & 0xFFFFFE) == 0xA11100;
        private static bool IsZ80Reset(uint addr) => (addr & 0xFFFFFE) == 0xA11200;
        private static bool IsZ80Mailbox(uint addr) => addr >= 0xA01B80 && addr <= 0xA01B8F;
        private static bool IsZ80BankReg(uint addr) => (addr & 0xFFFFFE) == 0xA06000;
        private bool CanAccessZ80BusRange(uint addr, int size)
        {
            if (_z80BusGranted || _z80Reset)
                return true;
            for (int i = 0; i < size; i++)
            {
                uint target = addr + (uint)i;
                if (IsZ80Mailbox(target) || IsZ80BankReg(target))
                    return true;
            }
            return false;
        }

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void Reset()
        {
            _z80BusGranted = false;
            _z80ForceGrant = false;
            _z80Reset = false;
            _z80RegReadLogRemaining = 32;
            _z80RegWriteLogRemaining = 32;
            _z80WinLogRemaining = 64;
            _z80BankRegLogRemaining = 16;
            _z80WinWarned = false;
            _z80BusReqWriteCount = 0;
            _z80BusReqToggleCount = 0;
            _z80ResetWriteCount = 0;
            _z80ResetToggleCount = 0;
            _z80BusAckLogState = -1;
            _z80BusAckLogState8 = -1;
            _mbx68kLogRemaining = 128;
            _mbx68kReadLogRemaining = 64;
            for (int i = 0; i < _mbx68kLast.Length; i++)
                _mbx68kLast[i] = 0xFF;
            _z80Win68kLogged = false;
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
                byte val = BuildBusAckRead8();
                LogZ80BusAckRead8(in_address, val);
                return val;
            }

            if (IsZ80Reset(in_address))
            {
                ushort val = BuildResetReadValue();
                LogZ80RegRead(in_address, val);
                return (byte)(val & 0x01);
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
            {
                if (!CanAccessZ80BusRange(in_address, 1))
                    return 0xFF;
                uint z80Addr = in_address;
                if (MapZ80OddReadToNext && (in_address & 1) != 0)
                    z80Addr = in_address + 1;
                byte val = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(z80Addr) : (byte)0xFF;
                LogZ80MailboxRead("8", z80Addr, val);
                return val;
            }

            // Okänt område → "open bus"
            Debug.WriteLine($"[BUS] read8 @0x{in_address:X6} (open)");
            return 0xFF;
        }

        public ushort read16(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                ushort val = BuildBusAckRead16();
                LogZ80BusAckRead(in_address, val);
                return val;
            }

            if (IsZ80Reset(in_address))
            {
                ushort val = BuildResetReadValue();
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
            {
                if (!CanAccessZ80BusRange(in_address, 2))
                    return 0xFFFF;
                if (IsZ80Mailbox(in_address) || IsZ80Mailbox(in_address + 1))
                {
                    byte mbxHi = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address) : (byte)0xFF;
                    byte mbxLo = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address + 1) : (byte)0xFF;
                    ushort mbxWord = (ushort)((mbxHi << 8) | mbxLo);
                    LogZ80MailboxRead("16", in_address, mbxWord);
                    return mbxWord;
                }
                uint byteAddr = (in_address & 1) == 0 ? in_address : in_address + 1;
                byte mbxVal8 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(byteAddr) : (byte)0xFF;
                ushort mbxMirrorWord = (ushort)((mbxVal8 << 8) | mbxVal8);
                LogZ80MailboxRead("16", in_address, mbxMirrorWord);
                return mbxMirrorWord;
            }

            Debug.WriteLine($"[BUS] read16 @0x{in_address:X6} (open)");
            return 0xFFFF;
        }

        public uint read32(uint in_address)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                ushort word = BuildBusAckRead16();
                uint val = (uint)((word << 16) | word);
                LogZ80BusAckRead(in_address, word);
                return val;
            }

            if (IsZ80Reset(in_address))
            {
                ushort word = BuildResetReadValue();
                uint val = (uint)((word << 16) | word);
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
            {
                if (!CanAccessZ80BusRange(in_address, 4))
                    return 0xFFFF_FFFFu;
                uint baseAddr = (in_address & 1) == 0 ? in_address : in_address + 1;
                byte hi0 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(baseAddr) : (byte)0xFF;
                byte hi1 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(baseAddr + 2) : (byte)0xFF;
                uint val = (uint)(((hi0 << 8) | hi0) << 16 | ((hi1 << 8) | hi1));
                LogZ80MailboxRead("32", in_address, val);
                return val;
            }

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
                uint aligned = in_address & 0xFFFFFE;
                bool requestedStop = (in_data & 0x01) != 0;
                ushort word = (ushort)(requestedStop ? 0x0100 : 0x0000);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80BUSREQ8] write addr=0x{in_address:X6} value=0x{in_data:X2} -> write16 addr=0x{aligned:X6} value=0x{word:X4}");
                write16(aligned, word);
                return;
            }

            if (IsZ80Reset(in_address))
            {
                uint aligned = in_address & 0xFFFFFE;
                ushort word = (in_address & 1) == 0 ? (ushort)(in_data << 8) : in_data;
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80RESET8]  write addr=0x{in_address:X6} value=0x{in_data:X2} -> write16 addr=0x{aligned:X6} value=0x{word:X4}");
                write16(aligned, word);
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
                if (!CanAccessZ80BusRange(in_address, 1))
                {
                    if (TraceZ80Win && !_z80WinWarned)
                    {
                        _z80WinWarned = true;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X2} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                uint z80Addr = in_address;
                if ((in_address & 0xFFFFFE) == 0xA06000)
                    LogZ80BankRegWrite("8", in_address, in_data);
                LogZ80Win68kOnce("byte", z80Addr, in_data);
                byte oldMbx = 0;
                bool hasOld = false;
                if (IsZ80Mailbox(z80Addr) && md_main.g_md_z80 != null)
                {
                    oldMbx = md_main.g_md_z80.PeekMailboxByte((int)(z80Addr - 0xA01B80));
                    hasOld = true;
                }
                md_main.g_md_z80?.write8(z80Addr, in_data);
                md_main.g_md_z80?.RecordMailboxWriteFrom68k(z80Addr, in_data);
                MaybeMirrorMailboxWrite(z80Addr, in_data);
                LogZ80MailboxByte(z80Addr, in_data, hasOld ? oldMbx : (byte)0xFF);
                if (TraceZ80Win && _z80WinLogRemaining > 0)
                {
                    _z80WinLogRemaining--;
                    uint z80Index = z80Addr & 0x1FFF;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN] W8 addr=0x{z80Addr:X6} -> z80[{z80Index:X4}]=0x{in_data:X2} busReq={busReq} reset={reset}");
                }
                if (TraceZ80Win && !_z80BusGranted && !_z80WinWarned)
                {
                    _z80WinWarned = true;
                    int busReq = _z80BusGranted ? 1 : 0;
                    int reset = _z80Reset ? 1 : 0;
                    Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{z80Addr:X6} val=0x{in_data:X2} busReq={busReq} reset={reset}");
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
                bool next = (in_data & 0x0101) != 0;
                _z80BusGranted = next;
                _z80ForceGrant = false;
                if (md_main.g_md_z80 != null)
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                Interlocked.Increment(ref _z80BusReqWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80BusReqToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (prev != next)
                    Console.WriteLine($"[Z80BUSREQ] write addr=0x{in_address:X6} val=0x{in_data:X4} stopOn={(next ? 1 : 0)}");
                if (prev != next && TraceZ80Sig)
                    Console.WriteLine($"[Z80SIG] BUSREQ={(next ? 1 : 0)} (stopOn={(next ? 1 : 0)})");
                _z80BusAckLogState = -1;
                _z80BusAckLogState8 = -1;
                return;
            }

            if (IsZ80Reset(in_address))
            {
                bool prev = _z80Reset;
                bool next = (in_data & 0x0100) == 0;
                _z80Reset = next;
                if (prev != next && md_main.g_md_z80 != null)
                {
                    if (next)
                    {
                        md_main.BeginZ80ResetCycle();
                        md_main.g_md_z80.reset();
                    }
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                }
                Interlocked.Increment(ref _z80ResetWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80ResetToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (prev != next)
                    Console.WriteLine($"[Z80RESET]  write addr=0x{in_address:X6} val=0x{in_data:X4} resetOn={(next ? 1 : 0)}");
                if (prev != next && TraceZ80Sig)
                    Console.WriteLine($"[Z80SIG] RESET={(next ? 1 : 0)} ({(next ? "assert" : "deassert")})");
                _z80BusAckLogState = -1;
                _z80BusAckLogState8 = -1;
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
                if (!CanAccessZ80BusRange(in_address, 2))
                {
                    if (TraceZ80Win && !_z80WinWarned)
                    {
                        _z80WinWarned = true;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X4} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                if (IsZ80Mailbox(in_address) || IsZ80Mailbox(in_address + 1))
                {
                    byte mbxHi = (byte)((in_data >> 8) & 0xFF);
                    byte mbxLo = (byte)(in_data & 0xFF);
                    write8(in_address, mbxHi);
                    write8(in_address + 1, mbxLo);
                    return;
                }
                uint byteAddr = (in_address & 1) == 0 ? in_address : in_address + 1;
                byte hi = (byte)((in_data >> 8) & 0xFF);
                write8(byteAddr, hi);
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
                bool next = (in_data & 0x0101_0101u) != 0;
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
                if (prev != next && TraceZ80Sig)
                    Console.WriteLine($"[Z80SIG] BUSREQ={(next ? 1 : 0)} (stopOn={(next ? 1 : 0)})");
                return;
            }

            if (IsZ80Reset(in_address))
            {
                bool prev = _z80Reset;
                bool next = (in_data & 0x0100_0000u) == 0;
                _z80Reset = next;
                if (prev != next && md_main.g_md_z80 != null)
                {
                    if (next)
                    {
                        md_main.BeginZ80ResetCycle();
                        md_main.g_md_z80.reset();
                    }
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                }
                Interlocked.Increment(ref _z80ResetWriteCount);
                if (prev != next)
                    Interlocked.Increment(ref _z80ResetToggleCount);
                LogZ80RegWrite(in_address, in_data);
                if (TraceZ80Win)
                    Console.WriteLine($"[Z80RESET]  write addr=0x{in_address:X6} value=0x{in_data:X8}");
                if (prev != next && TraceZ80Sig)
                    Console.WriteLine($"[Z80SIG] RESET={(next ? 1 : 0)} ({(next ? "assert" : "deassert")})");
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
                if (!CanAccessZ80BusRange(in_address, 4))
                {
                    if (TraceZ80Win && !_z80WinWarned)
                    {
                        _z80WinWarned = true;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X8} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                if (IsZ80Mailbox(in_address) || IsZ80Mailbox(in_address + 1) ||
                    IsZ80Mailbox(in_address + 2) || IsZ80Mailbox(in_address + 3))
                {
                    byte mbxB3 = (byte)((in_data >> 24) & 0xFF);
                    byte mbxB2 = (byte)((in_data >> 16) & 0xFF);
                    byte mbxB1 = (byte)((in_data >> 8) & 0xFF);
                    byte mbxB0 = (byte)(in_data & 0xFF);
                    write8(in_address, mbxB3);
                    write8(in_address + 1, mbxB2);
                    write8(in_address + 2, mbxB1);
                    write8(in_address + 3, mbxB0);
                    return;
                }
                uint baseAddr = (in_address & 1) == 0 ? in_address : in_address + 1;
                byte b3 = (byte)((in_data >> 24) & 0xFF);
                byte b1 = (byte)((in_data >> 8) & 0xFF);
                write8(baseAddr, b3);
                write8(baseAddr + 2, b1);
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

        private void LogZ80BankRegWrite(string size, uint addr, uint value)
        {
            if (_z80BankRegLogRemaining <= 0)
                return;
            _z80BankRegLogRemaining--;
            int busReq = _z80BusGranted ? 1 : 0;
            string fmt = size switch
            {
                "8" => "X2",
                "16" => "X4",
                _ => "X8"
            };
            Console.WriteLine($"[Z80BANKREG68K] W{size} addr=0x{addr:X6} value=0x{value.ToString(fmt)} busReq={busReq}");
        }

        private void MaybeMirrorMailboxWrite(uint addr, byte value)
        {
            if (!MirrorZ80Mailbox)
                return;
            if (addr < 0xA01B88 || addr > 0xA01B8F)
                return;
            uint mirrorAddr = addr - 0x08;
            md_main.g_md_z80?.write8(mirrorAddr, value);
        }

        private void LogZ80RegWrite(uint addr, uint val)
        {
            if (_z80RegWriteLogRemaining <= 0)
                return;
            _z80RegWriteLogRemaining--;
            MdLog.WriteLine($"[md_bus] Z80 reg write 0x{addr:X6} <- 0x{val:X}");
        }

        private void LogZ80MailboxByte(uint addr, byte value, byte oldValue)
        {
            if (!IsZ80Mailbox(addr))
                return;
            MbxSyncTrace.Record68kWrite(addr, value);
            if (!MbxSyncTrace.IsEnabled && !TraceZ80Mbx)
                return;
            int index = (int)(addr - 0xA01B80);
            if (_mbx68kLogRemaining <= 0)
                return;
            if (value == oldValue)
                return;
            _mbx68kLogRemaining--;
            _mbx68kLast[index] = value;
            string dump = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxDump() : string.Empty;
            Console.WriteLine(
                $"[MBXW68K] pc68k={md_m68k.g_reg_PC:X6} addr={addr:X6} z80={addr & 0x1FFF:X4} val={value:X2} dump= {dump}");
        }

        private void LogZ80MailboxRead(string size, uint addr, uint value)
        {
            if (!MbxSyncTrace.IsEnabled)
                return;
            if (_mbx68kReadLogRemaining <= 0)
                return;
            int bytes = size == "8" ? 1 : size == "16" ? 2 : 4;
            bool hit = false;
            for (int i = 0; i < bytes; i++)
            {
                if (IsZ80Mailbox(addr + (uint)i))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit)
                return;
            _mbx68kReadLogRemaining--;
            string fmt = size == "8" ? "X2" : size == "16" ? "X4" : "X8";
            string dump = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxDump() : string.Empty;
            Console.WriteLine(
                $"[MBXR68K] pc68k={md_m68k.g_reg_PC:X6} addr={addr:X6} size={size} val=0x{value.ToString(fmt)} dump= {dump}");
        }

        // BUSACK semantics: bit0 == 1 when Z80 is running, bit0 == 0 when bus granted to 68k.
        private byte BuildBusAckRead8()
        {
            bool busAck = !_z80BusGranted && !_z80Reset;
            return (byte)(busAck ? 0x01 : 0x00);
        }

        private ushort BuildBusAckRead16()
        {
            byte status = BuildBusAckRead8();
            return (ushort)((status << 8) | status);
        }

        private ushort BuildResetReadValue()
        {
            return _z80Reset ? (ushort)0x0000 : (ushort)0x0100;
        }

        private void LogZ80BusAckRead(uint addr, ushort value)
        {
            int busAck = (value & 0x01) != 0 ? 1 : 0;
            int prev = _z80BusAckLogState;
            _z80BusAckLogState = busAck;
            Console.WriteLine(
                $"[Z80BUSACK] read  addr=0x{addr & 0xFFFFFE:X6} -> 0x{value:X4} (bit0=BUSACK; 1=Z80 RUNNING, 0=BUS GRANTED) status={busAck} prev={prev}");
        }

        private void LogZ80BusAckRead8(uint addr, byte value)
        {
            int busAck = (value & 0x01) != 0 ? 1 : 0;
            if (_z80BusAckLogState8 == busAck)
                return;
            _z80BusAckLogState8 = busAck;
            Console.WriteLine(
                $"[Z80BUSACK8] read addr=0x{addr & 0xFFFFFE:X6} -> 0x{value:X2} (bit0=BUSACK; 1=Z80 RUNNING, 0=BUS GRANTED)");
        }

        private void LogZ80Win68kOnce(string size, uint addr, uint value)
        {
            if (!MbxSyncTrace.IsEnabled)
                return;
            if (_z80Win68kLogged)
                return;
            _z80Win68kLogged = true;
            Console.WriteLine(
                $"[Z80WIN68K] pc68k={md_m68k.g_reg_PC:X6} addr={addr:X6} size={size} val={value:X}");
        }
    }

    internal static class MbxSyncTrace
    {
        private static readonly bool Enabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBXSYNC"), "1", StringComparison.Ordinal);
        private static readonly object Sync = new();
        private static bool _pending;
        private static uint _last68kAddr;
        private static byte _last68kValue;
        private static long _last68kFrame;
        private static long _statSecond = -1;
        private static int _statW68k;
        private static int _statSync;

        internal static bool IsEnabled => Enabled;

        internal static void Record68kWrite(uint addr, byte value)
        {
            if (!Enabled)
                return;
            if (addr != 0xA01B88 && addr != 0xA01B89)
                return;
            lock (Sync)
            {
                UpdateStatsSecond();
                _statW68k++;
                _pending = true;
                _last68kAddr = addr;
                _last68kValue = value;
                _last68kFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
            }
        }

        internal static void MaybeSyncOnZ80Read(ushort addr, byte value, ushort pc, byte[] z80Ram)
        {
            if (!Enabled)
                return;
            if (addr < 0x1B80 || addr > 0x1B8F)
                return;
            lock (Sync)
            {
                UpdateStatsSecond();
                if (!_pending)
                    return;
                bool shouldClear = addr >= 0x1B88 || value == _last68kValue;
                if (!shouldClear)
                    return;
                _statSync++;
                string dump = BuildDump(z80Ram);
                Console.WriteLine(
                    $"[MBXSYNC] F={_last68kFrame} last68k={_last68kAddr:X6}:{_last68kValue:X2} " +
                    $"z80Read={addr:X4}:{value:X2} pc={pc:X4} dump= {dump}");
                _pending = false;
            }
        }

        internal static bool TryGetLast68k(out uint addr, out byte value)
        {
            addr = 0;
            value = 0;
            if (!Enabled)
                return false;
            lock (Sync)
            {
                if (!_pending)
                    return false;
                addr = _last68kAddr;
                value = _last68kValue;
                return true;
            }
        }

        private static void UpdateStatsSecond()
        {
            long nowSec = Environment.TickCount64 / 1000;
            if (_statSecond == -1)
            {
                _statSecond = nowSec;
                return;
            }
            if (nowSec == _statSecond)
                return;
            if (_statW68k > 0 || _statSync > 0)
                Console.WriteLine($"[MBXSTAT] sec={_statSecond} w68k={_statW68k} sync={_statSync}");
            _statW68k = 0;
            _statSync = 0;
            _statSecond = nowSec;
        }

        private static string BuildDump(byte[] z80Ram)
        {
            int baseIndex = 0x1B80 & 0x1FFF;
            StringBuilder sb = new StringBuilder(16 * 3 - 1);
            for (int i = 0; i < 0x10; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(z80Ram[(baseIndex + i) & 0x1FFF].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
