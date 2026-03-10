namespace KSNES.Specialchips.ST010;

internal static class Instructions
{
    public static void Execute(Upd77c25 cpu)
    {
        uint opcode = cpu.FetchOpcode();
        switch (opcode & 0xC00000)
        {
            case 0x000000:
            case 0x400000:
                ExecuteAlu(cpu, opcode);
                break;
            case 0x800000:
                ExecuteJump(cpu, opcode);
                break;
            case 0xC00000:
                ExecuteLoad(cpu, opcode);
                break;
        }
    }

    private static void ExecuteLoad(Upd77c25 cpu, uint opcode)
    {
        ushort value = (ushort)(opcode >> 6);
        ushort dest = (ushort)(opcode & 0xF);
        WriteRegister(cpu, dest, value);
    }

    private static void ExecuteJump(Upd77c25 cpu, uint opcode)
    {
        ushort opcodeU16 = (ushort)opcode;
        ushort jumpAddr = (ushort)((cpu.Regs.Pc & 0x2000) | ((opcodeU16 >> 2) & 0x07FF) | ((opcodeU16 & 0x0003) << 11));

        bool shouldJump = ((opcode >> 13) & 0x1FF) switch
        {
            0x000 => JumpSo(cpu, ref jumpAddr),
            0x080 => !cpu.Regs.FlagsA.C,
            0x082 => cpu.Regs.FlagsA.C,
            0x084 => !cpu.Regs.FlagsB.C,
            0x086 => cpu.Regs.FlagsB.C,
            0x088 => !cpu.Regs.FlagsA.Z,
            0x08A => cpu.Regs.FlagsA.Z,
            0x08C => !cpu.Regs.FlagsB.Z,
            0x08E => cpu.Regs.FlagsB.Z,
            0x090 => !cpu.Regs.FlagsA.Ov0,
            0x092 => cpu.Regs.FlagsA.Ov0,
            0x094 => !cpu.Regs.FlagsB.Ov0,
            0x096 => cpu.Regs.FlagsB.Ov0,
            0x098 => !cpu.Regs.FlagsA.Ov1,
            0x09A => cpu.Regs.FlagsA.Ov1,
            0x09C => !cpu.Regs.FlagsB.Ov1,
            0x09E => cpu.Regs.FlagsB.Ov1,
            0x0A0 => !cpu.Regs.FlagsA.S0,
            0x0A2 => cpu.Regs.FlagsA.S0,
            0x0A4 => !cpu.Regs.FlagsB.S0,
            0x0A6 => cpu.Regs.FlagsB.S0,
            0x0A8 => !cpu.Regs.FlagsA.S1,
            0x0AA => cpu.Regs.FlagsA.S1,
            0x0AC => !cpu.Regs.FlagsB.S1,
            0x0AE => cpu.Regs.FlagsB.S1,
            0x0B0 => (cpu.Regs.Dp & 0x0F) == 0,
            0x0B1 => (cpu.Regs.Dp & 0x0F) != 0,
            0x0B2 => (cpu.Regs.Dp & 0x0F) == 0x0F,
            0x0B3 => (cpu.Regs.Dp & 0x0F) != 0x0F,
            0x0BC => !cpu.Regs.Status.RequestForMaster,
            0x0BE => cpu.Regs.Status.RequestForMaster,
            0x100 => Jump(cpu, ref jumpAddr, false),
            0x101 => Jump(cpu, ref jumpAddr, true),
            0x140 => Call(cpu, ref jumpAddr, false),
            0x141 => Call(cpu, ref jumpAddr, true),
            _ => false
        };

        if (shouldJump)
        {
            if (jumpAddr == (ushort)((cpu.Regs.Pc - 1) & cpu.PcMask))
                cpu.SetIdle();
            cpu.Regs.Pc = jumpAddr;
        }
    }

    private static bool JumpSo(Upd77c25 cpu, ref ushort jumpAddr)
    {
        jumpAddr = (ushort)(cpu.Regs.So & cpu.PcMask);
        return true;
    }

    private static bool Jump(Upd77c25 cpu, ref ushort jumpAddr, bool setHigh)
    {
        if (setHigh)
            jumpAddr |= 0x4000;
        return true;
    }

    private static bool Call(Upd77c25 cpu, ref ushort jumpAddr, bool setHigh)
    {
        if (setHigh)
            jumpAddr |= 0x4000;
        cpu.Regs.PushStack(cpu.Regs.Pc);
        return true;
    }

