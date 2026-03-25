using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace KSNES.CPU;

public class CPU : ICPU
{
    private static readonly bool PerfStatsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_PERF"), "1", StringComparison.Ordinal)
        || OperatingSystem.IsAndroid();
    private static readonly bool DetailedPerfStatsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_PERF_DETAIL"), "1", StringComparison.Ordinal);
    private const int DBR = 0;
    private const int K = 1;
    private const int A = 0;
    private const int X = 1;
    private const int Y = 2;
    private const int SP = 3;
    private const int PC = 4;
    private const int DPR = 5;
    private const int IMP = 0;
    private const int IMM = 1;
    private const int IMMm = 2;
    private const int IMMx = 3;
    private const int IMMl = 4;
    private const int DP = 5;
    private const int DPX = 6;
    private const int DPY = 7;
    private const int IDP = 8;
    private const int IDX = 9;
    private const int IDY = 10;
    private const int IDYr = 11;
    private const int IDL = 12;
    private const int ILY = 13;
    private const int SR = 14;
    private const int ISY = 15;
    private const int ABS = 16;
    private const int ABX = 17;
    private const int ABXr = 18;
    private const int ABY = 19;
    private const int ABYr = 20;
    private const int ABL = 21;
    private const int ALX = 22;
    private const int IND = 23;
    private const int IAX = 24;
    private const int IAL = 25;
    private const int REL = 26;
    private const int RLL = 27;
    private const int BM = 28;

    private byte[] _r = new byte[2];
    private ushort[] _br = new ushort[6];

    public ushort ProgramCounter
    {
        get => _br[PC];
        set => _br[PC] = value;
    }

    public byte ProgramBank
    {
        get => _r[K];
        set => _r[K] = value;
    }

    public byte DataBank => _r[DBR];

    public int ProgramCounter24 => (_r[K] << 16) | _br[PC];

    internal string GetTraceState()
    {
        byte p = GetP();
        return $"A=0x{_br[A]:X4} X=0x{_br[X]:X4} Y=0x{_br[Y]:X4} SP=0x{_br[SP]:X4} D=0x{_br[DPR]:X4} DBR=0x{_r[DBR]:X2} PB=0x{_r[K]:X2} P=0x{p:X2} E={(_e ? 1 : 0)} M={(_m ? 1 : 0)} Xf={(_x ? 1 : 0)}";
    }

    internal string GetDebugStateWithStack(int byteCount = 6)
    {
        string regs = GetTraceState();
        if (_snes == null || byteCount <= 0)
            return regs;

        int sp = _br[SP];
        byte[] stack = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            int address = _e
                ? (0x0100 | ((sp + 1 + i) & 0xFF))
                : ((sp + 1 + i) & 0xFFFF);
            stack[i] = (byte)ReadBus(address);
        }

