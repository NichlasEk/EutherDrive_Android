using System;

namespace KSNES.Specialchips.DSP1;

internal sealed class Upd77c25
{
    private const int RecentIoCapacity = 512;
    private const ulong DspClockHz = 8_000_000;
    private const ulong SnesMasterClockNtsc = 21_477_272;
    private const ulong SnesMasterClockPal = 21_281_370;

    [NonSerialized]
    private readonly uint[] _programRom;
    [NonSerialized]
    private readonly ushort[] _dataRom;
    private readonly ushort[] _ram;
    private readonly Registers _registers;
    private bool _idling;
    private readonly ushort _pcMask;
    private readonly ushort _dpMask;
    private readonly ushort _rpMask;
    private readonly ulong _snesMasterClockHz;
    private ulong _masterCyclesProduct;
    private static readonly bool TraceIo =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_IO"), "1", StringComparison.Ordinal);
    private static readonly bool TraceIoSnes = GetTraceIoSnes();
    private static readonly bool TraceIoDsp = GetTraceIoDsp();
    private static readonly int TraceIoLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_DSP1_IO_LIMIT", 2000);
    private int _traceIoCount;
    private readonly byte[] _recentIoKind = new byte[RecentIoCapacity];
    private readonly ushort[] _recentIoValue = new ushort[RecentIoCapacity];
    private readonly byte[] _recentIoStatus = new byte[RecentIoCapacity];
    private int _recentIoWriteIndex;
    private int _recentIoCount;
    private int _consecutiveFfffWrites;
    private bool _ffffLoopDumped;
    private int _snesWordWritesSeen;
    private const int RecentOpcodeCapacity = 128;
    private readonly ushort[] _recentOpcodePc = new ushort[RecentOpcodeCapacity];
    private readonly uint[] _recentOpcodeValue = new uint[RecentOpcodeCapacity];
    private int _recentOpcodeWriteIndex;
    private int _recentOpcodeCount;
    private static readonly bool DetectFfffLoop =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DETECT_DSP1_FFFF_LOOP"), "1", StringComparison.Ordinal);
    private static readonly int DetectFfffLoopThreshold = ParseTraceLimit("EUTHERDRIVE_DETECT_DSP1_FFFF_LOOP_THRESHOLD", 16);

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
        _registers.OnUpdWriteData = OnUpdWriteData;
        _registers.SwapIoBytes = string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_SWAP_IO_BYTES"),
            "1",
            StringComparison.Ordinal);
    }

    public void Reset()
    {
        _registers.Reset();
        _idling = false;
        _masterCyclesProduct = 0;
        _consecutiveFfffWrites = 0;
        _ffffLoopDumped = false;
        _snesWordWritesSeen = 0;
        _recentOpcodeWriteIndex = 0;
        _recentOpcodeCount = 0;
    }

    public void PostLoadResync()
    {
        _consecutiveFfffWrites = 0;
        _ffffLoopDumped = false;
    }

    public byte ReadData()
    {
        byte value = _registers.SnesReadData();
        if (!_registers.Status.RequestForMaster)
            _idling = false;
        RecordIo(1, value, _registers.Status.ToByte());
        TraceIoRead(value);
        return value;
    }

    public void WriteData(byte value)
    {
        bool wordComplete = _registers.SnesWriteData(value, out ushort word);
        RecordIo((byte)(wordComplete ? 3 : 2), wordComplete ? word : value, _registers.Status.ToByte());
        if (wordComplete)
            _snesWordWritesSeen++;
        if (TraceIo && _traceIoCount < TraceIoLimit)
        {
            if (wordComplete)
                Console.WriteLine($"[DSP1-IO] SNES write word=0x{word:X4}");
            else
                Console.WriteLine($"[DSP1-IO] SNES write byte=0x{value:X2}");
            _traceIoCount++;
        }
        if (!_registers.Status.RequestForMaster)
            _idling = false;
    }

    public byte ReadStatus()
    {
        byte status = _registers.Status.ToByte();
        RecordIo(4, status, status);
        if (TraceIo && TraceIoSnes && _traceIoCount < TraceIoLimit)
        {
            Console.WriteLine($"[DSP1-IO] SNES read status=0x{status:X2}");
            _traceIoCount++;
        }
        return status;
    }

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
        ushort pc = _registers.Pc;
        uint opcode = _programRom[pc];
        _recentOpcodePc[_recentOpcodeWriteIndex] = pc;
        _recentOpcodeValue[_recentOpcodeWriteIndex] = opcode;
        _recentOpcodeWriteIndex = (_recentOpcodeWriteIndex + 1) % RecentOpcodeCapacity;
        if (_recentOpcodeCount < RecentOpcodeCapacity)
            _recentOpcodeCount++;
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
    public int SnesWordWritesSeen => _snesWordWritesSeen;

    internal void SetIdle() => _idling = true;

    public void DumpRecentOpcodes(string tag, int count)
    {
        count = Math.Clamp(count, 1, RecentOpcodeCapacity);
        count = Math.Min(count, _recentOpcodeCount);
        Console.WriteLine($"[DSP1-OP-DUMP] tag={tag} count={count} total={_recentOpcodeCount}");
        for (int i = 0; i < count; i++)
        {
            int idx = (_recentOpcodeWriteIndex - count + i + RecentOpcodeCapacity) % RecentOpcodeCapacity;
            Console.WriteLine($"[DSP1-OP-DUMP] tag={tag} idx={i + 1}/{count} pc=0x{_recentOpcodePc[idx]:X4} op=0x{_recentOpcodeValue[idx]:X6}");
        }
    }

    public void DumpRecentIo(string tag, int count)
    {
        count = Math.Clamp(count, 1, RecentIoCapacity);
        count = Math.Min(count, _recentIoCount);
        Console.WriteLine($"[DSP1-DUMP] tag={tag} count={count} total={_recentIoCount}");
        for (int i = 0; i < count; i++)
        {
            int idx = (_recentIoWriteIndex - count + i + RecentIoCapacity) % RecentIoCapacity;
            string kind = _recentIoKind[idx] switch
            {
                1 => "SNES-RD-DATA",
                2 => "SNES-WR-BYTE",
                3 => "SNES-WR-WORD",
                4 => "SNES-RD-STAT",
                5 => "DSP-WR-WORD",
                _ => "UNK"
            };
            Console.WriteLine(
                $"[DSP1-DUMP] tag={tag} idx={i + 1}/{count} kind={kind} val=0x{_recentIoValue[idx]:X4} status=0x{_recentIoStatus[idx]:X2}");
        }
    }

    private void TraceIoRead(byte value)
    {
        if (!TraceIo || !TraceIoSnes || _traceIoCount >= TraceIoLimit)
            return;
        byte status = _registers.Status.ToByte();
        Console.WriteLine($"[DSP1-IO] SNES read byte=0x{value:X2} status=0x{status:X2}");
        _traceIoCount++;
    }

    private void OnUpdWriteData(ushort value)
    {
        byte status = _registers.Status.ToByte();
        RecordIo(5, value, status);
        if (value == 0xFFFF)
        {
            _consecutiveFfffWrites++;
            if (DetectFfffLoop && !_ffffLoopDumped && _snesWordWritesSeen > 0 && _consecutiveFfffWrites >= DetectFfffLoopThreshold)
            {
                _ffffLoopDumped = true;
                Console.WriteLine($"[DSP1-FFFF-LOOP?] threshold={DetectFfffLoopThreshold} status=0x{status:X2} snesWords={_snesWordWritesSeen}");
                DumpRecentOpcodes("ffff-loop", Math.Min(_recentOpcodeCount, 64));
                DumpRecentIo("ffff-loop", Math.Min(_recentIoCount, 192));
            }
        }
        else
        {
            _consecutiveFfffWrites = 0;
        }
        if (!TraceIo || !TraceIoDsp || _traceIoCount >= TraceIoLimit)
            return;
        Console.WriteLine($"[DSP1-IO] DSP write word=0x{value:X4}");
        _traceIoCount++;
    }

    private void RecordIo(byte kind, ushort value, byte status)
    {
        _recentIoKind[_recentIoWriteIndex] = kind;
        _recentIoValue[_recentIoWriteIndex] = value;
        _recentIoStatus[_recentIoWriteIndex] = status;
        _recentIoWriteIndex = (_recentIoWriteIndex + 1) % RecentIoCapacity;
        if (_recentIoCount < RecentIoCapacity)
            _recentIoCount++;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int limit) && limit > 0)
            return limit;
        return defaultValue;
    }

    private static bool GetTraceIoSnes()
    {
        string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_IO_MODE");
        if (string.IsNullOrWhiteSpace(mode))
            return true;
        return !string.Equals(mode, "dsp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetTraceIoDsp()
    {
        string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_IO_MODE");
        if (string.IsNullOrWhiteSpace(mode))
            return true;
        return !string.Equals(mode, "snes", StringComparison.OrdinalIgnoreCase);
    }
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
    public Action<ushort>? OnUpdWriteData;
    public bool SwapIoBytes;

    public void Reset()
    {
        Pc = 0;
        FlagsA = new Flags();
        FlagsB = new Flags();
        Status = new Status
        {
            DrControl = DataRegisterBits.Sixteen
        };
        Rp = 0x3FF;
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
            return SwapIoBytes ? (byte)(Dr & 0xFF) : (byte)(Dr >> 8);
        }

        Status.DrBusy = true;
        return SwapIoBytes ? (byte)(Dr >> 8) : (byte)(Dr & 0xFF);
    }

    public bool SnesWriteData(byte value, out ushort word)
    {
        word = 0;
        if (Status.DrControl == DataRegisterBits.Eight)
        {
            Status.RequestForMaster = false;
            Dr = value;
            word = Dr;
            return true;
        }

        if (Status.DrBusy)
        {
            Status.DrBusy = false;
            Status.RequestForMaster = false;
            if (SwapIoBytes)
                Dr = (ushort)((Dr & 0xFF00) | value);
            else
                Dr = (ushort)((Dr & 0x00FF) | (value << 8));
            word = Dr;
            return true;
        }

        Status.DrBusy = true;
        if (SwapIoBytes)
            Dr = (ushort)((Dr & 0x00FF) | (value << 8));
        else
            Dr = (ushort)((Dr & 0xFF00) | value);
        return false;
    }

    public void UpdWriteData(ushort value)
    {
        Dr = value;
        Status.RequestForMaster = true;
        OnUpdWriteData?.Invoke(value);
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
