using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XScaffoldCore
{
    private static readonly ulong DefaultSh2InstructionsPerFrame = ParseInstructionBudget();
    private static readonly ulong DefaultSh2ExecutionSliceLength = ParseExecutionSliceLength();
    private static readonly bool TracePcWords =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_PC_WORDS"), "1", StringComparison.Ordinal);
    private static readonly string? TraceFilePath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_FILE");
    private readonly byte[] _romData;
    private readonly byte[] _masterBootRom;
    private readonly byte[] _slaveBootRom;
    private readonly Sega32XSh2Bus _masterBus;
    private readonly Sega32XSh2Bus _slaveBus;
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
        Bus = new Sega32XBus(_romData, vectors, Registers);
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
        FrameCounter = 0;
    }

    public void RunFrame()
    {
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
            SlaveSh2.Execute(slice, _slaveBus);
            MasterSh2.Execute(slice, _masterBus);
            remaining -= slice;
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

    public Sega32XSh2Cpu GetOtherCpu(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? SlaveSh2 : MasterSh2;

    public Sega32XSh2Bus GetOtherBus(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? _slaveBus : _masterBus;

    public Sega32XSh2Bus GetBus(Sega32XCpu whichCpu) =>
        whichCpu == Sega32XCpu.Master ? _masterBus : _slaveBus;

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
        _globalSh2Cycles = Math.Max(_globalSh2Cycles, Math.Max(_masterBus.CycleCounter, _slaveBus.CycleCounter));
        _masterBus.CycleLimit = _globalSh2Cycles;
        _slaveBus.CycleLimit = _globalSh2Cycles;
        Registers.UpdateInterruptLevels();
    }

    private static ulong ParseInstructionBudget()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_BUDGET");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        // 32X SH-2 CPUs run at roughly 23 MHz, which is about 383k cycles per 60 Hz frame.
        // Keep the scaffold close to hardware so boot ROM work doesn't get artificially stretched
        // across many host frames before the real integrated scheduler is in place.
        return 400_000;
    }

    private static ulong ParseExecutionSliceLength()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_SLICE");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        return 500;
    }

    private void DumpWordsNearPc(string tag, uint pc)
    {
        Console.Write($"[S32X-PCWORDS-{tag}] frame={FrameCounter} pc=0x{pc:X8}");
        if (pc >= 0x06000000 && pc < 0x06040000)
        {
            int baseIndex = (int)((pc - 0x06000000) >> 1);
            Console.Write(" words=");
            for (int i = 0; i < 8; i++)
            {
                int index = baseIndex + i;
                if ((uint)index >= Bus.Sdram.Length)
                    break;
                Console.Write($"{Bus.Sdram[index]:X4}");
                if (i != 7)
                    Console.Write(' ');
            }
        }
        else if (pc < 0x00004000)
        {
            ReadOnlySpan<byte> rom = tag == "M" ? MasterBootRom : SlaveBootRom;
            int offset = (int)(pc & ~1u);
            Console.Write(" words=");
            for (int i = 0; i < 8; i++)
            {
                int wordOffset = offset + (i * 2);
                if (wordOffset + 1 >= rom.Length)
                    break;
                ushort word = (ushort)((rom[wordOffset] << 8) | rom[wordOffset + 1]);
                Console.Write($"{word:X4}");
                if (i != 7)
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
