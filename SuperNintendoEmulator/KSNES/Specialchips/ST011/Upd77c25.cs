using System;

namespace KSNES.Specialchips.ST011;

internal sealed class Upd77c25
{
    private const ulong St011ClockHz = 15_000_000;
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
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ST011_IO"), "1", StringComparison.Ordinal);
    private static readonly bool TraceIoSnes = GetTraceIoSnes();
    private static readonly bool TraceIoDsp = GetTraceIoDsp();
    private static readonly int TraceIoLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_ST011_IO_LIMIT", 2000);
    private int _traceIoCount;

    public Upd77c25(byte[] rom, byte[] sram, bool isPal)
    {
        (uint[] programRom, ushort[] dataRom) = RomConvert.Convert(rom);
        _programRom = programRom;
        _dataRom = dataRom;
        _ram = ConvertSram(sram);
        _registers = new Registers();
        _idling = false;
        _pcMask = 0x3FFF; // 16K opcodes for ST011 (vs 2K for DSP1)
        _dpMask = 0x07FF; // 2K words RAM for ST011 (vs 256 words for DSP1)
        _rpMask = 0x07FF; // 2K words data ROM for ST011 (vs 1K for DSP1)
        _snesMasterClockHz = isPal ? SnesMasterClockPal : SnesMasterClockNtsc;
        _masterCyclesProduct = 0;
        _registers.OnUpdWriteData = OnUpdWriteData;
        _registers.SwapIoBytes = string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_ST011_SWAP_IO_BYTES"),
            "1",
            StringComparison.Ordinal);
    }

    public void Reset()
    {
        _registers.Reset();
        _idling = false;
        _masterCyclesProduct = 0;
    }

    public void PostLoadResync()
    {
        // Preserve serialized DSP runtime state across savestate load.
    }

    public byte ReadData()
    {
        byte value = _registers.SnesReadData();
        if (!_registers.Status.RequestForMaster)
            _idling = false;
        TraceIoRead(value);
        return value;
    }

    public void WriteData(byte value)
    {
        bool wordComplete = _registers.SnesWriteData(value, out ushort word);
        if (TraceIo && _traceIoCount < TraceIoLimit)
        {
            if (wordComplete)
                Console.WriteLine($"[ST011-IO] SNES write word=0x{word:X4}");
            else
                Console.WriteLine($"[ST011-IO] SNES write byte=0x{value:X2}");
            _traceIoCount++;
        }
        if (!_registers.Status.RequestForMaster)
            _idling = false;
    }

    public byte ReadStatus() => _registers.Status.ToByte();

    public void Tick(ulong masterCyclesElapsed)
    {
        if (_idling)
            return;

        _masterCyclesProduct += masterCyclesElapsed * St011ClockHz;
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

    public byte ReadRam(uint address)
    {
        ushort word = _ram[((address >> 1) & 0x7FF)];
        return (address & 1) == 0 ? (byte)(word & 0xFF) : (byte)(word >> 8);
    }

    public void WriteRam(uint address, byte value)
    {
        int wordAddr = (int)((address >> 1) & 0x7FF);
        if ((address & 1) == 0)
            _ram[wordAddr] = (ushort)((_ram[wordAddr] & 0xFF00) | value);
        else
            _ram[wordAddr] = (ushort)((_ram[wordAddr] & 0x00FF) | (value << 8));
    }

    public byte[] GetSram()
    {
        byte[] result = new byte[_ram.Length * 2];
        for (int i = 0; i < _ram.Length; i++)
        {
            result[i * 2] = (byte)(_ram[i] & 0xFF);
            result[i * 2 + 1] = (byte)(_ram[i] >> 8);
        }
        return result;
    }

    private static ushort[] ConvertSram(byte[] sram)
    {
        if (sram.Length < 0x1000 * 2)
            return new ushort[0x800]; // 2K words
        
        ushort[] ram = new ushort[0x800];
        for (int i = 0; i < 0x800; i++)
        {
            int idx = i * 2;
            ram[i] = (ushort)(sram[idx] | (sram[idx + 1] << 8));
        }
        return ram;
    }

    private void TraceIoRead(byte value)
    {
        if (!TraceIo || !TraceIoSnes || _traceIoCount >= TraceIoLimit)
            return;
        byte status = _registers.Status.ToByte();
        Console.WriteLine($"[ST011-IO] SNES read byte=0x{value:X2} status=0x{status:X2}");
        _traceIoCount++;
    }

    private void OnUpdWriteData(ushort value)
    {
        if (!TraceIo || !TraceIoDsp || _traceIoCount >= TraceIoLimit)
            return;
        Console.WriteLine($"[ST011-IO] DSP write word=0x{value:X4}");
        _traceIoCount++;
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
        string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ST011_IO_MODE");
        if (string.IsNullOrWhiteSpace(mode))
            return true;
        return !string.Equals(mode, "dsp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetTraceIoDsp()
    {
        string? mode = Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_ST011_IO_MODE");
        if (string.IsNullOrWhiteSpace(mode))
            return true;
        return !string.Equals(mode, "snes", StringComparison.OrdinalIgnoreCase);
    }
}
