using System;
using System.IO;
using System.Text;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdDiscInfo
{
    public string? Title { get; init; }
    public string? Region { get; init; }
    public string? Serial { get; init; }
    public string? RawHeaderString { get; init; }

    public static SegaCdDiscInfo? Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string dataPath = ResolveCueDataPath(path) ?? path;
        if (!File.Exists(dataPath))
            return null;

        if (!TryReadSector0(dataPath, out var header))
            return null;

        // Sega CD header data starts at offset 0x010 for 2352-byte sectors,
        // but at 0x000 for 2048-byte ISO sectors. Try both.
        int baseOffset = 0x010;
        if (header.Length < baseOffset + 0x200)
            return null;

        string headerStr = ReadAscii(header, baseOffset, 0x100);
        if (!LooksLikeSegaCdHeader(headerStr))
        {
            baseOffset = 0x000;
            headerStr = ReadAscii(header, baseOffset, 0x100);
            if (!LooksLikeSegaCdHeader(headerStr))
                return null;
        }

        string title = ReadAscii(header, baseOffset + 0x120, 0x30).Trim();
        string serial = ReadAscii(header, baseOffset + 0x180, 0x10).Trim();
        string regionRaw = ReadAscii(header, baseOffset + 0x1F0, 0x10).Trim();
        string? region = MapRegion(regionRaw);

        return new SegaCdDiscInfo
        {
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Serial = string.IsNullOrWhiteSpace(serial) ? null : serial,
            Region = region,
            RawHeaderString = headerStr.Trim()
        };
    }

    public static bool IsSegaCdDisc(string path)
    {
        return Read(path) != null;
    }

    private static bool TryReadSector0(string path, out byte[] header)
    {
        header = Array.Empty<byte>();
        long length = new FileInfo(path).Length;
        int sectorSize = GuessSectorSize(length);
        if (sectorSize <= 0)
            return false;

        using var stream = File.OpenRead(path);
        int dataOffset = sectorSize == 2352 ? 16 : 0;
        byte[] buffer = new byte[dataOffset + 0x800];
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
            return false;
        header = buffer;
        return true;
    }

    private static int GuessSectorSize(long length)
    {
        if (length <= 0)
            return 0;
        if (length % 2352 == 0)
            return 2352;
        if (length % 2048 == 0)
            return 2048;
        return 2048;
    }

    private static string? ResolveCueDataPath(string path)
    {
        if (!path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            return null;

        string baseDir = Path.GetDirectoryName(path) ?? "";
        foreach (var rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
                continue;

            int firstQuote = line.IndexOf('"');
            if (firstQuote >= 0)
            {
                int secondQuote = line.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote)
                {
                    string fileName = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                    string candidate = Path.Combine(baseDir, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            else
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string candidate = Path.Combine(baseDir, parts[1]);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset < 0 || offset + length > data.Length)
            return string.Empty;
        return Encoding.ASCII.GetString(data, offset, length).TrimEnd('\0', ' ');
    }

    private static bool LooksLikeSegaCdHeader(string header)
    {
        string upper = header.ToUpperInvariant();
        return upper.Contains("SEGADISCSYSTEM")
            || upper.Contains("SEGA MEGA CD")
            || upper.Contains("SEGA CD");
    }

    private static string? MapRegion(string regionRaw)
    {
        if (string.IsNullOrWhiteSpace(regionRaw))
            return null;
        string upper = regionRaw.ToUpperInvariant();
        if (upper.Contains("J"))
            return "NTSC-J";
        if (upper.Contains("U") || upper.Contains("USA"))
            return "NTSC-U";
        if (upper.Contains("E") || upper.Contains("EUROPE"))
            return "PAL";
        return null;
    }
}
