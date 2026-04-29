using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using EutherDrive.Core.Cpu.V25Emu;

namespace EutherDrive.Core.Arcade.System32;

// ROM region definitions are translated from MAME's BSD-3-Clause Sega System
// 32 driver by Aaron Giles.
internal sealed class System32RomSet
{
    public System32RomSet(
        string driverName,
        byte[] mainCpu,
        byte[] soundCpu,
        byte[] multiPcm,
        byte[] mcu,
        byte[] mcuOpcodeTable,
        byte[] tiles,
        byte[] sprites,
        bool isMulti32 = false)
    {
        DriverName = driverName;
        MainCpu = mainCpu;
        SoundCpu = soundCpu;
        MultiPcm = multiPcm;
        Mcu = mcu;
        McuOpcodeTable = mcuOpcodeTable;
        Tiles = tiles;
        Sprites = sprites;
        IsMulti32 = isMulti32;
    }

    public string DriverName { get; }
    public byte[] MainCpu { get; }
    public byte[] SoundCpu { get; }
    public byte[] MultiPcm { get; }
    public byte[] Mcu { get; }
    public byte[] McuOpcodeTable { get; }
    public byte[] Tiles { get; }
    public byte[] Sprites { get; }
    public bool IsMulti32 { get; }

    private static readonly byte[] ArabianFightOpcodeTable =
    {
        0x00,0x00,0x43,0x00,0x00,0x00,0x83,0x00,0x00,0x00,0xea,0x00,0x00,0xbc,0x73,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x3a,0x00,0x00,0xbe,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x80,0x00,
        0x00,0xb5,0x00,0x00,0x00,0x00,0x00,0x26,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xe8,0x8d,0x00,0x8b,0x00,
        0x00,0x00,0x00,0xfa,0x00,0x8a,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xba,0x88,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xbb,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x75,0x00,0xbf,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x03,0x3b,0x8e,0x74,0x00,0x00,0x81,0x00,
        0x00,0x00,0x00,0xc3,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xb9,0xb2,0x00,0x00,0x00,0x00,0x49,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xeb,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x02,0xb8
    };

    public static System32RomSet Load(string path)
    {
        string driverName = CanonicalDriverName(Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant());
        return driverName switch
        {
            "ga2" or "ga2u" or "ga2j" => LoadGoldenAxe2(path, driverName),
            "arabfgt" or "arabfgtu" or "arabfgtj" => LoadArabianFight(path, driverName),
            "spidman" or "spidmanu" or "spidmanj" => LoadSpiderMan(path, driverName),
            "sonic" or "sonicp" => LoadSegaSonic(path, driverName),
            "orunners" or "orunnersu" or "orunnersj" => LoadOutRunners(path, driverName),
            _ => throw new NotSupportedException($"Unsupported Sega System 32 driver '{driverName}'.")
        };
    }

    public static string CanonicalDriverName(string driverName)
        => driverName switch
        {
            "outrunners" => "orunners",
            _ => driverName
        };

    private static System32RomSet LoadGoldenAxe2(string path, string driverName)
    {
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
        DecryptV25Mcu(mcu);

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

        return new System32RomSet(driverName, mainCpu, soundCpu, Array.Empty<byte>(), mcu, V25.GoldenAxe2OpcodeTable, tiles, sprites);
    }

    private static System32RomSet LoadArabianFight(string path, string driverName)
    {
        string mainProgram = driverName switch
        {
            "arabfgt" => "epr-14609.ic8",
            "arabfgtu" => "epr-14608.ic8",
            "arabfgtj" => "epr-14597.ic8",
            _ => throw new NotSupportedException($"Unsupported Arabian Fight driver '{driverName}'.")
        };

        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX8(entries, mainCpu, 0x000000, mainProgram);
        Load16ByteX2(entries, mainCpu, 0x100000, "epr-14592.ic18");
        Load16ByteX2(entries, mainCpu, 0x100001, "epr-14591.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-14596.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-14595f.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-14594f.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-14593f.ic24");

        byte[] mcu = new byte[0x1_0000];
        LoadLinear(entries, mcu, 0x00000, "epr-14468-01.u3");
        DecryptV25Mcu(mcu);

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-14599f.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-14598f.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64Word(entries, sprites, 0x000000, "mpr-14600f.ic32");
        Load64Word(entries, sprites, 0x000002, "mpr-14602.ic30");
        Load64Word(entries, sprites, 0x000004, "mpr-14604.ic28");
        Load64Word(entries, sprites, 0x000006, "mpr-14606.ic26");
        Load64Word(entries, sprites, 0x800000, "mpr-14601f.ic31");
        Load64Word(entries, sprites, 0x800002, "mpr-14603.ic29");
        Load64Word(entries, sprites, 0x800004, "mpr-14605.ic27");
        Load64Word(entries, sprites, 0x800006, "mpr-14607.ic25");

        return new System32RomSet(driverName, mainCpu, soundCpu, Array.Empty<byte>(), mcu, ArabianFightOpcodeTable, tiles, sprites);
    }

