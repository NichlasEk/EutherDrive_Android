using System;

namespace KSNES.Specialchips.SPC7110;

[Serializable]
internal sealed class Spc7110Decompressor
{
    private static readonly byte[] EvolutionProbability =
    [
        90,37,17,8,3,1,90,63,44,32,23,17,12,9,7,5,4,3,2,
        90,72,58,46,38,31,25,21,17,14,11,9,8,7,5,4,4,3,2,
        2,88,77,67,59,52,46,41,37,86,79,71,65,60,55
    ];

    private static readonly byte[] EvolutionNextLps =
    [
        1,6,8,10,12,15,7,19,21,22,23,25,26,28,29,31,32,34,35,
        20,39,40,42,44,45,46,25,26,26,27,28,29,30,31,33,33,34,35,
        36,39,47,48,49,50,51,44,45,47,47,48,49,50,51
    ];

    private static readonly byte[] EvolutionNextMps =
    [
        1,2,3,4,5,5,7,8,9,10,11,12,13,14,15,16,17,18,5,
        20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,
        5,40,41,42,43,44,45,46,24,48,49,50,51,52,43
    ];

    private static readonly byte[] Mode2ContextTable = [0,0,0,15,17,19,21,23,25,25,25,25,25,27,29];

    private enum DecompressionMode
    {
        Zero,
        One,
        Two
    }

    [Serializable]
    private struct State
    {
        public bool Initialized;
        public DecompressionMode Mode;
        public uint Source;
        public uint Out;
        public byte Decoded;
        public byte InCount;
        public byte BufferIndex;
        public byte A;
        public byte B;
        public byte C;
        public byte Context;
        public byte Top;
        public ushort Input;
        public byte Plane1;
        public byte[] PlaneBuffer;
        public byte[] PixelOrder;
        public byte[] RealOrder;
        public byte[] ContextIndex;
        public byte[] ContextInvert;

        public void InitArrays()
        {
            PlaneBuffer ??= new byte[16];
            PixelOrder ??= new byte[16];
            RealOrder ??= new byte[16];
            ContextIndex ??= new byte[32];
            ContextInvert ??= new byte[32];
        }

        public byte InputMsb() => (byte)(Input >> 8);
    }

    public uint RomDirectoryBase;
    public byte RomDirectoryIndex;
    public ushort TargetOffset;
    public ushort LengthCounter;
    public bool SkipEnabled;
    private State _state;

    public Spc7110Decompressor()
    {
        _state.InitArrays();
    }

    public void WriteRomDirectoryBaseLow(byte value) => RomDirectoryBase = (RomDirectoryBase & 0xFFFF00) | value;
    public void WriteRomDirectoryBaseMid(byte value) => RomDirectoryBase = (RomDirectoryBase & 0xFF00FF) | ((uint)value << 8);
    public void WriteRomDirectoryBaseHigh(byte value) => RomDirectoryBase = (RomDirectoryBase & 0x00FFFF) | ((uint)value << 16);
    public void WriteTargetOffsetLow(byte value) => TargetOffset = (ushort)((TargetOffset & 0xFF00) | value);

    public void WriteTargetOffsetHigh(byte value, ReadOnlySpan<byte> dataRom)
    {
        TargetOffset = (ushort)((TargetOffset & 0x00FF) | (value << 8));
        Initialize(dataRom);
    }

    public void WriteLengthCounterLow(byte value) => LengthCounter = (ushort)((LengthCounter & 0xFF00) | value);
    public void WriteLengthCounterHigh(byte value) => LengthCounter = (ushort)((LengthCounter & 0x00FF) | (value << 8));
    public void WriteMode(byte value) => SkipEnabled = value == 0x02;
    public byte ReadMode() => (byte)(SkipEnabled ? 0x02 : 0x00);
    public byte ReadStatus() => (byte)(_state.Initialized ? 0x80 : 0x00);

    public byte NextByte(ReadOnlySpan<byte> dataRom)
    {
        if (!_state.Initialized)
            return 0;

        LengthCounter--;
        return _state.Mode switch
        {
            DecompressionMode.Zero => NextByteMode0(dataRom),
            DecompressionMode.One => NextByteMode1(dataRom),
            _ => NextByteMode2(dataRom)
        };
    }

