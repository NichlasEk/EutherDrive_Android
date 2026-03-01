using System;
using KSNES.CPU;
using KSNES.SNESSystem;
using KSNES.PictureProcessing;
using KSNES.AudioProcessing;
using KSNES.Rendering;
using KSNES.ROM;
using KSNES.Tracing;

namespace KSNES.Specialchips.SA1;

public sealed class Sa1
{
    private const int IramLen = 2 * 1024;
    private const uint IramWatchOffset = 0x64E;
    private const uint BwramWatchOffset = 0x004E;

    [NonSerialized]
    private readonly byte[] _rom;
    private byte[] _iram = new byte[IramLen];
    private byte[] _bwram;
    private readonly CPU.CPU _cpu;
    private readonly Sa1Mmc _mmc = new();
    private readonly Sa1Registers _registers = new();
    private readonly Sa1Timer _timer;
    private ulong _bwramWaitCycles;
    private ulong _lastSnesCycles;
    [NonSerialized]
    private readonly Sa1System _system;
    private readonly bool _traceIramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_IRAM_WATCH"), "1", StringComparison.Ordinal);
    private readonly bool _traceBwramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal);
    private bool _inReset = true;
    private bool _lastSa1Reset;
    private bool _lastSa1Wait;
    private bool _lastSa1Nmi;

    public Sa1(byte[] rom, byte[] bwram, bool isPal)
    {
        _rom = rom;
        if (bwram.Length < 256 * 1024)
        {
            byte[] newBwram = new byte[256 * 1024];
            if (bwram.Length > 0)
                Buffer.BlockCopy(bwram, 0, newBwram, 0, bwram.Length);
            bwram = newBwram;
        }
        _bwram = bwram;
        _cpu = new CPU.CPU();
        _cpu.StartInNativeMode = true;
        _timer = new Sa1Timer(isPal);
        _system = new Sa1System(this, _cpu);
        _cpu.SetSystem(_system);
        _lastSa1Reset = _registers.Sa1Reset;
        _lastSa1Wait = _registers.Sa1Wait;
    }

    public byte[] Bwram => _bwram;

    public ICPU GetCpu() => _cpu;

    public static bool HasBattery(byte[] rom, int bwramLen)
    {
        if (bwramLen == 0)
            return false;
        if (rom.Length <= 0x7FD6)
            return false;
        byte chipset = rom[0x7FD6];
        return chipset == 0x32 || chipset == 0x35;
    }

    public void SetBwram(byte[] bwram)
    {
        _bwram = bwram.Length == 0 ? new byte[256 * 1024] : bwram;
    }

    public void Tick(ulong snesCycles)
    {
        if (snesCycles <= _lastSnesCycles)
            return;

        ulong delta = snesCycles - _lastSnesCycles;
        ulong sa1Cycles = delta / 2;
        _lastSnesCycles += sa1Cycles * 2;

        for (ulong i = 0; i < sa1Cycles; i++)
        {
            if (_registers.Sa1Reset)
            {
                _inReset = true;
            }
            else if (_inReset)
            {
                _cpu.StartInNativeMode = true;
                _cpu.Reset();
                _cpu.ProgramCounter = _registers.Sa1ResetVector;
                _cpu.ProgramBank = 0;
                _inReset = false;
                TraceState("RESET-RELEASE");
            }

            if (_registers.Sa1Wait)
            {
                bool irqPending = (_registers.Sa1IrqFromSnesEnabled && _registers.Sa1IrqFromSnes)
                                  || (_registers.TimerIrqEnabled && _timer.IrqPending)
                                  || (_registers.DmaIrqEnabled && _registers.Sa1DmaIrq);
                bool nmiPending = _registers.Sa1NmiEnabled && _registers.Sa1Nmi;
                
                if (irqPending || nmiPending)
                {
                    _registers.Sa1Wait = false;
                }
            }

            if (_registers.Sa1Reset != _lastSa1Reset)
            {
                _lastSa1Reset = _registers.Sa1Reset;
                TraceState(_registers.Sa1Reset ? "RESET-ASSERT" : "RESET-DEASSERT");
            }
            if (_registers.Sa1Wait != _lastSa1Wait)
            {
                _lastSa1Wait = _registers.Sa1Wait;
                TraceState(_registers.Sa1Wait ? "WAIT-ASSERT" : "WAIT-DEASSERT");
            }

            bool cpuHalted = _registers.CpuHalted();
            bool cpuActive = !cpuHalted && !_registers.Sa1Wait && !_registers.Sa1Reset;

            if (_bwramWaitCycles > 0)
            {
                _bwramWaitCycles--;
            }
            else if (cpuActive)
            {
                _cpu.IrqWanted = (_registers.Sa1IrqFromSnesEnabled && _registers.Sa1IrqFromSnes)
                                 || (_registers.TimerIrqEnabled && _timer.IrqPending)
                                 || (_registers.DmaIrqEnabled && _registers.Sa1DmaIrq);

                bool currentNmi = _registers.Sa1NmiEnabled && _registers.Sa1Nmi;
                if (currentNmi && !_lastSa1Nmi)
                {
                    _cpu.NmiWanted = true;
                }
                _lastSa1Nmi = currentNmi;

                _tracePc = _cpu.ProgramCounter24;
                _traceOp = TryGetSa1OpByte(_tracePc);
                _cpu.Cycle();
                _bwramWaitCycles += _system.BwramWaitCycles;
                _system.BwramWaitCycles = 0;
            }

            if (_registers.DmaState != DmaState.Idle)
            {
                _registers.TickDma(_mmc, _rom, _iram, _bwram);
            }

            _timer.Tick();
            if (_timer.IrqPending)
                _registers.SnesIrqFromTimer = true;
        }
    }

    public bool SnesIrq()
    {
        return (_registers.SnesIrqFromSa1Enabled && _registers.SnesIrqFromSa1)
               || (_registers.SnesIrqFromDmaEnabled && _registers.CharacterConversionIrq)
               || (_registers.SnesIrqFromTimerEnabled && _registers.SnesIrqFromTimer);
    }

    public bool SnesNmi()
    {
        return _registers.SnesNmiEnabled && _registers.SnesNmiFromSa1;
    }

    public void Reset()
    {
        _registers.Reset(_timer, _mmc);
        _bwramWaitCycles = 0;
        _lastSnesCycles = 0;
        _inReset = true;
        _lastSa1Reset = _registers.Sa1Reset;
        _lastSa1Wait = _registers.Sa1Wait;
        _lastSa1Nmi = false;
    }

    public void NotifyDmaStart(uint sourceAddress)
    {
        _registers.NotifySnesDmaStart(sourceAddress);
    }

    public void NotifyDmaEnd()
    {
        _registers.NotifySnesDmaEnd();
    }


    private int TryGetSa1OpByte(int pc)
    {
        uint address = (uint)pc & 0xFFFFFF;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        if ((bank <= 0x3F || (bank >= 0x80 && bank <= 0xBF)) && (offset <= 0x07FF || (offset >= 0x3000 && offset <= 0x37FF)))
        {
            return _iram[offset & 0x7FF];
        }
        uint? romAddr = _mmc.MapRomAddress(address);
        if (romAddr.HasValue && romAddr.Value < _rom.Length)
            return _rom[(int)romAddr.Value];
        return -1;
    }

    private int _tracePc;
    private int _traceOp;

    private void TraceSa1(string rw, uint address, byte value, string region, uint? resolved = null)
    {
        if (!Sa1Trace.IsEnabled)
            return;
        string? regs = Sa1Trace.IncludeRegsEnabled ? _cpu.GetTraceState() : null;
        Sa1Trace.Log("SA1", _tracePc, _traceOp, address, rw, value, region, resolved, regs);
    }

    private void TraceState(string state)
    {
        if (!Sa1Trace.IsEnabled)
            return;
        int pc = _cpu.ProgramCounter24;
        int op = TryGetSa1OpByte(pc);
        string? regs = Sa1Trace.IncludeRegsEnabled ? _cpu.GetTraceState() : null;
        Sa1Trace.Log("SA1", pc, op, 0, "S", 0, state, null, regs);
    }

    private void TraceIramWatch(string source, string rw, uint address, byte value, int pc)
    {
        if (!_traceIramWatch)
            return;
        uint offset = address & 0x7FF;
        if (offset != IramWatchOffset)
            return;
        Console.WriteLine($"[I-RAM-WATCH] src={source} rw={rw} addr=0x{address:X6} off=0x{offset:X3} val=0x{value:X2} pc=0x{pc:X6}");
    }

    private void TraceBwramWatch(string source, string rw, uint address, uint bwramAddr, byte value, int pc)
    {
        if (!_traceBwramWatch)
            return;
        uint offset = bwramAddr & 0xFFFF;
        uint adr16 = address & 0xFFFF;
        if (offset != 0x72A4 && offset != 0x604C && adr16 != 0x604C && adr16 != 0x604D && adr16 != 0x604E && adr16 != 0x604F && offset != 0x004C && offset != 0x004D)
            return;
        Console.WriteLine($"[BW-RAM-WATCH] src={source} rw={rw} addr=0x{address:X6} bwram=0x{bwramAddr:X6} val=0x{value:X2} pc=0x{pc:X6}");
    }

    public bool TryResolveSnesAccess(uint address, out string region, out uint? resolved)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x2200 and <= 0x22FF):
            case (>= 0x80 and <= 0xBF, >= 0x2200 and <= 0x22FF):
                region = "SA1-IO";
                resolved = null;
                return true;
            case (<= 0x3F, >= 0x2300 and <= 0x230F):
            case (>= 0x80 and <= 0xBF, >= 0x2300 and <= 0x230F):
                region = "SA1-IO";
                resolved = null;
                return true;
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                region = "I-RAM";
                resolved = (address & 0x7FF);
                return true;
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                region = "BW-RAM-WIN";
                resolved = (_mmc.SnesBwramBaseAddr + (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                return true;
            case (>= 0x40 and <= 0x5F, _):
                region = _registers.CcdmaTransferInProgress ? "CCDMA" : "BW-RAM";
                resolved = address & (uint)(_bwram.Length - 1);
                return true;
            case (<= 0x3F, >= 0x8000):
            case (>= 0x80 and <= 0xBF, >= 0x8000):
            case (>= 0xC0 and <= 0xFF, _):
                {
                    uint? romAddr = _mmc.MapRomAddress(address);
                    region = "ROM";
                    resolved = romAddr;
                    return romAddr.HasValue;
                }
        }
        region = "UNMAPPED";
        resolved = null;
        return false;
    }

    public void TraceSnesReadMirror(uint address, byte value, string region, uint? resolved)
    {
        if (!Sa1Trace.IsEnabled)
            return;
        int pc = _cpu.ProgramCounter24;
        int op = TryGetSa1OpByte(pc);
        string? regs = Sa1Trace.IncludeRegsEnabled ? _cpu.GetTraceState() : null;
        Sa1Trace.Log("SA1", pc, op, address, "R", value, region, resolved, regs);
    }

    public byte? SnesRead(uint address)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x8000):
            case (>= 0x80 and <= 0xBF, >= 0x8000):
            case (>= 0xC0 and <= 0xFF, _):
                {
                    InterruptVectorSource nmiSource = _registers.SnesNmiVectorSource;
                    InterruptVectorSource irqSource = _registers.SnesIrqVectorSource;
                    bool isVectorBank = bank == 0x00 || bank == 0x80;
                    if (isVectorBank && offset == 0xFFEA && nmiSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesNmiVector.Lsb();
                    if (isVectorBank && offset == 0xFFEB && nmiSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesNmiVector.Msb();
                    if (isVectorBank && offset == 0xFFEE && irqSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesIrqVector.Lsb();
                    if (isVectorBank && offset == 0xFFEF && irqSource == InterruptVectorSource.IoPorts)
                        return _registers.SnesIrqVector.Msb();

                    uint? romAddr = _mmc.MapRomAddress(address);
                    return romAddr.HasValue && romAddr.Value < _rom.Length ? _rom[(int)romAddr.Value] : (byte?)null;
                }
            case (<= 0x3F, >= 0x2300 and <= 0x230F):
            case (>= 0x80 and <= 0xBF, >= 0x2300 and <= 0x230F):
                return _registers.SnesRead(address, _timer, _mmc, _rom);
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                {
                    byte value = _iram[(int)(address & 0x7FF)];
                    TraceIramWatch("SNES", "R", address, value, -1);
                    return value;
                }
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                {
                    if (_registers.DmaEnabled && _registers.DmaType == DmaType.CharacterConversion && _registers.CharacterConversionType == CharacterConversionType.One && _registers.DmaState == DmaState.CharacterConversion1Active)
                        return _registers.NextCcdmaByte(_iram, _bwram);

                    uint bwramAddr = (_mmc.SnesBwramBaseAddr + (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                    byte value = _bwram[(int)bwramAddr];
                    TraceBwramWatch("SNES", "R", address, bwramAddr, value, -1);
                    return value;
                }
            case (>= 0x40 and <= 0x5F, _):
                if (_registers.DmaEnabled && _registers.DmaType == DmaType.CharacterConversion && _registers.CharacterConversionType == CharacterConversionType.One && _registers.DmaState == DmaState.CharacterConversion1Active)
                    return _registers.NextCcdmaByte(_iram, _bwram);
                else
                {
                    uint bwramAddr = address & (uint)(_bwram.Length - 1);
                    byte value = _bwram[(int)bwramAddr];
                    TraceBwramWatch("SNES", "R", address, bwramAddr, value, -1);
                    return value;
                }
        }

        return null;
    }

    public void SnesWrite(uint address, byte value)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x2200 and <= 0x22FF):
            case (>= 0x80 and <= 0xBF, >= 0x2200 and <= 0x22FF):
                _registers.SnesWrite(address, value, _mmc, _rom, _iram);
                break;
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                {
                    uint iramAddr = address & 0x7FF;
                    _registers.LogReg($"[KIRBY-DEBUG] SNES W IRAM 0x{iramAddr+0x3000:X4} val=0x{value:X2}");
                    int writeProtectIdx = (int)(iramAddr >> 8);
                    if (_registers.SnesIramWritesEnabled[writeProtectIdx])
                        _iram[(int)iramAddr] = value;
                    TraceIramWatch("SNES", "W", address, value, -1);
                    break;
                }
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                {
                    uint bwramAddr = (_mmc.SnesBwramBaseAddr + (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                    if (bwramAddr == 0x72A4)
                        _registers.LogReg($"[KIRBY-DEBUG] SNES W BWRAM 0x72A4 val=0x{value:X2}");
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: true))
                    {
                        _bwram[(int)bwramAddr] = value;
                        TraceBwramWatch("SNES", "W", address, bwramAddr, value, -1);
                    }
                    else
                    {
                        TraceBwramWatch("SNES", "W(BLOCKED)", address, bwramAddr, value, -1);
                    }
                    break;
                }
            case (>= 0x40 and <= 0x5F, _):
                {
                    uint bwramAddr = address & (uint)(_bwram.Length - 1);
                    if (bwramAddr == 0x72A4)
                        _registers.LogReg($"[KIRBY-DEBUG] SNES W BWRAM 0x72A4 val=0x{value:X2}");
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: true))
                    {
                        _bwram[(int)bwramAddr] = value;
                        TraceBwramWatch("SNES", "W", address, bwramAddr, value, -1);
                    }
                    else
                    {
                        TraceBwramWatch("SNES", "W(BLOCKED)", address, bwramAddr, value, -1);
                    }
                    break;
                }
        }
    }

    internal byte ReadSa1Cpu(uint address, out bool bwramWait)
    {
        bwramWait = false;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x8000):
            case (>= 0x80 and <= 0xBF, >= 0x8000):
            case (>= 0xC0 and <= 0xFF, _):
                if (bank == 0x00 && offset == 0xFFEA)
                {
                    byte value = _registers.Sa1NmiVector.Lsb();
                    TraceSa1("R", address, value, "SA1-VEC");
                    return value;
                }
                if (bank == 0x00 && offset == 0xFFEB)
                {
                    byte value = _registers.Sa1NmiVector.Msb();
                    TraceSa1("R", address, value, "SA1-VEC");
                    return value;
                }
                if (bank == 0x00 && offset == 0xFFEE)
                {
                    byte value = _registers.Sa1IrqVector.Lsb();
                    TraceSa1("R", address, value, "SA1-VEC");
                    return value;
                }
                if (bank == 0x00 && offset == 0xFFEF)
                {
                    byte value = _registers.Sa1IrqVector.Msb();
                    TraceSa1("R", address, value, "SA1-VEC");
                    return value;
                }
                if (bank == 0x00 && offset == 0xFFFC)
                {
                    byte value = _registers.Sa1ResetVector.Lsb();
                    TraceSa1("R", address, value, "SA1-VEC");
                    return value;
                }
                if (bank == 0x00 && offset == 0xFFFD)
                {
                    byte value = _registers.Sa1ResetVector.Msb();
                    TraceSa1("R", address, value, "SA1-VEC");
                    return value;
                }
                {
                    uint? romAddr = _mmc.MapRomAddress(address);
                    byte value = romAddr.HasValue && romAddr.Value < _rom.Length ? _rom[(int)romAddr.Value] : (byte)0;
                    TraceSa1("R", address, value, "ROM", romAddr);
                    return value;
                }
            case (<= 0x3F, >= 0x2300 and <= 0x230F):
            case (>= 0x80 and <= 0xBF, >= 0x2300 and <= 0x230F):
                {
                    byte value = _registers.Sa1Read(address, _timer, _mmc, _rom);
                    TraceSa1("R", address, value, "SA1-IO");
                    return value;
                }
            case (<= 0x3F, >= 0x0000 and <= 0x07FF):
            case (>= 0x80 and <= 0xBF, >= 0x0000 and <= 0x07FF):
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                {
                    byte value = _iram[(int)(address & 0x7FF)];
                    TraceSa1("R", address, value, "I-RAM", address & 0x7FF);
                    TraceIramWatch("SA1", "R", address, value, _cpu.ProgramCounter24);
                    return value;
                }
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                bwramWait = true;
                if (_mmc.Sa1BwramSource == BwramMapSource.Normal)
                {
                    uint bwramAddr = (_mmc.Sa1BwramBaseAddr + (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                    byte value = _bwram[(int)bwramAddr];
                    TraceSa1("R", address, value, "BW-RAM-WIN", bwramAddr);
                    TraceBwramWatch("SA1", "R", address, bwramAddr, value, _cpu.ProgramCounter24);
                    return value;
                }
                {
                    uint bitmapAddr = _mmc.Sa1BwramBaseAddr | (address & 0x1FFF);
                    byte value = ReadBwramBitmap(bitmapAddr);
                    TraceSa1("R", address, value, "BW-RAM-BITMAP", bitmapAddr);
                    return value;
                }
            case (>= 0x40 and <= 0x5F, _):
                bwramWait = true;
                {
                    uint bwramAddr = address & (uint)(_bwram.Length - 1);
                    byte value = _bwram[(int)bwramAddr];
                    TraceSa1("R", address, value, "BW-RAM", bwramAddr);
                    TraceBwramWatch("SA1", "R", address, bwramAddr, value, _cpu.ProgramCounter24);
                    return value;
                }
            case (>= 0x60 and <= 0x6F, _):
                bwramWait = true;
                {
                    byte value = ReadBwramBitmap(address);
                    TraceSa1("R", address, value, "BW-RAM-BITMAP", address);
                    return value;
                }
        }

        TraceSa1("R", address, 0x00, "UNMAPPED");
        return 0;
    }

    internal void WriteSa1Cpu(uint address, byte value, out bool bwramWait)
    {
        bwramWait = false;
        bool handled = false;
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        switch (bank, offset)
        {
            case (<= 0x3F, >= 0x2200 and <= 0x22FF):
            case (>= 0x80 and <= 0xBF, >= 0x2200 and <= 0x22FF):
                _registers.Sa1Write(address, value, _timer, _mmc, _rom, _iram);
                TraceSa1("W", address, value, "SA1-IO");
                handled = true;
                break;
            case (<= 0x3F, >= 0x0000 and <= 0x07FF):
            case (>= 0x80 and <= 0xBF, >= 0x0000 and <= 0x07FF):
            case (<= 0x3F, >= 0x3000 and <= 0x37FF):
            case (>= 0x80 and <= 0xBF, >= 0x3000 and <= 0x37FF):
                {
                    uint iramAddr = address & 0x7FF;
                    if (iramAddr == 0)
                        _registers.LogReg($"[KIRBY-DEBUG] SA1 W IRAM 0x3000 val=0x{value:X2} PC=0x{(_cpu.ProgramBank << 16) | _cpu.ProgramCounter:X6}");
                    int writeProtectIdx = (int)(iramAddr >> 8);
                    if (_registers.Sa1IramWritesEnabled[writeProtectIdx])
                        _iram[(int)iramAddr] = value;
                    TraceSa1("W", address, value, "I-RAM", address & 0x7FF);
                    TraceIramWatch("SA1", "W", address, value, (_cpu.ProgramBank << 16) | _cpu.ProgramCounter);
                    handled = true;
                    break;
                }
            case (<= 0x3F, >= 0x6000 and <= 0x7FFF):
            case (>= 0x80 and <= 0xBF, >= 0x6000 and <= 0x7FFF):
                bwramWait = true;
                if (_mmc.Sa1BwramSource == BwramMapSource.Normal)
                {
                    uint bwramAddr = (_mmc.Sa1BwramBaseAddr + (address & 0x1FFF)) & (uint)(_bwram.Length - 1);
                    if (bwramAddr == 0x72A4)
                        _registers.LogReg($"[KIRBY-DEBUG] SA1 W BWRAM 0x72A4 val=0x{value:X2} PC=0x{(_cpu.ProgramBank << 16) | _cpu.ProgramCounter:X6}");
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: false))
                        _bwram[(int)bwramAddr] = value;
                    TraceSa1("W", address, value, "BW-RAM-WIN", bwramAddr);
                    TraceBwramWatch("SA1", "W", address, bwramAddr, value, (_cpu.ProgramBank << 16) | _cpu.ProgramCounter);
                }
                else
                {
                    WriteBwramBitmap(_mmc.Sa1BwramBaseAddr + (address & 0x1FFF), value);
                    TraceSa1("W", address, value, "BW-RAM-BITMAP", _mmc.Sa1BwramBaseAddr + (address & 0x1FFF));
                }
                handled = true;
                break;
            case (>= 0x40 and <= 0x5F, _):
                bwramWait = true;
                {
                    uint bwramAddr = address & (uint)(_bwram.Length - 1);
                    if (bwramAddr == 0x72A4)
                        _registers.LogReg($"[KIRBY-DEBUG] SA1 W BWRAM 0x72A4 val=0x{value:X2} PC=0x{(_cpu.ProgramBank << 16) | _cpu.ProgramCounter:X6}");
                    if (_registers.CanWriteBwram(bwramAddr, isSnes: false))
                        _bwram[(int)bwramAddr] = value;
                    TraceSa1("W", address, value, "BW-RAM", bwramAddr);
                    TraceBwramWatch("SA1", "W", address, bwramAddr, value, (_cpu.ProgramBank << 16) | _cpu.ProgramCounter);
                }
                handled = true;
                break;
            case (>= 0x60 and <= 0x6F, _):
                bwramWait = true;
                WriteBwramBitmap(address, value);
                TraceSa1("W", address, value, "BW-RAM-BITMAP", address);
                handled = true;
                break;
        }

        if (!handled)
            TraceSa1("W", address, value, "UNMAPPED");
    }

    private byte ReadBwramBitmap(uint address)
    {
        if (_mmc.BwramBitmapFormat == BwramBitmapBits.Two)
        {
            uint addr = address & 0xFFFFF;
            uint bwramAddr = (addr >> 2) & (uint)(_bwram.Length - 1);
            int shift = (int)(2 * (addr & 0x03));
            return (byte)((_bwram[(int)bwramAddr] >> shift) & 0x03);
        }

        uint addr4 = address & 0x7FFFF;
        uint bwramAddr4 = (addr4 >> 1) & (uint)(_bwram.Length - 1);
        int shift4 = (int)(4 * (addr4 & 0x01));
        return (byte)((_bwram[(int)bwramAddr4] >> shift4) & 0x0F);
    }

    private void WriteBwramBitmap(uint address, byte value)
    {
        if (_mmc.BwramBitmapFormat == BwramBitmapBits.Two)
        {
            uint addr = address & 0xFFFFF;
            uint bwramAddr = (addr >> 2) & (uint)(_bwram.Length - 1);
            int shift = (int)(2 * (addr & 0x03));
            byte existing = _bwram[(int)bwramAddr];
            byte newValue = (byte)((existing & ~(0x03 << shift)) | ((value & 0x03) << shift));
            _bwram[(int)bwramAddr] = newValue;
        }
        else
        {
            uint addr = address & 0x7FFFF;
            uint bwramAddr = (addr >> 1) & (uint)(_bwram.Length - 1);
            int shift = (int)(4 * (addr & 0x01));
            byte existing = _bwram[(int)bwramAddr];
            byte newValue = (byte)((existing & ~(0x0F << shift)) | ((value & 0x0F) << shift));
            _bwram[(int)bwramAddr] = newValue;
        }
    }

    private sealed class Sa1System : ISNESSystem
    {
        private readonly Sa1 _sa1;
        private readonly ICPU _cpu;
        private readonly IPPU _ppu = new NullPpu();
        private readonly IAPU _apu = new NullApu();
        private readonly IROM _rom = new NullRom();

        public Sa1System(Sa1 sa1, ICPU cpu)
        {
            _sa1 = sa1;
            _cpu = cpu;
        }

        public ulong BwramWaitCycles;

        public int Read(int addr, bool dma = false)
        {
            uint address = (uint)addr & 0xFFFFFF;
            byte value = _sa1.ReadSa1Cpu(address, out bool bwramWait);
            if (bwramWait)
                BwramWaitCycles++;
            return value;
        }

        public void Write(int addr, int value, bool dma = false)
        {
            uint address = (uint)addr & 0xFFFFFF;
            _sa1.WriteSa1Cpu(address, (byte)value, out bool bwramWait);
            if (bwramWait)
                BwramWaitCycles++;
        }

        public ISNESSystem Merge(ISNESSystem system) => this;
        public string FileName { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public ICPU CPU => _cpu;
        public IPPU PPU => _ppu;
        public IAPU APU => _apu;
        public IROM ROM { get => _rom; set { } }
        public void StopEmulation() { }
        public void ResumeEmulation() { }
        public void SetKeyDown(SNESButton button) { }
        public void SetKeyUp(SNESButton button) { }
        public void LoadROM(string fileName) { }
        public void Run() { }
        public IRenderer Renderer { get; set; } = new NullRenderer();
        public IAudioHandler AudioHandler { get; set; } = new NullAudioHandler();
        public bool IsRunning() => true;
        public event EventHandler? FrameRendered;
        public bool PPULatch => false;
        public int OpenBus => 0;
        public int XPos => 0;
        public int YPos => 0;
        public bool IsPal { get; set; }

        private sealed class NullPpu : IPPU
        {
            public void CheckOverscan(int line) { }
            public void RenderLine(int line) { }
            public int Read(int adr) => 0;
            public void Write(int adr, int value) { }
            public int[] GetPixels() => Array.Empty<int>();
            public bool FrameOverscan => false;
            public int LatchedHpos { get; set; }
            public int LatchedVpos { get; set; }
            public bool CountersLatched { get; set; }
            public void Reset() { }
            public void SetSystem(ISNESSystem system) { }
        }

        private sealed class NullApu : IAPU
        {
            public byte[] RAM { get; } = new byte[0];
            public void Attach() { }
            public void Cycle() { }
            public void Write(int adr, byte value) { }
            public byte Read(int adr) => 0;
            public byte[] SpcWritePorts { get; } = new byte[4];
            public byte[] SpcReadPorts { get; set; } = new byte[4];
            public void Reset() { }
            public void SetSamples(float[] left, float[] right) { }
        }

        private sealed class NullRenderer : IRenderer
        {
            public void RenderBuffer(int[] buffer) { }
            public void SetTargetControl(IHasWidthAndHeight box) { }
        }

        private sealed class NullAudioHandler : IAudioHandler
        {
            public float[] SampleBufferL { get; set; } = Array.Empty<float>();
            public float[] SampleBufferR { get; set; } = Array.Empty<float>();
            public void NextBuffer() { }
            public void Pauze() { }
            public void Resume() { }
        }

        private sealed class NullRom : IROM
        {
            public byte Read(int bank, int adr) => 0;
            public void Write(int bank, int adr, byte value) { }
            public Header Header { get; } = new Header { Name = string.Empty };
            public int RomLength => 0;
            public void LoadROM(byte[] data, Header header) { }
            public void LoadSRAM() { }
            public void ResetCoprocessor() { }
            public void RunCoprocessor(ulong snesCycles) { }
            public byte ReadRomByteLoRom(uint address) => 0;
            public bool IrqWanted => false;
            public bool NmiWanted => false;
            public void SetSystem(ISNESSystem system) { }
            public void NotifyDmaStart(uint sourceAddress) { }
            public void NotifyDmaEnd() { }
            public object? Sa1 => null;
        }
    }
}
