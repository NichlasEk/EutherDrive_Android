using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KSNES.CPU;
using KSNES.SNESSystem;
using KSNES.PictureProcessing;
using KSNES.AudioProcessing;
using KSNES.Rendering;
using KSNES.ROM;
using KSNES.Tracing;

namespace KSNES.Specialchips.SA1;

public sealed class Sa1
{
    private static readonly bool PerfStatsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SNES_PERF"), "1", StringComparison.Ordinal)
        || OperatingSystem.IsAndroid();
    private readonly struct PendingBwramWrite(uint address, byte value)
    {
        public uint Address { get; } = address;
        public byte Value { get; } = value;
    }

    private const int IramLen = 2 * 1024;
    private const uint IramWatchOffset = 0x64E;
    private const uint BwramWatchOffset = 0x004E;
    private const int DefaultPcBreakLogInstructions = 128;
    private const int DefaultPcRangeLogInstructions = 256;
    private const int TargetHistoryLength = 128;
    private static readonly uint[] ExtraIramWatchOffsets =
    [
        0x0105, 0x0106, 0x0107, 0x0108,
        0x06D0, 0x06D1, 0x06D2, 0x06D3,
        0x06DE, 0x06DF, 0x06E0
    ];

    [NonSerialized]
    private readonly byte[] _rom;
    private readonly byte[] _bankTypes = new byte[256];

    private const byte BankTypeRom = 0;
    private const byte BankTypeIram = 1;
    private const byte BankTypeBwram = 2;
    private const byte BankTypeIo = 3;
    private const byte BankTypeBwramBitmap = 4;
    private const byte BankTypeUnmapped = 5;

    private void InitializeBankTypes()
    {
        for (int i = 0; i < 256; i++)
        {
            if (i >= 0xC0) _bankTypes[i] = BankTypeRom;
            else if (i >= 0x40 && i <= 0x5F) _bankTypes[i] = BankTypeBwram;
            else if (i >= 0x60 && i <= 0x6F) _bankTypes[i] = BankTypeBwramBitmap;
            else if (i <= 0x3F || (i >= 0x80 && i <= 0xBF)) _bankTypes[i] = BankTypeIo;
            else _bankTypes[i] = BankTypeUnmapped;
        }
    }

