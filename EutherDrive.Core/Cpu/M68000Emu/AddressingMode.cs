namespace EutherDrive.Core.Cpu.M68000Emu;

internal enum IndexSize
{
    SignExtendedWord,
    LongWord,
}

internal readonly struct AddressingMode
{
    public readonly AddressingModeKind Kind;
    public readonly DataRegister DataReg;
    public readonly AddressRegister AddrReg;
    public readonly byte QuickValue;

    private AddressingMode(AddressingModeKind kind, DataRegister dataReg, AddressRegister addrReg, byte quickValue)
    {
        Kind = kind;
        DataReg = dataReg;
        AddrReg = addrReg;
        QuickValue = quickValue;
    }

    public static AddressingMode DataDirect(DataRegister reg) =>
        new(AddressingModeKind.DataDirect, reg, default, 0);
    public static AddressingMode AddressDirect(AddressRegister reg) =>
        new(AddressingModeKind.AddressDirect, default, reg, 0);
    public static AddressingMode AddressIndirect(AddressRegister reg) =>
        new(AddressingModeKind.AddressIndirect, default, reg, 0);
    public static AddressingMode AddressIndirectPostincrement(AddressRegister reg) =>
        new(AddressingModeKind.AddressIndirectPostincrement, default, reg, 0);
    public static AddressingMode AddressIndirectPredecrement(AddressRegister reg) =>
        new(AddressingModeKind.AddressIndirectPredecrement, default, reg, 0);
    public static AddressingMode AddressIndirectDisplacement(AddressRegister reg) =>
        new(AddressingModeKind.AddressIndirectDisplacement, default, reg, 0);
    public static AddressingMode AddressIndirectIndexed(AddressRegister reg) =>
        new(AddressingModeKind.AddressIndirectIndexed, default, reg, 0);
    public static AddressingMode PcRelativeDisplacement() =>
        new(AddressingModeKind.PcRelativeDisplacement, default, default, 0);
    public static AddressingMode PcRelativeIndexed() =>
        new(AddressingModeKind.PcRelativeIndexed, default, default, 0);
    public static AddressingMode AbsoluteShort() =>
        new(AddressingModeKind.AbsoluteShort, default, default, 0);
    public static AddressingMode AbsoluteLong() =>
        new(AddressingModeKind.AbsoluteLong, default, default, 0);
    public static AddressingMode Immediate() =>
        new(AddressingModeKind.Immediate, default, default, 0);
    public static AddressingMode Quick(byte value) =>
        new(AddressingModeKind.Quick, default, default, value);

    public bool IsDataDirect => Kind == AddressingModeKind.DataDirect;
    public bool IsAddressDirect => Kind == AddressingModeKind.AddressDirect;

    public bool IsMemory =>
        Kind == AddressingModeKind.AddressIndirect
        || Kind == AddressingModeKind.AddressIndirectPostincrement
        || Kind == AddressingModeKind.AddressIndirectPredecrement
        || Kind == AddressingModeKind.AddressIndirectDisplacement
        || Kind == AddressingModeKind.AddressIndirectIndexed
        || Kind == AddressingModeKind.PcRelativeDisplacement
        || Kind == AddressingModeKind.PcRelativeIndexed
        || Kind == AddressingModeKind.AbsoluteShort
        || Kind == AddressingModeKind.AbsoluteLong;

    public uint AddressCalculationCycles(OpSize size)
    {
        bool longWord = size == OpSize.LongWord;
        switch (Kind)
        {
            case AddressingModeKind.DataDirect:
            case AddressingModeKind.AddressDirect:
            case AddressingModeKind.Quick:
                return 0;
            case AddressingModeKind.AddressIndirect:
            case AddressingModeKind.AddressIndirectPostincrement:
            case AddressingModeKind.Immediate:
                return longWord ? 8u : 4u;
            case AddressingModeKind.AddressIndirectPredecrement:
                return longWord ? 10u : 6u;
            case AddressingModeKind.AddressIndirectDisplacement:
            case AddressingModeKind.PcRelativeDisplacement:
            case AddressingModeKind.AbsoluteShort:
                return longWord ? 12u : 8u;
            case AddressingModeKind.AddressIndirectIndexed:
            case AddressingModeKind.PcRelativeIndexed:
                return longWord ? 14u : 10u;
            case AddressingModeKind.AbsoluteLong:
                return longWord ? 16u : 12u;
            default:
                return 0;
        }
    }
}

internal enum AddressingModeKind
{
    DataDirect,
    AddressDirect,
    AddressIndirect,
    AddressIndirectPostincrement,
    AddressIndirectPredecrement,
    AddressIndirectDisplacement,
    AddressIndirectIndexed,
    PcRelativeDisplacement,
    PcRelativeIndexed,
    AbsoluteShort,
    AbsoluteLong,
    Immediate,
    Quick,
}
