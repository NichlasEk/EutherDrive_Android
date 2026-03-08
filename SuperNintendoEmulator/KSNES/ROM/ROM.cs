using System;
using System.Collections.Generic;
using KSNES.Tracing;

namespace KSNES.ROM;

public class ROM : IROM
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Header Header { get; private set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [NonSerialized]
    private byte[] _data = [];
    private byte[] _sram = [];
    private bool _hasSram;
    private int _banks;
    private int _sramSize;

    [NonSerialized]
    private ISNESSystem? _system;
    [NonSerialized]
    private KSNES.Specialchips.CX4.Cx4? _cx4;
    [NonSerialized]
    private KSNES.Specialchips.DSP1.Dsp1? _dsp1;
    [NonSerialized]
    private KSNES.Specialchips.SuperFX.SuperFx? _superFx;
    [NonSerialized]
    private KSNES.Specialchips.SA1.Sa1? _sa1;
    private bool _superFxHasBattery;
    private ulong _superFxOverclock = 1;
    private Dsp1PortMapping _dsp1PortMapping;
    private bool _dsp1IsHiRom;
    private bool _dsp1BroadMap;
    private bool _dsp1SwapPorts;
    private static readonly bool TraceCx4Bus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4_BUS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceDsp1Bus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_BUS"), "1", StringComparison.Ordinal);
    private static readonly int TraceDsp1BusLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_DSP1_BUS_LIMIT", 500);
    private int _traceDsp1BusCount;
    private static readonly bool TraceSnesVectors =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_VECTORS"), "1", StringComparison.Ordinal);
    private readonly bool _traceSa1BwramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly HashSet<uint> _traceSa1BwramOffsets =
        ParseTraceOffsets(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_ADDRS"));

    [NonSerialized]
    private Timer? _sRAMTimer;

    public void LoadROM(byte[] data, Header header)
    {
        _data = data;
        Header = header;
        _sram = new byte[header.RamSize];
        _hasSram = header.Chips > 0;
        _banks = header.RomSize / 0x8000;
        _sramSize = header.RamSize;

        bool hasSuperFx = header.MapMode == 0x20 && header.ChipsetByte >= 0x13 && header.ChipsetByte <= 0x1A;
        if (hasSuperFx)
        {
            int ramLen = KSNES.Specialchips.SuperFX.SuperFx.GuessRamLen(_data);
            _sram = new byte[ramLen];
            _sramSize = _sram.Length;
            _superFxHasBattery = KSNES.Specialchips.SuperFX.SuperFx.HasBattery(_data);
            _hasSram = _superFxHasBattery;
            _superFxOverclock = (ulong)ParseTraceLimit("EUTHERDRIVE_SUPERFX_OVERCLOCK", 1);
            _superFx = new KSNES.Specialchips.SuperFX.SuperFx(_data, _sram, _superFxOverclock);
        }
        else
        {
            _superFx = null;
            _superFxHasBattery = false;
            _superFxOverclock = 1;
        }

        if (header.ExCoprocessor == 0x10)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");
            _cx4 = new KSNES.Specialchips.CX4.Cx4(_system);
            _cx4.Reset();
        }
        else
        {
            _cx4 = null;
        }

        if (IsSa1Chipset(header.ChipsetByte))
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");
            _sa1 = new KSNES.Specialchips.SA1.Sa1(_data, _sram, _system.IsPal);
            _sram = _sa1.Bwram;
            _sramSize = _sram.Length;
            _hasSram = KSNES.Specialchips.SA1.Sa1.HasBattery(_data, _sramSize);
        }
        else
        {
            _sa1 = null;
        }

        bool hasDsp1 = header.ExCoprocessor == 0 && header.Chips >= 3 && header.Chips <= 5;
        if (hasDsp1)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");

            byte[]? dspRom = TryLoadDsp1Rom();
            if (dspRom == null)
            {
                Console.WriteLine("[DSP1] ROM not found; DSP-1 disabled.");
                _dsp1 = null;
            }
            else
            {
                _dsp1 = new KSNES.Specialchips.DSP1.Dsp1(dspRom, _system);
                _dsp1.Reset();
            }
            _dsp1IsHiRom = header.IsHiRom;
            string? forceLo = Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_FORCE_LOROM");
            string? forceHi = Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_FORCE_HIROM");
            if (string.Equals(forceLo, "1", StringComparison.Ordinal))
            {
                _dsp1IsHiRom = false;
                Console.WriteLine("[DSP1] Forcing LoROM port mapping.");
            }
            else if (string.Equals(forceHi, "1", StringComparison.Ordinal))
            {
                _dsp1IsHiRom = true;
                Console.WriteLine("[DSP1] Forcing HiROM port mapping.");
            }
            _dsp1BroadMap = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_BROAD_MAP"), "1", StringComparison.Ordinal);
            if (_dsp1BroadMap)
                Console.WriteLine("[DSP1] Using broad port mapping (banks 00-3F/80-BF, $6000-$7FFF).");
            _dsp1SwapPorts = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_SWAP_PORTS"), "1", StringComparison.Ordinal);
            if (_dsp1SwapPorts)
                Console.WriteLine("[DSP1] Swapping data/status port halves.");
            _dsp1PortMapping = Dsp1PortMapping.FromMetadata(header.RomSize, header.RamSize);
        }
        else
        {
            _dsp1 = null;
            _dsp1BroadMap = false;
            _dsp1SwapPorts = false;
        }
    }

    public int RomLength => _data.Length;
    internal KSNES.Specialchips.CX4.Cx4? Cx4 => _cx4;
    internal KSNES.Specialchips.DSP1.Dsp1? Dsp1 => _dsp1;
    internal KSNES.Specialchips.SuperFX.SuperFx? SuperFx => _superFx;
    public object? Sa1 => _sa1;

    public void LoadSRAM()
    {
        string fileName = GetSRAMFileName();
        if (new FileInfo(fileName).Exists)
        {
            byte[] data = File.ReadAllBytes(fileName);
            int copy = Math.Min(data.Length, _sram.Length);
            if (copy > 0)
            {
                Buffer.BlockCopy(data, 0, _sram, 0, copy);
            }
            if (data.Length != _sram.Length)
            {
                Console.WriteLine($"[SRAM] Loaded size {data.Length} != expected {_sram.Length}. Truncated to {copy}.");
            }
        }
    }

    public byte Read(int bank, int adr)
    {
        int SramIndex(int idx) => _sramSize <= 0 ? 0 : idx % _sramSize;
        if (TraceSnesVectors && bank == 0x00 && (adr == 0xFFEA || adr == 0xFFEB || adr == 0xFFEE || adr == 0xFFEF))
        {
            Console.WriteLine($"[SNES-VEC] read bank=0x{bank:X2} adr=0x{adr:X4}");
        }
        if (_sa1 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_sa1.TryResolveSnesAccess(address, out string region, out uint? resolved))
            {
                int snesPc = -1;
                int snesOp = -1;
                TryGetSnesPc(out snesPc, out snesOp);
                byte? sa1Value = _sa1.SnesRead(address, snesPc);
                if (sa1Value.HasValue)
                {
                    if (Sa1Trace.IsEnabled && (region.StartsWith("I-RAM", StringComparison.Ordinal) || region.StartsWith("BW-RAM", StringComparison.Ordinal)))
                    {
                        _sa1.TraceSnesReadMirror(address, sa1Value.Value, region, resolved);
                    }
                    if (_traceSa1BwramWatch && region.StartsWith("BW-RAM", StringComparison.Ordinal))
                    {
                        uint adr16 = address & 0xFFFF;
                        uint off = resolved ?? 0;
                        if (adr16 == 0x604E || adr16 == 0x604F || (off & 0xFFFF) == 0x004E || (off & 0xFFFF) == 0x004F)
                        {
                            Console.WriteLine($"[SNES-BWRAM-RD] addr=0x{address:X6} bwram=0x{off:X6} val=0x{sa1Value.Value:X2} pc=0x{snesPc:X6}");
                        }
                    }
                    if (Sa1Trace.IsEnabled && snesPc >= 0)
                        Sa1Trace.Log("SNES", snesPc, snesOp, address, "R", sa1Value.Value, region, resolved);
                    return sa1Value.Value;
                }
            }
        }

        if (_superFx != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_superFx.Read(address, out byte value))
                return value;
        }

        if (_cx4 != null)
        {
            if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceCx4Bus)
                    Console.WriteLine($"[CX4-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4}");
                return _cx4.Read(adr);
            }
            if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            {
                int idx = ((bank & 0x0f) << 15) | (adr & 0x7fff);
                return _sram[SramIndex(idx)];
            }
            bank &= 0x7f;
            if (adr >= 0x8000 || bank >= 0x40)
            {
                return _data[((bank & (_banks - 1)) << 15) | (adr & 0x7fff)];
            }
            return (byte)(_system?.OpenBus ?? 0);
        }

        if (TryReadDsp1(bank, adr, out byte dsp1Value))
        {
            return dsp1Value;
        }

        if (Header.IsHiRom)
        {
            if (adr >= 0x6000 && adr < 0x8000 && _hasSram && IsHiRomSramBank(bank))
            {
                int idx = ((bank & 0x1f) << 13) | (adr & 0x1fff);
                return _sram[SramIndex(idx)];
            }

            if (adr >= 0x8000 || (bank >= 0x40 && (bank & 0x7f) < 0x7e))
            {
                return ReadHiRom(bank, adr);
            }
            return (byte)(_system?.OpenBus ?? 0);
        }

        if (adr < 0x8000)
        {
            if (bank >= 0x70 && bank < 0x7e && _hasSram)
            {
                int idx = ((bank - 0x70) << 15) | (adr & 0x7fff);
                return _sram[SramIndex(idx)];
            }
        }
        return _data[((bank & (_banks - 1)) << 15) | (adr & 0x7fff)];
    }

    public void Write(int bank, int adr, byte value)
    {
        int SramIndex(int idx) => _sramSize <= 0 ? 0 : idx % _sramSize;
        if (_sa1 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_sa1.TryResolveSnesAccess(address, out string region, out uint? resolved))
            {
                int snesPc = -1;
                int snesOp = -1;
                TryGetSnesPc(out snesPc, out snesOp);
                _sa1.SnesWrite(address, value, snesPc);
                if (ShouldTraceBwramContext(address, region, resolved) &&
                    TryGetSnesContext(out int ctxPc, out string regs, out string opBytes))
                {
                    Console.WriteLine($"[SNES-BWRAM-CTX] pc=0x{ctxPc:X6} op=[{opBytes}] regs=[{regs}] addr=0x{address:X6} bwram=0x{(resolved ?? 0):X6} val=0x{value:X2}");
                }
                if (Sa1Trace.IsEnabled && snesPc >= 0)
                    Sa1Trace.Log("SNES", snesPc, snesOp, address, "W", value, region, resolved);
                if (_hasSram && region.StartsWith("BW-RAM", StringComparison.Ordinal))
                    _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                return;
            }
        }

        if (_superFx != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_superFx.Write(address, value, out bool wroteRam))
            {
                if (wroteRam && _hasSram)
                    _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                return;
            }
        }

        if (_cx4 != null)
        {
            if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceCx4Bus)
                    Console.WriteLine($"[CX4-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
                _cx4.Write(adr, value);
                return;
            }
            if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            {
                int idx = ((bank & 0x0f) << 15) | (adr & 0x7fff);
                _sram[SramIndex(idx)] = value;
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            return;
        }

        if (TryWriteDsp1(bank, adr, value))
        {
            return;
        }

        if (Header.IsHiRom)
        {
            if (adr >= 0x6000 && adr < 0x8000 && _hasSram && IsHiRomSramBank(bank))
            {
                int idx = ((bank & 0x1f) << 13) | (adr & 0x1fff);
                _sram[SramIndex(idx)] = value;
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            return;
        }

        if (adr < 0x8000 && bank >= 0x70 && bank < 0x7e && _hasSram)
        {
            int idx = ((bank - 0x70) << 15) | (adr & 0x7fff);
            _sram[SramIndex(idx)] = value;
            _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    private bool TryReadDsp1(int bank, int adr, out byte value)
    {
        value = 0;
        if (_dsp1 == null)
        {
            return false;
        }
        bool IsDataPort(int address) => _dsp1SwapPorts ? (address & 0x4000) != 0 : (address & 0x4000) == 0;

        if (_dsp1BroadMap)
        {
            if ((bank & 0x7f) <= 0x3f && adr >= 0x6000 && adr < 0x8000)
            {
                value = IsDataPort(adr) ? _dsp1.ReadData() : _dsp1.ReadStatus();
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                {
                    string port = IsDataPort(adr) ? "DATA" : "STAT";
                    Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} {port} (broad) -> 0x{value:X2}");
                }
                return true;
            }
        }

        if (!_dsp1IsHiRom)
        {
            if (_dsp1PortMapping.IsDspPort(bank, adr))
            {
                value = IsDataPort(adr) ? _dsp1.ReadData() : _dsp1.ReadStatus();
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                {
                    string port = IsDataPort(adr) ? "DATA" : "STAT";
                    Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} {port} -> 0x{value:X2}");
                }
                return true;
            }
            return false;
        }

        if (((bank & 0x7f) <= 0x0f) && adr >= 0x6000 && adr < 0x7000)
        {
            value = _dsp1.ReadData();
            if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} DATA -> 0x{value:X2}");
            return true;
        }
        if (((bank & 0x7f) <= 0x0f) && adr >= 0x7000 && adr < 0x8000)
        {
            value = _dsp1.ReadStatus();
            if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} STAT -> 0x{value:X2}");
            return true;
        }
        return false;
    }

    private bool TryWriteDsp1(int bank, int adr, byte value)
    {
        if (_dsp1 == null)
        {
            return false;
        }
        bool IsDataPort(int address) => _dsp1SwapPorts ? (address & 0x4000) != 0 : (address & 0x4000) == 0;

        if (_dsp1BroadMap)
        {
            if ((bank & 0x7f) <= 0x3f && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                    Console.WriteLine($"[DSP1-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2} (broad)");
                if (IsDataPort(adr))
                    _dsp1.WriteData(value);
                return true;
            }
        }

        if (!_dsp1IsHiRom)
        {
            if (_dsp1PortMapping.IsDspPort(bank, adr))
            {
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                    Console.WriteLine($"[DSP1-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
                if (IsDataPort(adr))
                    _dsp1.WriteData(value);
                return true;
            }
            return false;
        }

        if (((bank & 0x7f) <= 0x0f) && adr >= 0x6000 && adr < 0x7000)
        {
            if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                Console.WriteLine($"[DSP1-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
            _dsp1.WriteData(value);
            return true;
        }
        return false;
    }

    public void SetSystem(ISNESSystem system)
    {
        _system = system;
    }

    public void ResetCoprocessor()
    {
        _cx4?.Reset();
        _dsp1?.Reset();
        _superFx?.Reset();
        _sa1?.Reset();
    }

    public void RunCoprocessor(ulong snesCycles)
    {
        _cx4?.RunTo(snesCycles);
        _dsp1?.RunTo(snesCycles);
        _superFx?.Tick(snesCycles);
        _sa1?.Tick(snesCycles);
    }

    public void NotifyDmaStart(uint sourceAddress)
    {
        _sa1?.NotifyDmaStart(sourceAddress);
    }

    public void NotifyDmaEnd()
    {
        _sa1?.NotifyDmaEnd();
    }

    public bool IrqWanted => (_superFx?.Irq ?? false) || (_sa1?.SnesIrq() ?? false);
    public bool NmiWanted => _sa1?.SnesNmi() ?? false;

    public byte ReadRomByteLoRom(uint address)
    {
        if (_data.Length == 0)
            return 0;
        uint mapped = ((address & 0x7F0000) >> 1) | (address & 0x007FFF);
        uint mask = (uint)_data.Length - 1;
        if ((_data.Length & (_data.Length - 1)) == 0)
            return _data[mapped & mask];
        return _data[mapped % (uint)_data.Length];
    }

    private void SaveSRAM(object? state)
    {
        string fileName = GetSRAMFileName();
        try
        {
            File.WriteAllBytes(fileName, _sram);
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[SRAM] Save failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[SRAM] Save failed: {ex.Message}");
        }
        _sRAMTimer?.Dispose();
        _sRAMTimer = null;
    }

    private string GetSRAMFileName()
    {
        return Path.ChangeExtension(_system!.FileName, ".srm");
    }

    private static bool IsSa1Chipset(int chipsetByte)
    {
        return chipsetByte == 0x32 || chipsetByte == 0x34 || chipsetByte == 0x35;
    }

    private bool TryGetSnesPc(out int pc, out int op)
    {
        pc = 0;
        op = -1;
        if (_system is not KSNES.SNESSystem.SNESSystem snes)
            return false;
        if (snes.CPU is not KSNES.CPU.CPU cpu)
            return false;
        pc = cpu.ProgramCounter24;
        return true;
    }

    private bool TryGetSnesContext(out int pc, out string regs, out string opBytes)
    {
        pc = 0;
        regs = string.Empty;
        opBytes = string.Empty;
        if (_system is not KSNES.SNESSystem.SNESSystem snes)
            return false;
        if (snes.CPU is not KSNES.CPU.CPU cpu)
            return false;
        pc = cpu.ProgramCounter24;
        regs = cpu.GetTraceState();
        int b0 = snes.Peek(pc);
        int b1 = snes.Peek((pc + 1) & 0xFFFFFF);
        int b2 = snes.Peek((pc + 2) & 0xFFFFFF);
        int b3 = snes.Peek((pc + 3) & 0xFFFFFF);
        opBytes = $"{b0:X2} {b1:X2} {b2:X2} {b3:X2}";
        return true;
    }

    private bool ShouldTraceBwramContext(uint address, string region, uint? resolved)
    {
        if (!_traceSa1BwramWatch || !region.StartsWith("BW-RAM", StringComparison.Ordinal))
            return false;

        if (_traceSa1BwramOffsets.Count == 0)
            return true;

        uint adr16 = address & 0xFFFF;
        uint off16 = (resolved ?? 0) & 0xFFFF;
        return _traceSa1BwramOffsets.Contains(adr16) || _traceSa1BwramOffsets.Contains(off16);
    }

    private static HashSet<uint> ParseTraceOffsets(string? raw)
    {
        var offsets = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(raw))
            return offsets;

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (uint.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out uint value))
                offsets.Add(value);
        }

        return offsets;
    }

    private byte ReadHiRom(int bank, int adr)
    {
        uint address = (uint)(((bank & 0x7f) << 16) | (adr & 0xffff));
        uint mask = (uint)_data.Length - 1;
        if ((_data.Length & (_data.Length - 1)) == 0)
            return _data[address & mask];
        return _data[address % (uint)_data.Length];
    }

    private static bool IsHiRomSramBank(int bank)
    {
        int b = bank & 0x7f;
        return b >= 0x20 && b < 0x40;
    }

    private static byte[]? TryLoadDsp1Rom()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_ROM");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return File.ReadAllBytes(fromEnv);

        string defaultPath = "/home/nichlas/roms/DSP1 (World) (Enhancement Chip).bin";
        if (File.Exists(defaultPath))
            return File.ReadAllBytes(defaultPath);

        return null;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int limit) && limit > 0)
            return limit;
        return defaultValue;
    }

    private readonly struct Dsp1PortMapping
    {
        private readonly int _bankStart;
        private readonly int _bankEndExclusive;
        private readonly int _offsetMask;

        private Dsp1PortMapping(int bankStart, int bankEndExclusive, int offsetMask)
        {
            _bankStart = bankStart;
            _bankEndExclusive = bankEndExclusive;
            _offsetMask = offsetMask;
        }

        public static Dsp1PortMapping FromMetadata(int romSize, int sramSize)
        {
            if (romSize > 1024 * 1024)
                return new Dsp1PortMapping(0x60, 0x70, 0x0000);
            if (sramSize != 0)
                return new Dsp1PortMapping(0x20, 0x40, 0x8000);
            return new Dsp1PortMapping(0x30, 0x40, 0x8000);
        }

        public bool IsDspPort(int bank, int adr)
        {
            int bankMasked = bank & 0x7f;
            return bankMasked >= _bankStart && bankMasked < _bankEndExclusive && (adr & 0x8000) == _offsetMask;
        }
    }
}
