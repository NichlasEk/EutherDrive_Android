namespace EutherDrive.Core.Cpu.M68000Emu;

internal enum OpSize
{
    Byte,
    Word,
    LongWord,
}

internal static class OpSizeExtensions
{
    public static readonly OpSize[] All = { OpSize.Byte, OpSize.Word, OpSize.LongWord };

    public static uint IncrementStepFor(this OpSize size, AddressRegister register)
    {
        return size switch
        {
            OpSize.Byte => register.IsStackPointer ? 2u : 1u,
            OpSize.Word => 2u,
            OpSize.LongWord => 4u,
            _ => 0u
        };
    }
}
