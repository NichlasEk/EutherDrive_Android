namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XScaffoldCore
{
    private static readonly ulong DefaultSh2InstructionsPerFrame = ParseInstructionBudget();
    private static readonly ulong DefaultSh2ExecutionSliceLength = ParseExecutionSliceLength();
    private static readonly bool TracePcWords =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_PC_WORDS"), "1", StringComparison.Ordinal);
    private readonly byte[] _romData;
    private readonly byte[] _masterBootRom;
    private readonly byte[] _slaveBootRom;
    private readonly Sega32XSh2Bus _masterBus;
    private readonly Sega32XSh2Bus _slaveBus;
    private bool _commPortSyncInProgress;

    public Sega32XScaffoldCore(byte[] romData, Sega32XSystemRegisters? sharedRegisters = null)
    {
        _romData = romData;
        _masterBootRom = Sega32XBootRom.GetMasterBootRom().ToArray();
        _slaveBootRom = Sega32XBootRom.GetSlaveBootRom().ToArray();
        byte[] vectors = Sega32XBootRom.GetM68kVectors().ToArray();

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
    public ulong Sh2ExecutionSliceLength => DefaultSh2ExecutionSliceLength;

    public void Reset()
    {
        Registers.Reset();
        // Until the Genesis / 68000 side is wired in, bootstrap the 32X adapter into the
        // enabled state so the SH-2 boot ROMs can progress past the initial "adapter disabled"
        // sleep path.
        Registers.M68kWrite(0xA15100, 0x0003);
        MasterSh2.RequestReset();
        SlaveSh2.RequestReset();
        FrameCounter = 0;
    }

    public void RunFrame()
    {
        ulong remaining = DefaultSh2InstructionsPerFrame;
        ulong phaseBoundary = remaining / 8;

        Registers.NotifyVBlankStart();

        while (remaining > 0)
        {
            ulong slice = Math.Min(remaining, DefaultSh2ExecutionSliceLength);
            SlaveSh2.Execute(slice, _slaveBus);
            MasterSh2.Execute(slice, _masterBus);
            remaining -= slice;

            if (remaining == phaseBoundary)
            {
                if (Registers.EitherHInterruptEnabled)
                    Registers.NotifyHInterrupt();
            }
            else if (remaining == phaseBoundary / 2)
            {
                Registers.NotifyVBlankEnd();
            }
        }

        if (TracePcWords && (FrameCounter < 8 || FrameCounter % 60 == 59))
        {
            DumpWordsNearPc("M", MasterSh2.Registers.ProgramCounter);
            DumpWordsNearPc("S", SlaveSh2.Registers.ProgramCounter);
        }

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

    private static ulong ParseInstructionBudget()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_BUDGET");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        return 100_000;
    }

    private static ulong ParseExecutionSliceLength()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_SCAFFOLD_SH2_SLICE");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        return 50;
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
}
