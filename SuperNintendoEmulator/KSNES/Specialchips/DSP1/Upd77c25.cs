namespace KSNES.Specialchips.DSP1;

internal sealed class Upd77c25
{
    private const ulong DspClockHz = 8_000_000;
    private const ulong SnesMasterClockNtsc = 21_477_272;
    private const ulong SnesMasterClockPal = 21_281_370;

    private readonly uint[] _programRom;
    private readonly ushort[] _dataRom;
    private readonly ushort[] _ram;
    private readonly Registers _registers;
    private bool _idling;
    private readonly ushort _pcMask;
    private readonly ushort _dpMask;
    private readonly ushort _rpMask;
    private readonly ulong _snesMasterClockHz;
    private ulong _masterCyclesProduct;

    public Upd77c25(byte[] rom, bool isPal)
    {
        (uint[] programRom, ushort[] dataRom) = RomConvert.Convert(rom);
        _programRom = programRom;
        _dataRom = dataRom;
        _ram = new ushort[0x100];
        _registers = new Registers();
        _idling = false;
        _pcMask = 0x7FF;
        _dpMask = 0x0FF;
        _rpMask = 0x3FF;
        _snesMasterClockHz = isPal ? SnesMasterClockPal : SnesMasterClockNtsc;
        _masterCyclesProduct = 0;
    }

    public void Reset()
    {
        _registers.Reset();
        _idling = false;
        _masterCyclesProduct = 0;
    }

    public byte ReadData()
    {
        byte value = _registers.SnesReadData();
        if (!_registers.Status.RequestForMaster)
            _idling = false;
        return value;
    }

    public void WriteData(byte value)
    {
        _registers.SnesWriteData(value);
        if (!_registers.Status.RequestForMaster)
            _idling = false;
    }

    public byte ReadStatus() => _registers.Status.ToByte();

    public void Tick(ulong masterCyclesElapsed)
    {
        if (_idling)
            return;

        _masterCyclesProduct += masterCyclesElapsed * DspClockHz;
        while (_masterCyclesProduct >= _snesMasterClockHz)
        {
            Instructions.Execute(this);
            _masterCyclesProduct -= _snesMasterClockHz;
        }
    }

    internal uint FetchOpcode()
    {
        uint opcode = _programRom[_registers.Pc];
        _registers.Pc = (ushort)((_registers.Pc + 1) & _pcMask);
        return opcode;
    }

    internal ushort ReadRam(ushort address) => _ram[address & _dpMask];
    internal void WriteRam(ushort address, ushort value) => _ram[address & _dpMask] = value;

    internal ushort ReadDataRom(ushort address) => _dataRom[address & _rpMask];

    internal Registers Regs => _registers;
    internal ushort PcMask => _pcMask;
    internal ushort RpMask => _rpMask;
    internal ushort DpMask => _dpMask;

    internal void SetIdle() => _idling = true;
}

internal sealed class Registers
{
    public ushort Dp;
    public ushort Rp;
    public ushort Pc;
    public readonly ushort[] Stack = new ushort[4];
    public byte StackIndex;
    public short K;
    public short L;
    public short AccA;
    public short AccB;
    public Flags FlagsA;
    public Flags FlagsB;
    public short Tr;
    public short Trb;
    public Status Status;
    public ushort Dr;
    public ushort So;

    public void Reset()
    {
        Dp = 0;
        Rp = 0x3FF;
        Pc = 0;
        StackIndex = 0;
        K = 0;
        L = 0;
        AccA = 0;
        AccB = 0;
        FlagsA = new Flags();
        FlagsB = new Flags();
        Tr = 0;
        Trb = 0;
        Status = new Status
        {
            RequestForMaster = true,
            DrBusy = false,
            DrControl = DataRegisterBits.Sixteen
        };
        Dr = 0;
        So = 0;
    }

    public byte SnesReadData()
    {
        if (Status.DrControl == DataRegisterBits.Eight)
        {
            Status.RequestForMaster = false;
            return (byte)(Dr & 0xFF);
        }

        if (Status.DrBusy)
        {
            Status.DrBusy = false;
            Status.RequestForMaster = false;
            return (byte)(Dr >> 8);
        }

        Status.DrBusy = true;
        return (byte)(Dr & 0xFF);
    }