    private static System32RomSet LoadSpiderMan(string path, string driverName)
    {
        SpiderManProgramNames programNames = driverName switch
        {
            "spidman" => new SpiderManProgramNames("epr-14307.ic13", "epr-14306.ic7"),
            "spidmanu" => new SpiderManProgramNames("epr-14303a.ic13", "epr-14302a.ic7"),
            "spidmanj" => new SpiderManProgramNames("epr-14287.ic13", "epr-14286.ic7"),
            _ => throw new NotSupportedException($"Unsupported Spider-Man driver '{driverName}'.")
        };

        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, programNames.Main13);
        LoadX4(entries, mainCpu, 0x080000, programNames.Main7);
        Load16ByteX4(entries, mainCpu, 0x100000, "epr-14281.ic14");
        Load16ByteX4(entries, mainCpu, 0x100001, "epr-14280.ic6", "epr-14280.ic7");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX4(entries, soundCpu, 0x000000, "epr-14285.ic35");
        LoadX2(entries, soundCpu, 0x100000, "mpr-14284.ic31");
        LoadX2(entries, soundCpu, 0x200000, "mpr-14283.ic26");
        LoadX2(entries, soundCpu, 0x300000, "mpr-14282.ic22");

        byte[] tiles = new byte[0x40_0000];
        Load32Byte(entries, tiles, 0x000000, "mpr-14291-s.ic38");
        Load32Byte(entries, tiles, 0x000002, "mpr-14290-s.ic34");
        Load32Byte(entries, tiles, 0x000001, "mpr-14289-s.ic29");
        Load32Byte(entries, tiles, 0x000003, "mpr-14288-s.ic25");

        byte[] sprites = new byte[0x80_0000];
        Load64Byte(entries, sprites, 0x000000, "mpr-14299-h.ic36");
        Load64Byte(entries, sprites, 0x000001, "mpr-14298-h.ic32");
        Load64Byte(entries, sprites, 0x000002, "mpr-14297-h.ic27");
        Load64Byte(entries, sprites, 0x000003, "mpr-14296-h.ic23");
        Load64Byte(entries, sprites, 0x000004, "mpr-14295-h.ic37");
        Load64Byte(entries, sprites, 0x000005, "mpr-14294-h.ic33");
        Load64Byte(entries, sprites, 0x000006, "mpr-14293-s.ic28");
        Load64Byte(entries, sprites, 0x000007, "mpr-14292-s.ic24");

