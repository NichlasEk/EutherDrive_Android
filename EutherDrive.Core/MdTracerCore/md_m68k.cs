using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Globalization;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static bool _bootTraceEnabled = true;
        private static int _bootTraceRemaining = 200;
        private static int _bootTraceProbeRemaining = 16;
        private static int _btstLogRemaining = 16;
        private static int _bneLogRemaining = 32;
        private static int _d1LogRemaining = 64;
        private static uint _d1LogLastPc;
        private static int _pc466LogRemaining = 32;
        private static int _intLogRemaining = 32;
        private static int _illegalOpLogRemaining = 16;
        private static readonly bool TraceMdStall =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MD_STALL"), "1", StringComparison.Ordinal);
        private static readonly Stopwatch _stallStopwatch = Stopwatch.StartNew();
        private const long StallThreshold = 2000;
        private static long _stallLastReportMs;
        private struct StallAccess { public uint Address; public byte Size; public bool IsWrite; public uint Value; }
        private const int StallAccessBufferSize = 64;
        private static readonly StallAccess[] _stallAccessBuffer = new StallAccess[StallAccessBufferSize];
        private static int _stallAccessIndex;
        private static int _stallAccessCount;
        private static long _countC00004;
        private static long _countC00000;
        private static long _countC00008;
        private static long _countA11100;
        private static long _countA11200;
        private static long _countA10003;
        private static long _countA10005;
        private static long _countA04000;
        private static ushort _lastVdpStatusRead;
        private static readonly bool TracePcSample =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC_SAMPLE"), "1", StringComparison.Ordinal);
        private static readonly Stopwatch _pcSampleStopwatch = Stopwatch.StartNew();
        private static long _pcSampleLastMs;
        private static bool _pcWatchEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC"), "1", StringComparison.Ordinal);
        private static readonly uint PcWatchStart = ParseWatchAddr("EUTHERDRIVE_TRACE_PCWATCH_START") ?? 0x000320;
        private static readonly uint PcWatchEnd = ParseWatchAddr("EUTHERDRIVE_TRACE_PCWATCH_END") ?? 0x000340;
        private static bool _pcWatchDumped;
        private static bool _pcWatchInRange;
        private static bool _pcWatchExitLogged;
        private static readonly Stopwatch _pcWatchStopwatch = Stopwatch.StartNew();
        private static long _pcWatchLastProgressMs;
        private const long PcWatchProgressIntervalMs = 50;
        private const uint ChecksumLoopStart = 0x000320;
        private const uint ChecksumLoopEnd = 0x000340;
        private static bool _stallWatchEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC"), "1", StringComparison.Ordinal);
        private static bool _stallWatchDumped;
        private const uint StallWatchStart = 0x079F00;
        private const uint StallWatchEnd = 0x079F30;
        private static bool _stallWatchBootEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC"), "1", StringComparison.Ordinal);
        private static bool _stallWatchBootDumped;
        private const uint StallWatchBootStart = 0x000480;
        private const uint StallWatchBootEnd = 0x0004A0;
        private static bool _stallWatchMidEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC"), "1", StringComparison.Ordinal);
        private static bool _stallWatchMidDumped;
        private const uint StallWatchMidStart = 0x06A390;
        private const uint StallWatchMidEnd = 0x06A3B0;
        private static bool _stallWatchLowEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC"), "1", StringComparison.Ordinal);
        private static bool _stallWatchLowDumped;
        private const uint StallWatchLowStart = 0x000710;
        private const uint StallWatchLowEnd = 0x000730;
        private static readonly uint? TraceMemWatchAddr = ParseWatchAddr("EUTHERDRIVE_TRACE_MEM_WATCH");
        private static readonly int MemWatchLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_MEM_WATCH_LIMIT");
        private static readonly bool MemWatchAll =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MEM_WATCH_ALL"), "1", StringComparison.Ordinal);
        private static int _memWatchLogRemaining = MemWatchLimit;
        private static bool _memWatchLastValid;
        private static uint _memWatchLastValue;
        private static byte _memWatchLastSize;
        private uint _stallLastPc;
        private uint _stallSecondLastPc;
        private uint _stallOscCurrent;
        private uint _stallOscPartner;
        private long _stallSamePcCount;
        private long _stallOscCount;
        private static readonly bool TraceDbra =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DBRA"), "1", StringComparison.Ordinal);
        private static long _lastDbraLogFrame = -1;
        private static bool _checksumDoneLogged;
        // ... (alla dina fält osv som innan)

        // --- Headless trace helpers (ersätter Form_Code_Trace) ---
        [Conditional("DEBUG")]
        internal static void TraceCpu(uint pc)
        {
            Debug.WriteLine($"[m68k] PC={pc:X6}");
        }

        [Conditional("DEBUG")]
        internal static void TracePush(string kind, uint vector, uint start, uint pc, uint sp)
        {
            Debug.WriteLine($"[m68k] {kind} vec={vector:X4} start={start:X6} pc={pc:X6} sp={sp:X8}");
        }
        // ----------------------------------------------------------

        public md_m68k()
        {
            initialize();
            initialize2();
        }

        public void run(int in_clock)
        {
            g_clock_total += in_clock;
            int iter = 0;
            while (g_clock_now < g_clock_total)
            {
                if (++iter > 50000)
                {
                    if (_intLogRemaining > 0)
                    {
                        _intLogRemaining--;
                        Console.WriteLine($"[m68k] watchdog break pc=0x{g_reg_PC:X6} clock_now={g_clock_now} clock_total={g_clock_total}");
                    }
                    g_clock_total = g_clock_now;
                    break;
                }

                // md_main.g_form_code_trace.CPU_Trace(g_reg_PC);
                TraceCpu(g_reg_PC); // headless

                interrupt_chk();
                g_clock = md_main.g_md_vdp.dma_status_update();
                if (g_clock == 0)
                {
                    uint pcBefore = g_reg_PC;
                    g_opcode = read16(g_reg_PC);
                    g_op  = (byte)(g_opcode >> 12);
                    g_op1 = (byte)((g_opcode >> 9) & 0x07);
                    g_op2 = (byte)((g_opcode >> 6) & 0x07);
                    g_op3 = (byte)((g_opcode >> 3) & 0x07);
                    g_op4 = (byte)(g_opcode & 0x07);

                    MaybeLogPcSample(g_reg_PC, g_opcode);

                    if (g_68k_stop) { g_clock_now = g_clock_total; break; }

                    if (g_opcode == 0x33FC)
                    {
                        ushort imm = read16(g_reg_PC + 2);
                        uint addr = read32(g_reg_PC + 4);
                        md_main.g_md_bus.write16(addr, imm);
                        g_reg_PC += 8;
                        g_clock = 12;
                        g_status_N = (imm & 0x8000) != 0;
                        g_status_Z = imm == 0;
                        g_status_V = false;
                        g_status_C = false;
                    }
                    else if (g_opcode == 0x33D8)
                    {
                        uint src = g_reg_addr[0].l;
                        ushort val = md_main.g_md_bus.read16(src);
                        g_reg_addr[0].l = src + 2;
                        uint addr = read32(g_reg_PC + 2);
                        md_main.g_md_bus.write16(addr, val);
                        g_reg_PC += 6;
                        g_clock = 12;
                        g_status_N = (val & 0x8000) != 0;
                        g_status_Z = val == 0;
                        g_status_V = false;
                        g_status_C = false;
                    }
                    else
                    {
                    if (_bootTraceEnabled && _bootTraceRemaining == 200)
                        Console.WriteLine("[m68k boot] trace start");

                    if (_bootTraceEnabled && _bootTraceRemaining > 0)
                    {
                        Console.WriteLine($"[m68k boot] PC=0x{g_reg_PC:X6} OP=0x{g_opcode:X4}");
                        if (g_opcode == 0x0111 && _bootTraceProbeRemaining > 0)
                        {
                            uint a1 = g_reg_addr[1].l;
                            byte val = md_main.g_md_bus != null ? md_main.g_md_bus.read8(a1) : read8(a1);
                            MdLog.WriteLine($"[m68k boot] OP=0x0111 probe A1=0x{a1:X6} -> 0x{val:X2}");
                            _bootTraceProbeRemaining--;
                        }
                        _bootTraceRemaining--;
                        if (_bootTraceRemaining == 0)
                            _bootTraceEnabled = false;
                    }

                    if ((g_reg_PC == 0x000466 || g_reg_PC == 0x000464 || g_reg_PC == 0x000468) && _pc466LogRemaining > 0)
                    {
                        _pc466LogRemaining--;
                        ushort op0 = g_opcode;
                        ushort op1 = read16(g_reg_PC + 2);
                        ushort op2 = read16(g_reg_PC + 4);
                        MdLog.WriteLine($"[m68k] PC=0x{g_reg_PC:X6} OP=0x{op0:X4} N1=0x{op1:X4} N2=0x{op2:X4} SR=0x{g_reg_SR:X4} SP=0x{g_reg_addr[7].l:X8}");
                    }

                    MaybeLogPcWatch(g_reg_PC, g_opcode);
                    MaybeLogStallWatch(g_reg_PC, g_opcode);
                    MaybeLogStallBootWatch(g_reg_PC, g_opcode);
                    MaybeLogStallMidWatch(g_reg_PC, g_opcode);
                    MaybeLogStallLowWatch(g_reg_PC, g_opcode);

                    var opinfo = g_opcode_info != null ? g_opcode_info[g_opcode] : null;
                    if (opinfo?.opcode == null)
                    {
                        if (_illegalOpLogRemaining > 0)
                        {
                            _illegalOpLogRemaining--;
                            Console.WriteLine($"[m68k] missing opcode handler op=0x{g_opcode:X4} pc=0x{g_reg_PC:X6}");
                        }
                        g_reg_PC += 2;
                        g_clock = 4;
                    }
                    else
                    {
                        opinfo.opcode();
                    }
                    if (TraceMdStall)
                        CheckForStall(pcBefore);
                    if (g_clock == 0)
                        g_clock = 4;
                    }
                }
                g_clock_now += g_clock;
            }
        }

        private void interrupt_chk()
        {
            if (g_interrupt_H_req && (g_status_interrupt_mask < 4)
                && (md_main.g_md_vdp.g_vdp_reg_0_4_hinterrupt == 1))
            {
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(0x0070);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    MdLog.WriteLine($"[m68k int] HINT vec=0x0070 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...HINT...);
                TracePush("HINT", 0x0070, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (4 << 8));
                g_interrupt_H_req = false;
                g_interrupt_H_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_V_req && (g_status_interrupt_mask < 6)
                && (md_main.g_md_vdp.g_vdp_reg_1_5_vinterrupt == 1)
                && !g_interrupt_H_act)
            {
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(0x0078);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    MdLog.WriteLine($"[m68k int] VINT vec=0x0078 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...VINT...);
                TracePush("VINT", 0x0078, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (6 << 8));
                g_interrupt_V_req = false;
                g_interrupt_V_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_EXT_req && (g_status_interrupt_mask < 2))
            {
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(0x0068);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    MdLog.WriteLine($"[m68k int] EXT vec=0x0068 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...EXT...);
                TracePush("EXT", 0x0068, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (2 << 8));
                g_interrupt_EXT_req = false;
                g_interrupt_EXT_act = true;
                g_68k_stop = false;
            }
        }

        private static void MaybeLogPcSample(uint pc, ushort opcode)
        {
            if (!TracePcSample)
                return;

            long now = _pcSampleStopwatch.ElapsedMilliseconds;
            if (now - _pcSampleLastMs < 1000)
                return;
            _pcSampleLastMs = now;

            MdLog.WriteLine($"[PCSAMPLE] t={now}ms pc=0x{pc:X6} op=0x{opcode:X4} sr=0x{g_reg_SR:X4} d0=0x{g_reg_data[0].l:X8} d1=0x{g_reg_data[1].l:X8} a7=0x{g_reg_addr[7].l:X8}");
        }

        internal static void RecordVdpStatusRead(ushort status)
        {
            if (!TraceMdStall)
                return;
            _lastVdpStatusRead = status;
        }

        private void CheckForStall(uint pcStart)
        {
            _stallSamePcCount = (pcStart == _stallLastPc) ? _stallSamePcCount + 1 : 1;

            if (pcStart == _stallOscPartner && _stallLastPc == _stallOscCurrent)
            {
                _stallOscCount++;
            }
            else
            {
                _stallOscCount = 0;
                _stallOscCurrent = _stallLastPc;
                _stallOscPartner = pcStart;
            }

            _stallSecondLastPc = _stallLastPc;
            _stallLastPc = pcStart;

            if (_stallSamePcCount > StallThreshold)
            {
                ReportStall(pcStart, $"repeat count={_stallSamePcCount}");
            }
            else if (_stallOscCount > StallThreshold)
            {
                ReportStall(pcStart, $"oscillation count={_stallOscCount}");
            }
        }

        private void ReportStall(uint pcStart, string reason)
        {
            long now = _stallStopwatch.ElapsedMilliseconds;
            if (now - _stallLastReportMs < 1000)
                return;
            _stallLastReportMs = now;

            ushort sr = g_reg_SR;
            uint sp = g_reg_addr[7].l;
            var vdp = md_main.g_md_vdp;
            long frame = vdp?.FrameCounter ?? 0;
            int scanline = vdp?.g_scanline ?? 0;

            Console.WriteLine($"[STALL] reason={reason} pc=0x{pcStart:X6} sr=0x{sr:X4} sp=0x{sp:X8} frame={frame} scanline={scanline}");
            Console.WriteLine($"[STALL] bytes={FormatOpcodeBytes(pcStart)} opcode=0x{PeekOpcode(pcStart):X4}");
            Console.WriteLine($"[STALL] ioCounts: C00004={_countC00004} C00008={_countC00008} C00000={_countC00000} A11100={_countA11100} A11200={_countA11200} A10003={_countA10003} A10005={_countA10005} YM={_countA04000}");

            ushort status = _lastVdpStatusRead;
            Console.WriteLine($"[STALL] last VDP status=0x{status:X4} mask=0x{md_vdp.VDP_STATUS_VBLANK_MASK:X4} vblankFlag={((status & md_vdp.VDP_STATUS_VBLANK_MASK) != 0 ? 1 : 0)}");

            if (_stallAccessCount > 0)
            {
                Console.WriteLine(FormatAccessLog());
            }

            _countC00004 = 0;
            _countC00008 = 0;
            _countC00000 = 0;
            _countA11100 = 0;
            _countA11200 = 0;
            _countA10003 = 0;
            _countA10005 = 0;
            _countA04000 = 0;
            _stallAccessCount = 0;
        }

        private static string FormatOpcodeBytes(uint pcStart)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;
            uint addr = NormalizeAddr(pcStart);
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                int idx = (int)(((ulong)addr + (ulong)i) % MemorySize);
                sb.AppendFormat("{0:X2} ", mem[idx]);
            }
            return sb.ToString().TrimEnd();
        }

        private static ushort PeekOpcode(uint pcStart)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;
            uint addr = NormalizeAddr(pcStart);
            int idx = (int)(addr % MemorySize);
            byte hi = mem[idx];
            byte lo = mem[(idx + 1) % MemorySize];
            return (ushort)((hi << 8) | lo);
        }

        private static string FormatAccessLog()
        {
            var sb = new StringBuilder("[STALL] recent accesses:");
            int count = Math.Min(_stallAccessCount, StallAccessBufferSize);
            for (int i = 0; i < count; i++)
            {
                int idx = (_stallAccessIndex - count + i + StallAccessBufferSize) % StallAccessBufferSize;
                var entry = _stallAccessBuffer[idx];
                sb.AppendFormat(" {0}{1}@0x{2:X6}=0x{3}", entry.IsWrite ? 'W' : 'R', entry.Size, entry.Address, FormatAccessValue(entry.Value, entry.Size));
            }
            return sb.ToString();
        }

        private static string FormatRecentAccesses(int count)
        {
            if (_stallAccessCount == 0)
                return "accesses=none";
            int use = Math.Min(count, Math.Min(_stallAccessCount, StallAccessBufferSize));
            var sb = new StringBuilder("accesses=");
            for (int i = 0; i < use; i++)
            {
                int idx = (_stallAccessIndex - use + i + StallAccessBufferSize) % StallAccessBufferSize;
                var entry = _stallAccessBuffer[idx];
                sb.AppendFormat("{0}{1}@0x{2:X6}=0x{3}", entry.IsWrite ? 'W' : 'R', entry.Size, entry.Address, FormatAccessValue(entry.Value, entry.Size));
                if (i != use - 1)
                    sb.Append(',');
            }
            return sb.ToString();
        }

        private static string FormatAccessValue(uint value, byte size)
        {
            return size switch
            {
                1 => value.ToString("X2"),
                2 => value.ToString("X4"),
                4 => value.ToString("X8"),
                _ => value.ToString("X")
            };
        }

        private static void RecordMemoryAccess(uint address, byte size, bool write, uint value)
        {
            if (TraceMemWatchAddr.HasValue && address == TraceMemWatchAddr.Value && _memWatchLogRemaining > 0)
            {
                bool shouldLog = write || MemWatchAll || !_memWatchLastValid ||
                    _memWatchLastSize != size || _memWatchLastValue != value;
                if (shouldLog)
                {
                    char rw = write ? 'W' : 'R';
                    Console.WriteLine($"[MEMWATCH] {rw}{size} pc=0x{g_reg_PC:X6} addr=0x{address:X6} val=0x{FormatAccessValue(value, size)}");
                    _memWatchLastValid = true;
                    _memWatchLastSize = size;
                    _memWatchLastValue = value;
                    if (_memWatchLogRemaining != int.MaxValue)
                        _memWatchLogRemaining--;
                }
            }
            if (!TraceMdStall && !_pcWatchEnabled)
                return;
            _stallAccessBuffer[_stallAccessIndex] = new StallAccess { Address = address, Size = size, IsWrite = write, Value = value };
            _stallAccessIndex = (_stallAccessIndex + 1) % StallAccessBufferSize;
            if (_stallAccessCount < StallAccessBufferSize)
                _stallAccessCount++;
            UpdateIoCounters(address);
        }

        internal static void RecordBusAccess(uint address, byte size, bool write, uint value)
        {
            RecordMemoryAccess(address, size, write, value);
        }

        private static void UpdateIoCounters(uint address)
        {
            switch (address)
            {
                case 0x00C00004:
                    _countC00004++;
                    break;
                case 0x00C00008:
                    _countC00008++;
                    break;
                case 0x00C00000:
                    _countC00000++;
                    break;
                case 0x00A11100:
                    _countA11100++;
                    break;
                case 0x00A11200:
                    _countA11200++;
                    break;
                case 0x00A10003:
                    _countA10003++;
                    break;
                case 0x00A10005:
                    _countA10005++;
                    break;
                default:
                    if (address >= 0x00A04000 && address < 0x00A04200)
                        _countA04000++;
                    break;
            }
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

        private static int ParseWatchLimit(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return 256;
            raw = raw.Trim();
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return 256;
            if (value <= 0)
                return int.MaxValue;
            return value;
        }

        private static void MaybeLogPcWatch(uint pc, ushort opcode)
        {
            if (!_pcWatchEnabled)
                return;
            if (pc < PcWatchStart || pc > PcWatchEnd)
            {
                if (_pcWatchInRange && !_pcWatchExitLogged)
                {
                    _pcWatchInRange = false;
                    _pcWatchExitLogged = true;
                    Console.WriteLine(
                        $"[PCWATCH] exit pc=0x{pc:X6} A0=0x{g_reg_addr[0].l:X8} D0=0x{g_reg_data[0].l:X8} C={(g_status_C ? 1 : 0)} Z={(g_status_Z ? 1 : 0)}");
                    DumpPcWindowRange(pc, 16, 64);
                }
                return;
            }
            _pcWatchInRange = true;

            if (!_pcWatchDumped)
            {
                _pcWatchDumped = true;
                DumpPcWindow(pc);
                Console.WriteLine($"[PCWATCH] {FormatRecentAccesses(8)}");
                return;
            }

            MaybeLogChecksumProgress(pc, opcode);
        }

        private static void MaybeLogChecksumProgress(uint pc, ushort opcode)
        {
            if (pc < ChecksumLoopStart || pc > ChecksumLoopEnd)
                return;

            long now = _pcWatchStopwatch.ElapsedMilliseconds;
            if (now - _pcWatchLastProgressMs < PcWatchProgressIntervalMs)
                return;
            _pcWatchLastProgressMs = now;

            string lastRead = TryGetLastReadAccess(out uint addr, out byte size, out uint value)
                ? $"lastRead=0x{addr:X6} size={size} val=0x{value:X8}"
                : "lastRead=none";

            Console.WriteLine(
                $"[CHK] PC=0x{pc:X6} OP=0x{opcode:X4} A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8} D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} {lastRead}");

            if (!_checksumDoneLogged && g_reg_addr[0].l >= g_reg_data[0].l)
            {
                _checksumDoneLogged = true;
                ushort expected = PeekMem16(0x0001A4);
                Console.WriteLine($"[CHK] done A0=0x{g_reg_addr[0].l:X8} D0=0x{g_reg_data[0].l:X8} sum=0x{g_reg_data[1].l:X8} expected=0x{expected:X4}");
            }
        }

        private static bool TryGetLastReadAccess(out uint address, out byte size, out uint value)
        {
            int count = Math.Min(_stallAccessCount, StallAccessBufferSize);
            for (int i = 0; i < count; i++)
            {
                int idx = (_stallAccessIndex - 1 - i + StallAccessBufferSize) % StallAccessBufferSize;
                var entry = _stallAccessBuffer[idx];
                if (!entry.IsWrite)
                {
                    address = entry.Address;
                    size = entry.Size;
                    value = entry.Value;
                    return true;
                }
            }
            address = 0;
            size = 0;
            value = 0;
            return false;
        }
        private static void DumpPcWindow(uint pc)
        {
            DumpPcWindowRange(pc, 16, 32);
        }

        private static void DumpPcWindowRange(uint pc, uint before, uint length)
        {
            InitMemoryIfNeeded();
            uint start = pc >= before ? pc - before : 0u;
            var bytes = new StringBuilder();
            bytes.AppendFormat("[PCWATCH] bytes @0x{0:X6}:", start);
            for (uint i = 0; i < length; i++)
            {
                bytes.AppendFormat(" {0:X2}", PeekMem8(start + i));
            }
            Console.WriteLine(bytes.ToString());

            var disasm = new StringBuilder();
            disasm.AppendFormat("[PCWATCH] disasm @0x{0:X6}:", pc);
            uint addr = pc;
            int remaining = (int)length;
            int instrCount = 0;
            while (remaining > 0 && instrCount < 8)
            {
                ushort op = PeekMem16(addr);
                var info = g_opcode_info != null ? g_opcode_info[op] : null;
                int len = info?.opleng ?? 2;
                if (len <= 0)
                    len = 2;

                disasm.AppendFormat(" 0x{0:X6}:", addr);
                disasm.AppendFormat(" {0:X4}", op);
                for (int i = 2; i < len; i += 2)
                {
                    disasm.AppendFormat(" {0:X4}", PeekMem16(addr + (uint)i));
                }
                string name = info?.opname_out;
                if (string.IsNullOrEmpty(name))
                    name = "op";
                disasm.Append(' ');
                disasm.Append(name);

                addr += (uint)len;
                remaining -= len;
                instrCount++;
            }
            Console.WriteLine(disasm.ToString());
        }

        private static byte PeekMem8(uint address)
        {
            InitMemoryIfNeeded();
            var mem = g_memory!;
            uint addr = NormalizeAddr(address);
            return mem[addr];
        }

        private static ushort PeekMem16(uint address)
        {
            byte hi = PeekMem8(address);
            byte lo = PeekMem8(address + 1);
            return (ushort)((hi << 8) | lo);
        }

        private static void MaybeLogStallWatch(uint pc, ushort opcode)
        {
            if (!_stallWatchEnabled)
                return;
            if (pc < StallWatchStart || pc > StallWatchEnd)
                return;
            if (_stallWatchDumped)
                return;

            _stallWatchDumped = true;

            DumpPcWindowRange(pc, 16, 64);
            Console.WriteLine(
                $"[PCWATCH2] PC=0x{pc:X6} OP=0x{opcode:X4} SR=0x{g_reg_SR:X4} A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8} A2=0x{g_reg_addr[2].l:X8} D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} {FormatRecentAccesses(16)}");
            _stallWatchEnabled = false;
        }

        private static void MaybeLogStallBootWatch(uint pc, ushort opcode)
        {
            if (!_stallWatchBootEnabled)
                return;
            if (pc < StallWatchBootStart || pc > StallWatchBootEnd)
                return;
            if (_stallWatchBootDumped)
                return;

            _stallWatchBootDumped = true;
            DumpPcWindowRange(pc, 16, 64);
            Console.WriteLine(
                $"[PCWATCH2] PC=0x{pc:X6} OP=0x{opcode:X4} SR=0x{g_reg_SR:X4} A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8} A2=0x{g_reg_addr[2].l:X8} D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} {FormatRecentAccesses(16)}");
            _stallWatchBootEnabled = false;
        }

        private static void MaybeLogStallMidWatch(uint pc, ushort opcode)
        {
            if (!_stallWatchMidEnabled)
                return;
            if (pc < StallWatchMidStart || pc > StallWatchMidEnd)
                return;
            if (_stallWatchMidDumped)
                return;

            _stallWatchMidDumped = true;
            DumpPcWindowRange(pc, 16, 64);
            Console.WriteLine(
                $"[PCWATCH2] PC=0x{pc:X6} OP=0x{opcode:X4} SR=0x{g_reg_SR:X4} A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8} A2=0x{g_reg_addr[2].l:X8} D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} {FormatRecentAccesses(16)}");
            _stallWatchMidEnabled = false;
        }

        private static void MaybeLogStallLowWatch(uint pc, ushort opcode)
        {
            if (!_stallWatchLowEnabled)
                return;
            if (pc < StallWatchLowStart || pc > StallWatchLowEnd)
                return;
            if (_stallWatchLowDumped)
                return;

            _stallWatchLowDumped = true;
            DumpPcWindowRange(pc, 16, 64);
            Console.WriteLine(
                $"[PCWATCH2] PC=0x{pc:X6} OP=0x{opcode:X4} SR=0x{g_reg_SR:X4} A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8} A2=0x{g_reg_addr[2].l:X8} D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} {FormatRecentAccesses(16)}");
            _stallWatchLowEnabled = false;
        }

        // resten av filen (traceout/logout/logout2 etc) kan vara kvar oförändrat
    }
}