    private byte NextByteMode0(ReadOnlySpan<byte> dataRom)
    {
        _state.Decoded = 0;
        foreach (byte ctxOffset in new byte[] { 0, 1, 3, 7 })
        {
            _state.Context = (byte)(ctxOffset + _state.Decoded);
            DecompressBit(dataRom);
        }

        _state.Out = (_state.Out << 4) ^ (((_state.Out >> 12) ^ _state.Decoded) & 0xFu);

        _state.Decoded = 0;
        foreach (byte ctxOffset in new byte[] { 0, 1, 3, 7 })
        {
            _state.Context = (byte)(15 + ctxOffset + _state.Decoded);
            DecompressBit(dataRom);
        }

        _state.Out = (_state.Out << 4) ^ (((_state.Out >> 12) ^ _state.Decoded) & 0xFu);
        return (byte)_state.Out;
    }

    private byte NextByteMode1(ReadOnlySpan<byte> dataRom)
    {
        byte value;
        if ((_state.BufferIndex & 1) == 0)
        {
            for (int i = 0; i < 8; i++)
            {
                _state.A = (byte)((_state.Out >> 2) & 0x03);
                _state.B = (byte)((_state.Out >> 14) & 0x03);

                _state.Decoded = 0;
                _state.Context = GetContext(_state.A, _state.B, _state.C);
                DecompressBit(dataRom);

                _state.Context = (byte)(2 * _state.Context + 5 + _state.Decoded);
                DecompressBit(dataRom);

                AdjustPixelOrder(2);
            }

            DeinterleaveBits(_state.Out, out ushort plane1, out ushort plane0);
            _state.Plane1 = (byte)plane1;
            value = (byte)plane0;
        }
        else
        {
            value = _state.Plane1;
        }

        _state.BufferIndex++;
        return value;
    }

    private byte NextByteMode2(ReadOnlySpan<byte> dataRom)
    {
        byte value;
        if ((_state.BufferIndex & 0x11) == 0)
        {
            for (int i = 0; i < 8; i++)
            {
                _state.A = (byte)(_state.Out & 0x0F);
                _state.B = (byte)((_state.Out >> 28) & 0x0F);

                _state.Decoded = 0;
                _state.Context = 0;
                DecompressBit(dataRom);

                _state.Context = (byte)(_state.Decoded + 1);
                DecompressBit(dataRom);

                _state.Context = _state.Context == 2
                    ? (byte)(_state.Decoded + 11)
                    : (byte)(GetContext(_state.A, _state.B, _state.C) + 3 + 5 * _state.Decoded);
                DecompressBit(dataRom);

                _state.Context = (byte)(Mode2ContextTable[_state.Context] + (_state.Decoded & 0x01));
                DecompressBit(dataRom);

                AdjustPixelOrder(4);
            }

            DeinterleaveBits(_state.Out, out ushort evenBits, out ushort oddBits);
            DeinterleaveBits(oddBits, out ushort plane2, out ushort plane0);
            DeinterleaveBits(evenBits, out ushort plane3, out ushort plane1);
            _state.Plane1 = (byte)plane1;
            _state.PlaneBuffer[_state.BufferIndex & 0x0F] = (byte)plane2;
            _state.PlaneBuffer[(_state.BufferIndex + 1) & 0x0F] = (byte)plane3;
            value = (byte)plane0;
        }
        else if ((_state.BufferIndex & 0x10) == 0)
        {
            value = _state.Plane1;
        }
        else
        {
            value = _state.PlaneBuffer[_state.BufferIndex & 0x0F];
        }

        _state.BufferIndex++;
        return value;
    }

    private void Initialize(ReadOnlySpan<byte> dataRom)
    {
        _state.InitArrays();
        _state.Initialized = true;

        uint directoryAddr = RomDirectoryBase + 4u * RomDirectoryIndex;
        _state.Mode = RomGet(dataRom, directoryAddr) switch
        {
            0x01 => DecompressionMode.One,
            0x02 => DecompressionMode.Two,
            _ => DecompressionMode.Zero
        };
        _state.Source = (uint)((RomGet(dataRom, directoryAddr + 1) << 16) |
                               (RomGet(dataRom, directoryAddr + 2) << 8) |
                               RomGet(dataRom, directoryAddr + 3));
        _state.BufferIndex = 0;
        _state.Out = 0;
        _state.Top = 255;
        _state.C = 0;

        byte inputMsb = RomGet(dataRom, _state.Source);
        _state.Input = (ushort)(inputMsb << 8);
        _state.Source++;
        _state.InCount = 0;

        for (int i = 0; i < 16; i++)
            _state.PixelOrder[i] = (byte)i;
        Array.Clear(_state.ContextIndex, 0, _state.ContextIndex.Length);
        Array.Clear(_state.ContextInvert, 0, _state.ContextInvert.Length);

        if (SkipEnabled)
        {
            uint skipBytes = Bpp(_state.Mode) * TargetOffset;
            for (uint i = 0; i < skipBytes; i++)
                NextByte(dataRom);
            TargetOffset = 0;
        }
    }

