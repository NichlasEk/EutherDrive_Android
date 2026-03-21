using System.Runtime.CompilerServices;

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
    private enum RomMapKind : byte
    {
        None,
        LoRom,
        Linear
    }

    private const uint DefaultBankCAddr = 0x000000;
    private const uint DefaultBankDAddr = 0x100000;
    private const uint DefaultBankEAddr = 0x200000;
    private const uint DefaultBankFAddr = 0x300000;
    private static readonly bool TraceBwramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal);
    private readonly uint[] _romBankBase = new uint[256];
    private readonly byte[] _romBankKind = new byte[256];

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

    public Sa1Mmc()
    {
        System.Array.Fill(_romBankKind, (byte)RomMapKind.None);
        WriteCxb(0x00);
        WriteDxb(0x01);
        WriteExb(0x02);
        WriteFxb(0x03);
    }

    public uint? MapRomAddress(uint address)
    {
        return TryMapRomAddress(address, out uint romAddress) ? romAddress : (uint?)null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMapRomAddress(uint address, out uint romAddress)
    {
        int bank = (int)((address >> 16) & 0xFF);
        RomMapKind kind = (RomMapKind)_romBankKind[bank];
        switch (kind)
        {
            case RomMapKind.LoRom:
                if ((address & 0xFFFF) < 0x8000)
                {
                    romAddress = 0;
                    return false;
                }

                romAddress = _romBankBase[bank] | (address & 0x7FFF) | ((address & 0x1F0000) >> 1);
                return true;
            case RomMapKind.Linear:
                romAddress = _romBankBase[bank] | (address & 0xFFFFF);
                return true;
            default:
                romAddress = 0;
                return false;
        }
    }

    public void WriteCxb(byte value)
    {
        BankCBaseAddr = (uint)(value & 0x07) << 20;
        BankCLoRomMapped = value.Bit(7);
        LoRomBankCAddr = BankCLoRomMapped ? BankCBaseAddr : DefaultBankCAddr;
        UpdateLoRomBanks(0x00, LoRomBankCAddr);
        UpdateLinearBanks(0xC0, BankCBaseAddr);
    }

    public void WriteDxb(byte value)
    {
        BankDBaseAddr = (uint)(value & 0x07) << 20;
        BankDLoRomMapped = value.Bit(7);
        LoRomBankDAddr = BankDLoRomMapped ? BankDBaseAddr : DefaultBankDAddr;
        UpdateLoRomBanks(0x20, LoRomBankDAddr);
        UpdateLinearBanks(0xD0, BankDBaseAddr);
    }

    public void WriteExb(byte value)
    {
        BankEBaseAddr = (uint)(value & 0x07) << 20;
        BankELoRomMapped = value.Bit(7);
        LoRomBankEAddr = BankELoRomMapped ? BankEBaseAddr : DefaultBankEAddr;
        UpdateLoRomBanks(0x80, LoRomBankEAddr);
        UpdateLinearBanks(0xE0, BankEBaseAddr);
    }

    public void WriteFxb(byte value)
    {
        BankFBaseAddr = (uint)(value & 0x07) << 20;
        BankFLoRomMapped = value.Bit(7);
        LoRomBankFAddr = BankFLoRomMapped ? BankFBaseAddr : DefaultBankFAddr;
        UpdateLoRomBanks(0xA0, LoRomBankFAddr);
        UpdateLinearBanks(0xF0, BankFBaseAddr);
    }

    public void WriteBmaps(byte value)
    {
        SnesBwramBaseAddr = (uint)(value & 0x1F) << 13;
        if (TraceBwramWatch)
        {
            Console.WriteLine($"[BMAPS] val=0x{value:X2} snes_bwram_base=0x{SnesBwramBaseAddr:X5}");
        }
    }

    public void WriteBmap(byte value)
    {
        Sa1BwramBaseAddr = (uint)(value & 0x7F) << 13;
        Sa1BwramSource = BwramMapSourceExtensions.FromBit(value.Bit(7));
        if (TraceBwramWatch)
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

    private void UpdateLoRomBanks(int startBank, uint baseAddr)
    {
        for (int i = 0; i < 0x20; i++)
        {
            int bank = startBank + i;
            _romBankBase[bank] = baseAddr;
            _romBankKind[bank] = (byte)RomMapKind.LoRom;
        }
    }

    private void UpdateLinearBanks(int startBank, uint baseAddr)
    {
        for (int i = 0; i < 0x10; i++)
        {
            int bank = startBank + i;
            _romBankBase[bank] = baseAddr;
            _romBankKind[bank] = (byte)RomMapKind.Linear;
        }
    }
}