        return new System32RomSet(
            driverName,
            mainCpu,
            soundCpu,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            V25.GoldenAxe2OpcodeTable,
            tiles,
            sprites);
    }

    private static System32RomSet LoadSegaSonic(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        byte[] soundCpu = new byte[0x40_0000];
        byte[] tiles;
        byte[] sprites;

        if (driverName == "sonic")
        {
            LoadX4(entries, mainCpu, 0x000000, "epr-15787c.ic17", "epr-c-87.ic17");
            LoadX4(entries, mainCpu, 0x080000, "epr-15786c.ic8", "epr-c-86.ic8");
            Load16ByteX2(entries, mainCpu, 0x100000, "epr-15781c.ic18", "epr-c-81.ic18");
            Load16ByteX2(entries, mainCpu, 0x100001, "epr-15780c.ic9", "epr-c-80.ic9");

            LoadX4(entries, soundCpu, 0x000000, "epr-15785.ic36");
            LoadLinear(entries, soundCpu, 0x100000, "mpr-15784.ic35");
            LoadLinear(entries, soundCpu, 0x200000, "mpr-15783.ic34");
            LoadLinear(entries, soundCpu, 0x300000, "mpr-15782.ic33");

            tiles = new byte[0x20_0000];
            Load16Byte(entries, tiles, 0x000000, "mpr-15789.ic14");
            Load16Byte(entries, tiles, 0x000001, "mpr-15788.ic5");

            sprites = new byte[0x100_0000];
            Load64Word(entries, sprites, 0x000000, "mpr-15790.ic32");
            Load64Word(entries, sprites, 0x000002, "mpr-15792.ic30");
            Load64Word(entries, sprites, 0x000004, "mpr-15794.ic28");
            Load64Word(entries, sprites, 0x000006, "mpr-15796.ic26");
            Load64Word(entries, sprites, 0x800000, "mpr-15791.ic31");
            Load64Word(entries, sprites, 0x800002, "mpr-15793.ic29");
            Load64Word(entries, sprites, 0x800004, "mpr-15795.ic27");
            Load64Word(entries, sprites, 0x800006, "mpr-15797.ic25");
        }
        else
        {
            LoadX4(entries, mainCpu, 0x000000, "sonpg0.bin");
            LoadX4(entries, mainCpu, 0x080000, "sonpg1.bin");
            Load16ByteX2(entries, mainCpu, 0x100000, "sonpd0.bin");
            Load16ByteX2(entries, mainCpu, 0x100001, "sonpd1.bin");

            LoadX4(entries, soundCpu, 0x000000, "sonsnd0.bin");
            LoadLinear(entries, soundCpu, 0x100000, "sonsnd1.bin");
            LoadLinear(entries, soundCpu, 0x200000, "sonsnd2.bin");
            LoadLinear(entries, soundCpu, 0x300000, "sonsnd3.bin");

            tiles = new byte[0x20_0000];
            Load32Byte(entries, tiles, 0x000000, "sonscl0.bin");
            Load32Byte(entries, tiles, 0x000002, "sonscl1.bin");
            Load32Byte(entries, tiles, 0x000001, "sonscl2.bin");
            Load32Byte(entries, tiles, 0x000003, "sonscl3.bin");

            sprites = new byte[0x80_0000];
            Load64Byte(entries, sprites, 0x000000, "sonobj0.bin");
            Load64Byte(entries, sprites, 0x000001, "sonobj1.bin");
            Load64Byte(entries, sprites, 0x000002, "sonobj2.bin");
            Load64Byte(entries, sprites, 0x000003, "sonobj3.bin");
            Load64Byte(entries, sprites, 0x000004, "sonobj4.bin");
            Load64Byte(entries, sprites, 0x000005, "sonobj5.bin");
            Load64Byte(entries, sprites, 0x000006, "sonobj6.bin");
            Load64Byte(entries, sprites, 0x000007, "sonobj7.bin");
        }

        return new System32RomSet(
            driverName,
            mainCpu,
            soundCpu,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            V25.GoldenAxe2OpcodeTable,
            tiles,
            sprites);
    }

    private static System32RomSet LoadOutRunners(string path, string driverName)
    {
        OutRunnersProgramNames programNames = driverName switch
        {
            "orunners" => new OutRunnersProgramNames("epr-15620.ic37", "epr-15621.ic40"),
            "orunnersu" => new OutRunnersProgramNames("epr-15618.ic37", "epr-15619.ic40"),
            "orunnersj" => new OutRunnersProgramNames("epr-15616.ic37", "epr-15617.ic40"),
            _ => throw new NotSupportedException($"Unsupported OutRunners driver '{driverName}'.")
        };

        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        Load32WordX4(entries, mainCpu, 0x000000, programNames.Main37);
        Load32WordX4(entries, mainCpu, 0x000002, programNames.Main40);
        Load32Word(entries, mainCpu, 0x100000, "mpr-15538.ic36");
        Load32Word(entries, mainCpu, 0x100002, "mpr-15539.ic39");

        byte[] soundCpu = new byte[0x80_000];
        LoadLinear(entries, soundCpu, 0x00000, "epr-15550.ic31");

        byte[] multiPcm = new byte[0x40_0000];
        LoadLinear(entries, multiPcm, 0x000000, "mpr-15551.ic1");
        LoadLinear(entries, multiPcm, 0x200000, "mpr-15552.ic2");

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-15548.ic3");
        Load16Byte(entries, tiles, 0x000001, "mpr-15549.ic11");

        byte[] sprites = new byte[0x100_0000];
        Load64Word(entries, sprites, 0x000000, "mpr-15540.ic14");
        Load64Word(entries, sprites, 0x000002, "mpr-15542.ic15");
        Load64Word(entries, sprites, 0x000004, "mpr-15544.ic10");
        Load64Word(entries, sprites, 0x000006, "mpr-15546.ic38");
        Load64Word(entries, sprites, 0x800000, "mpr-15541.ic22");
        Load64Word(entries, sprites, 0x800002, "mpr-15543.ic23");
        Load64Word(entries, sprites, 0x800004, "mpr-15545.ic18");
        Load64Word(entries, sprites, 0x800006, "mpr-15547.ic41");

        return new System32RomSet(
            driverName,
            mainCpu,
            soundCpu,
            multiPcm,
            Array.Empty<byte>(),
            V25.GoldenAxe2OpcodeTable,
            tiles,
            sprites,
            isMulti32: true);
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

    private static void LoadX4(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
        for (int reload = 0; reload < 4; reload++)
            CopyReload(source, destination, offset + reload * source.Length, name, "V60");
    }

    private static void LoadX8(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int reload = 0; reload < 8; reload++)
            CopyReload(source, destination, offset + reload * source.Length, name, "V60");
    }

    private static void LoadX16(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int reload = 0; reload < 16; reload++)
            CopyReload(source, destination, offset + reload * source.Length, name, "sound");
    }

    private static void LoadX2(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int reload = 0; reload < 2; reload++)
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

    private static void Load16ByteX2(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
        Load16Byte(source, destination, offset, name, "V60");
        Load16Byte(source, destination, offset + 2 * source.Length, name, "V60");
    }

    private static void Load16ByteX4(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
        for (int reload = 0; reload < 4; reload++)
            Load16Byte(source, destination, offset + reload * 2 * source.Length, name, "V60");
    }

    private static void Load32Word(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        Load32Word(source, destination, offset, name, "V60");
    }

    private static void Load32WordX4(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int reload = 0; reload < 4; reload++)
            Load32Word(source, destination, offset + reload * 2 * source.Length, name, "V60");
    }

    private static void Load32Word(byte[] source, byte[] destination, int offset, string name, string regionName)
    {
        int words = source.Length / 2;
        for (int i = 0; i < words; i++)
        {
            int src = i * 2;
            int dst = offset + i * 4;
            if (dst + 1 >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 {regionName} region.");
            destination[dst] = source[src];
            destination[dst + 1] = source[src + 1];
        }
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

    private static void Load32Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int i = 0; i < source.Length; i++)
        {
            int dst = offset + i * 4;
            if (dst >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 tile region.");
            destination[dst] = source[i];
        }
    }

    private static void Load64Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
    {
        byte[] source = Find(entries, name);
        for (int i = 0; i < source.Length; i++)
        {
            int dst = offset + i * 8;
            if (dst >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 sprite region.");
            destination[dst] = source[i];
        }
    }

    private static byte[] Find(Dictionary<string, byte[]> entries, string name)
    {
        if (entries.TryGetValue(name, out byte[]? data))
            return data;

        string normalizedName = NormalizeRomName(name);
        foreach ((string key, byte[] value) in entries)
        {
            if (NormalizeRomName(key) == normalizedName)
                return value;
        }

        string present = string.Join(", ", entries.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).Take(32));
        throw new InvalidDataException(
            string.Create(CultureInfo.InvariantCulture, $"Missing Sega System 32 ROM file '{name}'. Present files: {present}"));
    }

    private static byte[] FindAny(Dictionary<string, byte[]> entries, params string[] names)
    {
        foreach (string name in names)
        {
            try
            {
                return Find(entries, name);
            }
            catch (InvalidDataException)
            {
            }
        }

        string present = string.Join(", ", entries.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).Take(32));
        throw new InvalidDataException(
            string.Create(CultureInfo.InvariantCulture, $"Missing Sega System 32 ROM file '{names[0]}'. Present files: {present}"));
    }

    private static void DecryptV25Mcu(byte[] rom)
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
    private sealed record SpiderManProgramNames(string Main13, string Main7);
    private sealed record OutRunnersProgramNames(string Main37, string Main40);

    private static string NormalizeRomName(string name)
        => name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
}
