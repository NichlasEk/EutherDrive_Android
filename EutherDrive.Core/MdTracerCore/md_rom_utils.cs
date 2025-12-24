using System;

namespace EutherDrive.Core.MdTracerCore;

internal readonly record struct RomNormalizationResult(byte[] Data, int HeaderSize, bool Deinterleaved, bool HasMegaSignature);

internal static class md_rom_utils
{
    private const int SmdBlockSize = 0x4000;

    public static RomNormalizationResult NormalizeMegaDriveRom(byte[] data)
    {
        int headerSize = DetectSmdHeaderSize(data);
        byte[] withoutHeader = headerSize > 0 ? CopyRange(data, headerSize, data.Length - headerSize) : data;

        bool hasSignature = HasMegaDriveSignature(withoutHeader);
        byte[] normalized = withoutHeader;
        bool deinterleaved = false;

        if (!hasSignature)
        {
            var deinterleavedData = DeinterleaveSmd(withoutHeader);
            if (HasMegaDriveSignature(deinterleavedData))
            {
                normalized = deinterleavedData;
                hasSignature = true;
                deinterleaved = true;
            }
        }

        if (headerSize == 0 && !deinterleaved)
            return new RomNormalizationResult(data, 0, false, hasSignature);

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

    private static int DetectSmdHeaderSize(byte[] data)
    {
        if (data.Length > SmdBlockSize && data.Length % SmdBlockSize == 512)
            return 512;
        return 0;
    }

    private static byte[] DeinterleaveSmd(byte[] data)
    {
        var result = new byte[data.Length];
        for (int blockStart = 0; blockStart < data.Length;)
        {
            int remaining = data.Length - blockStart;
            int chunkSize = Math.Min(SmdBlockSize, remaining);
            if (chunkSize < 2)
            {
                result[blockStart] = data[blockStart];
                blockStart++;
                continue;
            }

            int half = chunkSize / 2;
            for (int i = 0; i < half; i++)
            {
                int dstEven = blockStart + 2 * i;
                int srcEven = blockStart + i;
                int srcOdd = blockStart + half + i;
                if (dstEven < data.Length)
                    result[dstEven] = data[srcOdd];
                if (dstEven + 1 < blockStart + chunkSize)
                    result[dstEven + 1] = data[srcEven];
            }

            if ((chunkSize & 1) != 0)
            {
                result[blockStart + chunkSize - 1] = data[blockStart + chunkSize - 1];
            }

            blockStart += chunkSize;
        }

        return result;
    }

    private static byte[] CopyRange(byte[] data, int start, int length)
    {
        var copy = new byte[length];
        Array.Copy(data, start, copy, 0, length);
        return copy;
    }
}
