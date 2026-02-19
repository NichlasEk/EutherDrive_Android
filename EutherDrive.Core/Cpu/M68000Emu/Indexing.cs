namespace EutherDrive.Core.Cpu.M68000Emu;

internal enum IndexSize
{
    SignExtendedWord,
    LongWord,
}

internal readonly struct IndexRegister
{
    public readonly bool IsAddress;
    public readonly DataRegister DataReg;
    public readonly AddressRegister AddrReg;

    private IndexRegister(bool isAddress, DataRegister dataReg, AddressRegister addrReg)
    {
        IsAddress = isAddress;
        DataReg = dataReg;
        AddrReg = addrReg;
    }

    public static IndexRegister Data(DataRegister reg) => new(false, reg, default);
    public static IndexRegister Address(AddressRegister reg) => new(true, default, reg);

    public uint Read(Registers regs, IndexSize size)
    {
        uint raw = IsAddress ? AddrReg.Read(regs) : DataReg.Read(regs);
        return size == IndexSize.SignExtendedWord ? (uint)(short)raw : raw;
    }
}

internal static class Indexing
{
    public static (IndexRegister Reg, IndexSize Size) ParseIndex(ushort extension)
    {
        byte registerNumber = (byte)((extension >> 12) & 0x07);
        IndexRegister reg = extension.Test(15)
            ? IndexRegister.Address(new AddressRegister(registerNumber))
            : IndexRegister.Data(new DataRegister(registerNumber));

        IndexSize size = extension.Test(11) ? IndexSize.LongWord : IndexSize.SignExtendedWord;
        return (reg, size);
    }
}
