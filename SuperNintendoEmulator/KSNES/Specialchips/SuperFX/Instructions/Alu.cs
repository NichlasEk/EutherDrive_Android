using System;

namespace KSNES.Specialchips.SuperFX;

internal static class Alu
{
    public static byte Add(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort operand = gsu.Alt2
            ? (ushort)(opcode & 0x0F)
            : Instructions.ReadRegister(gsu, (byte)(opcode & 0x0F));

        ushort existingCarry = gsu.Alt1 ? (ushort)(gsu.CarryFlag ? 1 : 0) : (ushort)0;

        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        uint partialSum = (uint)source + operand;
        uint sum = partialSum + existingCarry;
        bool carry = (sum & 0x10000) != 0;

        bool bit14Carry = ((source & 0x7FFF) + (operand & 0x7FFF) + existingCarry) >= 0x8000;
        bool overflow = bit14Carry != carry;

        ushort result = (ushort)sum;
        gsu.ZeroFlag = result == 0;
        gsu.CarryFlag = carry;
        gsu.SignFlag = result.SignBit();
        gsu.OverflowFlag = overflow;

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, result, rom, ram);

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Sub(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort operand = (!gsu.Alt1 && gsu.Alt2)
            ? (ushort)(opcode & 0x0F)
            : Instructions.ReadRegister(gsu, (byte)(opcode & 0x0F));

        bool sbc = gsu.Alt1 && !gsu.Alt2;
        ushort existingBorrow = sbc ? (ushort)(gsu.CarryFlag ? 0 : 1) : (ushort)0;

        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        int partialDiff = source - operand;
        int difference = partialDiff - existingBorrow;
        bool borrow = difference < 0;

        bool bit14Borrow = (source & 0x7FFF) < (ushort)((operand & 0x7FFF) + existingBorrow);
        bool overflow = bit14Borrow != borrow;

        ushort result = (ushort)difference;
        gsu.ZeroFlag = result == 0;
        gsu.CarryFlag = !borrow;
        gsu.SignFlag = result.SignBit();
        gsu.OverflowFlag = overflow;

        byte cycles = 0;
        if (!(gsu.Alt1 && gsu.Alt2))
        {
            cycles = Instructions.WriteRegister(gsu, gsu.DReg, result, rom, ram);
        }

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Fmult(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        int source = (short)Instructions.ReadRegister(gsu, gsu.SReg);
        int operand = (short)gsu.R[6];
        int product = source * operand;
        ushort highWord = (ushort)((product >> 16) & 0xFFFF);

        if (gsu.Alt1)
        {
            gsu.R[4] = (ushort)product;
        }

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, highWord, rom, ram);

        gsu.ZeroFlag = highWord == 0;
        gsu.CarryFlag = (product & 0x8000) != 0;
        gsu.SignFlag = highWord.SignBit();

        Instructions.ClearPrefixFlags(gsu);

        return (byte)(cycles + (memoryType, gsu.MultiplierSpeed) switch
        {
            (MemoryType.CodeCache, MultiplierSpeed.Standard) => 8,
            (MemoryType.CodeCache, MultiplierSpeed.High) => 4,
            (MemoryType.Rom, MultiplierSpeed.Standard) => 11,
            (MemoryType.Ram, MultiplierSpeed.Standard) => 11,
            (MemoryType.Rom, MultiplierSpeed.High) => 7,
            (MemoryType.Ram, MultiplierSpeed.High) => 7,
            _ => 8
        });
    }

    public static byte Mult(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort operand = gsu.Alt2
            ? (ushort)(opcode & 0x0F)
            : (ushort)(Instructions.ReadRegister(gsu, (byte)(opcode & 0x0F)) & 0xFF);

        ushort source = (ushort)(Instructions.ReadRegister(gsu, gsu.SReg) & 0xFF);

        ushort product = gsu.Alt1
            ? (ushort)(source * operand)
            : (ushort)((short)(sbyte)source * (short)(sbyte)operand);

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, product, rom, ram);

