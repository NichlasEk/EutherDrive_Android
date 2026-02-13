namespace KSNES.Specialchips.SuperFX;

internal static class Flags
{
    public static byte From(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte cycles;
        if (!gsu.B)
        {
            gsu.SReg = (byte)(opcode & 0x0F);
            cycles = 0;
        }
        else
        {
            byte register = (byte)(opcode & 0x0F);
            ushort value = Instructions.ReadRegister(gsu, register);
            cycles = Instructions.WriteRegister(gsu, gsu.DReg, value, rom, ram);

            gsu.ZeroFlag = value == 0;
            gsu.OverflowFlag = (value & 0x80) != 0;
            gsu.SignFlag = value.SignBit();

            Instructions.ClearPrefixFlags(gsu);
        }

        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte To(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte cycles;
        if (!gsu.B)
        {
            gsu.DReg = (byte)(opcode & 0x0F);
            cycles = 0;
        }
        else
        {
            byte register = (byte)(opcode & 0x0F);
            ushort value = Instructions.ReadRegister(gsu, gsu.SReg);
            cycles = Instructions.WriteRegister(gsu, register, value, rom, ram);

            Instructions.ClearPrefixFlags(gsu);
        }

        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte With(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        byte register = (byte)(opcode & 0x0F);
        gsu.SReg = register;
        gsu.DReg = register;
        gsu.B = true;

        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    public static byte Alt1(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        gsu.Alt1 = true;
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    public static byte Alt2(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        gsu.Alt2 = true;
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    public static byte Alt3(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        gsu.Alt1 = true;
        gsu.Alt2 = true;
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }
}
