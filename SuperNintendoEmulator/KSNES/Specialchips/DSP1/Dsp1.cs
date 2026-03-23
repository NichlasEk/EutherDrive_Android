using System.Runtime.CompilerServices;
using KSNES.SNESSystem;

namespace KSNES.Specialchips.DSP1;

public sealed class Dsp1
{
    private readonly Upd77c25 _cpu;
    private ulong _lastSnesCycles;

    public Dsp1(byte[] rom, ISNESSystem snes)
    {
        _cpu = new Upd77c25(rom, snes?.IsPal ?? false);
        _lastSnesCycles = 0;
    }

    public void Reset()
    {
        _cpu.Reset();
        _lastSnesCycles = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RunTo(ulong snesCycles)
    {
        if (snesCycles <= _lastSnesCycles)
            return;

        ulong delta = snesCycles - _lastSnesCycles;
        _lastSnesCycles = snesCycles;
        _cpu.Tick(delta);
    }

    public void ResyncTo(ulong snesCycles)
    {
        _lastSnesCycles = snesCycles;
        _cpu.PostLoadResync();
    }

    public byte ReadData() => _cpu.ReadData();
    public byte ReadStatus() => _cpu.ReadStatus();
    public void WriteData(byte value) => _cpu.WriteData(value);
    public void DumpRecentIo(string tag, int count) => _cpu.DumpRecentIo(tag, count);
    public void DumpRecentOpcodes(string tag, int count) => _cpu.DumpRecentOpcodes(tag, count);
    public int SnesWordWritesSeen => _cpu.SnesWordWritesSeen;
}
