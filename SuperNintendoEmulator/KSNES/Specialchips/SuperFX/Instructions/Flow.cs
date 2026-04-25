using System.Runtime.CompilerServices;

namespace KSNES.Specialchips.SuperFX;

internal static class Flow
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Link(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        byte n = (byte)(opcode & 0x0F);
        gsu.R[11] = unchecked((ushort)(gsu.R[15] + n - 1));

        Instructions.ClearPrefixFlags(gsu);
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bra(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bge(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, !(gsu.SignFlag ^ gsu.OverflowFlag));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Blt(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, gsu.SignFlag ^ gsu.OverflowFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bne(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, !gsu.ZeroFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Beq(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, gsu.ZeroFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bpl(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, !gsu.SignFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bmi(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, gsu.SignFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bcc(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, !gsu.CarryFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bcs(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, gsu.CarryFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bvc(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, !gsu.OverflowFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Bvs(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
        => Branch(memoryType, gsu, rom, ram, gsu.OverflowFlag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Jmp(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte cycles = Instructions.FillCacheFromPc(gsu, rom, ram);

        gsu.R[15] = gsu.R[opcode & 0x0F];
        gsu.State.JustJumped = true;

        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Ljmp(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        gsu.R[15] = Instructions.ReadRegister(gsu, gsu.SReg);
        gsu.Pbr = (byte)gsu.R[opcode & 0x0F];

        ushort cbr = (ushort)(gsu.R[15] & 0xFFF0);
        gsu.CodeCache.UpdateCbr(cbr);
        byte cycles = Instructions.FillCacheToPc(gsu, gsu.R[15], rom, ram);

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Loop(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        gsu.R[12] = unchecked((ushort)(gsu.R[12] - 1));
        gsu.ZeroFlag = gsu.R[12] == 0;
        gsu.SignFlag = gsu.R[12].SignBit();

        byte cycles = 0;
        if (!gsu.ZeroFlag)
        {
            cycles = Instructions.FillCacheFromPc(gsu, rom, ram);
            gsu.R[15] = gsu.R[13];
            gsu.State.JustJumped = true;
        }

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    private static byte Branch(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram, bool condition)
    {
        sbyte e = unchecked((sbyte)gsu.State.OpcodeBuffer);
        Instructions.FetchOpcode(gsu, rom, ram);

        if (!condition)
        {
            return (byte)(2 * memoryType.AccessCycles(gsu.ClockSpeed));
        }

        byte cycles = Instructions.FillCacheFromPc(gsu, rom, ram);

        gsu.R[15] = unchecked((ushort)(gsu.R[15] + e - 1));
        gsu.State.JustJumped = true;

        return (byte)(cycles + 2 * memoryType.AccessCycles(gsu.ClockSpeed));
    }
}
