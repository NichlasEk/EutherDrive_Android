using System;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdPcmStub
{
    private const int WaveformRamLen = 64 * 1024;
    private readonly byte[] _waveformRam = new byte[WaveformRamLen];

    public byte Read(uint address)
    {
        address &= 0x1FFF;
        if (address >= 0x1000)
            return _waveformRam[address & 0x0FFF];
        return 0x00;
    }

    public void Write(uint address, byte value)
    {
        address &= 0x1FFF;
        if (address >= 0x1000)
            _waveformRam[address & 0x0FFF] = value;
    }

    public void DmaWrite(uint address, byte value)
    {
        uint addr = address & 0x0FFF;
        _waveformRam[addr] = value;
    }
}