        return $"{regs} STACK=[{string.Join(' ', stack.Select(static b => $"{b:X2}"))}]";
    }

    private bool _tracePc;
    private int _tracePcLimit;
    private int _tracePcCount;
    private bool _tracePcRange;
    private int _tracePcRangeStart;
    private int _tracePcRangeEnd;
    private int _tracePcRangeLimit;
    private int _tracePcRangeCount;
    private bool _traceWramPc;
    private bool _traceWramPcLogged;
    [NonSerialized]
    private bool _anyTraceEnabled;
    [NonSerialized]
    private readonly bool _traceSa1Fetch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_FETCH"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly string _traceSa1FetchPath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_FETCH_PATH") ?? string.Empty;
    [NonSerialized]
    private readonly object _traceSa1FetchLock = new();

    private void UpdateAnyTraceEnabled()
    {
        _anyTraceEnabled = _tracePc || _tracePcRange || _traceWramPc;
    }

    private static readonly bool TraceLdaWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_LDA_WATCH"), "1", StringComparison.Ordinal);
    private static readonly bool TraceIndexWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_INDEX_WATCH"), "1", StringComparison.Ordinal);
    private static readonly bool TraceStackSet =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_STACK_SET"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_STACK_SET"), "1", StringComparison.Ordinal);
    [JsonIgnore]
    private readonly int[] _modes = [
        IMP, IDX, IMM, SR, DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, DP, DPX, DPX, ILY, IMP, ABYr, IMP, IMP, ABS, ABXr, ABX, ALX,
        ABS, IDX, ABL, SR, DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, DPX, DPX, DPX, ILY, IMP, ABYr, IMP, IMP, ABXr,ABXr, ABX, ALX,
        IMP, IDX, IMM, SR, BM, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, BM, DPX, DPX, ILY, IMP, ABYr,IMP, IMP, ABL, ABXr, ABX, ALX,
        IMP, IDX, RLL, SR, DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, IND, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, DPX, DPX, DPX, ILY, IMP, ABYr,IMP, IMP, IAX, ABXr, ABX, ALX,
        REL, IDX, RLL, SR, DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDY, IDP, ISY, DPX, DPX, DPY, ILY, IMP, ABY, IMP, IMP, ABS, ABX, ABX, ALX,
        IMMx, IDX, IMMx, SR, DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, DPX, DPX, DPY, ILY, IMP, ABYr, IMP, IMP, ABXr, ABXr, ABYr,ALX,
        IMMx, IDX, IMM, SR, DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, DP, DPX, DPX, ILY, IMP, ABYr, IMP, IMP, IAL, ABXr, ABX, ALX,
        IMMx, IDX, IMM, SR , DP, DP, DP, IDL, IMP, IMMm, IMP, IMP, ABS, ABS, ABS, ABL,
        REL, IDYr, IDP, ISY, IMMl,DPX, DPX, ILY, IMP, ABYr, IMP, IMP, IAX, ABXr, ABX, ALX,
        IMP, IMP, IMP
    ];
        
    [JsonIgnore]
    private readonly int[] _cycles = [
        7, 6, 7, 4, 5, 3, 5, 6, 3, 2, 2, 4, 6, 4, 6, 5,
        2, 5, 5, 7, 5, 4, 6, 6, 2, 4, 2, 2, 6, 4, 7, 5,
        6, 6, 8, 4, 3, 3, 5, 6, 4, 2, 2, 5, 4, 4, 6, 5,
        2, 5, 5, 7, 4, 4, 6, 6, 2, 4, 2, 2, 4, 4, 7, 5,
        6, 6, 2, 4, 7, 3, 5, 6, 3, 2, 2, 3, 3, 4, 6, 5,
        2, 5, 5, 7, 7, 4, 6, 6, 2, 4, 3, 2, 4, 4, 7, 5,
        6, 6, 6, 4, 3, 3, 5, 6, 4, 2, 2, 6, 5, 4, 6, 5,
        2, 5, 5, 7, 4, 4, 6, 6, 2, 4, 4, 2, 6, 4, 7, 5,
        3, 6, 4, 4, 3, 3, 3, 6, 2, 2, 2, 3, 4, 4, 4, 5,
        2, 6, 5, 7, 4, 4, 4, 6, 2, 5, 2, 2, 4, 5, 5, 5,
        2, 6, 2, 4, 3, 3, 3, 6, 2, 2, 2, 4, 4, 4, 4, 5,
        2, 5, 5, 7, 4, 4, 4, 6, 2, 4, 2, 2, 4, 4, 4, 5,
        2, 6, 3, 4, 3, 3, 5, 6, 2, 2, 2, 3, 4, 4, 6, 5,
        2, 5, 5, 7, 6, 4, 6, 6, 2, 4, 3, 3, 6, 4, 7, 5,
        2, 6, 3, 4, 3, 3, 5, 6, 2, 2, 2, 3, 4, 4, 6, 5,
        2, 5, 5, 7, 5, 4, 6, 6, 2, 4, 4, 2, 8, 4, 7, 5,
        7, 7, 7
    ];

    [JsonIgnore]
    private readonly Action<int, int>[] _functions;
    
    private bool _n;
    private bool _v;
    private bool _m;
    private bool _x;
    private bool _d;
    private bool _i;
    private bool _z;
    private bool _c;
    private bool _e;

    public bool IrqWanted { get; set; }
    public bool NmiWanted { get; set; }
    private bool _aboWanted;

    private bool _stopped;
    private bool _waiting;

    public int CyclesLeft { get; set; }

    public bool StartInNativeMode { get; set; }

    [NonSerialized]
    private ISNESSystem _snes;
    [NonSerialized]
    private KSNES.SNESSystem.SNESSystem? _snesImpl;
    [NonSerialized]
    private KSNES.Specialchips.SA1.Sa1.Sa1System? _sa1Impl;
    [NonSerialized]
    private int _cachedDataPageIndex = -1;
    [NonSerialized]
    private ushort _cachedDataPageData;
    [NonSerialized]
    private bool _hasCachedDataPage;
    [NonSerialized]
    internal ulong PerfInstructions;
    [NonSerialized]
    internal ulong PerfProgramBytes;
    [NonSerialized]
    internal ulong PerfProgramPageReloads;
    [NonSerialized]
    internal ulong PerfOpcodeFetchTicks;
    [NonSerialized]
    internal ulong PerfAddressTicks;
    [NonSerialized]
    internal ulong PerfExecuteTicks;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public CPU()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    {
        _functions = [
            Brk, Ora, Cop, Ora, Tsb, Ora, Asl, Ora, Php, Ora, Asla, Phd, Tsb, Ora, Asl, Ora,
            Bpl, Ora, Ora, Ora, Trb, Ora, Asl, Ora, Clc, Ora, Inca, Tcs, Trb, Ora, Asl, Ora,
            Jsr, And, Jsl, And, Bit, And, Rol, And, Plp, And, Rola, Pld, Bit, And, Rol, And,
            Bmi, And, And, And, Bit, And, Rol, And, Sec, And, Deca, Tsc, Bit, And, Rol, And,
            Rti, Eor, Wdm, Eor, Mvp, Eor, Lsr, Eor, Pha, Eor, Lsra, Phk, Jmp, Eor, Lsr, Eor,
            Bvc, Eor, Eor, Eor, Mvn, Eor, Lsr, Eor, Cli, Eor, Phy, Tcd, Jml, Eor, Lsr, Eor,
            Rts, Adc, Per, Adc, Stz, Adc, Ror, Adc, Pla, Adc, Rora, Rtl, Jmp, Adc, Ror, Adc,
            Bvs, Adc, Adc, Adc, Stz, Adc, Ror, Adc, Sei, Adc, Ply, Tdc, Jmp, Adc, Ror, Adc,
            Bra, Sta, Brl, Sta, Sty, Sta, Stx, Sta, Dey, Biti, Txa, Phb, Sty, Sta, Stx, Sta,
            Bcc, Sta, Sta, Sta, Sty, Sta, Stx, Sta, Tya, Sta, Txs, Txy, Stz, Sta, Stz, Sta,
            Ldy, Lda, Ldx, Lda, Ldy, Lda, Ldx, Lda, Tay, Lda, Tax, Plb, Ldy, Lda, Ldx, Lda,
            Bcs, Lda, Lda, Lda, Ldy, Lda, Ldx, Lda, Clv, Lda, Tsx, Tyx, Ldy, Lda, Ldx, Lda,
            Cpy, Cmp, Rep, Cmp, Cpy, Cmp, Dec, Cmp, Iny, Cmp, Dex, Wai, Cpy, Cmp, Dec, Cmp,
            Bne, Cmp, Cmp, Cmp, Pei, Cmp, Dec, Cmp, Cld, Cmp, Phx, Stp, Jml, Cmp, Dec, Cmp,
            Cpx, Sbc, Sep, Sbc, Cpx, Sbc, Inc, Sbc, Inx, Sbc, Nop, Xba, Cpx, Sbc, Inc, Sbc,
            Beq, Sbc, Sbc, Sbc, Pea, Sbc, Inc, Sbc, Sed, Sbc, Plx, Xce, Jsr, Sbc, Inc, Sbc,
            Abo, Nmi, Irq
        ];

        InitTraceConfig();
    }

    private void InitTraceConfig()
    {
        _tracePc = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_CPU_PC"), "1", StringComparison.Ordinal);
        _tracePcLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_CPU_PC_LIMIT", 200);
        _tracePcCount = 0;

        _tracePcRange = false;
        _tracePcRangeStart = 0;
        _tracePcRangeEnd = 0;
        _tracePcRangeLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE_LIMIT", 200);
        _tracePcRangeCount = 0;
        if (TryParseTraceRange("EUTHERDRIVE_TRACE_SNES_CPU_PC_RANGE", out int start, out int end))
        {
            _tracePcRange = true;
            _tracePcRangeStart = start;
            _tracePcRangeEnd = end;
        }

        _traceWramPc = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_CPU_WRAM_PC"), "1", StringComparison.Ordinal);
        _traceWramPcLogged = false;
    }

    internal void RefreshTraceConfig() => InitTraceConfig();

    public void SetSystem(ISNESSystem system)
    {
        _snes = system;
        _snesImpl = system as KSNES.SNESSystem.SNESSystem;
        _sa1Impl = system as KSNES.Specialchips.SA1.Sa1.Sa1System;
        _cachedDataPageIndex = -1;
        _cachedDataPageData = 0;
        _hasCachedDataPage = false;
    }

    public void Reset()
    {
        _r = new byte[2];
        _br = new ushort[6];
        _cachedDataPageIndex = -1;
        _cachedDataPageData = 0;
        _hasCachedDataPage = false;
        _br[PC] = (ushort) (ReadBus(0xfffc) | (ReadBus(0xfffd) << 8));
        _br[SP] = 0x1FF;
        _n = false;
        _v = false;
        _d = false;
        _i = true;
        _z = false;
        _c = false;
        _e = !StartInNativeMode;
        _m = true;
        _x = true;
        IrqWanted = false;
        NmiWanted = false;
        _aboWanted = false;
        _stopped = false;
        _waiting = false;
        CyclesLeft = 7;
        PerfInstructions = 0;
        PerfProgramBytes = 0;
        PerfProgramPageReloads = 0;
        PerfOpcodeFetchTicks = 0;
        PerfAddressTicks = 0;
        PerfExecuteTicks = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Cycle() 
    {
        if (CyclesLeft == 0)
        {
            if (_stopped)
            {
                CyclesLeft = 1;
            }
            else if (!_waiting)
            {
                if (_anyTraceEnabled)
                {
                    int pcAddr = (_r[K] << 16) | _br[PC];
                    if (_traceWramPc && !_traceWramPcLogged && (pcAddr & 0xFFFF) < 0x2000)
                    {
                        if (_snesImpl is { } snes)
                        {
                            int b0 = snes.Peek(pcAddr);
                            int b1 = snes.Peek((pcAddr + 1) & 0xffffff);
                            int b2 = snes.Peek((pcAddr + 2) & 0xffffff);
                            int b3 = snes.Peek((pcAddr + 3) & 0xffffff);
                            Console.WriteLine($"[CPU-WRAM-PC] pc=0x{pcAddr:X6} op=[{b0:X2} {b1:X2} {b2:X2} {b3:X2}]");
                            _traceWramPcLogged = true;
                        }
                    }
                    if (_tracePc && _tracePcCount < _tracePcLimit)
                    {
                        if (_snesImpl is { } snes)
                        {
                            int b0 = snes.Peek(pcAddr);
                            int b1 = snes.Peek((pcAddr + 1) & 0xffffff);
                            int b2 = snes.Peek((pcAddr + 2) & 0xffffff);
                            string regs = GetDebugStateWithStack();
                            Console.WriteLine($"[CPU-PC] cpu=SNES pc=0x{pcAddr:X6} op=[{b0:X2} {b1:X2} {b2:X2}] regs=[{regs}]");
                            _tracePcCount++;
                        }
                    }
                    if (_tracePcRange && _tracePcRangeCount < _tracePcRangeLimit && pcAddr >= _tracePcRangeStart && pcAddr <= _tracePcRangeEnd)
                    {
                        if (_snesImpl is { } snes)
                        {
                            int b0 = snes.Peek(pcAddr);
                            int b1 = snes.Peek((pcAddr + 1) & 0xffffff);
                            int b2 = snes.Peek((pcAddr + 2) & 0xffffff);
                            string regs = GetDebugStateWithStack();
                            Console.WriteLine($"[CPU-PC-RANGE] cpu=SNES pc=0x{pcAddr:X6} op=[{b0:X2} {b1:X2} {b2:X2}] regs=[{regs}]");
                            _tracePcRangeCount++;
                        }
                    }
                }
                int pcBank = _r[K] << 16;
                ushort pc = _br[PC];
                int cachedPcPageIndex = -1;
                ushort cachedPcPageData = 0;
                bool hasCachedPcPage = false;
                long opcodeStart = DetailedPerfStatsEnabled ? Stopwatch.GetTimestamp() : 0;
                int instr = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (DetailedPerfStatsEnabled)
                    PerfOpcodeFetchTicks += (ulong)(Stopwatch.GetTimestamp() - opcodeStart);
                _br[PC] = pc;
                int opPc = (_r[K] << 16) | ((_br[PC] - 1) & 0xffff);
                CyclesLeft = _cycles[instr];
                int mode = _modes[instr];
                bool letRdnmiPollReadRunFirst = NmiWanted && IsImmediateRdnmiPoll(instr);
                if (IrqWanted && !_i || (NmiWanted && !letRdnmiPollReadRunFirst) || _aboWanted)
                {
                    _br[PC]--;
                    if (_aboWanted)
                    {
                        _aboWanted = false;
                        instr = 0x100;
                    }
                    else if (NmiWanted)
                    {
                        NmiWanted = false;
                        instr = 0x101;
                    }
                    else
                    {
                        instr = 0x102;
                    }
                    CyclesLeft = _cycles[instr];
                    mode = _modes[instr];
                    opPc = (_r[K] << 16) | (_br[PC] & 0xffff);
                }
                long addressStart = DetailedPerfStatsEnabled ? Stopwatch.GetTimestamp() : 0;
                var (item1, item2) = GetAdr(mode, pcBank, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (DetailedPerfStatsEnabled)
                    PerfAddressTicks += (ulong)(Stopwatch.GetTimestamp() - addressStart);
                TraceSa1FetchIfNeeded(opPc, instr, mode, item1, item2, "before");
                if (PerfStatsEnabled)
                    PerfInstructions++;
                long executeStart = DetailedPerfStatsEnabled ? Stopwatch.GetTimestamp() : 0;
                _functions[instr](item1, item2);
                if (DetailedPerfStatsEnabled)
                    PerfExecuteTicks += (ulong)(Stopwatch.GetTimestamp() - executeStart);
                TraceSa1FetchIfNeeded(opPc, instr, mode, item1, item2, "after");
            }
            else
            {
                if (_aboWanted || IrqWanted || NmiWanted)
                {
                    _waiting = false;
                }
                CyclesLeft = 1;
            }
        }
        CyclesLeft--;
    }

    internal void ResetPerfCounters()
    {
        if (!PerfStatsEnabled)
            return;
        PerfInstructions = 0;
        PerfProgramBytes = 0;
        PerfProgramPageReloads = 0;
        PerfOpcodeFetchTicks = 0;
        PerfAddressTicks = 0;
        PerfExecuteTicks = 0;
    }

    private bool IsImmediateRdnmiPoll(int opcode)
    {
        if (opcode != 0xAD || _snesImpl is null)
            return false;

        var snes = _snesImpl;
        int operandAddr = (_r[K] << 16) | _br[PC];
        int lo = snes.Peek(operandAddr);
        int hi = snes.Peek((operandAddr + 1) & 0xffffff);
        return lo == 0x10 && hi == 0x42;
    }

    private void TraceSa1FetchIfNeeded(int opPc, int instr, int mode, int adr, int adrh, string phase)
    {
        if (!_traceSa1Fetch || _sa1Impl is null)
            return;

        string line =
            $"[SA1-FETCH] phase={phase} opPc=0x{opPc:X6} instr=0x{instr:X2} mode={mode} adr=0x{adr:X6} adrh=0x{adrh:X6} pc=0x{ProgramCounter24:X6} regs=[{GetTraceState()}]";
        if (string.IsNullOrWhiteSpace(_traceSa1FetchPath))
        {
            Console.WriteLine(line);
            return;
        }

        lock (_traceSa1FetchLock)
        {
            File.AppendAllText(_traceSa1FetchPath, line + Environment.NewLine);
        }
    }

    private byte GetP()
    {
        byte val = 0;
        val |= (byte) (_n ? 0x80 : 0);
        val |= (byte) (_v ? 0x40 : 0);
        val |= (byte) (_m ? 0x20 : 0);
        val |= (byte) (_x ? 0x10 : 0);
        val |= (byte) (_d ? 0x08 : 0);
        val |= (byte) (_i ? 0x04 : 0);
        val |= (byte) (_z ? 0x02 : 0);
        val |= (byte) (_c ? 0x01 : 0);
        return val;
    }

    private void SetP(byte value)
    {
        _n = (value & 0x80) > 0;
        _v = (value & 0x40) > 0;
        _m = (value & 0x20) > 0;
        _x = (value & 0x10) > 0;
        _d = (value & 0x08) > 0;
        _i = (value & 0x04) > 0;
        _z = (value & 0x02) > 0;
        _c = (value & 0x01) > 0;
        if (_e)
        {
            // Emulation mode forces M/X and stack page 1
            _m = true;
            _x = true;
            _br[SP] = (ushort) (0x0100 | (_br[SP] & 0xff));
        }
        if (_x)
        {
            _br[X] &= 0xff;
            _br[Y] &= 0xff;
        }
    }

    private void SetZAndN(int value, bool byt) 
    {
        if (byt)
        {
            _z = (value & 0xff) == 0;
            _n = (value & 0x80) > 0;
            return;
        }
        _z = (value & 0xffff) == 0;
        _n = (value & 0x8000) > 0;
    }

    private static int GetSigned(int value, bool byt) 
    {
        if (byt)
        {
            return (value & 0xff) > 127 ? -(256 - (value & 0xff)) : value & 0xff;
        }
        return value > 32767 ? -(65536 - value) : value;
    }

    private static int Wrap24(int address)
    {
        return address & 0xffffff;
    }

    private static int BankAddress(int bank, int address16)
    {
        return ((bank & 0xff) << 16) | (address16 & 0xffff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadBus(int address)
    {
        if (_snesImpl is { } snes)
        {
            int fullAdr = address & 0xffffff;
            int pageIndex = fullAdr >> 8;
            if (!_hasCachedDataPage || _cachedDataPageIndex != pageIndex)
            {
                _cachedDataPageIndex = pageIndex;
                _cachedDataPageData = snes.GetCpuPageData(fullAdr);
                _hasCachedDataPage = true;
            }

            return snes.ReadCpuByteFast(fullAdr, _cachedDataPageData);
        }
        if (_sa1Impl != null)
            return _sa1Impl.Read(address);
        return _snes.Read(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBus(int address, int value)
    {
        if (_snesImpl is { } snes)
        {
            int fullAdr = address & 0xffffff;
            int pageIndex = fullAdr >> 8;
            if (!_hasCachedDataPage || _cachedDataPageIndex != pageIndex)
            {
                _cachedDataPageIndex = pageIndex;
                _cachedDataPageData = snes.GetCpuPageData(fullAdr);
                _hasCachedDataPage = true;
            }

            snes.WriteCpuByteFast(fullAdr, value, _cachedDataPageData);
            return;
        }
        if (_sa1Impl != null)
        {
            _sa1Impl.Write(address, value);
            return;
        }
        _snes.Write(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadProgramByte(int pcBank, ref ushort pc, ref int cachedPageIndex, ref ushort cachedPageData, ref bool hasCachedPage)
    {
        int address = pcBank | pc;
        pc++;
        if (PerfStatsEnabled)
            PerfProgramBytes++;

        if (_snesImpl is { } snes)
        {
            int pageIndex = address >> 8;
            if (!hasCachedPage || cachedPageIndex != pageIndex)
            {
                cachedPageIndex = pageIndex;
                cachedPageData = snes.GetCpuPageData(address);
                hasCachedPage = true;
                if (PerfStatsEnabled)
                    PerfProgramPageReloads++;
            }

            return snes.ReadCpuByteFast(address, cachedPageData);
        }

        if (PerfStatsEnabled)
            PerfProgramPageReloads++;
        return ReadBus(address);
    }

    private (int, int) DataBankPair(int address16)
    {
        int lo = BankAddress(_r[DBR], address16);
        int hi = BankAddress(_r[DBR], address16 + 1);
        return (lo, hi);
    }

   private void DoBranch(bool check, int rel)
   {
        if (check)
        {
            CyclesLeft++;
            _br[PC] = (ushort) (_br[PC] + rel);
        }
    }

    private void PushByte(int value) 
    {
        WriteBus(_br[SP], value);
        if (_e)
        {
            _br[SP] = (ushort) (0x0100 | ((_br[SP] - 1) & 0xff));
        }
        else
        {
            _br[SP]--;
        }
    }

    private int PullByte() 
    {
        if (_e)
        {
            _br[SP] = (ushort) (0x0100 | ((_br[SP] + 1) & 0xff));
        }
        else
        {
            _br[SP]++;
        }
        return ReadBus(_br[SP]);
    }

    private void PushWord(int value) 
    {
        PushByte((value & 0xff00) >> 8);
        PushByte(value & 0xff);
    }

    private int PullWord() 
    {
        int value = PullByte();
        value |= PullByte() << 8;
        return value;
    }

    private int ReadWord(int adr, int adrh) 
    {
        int value = ReadBus(adr);
        value |= ReadBus(adrh) << 8;
        return value;
    }

    private void WriteWord(int adr, int adrh, int result, bool reversed = false) 
    {
        if (reversed)
        {
            WriteBus(adrh, (result & 0xff00) >> 8);
            WriteBus(adr, result & 0xff);
        }
        else
        {
            WriteBus(adr, result & 0xff);
            WriteBus(adrh, (result & 0xff00) >> 8);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int, int) GetAdr(int mode, int pcBank, ref int cachedPcPageIndex, ref ushort cachedPcPageData, ref bool hasCachedPcPage) 
    {
        int dataBank = _r[DBR] << 16;
        int dpr = _br[DPR];
        int x = _br[X];
        int y = _br[Y];
        int sp = _br[SP];
        bool dprHasLowByteOffset = (dpr & 0xff) != 0;
        ushort pc = _br[PC];
        int adr = 0;
        int adrh = 0;

        switch (mode)
        {
            case IMP:
                break;
            case IMM:
                adr = pcBank | pc;
                pc++;
                break;
            case IMMm:
                adr = pcBank | pc;
                pc++;
                if (!_m)
                {
                    adrh = pcBank | pc;
                    pc++;
                }
                break;
            case IMMx:
                adr = pcBank | pc;
                pc++;
                if (!_x)
                {
                    adrh = pcBank | pc;
                    pc++;
                }
                break;
            case IMMl:
                adr = pcBank | pc;
                pc++;
                adrh = pcBank | pc;
                pc++;
                break;
            case DP:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand) & 0xffff;
                adr = baseAdr;
                adrh = (baseAdr + 1) & 0xffff;
                break;
            }
            case DPX:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand + x) & 0xffff;
                adr = baseAdr;
                adrh = (baseAdr + 1) & 0xffff;
                break;
            }
            case DPY:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand + y) & 0xffff;
                adr = baseAdr;
                adrh = (baseAdr + 1) & 0xffff;
                break;
            }
            case IDP:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                pointer &= 0xffff;
                adr = dataBank | pointer;
                adrh = dataBank | ((pointer + 1) & 0xffff);
                break;
            }
            case IDX:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand + x) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                pointer &= 0xffff;
                adr = dataBank | pointer;
                adrh = dataBank | ((pointer + 1) & 0xffff);
                break;
            }
            case IDY:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                int final = (pointer + y) & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case IDYr:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                int sum = pointer + y;
                if (pointer >> 8 != sum >> 8 || !_x)
                    CyclesLeft++;
                int final = sum & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case IDL:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                pointer |= ReadBus((baseAdr + 2) & 0xffff) << 16;
                adr = pointer & 0xffffff;
                adrh = (pointer + 1) & 0xffffff;
                break;
            }
            case ILY:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                if (dprHasLowByteOffset)
                    CyclesLeft++;
                int baseAdr = (dpr + operand) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                pointer |= ReadBus((baseAdr + 2) & 0xffff) << 16;
                int final = (pointer + y) & 0xffffff;
                adr = final;
                adrh = (final + 1) & 0xffffff;
                break;
            }
            case SR:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int baseAdr = (sp + operand) & 0xffff;
                adr = baseAdr;
                adrh = (baseAdr + 1) & 0xffff;
                break;
            }
            case ISY:
            {
                int operand = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int baseAdr = (sp + operand) & 0xffff;
                int pointer = ReadBus(baseAdr);
                pointer |= ReadBus((baseAdr + 1) & 0xffff) << 8;
                int final = (pointer + y) & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case ABS:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int baseAdr = lo | (hi << 8);
                adr = dataBank | baseAdr;
                adrh = dataBank | ((baseAdr + 1) & 0xffff);
                break;
            }
            case ABX:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int final = ((lo | (hi << 8)) + x) & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case ABXr:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int baseAdr = lo | (hi << 8);
                int sum = baseAdr + x;
                if (baseAdr >> 8 != sum >> 8 || !_x)
                    CyclesLeft++;
                int final = sum & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case ABY:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int final = ((lo | (hi << 8)) + y) & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case ABYr:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int baseAdr = lo | (hi << 8);
                int sum = baseAdr + y;
                if (baseAdr >> 8 != sum >> 8 || !_x)
                    CyclesLeft++;
                int final = sum & 0xffff;
                adr = dataBank | final;
                adrh = dataBank | ((final + 1) & 0xffff);
                break;
            }
            case ABL:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int mid = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int baseAdr = lo | (mid << 8) | (hi << 16);
                adr = baseAdr & 0xffffff;
                adrh = (baseAdr + 1) & 0xffffff;
                break;
            }
            case ALX:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int mid = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int final = (lo | (mid << 8) | (hi << 16)) + x;
                adr = final & 0xffffff;
                adrh = (adr + 1) & 0xffffff;
                break;
            }
            case IND:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int indirectAdr = lo | (hi << 8);
                int pointer = ReadBus(indirectAdr);
                pointer |= ReadBus((indirectAdr + 1) & 0xffff) << 8;
                adr = pcBank | pointer;
                break;
            }
            case IAX:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int indirectAdr = (lo | (hi << 8)) + x;
                int pointer = ReadBus(pcBank | (indirectAdr & 0xffff));
                pointer |= ReadBus(pcBank | ((indirectAdr + 1) & 0xffff)) << 8;
                adr = pcBank | pointer;
                break;
            }
            case IAL:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int indirectAdr = lo | (hi << 8);
                adr = ReadBus(indirectAdr);
                adr |= ReadBus((indirectAdr + 1) & 0xffff) << 8;
                adr |= ReadBus((indirectAdr + 2) & 0xffff) << 16;
                break;
            }
            case REL:
            {
                int rel = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                adr = GetSigned(rel, true);
                break;
            }
            case RLL:
            {
                int lo = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                int hi = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                adr = GetSigned(lo | (hi << 8), false);
                break;
            }
            case BM:
                adr = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                adrh = ReadProgramByte(pcBank, ref pc, ref cachedPcPageIndex, ref cachedPcPageData, ref hasCachedPcPage);
                break;
        }

        _br[PC] = pc;
        return (adr, adrh);
    }

    private void Adc(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            int result;
            if (_d)
            {
                result = (_br[A] & 0xf) + (value & 0xf) + (_c ? 1 : 0);
                result += result > 9 ? 6 : 0;
                result = (_br[A] & 0xf0) + (value & 0xf0) + (result > 0xf ? 0x10 : 0) + (result & 0xf);
            }
            else
            {
                result = (_br[A] & 0xff) + value + (_c ? 1 : 0);
            }
            _v = (_br[A] & 0x80) == (value & 0x80) && (value & 0x80) != (result & 0x80);
            result += _d && result > 0x9f ? 0x60 : 0;
            _c = result > 0xff;
            SetZAndN(result, _m);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (result & 0xff));
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft++;
            int result;
            if (_d)
            {
                result = (_br[A] & 0xf) + (value & 0xf) + (_c ? 1 : 0);
                result += result > 9 ? 6 : 0;
                result = (_br[A] & 0xf0) + (value & 0xf0) + (result > 0xf ? 0x10 : 0) + (result & 0xf);
                result += result > 0x9f ? 0x60 : 0;
                result = (_br[A] & 0xf00) + (value & 0xf00) + (result > 0xff ? 0x100 : 0) + (result & 0xff);
                result += result > 0x9ff ? 0x600 : 0;
                result = (_br[A] & 0xf000) + (value & 0xf000) + (result > 0xfff ? 0x1000 : 0) + (result & 0xfff);
            }
            else
            {
                result = _br[A] + value + (_c ? 1 : 0);
            }
            _v = (_br[A] & 0x8000) == (value & 0x8000) && (value & 0x8000) != (result & 0x8000);
            result += _d && result > 0x9fff ? 0x6000 : 0;
            _c = result > 0xffff;
            SetZAndN(result, _m);
            _br[A] = (ushort) result;
        }
    }

    private void Sbc(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr) ^ 0xff;
            int result;
            if (_d)
            {
                result = (_br[A] & 0xf) + (value & 0xf) + (_c ? 1 : 0);
                result -= result <= 0xf ? 6 : 0;
                result = (_br[A] & 0xf0) + (value & 0xf0) + (result > 0xf ? 0x10 : 0) + (result & 0xf);
            }
            else
            {
                result = (_br[A] & 0xff) + value + (_c ? 1 : 0);
            }
            _v = (_br[A] & 0x80) == (value & 0x80) && (value & 0x80) != (result & 0x80);
            result -= _d && result <= 0xff ? 0x60 : 0;
            _c = result > 0xff;
            SetZAndN(result, _m);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (result & 0xff));
        }
        else
        {
            int value = ReadWord(adr, adrh) ^ 0xffff;
            CyclesLeft++;
            int result;
            if (_d)
            {
                result = (_br[A] & 0xf) + (value & 0xf) + (_c ? 1 : 0);
                result -= result <= 0x0f ? 6 : 0;
                result = (_br[A] & 0xf0) + (value & 0xf0) + (result > 0xf ? 0x10 : 0) + (result & 0xf);
                result -= result <= 0xff ? 0x60 : 0;
                result = (_br[A] & 0xf00) + (value & 0xf00) + (result > 0xff ? 0x100 : 0) + (result & 0xff);
                result -= result <= 0xfff ? 0x600 : 0;
                result = (_br[A] & 0xf000) + (value & 0xf000) + (result > 0xfff ? 0x1000 : 0) + (result & 0xfff);
            }
            else
            {
                result = _br[A] + value + (_c ? 1 : 0);
            }
            _v = (_br[A] & 0x8000) == (value & 0x8000) && (value & 0x8000) != (result & 0x8000);
            result -= _d && result <= 0xffff ? 0x6000 : 0;
            _c = result > 0xffff;
            SetZAndN(result, _m);
            _br[A] = (ushort) result;
        }
    }

    private void Cmp(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr) ^ 0xff;
            int result = (_br[A] & 0xff) + value + 1;
            _c = result > 0xff;
            SetZAndN(result, _m);
        }
        else
        {
            int value = ReadWord(adr, adrh) ^ 0xffff;
            CyclesLeft++;
            int result = _br[A] + value + 1;
            _c = result > 0xffff;
            SetZAndN(result, _m);
        }
    }

    private void Cpx(int adr, int adrh) 
    {
        if (_x)
        {
            int value = ReadBus(adr) ^ 0xff;
            int result = (_br[X] & 0xff) + value + 1;
            _c = result > 0xff;
            SetZAndN(result, _x);
        }
        else
        {
            int value = ReadWord(adr, adrh) ^ 0xffff;
            CyclesLeft++;
            int result = _br[X] + value + 1;
            _c = result > 0xffff;
            SetZAndN(result, _x);
        }
    }

    private void Cpy(int adr, int adrh)
    {
        if (_x)
        {
            int value = ReadBus(adr) ^ 0xff;
            int result = (_br[Y] & 0xff) + value + 1;
            _c = result > 0xff;
            SetZAndN(result, _x);
        }
        else
        {
            int value = ReadWord(adr, adrh) ^ 0xffff;
            CyclesLeft++;
            int result = _br[Y] + value + 1;
            _c = result > 0xffff;
            SetZAndN(result, _x);
        }
    }

    private void Dec(int adr, int adrh)
    {
        if (_m)
        {
            int result = (ReadBus(adr) - 1) & 0xff;
            SetZAndN(result, _m);
            WriteBus(adr, (byte) result);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            int result = (value - 1) & 0xffff;
            SetZAndN(result, _m);
            WriteWord(adr, adrh, result, true);
        }
    }

    private void Deca(int adr, int adrh)
    {
        if (_m)
        {
            int result = ((_br[A] & 0xff) - 1) & 0xff;
            SetZAndN(result, _m);
            _br[A] = (ushort) (_br[A] & 0xff00 | result);
        }
        else
        {
            _br[A]--;
            SetZAndN(_br[A], _m);
        }
    }

    private void Dex(int adr, int adrh) 
    {
        if (_x)
        {
            int result = ((_br[X] & 0xff) - 1) & 0xff;
            SetZAndN(result, _x);
            _br[X] = (ushort) result;
        }
        else
        {
            _br[X]--;
            SetZAndN(_br[X], _x);
        }
    }

    private void Dey(int adr, int adrh) 
    {
        if (_x)
        {
            int result = ((_br[Y] & 0xff) - 1) & 0xff;
            SetZAndN(result, _x);
            _br[Y] = (ushort) result;
        }
        else
        {
            _br[Y]--;
            SetZAndN(_br[Y], _x);
        }
    }

    private void Inc(int adr, int adrh) 
    {
        if (_m)
        {
            int result = (ReadBus(adr) + 1) & 0xff;
            SetZAndN(result, _m);
            WriteBus(adr, (byte) result);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            int result = (value + 1) & 0xffff;
            SetZAndN(result, _m);
            WriteWord(adr, adrh, result, true);
        }
    }

    private void Inca(int adr, int adrh)
    {
        if (_m)
        {
            int result = ((_br[A] & 0xff) + 1) & 0xff;
            SetZAndN(result, _m);
            _br[A] = (ushort) (_br[A] & 0xff00 | result);
        }
        else
        {
            _br[A]++;
            SetZAndN(_br[A], _m);
        }
    }

    private void Inx(int adr, int adrh)
    {
        if (_x)
        {
            int result = ((_br[X] & 0xff) + 1) & 0xff;
            SetZAndN(result, _x);
            _br[X] = (ushort) result;
        }
        else
        {
            _br[X]++;
            SetZAndN(_br[X], _x);
        }
    }

    private void Iny(int adr, int adrh) 
    {
        if (_x)
        {
            int result = ((_br[Y] & 0xff) + 1) & 0xff;
            SetZAndN(result, _x);
            _br[Y] = (ushort) result;
        }
        else
        {
            _br[Y]++;
            SetZAndN(_br[Y], _x);
        }
    }

    private void And(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (_br[A] & value & 0xff));
            SetZAndN(_br[A], _m);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft++;
            _br[A] &= (ushort) value;
            SetZAndN(_br[A], _m);
        }
    }

    private void Eor(int adr, int adrh)
    {
        if (_m)
        {
            int value = ReadBus(adr);
            _br[A] = (ushort) ((_br[A] & 0xff00) | ((_br[A] ^ value) & 0xff));
            SetZAndN(_br[A], _m);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft++;
            _br[A] ^= (ushort) value;
            SetZAndN(_br[A], _m);
        }
    }

    private void Ora(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            _br[A] = (ushort) ((_br[A] & 0xff00) | ((_br[A] | value) & 0xff));
            SetZAndN(_br[A], _m);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft++;
            _br[A] |= (ushort) value;
            SetZAndN(_br[A], _m);
        }
    }

    private void Bit(int adr, int adrh)
    {
        if (_m)
        {
            int value = ReadBus(adr);
            int result = _br[A] & 0xff & value;
            _z = result == 0;
            _n = (value & 0x80) > 0;
            _v = (value & 0x40) > 0;
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft++;
            int result = _br[A] & value;
            _z = result == 0;
            _n = (value & 0x8000) > 0;
            _v = (value & 0x4000) > 0;
        }
    }

    private void Biti(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            int result = _br[A] & 0xff & value;
            _z = result == 0;
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft++;
            int result = _br[A] & value;
            _z = result == 0;
        }
    }

    private void Trb(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            int result = _br[A] & 0xff & value;
            value = value & ~(_br[A] & 0xff) & 0xff;
            _z = result == 0;
            WriteBus(adr, (byte) value);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            int result = _br[A] & value;
            value = value & ~_br[A] & 0xffff;
            _z = result == 0;
            WriteWord(adr, adrh, value, true);
        }
    }

    private void Tsb(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            int result = _br[A] & 0xff & value;
            value = (value | (_br[A] & 0xff)) & 0xff;
            _z = result == 0;
            WriteBus(adr, (byte) value);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            int result = _br[A] & value;
            value = (value | _br[A]) & 0xffff;
            _z = result == 0;
            WriteWord(adr, adrh, value, true);
        }
    }

    private void Asl(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            _c = (value & 0x80) > 0;
            value <<= 1;
            SetZAndN(value, _m);
            WriteBus(adr, value);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            _c = (value & 0x8000) > 0;
            value <<= 1;
            SetZAndN(value, _m);
            WriteWord(adr, adrh, value, true);
        }
    }

    private void Asla(int adr, int adrh) 
    {
        if (_m)
        {
            int value = _br[A] & 0xff;
            _c = (value & 0x80) > 0;
            value <<= 1;
            SetZAndN(value, _m);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (value & 0xff));
        }
        else
        {
            _c = (_br[A] & 0x8000) > 0;
            CyclesLeft += 2;
            _br[A] <<= 1;
            SetZAndN(_br[A], _m);
        }
    }

    private void Lsr(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            _c = (value & 0x1) > 0;
            value >>= 1;
            SetZAndN(value, _m);
            WriteBus(adr, value);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            _c = (value & 0x1) > 0;
            value >>= 1;
            SetZAndN(value, _m);
            WriteWord(adr, adrh, value, true);
        }
    }

    private void Lsra(int adr, int adrh) 
    {
        if (_m)
        {
            int value = _br[A] & 0xff;
            _c = (value & 0x1) > 0;
            value >>= 1;
            SetZAndN(value, _m);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (value & 0xff));
        }
        else
        {
            _c = (_br[A] & 0x1) > 0;
            CyclesLeft += 2;
            _br[A] >>= 1;
            SetZAndN(_br[A], _m);
        }
    }

    private void Rol(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            value = (value << 1) | (_c ? 1 : 0);
            _c = (value & 0x100) > 0;
            SetZAndN(value, _m);
            WriteBus(adr, (byte) value);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            value = (value << 1) | (_c ? 1 : 0);
            _c = (value & 0x10000) > 0;
            SetZAndN(value, _m);
            WriteWord(adr, adrh, value, true);
        }
    }

    private void Rola(int adr, int adrh) 
    {
        if (_m)
        {
            int value = _br[A] & 0xff;
            value = (value << 1) | (_c ? 1 : 0);
            _c = (value & 0x100) > 0;
            SetZAndN(value, _m);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (value & 0xff));
        }
        else
        {
            CyclesLeft += 2;
            int value = (_br[A] << 1) | (_c ? 1 : 0);
            _c = (value & 0x10000) > 0;
            SetZAndN(value, _m);
            _br[A] = (ushort) value;
        }
    }

    private void Ror(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            int carry = value & 0x1;
            value = (value >> 1) | (_c ? 0x80 : 0);
            _c = carry > 0;
            SetZAndN(value, _m);
            WriteBus(adr, (byte) value);
        }
        else
        {
            int value = ReadWord(adr, adrh);
            CyclesLeft += 2;
            int carry = value & 0x1;
            value = (value >> 1) | (_c ? 0x8000 : 0);
            _c = carry > 0;
            SetZAndN(value, _m);
            WriteWord(adr, adrh, value, true);
        }
    }

    private void Rora(int adr, int adrh) 
    {
        if (_m)
        {
            int value = _br[A] & 0xff;
            int carry = value & 0x1;
            value = (value >> 1) | (_c ? 0x80 : 0);
            _c = carry > 0;
            SetZAndN(value, _m);
            _br[A] = (ushort) ((_br[A] & 0xff00) | (value & 0xff));
        }
        else
        {
            CyclesLeft += 2;
            int carry = _br[A] & 0x1;
            int value = (_br[A] >> 1) | (_c ? 0x8000 : 0);
            _c = carry > 0;
            SetZAndN(value, _m);
            _br[A] = (ushort) value;
        }
    }

    private void Bcc(int adr, int adrh) 
    {
        DoBranch(!_c, adr);
    }

    private void Bcs(int adr, int adrh)
    {
        DoBranch(_c, adr);
    }

    private void Beq(int adr, int adrh) 
    {
        DoBranch(_z, adr);
    }

    private void Bmi(int adr, int adrh) 
    {
        DoBranch(_n, adr);
    }

    private void Bne(int adr, int adrh) 
    {
        DoBranch(!_z, adr);
    }

    private void Bpl(int adr, int adrh) 
    {
        DoBranch(!_n, adr);
    }

    private void Bra(int adr, int adrh) 
    {
        _br[PC] = (ushort) (_br[PC] + adr);
    }

    private void Bvc(int adr, int adrh) 
    {
        DoBranch(!_v, adr);
    }

    private void Bvs(int adr, int adrh)
    {
        DoBranch(_v, adr);
    }

    private void Brl(int adr, int adrh)
    {
        _br[PC] = (ushort) (_br[PC] + adr);
    }

    private void Jmp(int adr, int adrh)
    {
        _br[PC] = (ushort) (adr & 0xffff);
    }

    private void Jml(int adr, int adrh) 
    {
        _r[K] = (byte) ((adr & 0xff0000) >> 16);
        _br[PC] = (ushort) (adr & 0xffff);
    }

    private void Jsl(int adr, int adrh)
    {
        int pushPc = (_br[PC] - 1) & 0xffff;
        PushByte(_r[K]);
        PushWord(pushPc);
        _r[K] = (byte) ((adr & 0xff0000) >> 16);
        _br[PC] = (ushort) (adr & 0xffff);
    }

    private void Jsr(int adr, int adrh)
    {
        int pushPc = (_br[PC] - 1) & 0xffff;
        PushWord(pushPc);
        _br[PC] = (ushort) (adr & 0xffff);
    }

    private void Rtl(int adr, int adrh) 
    {
        int pullPc = PullWord();
        _r[K] = (byte) PullByte();
        _br[PC] = (ushort) (pullPc + 1);
    }

    private void Rts(int adr, int adrh)
    {
        int pullPc = PullWord();
        _br[PC] = (ushort) (pullPc + 1);
    }

    private void Brk(int adr, int adrh)
    {
        int pushPc = (_br[PC] + 1) & 0xffff;
        if (!_e) PushByte(_r[K]);
        PushWord(pushPc);
        PushByte(GetP());
        CyclesLeft++;
        _i = true;
        _d = false;
        _r[K] = 0;
        int vector = _e ? 0xfffe : 0xffe6;
        _br[PC] = (ushort) (ReadBus(vector) | (ReadBus(vector + 1) << 8));
    }

    private void Cop(int adr, int adrh) 
    {
        if (!_e) PushByte(_r[K]);
        PushWord(_br[PC]);
        PushByte(GetP());
        CyclesLeft++;
        _i = true;
        _d = false;
        _r[K] = 0;
        int vector = _e ? 0xfff4 : 0xffe4;
        _br[PC] = (ushort) (ReadBus(vector) | (ReadBus(vector + 1) << 8));
    }

    private void Abo(int adr, int adrh)
    {
        if (!_e) PushByte(_r[K]);
        PushWord(_br[PC]);
        PushByte(GetP());
        CyclesLeft++;
        _i = true;
        _d = false;
        _r[K] = 0;
        int vector = _e ? 0xfff8 : 0xffe8;
        _br[PC] = (ushort) (ReadBus(vector) | (ReadBus(vector + 1) << 8));
    }

    private void Nmi(int adr, int adrh)
    {
        if (!_e) PushByte(_r[K]);
        PushWord(_br[PC]);
        PushByte(GetP());
        CyclesLeft++;
        _i = true;
        _d = false;
        _r[K] = 0;
        int vector = _e ? 0xfffa : 0xffea;
        _br[PC] = (ushort) (ReadBus(vector) | (ReadBus(vector + 1) << 8));
    }

    private void Irq(int adr, int adrh) 
    {
        if (!_e) PushByte(_r[K]);
        PushWord(_br[PC]);
        PushByte(GetP());
        CyclesLeft++;
        _i = true;
        _d = false;
        _r[K] = 0;
        int vector = _e ? 0xfffe : 0xffee;
        _br[PC] = (ushort) (ReadBus(vector) | (ReadBus(vector + 1) << 8));
    }

    private void Rti(int adr, int adrh) 
    {
        SetP((byte) PullByte());
        CyclesLeft++;
        int pullPc = PullWord();
        if (!_e) _r[K] = (byte) PullByte();
        _br[PC] = (ushort) pullPc;
    }

    private void Clc(int adr, int adrh) 
    {
        _c = false;
    }

    private void Cld(int adr, int adrh) 
    {
        _d = false;
    }

    private void Cli(int adr, int adrh) 
    {
        _i = false;
    }

    private void Clv(int adr, int adrh)
    {
        _v = false;
    }

    private void Sec(int adr, int adrh) 
    {
        _c = true;
    }

    private void Sed(int adr, int adrh) 
    {
        _d = true;
    }

    private void Sei(int adr, int adrh)
    {
        _i = true;
    }

    private void Rep(int adr, int adrh)
    {
        int value = ReadBus(adr);
        SetP((byte) (GetP() & ~value));
    }

    private void Sep(int adr, int adrh) 
    {
        int value = ReadBus(adr);
        SetP((byte) (GetP() | value));
    }

    private void Lda(int adr, int adrh) 
    {
        if (_m)
        {
            int value = ReadBus(adr);
            if (TraceLdaWatch && _snesImpl != null && adr >= 0x6040 && adr <= 0x6050)
            {
                Console.WriteLine($"[LDA-WATCH] adr=0x{adr:X4} val=0x{value:X2} pc=0x{ProgramCounter24:X6} m=1");
            }
            _br[A] = (ushort) ((_br[A] & 0xff00) | (value & 0xff));
            SetZAndN(value, _m);
        }
        else
        {
            CyclesLeft++;
            int value = ReadWord(adr, adrh);
            if (TraceLdaWatch && _snesImpl != null && adr >= 0x6040 && adr <= 0x6050)
            {
                Console.WriteLine($"[LDA-WATCH] adr=0x{adr:X4} adrh=0x{adrh:X4} val=0x{value:X4} pc=0x{ProgramCounter24:X6} m=0");
            }
            _br[A] = (ushort) value;
            SetZAndN(_br[A], _m);
        }
    }

    private void Ldx(int adr, int adrh) 
    {
        if (_x)
        {
            _br[X] = (ushort) ReadBus(adr);
            if (TraceIndexWatch && adr >= 0x36D0 && adr <= 0x36DF)
            {
                Console.WriteLine($"[LDX-WATCH] adr=0x{adr:X4} val=0x{_br[X]:X2} pc=0x{ProgramCounter24:X6} x=1 D=0x{_br[DPR]:X4}");
            }
            SetZAndN(_br[X], _x);
        }
        else
        {
            CyclesLeft++;
            _br[X] = (ushort) ReadWord(adr, adrh);
            if (TraceIndexWatch && adr >= 0x36D0 && adr <= 0x36DF)
            {
                Console.WriteLine($"[LDX-WATCH] adr=0x{adr:X4} adrh=0x{adrh:X4} val=0x{_br[X]:X4} pc=0x{ProgramCounter24:X6} x=0 D=0x{_br[DPR]:X4}");
            }
            SetZAndN(_br[X], _x);
        }
    }

    private void Ldy(int adr, int adrh) 
    {
        if (_x)
        {
            _br[Y] = (ushort) ReadBus(adr);
            if (TraceIndexWatch && adr >= 0x36D0 && adr <= 0x36DF)
            {
                Console.WriteLine($"[LDY-WATCH] adr=0x{adr:X4} val=0x{_br[Y]:X2} pc=0x{ProgramCounter24:X6} x=1 D=0x{_br[DPR]:X4}");
            }
            SetZAndN(_br[Y], _x);
        }
        else
        {
            CyclesLeft++;
            _br[Y] = (ushort) ReadWord(adr, adrh);
            if (TraceIndexWatch && adr >= 0x36D0 && adr <= 0x36DF)
            {
                Console.WriteLine($"[LDY-WATCH] adr=0x{adr:X4} adrh=0x{adrh:X4} val=0x{_br[Y]:X4} pc=0x{ProgramCounter24:X6} x=0 D=0x{_br[DPR]:X4}");
            }
            SetZAndN(_br[Y], _x);
        }
    }

    private void Sta(int adr, int adrh)
    {
        if (_m)
        {
            WriteBus(adr, (byte) (_br[A] & 0xff));
        }
        else
        {
            CyclesLeft++;
            WriteWord(adr, adrh, _br[A]);
        }
    }

    private void Stx(int adr, int adrh)
    {
        if (_x)
        {
            WriteBus(adr, (byte) (_br[X] & 0xff));
        }
        else
        {
            CyclesLeft++;
            WriteWord(adr, adrh, _br[X]);
        }
    }

    private void Sty(int adr, int adrh) 
    {
        if (_x)
        {
            WriteBus(adr, (byte) (_br[Y] & 0xff));
        }
        else
        {
            CyclesLeft++;
            WriteWord(adr, adrh, _br[Y]);
        }
    }

    private void Stz(int adr, int adrh) 
    {
        if (_m)
        {
            WriteBus(adr, 0);
        }
        else
        {
            CyclesLeft++;
            WriteWord(adr, adrh, 0);
        }
    }

    private void Mvn(int adr, int adrh) 
    {
        _r[DBR] = (byte) adr;
        WriteBus((adr << 16) | _br[Y], ReadBus((adrh << 16) | _br[X]));
        _br[A]--;
        _br[X]++;
        _br[Y]++;
        if (_br[A] != 0xffff)
        {
            _br[PC] -= 3;
        }
    }

    private void Mvp(int adr, int adrh) 
    {
        _r[DBR] = (byte) adr;
        WriteBus((adr << 16) | _br[Y], ReadBus((adrh << 16) | _br[X]));
        _br[A]--;
        _br[X]--;
        _br[Y]--;
        if (_br[A] != 0xffff)
        {
            _br[PC] -= 3;
        }
    }

    private static void Nop(int adr, int adrh) { }

    private static void Wdm(int adr, int adrh) { }

    private void Pea(int adr, int adrh) 
    {
        PushWord(ReadWord(adr, adrh));
    }

    private void Pei(int adr, int adrh) 
    {
        PushWord(ReadWord(adr, adrh));
    }

    private void Per(int adr, int adrh) 
    {
        PushWord((_br[PC] + adr) & 0xffff);
    }

    private void Pha(int adr, int adrh)
    {
        if (_m)
        {
            PushByte((byte) (_br[A] & 0xff));
        }
        else
        {
            CyclesLeft++;
            PushWord(_br[A]);
        }
    }

    private void Phx(int adr, int adrh)
    {
        if (_x)
        {
            PushByte((byte) (_br[X] & 0xff));
        }
        else
        {
            CyclesLeft++;
            PushWord(_br[X]);
        }
    }

    private void Phy(int adr, int adrh) 
    {
        if (_x)
        {
            PushByte((byte) (_br[Y] & 0xff));
        }
        else
        {
            CyclesLeft++;
            PushWord(_br[Y]);
        }
    }

    private void Pla(int adr, int adrh) 
    {
        if (_m)
        {
            _br[A] = (ushort) ((_br[A] & 0xff00) | (PullByte() & 0xff));
            SetZAndN(_br[A], _m);
        }
        else
        {
            CyclesLeft++;
            _br[A] = (ushort) PullWord();
            SetZAndN(_br[A], _m);
        }
    }

    private void Plx(int adr, int adrh)
    {
        if (_x)
        {
            _br[X] = (ushort) PullByte();
            SetZAndN(_br[X], _x);
        }
        else
        {
            CyclesLeft++;
            _br[X] = (ushort) PullWord();
            SetZAndN(_br[X], _x);
        }
    }

    private void Ply(int adr, int adrh)
    {
        if (_x)
        {
            _br[Y] = (ushort) PullByte();
            SetZAndN(_br[Y], _x);
        }
        else
        {
            CyclesLeft++;
            _br[Y] = (ushort) PullWord();
            SetZAndN(_br[Y], _x);
        }
    }

    private void Phb(int adr, int adrh) 
    {
        PushByte(_r[DBR]);
    }

    private void Phd(int adr, int adrh) 
    {
        PushWord(_br[DPR]);
    }

    private void Phk(int adr, int adrh) 
    {
        PushByte(_r[K]);
    }

    private void Php(int adr, int adrh)
    {
        PushByte(GetP());
    }

    private void Plb(int adr, int adrh) 
    {
        _r[DBR] = (byte) PullByte();
        SetZAndN(_r[DBR], true);
    }

    private void Pld(int adr, int adrh) 
    {
        _br[DPR] = (ushort) PullWord();
        SetZAndN(_br[DPR], false);
    }

    private void Plp(int adr, int adrh)
    {
        SetP((byte) PullByte());
    }

    private void Stp(int adr, int adrh) 
    {
        _stopped = true;
    }

    private void Wai(int adr, int adrh) 
    {
        _waiting = true;
    }

    private void Tax(int adr, int adrh) 
    {
        if (_x)
        {
            _br[X] = (ushort) (_br[A] & 0xff);
            SetZAndN(_br[X], _x);
        }
        else
        {
            _br[X] = _br[A];
            SetZAndN(_br[X], _x);
        }
    }

    private void Tay(int adr, int adrh) 
    {
        if (_x)
        {
            _br[Y] = (ushort) (_br[A] & 0xff);
            SetZAndN(_br[Y], _x);
        }
        else
        {
            _br[Y] = _br[A];
            SetZAndN(_br[Y], _x);
        }
    }

    private void Tsx(int adr, int adrh) 
    {
        if (_x)
        {
            _br[X] = (ushort) (_br[SP] & 0xff);
            SetZAndN(_br[X], _x);
        }
        else
        {
            _br[X] = _br[SP];
            SetZAndN(_br[X], _x);
        }
    }

    private void Txa(int adr, int adrh) 
    {
        if (_m)
        {
            _br[A] = (ushort) ((_br[A] & 0xff00) | (_br[X] & 0xff));
            SetZAndN(_br[A], _m);
        }
        else
        {
            _br[A] = _br[X];
            SetZAndN(_br[A], _m);
        }
    }

    private void Txs(int adr, int adrh) 
    {
        ushort oldSp = _br[SP];
        _br[SP] = _br[X];
        if (_e)
        {
            _br[SP] = (ushort) (0x0100 | (_br[SP] & 0xff));
        }
        TraceStackPointerSet("TXS", oldSp);
    }

    private void Txy(int adr, int adrh)
    {
        if (_x)
        {
            _br[Y] = (ushort) (_br[X] & 0xff);
            SetZAndN(_br[Y], _x);
        }
        else
        {
            _br[Y] = _br[X];
            SetZAndN(_br[Y], _x);
        }
    }

    private void Tya(int adr, int adrh) 
    {
        if (_m)
        {
            _br[A] = (ushort) ((_br[A] & 0xff00) | (_br[Y] & 0xff));
            SetZAndN(_br[A], _m);
        }
        else
        {
            _br[A] = _br[Y];
            SetZAndN(_br[A], _m);
        }
    }

    private void Tyx(int adr, int adrh) 
    {
        if (_x)
        {
            _br[X] = (ushort) (_br[Y] & 0xff);
            SetZAndN(_br[X], _x);
        }
        else
        {
            _br[X] = _br[Y];
            SetZAndN(_br[X], _x);
        }
    }

    private void Tcd(int adr, int adrh) 
    {
        _br[DPR] = _br[A];
        SetZAndN(_br[DPR], false);
    }

    private void Tcs(int adr, int adrh) 
    {
        ushort oldSp = _br[SP];
        _br[SP] = _br[A];
        TraceStackPointerSet("TCS", oldSp);
    }

    private void Tdc(int adr, int adrh)
    {
        _br[A] = _br[DPR];
        SetZAndN(_br[A], false);
    }

    private void Tsc(int adr, int adrh)
    {
        _br[A] = _br[SP];
        SetZAndN(_br[A], false);
    }

    private void Xba(int adr, int adrh)
    {
        int low = _br[A] & 0xff;
        int high = (_br[A] & 0xff00) >> 8;
        _br[A] = (ushort) ((low << 8) | high);
        SetZAndN(_br[A], true);
    }

    private void Xce(int adr, int adrh) 
    {
        bool temp = _c;
        _c = _e;
        _e = temp;
        if (_e)
        {
            _m = true;
            _x = true;
        }
        if (_x)
        {
            _br[X] &= 0xff;
            _br[Y] &= 0xff;
        }
    }

    private void TraceStackPointerSet(string opName, ushort oldSp)
    {
        if (!TraceStackSet)
            return;

        Console.WriteLine($"[CPU-SP-SET] op={opName} pc=0x{ProgramCounter24:X6} sp=0x{oldSp:X4}->0x{_br[SP]:X4} regs=[{GetDebugStateWithStack()}]");
    }

    private static int ParseTraceLimit(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int value) && value > 0)
            return value;
        return fallback;
    }

    private static bool TryParseTraceRange(string name, out int start, out int end)
    {
        start = 0;
        end = 0;
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string[] parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;
        if (!TryParseHex(parts[0], out start) || !TryParseHex(parts[1], out end))
            return false;
        if (end < start)
        {
            int tmp = start;
            start = end;
            end = tmp;
        }
        return true;
    }

    private static bool TryParseHex(string raw, out int value)
    {
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];
        return int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
