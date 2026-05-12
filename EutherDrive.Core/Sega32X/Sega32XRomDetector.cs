using System.Text;

namespace EutherDrive.Core.Sega32X;

public static class Sega32XRomDetector
{
    public static bool IsSega32XRom(byte[] romData, string? path)
    {
        if (romData.Length == 0)
            return false;

        string extension = Path.GetExtension(path ?? string.Empty);
        if (extension.Equals(".32x", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Sega32XBootRom.SecurityProgramMatches(romData))
            return true;

        string header = ReadAscii(romData, 0x100, 0x50);
        if (header.IndexOf("32X", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        string name = Path.GetFileNameWithoutExtension(path ?? string.Empty);
        if (name.IndexOf("32x", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    public static string BuildSummary(byte[] romData, string path)
    {
        bool securityMatch = Sega32XBootRom.SecurityProgramMatches(romData);
        string header = ReadAscii(romData, 0x100, 0x50).Trim();
        string title = ReadInternationalTitle(romData);
        ConsoleRegion? region = DetectRegion(romData, out string regionRaw);
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileName(path);

        string regionLabel = region?.ToString() ?? ConsoleRegion.Auto.ToString();
        return $"32X scaffold: title='{title}', region={regionLabel} raw='{regionRaw}', securityProgram={(securityMatch ? "match" : "no-match")}, header='{header}'";
    }

    public static string ReadInternationalTitle(byte[] romData) => ReadAscii(romData, 0x150, 0x30).Trim();

    public static bool IsKnucklesChaotix(byte[] romData)
    {
        string header = ReadAscii(romData, 0x100, 0x90);
        return header.IndexOf("CHAOTIX", StringComparison.OrdinalIgnoreCase) >= 0 ||
            header.IndexOf("KNUCKLES", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsKolibri(byte[] romData)
    {
        string header = ReadAscii(romData, 0x100, 0x90);
        return header.IndexOf("KOLIBRI", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static ConsoleRegion? DetectRegion(byte[] romData, out string rawHeader)
    {
        rawHeader = ReadAscii(romData, 0x1F0, 0x10);
        if (rawHeader.Length == 0)
            return null;

        string upper = rawHeader.ToUpperInvariant();
        if (upper.StartsWith("EUROPE", StringComparison.Ordinal))
            return ConsoleRegion.EU;

        ReadOnlySpan<char> regionChars = upper.AsSpan(0, Math.Min(3, upper.Length));

        // Match jgenesis' Genesis/32X preference order for letter-based region fields.
        if (regionChars.IndexOf('U') >= 0)
            return ConsoleRegion.US;
        if (regionChars.IndexOf('J') >= 0)
            return ConsoleRegion.JP;
        if (regionChars.IndexOf('E') >= 0)
            return ConsoleRegion.EU;

        if (!TryParseHexNibble(regionChars[0], out byte mask))
            return null;

        // Old-style numeric region masks use bit 2 for US, bit 0 for Japan, bit 3 for Europe.
        if ((mask & 0x4) != 0)
            return ConsoleRegion.US;
        if ((mask & 0x1) != 0)
            return ConsoleRegion.JP;
        if ((mask & 0x8) != 0)
            return ConsoleRegion.EU;

        return null;
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset >= data.Length)
            return string.Empty;

        int count = Math.Min(length, data.Length - offset);
        string raw = Encoding.ASCII.GetString(data, offset, count);
        return raw.Replace('\0', ' ').Trim();
    }

    private static bool TryParseHexNibble(char c, out byte value)
    {
        value = 0;
        if (c >= '0' && c <= '9')
        {
            value = (byte)(c - '0');
            return true;
        }

        if (c >= 'A' && c <= 'F')
        {
            value = (byte)(10 + (c - 'A'));
            return true;
        }

        return false;
    }
}