    private void DecompressBit(ReadOnlySpan<byte> dataRom)
    {
        int context = _state.Context;
        _state.Decoded = (byte)((_state.Decoded << 1) | _state.ContextInvert[context]);

        int evolution = _state.ContextIndex[context];
        _state.Top -= EvolutionProbability[evolution];

        if (_state.InputMsb() > _state.Top)
        {
            byte inputMsb = (byte)(_state.InputMsb() - 1 - _state.Top);
            _state.Input = (ushort)((_state.Input & 0x00FF) | (inputMsb << 8));

            _state.Top = (byte)(EvolutionProbability[evolution] - 1);
            if (_state.Top > 79)
                _state.ContextInvert[context] ^= 1;

            _state.Decoded ^= 1;
            _state.ContextIndex[context] = EvolutionNextLps[evolution];
        }
        else
        {
            if (_state.Top <= 126)
                _state.ContextIndex[context] = EvolutionNextMps[evolution];
        }

        while (_state.Top <= 126)
        {
            if (_state.InCount == 0)
            {
                byte inputLsb = RomGet(dataRom, _state.Source);
                _state.Input = (ushort)((_state.Input & 0xFF00) | inputLsb);
                _state.Source++;
                _state.InCount = 8;
            }

            _state.Top = (byte)((_state.Top << 1) | 1);
            _state.Input <<= 1;
            _state.InCount--;
        }
    }

    private void AdjustPixelOrder(int bpp)
    {
        byte x = _state.A;
        for (int m = 0; ; m++)
        {
            (x, _state.PixelOrder[m]) = (_state.PixelOrder[m], x);
            if (x == _state.A)
                break;
        }

        for (int m = 0; m < (1 << bpp); m++)
            _state.RealOrder[m] = _state.PixelOrder[m];

        x = _state.C;
        for (int m = 0; ; m++)
        {
            (x, _state.RealOrder[m]) = (_state.RealOrder[m], x);
            if (x == _state.C)
                break;
        }

        x = _state.B;
        for (int m = 0; ; m++)
        {
            (x, _state.RealOrder[m]) = (_state.RealOrder[m], x);
            if (x == _state.B)
                break;
        }

        x = _state.A;
        for (int m = 0; ; m++)
        {
            (x, _state.RealOrder[m]) = (_state.RealOrder[m], x);
            if (x == _state.A)
                break;
        }

        _state.Out = (_state.Out << bpp) + _state.RealOrder[_state.Decoded];
        _state.C = _state.B;
    }

    private static uint Bpp(DecompressionMode mode) => mode switch
    {
        DecompressionMode.Zero => 1u,
        DecompressionMode.One => 2u,
        _ => 3u
    };

    private static byte GetContext(byte a, byte b, byte c)
    {
        if (a == b && b == c) return 0;
        if (a == b) return 1;
        if (b == c) return 2;
        if (a == c) return 3;
        return 4;
    }

    private static byte RomGet(ReadOnlySpan<byte> dataRom, uint address)
    {
        return address < dataRom.Length ? dataRom[(int)address] : (byte)0;
    }

    private static void DeinterleaveBits(uint n, out ushort even, out ushort odd)
    {
        ulong value = n;
        value = (value & 0x0000000055555555UL) | ((value << 31) & 0x5555555500000000UL);
        value = (value | (value >> 1)) & 0x3333333333333333UL;
        value = (value | (value >> 2)) & 0x0F0F0F0F0F0F0F0FUL;
        value = (value | (value >> 4)) & 0x00FF00FF00FF00FFUL;
        value = (value | (value >> 8)) & 0x0000FFFF0000FFFFUL;
        even = (ushort)value;
        odd = (ushort)(value >> 32);
    }
}
