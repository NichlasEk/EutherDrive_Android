using System;

namespace KSNES.Specialchips.SuperFX;

internal sealed class PlotState
{
    private readonly PixelBuffer _pixelBuffer = new();
    public byte LastCoarseX;
    public byte LastY;
    public byte FlushCyclesRemaining;
    public bool JustFlushed;

    public void Tick(byte gsuCycles)
    {
        if (JustFlushed)
        {
            JustFlushed = false;
        }
        else
        {
            FlushCyclesRemaining = (byte)Math.Max(0, FlushCyclesRemaining - gsuCycles);
        }
    }

    public PixelBuffer PixelBuffer => _pixelBuffer;
}

internal sealed class PixelBuffer
{
    private readonly byte[] _pixels = new byte[8];
    private byte _validBits;

    public void WritePixel(byte i, byte color)
    {
        _pixels[i] = color;
        _validBits |= (byte)(1 << i);
    }

    public bool IsValid(byte i) => ((_validBits >> i) & 1) != 0;
    public bool AnyValid() => _validBits != 0;
    public bool AllValid() => _validBits == 0xFF;
    public void ClearValid() => _validBits = 0;

    public byte GetPixel(byte i) => _pixels[i];
}
