using System;
using System.Runtime.CompilerServices;

namespace KSNES.Specialchips.SuperFX;

internal sealed class PlotState
{
    private readonly PixelBuffer _pixelBuffer = new();
    public byte LastCoarseX;
    public byte LastY;
    public byte FlushCyclesRemaining;
    public bool JustFlushed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePixel(byte i, byte color)
    {
        _pixels[i] = color;
        _validBits |= (byte)(1 << i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsValid(byte i) => ((_validBits >> i) & 1) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AnyValid() => _validBits != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllValid() => _validBits == 0xFF;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearValid() => _validBits = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetPixel(byte i) => _pixels[i];
}
