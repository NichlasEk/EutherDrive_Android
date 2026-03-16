using System;
using System.IO;
using ProjectPSX.IO;

namespace EutherDrive.Core.SegaCd;

public static class SegaCdBios
{
    public const int BiosSizeBytes = 128 * 1024;
    private const string DefaultBiosDir = "/home/nichlas/EutherDrive/bios";

    public static byte[] Load(ConsoleRegion region)
    {
        string? path = ResolvePath(region);
        if (string.IsNullOrWhiteSpace(path) || !VirtualFileSystem.Exists(path))
            throw new FileNotFoundException($"Sega CD BIOS not found for region {region}.", path ?? "(null)");

        byte[] data = VirtualFileSystem.ReadAllBytes(path);
        if (data.Length != BiosSizeBytes)
            throw new InvalidDataException($"Sega CD BIOS must be {BiosSizeBytes} bytes (got {data.Length}).");

        return data;
    }

    public static string? ResolvePath(ConsoleRegion region)
    {
        string? genericEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_BIOS");
        if (!string.IsNullOrWhiteSpace(genericEnv) && VirtualFileSystem.Exists(genericEnv))
            return genericEnv;

        string? env = region switch
        {
            ConsoleRegion.EU => Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_BIOS_E"),
            ConsoleRegion.JP => Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_BIOS_J"),
            ConsoleRegion.US => Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_BIOS_U"),
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(env) && VirtualFileSystem.Exists(env))
            return env;

        string fileName = region switch
        {
            ConsoleRegion.EU => "BIOS_CD_E.BIN",
            ConsoleRegion.JP => "BIOS_CD_J.BIN",
            ConsoleRegion.US => "BIOS_CD_U.BIN",
            _ => "BIOS_CD_U.BIN"
        };

        string candidate = Path.Combine(DefaultBiosDir, fileName);
        if (VirtualFileSystem.Exists(candidate))
            return candidate;

        return null;
    }
}
