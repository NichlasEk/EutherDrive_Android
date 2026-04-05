namespace EutherDrive.Core.SmsGg;

internal static class SmsGgRomLoader
{
    public static (byte[] RomBytes, string DisplayName) Load(string path)
    {
        if (RomArchiveExtractor.IsArchivePath(path) || RomArchiveExtractor.HasArchiveHeader(path))
        {
            if (!RomArchiveExtractor.TryExtractRom(path, out byte[] data, out string entryName, out _, out string? error))
                throw new InvalidOperationException($"Failed to read archive '{path}': {error}");

            return (data, string.IsNullOrWhiteSpace(entryName) ? Path.GetFileName(path) : entryName);
        }

        return (File.ReadAllBytes(path), Path.GetFileName(path));
    }
}
