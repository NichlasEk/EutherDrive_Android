using System;
using System.IO;

namespace EutherDrive.Core.Savestates;

internal static class PersistentStoragePath
{
    public static string ResolveSavestateDirectory(string? contentPath, string systemName)
    {
        if (TryGetWritableSiblingDirectory(contentPath, out string? siblingDirectory))
            return siblingDirectory;

        return BuildAppDataFallback("savestates", systemName);
    }

    public static string ResolveSaveDirectory(string? contentPath, string systemName, string? overrideDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return overrideDirectory;

        if (TryGetWritableSiblingDirectory(contentPath, out string? siblingDirectory))
            return siblingDirectory;

        return BuildAppDataFallback("saves", systemName);
    }

    private static string BuildAppDataFallback(string category, string systemName)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, category, systemName);

        return Path.Combine(Directory.GetCurrentDirectory(), category, systemName);
    }

    private static bool TryGetWritableSiblingDirectory(string? contentPath, out string? directory)
    {
        directory = null;
        if (string.IsNullOrWhiteSpace(contentPath) || !File.Exists(contentPath))
            return false;

        string? candidate = Path.GetDirectoryName(contentPath);
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (!IsDirectoryWritable(candidate))
            return false;

        directory = candidate;
        return true;
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probePath = Path.Combine(directory, $".eutherdrive_write_probe_{Guid.NewGuid():N}.tmp");
            using (FileStream stream = File.Open(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush();
            }

            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
