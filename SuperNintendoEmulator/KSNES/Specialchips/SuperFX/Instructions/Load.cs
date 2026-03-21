using System;

namespace KSNES.Specialchips.SuperFX;

internal static class Load
{
    private static readonly bool TraceRamWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_RAM_WATCH"), "1", StringComparison.Ordinal);
    private static readonly int TraceRamWatchLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_SUPERFX_RAM_WATCH_LIMIT", 1024);
    private static readonly int[] TraceRamWatchAddresses =
        ParseTraceAddresses(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_RAM_WATCH_ADDRS"));
    private static int _traceRamWatchCount;

    public static byte Ldb(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte m = (byte)(opcode & 0x0F);
        int ramAddr = gsu.R[m] & (ram.Length - 1);
        byte value = ram[ramAddr];
        TraceWatchedRamAccess(gsu, "LDB-RD", instructionPc, opcode, ramAddr, 1, value);

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, value, rom, ram);

        gsu.State.RamAddressBuffer = (ushort)ramAddr;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (gsu.ClockSpeed, memoryType) switch
        {
            (ClockSpeed.Slow, MemoryType.CodeCache) => 5,
            (ClockSpeed.Fast, MemoryType.CodeCache) => 7,
            (ClockSpeed.Slow, MemoryType.Rom) => 8,
            (ClockSpeed.Fast, MemoryType.Rom) => 9,
            (ClockSpeed.Slow, MemoryType.Ram) => 10,
            (ClockSpeed.Fast, MemoryType.Ram) => 11,
            _ => 5
        });
    }

    public static byte Ldw(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte m = (byte)(opcode & 0x0F);
        int ramAddr = gsu.R[m] & (ram.Length - 1);
        byte valueLsb = ram[ramAddr];
        byte valueMsb = ram[ramAddr ^ 1];
        ushort value = (ushort)(valueLsb | (valueMsb << 8));
        TraceWatchedRamAccess(gsu, "LDW-RD", instructionPc, opcode, ramAddr, 2, value);

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, value, rom, ram);

        gsu.State.RamAddressBuffer = (ushort)ramAddr;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (gsu.ClockSpeed, memoryType) switch
        {
            (ClockSpeed.Slow, MemoryType.CodeCache) => 7,
            (ClockSpeed.Fast, MemoryType.CodeCache) => 11,
            (ClockSpeed.Slow, MemoryType.Rom) => 10,
            (ClockSpeed.Fast, MemoryType.Rom) => 13,
            (ClockSpeed.Slow, MemoryType.Ram) => 12,
            (ClockSpeed.Fast, MemoryType.Ram) => 15,
            _ => 7
        });
    }

    public static byte Stb(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte value = (byte)Instructions.ReadRegister(gsu, gsu.SReg);

        byte register = (byte)(opcode & 0x0F);
        int ramAddr = gsu.R[register] & (ram.Length - 1);
        ram[ramAddr] = value;
        TraceWatchedRamAccess(gsu, "STB-WR", instructionPc, opcode, ramAddr, 1, value);

        byte cycles = gsu.State.RamBufferWaitCycles;
        gsu.State.RamBufferWaitCycles = gsu.ClockSpeed.MemoryAccessCycles();
        gsu.State.RamBufferWritten = true;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (memoryType switch
        {
            MemoryType.CodeCache => 1,
            MemoryType.Rom => 3,
            MemoryType.Ram => 6,
            _ => 1
        }));
    }

    public static byte Stw(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        byte sourceLsb = (byte)(source & 0xFF);
        byte sourceMsb = (byte)(source >> 8);

        byte register = (byte)(opcode & 0x0F);
        int ramAddr = gsu.R[register] & (ram.Length - 1);
        ram[ramAddr] = sourceLsb;
        ram[ramAddr ^ 1] = sourceMsb;
        TraceWatchedRamAccess(gsu, "STW-WR", instructionPc, opcode, ramAddr, 2, source);

        byte cycles = gsu.State.RamBufferWaitCycles;
        gsu.State.RamBufferWaitCycles = (byte)(2 * gsu.ClockSpeed.MemoryAccessCycles());
        gsu.State.RamBufferWritten = true;

        Instructions.ClearPrefixFlags(gsu);
        return memoryType switch
        {
            MemoryType.CodeCache => (byte)(1 + Math.Max(0, cycles - 1)),
            MemoryType.Rom or MemoryType.Ram => (byte)(gsu.ClockSpeed.MemoryAccessCycles() + cycles),
            _ => (byte)(gsu.ClockSpeed.MemoryAccessCycles() + cycles)
        };
    }

    public static byte Ibt(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte pp = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);

        byte register = (byte)(opcode & 0x0F);
        ushort value = (ushort)(sbyte)pp;
        byte writeCycles = Instructions.WriteRegister(gsu, register, value, rom, ram);

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(writeCycles + 2 * memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Iwt(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte lsb = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);
        byte msb = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);

        ushort value = (ushort)(lsb | (msb << 8));
        byte register = (byte)(opcode & 0x0F);
        byte cycles = Instructions.WriteRegister(gsu, register, value, rom, ram);

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + 3 * memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Lm(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte ramAddrLsb = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);
        byte ramAddrMsb = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);

        int ramAddr = ((ramAddrLsb | (ramAddrMsb << 8)) & (ram.Length - 1));
        byte valueLsb = ram[ramAddr];
        byte valueMsb = ram[ramAddr ^ 1];
        ushort value = (ushort)(valueLsb | (valueMsb << 8));
        TraceWatchedRamAccess(gsu, "LM-RD", instructionPc, opcode, ramAddr, 2, value);

        byte register = (byte)(opcode & 0x0F);
        byte cycles = Instructions.WriteRegister(gsu, register, value, rom, ram);

        gsu.State.RamAddressBuffer = (ushort)ramAddr;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (gsu.ClockSpeed, memoryType) switch
        {
            (ClockSpeed.Slow, MemoryType.CodeCache) => 9,
            (ClockSpeed.Fast, MemoryType.CodeCache) => 13,
            (ClockSpeed.Slow, MemoryType.Rom) => 17,
            (ClockSpeed.Fast, MemoryType.Rom) => 24,
            (ClockSpeed.Slow, MemoryType.Ram) => 18,
            (ClockSpeed.Fast, MemoryType.Ram) => 25,
            _ => 9
        });
    }

    public static byte Lms(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte kk = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);

        int ramAddr = ((kk << 1) & (ram.Length - 1));
        byte lsb = ram[ramAddr];
        byte msb = ram[ramAddr ^ 1];
        ushort value = (ushort)(lsb | (msb << 8));
        TraceWatchedRamAccess(gsu, "LMS-RD", instructionPc, opcode, ramAddr, 2, value);

        byte register = (byte)(opcode & 0x0F);
        byte cycles = Instructions.WriteRegister(gsu, register, value, rom, ram);

        gsu.State.RamAddressBuffer = (ushort)ramAddr;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (gsu.ClockSpeed, memoryType) switch
        {
            (ClockSpeed.Slow, MemoryType.CodeCache) => 8,
            (ClockSpeed.Fast, MemoryType.CodeCache) => 12,
            (ClockSpeed.Slow, MemoryType.Rom) => 15,
            (ClockSpeed.Fast, MemoryType.Rom) => 20,
            (ClockSpeed.Slow, MemoryType.Ram) => 15,
            (ClockSpeed.Fast, MemoryType.Ram) => 20,
            _ => 8
        });
    }

    public static byte Sm(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte ramAddrLsb = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);
        byte ramAddrMsb = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);

        int ramAddr = ((ramAddrLsb | (ramAddrMsb << 8)) & (ram.Length - 1));

        byte register = (byte)(opcode & 0x0F);
        ushort value = Instructions.ReadRegister(gsu, register);
        byte valueLsb = (byte)(value & 0xFF);
        byte valueMsb = (byte)(value >> 8);
        ram[ramAddr] = valueLsb;
        ram[ramAddr ^ 1] = valueMsb;
        TraceWatchedRamAccess(gsu, "SM-WR", instructionPc, opcode, ramAddr, 2, value);

        byte cycles = gsu.State.RamBufferWaitCycles;
        gsu.State.RamBufferWaitCycles = (byte)(2 * gsu.ClockSpeed.MemoryAccessCycles());
        gsu.State.RamBufferWritten = true;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (memoryType switch
        {
            MemoryType.CodeCache => 2,
            MemoryType.Rom => 9,
            MemoryType.Ram => 15,
            _ => 2
        }));
    }

    public static byte Sms(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        byte kk = gsu.State.OpcodeBuffer;
        Instructions.FetchOpcode(gsu, rom, ram);

        int ramAddr = ((kk << 1) & (ram.Length - 1));

        byte register = (byte)(opcode & 0x0F);
        ushort value = Instructions.ReadRegister(gsu, register);
        byte lsb = (byte)(value & 0xFF);
        byte msb = (byte)(value >> 8);
        ram[ramAddr] = lsb;
        ram[ramAddr ^ 1] = msb;
        TraceWatchedRamAccess(gsu, "SMS-WR", instructionPc, opcode, ramAddr, 2, value);

        byte cycles = gsu.State.RamBufferWaitCycles;
        gsu.State.RamBufferWaitCycles = (byte)(2 * gsu.ClockSpeed.MemoryAccessCycles());
        gsu.State.RamBufferWritten = true;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + (memoryType switch
        {
            MemoryType.CodeCache => 1,
            MemoryType.Rom => 6,
            MemoryType.Ram => 10,
            _ => 1
        }));
    }

    public static byte Sbk(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] ram)
    {
        ushort instructionPc = unchecked((ushort)(gsu.R[15] - 1));
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        byte sourceLsb = (byte)(source & 0xFF);
        byte sourceMsb = (byte)(source >> 8);

        ushort ramAddr = gsu.State.RamAddressBuffer;
        ram[ramAddr] = sourceLsb;
        ram[ramAddr ^ 1] = sourceMsb;
        TraceWatchedRamAccess(gsu, "SBK-WR", instructionPc, Instructions.NopOpcode, ramAddr, 2, source);

        byte cycles = gsu.State.RamBufferWaitCycles;
        gsu.State.RamBufferWaitCycles = (byte)(2 * gsu.ClockSpeed.MemoryAccessCycles());
        gsu.State.RamBufferWritten = true;

        Instructions.ClearPrefixFlags(gsu);
        return memoryType switch
        {
            MemoryType.CodeCache => (byte)(1 + Math.Max(0, cycles - 1)),
            MemoryType.Rom or MemoryType.Ram => (byte)(gsu.ClockSpeed.MemoryAccessCycles() + cycles),
            _ => (byte)(gsu.ClockSpeed.MemoryAccessCycles() + cycles)
        };
    }

    public static byte Romb(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        gsu.Rombr = (byte)Instructions.ReadRegister(gsu, gsu.SReg);

        byte cycles = gsu.State.RomBufferWaitCycles;
        gsu.State.RomBufferWaitCycles = 0;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Getb(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        byte valueByte = gsu.State.RomBuffer;
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);

        ushort value = (gsu.Alt1, gsu.Alt2) switch
        {
            (false, false) => valueByte,
            (true, false) => (ushort)((source & 0x00FF) | (valueByte << 8)),
            (false, true) => (ushort)((source & 0xFF00) | valueByte),
            (true, true) => (ushort)(sbyte)valueByte,
        };

        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, value, rom, ram);

        cycles = (byte)(cycles + gsu.State.RomBufferWaitCycles);
        gsu.State.RomBufferWaitCycles = 0;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Hib(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        byte sourceMsb = (byte)(source >> 8);
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, sourceMsb, rom, ram);

        gsu.ZeroFlag = sourceMsb == 0;
        gsu.SignFlag = sourceMsb.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Lob(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        byte sourceLsb = (byte)(source & 0xFF);
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, sourceLsb, rom, ram);

        gsu.ZeroFlag = sourceLsb == 0;
        gsu.SignFlag = sourceLsb.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Swap(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort source = Instructions.ReadRegister(gsu, gsu.SReg);
        ushort swapped = (ushort)((source << 8) | (source >> 8));
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, swapped, rom, ram);

        gsu.ZeroFlag = swapped == 0;
        gsu.SignFlag = swapped.SignBit();

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    public static byte Merge(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort merged = (ushort)((gsu.R[7] & 0xFF00) | (gsu.R[8] >> 8));
        byte cycles = Instructions.WriteRegister(gsu, gsu.DReg, merged, rom, ram);

        gsu.ZeroFlag = (merged & 0xF0F0) != 0;
        gsu.CarryFlag = (merged & 0xE0E0) != 0;
        gsu.SignFlag = (merged & 0x8080) != 0;
        gsu.OverflowFlag = (merged & 0xC0C0) != 0;

        Instructions.ClearPrefixFlags(gsu);
        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed));
    }

    private static void TraceWatchedRamAccess(
        GraphicsSupportUnit gsu,
        string kind,
        ushort instructionPc,
        byte opcode,
        int ramAddr,
        int width,
        ushort value)
    {
        if (!TraceRamWatch || _traceRamWatchCount >= TraceRamWatchLimit || !TouchesWatchedRamAddress(ramAddr, width))
            return;

        _traceRamWatchCount++;
        string valueText = width == 1 ? $"0x{(value & 0xFF):X2}" : $"0x{value:X4}";
        Console.WriteLine(
            $"[SFX-RAM] side=GSU kind={kind} pc=0x{instructionPc:X4} op=0x{opcode:X2} addr=0x{ramAddr:X4} " +
            $"width={width} val={valueText} pbr=0x{gsu.Pbr:X2} rombr=0x{gsu.Rombr:X2} r14=0x{gsu.R[14]:X4} r15=0x{gsu.R[15]:X4} " +
            $"sreg={gsu.SReg} dreg={gsu.DReg} go={(gsu.Go ? 1 : 0)} rom={gsu.RomAccess} ram={gsu.RamAccess}");
    }

    private static bool TouchesWatchedRamAddress(int ramAddr, int width)
    {
        if (TraceRamWatchAddresses.Length == 0)
            return false;

        foreach (int watchAddr in TraceRamWatchAddresses)
        {
            if (ramAddr == watchAddr)
                return true;
            if (width > 1 && (ramAddr ^ 1) == watchAddr)
                return true;
        }

        return false;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static int[] ParseTraceAddresses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<int>();

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new int[parts.Length];
        int count = 0;
        foreach (string part in parts)
        {
            string normalized = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (int.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out int parsed))
                result[count++] = parsed & 0xFFFF;
        }

        if (count == result.Length)
            return result;

        Array.Resize(ref result, count);
        return result;
    }
}
