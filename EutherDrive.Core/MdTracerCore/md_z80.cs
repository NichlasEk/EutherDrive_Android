using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EutherDrive.Core.MdTracerCore
{
    //----------------------------------------------------------------
    // CPU : Zilog Z80
    //----------------------------------------------------------------
    internal partial class md_z80
    {
        public bool g_active;
        internal ushort DebugPc => g_reg_PC;
        internal ushort DebugBc => g_reg_BC;

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
        private static readonly bool DumpZ80Ram =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DUMP_Z80_RAM"), "1", StringComparison.Ordinal);
        private static readonly bool HaltOnBusReq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_HALT_ON_BUSREQ"), "1", StringComparison.Ordinal);

        private long _instrThrottleCounter;
        private bool _z80Dumped;
        private bool _pcLeftBootRangeSinceLastSummary;
        private long _z80StatsLastTicks;
        private long _z80StatsInstrCount;

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

        // VIKTIGT: använd alltid bus/minnes-API:t för fetch (respekterar bankning)
        private byte   g_opcode1  => read8(g_reg_PC);
        private byte   g_opcode2  => read8((uint)(g_reg_PC + 1));
        private byte   g_opcode3  => read8((uint)(g_reg_PC + 2));
        private byte   g_opcode4  => read8((uint)(g_reg_PC + 3));
        private ushort g_opcode23 => (ushort)((read8((uint)(g_reg_PC + 2)) << 8) + read8((uint)(g_reg_PC + 1)));
        private ushort g_opcode34 => (ushort)((read8((uint)(g_reg_PC + 3)) << 8) + read8((uint)(g_reg_PC + 2)));

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
        private int _smsInstructionLog;
        private ushort _smsLoopPc;
        private int _smsLoopCount;
        private const int SmsInstructionLogLimit = 256;
        private const int SmsLoopReportThreshold = 64;
        private const int SmsControlLogLimit = 12;
        private static int _smsControlLogCount;
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
        private int _cyclesBudgetAccum;
        private int _cyclesActualAccum;
        private int _resetCallsThisCycle;
        private int _lastResetCycleId;
        private bool _duplicateResetLogged;
        private int _traceStepRemaining;
        private bool _lastCanRun;
        private string _irqSource = "unknown";
        private byte _irqStatus;

        //----------------------------------------------------------------
        public md_z80()
        {
            initialize();
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
            _z80StatsInstrCount = 0;
            _z80StatsLastTicks = now;

            bool busRequested = md_main.g_md_bus?.Z80BusGranted ?? false;
            bool reset = md_main.g_md_bus?.Z80Reset ?? false;
            long busReqWrites = 0;
            long busReqToggles = 0;
            long resetWrites = 0;
            long resetToggles = 0;
            md_main.g_md_bus?.ConsumeZ80SignalStats(out busReqWrites, out busReqToggles, out resetWrites, out resetToggles);

            double ips = instrDelta / elapsedSec;
            Console.WriteLine(
                $"[Z80Stats] dt={elapsedSec:0.00}s instr={instrDelta} ips={ips:0} active={(g_active ? 1 : 0)} " +
                $"halt={(g_halt ? 1 : 0)} pc=0x{g_reg_PC:X4} busReq={(busRequested ? 1 : 0)} " +
                $"reset={(reset ? 1 : 0)} busReqW={busReqWrites} busReqT={busReqToggles} " +
                $"resetW={resetWrites} resetT={resetToggles}");
        }

        public void run(int in_clock)
        {
            #if DEBUG
            // enkel manuell dump om du vill toggla den ibland
            bool www = false;
            if (www) pgout();
            #endif

            MaybeLogZ80Stats();
            bool busRequested = md_main.g_md_bus?.Z80BusGranted ?? false;
            bool reset = md_main.g_md_bus?.Z80Reset ?? false;
            bool canRun = g_active && !busRequested && !reset;
            if (TraceZ80Step && !_lastCanRun && canRun)
            {
                _traceStepRemaining = 64;
            }
            _lastCanRun = canRun;

            if (HaltOnBusReq && busRequested)
                return;
            if (g_active == false) return;

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
            while (g_clock_total >= 0)
            {
                if (TraceZ80Stats)
                    _z80StatsInstrCount++;

                // IRQ (NMI-block ej aktiverad i originalet)
            if (g_interrupt_irq)
            {
                if (g_IFF1)
                {
                    SmsControlLog($"[md_z80 SMS irq] IM{g_interruptMode} pending PC=0x{g_reg_PC:X4}");
                    if (g_halt) g_reg_PC += 1;

                    g_interrupt_irq = false;
                    LogZ80Int(false, _irqSource, _irqStatus, "service");
                        g_IFF1 = false;
                        g_IFF2 = false;
                        g_halt = false;

                        switch (g_interruptMode)
                        {
                            case 0:
                                // IM 0: devices place an opcode on bus – ej implementerat här
                                break;

                            case 1:
                                stack_push(g_reg_PCH);
                                stack_push(g_reg_PCL);
                                g_reg_PC = 0x0038;
                                g_halt = false;
                                break;

                            case 2:
                                // IM 2: vektor via I-register – ej implementerat här
                                break;
                        }
                    }
                }

            g_clock = 0;

            ushort pcBefore = g_reg_PC;
            if (TraceSmsDelay && pcBefore == 0x056B)
            {
                LogSmsDelayEntry(pcBefore);
                if (SkipSmsDelay)
                {
                    g_reg_PC = 0x057A;
                    continue;
                }
            }

            byte opcode = g_opcode1;
            if (TraceZ80Step && _traceStepRemaining > 0)
            {
                Console.WriteLine($"[Z80STEP] pc=0x{pcBefore:X4} op=0x{opcode:X2} busreq={(busRequested ? 1 : 0)} reset={(reset ? 1 : 0)}");
                _traceStepRemaining--;
            }
            bool log0576After = TraceSmsDelay && !_delayExitLogged && pcBefore == 0x0576;
            bool djnzTracePending = TraceSmsDelay && opcode == 0x10 && _djnzLogCount < DjnzLogLimit;
            byte djnzBefore = g_reg_B;
            ushort djnzTarget = 0;
            if (djnzTracePending)
                djnzTarget = (ushort)((int)pcBefore + 2 + (sbyte)g_opcode2);

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
            g_operand[opcode]();   // exekvera en instruktion
            cyclesConsumed += g_clock;
            md_main.g_md_music?.g_md_ym2612.TickTimersFromZ80Cycles(g_clock);

            if (log0576After)
                LogSmsDelayExit(pcBefore);

            if (djnzTracePending)
            {
                byte djnzAfter = g_reg_B;
                bool taken = g_reg_PC != (ushort)(pcBefore + 2);
                Console.WriteLine($"[SMS DJNZ] PC=0x{pcBefore:X4} B before=0x{djnzBefore:X2} after=0x{djnzAfter:X2} taken={(taken ? 1 : 0)} target=0x{djnzTarget:X4}");
                _djnzLogCount++;
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
        Console.WriteLine(
            $"[Z80DUMP] reason={reason} PC=0x{g_reg_PC:X4} SP=0x{g_reg_SP:X4} " +
            $"A=0x{g_reg_A:X2} BC=0x{g_reg_BC:X4} " +
            $"DE=0x{g_reg_DE:X4} HL=0x{g_reg_HL:X4} IX=0x{g_reg_IX:X4} " +
            $"IY=0x{g_reg_IY:X4} I=0x{g_reg_I:X2} R=0x{g_reg_R:X2} bank=0x{g_bank_register:X6}");
        DumpZ80Region(0x0000, 0x0200);
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
        }
        if (prev != in_val)
            LogZ80Int(in_val, source, status, "signal");
    }

    private void LogZ80Int(bool asserted, string source, byte status, string reason)
    {
        if (!MdTracerCore.MdLog.TraceZ80Int)
            return;
        string state = asserted ? "asserted" : "cleared";
        Console.WriteLine($"[Z80INT] {state} source={source} status=0x{status:X2} pc=0x{g_reg_PC:X4} reason={reason}");
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

            g_interruptMode = 0;
            g_halt = false;
            g_IFF1 = false;
            g_IFF2 = false;

            g_bank_register = 0u;
            ResetMailboxShadow();
            _z80Dumped = false;
            _smsBankSelect = 0;
            _smsInstructionLog = 0;
            _smsLoopPc = 0;
            _smsLoopCount = 0;
            _instrThrottleCounter = 0;
            _pcLeftBootRangeSinceLastSummary = false;
            g_active = true;
            if (TraceZ80Step)
                _traceStepRemaining = 64;
            ResetTraceBudgets();
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
