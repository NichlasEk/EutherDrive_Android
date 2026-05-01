using EutherDrive.Core.Savestates;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Cpu
{
    private enum PollingLoadSize
    {
        Byte,
        Word,
        Longword,
    }

    private const int MaxUnsupportedLogs = 256;
    private const byte BlockJitHotThreshold = 16;
    private const int MaxBlockJitCacheEntries = 256;
    public string Name { get; }
    public Sega32XSh2Registers Registers { get; } = new();
    public uint CurrentInstructionPc { get; private set; }
    public ulong CycleCounter { get; private set; }
    public bool ResetPending { get; set; } = true;
    private int _unsupportedLogCount;
    private bool _unsupportedLogSuppressed;
    [NonSerialized] private readonly Dictionary<uint, ulong> _pcSampleTicks = new();
    [NonSerialized] private readonly HashSet<ulong> _unsupportedOpcodeSites = new();
    [NonSerialized] private readonly Dictionary<uint, byte> _blockJitProbeCounts = new();
    [NonSerialized] private readonly Dictionary<uint, CompiledWaitBlock?> _blockJitCache = new();

    private static readonly byte ResetInterruptMask = 0x0F;
    private static readonly bool TraceBootLoop =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BOOT_LOOP"), "1", StringComparison.Ordinal);
    private static readonly bool TraceExceptions =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_EXCEPTIONS"), "1", StringComparison.Ordinal);
    private static readonly bool PerfPcHistogramEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_PERF_PC_HIST"), "1", StringComparison.Ordinal);
    private static readonly uint? TraceInstructionStart = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_INST_START");
    private static readonly uint? TraceInstructionEnd = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_INST_END");
    private static readonly int TraceInstructionMaxLogs = ParseTraceInstructionMaxLogs();
    private static readonly bool DisableTightDelayLoopBatching =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_DISABLE_TIGHT_DELAY_BATCH"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool DisablePollingLoopAcceleration =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_DISABLE_POLL_LOOP_ACCEL"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool DisableBlockJit =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_DISABLE_BLOCK_JIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceBlockJit =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BLOCK_JIT"),
            "1",
            StringComparison.Ordinal);
    private int _traceInstructionLogs;
    private int _blockJitTraceLogs;

    public Sega32XSh2Cpu(string name)
    {
        Name = name;
        RequestReset();
    }

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader) => StateBinarySerializer.ReadInto(reader, this);

    public void RequestReset()
    {
        ResetPending = true;
        Registers.StatusRegister = new Sega32XSh2StatusRegister { InterruptMask = ResetInterruptMask };
    }

    public void ResetTimingState()
    {
        CycleCounter = 0;
        _traceInstructionLogs = 0;
        _pcSampleTicks.Clear();
        _unsupportedOpcodeSites.Clear();
        _blockJitProbeCounts.Clear();
        _blockJitCache.Clear();
        _unsupportedLogCount = 0;
        _unsupportedLogSuppressed = false;
        _blockJitTraceLogs = 0;
    }

    public string? BuildAndResetPerfPcSummary(int maxEntries = 4)
    {
        if (!PerfPcHistogramEnabled || _pcSampleTicks.Count == 0)
            return null;

        KeyValuePair<uint, ulong>[] top = _pcSampleTicks
            .OrderByDescending(static pair => pair.Value)
            .Take(maxEntries)
            .ToArray();
        ulong total = 0;
        foreach (ulong ticks in _pcSampleTicks.Values)
            total += ticks;

        _pcSampleTicks.Clear();
        if (top.Length == 0 || total == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.Append(Name);
        sb.Append(':');
        for (int i = 0; i < top.Length; i++)
        {
            KeyValuePair<uint, ulong> entry = top[i];
            double percent = (entry.Value * 100.0) / total;
            sb.Append(' ');
            sb.Append("pc=0x");
            sb.Append(entry.Key.ToString("X8"));
            sb.Append(' ');
            sb.Append(percent.ToString("0.0"));
            sb.Append('%');
        }

        return sb.ToString();
    }

    public void Execute(ulong ticks, Sega32XSh2Bus bus)
    {
        if (ticks == 0)
            return;

        if (bus.ResetAsserted)
        {
            RequestReset();
            bus.IncrementCycleCounter(5);
            CycleCounter += 5;
            return;
        }

        if (ResetPending)
        {
            ResetPending = false;
            Registers.ProgramCounter = bus.ReadLongword(0x00000000, Sega32XSh2AccessContext.InterruptVector);
            Registers.NextProgramCounter = Registers.ProgramCounter + 2;
            Registers.StackPointer = bus.ReadLongword(0x00000004, Sega32XSh2AccessContext.InterruptVector);
            Registers.VectorBaseRegister = 0;
            Registers.StatusRegister = new Sega32XSh2StatusRegister
            {
                InterruptMask = ResetInterruptMask,
            };

            bus.IncrementCycleCounter(5);
            CycleCounter += 5;
            return;
        }

        for (ulong i = 0; i < ticks; i++)
        {
            if (bus.ShouldStopExecution)
                return;
            if (!bus.TryTickDma())
                break;
            if (bus.ShouldStopExecution)
                return;
        }

        if (Registers.NextInstructionInDelaySlot)
        {
            ExecuteSingleInstruction(bus);
            ticks--;
        }

        if (!Registers.NextInstructionInDelaySlot)
        {
            byte externalInterruptLevel = bus.InterruptLevel;
            byte internalInterruptLevel = bus.InternalInterruptLevel;
            if (externalInterruptLevel > Registers.StatusRegister.InterruptMask
                && externalInterruptLevel >= internalInterruptLevel)
            {
                uint vectorNumber = 64u + (uint)(externalInterruptLevel >> 1);
                HandleException(externalInterruptLevel, vectorNumber, bus);
                return;
            }

            if (internalInterruptLevel > Registers.StatusRegister.InterruptMask)
            {
                HandleException(internalInterruptLevel, bus.InternalInterruptVectorNumber, bus);
                return;
            }
        }

        ulong remainingInstructions = ticks;
        while (remainingInstructions > 0)
        {
            uint pc = Registers.ProgramCounter;
            ushort opcode = bus.ReadOpcode(pc);

            if (!DisableTightDelayLoopBatching &&
                TryExecuteTightDelayLoop(bus, remainingInstructions, pc, opcode, out ulong consumedInstructions))
            {
                remainingInstructions = consumedInstructions >= remainingInstructions ? 0 : remainingInstructions - consumedInstructions;
                if (bus.ShouldStopExecution)
                    return;
                continue;
            }

            if (!DisablePollingLoopAcceleration &&
                TryExecuteIdleBranchLoop(bus, remainingInstructions, pc, opcode, out consumedInstructions))
            {
                remainingInstructions = consumedInstructions >= remainingInstructions ? 0 : remainingInstructions - consumedInstructions;
                if (bus.ShouldStopExecution)
                    return;
                continue;
            }

            if (!DisablePollingLoopAcceleration &&
                TryExecutePollingLoop(bus, remainingInstructions, pc, opcode, out consumedInstructions))
            {
                remainingInstructions = consumedInstructions >= remainingInstructions ? 0 : remainingInstructions - consumedInstructions;
                if (bus.ShouldStopExecution)
                    return;
                continue;
            }

            if (!DisableBlockJit &&
                TryExecuteCompiledWaitBlock(bus, remainingInstructions, pc, opcode, out consumedInstructions))
            {
                remainingInstructions = consumedInstructions >= remainingInstructions ? 0 : remainingInstructions - consumedInstructions;
                if (bus.ShouldStopExecution)
                    return;
                continue;
            }

            ExecuteFetchedInstruction(bus, pc, opcode);
            remainingInstructions--;
            if (bus.ShouldStopExecution)
                return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteTightDelayLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if (remainingInstructions < 2 || bus.CycleLimit == ulong.MaxValue)
            return false;

        if (!TryMatchDelayLoop(bus, loopStartPc, firstOpcode, out int instructionsPerIteration, out uint exitPc, out int registerIndex))
            return false;

        uint counter = Registers.GeneralPurposeRegisters[registerIndex];
        if (counter == 0)
            return false;

        ulong maxIterationsByInstructions = remainingInstructions / (ulong)instructionsPerIteration;
        if (maxIterationsByInstructions == 0)
            return false;

        ulong remainingCycles = bus.CycleLimit > bus.SchedulerCycleCounter
            ? bus.CycleLimit - bus.SchedulerCycleCounter
            : 0;
        ulong maxIterationsByCycles = remainingCycles / (ulong)instructionsPerIteration;
        if (maxIterationsByCycles == 0)
            return false;

        ulong iterations = Math.Min(maxIterationsByInstructions, maxIterationsByCycles);
        iterations = Math.Min(iterations, counter);
        if (iterations == 0)
            return false;

        Registers.GeneralPurposeRegisters[registerIndex] -= (uint)iterations;
        bool loopFinished = Registers.GeneralPurposeRegisters[registerIndex] == 0;
        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = loopFinished;
        Registers.StatusRegister = sr;

        ulong schedulerCycles = iterations * (ulong)instructionsPerIteration;
        bus.IncrementCycleCounter(schedulerCycles);
        // Fetches still cost detail cycles even though the interpreter is batching the loop body.
        bus.IncrementDetailCycleCounter(schedulerCycles);
        CycleCounter += schedulerCycles;

        if (loopFinished)
        {
            Registers.ProgramCounter = exitPc;
            Registers.NextProgramCounter = exitPc + 2;
        }
        else
        {
            Registers.ProgramCounter = loopStartPc;
            Registers.NextProgramCounter = loopStartPc + 2;
        }

        Registers.NextInstructionInDelaySlot = false;
        consumedInstructions = schedulerCycles;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryMatchDelayLoop(
        Sega32XSh2Bus bus,
        uint loopStartPc,
        ushort firstOpcode,
        out int instructionsPerIteration,
        out uint exitPc,
        out int registerIndex)
    {
        instructionsPerIteration = 0;
        exitPc = 0;
        registerIndex = 0;

        if (firstOpcode == 0x0009)
        {
            if (!TryMatchDtBfBackLoop(bus, loopStartPc + 2, loopStartPc + 4, loopStartPc, out registerIndex))
                return false;

            instructionsPerIteration = 3;
            exitPc = loopStartPc + 6;
            return true;
        }

        if (!TryMatchDtBfBackLoop(bus, loopStartPc, loopStartPc + 2, loopStartPc, out registerIndex))
            return false;

        instructionsPerIteration = 2;
        exitPc = loopStartPc + 4;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryMatchDtBfBackLoop(Sega32XSh2Bus bus, uint dtPc, uint branchPc, uint branchTargetPc, out int registerIndex)
    {
        registerIndex = 0;

        if (!bus.TryPeekInstructionWord(dtPc, out ushort dtOpcode) ||
            !bus.TryPeekInstructionWord(branchPc, out ushort branchOpcode))
        {
            return false;
        }

        if ((dtOpcode & 0xF0FF) != 0x4010)
            return false;

        if ((branchOpcode & 0xFF00) != 0x8B00)
            return false;

        int displacement = (sbyte)(branchOpcode & 0xFF) << 1;
        uint target = unchecked(branchPc + 4 + (uint)displacement);
        if (target != branchTargetPc)
            return false;

        registerIndex = (dtOpcode >> 8) & 0xF;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteIdleBranchLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if (TraceInstructionStart.HasValue || TraceInstructionEnd.HasValue)
            return false;
        if (remainingInstructions < 2 || bus.CycleLimit == ulong.MaxValue)
            return false;
        if ((firstOpcode & 0xF000) != 0xA000) // BRA disp
            return false;
        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort delayOpcode) || delayOpcode != 0x0009)
            return false;

        int displacement = ((short)(firstOpcode << 4)) >> 4;
        uint target = unchecked(loopStartPc + 4u + (uint)(displacement << 1));
        if (target != loopStartPc)
            return false;

        ulong remainingCycles = bus.CycleLimit > bus.SchedulerCycleCounter
            ? bus.CycleLimit - bus.SchedulerCycleCounter
            : 0;
        if (remainingCycles < 2)
            return false;

        ulong cyclesToConsume = Math.Min(remainingInstructions, remainingCycles);
        if (cyclesToConsume < 2)
            return false;

        Registers.ProgramCounter = loopStartPc;
        Registers.NextProgramCounter = loopStartPc + 2;
        Registers.NextInstructionInDelaySlot = false;

        bus.IncrementCycleCounter(cyclesToConsume);
        CycleCounter += cyclesToConsume;
        AccumulatePcSample(loopStartPc, cyclesToConsume);
        consumedInstructions = cyclesToConsume;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecutePollingLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if (TraceInstructionStart.HasValue || TraceInstructionEnd.HasValue)
            return false;
        if (remainingInstructions < 3 || bus.CycleLimit == ulong.MaxValue)
            return false;

        ulong remainingCycles = bus.CycleLimit > bus.SchedulerCycleCounter
            ? bus.CycleLimit - bus.SchedulerCycleCounter
            : 0;
        if (remainingCycles < 3)
            return false;

        if (!TryDecodePollingLoad(firstOpcode, out int loadRegister, out uint address, out PollingLoadSize loadSize))
            return false;
        if (!bus.IsFastPollingRegister(address))
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort testOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 4, out ushort branchOpcode))
        {
            return false;
        }

        bool branchOnTrue;
        switch (branchOpcode & 0xFF00)
        {
            case 0x8900: // BT disp
                branchOnTrue = true;
                break;
            case 0x8B00: // BF disp
                branchOnTrue = false;
                break;
            default:
                return false;
        }

        uint branchPc = loopStartPc + 4;
        uint branchTarget = unchecked(branchPc + 4u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return false;
        if (!CanEvaluatePollingTest(testOpcode, loadRegister))
            return false;

        Registers.GeneralPurposeRegisters[loadRegister] = ReadPollingLoadValue(bus, address, loadSize);
        if (!TryEvaluatePollingTest(testOpcode, loadRegister, out bool testResult))
            return false;

        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = testResult;
        Registers.StatusRegister = sr;

        bool branchTaken = branchOnTrue ? testResult : !testResult;
        ulong cyclesToConsume = 3;
        if (branchTaken)
            cyclesToConsume = Math.Min(remainingInstructions, remainingCycles);
        if (cyclesToConsume < 3)
            return false;

        if (branchTaken)
        {
            Registers.ProgramCounter = loopStartPc;
            Registers.NextProgramCounter = loopStartPc + 2;
        }
        else
        {
            uint exitPc = loopStartPc + 6;
            Registers.ProgramCounter = exitPc;
            Registers.NextProgramCounter = exitPc + 2;
        }
        Registers.NextInstructionInDelaySlot = false;

        bus.IncrementCycleCounter(cyclesToConsume);
        CycleCounter += cyclesToConsume;
        AccumulatePcSample(loopStartPc, cyclesToConsume);
        consumedInstructions = cyclesToConsume;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteCompiledWaitBlock(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint pc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if ((firstOpcode & 0xF00F) != 0x6001 || ((firstOpcode >> 8) & 0xF) != 0)
            return false;
        if (TraceInstructionStart.HasValue || TraceInstructionEnd.HasValue)
            return false;
        if (remainingInstructions < CompiledWaitBlock.InstructionsPerIteration || bus.CycleLimit == ulong.MaxValue)
            return false;

        if (_blockJitCache.TryGetValue(pc, out CompiledWaitBlock? cachedBlock))
            return cachedBlock != null && cachedBlock.TryExecute(this, bus, remainingInstructions, out consumedInstructions);

        byte probes = _blockJitProbeCounts.GetValueOrDefault(pc);
        if (probes < BlockJitHotThreshold)
        {
            _blockJitProbeCounts[pc] = (byte)(probes + 1);
            return false;
        }

        if (_blockJitCache.Count >= MaxBlockJitCacheEntries)
        {
            _blockJitCache.Clear();
            _blockJitProbeCounts.Clear();
            return false;
        }

        CompiledWaitBlock? block = TryCompileWaitBlock(bus, pc);
        _blockJitCache[pc] = block;
        if (block == null)
            return false;

        if (TraceBlockJit && _blockJitTraceLogs < 32)
        {
            _blockJitTraceLogs++;
            Console.WriteLine($"[S32X-BLOCK-JIT-{Name}] compiled increment-poll loop pc=0x{pc:X8}");
        }

        return block.TryExecute(this, bus, remainingInstructions, out consumedInstructions);
    }

    private static CompiledWaitBlock? TryCompileWaitBlock(Sega32XSh2Bus bus, uint loopStartPc)
    {
        Span<ushort> opcodes = stackalloc ushort[CompiledWaitBlock.InstructionsPerIteration];
        for (int i = 0; i < opcodes.Length; i++)
        {
            if (!bus.TryPeekInstructionWord(loopStartPc + (uint)(i << 1), out opcodes[i]))
                return null;
        }

        ushort loadCounterOpcode = opcodes[0];
        if ((loadCounterOpcode & 0xF00F) != 0x6001 || ((loadCounterOpcode >> 8) & 0xF) != 0)
            return null;
        int counterAddressRegister = (loadCounterOpcode >> 4) & 0xF;

        ushort addCounterOpcode = opcodes[1];
        if ((addCounterOpcode & 0xFF00) != 0x7000)
            return null;
        sbyte counterDelta = unchecked((sbyte)(addCounterOpcode & 0xFF));
        if (counterDelta == 0)
            return null;

        ushort storeCounterOpcode = opcodes[2];
        if ((storeCounterOpcode & 0xF00F) != 0x2001 || ((storeCounterOpcode >> 4) & 0xF) != 0)
            return null;
        if (((storeCounterOpcode >> 8) & 0xF) != counterAddressRegister)
            return null;

        ushort loadPollOpcode = opcodes[3];
        if ((loadPollOpcode & 0xFF00) is not 0xC400 and not 0xC500 and not 0xC600)
            return null;

        PollingLoadSize pollingLoadSize = (loadPollOpcode & 0xFF00) switch
        {
            0xC400 => PollingLoadSize.Byte,
            0xC500 => PollingLoadSize.Word,
            _ => PollingLoadSize.Longword,
        };
        uint pollingDisplacement = pollingLoadSize switch
        {
            PollingLoadSize.Byte => (uint)(loadPollOpcode & 0xFF),
            PollingLoadSize.Word => (uint)((loadPollOpcode & 0xFF) << 1),
            _ => (uint)((loadPollOpcode & 0xFF) << 2),
        };

        ushort compareOpcode = opcodes[4];
        if ((compareOpcode & 0xFF00) != 0x8800)
            return null;
        uint compareImmediate = unchecked((uint)(sbyte)(compareOpcode & 0xFF));

        ushort branchOpcode = opcodes[5];
        bool branchOnTrue;
        switch (branchOpcode & 0xFF00)
        {
            case 0x8900:
                branchOnTrue = true;
                break;
            case 0x8B00:
                branchOnTrue = false;
                break;
            default:
                return null;
        }

        uint branchPc = loopStartPc + 10;
        uint branchTarget = unchecked(branchPc + 4u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return null;

        return new CompiledWaitBlock(
            loopStartPc,
            opcodes.ToArray(),
            counterAddressRegister,
            counterDelta,
            pollingDisplacement,
            pollingLoadSize,
            compareImmediate,
            branchOnTrue);
    }

    private sealed class CompiledWaitBlock
    {
        public const int InstructionsPerIteration = 6;
        private const uint ExitOffset = InstructionsPerIteration * 2u;

        private readonly uint _loopStartPc;
        private readonly ushort[] _opcodes;
        private readonly int _counterAddressRegister;
        private readonly sbyte _counterDelta;
        private readonly uint _pollingDisplacement;
        private readonly PollingLoadSize _pollingLoadSize;
        private readonly uint _compareImmediate;
        private readonly bool _branchOnTrue;

        public CompiledWaitBlock(
            uint loopStartPc,
            ushort[] opcodes,
            int counterAddressRegister,
            sbyte counterDelta,
            uint pollingDisplacement,
            PollingLoadSize pollingLoadSize,
            uint compareImmediate,
            bool branchOnTrue)
        {
            _loopStartPc = loopStartPc;
            _opcodes = opcodes;
            _counterAddressRegister = counterAddressRegister;
            _counterDelta = counterDelta;
            _pollingDisplacement = pollingDisplacement;
            _pollingLoadSize = pollingLoadSize;
            _compareImmediate = compareImmediate;
            _branchOnTrue = branchOnTrue;
        }

        public bool TryExecute(
            Sega32XSh2Cpu cpu,
            Sega32XSh2Bus bus,
            ulong remainingInstructions,
            out ulong consumedInstructions)
        {
            consumedInstructions = 0;
            if (remainingInstructions < InstructionsPerIteration)
                return false;

            ulong remainingCycles = bus.CycleLimit > bus.SchedulerCycleCounter
                ? bus.CycleLimit - bus.SchedulerCycleCounter
                : 0;
            if (remainingCycles < InstructionsPerIteration)
                return false;

            for (int i = 0; i < _opcodes.Length; i++)
            {
                if (!bus.TryPeekInstructionWord(_loopStartPc + (uint)(i << 1), out ushort opcode) || opcode != _opcodes[i])
                    return false;
            }

            uint counterAddress = cpu.Registers.GeneralPurposeRegisters[_counterAddressRegister];
            if (!bus.IsSimpleSdramWordAddress(counterAddress))
                return false;

            uint pollingAddress = cpu.Registers.GlobalBaseRegister + _pollingDisplacement;
            if (!bus.IsFastPollingRegister(pollingAddress))
                return false;

            ushort initialCounter = bus.ReadWord(counterAddress, Sega32XSh2AccessContext.Data);
            uint pollingValue = ReadPollingLoadValue(bus, pollingAddress, _pollingLoadSize);
            bool testResult = pollingValue == _compareImmediate;
            bool branchTaken = _branchOnTrue ? testResult : !testResult;

            ulong iterations = 1;
            if (branchTaken)
            {
                iterations = Math.Min(
                    remainingInstructions / InstructionsPerIteration,
                    remainingCycles / InstructionsPerIteration);
            }

            if (iterations == 0)
                return false;

            int delta = _counterDelta;
            uint totalDelta = (uint)(iterations * (ulong)Math.Abs(delta));
            ushort finalCounter = delta >= 0
                ? (ushort)(initialCounter + totalDelta)
                : (ushort)(initialCounter - totalDelta);
            bus.WriteWord(counterAddress, finalCounter, Sega32XSh2AccessContext.Data);

            cpu.Registers.GeneralPurposeRegisters[0] = pollingValue;
            Sega32XSh2StatusRegister sr = cpu.Registers.StatusRegister;
            sr.T = testResult;
            cpu.Registers.StatusRegister = sr;

            if (branchTaken)
            {
                cpu.Registers.ProgramCounter = _loopStartPc;
                cpu.Registers.NextProgramCounter = _loopStartPc + 2;
            }
            else
            {
                uint exitPc = _loopStartPc + ExitOffset;
                cpu.Registers.ProgramCounter = exitPc;
                cpu.Registers.NextProgramCounter = exitPc + 2;
            }
            cpu.Registers.NextInstructionInDelaySlot = false;

            ulong cycles = iterations * InstructionsPerIteration;
            bus.IncrementCycleCounter(cycles);
            cpu.CycleCounter += cycles;
            cpu.AccumulatePcSample(_loopStartPc, cycles);
            consumedInstructions = cycles;
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDecodePollingLoad(ushort opcode, out int loadRegister, out uint address, out PollingLoadSize loadSize)
    {
        loadRegister = 0;
        address = 0;
        loadSize = PollingLoadSize.Word;

        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;
        switch (opcode & 0xF00F)
        {
            case 0x000C: // MOV.B @(R0, Rm), Rn
                loadRegister = n;
                address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m];
                loadSize = PollingLoadSize.Byte;
                return true;
            case 0x000D: // MOV.W @(R0, Rm), Rn
                loadRegister = n;
                address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m];
                loadSize = PollingLoadSize.Word;
                return true;
            case 0x000E: // MOV.L @(R0, Rm), Rn
                loadRegister = n;
                address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m];
                loadSize = PollingLoadSize.Longword;
                return true;
            case 0x6000: // MOV.B @Rm, Rn
                loadRegister = n;
                address = Registers.GeneralPurposeRegisters[m];
                loadSize = PollingLoadSize.Byte;
                return true;
            case 0x6001: // MOV.W @Rm, Rn
                loadRegister = n;
                address = Registers.GeneralPurposeRegisters[m];
                loadSize = PollingLoadSize.Word;
                return true;
            case 0x6002: // MOV.L @Rm, Rn
                loadRegister = n;
                address = Registers.GeneralPurposeRegisters[m];
                loadSize = PollingLoadSize.Longword;
                return true;
        }

        if ((opcode & 0xF000) == 0x5000) // MOV.L @(disp, Rm), Rn
        {
            loadRegister = n;
            address = Registers.GeneralPurposeRegisters[m] + (uint)((opcode & 0xF) << 2);
            loadSize = PollingLoadSize.Longword;
            return true;
        }

        switch (opcode & 0xFF00)
        {
            case 0xC400: // MOV.B @(disp, GBR), R0
                loadRegister = 0;
                address = Registers.GlobalBaseRegister + (uint)(opcode & 0xFF);
                loadSize = PollingLoadSize.Byte;
                return true;
            case 0xC500: // MOV.W @(disp, GBR), R0
                loadRegister = 0;
                address = Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1);
                loadSize = PollingLoadSize.Word;
                return true;
            case 0xC600: // MOV.L @(disp, GBR), R0
                loadRegister = 0;
                address = Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2);
                loadSize = PollingLoadSize.Longword;
                return true;
            case 0x8400: // MOV.B @(disp, Rm), R0
                loadRegister = 0;
                address = Registers.GeneralPurposeRegisters[m] + (uint)(opcode & 0xF);
                loadSize = PollingLoadSize.Byte;
                return true;
            case 0x8500: // MOV.W @(disp, Rm), R0
                loadRegister = 0;
                address = Registers.GeneralPurposeRegisters[m] + (uint)((opcode & 0xF) << 1);
                loadSize = PollingLoadSize.Word;
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadPollingLoadValue(Sega32XSh2Bus bus, uint address, PollingLoadSize loadSize)
    {
        return loadSize switch
        {
            PollingLoadSize.Byte => unchecked((uint)(sbyte)bus.ReadByte(address, Sega32XSh2AccessContext.Data)),
            PollingLoadSize.Word => unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data)),
            _ => bus.ReadLongword(address, Sega32XSh2AccessContext.Data),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanEvaluatePollingTest(ushort opcode, int loadedRegister)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        return ((opcode & 0xF0FF) == 0x4011 && n == loadedRegister) // CMP/PZ Rn
            || ((opcode & 0xF0FF) == 0x4015 && n == loadedRegister) // CMP/PL Rn
            || ((opcode & 0xF00F) == 0x2008 && (n == loadedRegister || m == loadedRegister)) // TST Rm, Rn
            || ((opcode & 0xF00F) == 0x3000 && (n == loadedRegister || m == loadedRegister)) // CMP/EQ Rm, Rn
            || ((opcode & 0xFF00) == 0x8800 && loadedRegister == 0) // CMP/EQ #imm, R0
            || ((opcode & 0xFF00) == 0xC800 && loadedRegister == 0); // TST #imm, R0
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEvaluatePollingTest(ushort opcode, int loadedRegister, out bool result)
    {
        result = false;

        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;
        switch (opcode & 0xF0FF)
        {
            case 0x4011 when n == loadedRegister: // CMP/PZ Rn
                result = (int)Registers.GeneralPurposeRegisters[n] >= 0;
                return true;
            case 0x4015 when n == loadedRegister: // CMP/PL Rn
                result = (int)Registers.GeneralPurposeRegisters[n] > 0;
                return true;
        }

        switch (opcode & 0xF00F)
        {
            case 0x2008 when n == loadedRegister || m == loadedRegister: // TST Rm, Rn
                result = (Registers.GeneralPurposeRegisters[m] & Registers.GeneralPurposeRegisters[n]) == 0;
                return true;
            case 0x3000 when n == loadedRegister || m == loadedRegister: // CMP/EQ Rm, Rn
                result = Registers.GeneralPurposeRegisters[n] == Registers.GeneralPurposeRegisters[m];
                return true;
        }

        if ((opcode & 0xFF00) == 0xC800 && loadedRegister == 0) // TST #imm, R0
        {
            result = (Registers.GeneralPurposeRegisters[0] & (uint)(opcode & 0xFF)) == 0;
            return true;
        }

        if ((opcode & 0xFF00) == 0x8800 && loadedRegister == 0) // CMP/EQ #imm, R0
        {
            result = Registers.GeneralPurposeRegisters[0] == unchecked((uint)(sbyte)(opcode & 0xFF));
            return true;
        }

        return false;
    }

    private void ExecuteSingleInstruction(Sega32XSh2Bus bus)
    {
        uint pc = Registers.ProgramCounter;
        ushort opcode = bus.ReadOpcode(pc);
        ExecuteFetchedInstruction(bus, pc, opcode);
    }

    private void ExecuteFetchedInstruction(Sega32XSh2Bus bus, uint pc, ushort opcode)
    {
        MaybeTraceInstruction(pc, opcode);

        if (TraceBootLoop && pc >= 0x00000180 && pc <= 0x00000220)
        {
            EmitTraceLine(
                $"[S32X-SH2-{Name}] pc=0x{pc:X8} op=0x{opcode:X4} " +
                $"r0=0x{Registers.GeneralPurposeRegisters[0]:X8} " +
                $"r1=0x{Registers.GeneralPurposeRegisters[1]:X8} " +
                $"r15=0x{Registers.StackPointer:X8} " +
                $"pr=0x{Registers.ProcedureRegister:X8} " +
                $"t={(Registers.StatusRegister.T ? 1 : 0)}");
        }

        Registers.ProgramCounter = Registers.NextProgramCounter;
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;

        CurrentInstructionPc = pc;
        try
        {
            if (TryExecute(opcode, bus))
            {
                bus.IncrementCycleCounter(1);
                CycleCounter += 1;
                AccumulatePcSample(pc, 1);
                return;
            }

            ulong unsupportedKey = ((ulong)pc << 16) | opcode;
            if (_unsupportedOpcodeSites.Add(unsupportedKey) && _unsupportedLogCount < MaxUnsupportedLogs)
            {
                _unsupportedLogCount++;
                EmitTraceLine($"[S32X-SH2-{Name}] illegal opcode 0x{opcode:X4} at PC=0x{pc:X8}");
            }
            else if (_unsupportedLogCount >= MaxUnsupportedLogs && !_unsupportedLogSuppressed)
            {
                _unsupportedLogSuppressed = true;
                EmitTraceLine($"[S32X-SH2-{Name}] further illegal opcode logs suppressed");
            }

            Registers.ProgramCounter = pc;
            Registers.NextProgramCounter = pc + 2;
            Registers.NextInstructionInDelaySlot = false;
            HandleException(null, 4, bus);
            bus.IncrementCycleCounter(1);
            CycleCounter += 1;
            AccumulatePcSample(pc, 1);
        }
        finally
        {
            CurrentInstructionPc = 0;
        }
    }

    private void HandleException(byte? interruptLevel, uint vectorNumber, Sega32XSh2Bus bus)
    {
        uint faultPc = Registers.ProgramCounter;
        uint sp = Registers.StackPointer - 4;
        bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);
        sp -= 4;
        bus.WriteLongword(sp, Registers.ProgramCounter, Sega32XSh2AccessContext.Data);
        Registers.StackPointer = sp;

        if (interruptLevel.HasValue)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.InterruptMask = interruptLevel.Value;
            Registers.StatusRegister = sr;
        }

        uint vectorAddress = Registers.VectorBaseRegister + (vectorNumber << 2);
        Registers.ProgramCounter = bus.ReadLongword(vectorAddress, Sega32XSh2AccessContext.InterruptVector);
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;

        if (TraceExceptions)
        {
            Console.WriteLine($"[S32X-SH2-{Name}-EXC] pc=0x{faultPc:X8} vector=0x{vectorNumber:X2} target=0x{Registers.ProgramCounter:X8}");
        }

        bus.IncrementCycleCounter(5);
        CycleCounter += 5;
    }

    private static void EmitTraceLine(string line) => Console.WriteLine(line);

    private void AccumulatePcSample(uint pc, ulong ticks)
    {
        if (!PerfPcHistogramEnabled || ticks == 0)
            return;

        _pcSampleTicks[pc] = _pcSampleTicks.GetValueOrDefault(pc) + ticks;
    }

    private static uint? ParseOptionalHex(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? parsed
            : null;
    }

    private static int ParseTraceInstructionMaxLogs()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_INST_MAX");
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : 256;
    }

    private void MaybeTraceInstruction(uint pc, ushort opcode)
    {
        if (!TraceInstructionStart.HasValue || !TraceInstructionEnd.HasValue)
            return;
        if (pc < TraceInstructionStart.Value || pc > TraceInstructionEnd.Value)
            return;
        if (_traceInstructionLogs >= TraceInstructionMaxLogs)
            return;

        _traceInstructionLogs++;
        Console.WriteLine(
            $"[S32X-INST-{Name}] pc=0x{pc:X8} op=0x{opcode:X4} " +
            $"r0=0x{Registers.GeneralPurposeRegisters[0]:X8} " +
            $"r1=0x{Registers.GeneralPurposeRegisters[1]:X8} " +
            $"r2=0x{Registers.GeneralPurposeRegisters[2]:X8} " +
            $"r4=0x{Registers.GeneralPurposeRegisters[4]:X8} " +
            $"r14=0x{Registers.GeneralPurposeRegisters[14]:X8} " +
            $"r15=0x{Registers.StackPointer:X8} pr=0x{Registers.ProcedureRegister:X8} " +
            $"t={(Registers.StatusRegister.T ? 1 : 0)} cyc={CycleCounter}");
    }

    private long GetMac() => ((long)(int)Registers.MacHigh << 32) | Registers.MacLow;

    private void SetMac(long value)
    {
        Registers.MacLow = unchecked((uint)value);
        Registers.MacHigh = unchecked((uint)(value >> 32));
    }

    private static bool CompareStringBytes(uint lhs, uint rhs)
    {
        uint xor = lhs ^ rhs;
        return (xor & 0xFF000000) == 0
            || (xor & 0x00FF0000) == 0
            || (xor & 0x0000FF00) == 0
            || (xor & 0x000000FF) == 0;
    }

    private static uint DynamicLogicalShift(uint value, uint amount)
    {
        if ((amount & 0x80000000) == 0)
            return value << (int)(amount & 0x1F);

        int shift = (int)((~amount & 0x1F) + 1);
        return shift == 32 ? 0 : value >> shift;
    }

    private static uint DynamicArithmeticShift(uint value, uint amount)
    {
        if ((amount & 0x80000000) == 0)
            return value << (int)(amount & 0x1F);

        int shift = (int)((~amount & 0x1F) + 1);
        return shift == 32
            ? ((value & 0x80000000) != 0 ? 0xFFFFFFFFu : 0)
            : (uint)((int)value >> shift);
    }

    private bool TryExecute(ushort opcode, Sega32XSh2Bus bus)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        switch (opcode & 0xF000)
        {
            case 0xE000: // MOV #imm, Rn
                Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)(opcode & 0xFF));
                return true;
            case 0xD000: // MOV.L @(disp, PC), Rn
                {
                    uint disp = (uint)((opcode & 0xFF) << 2);
                    uint address = (Registers.NextProgramCounter & ~3u) + disp;
                    Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
                    return true;
                }
            case 0x9000: // MOV.W @(disp, PC), Rn
                {
                    uint disp = (uint)((opcode & 0xFF) << 1);
                    uint address = Registers.NextProgramCounter + disp;
                    Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
                    return true;
                }
            case 0x5000: // MOV.L @(disp, Rm), Rn
                {
                    uint disp = (uint)((opcode & 0xF) << 2);
                    uint address = Registers.GeneralPurposeRegisters[m] + disp;
                    Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
                    return true;
                }
            case 0x1000: // MOV.L Rm, @(disp, Rn)
                {
                    uint disp = (uint)((opcode & 0xF) << 2);
                    uint address = Registers.GeneralPurposeRegisters[n] + disp;
                    bus.WriteLongword(address, Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                    return true;
                }
            case 0x7000: // ADD #imm, Rn
                Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] + (uint)(sbyte)(opcode & 0xFF));
                return true;
            case 0xA000: // BRA label
                {
                    int disp = ((short)(opcode << 4)) >> 4;
                    Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                    Registers.NextInstructionInDelaySlot = true;
                    return true;
                }
            case 0xB000: // BSR label
                {
                    int disp = ((short)(opcode << 4)) >> 4;
                    Registers.ProcedureRegister = Registers.NextProgramCounter;
                    Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                    Registers.NextInstructionInDelaySlot = true;
                    return true;
                }
            case 0x6000:
                switch (opcode & 0xF00F)
                {
                    case 0x6000: // MOV.B @Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        return true;
                    case 0x6001: // MOV.W @Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        return true;
                    case 0x6002: // MOV.L @Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x6003: // MOV Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x6004: // MOV.B @Rm+, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        if (n != m) Registers.GeneralPurposeRegisters[m]++;
                        return true;
                    case 0x6005: // MOV.W @Rm+, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        if (n != m) Registers.GeneralPurposeRegisters[m] += 2;
                        return true;
                    case 0x6006: // MOV.L @Rm+, Rn
                        Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        if (n != m) Registers.GeneralPurposeRegisters[m] += 4;
                        return true;
                    case 0x6007: // NOT Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = ~Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x6008: // SWAP.B Rm, Rn
                        {
                            uint val = Registers.GeneralPurposeRegisters[m];
                            Registers.GeneralPurposeRegisters[n] = (val & 0xFFFF0000) | ((val & 0x000000FF) << 8) | ((val & 0x0000FF00) >> 8);
                            return true;
                        }
                    case 0x6009: // SWAP.W Rm, Rn
                        {
                            uint val = Registers.GeneralPurposeRegisters[m];
                            Registers.GeneralPurposeRegisters[n] = (val << 16) | (val >> 16);
                            return true;
                        }
                    case 0x600A: // NEGC Rm, Rn
                        {
                            uint src = Registers.GeneralPurposeRegisters[m];
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            uint partial = unchecked(0u - src);
                            bool b1 = partial > 0;
                            uint res = unchecked(partial - (sr.T ? 1u : 0u));
                            bool b2 = res > partial;
                            sr.T = b1 || b2;
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x600B: // NEG Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(0u - Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x600C: // EXTU.B Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m] & 0xFF;
                        return true;
                    case 0x600D: // EXTU.W Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m] & 0xFFFF;
                        return true;
                    case 0x600E: // EXTS.B Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x600F: // EXTS.W Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)Registers.GeneralPurposeRegisters[m]);
                        return true;
                }
                break;
            case 0x2000:
                switch (opcode & 0xF00F)
                {
                    case 0x2000: // MOV.B Rm, @Rn
                        bus.WriteByte(Registers.GeneralPurposeRegisters[n], (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2001: // MOV.W Rm, @Rn
                        bus.WriteWord(Registers.GeneralPurposeRegisters[n], (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2002: // MOV.L Rm, @Rn
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2004: // MOV.B Rm, @-Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 1);
                        bus.WriteByte(Registers.GeneralPurposeRegisters[n], (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2005: // MOV.W Rm, @-Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 2);
                        bus.WriteWord(Registers.GeneralPurposeRegisters[n], (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2006: // MOV.L Rm, @-Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2008: // TST Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (Registers.GeneralPurposeRegisters[m] & Registers.GeneralPurposeRegisters[n]) == 0;
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x2009: // AND Rm, Rn
                        Registers.GeneralPurposeRegisters[n] &= Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x200A: // XOR Rm, Rn
                        Registers.GeneralPurposeRegisters[n] ^= Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x200B: // OR Rm, Rn
                        Registers.GeneralPurposeRegisters[n] |= Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x200C: // CMP/STR Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = CompareStringBytes(Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m]);
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x200D: // XTRACT Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = (Registers.GeneralPurposeRegisters[m] << 16) | (Registers.GeneralPurposeRegisters[n] >> 16);
                        return true;
                    case 0x200E: // MULU.W Rm, Rn
                        Registers.MacLow = (Registers.GeneralPurposeRegisters[m] & 0xFFFF) * (Registers.GeneralPurposeRegisters[n] & 0xFFFF);
                        return true;
                    case 0x200F: // MULS.W Rm, Rn
                        Registers.MacLow = unchecked((uint)((short)Registers.GeneralPurposeRegisters[m] * (short)Registers.GeneralPurposeRegisters[n]));
                        return true;
                    case 0x2007: // DIV0S Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.M = (Registers.GeneralPurposeRegisters[m] & 0x80000000) != 0;
                            sr.Q = (Registers.GeneralPurposeRegisters[n] & 0x80000000) != 0;
                            sr.T = sr.M != sr.Q;
                            Registers.StatusRegister = sr;
                            return true;
                        }
                }
                break;
            case 0x3000:
                switch (opcode & 0xF00F)
                {
                    case 0x3000: // CMP/EQ Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = Registers.GeneralPurposeRegisters[n] == Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3002: // CMP/HS Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = Registers.GeneralPurposeRegisters[n] >= Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3003: // CMP/GE Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (int)Registers.GeneralPurposeRegisters[n] >= (int)Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3006: // CMP/HI Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = Registers.GeneralPurposeRegisters[n] > Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3007: // CMP/GT Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (int)Registers.GeneralPurposeRegisters[n] > (int)Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3008: // SUB Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x300A: // SUBC Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            uint p = unchecked(lhs - rhs);
                            bool b1 = p > lhs;
                            uint res = unchecked(p - (sr.T ? 1u : 0u));
                            bool b2 = res > p;
                            sr.T = b1 || b2;
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300B: // SUBV Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            uint res = unchecked(lhs - rhs);
                            bool s1 = (rhs & 0x80000000) != 0;
                            bool s2 = (lhs & 0x80000000) != 0;
                            bool s3 = (res & 0x80000000) != 0;
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (s1 != s2) && (s3 != s2);
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300C: // ADD Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] + Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x300E: // ADDC Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            uint p = unchecked(lhs + rhs);
                            bool c1 = p < lhs;
                            uint res = unchecked(p + (Registers.StatusRegister.T ? 1u : 0u));
                            bool c2 = res < p;
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = c1 || c2;
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300F: // ADDV Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            uint res = unchecked(lhs + rhs);
                            bool s1 = (lhs & 0x80000000) != 0;
                            bool s2 = (rhs & 0x80000000) != 0;
                            bool s3 = (res & 0x80000000) != 0;
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (s1 == s2) && (s1 != s3);
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300D: // DMULS.L Rm, Rn
                        {
                            long res = (long)(int)Registers.GeneralPurposeRegisters[m] * (int)Registers.GeneralPurposeRegisters[n];
                            Registers.MacLow = unchecked((uint)res);
                            Registers.MacHigh = unchecked((uint)(res >> 32));
                            return true;
                        }
                    case 0x3005: // DMULU.L Rm, Rn
                        {
                            ulong res = (ulong)Registers.GeneralPurposeRegisters[m] * Registers.GeneralPurposeRegisters[n];
                            Registers.MacLow = (uint)res;
                            Registers.MacHigh = (uint)(res >> 32);
                            return true;
                        }
                    case 0x3004: // DIV1 Rm, Rn
                        {
                            uint divisor = Registers.GeneralPurposeRegisters[m];
                            uint dividend = Registers.GeneralPurposeRegisters[n];
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            bool oldQ = sr.Q;
                            sr.Q = (dividend & 0x80000000) != 0;
                            dividend <<= 1;
                            if (sr.T) dividend |= 1;
                            if (oldQ == sr.M)
                            {
                                uint prev = dividend;
                                dividend = unchecked(dividend - divisor);
                                sr.T = dividend > prev;
                            }
                            else
                            {
                                uint prev = dividend;
                                dividend = unchecked(dividend + divisor);
                                sr.T = dividend < prev;
                            }
                            sr.Q = (sr.Q ^ sr.M ^ sr.T);
                            sr.T = (sr.Q == sr.M);
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = dividend;
                            return true;
                        }
                }
                break;
            case 0x4000:
                if ((opcode & 0xF00F) == 0x400F) // MAC.W @Rm+, @Rn+
                {
                    short valM = (short)bus.ReadWord(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                    Registers.GeneralPurposeRegisters[m] += 2;
                    short valN = (short)bus.ReadWord(Registers.GeneralPurposeRegisters[n], Sega32XSh2AccessContext.Data);
                    Registers.GeneralPurposeRegisters[n] += 2;
                    int product = valM * valN;
                    if (Registers.StatusRegister.S)
                    {
                        long currentMacL = (int)Registers.MacLow;
                        long result = currentMacL + product;
                        if (result > int.MaxValue) { Registers.MacLow = int.MaxValue; Registers.MacHigh |= 1; }
                        else if (result < int.MinValue) { Registers.MacLow = unchecked((uint)int.MinValue); Registers.MacHigh |= 1; }
                        else Registers.MacLow = (uint)result;
                    }
                    else SetMac(GetMac() + product);
                    return true;
                }
                if ((opcode & 0xF00F) == 0x400C) // SHAD Rm, Rn
                {
                    Registers.GeneralPurposeRegisters[n] = DynamicArithmeticShift(
                        Registers.GeneralPurposeRegisters[n],
                        Registers.GeneralPurposeRegisters[m]);
                    return true;
                }
                if ((opcode & 0xF00F) == 0x400D) // SHLD Rm, Rn
                {
                    Registers.GeneralPurposeRegisters[n] = DynamicLogicalShift(
                        Registers.GeneralPurposeRegisters[n],
                        Registers.GeneralPurposeRegisters[m]);
                    return true;
                }

                switch (opcode & 0xF0FF)
                {
                    case 0x4015: // CMP/PL Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (int)Registers.GeneralPurposeRegisters[n] > 0; Registers.StatusRegister = sr; return true; }
                    case 0x4011: // CMP/PZ Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (int)Registers.GeneralPurposeRegisters[n] >= 0; Registers.StatusRegister = sr; return true; }
                    case 0x4010: // DT Rn
                        { Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 1); Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = Registers.GeneralPurposeRegisters[n] == 0; Registers.StatusRegister = sr; return true; }
                    case 0x4001: // SHLR Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[n] & 1) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] >>= 1; return true; }
                    case 0x4000: // SHLL Rn
                    case 0x4020: // SHAL Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[n] & 0x80000000) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] <<= 1; return true; }
                    case 0x4021: // SHAR Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[n] & 1) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] = (uint)((int)Registers.GeneralPurposeRegisters[n] >> 1); return true; }
                    case 0x4004: // ROTL Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (val & 0x80000000) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] = (val << 1) | (val >> 31); return true; }
                    case 0x4005: // ROTR Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (val & 1) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] = (val >> 1) | (val << 31); return true; }
                    case 0x4024: // ROTCL Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; bool cO = (val & 0x80000000) != 0; Registers.GeneralPurposeRegisters[n] = (val << 1) | (sr.T ? 1u : 0u); sr.T = cO; Registers.StatusRegister = sr; return true; }
                    case 0x4025: // ROTCR Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; bool cO = (val & 1) != 0; Registers.GeneralPurposeRegisters[n] = (val >> 1) | ((sr.T ? 1u : 0u) << 31); sr.T = cO; Registers.StatusRegister = sr; return true; }
                    case 0x4008: // SHLL2 Rn
                        Registers.GeneralPurposeRegisters[n] <<= 2; return true;
                    case 0x4009: // SHLR2 Rn
                        Registers.GeneralPurposeRegisters[n] >>= 2; return true;
                    case 0x4018: // SHLL8 Rn
                        Registers.GeneralPurposeRegisters[n] <<= 8; return true;
                    case 0x4019: // SHLR8 Rn
                        Registers.GeneralPurposeRegisters[n] >>= 8; return true;
                    case 0x4028: // SHLL16 Rn
                        Registers.GeneralPurposeRegisters[n] <<= 16; return true;
                    case 0x4029: // SHLR16 Rn
                        Registers.GeneralPurposeRegisters[n] >>= 16; return true;
                    case 0x400B: // JSR @Rn
                        Registers.ProcedureRegister = Registers.NextProgramCounter; Registers.NextProgramCounter = Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; bus.IncrementCycleCounter(1); CycleCounter += 1; return true;
                    case 0x402B: // JMP @Rn
                        Registers.NextProgramCounter = Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; bus.IncrementCycleCounter(1); CycleCounter += 1; return true;
                    case 0x400A: // LDS Rn, MACH
                        Registers.MacHigh = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x401A: // LDS Rn, MACL
                        Registers.MacLow = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x402A: // LDS Rn, PR
                        Registers.ProcedureRegister = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x400E: // LDC Rn, SR
                        Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(Registers.GeneralPurposeRegisters[n]); return true;
                    case 0x401E: // LDC Rn, GBR
                        Registers.GlobalBaseRegister = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x402E: // LDC Rn, VBR
                        Registers.VectorBaseRegister = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x4006: // LDS.L @Rn+, MACH
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.MacHigh = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4016: // LDS.L @Rn+, MACL
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.MacLow = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4026: // LDS.L @Rn+, PR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.ProcedureRegister = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4007: // LDC.L @Rn+, SR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(bus.ReadLongword(addr, Sega32XSh2AccessContext.Data)); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4017: // LDC.L @Rn+, GBR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.GlobalBaseRegister = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4027: // LDC.L @Rn+, VBR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.VectorBaseRegister = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4002: // STS.L MACH, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.MacHigh, Sega32XSh2AccessContext.Data); return true;
                    case 0x4012: // STS.L MACL, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.MacLow, Sega32XSh2AccessContext.Data); return true;
                    case 0x4022: // STS.L PR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.ProcedureRegister, Sega32XSh2AccessContext.Data); return true;
                    case 0x4003: // STC.L SR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data); return true;
                    case 0x4013: // STC.L GBR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GlobalBaseRegister, Sega32XSh2AccessContext.Data); return true;
                    case 0x4023: // STC.L VBR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.VectorBaseRegister, Sega32XSh2AccessContext.Data); return true;
                    case 0x401B: // TAS.B @Rn
                        { uint addr = Registers.GeneralPurposeRegisters[n]; byte val = bus.ReadExternalByteUncached(addr, Sega32XSh2AccessContext.Data); bus.WriteByte(addr, (byte)(val | 0x80), Sega32XSh2AccessContext.Data); Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = val == 0; Registers.StatusRegister = sr; bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                }
                break;
            case 0x0000:
                switch (opcode & 0xF00F)
                {
                    case 0x0004: // MOV.B Rm, @(R0, Rn)
                        bus.WriteByte(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n], (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x0005: // MOV.W Rm, @(R0, Rn)
                        bus.WriteWord(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n], (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x0006: // MOV.L Rm, @(R0, Rn)
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x000C: // MOV.B @(R0, Rm), Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data)); return true;
                    case 0x000D: // MOV.W @(R0, Rm), Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data)); return true;
                    case 0x000E: // MOV.L @(R0, Rm), Rn
                        Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x0007: // MUL.L Rm, Rn
                        Registers.MacLow = unchecked(Registers.GeneralPurposeRegisters[n] * Registers.GeneralPurposeRegisters[m]); return true;
                    case 0x000F: // MAC.L @Rm+, @Rn+
                        {
                            uint valM = bus.ReadLongword(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[m] += 4;
                            uint valN = bus.ReadLongword(Registers.GeneralPurposeRegisters[n], Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4;
                            long pS = unchecked(((long)(int)valM * (int)valN) + GetMac());
                            if (Registers.StatusRegister.S) { const long Min48 = -(1L << 47); const long Max48 = (1L << 47) - 1; pS = Math.Clamp(pS, Min48, Max48); }
                            SetMac(pS); return true;
                        }
                }
                switch (opcode & 0xF0FF)
                {
                    case 0x0002: // STC SR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.StatusRegister.ToUInt32(); return true;
                    case 0x0012: // STC GBR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GlobalBaseRegister; return true;
                    case 0x0022: // STC VBR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.VectorBaseRegister; return true;
                    case 0x000A: // STS MACH, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.MacHigh; return true;
                    case 0x001A: // STS MACL, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.MacLow; return true;
                    case 0x002A: // STS PR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.ProcedureRegister; return true;
                    case 0x00C3: // MOVCA.L R0, @Rn
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x0003: // BSRF Rn
                        Registers.ProcedureRegister = Registers.NextProgramCounter; Registers.NextProgramCounter = Registers.NextProgramCounter + Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; return true;
                    case 0x0023: // BRAF Rn
                        Registers.NextProgramCounter = Registers.NextProgramCounter + Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; return true;
                    case 0x0008: // CLRT
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = false; Registers.StatusRegister = sr; return true; }
                    case 0x0018: // SETT
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = true; Registers.StatusRegister = sr; return true; }
                    case 0x001B: // SLEEP
                        return true;
                    case 0x0028: // CLRMAC
                        Registers.MacLow = 0; Registers.MacHigh = 0; return true;
                    case 0x0019: // DIV0U
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.M = false; sr.Q = false; sr.T = false; Registers.StatusRegister = sr; return true; }
                    case 0x0029: // MOVT Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.StatusRegister.T ? 1u : 0u; return true;
                }
                if (opcode == 0x000B) { Registers.NextProgramCounter = Registers.ProcedureRegister; Registers.NextInstructionInDelaySlot = true; return true; }
                if (opcode == 0x002B) {
                    uint sp = Registers.StackPointer;
                    Registers.NextProgramCounter = bus.ReadLongword(sp, Sega32XSh2AccessContext.Data);
                    Registers.StackPointer = sp + 4;
                    Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(bus.ReadLongword(Registers.StackPointer, Sega32XSh2AccessContext.Data));
                    Registers.StackPointer += 4;
                    Registers.NextInstructionInDelaySlot = true;
                    bus.IncrementCycleCounter(3); CycleCounter += 3;
                    return true;
                }
                if (opcode == 0x0009) return true;
                break;
            case 0x8000:
                switch ((opcode >> 8) & 0xF)
                {
                    case 0x8: { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = Registers.GeneralPurposeRegisters[0] == unchecked((uint)(sbyte)(opcode & 0xFF)); Registers.StatusRegister = sr; return true; }
                    case 0x9: if (Registers.StatusRegister.T) { Registers.ProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextProgramCounter = Registers.ProgramCounter + 2; } return true;
                    case 0xB: if (!Registers.StatusRegister.T) { Registers.ProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextProgramCounter = Registers.ProgramCounter + 2; } return true;
                    case 0xD: if (Registers.StatusRegister.T) { Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextInstructionInDelaySlot = true; } return true;
                    case 0xF: if (!Registers.StatusRegister.T) { Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextInstructionInDelaySlot = true; } return true;
                    case 0x0: bus.WriteByte(Registers.GeneralPurposeRegisters[m] + (uint)(opcode & 0xF), (byte)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x1: bus.WriteWord(Registers.GeneralPurposeRegisters[m] + (uint)((opcode & 0xF) << 1), (ushort)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x4: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[m] + (uint)(opcode & 0xF), Sega32XSh2AccessContext.Data)); return true;
                    case 0x5: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[m] + (uint)((opcode & 0xF) << 1), Sega32XSh2AccessContext.Data)); return true;
                }
                break;
            case 0xC000:
                switch ((opcode >> 8) & 0xF)
                {
                    case 0x0: bus.WriteByte(Registers.GlobalBaseRegister + (uint)(opcode & 0xFF), (byte)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x1: bus.WriteWord(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1), (ushort)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x2: bus.WriteLongword(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2), Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x4: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GlobalBaseRegister + (uint)(opcode & 0xFF), Sega32XSh2AccessContext.Data)); return true;
                    case 0x5: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(short)bus.ReadWord(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1), Sega32XSh2AccessContext.Data)); return true;
                    case 0x6: Registers.GeneralPurposeRegisters[0] = bus.ReadLongword(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2), Sega32XSh2AccessContext.Data); return true;
                    case 0x8: { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[0] & (uint)(opcode & 0xFF)) == 0; Registers.StatusRegister = sr; return true; }
                    case 0x9: Registers.GeneralPurposeRegisters[0] &= (uint)(opcode & 0xFF); return true;
                    case 0xA: Registers.GeneralPurposeRegisters[0] ^= (uint)(opcode & 0xFF); return true;
                    case 0xB: Registers.GeneralPurposeRegisters[0] |= (uint)(opcode & 0xFF); return true;
                    case 0xC: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = bus.ReadByte(address, Sega32XSh2AccessContext.Data); Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (val & (uint)(opcode & 0xFF)) == 0; Registers.StatusRegister = sr; bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0xD: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = (byte)(bus.ReadByte(address, Sega32XSh2AccessContext.Data) & (opcode & 0xFF)); bus.WriteByte(address, val, Sega32XSh2AccessContext.Data); bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0xE: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = (byte)(bus.ReadByte(address, Sega32XSh2AccessContext.Data) ^ (opcode & 0xFF)); bus.WriteByte(address, val, Sega32XSh2AccessContext.Data); bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0xF: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = (byte)(bus.ReadByte(address, Sega32XSh2AccessContext.Data) | (opcode & 0xFF)); bus.WriteByte(address, val, Sega32XSh2AccessContext.Data); bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0x7: Registers.GeneralPurposeRegisters[0] = (Registers.NextProgramCounter & ~3u) + (uint)((opcode & 0xFF) << 2); return true;
                    case 0x3:
                        {
                            uint sp = unchecked(Registers.StackPointer - 4);
                            bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);
                            sp -= 4; bus.WriteLongword(sp, Registers.NextProgramCounter, Sega32XSh2AccessContext.Data);
                            Registers.StackPointer = sp;
                            uint vA = Registers.VectorBaseRegister + (uint)((opcode & 0xFF) << 2);
                            Registers.ProgramCounter = bus.ReadLongword(vA, Sega32XSh2AccessContext.InterruptVector);
                            Registers.NextProgramCounter = Registers.ProgramCounter + 2;
                            bus.IncrementCycleCounter(6); CycleCounter += 6;
                            return true;
                        }
                }
                break;
        }

        return false;
    }
}

