using System;
using System.Text;
using EutherDrive.Core;

namespace EutherDrive.Core.MdTracerCore;

internal readonly record struct RomNormalizationResult(byte[] Data, int HeaderSize, bool Deinterleaved, bool HasMegaSignature);

internal static class md_rom_utils
{
    private const int SmdBlockSize = 0x4000;
    private const int CopierHeaderSize = 512;
    private const int RegionHeaderOffset = 0x1F0;
    private const int RegionHeaderLength = 0x10;

    public static RomNormalizationResult NormalizeMegaDriveRom(byte[] data)
    {
        int headerSize = 0;
        byte[] normalized = data;

        if (ShouldRemoveCopierHeader(normalized))
        {
            headerSize = CopierHeaderSize;
            normalized = CopyRange(normalized, CopierHeaderSize, normalized.Length - CopierHeaderSize);
        }

        bool deinterleaved = false;
        if (ShouldDeinterleave(normalized))
        {
            normalized = DeinterleaveSmd(normalized);
            deinterleaved = true;
        }

        if (HasSwappedMegaDriveSignature(normalized))
        {
            normalized = ByteSwapWords(normalized);
        }

        bool hasSignature = HasMegaDriveSignature(normalized);
        return new RomNormalizationResult(normalized, headerSize, deinterleaved, hasSignature);
    }

    internal static bool HasMegaDriveSignature(byte[] data)
    {
        if (data.Length <= 0x103)
            return false;
        return data[0x100] == (byte)'S' &&
               data[0x101] == (byte)'E' &&
               data[0x102] == (byte)'G' &&
               data[0x103] == (byte)'A';
    }

    internal static ConsoleRegion? DetectRegionFromHeader(byte[] rom, out string rawHeader)
    {
        rawHeader = string.Empty;
        if (rom.Length < RegionHeaderOffset + RegionHeaderLength)
            return null;

        Span<byte> span = stackalloc byte[RegionHeaderLength];
        for (int i = 0; i < RegionHeaderLength; i++)
            span[i] = rom[RegionHeaderOffset + i];

        rawHeader = Encoding.ASCII.GetString(span).TrimEnd('\0', ' ');
        if (rawHeader.Length == 0)
            return null;

        string upper = rawHeader.ToUpperInvariant();
        bool hasJ = upper.Contains('J');
        bool hasU = upper.Contains('U');
        bool hasE = upper.Contains('E');

        int matches = (hasJ ? 1 : 0) + (hasU ? 1 : 0) + (hasE ? 1 : 0);
        if (matches > 1)
            return ConsoleRegion.US;

        if (hasJ)
            return ConsoleRegion.JP;
        if (hasU)
            return ConsoleRegion.US;
        if (hasE)
            return ConsoleRegion.EU;

        return null;
    }

    private static bool ShouldRemoveCopierHeader(byte[] data)
    {
        if ((data.Length & 0x3FF) != 0x200)
            return false;

        if (data.Length < 0x2282)
            return false;

        bool tmssAt300 = MatchesAscii4(data, 0x300, "SEGA") || MatchesAscii4(data, 0x300, "ESAG");
        bool interleavedTmss = MatchesAscii2(data, 0x0280, "EA") && MatchesAscii2(data, 0x2280, "SG");
        return tmssAt300 || interleavedTmss;
    }

    private static bool ShouldDeinterleave(byte[] data)
    {
        if (data.Length < 0x2082)
            return false;

        if ((data.Length % SmdBlockSize) != 0)
            return false;

        if (MatchesAscii4(data, 0x100, "SEGA") || MatchesAscii4(data, 0x100, "ESAG"))
            return false;

        return MatchesAscii2(data, 0x0080, "EA") && MatchesAscii2(data, 0x2080, "SG");
    }

    private static byte[] DeinterleaveSmd(byte[] data)
    {
        var result = new byte[data.Length];
        for (int blockStart = 0; blockStart < data.Length; blockStart += SmdBlockSize)
        {
            for (int i = 0; i < 0x2000; i++)
            {
                result[blockStart + 2 * i] = data[blockStart + 0x2000 + i];
                result[blockStart + 2 * i + 1] = data[blockStart + i];
            }
        }

        return result;
    }

    private static bool HasSwappedMegaDriveSignature(byte[] data)
    {
        return MatchesAscii4(data, 0x100, "ESAG");
    }

    private static byte[] ByteSwapWords(byte[] data)
    {
        byte[] swapped = (byte[])data.Clone();
        for (int i = 0; i + 1 < swapped.Length; i += 2)
        {
            byte tmp = swapped[i];
            swapped[i] = swapped[i + 1];
            swapped[i + 1] = tmp;
        }

        return swapped;
    }

    private static bool MatchesAscii2(byte[] data, int offset, string value)
    {
        if (offset < 0 || offset + 1 >= data.Length || value.Length != 2)
            return false;
        return data[offset] == (byte)value[0] && data[offset + 1] == (byte)value[1];
    }

    private static bool MatchesAscii4(byte[] data, int offset, string value)
    {
        if (offset < 0 || offset + 3 >= data.Length || value.Length != 4)
            return false;
        return data[offset] == (byte)value[0]
            && data[offset + 1] == (byte)value[1]
            && data[offset + 2] == (byte)value[2]
            && data[offset + 3] == (byte)value[3];
    }

    private static byte[] CopyRange(byte[] data, int start, int length)
    {
        var copy = new byte[length];
        Array.Copy(data, start, copy, 0, length);
        return copy;
    }
}
