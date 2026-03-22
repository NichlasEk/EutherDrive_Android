using System;
using System.Diagnostics;

namespace KSNES.Specialchips.SuperFX;

internal sealed class SuperFx
{
    private static readonly bool TraceBus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_BUS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceRamWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_RAM_WATCH"), "1", StringComparison.Ordinal);
    private static readonly int TraceRamWatchLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_SUPERFX_RAM_WATCH_LIMIT", 1024);
    private static readonly int[] TraceRamWatchAddresses =
        ParseTraceAddresses(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_RAM_WATCH_ADDRS"));
    private static readonly bool AllowSnesRomReadWhileRunning =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SUPERFX_ALLOW_SNES_ROM_READ_WHILE_RUNNING"), "1", StringComparison.Ordinal);
    private static readonly bool AllowSnesRomReadWhileWaitingOnRam =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SUPERFX_ALLOW_SNES_ROM_READ_WHILE_WAITING_ON_RAM"), "1", StringComparison.Ordinal);
    private static readonly bool TraceState =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_STATE"), "1", StringComparison.Ordinal);
    private static readonly int TraceBusLimit = 512;
    private static int _traceBusCount;
    private static int _traceRamWatchCount;

    [NonSerialized]
    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly GraphicsSupportUnit _gsu;
    private readonly ulong _overclockFactor;
    private ulong _lastSnesCycles;
    internal ulong PerfTickCalls;
    internal ulong PerfTickCycles;
    internal long PerfTickTicks;

    public SuperFx(byte[] rom, byte[] ram, ulong overclockFactor)
    {
        _rom = rom;
        _ram = ram;
        _gsu = new GraphicsSupportUnit();
        _overclockFactor = Math.Max(1UL, overclockFactor);
        _lastSnesCycles = 0;
    }

    public bool Read(uint address, out byte value, bool allowSnesRomReadWhileRunning = false)
    {
        byte bank = (byte)(address >> 16);
        ushort offset = (ushort)(address & 0xFFFF);

        switch (bank, offset)
        {
            case (>= 0x00 and <= 0x3F, >= 0x3000 and <= 0x30FF):
            case (>= 0x00 and <= 0x3F, >= 0x3300 and <= 0x34FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x30FF):
            case (>= 0x80 and <= 0xBF, >= 0x3300 and <= 0x34FF):
                {
                    byte? reg = _gsu.ReadRegister(address);
                    if (reg.HasValue)
                    {
                        value = reg.Value;
                        return true;
                    }
                    value = 0;
                    return false;
                }
            case (>= 0x00 and <= 0x3F, >= 0x3100 and <= 0x32FF):
            case (>= 0x80 and <= 0xBF, >= 0x3100 and <= 0x32FF):
                {
                    byte? ram = _gsu.ReadCodeCacheRam(address);
                    if (ram.HasValue)
                    {
                        value = ram.Value;
                        return true;
                    }
                    value = 0;
                    return false;
                }
            case (>= 0x00 and <= 0x3F, >= 0x8000 and <= 0xFFFF):
            case (>= 0x80 and <= 0xBF, >= 0x8000 and <= 0xFFFF):
                {
                    if (CanSnesReadRom(allowSnesRomReadWhileRunning))
                    {
                        uint romAddr = MapLoRomAddress(address, (uint)_rom.Length);
                        value = _rom[romAddr % (uint)_rom.Length];
                        return true;
                    }
                    if (FixedInterruptVector(address, out byte vec))
                    {
                        value = vec;
                        return true;
                    }
                    TraceBlockedBus("ROM", address);
                    value = 0;
                    return false;
                }
            case (>= 0x40 and <= 0x5F, _):
            case (>= 0xC0 and <= 0xDF, _):
                {
                    if (CanSnesReadRom(allowSnesRomReadWhileRunning))
                    {
                        uint romAddr = MapHiRomAddress(address, (uint)_rom.Length);
                        value = _rom[romAddr % (uint)_rom.Length];
                        return true;
                    }
                    if (FixedInterruptVector(address, out byte vec))
                    {
                        value = vec;
                        return true;
                    }
                    TraceBlockedBus("ROM", address);
                    value = 0;
                    return false;
                }
            case (>= 0x00 and <= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                {
                    if (!_gsu.IsRunning() || _gsu.RamAccess == BusAccess.Snes)
                    {
                        int ramAddr = (int)(address & 0x1FFF);
                        value = _ram[ramAddr];
                        TraceWatchedRamAccess("SNES-RD", address, ramAddr, value, 1);
                        return true;
                    }
                    TraceBlockedBus("RAM", address);
                    value = 0;
                    return false;
                }
            case (>= 0x70 and <= 0x71, _):
            case (>= 0xF0 and <= 0xF1, _):
                {
                    if (!_gsu.IsRunning() || _gsu.RamAccess == BusAccess.Snes)
                    {
                        int ramAddr = (int)(address & (uint)(_ram.Length - 1));
                        value = _ram[ramAddr];
                        TraceWatchedRamAccess("SNES-RD", address, ramAddr, value, 1);
                        return true;
                    }
                    TraceBlockedBus("RAM", address);
                    value = 0;
                    return false;
                }
        }

        value = 0;
        return false;
    }

    public bool Write(uint address, byte value, out bool wroteRam)
    {
        wroteRam = false;
        byte bank = (byte)(address >> 16);
        ushort offset = (ushort)(address & 0xFFFF);

        switch (bank, offset)
        {
            case (>= 0x00 and <= 0x3F, >= 0x3000 and <= 0x30FF):
            case (>= 0x00 and <= 0x3F, >= 0x3300 and <= 0x34FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x30FF):
            case (>= 0x80 and <= 0xBF, >= 0x3300 and <= 0x34FF):
                _gsu.WriteRegister(address, value);
                return true;
            case (>= 0x00 and <= 0x3F, >= 0x3100 and <= 0x32FF):
            case (>= 0x80 and <= 0xBF, >= 0x3100 and <= 0x32FF):
                _gsu.WriteCodeCacheRam(address, value);
                return true;
            case (>= 0x00 and <= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                if (!_gsu.IsRunning() || _gsu.RamAccess == BusAccess.Snes)
                {
                    int ramAddr = (int)(address & 0x1FFF);
                    _ram[ramAddr] = value;
                    TraceWatchedRamAccess("SNES-WR", address, ramAddr, value, 1);
                    wroteRam = true;
                }
                return true;
            case (>= 0x70 and <= 0x71, _):
            case (>= 0xF0 and <= 0xF1, _):
                if (!_gsu.IsRunning() || _gsu.RamAccess == BusAccess.Snes)
                {
                    int ramAddr = (int)(address & (uint)(_ram.Length - 1));
                    _ram[ramAddr] = value;
                    TraceWatchedRamAccess("SNES-WR", address, ramAddr, value, 1);
                    wroteRam = true;
                }
                return true;
        }

        return false;
    }

    public void Tick(ulong masterCyclesElapsed)
    {
        long startTicks = Stopwatch.GetTimestamp();
        PerfTickCalls++;
        if (masterCyclesElapsed <= _lastSnesCycles)
        {
            PerfTickTicks += Stopwatch.GetTimestamp() - startTicks;
            return;
        }
        ulong delta = masterCyclesElapsed - _lastSnesCycles;
        PerfTickCycles += delta;
        _lastSnesCycles = masterCyclesElapsed;
        _gsu.Tick(_overclockFactor * delta, _rom, _ram);
        PerfTickTicks += Stopwatch.GetTimestamp() - startTicks;
    }

    public bool Irq => _gsu.IrqAsserted();
    public bool IsRunning => _gsu.IsRunning();

    public void Reset()
    {
        _gsu.Reset();
        _lastSnesCycles = 0;
        ResetPerfCounters();
    }

    public void ResyncTo(ulong snesCycles)
    {
        _lastSnesCycles = snesCycles;
    }

    internal void ResetPerfCounters()
    {
        PerfTickCalls = 0;
        PerfTickCycles = 0;
        PerfTickTicks = 0;
    }

    public byte[] Ram => _ram;

    public string GetDivergenceSummary()
    {
        WaitKind waitKind = Instructions.GetWaitKind(_gsu);
        MemoryType nextType = Instructions.NextOpcodeMemoryType(_gsu);
        ulong ramHash = ComputeHash(_ram);
        string summary = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"sfx(go={(_gsu.IsRunning() ? 1 : 0)} irq={(_gsu.IrqAsserted() ? 1 : 0)} stop={_gsu.StopState} " +
            $"pbr=0x{_gsu.Pbr:X2} rombr=0x{_gsu.Rombr:X2} r15=0x{_gsu.R[15]:X4} op=0x{_gsu.State.OpcodeBuffer:X2} " +
            $"next={nextType} wait={waitKind} rom={_gsu.RomAccess} ram={_gsu.RamAccess} alt1={(_gsu.Alt1 ? 1 : 0)} alt2={(_gsu.Alt2 ? 1 : 0)} " +
            $"justJumped={(_gsu.State.JustJumped ? 1 : 0)} cbr=0x{_gsu.CodeCache.Cbr:X4} lines=0x{_gsu.CodeCache.CachedLines:X8} sfxRam=0x{ramHash:X16})");

        if (!TraceState)
        {
            return summary;
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{summary} regs(r0=0x{_gsu.R[0]:X4} r1=0x{_gsu.R[1]:X4} r2=0x{_gsu.R[2]:X4} r3=0x{_gsu.R[3]:X4} " +
            $"r4=0x{_gsu.R[4]:X4} r5=0x{_gsu.R[5]:X4} r6=0x{_gsu.R[6]:X4} r7=0x{_gsu.R[7]:X4} " +
            $"r8=0x{_gsu.R[8]:X4} r9=0x{_gsu.R[9]:X4} r10=0x{_gsu.R[10]:X4} r11=0x{_gsu.R[11]:X4} " +
            $"r12=0x{_gsu.R[12]:X4} r13=0x{_gsu.R[13]:X4} r14=0x{_gsu.R[14]:X4} z={(_gsu.ZeroFlag ? 1 : 0)} " +
            $"c={(_gsu.CarryFlag ? 1 : 0)} s={(_gsu.SignFlag ? 1 : 0)} ov={(_gsu.OverflowFlag ? 1 : 0)} " +
            $"romWait={_gsu.State.RomBufferWaitCycles} ramWait={_gsu.State.RamBufferWaitCycles} " +
            $"ramAddr=0x{_gsu.State.RamAddressBuffer:X4} romBuf=0x{_gsu.State.RomBuffer:X2} waitCycles={_gsu.WaitCycles})");
    }

    private void TraceBlockedBus(string kind, uint address)
    {
        if (!TraceBus || _traceBusCount >= TraceBusLimit)
            return;

        _traceBusCount++;
        MemoryType nextType = Instructions.NextOpcodeMemoryType(_gsu);
        byte nextOpcode = _gsu.State.OpcodeBuffer;
        Console.WriteLine(
            $"[SFX-BUS-BLOCK] kind={kind} addr=0x{address:X6} go={(_gsu.IsRunning() ? 1 : 0)} " +
            $"rom={_gsu.RomAccess} ram={_gsu.RamAccess} pbr=0x{_gsu.Pbr:X2} rombr=0x{_gsu.Rombr:X2} r15=0x{_gsu.R[15]:X4} " +
            $"nextType={nextType} nextOp=0x{nextOpcode:X2} justJumped={(_gsu.State.JustJumped ? 1 : 0)}");
    }

    private void TraceWatchedRamAccess(string kind, uint address, int ramAddr, byte value, int width)
    {
        if (!TraceRamWatch || _traceRamWatchCount >= TraceRamWatchLimit || !TouchesWatchedRamAddress(ramAddr, width))
            return;

        _traceRamWatchCount++;
        string valueText = width == 1 ? $"0x{value:X2}" : $"0x{value:X4}";
        Console.WriteLine(
            $"[SFX-RAM] side=SNES kind={kind} snes=0x{address:X6} addr=0x{ramAddr:X4} width={width} val={valueText} " +
            $"go={(_gsu.IsRunning() ? 1 : 0)} rom={_gsu.RomAccess} ram={_gsu.RamAccess} pbr=0x{_gsu.Pbr:X2} rombr=0x{_gsu.Rombr:X2} r15=0x{_gsu.R[15]:X4}");
    }

    private bool CanSnesReadRom(bool allowSnesRomReadWhileRunning)
    {
        if (!_gsu.IsRunning() || _gsu.RomAccess == BusAccess.Snes || allowSnesRomReadWhileRunning || AllowSnesRomReadWhileRunning)
            return true;

        return AllowSnesRomReadWhileWaitingOnRam && Instructions.GetWaitKind(_gsu) == WaitKind.Ram;
    }

    private static bool TouchesWatchedRamAddress(int ramAddr, int width)
    {
        if (TraceRamWatchAddresses.Length == 0)
            return false;

        foreach (int watchAddr in TraceRamWatchAddresses)
        {
            if (ramAddr == watchAddr)
                return true;
            if (width > 1 && (ramAddr ^ 1) == watchAddr)
                return true;
        }

        return false;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static int[] ParseTraceAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<int>();

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new int[parts.Length];
        int count = 0;
        foreach (string part in parts)
        {
            string normalized = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (int.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out int parsed))
                result[count++] = parsed & 0xFFFF;
        }

        if (count == result.Length)
            return result;

        Array.Resize(ref result, count);
        return result;
    }

    public static uint MapLoRomAddress(uint address, uint romLen)
    {
        uint romAddr = (address & 0x7FFF) | ((address & 0x7F0000) >> 1);
        return romAddr & (romLen - 1);
    }

    public static uint MapHiRomAddress(uint address, uint romLen)
    {
        uint romAddr = address & 0x3FFFFF;
        return romAddr & (romLen - 1);
    }

    private const ushort SfxCopVector = 0x0104;
    private const ushort SfxBrkVector = 0x0100;
    private const ushort SfxAbortVector = 0x0100;
    private const ushort SfxNmiVector = 0x0108;
    private const ushort SfxIrqVector = 0x010C;

    private static bool FixedInterruptVector(uint address, out byte value)
    {
        switch (address & 0xF)
        {
            case 0x4: value = BitUtils.Lsb(SfxCopVector); return true;
            case 0x5: value = BitUtils.Msb(SfxCopVector); return true;
            case 0x6: value = BitUtils.Lsb(SfxBrkVector); return true;
            case 0x7: value = BitUtils.Msb(SfxBrkVector); return true;
            case 0x8: value = BitUtils.Lsb(SfxAbortVector); return true;
            case 0x9: value = BitUtils.Msb(SfxAbortVector); return true;
            case 0xA: value = BitUtils.Lsb(SfxNmiVector); return true;
            case 0xB: value = BitUtils.Msb(SfxNmiVector); return true;
            case 0xE: value = BitUtils.Lsb(SfxIrqVector); return true;
            case 0xF: value = BitUtils.Msb(SfxIrqVector); return true;
            default: value = 0; return false;
        }
    }

    public static int GuessRamLen(byte[] rom)
    {
        if (rom.Length > 0x7FBD && rom[0x7FDA] == 0x33 && rom[0x7FBD] == 0x06)
            return 64 * 1024;
        if (rom.Length > 0x7FD2)
        {
            bool voxels = true;
            byte[] tag = System.Text.Encoding.ASCII.GetBytes("Voxels in progress");
            for (int i = 0; i < tag.Length; i++)
            {
                if (rom[0x7FC0 + i] != tag[i]) { voxels = false; break; }
            }
            if (voxels)
                return 64 * 1024;
        }
        return 32 * 1024;
    }

    public static bool HasBattery(byte[] rom)
    {
        byte chipsetByte = rom[0x7FD6];
        return chipsetByte == 0x15 || chipsetByte == 0x1A;
    }

    private static ulong ComputeHash(byte[] data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }
}
