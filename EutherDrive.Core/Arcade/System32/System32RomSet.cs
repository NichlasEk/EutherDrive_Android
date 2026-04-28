using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.System32;

// ROM region definitions are translated from MAME's BSD-3-Clause Sega System
// 32 driver by Aaron Giles.
internal sealed class System32RomSet
{
    public System32RomSet(
        string driverName,
        byte[] mainCpu,
        byte[] soundCpu,
        byte[] mcu,
        byte[] tiles,
        byte[] sprites)
    {
        DriverName = driverName;
        MainCpu = mainCpu;
        SoundCpu = soundCpu;
        Mcu = mcu;
        Tiles = tiles;
        Sprites = sprites;
    }

    public string DriverName { get; }
    public byte[] MainCpu { get; }
    public byte[] SoundCpu { get; }
    public byte[] Mcu { get; }
    public byte[] Tiles { get; }
    public byte[] Sprites { get; }

    public static System32RomSet LoadGoldenAxe2(string path)
    {
        string driverName = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        GoldenAxe2ProgramNames programNames = driverName switch
        {
            "ga2" => new GoldenAxe2ProgramNames("epr-14961b.ic17", "epr-14958b.ic8", "epr-15148b.ic18", "epr-15147b.ic9"),
            "ga2u" => new GoldenAxe2ProgramNames("epr-14960a.ic17", "epr-14957a.ic8", "epr-15146a.ic18", "epr-15145a.ic9"),
            "ga2j" => new GoldenAxe2ProgramNames("epr-14959.ic17", "epr-14946.ic8", "epr-14941.ic18", "epr-14940.ic9"),
            _ => throw new NotSupportedException($"Unsupported Sega System 32 driver '{driverName}'.")
        };

        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, programNames.Main17);
        LoadX4(entries, mainCpu, 0x080000, programNames.Main8);
        Load16ByteX2(entries, mainCpu, 0x100000, programNames.Main18);
        Load16ByteX2(entries, mainCpu, 0x100001, programNames.Main9);

        byte[] soundCpu = new byte[0x40_0000];
        LoadX16(entries, soundCpu, 0x000000, "epr-14945.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-14944.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-14943.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-14942.ic24");

        byte[] mcu = new byte[0x1_0000];
        LoadLinear(entries, mcu, 0x00000, "epr-14468-02.u3");
        DecryptGoldenAxe2Mcu(mcu);

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-14948.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-14947.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64Word(entries, sprites, 0x000000, "mpr-14949.ic32");
        Load64Word(entries, sprites, 0x000002, "mpr-14951.ic30");
        Load64Word(entries, sprites, 0x000004, "mpr-14953.ic28");
        Load64Word(entries, sprites, 0x000006, "mpr-14955.ic26");
        Load64Word(entries, sprites, 0x800000, "mpr-14950.ic31");
        Load64Word(entries, sprites, 0x800002, "mpr-14952.ic29");
        Load64Word(entries, sprites, 0x800004, "mpr-14954.ic27");
        Load64Word(entries, sprites, 0x800006, "mpr-14956.ic25");

        return new System32RomSet(driverName, mainCpu, soundCpu, mcu, tiles, sprites);
    }

    private static Dictionary<string, byte[]> ReadArchive(string path)
    {
        using IArchive archive = ArchiveFactory.Open(path);
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                continue;

            using Stream stream = entry.OpenEntryStream();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            entries[Path.GetFileName(entry.Key)] = memory.ToArray();
        }

        return entries;
    }

    private static void LoadLinear(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        if (offset + source.Length > destination.Length)
            throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 region.");

        source.CopyTo(destination.AsSpan(offset));
    }

    private static void LoadX4(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int reload = 0; reload < 4; reload++)
            CopyReload(source, destination, offset + reload * source.Length, name, "V60");
    }

    private static void LoadX16(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int reload = 0; reload < 16; reload++)
            CopyReload(source, destination, offset + reload * source.Length, name, "sound");
    }

    private static void Load16Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int i = 0; i < source.Length; i++)
        {
            int dst = offset + i * 2;
            if (dst >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 tile region.");
            destination[dst] = source[i];
        }
    }

    private static void Load16ByteX2(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        Load16Byte(source, destination, offset, name, "V60");
        Load16Byte(source, destination, offset + 2 * source.Length, name, "V60");
    }

    private static void Load16Byte(byte[] source, byte[] destination, int offset, string name, string regionName)
    {
        for (int i = 0; i < source.Length; i++)
        {
            int dst = offset + i * 2;
            if (dst >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 {regionName} region.");
            destination[dst] = source[i];
        }
    }

    private static void CopyReload(byte[] source, byte[] destination, int offset, string name, string regionName)
    {
        if (offset + source.Length > destination.Length)
            throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 {regionName} region.");

        source.CopyTo(destination.AsSpan(offset));
    }

    private static void Load64Word(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        int words = source.Length / 2;
        for (int i = 0; i < words; i++)
        {
            int src = i * 2;
            int dst = offset + i * 8;
            if (dst + 1 >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 sprite region.");
            destination[dst] = source[src];
            destination[dst + 1] = source[src + 1];
        }
    }

    private static byte[] Find(Dictionary<string, byte[]> entries, string name)
    {
        if (entries.TryGetValue(name, out byte[]? data))
            return data;

        string present = string.Join(", ", entries.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).Take(32));
        throw new InvalidDataException(
            string.Create(CultureInfo.InvariantCulture, $"Missing Sega System 32 ROM file '{name}'. Present files: {present}"));
    }

    private static void DecryptGoldenAxe2Mcu(byte[] rom)
    {
        byte[] copy = (byte[])rom.Clone();
        for (int i = 0; i < rom.Length; i++)
            rom[i] = copy[Bitswap16(i, 14, 11, 15, 12, 13, 4, 3, 7, 5, 10, 2, 8, 9, 6, 1, 0)];
    }

    private static int Bitswap16(int value, params int[] bits)
    {
        int result = 0;
        for (int i = 0; i < bits.Length; i++)
            result = (result << 1) | ((value >> bits[i]) & 1);

        return result;
    }

    private sealed record GoldenAxe2ProgramNames(string Main17, string Main8, string Main18, string Main9);
}
