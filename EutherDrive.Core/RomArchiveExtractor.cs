using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;

namespace EutherDrive.Core;

internal static class RomArchiveExtractor
{
    private static readonly string[] RomExtensions =
    {
        ".md", ".bin", ".gen", ".smd", ".sms", ".sg", ".gg", ".iso"
    };

    private static readonly HashSet<string> SmsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sms", ".sg", ".gg"
    };

    internal static bool IsSmsExtension(string ext) => SmsExtensions.Contains(ext);

    internal static bool IsArchivePath(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".7z", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasArchiveHeader(string path)
    {
        Span<byte> header = stackalloc byte[6];
        try
        {
            using var fs = File.OpenRead(path);
            int read = fs.Read(header);
            header = header[..read];
        }
        catch
        {
            return false;
        }

        return LooksLikeZip(header) || LooksLike7z(header);
    }

    internal static bool TryExtractRom(string archivePath, out byte[] data, out string entryName, out bool isSms, out string? error)
    {
        data = Array.Empty<byte>();
        entryName = string.Empty;
        isSms = false;
        error = null;

        try
        {
            using var archive = ArchiveFactory.Open(archivePath);
            IArchiveEntry? best = null;
            int bestPriority = int.MaxValue;
            long bestSize = -1;

            var priorities = BuildExtensionPriorities();

            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;

                string ext = Path.GetExtension(entry.Key);
                int priority = priorities.TryGetValue(ext, out int value) ? value : int.MaxValue;
                long size = entry.Size;

                if (best == null ||
                    priority < bestPriority ||
                    (priority == bestPriority && size > bestSize))
                {
                    best = entry;
                    bestPriority = priority;
                    bestSize = size;
                }
            }

            if (best == null)
            {
                error = "archive contained no files";
                return false;
            }

            using var stream = best.OpenEntryStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
            entryName = string.IsNullOrWhiteSpace(best.Key) ? "rom" : Path.GetFileName(best.Key);
            isSms = IsSmsExtension(Path.GetExtension(entryName));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Dictionary<string, int> BuildExtensionPriorities()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < RomExtensions.Length; i++)
            map[RomExtensions[i]] = i;
        return map;
    }

    private static bool LooksLikeZip(ReadOnlySpan<byte> header)
        => header.Length >= 4 &&
           header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;

    private static bool LooksLike7z(ReadOnlySpan<byte> header)
        => header.Length >= 6 &&
           header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
           header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C;
}
