using System;

namespace KSNES.Specialchips.ST011;

internal static class RomConvert
{
    public static (uint[] ProgramRom, ushort[] DataRom) Convert(byte[] rom)
    {
        Endianness endianness = DetectEndianness(rom);
        int programRomLen = 3 * 16384; // 16K opcodes for ST011 (vs 2K for DSP1)
        uint[] program = ConvertProgramRom(rom.AsSpan(0, programRomLen), endianness);
        ushort[] data = ConvertToU16(rom.AsSpan(programRomLen), endianness);
        return (program, data);
    }

    private static Endianness DetectEndianness(ReadOnlySpan<byte> programRom)
    {
        for (int i = 0; i < 4; i++)
        {
            int idx = i * 3;
            if (idx + 2 >= programRom.Length)
                break;
            if (programRom[idx] == (byte)(i << 2) && programRom[idx + 1] == 0xC0 && programRom[idx + 2] == 0x97)
                return Endianness.Little;
            if (programRom[idx] == 0x97 && programRom[idx + 1] == 0xC0 && programRom[idx + 2] == (byte)(i << 2))
                return Endianness.Big;
        }
        return Endianness.Little;
    }

    private static uint[] ConvertProgramRom(ReadOnlySpan<byte> programRom, Endianness endianness)
    {
        int count = programRom.Length / 3;
        uint[] opcodes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i * 3;
            uint value = endianness == Endianness.Little
                ? (uint)(programRom[idx] | (programRom[idx + 1] << 8) | (programRom[idx + 2] << 16))
                : (uint)((programRom[idx] << 16) | (programRom[idx + 1] << 8) | programRom[idx + 2]);
            opcodes[i] = value;
        }
        return opcodes;
    }

    private static ushort[] ConvertToU16(ReadOnlySpan<byte> bytes, Endianness endianness)
    {
        int count = bytes.Length / 2;
        ushort[] words = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i * 2;
            ushort value = endianness == Endianness.Little
                ? (ushort)(bytes[idx] | (bytes[idx + 1] << 8))
                : (ushort)((bytes[idx] << 8) | bytes[idx + 1]);
            words[i] = value;
        }
        return words;
    }

    private enum Endianness
    {
        Little,
        Big
    }
}