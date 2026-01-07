using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // Bus arbiter : chips:315-5308 (headless version, no UI)
    //----------------------------------------------------------------
    internal class md_bus
    {
        private enum SramAccessMode
        {
            ByteOdd,
            ByteEven,
            Word
        }

        // File-based SRAM logger for UI mode
        private static StreamWriter? _sramLog;
        private static readonly object _sramLogLock = new();

        private static void SramLog(string msg)
        {
            try
            {
                lock (_sramLogLock)
                {
                    if (_sramLog == null)
                    {
                        string? path = Environment.GetEnvironmentVariable("EUTHERDRIVE_SRAM_LOG");
                        if (string.IsNullOrEmpty(path))
                            path = "/tmp/eutherdrive_sram.log";
                        _sramLog = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
                    }
                    _sramLog.WriteLine(msg);
                }
            }
            catch { }
        }
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
        private int _z80BusReqLogRemaining = 64;
        private int _z80OddReadLogRemaining = 32;
        private int _z80RegReadLogRemaining = 32;
        private int _z80RegWriteLogRemaining = 32;
        private int _z80WinLogRemaining = TraceZ80WinLimit;
        private int _z80WinRangeLogRemaining = TraceZ80WinRangeLimit;
        private int _z80WinDumpRemaining = TraceZ80WinDumpLimit;
        private int _z80BankRegLogRemaining = 16;
        private int _mbx68kLogRemaining = 128;
        private int _mbx68kEdgeRemaining = 32;
        private int _mbx68kReadLogRemaining = 64;
        private int _mbxSrcDumpRemaining = 0;
        private int _z80Flag65WinRemaining = TraceZ80Flag65WinLimit;
        private int _z80Flag65LatchRemaining = Z80Flag65LatchLimit;
        private bool _z80Flag65MirrorLogged;
        private int _mbxRangeMirrorRemaining = MirrorMbxRangeToZ80FlagLimit;
        private int _mbxWideRangeMirrorRemaining = MirrorMbxWideRangeToZ80FlagLimit;
        private bool _mbxLoopInit;
        private uint _mbxLoopA5;
        private uint _mbxLoopA6;
        private int _z80WinReadBlockLogRemaining = 64;
        private readonly byte[] _mbx68kLast = new byte[0x80];
        private int _mbx68kStatWrites;
        private int _mbx68kStatWideWrites;
        private uint _mbx68kStatLastAddr;
        private uint _mbx68kStatLastPc;
        private byte _mbx68kStatLastValue;
        private int _z80WinStatWrites;
        private int _z80WinStatBlocked;
        private uint _z80WinStatLastAddr;
        private uint _z80WinStatLastPc;
        private uint _z80WinStatLastValue;
        private int _z80WinStatLastSize;
        private readonly int[] _z80WinHist = new int[256];
        private int _z80WinHistTotal;
        private bool _z80SafeBootActive;
        private bool _z80SafeBootUploadActive;
        private bool _z80SafeBootSawUpload;
        private bool _z80SafeBootBusReqGrantedLogged;
        private long _z80SafeBootStartFrame;
        private long _z80SafeBootLastUploadFrame = -1;
        private long _z80SafeBootResetReleaseFrame = -1;
        private byte[]? _sram;
        private uint _sramStart;
        private uint _sramEnd;
        private bool _sramLock;
        private bool _sramDirty;
        private string? _sramPath;
        private bool _sramLoaded;
        private bool _sramNoPathLogged;
        private SramAccessMode _sramAccess = SramAccessMode.Word;
        private int _z80ResetOnBusReqRemaining = ResetZ80OnBusReqReleaseLimit;
        private int _z80ForcePcOnUploadRemaining = ForceZ80PcOnUploadLimit;
        private bool _z80Win68kLogged;
        private int _z80SafeBootWriteCount;
        private bool _z80WinWarned;
        private bool _suppressZ80WinRangeByteLog;
        private bool _suppressMbxByteLog;
        private int _ymWriteLogRemaining = 64;
        private static readonly bool TraceZ80Win =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80RegDecode =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80REG_DECODE"), "1", StringComparison.Ordinal);
        private static readonly bool ResetZ80OnBusReqRelease =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_RESET_ON_BUSREQ_RELEASE"), "1", StringComparison.Ordinal);
        private static readonly int ResetZ80OnBusReqReleaseLimit =
            ParseTraceLimit("EUTHERDRIVE_Z80_RESET_ON_BUSREQ_RELEASE_LIMIT", 1);
        private static readonly bool Z80SafeBootEnabled =
            ReadEnvDefaultOn("EUTHERDRIVE_Z80_SAFE_BOOT");
        private static readonly int Z80SafeBootDelayFrames =
            ParseSafeBootDelayFrames("EUTHERDRIVE_Z80_SAFE_BOOT_DELAY", 1);
        private const int Z80SafeBootBusReqTimeoutFrames = 2;
        private const int Z80SafeBootUploadQuietFrames = 1;
        private static readonly bool Z80ResetAssertOnBoot =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_RESET_ASSERT_ON_BOOT"), "1", StringComparison.Ordinal);
        private static readonly bool Z80ResetAssertLog =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_RESET_ASSERT_LOG"), "1", StringComparison.Ordinal);
        private static readonly bool ForceZ80PcOnUpload =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_FORCE_PC_ON_UPLOAD"), "1", StringComparison.Ordinal);
        private static readonly ushort ForceZ80PcOnUploadStart =
            ParseZ80Addr("EUTHERDRIVE_Z80_FORCE_PC_ON_UPLOAD_START") ?? 0x0D00;
        private static readonly ushort ForceZ80PcOnUploadEnd =
            ParseZ80Addr("EUTHERDRIVE_Z80_FORCE_PC_ON_UPLOAD_END") ?? 0x0E50;
        private static readonly ushort ForceZ80PcOnUploadTarget =
            ParseZ80Addr("EUTHERDRIVE_Z80_FORCE_PC_ON_UPLOAD_TARGET") ?? 0x0D00;
        private static readonly int ForceZ80PcOnUploadLimit =
            ParseTraceLimit("EUTHERDRIVE_Z80_FORCE_PC_ON_UPLOAD_LIMIT", 4);
        private static readonly bool TraceZ80Flag65Win =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_0065_WIN"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80Flag65WinLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80_0065_WIN_LIMIT", 64);
        private static readonly bool Z80Flag65Latch =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80FLAG65_LATCH"), "1", StringComparison.Ordinal);
        private static readonly int Z80Flag65LatchLimit =
            ParseTraceLimit("EUTHERDRIVE_Z80FLAG65_LATCH_LIMIT", 32);
        private static readonly bool TraceZ80WinRegs =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN_REGS"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80WinLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80WIN_LIMIT", 64);
        private static readonly ushort? TraceZ80WinRangeStart =
            ParseZ80WinRangeOffset("EUTHERDRIVE_TRACE_Z80WIN_RANGE_START");
        private static readonly ushort? TraceZ80WinRangeEnd =
            ParseZ80WinRangeOffset("EUTHERDRIVE_TRACE_Z80WIN_RANGE_END");
        private static readonly int TraceZ80WinRangeLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80WIN_RANGE_LIMIT", 256);
        private static readonly bool TraceZ80WinDump =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN_DUMP"), "1", StringComparison.Ordinal);
        private static readonly ushort? TraceZ80WinDumpStart =
            ParseZ80WinRangeOffset("EUTHERDRIVE_TRACE_Z80WIN_DUMP_START");
        private static readonly ushort? TraceZ80WinDumpEnd =
            ParseZ80WinRangeOffset("EUTHERDRIVE_TRACE_Z80WIN_DUMP_END");
        private static readonly bool TraceZ80WinDumpOnEnd =
            ReadEnvDefaultOn("EUTHERDRIVE_TRACE_Z80WIN_DUMP_ON_END");
        private static readonly int TraceZ80WinDumpLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80WIN_DUMP_LIMIT", 1);
        private static readonly bool TraceZ80WinHist =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN_HIST"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80WinHistLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80WIN_HIST_LIMIT", 8);
        private static readonly int TraceZ80WinHistMin =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80WIN_HIST_MIN", 1);
        private static readonly bool TraceZ80WinStat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WINSTAT"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Mbx =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80MBX"), "1", StringComparison.Ordinal);
        private static readonly bool TraceMbxSrc =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBX_SRC"), "1", StringComparison.Ordinal);
        private static readonly bool TraceMbxSrcDump =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBX_SRC_DUMP"), "1", StringComparison.Ordinal);
        private static readonly int TraceMbxSrcDumpLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_MBX_SRC_DUMP_LIMIT", 16);
        private static readonly bool TraceMbx68kEdge =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBX68K_EDGE"), "1", StringComparison.Ordinal);
        private static readonly int TraceMbx68kEdgeLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_MBX68K_EDGE_LIMIT", 32);
        private static readonly bool MirrorMbxToZ80Flag =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG"), "1", StringComparison.Ordinal);
        private static readonly byte? MirrorMbxToZ80FlagValue =
            ParseByteEnv("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_VALUE");
        private static readonly ushort Z80FlagMirrorAddr =
            ParseZ80Addr("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_ADDR") ?? 0x0065;
        private static readonly ushort? Z80FlagMirrorAddr2 =
            ParseZ80Addr("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_ADDR2");
        private static readonly bool MirrorMbxRangeToZ80Flag =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_RANGE"), "1", StringComparison.Ordinal);
        private static readonly ushort Z80FlagRangeMirrorAddr =
            ParseZ80Addr("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_RANGE_ADDR") ?? 0x0060;
        private static readonly int MirrorMbxRangeToZ80FlagLimit =
            ParseTraceLimit("EUTHERDRIVE_MIRROR_MBX_TO_Z80FLAG_RANGE_LIMIT", 32);
        private static readonly bool MirrorMbxWideRangeToZ80Flag =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE"), "1", StringComparison.Ordinal);
        private static readonly ushort Z80FlagWideRangeMirrorStart =
            ParseZ80Addr("EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_START") ?? 0x1B20;
        private static readonly ushort Z80FlagWideRangeMirrorEnd =
            ParseZ80Addr("EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_END") ?? 0x1B2F;
        private static readonly ushort Z80FlagWideRangeMirrorAddr =
            ParseZ80Addr("EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_ADDR") ?? 0x0060;
        private static readonly int MirrorMbxWideRangeToZ80FlagLimit =
            ParseTraceLimit("EUTHERDRIVE_MIRROR_MBX_WIDE_TO_Z80FLAG_RANGE_LIMIT", 32);
        private static readonly ushort Z80FlagLatchAddr =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG65_LATCH_ADDR") ?? 0x0065;
        private static readonly ushort? Z80FlagLatchAddr2 =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG65_LATCH_ADDR2");
        private static readonly bool ForceZ80FlagBit2 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80FLAG_FORCE_BIT2"), "1", StringComparison.Ordinal);
        private static readonly ushort? ForceZ80FlagBit2Addr =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG_FORCE_BIT2_ADDR");
        private static ushort ForceZ80FlagBit2Target =>
            ForceZ80FlagBit2Addr ?? 0x0066;
        private static bool ShouldForceZ80FlagBit2 =>
            ForceZ80FlagBit2 || ForceZ80FlagBit2Addr.HasValue;
        private static readonly bool TraceMbx68kStat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MBX68KSTAT"), "1", StringComparison.Ordinal);
        private static bool MapZ80OddReadToNext => ReadEnvDefaultOn("EUTHERDRIVE_Z80_ODD_READ_TO_NEXT");
        private static bool TraceZ80Sig => MdLog.TraceZ80Sig;
        private static bool MirrorZ80Mailbox => ReadEnvDefaultOn("EUTHERDRIVE_MBX_MIRROR");
        private static readonly bool UseMdTracerCompat =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_MDTRACER_COMPAT"), "1", StringComparison.Ordinal);
        private static bool _ymEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "1", StringComparison.Ordinal);
        private static readonly bool TraceYm =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_YM"), "1", StringComparison.Ordinal);
        private static readonly bool Z80WindowWide =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_WINDOW_WIDE"), "1", StringComparison.Ordinal);
        private static readonly bool AllowZ80MailboxWide =
            ReadEnvDefaultOn("EUTHERDRIVE_Z80_MBX_WIDE");
        private static readonly bool IgnoreZ80BusReq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_IGNORE_BUSREQ"), "1", StringComparison.Ordinal);
        private static readonly bool DirectZ80WindowWords =
            ReadEnvDefaultOn("EUTHERDRIVE_Z80_DIRECT_WORDS");
        private static readonly bool Z80UdsOnly =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_UDS_ONLY"), "1", StringComparison.Ordinal);
        private static readonly bool MirrorZ80WindowReads =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_WINDOW_MIRROR_READS"), "1", StringComparison.Ordinal);
        private static readonly bool MirrorZ80MailboxWideCmd =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_MBX_WIDE_CMD_MIRROR"), "1", StringComparison.Ordinal);
        private static bool IsZ80BusReq(uint addr) => (addr & 0xFFFFFE) == 0xA11100;
        private static bool IsZ80Reset(uint addr) => (addr & 0xFFFFFE) == 0xA11200;
        private static bool IsZ80Mailbox(uint addr)
        {
            uint low = addr & 0x1FFF;
            return low >= 0x1B80 && low <= 0x1BFF;
        }
        private static bool IsZ80MailboxWide(uint addr)
        {
            if (!AllowZ80MailboxWide)
                return false;
            uint low = addr & 0x1FFF;
            return low >= 0x1B00 && low <= 0x1B7F;
        }
        private static bool IsZ80MailboxWriteRange(uint addr)
        {
            return IsZ80Mailbox(addr) || IsZ80MailboxWide(addr);
        }
        private static bool IsZ80MailboxAccess(uint addr)
        {
            uint low = addr & 0x1FFF;
            if (low >= 0x1B80 && low <= 0x1BFF)
                return true;
            if (!AllowZ80MailboxWide)
                return false;
            return low >= 0x1B00 && low <= 0x1B7F;
        }
        private static bool IsZ80BankReg(uint addr) => (addr & 0xFFFFFE) == 0xA06000;
        private bool CanAccessZ80BusRange(uint addr, int size)
        {
            if (IgnoreZ80BusReq)
                return true;
            if (_z80BusGranted || _z80Reset)
                return true;
            for (int i = 0; i < size; i++)
            {
                uint target = addr + (uint)i;
                if (IsZ80MailboxAccess(target) || IsZ80BankReg(target))
                    return true;
            }
            return false;
        }

        private static int ParseTraceLimit(string name, int fallback)
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

        private static int ParseSafeBootDelayFrames(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return fallback;
            return value < 0 ? fallback : value;
        }

        private static ushort? ParseZ80WinRangeOffset(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);
            if (ushort.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
                return value;
            if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
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
            if (ushort.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort hex))
                return hex;
            if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort dec))
                return dec;
            return null;
        }

        private static byte? ParseByteEnv(string name)
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

        private static bool ReadEnvDefaultOn(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
                return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static uint NormalizeM68kAddr(uint addr)
        {
            addr &= 0x00FF_FFFF;
            if (addr >= 0x00E0_0000)
                addr = (addr & 0x0000_FFFF) | 0x00FF_0000;
            return addr;
        }

        private static bool IsSramLockReg(uint addr) => (addr & 0x00FF_FFFF) == 0x00A1_30F1;

        private bool IsSramConfigured => _sram != null && _sramEnd >= _sramStart;

        private bool IsSramAddress(uint addr)
        {
            if (!IsSramConfigured)
                return false;
            return addr >= _sramStart && addr <= _sramEnd;
        }

        private byte ReadSramByte(uint addr)
        {
            if (!IsSramConfigured)
            {
                Console.WriteLine($"[SRAM-READ] not configured addr=0x{addr:X6}");
                return 0xFF;
            }
            if (!IsSramAddress(addr))
            {
                Console.WriteLine($"[SRAM-READ] out of range addr=0x{addr:X6}");
                return 0xFF;
            }
            if (!IsSramByteAddress(addr))
            {
                Console.WriteLine($"[SRAM-READ] invalid byte addr=0x{addr:X6}");
                return 0xFF;
            }
            int index = GetSramIndex(addr);
            if (_sram == null || index < 0 || index >= _sram.Length)
            {
                Console.WriteLine($"[SRAM-READ] invalid index addr=0x{addr:X6} index={index}");
                return 0xFF;
            }
            byte val = _sram[index];
            if (MdLog.Enabled)
                Console.WriteLine($"[SRAM-READ] addr=0x{addr:X6} idx=0x{index:X} val=0x{val:X2}");
            return val;
        }

        private void WriteSramByte(uint addr, byte value)
        {
            if (!IsSramConfigured)
            {
                Console.WriteLine($"[SRAM-WRITE] not configured addr=0x{addr:X6} val=0x{value:X2}");
                return;
            }
            if (!IsSramAddress(addr))
            {
                Console.WriteLine($"[SRAM-WRITE] out of range addr=0x{addr:X6} val=0x{value:X2}");
                return;
            }
            if (!IsSramByteAddress(addr))
            {
                Console.WriteLine($"[SRAM-WRITE] invalid byte addr=0x{addr:X6} val=0x{value:X2}");
                return;
            }
            int index = GetSramIndex(addr);
            if (_sram == null || index < 0 || index >= _sram.Length)
            {
                Console.WriteLine($"[SRAM-WRITE] invalid index addr=0x{addr:X6} val=0x{value:X2}");
                return;
            }
            if (_sram[index] == value)
                return;
            _sram[index] = value;
            _sramDirty = true;
            if (MdLog.Enabled)
                Console.WriteLine($"[SRAM-WRITE] addr=0x{addr:X6} idx=0x{index:X} val=0x{value:X2}");
        }

        private bool IsSramByteAddress(uint addr)
        {
            return _sramAccess switch
            {
                SramAccessMode.Word => true,
                SramAccessMode.ByteEven => (addr & 1) == 0,
                _ => (addr & 1) != 0
            };
        }

        private int GetSramIndex(uint addr)
        {
            if (_sramAccess == SramAccessMode.Word)
                return (int)(addr - _sramStart);
            return (int)((addr - _sramStart) >> 1);
        }

        private void EnsureSramInitialized()
        {
            if (_sramLoaded)
                return;
            var cart = md_main.g_md_cartridge;
            if (cart == null)
                return;

            if (!TryGetSramRange(cart, out uint start, out uint end))
                return;

            _sramAccess = ResolveSramAccess(cart);
            int shift = _sramAccess == SramAccessMode.Word ? 0 : 1;
            int size = (int)(((end - start) >> shift) + 1);
            if (size <= 0 || size > 0x200000)
            {
                Console.WriteLine($"[SRAM] range ignored: start=0x{start:X6} end=0x{end:X6} size=0x{size:X}");
                return;
            }

            string? path = BuildSramPath(cart);
            bool needsAlloc = _sram == null || _sram.Length != size || !string.Equals(_sramPath, path, StringComparison.Ordinal);
            if (needsAlloc)
            {
                _sram = new byte[size];
                for (int i = 0; i < _sram.Length; i++)
                    _sram[i] = 0xFF;
            }

            _sramStart = start;
            _sramEnd = end;
            _sramPath = path;
            _sramLoaded = true;
            _sramDirty = false;
            _sramNoPathLogged = false;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    int copy = Math.Min(data.Length, _sram.Length);
                    Buffer.BlockCopy(data, 0, _sram, 0, copy);
                    Console.WriteLine($"[SRAM] loaded '{path}' bytes=0x{copy:X}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SRAM] load failed '{path}': {ex.Message}");
                }
            }
        }

        private void SaveSramIfNeeded(string reason)
        {
            if (!_sramDirty || _sram == null)
                return;
            if (string.IsNullOrWhiteSpace(_sramPath))
            {
                if (!_sramNoPathLogged)
                {
                    Console.WriteLine($"[SRAM] save skipped ({reason}): no path");
                    _sramNoPathLogged = true;
                }
                return;
            }
            try
            {
                File.WriteAllBytes(_sramPath, _sram);
                _sramDirty = false;
                SramLog($"[SRAM] saved '{_sramPath}' reason={reason}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SRAM] save failed '{_sramPath}' reason={reason}: {ex.Message}");
            }
        }

        private static string? BuildSramPath(md_cartridge cart)
        {
            if (string.IsNullOrWhiteSpace(cart.g_file_path))
                return null;
            try
            {
                return Path.ChangeExtension(cart.g_file_path, ".srm");
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetSramRange(md_cartridge cart, out uint start, out uint end)
        {
            start = 0;
            end = 0;

            // Check for manual SRAM override via environment variable
            // Format: EUTHERDRIVE_SRAM=START-END (hex), e.g., 0x200000-0x3FFFFF
            string? sramEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_SRAM");
            if (!string.IsNullOrEmpty(sramEnv))
            {
                if (TryParseSramRange(sramEnv, out start, out end))
                {
                    SramLog($"[SRAM] manual config: start=0x{start:X6} end=0x{end:X6}");
                    return true;
                }
            }

            bool hasExtraRange = cart.g_extra_memory_end >= cart.g_extra_memory_start &&
                                 cart.g_extra_memory_end != 0 &&
                                 cart.g_extra_memory_start >= 0x200000 &&
                                 cart.g_extra_memory_end <= 0x3FFFFF;
            bool hasRaSig = cart.g_extra_memory_ra;

            if (hasExtraRange && cart.g_extra_memory_is_sram)
            {
                start = cart.g_extra_memory_start;
                end = cart.g_extra_memory_end;
            }
            else if (hasExtraRange && !hasRaSig)
            {
                start = cart.g_extra_memory_start;
                end = cart.g_extra_memory_end;
            }
            else
            {
                // No SRAM info in header - use default range for games that need it
                start = 0x200001;
                end = 0x20FFFF;
                return true;
            }

            if (end < start)
                return false;
            if (start < 0x200000 || end > 0x3FFFFF)
                return false;

            return true;
        }

        private static bool TryParseSramRange(string s, out uint start, out uint end)
        {
            start = 0;
            end = 0;
            try
            {
                // Support formats: "0x200000-0x3FFFFF" or "200000-3FFFFF"
                string[] parts = s.Split('-');
                if (parts.Length != 2)
                    return false;
                start = Convert.ToUInt32(parts[0].Trim(), 16);
                end = Convert.ToUInt32(parts[1].Trim(), 16);
                if (end < start)
                    return false;
                if (start < 0x200000 || end > 0x3FFFFF)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static SramAccessMode ResolveSramAccess(md_cartridge cart)
        {
            // Check for manual access mode override
            string? accessEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_SRAM_ACCESS");
            if (!string.IsNullOrEmpty(accessEnv))
            {
                return accessEnv.ToLowerInvariant() switch
                {
                    "word" => SramAccessMode.Word,
                    "byte-even" => SramAccessMode.ByteEven,
                    "byte-odd" => SramAccessMode.ByteOdd,
                    _ => SramAccessMode.Word
                };
            }

            if (cart.g_extra_memory_is_sram)
            {
                return cart.g_extra_memory_access.ToLowerInvariant() switch
                {
                    "word" => SramAccessMode.Word,
                    "byte-even" => SramAccessMode.ByteEven,
                    _ => SramAccessMode.Word
                };
            }
            return SramAccessMode.Word;
        }

        private void SetSramLock(bool enabled, string reason)
        {
            bool prev = _sramLock;
            if (enabled && !_sramLoaded)
                EnsureSramInitialized();
            _sramLock = enabled;
            SramLog($"[SRAM-LOCK] {reason}: enabled={enabled} prev={prev} loaded={_sramLoaded} configured={IsSramConfigured}");
            if (prev && !enabled)
                SaveSramIfNeeded(reason);
        }

        public void Reset()
        {
            SaveSramIfNeeded("reset");
            _z80BusGranted = false;
            _z80ForceGrant = false;
            _z80Reset = Z80ResetAssertOnBoot;
            _sramLock = false;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
            if (Z80ResetAssertOnBoot && Z80ResetAssertLog)
            {
                Console.WriteLine(
                    $"[Z80RESET-BOOT] asserted=1 busReq={(_z80BusGranted ? 1 : 0)} reset={(_z80Reset ? 1 : 0)}");
            }
            _z80RegReadLogRemaining = 32;
            _z80RegWriteLogRemaining = 32;
            _z80WinLogRemaining = TraceZ80WinLimit;
            _z80WinRangeLogRemaining = TraceZ80WinRangeLimit;
            _z80WinDumpRemaining = TraceZ80WinDumpLimit;
            _z80BankRegLogRemaining = 16;
            _z80WinWarned = false;
            _z80BusReqWriteCount = 0;
            _z80BusReqToggleCount = 0;
            _z80ResetWriteCount = 0;
            _z80ResetToggleCount = 0;
            _z80BusAckLogState = -1;
            _z80BusAckLogState8 = -1;
            _z80BusReqLogRemaining = 64;
            _z80OddReadLogRemaining = 32;
            _mbx68kLogRemaining = 128;
            _mbx68kReadLogRemaining = 64;
            _mbx68kEdgeRemaining = TraceMbx68kEdgeLimit;
            _mbxSrcDumpRemaining = TraceMbxSrcDumpLimit;
            _z80Flag65WinRemaining = TraceZ80Flag65WinLimit;
            _z80Flag65LatchRemaining = Z80Flag65LatchLimit;
            _z80Flag65MirrorLogged = false;
            _z80WinReadBlockLogRemaining = 64;
            _suppressMbxByteLog = false;
            _mbxLoopInit = false;
            _mbxLoopA5 = 0;
            _mbxLoopA6 = 0;
            _mbx68kStatWrites = 0;
            _mbx68kStatWideWrites = 0;
            _z80WinStatWrites = 0;
            _z80WinStatBlocked = 0;
            _z80WinStatLastAddr = 0;
            _z80WinStatLastPc = 0;
            _z80WinStatLastValue = 0;
            _z80WinStatLastSize = 0;
            for (int i = 0; i < _mbx68kLast.Length; i++)
                _mbx68kLast[i] = 0xFF;
            _z80Win68kLogged = false;
            _z80SafeBootActive = false;
            _z80SafeBootUploadActive = false;
            _z80SafeBootSawUpload = false;
            _z80SafeBootBusReqGrantedLogged = false;
            _z80SafeBootStartFrame = 0;
            _z80SafeBootLastUploadFrame = -1;
            _z80SafeBootResetReleaseFrame = -1;

            if (Z80SafeBootEnabled)
                StartZ80SafeBoot();

            EnsureSramInitialized();
        }

        internal void TickZ80SafeBoot(long frame)
        {
            if (!Z80SafeBootEnabled || !_z80SafeBootActive)
                return;
            long now = frame >= 0 ? frame : SafeBootFrameNow();
            if (!_z80SafeBootBusReqGrantedLogged)
            {
                if (_z80BusGranted)
                {
                    _z80SafeBootBusReqGrantedLogged = true;
                    Console.WriteLine($"[Z80SAFE] busreq granted frame={now}");
                }
                else if (now - _z80SafeBootStartFrame >= Z80SafeBootBusReqTimeoutFrames)
                {
                    Console.WriteLine($"[Z80SAFE] busreq grant timeout frame={now} -> fallback");
                    AbortZ80SafeBoot();
                    return;
                }
            }

            if (_z80SafeBootUploadActive)
            {
                // Only release reset after seeing actual uploads AND quiet period
                // DON'T auto-release on timeout - let 68K control timing
                if (_z80SafeBootSawUpload)
                {
                    if (now - _z80SafeBootLastUploadFrame >= Z80SafeBootUploadQuietFrames)
                    {
                        ReleaseZ80SafeBootReset(now);
                    }
                }
                else if (now - _z80SafeBootStartFrame >= 100)
                {
                    // Warn if no uploads for 100 frames - likely game does its own Z80 reset sequence
                    if ((now - _z80SafeBootStartFrame) % 100 == 0)
                    {
                        Console.WriteLine($"[Z80SAFE-WARN] frame={now} no uploads yet, Z80 held in reset");
                    }
                }
                return;
            }

            if (_z80SafeBootResetReleaseFrame >= 0 &&
                now - _z80SafeBootResetReleaseFrame >= Z80SafeBootDelayFrames)
            {
                ReleaseZ80SafeBootBusReq(now);
            }
        }

        internal void FlushSram(string reason)
        {
            SaveSramIfNeeded(reason);
        }

        private void StartZ80SafeBoot()
        {
            if (md_main.g_md_z80 == null)
                return;
            long frame = SafeBootFrameNow();
            _z80SafeBootActive = true;
            _z80SafeBootUploadActive = true;
            _z80SafeBootSawUpload = false;
            _z80SafeBootBusReqGrantedLogged = false;
            _z80SafeBootStartFrame = frame;
            _z80SafeBootLastUploadFrame = frame;
            _z80SafeBootResetReleaseFrame = -1;
            _z80SafeBootWriteCount = 0;

            _z80BusGranted = true;
            md_main.g_md_z80.g_active = false;
            Console.WriteLine($"[Z80SAFE] busreq asserted frame={frame}");
            if (_z80BusGranted)
            {
                _z80SafeBootBusReqGrantedLogged = true;
                Console.WriteLine($"[Z80SAFE] busreq granted frame={frame}");
            }

            _z80Reset = true;
            md_main.BeginZ80ResetCycle();
            md_main.g_md_z80.reset();
            md_main.g_md_z80.g_active = false;
            Console.WriteLine($"[Z80SAFE] reset asserted frame={frame}");

            md_main.g_md_z80.ClearZ80Ram();
            Console.WriteLine($"[Z80SAFE] ram cleared frame={frame}");
        }

        private void AbortZ80SafeBoot()
        {
            _z80SafeBootActive = false;
            _z80SafeBootUploadActive = false;
            _z80SafeBootSawUpload = false;
            _z80SafeBootBusReqGrantedLogged = false;
            _z80SafeBootResetReleaseFrame = -1;
            _z80BusGranted = false;
            _z80Reset = false;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
        }

        private void ReleaseZ80SafeBootReset(long frame)
        {
            _z80SafeBootUploadActive = false;
            _z80SafeBootResetReleaseFrame = frame;
            if (_z80Reset)
            {
                _z80Reset = false;
                if (md_main.g_md_z80 != null)
                {
                    // Sync Z80 and reinitialize FM when reset is released (matching clownmdemu behavior)
                    // DON'T call reset() here - it would set PC=0!
                    md_main.BeginZ80ResetCycle();
                    md_main.g_md_music?.g_md_ym2612.YM2612_Start();
                    md_main.g_md_z80.ArmPostResetHold();
                    md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
                }
            }
            Console.WriteLine($"[Z80SAFE] reset released frame={frame} - uploads will NOT be protected after this!");
            LogZ80SafeBootVerify();
        }

        private void ReleaseZ80SafeBootBusReq(long frame)
        {
            _z80BusGranted = false;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
            _z80SafeBootActive = false;
            Console.WriteLine($"[Z80SAFE] busreq released frame={frame} delay={Z80SafeBootDelayFrames}");
        }

        private void MarkZ80SafeBootUploadWrite(uint addr)
        {
            if (!Z80SafeBootEnabled || !_z80SafeBootUploadActive)
                return;
            long frame = SafeBootFrameNow();
            _z80SafeBootSawUpload = true;
            _z80SafeBootLastUploadFrame = frame;
            _z80SafeBootWriteCount++;
            // Log first 8 writes to show what's being uploaded
            if (_z80SafeBootWriteCount <= 8)
            {
                ushort z80Addr = (ushort)(addr & 0x1FFF);
                Console.WriteLine($"[Z80SAFE-UPLOAD] #{_z80SafeBootWriteCount} frame={frame} addr=0x{addr:X6} -> z80[{z80Addr:X4}]");
            }
        }

        private long SafeBootFrameNow()
        {
            long frame = md_main.g_md_vdp?.FrameCounter ?? 0;
            return frame < 0 ? 0 : frame;
        }

        private void LogZ80SafeBootVerify()
        {
            if (md_main.g_md_z80 == null)
                return;
            var sb = new StringBuilder(16 * 3);
            byte xor = 0x00;
            for (int i = 0; i < 16; i++)
            {
                byte val = md_main.g_md_z80.PeekZ80Ram((ushort)i);
                xor ^= val;
                if (i > 0)
                    sb.Append(' ');
                sb.Append(val.ToString("X2"));
            }
            Console.WriteLine($"[Z80SAFE] verify 0x0000..0x000F bytes={sb} xor=0x{xor:X2}");
            Console.WriteLine($"[Z80SAFE] total writes during safe boot: {_z80SafeBootWriteCount} bytes");
        }

        private bool ShouldSuppressSafeBootMirror()
        {
            return Z80SafeBootEnabled && _z80SafeBootUploadActive;
        }

        internal bool Z80BusGranted => _z80BusGranted;
        internal bool Z80Reset => _z80Reset;
        internal void PeekZ80SignalStats(out long busReqWrites, out long busReqToggles, out long resetWrites, out long resetToggles)
        {
            busReqWrites = Interlocked.Read(ref _z80BusReqWriteCount);
            busReqToggles = Interlocked.Read(ref _z80BusReqToggleCount);
            resetWrites = Interlocked.Read(ref _z80ResetWriteCount);
            resetToggles = Interlocked.Read(ref _z80ResetToggleCount);
        }
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

            if (IsSramLockReg(in_address))
            {
                byte val = _sramLock ? (byte)0x01 : (byte)0x00;
                SramLog($"[SRAM-LOCK-READ] addr=0x{in_address:X6} val=0x{val:X2} configured={IsSramConfigured}");
                return val;
            }

            if (IsSramAddress(in_address))
                return ReadSramByte(in_address);

            // Log any access to 0xA130F0-0xA130FF range (SRAM control area)
            if ((in_address & 0xFFFFF0) == 0xA130F0)
            {
                SramLog($"[SRAM-CTRL-READ] addr=0x{in_address:X6} val=0xFF");
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
                uint z80Addr = in_address;
                if (MapZ80OddReadToNext && (in_address & 1) != 0)
                    z80Addr = in_address + 1;
                if (TraceZ80Win && _z80OddReadLogRemaining > 0 && (in_address & 0x1FFF) == 0x1FFD)
                {
                    _z80OddReadLogRemaining--;
                    byte v1ffc = md_main.g_md_z80 != null ? md_main.g_md_z80.PeekZ80Ram(0x1FFC) : (byte)0xFF;
                    byte v1ffd = md_main.g_md_z80 != null ? md_main.g_md_z80.PeekZ80Ram(0x1FFD) : (byte)0xFF;
                    byte v1ffe = md_main.g_md_z80 != null ? md_main.g_md_z80.PeekZ80Ram(0x1FFE) : (byte)0xFF;
                    Console.WriteLine(
                        $"[Z80ODD] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{in_address:X6} map=0x{z80Addr:X6} v1ffc=0x{v1ffc:X2} v1ffd=0x{v1ffd:X2} v1ffe=0x{v1ffe:X2}");
                }
                if (UseMdTracerCompat)
                    return md_main.g_md_z80 != null ? md_main.g_md_z80.read8(z80Addr) : (byte)0xFF;
                if (!CanAccessZ80BusRange(in_address, 1))
                {
                    LogZ80WindowBlocked("R8", in_address);
                    return 0xFF;
                }
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

            if (IsSramLockReg(in_address) || IsSramLockReg(in_address + 1))
                return _sramLock ? (ushort)0x0101 : (ushort)0x0000;

            if (IsSramAddress(in_address))
            {
                byte hi = ReadSramByte(in_address);
                byte lo = ReadSramByte(in_address + 1);
                return (ushort)((hi << 8) | lo);
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
                if (UseMdTracerCompat)
                    return md_main.g_md_z80 != null ? md_main.g_md_z80.read16(in_address) : (ushort)0xFFFF;
                if (!CanAccessZ80BusRange(in_address, 2))
                {
                    LogZ80WindowBlocked("R16", in_address);
                    return 0xFFFF;
                }
                if (MirrorZ80WindowReads && !IsZ80Mailbox(in_address) && !IsZ80Mailbox(in_address + 1))
                {
                    uint byteAddr = (in_address & 1) == 0 ? in_address : in_address + 1;
                    byte mbxVal8 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(byteAddr) : (byte)0xFF;
                    ushort mbxMirrorWord = (ushort)((mbxVal8 << 8) | mbxVal8);
                    LogZ80MailboxRead("16", in_address, mbxMirrorWord);
                    return mbxMirrorWord;
                }
                byte hi = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address) : (byte)0xFF;
                byte lo = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address + 1) : (byte)0xFF;
                ushort word = (ushort)((hi << 8) | lo);
                LogZ80MailboxRead("16", in_address, word);
                return word;
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

            if (IsSramLockReg(in_address) || IsSramLockReg(in_address + 1) ||
                IsSramLockReg(in_address + 2) || IsSramLockReg(in_address + 3))
                return _sramLock ? 0x0101_0101u : 0x0000_0000u;

            if (IsSramAddress(in_address))
            {
                byte b0 = ReadSramByte(in_address);
                byte b1 = ReadSramByte(in_address + 1);
                byte b2 = ReadSramByte(in_address + 2);
                byte b3 = ReadSramByte(in_address + 3);
                return (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
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
                if (UseMdTracerCompat)
                    return md_main.g_md_z80 != null ? md_main.g_md_z80.read32(in_address) : 0xFFFF_FFFFu;
                if (!CanAccessZ80BusRange(in_address, 4))
                {
                    LogZ80WindowBlocked("R32", in_address);
                    return 0xFFFF_FFFFu;
                }
                if (MirrorZ80WindowReads &&
                    !IsZ80Mailbox(in_address) &&
                    !IsZ80Mailbox(in_address + 1) &&
                    !IsZ80Mailbox(in_address + 2) &&
                    !IsZ80Mailbox(in_address + 3))
                {
                    uint baseAddr = (in_address & 1) == 0 ? in_address : in_address + 1;
                    byte hi0 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(baseAddr) : (byte)0xFF;
                    byte hi1 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(baseAddr + 2) : (byte)0xFF;
                    uint mirrorVal = (uint)(((hi0 << 8) | hi0) << 16 | ((hi1 << 8) | hi1));
                    LogZ80MailboxRead("32", in_address, mirrorVal);
                    return mirrorVal;
                }
                byte b0 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address) : (byte)0xFF;
                byte b1 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address + 1) : (byte)0xFF;
                byte b2 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address + 2) : (byte)0xFF;
                byte b3 = md_main.g_md_z80 != null ? md_main.g_md_z80.read8(in_address + 3) : (byte)0xFF;
                uint val = (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
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
                bool uds = (in_address & 1) == 0;
                bool lds = !uds;
                ushort raw = uds ? (ushort)(in_data << 8) : in_data;
                HandleZ80BusReqWrite(in_address, raw, uds, lds, raw);
                return;
            }

            if (IsZ80Reset(in_address))
            {
                bool uds = (in_address & 1) == 0;
                bool lds = !uds;
                ushort raw = uds ? (ushort)(in_data << 8) : in_data;
                HandleZ80ResetWrite(in_address, raw, uds, lds, raw);
                return;
            }

            if (IsSramLockReg(in_address))
            {
                SramLog($"[SRAM-LOCK-WRITE] addr=0x{in_address:X6} data=0x{in_data:X2} lockBit={in_data & 0x01}");
                SetSramLock((in_data & 0x01) != 0, "lock");
                return;
            }

            if (IsSramAddress(in_address))
            {
                SramLog($"[SRAM-WRITE] addr=0x{in_address:X6} data=0x{in_data:X2} lock={_sramLock} configured={IsSramConfigured}");
                WriteSramByte(in_address, in_data);
                return;
            }

            // Log any access to 0xA130F0-0xA130FF range (SRAM control area)
            if ((in_address & 0xFFFFF0) == 0xA130F0)
            {
                SramLog($"[SRAM-CTRL-WRITE] addr=0x{in_address:X6} data=0x{in_data:X2}");
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
                bool uds = (in_address & 1) == 0;
                bool lds = !uds;
                bool udsOnlyRam = Z80UdsOnly && ((in_address & 0xFFFF) < 0x4000);
                if (udsOnlyRam && (in_address & 1) != 0)
                    return;
                if (UseMdTracerCompat)
                {
                    RecordZ80WinStat(in_address, in_data, 1, blocked: false);
                    MaybeLogZ80WinRangeWrite(in_address, in_data, false);
                    RecordZ80WinHist(in_address);
                    RecordMbx68kStatCompat(in_address, in_data);
                    if (TraceZ80Win && _z80WinLogRemaining > 0)
                    {
                        _z80WinLogRemaining--;
                        uint z80Index = in_address & 0x1FFF;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN] W8 addr=0x{in_address:X6} -> z80[{z80Index:X4}]=0x{in_data:X2} busReq={busReq} reset={reset}");
                    }
                    MaybeLogMbxByteWrite(1, in_address, in_data, uds, lds, blocked: false);
                    MaybeLogZ80Flag65WinWrite(in_address, in_data);
                    WriteZ80WindowByte(in_address, in_data);
                    MaybeDumpZ80WinRangeSnapshot((ushort)(in_address & 0x1FFF), 1, false);
                    return;
                }
                if (!CanAccessZ80BusRange(in_address, 1))
                {
                    RecordZ80WinStat(in_address, in_data, 1, blocked: true);
                    MaybeLogZ80WinRangeWrite(in_address, in_data, true);
                    MaybeLogMbxByteWrite(1, in_address, in_data, uds, lds, blocked: true);
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
                {
                    // M68K writes to Z80 bank register (shift-register style, like on real hardware)
                    // Each write shifts the register right and injects the LSB as bit 8
                    if (md_main.g_md_z80 != null)
                    {
                        var bank = md_main.g_md_z80.GetBankRegister();
                        bank >>= 1;
                        bank |= (in_data & 1) != 0 ? 0x100u : 0u;
                        md_main.g_md_z80.SetBankRegister(bank);
                    }
                    LogZ80BankRegWrite("8", in_address, in_data);
                }
                LogZ80Win68kOnce("byte", z80Addr, in_data);
                RecordZ80WinStat(z80Addr, in_data, 1, blocked: false);
                byte oldMbx = 0;
                bool hasOld = false;
                if (IsZ80Mailbox(z80Addr) && md_main.g_md_z80 != null)
                {
                    oldMbx = md_main.g_md_z80.PeekMailboxByte((int)((z80Addr & 0x1FFF) - 0x1B80));
                    hasOld = true;
                }
                MaybeLogMbxByteWrite(1, z80Addr, in_data, uds, lds, blocked: false);
                MaybeLogZ80Flag65WinWrite(z80Addr, in_data);
                WriteZ80WindowByte(z80Addr, in_data);
                RecordZ80WinHist(z80Addr);
                MaybeLogZ80WinRangeWrite(z80Addr, in_data, false);
                MaybeDumpZ80WinRangeSnapshot((ushort)(z80Addr & 0x1FFF), 1, false);
                md_main.g_md_z80?.RecordMailboxWriteFrom68k(z80Addr, in_data);
                MaybeMirrorMailboxWrite(z80Addr, in_data);
                MaybeMirrorMailboxWideCmdWrite(z80Addr, in_data);
                MaybeLogMbxSourceByte(z80Addr, in_data);
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
                HandleZ80BusReqWrite(in_address, in_data, uds: true, lds: true, in_data);
                return;
            }

            if (IsZ80Reset(in_address))
            {
                HandleZ80ResetWrite(in_address, in_data, uds: true, lds: true, in_data);
                return;
            }

            if (IsSramLockReg(in_address) || IsSramLockReg(in_address + 1))
            {
                byte regByte = (byte)(in_data & 0xFF);
                SetSramLock((regByte & 0x01) != 0, "lock16");
                return;
            }

            if (IsSramAddress(in_address))
            {
                byte hi = (byte)((in_data >> 8) & 0xFF);
                byte lo = (byte)(in_data & 0xFF);
                WriteSramByte(in_address, hi);
                WriteSramByte(in_address + 1, lo);
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
                bool udsOnlyRam = Z80UdsOnly && ((in_address & 0xFFFF) < 0x4000);
                if (udsOnlyRam)
                {
                    uint aligned = in_address & 0xFFFFFEu;
                    MaybeLogZ80WinRangeWrite16(aligned, in_data, uds: true, lds: false, blocked: false);
                    byte udsHi = (byte)((in_data >> 8) & 0xFF);
                    bool prev = _suppressZ80WinRangeByteLog;
                    _suppressZ80WinRangeByteLog = true;
                    write8(aligned, udsHi);
                    _suppressZ80WinRangeByteLog = prev;
                    return;
                }
                if (UseMdTracerCompat)
                {
                    byte hiCompat = (byte)((in_data >> 8) & 0xFF);
                    byte loCompat = (byte)(in_data & 0xFF);
                    bool latchHits = Z80Flag65Latch && Z80WindowHitsFlag65(in_address, 2);
                    RecordZ80WinStat(in_address, in_data, 2, blocked: false);
                    MaybeLogZ80WinRangeWrite16(in_address, in_data, uds: true, lds: true, blocked: false);
                    RecordZ80WinHist(in_address);
                    RecordZ80WinHist(in_address + 1);
                    RecordMbx68kStatCompat(in_address, hiCompat);
                    RecordMbx68kStatCompat(in_address + 1, loCompat);
                    if (TraceZ80Win && _z80WinLogRemaining > 0)
                    {
                        _z80WinLogRemaining--;
                        uint z80Index = in_address & 0x1FFF;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN] W16 addr=0x{in_address:X6} -> z80[{z80Index:X4}] val=0x{in_data:X4} busReq={busReq} reset={reset}");
                    }
                    MaybeLogMbxByteWrite(2, in_address, in_data, uds: true, lds: true, blocked: false);
                    MaybeLogZ80Flag65WinWrite(in_address, hiCompat);
                    MaybeLogZ80Flag65WinWrite(in_address + 1, loCompat);
                    if (latchHits)
                    {
                        WriteZ80WindowByte(in_address, hiCompat);
                        WriteZ80WindowByte(in_address + 1, loCompat);
                    }
                    else
                    {
                        // [BOOT-PROTECT] Check for boot code area protection in compat mode
                        ushort z80Addr = (ushort)(in_address & 0x1FFF);
                        bool inBootArea = z80Addr >= 0x0040 && z80Addr <= 0x0046;
                        bool nextInBootArea = (z80Addr + 1) >= 0x0040 && (z80Addr + 1) <= 0x0046;

                        if (Z80SafeBootEnabled && _z80SafeBootUploadActive && (inBootArea || nextInBootArea))
                        {
                            // Handle partial boot area writes in compat mode
                            if (TraceZ80Win && _z80WinLogRemaining > 0)
                            {
                                _z80WinLogRemaining--;
                                Console.WriteLine($"[Z80WIN-PROTECT-COMPAT] addr=0x{in_address:X6} val=0x{in_data:X4} skipped");
                            }
                        }
                        else
                        {
                            MarkZ80SafeBootUploadWrite(in_address);
                            md_main.g_md_z80?.write16(in_address, in_data);
                        }
                    }
                    MaybeDumpZ80WinRangeSnapshot((ushort)(in_address & 0x1FFF), 2, false);
                    return;
                }
                if (!CanAccessZ80BusRange(in_address, 2))
                {
                    RecordZ80WinStat(in_address, in_data, 2, blocked: true);
                    MaybeLogZ80WinRangeWrite16(in_address, in_data, uds: true, lds: true, blocked: true);
                    MaybeLogMbxByteWrite(2, in_address, in_data, uds: true, lds: true, blocked: true);
                    if (TraceZ80Win && !_z80WinWarned)
                    {
                        _z80WinWarned = true;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X4} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                if (IsZ80MailboxWriteRange(in_address) || IsZ80MailboxWriteRange(in_address + 1))
                {
                    MaybeLogZ80WinRangeWrite16(in_address, in_data, uds: true, lds: true, blocked: false);
                    byte mbxHi = (byte)((in_data >> 8) & 0xFF);
                    byte mbxLo = (byte)(in_data & 0xFF);
                    bool prev = _suppressZ80WinRangeByteLog;
                    _suppressZ80WinRangeByteLog = true;
                    bool prevMbx = _suppressMbxByteLog;
                    bool logged = MaybeLogMbxByteWrite(2, in_address, in_data, uds: true, lds: true, blocked: false);
                    if (logged)
                        _suppressMbxByteLog = true;
                    write8(in_address, mbxHi);
                    write8(in_address + 1, mbxLo);
                    _suppressMbxByteLog = prevMbx;
                    _suppressZ80WinRangeByteLog = prev;
                    return;
                }
                if (DirectZ80WindowWords)
                {
                    RecordZ80WinStat(in_address, in_data, 2, blocked: false);
                    byte logHi = (byte)((in_data >> 8) & 0xFF);
                    byte logLo = (byte)(in_data & 0xFF);
                    MaybeLogZ80Flag65WinWrite(in_address, logHi);
                    MaybeLogZ80Flag65WinWrite(in_address + 1, logLo);
                    if (Z80Flag65Latch && Z80WindowHitsFlag65(in_address, 2))
                    {
                        WriteZ80WindowByte(in_address, logHi);
                        WriteZ80WindowByte(in_address + 1, logLo);
                    }
                    else
                    {
                        MarkZ80SafeBootUploadWrite(in_address);
                        md_main.g_md_z80?.write16(in_address, in_data);
                    }
                    MaybeDumpZ80WinRangeSnapshot((ushort)(in_address & 0x1FFF), 2, false);
                    MaybeLogZ80WinRangeWrite16(in_address, in_data, uds: true, lds: true, blocked: false);
                    MaybeLogMbxByteWrite(2, in_address, in_data, uds: true, lds: true, blocked: false);
                    if (TraceZ80Win && _z80WinLogRemaining > 0)
                    {
                        _z80WinLogRemaining--;
                        uint z80Index = in_address & 0x1FFF;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN] W16 addr=0x{in_address:X6} -> z80[{z80Index:X4}] val=0x{in_data:X4} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                byte hi = (byte)((in_data >> 8) & 0xFF);
                byte lo = (byte)(in_data & 0xFF);
                MaybeLogZ80WinRangeWrite16(in_address, in_data, uds: true, lds: true, blocked: false);
                bool prevWord = _suppressZ80WinRangeByteLog;
                _suppressZ80WinRangeByteLog = true;
                write8(in_address, hi);
                write8(in_address + 1, lo);
                _suppressZ80WinRangeByteLog = prevWord;
                return;
            }

            Debug.WriteLine($"[BUS] write16 @0x{in_address:X6} = 0x{in_data:X4} (ignored)");
        }

        public void write32(uint in_address, uint in_data)
        {
            in_address &= 0x00FF_FFFF;

            if (IsZ80BusReq(in_address))
            {
                ushort raw = (ushort)((in_data >> 16) & 0xFFFF);
                HandleZ80BusReqWrite(in_address, raw, uds: true, lds: true, in_data);
                return;
            }

            if (IsZ80Reset(in_address))
            {
                ushort raw = (ushort)((in_data >> 16) & 0xFFFF);
                HandleZ80ResetWrite(in_address, raw, uds: true, lds: true, in_data);
                return;
            }

            if (IsSramLockReg(in_address) || IsSramLockReg(in_address + 1) ||
                IsSramLockReg(in_address + 2) || IsSramLockReg(in_address + 3))
            {
                byte regByte = (byte)(in_data & 0xFF);
                SetSramLock((regByte & 0x01) != 0, "lock32");
                return;
            }

            if (IsSramAddress(in_address))
            {
                byte b3 = (byte)((in_data >> 24) & 0xFF);
                byte b2 = (byte)((in_data >> 16) & 0xFF);
                byte b1 = (byte)((in_data >> 8) & 0xFF);
                byte b0 = (byte)(in_data & 0xFF);
                WriteSramByte(in_address, b3);
                WriteSramByte(in_address + 1, b2);
                WriteSramByte(in_address + 2, b1);
                WriteSramByte(in_address + 3, b0);
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
                bool udsOnlyRam = Z80UdsOnly && ((in_address & 0xFFFF) < 0x4000);
                if (udsOnlyRam)
                {
                    uint aligned = in_address & 0xFFFFFEu;
                    MaybeLogZ80WinRangeWrite32(aligned, in_data, uds: true, lds: false, blocked: false);
                    byte udsHi1 = (byte)((in_data >> 24) & 0xFF);
                    byte udsHi0 = (byte)((in_data >> 8) & 0xFF);
                    bool prev = _suppressZ80WinRangeByteLog;
                    _suppressZ80WinRangeByteLog = true;
                    write8(aligned, udsHi1);
                    write8(aligned + 2, udsHi0);
                    _suppressZ80WinRangeByteLog = prev;
                    return;
                }
                if (UseMdTracerCompat)
                {
                    byte b3Compat = (byte)((in_data >> 24) & 0xFF);
                    byte b2Compat = (byte)((in_data >> 16) & 0xFF);
                    byte b1Compat = (byte)((in_data >> 8) & 0xFF);
                    byte b0Compat = (byte)(in_data & 0xFF);
                    bool latchHits = Z80Flag65Latch && Z80WindowHitsFlag65(in_address, 4);
                    RecordZ80WinStat(in_address, in_data, 4, blocked: false);
                    MaybeLogZ80WinRangeWrite32(in_address, in_data, uds: true, lds: true, blocked: false);
                    RecordZ80WinHist(in_address);
                    RecordZ80WinHist(in_address + 1);
                    RecordZ80WinHist(in_address + 2);
                    RecordZ80WinHist(in_address + 3);
                    RecordMbx68kStatCompat(in_address, b3Compat);
                    RecordMbx68kStatCompat(in_address + 1, b2Compat);
                    RecordMbx68kStatCompat(in_address + 2, b1Compat);
                    RecordMbx68kStatCompat(in_address + 3, b0Compat);
                    if (TraceZ80Win && _z80WinLogRemaining > 0)
                    {
                        _z80WinLogRemaining--;
                        uint z80Index = in_address & 0x1FFF;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN] W32 addr=0x{in_address:X6} -> z80[{z80Index:X4}] val=0x{in_data:X8} busReq={busReq} reset={reset}");
                    }
                    MaybeLogMbxByteWrite(4, in_address, in_data, uds: true, lds: true, blocked: false);
                    MaybeLogZ80Flag65WinWrite(in_address, b3Compat);
                    MaybeLogZ80Flag65WinWrite(in_address + 1, b2Compat);
                    MaybeLogZ80Flag65WinWrite(in_address + 2, b1Compat);
                    MaybeLogZ80Flag65WinWrite(in_address + 3, b0Compat);
                    if (latchHits)
                    {
                        WriteZ80WindowByte(in_address, b3Compat);
                        WriteZ80WindowByte(in_address + 1, b2Compat);
                        WriteZ80WindowByte(in_address + 2, b1Compat);
                        WriteZ80WindowByte(in_address + 3, b0Compat);
                    }
                    else
                    {
                        MarkZ80SafeBootUploadWrite(in_address);
                        md_main.g_md_z80?.write32(in_address, in_data);
                    }
                    MaybeDumpZ80WinRangeSnapshot((ushort)(in_address & 0x1FFF), 4, false);
                    return;
                }
                if (!CanAccessZ80BusRange(in_address, 4))
                {
                    RecordZ80WinStat(in_address, in_data, 4, blocked: true);
                    MaybeLogZ80WinRangeWrite32(in_address, in_data, uds: true, lds: true, blocked: true);
                    MaybeLogMbxByteWrite(4, in_address, in_data, uds: true, lds: true, blocked: true);
                    if (TraceZ80Win && !_z80WinWarned)
                    {
                        _z80WinWarned = true;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{in_address:X6} val=0x{in_data:X8} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                if (IsZ80MailboxWriteRange(in_address) || IsZ80MailboxWriteRange(in_address + 1) ||
                    IsZ80MailboxWriteRange(in_address + 2) || IsZ80MailboxWriteRange(in_address + 3))
                {
                    MaybeLogZ80WinRangeWrite32(in_address, in_data, uds: true, lds: true, blocked: false);
                    byte mbxB3 = (byte)((in_data >> 24) & 0xFF);
                    byte mbxB2 = (byte)((in_data >> 16) & 0xFF);
                    byte mbxB1 = (byte)((in_data >> 8) & 0xFF);
                    byte mbxB0 = (byte)(in_data & 0xFF);
                    bool prev = _suppressZ80WinRangeByteLog;
                    _suppressZ80WinRangeByteLog = true;
                    bool prevMbx = _suppressMbxByteLog;
                    bool logged = MaybeLogMbxByteWrite(4, in_address, in_data, uds: true, lds: true, blocked: false);
                    if (logged)
                        _suppressMbxByteLog = true;
                    write8(in_address, mbxB3);
                    write8(in_address + 1, mbxB2);
                    write8(in_address + 2, mbxB1);
                    write8(in_address + 3, mbxB0);
                    _suppressMbxByteLog = prevMbx;
                    _suppressZ80WinRangeByteLog = prev;
                    return;
                }
                if (DirectZ80WindowWords)
                {
                    RecordZ80WinStat(in_address, in_data, 4, blocked: false);
                    byte logB3 = (byte)((in_data >> 24) & 0xFF);
                    byte logB2 = (byte)((in_data >> 16) & 0xFF);
                    byte logB1 = (byte)((in_data >> 8) & 0xFF);
                    byte logB0 = (byte)(in_data & 0xFF);
                    MaybeLogZ80Flag65WinWrite(in_address, logB3);
                    MaybeLogZ80Flag65WinWrite(in_address + 1, logB2);
                    MaybeLogZ80Flag65WinWrite(in_address + 2, logB1);
                    MaybeLogZ80Flag65WinWrite(in_address + 3, logB0);
                    if (Z80Flag65Latch && Z80WindowHitsFlag65(in_address, 4))
                    {
                        WriteZ80WindowByte(in_address, logB3);
                        WriteZ80WindowByte(in_address + 1, logB2);
                        WriteZ80WindowByte(in_address + 2, logB1);
                        WriteZ80WindowByte(in_address + 3, logB0);
                    }
                    else
                    {
                        MarkZ80SafeBootUploadWrite(in_address);
                        md_main.g_md_z80?.write32(in_address, in_data);
                    }
                    MaybeDumpZ80WinRangeSnapshot((ushort)(in_address & 0x1FFF), 4, false);
                    MaybeLogZ80WinRangeWrite32(in_address, in_data, uds: true, lds: true, blocked: false);
                    MaybeLogMbxByteWrite(4, in_address, in_data, uds: true, lds: true, blocked: false);
                    if (TraceZ80Win && _z80WinLogRemaining > 0)
                    {
                        _z80WinLogRemaining--;
                        uint z80Index = in_address & 0x1FFF;
                        int busReq = _z80BusGranted ? 1 : 0;
                        int reset = _z80Reset ? 1 : 0;
                        Console.WriteLine($"[Z80WIN] W32 addr=0x{in_address:X6} -> z80[{z80Index:X4}] val=0x{in_data:X8} busReq={busReq} reset={reset}");
                    }
                    return;
                }
                byte b3 = (byte)((in_data >> 24) & 0xFF);
                byte b2 = (byte)((in_data >> 16) & 0xFF);
                byte b1 = (byte)((in_data >> 8) & 0xFF);
                byte b0 = (byte)(in_data & 0xFF);
                MaybeLogZ80WinRangeWrite32(in_address, in_data, uds: true, lds: true, blocked: false);
                bool prevLong = _suppressZ80WinRangeByteLog;
                _suppressZ80WinRangeByteLog = true;
                write8(in_address, b3);
                write8(in_address + 1, b2);
                write8(in_address + 2, b1);
                write8(in_address + 3, b0);
                _suppressZ80WinRangeByteLog = prevLong;
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
            if (ShouldSuppressSafeBootMirror())
                return;
            if (!MirrorZ80Mailbox)
                return;
            uint low = addr & 0x1FFF;
            if (low < 0x1B88 || low > 0x1B8F)
                return;
            uint mirrorAddr = addr - 0x08;
            md_main.g_md_z80?.write8(mirrorAddr, value);
        }

        private void MaybeMirrorMailboxWideCmdWrite(uint addr, byte value)
        {
            if (ShouldSuppressSafeBootMirror())
                return;
            if (!MirrorZ80MailboxWideCmd)
                return;
            uint low = addr & 0x1FFF;
            if (low != 0x1B20 && low != 0x1B21)
                return;
            uint mirrorAddr = addr - 0x04; // 1B20->1B1C, 1B21->1B1D
            if (value == 0x00 && md_main.g_md_z80 != null)
            {
                byte current = md_main.g_md_z80.PeekZ80Ram(mirrorAddr);
                if (current != 0x00)
                {
                    if (TraceZ80Mbx)
                    {
                        Console.WriteLine(
                            $"[MBXW68K-WM] pc68k={md_m68k.g_reg_PC:X6} addr=0x{addr:X6} -> z80=0x{mirrorAddr & 0x1FFF:X4} val=0x{value:X2} (skip-clear)");
                    }
                    return;
                }
            }
            md_main.g_md_z80?.write8(mirrorAddr, value);
            if ((addr & 0x1FFF) == 0x1B20)
                md_main.g_md_z80?.LatchMailboxWideCmd(value);
            if (TraceZ80Mbx)
            {
                Console.WriteLine(
                    $"[MBXW68K-WM] pc68k={md_m68k.g_reg_PC:X6} addr=0x{addr:X6} -> z80=0x{mirrorAddr & 0x1FFF:X4} val=0x{value:X2}");
            }
        }

        private void LogZ80RegWrite(uint addr, uint val)
        {
            if (_z80RegWriteLogRemaining <= 0)
                return;
            _z80RegWriteLogRemaining--;
            MdLog.WriteLine($"[md_bus] Z80 reg write 0x{addr:X6} <- 0x{val:X}");
        }

        private static byte DecodeZ80RegByte(ushort raw, bool uds, bool lds)
        {
            if (uds && !lds)
                return (byte)((raw >> 8) & 0xFF);
            if (lds && !uds)
                return (byte)(raw & 0xFF);
            return (byte)((raw >> 8) & 0xFF);
        }

        private void LogZ80RegDecode(string tag, uint addr, ushort raw, bool uds, bool lds, byte regByte, bool state)
        {
            if (!TraceZ80RegDecode)
                return;
            string stateName = tag == "BUSREQ" ? "busreq" : "reset";
            Console.WriteLine(
                $"[Z80REG] {tag} addr=0x{addr:X6} raw=0x{raw:X4} uds={(uds ? 1 : 0)} lds={(lds ? 1 : 0)} " +
                $"byte=0x{regByte:X2} {stateName}={(state ? 1 : 0)}");
        }

        private void HandleZ80BusReqWrite(uint addr, ushort raw, bool uds, bool lds, uint logValue)
        {
            bool prev = _z80BusGranted;
            byte regByte = DecodeZ80RegByte(raw, uds, lds);
            bool next = (regByte & 0x01) != 0;
            if (Z80SafeBootEnabled && _z80SafeBootActive)
                next = true;
            _z80BusGranted = next;
            _z80ForceGrant = false;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
            Interlocked.Increment(ref _z80BusReqWriteCount);
            if (prev != next)
                Interlocked.Increment(ref _z80BusReqToggleCount);
            LogZ80RegWrite(addr, logValue);
            LogZ80RegDecode("BUSREQ", addr, raw, uds, lds, regByte, next);
            if (TraceZ80Win && _z80BusReqLogRemaining > 0)
            {
                _z80BusReqLogRemaining--;
                Console.WriteLine(
                    $"[Z80BUSREQ] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{addr:X6} val=0x{logValue:X} stopOn={(next ? 1 : 0)}");
            }
            if (prev != next && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] BUSREQ={(next ? 1 : 0)} (stopOn={(next ? 1 : 0)})");
            _z80BusAckLogState = -1;
            _z80BusAckLogState8 = -1;
            if (ResetZ80OnBusReqRelease && prev && !next && md_main.g_md_z80 != null &&
                _z80ResetOnBusReqRemaining > 0)
            {
                md_main.BeginZ80ResetCycle();
                md_main.g_md_z80.reset();
                Console.WriteLine(
                    $"[Z80BUSREQ-RESET] pc68k=0x{md_m68k.g_reg_PC:X6} reason=busreq_release");
                if (_z80ResetOnBusReqRemaining != int.MaxValue)
                    _z80ResetOnBusReqRemaining--;
            }
        }

        private void HandleZ80ResetWrite(uint addr, ushort raw, bool uds, bool lds, uint logValue)
        {
            bool prev = _z80Reset;
            byte regByte = DecodeZ80RegByte(raw, uds, lds);
            bool next = (regByte & 0x01) == 0;
            if (Z80SafeBootEnabled && _z80SafeBootUploadActive)
                next = true;
            _z80Reset = next;
            if (prev != next && md_main.g_md_z80 != null)
            {
                if (next)
                {
                    md_main.BeginZ80ResetCycle();
                    md_main.g_md_z80.reset();
                }
                else
                {
                    // Sync Z80 and reinitialize FM when reset is released (matching clownmdemu behavior)
                    // DON'T call reset() here - it would set PC=0 and restart Z80 from boot!
                    md_main.BeginZ80ResetCycle();
                    md_main.g_md_music?.g_md_ym2612.YM2612_Start();
                    md_main.g_md_z80.ArmPostResetHold();
                    // Set SP to same value as boot code would (0x1F00) since we skip boot code execution
                    md_main.g_md_z80.SetStackPointer(0x1F00);
                    // Force Z80 to execute driver code at 0x0167 after reset release
                    // This gives the 68K time to upload the driver before Z80 starts executing
                    // Only do this after safe boot has completed (when busreq is not granted)
                    if (!_z80BusGranted)
                    {
                        md_main.g_md_z80.ArmForcePc(0x0167, "resetRelease");
                    }
                }
                md_main.g_md_z80.g_active = !_z80BusGranted && !_z80Reset;
            }
            Interlocked.Increment(ref _z80ResetWriteCount);
            if (prev != next)
                Interlocked.Increment(ref _z80ResetToggleCount);
            LogZ80RegWrite(addr, logValue);
            LogZ80RegDecode("RESET", addr, raw, uds, lds, regByte, next);
            if (prev != next)
            {
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                uint pc68k = md_main.g_md_m68k != null ? md_m68k.g_reg_PC : 0u;
                Console.WriteLine($"[Z80RESET] frame={frame} write addr=0x{addr:X6} val=0x{logValue:X} resetOn={(next ? 1 : 0)} pc68k=0x{pc68k:X6}");
            }
            if (prev != next && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] RESET={(next ? 1 : 0)} ({(next ? "assert" : "deassert")})");
            _z80BusAckLogState = -1;
            _z80BusAckLogState8 = -1;
        }

        private void LogZ80MailboxByte(uint addr, byte value, byte oldValue)
        {
            uint low = addr & 0x1FFF;
            if (low >= 0x1B80 && low <= 0x1BFF)
            {
                RecordMbx68kStat(addr, value);
                MbxSyncTrace.Record68kWrite(addr, value);
                if (low == 0x1B8F && TraceMbx68kEdge && _mbx68kEdgeRemaining > 0)
                {
                    long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                    string edgeDump = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxDump() : string.Empty;
                    Console.WriteLine(
                        $"[MBX68K-EDGE] frame={frame} pc68k={md_m68k.g_reg_PC:X6} addr={addr:X6} z80={low:X4} val={value:X2} dump= {edgeDump}");
                    if (_mbx68kEdgeRemaining != int.MaxValue)
                        _mbx68kEdgeRemaining--;
                }
                if (low == 0x1B8F && MirrorMbxToZ80Flag && md_main.g_md_z80 != null && !ShouldSuppressSafeBootMirror())
                {
                    byte flagValue = MirrorMbxToZ80FlagValue ?? value;
                    md_main.g_md_z80.write8(Z80FlagMirrorAddr, flagValue);
                    byte after = md_main.g_md_z80.PeekZ80Ram(Z80FlagMirrorAddr);
                    ushort? addr2 = Z80FlagMirrorAddr2;
                    byte after2 = 0x00;
                    if (addr2.HasValue && addr2.Value != Z80FlagMirrorAddr)
                    {
                        md_main.g_md_z80.write8(addr2.Value, flagValue);
                        after2 = md_main.g_md_z80.PeekZ80Ram(addr2.Value);
                    }
                    if (!_z80Flag65MirrorLogged)
                    {
                        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                        string extra = addr2.HasValue && addr2.Value != Z80FlagMirrorAddr
                            ? $" addr2=0x{addr2.Value:X4} after2=0x{after2:X2}"
                            : string.Empty;
                        Console.WriteLine(
                            $"[Z80FLAG65] frame={frame} pc68k=0x{md_m68k.g_reg_PC:X6} addr=0x{Z80FlagMirrorAddr:X4} " +
                            $"wrote=0x{flagValue:X2} after=0x{after:X2}{extra} source=1B8F");
                        _z80Flag65MirrorLogged = true;
                    }
                }
                if (MirrorMbxRangeToZ80Flag && md_main.g_md_z80 != null && low <= 0x1B8F && !ShouldSuppressSafeBootMirror())
                {
                    ushort target = (ushort)(Z80FlagRangeMirrorAddr + (low - 0x1B80));
                    byte before = md_main.g_md_z80.PeekZ80Ram(target);
                    if (value != 0x00 || before != 0x00)
                    {
                        md_main.g_md_z80.write8(target, value);
                        byte after = md_main.g_md_z80.PeekZ80Ram(target);
                        if (_mbxRangeMirrorRemaining > 0 && value != before)
                        {
                            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                            Console.WriteLine(
                                $"[Z80FLAG-RANGE] frame={frame} pc68k=0x{md_m68k.g_reg_PC:X6} " +
                                $"mbx=0x{low:X4} addr=0x{target:X4} val=0x{value:X2} after=0x{after:X2}");
                            if (_mbxRangeMirrorRemaining != int.MaxValue)
                                _mbxRangeMirrorRemaining--;
                        }
                    }
                }
                if (!MbxSyncTrace.IsEnabled && !TraceZ80Mbx)
                    return;
                int index = (int)(low - 0x1B80);
                if (_mbx68kLogRemaining <= 0)
                    return;
                _mbx68kLogRemaining--;
                _mbx68kLast[index] = value;
                string dump = md_main.g_md_z80 != null ? md_main.g_md_z80.GetMailboxDump() : string.Empty;
                Console.WriteLine(
                    $"[MBXW68K] pc68k={md_m68k.g_reg_PC:X6} addr={addr:X6} z80={addr & 0x1FFF:X4} val={value:X2} dump= {dump}");
                return;
            }
            if (!IsZ80MailboxWide(addr))
                return;
            RecordMbx68kStat(addr, value);
            if (MirrorMbxWideRangeToZ80Flag && md_main.g_md_z80 != null && !ShouldSuppressSafeBootMirror())
            {
                ushort start = Z80FlagWideRangeMirrorStart;
                ushort end = Z80FlagWideRangeMirrorEnd;
                if (start > end)
                {
                    ushort tmp = start;
                    start = end;
                    end = tmp;
                }
                if (low >= start && low <= end)
                {
                    ushort target = (ushort)(Z80FlagWideRangeMirrorAddr + (low - start));
                    byte before = md_main.g_md_z80.PeekZ80Ram(target);
                    if (value != 0x00 || before != 0x00)
                    {
                        md_main.g_md_z80.write8(target, value);
                        byte after = md_main.g_md_z80.PeekZ80Ram(target);
                        if (_mbxWideRangeMirrorRemaining > 0 && value != before)
                        {
                            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                            Console.WriteLine(
                                $"[Z80FLAG-WIDE] frame={frame} pc68k=0x{md_m68k.g_reg_PC:X6} " +
                                $"mbx=0x{low:X4} addr=0x{target:X4} val=0x{value:X2} after=0x{after:X2}");
                            if (_mbxWideRangeMirrorRemaining != int.MaxValue)
                                _mbxWideRangeMirrorRemaining--;
                        }
                    }
                }
            }
            if (!TraceZ80Mbx)
                return;
            bool nonZero = value != 0x00;
            if (!nonZero && _mbx68kLogRemaining <= 0)
                return;
            if (!nonZero)
                _mbx68kLogRemaining--;
            Console.WriteLine(
                $"[MBXW68K-W] pc68k={md_m68k.g_reg_PC:X6} addr={addr:X6} z80={addr & 0x1FFF:X4} val={value:X2}");
        }

        private void MaybeLogZ80Flag65WinWrite(uint addr, byte value)
        {
            if (!TraceZ80Flag65Win || _z80Flag65WinRemaining <= 0)
                return;
            if (md_main.g_md_z80 == null)
                return;
            uint z80Index = addr & 0x1FFF;
            if (z80Index < 0x0060 || z80Index > 0x006F)
                return;
            byte oldValue = md_main.g_md_z80.PeekZ80Ram((ushort)z80Index);
            if (value == 0x00 && oldValue == 0x00)
                return;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine(
                $"[Z80WIN65] frame={frame} pc68k=0x{md_m68k.g_reg_PC:X6} addr=0x{addr:X6} " +
                $"z80=0x{z80Index:X4} val=0x{value:X2} old=0x{oldValue:X2}");
            if (_z80Flag65WinRemaining != int.MaxValue)
                _z80Flag65WinRemaining--;
        }

        private bool Z80WindowHitsFlag65(uint addr, int size)
        {
            uint start = addr & 0x1FFF;
            uint end = (start + (uint)size - 1) & 0x1FFF;
            if (start <= end)
                return start <= 0x0065 && 0x0065 <= end;
            return 0x0065 >= start || 0x0065 <= end;
        }

        private bool ShouldSuppressZ80Flag65Write(uint addr, byte value, out byte kept)
        {
            kept = 0x00;
            if (!Z80Flag65Latch)
                return false;
            if (value != 0x00)
                return false;
            if (md_main.g_md_z80 == null)
                return false;
            uint z80Index = addr & 0x1FFF;
            if (z80Index != Z80FlagLatchAddr &&
                (!Z80FlagLatchAddr2.HasValue || z80Index != Z80FlagLatchAddr2.Value))
                return false;
            kept = md_main.g_md_z80.PeekZ80Ram((ushort)z80Index);
            if (kept == 0x00)
                return false;
            if (_z80Flag65LatchRemaining > 0)
            {
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine(
                    $"[Z80FLAG65-LATCH] frame={frame} pc68k=0x{md_m68k.g_reg_PC:X6} " +
                    $"addr=0x{z80Index:X4} wrote=0x{value:X2} kept=0x{kept:X2} reason=upload");
                if (_z80Flag65LatchRemaining != int.MaxValue)
                    _z80Flag65LatchRemaining--;
            }
            return true;
        }

        private void WriteZ80WindowByte(uint addr, byte value)
        {
            if (md_main.g_md_z80 == null)
                return;

            // [BOOT-PROTECT] Protect boot code area (0x0040-0x0046) during safe boot upload
            // This includes: DI (0x0040), LD SP,nn (0x0041-0x0043), JP nn (0x0044-0x0046)
            ushort z80Addr = (ushort)(addr & 0x1FFF);
            if (Z80SafeBootEnabled && _z80SafeBootUploadActive && z80Addr >= 0x0040 && z80Addr <= 0x0046)
            {
                // Skip write to protect boot code (DI, LD SP, JP to driver)
                if (TraceZ80Win && _z80WinLogRemaining > 0)
                {
                    _z80WinLogRemaining--;
                    Console.WriteLine($"[Z80WIN-PROTECT] addr=0x{addr:X6} -> z80[{z80Addr:X4}] val=0x{value:X2} skipped");
                }
                return;
            }

            MarkZ80SafeBootUploadWrite(addr);
            if (ShouldForceZ80FlagBit2 && (addr & 0x1FFF) == ForceZ80FlagBit2Target)
                value = (byte)(value | 0x04);
            if (ShouldSuppressZ80Flag65Write(addr, value, out _))
                return;
            md_main.g_md_z80.write8(addr, value);
            MaybeForceZ80PcOnUpload(addr);
        }

        private void MaybeForceZ80PcOnUpload(uint addr)
        {
            if (!ForceZ80PcOnUpload || _z80ForcePcOnUploadRemaining <= 0)
                return;
            ushort start = ForceZ80PcOnUploadStart;
            ushort end = ForceZ80PcOnUploadEnd;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            ushort z80Index = (ushort)(addr & 0x1FFF);
            if (z80Index < start || z80Index > end)
                return;
            md_main.g_md_z80?.ArmForcePc(ForceZ80PcOnUploadTarget, "upload");
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine(
                $"[Z80PC-ARM] frame={frame} z80=0x{z80Index:X4} target=0x{ForceZ80PcOnUploadTarget:X4}");
            if (_z80ForcePcOnUploadRemaining != int.MaxValue)
                _z80ForcePcOnUploadRemaining--;
        }

        private static void MaybeLogMbxSourceByte(uint addr, byte value)
        {
            if (!TraceMbxSrc)
                return;
            uint low = addr & 0x1FFF;
            if (low != 0x1B8F)
                return;
            uint a0 = md_m68k.g_reg_addr[0].l;
            uint src = a0 + (low - 0x1B80);
            md_m68k.InitMemoryIfNeeded();
            byte? srcVal = null;
            if (md_m68k.g_memory != null)
            {
                uint norm = NormalizeM68kAddr(src);
                srcVal = md_m68k.g_memory[norm];
            }
            int compat = UseMdTracerCompat ? 1 : 0;
            string srcValInfo = srcVal.HasValue ? $" srcVal=0x{srcVal.Value:X2}" : " srcVal=<none>";
            Console.WriteLine(
                $"[MBXSRC] pc68k=0x{md_m68k.g_reg_PC:X6} z80=0x{low:X4} val=0x{value:X2} " +
                $"a0=0x{a0:X6} src=0x{src:X6}{srcValInfo} compat={compat}");
        }

        private static bool HitsMbxByte(uint addr, int size)
        {
            uint low = addr & 0x1FFF;
            for (int i = 0; i < size; i++)
            {
                uint offset = (low + (uint)i) & 0x1FFF;
                if (offset == 0x1B8F)
                    return true;
            }
            return false;
        }

        private static ushort PeekM68kMem16(uint addr)
        {
            md_m68k.InitMemoryIfNeeded();
            if (md_m68k.g_memory == null)
                return 0;
            uint n0 = NormalizeM68kAddr(addr);
            uint n1 = NormalizeM68kAddr(addr + 1);
            byte hi = md_m68k.g_memory[n0];
            byte lo = md_m68k.g_memory[n1];
            return (ushort)((hi << 8) | lo);
        }

        private static string DumpM68kBytes(uint start, int count)
        {
            md_m68k.InitMemoryIfNeeded();
            if (md_m68k.g_memory == null || count <= 0)
                return "<none>";
            var sb = new StringBuilder(count * 3 - 1);
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                uint norm = NormalizeM68kAddr(start + (uint)i);
                sb.Append(md_m68k.g_memory[norm].ToString("X2"));
            }
            return sb.ToString();
        }

        private bool MaybeLogMbxByteWrite(int size, uint addr, uint value, bool uds, bool lds, bool blocked)
        {
            if (!TraceMbxSrcDump || _mbxSrcDumpRemaining <= 0 || _suppressMbxByteLog)
                return false;
            if (!HitsMbxByte(addr, size))
                return false;

            string sizeTag = size == 1 ? "W8" : size == 2 ? "W16" : "W32";
            string valueInfo;
            if (size == 1)
            {
                valueInfo = $"val=0x{(byte)value:X2}";
            }
            else if (size == 2)
            {
                byte hi = (byte)((value >> 8) & 0xFF);
                byte lo = (byte)(value & 0xFF);
                valueInfo = $"val=0x{(ushort)value:X4} bytes=0x{hi:X2} 0x{lo:X2}";
            }
            else
            {
                byte b3 = (byte)((value >> 24) & 0xFF);
                byte b2 = (byte)((value >> 16) & 0xFF);
                byte b1 = (byte)((value >> 8) & 0xFF);
                byte b0 = (byte)(value & 0xFF);
                valueInfo = $"val=0x{value:X8} bytes=0x{b3:X2} 0x{b2:X2} 0x{b1:X2} 0x{b0:X2}";
            }

            int busReq = _z80BusGranted ? 1 : 0;
            int reset = _z80Reset ? 1 : 0;
            int busAck = (BuildBusAckRead8() & 0x01) != 0 ? 1 : 0;
            uint pc = md_m68k.g_reg_PC;
            ushort op = PeekM68kMem16(pc);
            ushort opM2 = PeekM68kMem16(unchecked(pc - 2u));
            ushort opM4 = PeekM68kMem16(unchecked(pc - 4u));
            ushort opM6 = PeekM68kMem16(unchecked(pc - 6u));
            ushort opM8 = PeekM68kMem16(unchecked(pc - 8u));
            uint a0 = md_m68k.g_reg_addr[0].l;
            uint a1 = md_m68k.g_reg_addr[1].l;
            uint a2 = md_m68k.g_reg_addr[2].l;
            uint a3 = md_m68k.g_reg_addr[3].l;
            uint a4 = md_m68k.g_reg_addr[4].l;
            uint a5 = md_m68k.g_reg_addr[5].l;
            uint a6 = md_m68k.g_reg_addr[6].l;
            uint a7 = md_m68k.g_reg_addr[7].l;
            uint d0 = md_m68k.g_reg_data[0].l;
            uint d1 = md_m68k.g_reg_data[1].l;
            uint d2 = md_m68k.g_reg_data[2].l;
            uint d3 = md_m68k.g_reg_data[3].l;
            uint d4 = md_m68k.g_reg_data[4].l;
            uint d5 = md_m68k.g_reg_data[5].l;
            uint d6 = md_m68k.g_reg_data[6].l;
            uint d7 = md_m68k.g_reg_data[7].l;
            uint srcNeg = unchecked(a0 - 0x10u);
            uint srcPos = a0;
            string dumpNeg = DumpM68kBytes(srcNeg, 32);
            string dumpPos = DumpM68kBytes(srcPos, 32);
            uint a5DumpStart = unchecked(a5 - 0x10u);
            uint a6DumpStart = unchecked(a6 - 0x10u);
            string dumpA5 = DumpM68kBytes(a5DumpStart, 48);
            string dumpA6 = DumpM68kBytes(a6DumpStart, 48);
            uint pcDumpStart = unchecked(pc - 0x10u);
            string pcDump = DumpM68kBytes(pcDumpStart, 33);

            if (!_mbxLoopInit || _mbxLoopA5 != a5 || _mbxLoopA6 != a6)
            {
                _mbxLoopInit = true;
                _mbxLoopA5 = a5;
                _mbxLoopA6 = a6;
                Console.WriteLine(
                    $"[MBXLOOP] pc68k=0x{pc:X6} a5=0x{a5:X6} a6=0x{a6:X6} " +
                    $"a5dump=0x{a5DumpStart:X6}:{dumpA5} a6dump=0x{a6DumpStart:X6}:{dumpA6}");
            }

            Console.WriteLine(
                $"[MBXBYTE] pc68k=0x{pc:X6} op=0x{op:X4} op-2=0x{opM2:X4} op-4=0x{opM4:X4} op-6=0x{opM6:X4} op-8=0x{opM8:X4} " +
                $"{sizeTag} uds={(uds ? 1 : 0)} lds={(lds ? 1 : 0)} " +
                $"busReq={busReq} busAck={busAck} reset={reset} blocked={(blocked ? 1 : 0)} " +
                $"a0=0x{a0:X6} a1=0x{a1:X6} a2=0x{a2:X6} a3=0x{a3:X6} " +
                $"a4=0x{a4:X6} a5=0x{a5:X6} a6=0x{a6:X6} a7=0x{a7:X6} " +
                $"d0=0x{d0:X8} d1=0x{d1:X8} d2=0x{d2:X8} d3=0x{d3:X8} d4=0x{d4:X8} d5=0x{d5:X8} d6=0x{d6:X8} d7=0x{d7:X8} " +
                $"{valueInfo} src0=0x{srcNeg:X6}:{dumpNeg} src1=0x{srcPos:X6}:{dumpPos} " +
                $"a5dump=0x{a5DumpStart:X6}:{dumpA5} a6dump=0x{a6DumpStart:X6}:{dumpA6} " +
                $"pcdump=0x{pcDumpStart:X6}:{pcDump}");

            if (_mbxSrcDumpRemaining != int.MaxValue)
                _mbxSrcDumpRemaining--;
            return true;
        }

        private void RecordMbx68kStat(uint addr, byte value)
        {
            if (!TraceMbx68kStat)
                return;
            _mbx68kStatLastAddr = addr;
            _mbx68kStatLastValue = value;
            _mbx68kStatLastPc = md_m68k.g_reg_PC;
            if (IsZ80Mailbox(addr))
                _mbx68kStatWrites++;
            else if (IsZ80MailboxWide(addr))
                _mbx68kStatWideWrites++;
        }

        internal void FlushMbx68kStat(long frame)
        {
            if (!TraceMbx68kStat)
                return;
            if (_mbx68kStatWrites == 0 && _mbx68kStatWideWrites == 0)
            {
                Console.WriteLine($"[MBX68K] frame={frame} writes=0 wide=0");
                return;
            }
            uint z80Addr = _mbx68kStatLastAddr & 0x1FFF;
            Console.WriteLine($"[MBX68K] frame={frame} writes={_mbx68kStatWrites} wide={_mbx68kStatWideWrites} last=0x{z80Addr:X4} pc=0x{_mbx68kStatLastPc:X6} val=0x{_mbx68kStatLastValue:X2}");
            _mbx68kStatWrites = 0;
            _mbx68kStatWideWrites = 0;
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
            return _z80Reset ? (ushort)0x0000 : (ushort)0x0101;
        }

        private void MaybeDumpZ80WinRangeSnapshot(ushort z80Index, int size, bool blocked)
        {
            if (!TraceZ80WinDump || _z80WinDumpRemaining <= 0)
                return;
            if (!TraceZ80WinDumpStart.HasValue || !TraceZ80WinDumpEnd.HasValue)
                return;
            if (blocked)
                return;
            ushort start = TraceZ80WinDumpStart.Value;
            ushort end = TraceZ80WinDumpEnd.Value;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            bool hit = false;
            bool hitEnd = false;
            for (int i = 0; i < size; i++)
            {
                ushort idx = (ushort)((z80Index + i) & 0x1FFF);
                if (idx >= start && idx <= end)
                    hit = true;
                if (idx == end)
                    hitEnd = true;
            }
            if (!hit)
                return;
            if (TraceZ80WinDumpOnEnd && !hitEnd)
                return;
            string reason = $"z80win pc68k=0x{md_m68k.g_reg_PC:X6}";
            md_main.g_md_z80?.DumpRamRangeWithChecksum(start, end, reason);
            if (_z80WinDumpRemaining != int.MaxValue)
                _z80WinDumpRemaining--;
        }

        private void MaybeLogZ80WinRangeWrite(uint addr, byte value, bool blocked)
        {
            if (_suppressZ80WinRangeByteLog)
                return;
            if (!TraceZ80WinRangeStart.HasValue || !TraceZ80WinRangeEnd.HasValue)
                return;
            if (_z80WinRangeLogRemaining <= 0)
                return;
            ushort start = TraceZ80WinRangeStart.Value;
            ushort end = TraceZ80WinRangeEnd.Value;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            ushort z80Index = (ushort)(addr & 0x1FFF);
            if (z80Index < start || z80Index > end)
                return;
            bool uds = (addr & 1) == 0;
            bool lds = !uds;
            string bytesInfo = $"0x{value:X2}@0x{z80Index:X4}";
            int busReq = _z80BusGranted ? 1 : 0;
            int reset = _z80Reset ? 1 : 0;
            int busAck = (BuildBusAckRead8() & 0x01) != 0 ? 1 : 0;
            string regInfo = TraceZ80WinRegs
                ? $" a0=0x{md_m68k.g_reg_addr[0].l:X6} a1=0x{md_m68k.g_reg_addr[1].l:X6}"
                : string.Empty;
            Console.WriteLine(
                $"[Z80WIN-W] pc68k=0x{md_m68k.g_reg_PC:X6} W8 addr=0x{addr:X6} z80=0x{z80Index:X4} " +
                $"val=0x{value:X2} bytes={bytesInfo} uds={(uds ? 1 : 0)} lds={(lds ? 1 : 0)} " +
                $"busReq={busReq} busAck={busAck} reset={reset} blocked={(blocked ? 1 : 0)}{regInfo}");
            if (_z80WinRangeLogRemaining != int.MaxValue)
                _z80WinRangeLogRemaining--;
        }

        private void MaybeLogZ80WinRangeWrite16(uint addr, ushort value, bool uds, bool lds, bool blocked)
        {
            ushort z80Index = (ushort)(addr & 0x1FFF);
            if (!TraceZ80WinRangeStart.HasValue || !TraceZ80WinRangeEnd.HasValue)
                return;
            if (_z80WinRangeLogRemaining <= 0)
                return;
            ushort start = TraceZ80WinRangeStart.Value;
            ushort end = TraceZ80WinRangeEnd.Value;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            ushort z80Index1 = (ushort)((z80Index + 1) & 0x1FFF);
            if ((z80Index < start || z80Index > end) && (z80Index1 < start || z80Index1 > end))
                return;
            byte hi = (byte)((value >> 8) & 0xFF);
            byte lo = (byte)(value & 0xFF);
            string bytesInfo;
            if (uds && lds)
                bytesInfo = $"0x{hi:X2}@0x{z80Index:X4} 0x{lo:X2}@0x{z80Index1:X4}";
            else if (uds)
                bytesInfo = $"0x{hi:X2}@0x{z80Index:X4} --@0x{z80Index1:X4}";
            else if (lds)
                bytesInfo = $"--@0x{z80Index:X4} 0x{lo:X2}@0x{z80Index1:X4}";
            else
                bytesInfo = $"--@0x{z80Index:X4} --@0x{z80Index1:X4}";
            int busReq = _z80BusGranted ? 1 : 0;
            int reset = _z80Reset ? 1 : 0;
            int busAck = (BuildBusAckRead8() & 0x01) != 0 ? 1 : 0;
            string regInfo = TraceZ80WinRegs
                ? $" a0=0x{md_m68k.g_reg_addr[0].l:X6} a1=0x{md_m68k.g_reg_addr[1].l:X6}"
                : string.Empty;
            Console.WriteLine(
                $"[Z80WIN-W] pc68k=0x{md_m68k.g_reg_PC:X6} W16 addr=0x{addr:X6} z80=0x{z80Index:X4} " +
                $"val=0x{value:X4} bytes={bytesInfo} uds={(uds ? 1 : 0)} lds={(lds ? 1 : 0)} " +
                $"busReq={busReq} busAck={busAck} reset={reset} blocked={(blocked ? 1 : 0)}{regInfo}");
            if (_z80WinRangeLogRemaining != int.MaxValue)
                _z80WinRangeLogRemaining--;
        }

        private void MaybeLogZ80WinRangeWrite32(uint addr, uint value, bool uds, bool lds, bool blocked)
        {
            ushort z80Index = (ushort)(addr & 0x1FFF);
            if (!TraceZ80WinRangeStart.HasValue || !TraceZ80WinRangeEnd.HasValue)
                return;
            if (_z80WinRangeLogRemaining <= 0)
                return;
            ushort start = TraceZ80WinRangeStart.Value;
            ushort end = TraceZ80WinRangeEnd.Value;
            if (start > end)
            {
                ushort tmp = start;
                start = end;
                end = tmp;
            }
            ushort z80Index1 = (ushort)((z80Index + 1) & 0x1FFF);
            ushort z80Index2 = (ushort)((z80Index + 2) & 0x1FFF);
            ushort z80Index3 = (ushort)((z80Index + 3) & 0x1FFF);
            bool hit = (z80Index >= start && z80Index <= end) ||
                       (z80Index1 >= start && z80Index1 <= end) ||
                       (z80Index2 >= start && z80Index2 <= end) ||
                       (z80Index3 >= start && z80Index3 <= end);
            if (!hit)
                return;
            byte b3 = (byte)((value >> 24) & 0xFF);
            byte b2 = (byte)((value >> 16) & 0xFF);
            byte b1 = (byte)((value >> 8) & 0xFF);
            byte b0 = (byte)(value & 0xFF);
            string bytesInfo;
            if (uds && lds)
            {
                bytesInfo =
                    $"0x{b3:X2}@0x{z80Index:X4} 0x{b2:X2}@0x{z80Index1:X4} " +
                    $"0x{b1:X2}@0x{z80Index2:X4} 0x{b0:X2}@0x{z80Index3:X4}";
            }
            else if (uds)
            {
                bytesInfo =
                    $"0x{b3:X2}@0x{z80Index:X4} --@0x{z80Index1:X4} " +
                    $"0x{b1:X2}@0x{z80Index2:X4} --@0x{z80Index3:X4}";
            }
            else if (lds)
            {
                bytesInfo =
                    $"--@0x{z80Index:X4} 0x{b2:X2}@0x{z80Index1:X4} " +
                    $"--@0x{z80Index2:X4} 0x{b0:X2}@0x{z80Index3:X4}";
            }
            else
            {
                bytesInfo =
                    $"--@0x{z80Index:X4} --@0x{z80Index1:X4} " +
                    $"--@0x{z80Index2:X4} --@0x{z80Index3:X4}";
            }
            int busReq = _z80BusGranted ? 1 : 0;
            int reset = _z80Reset ? 1 : 0;
            int busAck = (BuildBusAckRead8() & 0x01) != 0 ? 1 : 0;
            string regInfo = TraceZ80WinRegs
                ? $" a0=0x{md_m68k.g_reg_addr[0].l:X6} a1=0x{md_m68k.g_reg_addr[1].l:X6}"
                : string.Empty;
            Console.WriteLine(
                $"[Z80WIN-W] pc68k=0x{md_m68k.g_reg_PC:X6} W32 addr=0x{addr:X6} z80=0x{z80Index:X4} " +
                $"val=0x{value:X8} bytes={bytesInfo} uds={(uds ? 1 : 0)} lds={(lds ? 1 : 0)} " +
                $"busReq={busReq} busAck={busAck} reset={reset} blocked={(blocked ? 1 : 0)}{regInfo}");
            if (_z80WinRangeLogRemaining != int.MaxValue)
                _z80WinRangeLogRemaining--;
        }

        private void LogZ80BusAckRead(uint addr, ushort value)
        {
            if (!TraceZ80Sig)
                return;
            int busAck = (value & 0x01) != 0 ? 1 : 0;
            int prev = _z80BusAckLogState;
            _z80BusAckLogState = busAck;
            Console.WriteLine(
                $"[Z80BUSACK] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{addr & 0xFFFFFE:X6} -> 0x{value:X4} (bit0=BUSACK; 1=Z80 RUNNING, 0=BUS GRANTED) status={busAck} prev={prev}");
        }

        private void LogZ80BusAckRead8(uint addr, byte value)
        {
            if (!TraceZ80Sig)
                return;
            int busAck = (value & 0x01) != 0 ? 1 : 0;
            if (_z80BusAckLogState8 == busAck)
                return;
            _z80BusAckLogState8 = busAck;
            Console.WriteLine(
                $"[Z80BUSACK8] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{addr & 0xFFFFFE:X6} -> 0x{value:X2} (bit0=BUSACK; 1=Z80 RUNNING, 0=BUS GRANTED)");
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

        private void LogZ80WindowBlocked(string size, uint addr)
        {
            if (!TraceZ80Win || _z80WinReadBlockLogRemaining <= 0)
                return;
            _z80WinReadBlockLogRemaining--;
            int busReq = _z80BusGranted ? 1 : 0;
            int reset = _z80Reset ? 1 : 0;
            Console.WriteLine($"[Z80WIN][BLOCK] {size} addr=0x{addr:X6} busReq={busReq} reset={reset}");
        }

        private void RecordZ80WinStat(uint addr, uint value, int size, bool blocked)
        {
            if (!TraceZ80WinStat)
                return;
            _z80WinStatWrites++;
            if (blocked)
                _z80WinStatBlocked++;
            _z80WinStatLastAddr = addr;
            _z80WinStatLastPc = md_m68k.g_reg_PC;
            _z80WinStatLastValue = value;
            _z80WinStatLastSize = size;
        }

        private void RecordZ80WinHist(uint addr)
        {
            if (!TraceZ80WinHist)
                return;
            int bin = (int)((addr >> 8) & 0xFF);
            _z80WinHist[bin]++;
            _z80WinHistTotal++;
        }

        private void RecordMbx68kStatCompat(uint addr, byte value)
        {
            if (!TraceMbx68kStat && !MbxSyncTrace.IsEnabled)
                return;
            if (!IsZ80Mailbox(addr) && !IsZ80MailboxWide(addr))
                return;
            RecordMbx68kStat(addr, value);
            MbxSyncTrace.Record68kWrite(addr, value);
        }

        internal void FlushZ80WinStat(long frame)
        {
            if (!TraceZ80WinStat)
                return;
            int busReq = _z80BusGranted ? 1 : 0;
            int reset = _z80Reset ? 1 : 0;
            Console.WriteLine(
                $"[Z80WIN-STAT] frame={frame} writes={_z80WinStatWrites} blocked={_z80WinStatBlocked} " +
                $"last=0x{_z80WinStatLastAddr:X6} pc=0x{_z80WinStatLastPc:X6} size={_z80WinStatLastSize} " +
                $"val=0x{_z80WinStatLastValue:X8} busReq={busReq} reset={reset}");
            _z80WinStatWrites = 0;
            _z80WinStatBlocked = 0;
            _z80WinStatLastAddr = 0;
            _z80WinStatLastPc = 0;
            _z80WinStatLastValue = 0;
            _z80WinStatLastSize = 0;
        }

        internal void FlushZ80WinHist(long frame)
        {
            if (!TraceZ80WinHist)
                return;
            if (_z80WinHistTotal == 0)
            {
                Console.WriteLine($"[Z80WIN-HIST] frame={frame} total=0");
                return;
            }

            int limit = TraceZ80WinHistLimit > 0 ? TraceZ80WinHistLimit : 1;
            Span<int> topCount = stackalloc int[limit];
            Span<int> topBin = stackalloc int[limit];
            for (int i = 0; i < limit; i++)
                topBin[i] = -1;

            for (int bin = 0; bin < 256; bin++)
            {
                int count = _z80WinHist[bin];
                if (count < TraceZ80WinHistMin)
                    continue;
                for (int i = 0; i < limit; i++)
                {
                    if (topBin[i] == bin)
                    {
                        topCount[i] = count;
                        goto NextBin;
                    }
                }
                for (int i = 0; i < limit; i++)
                {
                    if (count > topCount[i])
                    {
                        for (int j = limit - 1; j > i; j--)
                        {
                            topCount[j] = topCount[j - 1];
                            topBin[j] = topBin[j - 1];
                        }
                        topCount[i] = count;
                        topBin[i] = bin;
                        break;
                    }
                }
NextBin:;
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < limit; i++)
            {
                if (topBin[i] < 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append("0x");
                sb.Append(topBin[i].ToString("X2"));
                sb.Append(':');
                sb.Append(topCount[i]);
            }

            Console.WriteLine($"[Z80WIN-HIST] frame={frame} total={_z80WinHistTotal} bins={sb}");
            Array.Clear(_z80WinHist, 0, _z80WinHist.Length);
            _z80WinHistTotal = 0;
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
            uint low = addr & 0x1FFF;
            if (low != 0x1B88 && low != 0x1B89)
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