    private static void ExecuteAlu(Upd77c25 cpu, uint opcode)
    {
        uint aluInput = (opcode >> 20) & 0x3;
        uint aluOpcode = (opcode >> 16) & 0xF;

        uint sourceRegister = (opcode >> 4) & 0xF;
        ushort source = ReadRegister(cpu, sourceRegister);

        short operand = aluInput switch
        {
            0x0 => (short)cpu.ReadRam(cpu.Regs.Dp),
            0x1 => (short)source,
            0x2 => (short)((2 * cpu.Regs.KlProduct()) >> 16),
            _ => (short)(2 * cpu.Regs.KlProduct())
        };

        bool useB = ((opcode >> 15) & 1) != 0;
        ref short acc = ref (useB ? ref cpu.Regs.AccB : ref cpu.Regs.AccA);
        ref Flags flags = ref (useB ? ref cpu.Regs.FlagsB : ref cpu.Regs.FlagsA);
        Flags otherFlags = useB ? cpu.Regs.FlagsA : cpu.Regs.FlagsB;

        switch (aluOpcode)
        {
            case 0x00: break;
            case 0x01: Or(ref acc, operand, ref flags); break;
            case 0x02: And(ref acc, operand, ref flags); break;
            case 0x03: Xor(ref acc, operand, ref flags); break;
            case 0x04: Sub(ref acc, operand, false, ref flags); break;
            case 0x05: Add(ref acc, operand, false, ref flags); break;
            case 0x06: Sub(ref acc, operand, otherFlags.C, ref flags); break;
            case 0x07: Add(ref acc, operand, otherFlags.C, ref flags); break;
            case 0x08: Sub(ref acc, 1, false, ref flags); break;
            case 0x09: Add(ref acc, 1, false, ref flags); break;
            case 0x0A: Not(ref acc, ref flags); break;
            case 0x0B: Sar1(ref acc, ref flags); break;
            case 0x0C: Rcl1(ref acc, otherFlags.C, ref flags); break;
            case 0x0D: Sll2(ref acc, ref flags); break;
            case 0x0E: Sll4(ref acc, ref flags); break;
            case 0x0F: Xchg(ref acc, ref flags); break;
        }

        ushort destRegister = (ushort)(opcode & 0xF);
        WriteRegister(cpu, destRegister, source);

        uint dplAdjust = (opcode >> 13) & 0x3;
        switch (dplAdjust)
        {
            case 0x01:
                cpu.Regs.Dp = (ushort)((cpu.Regs.Dp & ~0x0F) | ((cpu.Regs.Dp + 1) & 0x0F));
                break;
            case 0x02:
                cpu.Regs.Dp = (ushort)((cpu.Regs.Dp & ~0x0F) | ((cpu.Regs.Dp - 1) & 0x0F));
                break;
            case 0x03:
                cpu.Regs.Dp = (ushort)(cpu.Regs.Dp & ~0x0F);
                break;
        }

        ushort dphAdjust = (ushort)(((opcode >> 9) & 0xF) << 4);
        cpu.Regs.Dp = (ushort)(cpu.Regs.Dp ^ dphAdjust);

        bool rpAdjust = ((opcode >> 8) & 1) != 0;
        if (rpAdjust)
            cpu.Regs.Rp = (ushort)((cpu.Regs.Rp - 1) & cpu.RpMask);

        bool ret = ((opcode >> 22) & 1) != 0;
        if (ret)
            cpu.Regs.Pc = cpu.Regs.PopStack();
    }

    private static void Or(ref short acc, short operand, ref Flags flags)
    {
        acc = (short)(acc | operand);
        flags = SetFlagsBitOp(acc);
    }

    private static void And(ref short acc, short operand, ref Flags flags)
    {
        acc = (short)(acc & operand);
        flags = SetFlagsBitOp(acc);
    }

    private static void Xor(ref short acc, short operand, ref Flags flags)
    {
        acc = (short)(acc ^ operand);
        flags = SetFlagsBitOp(acc);
    }

    private static void Sub(ref short acc, short operand, bool borrow, ref Flags flags)
    {
        ushort ua = (ushort)acc;
        ushort uo = (ushort)operand;
        uint temp = (uint)ua - uo - (borrow ? 1u : 0u);
        bool newBorrow = temp > 0xFFFF;

        int signed = acc - operand - (borrow ? 1 : 0);
        bool overflow = signed < short.MinValue || signed > short.MaxValue;

        acc = (short)temp;
        SetFlagsAddSub(acc, newBorrow, overflow, ref flags);
    }

    private static void Add(ref short acc, short operand, bool carry, ref Flags flags)
    {
        ushort ua = (ushort)acc;
        ushort uo = (ushort)operand;
        uint temp = (uint)ua + uo + (carry ? 1u : 0u);
        bool newCarry = temp > 0xFFFF;

        int signed = acc + operand + (carry ? 1 : 0);
        bool overflow = signed < short.MinValue || signed > short.MaxValue;

        acc = (short)temp;
        SetFlagsAddSub(acc, newCarry, overflow, ref flags);
    }

    private static void Not(ref short acc, ref Flags flags)
    {
        acc = (short)~acc;
        flags = SetFlagsBitOp(acc);
    }

