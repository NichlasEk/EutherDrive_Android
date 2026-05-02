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
            "alien3" => LoadAlien3(path, driverName),
            "ga2" or "ga2u" or "ga2j" => LoadGoldenAxe2(path, driverName),
            "arescue" => LoadAirRescue(path, driverName),
            "arabfgt" or "arabfgtu" or "arabfgtj" => LoadArabianFight(path, driverName),
            "brival" => LoadBurningRival(path, driverName),
            "darkedge" or "darkedgej" => LoadDarkEdge(path, driverName),
            "dbzvrvs" => LoadDragonBallZ(path, driverName),
            "f1en" => LoadF1ExhaustNote(path, driverName),
            "f1lap" => LoadF1SuperLap(path, driverName),
            "holo" => LoadHolosseum(path, driverName),
            "jpark" => LoadJurassicPark(path, driverName),
            "kokoroj2" => LoadKokoroji2(path, driverName),
            "radm" => LoadRadMobile(path, driverName),
            "radr" => LoadRadRally(path, driverName),
            "spidman" or "spidmanu" or "spidmanj" => LoadSpiderMan(path, driverName),
            "sonic" or "sonicp" => LoadSegaSonic(path, driverName),
            "svf" => LoadSuperVisualFootball(path, driverName),
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

    private static System32RomSet LoadAlien3(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX2(entries, mainCpu, 0x000000, "epr-15943.ic17");
        LoadX2(entries, mainCpu, 0x080000, "epr-15942.ic8");
        Load16Byte(entries, mainCpu, 0x100000, "mpr-15855.ic18");
        Load16Byte(entries, mainCpu, 0x100001, "mpr-15854.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX4(entries, soundCpu, 0x000000, "epr-15859a.ic36", "epr-15859.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-15858.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-15857.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-15856.ic24");

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-15863.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-15862.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "mpr-15864.ic32", "mpr-15866.ic30", "mpr-15868.ic28", "mpr-15870.ic26",
            "mpr-15865.ic31", "mpr-15867.ic29", "mpr-15869.ic27", "mpr-15871.ic25");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

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

    private static System32RomSet LoadAirRescue(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, "epr-14542.ic13");
        LoadX4(entries, mainCpu, 0x080000, "epr-14541.ic6");
        Load16Byte(entries, mainCpu, 0x100000, "epr-14509.ic14");
        Load16Byte(entries, mainCpu, 0x100001, "epr-14508.ic7");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX4(entries, soundCpu, 0x000000, "epr-14513.ic35");
        LoadX2(entries, soundCpu, 0x100000, "mpr-14512.ic31");
        LoadX2(entries, soundCpu, 0x200000, "mpr-14511.ic26");
        LoadX2(entries, soundCpu, 0x300000, "mpr-14510.ic22");

        byte[] tiles = new byte[0x20_0000];
        Load32Byte(entries, tiles, 0x000003, "mpr-14496.ic25");
        Load32Byte(entries, tiles, 0x000001, "mpr-14497.ic29");
        Load32Byte(entries, tiles, 0x000002, "mpr-14498.ic34");
        Load32Byte(entries, tiles, 0x000000, "mpr-14499.ic38");

        byte[] sprites = new byte[0x80_0000];
        Load64Byte(entries, sprites, 0x000007, "mpr-14500.ic24");
        Load64Byte(entries, sprites, 0x000006, "mpr-14501.ic28");
        Load64Byte(entries, sprites, 0x000005, "mpr-14502.ic33");
        Load64Byte(entries, sprites, 0x000004, "mpr-14503.ic37");
        Load64Byte(entries, sprites, 0x000003, "mpr-14504.ic23");
        Load64Byte(entries, sprites, 0x000002, "mpr-14505.ic27");
        Load64Byte(entries, sprites, 0x000001, "mpr-14506.ic32");
        Load64Byte(entries, sprites, 0x000000, "mpr-14507.ic36");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites, isMulti32: true);
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

    private static System32RomSet LoadBurningRival(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX8(entries, mainCpu, 0x000000, "epr-15722.ic8");
        Load16Byte(entries, mainCpu, 0x100000, "epr-15723.ic18");
        Load16Byte(entries, mainCpu, 0x100001, "epr-15724.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-15725.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-15627.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-15626.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-15625.ic24");

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-15629.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-15628.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "mpr-15637.ic32", "mpr-15635.ic30", "mpr-15633.ic28", "mpr-15631.ic26",
            "mpr-15636.ic31", "mpr-15634.ic29", "mpr-15632.ic27", "mpr-15630.ic25");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadDarkEdge(string path, string driverName)
    {
        string mainProgram = driverName switch
        {
            "darkedge" => "epr-15246.ic8",
            "darkedgej" => "epr-15244.ic8",
            _ => throw new NotSupportedException($"Unsupported Dark Edge driver '{driverName}'.")
        };

        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, mainProgram);

        byte[] soundCpu = new byte[0x40_0000];
        LoadX16(entries, soundCpu, 0x000000, "epr-15243.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-15242.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-15241.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-15240.ic24");

        byte[] tiles = new byte[0x10_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-15248.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-15247.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64Word(entries, sprites, 0x000000, "mpr-15249.ic32");
        Load64Word(entries, sprites, 0x000002, "mpr-15251.ic30");
        Load64Word(entries, sprites, 0x000004, "mpr-15253.ic28");
        Load64Word(entries, sprites, 0x000006, "mpr-15255.ic26");
        Load64Word(entries, sprites, 0x800000, "mpr-15250.ic31");
        Load64Word(entries, sprites, 0x800002, "mpr-15252.ic29");
        Load64Word(entries, sprites, 0x800004, "mpr-15254.ic27");
        Load64Word(entries, sprites, 0x800006, "mpr-15256.ic25");

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

    private static System32RomSet LoadDragonBallZ(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadLinear(entries, mainCpu, 0x000000, "epr-16543", "16543");
        LoadLinear(entries, mainCpu, 0x080000, "epr-16542a", "16542.a");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX4(entries, soundCpu, 0x000000, "epr-16541", "16541");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-16540", "16540");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-16539", "16539");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-16538", "16538");

        byte[] tiles = new byte[0x20_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-16545", "16545");
        Load16Byte(entries, tiles, 0x000001, "mpr-16544", "16544");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "16546", "16548", "16550", "16552",
            "16547", "16549", "16551", "16553");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadF1ExhaustNote(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX8(entries, mainCpu, 0x000000, "epr-14452a.ic6");
        Load16ByteX2(entries, mainCpu, 0x100000, "epr-14445.ic14");
        Load16ByteX2(entries, mainCpu, 0x100001, "epr-14444.ic7");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-14449.ic35");
        LoadX2(entries, soundCpu, 0x100000, "epr-14448.ic31");
        LoadX2(entries, soundCpu, 0x200000, "epr-14447.ic26");
        LoadX2(entries, soundCpu, 0x300000, "epr-14446.ic22");

        byte[] tiles = new byte[0x10_0000];
        Load32Byte(entries, tiles, 0x000000, "mpr-14362.ic38", "mpr-14362");
        Load32Byte(entries, tiles, 0x000002, "mpr-14361.ic34", "mpr-14361");
        Load32Byte(entries, tiles, 0x000001, "mpr-14360.ic29", "mpr-14360");
        Load32Byte(entries, tiles, 0x000003, "mpr-14359.ic25", "mpr-14359");

        byte[] sprites = new byte[0x80_0000];
        Load64Byte(entries, sprites, 0x000000, "mpr-14370.ic36", "mpr-14370");
        Load64Byte(entries, sprites, 0x000001, "mpr-14369.ic32", "mpr-14369");
        Load64Byte(entries, sprites, 0x000002, "mpr-14368.ic27", "mpr-14368");
        Load64Byte(entries, sprites, 0x000003, "mpr-14367.ic23", "mpr-14367");
        Load64Byte(entries, sprites, 0x000004, "mpr-14366.ic37", "mpr-14366");
        Load64Byte(entries, sprites, 0x000005, "mpr-14365.ic33", "mpr-14365");
        Load64Byte(entries, sprites, 0x000006, "mpr-14364.ic28", "mpr-14364");
        Load64Byte(entries, sprites, 0x000007, "mpr-14363.ic24", "mpr-14363");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites, isMulti32: true);
    }

    private static System32RomSet LoadF1SuperLap(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, "epr-15598.ic17");
        LoadX4(entries, mainCpu, 0x080000, "epr-15611.ic8");
        Load16ByteX2(entries, mainCpu, 0x100000, "epr-15596.ic18");
        Load16ByteX2(entries, mainCpu, 0x100001, "epr-15597.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-15592.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-15593.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-15594.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-15595.ic24");

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-15608.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-15609.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "mpr-15600.ic32", "mpr-15602.ic30", "mpr-15604.ic28", "mpr-15606.ic26",
            "mpr-15601.ic31", "mpr-15603.ic29", "mpr-15605.ic27", "mpr-15607.ic25");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadHolosseum(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, "epr-14977a");
        LoadX4(entries, mainCpu, 0x080000, "epr-14976a");
        Load16ByteX4(entries, mainCpu, 0x100000, "epr-15011");
        Load16ByteX4(entries, mainCpu, 0x100001, "epr-15010");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-14965");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-14964");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-14963");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-14962");

        byte[] tiles = new byte[0x100];
        byte[] sprites = new byte[0x80_0000];
        Load64Byte(entries, sprites, 0x000000, "mpr-14973");
        Load64Byte(entries, sprites, 0x000001, "mpr-14972");
        Load64Byte(entries, sprites, 0x000002, "mpr-14971");
        Load64Byte(entries, sprites, 0x000003, "mpr-14970");
        Load64Byte(entries, sprites, 0x000004, "mpr-14969");
        Load64Byte(entries, sprites, 0x000005, "mpr-14968");
        Load64Byte(entries, sprites, 0x000006, "mpr-14967");
        Load64Byte(entries, sprites, 0x000007, "mpr-14966");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadJurassicPark(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX2(entries, mainCpu, 0x000000, "epr-16402a.ic8");
        Load16Byte(entries, mainCpu, 0x100000, "epr-16395.ic18");
        Load16Byte(entries, mainCpu, 0x100001, "epr-16394.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX4(entries, soundCpu, 0x000000, "epr-16399.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-16398.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-16397.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-16396.ic24");

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-16404.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-16403.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "mpr-16405.ic32", "mpr-16407.ic30", "mpr-16409.ic28", "mpr-16411.ic26",
            "mpr-16406.ic31", "mpr-16408.ic29", "mpr-16410.ic27", "mpr-16412.ic25");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadKokoroji2(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX8(entries, mainCpu, 0x000000, "epr-16186.ic8");
        Load16Byte(entries, mainCpu, 0x100000, "epr-16183.ic18");
        Load16Byte(entries, mainCpu, 0x100001, "epr-16182.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX4(entries, soundCpu, 0x000000, "epr-16185.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-16184.ic35");

        byte[] tiles = new byte[0x40_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-16188.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-16187.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "mpr-16189.ic32", "mpr-16191.ic30", "mpr-16193.ic28", "mpr-16195.ic26",
            "mpr-16190.ic31", "mpr-16192.ic29", "mpr-16194.ic27", "mpr-16196.ic25");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadRadMobile(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX8(entries, mainCpu, 0x000000, "epr-13693.ic21");
        Load16Byte(entries, mainCpu, 0x100000, "epr-13525.ic37");
        Load16Byte(entries, mainCpu, 0x100001, "epr-13526.ic38");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-13527.ic9");
        LoadX2(entries, soundCpu, 0x100000, "epr-13523.ic14");
        LoadX2(entries, soundCpu, 0x200000, "epr-13699.ic20");
        LoadX2(entries, soundCpu, 0x300000, "epr-13523.ic22");

        byte[] tiles = new byte[0x20_0000];
        Load32Byte(entries, tiles, 0x000000, "mpr-13519.ic3");
        Load32Byte(entries, tiles, 0x000002, "mpr-13520.ic7");
        Load32Byte(entries, tiles, 0x000001, "mpr-13521.ic12");
        Load32Byte(entries, tiles, 0x000003, "mpr-13522.ic18");

        byte[] sprites = LoadRadSprites(entries);
        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
    }

    private static System32RomSet LoadRadRally(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX8(entries, mainCpu, 0x000000, "epr-14241.ic21");
        Load16Byte(entries, mainCpu, 0x100000, "epr-14106.ic37");
        Load16Byte(entries, mainCpu, 0x100001, "epr-14107.ic38");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-14108.ic9");
        LoadX2(entries, soundCpu, 0x100000, "epr-14109.ic14");
        LoadX2(entries, soundCpu, 0x200000, "epr-14110.ic20");
        LoadX2(entries, soundCpu, 0x300000, "epr-14237.ic22");

        byte[] tiles = new byte[0x10_0000];
        Load32Byte(entries, tiles, 0x000000, "epr-14102.ic3");
        Load32Byte(entries, tiles, 0x000002, "epr-14103.ic7");
        Load32Byte(entries, tiles, 0x000001, "epr-14104.ic12");
        Load32Byte(entries, tiles, 0x000003, "epr-14105.ic18");

        byte[] sprites = LoadRadSprites(entries);
        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
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

    private static System32RomSet LoadSuperVisualFootball(string path, string driverName)
    {
        Dictionary<string, byte[]> entries = ReadArchive(path);
        byte[] mainCpu = new byte[0x20_0000];
        LoadX4(entries, mainCpu, 0x000000, "epr-16872a.ic17");
        LoadX4(entries, mainCpu, 0x080000, "epr-16871a.ic8");
        Load16Byte(entries, mainCpu, 0x100000, "epr-16865.ic18");
        Load16Byte(entries, mainCpu, 0x100001, "epr-16864.ic9");

        byte[] soundCpu = new byte[0x40_0000];
        LoadX8(entries, soundCpu, 0x000000, "epr-16866.ic36");
        LoadLinear(entries, soundCpu, 0x100000, "mpr-16779.ic35");
        LoadLinear(entries, soundCpu, 0x200000, "mpr-16778.ic34");
        LoadLinear(entries, soundCpu, 0x300000, "mpr-16777.ic24");

        byte[] tiles = new byte[0x20_0000];
        Load16Byte(entries, tiles, 0x000000, "mpr-16784.ic14");
        Load16Byte(entries, tiles, 0x000001, "mpr-16783.ic5");

        byte[] sprites = new byte[0x100_0000];
        Load64WordPairs(entries, sprites,
            "mpr-16785.ic32", "mpr-16787.ic30", "mpr-16789.ic28", "mpr-16791.ic26",
            "mpr-16860.ic31", "mpr-16861.ic29", "mpr-16862.ic27", "mpr-16863.ic25");

        return CreateNoMcuSet(driverName, mainCpu, soundCpu, tiles, sprites);
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
        using IArchive archive = RomArchiveExtractor.OpenArchive(path);
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

    private static void LoadLinear(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
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

    private static void Load16Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
        Load16Byte(source, destination, offset, name, "V60");
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

    private static void Load64WordPairs(
        Dictionary<string, byte[]> entries,
        byte[] destination,
        string word0,
        string word1,
        string word2,
        string word3,
        string word4,
        string word5,
        string word6,
        string word7)
    {
        Load64Word(entries, destination, 0x000000, word0);
        Load64Word(entries, destination, 0x000002, word1);
        Load64Word(entries, destination, 0x000004, word2);
        Load64Word(entries, destination, 0x000006, word3);
        Load64Word(entries, destination, 0x800000, word4);
        Load64Word(entries, destination, 0x800002, word5);
        Load64Word(entries, destination, 0x800004, word6);
        Load64Word(entries, destination, 0x800006, word7);
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

    private static void Load32Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
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

    private static void Load64Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
    {
        byte[] source = FindAny(entries, names);
        string name = names[0];
        for (int i = 0; i < source.Length; i++)
        {
            int dst = offset + i * 8;
            if (dst >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the Sega System 32 sprite region.");
            destination[dst] = source[i];
        }
    }

    private static byte[] LoadRadSprites(Dictionary<string, byte[]> entries)
    {
        byte[] sprites = new byte[0x80_0000];
        Load64Byte(entries, sprites, 0x000000, "mpr-13511.ic1");
        Load64Byte(entries, sprites, 0x000001, "mpr-13512.ic5");
        Load64Byte(entries, sprites, 0x000002, "mpr-13513.ic10");
        Load64Byte(entries, sprites, 0x000003, "mpr-13514.ic16");
        Load64Byte(entries, sprites, 0x000004, "mpr-13515.ic2");
        Load64Byte(entries, sprites, 0x000005, "mpr-13516.ic6");
        Load64Byte(entries, sprites, 0x000006, "mpr-13517.ic11");
        Load64Byte(entries, sprites, 0x000007, "mpr-13518.ic17");
        return sprites;
    }

    private static System32RomSet CreateNoMcuSet(
        string driverName,
        byte[] mainCpu,
        byte[] soundCpu,
        byte[] tiles,
        byte[] sprites,
        bool isMulti32 = false)
        => new(
            driverName,
            mainCpu,
            soundCpu,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            V25.GoldenAxe2OpcodeTable,
            tiles,
            sprites,
            isMulti32);

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
