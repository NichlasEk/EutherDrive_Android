using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XScaffoldCore
{
    private const int CommWriteFifoSize = 8;
    private const ulong DefaultNominalSh2CyclesPerFrame = 400_000;
    private static readonly bool ExperimentalSharedTimebaseEnabled = ParseBoolEnv("EUTHERDRIVE_S32X_EXPERIMENTAL_SHARED_TIMEBASE");
    private static readonly bool ExperimentalCommPollEnabled = ParseBoolEnv("EUTHERDRIVE_S32X_EXPERIMENTAL_COMM_POLL");
    private static readonly ulong DefaultSh2InstructionsPerFrame = ParseInstructionBudget();
    private static readonly ulong DefaultM68kCyclesPerFrame = ParseM68kCycleBudget();
    private const ulong NativeM68kDivider = 7;
    private const ulong NativeSh2Multiplier = 3;
    private static readonly ulong DefaultSh2ExecutionSliceLength = ParseExecutionSliceLength();
    private static readonly ulong DefaultM68kCommSyncSliceLength = ParseM68kCommSyncSliceLength();
    private static readonly ulong CommWriteVisibilityWindow = ParseCommWriteVisibilityWindow();
    private static readonly bool TracePcWords =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_PC_WORDS"), "1", StringComparison.Ordinal);
    private static readonly string? TraceFilePath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_FILE");
    private readonly byte[] _romData;
    private readonly byte[] _masterBootRom;
    private readonly byte[] _slaveBootRom;
    private readonly Sega32XSh2Bus _masterBus;
    private readonly Sega32XSh2Bus _slaveBus;
    private readonly uint[] _commWriteAddresses = new uint[CommWriteFifoSize];
    private readonly ushort[] _commWriteValues = new ushort[CommWriteFifoSize];
    private readonly ulong[] _commWriteM68kReferenceCycles = new ulong[CommWriteFifoSize];
    private readonly Sega32XCommSource[] _commWriteSources = new Sega32XCommSource[CommWriteFifoSize];
    private readonly bool[] _commWriteValid = new bool[CommWriteFifoSize];
    private int _commWriteNextIndex;
    private ulong _globalSh2Cycles;
    private bool _commPortSyncInProgress;

    public Sega32XScaffoldCore(byte[] romData, Sega32XSystemRegisters? sharedRegisters = null)
    {
        _romData = romData;
        _masterBootRom = Sega32XBootRom.GetMasterBootRom().ToArray();
        _slaveBootRom = Sega32XBootRom.GetSlaveBootRom().ToArray();
        byte[] vectors = Sega32XBootRom.GetM68kVectors().ToArray();
        if (_romData.Length >= 4)
        {
            // Keep the game's initial SSP from the cartridge header while still booting through
            // the 32X vector table. A zero SSP causes bogus wrapped stack traffic until game code
            // manually repairs SP.
            Buffer.BlockCopy(_romData, 0, vectors, 0, 4);
        }

        Registers = sharedRegisters ?? new Sega32XSystemRegisters();
        if (ExperimentalCommPollEnabled)
            Registers.CommunicationPortWritten += OnCommunicationPortWritten;
        Bus = new Sega32XBus(_romData, vectors, Registers, SyncSh2sForM68kCommAccess);
        MasterSh2 = new Sega32XSh2Cpu("Master");
        SlaveSh2 = new Sega32XSh2Cpu("Slave");
        _masterBus = new Sega32XSh2Bus(this, Sega32XCpu.Master);
        _slaveBus = new Sega32XSh2Bus(this, Sega32XCpu.Slave);
    }

    public Sega32XSystemRegisters Registers { get; }
    public Sega32XBus Bus { get; }
    public Sega32XSh2Cpu MasterSh2 { get; }
    public Sega32XSh2Cpu SlaveSh2 { get; }
    public long FrameCounter { get; private set; }

    public ReadOnlySpan<byte> MasterBootRom => _masterBootRom;
    public ReadOnlySpan<byte> SlaveBootRom => _slaveBootRom;
    public bool UseExperimentalSharedTimebase => ExperimentalSharedTimebaseEnabled;
    public bool UseExperimentalCommPollModel => ExperimentalCommPollEnabled;
    public ulong Sh2InstructionsPerFrame => DefaultSh2InstructionsPerFrame;
    public ulong Sh2ExecutionSliceLength => DefaultSh2ExecutionSliceLength;

    public void Reset()
    {
        Registers.Reset();
        Bus.Vdp.Reset();
        // Until the Genesis / 68000 side is wired in, bootstrap the 32X adapter into the
        // enabled state so the SH-2 boot ROMs can progress past the initial "adapter disabled"
        // sleep path.
        Registers.M68kWrite(0xA15100, 0x0003);
        MasterSh2.RequestReset();
        SlaveSh2.RequestReset();
        MasterSh2.ResetTimingState();
        SlaveSh2.ResetTimingState();
        _masterBus.ResetTimingState();
        _slaveBus.ResetTimingState();
        _globalSh2Cycles = 0;
        Array.Clear(_commWriteAddresses, 0, _commWriteAddresses.Length);
        Array.Clear(_commWriteValues, 0, _commWriteValues.Length);
        Array.Clear(_commWriteM68kReferenceCycles, 0, _commWriteM68kReferenceCycles.Length);
        Array.Clear(_commWriteSources, 0, _commWriteSources.Length);
        Array.Clear(_commWriteValid, 0, _commWriteValid.Length);
        _commWriteNextIndex = 0;
        FrameCounter = 0;
    }

    public void RunFrame()
    {
        if (ExperimentalSharedTimebaseEnabled)
            RunM68kCycles(DefaultM68kCyclesPerFrame);
        else
            RunSlice(DefaultSh2InstructionsPerFrame);
        FinishFrame();
    }

    public void RunSlice(ulong ticks)
    {
        ulong remaining = ticks;
        while (remaining > 0)
        {
            ulong eventMclk = Bus.Vdp.MclkCyclesUntilNextEvent(Registers.EitherHInterruptEnabled);
            ulong eventTicks = (eventMclk * DefaultSh2InstructionsPerFrame + (Sega32XVdp.FrameMclkCycles - 1)) / Sega32XVdp.FrameMclkCycles;
            if (eventTicks == 0)
                eventTicks = 1;

            ulong slice = Math.Min(remaining, Math.Min(DefaultSh2ExecutionSliceLength, eventTicks));
            Bus.Vdp.AdvanceFrameTiming(slice, DefaultSh2InstructionsPerFrame, Registers);
            _globalSh2Cycles += slice;
            _slaveBus.CycleLimit = _globalSh2Cycles;
            _masterBus.CycleLimit = _globalSh2Cycles;

            while (_slaveBus.SchedulerCycleCounter < _globalSh2Cycles)
                SlaveSh2.Execute(DefaultSh2ExecutionSliceLength, _slaveBus);

            while (_masterBus.SchedulerCycleCounter < _globalSh2Cycles)
                MasterSh2.Execute(DefaultSh2ExecutionSliceLength, _masterBus);

            remaining -= slice;
        }
    }

    public void RunM68kCycles(ulong m68kCycles)
    {
        ulong remaining = m68kCycles;
        while (remaining > 0)
        {
            ulong eventMclk = Bus.Vdp.MclkCyclesUntilNextEvent(Registers.EitherHInterruptEnabled);
            ulong eventM68kCycles = (eventMclk + (NativeM68kDivider - 1)) / NativeM68kDivider;
            if (eventM68kCycles == 0)
                eventM68kCycles = 1;

            ulong sliceM68kCycles = Math.Min(remaining, eventM68kCycles);
            ulong elapsedSh2Cycles = sliceM68kCycles * NativeSh2Multiplier;
            _globalSh2Cycles += elapsedSh2Cycles;
            _slaveBus.CycleLimit = _globalSh2Cycles;
            _masterBus.CycleLimit = _globalSh2Cycles;

            while (_slaveBus.SchedulerCycleCounter < _globalSh2Cycles)
                SlaveSh2.Execute(DefaultSh2ExecutionSliceLength, _slaveBus);

            while (_masterBus.SchedulerCycleCounter < _globalSh2Cycles)
                MasterSh2.Execute(DefaultSh2ExecutionSliceLength, _masterBus);

            // Keep the 32X side in the same time domain as the current MD host bridge:
            // M68K/SystemCycles are the master clock, and the 32X consumes time derived from that.
            Bus.Vdp.AdvanceMclk(sliceM68kCycles * NativeM68kDivider, Registers);

            remaining -= sliceM68kCycles;
        }
    }

    public void FinishFrame()
    {
        if (TracePcWords && (FrameCounter < 8 || FrameCounter % 60 == 59))
        {
            DumpWordsNearPc("M", MasterSh2.Registers.ProgramCounter);
            DumpWordsNearPc("S", SlaveSh2.Registers.ProgramCounter);
        }

        TraceFrameState();

        FrameCounter++;
    }

    public bool BeginCommPortSync()
    {
        if (_commPortSyncInProgress)
            return false;

        _commPortSyncInProgress = true;
        return true;
    }

    public void EndCommPortSync()
    {
        _commPortSyncInProgress = false;
    }

    public void SyncSh2sForM68kCommAccess()
    {
        if (!_commPortSyncInProgress)
        {
            if (!BeginCommPortSync())
                return;
        }
        else
        {
            return;
        }

        ulong previousMasterLimit = _masterBus.CycleLimit;
        ulong previousSlaveLimit = _slaveBus.CycleLimit;

        try
        {
            ulong syncLimit = Math.Max(_masterBus.SchedulerCycleCounter, _slaveBus.SchedulerCycleCounter) + DefaultM68kCommSyncSliceLength;
            _masterBus.CycleLimit = syncLimit;
            _slaveBus.CycleLimit = syncLimit;
            SlaveSh2.Execute(DefaultM68kCommSyncSliceLength, _slaveBus);
            MasterSh2.Execute(DefaultM68kCommSyncSliceLength, _masterBus);
        }
        finally
        {
            _masterBus.CycleLimit = previousMasterLimit;
            _slaveBus.CycleLimit = previousSlaveLimit;
            EndCommPortSync();
        }
    }

    public Sega32XSh2Cpu GetOtherCpu(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? SlaveSh2 : MasterSh2;

    public Sega32XSh2Bus GetOtherBus(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? _slaveBus : _masterBus;

    public Sega32XSh2Bus GetBus(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? _masterBus : _slaveBus;

    public bool TryConsumeRecentCommWrite(uint address, Sega32XCpu readerCpu, ulong readerM68kReferenceCyclesDone, out ushort value)
    {
        if (!ExperimentalCommPollEnabled)
        {
            value = 0;
            return false;
        }

        Sega32XCommSource readerSource = ToCommSource(readerCpu);

        for (int offset = 1; offset <= CommWriteFifoSize; offset++)
        {
            int index = (_commWriteNextIndex - offset + CommWriteFifoSize) % CommWriteFifoSize;
            if (!_commWriteValid[index] || _commWriteAddresses[index] != (address & ~1u))
                continue;
            if (_commWriteSources[index] == readerSource)
                continue;

            ulong eventCycles = _commWriteM68kReferenceCycles[index];
            if (eventCycles > readerM68kReferenceCyclesDone)
                continue;
            if (readerM68kReferenceCyclesDone - eventCycles > CommWriteVisibilityWindow)
                continue;

            value = _commWriteValues[index];
            _commWriteValid[index] = false;
            return true;
        }

        value = 0;
        return false;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(FrameCounter);
        writer.Write(_commPortSyncInProgress);
        Bus.SaveState(writer);
        MasterSh2.SaveState(writer);
        SlaveSh2.SaveState(writer);
        _masterBus.SaveState(writer);
        _slaveBus.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        FrameCounter = reader.ReadInt64();
        _ = reader.ReadBoolean();
        Bus.LoadState(reader);
        MasterSh2.LoadState(reader);
        SlaveSh2.LoadState(reader);
        _masterBus.LoadState(reader);
        _slaveBus.LoadState(reader);
        _commPortSyncInProgress = false;
        _globalSh2Cycles = Math.Max(_globalSh2Cycles, Math.Max(_masterBus.SchedulerCycleCounter, _slaveBus.SchedulerCycleCounter));
        _masterBus.CycleLimit = _globalSh2Cycles;
        _slaveBus.CycleLimit = _globalSh2Cycles;
        Registers.UpdateInterruptLevels();
    }

    private void OnCommunicationPortWritten(Sega32XCommSource source, uint address, ushort value)
    {
        ulong m68kReferenceCycles = source switch
        {
            Sega32XCommSource.M68k => _globalSh2Cycles / NativeSh2Multiplier,
            Sega32XCommSource.MasterSh2 => _masterBus.M68kReferenceCyclesDone,
            Sega32XCommSource.SlaveSh2 => _slaveBus.M68kReferenceCyclesDone,
            _ => 0,
        };

        int index = _commWriteNextIndex;
        _commWriteAddresses[index] = address & ~1u;
        _commWriteValues[index] = value;
        _commWriteM68kReferenceCycles[index] = m68kReferenceCycles;
        _commWriteSources[index] = source;
        _commWriteValid[index] = true;
        _commWriteNextIndex = (_commWriteNextIndex + 1) % CommWriteFifoSize;

        WakeCommPollers(source, address);
    }

    private void WakeCommPollers(Sega32XCommSource source, uint address)
    {
        if (source != Sega32XCommSource.MasterSh2 && MasterSh2.ShouldWakeOnCommWrite(address))
            MasterSh2.ExitCommPoll();

        if (source != Sega32XCommSource.SlaveSh2 && SlaveSh2.ShouldWakeOnCommWrite(address))
            SlaveSh2.ExitCommPoll();
    }

    private static Sega32XCommSource ToCommSource(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? Sega32XCommSource.MasterSh2 : Sega32XCommSource.SlaveSh2;

    private static ulong ParseM68kCycleBudget()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_BUDGET");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return Math.Max(1, parsed / NativeSh2Multiplier);

        return Math.Max(1, DefaultNominalSh2CyclesPerFrame / NativeSh2Multiplier);
    }

    private static ulong ParseInstructionBudget()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_BUDGET");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        // Drive the global scheduler in nominal SH-2 cycles. The bus still tracks detailed
        // wait-state timing separately for VDP/timer effects, but frame pacing and cross-CPU
        // deadlines should not slow down just because the current bus model charges extra detail.
        return DefaultNominalSh2CyclesPerFrame;
    }

    private static ulong ParseExecutionSliceLength()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_SLICE");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        // A moderately larger default slice reduces scheduler overhead now that global deadlines
        // use nominal SH-2 cycles again. 20k preserved VF/Cosmic/Zaxxon bring-up in headless
        // tests while trimming 32XSlice cost versus the older 5k default.
        return 20_000;
    }

    private static ulong ParseM68kCommSyncSliceLength()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_M68K_COMM_SYNC_SLICE");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        // A small same-line catch-up slice lets the SH-2s respond to tight 68k communication-port
        // polling loops without paying the cost of globally finer interleaving.
        return 128;
    }

    private static ulong ParseCommWriteVisibilityWindow()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_COMM_FIFO_WINDOW");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        return 64;
    }

    private static bool ParseBoolEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private void DumpWordsNearPc(string tag, uint pc)
    {
        Console.Write($"[S32X-PCWORDS-{tag}] frame={FrameCounter} pc=0x{pc:X8}");
        if (pc >= 0x06000000 && pc < 0x06040000)
        {
            int baseIndex = (int)((pc - 0x06000000) >> 1);
            Console.Write(" words=");
            for (int i = 0; i < 24; i++)
            {
                int index = baseIndex + i;
                if ((uint)index >= Bus.Sdram.Length)
                    break;
                Console.Write($"{Bus.Sdram[index]:X4}");
                if (i != 23)
                    Console.Write(' ');
            }
        }
        else if (pc < 0x00004000)
        {
            ReadOnlySpan<byte> rom = tag == "M" ? MasterBootRom : SlaveBootRom;
            int offset = (int)(pc & ~1u);
            Console.Write(" words=");
            for (int i = 0; i < 24; i++)
            {
                int wordOffset = offset + (i * 2);
                if (wordOffset + 1 >= rom.Length)
                    break;
                ushort word = (ushort)((rom[wordOffset] << 8) | rom[wordOffset + 1]);
                Console.Write($"{word:X4}");
                if (i != 23)
                    Console.Write(' ');
            }
        }
        Console.WriteLine();
    }

    private void TraceFrameState()
    {
        if (string.IsNullOrWhiteSpace(TraceFilePath))
            return;

        ushort[] comm = Registers.CommunicationPorts;
        string masterWords = GetWordsNearPc(MasterSh2.Registers.ProgramCounter, true);
        string slaveWords = GetWordsNearPc(SlaveSh2.Registers.ProgramCounter, false);
        string line =
            $"frame={FrameCounter} mpc=0x{MasterSh2.Registers.ProgramCounter:X8} spc=0x{SlaveSh2.Registers.ProgramCounter:X8} " +
            $"aden={(Registers.AdapterEnabled ? 1 : 0)} reset={(Registers.ResetSh2 ? 1 : 0)} fm={(Registers.VdpAccess == Sega32XAccess.Sh2 ? 1 : 0)} " +
            $"comm=0x{comm[0]:X4}/0x{comm[1]:X4}/0x{comm[2]:X4}/0x{comm[3]:X4} " +
            $"mr0=0x{MasterSh2.Registers.GeneralPurposeRegisters[0]:X8} " +
            $"mr1=0x{MasterSh2.Registers.GeneralPurposeRegisters[1]:X8} " +
            $"mr2=0x{MasterSh2.Registers.GeneralPurposeRegisters[2]:X8} " +
            $"mr8=0x{MasterSh2.Registers.GeneralPurposeRegisters[8]:X8} " +
            $"mr9=0x{MasterSh2.Registers.GeneralPurposeRegisters[9]:X8} " +
            $"sr0=0x{SlaveSh2.Registers.GeneralPurposeRegisters[0]:X8} " +
            $"sr1=0x{SlaveSh2.Registers.GeneralPurposeRegisters[1]:X8} " +
            $"sr2=0x{SlaveSh2.Registers.GeneralPurposeRegisters[2]:X8} " +
            $"sr8=0x{SlaveSh2.Registers.GeneralPurposeRegisters[8]:X8} " +
            $"sr9=0x{SlaveSh2.Registers.GeneralPurposeRegisters[9]:X8} " +
            $"mwords={masterWords} swords={slaveWords}{Environment.NewLine}";
        System.IO.File.AppendAllText(TraceFilePath, line);
    }

    private string GetWordsNearPc(uint pc, bool masterCpu)
    {
        if (pc >= 0x06000000 && pc < 0x06040000)
        {
            int baseIndex = (int)((pc - 0x06000000) >> 1);
            var words = new System.Text.StringBuilder();
            for (int i = 0; i < 6; i++)
            {
                int index = baseIndex + i;
                if ((uint)index >= Bus.Sdram.Length)
                    break;
                if (i != 0)
                    words.Append(',');
                words.Append("0x");
                words.Append(Bus.Sdram[index].ToString("X4"));
            }
            return words.ToString();
        }

        if (pc < 0x00004000)
        {
            ReadOnlySpan<byte> rom = masterCpu ? MasterBootRom : SlaveBootRom;
            int offset = (int)(pc & ~1u);
            var words = new System.Text.StringBuilder();
            for (int i = 0; i < 6; i++)
            {
                int wordOffset = offset + (i * 2);
                if (wordOffset + 1 >= rom.Length)
                    break;
                ushort word = (ushort)((rom[wordOffset] << 8) | rom[wordOffset + 1]);
                if (i != 0)
                    words.Append(',');
                words.Append("0x");
                words.Append(word.ToString("X4"));
            }
            return words.ToString();
        }

        return string.Empty;
    }
}