    public void SnesWriteData(byte value)
    {
        if (Status.DrControl == DataRegisterBits.Eight)
        {
            Status.RequestForMaster = false;
            Dr = value;
            return;
        }

        if (Status.DrBusy)
        {
            Status.DrBusy = false;
            Status.RequestForMaster = false;
            Dr = (ushort)((Dr & 0x00FF) | (value << 8));
            return;
        }

        Status.DrBusy = true;
        Dr = (ushort)((Dr & 0xFF00) | value);
    }

    public void UpdWriteData(ushort value)
    {
        Dr = value;
        Status.RequestForMaster = true;
    }

    public void PushStack(ushort pc)
    {
        Stack[StackIndex & 0x03] = pc;
        StackIndex = (byte)((StackIndex + 1) & 0x03);
    }

    public ushort PopStack()
    {
        StackIndex = (byte)((StackIndex - 1) & 0x03);
        return Stack[StackIndex & 0x03];
    }

    public int KlProduct() => K * L;
}

internal struct Flags
{
    public bool Z;
    public bool C;
    public bool S0;
    public bool S1;
    public bool Ov0;
    public bool Ov1;
}

internal enum DataRegisterBits
{
    Eight,
    Sixteen
}

internal struct Status
{
    public bool RequestForMaster;
    public bool UserFlag0;
    public bool UserFlag1;
    public bool DrBusy;
    public DataRegisterBits DrControl;

    public void Write(ushort value)
    {
        UserFlag1 = ((value >> 14) & 1) != 0;
        UserFlag0 = ((value >> 13) & 1) != 0;
        DrControl = ((value >> 10) & 1) != 0 ? DataRegisterBits.Eight : DataRegisterBits.Sixteen;
    }

    public byte ToByte()
    {
        return (byte)(
            (RequestForMaster ? 0x80 : 0) |
            (UserFlag1 ? 0x40 : 0) |
            (UserFlag0 ? 0x20 : 0) |
            (DrBusy ? 0x10 : 0) |
            (DrControl == DataRegisterBits.Eight ? 0x04 : 0)
        );
    }
}

internal static class RomConvert
{
    public static (uint[] ProgramRom, ushort[] DataRom) Convert(byte[] rom)
    {
        Endianness endianness = DetectEndianness(rom);
        int programRomLen = 3 * 2048;
        uint[] program = ConvertProgramRom(rom.AsSpan(0, programRomLen), endianness);
        ushort[] data = ConvertToU16(rom.AsSpan(programRomLen), endianness);
        return (program, data);
    }

    private static Endianness DetectEndianness(ReadOnlySpan<byte> programRom)
    {
        for (int i = 0; i < 4; i++)
        {
            int idx = i * 3;
            if (idx + 2 >= programRom.Length)
                break;
            if (programRom[idx] == (byte)(i << 2) && programRom[idx + 1] == 0xC0 && programRom[idx + 2] == 0x97)
                return Endianness.Little;
            if (programRom[idx] == 0x97 && programRom[idx + 1] == 0xC0 && programRom[idx + 2] == (byte)(i << 2))
                return Endianness.Big;
        }
        return Endianness.Little;
    }

    private static uint[] ConvertProgramRom(ReadOnlySpan<byte> programRom, Endianness endianness)
    {
        int count = programRom.Length / 3;
        uint[] opcodes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i * 3;
            uint value = endianness == Endianness.Little
                ? (uint)(programRom[idx] | (programRom[idx + 1] << 8) | (programRom[idx + 2] << 16))
                : (uint)((programRom[idx] << 16) | (programRom[idx + 1] << 8) | programRom[idx + 2]);
            opcodes[i] = value;
        }
        return opcodes;
    }

    private static ushort[] ConvertToU16(ReadOnlySpan<byte> bytes, Endianness endianness)
    {
        int count = bytes.Length / 2;
        ushort[] words = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i * 2;
            ushort value = endianness == Endianness.Little
                ? (ushort)(bytes[idx] | (bytes[idx + 1] << 8))
                : (ushort)((bytes[idx] << 8) | bytes[idx + 1]);
            words[i] = value;
        }
        return words;
    }

    private enum Endianness
    {
        Little,
        Big
    }
}
