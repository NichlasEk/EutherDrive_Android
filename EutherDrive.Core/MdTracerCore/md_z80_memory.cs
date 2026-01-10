using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EutherDrive.Core.MdTracerCore;
using static EutherDrive.Core.MdTracerCore.md_m68k;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_z80
    {
        private byte[] g_ram = Array.Empty<byte>();
        private uint g_bank_register; // Z80 bankregister (9-bit index)
        private const int SmsLogLimit = 48;
        private int _smsLogCount;
        private byte _smsBankSelect;
        private const int SmsPortLogLimit = 16;
        private static int _smsPortBeReadLog;
        private static int _smsPortBfReadLog;
        private static int _smsPortBeWriteLog;
        private static int _smsPortBfWriteLog;
        private static int _smsPort7EWriteLog;
        private static int _smsPort7FWriteLog;
        private static bool _smsFirstBeWriteLogged;
        private static bool _smsFirstBfWriteLogged;
        private static long _smsStatusPollFrame = -1;
        private static bool _forceStatus7Logged;
        private int _ymWriteLogRemaining = 64;
        private int _z80BankRegLogRemaining = 16;
        private int _z80BankReadLogRemaining = 32;
        private int _z80MailboxReadLogRemaining = 256;
        private int _z80MailboxWideLogRemaining = 128;
        private int _z80MailboxWideReadAllRemaining = 128;
        private readonly byte[] _z80MailboxSnapshot = new byte[0x10];
        private readonly byte[] _mbxShadow = new byte[0x10];
        private bool _mbxShadowValid;
        private bool _mbxWideCmdLatchValid;
        private byte _mbxWideCmdLatchValue;
        private int _z80MbxPollReads;
        private int _z80MbxPollWideReads;
        private ushort _z80MbxPollLastAddr;
        private byte _z80MbxPollLastValue;
        private ushort _z80MbxPollLastPc;
        private byte _z80MbxPollEdgeLastValue;
        private bool _z80MbxPollEdgeLastValid;
        private int _z80MbxPollEdgeRemaining;
        private int _z80MbxPollDataRemaining;
        private readonly byte[] _z80MbxPollDataLast = new byte[0x10];
        private bool _z80MbxPollDataLastValid;
        // Wait loop polling histogram (for 0x11C3-0x11DB NOP loop)
        private static readonly int[] _waitLoopPollHist = new int[0x2000]; // Full Z80 RAM histogram
        private static readonly bool TraceZ80WaitLoop =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_WAITLOOP"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80WaitLoopLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_WAITLOOP_LIMIT", 256);
        private int _waitLoopLoggedCount;
        private bool _z80Flag65LastReadValid;
        private byte _z80Flag65LastReadValue;
        private bool _z80PostFlagLastReadValid;
        private byte _z80PostFlagLastReadValue;
        private long _z80BankStatSecond = -1;
        private int _z80BankStatReadCount;
        private int _z80BankStatReadFfCount;
        private ushort _z80BankStatLastPc;
        private ushort _z80BankStatLastAddr;
        private static readonly bool ForceSmsStatus7 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_SMS_STATUS7"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYm =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Win =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80BankEnv =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80BANK"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Io =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_IO"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80IoLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_IO_LIMIT");
        private static readonly bool TraceZ80BootIo =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_BOOT_IO"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80BootIoInstrLimit = ParseZ80BootIoLimit();
        private static bool TraceZ80Bank => TraceZ80BankEnv || TraceZ80Win || MbxSyncTrace.IsEnabled;
        private static bool TraceZ80Ym => MdTracerCore.MdLog.TraceZ80Ym;
        private static readonly int TraceZ80YmLimit = ParseZ80YmLimit();
        private static readonly bool TraceMbxSync =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBXSYNC"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Mbx =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Flag65 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_0065"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80Flag65Limit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_0065_LIMIT");
        private static readonly bool ForceZ80Flag65ReadFromMbx =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80FLAG65_READ_FROM_MBX"), "1", StringComparison.Ordinal);
        private static readonly ushort ForceZ80Flag65ReadAddr =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG65_READ_ADDR") ?? 0x0065;
        private static readonly ushort ForceZ80Flag65ReadSource =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG65_READ_SOURCE") ?? 0x1B8F;
        private static readonly byte? ForceZ80Flag65ReadAnd =
            ParseByteEnvLocal("EUTHERDRIVE_Z80FLAG65_READ_AND");
        private static readonly byte? ForceZ80Flag65ReadOr =
            ParseByteEnvLocal("EUTHERDRIVE_Z80FLAG65_READ_OR");
        private static readonly int ForceZ80Flag65ReadLimit =
            ParseWatchLimit("EUTHERDRIVE_Z80FLAG65_READ_LIMIT", 32);
        private static readonly bool ForceZ80BootJump =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_BOOT_JP"), "1", StringComparison.Ordinal);
        private static readonly ushort ForceZ80BootJumpTarget =
            ParseZ80Addr("EUTHERDRIVE_Z80_BOOT_JP_TARGET") ?? 0x0D00;
        private static readonly int ForceZ80BootJumpLimit =
            ParseWatchLimit("EUTHERDRIVE_Z80_BOOT_JP_LIMIT", 4);
        private static readonly bool ForceZ80FlagBit2 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80FLAG_FORCE_BIT2"), "1", StringComparison.Ordinal);
        private static readonly ushort ForceZ80FlagBit2Addr =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG_FORCE_BIT2_ADDR") ?? 0x0066;
        private static readonly ushort? ForceZ80FlagBit2Addr2 =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG_FORCE_BIT2_ADDR2");
        private static readonly bool TraceZ80PostFlag =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_POSTFLAG"), "1", StringComparison.Ordinal);
        private static readonly ushort TraceZ80PostFlagStart =
            ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_POSTFLAG_START") ?? 0x0060;
        private static readonly ushort TraceZ80PostFlagEnd =
            ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_POSTFLAG_END") ?? 0x01FF;
        private static readonly int TraceZ80PostFlagLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_POSTFLAG_LIMIT");
        private static readonly bool TraceZ80AfterFlagRet =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_AFTER_FLAGRET"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80AfterFlagRetLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_AFTER_FLAGRET_LIMIT", 256);
        private static readonly bool TraceZ80Ram1800 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_RAM_1800"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80Ram1800Frames =
            ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_RAM_1800_FRAMES", 120);
        private static readonly bool TraceZ80MbxPoll =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBXPOLL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80MbxPollEdge =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX_POLL_EDGE"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80MbxPollEdgeLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_Z80MBX_POLL_EDGE_LIMIT");
        private static readonly bool TraceZ80MbxPollData =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX_POLL_DATA"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80MbxPollDataLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_Z80MBX_POLL_DATA_LIMIT");
        private static readonly bool TraceZ80MbxWideReadAll =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX_WIDE_READ_ALL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80MbxWideCmd =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX_WIDE_CMD"), "1", StringComparison.Ordinal);
        private static readonly bool LatchZ80MbxWideCmd =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_MBX_WIDE_CMD_LATCH"), "1", StringComparison.Ordinal);
        private static readonly bool MirrorZ80Mailbox = ReadEnvDefaultOn("EUTHERDRIVE_MBX_MIRROR");
        private static readonly bool UseMdTracerCompat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_MDTRACER_COMPAT"), "1", StringComparison.Ordinal);
        private static readonly bool Z80WindowWide =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_WINDOW_WIDE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Vdp =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80VDP"), "1", StringComparison.Ordinal);
        private static readonly uint? TraceMemWatchAddr = ParseWatchAddr("EUTHERDRIVE_TRACE_MEM_WATCH");
        private static readonly ushort? TraceZ80Addr = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_ADDR");
        private static readonly bool ForceZ80B154Map =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_Z80_B154_MAP"), "1", StringComparison.Ordinal);
        private static readonly ushort ForceZ80B154Z80Addr =
            ParseZ80Addr("EUTHERDRIVE_FORCE_Z80_B154_Z80ADDR") ?? 0xB154;
        private static readonly ushort? ForceZ80B154Z80Addr2 =
            ParseZ80Addr("EUTHERDRIVE_FORCE_Z80_B154_Z80ADDR2");
        private static readonly uint ForceZ80B154M68kAddr =
            ParseM68kAddr("EUTHERDRIVE_FORCE_Z80_B154_ADDR") ?? 0x00FFB154u;
        private static readonly int ForceZ80B154Limit =
            ParseWatchLimit("EUTHERDRIVE_FORCE_Z80_B154_LIMIT");
        private static readonly int TraceZ80AddrLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_ADDR_LIMIT");
        private int _z80AddrWatchRemaining = TraceZ80AddrLimit;
        private int _forceZ80B154Remaining = ForceZ80B154Limit;
        private int _z80IoLogRemaining = TraceZ80IoLimit;
        private int _z80YmLogRemaining = TraceZ80YmLimit;
        private static readonly ushort? TraceZ80RamWriteAddr = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RAM_WRITE_ADDR");
        private static readonly int TraceZ80RamWriteLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_RAM_WRITE_LIMIT");
        private int _z80RamWriteLogRemaining = TraceZ80RamWriteLimit;
        private static readonly ushort? TraceZ80RamWriteRangeStart = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RAM_WRITE_RANGE_START");
        private static readonly ushort? TraceZ80RamWriteRangeEnd = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RAM_WRITE_RANGE_END");
        private static readonly int TraceZ80RamWriteRangeLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_RAM_WRITE_RANGE_LIMIT");
        private static readonly int TraceZ80RamFillLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_RAM_FILL_LIMIT");
        private int _z80RamWriteRangeRemaining = TraceZ80RamWriteRangeLimit;
        private int _z80RamFillLogRemaining = TraceZ80RamFillLimit;
        private static readonly ushort? TraceZ80RamReadRangeStart = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RAM_READ_RANGE_START");
        private static readonly ushort? TraceZ80RamReadRangeEnd = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RAM_READ_RANGE_END");
        private static readonly int TraceZ80RamReadRangeLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_RAM_READ_RANGE_LIMIT");
        private static readonly ushort? TraceZ80ReadRangeStart = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RD_RANGE_START");
        private static readonly ushort? TraceZ80ReadRangeEnd = ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_RD_RANGE_END");
        private static readonly int TraceZ80ReadRangeLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_Z80_RD_RANGE_LIMIT");
        private int _z80RamReadRangeRemaining = TraceZ80RamReadRangeLimit;
        private int _z80ReadRangeRemaining = TraceZ80ReadRangeLimit;
        private long _z80Ram1800TraceStartFrame = -1;
        private long _z80Ram1800TraceEndFrame = -1;
        private int _z80Flag65ReadRemaining = TraceZ80Flag65Limit;
        private int _z80Flag65WriteRemaining = TraceZ80Flag65Limit;
        private int _z80Flag65ReadOverrideRemaining = ForceZ80Flag65ReadLimit;
        private int _z80BootJumpRemaining = ForceZ80BootJumpLimit;
        private int _z80PostFlagReadRemaining;
        private int _z80AfterFlagRetRemaining;
        private ushort _lastReadAddr;
        private uint _lastReadM68kAddr;
        private byte _lastReadValue;
        private ushort _lastReadPc;
        private bool _lastReadWasBanked;
        private bool _mbx1b8fLastReadValid;
        private byte _mbx1b8fLastReadValue;
        private bool _z80FillActive;
        private ushort _z80FillStartAddr;
        private ushort _z80FillLastAddr;
        private uint _z80FillStartRomAddr;
        private uint _z80FillLastRomAddr;

        private void MaybePatchZ80BootJump()
        {
            if (!ForceZ80BootJump || _z80BootJumpRemaining <= 0)
                return;
            if (g_ram.Length < 3)
                return;
            byte lo = (byte)(ForceZ80BootJumpTarget & 0xFF);
            byte hi = (byte)((ForceZ80BootJumpTarget >> 8) & 0xFF);
            g_ram[0] = 0xC3; // JP nn
            g_ram[1] = lo;
            g_ram[2] = hi;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine(
                $"[Z80BOOT-JP] frame={frame} target=0x{ForceZ80BootJumpTarget:X4} bytes=C3 {lo:X2} {hi:X2}");
            if (_z80BootJumpRemaining != int.MaxValue)
                _z80BootJumpRemaining--;
        }

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static uint? ParseWatchAddr(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (!uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                return null;
            return value & 0x00FF_FFFF;
        }

        private static uint? ParseM68kAddr(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                return hex & 0x00FF_FFFF;
            if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec))
                return dec & 0x00FF_FFFF;
            return null;
        }

        private static ushort? ParseZ80Addr(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (!ushort.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
                return null;
            return value;
        }

        private byte MaybeOverrideFlag65Read(ushort ramAddr, byte value)
        {
            if (!ForceZ80Flag65ReadFromMbx || ramAddr != ForceZ80Flag65ReadAddr)
                return value;
            ushort sourceAddr = (ushort)(ForceZ80Flag65ReadSource & 0x1FFF);
            byte source = g_ram[sourceAddr];
            if (ForceZ80Flag65ReadAnd.HasValue)
                source = (byte)(source & ForceZ80Flag65ReadAnd.Value);
            if (ForceZ80Flag65ReadOr.HasValue)
                source = (byte)(source | ForceZ80Flag65ReadOr.Value);
            if (_z80Flag65ReadOverrideRemaining > 0)
            {
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine(
                    $"[Z80FLAG65-READ] frame={frame} pc=0x{DebugPc:X4} addr=0x{ramAddr:X4} " +
                    $"src=0x{ForceZ80Flag65ReadSource:X4} val=0x{source:X2}");
                if (_z80Flag65ReadOverrideRemaining != int.MaxValue)
                    _z80Flag65ReadOverrideRemaining--;
            }
            return source;
        }

        private static byte? ParseByteEnvLocal(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (byte.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hex))
                return hex;
            if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte dec))
                return dec;
            return null;
        }

        private static int ParseWatchLimit(string name)
        {
            return ParseWatchLimit(name, 64);
        }

        private static int ParseWatchLimit(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseZ80BootIoLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_BOOT_IO_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return 2000;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 2000;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static int ParseZ80YmLimit()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80YM_LIMIT");
            if (string.IsNullOrWhiteSpace(raw))
                return int.MaxValue;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return int.MaxValue;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static bool TryGetTraceRamWriteRange(out ushort start, out ushort end)
        {
            if (!TraceZ80RamWriteRangeStart.HasValue || !TraceZ80RamWriteRangeEnd.HasValue)
            {
                start = 0;
                end = 0;
                return false;
            }
            start = TraceZ80RamWriteRangeStart.Value;
            end = TraceZ80RamWriteRangeEnd.Value;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            return true;
        }

        private bool ShouldTraceBootIo()
        {
            return TraceZ80BootIo && _bootInstrCount < TraceZ80BootIoInstrLimit;
        }

        private bool IsOpcodeFetch(ushort addr)
        {
            ushort pc = g_reg_PC;
            return addr == pc || addr == (ushort)(pc + 1) || addr == (ushort)(pc + 2) || addr == (ushort)(pc + 3);
        }

        private void TrackLastRead(ushort addr, byte value, bool banked, uint bankedAddr)
        {
            if (IsOpcodeFetch(addr))
                return;
            _lastReadAddr = addr;
            _lastReadValue = value;
            _lastReadPc = g_reg_PC;
            _lastReadWasBanked = banked;
            _lastReadM68kAddr = banked ? bankedAddr : 0;
        }

        private void RecordZ80MbxPoll(ushort addr, byte value)
        {
            if (!TraceZ80MbxPoll)
                return;
            _z80MbxPollLastAddr = addr;
            _z80MbxPollLastValue = value;
            _z80MbxPollLastPc = g_reg_PC;
            if (addr >= 0x1B80 && addr <= 0x1B8F)
                _z80MbxPollReads++;
            else if (addr >= 0x1B00 && addr <= 0x1B7F)
                _z80MbxPollWideReads++;
        }

        private void MaybeLogZ80MbxPollEdge(ushort addr, byte value)
        {
            if (!TraceZ80MbxPollEdge || _z80MbxPollEdgeRemaining <= 0)
                return;
            bool changed = !_z80MbxPollEdgeLastValid || value != _z80MbxPollEdgeLastValue;
            if (!changed && value == 0x00)
                return;
            _z80MbxPollEdgeLastValid = true;
            _z80MbxPollEdgeLastValue = value;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine($"[Z80MBX-POLL-EDGE] frame={frame} pc=0x{DebugPc:X4} addr=0x{addr:X4} val=0x{value:X2}");
            if (_z80MbxPollEdgeRemaining != int.MaxValue)
                _z80MbxPollEdgeRemaining--;
        }

        private void MaybeLogZ80MbxPollData(ushort addr, ushort ramAddr, byte value)
        {
            if (!TraceZ80MbxPollData || _z80MbxPollDataRemaining <= 0)
                return;
            if (IsOpcodeFetch(addr))
                return;
            if (ramAddr < 0x1B80 || ramAddr > 0x1B8F)
                return;
            int offset = ramAddr - 0x1B80;
            byte prev = _z80MbxPollDataLastValid ? _z80MbxPollDataLast[offset] : (byte)0x00;
            if (value == 0x00 && prev == 0x00)
                return;
            if (_z80MbxPollDataLastValid && value == prev)
                return;
            _z80MbxPollDataLast[offset] = value;
            _z80MbxPollDataLastValid = true;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine($"[Z80MBX-DATA] frame={frame} pc=0x{DebugPc:X4} addr=0x{ramAddr:X4} val=0x{value:X2}");
            if (_z80MbxPollDataRemaining != int.MaxValue)
                _z80MbxPollDataRemaining--;
        }

        private void MaybeLogZ80Flag65ReadEdge(ushort addr, ushort ramAddr, byte value)
        {
            if (!TraceZ80Flag65 || _z80Flag65ReadRemaining <= 0)
                return;
            if (ramAddr != 0x0065)
                return;
            if (IsOpcodeFetch(addr))
                return;
            byte prev = _z80Flag65LastReadValid ? _z80Flag65LastReadValue : (byte)0x00;
            if (value == 0x00 && prev == 0x00)
                return;
            if (_z80Flag65LastReadValid && value == prev)
                return;
            _z80Flag65LastReadValid = true;
            _z80Flag65LastReadValue = value;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            string dump = string.Empty;
            if (value != 0x00)
            {
                const ushort dumpStart = 0x0060;
                const int dumpLen = 0x11;
                var sb = new StringBuilder(dumpLen * 3);
                for (int i = 0; i < dumpLen; i++)
                {
                    if (i > 0)
                        sb.Append(' ');
                    sb.Append(g_ram[(ushort)((dumpStart + i) & 0x1FFF)].ToString("X2"));
                }
                dump = $" dump=0x{dumpStart:X4}:{sb}";
            }
            Console.WriteLine(
                $"[Z80RD65] frame={frame} pc=0x{DebugPc:X4} addr=0x{addr:X4} val=0x{value:X2} last=0x{prev:X2}{dump}");
            if (_z80Flag65ReadRemaining != int.MaxValue)
                _z80Flag65ReadRemaining--;
        }

        private void MaybeArmZ80PostFlagTrace(ushort addr, ushort ramAddr, byte value)
        {
            if (!TraceZ80PostFlag)
                return;
            if (ramAddr != 0x0065)
                return;
            if (IsOpcodeFetch(addr))
                return;
            byte prev = _z80PostFlagLastReadValid ? _z80PostFlagLastReadValue : (byte)0x00;
            _z80PostFlagLastReadValid = true;
            _z80PostFlagLastReadValue = value;
            if (value == 0x00 || prev != 0x00)
                return;
            if (_z80PostFlagReadRemaining > 0)
                return;
            _z80PostFlagReadRemaining = TraceZ80PostFlagLimit;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            ushort start = TraceZ80PostFlagStart;
            ushort end = TraceZ80PostFlagEnd;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            string pcdump = DumpZ80PcBytes(DebugPc, 8, 16);
            Console.WriteLine(
                $"[Z80PF-ARM] frame={frame} pc=0x{DebugPc:X4} val=0x{value:X2} range=0x{start:X4}..0x{end:X4} limit={TraceZ80PostFlagLimit} pcdump={pcdump}");
        }

        private void MaybeLogZ80PostFlagRead(ushort addr, byte value)
        {
            if (!TraceZ80PostFlag || _z80PostFlagReadRemaining <= 0)
                return;
            if (IsOpcodeFetch(addr))
                return;
            ushort start = TraceZ80PostFlagStart;
            ushort end = TraceZ80PostFlagEnd;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            if (addr < start || addr > end)
                return;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine($"[Z80RDPF] frame={frame} pc=0x{DebugPc:X4} addr=0x{addr:X4} val=0x{value:X2}");
            if (_z80PostFlagReadRemaining != int.MaxValue)
                _z80PostFlagReadRemaining--;
        }

        private void MaybeLogZ80AfterFlagRet(string tag, string op, ushort addr, byte value)
        {
            if (!TraceZ80AfterFlagRet || _z80AfterFlagRetRemaining <= 0)
                return;
            if (tag == "Z80AFTERRET-RAM")
            {
                bool isRead = op == "read";
                bool isWrite = op == "write";
                bool allowRead = isRead && (addr == 0x0065 || (addr >= 0x0FFD && addr <= 0x101F));
                bool allowWrite = isWrite && (addr == 0x1FF7 || addr == 0x1FF8);
                if (!allowRead && !allowWrite)
                    return;
            }
            else
            {
                return;
            }
            Console.WriteLine(
                $"[{tag}] pc=0x{DebugPc:X4} ix=0x{g_reg_IX:X4} iy=0x{g_reg_IY:X4} sp=0x{g_reg_SP:X4} " +
                $"op={op} addr=0x{addr:X4} val=0x{value:X2}");
            if (_z80AfterFlagRetRemaining != int.MaxValue)
                _z80AfterFlagRetRemaining--;
            if (op == "read" && addr == 0x0065 && (DebugPc == 0x0DD8 || DebugPc == 0x0DEA))
            {
                Console.WriteLine($"[Z80AFTERRET-DONE] pc=0x{DebugPc:X4} addr=0x{addr:X4} val=0x{value:X2}");
                _z80AfterFlagRetRemaining = 0;
            }
        }

        internal void FlushZ80MbxPoll(long frame)
        {
            if (!TraceZ80MbxPoll)
                return;
            if (_z80MbxPollReads == 0 && _z80MbxPollWideReads == 0)
            {
                Console.WriteLine($"[Z80MBX-POLL] frame={frame} reads=0 wide=0");
                return;
            }
            Console.WriteLine($"[Z80MBX-POLL] frame={frame} reads={_z80MbxPollReads} wide={_z80MbxPollWideReads} last=0x{_z80MbxPollLastAddr:X4} pc=0x{_z80MbxPollLastPc:X4} val=0x{_z80MbxPollLastValue:X2}");
            _z80MbxPollReads = 0;
            _z80MbxPollWideReads = 0;
        }

        internal void FlushZ80WaitLoopHist(long frame)
        {
            if (!TraceZ80WaitLoop)
                return;
            // Find top 5 polled addresses
            int[] topAddrs = new int[5];
            int[] topCounts = new int[5];
            for (int i = 0; i < 5; i++)
            {
                topAddrs[i] = -1;
                topCounts[i] = 0;
            }
            for (int addr = 0x11C0; addr <= 0x11E0; addr++)
            {
                int count = _waitLoopPollHist[addr];
                if (count == 0)
                    continue;
                // Insert into top 5
                for (int i = 0; i < 5; i++)
                {
                    if (count > topCounts[i])
                    {
                        // Shift down
                        for (int j = 4; j > i; j--)
                        {
                            topAddrs[j] = topAddrs[j - 1];
                            topCounts[j] = topCounts[j - 1];
                        }
                        topAddrs[i] = addr;
                        topCounts[i] = count;
                        break;
                    }
                }
            }
            // Log top 5
            Console.Write($"[Z80-WAIT-HIST] frame={frame} top5=");
            bool first = true;
            for (int i = 0; i < 5; i++)
            {
                if (topAddrs[i] >= 0)
                {
                    if (!first) Console.Write(",");
                    Console.Write($"0x{topAddrs[i]:X4}:{topCounts[i]}");
                    first = false;
                }
            }
            Console.WriteLine();
            // Clear histogram
            for (int addr = 0x11C0; addr <= 0x11E0; addr++)
                _waitLoopPollHist[addr] = 0;
            _waitLoopLoggedCount = 0;
        }

        private void LogBootIo(string op, ushort addr, byte value)
        {
            Console.WriteLine($"[Z80BOOTIO] instr={_bootInstrCount} pc=0x{DebugPc:X4} {op} addr=0x{addr:X4} val=0x{value:X2}");
        }

        private void LogZ80IoRead(ushort addr, byte value)
        {
            uint bankReg = g_bank_register & 0x1FFu;
            Console.WriteLine($"[Z80IO] pc=0x{DebugPc:X4} addr=0x{addr:X4} read val=0x{value:X2} bank=0x{GetBankBase():X6} reg=0x{bankReg:X3}");
        }

        private void LogZ80RamWrite(ushort ramAddr, byte value, byte oldValue, bool rangeWatch)
        {
            string lastBankInfo = _lastReadWasBanked ? $" m68k=0x{_lastReadM68kAddr:X6}" : string.Empty;
            string watchInfo = rangeWatch ? " watch=range" : string.Empty;
            Console.WriteLine(
                $"[Z80RAMWR] pc=0x{DebugPc:X4} addr=0x{ramAddr:X4} val=0x{value:X2} old=0x{oldValue:X2} " +
                $"hl=0x{g_reg_HL:X4} de=0x{g_reg_DE:X4} bc=0x{g_reg_BC:X4} sp=0x{g_reg_SP:X4} " +
                $"last=0x{_lastReadAddr:X4} lastVal=0x{_lastReadValue:X2} lastPc=0x{_lastReadPc:X4}{lastBankInfo}{watchInfo}");
        }

        private void LogZ80RamRead(ushort ramAddr, byte value, bool rangeWatch)
        {
            string watchInfo = rangeWatch ? " watch=range" : string.Empty;
            Console.WriteLine($"[Z80RAMRD] pc=0x{DebugPc:X4} addr=0x{ramAddr:X4} val=0x{value:X2} hl=0x{g_reg_HL:X4}{watchInfo}");
        }

        private void MaybeLogZ80ReadRange(ushort addr, byte value)
        {
            if (!TraceZ80ReadRangeStart.HasValue || !TraceZ80ReadRangeEnd.HasValue)
                return;
            if (_z80ReadRangeRemaining <= 0)
                return;
            ushort start = TraceZ80ReadRangeStart.Value;
            ushort end = TraceZ80ReadRangeEnd.Value;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            if (addr < start || addr > end)
                return;
            int opcode = IsOpcodeFetch(addr) ? 1 : 0;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine($"[Z80RD] frame={frame} pc=0x{DebugPc:X4} addr=0x{addr:X4} val=0x{value:X2} opcode={opcode}");
            if (_z80ReadRangeRemaining != int.MaxValue)
                _z80ReadRangeRemaining--;
        }

        private void StartZ80Fill(ushort ramAddr, uint romAddr)
        {
            _z80FillActive = true;
            _z80FillStartAddr = ramAddr;
            _z80FillLastAddr = ramAddr;
            _z80FillStartRomAddr = romAddr;
            _z80FillLastRomAddr = romAddr;
        }

        private void ExtendZ80Fill(ushort ramAddr, uint romAddr)
        {
            _z80FillLastAddr = ramAddr;
            _z80FillLastRomAddr = romAddr;
        }

        private uint SumZ80Ram(ushort start, int length)
        {
            uint sum = 0;
            for (int i = 0; i < length; i++)
                sum += g_ram[(ushort)((start + i) & 0x1FFF)];
            return sum;
        }

        private int SumRom(uint start, int length, out uint sum)
        {
            sum = 0;
            byte[]? mem = g_memory;
            if (mem == null)
                return 0;
            uint addr = start & 0x00FF_FFFF;
            if (addr >= (uint)mem.Length)
                return 0;
            int available = length;
            int maxAvailable = mem.Length - (int)addr;
            if (available > maxAvailable)
                available = maxAvailable;
            for (int i = 0; i < available; i++)
                sum += mem[addr + i];
            return available;
        }

        private string DumpZ80RamBytes(ushort start, int length)
        {
            int count = Math.Min(16, length);
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(g_ram[(ushort)((start + i) & 0x1FFF)].ToString("X2"));
            }
            return sb.ToString();
        }

        private byte ApplyForceZ80FlagBit2(ushort ramAddr, byte value)
        {
            if (!ForceZ80FlagBit2)
                return value;
            if (ramAddr == ForceZ80FlagBit2Addr ||
                (ForceZ80FlagBit2Addr2.HasValue && ramAddr == ForceZ80FlagBit2Addr2.Value))
                return (byte)(value | 0x04);
            return value;
        }

        private byte PeekZ80ByteNoSideEffect(ushort addr)
        {
            if (addr < 0x4000)
            {
                ushort ramAddr = (ushort)(addr & 0x1FFF);
                byte value = g_ram[ramAddr];
                value = MaybeOverrideFlag65Read(ramAddr, value);
                return ApplyForceZ80FlagBit2(ramAddr, value);
            }
            if (addr >= 0x8000)
            {
                uint bankBase = GetBankBase();
                uint m68kAddr = bankBase | (uint)(addr & 0x7FFF);
                byte[]? mem = g_memory;
                if (mem != null && m68kAddr < (uint)mem.Length)
                    return mem[m68kAddr];
                return 0xFF;
            }
            return 0xFF;
        }

        private string DumpZ80PcBytes(ushort pc, int before, int after)
        {
            int total = before + after + 1;
            ushort start = (ushort)(pc - before);
            var sb = new StringBuilder(total * 3);
            for (int i = 0; i < total; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                ushort addr = (ushort)(start + i);
                sb.Append(PeekZ80ByteNoSideEffect(addr).ToString("X2"));
            }
            return $"0x{start:X4}:{sb}";
        }

        private string DumpRom(uint start, int length)
        {
            byte[]? mem = g_memory;
            if (mem == null)
                return "n/a";
            uint addr = start & 0x00FF_FFFF;
            if (addr >= (uint)mem.Length)
                return "n/a";
            int count = Math.Min(16, length);
            int maxAvailable = mem.Length - (int)addr;
            if (count > maxAvailable)
                count = maxAvailable;
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(mem[addr + i].ToString("X2"));
            }
            return sb.ToString();
        }

        private void FinalizeZ80Fill(string reason)
        {
            if (!_z80FillActive)
                return;
            _z80FillActive = false;
            int length = _z80FillLastAddr - _z80FillStartAddr + 1;
            if (length <= 0)
                return;
            if (_z80RamFillLogRemaining <= 0)
                return;
            uint sumRam = SumZ80Ram(_z80FillStartAddr, length);
            uint sumRom;
            int romAvailable = SumRom(_z80FillStartRomAddr, length, out sumRom);
            string ramDump = DumpZ80RamBytes(_z80FillStartAddr, length);
            string romDump = DumpRom(_z80FillStartRomAddr, length);
            string romAvailInfo = romAvailable == length ? string.Empty : $" romAvail=0x{romAvailable:X4}";
            Console.WriteLine(
                $"[Z80FILL] ram=0x{_z80FillStartAddr:X4} len=0x{length:X4} rom=0x{_z80FillStartRomAddr:X6} " +
                $"sumRam=0x{sumRam:X8} sumRom=0x{sumRom:X8} ram16={ramDump} rom16={romDump}{romAvailInfo} reason={reason}");
            if (_z80RamFillLogRemaining != int.MaxValue)
                _z80RamFillLogRemaining--;
        }

        private void UpdateZ80FillTrace(ushort ramAddr, bool inWriteRange, ushort rangeEnd)
        {
            if (!inWriteRange)
            {
                if (_z80FillActive)
                    FinalizeZ80Fill("out");
                return;
            }
            if (!_lastReadWasBanked)
            {
                if (_z80FillActive)
                    FinalizeZ80Fill("nobank");
                return;
            }
            uint romAddr = _lastReadM68kAddr;
            if (!_z80FillActive)
            {
                StartZ80Fill(ramAddr, romAddr);
            }
            else if (ramAddr == (ushort)(_z80FillLastAddr + 1) && romAddr == _z80FillLastRomAddr + 1)
            {
                ExtendZ80Fill(ramAddr, romAddr);
            }
            else
            {
                FinalizeZ80Fill("nonseq");
                StartZ80Fill(ramAddr, romAddr);
            }
            if (ramAddr == rangeEnd)
                FinalizeZ80Fill("rangeend");
        }

        //----------------------------------------------------------------
        // read
        //----------------------------------------------------------------
        private uint GetBankBase()
        {
            return (g_bank_register & 0x1FFu) * 0x8000u;
        }

        public byte read8(uint in_address)
        {
            byte w_out = 0;
            ushort a = (ushort)(in_address & 0xFFFF);
            bool wasBanked = false;
            uint bankedAddr = 0;

            if (md_main.g_masterSystemMode)
            {
                byte result = ReadSms(a);
                LogSmsAccess("read", a, result);
                MaybeLogZ80ReadRange(a, result);
                return result;
            }

            if (a < 0x4000)
            {
                // 8 KB Z80 RAM (0x0000..0x1FFF) speglad över 0x0000..0x3FFF
                ushort ramAddr = (ushort)(a & 0x1FFF);
                w_out = g_ram[ramAddr];
                w_out = MaybeOverrideFlag65Read(ramAddr, w_out);
                w_out = ApplyForceZ80FlagBit2(ramAddr, w_out);
                if (ShouldTraceZ80Ram1800(ramAddr))
                    LogZ80Ram1800("R", ramAddr, w_out);
                bool isMbxPoll = TraceZ80MbxPoll && ramAddr >= 0x1B00 && ramAddr <= 0x1B8F;
                if (ShouldTraceBootIo())
                    LogBootIo("read", a, w_out);
                if (TraceZ80RamReadRangeStart.HasValue && TraceZ80RamReadRangeEnd.HasValue && _z80RamReadRangeRemaining > 0)
                {
                    ushort rangeStart = TraceZ80RamReadRangeStart.Value;
                    ushort rangeEnd = TraceZ80RamReadRangeEnd.Value;
                    if (rangeStart > rangeEnd)
                    {
                        ushort tmp = rangeStart;
                        rangeStart = rangeEnd;
                        rangeEnd = tmp;
                    }
                    if (ramAddr >= rangeStart && ramAddr <= rangeEnd)
                    {
                        LogZ80RamRead(ramAddr, w_out, true);
                        if (_z80RamReadRangeRemaining != int.MaxValue)
                            _z80RamReadRangeRemaining--;
                    }
                }
                if (!UseMdTracerCompat)
                {
                    if (LatchZ80MbxWideCmd && a == 0x1B1C && _mbxWideCmdLatchValid && w_out == 0x00)
                    {
                        w_out = _mbxWideCmdLatchValue;
                        _mbxWideCmdLatchValid = false;
                        if (TraceZ80Mbx)
                            Console.WriteLine($"[Z80MBXRD-WL] pc={DebugPc:X4} addr={a:X4} val={w_out:X2}");
                    }
                    if (a >= 0x1B80 && a <= 0x1B8F)
                    {
                        if (MirrorZ80Mailbox)
                            w_out = MaybeMirrorMailboxRead(a, w_out);
                        w_out = MaybeApplyMailboxShadow(a, w_out);
                        if (MbxSyncTrace.IsEnabled)
                            TrackMailboxRead(a, w_out);
                    }
                    else if (Z80WindowWide && a >= 0x1B00 && a <= 0x1B7F)
                    {
                        if (TraceZ80Mbx && w_out != 0x00 && _z80MailboxWideLogRemaining > 0)
                        {
                            _z80MailboxWideLogRemaining--;
                            Console.WriteLine($"[Z80MBXRD-W] pc={DebugPc:X4} addr={a:X4} val={w_out:X2}");
                        }
                        if (TraceZ80MbxWideReadAll && _z80MailboxWideReadAllRemaining > 0)
                        {
                            _z80MailboxWideReadAllRemaining--;
                            Console.WriteLine($"[Z80MBXRD-WA] pc={DebugPc:X4} addr={a:X4} val={w_out:X2}");
                        }
                    }
                    MbxSyncTrace.MaybeSyncOnZ80Read(a, w_out, DebugPc, g_ram);
                }
                if (ramAddr == 0x1B8F)
                {
                    byte prev = _mbx1b8fLastReadValid ? _mbx1b8fLastReadValue : (byte)0x00;
                    _mbx1b8fLastReadValue = w_out;
                    _mbx1b8fLastReadValid = true;
                    if (w_out != 0x00)
                    {
                        md_main.NotifyMbxInjectedRead(ramAddr, w_out);
                        if (prev != w_out)
                            LogMbxEdge("rdNZ", ramAddr, w_out, null, DebugPc, null);
                    }
                    MaybeLogZ80MbxPollEdge(ramAddr, w_out);
                }
                MaybeLogZ80MbxPollData(a, ramAddr, w_out);
                MaybeLogZ80Flag65ReadEdge(a, ramAddr, w_out);
                MaybeArmZ80PostFlagTrace(a, ramAddr, w_out);
                if (isMbxPoll)
                    RecordZ80MbxPoll(ramAddr, w_out);
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-RAM", "read", ramAddr, w_out);
            }
            else if (a <= 0x5FFF)
            {
                // YM2612
                w_out = UseMdTracerCompat
                    ? md_main.g_md_music.g_md_ym2612.read8(a)
                    : (a == 0x4000 || a == 0x4002
                        ? md_main.g_md_music.g_md_ym2612.ReadStatus(false)
                        : md_main.g_md_music.g_md_ym2612.read8(a));
                if (ShouldTraceBootIo())
                    LogBootIo("read", a, w_out);
                if (TraceZ80Ym && (a == 0x4000 || a == 0x4002) && _z80YmLogRemaining > 0)
                {
                    Console.WriteLine($"[Z80YM] read addr=0x{a:X4} -> 0x{w_out:X2}");
                    if (_z80YmLogRemaining != int.MaxValue)
                        _z80YmLogRemaining--;
                }
            }
            else if (a >= 0x6000 && a <= 0x7EFF)
            {
                // I/O/UB – returnera “öppet bussvärde”
                w_out = 0xFF;
                if (ShouldTraceBootIo())
                    LogBootIo("read", a, w_out);
                if (TraceZ80Io && _z80IoLogRemaining > 0)
                {
                    LogZ80IoRead(a, w_out);
                    if (_z80IoLogRemaining != int.MaxValue)
                        _z80IoLogRemaining--;
                }
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-IO", "read", a, w_out);
            }
            else if (a >= 0x7F00 && a <= 0x7FFF)
            {
                // Ej mappat (förutom PSG write på 0x7F11)
                w_out = 0xFF;
                if (ShouldTraceBootIo())
                    LogBootIo("read", a, w_out);
                if (TraceZ80Io && _z80IoLogRemaining > 0)
                {
                    LogZ80IoRead(a, w_out);
                    if (_z80IoLogRemaining != int.MaxValue)
                        _z80IoLogRemaining--;
                }
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-IO", "read", a, w_out);
            }
            else if (a >= 0x8000)
            {
                // 68k bankfönster, 0x8000..0xFFFF => 32 KB
                // Maskera alltid till 32KB offset och OR:a med bankbasen.
                bool forceB154 = ForceZ80B154Map &&
                                 (a == ForceZ80B154Z80Addr ||
                                  (ForceZ80B154Z80Addr2.HasValue && a == ForceZ80B154Z80Addr2.Value));
                if (forceB154)
                {
                    uint m68kAddr = ForceZ80B154M68kAddr;
                    wasBanked = true;
                    bankedAddr = m68kAddr;
                    if (UseMdTracerCompat)
                        w_out = md_m68k.read8(m68kAddr);
                    else if (md_main.g_md_bus != null)
                        w_out = md_main.g_md_bus.read8(m68kAddr);
                    else
                        w_out = md_m68k.read8(m68kAddr);
                    if (_forceZ80B154Remaining > 0)
                    {
                        Console.WriteLine(
                            $"[Z80B154-OVERRIDE] pc=0x{DebugPc:X4} addr=0x{a:X4} m68k=0x{m68kAddr:X6} val=0x{w_out:X2}");
                        if (_forceZ80B154Remaining != int.MaxValue)
                            _forceZ80B154Remaining--;
                    }
                }
                else
                {
                    uint bankBase = GetBankBase();
                    uint m68kAddr = bankBase | (uint)(a & 0x7FFF);
                    wasBanked = true;
                    bankedAddr = m68kAddr;
                    if (TraceZ80Addr.HasValue && a == TraceZ80Addr.Value && _z80AddrWatchRemaining > 0)
                    {
                        Console.WriteLine($"[Z80ADDR] pc=0x{DebugPc:X4} addr=0x{a:X4} read bank=0x{bankBase:X6} m68k=0x{m68kAddr:X6}");
                        if (_z80AddrWatchRemaining != int.MaxValue)
                            _z80AddrWatchRemaining--;
                    }
                    if (TraceMemWatchAddr.HasValue && m68kAddr == TraceMemWatchAddr.Value)
                        Console.WriteLine($"[Z80->68K] pc=0x{DebugPc:X4} addr=0x{m68kAddr:X6} read bank=0x{bankBase:X6}");
                    if (UseMdTracerCompat)
                        w_out = md_m68k.read8(m68kAddr);
                    else if (md_main.g_md_bus != null)
                        w_out = md_main.g_md_bus.read8(m68kAddr);
                    else
                        w_out = md_m68k.read8(m68kAddr);
                    if (TraceZ80Bank)
                        UpdateBankStat(a, w_out);
                    if (TraceZ80Bank && _z80BankReadLogRemaining > 0)
                    {
                        _z80BankReadLogRemaining--;
                        Console.WriteLine($"[Z80BANKRD] pc=0x{DebugPc:X4} z80addr=0x{a:X4} val=0x{w_out:X2} bank=0x{bankBase:X6}");
                    }
                }
            }
            else
            {
                MessageBox.Show("md_z80_memory.read8", "error");
            }

            // Track wait loop polling (0x11C3-0x11DB NOP loop area)
            if (TraceZ80WaitLoop && a >= 0x11C0 && a <= 0x11E0)
            {
                _waitLoopPollHist[a]++;
                if (_waitLoopLoggedCount < TraceZ80WaitLoopLimit)
                {
                    _waitLoopLoggedCount++;
                    long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                    Console.WriteLine($"[Z80-WAIT-READ] frame={frame} pc=0x{DebugPc:X4} addr=0x{a:X4} val=0x{w_out:X2}");
                }
            }

            TrackLastRead(a, w_out, wasBanked, bankedAddr);
            MaybeLogZ80ReadRange(a, w_out);
            MaybeLogZ80PostFlagRead(a, w_out);
            return w_out;
        }

        public ushort read16(uint in_address)
        {
            // Läs via read8 så MMIO-sidoeffekter och wrapping blir korrekt
            ushort a = (ushort)(in_address & 0xFFFF);
            byte hi = read8(a);
            byte lo = read8((ushort)(a + 1));
            return (ushort)((hi << 8) | lo);
        }

        public uint read32(uint in_address)
        {
            ushort a = (ushort)(in_address & 0xFFFF);
            byte b3 = read8(a);
            byte b2 = read8((ushort)(a + 1));
            byte b1 = read8((ushort)(a + 2));
            byte b0 = read8((ushort)(a + 3));
            return (uint)((b3 << 24) | (b2 << 16) | (b1 << 8) | b0);
        }

        //----------------------------------------------------------------
        // write
        //----------------------------------------------------------------
        public void write8(uint in_address, byte in_data)
        {
            ushort a = (ushort)(in_address & 0xFFFF);

            if (md_main.g_masterSystemMode)
            {
                LogSmsAccess("write", a, in_data);
                if (HandleSmsPortWrite(a, in_data))
                    return;
                if (a >= 0xC000)
                {
                    g_ram[(ushort)(a & 0x1FFF)] = in_data;
                    return;
                }
                if (a < 0x4000)
                    return;
            }
            if (a < 0x4000)
            {
                // 8 KB Z80 RAM (0x0000..0x1FFF) speglad över 0x0000..0x3FFF
                ushort ramAddr = (ushort)(a & 0x1FFF);
                byte oldValue = g_ram[ramAddr];
                g_ram[ramAddr] = in_data;
                if (ShouldTraceZ80Ram1800(ramAddr))
                    LogZ80Ram1800("W", ramAddr, in_data);
                if (TraceZ80Flag65 && ramAddr == 0x0065 && _z80Flag65WriteRemaining > 0)
                {
                    long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                    Console.WriteLine($"[Z80WR65] frame={frame} pc=0x{DebugPc:X4} addr=0x{a:X4} val=0x{in_data:X2} old=0x{oldValue:X2}");
                    if (_z80Flag65WriteRemaining != int.MaxValue)
                        _z80Flag65WriteRemaining--;
                }
                if (ramAddr == 0x1B8F && oldValue != in_data)
                    LogMbxEdge("chg", ramAddr, in_data, oldValue, DebugPc, null);
                if (TraceZ80RamWriteAddr.HasValue && ramAddr == TraceZ80RamWriteAddr.Value && _z80RamWriteLogRemaining > 0)
                {
                    LogZ80RamWrite(ramAddr, in_data, oldValue, false);
                    if (_z80RamWriteLogRemaining != int.MaxValue)
                        _z80RamWriteLogRemaining--;
                }
                bool hasWriteRange = TryGetTraceRamWriteRange(out ushort rangeStart, out ushort rangeEnd);
                bool inWriteRange = hasWriteRange && ramAddr >= rangeStart && ramAddr <= rangeEnd;
                if (inWriteRange && _z80RamWriteRangeRemaining > 0)
                {
                    LogZ80RamWrite(ramAddr, in_data, oldValue, true);
                    if (_z80RamWriteRangeRemaining != int.MaxValue)
                        _z80RamWriteRangeRemaining--;
                }
                if (ShouldTraceBootIo())
                    LogBootIo("write", a, in_data);
                if (hasWriteRange)
                    UpdateZ80FillTrace(ramAddr, inWriteRange, rangeEnd);
                if (!UseMdTracerCompat)
                {
                    if (a >= 0x1B80 && a <= 0x1B8F)
                    {
                        if (MirrorZ80Mailbox)
                            MaybeMirrorMailboxWriteZ80(a, in_data);
                        ClearMailboxShadowEntry(a - 0x1B80);
                        if (MbxSyncTrace.IsEnabled)
                        {
                            string dump = BuildMailboxDump();
                            Console.WriteLine($"[Z80MBXWR] pc={DebugPc:X4} addr={a:X4} val={in_data:X2} dump={dump}");
                        }
                    }
                    else if (Z80WindowWide && a >= 0x1B00 && a <= 0x1B7F)
                    {
                        if (TraceZ80MbxWideCmd && (a == 0x1B1C || a == 0x1B1D))
                            Console.WriteLine($"[Z80MBXWR-WC] pc={DebugPc:X4} addr={a:X4} val={in_data:X2}");
                        if (TraceZ80Mbx && in_data != 0x00 && _z80MailboxWideLogRemaining > 0)
                        {
                            _z80MailboxWideLogRemaining--;
                            Console.WriteLine($"[Z80MBXWR-W] pc={DebugPc:X4} addr={a:X4} val={in_data:X2}");
                        }
                    }
                }
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-RAM", "write", ramAddr, in_data);
                return;
            }
            else if (a >= 0x4000 && a <= 0x5FFF)
            {
                // YM2612
                md_main.g_md_music.g_md_ym2612.write8(a, in_data, "Z80");
                if (ShouldTraceBootIo())
                    LogBootIo("write", a, in_data);
                if (TraceYm && _ymWriteLogRemaining > 0)
                {
                    _ymWriteLogRemaining--;
                    Console.WriteLine($"[YMTRACE] Z80 pc=0x{DebugPc:X4} addr=0x{a:X4} val=0x{in_data:X2}");
                }
                if (TraceZ80Ym && _z80YmLogRemaining > 0)
                {
                    Console.WriteLine($"[Z80YM] write pc=0x{g_reg_PC:X4} addr=0x{a:X4} val=0x{in_data:X2}");
                    if (_z80YmLogRemaining != int.MaxValue)
                        _z80YmLogRemaining--;
                }
                if (a <= 0x4003)
                    MaybeLogZ80AfterFlagRet("Z80AFTERRET-YM", "write", a, in_data);
            }
            else if (a >= 0x6000 && a <= 0x60FF)
            {
                // Z80 bank register till 68k-bussen (9-bit skiftregister).
                // Värde bit0 skiftas in i bit8 varje skrivning.
                uint newBank = (g_bank_register >> 1) & 0x1FFu;
                if ((in_data & 0x01) != 0)
                    newBank |= 0x100u;
                if (ShouldTraceBootIo())
                    LogBootIo("write", a, in_data);
                if (TraceZ80Bank && _z80BankRegLogRemaining > 0)
                {
                    _z80BankRegLogRemaining--;
                    uint bankBase = (newBank & 0x1FFu) * 0x8000u;
                    Console.WriteLine($"[Z80BANKREG] pc=0x{DebugPc:X4} addr=0x{a:X4} val=0x{in_data:X2} bank=0x{bankBase:X6}");
                }
                g_bank_register = newBank;
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-IO", "write", a, in_data);
            }
            else if (a >= 0x6100 && a <= 0x7EFF)
            {
                // “nothing”
                if (ShouldTraceBootIo())
                    LogBootIo("write", a, in_data);
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-IO", "write", a, in_data);
            }
            else if (a == 0x7F11)
            {
                // SN76489 PSG
                md_psg_trace.TraceWrite("Z80", a, in_data, DebugPc);
                md_main.g_md_music.g_md_sn76489.write8(in_data);
                if (ShouldTraceBootIo())
                    LogBootIo("write", a, in_data);
                if (TraceZ80Ym && _z80YmLogRemaining > 0)
                {
                    Console.WriteLine($"[Z80YM] write pc=0x{g_reg_PC:X4} addr=0x{a:X4} val=0x{in_data:X2}");
                    if (_z80YmLogRemaining != int.MaxValue)
                        _z80YmLogRemaining--;
                }
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-PSG", "write", a, in_data);
            }
            else if (a >= 0x7F00 && a <= 0x7FFF)
            {
                // Ej mappat (förutom PSG på 0x7F11)
                if (ShouldTraceBootIo())
                    LogBootIo("write", a, in_data);
                MaybeLogZ80AfterFlagRet("Z80AFTERRET-IO", "write", a, in_data);
            }
            else if (a >= 0x8000)
            {
                // 68k bankfönster (32KB)
                bool forceB154 = ForceZ80B154Map &&
                                 (a == ForceZ80B154Z80Addr ||
                                  (ForceZ80B154Z80Addr2.HasValue && a == ForceZ80B154Z80Addr2.Value));
                if (forceB154)
                {
                    uint m68kAddr = ForceZ80B154M68kAddr;
                    if (UseMdTracerCompat)
                        md_m68k.write8(m68kAddr, in_data);
                    else if (md_main.g_md_bus != null)
                        md_main.g_md_bus.write8(m68kAddr, in_data);
                    else
                        md_m68k.write8(m68kAddr, in_data);
                    if (_forceZ80B154Remaining > 0)
                    {
                        Console.WriteLine(
                            $"[Z80B154-OVERRIDE] pc=0x{DebugPc:X4} addr=0x{a:X4} m68k=0x{m68kAddr:X6} val=0x{in_data:X2} write=1");
                        if (_forceZ80B154Remaining != int.MaxValue)
                            _forceZ80B154Remaining--;
                    }
                }
                else
                {
                    uint bankBase = GetBankBase();
                    uint m68kAddr = bankBase | (uint)(a & 0x7FFF);
                    if (TraceZ80Addr.HasValue && a == TraceZ80Addr.Value && _z80AddrWatchRemaining > 0)
                    {
                        Console.WriteLine($"[Z80ADDR] pc=0x{DebugPc:X4} addr=0x{a:X4} write bank=0x{bankBase:X6} m68k=0x{m68kAddr:X6} val=0x{in_data:X2}");
                        if (_z80AddrWatchRemaining != int.MaxValue)
                            _z80AddrWatchRemaining--;
                    }
                    if (TraceMemWatchAddr.HasValue && m68kAddr == TraceMemWatchAddr.Value)
                        Console.WriteLine($"[Z80->68K] pc=0x{DebugPc:X4} addr=0x{m68kAddr:X6} val=0x{in_data:X2} bank=0x{bankBase:X6}");
                    if (UseMdTracerCompat)
                        md_m68k.write8(m68kAddr, in_data);
                    else if (md_main.g_md_bus != null)
                        md_main.g_md_bus.write8(m68kAddr, in_data);
                    else
                        md_m68k.write8(m68kAddr, in_data);
                }
            }
            else
            {
                MessageBox.Show("md_z80_memory.write8", "error");
            }
        }

        private byte ReadSms(ushort a)
        {
            if (TryReadSmsPort(a, out byte portValue))
                return portValue;

            if (a >= 0xC000)
            {
                return g_ram[(ushort)(a & 0x1FFF)];
            }

            if (a < 0x4000)
            {
                if (md_main.g_masterSystemRomSize > (int)a)
                    return md_main.g_masterSystemRom[a];
                return 0xFF;
            }

            if (a >= 0x4000 && a <= 0xBFFF && md_main.g_masterSystemRomSize > 0)
            {
                uint romIdx = (uint)(a & 0x3FFF);
                int bankCount = Math.Max(1, (md_main.g_masterSystemRomSize + 0x3FFF) / 0x4000);
                uint bank = (uint)(_smsBankSelect % bankCount);
                uint bankOffset = bank * 0x4000u;
                uint idx = (bankOffset + romIdx) % (uint)md_main.g_masterSystemRomSize;
                return md_main.g_masterSystemRom[idx];
            }

            return 0xFF;
        }

        private void ResetTraceBudgets()
        {
            if (TraceZ80Bank)
            {
                _z80BankRegLogRemaining = 32;
                _z80BankReadLogRemaining = 32;
                _z80BankStatSecond = -1;
                _z80BankStatReadCount = 0;
                _z80BankStatLastPc = 0;
                _z80BankStatLastAddr = 0;
            }
            if (MbxSyncTrace.IsEnabled)
            {
                _z80MailboxReadLogRemaining = 256;
                for (int i = 0; i < _z80MailboxSnapshot.Length; i++)
                    _z80MailboxSnapshot[i] = 0xFF;
            }
            if (TraceZ80Mbx)
            {
                _z80MailboxWideLogRemaining = 128;
            }
            if (TraceZ80MbxWideReadAll)
            {
                _z80MailboxWideReadAllRemaining = 256;
            }
            if (TraceZ80Flag65)
            {
                _z80Flag65ReadRemaining = TraceZ80Flag65Limit;
                _z80Flag65WriteRemaining = TraceZ80Flag65Limit;
                _z80Flag65LastReadValid = false;
                _z80Flag65LastReadValue = 0x00;
            }
            if (TraceZ80DdcbBit)
            {
                _z80DdcbBitRemaining = TraceZ80DdcbBitLimit;
            }
            _z80AfterFlagRetRemaining = 0;
            _mbx1b8fLastReadValid = false;
            _mbx1b8fLastReadValue = 0x00;
            ResetMailboxShadow();
        }

        private void TrackMailboxRead(ushort addr, byte value)
        {
            if (_z80MailboxReadLogRemaining <= 0)
                return;
            ushort pc = DebugPc;
            int baseIndex = 0x1B80 & 0x1FFF;
            bool changed = false;
            for (int i = 8; i < 0x10; i++)
            {
                byte current = g_ram[(baseIndex + i) & 0x1FFF];
                if (current != _z80MailboxSnapshot[i])
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
                return;
            _z80MailboxReadLogRemaining--;
            string dump = BuildMailboxDump();
            Console.WriteLine($"[Z80MBXRD] pc={pc:X4} addr={addr:X4} val={value:X2} dump={dump}");
            for (int i = 0; i < 0x10; i++)
                _z80MailboxSnapshot[i] = g_ram[(baseIndex + i) & 0x1FFF];
        }

        private bool ShouldLogMbxEdge()
        {
            return TraceZ80Mbx || TraceZ80MbxPoll || MbxSyncTrace.IsEnabled;
        }

        private void LogMbxEdge(string reason, ushort addr, byte value, byte? oldValue, ushort? pc, uint? pc68k)
        {
            if (!ShouldLogMbxEdge())
                return;
            string dump = BuildMailboxDump();
            string mode = md_main.g_masterSystemMode ? "SMS" : "MD";
            int wide = Z80WindowWide ? 1 : 0;
            int compat = UseMdTracerCompat ? 1 : 0;
            string oldInfo = oldValue.HasValue ? $" old=0x{oldValue.Value:X2}" : string.Empty;
            string pcInfo = pc.HasValue ? $" pc=0x{pc.Value:X4}" : string.Empty;
            string pc68kInfo = pc68k.HasValue ? $" pc68k=0x{pc68k.Value:X6}" : string.Empty;
            Console.WriteLine(
                $"[MBXEDGE] reason={reason} addr=0x{addr:X4} val=0x{value:X2}{oldInfo}{pcInfo}{pc68kInfo} " +
                $"mode={mode} wide={wide} compat={compat} dump= {dump}");
        }

        private byte MaybeMirrorMailboxRead(ushort addr, byte value)
        {
            if (addr < 0x1B80 || addr > 0x1B8F)
                return value;
            int baseIndex = 0x1B80 & 0x1FFF;
            int offset = addr - 0x1B80;
            int mirrorOffset = offset < 8 ? offset + 8 : offset - 8;
            byte mirror = g_ram[(baseIndex + mirrorOffset) & 0x1FFF];
            if (offset < 8)
            {
                if (mirror != 0x00 && mirror != value)
                    return mirror;
                if (MbxSyncTrace.TryGetLast68k(out uint lastAddr, out byte lastVal))
                {
                    uint lastLow = lastAddr & 0x1FFF;
                    if (lastLow >= 0x1B88 && lastLow <= 0x1B8F)
                    {
                        int lastOffset = (int)(lastLow - 0x1B88);
                        if (lastOffset == offset)
                            return lastVal;
                    }
                }
                return value;
            }
            if (value == 0x00 && mirror != 0x00)
                return mirror;
            return value;
        }

        private void MaybeMirrorMailboxWriteZ80(ushort addr, byte value)
        {
            if (addr < 0x1B80 || addr > 0x1B8F)
                return;
            int baseIndex = 0x1B80 & 0x1FFF;
            int offset = addr - 0x1B80;
            if (offset < 8)
                return;
            int mirrorOffset = offset - 8;
            g_ram[(baseIndex + mirrorOffset) & 0x1FFF] = value;
        }

        private byte MaybeApplyMailboxShadow(ushort addr, byte value)
        {
            if (!_mbxShadowValid || value != 0x00)
                return value;
            if (addr < 0x1B80 || addr > 0x1B8F)
                return value;
            int offset = addr - 0x1B80;
            byte shadow = _mbxShadow[offset];
            return shadow == 0x00 ? value : shadow;
        }

        private void ResetMailboxShadow()
        {
            Array.Clear(_mbxShadow, 0, _mbxShadow.Length);
            _mbxShadowValid = false;
        }

        private void ClearMailboxShadowEntry(int offset)
        {
            if (!_mbxShadowValid)
                return;
            if ((uint)offset >= _mbxShadow.Length)
                return;
            _mbxShadow[offset] = 0;
            for (int i = 0; i < _mbxShadow.Length; i++)
            {
                if (_mbxShadow[i] != 0)
                    return;
            }
            _mbxShadowValid = false;
        }

        internal void RecordMailboxWriteFrom68k(uint addr, byte value)
        {
            uint low = addr & 0x1FFF;
            if (low < 0x1B80 || low > 0x1B8F)
                return;
            int offset = (int)(low - 0x1B80);
            _mbxShadow[offset] = value;
            _mbxShadowValid = true;
        }

        private string BuildMailboxDump()
        {
            int baseIndex = 0x1B80 & 0x1FFF;
            StringBuilder sb = new StringBuilder(16 * 3 - 1);
            for (int i = 0; i < 0x10; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(g_ram[(baseIndex + i) & 0x1FFF].ToString("X2"));
            }
            return sb.ToString();
        }

        internal string GetMailboxDump()
        {
            return BuildMailboxDump();
        }

        internal byte PeekMailboxByte(int index)
        {
            int baseIndex = 0x1B80 & 0x1FFF;
            return g_ram[(baseIndex + index) & 0x1FFF];
        }

        internal byte PeekZ80Ram(uint addr)
        {
            return g_ram[(ushort)(addr & 0x1FFF)];
        }

        internal void ClearZ80Ram()
        {
            if (g_ram == null || g_ram.Length == 0)
                return;
            int length = Math.Min(0x2000, g_ram.Length);
            Array.Clear(g_ram, 0, length);

            // Restore Z80 boot ROM (simulated internal boot ROM)
            // This boot ROM is always present in real Genesis hardware.
            // It provides a small jump stub that the 68K's Z80 driver upload will overwrite.
            // SP must be within Z80 RAM (0x0000-0x1FFF) for RET to work correctly.
            for (int i = 0; i < 64; i++)
                g_ram[i] = 0x00; // NOPs during reset
            g_ram[0x40] = 0xF3;      // DI
            g_ram[0x41] = 0x31;      // LD SP, nn
            g_ram[0x42] = 0x00;      // low byte of SP
            g_ram[0x43] = 0x1F;      // high byte of SP (0x1F00 - within Z80 RAM 0x0000-0x1FFF)
            g_ram[0x44] = 0xC3;      // JP nn
            g_ram[0x45] = 0x67;      // low byte of target address (0x0167 - internal boot ROM driver entry)
            g_ram[0x46] = 0x01;      // high byte of target address
        }

        internal void LatchMailboxWideCmd(byte value)
        {
            if (!LatchZ80MbxWideCmd || value == 0x00)
                return;
            _mbxWideCmdLatchValue = value;
            _mbxWideCmdLatchValid = true;
        }

        private void UpdateBankStat(ushort addr, byte val)
        {
            long nowSec = Environment.TickCount64 / 1000;
            if (_z80BankStatSecond == -1)
                _z80BankStatSecond = nowSec;
            if (nowSec != _z80BankStatSecond)
            {
                if (_z80BankStatReadCount > 0)
                {
                    Console.WriteLine(
                        $"[Z80BANKSTAT] sec={_z80BankStatSecond} rd={_z80BankStatReadCount} " +
                        $"ff={_z80BankStatReadFfCount} lastPc={_z80BankStatLastPc:X4} " +
                        $"lastAddr={_z80BankStatLastAddr:X4} bank={GetBankBase():X6}");
                }
                _z80BankStatReadCount = 0;
                _z80BankStatReadFfCount = 0;
                _z80BankStatSecond = nowSec;
            }
            _z80BankStatReadCount++;
            if (val == 0xFF)
                _z80BankStatReadFfCount++;
            _z80BankStatLastPc = DebugPc;
            _z80BankStatLastAddr = addr;
        }

        private bool TryReadSmsPort(ushort addr, out byte value)
        {
            value = 0;
            if (!md_main.g_masterSystemMode)
                return false;

            ushort port = (ushort)(addr & 0xFF);
            switch (port)
            {
                case 0xBE:
                    if (md_main.g_md_vdp != null)
                    {
                        value = md_main.g_md_vdp.read8(0xC00000);
                        SmsPortLog(port, "read", value);
                        return true;
                    }
                    break;
                case 0xBF:
                    if (md_main.g_md_vdp != null)
                    {
                        bool irqPending = md_m68k.g_interrupt_V_req;
                        byte raw = md_main.g_md_vdp.read8(0xC00004);
                        value = raw;
                        if (ForceSmsStatus7)
                        {
                            value = (byte)(raw | 0x80);
                            if (!_forceStatus7Logged)
                            {
                                _forceStatus7Logged = true;
                                Console.WriteLine("[SMS VDP] forcing status bit7 in IN 0xBF return");
                            }
                        }
                        LogSmsStatusPoll(raw, value, irqPending);
                        SmsPortLog(port, "read", value);
                        return true;
                    }
                    break;
            }

            return false;
        }

        private bool HandleSmsPortWrite(ushort addr, byte data)
        {
            if (!md_main.g_masterSystemMode)
                return false;

            ushort port = (ushort)(addr & 0xFF);
            switch (port)
            {
                case 0xBE:
                    if (!_smsFirstBeWriteLogged)
                    {
                        _smsFirstBeWriteLogged = true;
                        ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                        Console.WriteLine($"[SMS IO] first BE write val=0x{data:X2} PC=0x{pc:X4}");
                    }
                    md_main.g_md_vdp?.RecordSmsBeWrite();
                    md_main.g_md_vdp?.write8(0xC00000, data);
                    SmsPortLog(port, "write", data);
                    return true;
                case 0xBF:
                    if (!_smsFirstBfWriteLogged)
                    {
                        _smsFirstBfWriteLogged = true;
                        ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
                        Console.WriteLine($"[SMS IO] first BF write val=0x{data:X2} PC=0x{pc:X4}");
                    }
                    md_main.g_md_vdp?.RecordSmsBfWrite();
                    md_main.g_md_vdp?.write8(0xC00004, data);
                    SmsPortLog(port, "write", data);
                    return true;
                case 0x7E:
                    SetSmsBank(data);
                    g_bank_register = _smsBankSelect;
                    SmsPortLog(port, "write", data);
                    return true;
                case 0x7F:
                    md_psg_trace.TraceWrite("Z80-SMS", port, data, md_main.g_md_z80?.DebugPc ?? 0);
                    md_main.g_md_music?.g_md_sn76489.write8(data);
                    SmsPortLog(port, "write", data);
                    return true;
            }

            return false;
        }

        private void SetSmsBank(byte value)
        {
            if (md_main.g_masterSystemRomSize == 0)
            {
                _smsBankSelect = 0;
                return;
            }

            int bankCount = Math.Max(1, (md_main.g_masterSystemRomSize + 0x3FFF) / 0x4000);
            _smsBankSelect = (byte)(value % bankCount);
        }

        private static void SmsPortLog(ushort port, string action, ushort value)
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;

            switch ((port, action))
            {
                case (0xBE, "read"):
                    if (_smsPortBeReadLog >= SmsPortLogLimit) return;
                    _smsPortBeReadLog++;
                    break;
                case (0xBF, "read"):
                    if (_smsPortBfReadLog >= SmsPortLogLimit) return;
                    _smsPortBfReadLog++;
                    break;
                case (0xBE, "write"):
                    if (_smsPortBeWriteLog >= SmsPortLogLimit) return;
                    _smsPortBeWriteLog++;
                    break;
                case (0xBF, "write"):
                    if (_smsPortBfWriteLog >= SmsPortLogLimit) return;
                    _smsPortBfWriteLog++;
                    break;
                case (0x7E, "write"):
                    if (_smsPort7EWriteLog >= SmsPortLogLimit) return;
                    _smsPort7EWriteLog++;
                    break;
                case (0x7F, "write"):
                    if (_smsPort7FWriteLog >= SmsPortLogLimit) return;
                    _smsPort7FWriteLog++;
                    break;
                default:
                    return;
            }

            MdTracerCore.MdLog.WriteLine($"[md_z80 SMS port {action}] port=0x{port:X2} value=0x{value:X4}");
        }

        private static void LogSmsStatusPoll(byte rawStatus, byte finalStatus, bool irqPending)
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;

            ushort pc = md_main.g_md_z80?.DebugPc ?? 0;
            if (pc < 0x0570 || pc > 0x0580)
                return;

            md_vdp? vdp = md_main.g_md_vdp;
            if (vdp == null)
                return;

            long frame = vdp.FrameCounter;
            if (frame == _smsStatusPollFrame)
                return;

            _smsStatusPollFrame = frame;

            int bit7 = (finalStatus & 0x80) != 0 ? 1 : 0;
            int irq = irqPending ? 1 : 0;
            int line = vdp.g_scanline;

            MdTracerCore.MdLog.WriteLine(
                $"[SMS WAIT] PC=0x{pc:X4} raw=0x{rawStatus:X2} final=0x{finalStatus:X2} bit7={bit7} irq={irq} line={line} frame={frame}");
        }

        private void LogSmsAccess(string op, ushort addr, byte value)
        {
            if (!md_main.g_masterSystemMode)
                return;

            if (op == "read" && !MdTracerCore.MdLog.TraceZ80InstructionLogging)
                return;

            if (_smsLogCount >= SmsLogLimit)
                return;

            _smsLogCount++;
            MdTracerCore.MdLog.WriteLine($"[md_z80 SMS {op}] addr=0x{addr:X4} val=0x{value:X2} bank=0x{g_bank_register:X6}");
        }

        public void write16(uint in_address, ushort in_data)
        {
            // Skriv via write8 så MMIO hanteras korrekt och wrapping funkar
            ushort a = (ushort)(in_address & 0xFFFF);
            write8(a,     (byte)((in_data >> 8) & 0xFF));
            write8((ushort)(a + 1), (byte)(in_data & 0xFF));
        }

        public void write32(uint in_address, uint in_data)
        {
            ushort a = (ushort)(in_address & 0xFFFF);
            write8(a,                     (byte)((in_data >> 24) & 0xFF));
            write8((ushort)(a + 1),       (byte)((in_data >> 16) & 0xFF));
            write8((ushort)(a + 2),       (byte)((in_data >> 8)  & 0xFF));
            write8((ushort)(a + 3),       (byte)( in_data        & 0xFF));
        }

        private void ResetZ80Ram1800Trace()
        {
            if (!TraceZ80Ram1800)
            {
                _z80Ram1800TraceStartFrame = -1;
                _z80Ram1800TraceEndFrame = -1;
                return;
            }
            long frame = md_main.g_md_vdp?.FrameCounter ?? 0;
            if (frame < 0)
                frame = 0;
            _z80Ram1800TraceStartFrame = frame;
            _z80Ram1800TraceEndFrame = frame + TraceZ80Ram1800Frames;
        }

        private bool ShouldTraceZ80Ram1800(ushort ramAddr)
        {
            if (!TraceZ80Ram1800)
                return false;
            if (ramAddr < 0x1800 || ramAddr > 0x1FFF)
                return false;
            long frame = md_main.g_md_vdp?.FrameCounter ?? 0;
            return frame >= _z80Ram1800TraceStartFrame && frame <= _z80Ram1800TraceEndFrame;
        }

        private void LogZ80Ram1800(string op, ushort addr, byte value)
        {
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine($"[Z80RAM18] {op} frame={frame} pc=0x{DebugPc:X4} addr=0x{addr:X4} val=0x{value:X2}");
        }
    }
}
