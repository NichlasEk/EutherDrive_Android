using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceConsoleEnabled =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CONSOLE"), "0", StringComparison.Ordinal)
            && !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_AUDIO_RAW_TIMING"), "1", StringComparison.Ordinal);
        private static readonly bool TraceM68kBoot =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_M68K_BOOT"), "1", StringComparison.Ordinal)
            && TraceConsoleEnabled;
        private static readonly int TraceM68kBootLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_M68K_BOOT_LIMIT", 200);
        private static readonly int TraceM68kBootProbeLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_M68K_BOOT_PROBE_LIMIT", 16);
        private static bool _bootTraceEnabled = TraceM68kBoot;
        private static int _bootTraceRemaining = TraceM68kBoot ? TraceM68kBootLimit : 0;
        private static int _bootTraceProbeRemaining = TraceM68kBoot ? TraceM68kBootProbeLimit : 0;
        private static int _btstLogRemaining = 16;
        private static int _bneLogRemaining = 32;
        private static int _d1LogRemaining = 64;
        private static uint _d1LogLastPc;
        private static int _pc466LogRemaining = 32;
        private static int _intLogRemaining = 32;
        private static int _illegalOpLogRemaining = 16;
        private static int _illegalVectorLogRemaining = 16;
        private static int _headerPcLogRemaining = 16;
        private static int _spWatchRemaining = 32;
        private static int _rtsBadLogRemaining = 16;
        private static int _spOverflowLogRemaining = 16;
        private static int _spZeroLogRemaining = 16;
        private static int _spZeroAsyncLogRemaining = 16;
        private static readonly bool TraceA7Write =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_A7_WRITE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceOp30FC =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OPCODE_30FC"), "1", StringComparison.Ordinal);
        private static int _a7WriteLogRemaining = TraceA7Write ? 128 : 0;
        private static uint _lastSpBeforeOp;
        private static uint _lastSpAfterOp;
        private static uint _lastPcBeforeOp;
        private static ushort _lastOpBeforeOp;
        private static uint _lastSpObserved;
        private static int _swapLogRemaining = 16;
        private static uint _lastPcAfter;
        private static ushort _lastOpAfter;
        private static int _pcZeroLogRemaining = 16;
        private static readonly bool TraceMdStall =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MD_STALL"), "1", StringComparison.Ordinal);
        private static readonly bool TraceOp4A38 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OP4A38"), "1", StringComparison.Ordinal)
            && TraceConsoleEnabled;
        private static readonly bool TraceM68kInt =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_M68K_INT"), "1", StringComparison.Ordinal)
            && TraceConsoleEnabled;
        private static readonly bool TraceM68kIntPending =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_M68K_INT_PENDING"), "1", StringComparison.Ordinal)
            && TraceConsoleEnabled;
        private static readonly int TraceM68kIntLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_M68K_INT_LIMIT", 64);
        private static int _traceM68kIntRemaining = TraceM68kInt ? TraceM68kIntLimit : 0;
        private static int _traceM68kIntPendingRemaining = TraceM68kIntPending ? TraceM68kIntLimit : 0;
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
        private static readonly bool _madouTraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MADOU"), "1", StringComparison.Ordinal);
        private static readonly bool _madouFullTrace =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MADOU_FULL"), "1", StringComparison.Ordinal);
        private static readonly bool _madouRomTrace =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MADOU_ROM_WORDS"), "1", StringComparison.Ordinal);
        private static readonly bool _madouRotateTrace =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MADOU_ROTATE"), "1", StringComparison.Ordinal);
        private static readonly bool _madouBootTrace =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MADOU_BOOT"), "1", StringComparison.Ordinal);
        internal static readonly bool FixMovemPredec =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FIX_MOVEM_PREDEC"), "1", StringComparison.Ordinal);
        internal static readonly bool TraceMovemPredec =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MOVEM_PREDEC"), "1", StringComparison.Ordinal);
        internal static readonly bool AllowTasWrites =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_ALLOW_TAS_WRITES"), "1", StringComparison.Ordinal);
        internal static readonly bool FixBranchBaseAfterExtension =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FIX_BRANCH_BASE_AFTER_EXT"), "1", StringComparison.Ordinal);
        internal static int TraceMovemPredecRemaining = 8;
        internal static readonly List<(uint Start, uint End)> FixMovemPredecRanges =
            ParseWatchRangeList("EUTHERDRIVE_FIX_MOVEM_PREDEC_PC_RANGE");
        internal static readonly bool TraceOpcode102A =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OPCODE_102A"), "1", StringComparison.Ordinal);
        internal static readonly bool TraceOpcodeB01B =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OPCODE_B01B"), "1", StringComparison.Ordinal);
        internal static readonly bool TraceOpcode544A =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OPCODE_544A"), "1", StringComparison.Ordinal);
        internal static readonly bool TraceOpcode20C0 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OPCODE_20C0"), "1", StringComparison.Ordinal);
        internal static readonly bool TraceOpcode51C9 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_OPCODE_51C9"), "1", StringComparison.Ordinal);
        internal static readonly List<(uint Start, uint End)> TraceOpcodePcRanges =
            ParseWatchRangeList("EUTHERDRIVE_TRACE_OPCODE_PC_RANGE");
        private static readonly int TraceOpcodeLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_OPCODE_LIMIT");
        private static int _traceOpcodeRemaining = TraceOpcodeLimit;

        internal static bool ShouldFixMovemPredec(uint pc)
        {
            if (FixMovemPredecRanges.Count == 0)
                return true;
            foreach ((uint start, uint end) in FixMovemPredecRanges)
            {
                if (pc >= start && pc <= end)
                    return true;
            }
            return false;
        }

        internal static bool ShouldTraceOpcode(bool enabled, uint pc)
        {
            if (!enabled)
                return false;
            if (_traceOpcodeRemaining <= 0)
                return false;
            if (TraceOpcodePcRanges.Count > 0)
            {
                bool inRange = false;
                foreach ((uint start, uint end) in TraceOpcodePcRanges)
                {
                    if (pc >= start && pc <= end)
                    {
                        inRange = true;
                        break;
                    }
                }
                if (!inRange)
                    return false;
            }
            _traceOpcodeRemaining--;
            return true;
        }
        private static readonly uint PcWatchStart = ParseWatchAddr("EUTHERDRIVE_TRACE_PCWATCH_START") ?? 0x000320;
        private static readonly uint PcWatchEnd = ParseWatchAddr("EUTHERDRIVE_TRACE_PCWATCH_END") ?? 0x000340;
        
        // Special Stage debugging (generic)
        private static bool _specialStageDebug =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DEBUG_SPECIAL_STAGE"), "1", StringComparison.Ordinal);
        private static long _lastSpecialStageFrameLogged = -1000;
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
        private static readonly HashSet<uint> TraceMemWatchAddrs =
            ParseWatchAddrList("EUTHERDRIVE_TRACE_MEM_WATCH_LIST");
        private static readonly int MemWatchLimit = ParseWatchLimit("EUTHERDRIVE_TRACE_MEM_WATCH_LIMIT");
        private static readonly bool MemWatchAll =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_MEM_WATCH_ALL"), "1", StringComparison.Ordinal);
        private static readonly HashSet<uint> TracePcTapAddrs =
            ParseWatchAddrList("EUTHERDRIVE_TRACE_PC_TAP_LIST");
        private static readonly List<(uint Start, uint End)> TracePcTapPeekRanges =
            ParseWatchRangeList("EUTHERDRIVE_TRACE_PC_TAP_PEEK_LIST");
        private static readonly bool TracePcTapOncePerFrame =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PC_TAP_ONCE_PER_FRAME"), "1", StringComparison.Ordinal);
        private static int _memWatchLogRemaining = MemWatchLimit;
        private static bool _memWatchLastValid;
        private static uint _memWatchLastValue;
        private static byte _memWatchLastSize;
        private static long _pcTapLastFrame = -1;
        private static uint _pcTapLastPc;
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
        
        // Special Stage debugging
        internal static void TraceSpecialStage(uint pc, long frame)
        {
            if (!_specialStageDebug)
                return;
                
            // Log PC during special stage transition frames
            // if (frame >= 4900 && frame <= 4950)
            {
                // Always log when PC is at the stuck addresses
                if (pc == 0x003346 || pc == 0x003342 || pc == 0x003344)
                {
                    // Read the opcode at this address
                    ushort opcode = PeekOpcode(pc);
                    // Also read next word for branch displacement
                    ushort nextWord = PeekOpcode((uint)(pc + 2));
                     Console.WriteLine($"[STUCK-PC] frame={frame} PC=0x{pc:X6} OP=0x{opcode:X4} NEXT=0x{nextWord:X4}");
                }
                else if (frame - _lastSpecialStageFrameLogged > 5) // Log other PCs every 5 frames
                {
                    Console.WriteLine($"[SPECIAL-STAGE] frame={frame} PC=0x{pc:X6}");
                    _lastSpecialStageFrameLogged = frame;
                }
            }
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
            md_main.AddM68kCycles(in_clock);
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
                MaybeLogPcTap(g_reg_PC);
                
                // Special Stage debugging
                if (_specialStageDebug && md_main.g_md_vdp != null)
                {
                    long frame = md_main.g_md_vdp.FrameCounter;
                    TraceSpecialStage(g_reg_PC, frame);
                }

                interrupt_chk();
                g_clock = md_main.g_md_vdp.dma_status_update();
                if (g_clock == 0)
                {
                    uint pcBefore = g_reg_PC;
                    uint spNow = g_reg_addr[7].l;
                    if (_spZeroAsyncLogRemaining > 0 && spNow == 0 && _lastSpObserved != 0)
                    {
                        _spZeroAsyncLogRemaining--;
                        Console.WriteLine(
                            $"[m68k] SP=0 prefetch pc=0x{pcBefore:X6} lastSp=0x{_lastSpObserved:X8} " +
                            $"lastOpPc=0x{_lastPcBeforeOp:X6} lastOp=0x{_lastOpBeforeOp:X4} " +
                            $"lastSp:0x{_lastSpBeforeOp:X8}->0x{_lastSpAfterOp:X8}");
                    }
                    if (_pcZeroLogRemaining > 0 && (pcBefore == 0x000000 || pcBefore == 0x0000F4) && _lastPcAfter != 0)
                    {
                        _pcZeroLogRemaining--;
                        Console.WriteLine(
                            $"[m68k] PC jump pc=0x{pcBefore:X6} lastPc=0x{_lastPcAfter:X6} lastOp=0x{_lastOpAfter:X4} SP=0x{spNow:X8}");
                    }
                    _lastSpObserved = spNow;
                    g_opcode = read16(g_reg_PC);
                    g_op  = (byte)(g_opcode >> 12);
                    g_op1 = (byte)((g_opcode >> 9) & 0x07);
                    g_op2 = (byte)((g_opcode >> 6) & 0x07);
                    g_op3 = (byte)((g_opcode >> 3) & 0x07);
                    g_op4 = (byte)(g_opcode & 0x07);
                    uint spBefore = g_reg_addr[7].l;
                    _lastSpBeforeOp = spBefore;
                    _lastPcBeforeOp = pcBefore;
                    _lastOpBeforeOp = g_opcode;

                    MaybeLogPcSample(g_reg_PC, g_opcode);

                    if (g_reg_PC >= 0x000100 && g_reg_PC <= 0x000110 && _headerPcLogRemaining > 0)
                    {
                        _headerPcLogRemaining--;
                        uint sp = g_reg_addr[7].l;
                        uint ret0 = read32(sp);
                        uint ret1 = read32(sp + 4);
                        Console.WriteLine(
                            $"[m68k] PC in header pc=0x{g_reg_PC:X6} op=0x{g_opcode:X4} prev=0x{pcBefore:X6} " +
                            $"SP=0x{sp:X8} [SP]=0x{ret0:X8} [SP+4]=0x{ret1:X8}");
                    }


                    if (g_opcode == 0x30FC && TraceOp30FC)
                    {
                        ushort imm = read16(g_reg_PC + 2);
                        ushort addr = read16(g_reg_PC + 4);
                        Console.WriteLine($"[m68k] OP30FC pc=0x{g_reg_PC:X6} imm=0x{imm:X4} addr.w=0x{addr:X4}");
                    }
                    if (g_opcode == 0x4A38 && TraceOp4A38)
                    {
                        ushort addr = read16(g_reg_PC + 2);
                        Console.WriteLine($"[m68k] OP4A38 pc=0x{g_reg_PC:X6} addr.w=0x{addr:X4}");
                    }

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
                        HandleIllegalOpcode(g_opcode);
                    }
                    else
                    {
                        opinfo.opcode();
                    }
                    _lastPcAfter = g_reg_PC;
                    _lastOpAfter = g_opcode;
                    uint spAfter = g_reg_addr[7].l;
                    _lastSpAfterOp = spAfter;
                    if (_a7WriteLogRemaining > 0 && spAfter != spBefore)
                    {
                        _a7WriteLogRemaining--;
                        string opname = opinfo?.opname_out ?? "unknown";
                        Console.WriteLine(
                            $"[m68k] A7 write pc=0x{pcBefore:X6} op=0x{g_opcode:X4} {opname} " +
                            $"A7:0x{spBefore:X8}->0x{spAfter:X8} USP=0x{g_reg_addr_usp.l:X8}");
                    }
                    if (_spZeroLogRemaining > 0 && spAfter == 0 && spBefore != 0)
                    {
                        _spZeroLogRemaining--;
                        string opname = opinfo?.opname_out ?? "unknown";
                        Console.WriteLine(
                            $"[m68k] SP=0 pc=0x{pcBefore:X6} op=0x{g_opcode:X4} {opname} " +
                            $"SP:0x{spBefore:X8}->0x{spAfter:X8} USP=0x{g_reg_addr_usp.l:X8}");
                    }
                    if (_spWatchRemaining > 0 && spAfter != spBefore)
                    {
                        if (spAfter < 0x1000 || spAfter == 0)
                        {
                            _spWatchRemaining--;
                            string opname = opinfo?.opname_out ?? "unknown";
                            Console.WriteLine(
                                $"[m68k] SP change pc=0x{pcBefore:X6} op=0x{g_opcode:X4} {opname} " +
                                $"S={(g_status_S ? 1 : 0)} SP:0x{spBefore:X8}->0x{spAfter:X8} USP=0x{g_reg_addr_usp.l:X8}");
                        }
                    }
                    if (_spOverflowLogRemaining > 0 && spBefore <= g_stack_top && spAfter > g_stack_top)
                    {
                        _spOverflowLogRemaining--;
                        string opname = opinfo?.opname_out ?? "unknown";
                        Console.WriteLine(
                            $"[m68k] SP overflow pc=0x{pcBefore:X6} op=0x{g_opcode:X4} {opname} " +
                            $"SP:0x{spBefore:X8}->0x{spAfter:X8} top=0x{g_stack_top:X8}");
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
            uint spBefore = g_reg_addr[7].l;
            uint pcBefore = g_reg_PC;

            bool vintPending = g_interrupt_V_req;
            bool vintEnabled = md_main.g_md_vdp.g_vdp_reg_1_5_vinterrupt == 1;
            bool canVint = vintPending && (g_status_interrupt_mask < 6) && vintEnabled && !g_interrupt_H_act;
            if (TraceM68kIntPending && vintPending && !canVint && _traceM68kIntPendingRemaining > 0)
            {
                _traceM68kIntPendingRemaining--;
                Console.WriteLine(
                    $"[m68k int] VINT pending BLOCKED pc=0x{pcBefore:X6} sr=0x{g_reg_SR:X4} " +
                    $"mask={g_status_interrupt_mask} vdpVint={vintEnabled} hintAct={(g_interrupt_H_act ? 1 : 0)}");
            }

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
                if (TraceM68kInt && _traceM68kIntRemaining > 0)
                {
                    _traceM68kIntRemaining--;
                    Console.WriteLine($"[m68k int] HINT vec=0x0070 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...HINT...);
                TracePush("HINT", 0x0070, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                if (_spZeroLogRemaining > 0 && g_reg_addr[7].l == 0)
                {
                    _spZeroLogRemaining--;
                    Console.WriteLine(
                        $"[m68k] SP=0 in HINT pc=0x{pcBefore:X6} vec=0x0070 start=0x{w_start_address:X6} oldSr=0x{oldSr:X4}");
                }
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
                if (TraceM68kInt && _traceM68kIntRemaining > 0)
                {
                    _traceM68kIntRemaining--;
                    Console.WriteLine($"[m68k int] VINT vec=0x0078 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...VINT...);
                TracePush("VINT", 0x0078, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                if (_spZeroLogRemaining > 0 && g_reg_addr[7].l == 0)
                {
                    _spZeroLogRemaining--;
                    Console.WriteLine(
                        $"[m68k] SP=0 in VINT pc=0x{pcBefore:X6} vec=0x0078 start=0x{w_start_address:X6} oldSr=0x{oldSr:X4}");
                }
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (6 << 8));
                g_interrupt_V_req = false;
                g_interrupt_V_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_EXT_req && (g_status_interrupt_mask < g_interrupt_EXT_level))
            {
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(g_interrupt_EXT_vector);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    MdLog.WriteLine($"[m68k int] EXT vec=0x{g_interrupt_EXT_vector:X4} start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                if (TraceM68kInt && _traceM68kIntRemaining > 0)
                {
                    _traceM68kIntRemaining--;
                    Console.WriteLine($"[m68k int] EXT vec=0x{g_interrupt_EXT_vector:X4} start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...EXT...);
                TracePush("EXT", g_interrupt_EXT_vector, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                if (_spZeroLogRemaining > 0 && g_reg_addr[7].l == 0)
                {
                    _spZeroLogRemaining--;
                    Console.WriteLine(
                        $"[m68k] SP=0 in EXT pc=0x{pcBefore:X6} vec=0x0068 start=0x{w_start_address:X6} oldSr=0x{oldSr:X4}");
                }
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (g_interrupt_EXT_level << 8));
                g_interrupt_EXT_req = false;
                g_interrupt_EXT_act = true;
                g_interrupt_EXT_ack?.Invoke(g_interrupt_EXT_level);
                g_68k_stop = false;
            }

            uint spAfter = g_reg_addr[7].l;
            if (_spWatchRemaining > 0 && spAfter != spBefore && (spAfter == 0 || spAfter < 0x1000))
            {
                _spWatchRemaining--;
                Console.WriteLine(
                    $"[m68k] SP change in interrupt_chk pc=0x{pcBefore:X6} SP:0x{spBefore:X8}->0x{spAfter:X8} S={(g_status_S ? 1 : 0)} SR=0x{g_reg_SR:X4}");
            }
        }

        private void HandleIllegalOpcode(ushort opcode)
        {
            uint vector;
            string kind;
            switch ((opcode >> 12) & 0xF)
            {
                case 0xA:
                    vector = 0x0028; // Line-A emulator
                    kind = "LINE-A";
                    break;
                case 0xF:
                    vector = 0x002C; // Line-F emulator
                    kind = "LINE-F";
                    break;
                default:
                    vector = 0x0010; // Illegal instruction
                    kind = "ILLEGAL";
                    break;
            }

            RaiseException(kind, vector);
            if (g_clock == 0)
                g_clock = 34;
        }

        private void RaiseException(string kind, uint vectorAddress)
        {
            ushort oldSr = g_reg_SR;
            if (!g_status_S)
            {
                SwapStacks();
                g_status_S = true;
            }

            uint start = read32(vectorAddress);
            if (_illegalVectorLogRemaining > 0)
            {
                _illegalVectorLogRemaining--;
                ushort v0 = read16(start);
                ushort v1 = read16(start + 2);
                Console.WriteLine(
                    $"[m68k] exception {kind} vec=0x{vectorAddress:X4} start=0x{start:X6} " +
                    $"pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8} " +
                    $"op@start=0x{v0:X4} next=0x{v1:X4}");
            }
            stack_push32(g_reg_PC);
            TracePush(kind, vectorAddress, start, g_reg_PC, g_reg_addr[7].l);
            stack_push16(oldSr);
            g_reg_PC = start;
            g_68k_stop = false;
        }

        private static void MaybeLogPcSample(uint pc, ushort opcode)
        {
            if (!TracePcSample)
                return;

            long now = _pcSampleStopwatch.ElapsedMilliseconds;
            if (now - _pcSampleLastMs < 1000)
                return;
            _pcSampleLastMs = now;

            Console.WriteLine($"[PCSAMPLE] t={now}ms pc=0x{pc:X6} op=0x{opcode:X4} sr=0x{g_reg_SR:X4} d0=0x{g_reg_data[0].l:X8} d1=0x{g_reg_data[1].l:X8} a7=0x{g_reg_addr[7].l:X8}");
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
            if (TraceMemWatchAddrs.Count > 0 && TraceMemWatchAddrs.Contains(address) && _memWatchLogRemaining > 0)
            {
                if (write || MemWatchAll)
                {
                    char rw = write ? 'W' : 'R';
                    Console.WriteLine($"[MEMWATCH] {rw}{size} pc=0x{g_reg_PC:X6} addr=0x{address:X6} val=0x{FormatAccessValue(value, size)}");
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
                    // Log VDP status reads during special stage
                    if (_specialStageDebug && md_main.g_md_vdp != null)
                    {
                        long frame = md_main.g_md_vdp.FrameCounter;
                        // if (frame >= 4900 && frame <= 4950)
                        // {
                        //     Console.WriteLine($"[SPECIAL-STAGE-VDP-STATUS-READ] frame={frame} PC=0x{g_reg_PC:X6}");
                        // }
                    }
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

        private static HashSet<uint> ParseWatchAddrList(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            var result = new HashSet<uint>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;
            string[] tokens = raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                string trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;
                string parsed = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? trimmed.Substring(2)
                    : trimmed;
                if (uint.TryParse(parsed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ||
                    uint.TryParse(parsed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    result.Add(value & 0x00FF_FFFF);
                }
            }
            return result;
        }

        public static List<(uint Start, uint End)> ParseWatchRangeList(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            var result = new List<(uint, uint)>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;
            string[] tokens = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                string trimmed = token.Trim();
                if (trimmed.Length == 0)
                    continue;
                int sep = trimmed.IndexOf('-');
                if (sep <= 0 || sep >= trimmed.Length - 1)
                    continue;
                string left = trimmed.Substring(0, sep);
                string right = trimmed.Substring(sep + 1);
                if (!TryParseAddrToken(left, out uint start) || !TryParseAddrToken(right, out uint end))
                    continue;
                if (end < start)
                {
                    uint tmp = start;
                    start = end;
                    end = tmp;
                }
                result.Add((start & 0x00FF_FFFF, end & 0x00FF_FFFF));
            }
            return result;
        }

        private static bool TryParseAddrToken(string token, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(token))
                return false;
            string trimmed = token.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(2);
            if (uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return true;
            if (uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;
            return false;
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

        private static void MaybeLogPcTap(uint pc)
        {
            if (TracePcTapAddrs.Count == 0 || !TracePcTapAddrs.Contains(pc))
                return;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            if (TracePcTapOncePerFrame && frame == _pcTapLastFrame && pc == _pcTapLastPc)
                return;
            _pcTapLastFrame = frame;
            _pcTapLastPc = pc;

            Console.WriteLine(
                $"[PCTAP] frame={frame} pc=0x{pc:X6} SR=0x{g_reg_SR:X4} " +
                $"D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} D3=0x{g_reg_data[3].l:X8} " +
                $"D4=0x{g_reg_data[4].l:X8} D5=0x{g_reg_data[5].l:X8} D6=0x{g_reg_data[6].l:X8} D7=0x{g_reg_data[7].l:X8} " +
                $"A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8} A2=0x{g_reg_addr[2].l:X8} A3=0x{g_reg_addr[3].l:X8} " +
                $"A4=0x{g_reg_addr[4].l:X8} A5=0x{g_reg_addr[5].l:X8} A6=0x{g_reg_addr[6].l:X8} A7=0x{g_reg_addr[7].l:X8} " +
                $"{FormatRecentAccesses(32)}");

            DumpPcWindowRange(pc, 32, 64);

            for (int i = 0; i < TracePcTapPeekRanges.Count; i++)
            {
                var range = TracePcTapPeekRanges[i];
                DumpPcTapRange(range.Start, range.End);
            }
        }

        private static void DumpPcTapRange(uint start, uint end)
        {
            if (end < start)
                return;
            const int bytesPerLine = 16;
            uint addr = start;
            while (addr <= end)
            {
                var sb = new StringBuilder();
                sb.AppendFormat("[PCTAP-PEEK] addr=0x{0:X6}:", addr);
                uint lineEnd = addr + (uint)bytesPerLine - 1;
                if (lineEnd > end)
                    lineEnd = end;
                for (uint a = addr; a <= lineEnd; a++)
                {
                    sb.AppendFormat(" {0:X2}", PeekMem8(a));
                }
                Console.WriteLine(sb.ToString());
                if (lineEnd == uint.MaxValue)
                    break;
                addr = lineEnd + 1;
            }
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
            while (remaining > 0 && instrCount < 16)
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
            // Prefer bus override so Sega CD BIOS/PRG RAM peeks work in traces.
            if (md_main.g_md_bus?.OverrideBus is IM68kBusOverride ob
                && ob.TryRead8(address, out byte value))
            {
                return value;
            }

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
