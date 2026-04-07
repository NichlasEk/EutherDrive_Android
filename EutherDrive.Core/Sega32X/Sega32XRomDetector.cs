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
        string title = ReadAscii(romData, 0x150, 0x30).Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = Path.GetFileName(path);

        return $"32X scaffold: title='{title}', securityProgram={(securityMatch ? "match" : "no-match")}, header='{header}'";
    }

    private static string ReadAscii(byte[] data, int offset, int length)
    {
        if (offset >= data.Length)
            return string.Empty;

        int count = Math.Min(length, data.Length - offset);
        string raw = Encoding.ASCII.GetString(data, offset, count);
        return raw.Replace('\0', ' ').Trim();
    }
}
