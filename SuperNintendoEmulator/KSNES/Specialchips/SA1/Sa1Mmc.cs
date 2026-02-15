namespace KSNES.Specialchips.SA1;

internal enum BwramMapSource
{
    Normal,
    Bitmap
}

internal static class BwramMapSourceExtensions
{
    public static BwramMapSource FromBit(bool bit) => bit ? BwramMapSource.Bitmap : BwramMapSource.Normal;
}

internal enum BwramBitmapBits
{
    Two,
    Four
}

internal static class BwramBitmapBitsExtensions
{
    public static BwramBitmapBits FromBit(bool bit) => bit ? BwramBitmapBits.Two : BwramBitmapBits.Four;
}

internal sealed class Sa1Mmc
{
    private const uint DefaultBankCAddr = 0x000000;
    private const uint DefaultBankDAddr = 0x100000;
    private const uint DefaultBankEAddr = 0x200000;
    private const uint DefaultBankFAddr = 0x300000;

    public uint BankCBaseAddr = DefaultBankCAddr;
    public bool BankCLoRomMapped;
    public uint LoRomBankCAddr = DefaultBankCAddr;
    public uint BankDBaseAddr = DefaultBankDAddr;
    public bool BankDLoRomMapped;
    public uint LoRomBankDAddr = DefaultBankDAddr;
    public uint BankEBaseAddr = DefaultBankEAddr;
    public bool BankELoRomMapped;
    public uint LoRomBankEAddr = DefaultBankEAddr;
    public uint BankFBaseAddr = DefaultBankFAddr;
    public bool BankFLoRomMapped;
    public uint LoRomBankFAddr = DefaultBankFAddr;
    public uint SnesBwramBaseAddr;
    public uint Sa1BwramBaseAddr;
    public BwramMapSource Sa1BwramSource = BwramMapSource.Normal;
    public BwramBitmapBits BwramBitmapFormat = BwramBitmapBits.Four;

    public uint? MapRomAddress(uint address)
    {
        uint bank = (address >> 16) & 0xFF;
        uint offset = address & 0xFFFF;
        return (bank, offset) switch
        {
            (>= 0x00 and <= 0x1F, >= 0x8000) => LoRomMapAddress(LoRomBankCAddr, address),
            (>= 0x20 and <= 0x3F, >= 0x8000) => LoRomMapAddress(LoRomBankDAddr, address),
            (>= 0x80 and <= 0x9F, >= 0x8000) => LoRomMapAddress(LoRomBankEAddr, address),
            (>= 0xA0 and <= 0xBF, >= 0x8000) => LoRomMapAddress(LoRomBankFAddr, address),
            (>= 0xC0 and <= 0xCF, _) => BankCBaseAddr | (address & 0xFFFFF),
            (>= 0xD0 and <= 0xDF, _) => BankDBaseAddr | (address & 0xFFFFF),
            (>= 0xE0 and <= 0xEF, _) => BankEBaseAddr | (address & 0xFFFFF),
            (>= 0xF0 and <= 0xFF, _) => BankFBaseAddr | (address & 0xFFFFF),
            _ => (uint?)null
        };
    }

    public void WriteCxb(byte value)
    {
        BankCBaseAddr = (uint)(value & 0x07) << 20;
        BankCLoRomMapped = value.Bit(7);
        LoRomBankCAddr = BankCLoRomMapped ? BankCBaseAddr : DefaultBankCAddr;
    }

    public void WriteDxb(byte value)
    {
        BankDBaseAddr = (uint)(value & 0x07) << 20;
        BankDLoRomMapped = value.Bit(7);
        LoRomBankDAddr = BankDLoRomMapped ? BankDBaseAddr : DefaultBankDAddr;
    }

    public void WriteExb(byte value)
    {
        BankEBaseAddr = (uint)(value & 0x07) << 20;
        BankELoRomMapped = value.Bit(7);
        LoRomBankEAddr = BankELoRomMapped ? BankEBaseAddr : DefaultBankEAddr;
    }

    public void WriteFxb(byte value)
    {
        BankFBaseAddr = (uint)(value & 0x07) << 20;
        BankFLoRomMapped = value.Bit(7);
        LoRomBankFAddr = BankFLoRomMapped ? BankFBaseAddr : DefaultBankFAddr;
    }

    public void WriteBmaps(byte value)
    {
        SnesBwramBaseAddr = (uint)(value & 0x1F) << 13;
        if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal))
        {
            Console.WriteLine($"[BMAPS] val=0x{value:X2} snes_bwram_base=0x{SnesBwramBaseAddr:X5}");
        }
    }

    public void WriteBmap(byte value)
    {
        Sa1BwramBaseAddr = (uint)(value & 0x7F) << 13;
        Sa1BwramSource = BwramMapSourceExtensions.FromBit(value.Bit(7));
        if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal))
        {
            Console.WriteLine($"[BMAP] val=0x{value:X2} sa1_bwram_base=0x{Sa1BwramBaseAddr:X5} src={Sa1BwramSource}");
        }
    }

    public void WriteBbf(byte value)
    {
        BwramBitmapFormat = BwramBitmapBitsExtensions.FromBit(value.Bit(7));
    }

    private static uint LoRomMapAddress(uint baseAddr, uint address)
    {
        return baseAddr | (address & 0x7FFF) | ((address & 0x1F0000) >> 1);
    }
}