    private static void Sar1(ref short acc, ref Flags flags)
    {
        bool carry = (acc & 1) != 0;
        acc = (short)(acc >> 1);
        flags = SetFlagsShiftOp(acc, carry);
    }

    private static void Rcl1(ref short acc, bool carry, ref Flags flags)
    {
        bool newCarry = (acc & 0x8000) != 0;
        acc = (short)((acc << 1) | (carry ? 1 : 0));
        flags = SetFlagsShiftOp(acc, newCarry);
    }

    private static void Sll2(ref short acc, ref Flags flags)
    {
        acc = (short)((acc << 2) | 0x03);
        flags = SetFlagsBitOp(acc);
    }

    private static void Sll4(ref short acc, ref Flags flags)
    {
        acc = (short)((acc << 4) | 0x0F);
        flags = SetFlagsBitOp(acc);
    }

    private static void Xchg(ref short acc, ref Flags flags)
    {
        acc = (short)((acc << 8) | ((acc >> 8) & 0xFF));
        flags = SetFlagsBitOp(acc);
    }

    private static Flags SetFlagsBitOp(short acc)
    {
        return new Flags
        {
            Z = acc == 0,
            C = false,
            S0 = acc < 0,
            S1 = acc < 0,
            Ov0 = false,
            Ov1 = false
        };
    }

    private static Flags SetFlagsShiftOp(short acc, bool carry)
    {
        return new Flags
        {
            Z = acc == 0,
            C = carry,
            S0 = acc < 0,
            S1 = acc < 0,
            Ov0 = false,
            Ov1 = false
        };
    }

    private static void SetFlagsAddSub(short acc, bool carry, bool overflow, ref Flags flags)
    {
        flags.Z = acc == 0;
        flags.C = carry;
        flags.S0 = acc < 0;
        flags.Ov0 = overflow;
        if (overflow)
        {
            flags.S1 = acc < 0;
            flags.Ov1 = !flags.Ov1;
        }
    }

    private static ushort ReadRegister(Upd77c25 cpu, uint reg)
    {
        return reg switch
        {
            0x00 => (ushort)cpu.Regs.Trb,
            0x01 => (ushort)cpu.Regs.AccA,
            0x02 => (ushort)cpu.Regs.AccB,
            0x03 => (ushort)cpu.Regs.Tr,
            0x04 => cpu.Regs.Dp,
            0x05 => cpu.Regs.Rp,
            0x06 => cpu.ReadDataRom(cpu.Regs.Rp),
            0x07 => (ushort)(0x8000 - (cpu.Regs.FlagsA.S1 ? 1 : 0)),
            0x08 => ReadDrSetRqm(cpu),
            0x09 => cpu.Regs.Dr,
            0x0A => (ushort)(cpu.Regs.Status.ToByte() << 8),
            0x0B => cpu.Regs.So,
            0x0C => cpu.Regs.So,
            0x0D => (ushort)cpu.Regs.K,
            0x0E => (ushort)cpu.Regs.L,
            0x0F => cpu.ReadRam(cpu.Regs.Dp),
            _ => 0
        };
    }

    private static ushort ReadDrSetRqm(Upd77c25 cpu)
    {
        cpu.Regs.Status.RequestForMaster = true;
        return cpu.Regs.Dr;
    }

    private static void WriteRegister(Upd77c25 cpu, ushort reg, ushort value)
    {
        switch (reg)
        {
            case 0x00:
                break;
            case 0x01:
                cpu.Regs.AccA = (short)value;
                break;
            case 0x02:
                cpu.Regs.AccB = (short)value;
                break;
            case 0x03:
                cpu.Regs.Tr = (short)value;
                break;
            case 0x04:
                cpu.Regs.Dp = (ushort)(value & cpu.DpMask);
                break;
            case 0x05:
                cpu.Regs.Rp = (ushort)(value & cpu.RpMask);
                break;
            case 0x06:
                cpu.Regs.UpdWriteData(value);
                break;
            case 0x07:
                cpu.Regs.Status.Write(value);
                break;
            case 0x08:
            case 0x09:
                cpu.Regs.So = value;
                break;
            case 0x0A:
                cpu.Regs.K = (short)value;
                break;
            case 0x0B:
                cpu.Regs.K = (short)value;
                cpu.Regs.L = (short)cpu.ReadDataRom(cpu.Regs.Rp);
                break;
            case 0x0C:
                cpu.Regs.L = (short)value;
                cpu.Regs.K = (short)cpu.ReadRam((ushort)(cpu.Regs.Dp | 0x40));
                break;
            case 0x0D:
                cpu.Regs.L = (short)value;
                break;
            case 0x0E:
                cpu.Regs.Trb = (short)value;
                break;
            case 0x0F:
                cpu.WriteRam(cpu.Regs.Dp, value);
                break;
        }
    }
}