internal sealed class Sega32XSh2Registers
{
    public uint[] GeneralPurposeRegisters { get; } = new uint[16];
    public Sega32XSh2StatusRegister StatusRegister { get; set; } = new();
    public uint GlobalBaseRegister { get; set; }
    public uint VectorBaseRegister { get; set; }
    public uint MacLow { get; set; }
    public uint MacHigh { get; set; }
    public uint ProcedureRegister { get; set; }
    public uint ProgramCounter { get; set; }
    public uint NextProgramCounter { get; set; }
    public bool NextInstructionInDelaySlot { get; set; }

    public uint StackPointer
    {
        get => GeneralPurposeRegisters[15];
        set => GeneralPurposeRegisters[15] = value;
    }
}

internal struct Sega32XSh2StatusRegister
{
    public byte InterruptMask { get; set; }
    public bool T { get; set; }
    public bool S { get; set; }
    public bool Q { get; set; }
    public bool M { get; set; }

    public static Sega32XSh2StatusRegister FromUInt32(uint value)
    {
        return new Sega32XSh2StatusRegister
        {
            InterruptMask = (byte)((value >> 4) & 0xF),
            T = (value & 0x0001) != 0,
            S = (value & 0x0002) != 0,
            Q = (value & 0x0100) != 0,
            M = (value & 0x0200) != 0,
        };
    }

    public uint ToUInt32()
    {
        return ((M ? 1u : 0u) << 9)
            | ((Q ? 1u : 0u) << 8)
            | ((uint)InterruptMask << 4)
            | ((S ? 1u : 0u) << 1)
            | (T ? 1u : 0u);
    }
}

internal enum Sega32XSh2AccessContext
{
    Fetch,
    Data,
    InterruptVector,
}