        gsu.ZeroFlag = product == 0;
        gsu.SignFlag = product.SignBit();

        Instructions.ClearPrefixFlags(gsu);

        return (byte)(cycles + (memoryType, gsu.MultiplierSpeed) switch
        {
            (MemoryType.CodeCache, MultiplierSpeed.Standard) => 2,
            (MemoryType.CodeCache, MultiplierSpeed.High) => 1,
            (MemoryType.Rom, MultiplierSpeed.Standard) => 5,
            (MemoryType.Ram, MultiplierSpeed.Standard) => 5,
            (MemoryType.Rom, MultiplierSpeed.High) => 3,
            (MemoryType.Ram, MultiplierSpeed.High) => 3,
            _ => 2
        });
    }

    public static byte Inc(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte register = (byte)(opcode & 0x0F);
        ushort incremented = unchecked((ushort)(gsu.R[register] + 1));
        byte cycles = Instructions.WriteRegister(gsu, register, incremented, rom, ram);

        gsu.ZeroFlag = incremented == 0;
        gsu.SignFlag = incremented.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Dec(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte register = (byte)(opcode & 0x0F);
        ushort decremented = unchecked((ushort)(gsu.R[register] - 1));
        byte cycles = Instructions.WriteRegister(gsu, register, decremented, rom, ram);

        gsu.ZeroFlag = decremented == 0;
        gsu.SignFlag = decremented.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte And(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort operand = gsu.Alt2
            ? (ushort)(opcode & 0x0F)
            : Instructions.ReadRegister(gsu, (byte)(opcode & 0x0F));

        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort result = gsu.Alt1 ? (ushort)(source & ~operand) : (ushort)(source & operand);

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, result, rom, ram);

        gsu.ZeroFlag = result == 0;
        gsu.SignFlag = result.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Or(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort operand = gsu.Alt2
            ? (ushort)(opcode & 0x0F)
            : Instructions.ReadRegister(gsu, (byte)(opcode & 0x0F));

        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort result = gsu.Alt1 ? (ushort)(source ^ operand) : (ushort)(source | operand);

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, result, rom, ram);

        gsu.ZeroFlag = result == 0;
        gsu.SignFlag = result.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Not(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort inverted = (ushort)~source;
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, inverted, rom, ram);

        gsu.ZeroFlag = inverted == 0;
        gsu.SignFlag = inverted.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Asr(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);

        ushort shifted = (gsu.Alt1 && source == ushort.MaxValue)
            ? (ushort)0
            : (ushort)((source >> 1) | (source & 0x8000));

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, shifted, rom, ram);

        gsu.ZeroFlag = shifted == 0;
        gsu.CarryFlag = (source & 0x0001) != 0;
        gsu.SignFlag = shifted.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Lsr(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort shifted = (ushort)(source >> 1);
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, shifted, rom, ram);

        gsu.ZeroFlag = shifted == 0;
        gsu.CarryFlag = (source & 0x0001) != 0;
        gsu.SignFlag = false;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Rol(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort rotated = (ushort)((source << 1) | (gsu.CarryFlag ? 1 : 0));
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, rotated, rom, ram);

        gsu.ZeroFlag = rotated == 0;
        gsu.CarryFlag = source.SignBit();
        gsu.SignFlag = rotated.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Ror(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort rotated = (ushort)((source >> 1) | ((gsu.CarryFlag ? 1 : 0) << 15));
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, rotated, rom, ram);

        gsu.ZeroFlag = rotated == 0;
        gsu.CarryFlag = (source & 0x0001) != 0;
        gsu.SignFlag = rotated.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Sex(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort extended = (ushort)(sbyte)source;
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, extended, rom, ram);

        gsu.ZeroFlag = extended == 0;
        gsu.SignFlag = extended.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }
}
