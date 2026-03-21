using System;

namespace KSNES.Specialchips.SuperFX;

internal sealed class SuperFx
{
    private static readonly bool TraceBus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_BUS"), "1", StringComparison.Ordinal);
    private static readonly bool AllowSnesRomReadWhileRunning =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SUPERFX_ALLOW_SNES_ROM_READ_WHILE_RUNNING"), "1", StringComparison.Ordinal);
    private static readonly int TraceBusLimit = 512;
    private static int _traceBusCount;

    [NonSerialized]
    private readonly byte[] _rom;
    private readonly byte[] _ram;
    private readonly GraphicsSupportUnit _gsu;
    private readonly ulong _overclockFactor;
    private ulong _lastSnesCycles;

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
                    if (!_gsu.IsRunning() || _gsu.RomAccess == BusAccess.Snes || allowSnesRomReadWhileRunning || AllowSnesRomReadWhileRunning)
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
                    if (!_gsu.IsRunning() || _gsu.RomAccess == BusAccess.Snes || allowSnesRomReadWhileRunning || AllowSnesRomReadWhileRunning)
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
                        value = _ram[address & 0x1FFF];
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
                        value = _ram[address & (_ram.Length - 1)];
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
                    _ram[address & 0x1FFF] = value;
                    wroteRam = true;
                }
                return true;
            case (>= 0x70 and <= 0x71, _):
            case (>= 0xF0 and <= 0xF1, _):
                if (!_gsu.IsRunning() || _gsu.RamAccess == BusAccess.Snes)
                {
                    _ram[address & (_ram.Length - 1)] = value;
                    wroteRam = true;
                }
                return true;
        }

        return false;
    }

    public void Tick(ulong masterCyclesElapsed)
    {
        if (masterCyclesElapsed <= _lastSnesCycles)
        {
            return;
        }
        ulong delta = masterCyclesElapsed - _lastSnesCycles;
        _lastSnesCycles = masterCyclesElapsed;
        _gsu.Tick(_overclockFactor * delta, _rom, _ram);
    }

    public bool Irq => _gsu.IrqAsserted();

    public void Reset()
    {
        _gsu.Reset();
        _lastSnesCycles = 0;
    }

    public void ResyncTo(ulong snesCycles)
    {
        _lastSnesCycles = snesCycles;
    }

    public byte[] Ram => _ram;

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
}
