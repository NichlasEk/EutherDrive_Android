using System;

namespace KSNES.Specialchips.SDD1;

internal sealed class Sdd1Decompressor
{
    private static readonly byte[] EvolutionCodeSize =
    [
        0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 5, 5, 6, 6, 7, 7, 0, 1, 2, 3, 4, 5, 6, 7
    ];

    private static readonly byte[] EvolutionMpsNext =
    [
        25, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17,
        18, 19, 20, 21, 22, 23, 24, 24, 26, 27, 28, 29, 30, 31, 32, 24
    ];

    private static readonly byte[] EvolutionLpsNext =
    [
        25, 1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 19, 20, 21, 22, 23, 1, 2, 4, 8, 12, 16, 18, 22
    ];

    private static readonly byte[] RunTable =
    [
        128, 64, 96, 32, 112, 48, 80, 16, 120, 56, 88, 24, 104, 40, 72, 8,
        124, 60, 92, 28, 108, 44, 76, 12, 116, 52, 84, 20, 100, 36, 68, 4,
        126, 62, 94, 30, 110, 46, 78, 14, 118, 54, 86, 22, 102, 38, 70, 6,
        122, 58, 90, 26, 106, 42, 74, 10, 114, 50, 82, 18, 98, 34, 66, 2,
        127, 63, 95, 31, 111, 47, 79, 15, 119, 55, 87, 23, 103, 39, 71, 7,
        123, 59, 91, 27, 107, 43, 75, 11, 115, 51, 83, 19, 99, 35, 67, 3,
        125, 61, 93, 29, 109, 45, 77, 13, 117, 53, 85, 21, 101, 37, 69, 5,
        121, 57, 89, 25, 105, 41, 73, 9, 113, 49, 81, 17, 97, 33, 65, 1
    ];

    private uint _sourceAddress;
    private ushort _input;
    private byte _plane;
    private byte _numPlanes;
    private byte _yLocation;
    private int _validBits;
    private ushort _highContextBits;
    private ushort _lowContextBits;
    private readonly ushort[] _bitCounter = new ushort[8];
    private readonly ushort[] _prevBits = new ushort[8];
    private readonly byte[] _contextStates = new byte[32];
    private readonly byte[] _contextMps = new byte[32];

    public void Reset()
    {
        _sourceAddress = 0;
        _input = 0;
        _plane = 0;
        _numPlanes = 0;
        _yLocation = 0;
        _validBits = 0;
        _highContextBits = 0;
        _lowContextBits = 0;
        Array.Clear(_bitCounter);
        Array.Clear(_prevBits);
        Array.Clear(_contextStates);
        Array.Clear(_contextMps);
    }

    public void Init(uint sourceAddress, Sdd1.Sdd1Mmc mmc, byte[] rom)
    {
        _input = ReadByte(sourceAddress, mmc, rom);
        _sourceAddress = sourceAddress + 1;

        _numPlanes = (_input & 0xC0) switch
        {
            0x00 => 2,
            0x40 => 8,
            0x80 => 4,
            _ => 0
        };

        (_highContextBits, _lowContextBits) = (_input & 0x30) switch
        {
            0x00 => ((ushort)0x01C0, (ushort)0x0001),
            0x10 => ((ushort)0x0180, (ushort)0x0001),
            0x20 => ((ushort)0x00C0, (ushort)0x0001),
            _ => ((ushort)0x0180, (ushort)0x0003)
        };

        ushort nextByte = ReadByte(_sourceAddress, mmc, rom);
        _input = (ushort)((_input << 11) | (nextByte << 3));
        _sourceAddress++;
        _validBits = 5;

        Array.Clear(_bitCounter);
        Array.Clear(_prevBits);
        Array.Clear(_contextStates);
        Array.Clear(_contextMps);

        _plane = 0;
        _yLocation = 0;
    }

    public byte NextByte(Sdd1.Sdd1Mmc mmc, byte[] rom)
    {
        if (_numPlanes == 0)
        {
            byte value = 0;
            for (byte plane = 0; plane < 8; plane++)
                value |= (byte)(GetBit(plane, mmc, rom) << plane);
            return value;
        }

        if ((_plane & 0x01) == 0)
        {
            for (int i = 0; i < 8; i++)
            {
                GetBit(_plane, mmc, rom);
                GetBit((byte)(_plane + 1), mmc, rom);
            }

            byte value = (byte)(_prevBits[_plane] & 0xFF);
            _plane++;
            return value;
        }

        byte oddValue = (byte)(_prevBits[_plane] & 0xFF);
        _plane--;
        _yLocation++;
        if (_yLocation == 8)
        {
            _yLocation = 0;
            _plane = (byte)((_plane + 2) & (_numPlanes - 1));
        }

        return oddValue;
    }

    private byte GetBit(byte plane, Sdd1.Sdd1Mmc mmc, byte[] rom)
    {
        ushort context = (ushort)(((plane & 0x01) << 4) |
            ((_prevBits[plane] & _highContextBits) >> 5) |
            (_prevBits[plane] & _lowContextBits));

        byte bit = GetProbableBit(context, mmc, rom);
        _prevBits[plane] = (ushort)((_prevBits[plane] << 1) | bit);
        return bit;
    }

    private byte GetProbableBit(ushort context, Sdd1.Sdd1Mmc mmc, byte[] rom)
    {
        byte state = _contextStates[context];
        byte codeSize = EvolutionCodeSize[state];

        if ((_bitCounter[codeSize] & 0x7F) == 0)
            _bitCounter[codeSize] = GetCodeword(codeSize, mmc, rom);

        byte probableBit = _contextMps[context];
        _bitCounter[codeSize]--;

        if (_bitCounter[codeSize] == 0x00)
        {
            _contextStates[context] = EvolutionLpsNext[state];
            probableBit ^= 0x01;
            if (state < 2)
                _contextMps[context] = probableBit;
        }
        else if (_bitCounter[codeSize] == 0x80)
        {
            _contextStates[context] = EvolutionMpsNext[state];
        }

        return probableBit;
    }

    private ushort GetCodeword(byte codeSize, Sdd1.Sdd1Mmc mmc, byte[] rom)
    {
        if (_validBits == 0)
        {
            _input |= ReadByte(_sourceAddress, mmc, rom);
            _sourceAddress++;
            _validBits = 8;
        }

        _input <<= 1;
        _validBits--;

        if ((_input & 0x8000) == 0)
            return (ushort)(0x80 + (1 << codeSize));

        int runTableIndex = ((_input >> 8) & 0x7F) | (0x7F >> codeSize);
        _input = (ushort)(_input << codeSize);
        _validBits -= codeSize;
        if (_validBits < 0)
        {
            ushort nextByte = ReadByte(_sourceAddress, mmc, rom);
            _input |= (ushort)(nextByte << (-_validBits));
            _sourceAddress++;
            _validBits += 8;
        }

        return RunTable[runTableIndex];
    }

    private static ushort ReadByte(uint address, Sdd1.Sdd1Mmc mmc, byte[] rom)
    {
        uint? romAddr = mmc.MapRomAddress(address, (uint)rom.Length);
        if (!romAddr.HasValue || romAddr.Value >= rom.Length)
            return 0;
        return rom[romAddr.Value];
    }
}
