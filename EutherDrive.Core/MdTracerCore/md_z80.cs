using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // CPU : Zilog Z80
    //----------------------------------------------------------------
    internal partial class md_z80
    {
        private static readonly bool TraceZ80ForceActive =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_FORCE_ACTIVE"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80IntDebug =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_INT_DEBUG"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Boot =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_BOOT"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80BootFile =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_BOOT_FILE"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80BootFileLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80_BOOT_FILE_LIMIT", 2000);
        private static readonly bool TraceZ80Speed =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_SPEED"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80SigTransitions =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80SIG_TRANS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Run =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_RUN"), "1", StringComparison.Ordinal);
        public bool g_active;
        internal ushort DebugPc => g_reg_PC;
        internal ushort CpuPc => g_reg_PC;
        internal ushort DebugBc => g_reg_BC;
        internal ushort DebugDe => (ushort)((g_reg_D << 8) + g_reg_E);
        internal ushort DebugHl => (ushort)((g_reg_H << 8) + g_reg_L);
        internal ushort DebugSp => g_reg_SP;
        internal byte DebugA => g_reg_A;
        internal ushort DebugIx => g_reg_IX;
        internal ushort DebugIy => g_reg_IY;
        internal uint DebugBankRegister => g_bank_register & 0x1FFu;
        internal uint DebugBankBase => (g_bank_register & 0x1FFu) * 0x8000u;
        internal ushort DebugLastReadAddr => _lastReadAddr;
        internal uint DebugLastReadM68kAddr => _lastReadM68kAddr;
        internal byte DebugLastReadValue => _lastReadValue;
        internal ushort DebugLastReadPc => _lastReadPc;
        internal bool DebugLastReadWasBanked => _lastReadWasBanked;
        internal long DebugTotalCycles => _totalCycles;
        internal long BudgetCycles => _budgetCycles;  // Always advances when run() is called
        internal long DebugSystemCycleDelta => _systemCycleDelta;
        private static readonly bool TraceSmsPc77 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_PC77"), "1", StringComparison.Ordinal);
        private const int SmsPc77LogLimit = 50000;
        private static int _smsPc77LogCount;
        private static string? _smsPc77LogPath;
        private static readonly bool TraceSms77ee =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DEBUG_77EE"), "1", StringComparison.Ordinal);
        private static int _sms77eeLogCount;
        private static string? _sms77eeLogPath;
        private static ushort _prevPc;
        private static readonly bool TraceSmsPc77Pre =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_PC77_PRE"), "1", StringComparison.Ordinal);
        private const int SmsPc77PreLogLimit = 50000;
        private static int _smsPc77PreLogCount;
        private static string? _smsPc77PreLogPath;
        private static readonly bool TraceSms7728 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DEBUG_7728"), "1", StringComparison.Ordinal);
        private static int _sms7728LogCount;
        private static string? _sms7728LogPath;
        private static readonly bool TraceSmsPc96 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_PC96"), "1", StringComparison.Ordinal);
        private const int SmsPc96LogLimit = 50000;
        private static int _smsPc96LogCount;
        private static string? _smsPc96LogPath;
        private static readonly bool TraceSmsPc07 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_PC07"), "1", StringComparison.Ordinal);
        private const int SmsPc07LogLimit = 50000;
        private static int _smsPc07LogCount;
        private static string? _smsPc07LogPath;
        private static readonly bool TraceSmsPc41 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_PC41"), "1", StringComparison.Ordinal);
        private const int SmsPc41LogLimit = 50000;
        private static int _smsPc41LogCount;
        private static string? _smsPc41LogPath;
        private static readonly bool TraceSmsPc54 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_TRACE_PC54"), "1", StringComparison.Ordinal);
        private const int SmsPc54LogLimit = 50000;
        private static int _smsPc54LogCount;
        private static string? _smsPc54LogPath;

        internal void BeginSystemCycleSlice()
        {
            _systemCycleDelta = 0;
        }

        internal void EndSystemCycleSlice()
        {
            _systemCycleDelta = 0;
        }

        // Bank register access for M68K bus writes (shift-register style)
        internal uint GetBankRegister() => g_bank_register;
        internal void SetBankRegister(uint value) => g_bank_register = value;

        private ushort g_reg_PC;
        private byte   g_reg_A;
        private byte   g_reg_B;
        private byte   g_reg_C;
        private byte   g_reg_D;
        private byte   g_reg_E;
        private byte   g_reg_H;
        private byte   g_reg_L;

        private byte   g_reg_Au;
        private byte   g_reg_Bu;
        private byte   g_reg_Cu;
        private byte   g_reg_Du;
        private byte   g_reg_Eu;
        private byte   g_reg_Fu;
        private byte   g_reg_Hu;
        private byte   g_reg_Lu;

        private ushort g_reg_SP;
        private ushort g_reg_IX;
        private ushort g_reg_IY;

        private int g_flag_S;
        private int g_flag_Z;
        private int g_flag_H;
        private int g_flag_PV;
        private int g_flag_N;
        private int g_flag_C;

        private int g_flag_Su;
        private int g_flag_Zu;
        private int g_flag_Hu;
        private int g_flag_PVu;
        private int g_flag_Nu;
        private int g_flag_Cu;

        private byte g_reg_R;
        private byte g_reg_I;

        private bool g_IFF1;
        private bool g_IFF2;
        private int  g_interruptMode;
        private bool g_interrupt_irq;
        private bool g_interrupt_nmi;
        private bool g_halt;
        private static readonly bool TraceBoot =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_BOOT"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Stats =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_STATS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80PcHist =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80PCHIST"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80PcHistLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80PCHIST_LIMIT", 4);
        private static readonly int TraceZ80PcHistMin =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80PCHIST_MIN", 1);
        private static readonly bool TraceZ80PcFile =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_PC_FILE"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80PcFileLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80_PC_FILE_LIMIT", 20000);
        private static int _traceZ80PcFileCount;
        private static string? _traceZ80PcFilePath;
        private static readonly bool TraceZ80DdcbBit =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_DDCB_BIT"), "1", StringComparison.Ordinal);
        private static readonly int TraceZ80DdcbBitLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80_DDCB_BIT_LIMIT", 64);
        private static readonly ushort? TraceZ80PcRangeStart =
            ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_PC_RANGE_START");
        private static readonly ushort? TraceZ80PcRangeEnd =
            ParseZ80Addr("EUTHERDRIVE_TRACE_Z80_PC_RANGE_END");
        private static readonly int TraceZ80PcRangeLimit =
            ParseTraceLimit("EUTHERDRIVE_TRACE_Z80_PC_RANGE_LIMIT", 128);
        private static readonly bool ForceZ80FlagJr =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80FLAG_FORCE_JR"), "1", StringComparison.Ordinal);
        private static readonly ushort? ForceZ80FlagJrTargetOverride =
            ParseZ80Addr("EUTHERDRIVE_Z80FLAG_FORCE_JR_TARGET");
        private const ushort ForceZ80FlagJrFallback = 0x0DF0;
        private static readonly int ForceZ80FlagJrLimit =
            ParseTraceLimit("EUTHERDRIVE_Z80FLAG_FORCE_JR_LIMIT", 8);
        private static readonly int Z80ResetHoldCycles =
            ParseTraceLimit("EUTHERDRIVE_Z80_RESET_HOLD_Z80_CYCLES", 0);
        private static readonly int Z80ResetHoldLogLimit =
            ParseTraceLimit("EUTHERDRIVE_Z80_RESET_HOLD_LIMIT", 8);
        private static readonly bool DumpZ80Ram =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DUMP_Z80_RAM"), "1", StringComparison.Ordinal);
        private static readonly int DumpZ80RamFrame =
            ParseTraceLimit("EUTHERDRIVE_DUMP_Z80_RAM_FRAME", -1);
        private static readonly bool DumpZ80RamExtra =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DUMP_Z80_RAM_EXTRA"), "1", StringComparison.Ordinal);
        private static readonly bool HaltOnBusReq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_HALT_ON_BUSREQ"), "1", StringComparison.Ordinal);
        private static readonly uint Z80BankDefault = ParseZ80BankDefault();
        private static readonly byte Z80Im2Vector = ParseZ80Im2Vector();

        private long _instrThrottleCounter;
        private long _totalCycles;
        private long _z80SpeedCycles;
        private long _z80SpeedLastTicks;
        private long _budgetCycles;  // Cycles budgeted to Z80 (always advances)
        [NonSerialized] private long _systemCycleDelta;
        private bool _z80Dumped;
        private bool _z80DumpedFrame;
        private bool _pcLeftBootRangeSinceLastSummary;
        private long _z80StatsLastTicks;
        private long _z80StatsInstrCount;
        private long _z80StatsCycleCount;
        private long _z80StatsBudgetCount;
        private long _z80StatsIrqCount;
        private long _z80StatsBlockedCount;
        private int _bootInstrCount;
        private int _bootFileLogged;
        private string? _bootFilePath;
        private int _runCount;
        private readonly int[] _pcHist = new int[0x10000];
        private int _pcHistTotal;
        private int _forceZ80FlagJrRemaining = ForceZ80FlagJrLimit;
        private int _z80PcRangeRemaining = TraceZ80PcRangeLimit;
        private int _z80ResetHoldRemaining;
        private int _z80ResetHoldLogRemaining = Z80ResetHoldLogLimit;
        private bool _forcePcPending;
        private ushort _forcePcTarget;
        private string _forcePcReason = string.Empty;
        private int _iff1Delay;
        private int _waitCycles;

        // [INT-STATS] ZINT interrupt counting per frame
        private int _zintAssertCount;
        private int _zintClearCount;
        private int _zintServiceCount;
        private long _zintLastFrameLogged = -1;
        private int _irqAutoClearCycles;
        private string? _irqAutoClearSource;

        // [Z80-BANK] Log banked memory entry once
        private bool _z80BankEntryLogged;

        private ushort g_reg_BC => (ushort)((g_reg_B << 8) + g_reg_C);
        private ushort g_reg_DE => (ushort)((g_reg_D << 8) + g_reg_E);
        private ushort g_reg_HL => (ushort)((g_reg_H << 8) + g_reg_L);

        private byte g_reg_PCH => (byte)((g_reg_PC & 0xff00) >> 8);
        private byte g_reg_PCL => (byte)(g_reg_PC & 0x00ff);
        private void g_write_PCH(byte in_val) => g_reg_PC = (ushort)((in_val << 8) + g_reg_PCL);
        private void g_write_PCL(byte in_val) => g_reg_PC = (ushort)((g_reg_PCH << 8) + in_val);

        private byte g_reg_IXH => (byte)((g_reg_IX & 0xff00) >> 8);
        private byte g_reg_IXL => (byte)(g_reg_IX & 0x00ff);
        private void g_write_IXH(byte in_val) => g_reg_IX = (ushort)((in_val << 8) + g_reg_IXL);
        private void g_write_IXL(byte in_val) => g_reg_IX = (ushort)((g_reg_IXH << 8) + in_val);

        private byte g_reg_IYH => (byte)((g_reg_IY & 0xff00) >> 8);
        private byte g_reg_IYL => (byte)(g_reg_IY & 0x00ff);
        private void g_write_IYH(byte in_val) => g_reg_IY = (ushort)((in_val << 8) + g_reg_IYL);
        private void g_write_IYL(byte in_val) => g_reg_IY = (ushort)((g_reg_IYH << 8) + in_val);

        // Opcode fetch should not trigger MMIO/mailbox side effects; keep SMS mapping intact.
        private byte ReadOpcodeByte(uint addr)
        {
            if (md_main.g_masterSystemMode)
                return read8(addr);
            return PeekZ80ByteNoSideEffect((ushort)(addr & 0xFFFF));
        }
        private byte   g_opcode1  => ReadOpcodeByte(g_reg_PC);
        private byte   g_opcode2  => ReadOpcodeByte((uint)(g_reg_PC + 1));
        private byte   g_opcode3  => ReadOpcodeByte((uint)(g_reg_PC + 2));
        private byte   g_opcode4  => ReadOpcodeByte((uint)(g_reg_PC + 3));
        private ushort g_opcode23 => (ushort)((ReadOpcodeByte((uint)(g_reg_PC + 2)) << 8) + ReadOpcodeByte((uint)(g_reg_PC + 1)));
        private ushort g_opcode34 => (ushort)((ReadOpcodeByte((uint)(g_reg_PC + 3)) << 8) + ReadOpcodeByte((uint)(g_reg_PC + 2)));

        private byte g_opcode1_210 => (byte)(g_opcode1 & 0x07);
        private byte g_opcode2_210 => (byte)(g_opcode2 & 0x07);
        private byte g_opcode1_543 => (byte)((g_opcode1 >> 3) & 0x07);
        private byte g_opcode2_543 => (byte)((g_opcode2 >> 3) & 0x07);
        private byte g_opcode3_543 => (byte)((g_opcode3 >> 3) & 0x07);
        private byte g_opcode4_543 => (byte)((g_opcode4 >> 3) & 0x07);
        private byte g_opcode1_54  => (byte)((g_opcode1 >> 4) & 0x03);
        private byte g_opcode2_54  => (byte)((g_opcode2 >> 4) & 0x03);

        private int g_clock;
        private int g_clock_total;
        internal int LineCycles { get; private set; }
        private int _smsInstructionLog;
        private ushort _smsLoopPc;
        private int _smsLoopCount;
        private const int SmsInstructionLogLimit = 256;
        private const int SmsLoopReportThreshold = 64;
        private const int SmsControlLogLimit = 12;

        private static uint ParseZ80BankDefault()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_BANK_DEFAULT");
            if (string.IsNullOrWhiteSpace(raw))
                return 0x00u;  // Default to bank 0 for compatibility with games that don't configure the bank
            raw = raw.Trim();
            uint value;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(raw.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
                    return 0x00u;
            }
            else if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return 0x00u;
            }
            return value & 0x1FFu;
        }

        private static byte ParseZ80Im2Vector()
        {
            string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_IM2_VECTOR");
            if (string.IsNullOrWhiteSpace(raw))
                return 0xFF;
            raw = raw.Trim();
            uint value;
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(raw.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
                    return 0xFF;
            }
            else if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return 0xFF;
            }
            return (byte)(value & 0xFF);
        }

        private static int ParseTraceLimit(string name, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;
            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(raw.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int hex))
                    return hex;
                return fallback;
            }
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
            return fallback;
        }

        private static int _smsControlLogCount;
        private static readonly bool TraceSmsIrq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_IRQ"), "1", StringComparison.Ordinal);
        private static int _traceSmsIrqCount;
        private static int _traceSmsIrqBlockCount;
        private static int _traceSmsIrqStateCount;
        private const int SmsLoopRegLogLimit = 4;
        private static int _smsLoopRegLogCount;
        private int _smsDelayTrace;
        private const int SmsDelayTraceMask = 0x0FFF;
        private const int DjnzLogLimit = 4;
        private static readonly bool TraceSmsDelay =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_DELAY"), "1", StringComparison.Ordinal);
        private static readonly bool SkipSmsDelay =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SKIP_SMS_DELAYS"), "1", StringComparison.Ordinal);
        private static readonly bool TraceZ80Console =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80"), "1", StringComparison.Ordinal);
        private static bool TraceZ80Step => MdTracerCore.MdLog.TraceZ80Step;
        private long _delayTraceFrame = -1;
        private bool _delayEntryLogged;
        private bool _delayExitLogged;
        private int _djnzLogCount;
        private int _sonic2DebugCount;
        private int _z80DebugCount;
        private int _cyclesBudgetAccum;
        private int _cyclesActualAccum;
        private int _resetCallsThisCycle;
        private int _lastResetCycleId;
        private bool _duplicateResetLogged;
        private int _traceStepRemaining;
        private int _z80DdcbBitRemaining;
        private bool _lastCanRun;
        private string _irqSource = "unknown";
        private byte _irqStatus;

        //----------------------------------------------------------------
        public md_z80()
        {
            initialize();
        }

        private void MaybeArmZ80AfterFlagRetTrace(ushort pcBefore, ushort pcAfter)
        {
            if (!TraceZ80AfterFlagRet)
                return;
            if (_z80AfterFlagRetRemaining > 0)
                return;
            _z80AfterFlagRetRemaining = TraceZ80AfterFlagRetLimit;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            string pcdump = DumpZ80PcBytes(pcAfter, 0, 7);
            Console.WriteLine(
                $"[Z80AFTERRET-ARM] frame={frame} from=0x{pcBefore:X4} to=0x{pcAfter:X4} " +
                $"ix=0x{g_reg_IX:X4} iy=0x{g_reg_IY:X4} sp=0x{g_reg_SP:X4} " +
                $"limit={TraceZ80AfterFlagRetLimit} pcdump={pcdump}");
        }

        private void MaybeLogZ80Stats()
        {
            if (!TraceZ80Stats)
                return;

            long now = Stopwatch.GetTimestamp();
            if (_z80StatsLastTicks == 0)
            {
                _z80StatsLastTicks = now;
                return;
            }

            long elapsedTicks = now - _z80StatsLastTicks;
            if (elapsedTicks < Stopwatch.Frequency)
                return;

            double elapsedSec = (double)elapsedTicks / Stopwatch.Frequency;
            long instrDelta = _z80StatsInstrCount;
            long cycleDelta = _z80StatsCycleCount;
            long budgetDelta = _z80StatsBudgetCount;
            long irqDelta = _z80StatsIrqCount;
            long blockedDelta = _z80StatsBlockedCount;
            _z80StatsInstrCount = 0;
            _z80StatsCycleCount = 0;
            _z80StatsBudgetCount = 0;
            _z80StatsIrqCount = 0;
            _z80StatsBlockedCount = 0;
            _z80StatsLastTicks = now;

            bool busRequested = md_main.g_md_bus?.Z80BusGranted ?? false;
            bool reset = md_main.g_md_bus?.Z80Reset ?? false;
            long busReqWrites = 0;
            long busReqToggles = 0;
            long resetWrites = 0;
            long resetToggles = 0;
            md_main.g_md_bus?.ConsumeZ80SignalStats(out busReqWrites, out busReqToggles, out resetWrites, out resetToggles);

            double ips = instrDelta / elapsedSec;
            double cps = cycleDelta / elapsedSec;
            double budgetCps = budgetDelta / elapsedSec;
            double util = budgetDelta > 0 ? (cycleDelta * 100.0) / budgetDelta : 0.0;
            Console.WriteLine(
                $"[Z80Stats] dt={elapsedSec:0.00}s instr={instrDelta} ips={ips:0} cycles={cycleDelta} cps={cps:0} " +
                $"budget={budgetDelta} budgetCps={budgetCps:0} util={util:0.0}% irq={irqDelta} blocked={blockedDelta} " +
                $"active={(g_active ? 1 : 0)} halt={(g_halt ? 1 : 0)} pc=0x{g_reg_PC:X4} " +
                $"busReq={(busRequested ? 1 : 0)} reset={(reset ? 1 : 0)} busReqW={busReqWrites} " +
                $"busReqT={busReqToggles} resetW={resetWrites} resetT={resetToggles}");
        }

        public void run(int in_clock)
        {
            // Simple debug to see if run is called
            if (TraceZ80Run && _runCount < 5)
            {
                _runCount++;
                Console.WriteLine($"[Z80-RUN-{_runCount}] clock={in_clock} g_active={g_active} PC=0x{g_reg_PC:X4} busGranted={md_main.g_md_bus?.Z80BusGranted ?? false} reset={md_main.g_md_bus?.Z80Reset ?? false}");
            }

            md_main.g_md_bus?.ApplyZ80BusReqLatch();
            bool busRequested = md_main.g_md_bus?.Z80BusGranted ?? false;
            bool z80reset = md_main.g_md_bus?.Z80Reset ?? false;
            
            // DEBUG: Log Z80 execution state
            if (TraceZ80Run && md_main.g_md_vdp?.FrameCounter >= 10 && md_main.g_md_vdp.FrameCounter <= 20)
            {
                bool canRunDebug = g_active && !busRequested && !z80reset;
                Console.WriteLine($"[Z80-RUN] frame={md_main.g_md_vdp.FrameCounter} clock={in_clock} active={g_active} canRun={canRunDebug} busreq={busRequested} reset={z80reset} pc=0x{g_reg_PC:X4}");
            }
            bool canRun = g_active && !busRequested && !z80reset;

            // Advance budget cycles - these are the cycles allocated to Z80,
            // which should be used for timekeeping (YM2612 busy flag, timers)
            // regardless of whether Z80 actually executes.
            _budgetCycles += in_clock;
            if (TraceZ80Stats)
                _z80StatsBudgetCount += in_clock;

            if (TraceZ80Step && !_lastCanRun && canRun)
            {
                _traceStepRemaining = 64;
            }
            _lastCanRun = canRun;

            if (HaltOnBusReq && busRequested)
                return;
            if (g_active == false) 
            {
                // DEBUG: Try forcing active
                if (md_main.g_md_vdp?.FrameCounter >= 10)
                {
                    if (TraceZ80ForceActive)
                        Console.WriteLine($"[Z80-DEBUG] g_active=false at frame={md_main.g_md_vdp.FrameCounter}, forcing true");
                    g_active = true;
                }
                else
                {
                    return;
                }
            }
            if (busRequested || z80reset)
            {
                if (TraceZ80Stats)
                    _z80StatsBlockedCount++;
                if (TraceZ80SigTransitions && g_active)
                {
                    long blockFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                    Console.WriteLine($"[Z80RUN-BLOCK] frame={blockFrame} pc=0x{g_reg_PC:X4} busReq={(busRequested ? 1 : 0)} reset={(z80reset ? 1 : 0)}");
                }
                return;
            }

            int cyclesConsumed = 0;
            long frameCounter = md_main.g_md_vdp?.FrameCounter ?? -1;
            if (frameCounter != _delayTraceFrame)
            {
                _delayTraceFrame = frameCounter;
                _delayEntryLogged = false;
                _delayExitLogged = false;
                _djnzLogCount = 0;
            }

            g_clock_total += in_clock;
            while (g_clock_total > 0)
            {
                md_main.g_md_bus?.ApplyZ80BusReqLatch();
                busRequested = md_main.g_md_bus?.Z80BusGranted ?? false;
                z80reset = md_main.g_md_bus?.Z80Reset ?? false;
                if (busRequested || z80reset)
                {
                    if (TraceZ80SigTransitions && g_active)
                    {
                        long blockFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                        Console.WriteLine($"[Z80RUN-BLOCK] frame={blockFrame} pc=0x{g_reg_PC:X4} busReq={(busRequested ? 1 : 0)} reset={(z80reset ? 1 : 0)}");
                    }
                    return;
                }

                if (TraceZ80Stats)
                    _z80StatsInstrCount++;

                if (_z80ResetHoldRemaining > 0)
                {
                    int burn = Math.Min(_z80ResetHoldRemaining, Math.Max(1, g_clock_total + 1));
                    _z80ResetHoldRemaining -= burn;
                    _totalCycles += burn;
                    MaybeLogZ80Speed(burn);
                    cyclesConsumed += burn;
                    LineCycles += burn;
                    if (TraceZ80Stats)
                        _z80StatsCycleCount += burn;
                    md_main.g_md_music?.g_md_ym2612.TickTimersFromZ80Cycles(burn);
                    g_clock_total -= burn;
                    continue;
                }

                if (g_interrupt_nmi)
                {
                    g_interrupt_nmi = false;
                    if (g_halt) g_reg_PC += 1;
                    stack_push(g_reg_PCH);
                    stack_push(g_reg_PCL);
                    g_reg_PC = 0x0066;
                    g_halt = false;
                    g_IFF1 = false;

                    const int nmiCycles = 11;
                    _totalCycles += nmiCycles;
                    MaybeLogZ80Speed(nmiCycles);
                    cyclesConsumed += nmiCycles;
                    LineCycles += nmiCycles;
                    md_main.g_md_music?.g_md_ym2612.TickTimersFromZ80Cycles(nmiCycles);
                    g_clock_total -= nmiCycles;
                    continue;
                }

                // IRQ (NMI-block ej aktiverad i originalet)
            if (TraceSmsIrq && md_main.g_masterSystemMode && _traceSmsIrqStateCount < 16)
            {
                _traceSmsIrqStateCount++;
                Console.WriteLine($"[SMS IRQ] state irq={(g_interrupt_irq ? 1 : 0)} IFF1={(g_IFF1 ? 1 : 0)} halt={(g_halt ? 1 : 0)} PC=0x{g_reg_PC:X4}");
            }
            if (g_interrupt_irq)
            {
                if (g_IFF1)
                {
                    if (TraceZ80Stats)
                        _z80StatsIrqCount++;
                    SmsControlLog($"[md_z80 SMS irq] IM{g_interruptMode} pending PC=0x{g_reg_PC:X4}");
                    if (TraceSmsIrq && md_main.g_masterSystemMode)
                        Console.WriteLine($"[SMS IRQ] service IM{g_interruptMode} PC=0x{g_reg_PC:X4}");
                    if (g_halt) g_reg_PC += 1;

                    g_interrupt_irq = false;
                    LogZ80Int(false, _irqSource, _irqStatus, "service");
                        g_IFF1 = false;
                        g_IFF2 = false;
                        g_halt = false;

                        const int irqCycles = 13;
                        _totalCycles += irqCycles;
                        MaybeLogZ80Speed(irqCycles);
                        cyclesConsumed += irqCycles;
                        md_main.g_md_music?.g_md_ym2612.TickTimersFromZ80Cycles(irqCycles);
                        g_clock_total -= irqCycles;

                        switch (g_interruptMode)
                        {
                            case 0:
                                // IM 0: treat as RST 38h for SMS (common VDP behavior)
                                {
                                    ushort spBefore = g_reg_SP;
                                    ushort pcPushed = g_reg_PC;
                                    bool spInRam = spBefore >= 0x0000 && spBefore <= 0x1FFF;
                                    bool spInRom = spBefore >= 0x2000 && spBefore <= 0x3FFF;
                                    if (TraceZ80IntDebug)
                                        Console.WriteLine($"[Z80-INT-IM0] frame={md_main.g_md_vdp?.FrameCounter ?? -1} pc=0x{g_reg_PC:X4} SP=0x{spBefore:X4} pushPC=0x{pcPushed:X4} spRegion={(spInRam ? "RAM" : spInRom ? "ROM" : "INVALID")} src={_irqSource} status=0x{_irqStatus:X2}");
                                    stack_push(g_reg_PCH);
                                    stack_push(g_reg_PCL);
                                    g_reg_PC = 0x0038;
                                    g_halt = false;
                                    CountZ80IntService(); // [INT-STATS]
                                    break;
                                }

                            case 1:
                                {
                                    // [INT-INSTRUMENTATION] Log SP and push before stack operation
                                    ushort spBefore = g_reg_SP;
                                    ushort pcPushed = g_reg_PC;
                                    bool spInRam = spBefore >= 0x0000 && spBefore <= 0x1FFF;
                                    bool spInRom = spBefore >= 0x2000 && spBefore <= 0x3FFF;
                                    if (TraceZ80IntDebug)
                                        Console.WriteLine($"[Z80-INT-IM1] frame={md_main.g_md_vdp?.FrameCounter ?? -1} pc=0x{g_reg_PC:X4} SP=0x{spBefore:X4} pushPC=0x{pcPushed:X4} spRegion={(spInRam ? "RAM" : spInRom ? "ROM" : "INVALID")} src={_irqSource} status=0x{_irqStatus:X2}");
                                    stack_push(g_reg_PCH);
                                    stack_push(g_reg_PCL);
                                    g_reg_PC = 0x0038;
                                    g_halt = false;
                                    CountZ80IntService(); // [INT-STATS]
                                    break;
                                }

                            case 2:
                                {
                                    // [INT-INSTRUMENTATION] Log SP and push before stack operation
                                    ushort spBefore = g_reg_SP;
                                    ushort pcPushed = g_reg_PC;
                                    bool spInRam = spBefore >= 0x0000 && spBefore <= 0x1FFF;
                                    bool spInRom = spBefore >= 0x2000 && spBefore <= 0x3FFF;
                                    if (TraceZ80IntDebug)
                                        Console.WriteLine($"[Z80-INT-IM2] frame={md_main.g_md_vdp?.FrameCounter ?? -1} pc=0x{g_reg_PC:X4} SP=0x{spBefore:X4} pushPC=0x{pcPushed:X4} spRegion={(spInRam ? "RAM" : spInRom ? "ROM" : "INVALID")} src={_irqSource} status=0x{_irqStatus:X2}");
                                    // IM 2: vektor via I-register
                                    stack_push(g_reg_PCH);
                                    stack_push(g_reg_PCL);
                                    byte vector = Z80Im2Vector;
                                    ushort vectorAddr = (ushort)((g_reg_I << 8) | vector);
                                    byte lo = read8(vectorAddr);
                                    byte hi = read8((ushort)(vectorAddr + 1));
                                    g_reg_PC = (ushort)((hi << 8) | lo);
                                    g_halt = false;
                                    CountZ80IntService(); // [INT-STATS]
                                    break;
                                }
                        }
                    }
                }

            g_clock = 0;

            if (_forcePcPending)
            {
                g_reg_PC = _forcePcTarget;
                _forcePcPending = false;
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string pcdump = DumpZ80PcBytes(g_reg_PC, 0, 7);
                Console.WriteLine(
                    $"[Z80PC-FORCE] frame={frame} pc=0x{g_reg_PC:X4} reason={_forcePcReason} bytes={pcdump}");
            }
            ushort pcBefore = g_reg_PC;
            RecordPcHist(pcBefore);
            if (md_main.g_masterSystemMode && TraceSms77ee && _sms77eeLogCount < 2000 && pcBefore == 0x77EE)
            {
                if (_sms77eeLogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _sms77eeLogPath = Path.Combine(dir, "sms_77ee_debug.log");
                    File.WriteAllText(_sms77eeLogPath, "SMS 77EE debug log\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                ushort sp = g_reg_SP;
                byte lo = read8(sp);
                byte hi = read8((ushort)(sp + 1));
                ushort ret = (ushort)((hi << 8) | lo);
                File.AppendAllText(_sms77eeLogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} prev=0x{_prevPc:X4} SP=0x{sp:X4} ret=0x{ret:X4}\n");
                _sms77eeLogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSms7728 && _sms7728LogCount < 2000 && pcBefore == 0x7728)
            {
                if (_sms7728LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _sms7728LogPath = Path.Combine(dir, "sms_7728_debug.log");
                    File.WriteAllText(_sms7728LogPath, "SMS 7728 debug log\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                ushort sp = g_reg_SP;
                byte lo = read8(sp);
                byte hi = read8((ushort)(sp + 1));
                ushort ret = (ushort)((hi << 8) | lo);
                File.AppendAllText(_sms7728LogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} prev=0x{_prevPc:X4} SP=0x{sp:X4} ret=0x{ret:X4}\n");
                _sms7728LogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSmsPc96 && _smsPc96LogCount < SmsPc96LogLimit &&
                pcBefore >= 0x9680 && pcBefore <= 0x96B0)
            {
                if (_smsPc96LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsPc96LogPath = Path.Combine(dir, "sms_pc_96.log");
                    File.WriteAllText(_smsPc96LogPath, "SMS PC log (0x9680-0x96B0)\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string bytes = DumpZ80PcBytes(pcBefore, 0, 7);
                string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
                File.AppendAllText(_smsPc96LogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} prev=0x{_prevPc:X4} op=0x{g_opcode1:X2} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} op4=0x{g_opcode4:X2} {flags} bytes={bytes}\n");
                _smsPc96LogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSmsPc07 && _smsPc07LogCount < SmsPc07LogLimit &&
                pcBefore >= 0x0700 && pcBefore <= 0x0730)
            {
                if (_smsPc07LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsPc07LogPath = Path.Combine(dir, "sms_pc_07.log");
                    File.WriteAllText(_smsPc07LogPath, "SMS PC log (0x0700-0x0730)\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string bytes = DumpZ80PcBytes(pcBefore, 0, 7);
                string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
                File.AppendAllText(_smsPc07LogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} prev=0x{_prevPc:X4} op=0x{g_opcode1:X2} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} op4=0x{g_opcode4:X2} {flags} bytes={bytes}\n");
                _smsPc07LogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSmsPc41 && _smsPc41LogCount < SmsPc41LogLimit &&
                pcBefore >= 0x4130 && pcBefore <= 0x4180)
            {
                if (_smsPc41LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsPc41LogPath = Path.Combine(dir, "sms_pc_41.log");
                    File.WriteAllText(_smsPc41LogPath, "SMS PC log (0x4130-0x4180)\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string bytes = DumpZ80PcBytes(pcBefore, 0, 7);
                string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
                File.AppendAllText(_smsPc41LogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} prev=0x{_prevPc:X4} op=0x{g_opcode1:X2} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} op4=0x{g_opcode4:X2} {flags} bytes={bytes}\n");
                _smsPc41LogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSmsPc77Pre && _smsPc77PreLogCount < SmsPc77PreLogLimit &&
                pcBefore >= 0x7700 && pcBefore <= 0x7750)
            {
                if (_smsPc77PreLogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsPc77PreLogPath = Path.Combine(dir, "sms_pc_77_pre.log");
                    File.WriteAllText(_smsPc77PreLogPath, "SMS PC log (0x7700-0x7750)\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string bytes = DumpZ80PcBytes(pcBefore, 0, 7);
                string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
                File.AppendAllText(_smsPc77PreLogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} op=0x{g_opcode1:X2} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} op4=0x{g_opcode4:X2} {flags} bytes={bytes}\n");
                _smsPc77PreLogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSmsPc54 && _smsPc54LogCount < SmsPc54LogLimit &&
                pcBefore >= 0x54E0 && pcBefore <= 0x5510)
            {
                if (_smsPc54LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsPc54LogPath = Path.Combine(dir, "sms_pc_54.log");
                    File.WriteAllText(_smsPc54LogPath, "SMS PC log (0x54E0-0x5510)\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string bytes = DumpZ80PcBytes(pcBefore, 0, 7);
                string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
                File.AppendAllText(_smsPc54LogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} op=0x{g_opcode1:X2} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} op4=0x{g_opcode4:X2} {flags} bytes={bytes}\n");
                _smsPc54LogCount++;
            }
            if (md_main.g_masterSystemMode && TraceSmsPc77 && _smsPc77LogCount < SmsPc77LogLimit &&
                pcBefore >= 0x77AB && pcBefore <= 0x7805)
            {
                if (_smsPc77LogPath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _smsPc77LogPath = Path.Combine(dir, "sms_pc_77.log");
                    File.WriteAllText(_smsPc77LogPath, "SMS PC log (0x77AB-0x7805)\n");
                }
                long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string bytes = DumpZ80PcBytes(pcBefore, 0, 7);
                string flags = $"S={g_flag_S} Z={g_flag_Z} H={g_flag_H} P/V={g_flag_PV} N={g_flag_N} C={g_flag_C}";
                File.AppendAllText(_smsPc77LogPath,
                    $"frame={frame} pc=0x{pcBefore:X4} op=0x{g_opcode1:X2} op2=0x{g_opcode2:X2} op3=0x{g_opcode3:X2} op4=0x{g_opcode4:X2} {flags} bytes={bytes}\n");
                _smsPc77LogCount++;
            }
            _prevPc = pcBefore;
            if (TraceSmsDelay && pcBefore == 0x056B)
            {
                LogSmsDelayEntry(pcBefore);
                if (SkipSmsDelay)
                {
                    g_reg_PC = 0x057A;
                    continue;
                }
                else if (TraceSmsIrq && md_main.g_masterSystemMode && _traceSmsIrqBlockCount < 8)
                {
                    _traceSmsIrqBlockCount++;
                    Console.WriteLine($"[SMS IRQ] blocked IFF1=0 PC=0x{g_reg_PC:X4} IM={g_interruptMode}");
                }
            }

            byte opcode = g_opcode1;
            byte opcode2 = g_opcode2;
            byte opcode3 = g_opcode3;
            byte opcode4 = g_opcode4;
            ushort ixBefore = g_reg_IX;

            // [VERIFY-RAM-INTEGRITY] Compare opcode fetch with direct RAM read
            // This verifies that ReadOpcodeByte(PC) == read8(PC) == g_ram[PC & 0x1FFF]
            if (MdTracerCore.MdLog.Enabled && string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80_RAM_MISMATCH"), "1", StringComparison.Ordinal) &&
                pcBefore < 0x0040 && pcBefore < 0x2000)
            {
                ushort z80Addr = (ushort)(pcBefore & 0x1FFF);
                byte fromRam = g_ram != null ? g_ram[z80Addr] : (byte)0xFF;
                byte fromRead8 = md_main.g_md_z80?.read8(pcBefore) ?? (byte)0xFF;
                if (opcode != fromRam || opcode != fromRead8)
                {
                    Console.WriteLine($"[Z80-RAM-MISMATCH] pc=0x{pcBefore:X4} opcode=0x{opcode:X2} read8=0x{fromRead8:X2} ram[0x{z80Addr:X4}]=0x{fromRam:X2}");
                }
            }

            if (TraceZ80Step && _traceStepRemaining > 0)
            {
                _traceStepRemaining--;
                Console.WriteLine($"[Z80STEP] pc=0x{pcBefore:X4} op=0x{opcode:X2} busreq={(busRequested ? 1 : 0)} reset={(z80reset ? 1 : 0)}");
            }
            
            // DEBUG: Log Z80 instructions at specific PC for debugging
            // if (md_main.g_md_vdp?.FrameCounter >= 4900 && md_main.g_md_vdp.FrameCounter <= 4910 && _sonic2DebugCount < 10)
            // {
            //     _sonic2DebugCount++;
            //     Console.WriteLine($"[Z80-STEP-DEBUG] frame={md_main.g_md_vdp.FrameCounter} pc=0x{pcBefore:X4} op=0x{opcode:X2} busreq={(busRequested ? 1 : 0)} reset={(z80reset ? 1 : 0)} active={g_active}");
            // }
            
            // DEBUG: Log Z80 instructions at specific PC for debugging
            // if (pcBefore == 0x0167 && _sonic2DebugCount < 20)
            // {
            //     _sonic2DebugCount++;
            //     // Also dump bytes around PC to see what code is there
            //     string bytes = "";
            //     for (int i = -4; i <= 4; i++)
            //     {
            //         ushort addr = (ushort)(pcBefore + i);
            //         if (addr >= 0 && addr < 0x2000) // Z80 RAM range
            //         {
            //             byte b = md_main.g_md_bus?.Z80Read8(addr) ?? 0xFF;
            //             bytes += $"{b:X2} ";
            //         }
            //         else
            //         {
            //             bytes += "?? ";
            //         }
            //     }
            //     Console.WriteLine($"[Z80-DRIVER-ENTRY-DEBUG] frame={md_main.g_md_vdp?.FrameCounter ?? -1} pc=0x{pcBefore:X4} op=0x{opcode:X2} bytes: {bytes}");
            // }
            
            long frameNow = md_main.g_md_vdp?.FrameCounter ?? -1;
            if (DumpZ80RamFrame >= 0 && !_z80DumpedFrame && frameNow >= DumpZ80RamFrame)
                MaybeDumpZ80RamFrame($"frame>={DumpZ80RamFrame}");

            // DEBUG: Log Z80 instructions at PC 0x0167 (Sonic 2 driver entry)
            if (pcBefore == 0x0167 && _sonic2DebugCount < 20)
            {
                _sonic2DebugCount++;
                // Also dump bytes around PC to see what code is there
                string bytes = "";
                for (int i = -4; i <= 4; i++)
                {
                    ushort addr = (ushort)(pcBefore + i);
                    if (addr >= 0 && addr < 0x2000)
                    {
                        byte val = PeekZ80Ram(addr);
                        bytes += $" {addr:X4}:{val:X2}";
                    }
                }
                if (TraceZ80Boot)
                    Console.WriteLine($"[SONIC2-Z80-0167] frame={frameNow} pc=0x{pcBefore:X4} op=0x{opcode:X2} nextPC=0x{g_reg_PC:X4} active={g_active} bytes={bytes}");
                

            }
            // [BOOT-DEBUG] Log when Z80 reaches address 0x0000
            if (TraceZ80Boot && pcBefore == 0x0000 && _z80DebugCount < 10)
            {
                _z80DebugCount++;
                Console.WriteLine($"[Z80-0000] frame={frameNow} pc=0x{pcBefore:X4} op=0x{opcode:X2} SP=0x{g_reg_SP:X4} - Executing from address 0x0000");
                // Dump first 16 bytes of RAM
                if (g_ram != null && g_ram.Length >= 0x10)
                {
                    Console.WriteLine($"[Z80-RAM-0000] 0x0000-0x000F: {g_ram[0x00]:X2} {g_ram[0x01]:X2} {g_ram[0x02]:X2} {g_ram[0x03]:X2} {g_ram[0x04]:X2} {g_ram[0x05]:X2} {g_ram[0x06]:X2} {g_ram[0x07]:X2} {g_ram[0x08]:X2} {g_ram[0x09]:X2} {g_ram[0x0A]:X2} {g_ram[0x0B]:X2} {g_ram[0x0C]:X2} {g_ram[0x0D]:X2} {g_ram[0x0E]:X2} {g_ram[0x0F]:X2}");
                }
            }
            
            // [BOOT-DEBUG] Log when Z80 reaches boot code execution
            if (TraceZ80Boot && pcBefore == 0x0040)
            {
                Console.WriteLine($"[Z80-BOOT-CODE] frame={frameNow} pc=0x{pcBefore:X4} SP=0x{g_reg_SP:X4} - Entering boot code (DI, LD SP, JP)");
                // Dump boot code bytes to verify it hasn't been overwritten
                if (g_ram != null && g_ram.Length >= 0x50)
                {
                    Console.WriteLine($"[Z80-BOOT-DUMP] 0x0040-0x004F: {g_ram[0x40]:X2} {g_ram[0x41]:X2} {g_ram[0x42]:X2} {g_ram[0x43]:X2} {g_ram[0x44]:X2} {g_ram[0x45]:X2} {g_ram[0x46]:X2} {g_ram[0x47]:X2} {g_ram[0x48]:X2} {g_ram[0x49]:X2} {g_ram[0x4A]:X2} {g_ram[0x4B]:X2} {g_ram[0x4C]:X2} {g_ram[0x4D]:X2} {g_ram[0x4E]:X2} {g_ram[0x4F]:X2}");
                }
                // Also dump driver area to verify driver was uploaded
                if (g_ram != null && g_ram.Length >= 0x180)
                {
                    Console.WriteLine($"[Z80-RAM-DUMP] 0x0160-0x017F: {g_ram[0x160]:X2} {g_ram[0x161]:X2} {g_ram[0x162]:X2} {g_ram[0x163]:X2} {g_ram[0x164]:X2} {g_ram[0x165]:X2} {g_ram[0x166]:X2} {g_ram[0x167]:X2} {g_ram[0x168]:X2} {g_ram[0x169]:X2} {g_ram[0x16A]:X2} {g_ram[0x16B]:X2} {g_ram[0x16C]:X2} {g_ram[0x16D]:X2} {g_ram[0x16E]:X2} {g_ram[0x16F]:X2}");
                    Console.WriteLine($"[Z80-RAM-DUMP] 0x0170-0x017F: {g_ram[0x170]:X2} {g_ram[0x171]:X2} {g_ram[0x172]:X2} {g_ram[0x173]:X2} {g_ram[0x174]:X2} {g_ram[0x175]:X2} {g_ram[0x176]:X2} {g_ram[0x177]:X2} {g_ram[0x178]:X2} {g_ram[0x179]:X2} {g_ram[0x17A]:X2} {g_ram[0x17B]:X2} {g_ram[0x17C]:X2} {g_ram[0x17D]:X2} {g_ram[0x17E]:X2} {g_ram[0x17F]:X2}");
                }
            }
            // [BOOT-DEBUG] Log when Z80 reaches driver entry
            if (TraceZ80Boot && pcBefore == 0x0167)
            {
                Console.WriteLine($"[Z80-DRIVER-ENTRY] frame={frameNow} pc=0x{pcBefore:X4} SP=0x{g_reg_SP:X4} - Jumping to Z80 driver!");
            }
            // [Z80-BANK] Log when Z80 enters banked memory area (0x8000+)
            if (TraceZ80Boot && pcBefore >= 0x8000 && pcBefore <= 0xFFFF && !_z80BankEntryLogged)
            {
                uint bankBase = GetBankBase();
                Console.WriteLine($"[Z80-BANK] frame={frameNow} pc=0x{pcBefore:X4} bankReg=0x{g_bank_register:X3} bankBase=0x{bankBase:X6} - Entering banked memory!");
                _z80BankEntryLogged = true;
            }
            bool log0576After = TraceSmsDelay && !_delayExitLogged && pcBefore == 0x0576;
            bool djnzTracePending = TraceSmsDelay && opcode == 0x10 && _djnzLogCount < DjnzLogLimit;
            byte djnzBefore = g_reg_B;
            ushort djnzTarget = 0;
            if (djnzTracePending)
                djnzTarget = (ushort)((int)pcBefore + 2 + (sbyte)opcode2);

            if (md_main.g_masterSystemMode && MdTracerCore.MdLog.TraceZ80InstructionLogging)
            {
                if (_smsInstructionLog < SmsInstructionLogLimit)
                    MdTracerCore.MdLog.WriteLine($"[md_z80 SMS exec] PC=0x{g_reg_PC:X4} OPCODE=0x{opcode:X2}");

                if (g_reg_PC == _smsLoopPc)
                {
                    _smsLoopCount++;
                    if (_smsLoopCount == SmsLoopReportThreshold)
                        MdTracerCore.MdLog.WriteLine($"[md_z80 SMS loop] PC=0x{g_reg_PC:X4} repeated {SmsLoopReportThreshold} times");
                }
                else
                {
                    _smsLoopPc = g_reg_PC;
                    _smsLoopCount = 1;
                }

                _smsInstructionLog++;
            }

            ThrottleInstructionLog(opcode);
            bool traceDdcbBit = TraceZ80DdcbBit &&
                                _z80DdcbBitRemaining > 0 &&
                                pcBefore >= 0x0DD0 &&
                                pcBefore <= 0x0DF0;
            bool tracePcRange = TraceZ80PcRangeStart.HasValue &&
                                TraceZ80PcRangeEnd.HasValue &&
                                _z80PcRangeRemaining > 0 &&
                                pcBefore >= TraceZ80PcRangeStart.Value &&
                                pcBefore <= TraceZ80PcRangeEnd.Value;
            bool logDdcbBit = false;
            bool logJrNz = false;
            bool logRetNz = false;
            byte ddcbDisp = 0;
            ushort ddcbEa = 0;
            byte ddcbMem = 0;
            int ddcbBit = 0;
            byte flagBefore = 0;
            if (traceDdcbBit)
            {
                if (opcode == 0xDD && opcode2 == 0xCB && opcode4 == 0x56)
                {
                    logDdcbBit = true;
                    ddcbDisp = opcode3;
                    ddcbEa = (ushort)(ixBefore + (sbyte)ddcbDisp);
                    ddcbMem = PeekZ80ByteNoSideEffect(ddcbEa);
                    ddcbBit = ((ddcbMem & 0x04) != 0) ? 1 : 0;
                    flagBefore = g_status_flag;
                }
                else if (opcode == 0x20)
                {
                    logJrNz = true;
                    flagBefore = g_status_flag;
                }
                else if (opcode == 0xC0)
                {
                    logRetNz = true;
                    flagBefore = g_status_flag;
                }
            }
            if (tracePcRange)
            {
                long pcDebugFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine(
                    $"[Z80PC] frame={pcDebugFrame} pc=0x{pcBefore:X4} op=0x{opcode:X2} op2=0x{opcode2:X2} " +
                    $"op3=0x{opcode3:X2} op4=0x{opcode4:X2} " +
                    $"A=0x{g_reg_A:X2} BC=0x{g_reg_BC:X4} DE=0x{g_reg_DE:X4} HL=0x{g_reg_HL:X4} SP=0x{g_reg_SP:X4}");
            }
            LogZ80PcFile(pcBefore, opcode, opcode2, opcode3, opcode4);
            g_operand[opcode]();   // exekvera en instruktion
            if (_waitCycles > 0)
            {
                g_clock += _waitCycles;
                _waitCycles = 0;
            }
            _bootInstrCount++;
            _totalCycles += g_clock;
            MaybeLogZ80Speed(g_clock);
            cyclesConsumed += g_clock;
            LineCycles += g_clock;
            _systemCycleDelta += g_clock;
            if (TraceZ80Stats)
                _z80StatsCycleCount += g_clock;
            // Advance YM2612 based on elapsed SystemCycles
            md_main.g_md_music?.g_md_ym2612.TickTimersFromZ80Cycles(g_clock);
            TickIrqAutoClear(g_clock);
            ApplyEiDelay();

            if (log0576After)
                LogSmsDelayExit(pcBefore);
            if (tracePcRange)
            {
                long pcFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine(
                    $"[Z80PC] frame={pcFrame} nextpc=0x{g_reg_PC:X4} F=0x{g_status_flag:X2}");
                if (_z80PcRangeRemaining != int.MaxValue)
                    _z80PcRangeRemaining--;
            }
            
            // Debug: Always log first 100 instructions
            if (TraceZ80Boot && _bootInstrCount <= 100)
            {
                long traceFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine($"[Z80-TRACE-{_bootInstrCount}] frame={traceFrame} pc=0x{pcBefore:X4}->0x{g_reg_PC:X4} op=0x{opcode:X2} active={g_active} SP=0x{g_reg_SP:X4}");
            }
            
            // Debug: Log if we reach sound driver
            if (TraceZ80Boot && pcBefore == 0x0003 && opcode == 0xC3) // JP instruction at address 0x0003
            {
                Console.WriteLine($"[Z80-JP-0167] Jumping from 0x{pcBefore:X4} to 0x{g_reg_PC:X4}");
            }
            
            // Debug: Log first few instructions after reset
            if (TraceZ80Boot && _bootInstrCount <= 20)
            {
                Console.WriteLine($"[Z80BOOT] instr={_bootInstrCount} pc=0x{pcBefore:X4}->0x{g_reg_PC:X4} opcode=0x{opcode:X2} cycles={g_clock}");
            }
            
            // Debug: Log when Z80 actually executes
            if (TraceZ80Boot && _bootInstrCount == 1)
            {
                Console.WriteLine($"[Z80-FIRST-INSTR] pc=0x{pcBefore:X4}->0x{g_reg_PC:X4} opcode=0x{opcode:X2} g_active={g_active}");
            }
            
            // Debug: Log all instructions for first 50
            if (TraceZ80Boot && _bootInstrCount <= 50)
            {
                Console.WriteLine($"[Z80-EXEC-{_bootInstrCount}] pc=0x{pcBefore:X4}->0x{g_reg_PC:X4} opcode=0x{opcode:X2} SP=0x{g_reg_SP:X4}");
            }
            if (TraceZ80BootFile && _bootFileLogged < TraceZ80BootFileLimit)
            {
                if (_bootFilePath == null)
                {
                    string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
                    if (string.IsNullOrWhiteSpace(dir))
                        dir = "/home/nichlas/EutherDrive/logs";
                    Directory.CreateDirectory(dir);
                    _bootFilePath = Path.Combine(dir, "z80_boot_trace.log");
                    File.WriteAllText(_bootFilePath, "Z80 boot trace\n");
                }

                long traceFrame = md_main.g_md_vdp?.FrameCounter ?? -1;
                string line =
                    $"frame={traceFrame} pc=0x{pcBefore:X4}->0x{g_reg_PC:X4} op=0x{opcode:X2} " +
                    $"SP=0x{g_reg_SP:X4} A=0x{g_reg_A:X2} BC=0x{g_reg_BC:X4} DE=0x{g_reg_DE:X4} HL=0x{g_reg_HL:X4}\n";
                File.AppendAllText(_bootFilePath, line);
                _bootFileLogged++;
            }

            if (djnzTracePending)
            {
                byte djnzAfter = g_reg_B;
                bool taken = g_reg_PC != (ushort)(pcBefore + 2);
                Console.WriteLine($"[SMS DJNZ] PC=0x{pcBefore:X4} B before=0x{djnzBefore:X2} after=0x{djnzAfter:X2} taken={(taken ? 1 : 0)} target=0x{djnzTarget:X4}");
                _djnzLogCount++;
            }
            if (ForceZ80FlagJr &&
                pcBefore == 0x0DD8 &&
                opcode == 0xDD &&
                opcode2 == 0xCB &&
                opcode4 == 0x56)
            {
                ushort ea = (ushort)(ixBefore + (sbyte)opcode3);
                byte mem = PeekZ80ByteNoSideEffect(ea);
                if ((mem & 0x04) != 0)
                {
                    ushort target = ForceZ80FlagJrTargetOverride ?? ForceZ80FlagJrFallback;
                    if (!ForceZ80FlagJrTargetOverride.HasValue)
                    {
                        ushort jrPc = (ushort)(pcBefore + 4);
                        byte jrOp = ReadOpcodeByte(jrPc);
                        byte jrDisp = ReadOpcodeByte((ushort)(jrPc + 1));
                        if (jrOp == 0x20)
                            target = (ushort)(jrPc + 2 + (sbyte)jrDisp);
                    }
                    g_reg_PC = target;
                    if (_forceZ80FlagJrRemaining > 0)
                    {
                        Console.WriteLine(
                            $"[Z80FLAG-JR] pc=0x{pcBefore:X4} ix=0x{ixBefore:X4} ea=0x{ea:X4} " +
                            $"mem=0x{mem:X2} -> pc=0x{g_reg_PC:X4}");
                        if (_forceZ80FlagJrRemaining != int.MaxValue)
                            _forceZ80FlagJrRemaining--;
                    }
                }
            }
            if (traceDdcbBit && (logDdcbBit || logJrNz || logRetNz) && _z80DdcbBitRemaining > 0)
            {
                byte flagAfter = g_status_flag;
                int zFlag = g_flag_Z != 0 ? 1 : 0;
                ushort pcAfter = g_reg_PC;
                bool taken = false;
                string instr = "BIT";
                if (logJrNz)
                {
                    instr = "JR";
                    taken = pcAfter != (ushort)(pcBefore + 2);
                }
                else if (logRetNz)
                {
                    instr = "RET";
                    taken = pcAfter != (ushort)(pcBefore + 1);
                }
                string dispText = logDdcbBit ? $"0x{ddcbDisp:X2}" : "--";
                string eaText = logDdcbBit ? $"0x{ddcbEa:X4}" : "----";
                string memText = logDdcbBit ? $"0x{ddcbMem:X2}" : "--";
                string bitText = logDdcbBit ? (ddcbBit != 0 ? "1" : "0") : "-";
                long z80Frame = md_main.g_md_vdp?.FrameCounter ?? -1;
                Console.WriteLine(
                    $"[Z80DDCB] frame={z80Frame} pc=0x{pcBefore:X4} ix=0x{g_reg_IX:X4} iy=0x{g_reg_IY:X4} sp=0x{g_reg_SP:X4} " +
                    $"d={dispText} ea={eaText} mem={memText} bit2={bitText} F(before)=0x{flagBefore:X2} " +
                    $"F(after)=0x{flagAfter:X2} Z={zFlag} nextpc=0x{pcAfter:X4} instr={instr} taken={(taken ? 1 : 0)}");
                _z80DdcbBitRemaining--;
            }
            if (opcode == 0xC0 || opcode == 0xC9)
            {
                ushort pcAfter = g_reg_PC;
                bool taken = opcode == 0xC0 ? pcAfter != (ushort)(pcBefore + 1) : true;
                if (taken &&
                    (pcBefore == 0x0DDC || pcBefore == 0x0DEE ||
                     pcAfter == 0x0E75 || pcAfter == 0x0E84))
                {
                    MaybeArmZ80AfterFlagRetTrace(pcBefore, pcAfter);
                }
            }
            TrackPcBootRange();

            if (md_main.g_masterSystemMode &&
                (g_reg_PC == 0x0571 || g_reg_PC == 0x0572 || g_reg_PC == 0x0573 || g_reg_PC == 0x0574))
            {
                if ((_smsDelayTrace++ & SmsDelayTraceMask) == 0)
                {
                    bool zero = g_flag_Z != 0;
                    MdTracerCore.MdLog.WriteLine($"[md_z80 SMS delay] PC=0x{g_reg_PC:X4} BC=0x{g_reg_BC:X4} Z={(zero ? 1 : 0)}");
                }
                LogSmsLoopRegs();
            }

        #if DEBUG
        //traceout();
        //logout2();
        #endif

            g_reg_R = (byte)((g_reg_R + 1) & 0x7f);
            g_clock_total -= g_clock;
        }
        AccumulateLineCycles(in_clock, cyclesConsumed);
        MaybeLogZ80Stats();

        // Master cycles are advanced in md_main.cs when Z80 runs
        // No need to handle YM2612 timing here anymore
    }

    private void RecordPcHist(ushort pc)
    {
        if (!TraceZ80PcHist)
            return;
        _pcHist[pc]++;
        _pcHistTotal++;
    }

    private static void LogZ80PcFile(ushort pc, byte op1, byte op2, byte op3, byte op4)
    {
        if (!TraceZ80PcFile)
            return;
        if (_traceZ80PcFileCount >= TraceZ80PcFileLimit)
            return;
        if (_traceZ80PcFilePath == null)
        {
            string dir = Environment.GetEnvironmentVariable("EUTHERDRIVE_SMS_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = "/home/nichlas/EutherDrive/logs";
            Directory.CreateDirectory(dir);
            _traceZ80PcFilePath = Path.Combine(dir, "z80_pc_trace.log");
            File.WriteAllText(_traceZ80PcFilePath, "Z80 PC trace\n");
        }
        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
        string line = $"frame={frame} pc=0x{pc:X4} op=0x{op1:X2} op2=0x{op2:X2} op3=0x{op3:X2} op4=0x{op4:X2}\n";
        File.AppendAllText(_traceZ80PcFilePath, line);
        _traceZ80PcFileCount++;
    }

    internal void FlushPcHist(long frame)
    {
        if (!TraceZ80PcHist)
            return;
        if (_pcHistTotal == 0)
        {
            Console.WriteLine($"[Z80PC-HIST] frame={frame} total=0");
            return;
        }

        int limit = TraceZ80PcHistLimit > 0 ? TraceZ80PcHistLimit : 1;
        Span<int> topCount = stackalloc int[limit];
        Span<int> topPc = stackalloc int[limit];
        for (int i = 0; i < limit; i++)
            topPc[i] = -1;

        for (int pc = 0; pc < 0x10000; pc++)
        {
            int count = _pcHist[pc];
            if (count < TraceZ80PcHistMin)
                continue;
            for (int i = 0; i < limit; i++)
            {
                if (topPc[i] == pc)
                {
                    topCount[i] = count;
                    goto NextPc;
                }
            }
            for (int i = 0; i < limit; i++)
            {
                if (count > topCount[i])
                {
                    for (int j = limit - 1; j > i; j--)
                    {
                        topCount[j] = topCount[j - 1];
                        topPc[j] = topPc[j - 1];
                    }
                    topCount[i] = count;
                    topPc[i] = pc;
                    break;
                }
            }
NextPc:;
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < limit; i++)
        {
            if (topPc[i] < 0)
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append("0x");
            sb.Append(topPc[i].ToString("X4"));
            sb.Append(':');
            sb.Append(topCount[i]);
        }

        Console.WriteLine($"[Z80PC-HIST] frame={frame} total={_pcHistTotal} bins={sb}");
        Array.Clear(_pcHist, 0, _pcHist.Length);
        _pcHistTotal = 0;
    }

    private void ThrottleInstructionLog(byte opcode)
    {
        if (!TraceZ80Console)
            return;

        _instrThrottleCounter++;
        if (_instrThrottleCounter >= 1_000_000)
        {
            _instrThrottleCounter = 0;
            Console.WriteLine($"[Z80] PC=0x{g_reg_PC:X4} OP=0x{opcode:X2} bank=0x{g_bank_register:X6} SP=0x{g_reg_SP:X4}");
        }
    }

    private void TrackPcBootRange()
    {
        if (g_reg_PC > 0x01FF)
        {
            _pcLeftBootRangeSinceLastSummary = true;
            MaybeDumpZ80Ram("pc>01FF");
        }
    }

    private void MaybeDumpZ80Ram(string reason)
    {
        if (!DumpZ80Ram || _z80Dumped)
            return;
        if (g_ram == null || g_ram.Length == 0)
            return;
        _z80Dumped = true;
        DumpZ80RamCore(reason);
    }

    private void MaybeDumpZ80RamFrame(string reason)
    {
        if (!DumpZ80Ram || _z80DumpedFrame)
            return;
        if (g_ram == null || g_ram.Length == 0)
            return;
        _z80DumpedFrame = true;
        DumpZ80RamCore(reason);
    }

    private void DumpZ80RamCore(string reason)
    {
        Console.WriteLine(
            $"[Z80DUMP] reason={reason} PC=0x{g_reg_PC:X4} SP=0x{g_reg_SP:X4} " +
            $"A=0x{g_reg_A:X2} BC=0x{g_reg_BC:X4} " +
            $"DE=0x{g_reg_DE:X4} HL=0x{g_reg_HL:X4} IX=0x{g_reg_IX:X4} " +
            $"IY=0x{g_reg_IY:X4} I=0x{g_reg_I:X2} R=0x{g_reg_R:X2} bank=0x{g_bank_register:X6}");
        DumpZ80Region(0x0000, 0x0200);
        if (DumpZ80RamExtra)
        {
            DumpZ80Region(0x0500, 0x0200);
            DumpZ80Region(0x0600, 0x0200);
            DumpZ80Region(0x0900, 0x0200);
            DumpZ80Region(0x0B00, 0x0200);
        }
        DumpZ80Region(0x1B80, 0x0080);
    }

    private void DumpZ80Region(int start, int length)
    {
        int end = Math.Min(start + length, g_ram.Length);
        for (int addr = start; addr < end; addr += 16)
        {
            int lineLen = Math.Min(16, end - addr);
            var sb = new StringBuilder(16 * 3 + 16);
            sb.Append("[Z80RAM] ").Append(addr.ToString("X4")).Append(": ");
            for (int i = 0; i < lineLen; i++)
            {
                sb.Append(g_ram[addr + i].ToString("X2")).Append(' ');
            }
            Console.WriteLine(sb.ToString().TrimEnd());
        }
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            table[i] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFFu;
        int end = offset + length;
        for (int i = offset; i < end; i++)
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    internal void DumpRamRangeWithChecksum(ushort start, ushort end, string reason)
    {
        if (g_ram == null || g_ram.Length == 0)
            return;
        ushort rangeStart = start;
        ushort rangeEnd = end;
        if (rangeStart > rangeEnd)
        {
            ushort tmp = rangeStart;
            rangeStart = rangeEnd;
            rangeEnd = tmp;
        }
        if (rangeStart >= g_ram.Length)
            return;
        int cappedEnd = Math.Min(rangeEnd, g_ram.Length - 1);
        int length = cappedEnd - rangeStart + 1;
        uint crc = ComputeCrc32(g_ram, rangeStart, length);
        Console.WriteLine(
            $"[Z80WIN-DUMP] reason={reason} range=0x{rangeStart:X4}..0x{cappedEnd:X4} len=0x{length:X4} crc32=0x{crc:X8}");
        for (int addr = rangeStart; addr <= cappedEnd; addr += 16)
        {
            int lineLen = Math.Min(16, cappedEnd - addr + 1);
            var sb = new StringBuilder(16 * 3 + 16);
            sb.Append("[Z80WIN-DUMP] ").Append(addr.ToString("X4")).Append(": ");
            for (int i = 0; i < lineLen; i++)
                sb.Append(g_ram[addr + i].ToString("X2")).Append(' ');
            Console.WriteLine(sb.ToString().TrimEnd());
        }
    }

    private void LogSmsDelayEntry(ushort pc)
    {
        if (_delayEntryLogged)
            return;

        ushort ret = PeekStackReturn();
        Console.WriteLine($"[SMS DELAY] entry PC=0x{pc:X4} B=0x{g_reg_B:X2} BC=0x{g_reg_BC:X4} SP=0x{g_reg_SP:X4} ret=0x{ret:X4}");
        _delayEntryLogged = true;
        if (SkipSmsDelay)
            Console.WriteLine("[SMS DELAY] skipping delay to 0x057A");
    }

    private void LogSmsDelayExit(ushort pc)
    {
        if (_delayExitLogged)
            return;

        Console.WriteLine($"[SMS DELAY] exit PC=0x{pc:X4} B=0x{g_reg_B:X2} C=0x{g_reg_C:X2} SP=0x{g_reg_SP:X4}");
        _delayExitLogged = true;
    }

    private ushort PeekStackReturn()
    {
        byte lo = read_byte(g_reg_SP);
        byte hi = read_byte((ushort)(g_reg_SP + 1));
        return (ushort)((hi << 8) | lo);
    }

    private void AccumulateLineCycles(int budget, int consumed)
    {
        _cyclesBudgetAccum += budget;
        _cyclesActualAccum += consumed;
    }

    internal (int actual, int budget) ConsumeFrameCycleStats()
    {
        int actual = _cyclesActualAccum;
        int budget = _cyclesBudgetAccum;
        _cyclesActualAccum = 0;
        _cyclesBudgetAccum = 0;
        return (actual, budget);
    }

    internal void ResetLineCycles()
    {
        LineCycles = 0;
    }

    internal bool ConsumeBootRangeExitFlag()
    {
        bool result = _pcLeftBootRangeSinceLastSummary;
        _pcLeftBootRangeSinceLastSummary = false;
        return result;
    }

    public void irq_request(bool in_val)
    {
        irq_request(in_val, "unknown", 0);
    }

    public void irq_request(bool in_val, string source, byte status)
    {
        bool prev = g_interrupt_irq;
        g_interrupt_irq = in_val;
        if (in_val)
        {
            _irqSource = source;
            _irqStatus = status;
            if (TraceSmsIrq && md_main.g_masterSystemMode && _traceSmsIrqCount < 16)
            {
                _traceSmsIrqCount++;
                Console.WriteLine($"[SMS IRQ] request src={source} status=0x{status:X2} PC=0x{g_reg_PC:X4}");
            }
        }
        if (prev != in_val)
            LogZ80Int(in_val, source, status, "signal");
    }

    internal void ArmIrqAutoClear(string source, int cycles)
    {
        if (cycles <= 0)
            return;
        _irqAutoClearCycles = cycles;
        _irqAutoClearSource = source;
    }

    private void AddWaitCycles(int cycles)
    {
        if (cycles > 0)
            _waitCycles += cycles;
    }

    private void TickIrqAutoClear(int cycles)
    {
        if (_irqAutoClearCycles <= 0)
            return;
        if (!g_interrupt_irq)
        {
            _irqAutoClearCycles = 0;
            _irqAutoClearSource = null;
            return;
        }
        if (_irqAutoClearSource == null || !string.Equals(_irqAutoClearSource, _irqSource, StringComparison.Ordinal))
            return;
        _irqAutoClearCycles -= cycles;
        if (_irqAutoClearCycles > 0)
            return;
        _irqAutoClearCycles = 0;
        _irqAutoClearSource = null;
        irq_request(false, _irqSource, _irqStatus);
    }

    private void ApplyEiDelay()
    {
        if (_iff1Delay <= 0)
            return;
        _iff1Delay--;
        if (_iff1Delay > 0)
            return;
        g_IFF1 = true;
        g_IFF2 = true;
    }

    internal void ArmPostResetHold()
    {
        if (Z80ResetHoldCycles <= 0)
            return;
        _z80ResetHoldRemaining = Z80ResetHoldCycles;
        if (_z80ResetHoldLogRemaining > 0)
        {
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine(
                $"[Z80RESET-HOLD] frame={frame} cycles={Z80ResetHoldCycles} pc=0x{g_reg_PC:X4}");
            if (_z80ResetHoldLogRemaining != int.MaxValue)
                _z80ResetHoldLogRemaining--;
        }
    }

    internal void RequestNmi()
    {
        g_interrupt_nmi = true;
    }

        private void LogZ80Int(bool asserted, string source, byte status, string reason)
        {
            // Always track VDP IRQ counts for debugging, even if tracing is off
            if (source == "VDP")
            {
                if (asserted)
                {
                    _zintAssertCount++;
                    // ALADDIN DEBUG: Track IRQ count
                    md_main.IncrementZ80Irq();
                    if (TraceZ80IntDebug)
                        Console.WriteLine($"[Z80INT-DEBUG] IncrementZ80Irq called, frame={md_main.g_md_vdp?.FrameCounter ?? -1}");
                }
                else
                    _zintClearCount++;
            }
            
            if (!MdTracerCore.MdLog.TraceZ80Int)
                return;
            string state = asserted ? "asserted" : "cleared";
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;

            // [INT-STATS] Count ZINT events
            if (source == "VDP")
            {
                if (asserted)
                {
                    _zintAssertCount++;
                    // ALADDIN DEBUG: Track IRQ count
                    md_main.IncrementZ80Irq();
                    if (TraceZ80IntDebug)
                        Console.WriteLine($"[Z80INT-DEBUG] IncrementZ80Irq called, frame={frame}");
                }
                else
                    _zintClearCount++;
            }

            Console.WriteLine(
                $"[Z80INT] {state} source={source} status=0x{status:X2} pc=0x{g_reg_PC:X4} frame={frame} reason={reason}");
        }

        internal void CountZ80IntService()
        {
            _zintServiceCount++;
        }

        internal void FlushZ80IntStats(long frame)
        {
            if (!MdTracerCore.MdLog.TraceZ80Int)
                return;
            if (frame != _zintLastFrameLogged && (_zintAssertCount > 0 || _zintClearCount > 0 || _zintServiceCount > 0))
            {
                Console.WriteLine($"[Z80INT-STATS] frame={frame} assert={_zintAssertCount} clear={_zintClearCount} service={_zintServiceCount}");
                _zintLastFrameLogged = frame;
            }
            _zintAssertCount = 0;
            _zintClearCount = 0;
            _zintServiceCount = 0;
        }

        public void reset()
        {
            int cycleId = md_main.Z80ResetCycleId;
            if (_lastResetCycleId != cycleId)
            {
                _lastResetCycleId = cycleId;
                _resetCallsThisCycle = 0;
                _duplicateResetLogged = false;
            }

            _resetCallsThisCycle++;
            if (_resetCallsThisCycle > 1 && !_duplicateResetLogged)
            {
                _duplicateResetLogged = true;
                Console.WriteLine($"[Z80] reset called again! cycle={cycleId} count={_resetCallsThisCycle}");
                Console.WriteLine(new StackTrace(1, true));
            }
            g_reg_PC = 0;
            _z80ResetHoldRemaining = 0;

            g_reg_A = g_reg_B = g_reg_C = g_reg_D = g_reg_E = g_reg_H = g_reg_L = 0;
            g_reg_R = g_reg_I = 0;

            g_reg_Au = g_reg_Bu = g_reg_Cu = g_reg_Du = g_reg_Eu = g_reg_Fu = g_reg_Hu = g_reg_Lu = 0;

            g_reg_SP = 0;
            g_reg_IX = 0xffff;
            g_reg_IY = 0xffff;

            g_flag_S = 0;
            g_flag_Z = 1;
            g_flag_H = 0;
            g_flag_PV = 0;
            g_flag_N = 0;
            g_flag_C = 0;
            MaybePatchZ80BootJump();

            g_interruptMode = 0;
            g_halt = false;
            g_IFF1 = false;
            g_IFF2 = false;
            _iff1Delay = 0;
            _waitCycles = 0;
            _irqAutoClearCycles = 0;
            _irqAutoClearSource = null;

            g_bank_register = Z80BankDefault;
            ResetMailboxShadow();
            _z80Dumped = false;
            _smsBankSelect = 0;
            _smsBank0 = 0;
            _smsBank1 = 1;
            _smsBank2 = 2;
            _smsCodemastersRamEnabled = false;
            _smsSegaRamEnabled = false;
            _smsSegaRamBank = 0;
            _smsCartridgeEnabled = true;
            _smsBiosEnabled = false;
            _smsIoControl = 0x00;
            if (_smsCartRam.Length > 0)
                Array.Clear(_smsCartRam, 0, _smsCartRam.Length);
            _smsInstructionLog = 0;
            _smsLoopPc = 0;
            if (md_main.g_masterSystemMode && g_ram.Length > 0)
                g_ram[0] = 0xAB;
            _smsLoopCount = 0;
            _instrThrottleCounter = 0;
            _bootInstrCount = 0;
            _z80IoLogRemaining = TraceZ80IoLimit;
            _z80YmLogRemaining = TraceZ80YmLimit;
            _z80RamWriteLogRemaining = TraceZ80RamWriteLimit;
            _z80RamWriteRangeRemaining = TraceZ80RamWriteRangeLimit;
            _z80RamReadRangeRemaining = TraceZ80RamReadRangeLimit;
            _z80ReadRangeRemaining = TraceZ80ReadRangeLimit;
            _z80MbxPollEdgeRemaining = TraceZ80MbxPollEdgeLimit;
            _z80MbxPollDataRemaining = TraceZ80MbxPollDataLimit;
            _z80MbxPollEdgeLastValid = false;
            _z80MbxPollEdgeLastValue = 0x00;
            _z80MbxPollDataLastValid = false;
            _lastReadAddr = 0;
            _lastReadM68kAddr = 0;
            _lastReadValue = 0;
            _lastReadPc = 0;
            _lastReadWasBanked = false;
            _pcLeftBootRangeSinceLastSummary = false;
            g_active = true;
            if (TraceZ80Step)
                _traceStepRemaining = 64;
            ResetTraceBudgets();
            ResetZ80Ram1800Trace();
        }

        internal void ArmForcePc(ushort target, string reason)
        {
            _forcePcPending = true;
            _forcePcTarget = target;
            _forcePcReason = reason;
            _z80PcRangeRemaining = TraceZ80PcRangeLimit;
        }

        internal void SetStackPointer(ushort sp)
        {
            g_reg_SP = sp;
        }
        
        internal void ForceJumpToDriver()
        {
            // Force Z80 to jump to sound driver at 0x0167
            Console.WriteLine($"[Z80-FORCE] Forcing jump to 0x0167 from PC=0x{g_reg_PC:X4}");
            g_reg_PC = 0x0167;
        }

        // ---- Prefixgrenar -----------------------------------------------------

        private void op_dd()
        {
            if (g_operand_dd[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_dd: odefinierad opcode");
            }
            g_operand_dd[g_opcode2]();
            g_reg_R += 1;
        }

        private void op_ddcb()
        {
            if (g_operand_ddcb[g_opcode4] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_ddcb: odefinierad opcode");
            }
            g_operand_ddcb[g_opcode4]();
            g_reg_R += 1;
        }

        private void op_fd()
        {
            if (g_operand_fd[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_fd: odefinierad opcode");
            }
            g_operand_fd[g_opcode2]();
            g_reg_R += 1;
        }

        private void op_fdcb()
        {
            if (g_operand_fdcb[g_opcode4] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_fdcb: odefinierad opcode");
            }
            g_operand_fdcb[g_opcode4]();
            g_reg_R += 1;
        }

        private void op_ed()
        {
            if (md_main.g_masterSystemMode &&
                string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SMS_ED"), "1", StringComparison.Ordinal))
            {
                ushort pc = g_reg_PC;
                byte op2 = g_opcode2;
                Console.WriteLine($"[SMS ED] pc=0x{pc:X4} op2=0x{op2:X2}");
            }
            if (g_operand_ed[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_ed: odefinierad opcode");
            }
            g_operand_ed[g_opcode2]();
            g_reg_R += 1;
        }

        private void op_cb()
        {
            if (g_operand_cb[g_opcode2] == op_NOP)
            {
                Debug.WriteLine("md_z80.op_cb: odefinierad opcode");
            }
            g_operand_cb[g_opcode2]();
            g_reg_R += 1;
        }

        private void SmsControlLog(string message)
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;
            if (_smsControlLogCount >= SmsControlLogLimit)
                return;
            MdTracerCore.MdLog.WriteLine(message);
            _smsControlLogCount++;
        }

        private void LogSmsLoopRegs()
        {
            if (!md_main.g_masterSystemMode || !MdTracerCore.MdLog.Enabled)
                return;
            if (_smsLoopRegLogCount >= SmsLoopRegLogLimit)
                return;
            MdTracerCore.MdLog.WriteLine($"[md_z80 SMS loop regs] PC=0x{g_reg_PC:X4} B=0x{g_reg_B:X2} C=0x{g_reg_C:X2}");
            _smsLoopRegLogCount++;
        }

        private void MaybeLogZ80Speed(int cycles)
        {
            if (!TraceZ80Speed || cycles <= 0)
                return;

            long now = Stopwatch.GetTimestamp();
            if (_z80SpeedLastTicks == 0)
                _z80SpeedLastTicks = now;

            _z80SpeedCycles += cycles;
            long elapsed = now - _z80SpeedLastTicks;
            if (elapsed < Stopwatch.Frequency)
                return;

            double secs = elapsed / (double)Stopwatch.Frequency;
            double hz = _z80SpeedCycles / secs;
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            Console.WriteLine($"[Z80-SPEED] frame={frame} hz={hz:0.##} cycles={_z80SpeedCycles} secs={secs:0.###}");

            _z80SpeedCycles = 0;
            _z80SpeedLastTicks = now;
        }

        // ---- Debughjälp (endast i DEBUG) -------------------------------------

        #if DEBUG
        private void pgout()
        {
            try
            {
                using (BinaryWriter writer = new BinaryWriter(File.OpenWrite(@"d:\t_1.bin")))
                {
                    // OBS: g_ram kan vara def i annan partial – anpassa/kommentera bort vid behov
                    writer.Write(g_ram, 0, Math.Min(g_ram.Length, 8192));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"pgout() failed: {ex}");
            }
        }

        private readonly uint[] log_trace = new uint[100];

        private void traceout()
        {
            for (int i = 98; i >= 0; i--) log_trace[i + 1] = log_trace[i];
            log_trace[0] = g_reg_PC;
        }

        private void logout2()
        {
            try
            {
                // g_status_flag verkar inte definieras i den här partialen — kommentera/byt om nödvändigt
                byte statusMasked = (byte)(g_status_flag & 0xD7);

                using (FileStream fs = new FileStream("d:\\log2.txt", FileMode.Append, FileAccess.Write, FileShare.None, 4096, useAsync: false))
                {
                    var sb = new StringBuilder(80);
                    sb.Append(g_reg_PC.ToString("x4")).Append(',')
                    .Append(g_reg_A.ToString("x2")).Append(',')
                    .Append(statusMasked.ToString("x2")).Append(',')
                    .Append(g_reg_BC.ToString("x4")).Append(',')
                    .Append(g_reg_DE.ToString("x4")).Append(',')
                    .Append(g_reg_HL.ToString("x4")).Append(',')
                    .Append(g_reg_IX.ToString("x4")).Append(',')
                    .Append(g_reg_IY.ToString("x4")).Append(',')
                    .Append(g_reg_SP.ToString("x4")).Append(',')
                    .Append(g_bank_register.ToString("x1"))
                    .Append(Environment.NewLine);

                    byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
                    fs.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"logout2() failed: {ex}");
            }
        }

        #endif
    }
}
