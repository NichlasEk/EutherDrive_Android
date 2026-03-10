using KSNES.SNESSystem;

namespace KSNES.Specialchips.ST010;

public sealed class St010
{
    private readonly Upd77c25 _cpu;
    private ulong _lastSnesCycles;

    public St010(byte[] rom, byte[] sram, ISNESSystem snes)
    {
        _cpu = new Upd77c25(rom, sram, snes?.IsPal ?? false, isSt010: true);
        _lastSnesCycles = 0;
    }

    public void Reset()
    {
        _cpu.Reset();
        _lastSnesCycles = 0;
    }

    public void RunTo(ulong snesCycles)
    {
        if (snesCycles <= _lastSnesCycles)
            return;

        ulong delta = snesCycles - _lastSnesCycles;
        _lastSnesCycles = snesCycles;
        _cpu.Tick(delta);
    }

    public byte ReadData() => _cpu.ReadData();
    public byte ReadStatus() => _cpu.ReadStatus();
    public void WriteData(byte value) => _cpu.WriteData(value);
    
    public byte ReadRam(uint address) => _cpu.ReadRam(address);
    public void WriteRam(uint address, byte value) => _cpu.WriteRam(address, value);
    
    public byte[] GetSram() => _cpu.GetSram();
}