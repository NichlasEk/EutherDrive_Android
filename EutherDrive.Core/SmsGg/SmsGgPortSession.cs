namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgPortSession
{
    private static readonly uint[] s_crc32Table = BuildCrc32Table();
    private readonly SmsGgMemory _memory;

    private SmsGgPortSession(
        string romPath,
        string displayName,
        SmsGgHardware hardware,
        byte[] romBytes,
        uint crc32,
        bool hasBatteryBackedSram,
        SmsGgMemory memory)
    {
        RomPath = romPath;
        DisplayName = displayName;
        Hardware = hardware;
        RomBytes = romBytes;
        Crc32 = crc32;
        HasBatteryBackedSram = hasBatteryBackedSram;
        _memory = memory;
    }

    public string RomPath { get; }
    public string DisplayName { get; }
    public SmsGgHardware Hardware { get; }
    public byte[] RomBytes { get; }
    public uint Crc32 { get; }
    public bool HasBatteryBackedSram { get; }
    public SmsGgInputState InputState { get; } = new();
    public SmsGgMemory Memory => _memory;
    public SmsGgRegion Region => _memory.GuessCartridgeRegion();
    public SmsGgMapperType MapperType => _memory.MapperType;

    public static SmsGgPortSession Load(string romPath)
    {
        if (!File.Exists(romPath))
            throw new FileNotFoundException("ROM not found", romPath);

        var loaded = SmsGgRomLoader.Load(romPath);
        byte[] romBytes = loaded.RomBytes;
        SmsGgHardware hardware = DetectHardware(loaded.DisplayName, romBytes);
        uint crc32 = ComputeCrc32(romBytes);
        var memory = new SmsGgMemory(romBytes, biosRom: null, initialCartridgeRam: null, hardware);
        bool hasBatteryBackedSram = memory.CartridgeHasBattery;
        string displayName = loaded.DisplayName;

        return new SmsGgPortSession(
            romPath,
            displayName,
            hardware,
            romBytes,
            crc32,
            hasBatteryBackedSram,
            memory);
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool button1,
        bool button2,
        bool pause)
    {
        InputState.UpdateFromEutherInput(up, down, left, right, button1, button2, pause);
    }

    public RomInfo BuildRomInfo()
    {
        string hardwareLabel = Hardware == SmsGgHardware.GameGear ? "Game Gear" : "Master System";
        string regionLabel = Region == SmsGgRegion.International ? "International" : "Domestic";
        string sizeLabel = RomBytes.Length >= 1024
            ? $"{RomBytes.Length / 1024} KiB"
            : $"{RomBytes.Length} bytes";

        return new RomInfo
        {
            Summary = $"{hardwareLabel}: {DisplayName}",
            RegionHint = ConsoleRegion.Auto,
            ExtraInfo =
                $"Port seed: jgenesis smsgg-core\n" +
                $"Runtime: MdTracer fallback\n" +
                $"Size: {sizeLabel}\n" +
                $"CRC32: {Crc32:X8}\n" +
                $"Mapper: {MapperType}\n" +
                $"Region: {regionLabel}\n" +
                $"Battery SRAM: {(HasBatteryBackedSram ? "Yes" : "No")}"
        };
    }

    private static SmsGgHardware DetectHardware(string romPath, byte[] romBytes)
    {
        string ext = Path.GetExtension(romPath).ToLowerInvariant();
        if (ext == ".gg")
            return SmsGgHardware.GameGear;

        if (LooksLikeGameGearHeader(romBytes))
            return SmsGgHardware.GameGear;

        return SmsGgHardware.MasterSystem;
    }

    private static bool LooksLikeGameGearHeader(byte[] romBytes)
    {
        if (romBytes.Length < 0x8000)
            return false;

        ReadOnlySpan<byte> tmr = "TMR SEGA"u8;
        ReadOnlySpan<int> candidates = stackalloc[] { 0x1FF0, 0x3FF0, 0x7FF0 };
        foreach (int offset in candidates)
        {
            if (offset + 0x10 > romBytes.Length)
                continue;

            if (!romBytes.AsSpan(offset, tmr.Length).SequenceEqual(tmr))
                continue;

            byte productRegion = romBytes[offset + 0x0F];
            int regionCode = productRegion >> 4;
            if (regionCode is 5 or 6 or 7)
                return true;
        }

        return false;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = s_crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;

            table[i] = crc;
        }

        return table;
    }
}