    private byte[] _iram = new byte[IramLen];
    private byte[] _bwram;
    private readonly CPU.CPU _cpu;
    private readonly Sa1Mmc _mmc = new();
    private readonly Sa1Registers _registers = new();
    private readonly Sa1Timer _timer;
    private ulong _bwramWaitCycles;
    private ulong _lastSnesCycles;
    [NonSerialized]
    private readonly Sa1System _system;
    [NonSerialized]
    private readonly bool _traceIramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_IRAM_WATCH"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _traceIramWriteOnly =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_IRAM_WRITE_ONLY"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly HashSet<uint> _traceIramOffsets = ParseTraceOffsets(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_IRAM_ADDRS"));
    [NonSerialized]
    private readonly bool _traceBwramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _traceBwramWriteOnly =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WRITE_ONLY"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly HashSet<uint> _traceBwramOffsets = ParseTraceOffsets(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_ADDRS"));
    [NonSerialized]
    private readonly bool _traceBwramBlockedWrites =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_BLOCKS"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly bool _traceBadDispatchPointer =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BAD_PTR"), "1", StringComparison.Ordinal);
    private bool _inReset = true;
    private bool _lastSa1Reset;
    private bool _lastSa1Wait;
    private bool _lastSa1Nmi;
    [NonSerialized]
    internal long PerfTickTicks;
    [NonSerialized]
    internal long PerfCpuTicks;
    [NonSerialized]
    internal long PerfDmaTicks;
    [NonSerialized]
    internal ulong PerfSa1Cycles;
    [NonSerialized]
    internal ulong PerfCpuCycles;
    [NonSerialized]
    internal ulong PerfBwramWaitCycles;
    [NonSerialized]
    internal ulong PerfDmaCycles;
    [NonSerialized]
    internal ulong PerfRomReads;
    [NonSerialized]
    internal ulong PerfIramReads;
    [NonSerialized]
    internal ulong PerfIoReads;
    [NonSerialized]
    internal ulong PerfBwramReads;
    [NonSerialized]
    internal ulong PerfBwramBitmapReads;
    [NonSerialized]
    internal ulong PerfUnmappedReads;
    [NonSerialized]
    internal ulong PerfIramWrites;
    [NonSerialized]
    internal ulong PerfIoWrites;
    [NonSerialized]
    internal ulong PerfBwramWrites;
    [NonSerialized]
    internal ulong PerfBwramBitmapWrites;
    [NonSerialized]
    internal ulong PerfUnmappedWrites;
    [NonSerialized]
    private bool _badDispatchPointerLogged;
    [NonSerialized]
    private readonly bool _tracePcBreakEnabled;
    [NonSerialized]
    private readonly int _tracePcBreakStart;
    [NonSerialized]
    private readonly int _tracePcBreakEnd;
    [NonSerialized]
    private int _tracePcBreakRemaining;
    [NonSerialized]
    private bool _tracePcBreakTriggered;
    [NonSerialized]
    private readonly bool _tracePcRangeEnabled;
    [NonSerialized]
    private readonly int _tracePcRangeStart;
    [NonSerialized]
    private readonly int _tracePcRangeEnd;
    [NonSerialized]
    private int _tracePcRangeRemaining;
    [NonSerialized]
    private readonly bool _tracePcTargetEnabled;
    [NonSerialized]
    private readonly int _tracePcTarget;
    [NonSerialized]
    private int _tracePcTargetRemaining;
    [NonSerialized]
    private readonly bool _hasPcTraceHooks;
    [NonSerialized]
    private readonly Queue<string> _tracePcTargetHistory = new(TargetHistoryLength);
    [NonSerialized]
    private int _traceBwramBlockedWritesRemaining = ParseTraceLimit("EUTHERDRIVE_TRACE_SA1_BWRAM_BLOCKS_LIMIT", 256);
    [NonSerialized]
    private readonly List<PendingBwramWrite> _pendingSa1BwramWrites = [];
    [NonSerialized]
    private int _debugCpuResetCount;
    [NonSerialized]
    private int _debugLastResetPc;
    [NonSerialized]
    private readonly string _tracePcHookPath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_PC_HOOK_PATH") ?? string.Empty;
    [NonSerialized]
    private readonly object _tracePcHookLock = new();

    public Sa1(byte[] rom, byte[] bwram, bool isPal)
    {
        _rom = rom;
        InitializeBankTypes();
        if (bwram.Length == 0)
        {
            byte[] newBwram = new byte[64 * 1024];
            bwram = newBwram;
        }
        _bwram = bwram;
        _cpu = new CPU.CPU();
        _cpu.StartInNativeMode = false;
        _timer = new Sa1Timer(isPal);
        _system = new Sa1System(this, _cpu);
        _cpu.SetSystem(_system);
        _lastSa1Reset = _registers.Sa1Reset;
        _lastSa1Wait = _registers.Sa1Wait;
        if (TryParseTraceRange(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_PC_BREAK"), out int breakStart, out int breakEnd))
        {
            _tracePcBreakEnabled = true;
            _tracePcBreakStart = breakStart;
            _tracePcBreakEnd = breakEnd;
            _tracePcBreakRemaining = ParseTraceLimit("EUTHERDRIVE_TRACE_SA1_PC_BREAK_LIMIT", DefaultPcBreakLogInstructions);
        }
        if (TryParseTraceRange(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_PC_RANGE"), out int rangeStart, out int rangeEnd))
        {
            _tracePcRangeEnabled = true;
            _tracePcRangeStart = rangeStart;
            _tracePcRangeEnd = rangeEnd;
            _tracePcRangeRemaining = ParseTraceLimit("EUTHERDRIVE_TRACE_SA1_PC_RANGE_LIMIT", DefaultPcRangeLogInstructions);
        }
        if (TryParseTraceAddress(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_PC_TARGET"), out int targetPc))
        {
            _tracePcTargetEnabled = true;
            _tracePcTarget = targetPc;
            _tracePcTargetRemaining = ParseTraceLimit("EUTHERDRIVE_TRACE_SA1_PC_TARGET_LIMIT", 4);
        }
        _hasPcTraceHooks = _tracePcBreakEnabled || _tracePcRangeEnabled || _tracePcTargetEnabled;
    }

    public byte[] Bwram => _bwram;
    public bool RequiresSnesAccessPc => _traceIramWatch || _traceBwramWatch || _traceBwramBlockedWrites;

    public ICPU GetCpu() => _cpu;

    public string GetDebugState()
    {
        string cpuState = _cpu.GetDebugStateWithStack();
        return
            $"pc=0x{_cpu.ProgramCounter24:X6} {cpuState} " +
            $"cpuResetCount={_debugCpuResetCount} lastResetPc=0x{_debugLastResetPc:X6} " +
            $"wait={(_registers.Sa1Wait ? 1 : 0)} reset={(_registers.Sa1Reset ? 1 : 0)} nmi={( _registers.Sa1Nmi ? 1 : 0)} " +
            $"irqFromSnes={(_registers.Sa1IrqFromSnes ? 1 : 0)} timerIrq={(_timer.IrqPending ? 1 : 0)} dmaIrq={(_registers.Sa1DmaIrq ? 1 : 0)} " +
            $"dmaState={_registers.DmaState} dmaEnabled={(_registers.DmaEnabled ? 1 : 0)} src={_registers.DmaSource} dst={_registers.DmaDestination} " +
            $"CRV=0x{_registers.Sa1ResetVector:X4} CNV=0x{_registers.Sa1NmiVector:X4} CIV=0x{_registers.Sa1IrqVector:X4} " +
            $"BMAP=0x{_mmc.Sa1BwramBaseAddr:X5} BMAPS=0x{_mmc.SnesBwramBaseAddr:X5}";
    }

    public string GetKirbyDebugSnapshot()
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"state={GetDebugState()}",
            $"iram36D0=[{GetByteWindow(0x0036D0, 16)}]",
            $"iram36D8=[{GetByteWindow(0x0036D8, 16)}]",
            $"bwram00A0=[{GetBwramWindow(0x00A0, 32)}]",
            $"bwram01A0=[{GetBwramWindow(0x01A0, 32)}]",
            $"bwram02A0=[{GetBwramWindow(0x02A0, 32)}]",
            $"bwram0520=[{GetBwramWindow(0x0520, 32)}]",
            $"bwram05A0=[{GetBwramWindow(0x05A0, 32)}]",
            $"bwram3220=[{GetBwramWindow(0x3220, 32)}]",
            $"bwram3520=[{GetBwramWindow(0x3520, 32)}]",
            $"bwram3620=[{GetBwramWindow(0x3620, 32)}]",
            $"bwram3320=[{GetBwramWindow(0x3320, 32)}]",
            $"bwram3420=[{GetBwramWindow(0x3420, 32)}]",
            $"bwram72A0=[{GetBwramWindow(0x72A0, 16)}]"
        });
    }

    public static bool HasBattery(byte[] rom, int bwramLen)
    {
        if (bwramLen == 0)
            return false;
        if (rom.Length <= 0x7FD6)
            return false;
        byte chipset = rom[0x7FD6];
        return chipset == 0x32 || chipset == 0x35;
    }

    public void SetBwram(byte[] bwram)
    {
        _bwram = bwram.Length == 0 ? new byte[256 * 1024] : bwram;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick(ulong snesCycles)
    {
        if (snesCycles <= _lastSnesCycles)
            return;

        long tickStart = PerfStatsEnabled ? Stopwatch.GetTimestamp() : 0;
        ulong delta = snesCycles - _lastSnesCycles;
        ulong sa1Cycles = delta / 2;
        _lastSnesCycles += sa1Cycles * 2;
        ulong spentWait = Math.Min(sa1Cycles, _bwramWaitCycles);
        ulong cpuCycles = sa1Cycles - spentWait;
        _bwramWaitCycles -= spentWait;
        if (PerfStatsEnabled)
        {
            PerfSa1Cycles += sa1Cycles;
            PerfCpuCycles += cpuCycles;
            PerfBwramWaitCycles += spentWait;
        }

        if (_registers.Sa1Reset != _lastSa1Reset)
        {
            _lastSa1Reset = _registers.Sa1Reset;
            TraceState(_registers.Sa1Reset ? "RESET-ASSERT" : "RESET-DEASSERT");
        }
        if (_registers.Sa1Wait != _lastSa1Wait)
        {
            _lastSa1Wait = _registers.Sa1Wait;
            TraceState(_registers.Sa1Wait ? "WAIT-ASSERT" : "WAIT-DEASSERT");
        }

        // Reset release must not depend on CPU execution eligibility. Kirby 3 programs
        // SA-1 state while the core is effectively halted, and deferring the reset vector
        // load until "not halted" leaves the CPU parked at PC=0x000000.
        if (_registers.Sa1Reset)
        {
            _inReset = true;
            _lastSa1Nmi = false;
        }
        else if (_inReset)
        {
            _cpu.Reset();
            _inReset = false;
            _lastSa1Nmi = false;
            _debugCpuResetCount++;
            _debugLastResetPc = _cpu.ProgramCounter24;
            TraceState("RESET-RELEASE");
        }

        if (!_registers.CpuHalted())
        {
            long cpuStart = PerfStatsEnabled ? Stopwatch.GetTimestamp() : 0;
            ulong i = 0;
            while (i < cpuCycles)
            {
                if (_registers.Sa1Reset)
                {
                    _inReset = true;
                    i++;
                    continue;
                }

                if (_inReset)
                {
                    _cpu.Reset();
                    _inReset = false;
                    TraceState("RESET-RELEASE");
                    i++;
                    continue;
                }

                if (_registers.Sa1Wait)
                {
                    i = cpuCycles; // Fast forward out of wait
                    continue;
                }

                if (_cpu.CyclesLeft > 0)
                {
                    ulong skip = Math.Min(cpuCycles - i, (ulong)_cpu.CyclesLeft);
                    _cpu.CyclesLeft -= (int)skip;
                    i += skip;
                    if (i >= cpuCycles)
                        break;
                }

                _cpu.IrqWanted = (_registers.Sa1IrqFromSnesEnabled && _registers.Sa1IrqFromSnes)
                                 || (_registers.TimerIrqEnabled && _timer.IrqPending)
                                 || (_registers.DmaIrqEnabled && _registers.Sa1DmaIrq);

                bool currentNmi = _registers.Sa1NmiEnabled && _registers.Sa1Nmi;
                if (currentNmi && !_lastSa1Nmi)
                    _cpu.NmiWanted = true;
                _lastSa1Nmi = currentNmi;

                _tracePc = _cpu.ProgramCounter24;
                if (Sa1Trace.IsEnabled || _hasPcTraceHooks)
                    _traceOp = TryGetSa1OpByte(_tracePc);
                int pcBefore = _tracePc;
                _cpu.Cycle();
                if (_cpu.CyclesLeft == 0)
                    FlushPendingSa1BwramWrites();
                int pcAfter = _cpu.ProgramCounter24;
                if (_hasPcTraceHooks)
                {
                    TracePcTargetIfNeeded(pcBefore, pcAfter);
                    TracePcRangeIfNeeded(pcBefore, pcAfter);
                    TracePcBreakIfNeeded(pcBefore, pcAfter);
                }
                
                i++;
            }
            if (PerfStatsEnabled)
                PerfCpuTicks += Stopwatch.GetTimestamp() - cpuStart;
            _bwramWaitCycles += _system.BwramWaitCycles;
            if (PerfStatsEnabled)
                PerfBwramWaitCycles += _system.BwramWaitCycles;
            _system.BwramWaitCycles = 0;
        }

        if (_registers.DmaState != DmaState.Idle)
        {
            long dmaStart = PerfStatsEnabled ? Stopwatch.GetTimestamp() : 0;
            _registers.TickDmaBatch(sa1Cycles, _mmc, _rom, _iram, _bwram);
            if (PerfStatsEnabled)
            {
                PerfDmaTicks += Stopwatch.GetTimestamp() - dmaStart;
                PerfDmaCycles += sa1Cycles;
            }
        }

        _timer.Advance(sa1Cycles);
        if (PerfStatsEnabled)
            PerfTickTicks += Stopwatch.GetTimestamp() - tickStart;
    }

    public void ResyncTo(ulong snesCycles)
    {
        _lastSnesCycles = snesCycles;
    }

    public bool SnesIrq()
    {
        return (_registers.SnesIrqFromSa1Enabled && _registers.SnesIrqFromSa1)
               || (_registers.SnesIrqFromDmaEnabled && _registers.CharacterConversionIrq);
    }

    public bool SnesNmi()
    {
        return false;
    }

    public void Reset()
    {
        _registers.Reset(_timer, _mmc);
        _bwramWaitCycles = 0;
        _lastSnesCycles = 0;
        _inReset = true;
        _lastSa1Reset = _registers.Sa1Reset;
        _lastSa1Wait = _registers.Sa1Wait;
        _lastSa1Nmi = false;
        _debugCpuResetCount = 0;
        _debugLastResetPc = 0;
        _pendingSa1BwramWrites.Clear();
    }

    public void NotifyDmaStart(uint sourceAddress)
    {
        _registers.NotifySnesDmaStart(sourceAddress);
    }

    public void NotifyDmaEnd()
    {
        _registers.NotifySnesDmaEnd();
    }


    private int TryGetSa1OpByte(int pc)
    {
        uint address = (uint)pc & 0xFFFFFF;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        if ((bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)) && (offset <= 0x07FF || (offset >= 0x3000 && offset <= 0x37FF)))
        {
            return _iram[offset & 0x7FF];
        }
        if ((bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)) && offset >= 0x6000 && offset <= 0x7FFF)
        {
            if (_mmc.Sa1BwramSource == BwramMapSource.Normal)
            {
                uint bwramAddr = ResolveSa1BwramWindowAddress(address);
                return _bwram[(int)bwramAddr];
            }

            uint bitmapAddr = _mmc.Sa1BwramBaseAddr | (address & 0x1FFF);
            return ReadBwramBitmap(bitmapAddr);
        }
        if (bank >= 0x40 && bank <= 0x4F)
        {
            uint bwramAddr = address & (uint)(_bwram.Length - 1);
            return _bwram[(int)bwramAddr];
        }
        if (bank >= 0x60 && bank <= 0x6F)
        {
            return ReadBwramBitmap(address);
        }
        if (_mmc.TryMapRomAddress(address, out uint romAddr) && romAddr < _rom.Length)
            return _rom[(int)romAddr];
        return -1;
    }

    private int _tracePc;
    private int _traceOp;

    private void TracePcBreakIfNeeded(int pcBefore, int pcAfter)
    {
        if (!_tracePcBreakEnabled || _tracePcBreakRemaining <= 0)
            return;

        bool beforeInRange = pcBefore >= _tracePcBreakStart && pcBefore <= _tracePcBreakEnd;
        bool afterInRange = pcAfter >= _tracePcBreakStart && pcAfter <= _tracePcBreakEnd;
        if (!_tracePcBreakTriggered)
        {
            if (!beforeInRange || afterInRange)
                return;

            _tracePcBreakTriggered = true;
            Console.WriteLine($"[SA1-PC-BREAK] trigger before=0x{pcBefore:X6} after=0x{pcAfter:X6} state={GetDebugState()}");
        }

        _tracePcBreakRemaining--;
        string beforeBytes = GetOpWindow(pcBefore);
        string afterBytes = GetOpWindow(pcAfter);
        WritePcHookLine($"[SA1-PC-BREAK] pc=0x{pcBefore:X6}->0x{pcAfter:X6} before=[{beforeBytes}] after=[{afterBytes}] state={GetDebugState()}");
    }

    private void TracePcRangeIfNeeded(int pcBefore, int pcAfter)
    {
        if (!_tracePcRangeEnabled || _tracePcRangeRemaining <= 0)
            return;

        if (pcBefore < _tracePcRangeStart || pcBefore > _tracePcRangeEnd)
            return;

        _tracePcRangeRemaining--;
        string beforeBytes = GetOpWindow(pcBefore);
        string afterBytes = GetOpWindow(pcAfter);
        WritePcHookLine($"[SA1-PC-RANGE] pc=0x{pcBefore:X6}->0x{pcAfter:X6} before=[{beforeBytes}] after=[{afterBytes}] state={GetDebugState()}");
    }

    private void TracePcTargetIfNeeded(int pcBefore, int pcAfter)
    {
        if (!_tracePcTargetEnabled)
            return;

        string entry = $"pc=0x{pcBefore:X6}->0x{pcAfter:X6} before=[{GetOpWindow(pcBefore)}] after=[{GetOpWindow(pcAfter)}]";
        if (_tracePcTargetHistory.Count == TargetHistoryLength)
            _tracePcTargetHistory.Dequeue();
        _tracePcTargetHistory.Enqueue(entry);

        if (_tracePcTargetRemaining <= 0 || pcAfter != _tracePcTarget)
            return;

        _tracePcTargetRemaining--;
        WritePcHookLine($"[SA1-PC-TARGET] hit pc=0x{pcAfter:X6} state={GetDebugState()}");
        WritePcHookLine($"[SA1-PC-TARGET] iram0100=[{GetByteWindow(0x000100, 16)}]");
        WritePcHookLine($"[SA1-PC-TARGET] iram36DE=[{GetByteWindow(0x0036DE, 8)}]");
        WritePcHookLine($"[SA1-PC-TARGET] bwram5FF8=[{GetByteWindow(0x005FF8, 16)}]");
        WritePcHookLine($"[SA1-PC-TARGET] bwram6000=[{GetByteWindow(0x006000, 16)}]");
        foreach (string history in _tracePcTargetHistory)
            WritePcHookLine($"[SA1-PC-TARGET] {history}");
    }

    private void WritePcHookLine(string line)
    {
        Console.WriteLine(line);
        if (string.IsNullOrWhiteSpace(_tracePcHookPath))
            return;

        lock (_tracePcHookLock)
        {
            File.AppendAllText(_tracePcHookPath, line + Environment.NewLine);
        }
    }

    private string GetOpWindow(int pc)
    {
        return string.Join(" ", Enumerable.Range(0, 4).Select(i =>
        {
            int op = TryGetSa1OpByte((pc + i) & 0xFFFFFF);
            return op < 0 ? "--" : $"{op:X2}";
        }));
    }

    private string GetByteWindow(int address, int count)
    {
        return string.Join(" ", Enumerable.Range(0, count).Select(i =>
        {
            int op = TryGetSa1OpByte((address + i) & 0xFFFFFF);
            return op < 0 ? "--" : $"{op:X2}";
        }));
    }

    private string GetBwramWindow(int offset, int count)
    {
        return string.Join(" ", Enumerable.Range(0, count).Select(i =>
        {
            int idx = (offset + i) & (_bwram.Length - 1);
            return $"{_bwram[idx]:X2}";
        }));
    }

    private void TraceBadDispatchPointerIfNeeded()
    {
        if (!_traceBadDispatchPointer || _badDispatchPointerLogged)
            return;

        if (_iram[0x6DE] != 0x05 || _iram[0x6DF] != 0x01 || _iram[0x6E0] != 0x00)
            return;

        _badDispatchPointerLogged = true;
        Console.WriteLine($"[SA1-BAD-PTR] state={GetDebugState()}");
        Console.WriteLine($"[SA1-BAD-PTR] op=[{GetOpWindow(_cpu.ProgramCounter24)}]");
        Console.WriteLine($"[SA1-BAD-PTR] iram36D8=[{GetByteWindow(0x0036D8, 16)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram00A0=[{GetBwramWindow(0x00A0, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram0120=[{GetBwramWindow(0x0120, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram02A0=[{GetBwramWindow(0x02A0, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram04A0=[{GetBwramWindow(0x04A0, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram05A0=[{GetBwramWindow(0x05A0, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram3220=[{GetBwramWindow(0x3220, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram3520=[{GetBwramWindow(0x3520, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram3620=[{GetBwramWindow(0x3620, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram3320=[{GetBwramWindow(0x3320, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram3420=[{GetBwramWindow(0x3420, 32)}]");
        Console.WriteLine($"[SA1-BAD-PTR] bwram72A0=[{GetBwramWindow(0x72A0, 16)}]");
    }

    private static bool TryParseTraceRange(string? raw, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string[] parts = raw.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out start) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out end))
        {
            return false;
        }

        if (end < start)
            (start, end) = (end, start);
        return true;
    }

    private static bool TryParseTraceAddress(string? raw, out int address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return int.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private static int ParseTraceLimit(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
    }

    private static HashSet<uint> ParseTraceOffsets(string? raw)
    {
        var offsets = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(raw))
            return offsets;

        foreach (string part in raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (uint.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out uint value))
                offsets.Add(value & 0xFFFF);
        }

        return offsets;
    }

    private void TraceSa1(string rw, uint address, byte value, string region, uint? resolved = null)
    {
        if (!Sa1Trace.IsEnabled)
            return;
        string? regs = Sa1Trace.IncludeRegsEnabled ? _cpu.GetTraceState() : null;
        Sa1Trace.Log("SA1", _tracePc, _traceOp, address, rw, value, region, resolved, regs);
    }

    private void TraceState(string state)
    {
        if (!Sa1Trace.IsEnabled)
            return;
        int pc = _cpu.ProgramCounter24;
        int op = TryGetSa1OpByte(pc);
        string? regs = Sa1Trace.IncludeRegsEnabled ? _cpu.GetTraceState() : null;
        Sa1Trace.Log("SA1", pc, op, 0, "S", 0, state, null, regs);
    }

    private void TraceIramWatch(string source, string rw, uint address, byte value, int pc)
    {
        if (!_traceIramWatch)
            return;
        if (_traceIramWriteOnly && rw != "W")
            return;
        uint offset = address & 0x7FF;
        if (_traceIramOffsets.Count > 0)
        {
            if (!_traceIramOffsets.Contains(offset))
                return;
        }
        else if (offset != IramWatchOffset && Array.IndexOf(ExtraIramWatchOffsets, offset) < 0)
        {
            return;
        }
        Console.WriteLine($"[I-RAM-WATCH] src={source} rw={rw} addr=0x{address:X6} off=0x{offset:X3} val=0x{value:X2} pc=0x{pc:X6}");
    }

    private void TraceBwramWatch(string source, string rw, uint address, uint bwramAddr, byte value, int pc)
    {
        if (!_traceBwramWatch)
            return;
        if (_traceBwramWriteOnly && rw != "W")
            return;
        uint offset = bwramAddr & 0xFFFF;
        uint adr16 = address & 0xFFFF;
        if (_traceBwramOffsets.Count > 0)
        {
            if (!_traceBwramOffsets.Contains(offset) && !_traceBwramOffsets.Contains(adr16))
                return;
        }
        else if (offset != 0x72A4 &&
                 offset != 0x72AC &&
                 offset != 0x72AD &&
                 offset != 0x72AE &&
                 offset != 0x604C &&
                 adr16 != 0x604C &&
                 adr16 != 0x604D &&
                 adr16 != 0x604E &&
                 adr16 != 0x604F &&
                 offset != 0x004C &&
                 offset != 0x004D)
        {
            return;
        }
        Console.WriteLine($"[BW-RAM-WATCH] src={source} rw={rw} addr=0x{address:X6} bwram=0x{bwramAddr:X6} val=0x{value:X2} pc=0x{pc:X6}");
    }

    private void TraceBwramBlockedWrite(string source, uint address, uint bwramAddr, byte value, int pc)
    {
        if (!_traceBwramBlockedWrites || _traceBwramBlockedWritesRemaining <= 0)
            return;

        _traceBwramBlockedWritesRemaining--;
        Console.WriteLine(
            $"[BW-RAM-BLOCK] src={source} addr=0x{address:X6} bwram=0x{bwramAddr:X6} val=0x{value:X2} pc=0x{pc:X6} " +
            $"writeEnabledSNES={(_registers.SnesBwramWritesEnabled ? 1 : 0)} writeEnabledSA1={(_registers.Sa1BwramWritesEnabled ? 1 : 0)} " +
            $"bwpa=0x{_registers.BwramWriteProtectionSize:X6}");
    }

    public bool TryResolveSnesAccess(uint address, out string region, out uint? resolved)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x2200 and <= 0x22FF):
            case (>= 0x80 and <= 0xBF, >= 0x2200 and <= 0x22FF):
                region = "SA1-IO";
                resolved = null;
                return true;
            case (<= 0x3F, >= 0x2300 and <= 0x230F):
            case (>= 0x80 and <= 0xBF, >= 0x2300 and <= 0x230F):
                region = "SA1-IO";
                resolved = null;
                return true;
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                region = "I-RAM";
                resolved = (address & 0x7FF);
                return true;
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                region = "BW-RAM-WIN";
                resolved = (_mmc.SnesBwramBaseAddr | (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                return true;
            case (>= 0x40 and <= 0x5F, _):
                region = _registers.CcdmaTransferInProgress ? "CCDMA" : "BW-RAM";
                resolved = address & (uint)(_bwram.Length - 1);
                return true;
            case (<= 0x3F, >= 0x8000):
            case (>= 0x80 and <= 0xBF, >= 0x8000):
            case (>= 0xC0 and <= 0xFF, _):
                {
                    uint? romAddr = _mmc.MapRomAddress(address);
                    region = "ROM";
                    resolved = romAddr;
                    return romAddr.HasValue;
                }
        }
        region = "UNMAPPED";
        resolved = null;
        return false;
    }

    public void TraceSnesReadMirror(uint address, byte value, string region, uint? resolved)
    {
        if (!Sa1Trace.IsEnabled)
            return;
        int pc = _cpu.ProgramCounter24;
        int op = TryGetSa1OpByte(pc);
        string? regs = Sa1Trace.IncludeRegsEnabled ? _cpu.GetTraceState() : null;
        Sa1Trace.Log("SA1", pc, op, address, "R", value, region, resolved, regs);
    }

    public byte? SnesRead(uint address, int snesPc = -1)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x8000):
            case (>= 0x80 and <= 0xBF, >= 0x8000):
            case (>= 0xC0 and <= 0xFF, _):
                {
                    InterruptVectorSource nmiSource = _registers.SnesNmiVectorSource;
                    InterruptVectorSource irqSource = _registers.SnesIrqVectorSource;
                    bool isVectorBank = bank == 0x00 || bank == 0x80;
                    if (isVectorBank && offset == 0xFFEA && nmiSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesNmiVector.Lsb();
                    if (isVectorBank && offset == 0xFFEB && nmiSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesNmiVector.Msb();
                    if (isVectorBank && offset == 0xFFEE && irqSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesIrqVector.Lsb();
                    if (isVectorBank && offset == 0xFFEF && irqSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesIrqVector.Msb();

                    return _mmc.TryReadRomByte(address, _rom, out byte value) ? value : (byte?)null;
                }
            case (<= 0x3F, >= 0x2300 and <= 0x230F):
            case (>= 0x80 and <= 0xBF, >= 0x2300 and <= 0x230F):
                return _registers.SnesRead(address, _timer, _mmc, _rom);
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                {
                    byte value = _iram[(int)(address & 0x7FF)];
                    TraceIramWatch("SNES", "R", address, value, snesPc);
                    return value;
                }
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                {
                    uint bwramAddr = (_mmc.SnesBwramBaseAddr | (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                    byte value = _bwram[(int)bwramAddr];
                    TraceBwramWatch("SNES", "R", address, bwramAddr, value, snesPc);
                    return value;
                }
            case (>= 0x40 and <= 0x5F, _):
                if (_registers.CcdmaTransferInProgress)
                    return _registers.NextCcdmaByte(_iram, _bwram);
                else
                {
                    uint bwramAddr = address & (uint)(_bwram.Length - 1);
                    byte value = _bwram[(int)bwramAddr];
                    TraceBwramWatch("SNES", "R", address, bwramAddr, value, snesPc);
                    return value;
                }
        }

        return null;
    }

    public bool TrySnesWrite(uint address, byte value, out bool touchesBwram, int snesPc = -1)
    {
        touchesBwram = false;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x2200 and <= 0x22FF):
            case (>= 0x80 and <= 0xBF, >= 0x2200 and <= 0x22FF):
                _registers.SnesWrite(address, value, _mmc, _rom, _iram);
                return true;
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                {
                    uint iramAddr = address & 0x7FF;
                    int writeProtectIdx = (int)(iramAddr >> 8);
                    if (_registers.SnesIramWritesEnabled[writeProtectIdx])
                        _iram[(int)iramAddr] = value;
                    TraceIramWatch("SNES", "W", address, value, snesPc);
                    return true;
                }
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                {
                    touchesBwram = true;
                    uint bwramAddr = (_mmc.SnesBwramBaseAddr | (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: true))
                    {
                        _bwram[(int)bwramAddr] = value;
                        TraceBwramWatch("SNES", "W", address, bwramAddr, value, snesPc);
                    }
                    else
                    {
                        TraceBwramWatch("SNES", "W(BLOCKED)", address, bwramAddr, value, snesPc);
                        TraceBwramBlockedWrite("SNES", address, bwramAddr, value, snesPc);
                    }
                    return true;
                }
            case (>= 0x40 and <= 0x5F, _):
                {
                    touchesBwram = true;
                    uint bwramAddr = address & (uint)(_bwram.Length - 1);
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: true))
                    {
                        _bwram[(int)bwramAddr] = value;
                        TraceBwramWatch("SNES", "W", address, bwramAddr, value, snesPc);
                    }
                    else
                    {
                        TraceBwramWatch("SNES", "W(BLOCKED)", address, bwramAddr, value, snesPc);
                        TraceBwramBlockedWrite("SNES", address, bwramAddr, value, snesPc);
                    }
                    return true;
                }
        }

        return false;
    }

    public void SnesWrite(uint address, byte value, int snesPc = -1)
    {
        TrySnesWrite(address, value, out _, snesPc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadSa1Cpu(uint address, out bool bwramWait)
    {
        bwramWait = false;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;

        byte bankType = _bankTypes[bank];

        if (bankType == BankTypeIo)
        {
            if (offset >= 0x8000)
            {
                if (bank == 0x00)
                {
                    if (offset == 0xFFEA) return CountIoRead(_registers.Sa1NmiVector.Lsb());
                    if (offset == 0xFFEB) return CountIoRead(_registers.Sa1NmiVector.Msb());
                    if (offset == 0xFFEE) return CountIoRead(_registers.Sa1IrqVector.Lsb());
                    if (offset == 0xFFEF) return CountIoRead(_registers.Sa1IrqVector.Msb());
                    if (offset == 0xFFFC) return CountIoRead(_registers.Sa1ResetVector.Lsb());
                    if (offset == 0xFFFD) return CountIoRead(_registers.Sa1ResetVector.Msb());
                }
                return CountRomRead(ReadRomByteOrZero(address));
            }

            if (offset <= 0x07FF || (offset >= 0x3000 && offset <= 0x37FF))
            {
                return CountIramRead(_iram[(int)(address & 0x7FF)]);
            }

            if (offset >= 0x2300 && offset <= 0x230F)
            {
                return CountIoRead(_registers.Sa1Read(address, _timer, _mmc, _rom));
            }

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                bwramWait = true;
                if (_mmc.Sa1BwramSource == BwramMapSource.Normal)
                {
                    uint bwramAddr = ResolveSa1BwramWindowAddress(address);
                    return CountBwramRead(_bwram[(int)bwramAddr]);
                }
                return CountBwramBitmapRead(ReadBwramBitmap(_mmc.Sa1BwramBaseAddr | (address & 0x1FFF)));
            }
        }
        else if (bankType == BankTypeRom)
        {
            return CountRomRead(ReadRomByteOrZero(address));
        }
        else if (bankType == BankTypeBwram)
        {
            bwramWait = true;
            return CountBwramRead(_bwram[address & (uint)(_bwram.Length - 1)]);
        }
        else if (bankType == BankTypeBwramBitmap)
        {
            bwramWait = true;
            return CountBwramBitmapRead(ReadBwramBitmap(address));
        }

        return CountUnmappedRead();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteSa1Cpu(uint address, byte value, out bool bwramWait)
    {
        bwramWait = false;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;

        byte bankType = _bankTypes[bank];

        if (bankType == BankTypeIo)
        {
            if (offset >= 0x2200 && offset <= 0x22FF)
            {
                CountIoWrite();
                _registers.Sa1Write(address, value, _timer, _mmc, _rom, _iram);
                return;
            }

            if (offset <= 0x07FF || (offset >= 0x3000 && offset <= 0x37FF))
            {
                CountIramWrite();
                uint iramAddr = address & 0x7FF;
                int writeProtectIdx = (int)(iramAddr >> 8);
                if (_registers.Sa1IramWritesEnabled[writeProtectIdx])
                    _iram[(int)iramAddr] = value;
                return;
            }

            if (offset >= 0x6000 && offset <= 0x7FFF)
            {
                bwramWait = true;
                if (_mmc.Sa1BwramSource == BwramMapSource.Normal)
                {
                    CountBwramWrite();
                    uint bwramAddr = ResolveSa1BwramWindowAddress(address);
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: false))
                        _bwram[(int)bwramAddr] = value;
                }
                else
                {
                    CountBwramBitmapWrite();
                    WriteBwramBitmap(_mmc.Sa1BwramBaseAddr | (address & 0x1FFF), value);
                }
                return;
            }
        }
        else if (bankType == BankTypeBwram)
        {
            bwramWait = true;
            CountBwramWrite();
            uint bwramAddr = address & (uint)(_bwram.Length - 1);
            if (_registers.CanWriteBwram(bwramAddr, isSnes: false))
                _bwram[(int)bwramAddr] = value;
            return;
        }
        else if (bankType == BankTypeBwramBitmap)
        {
            bwramWait = true;
            CountBwramBitmapWrite();
            WriteBwramBitmap(address, value);
            return;
        }

        CountUnmappedWrite();
    }

    internal void ResetPerfCounters()
    {
        if (!PerfStatsEnabled)
            return;

        PerfTickTicks = 0;
        PerfCpuTicks = 0;
        PerfDmaTicks = 0;
        PerfSa1Cycles = 0;
        PerfCpuCycles = 0;
        PerfBwramWaitCycles = 0;
        PerfDmaCycles = 0;
        PerfRomReads = 0;
        PerfIramReads = 0;
        PerfIoReads = 0;
        PerfBwramReads = 0;
        PerfBwramBitmapReads = 0;
        PerfUnmappedReads = 0;
        PerfIramWrites = 0;
        PerfIoWrites = 0;
        PerfBwramWrites = 0;
        PerfBwramBitmapWrites = 0;
        PerfUnmappedWrites = 0;
        _cpu.ResetPerfCounters();
    }

    internal string GetPerfSummary()
    {
        if (!PerfStatsEnabled)
            return string.Empty;

        double tickMs = PerfTickTicks * 1000.0 / Stopwatch.Frequency;
        double cpuMs = PerfCpuTicks * 1000.0 / Stopwatch.Frequency;
        double dmaMs = PerfDmaTicks * 1000.0 / Stopwatch.Frequency;
        return
            $"SA1 tick:{tickMs:0.0}ms  cpu:{cpuMs:0.0}ms  dma:{dmaMs:0.0}ms  cyc:{PerfSa1Cycles}  run:{PerfCpuCycles}  wait:{PerfBwramWaitCycles}  dmaCyc:{PerfDmaCycles}\n" +
            $"SA1 bus  instr:{_cpu.PerfInstructions}  rom:{PerfRomReads}  iram:{PerfIramReads}/{PerfIramWrites}  io:{PerfIoReads}/{PerfIoWrites}  bw:{PerfBwramReads}/{PerfBwramWrites}  bmap:{PerfBwramBitmapReads}/{PerfBwramBitmapWrites}  unm:{PerfUnmappedReads}/{PerfUnmappedWrites}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte CountRomRead(byte value)
    {
        if (PerfStatsEnabled)
            PerfRomReads++;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte CountIramRead(byte value)
    {
        if (PerfStatsEnabled)
            PerfIramReads++;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte CountIoRead(byte value)
    {
        if (PerfStatsEnabled)
            PerfIoReads++;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte CountBwramRead(byte value)
    {
        if (PerfStatsEnabled)
            PerfBwramReads++;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte CountBwramBitmapRead(byte value)
    {
        if (PerfStatsEnabled)
            PerfBwramBitmapReads++;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte CountUnmappedRead()
    {
        if (PerfStatsEnabled)
            PerfUnmappedReads++;
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountIramWrite()
    {
        if (PerfStatsEnabled)
            PerfIramWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountIoWrite()
    {
        if (PerfStatsEnabled)
            PerfIoWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountBwramWrite()
    {
        if (PerfStatsEnabled)
            PerfBwramWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountBwramBitmapWrite()
    {
        if (PerfStatsEnabled)
            PerfBwramBitmapWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountUnmappedWrite()
    {
        if (PerfStatsEnabled)
            PerfUnmappedWrites++;
    }

    private void QueuePendingSa1BwramWrite(uint bwramAddr, byte value)
    {
        _bwram[(int)bwramAddr] = value;
    }

    private void FlushPendingSa1BwramWrites()
    {
        _pendingSa1BwramWrites.Clear();
    }

    private byte ReadBwramBitmap(uint address)
    {
        if (_mmc.BwramBitmapFormat == BwramBitmapBits.Two)
        {
            uint addr = address & 0xFFFFF;
            uint bwramAddr = (addr >> 2) & (uint)(_bwram.Length - 1);
            int shift = (int)(2 * (addr & 0x03));
            return (byte)((_bwram[(int)bwramAddr] >> shift) & 0x03);
        }

        uint addr4 = address & 0x7FFFF;
        uint bwramAddr4 = (addr4 >> 1) & (uint)(_bwram.Length - 1);
        int shift4 = (int)(4 * (addr4 & 0x01));
        return (byte)((_bwram[(int)bwramAddr4] >> shift4) & 0x0F);
    }

    private void WriteBwramBitmap(uint address, byte value)
    {
        if (_mmc.BwramBitmapFormat == BwramBitmapBits.Two)
        {
            uint addr = address & 0xFFFFF;
            uint bwramAddr = (addr >> 2) & (uint)(_bwram.Length - 1);
            int shift = (int)(2 * (addr & 0x03));
            byte existing = _bwram[(int)bwramAddr];
            byte newValue = (byte)((existing & ~(0x03 << shift)) | ((value & 0x03) << shift));
            _bwram[(int)bwramAddr] = newValue;
        }
        else
        {
            uint addr = address & 0x7FFFF;
            uint bwramAddr = (addr >> 1) & (uint)(_bwram.Length - 1);
            int shift = (int)(4 * (addr & 0x01));
            byte existing = _bwram[(int)bwramAddr];
            byte newValue = (byte)((existing & ~(0x0F << shift)) | ((value & 0x0F) << shift));
            _bwram[(int)bwramAddr] = newValue;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadRomByteOrZero(uint address)
    {
        return _mmc.ReadRomByteOrZero(address, _rom);
    }

    private uint ResolveSa1BwramWindowAddress(uint address)
    {
        return (_mmc.Sa1BwramBaseAddr | (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
    }

    internal sealed class Sa1System : ISNESSystem
    {
        private readonly Sa1 _sa1;
        private readonly ICPU _cpu;
        private readonly IPPU _ppu = new NullPpu();
        private readonly IAPU _apu = new NullApu();
        private readonly IROM _rom = new NullRom();

        public Sa1System(Sa1 sa1, ICPU cpu)
        {
            _sa1 = sa1;
            _cpu = cpu;
        }

        public ulong BwramWaitCycles;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(int addr, bool dma = false)
        {
            uint address = (uint)addr & 0xFFFFFF;
            byte value = _sa1.ReadSa1Cpu(address, out bool bwramWait);
            if (bwramWait)
                BwramWaitCycles++;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int addr, int value, bool dma = false)
        {
            uint address = (uint)addr & 0xFFFFFF;
            _sa1.WriteSa1Cpu(address, (byte)value, out bool bwramWait);
            if (bwramWait)
                BwramWaitCycles++;
        }

        public ISNESSystem Merge(ISNESSystem system) => this;
        public string FileName { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public ICPU CPU => _cpu;
        public IPPU PPU => _ppu;
        public IAPU APU => _apu;
        public IROM ROM { get => _rom; set { } }
        public void StopEmulation() { }
        public void ResumeEmulation() { }
        public void SetKeyDown(SNESButton button) { }
        public void SetKeyUp(SNESButton button) { }
        public void LoadROM(string fileName) { }
        public void Run() { }
        public IRenderer Renderer { get; set; } = new NullRenderer();
        public IAudioHandler AudioHandler { get; set; } = new NullAudioHandler();
        public bool IsRunning() => true;
        public event EventHandler? FrameRendered;
        public bool PPULatch => false;
        public int OpenBus => 0;
        public int XPos => 0;
        public int YPos => 0;
        public bool IsPal { get; set; }

        private sealed class NullPpu : IPPU
        {
            public void CheckOverscan(int line) { }
            public void PrepareSpriteLine(int line) { }
            public void RenderLine(int line) { }
            public int Read(int adr) => 0;
            public void Write(int adr, int value, bool dma = false) { }
            public int[] GetPixels() => Array.Empty<int>();
            public bool FrameOverscan => false;
            public int LatchedHpos { get; set; }
            public int LatchedVpos { get; set; }
            public bool CountersLatched { get; set; }
            public void Reset() { }
            public void SetSystem(ISNESSystem system) { }
        }

        private sealed class NullApu : IAPU
        {
            public byte[] RAM { get; } = new byte[0];
            public ISPC700 Spc { get; } = new NullSpc();
            public void Attach() { }
            public void Cycle() { }
            public bool TryWriteMainCpuPort(int portIndex, byte value) => true;
            public void Write(int adr, byte value) { }
            public byte Read(int adr) => 0;
            public byte[] SpcWritePorts { get; } = new byte[4];
            public byte[] SpcReadPorts { get; set; } = new byte[4];
            public void Reset() { }
            public void SetSamples(float[] left, float[] right) { }
        }

        private sealed class NullSpc : ISPC700
        {
            public ushort ProgramCounter => 0;
            public void SetAPU(IAPU apu) { }
            public void Cycle() { }
            public void Reset() { }
        }

        private sealed class NullRenderer : IRenderer
        {
            public void RenderBuffer(int[] buffer) { }
            public void SetTargetControl(IHasWidthAndHeight box) { }
        }

        private sealed class NullAudioHandler : IAudioHandler
        {
            public float[] SampleBufferL { get; set; } = Array.Empty<float>();
            public float[] SampleBufferR { get; set; } = Array.Empty<float>();
            public void NextBuffer() { }
            public void Pauze() { }
            public void Resume() { }
        }

        private sealed class NullRom : IROM
        {
            public byte Read(int bank, int adr) => 0;
            public void Write(int bank, int adr, byte value) { }
            public Header Header { get; } = new Header { Name = string.Empty };
            public int RomLength => 0;
            public void LoadROM(byte[] data, Header header) { }
            public void LoadSRAM() { }
            public void ResetCoprocessor() { }
            public void RunCoprocessor(ulong snesCycles) { }
            public byte ReadRomByteLoRom(uint address) => 0;
            public bool IrqWanted => false;
            public bool NmiWanted => false;
            public void SetSystem(ISNESSystem system) { }
            public void NotifyDmaStart(uint sourceAddress) { }
            public void NotifyDmaEnd() { }
            public object? Sa1 => null;
            public bool HasCoprocessor => false;
        }
    }
}
