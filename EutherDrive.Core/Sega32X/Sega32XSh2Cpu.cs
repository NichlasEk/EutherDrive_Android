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
    private const int MinDecodedBlockOps = 4;
    private const int MaxDecodedBlockOps = 32;
    private const uint DecodedBlockPageMask = 0xFFFF_F000;
    public string Name { get; }
    public Sega32XSh2Registers Registers { get; } = new();
    public uint CurrentInstructionPc { get; private set; }
    public ulong CycleCounter { get; private set; }
    public bool ResetPending { get; set; } = true;
    public static bool SchedulerWaitLoopFastForwardEnabled => SchedulerWaitLoopFastForward;
    private int _unsupportedLogCount;
    private bool _unsupportedLogSuppressed;
    [NonSerialized] private readonly Dictionary<uint, ulong> _pcSampleTicks = new();
    [NonSerialized] private readonly HashSet<ulong> _unsupportedOpcodeSites = new();
    [NonSerialized] private readonly Dictionary<uint, byte> _blockJitProbeCounts = new();
    [NonSerialized] private readonly Dictionary<uint, CompiledWaitBlock?> _blockJitCache = new();
    [NonSerialized] private readonly Dictionary<uint, DecodedBlock?> _decodedBlockCache = new();
    private ulong _decodedBlocksCompiled;
    private ulong _decodedBlockHits;
    private ulong _decodedBlockMisses;
    private ulong _decodedBlockInvalidations;
    private ulong _decodedBlockFallbackInstructions;
    private ulong _decodedBlockCompiledOps;
    private ulong _memLoopFusionHits;
    private ulong _memLoopFusionIterations;
    [NonSerialized] private bool _turboStraightLineEnabled = TurboStraightLineDefaultEnabled;

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
    private static readonly bool SchedulerWaitLoopFastForward =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCHED_WAIT_FF"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool SchedulerPollingWaitLoopFastForward =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCHED_POLL_WAIT_FF"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool DisableBlockJit =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_DISABLE_BLOCK_JIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool FastCoreEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_FAST_CORE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TurboStraightLineDefaultEnabled =
        ParseBoolEnvDefault("EUTHERDRIVE_S32X_TURBO_STRAIGHT_LINE", false);
    private const ulong TurboStraightLineMaxOps = 48;
    private static readonly DecodedOp[] FastOpcodeTable = BuildFastOpcodeTable();
    private static readonly bool MemLoopFusionEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_MEM_LOOP_FUSION"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceBlockJit =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BLOCK_JIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool BlockInterpreterEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_32X_BLOCK_INTERP"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool AggressiveBlockInterpreter =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_32X_BLOCK_INTERP_AGGRESSIVE"),
            "1",
            StringComparison.Ordinal);
    private static readonly int BlockInterpreterCompareInstructions = ParseBlockInterpreterCompareInstructions();
    private int _traceInstructionLogs;
    private int _blockJitTraceLogs;
    private int _blockInterpreterCompareLogs;

    public Sega32XSh2Cpu(string name)
    {
        Name = name;
        RequestReset();
    }

    public bool TurboStraightLineEnabled
    {
        get => _turboStraightLineEnabled;
        set => _turboStraightLineEnabled = value;
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
        _decodedBlockCache.Clear();
        _unsupportedLogCount = 0;
        _unsupportedLogSuppressed = false;
        _blockJitTraceLogs = 0;
        _blockInterpreterCompareLogs = 0;
        _decodedBlocksCompiled = 0;
        _decodedBlockHits = 0;
        _decodedBlockMisses = 0;
        _decodedBlockInvalidations = 0;
        _decodedBlockFallbackInstructions = 0;
        _decodedBlockCompiledOps = 0;
        _memLoopFusionHits = 0;
        _memLoopFusionIterations = 0;
    }

    public string? BuildAndResetPerfPcSummary(int maxEntries = 4)
    {
        if ((!PerfPcHistogramEnabled || _pcSampleTicks.Count == 0) && !BlockInterpreterEnabled && _memLoopFusionHits == 0)
            return null;

        KeyValuePair<uint, ulong>[] top = PerfPcHistogramEnabled
            ? _pcSampleTicks
                .OrderByDescending(static pair => pair.Value)
                .Take(maxEntries)
                .ToArray()
            : Array.Empty<KeyValuePair<uint, ulong>>();
        ulong total = 0;
        foreach (ulong ticks in _pcSampleTicks.Values)
            total += ticks;

        _pcSampleTicks.Clear();
        if ((top.Length == 0 || total == 0) && !BlockInterpreterEnabled && _memLoopFusionHits == 0)
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

        if (BlockInterpreterEnabled)
        {
            double avgOps = _decodedBlocksCompiled == 0
                ? 0
                : _decodedBlockCompiledOps / (double)_decodedBlocksCompiled;
            sb.Append(" block_interp=");
            sb.Append("compiled=");
            sb.Append(_decodedBlocksCompiled);
            sb.Append(" hits=");
            sb.Append(_decodedBlockHits);
            sb.Append(" misses=");
            sb.Append(_decodedBlockMisses);
            sb.Append(" invalidations=");
            sb.Append(_decodedBlockInvalidations);
            sb.Append(" fallbacks=");
            sb.Append(_decodedBlockFallbackInstructions);
            sb.Append(" avg_ops=");
            sb.Append(avgOps.ToString("0.0"));
        }

        if (_memLoopFusionHits != 0)
        {
            double avgIterations = _memLoopFusionIterations / (double)_memLoopFusionHits;
            sb.Append(" mem_loop_fusion=");
            sb.Append("hits=");
            sb.Append(_memLoopFusionHits);
            sb.Append(" iterations=");
            sb.Append(_memLoopFusionIterations);
            sb.Append(" avg_iter=");
            sb.Append(avgIterations.ToString("0.0"));
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
            ushort opcode = bus.ReadOpcodeFast(pc);

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

            if (MemLoopFusionEnabled &&
                TryExecuteMemoryTransferLoop(bus, remainingInstructions, pc, opcode, out consumedInstructions))
            {
                remainingInstructions = consumedInstructions >= remainingInstructions ? 0 : remainingInstructions - consumedInstructions;
                if (bus.ShouldStopExecution)
                    return;
                continue;
            }

            if (_turboStraightLineEnabled &&
                TryExecuteTurboStraightLine(bus, remainingInstructions, pc, opcode, out consumedInstructions))
            {
                remainingInstructions = consumedInstructions >= remainingInstructions ? 0 : remainingInstructions - consumedInstructions;
                if (bus.ShouldStopExecution)
                    return;
                continue;
            }

            if (BlockInterpreterEnabled &&
                TryExecuteDecodedBlock(bus, remainingInstructions, pc, opcode, out consumedInstructions))
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

    public bool IsAtBatchableWaitLoop(Sega32XSh2Bus bus)
    {
        if (Registers.NextInstructionInDelaySlot ||
            TraceInstructionStart.HasValue ||
            TraceInstructionEnd.HasValue ||
            HasPendingInterrupt(bus))
        {
            return false;
        }

        uint pc = Registers.ProgramCounter;
        if (!bus.TryPeekInstructionWord(pc, out ushort firstOpcode))
            return false;

        if ((firstOpcode & 0xF000) == 0xA000 &&
            bus.TryPeekInstructionWord(pc + 2, out ushort braDelayOpcode) &&
            braDelayOpcode == 0x0009)
        {
            int displacement = ((short)(firstOpcode << 4)) >> 4;
            uint target = unchecked(pc + 4u + (uint)(displacement << 1));
            if (target == pc)
                return true;
        }

        if ((firstOpcode & 0xF00F) == 0x2008)
        {
            int n = (firstOpcode >> 8) & 0xF;
            int m = (firstOpcode >> 4) & 0xF;
            if (n == m &&
                bus.TryPeekInstructionWord(pc + 2, out ushort branchOpcode) &&
                bus.TryPeekInstructionWord(pc + 4, out ushort bfsDelayOpcode) &&
                (branchOpcode & 0xFF00) == 0x8F00 &&
                (bfsDelayOpcode & 0xF000) == 0x7000 &&
                ((bfsDelayOpcode >> 8) & 0xF) == n &&
                unchecked((sbyte)(bfsDelayOpcode & 0xFF)) == -1)
            {
                uint branchTarget = unchecked(pc + 6u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
                if (branchTarget == pc)
                    return true;
            }
        }

        return TryMatchDelayLoop(bus, pc, firstOpcode, out _, out _, out _);
    }

    public bool TryFastForwardSchedulerWaitLoop(Sega32XSh2Bus bus, ulong targetCycles)
    {
        if (!SchedulerWaitLoopFastForward ||
            DisablePollingLoopAcceleration ||
            ResetPending ||
            Registers.NextInstructionInDelaySlot ||
            TraceInstructionStart.HasValue ||
            TraceInstructionEnd.HasValue ||
            HasPendingInterrupt(bus))
        {
            return false;
        }

        ulong remainingCycles = targetCycles > bus.SchedulerCycleCounter
            ? targetCycles - bus.SchedulerCycleCounter
            : 0;
        if (remainingCycles < 2)
            return false;

        uint pc = Registers.ProgramCounter;
        if (!bus.TryPeekInstructionWord(pc, out ushort firstOpcode))
            return false;

        if (TryFastForwardSchedulerIdleBranch(bus, remainingCycles, pc, firstOpcode))
            return true;

        return SchedulerPollingWaitLoopFastForward &&
            TryFastForwardSchedulerPollingLoop(bus, remainingCycles, pc, firstOpcode);
    }

    public bool TryFastForwardKnownSchedulerIdleLoop(Sega32XSh2Bus bus, ulong targetCycles)
    {
        if (DisablePollingLoopAcceleration ||
            ResetPending ||
            Registers.NextInstructionInDelaySlot ||
            TraceInstructionStart.HasValue ||
            TraceInstructionEnd.HasValue ||
            HasPendingInterrupt(bus))
        {
            return false;
        }

        ulong remainingCycles = targetCycles > bus.SchedulerCycleCounter
            ? targetCycles - bus.SchedulerCycleCounter
            : 0;
        if (remainingCycles < 2)
            return false;

        uint pc = Registers.ProgramCounter;
        if (!bus.TryPeekInstructionWord(pc, out ushort firstOpcode))
            return false;

        return TryFastForwardSchedulerIdleBranch(bus, remainingCycles, pc, firstOpcode);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryFastForwardSchedulerIdleBranch(
        Sega32XSh2Bus bus,
        ulong remainingCycles,
        uint loopStartPc,
        ushort firstOpcode)
    {
        if (remainingCycles < 2 || (firstOpcode & 0xF000) != 0xA000)
            return false;
        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort delayOpcode) || delayOpcode != 0x0009)
            return false;

        int displacement = ((short)(firstOpcode << 4)) >> 4;
        uint target = unchecked(loopStartPc + 4u + (uint)(displacement << 1));
        if (target != loopStartPc)
            return false;

        Registers.ProgramCounter = loopStartPc;
        Registers.NextProgramCounter = loopStartPc + 2;
        Registers.NextInstructionInDelaySlot = false;

        bus.IncrementCycleCounter(remainingCycles);
        CycleCounter += remainingCycles;
        AccumulatePcSample(loopStartPc, remainingCycles);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryFastForwardSchedulerPollingLoop(
        Sega32XSh2Bus bus,
        ulong remainingCycles,
        uint loopStartPc,
        ushort firstOpcode)
    {
        if (remainingCycles < 3)
            return false;

        if (!TryDecodePollingLoad(firstOpcode, out int loadRegister, out uint address, out PollingLoadSize loadSize))
            return TryFastForwardSchedulerPcRelativePollingLoop(bus, remainingCycles, loopStartPc, firstOpcode);
        if (!IsFastPollingSource(bus, address, loadSize))
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort testOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 4, out ushort branchOpcode))
        {
            return false;
        }

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
                return false;
        }

        uint branchPc = loopStartPc + 4;
        uint branchTarget = unchecked(branchPc + 4u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc || !CanEvaluatePollingTest(testOpcode, loadRegister))
            return false;

        Registers.GeneralPurposeRegisters[loadRegister] = ReadPollingLoadValue(bus, address, loadSize);
        if (!TryEvaluatePollingTest(testOpcode, loadRegister, out bool testResult))
            return false;

        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = testResult;
        Registers.StatusRegister = sr;

        bool branchTaken = branchOnTrue ? testResult : !testResult;
        ulong cyclesToConsume = branchTaken ? remainingCycles : 3;
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
        return true;
    }

    private bool TryFastForwardSchedulerPcRelativePollingLoop(
        Sega32XSh2Bus bus,
        ulong remainingCycles,
        uint loopStartPc,
        ushort firstOpcode)
    {
        const ulong InstructionsPerIteration = 4;
        if (remainingCycles < InstructionsPerIteration)
            return false;
        if (!TryDecodePcRelativePollingLoop(bus, loopStartPc, firstOpcode, out int pointerRegister, out uint address, out int loadRegister, out PollingLoadSize loadSize, out ushort testOpcode, out ushort branchOpcode))
            return false;

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
                return false;
        }

        uint branchPc = loopStartPc + 6;
        uint branchTarget = unchecked(branchPc + 4u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return false;

        Registers.GeneralPurposeRegisters[pointerRegister] = address;
        Registers.GeneralPurposeRegisters[loadRegister] = ReadPollingLoadValue(bus, address, loadSize);
        if (!TryEvaluatePollingTest(testOpcode, loadRegister, out bool testResult))
            return false;

        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = testResult;
        Registers.StatusRegister = sr;

        bool branchTaken = branchOnTrue ? testResult : !testResult;
        ulong cyclesToConsume = branchTaken ? remainingCycles : InstructionsPerIteration;
        if (cyclesToConsume < InstructionsPerIteration)
            return false;

        if (branchTaken)
        {
            Registers.ProgramCounter = loopStartPc;
            Registers.NextProgramCounter = loopStartPc + 2;
        }
        else
        {
            uint exitPc = loopStartPc + 8;
            Registers.ProgramCounter = exitPc;
            Registers.NextProgramCounter = exitPc + 2;
        }
        Registers.NextInstructionInDelaySlot = false;

        bus.IncrementCycleCounter(cyclesToConsume);
        CycleCounter += cyclesToConsume;
        AccumulatePcSample(loopStartPc, cyclesToConsume);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteDecodedBlock(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint pc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if (remainingInstructions < 2 ||
            Registers.NextInstructionInDelaySlot ||
            TraceInstructionStart.HasValue ||
            TraceInstructionEnd.HasValue ||
            HasPendingInterrupt(bus))
        {
            return false;
        }

        if (IsSdramExecutableAddress(pc))
            return false;

        if (!CanStartDecodedBlock(firstOpcode))
            return false;

        bool blockCompiledNow = false;
        if (_decodedBlockCache.TryGetValue(pc, out DecodedBlock? block))
        {
            if (block == null)
            {
                _decodedBlockMisses++;
                return false;
            }

            if (block.ExecutableVersion != bus.GetExecutableVersion(pc))
            {
                _decodedBlockCache.Remove(pc);
                _decodedBlockInvalidations++;
                block = null;
            }
        }

        if (block == null)
        {
            _decodedBlockMisses++;
            block = TryCompileDecodedBlock(bus, pc, firstOpcode);
            _decodedBlockCache[pc] = block;
            if (block == null)
                return false;
            blockCompiledNow = true;
        }
        else
        {
            _decodedBlockHits++;
        }

        int opLimit = (int)Math.Min((ulong)block.Operations.Length, remainingInstructions);
        for (int i = 0; i < opLimit; i++)
        {
            if (HasPendingInterrupt(bus))
                return consumedInstructions != 0;

            DecodedOp op = block.Operations[i];
            ushort fetched = i == 0
                ? firstOpcode
                : blockCompiledNow || AggressiveBlockInterpreter ? op.Opcode : bus.ReadOpcode(op.Pc);
            if (fetched != op.Opcode || Registers.ProgramCounter != op.Pc)
            {
                _decodedBlockInvalidations++;
                _decodedBlockFallbackInstructions++;
                _decodedBlockCache.Remove(pc);
                if (consumedInstructions != 0)
                    return true;

                ExecuteFetchedInstruction(bus, op.Pc, fetched);
                consumedInstructions = 1;
                return true;
            }

            if (IsDecodedCompareSafe(op.Kind) &&
                BlockInterpreterCompareInstructions > 0 &&
                _blockInterpreterCompareLogs < BlockInterpreterCompareInstructions)
            {
                CompareDecodedOpWithInterpreter(op);
            }

            ExecuteDecodedFetchedInstruction(bus, op);
            consumedInstructions++;
            if (bus.ShouldStopExecution || Registers.NextInstructionInDelaySlot)
                return true;
        }

        return consumedInstructions != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteMemoryTransferLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if (TraceInstructionStart.HasValue || TraceInstructionEnd.HasValue ||
            Registers.NextInstructionInDelaySlot ||
            HasPendingInterrupt(bus) ||
            bus.CycleLimit == ulong.MaxValue)
        {
            return false;
        }

        if (TryExecuteCopyPostIncrementLoop(
                bus,
                remainingInstructions,
                loopStartPc,
                firstOpcode,
                isLongword: true,
                out consumedInstructions))
        {
            return true;
        }

        if (TryExecuteCopyPostIncrementLoop(
                bus,
                remainingInstructions,
                loopStartPc,
                firstOpcode,
                isLongword: false,
                out consumedInstructions))
        {
            return true;
        }

        if (TryExecuteFillIncrementLoop(
                bus,
                remainingInstructions,
                loopStartPc,
                firstOpcode,
                isLongword: true,
                out consumedInstructions))
        {
            return true;
        }

        return TryExecuteFillIncrementLoop(
            bus,
            remainingInstructions,
            loopStartPc,
            firstOpcode,
            isLongword: false,
            out consumedInstructions);
    }

    private bool TryExecuteCopyPostIncrementLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        bool isLongword,
        out ulong consumedInstructions)
    {
        const int InstructionsPerIteration = 5;
        consumedInstructions = 0;

        if (remainingInstructions < InstructionsPerIteration)
            return false;

        ushort loadMask = isLongword ? (ushort)0x6006 : (ushort)0x6005; // MOV.L/W @Rm+, Rn
        ushort storeMask = isLongword ? (ushort)0x2002 : (ushort)0x2001; // MOV.L/W Rm, @Rn
        int transferSize = isLongword ? 4 : 2;
        if ((firstOpcode & 0xF00F) != loadMask)
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 8, out ushort branchOpcode) ||
            (branchOpcode & 0xFF00) != 0x8B00)
        {
            return false;
        }

        uint branchTarget = unchecked(loopStartPc + 12u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort storeOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 4, out ushort addOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 6, out ushort dtOpcode))
        {
            return false;
        }

        if ((storeOpcode & 0xF00F) != storeMask ||
            (addOpcode & 0xF000) != 0x7000 ||
            (dtOpcode & 0xF0FF) != 0x4010)
        {
            return false;
        }

        int tempRegister = (firstOpcode >> 8) & 0xF;
        int sourceRegister = (firstOpcode >> 4) & 0xF;
        int destinationRegister = (storeOpcode >> 8) & 0xF;
        int storedRegister = (storeOpcode >> 4) & 0xF;
        int addRegister = (addOpcode >> 8) & 0xF;
        int counterRegister = (dtOpcode >> 8) & 0xF;

        if (storedRegister != tempRegister ||
            addRegister != destinationRegister ||
            unchecked((sbyte)(addOpcode & 0xFF)) != transferSize)
        {
            return false;
        }

        if (tempRegister == sourceRegister ||
            tempRegister == destinationRegister ||
            tempRegister == counterRegister ||
            sourceRegister == destinationRegister ||
            sourceRegister == counterRegister ||
            destinationRegister == counterRegister)
        {
            return false;
        }

        if (!TryGetMemoryLoopIterations(
                bus,
                remainingInstructions,
                InstructionsPerIteration,
                counterRegister,
                out ulong iterations))
        {
            return false;
        }

        uint source = Registers.GeneralPurposeRegisters[sourceRegister];
        uint destination = Registers.GeneralPurposeRegisters[destinationRegister];
        uint value = Registers.GeneralPurposeRegisters[tempRegister];

        CurrentInstructionPc = loopStartPc;
        try
        {
            uint lastSource = source + (uint)((iterations - 1) * (ulong)transferSize);
            if (bus.TryPeekSdramValueNoTiming(lastSource, isLongword, out uint lastValue) &&
                bus.TryBulkCopySdram(source, destination, isLongword, iterations))
            {
                value = lastValue;
            }
            else
            {
                for (ulong i = 0; i < iterations; i++)
                {
                    if (isLongword)
                    {
                        value = bus.ReadLongword(source, Sega32XSh2AccessContext.Data);
                        bus.WriteLongword(destination, value, Sega32XSh2AccessContext.Data);
                    }
                    else
                    {
                        value = unchecked((uint)(short)bus.ReadWord(source, Sega32XSh2AccessContext.Data));
                        bus.WriteWord(destination, (ushort)value, Sega32XSh2AccessContext.Data);
                    }

                    source += (uint)transferSize;
                    destination += (uint)transferSize;
                }
            }
        }
        finally
        {
            CurrentInstructionPc = 0;
        }

        Registers.GeneralPurposeRegisters[tempRegister] = value;
        Registers.GeneralPurposeRegisters[sourceRegister] += (uint)(iterations * (ulong)transferSize);
        Registers.GeneralPurposeRegisters[destinationRegister] += (uint)(iterations * (ulong)transferSize);
        return FinishMemoryLoopIterations(bus, loopStartPc, InstructionsPerIteration, counterRegister, iterations, out consumedInstructions);
    }

    private bool TryExecuteFillIncrementLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        bool isLongword,
        out ulong consumedInstructions)
    {
        const int InstructionsPerIteration = 4;
        consumedInstructions = 0;

        if (remainingInstructions < InstructionsPerIteration)
            return false;

        ushort storeMask = isLongword ? (ushort)0x2002 : (ushort)0x2001; // MOV.L/W Rm, @Rn
        int transferSize = isLongword ? 4 : 2;
        if ((firstOpcode & 0xF00F) != storeMask)
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 6, out ushort branchOpcode) ||
            (branchOpcode & 0xFF00) != 0x8B00)
        {
            return false;
        }

        uint branchTarget = unchecked(loopStartPc + 10u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort addOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 4, out ushort dtOpcode))
        {
            return false;
        }

        if ((addOpcode & 0xF000) != 0x7000 ||
            (dtOpcode & 0xF0FF) != 0x4010)
        {
            return false;
        }

        int destinationRegister = (firstOpcode >> 8) & 0xF;
        int valueRegister = (firstOpcode >> 4) & 0xF;
        int addRegister = (addOpcode >> 8) & 0xF;
        int counterRegister = (dtOpcode >> 8) & 0xF;

        if (addRegister != destinationRegister ||
            unchecked((sbyte)(addOpcode & 0xFF)) != transferSize)
        {
            return false;
        }

        if (valueRegister == destinationRegister ||
            valueRegister == counterRegister ||
            destinationRegister == counterRegister)
        {
            return false;
        }

        if (!TryGetMemoryLoopIterations(
                bus,
                remainingInstructions,
                InstructionsPerIteration,
                counterRegister,
                out ulong iterations))
        {
            return false;
        }

        uint destination = Registers.GeneralPurposeRegisters[destinationRegister];
        uint value = Registers.GeneralPurposeRegisters[valueRegister];

        CurrentInstructionPc = loopStartPc;
        try
        {
            if (!bus.TryBulkFillSdram(destination, value, isLongword, iterations))
            {
                for (ulong i = 0; i < iterations; i++)
                {
                    if (isLongword)
                        bus.WriteLongword(destination, value, Sega32XSh2AccessContext.Data);
                    else
                        bus.WriteWord(destination, (ushort)value, Sega32XSh2AccessContext.Data);

                    destination += (uint)transferSize;
                }
            }
        }
        finally
        {
            CurrentInstructionPc = 0;
        }

        Registers.GeneralPurposeRegisters[destinationRegister] += (uint)(iterations * (ulong)transferSize);
        return FinishMemoryLoopIterations(bus, loopStartPc, InstructionsPerIteration, counterRegister, iterations, out consumedInstructions);
    }

    private bool TryGetMemoryLoopIterations(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        int instructionsPerIteration,
        int counterRegister,
        out ulong iterations)
    {
        iterations = 0;

        uint counter = Registers.GeneralPurposeRegisters[counterRegister];
        if (counter == 0)
            return false;

        ulong remainingCycles = bus.CycleLimit > bus.SchedulerCycleCounter
            ? bus.CycleLimit - bus.SchedulerCycleCounter
            : 0;
        ulong maxIterations = Math.Min(
            remainingInstructions / (ulong)instructionsPerIteration,
            remainingCycles / (ulong)instructionsPerIteration);
        maxIterations = Math.Min(maxIterations, counter);
        if (maxIterations == 0)
            return false;

        iterations = maxIterations;
        return true;
    }

    private bool FinishMemoryLoopIterations(
        Sega32XSh2Bus bus,
        uint loopStartPc,
        int instructionsPerIteration,
        int counterRegister,
        ulong iterations,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        uint counter = Registers.GeneralPurposeRegisters[counterRegister];
        Registers.GeneralPurposeRegisters[counterRegister] = unchecked(counter - (uint)iterations);
        bool loopFinished = Registers.GeneralPurposeRegisters[counterRegister] == 0;
        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = loopFinished;
        Registers.StatusRegister = sr;

        ulong instructionCycles = iterations * (ulong)instructionsPerIteration;
        bus.IncrementCycleCounter(instructionCycles);
        CycleCounter += instructionCycles;
        AccumulatePcSample(loopStartPc, instructionCycles);

        if (loopFinished)
        {
            uint exitPc = loopStartPc + (uint)(instructionsPerIteration << 1);
            Registers.ProgramCounter = exitPc;
            Registers.NextProgramCounter = exitPc + 2;
        }
        else
        {
            Registers.ProgramCounter = loopStartPc;
            Registers.NextProgramCounter = loopStartPc + 2;
        }

        Registers.NextInstructionInDelaySlot = false;
        _memLoopFusionHits++;
        _memLoopFusionIterations += iterations;
        consumedInstructions = instructionCycles;
        return true;
    }

    private DecodedBlock? TryCompileDecodedBlock(Sega32XSh2Bus bus, uint startPc, ushort firstOpcode)
    {
        DecodedOp[] ops = new DecodedOp[MaxDecodedBlockOps];
        int count = 0;
        uint pc = startPc;
        uint page = startPc & DecodedBlockPageMask;

        while (count < ops.Length)
        {
            if ((pc & DecodedBlockPageMask) != page)
                break;

            ushort opcode;
            if (count == 0)
            {
                opcode = firstOpcode;
            }
            else
            {
                if (!bus.TryPeekInstructionWord(pc, out opcode) ||
                    !TryDecodeBlockOp(pc, opcode, out _))
                {
                    break;
                }
                opcode = bus.ReadOpcodeFast(pc);
            }
            if (!TryDecodeBlockOp(pc, opcode, out DecodedOp op))
                break;

            ops[count++] = op;
            if (IsDecodedBlockTerminator(opcode))
                break;

            pc += 2;
        }

        if (count < MinDecodedBlockOps)
            return null;

        Array.Resize(ref ops, count);
        _decodedBlocksCompiled++;
        _decodedBlockCompiledOps += (ulong)count;
        return new DecodedBlock(startPc, bus.GetExecutableVersion(startPc), ops);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanStartDecodedBlock(ushort opcode)
    {
        return (opcode & 0xF000) switch
        {
            0x1000 or 0x5000 or 0x9000 or 0xD000 => AggressiveBlockInterpreter && TryDecodeBlockOp(0, opcode, out _),
            0xE000 or 0x7000 or 0x6000 or 0x2000 or 0x3000 or 0x4000 or 0x0000 or 0x8000 or 0xC000
                => TryDecodeBlockOp(0, opcode, out _),
            _ => false,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSdramExecutableAddress(uint pc)
    {
        uint masked = pc & 0x1FFF_FFFF;
        return masked >= 0x0600_0000 && masked < 0x0604_0000;
    }

    private bool HasPendingInterrupt(Sega32XSh2Bus bus)
    {
        if (Registers.NextInstructionInDelaySlot)
            return false;

        byte interruptMask = Registers.StatusRegister.InterruptMask;
        return bus.InterruptLevel > interruptMask || bus.InternalInterruptLevel > interruptMask;
    }

    private void ExecuteDecodedFetchedInstruction(Sega32XSh2Bus bus, DecodedOp op)
    {
        MaybeTraceInstruction(op.Pc, op.Opcode);

        ApplyFetchedInstructionPrelude();

        CurrentInstructionPc = op.Pc;
        try
        {
            ExecuteDecodedOp(op, bus);
            bus.IncrementCycleCounter(1);
            CycleCounter += 1;
            AccumulatePcSample(op.Pc, 1);
        }
        finally
        {
            CurrentInstructionPc = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteTurboStraightLine(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint firstPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        consumedInstructions = 0;

        if (remainingInstructions < 2 ||
            Registers.NextInstructionInDelaySlot ||
            TraceInstructionStart.HasValue ||
            TraceInstructionEnd.HasValue ||
            TraceBootLoop ||
            HasPendingInterrupt(bus))
        {
            return false;
        }

        ulong opLimit = Math.Min(remainingInstructions, TurboStraightLineMaxOps);
        uint pc = firstPc;
        ushort opcode = firstOpcode;

        for (ulong i = 0; i < opLimit; i++)
        {
            DecodedOp template = FastOpcodeTable[opcode];
            if (template.Kind == DecodedOpKind.Invalid ||
                IsTurboStraightLineBoundary(template.Kind))
            {
                break;
            }

            DecodedOp op = new(pc, opcode, template.Kind, template.N, template.M, template.Imm);
            ApplyFetchedInstructionPrelude();

            CurrentInstructionPc = pc;
            try
            {
                ExecuteDecodedOp(op, bus);
                bus.IncrementCycleCounter(1);
                CycleCounter += 1;
                AccumulatePcSample(pc, 1);
            }
            finally
            {
                CurrentInstructionPc = 0;
            }

            consumedInstructions++;
            if (bus.ShouldStopExecution || Registers.NextInstructionInDelaySlot || HasPendingInterrupt(bus))
                return true;

            pc = Registers.ProgramCounter;
            if (consumedInstructions >= remainingInstructions || consumedInstructions >= TurboStraightLineMaxOps)
                return true;

            opcode = bus.ReadOpcodeFast(pc);
        }

        return consumedInstructions != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTurboStraightLineBoundary(DecodedOpKind kind)
    {
        return kind is
            DecodedOpKind.Invalid or
            DecodedOpKind.Sleep or
            DecodedOpKind.Tst or
            DecodedOpKind.CmpEq or
            DecodedOpKind.CmpHs or
            DecodedOpKind.CmpGe or
            DecodedOpKind.CmpHi or
            DecodedOpKind.CmpGt or
            DecodedOpKind.CmpEqImm or
            DecodedOpKind.TstImm or
            DecodedOpKind.Dt or
            DecodedOpKind.CmpPz or
            DecodedOpKind.CmpPl or
            DecodedOpKind.Bra or
            DecodedOpKind.Bsr or
            DecodedOpKind.Bt or
            DecodedOpKind.Bf or
            DecodedOpKind.BtS or
            DecodedOpKind.BfS or
            DecodedOpKind.Rts or
            DecodedOpKind.Jmp or
            DecodedOpKind.Jsr or
            DecodedOpKind.Braf or
            DecodedOpKind.Bsrf;
    }

    private void ExecuteDecodedOp(DecodedOp op, Sega32XSh2Bus bus)
    {
        uint[] r = Registers.GeneralPurposeRegisters;
        int n = op.N;
        int m = op.M;
        Sega32XSh2StatusRegister sr;

        switch (op.Kind)
        {
            case DecodedOpKind.Nop:
                return;
            case DecodedOpKind.Sleep:
                return;
            case DecodedOpKind.MovImm:
                r[n] = unchecked((uint)op.Imm);
                return;
            case DecodedOpKind.AddImm:
                r[n] = unchecked(r[n] + (uint)op.Imm);
                return;
            case DecodedOpKind.MovReg:
                r[n] = r[m];
                return;
            case DecodedOpKind.Not:
                r[n] = ~r[m];
                return;
            case DecodedOpKind.Neg:
                r[n] = unchecked(0u - r[m]);
                return;
            case DecodedOpKind.ExtuB:
                r[n] = r[m] & 0xFF;
                return;
            case DecodedOpKind.ExtuW:
                r[n] = r[m] & 0xFFFF;
                return;
            case DecodedOpKind.ExtsB:
                r[n] = unchecked((uint)(sbyte)r[m]);
                return;
            case DecodedOpKind.ExtsW:
                r[n] = unchecked((uint)(short)r[m]);
                return;
            case DecodedOpKind.SwapB:
                {
                    uint value = r[m];
                    r[n] = (value & 0xFFFF0000) | ((value & 0x000000FF) << 8) | ((value & 0x0000FF00) >> 8);
                    return;
                }
            case DecodedOpKind.SwapW:
                {
                    uint value = r[m];
                    r[n] = (value << 16) | (value >> 16);
                    return;
                }
            case DecodedOpKind.And:
                r[n] &= r[m];
                return;
            case DecodedOpKind.Or:
                r[n] |= r[m];
                return;
            case DecodedOpKind.Xor:
                r[n] ^= r[m];
                return;
            case DecodedOpKind.Tst:
                sr = Registers.StatusRegister;
                sr.T = (r[m] & r[n]) == 0;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpEq:
                sr = Registers.StatusRegister;
                sr.T = r[n] == r[m];
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpHs:
                sr = Registers.StatusRegister;
                sr.T = r[n] >= r[m];
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpGe:
                sr = Registers.StatusRegister;
                sr.T = (int)r[n] >= (int)r[m];
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpHi:
                sr = Registers.StatusRegister;
                sr.T = r[n] > r[m];
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpGt:
                sr = Registers.StatusRegister;
                sr.T = (int)r[n] > (int)r[m];
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpEqImm:
                sr = Registers.StatusRegister;
                sr.T = r[0] == unchecked((uint)op.Imm);
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.TstImm:
                sr = Registers.StatusRegister;
                sr.T = (r[0] & (uint)(byte)op.Imm) == 0;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.AndImm:
                r[0] &= (uint)(byte)op.Imm;
                return;
            case DecodedOpKind.OrImm:
                r[0] |= (uint)(byte)op.Imm;
                return;
            case DecodedOpKind.XorImm:
                r[0] ^= (uint)(byte)op.Imm;
                return;
            case DecodedOpKind.Add:
                r[n] = unchecked(r[n] + r[m]);
                return;
            case DecodedOpKind.Sub:
                r[n] = unchecked(r[n] - r[m]);
                return;
            case DecodedOpKind.MulU:
                Registers.MacLow = (r[m] & 0xFFFF) * (r[n] & 0xFFFF);
                return;
            case DecodedOpKind.MulS:
                Registers.MacLow = unchecked((uint)((short)r[m] * (short)r[n]));
                return;
            case DecodedOpKind.MulL:
                Registers.MacLow = unchecked(r[n] * r[m]);
                return;
            case DecodedOpKind.MovT:
                r[n] = Registers.StatusRegister.T ? 1u : 0u;
                return;
            case DecodedOpKind.ClrT:
                sr = Registers.StatusRegister;
                sr.T = false;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.SetT:
                sr = Registers.StatusRegister;
                sr.T = true;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.ClrMac:
                Registers.MacLow = 0;
                Registers.MacHigh = 0;
                return;
            case DecodedOpKind.Div0U:
                sr = Registers.StatusRegister;
                sr.M = false;
                sr.Q = false;
                sr.T = false;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.Shll:
                sr = Registers.StatusRegister;
                sr.T = (r[n] & 0x80000000) != 0;
                r[n] <<= 1;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.Shlr:
                sr = Registers.StatusRegister;
                sr.T = (r[n] & 1) != 0;
                r[n] >>= 1;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.Shar:
                sr = Registers.StatusRegister;
                sr.T = (r[n] & 1) != 0;
                r[n] = (uint)((int)r[n] >> 1);
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.RotL:
                sr = Registers.StatusRegister;
                sr.T = (r[n] & 0x80000000) != 0;
                r[n] = (r[n] << 1) | (sr.T ? 1u : 0u);
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.RotR:
                sr = Registers.StatusRegister;
                sr.T = (r[n] & 1) != 0;
                r[n] = (r[n] >> 1) | (sr.T ? 0x80000000u : 0u);
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.Shll2:
                r[n] <<= 2;
                return;
            case DecodedOpKind.Shlr2:
                r[n] >>= 2;
                return;
            case DecodedOpKind.Shll8:
                r[n] <<= 8;
                return;
            case DecodedOpKind.Shlr8:
                r[n] >>= 8;
                return;
            case DecodedOpKind.Shll16:
                r[n] <<= 16;
                return;
            case DecodedOpKind.Shlr16:
                r[n] >>= 16;
                return;
            case DecodedOpKind.MovLDispPc:
                r[n] = bus.ReadLongword(((op.Pc + 4) & ~3u) + (uint)op.Imm, Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovWDispPc:
                r[n] = unchecked((uint)(short)bus.ReadWord(op.Pc + 4 + (uint)op.Imm, Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovLDispRm:
                r[n] = bus.ReadLongword(r[m] + (uint)op.Imm, Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovLStoreDispRn:
                bus.WriteLongword(r[n] + (uint)op.Imm, r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBLoad:
                r[n] = unchecked((uint)(sbyte)bus.ReadByte(r[m], Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovWLoad:
                r[n] = unchecked((uint)(short)bus.ReadWord(r[m], Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovLLoad:
                r[n] = bus.ReadLongword(r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBLoadPost:
                r[n] = unchecked((uint)(sbyte)bus.ReadByte(r[m], Sega32XSh2AccessContext.Data));
                if (n != m)
                    r[m]++;
                return;
            case DecodedOpKind.MovWLoadPost:
                r[n] = unchecked((uint)(short)bus.ReadWord(r[m], Sega32XSh2AccessContext.Data));
                if (n != m)
                    r[m] += 2;
                return;
            case DecodedOpKind.MovLLoadPost:
                r[n] = bus.ReadLongword(r[m], Sega32XSh2AccessContext.Data);
                if (n != m)
                    r[m] += 4;
                return;
            case DecodedOpKind.MovBStore:
                bus.WriteByte(r[n], (byte)r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovWStore:
                bus.WriteWord(r[n], (ushort)r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovLStore:
                bus.WriteLongword(r[n], r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBStorePre:
                r[n]--;
                bus.WriteByte(r[n], (byte)r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovWStorePre:
                r[n] -= 2;
                bus.WriteWord(r[n], (ushort)r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovLStorePre:
                r[n] -= 4;
                bus.WriteLongword(r[n], r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBStoreR0Rn:
                bus.WriteByte(r[0] + r[n], (byte)r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovWStoreR0Rn:
                bus.WriteWord(r[0] + r[n], (ushort)r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovLStoreR0Rn:
                bus.WriteLongword(r[0] + r[n], r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBLoadR0Rm:
                r[n] = unchecked((uint)(sbyte)bus.ReadByte(r[0] + r[m], Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovWLoadR0Rm:
                r[n] = unchecked((uint)(short)bus.ReadWord(r[0] + r[m], Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovLLoadR0Rm:
                r[n] = bus.ReadLongword(r[0] + r[m], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBStoreDispRm:
                bus.WriteByte(r[m] + (uint)op.Imm, (byte)r[0], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovWStoreDispRm:
                bus.WriteWord(r[m] + (uint)op.Imm, (ushort)r[0], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBLoadDispRm:
                r[0] = unchecked((uint)(sbyte)bus.ReadByte(r[m] + (uint)op.Imm, Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovWLoadDispRm:
                r[0] = unchecked((uint)(short)bus.ReadWord(r[m] + (uint)op.Imm, Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovBStoreGbr:
                bus.WriteByte(Registers.GlobalBaseRegister + (uint)op.Imm, (byte)r[0], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovWStoreGbr:
                bus.WriteWord(Registers.GlobalBaseRegister + (uint)op.Imm, (ushort)r[0], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovLStoreGbr:
                bus.WriteLongword(Registers.GlobalBaseRegister + (uint)op.Imm, r[0], Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovBLoadGbr:
                r[0] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GlobalBaseRegister + (uint)op.Imm, Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovWLoadGbr:
                r[0] = unchecked((uint)(short)bus.ReadWord(Registers.GlobalBaseRegister + (uint)op.Imm, Sega32XSh2AccessContext.Data));
                return;
            case DecodedOpKind.MovLLoadGbr:
                r[0] = bus.ReadLongword(Registers.GlobalBaseRegister + (uint)op.Imm, Sega32XSh2AccessContext.Data);
                return;
            case DecodedOpKind.MovA:
                r[0] = ((op.Pc + 4) & ~3u) + (uint)op.Imm;
                return;
            case DecodedOpKind.Dt:
                r[n]--;
                sr = Registers.StatusRegister;
                sr.T = r[n] == 0;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpPz:
                sr = Registers.StatusRegister;
                sr.T = (int)r[n] >= 0;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.CmpPl:
                sr = Registers.StatusRegister;
                sr.T = (int)r[n] > 0;
                Registers.StatusRegister = sr;
                return;
            case DecodedOpKind.Bra:
                Registers.NextProgramCounter = unchecked(Registers.NextProgramCounter + (uint)(op.Imm << 1));
                Registers.NextInstructionInDelaySlot = true;
                return;
            case DecodedOpKind.Bsr:
                Registers.ProcedureRegister = Registers.NextProgramCounter;
                Registers.NextProgramCounter = unchecked(Registers.NextProgramCounter + (uint)(op.Imm << 1));
                Registers.NextInstructionInDelaySlot = true;
                return;
            case DecodedOpKind.Bt:
                if (Registers.StatusRegister.T)
                {
                    Registers.ProgramCounter = unchecked(Registers.NextProgramCounter + (uint)(op.Imm << 1));
                    Registers.NextProgramCounter = Registers.ProgramCounter + 2;
                }
                return;
            case DecodedOpKind.Bf:
                if (!Registers.StatusRegister.T)
                {
                    Registers.ProgramCounter = unchecked(Registers.NextProgramCounter + (uint)(op.Imm << 1));
                    Registers.NextProgramCounter = Registers.ProgramCounter + 2;
                }
                return;
            case DecodedOpKind.BtS:
                if (Registers.StatusRegister.T)
                {
                    Registers.NextProgramCounter = unchecked(Registers.NextProgramCounter + (uint)(op.Imm << 1));
                    Registers.NextInstructionInDelaySlot = true;
                }
                return;
            case DecodedOpKind.BfS:
                if (!Registers.StatusRegister.T)
                {
                    Registers.NextProgramCounter = unchecked(Registers.NextProgramCounter + (uint)(op.Imm << 1));
                    Registers.NextInstructionInDelaySlot = true;
                }
                return;
            case DecodedOpKind.Rts:
                Registers.NextProgramCounter = Registers.ProcedureRegister;
                Registers.NextInstructionInDelaySlot = true;
                return;
            case DecodedOpKind.Jmp:
                Registers.NextProgramCounter = r[n];
                Registers.NextInstructionInDelaySlot = true;
                bus.IncrementCycleCounter(1);
                CycleCounter += 1;
                return;
            case DecodedOpKind.Jsr:
                Registers.ProcedureRegister = Registers.NextProgramCounter;
                Registers.NextProgramCounter = r[n];
                Registers.NextInstructionInDelaySlot = true;
                bus.IncrementCycleCounter(1);
                CycleCounter += 1;
                return;
            case DecodedOpKind.Braf:
                Registers.NextProgramCounter = unchecked(Registers.NextProgramCounter + r[n]);
                Registers.NextInstructionInDelaySlot = true;
                return;
            case DecodedOpKind.Bsrf:
                Registers.ProcedureRegister = Registers.NextProgramCounter;
                Registers.NextProgramCounter = unchecked(Registers.NextProgramCounter + r[n]);
                Registers.NextInstructionInDelaySlot = true;
                return;
            case DecodedOpKind.LoadPr:
                Registers.ProcedureRegister = r[n];
                return;
        }
    }

    private static bool IsDecodedCompareSafe(DecodedOpKind kind)
    {
        return kind is not (
            DecodedOpKind.MovLDispPc or DecodedOpKind.MovWDispPc or DecodedOpKind.MovLDispRm or
            DecodedOpKind.MovLStoreDispRn or DecodedOpKind.MovBLoad or DecodedOpKind.MovWLoad or
            DecodedOpKind.MovLLoad or DecodedOpKind.MovBLoadPost or DecodedOpKind.MovWLoadPost or
            DecodedOpKind.MovLLoadPost or DecodedOpKind.MovBStore or DecodedOpKind.MovWStore or
            DecodedOpKind.MovLStore or DecodedOpKind.MovBStorePre or DecodedOpKind.MovWStorePre or
            DecodedOpKind.MovLStorePre or DecodedOpKind.MovBStoreR0Rn or DecodedOpKind.MovWStoreR0Rn or
            DecodedOpKind.MovLStoreR0Rn or DecodedOpKind.MovBLoadR0Rm or DecodedOpKind.MovWLoadR0Rm or
            DecodedOpKind.MovLLoadR0Rm or DecodedOpKind.MovBStoreDispRm or DecodedOpKind.MovWStoreDispRm or
            DecodedOpKind.MovBLoadDispRm or DecodedOpKind.MovWLoadDispRm or DecodedOpKind.MovBStoreGbr or
            DecodedOpKind.MovWStoreGbr or DecodedOpKind.MovLStoreGbr or DecodedOpKind.MovBLoadGbr or
            DecodedOpKind.MovWLoadGbr or DecodedOpKind.MovLLoadGbr);
    }

    private void CompareDecodedOpWithInterpreter(DecodedOp op)
    {
        _blockInterpreterCompareLogs++;
        CpuSnapshot before = CaptureSnapshot();

        ApplyFetchedInstructionPrelude();
        ExecuteDecodedOp(op, null!);
        CpuSnapshot decoded = CaptureSnapshot();

        RestoreSnapshot(before);
        ApplyFetchedInstructionPrelude();
        bool interpreted = TryExecute(op.Opcode, null!);
        CpuSnapshot fallback = CaptureSnapshot();

        RestoreSnapshot(before);

        if (!interpreted || !decoded.Equals(fallback))
        {
            Console.WriteLine(
                $"[S32X-BLOCK-INTERP-{Name}] compare divergence pc=0x{op.Pc:X8} op=0x{op.Opcode:X4} " +
                $"decoded_pc=0x{decoded.ProgramCounter:X8} fallback_pc=0x{fallback.ProgramCounter:X8}");
            _blockInterpreterCompareLogs = BlockInterpreterCompareInstructions;
        }
        else if (_blockInterpreterCompareLogs == BlockInterpreterCompareInstructions)
        {
            Console.WriteLine($"[S32X-BLOCK-INTERP-{Name}] compare sampled {BlockInterpreterCompareInstructions} decoded ops without divergence");
        }
    }

    private void ApplyFetchedInstructionPrelude()
    {
        Registers.ProgramCounter = Registers.NextProgramCounter;
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;
    }

    private CpuSnapshot CaptureSnapshot() => new(Registers, CycleCounter);

    private void RestoreSnapshot(CpuSnapshot snapshot)
    {
        Array.Copy(snapshot.GeneralPurposeRegisters, Registers.GeneralPurposeRegisters, Registers.GeneralPurposeRegisters.Length);
        Registers.StatusRegister = snapshot.StatusRegister;
        Registers.GlobalBaseRegister = snapshot.GlobalBaseRegister;
        Registers.VectorBaseRegister = snapshot.VectorBaseRegister;
        Registers.MacLow = snapshot.MacLow;
        Registers.MacHigh = snapshot.MacHigh;
        Registers.ProcedureRegister = snapshot.ProcedureRegister;
        Registers.ProgramCounter = snapshot.ProgramCounter;
        Registers.NextProgramCounter = snapshot.NextProgramCounter;
        Registers.NextInstructionInDelaySlot = snapshot.NextInstructionInDelaySlot;
        CycleCounter = snapshot.CycleCounter;
    }

    private static bool TryDecodeBlockOp(uint pc, ushort opcode, out DecodedOp op)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;
        op = new DecodedOp(pc, opcode, DecodedOpKind.Nop, n, m, 0);

        switch (opcode & 0xF000)
        {
            case 0x1000:
                if (!AggressiveBlockInterpreter)
                    return false;
                op = new DecodedOp(pc, opcode, DecodedOpKind.MovLStoreDispRn, n, m, (opcode & 0xF) << 2);
                return true;
            case 0x5000:
                if (!AggressiveBlockInterpreter)
                    return false;
                op = new DecodedOp(pc, opcode, DecodedOpKind.MovLDispRm, n, m, (opcode & 0xF) << 2);
                return true;
            case 0x9000:
                if (!AggressiveBlockInterpreter)
                    return false;
                op = new DecodedOp(pc, opcode, DecodedOpKind.MovWDispPc, n, m, (opcode & 0xFF) << 1);
                return true;
            case 0xD000:
                if (!AggressiveBlockInterpreter)
                    return false;
                op = new DecodedOp(pc, opcode, DecodedOpKind.MovLDispPc, n, m, (opcode & 0xFF) << 2);
                return true;
            case 0xE000:
                op = new DecodedOp(pc, opcode, DecodedOpKind.MovImm, n, m, (sbyte)(opcode & 0xFF));
                return true;
            case 0x7000:
                op = new DecodedOp(pc, opcode, DecodedOpKind.AddImm, n, m, (sbyte)(opcode & 0xFF));
                return true;
            case 0x6000:
                return TryDecodeBlockOp6(pc, opcode, n, m, out op);
            case 0x2000:
                return TryDecodeBlockOp2(pc, opcode, n, m, out op);
            case 0x3000:
                return TryDecodeBlockOp3(pc, opcode, n, m, out op);
            case 0x4000:
                return TryDecodeBlockOp4(pc, opcode, n, out op);
            case 0x0000:
                return TryDecodeBlockOp0(pc, opcode, n, out op);
            case 0x8000:
                if (TryDecodeBlockOp8(pc, opcode, n, m, out op))
                    return true;
                if ((opcode & 0xFF00) == 0x8800)
                {
                    op = new DecodedOp(pc, opcode, DecodedOpKind.CmpEqImm, n, m, (sbyte)(opcode & 0xFF));
                    return true;
                }
                return false;
            case 0xC000:
                return TryDecodeBlockOpC(pc, opcode, n, m, out op);
            default:
                return false;
        }
    }

    private static DecodedOp[] BuildFastOpcodeTable()
    {
        DecodedOp[] table = new DecodedOp[ushort.MaxValue + 1];
        for (int opcode = 0; opcode < table.Length; opcode++)
        {
            if (TryDecodeFastOpcode((ushort)opcode, out DecodedOp op))
                table[opcode] = op;
        }

        return table;
    }

    private static bool TryDecodeFastOpcode(ushort opcode, out DecodedOp op)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        switch (opcode & 0xF000)
        {
            case 0x1000:
                op = new DecodedOp(0, opcode, DecodedOpKind.MovLStoreDispRn, n, m, (opcode & 0xF) << 2);
                return true;
            case 0x5000:
                op = new DecodedOp(0, opcode, DecodedOpKind.MovLDispRm, n, m, (opcode & 0xF) << 2);
                return true;
            case 0x9000:
                op = new DecodedOp(0, opcode, DecodedOpKind.MovWDispPc, n, m, (opcode & 0xFF) << 1);
                return true;
            case 0xD000:
                op = new DecodedOp(0, opcode, DecodedOpKind.MovLDispPc, n, m, (opcode & 0xFF) << 2);
                return true;
            case 0xE000:
                op = new DecodedOp(0, opcode, DecodedOpKind.MovImm, n, m, (sbyte)(opcode & 0xFF));
                return true;
            case 0x7000:
                op = new DecodedOp(0, opcode, DecodedOpKind.AddImm, n, m, (sbyte)(opcode & 0xFF));
                return true;
            case 0xA000:
                op = new DecodedOp(0, opcode, DecodedOpKind.Bra, n, m, ((short)(opcode << 4)) >> 4);
                return true;
            case 0xB000:
                op = new DecodedOp(0, opcode, DecodedOpKind.Bsr, n, m, ((short)(opcode << 4)) >> 4);
                return true;
            case 0x6000:
                return TryDecodeFastOpcode6(opcode, n, m, out op);
            case 0x2000:
                return TryDecodeFastOpcode2(opcode, n, m, out op);
            case 0x3000:
                return TryDecodeFastOpcode3(opcode, n, m, out op);
            case 0x4000:
                return TryDecodeFastOpcode4(opcode, n, m, out op);
            case 0x0000:
                return TryDecodeFastOpcode0(opcode, n, m, out op);
            case 0x8000:
                return TryDecodeFastOpcode8(opcode, n, m, out op);
            case 0xC000:
                return TryDecodeFastOpcodeC(opcode, n, m, out op);
            default:
                op = default;
                return false;
        }
    }

    private static bool TryDecodeFastOpcode6(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF00F) switch
        {
            0x6000 => DecodedOpKind.MovBLoad,
            0x6001 => DecodedOpKind.MovWLoad,
            0x6002 => DecodedOpKind.MovLLoad,
            0x6003 => DecodedOpKind.MovReg,
            0x6004 => DecodedOpKind.MovBLoadPost,
            0x6005 => DecodedOpKind.MovWLoadPost,
            0x6006 => DecodedOpKind.MovLLoadPost,
            0x6007 => DecodedOpKind.Not,
            0x6008 => DecodedOpKind.SwapB,
            0x6009 => DecodedOpKind.SwapW,
            0x600B => DecodedOpKind.Neg,
            0x600C => DecodedOpKind.ExtuB,
            0x600D => DecodedOpKind.ExtuW,
            0x600E => DecodedOpKind.ExtsB,
            0x600F => DecodedOpKind.ExtsW,
            _ => DecodedOpKind.Invalid,
        };
        op = new DecodedOp(0, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeFastOpcode2(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF00F) switch
        {
            0x2000 => DecodedOpKind.MovBStore,
            0x2001 => DecodedOpKind.MovWStore,
            0x2002 => DecodedOpKind.MovLStore,
            0x2004 => DecodedOpKind.MovBStorePre,
            0x2005 => DecodedOpKind.MovWStorePre,
            0x2006 => DecodedOpKind.MovLStorePre,
            0x2008 => DecodedOpKind.Tst,
            0x2009 => DecodedOpKind.And,
            0x200A => DecodedOpKind.Xor,
            0x200B => DecodedOpKind.Or,
            0x200E => DecodedOpKind.MulU,
            0x200F => DecodedOpKind.MulS,
            _ => DecodedOpKind.Invalid,
        };
        op = new DecodedOp(0, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeFastOpcode3(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF00F) switch
        {
            0x3000 => DecodedOpKind.CmpEq,
            0x3002 => DecodedOpKind.CmpHs,
            0x3003 => DecodedOpKind.CmpGe,
            0x3006 => DecodedOpKind.CmpHi,
            0x3007 => DecodedOpKind.CmpGt,
            0x3008 => DecodedOpKind.Sub,
            0x300C => DecodedOpKind.Add,
            _ => DecodedOpKind.Invalid,
        };
        op = new DecodedOp(0, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeFastOpcode4(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF0FF) switch
        {
            0x4000 or 0x4020 => DecodedOpKind.Shll,
            0x4001 => DecodedOpKind.Shlr,
            0x4010 => DecodedOpKind.Dt,
            0x4011 => DecodedOpKind.CmpPz,
            0x4015 => DecodedOpKind.CmpPl,
            0x4021 => DecodedOpKind.Shar,
            0x4004 => DecodedOpKind.RotL,
            0x4005 => DecodedOpKind.RotR,
            0x4008 => DecodedOpKind.Shll2,
            0x4009 => DecodedOpKind.Shlr2,
            0x4018 => DecodedOpKind.Shll8,
            0x4019 => DecodedOpKind.Shlr8,
            0x4028 => DecodedOpKind.Shll16,
            0x4029 => DecodedOpKind.Shlr16,
            0x400B => DecodedOpKind.Jsr,
            0x402B => DecodedOpKind.Jmp,
            0x402A => DecodedOpKind.LoadPr,
            _ => DecodedOpKind.Invalid,
        };
        op = new DecodedOp(0, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeFastOpcode0(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = opcode switch
        {
            0x0008 => DecodedOpKind.ClrT,
            0x0018 => DecodedOpKind.SetT,
            0x0028 => DecodedOpKind.ClrMac,
            0x0019 => DecodedOpKind.Div0U,
            0x0009 => DecodedOpKind.Nop,
            0x000B => DecodedOpKind.Rts,
            0x001B => DecodedOpKind.Sleep,
            _ => DecodedOpKind.Invalid,
        };
        if ((opcode & 0xF00F) == 0x0004)
            kind = DecodedOpKind.MovBStoreR0Rn;
        else if ((opcode & 0xF00F) == 0x0005)
            kind = DecodedOpKind.MovWStoreR0Rn;
        else if ((opcode & 0xF00F) == 0x0006)
            kind = DecodedOpKind.MovLStoreR0Rn;
        else if ((opcode & 0xF00F) == 0x000C)
            kind = DecodedOpKind.MovBLoadR0Rm;
        else if ((opcode & 0xF00F) == 0x000D)
            kind = DecodedOpKind.MovWLoadR0Rm;
        else if ((opcode & 0xF00F) == 0x000E)
            kind = DecodedOpKind.MovLLoadR0Rm;
        else if ((opcode & 0xF00F) == 0x0007)
            kind = DecodedOpKind.MulL;
        else if ((opcode & 0xF0FF) == 0x0029)
            kind = DecodedOpKind.MovT;
        else if ((opcode & 0xF0FF) == 0x0003)
            kind = DecodedOpKind.Bsrf;
        else if ((opcode & 0xF0FF) == 0x0023)
            kind = DecodedOpKind.Braf;

        op = new DecodedOp(0, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeFastOpcode8(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = ((opcode >> 8) & 0xF) switch
        {
            0x0 => DecodedOpKind.MovBStoreDispRm,
            0x1 => DecodedOpKind.MovWStoreDispRm,
            0x4 => DecodedOpKind.MovBLoadDispRm,
            0x5 => DecodedOpKind.MovWLoadDispRm,
            0x8 => DecodedOpKind.CmpEqImm,
            0x9 => DecodedOpKind.Bt,
            0xB => DecodedOpKind.Bf,
            0xD => DecodedOpKind.BtS,
            0xF => DecodedOpKind.BfS,
            _ => DecodedOpKind.Invalid,
        };

        int imm = opcode & 0xFF;
        if (kind is DecodedOpKind.MovBStoreDispRm or DecodedOpKind.MovBLoadDispRm)
            imm = opcode & 0xF;
        else if (kind is DecodedOpKind.MovWStoreDispRm or DecodedOpKind.MovWLoadDispRm)
            imm = (opcode & 0xF) << 1;
        else if (kind is DecodedOpKind.CmpEqImm or DecodedOpKind.Bt or DecodedOpKind.Bf or DecodedOpKind.BtS or DecodedOpKind.BfS)
            imm = (sbyte)(opcode & 0xFF);

        op = new DecodedOp(0, opcode, kind, n, m, imm);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeFastOpcodeC(ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xFF00) switch
        {
            0xC000 => DecodedOpKind.MovBStoreGbr,
            0xC100 => DecodedOpKind.MovWStoreGbr,
            0xC200 => DecodedOpKind.MovLStoreGbr,
            0xC400 => DecodedOpKind.MovBLoadGbr,
            0xC500 => DecodedOpKind.MovWLoadGbr,
            0xC600 => DecodedOpKind.MovLLoadGbr,
            0xC700 => DecodedOpKind.MovA,
            0xC800 => DecodedOpKind.TstImm,
            0xC900 => DecodedOpKind.AndImm,
            0xCA00 => DecodedOpKind.XorImm,
            0xCB00 => DecodedOpKind.OrImm,
            _ => DecodedOpKind.Invalid,
        };

        int displacement = opcode & 0xFF;
        if (kind is DecodedOpKind.MovWStoreGbr or DecodedOpKind.MovWLoadGbr)
            displacement <<= 1;
        else if (kind is DecodedOpKind.MovLStoreGbr or DecodedOpKind.MovLLoadGbr or DecodedOpKind.MovA)
            displacement <<= 2;

        op = new DecodedOp(0, opcode, kind, n, m, displacement);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOp6(uint pc, ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF00F) switch
        {
            0x6000 => DecodedOpKind.MovBLoad,
            0x6001 => DecodedOpKind.MovWLoad,
            0x6002 => DecodedOpKind.MovLLoad,
            0x6003 => DecodedOpKind.MovReg,
            0x6004 => DecodedOpKind.MovBLoadPost,
            0x6005 => DecodedOpKind.MovWLoadPost,
            0x6006 => DecodedOpKind.MovLLoadPost,
            0x6007 => DecodedOpKind.Not,
            0x6008 => DecodedOpKind.SwapB,
            0x6009 => DecodedOpKind.SwapW,
            0x600B => DecodedOpKind.Neg,
            0x600C => DecodedOpKind.ExtuB,
            0x600D => DecodedOpKind.ExtuW,
            0x600E => DecodedOpKind.ExtsB,
            0x600F => DecodedOpKind.ExtsW,
            _ => DecodedOpKind.Invalid,
        };
        if (!AggressiveBlockInterpreter && IsAggressiveOnlyDecodedKind(kind))
            kind = DecodedOpKind.Invalid;
        op = new DecodedOp(pc, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOp2(uint pc, ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF00F) switch
        {
            0x2000 => DecodedOpKind.MovBStore,
            0x2001 => DecodedOpKind.MovWStore,
            0x2002 => DecodedOpKind.MovLStore,
            0x2004 => DecodedOpKind.MovBStorePre,
            0x2005 => DecodedOpKind.MovWStorePre,
            0x2006 => DecodedOpKind.MovLStorePre,
            0x2008 => DecodedOpKind.Tst,
            0x2009 => DecodedOpKind.And,
            0x200A => DecodedOpKind.Xor,
            0x200B => DecodedOpKind.Or,
            0x200E => DecodedOpKind.MulU,
            0x200F => DecodedOpKind.MulS,
            _ => DecodedOpKind.Invalid,
        };
        if (!AggressiveBlockInterpreter && IsAggressiveOnlyDecodedKind(kind))
            kind = DecodedOpKind.Invalid;
        op = new DecodedOp(pc, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOp3(uint pc, ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF00F) switch
        {
            0x3000 => DecodedOpKind.CmpEq,
            0x3002 => DecodedOpKind.CmpHs,
            0x3003 => DecodedOpKind.CmpGe,
            0x3006 => DecodedOpKind.CmpHi,
            0x3007 => DecodedOpKind.CmpGt,
            0x3008 => DecodedOpKind.Sub,
            0x300C => DecodedOpKind.Add,
            _ => DecodedOpKind.Invalid,
        };
        op = new DecodedOp(pc, opcode, kind, n, m, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOp4(uint pc, ushort opcode, int n, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xF0FF) switch
        {
            0x4000 => DecodedOpKind.Shll,
            0x4001 => DecodedOpKind.Shlr,
            0x4010 => DecodedOpKind.Dt,
            0x4011 => DecodedOpKind.CmpPz,
            0x4015 => DecodedOpKind.CmpPl,
            0x4021 => DecodedOpKind.Shar,
            0x4004 => DecodedOpKind.RotL,
            0x4005 => DecodedOpKind.RotR,
            0x4008 => DecodedOpKind.Shll2,
            0x4009 => DecodedOpKind.Shlr2,
            0x4018 => DecodedOpKind.Shll8,
            0x4019 => DecodedOpKind.Shlr8,
            0x4028 => DecodedOpKind.Shll16,
            0x4029 => DecodedOpKind.Shlr16,
            _ => DecodedOpKind.Invalid,
        };
        if (!AggressiveBlockInterpreter && IsAggressiveOnlyDecodedKind(kind))
            kind = DecodedOpKind.Invalid;
        op = new DecodedOp(pc, opcode, kind, n, 0, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOp0(uint pc, ushort opcode, int n, out DecodedOp op)
    {
        DecodedOpKind kind = opcode switch
        {
            0x0004 => DecodedOpKind.MovBStoreR0Rn,
            0x0005 => DecodedOpKind.MovWStoreR0Rn,
            0x0006 => DecodedOpKind.MovLStoreR0Rn,
            0x000C => DecodedOpKind.MovBLoadR0Rm,
            0x000D => DecodedOpKind.MovWLoadR0Rm,
            0x000E => DecodedOpKind.MovLLoadR0Rm,
            0x0008 => DecodedOpKind.ClrT,
            0x0018 => DecodedOpKind.SetT,
            0x0028 => DecodedOpKind.ClrMac,
            0x0019 => DecodedOpKind.Div0U,
            0x0009 => DecodedOpKind.Nop,
            _ => DecodedOpKind.Invalid,
        };
        if ((opcode & 0xF0FF) == 0x0029)
            kind = DecodedOpKind.MovT;
        if ((opcode & 0xF00F) == 0x0007)
            kind = DecodedOpKind.MulL;
        if (!AggressiveBlockInterpreter && IsAggressiveOnlyDecodedKind(kind))
            kind = DecodedOpKind.Invalid;
        op = new DecodedOp(pc, opcode, kind, n, (opcode >> 4) & 0xF, 0);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOp8(uint pc, ushort opcode, int n, int m, out DecodedOp op)
    {
        if (!AggressiveBlockInterpreter)
        {
            op = new DecodedOp(pc, opcode, DecodedOpKind.Invalid, n, m, 0);
            return false;
        }

        DecodedOpKind kind = ((opcode >> 8) & 0xF) switch
        {
            0x0 => DecodedOpKind.MovBStoreDispRm,
            0x1 => DecodedOpKind.MovWStoreDispRm,
            0x4 => DecodedOpKind.MovBLoadDispRm,
            0x5 => DecodedOpKind.MovWLoadDispRm,
            _ => DecodedOpKind.Invalid,
        };
        int displacement = opcode & 0xF;
        if (kind is DecodedOpKind.MovWStoreDispRm or DecodedOpKind.MovWLoadDispRm)
            displacement <<= 1;
        op = new DecodedOp(pc, opcode, kind, n, m, displacement);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool TryDecodeBlockOpC(uint pc, ushort opcode, int n, int m, out DecodedOp op)
    {
        DecodedOpKind kind = (opcode & 0xFF00) switch
        {
            0xC000 => DecodedOpKind.MovBStoreGbr,
            0xC100 => DecodedOpKind.MovWStoreGbr,
            0xC200 => DecodedOpKind.MovLStoreGbr,
            0xC400 => DecodedOpKind.MovBLoadGbr,
            0xC500 => DecodedOpKind.MovWLoadGbr,
            0xC600 => DecodedOpKind.MovLLoadGbr,
            0xC700 => DecodedOpKind.MovA,
            0xC800 => DecodedOpKind.TstImm,
            0xC900 => DecodedOpKind.AndImm,
            0xCA00 => DecodedOpKind.XorImm,
            0xCB00 => DecodedOpKind.OrImm,
            _ => DecodedOpKind.Invalid,
        };
        if (!AggressiveBlockInterpreter && IsAggressiveOnlyDecodedKind(kind))
            kind = DecodedOpKind.Invalid;
        int displacement = opcode & 0xFF;
        if (kind is DecodedOpKind.MovWStoreGbr or DecodedOpKind.MovWLoadGbr)
            displacement <<= 1;
        else if (kind is DecodedOpKind.MovLStoreGbr or DecodedOpKind.MovLLoadGbr or DecodedOpKind.MovA)
            displacement <<= 2;
        op = new DecodedOp(pc, opcode, kind, n, m, displacement);
        return kind != DecodedOpKind.Invalid;
    }

    private static bool IsAggressiveOnlyDecodedKind(DecodedOpKind kind)
    {
        return kind is
            DecodedOpKind.MovLDispPc or DecodedOpKind.MovWDispPc or DecodedOpKind.MovLDispRm or
            DecodedOpKind.MovLStoreDispRn or DecodedOpKind.MovBLoad or DecodedOpKind.MovWLoad or
            DecodedOpKind.MovLLoad or DecodedOpKind.MovBLoadPost or DecodedOpKind.MovWLoadPost or
            DecodedOpKind.MovLLoadPost or DecodedOpKind.MovBStore or DecodedOpKind.MovWStore or
            DecodedOpKind.MovLStore or DecodedOpKind.MovBStorePre or DecodedOpKind.MovWStorePre or
            DecodedOpKind.MovLStorePre or DecodedOpKind.MovBStoreR0Rn or DecodedOpKind.MovWStoreR0Rn or
            DecodedOpKind.MovLStoreR0Rn or DecodedOpKind.MovBLoadR0Rm or DecodedOpKind.MovWLoadR0Rm or
            DecodedOpKind.MovLLoadR0Rm or DecodedOpKind.MovBStoreDispRm or DecodedOpKind.MovWStoreDispRm or
            DecodedOpKind.MovBLoadDispRm or DecodedOpKind.MovWLoadDispRm or DecodedOpKind.MovBStoreGbr or
            DecodedOpKind.MovWStoreGbr or DecodedOpKind.MovLStoreGbr or DecodedOpKind.MovBLoadGbr or
            DecodedOpKind.MovWLoadGbr or DecodedOpKind.MovLLoadGbr or DecodedOpKind.MovA or
            DecodedOpKind.Dt or DecodedOpKind.CmpPz or DecodedOpKind.CmpPl;
    }

    private static bool IsDecodedBlockTerminator(ushort opcode)
    {
        // This first conservative block interpreter never decodes control-flow instructions, so a
        // decoded opcode is currently never a terminator. Keep the hook explicit for future growth.
        return false;
    }

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

        if (TryExecuteTstBfsAddDelayLoop(bus, remainingInstructions, loopStartPc, firstOpcode, out consumedInstructions))
            return true;

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
    private bool TryExecuteTstBfsAddDelayLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        uint loopStartPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        const int InstructionsPerIteration = 3;
        consumedInstructions = 0;

        if (remainingInstructions < InstructionsPerIteration)
            return false;
        if ((firstOpcode & 0xF00F) != 0x2008) // TST Rm, Rn
            return false;

        int n = (firstOpcode >> 8) & 0xF;
        int m = (firstOpcode >> 4) & 0xF;
        if (n != m)
            return false;

        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort branchOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 4, out ushort delayOpcode))
        {
            return false;
        }

        if ((branchOpcode & 0xFF00) != 0x8F00) // BF/S disp
            return false;
        uint branchTarget = unchecked(loopStartPc + 6u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return false;

        if ((delayOpcode & 0xF000) != 0x7000 || ((delayOpcode >> 8) & 0xF) != n)
            return false;
        sbyte delta = unchecked((sbyte)(delayOpcode & 0xFF));
        if (delta != -1)
            return false;

        ulong remainingCycles = bus.CycleLimit > bus.SchedulerCycleCounter
            ? bus.CycleLimit - bus.SchedulerCycleCounter
            : 0;
        ulong maxIterations = Math.Min(
            remainingInstructions / InstructionsPerIteration,
            remainingCycles / InstructionsPerIteration);
        if (maxIterations == 0)
            return false;

        uint counter = Registers.GeneralPurposeRegisters[n];
        ulong iterationsToExit = (ulong)counter + 1;
        ulong iterations = Math.Min(maxIterations, iterationsToExit);
        if (iterations == 0)
            return false;

        bool loopFinished = iterations == iterationsToExit;
        Registers.GeneralPurposeRegisters[n] = unchecked(counter - (uint)iterations);

        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = loopFinished;
        Registers.StatusRegister = sr;

        ulong schedulerCycles = iterations * InstructionsPerIteration;
        bus.IncrementCycleCounter(schedulerCycles);
        bus.IncrementDetailCycleCounter(schedulerCycles);
        CycleCounter += schedulerCycles;

        if (loopFinished)
        {
            uint exitPc = loopStartPc + 6;
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
            return TryExecutePcRelativePollingLoop(bus, remainingInstructions, remainingCycles, loopStartPc, firstOpcode, out consumedInstructions);
        if (!IsFastPollingSource(bus, address, loadSize))
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
    private bool TryExecutePcRelativePollingLoop(
        Sega32XSh2Bus bus,
        ulong remainingInstructions,
        ulong remainingCycles,
        uint loopStartPc,
        ushort firstOpcode,
        out ulong consumedInstructions)
    {
        const ulong InstructionsPerIteration = 4;
        consumedInstructions = 0;
        if (remainingInstructions < InstructionsPerIteration || remainingCycles < InstructionsPerIteration)
            return false;
        if (!TryDecodePcRelativePollingLoop(bus, loopStartPc, firstOpcode, out int pointerRegister, out uint address, out int loadRegister, out PollingLoadSize loadSize, out ushort testOpcode, out ushort branchOpcode))
            return false;

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

        uint branchPc = loopStartPc + 6;
        uint branchTarget = unchecked(branchPc + 4u + (uint)(((sbyte)(branchOpcode & 0xFF)) << 1));
        if (branchTarget != loopStartPc)
            return false;

        Registers.GeneralPurposeRegisters[pointerRegister] = address;
        Registers.GeneralPurposeRegisters[loadRegister] = ReadPollingLoadValue(bus, address, loadSize);
        if (!TryEvaluatePollingTest(testOpcode, loadRegister, out bool testResult))
            return false;

        Sega32XSh2StatusRegister sr = Registers.StatusRegister;
        sr.T = testResult;
        Registers.StatusRegister = sr;

        bool branchTaken = branchOnTrue ? testResult : !testResult;
        ulong cyclesToConsume = InstructionsPerIteration;
        if (branchTaken)
            cyclesToConsume = Math.Min(remainingInstructions, remainingCycles);
        if (cyclesToConsume < InstructionsPerIteration)
            return false;

        if (branchTaken)
        {
            Registers.ProgramCounter = loopStartPc;
            Registers.NextProgramCounter = loopStartPc + 2;
        }
        else
        {
            uint exitPc = loopStartPc + 8;
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
            if (!IsFastPollingSource(bus, pollingAddress, _pollingLoadSize))
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
    private bool TryDecodePcRelativePollingLoop(
        Sega32XSh2Bus bus,
        uint loopStartPc,
        ushort firstOpcode,
        out int pointerRegister,
        out uint address,
        out int loadRegister,
        out PollingLoadSize loadSize,
        out ushort testOpcode,
        out ushort branchOpcode)
    {
        pointerRegister = 0;
        address = 0;
        loadRegister = 0;
        loadSize = PollingLoadSize.Word;
        testOpcode = 0;
        branchOpcode = 0;

        if ((firstOpcode & 0xF000) != 0xD000) // MOV.L @(disp, PC), Rn
            return false;

        pointerRegister = (firstOpcode >> 8) & 0xF;
        uint literalAddress = ((loopStartPc + 4) & ~3u) + (uint)((firstOpcode & 0xFF) << 2);
        if (!bus.TryPeekInstructionWord(literalAddress, out ushort high) ||
            !bus.TryPeekInstructionWord(literalAddress + 2, out ushort low))
        {
            return false;
        }

        uint baseAddress = ((uint)high << 16) | low;
        if (!bus.TryPeekInstructionWord(loopStartPc + 2, out ushort loadOpcode) ||
            !TryDecodePointerPollingLoad(loadOpcode, pointerRegister, baseAddress, out loadRegister, out address, out loadSize) ||
            !bus.TryPeekInstructionWord(loopStartPc + 4, out testOpcode) ||
            !bus.TryPeekInstructionWord(loopStartPc + 6, out branchOpcode))
        {
            return false;
        }

        return IsFastPollingSource(bus, address, loadSize) &&
            CanEvaluatePollingTest(testOpcode, loadRegister);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDecodePointerPollingLoad(
        ushort opcode,
        int pointerRegister,
        uint baseAddress,
        out int loadRegister,
        out uint address,
        out PollingLoadSize loadSize)
    {
        loadRegister = 0;
        address = 0;
        loadSize = PollingLoadSize.Word;

        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;
        switch (opcode & 0xF00F)
        {
            case 0x6000 when m == pointerRegister: // MOV.B @Rm, Rn
                loadRegister = n;
                address = baseAddress;
                loadSize = PollingLoadSize.Byte;
                return true;
            case 0x6001 when m == pointerRegister: // MOV.W @Rm, Rn
                loadRegister = n;
                address = baseAddress;
                loadSize = PollingLoadSize.Word;
                return true;
            case 0x6002 when m == pointerRegister: // MOV.L @Rm, Rn
                loadRegister = n;
                address = baseAddress;
                loadSize = PollingLoadSize.Longword;
                return true;
        }

        switch (opcode & 0xFF00)
        {
            case 0x8400 when m == pointerRegister: // MOV.B @(disp, Rm), R0
                loadRegister = 0;
                address = baseAddress + (uint)(opcode & 0xF);
                loadSize = PollingLoadSize.Byte;
                return true;
            case 0x8500 when m == pointerRegister: // MOV.W @(disp, Rm), R0
                loadRegister = 0;
                address = baseAddress + (uint)((opcode & 0xF) << 1);
                loadSize = PollingLoadSize.Word;
                return true;
        }

        return false;
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
    private static bool IsFastPollingSource(Sega32XSh2Bus bus, uint address, PollingLoadSize loadSize)
    {
        if (bus.IsFastPollingRegister(address))
            return true;

        int sizeBytes = loadSize switch
        {
            PollingLoadSize.Byte => 1,
            PollingLoadSize.Word => 2,
            _ => 4,
        };
        return bus.IsSimpleSdramAddress(address, sizeBytes);
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
        ushort opcode = bus.ReadOpcodeFast(pc);
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
            if ((FastCoreEnabled && TryExecuteFastCore(pc, opcode, bus)) || TryExecute(opcode, bus))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryExecuteFastCore(uint pc, ushort opcode, Sega32XSh2Bus bus)
    {
        DecodedOp op = FastOpcodeTable[opcode];
        if (op.Kind == DecodedOpKind.Invalid)
            return false;

        ExecuteDecodedOp(new DecodedOp(pc, opcode, op.Kind, op.N, op.M, op.Imm), bus);
        return true;
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

    private static bool ParseBoolEnvDefault(string name, bool defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseTraceInstructionMaxLogs()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_INST_MAX");
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : 256;
    }

    private static int ParseBlockInterpreterCompareInstructions()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_32X_BLOCK_INTERP_COMPARE");
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : 0;
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

internal enum DecodedOpKind : byte
{
    Invalid,
    Nop,
    Sleep,
    MovImm,
    AddImm,
    MovReg,
    Not,
    Neg,
    ExtuB,
    ExtuW,
    ExtsB,
    ExtsW,
    SwapB,
    SwapW,
    And,
    Or,
    Xor,
    Tst,
    CmpEq,
    CmpHs,
    CmpGe,
    CmpHi,
    CmpGt,
    CmpEqImm,
    TstImm,
    AndImm,
    OrImm,
    XorImm,
    Add,
    Sub,
    MulU,
    MulS,
    MulL,
    MovT,
    ClrT,
    SetT,
    ClrMac,
    Div0U,
    Shll,
    Shlr,
    Shar,
    RotL,
    RotR,
    Shll2,
    Shlr2,
    Shll8,
    Shlr8,
    Shll16,
    Shlr16,
    MovLDispPc,
    MovWDispPc,
    MovLDispRm,
    MovLStoreDispRn,
    MovBLoad,
    MovWLoad,
    MovLLoad,
    MovBLoadPost,
    MovWLoadPost,
    MovLLoadPost,
    MovBStore,
    MovWStore,
    MovLStore,
    MovBStorePre,
    MovWStorePre,
    MovLStorePre,
    MovBStoreR0Rn,
    MovWStoreR0Rn,
    MovLStoreR0Rn,
    MovBLoadR0Rm,
    MovWLoadR0Rm,
    MovLLoadR0Rm,
    MovBStoreDispRm,
    MovWStoreDispRm,
    MovBLoadDispRm,
    MovWLoadDispRm,
    MovBStoreGbr,
    MovWStoreGbr,
    MovLStoreGbr,
    MovBLoadGbr,
    MovWLoadGbr,
    MovLLoadGbr,
    MovA,
    Dt,
    CmpPz,
    CmpPl,
    Bra,
    Bsr,
    Bt,
    Bf,
    BtS,
    BfS,
    Rts,
    Jmp,
    Jsr,
    Braf,
    Bsrf,
    LoadPr,
}

internal readonly struct DecodedOp
{
    public readonly uint Pc;
    public readonly ushort Opcode;
    public readonly DecodedOpKind Kind;
    public readonly byte N;
    public readonly byte M;
    public readonly int Imm;

    public DecodedOp(uint pc, ushort opcode, DecodedOpKind kind, int n, int m, int imm)
    {
        Pc = pc;
        Opcode = opcode;
        Kind = kind;
        N = (byte)n;
        M = (byte)m;
        Imm = imm;
    }
}

internal sealed class DecodedBlock
{
    public DecodedBlock(uint startPc, ulong executableVersion, DecodedOp[] operations)
    {
        StartPc = startPc;
        ExecutableVersion = executableVersion;
        Operations = operations;
    }

    public uint StartPc { get; }
    public ulong ExecutableVersion { get; }
    public DecodedOp[] Operations { get; }
}

internal readonly struct CpuSnapshot : IEquatable<CpuSnapshot>
{
    public readonly uint[] GeneralPurposeRegisters;
    public readonly Sega32XSh2StatusRegister StatusRegister;
    public readonly uint GlobalBaseRegister;
    public readonly uint VectorBaseRegister;
    public readonly uint MacLow;
    public readonly uint MacHigh;
    public readonly uint ProcedureRegister;
    public readonly uint ProgramCounter;
    public readonly uint NextProgramCounter;
    public readonly bool NextInstructionInDelaySlot;
    public readonly ulong CycleCounter;

    public CpuSnapshot(Sega32XSh2Registers registers, ulong cycleCounter)
    {
        GeneralPurposeRegisters = new uint[16];
        Array.Copy(registers.GeneralPurposeRegisters, GeneralPurposeRegisters, GeneralPurposeRegisters.Length);
        StatusRegister = registers.StatusRegister;
        GlobalBaseRegister = registers.GlobalBaseRegister;
        VectorBaseRegister = registers.VectorBaseRegister;
        MacLow = registers.MacLow;
        MacHigh = registers.MacHigh;
        ProcedureRegister = registers.ProcedureRegister;
        ProgramCounter = registers.ProgramCounter;
        NextProgramCounter = registers.NextProgramCounter;
        NextInstructionInDelaySlot = registers.NextInstructionInDelaySlot;
        CycleCounter = cycleCounter;
    }

    public bool Equals(CpuSnapshot other)
    {
        for (int i = 0; i < GeneralPurposeRegisters.Length; i++)
        {
            if (GeneralPurposeRegisters[i] != other.GeneralPurposeRegisters[i])
                return false;
        }

        return StatusRegister.ToUInt32() == other.StatusRegister.ToUInt32()
            && GlobalBaseRegister == other.GlobalBaseRegister
            && VectorBaseRegister == other.VectorBaseRegister
            && MacLow == other.MacLow
            && MacHigh == other.MacHigh
            && ProcedureRegister == other.ProcedureRegister
            && ProgramCounter == other.ProgramCounter
            && NextProgramCounter == other.NextProgramCounter
            && NextInstructionInDelaySlot == other.NextInstructionInDelaySlot
            && CycleCounter == other.CycleCounter;
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
