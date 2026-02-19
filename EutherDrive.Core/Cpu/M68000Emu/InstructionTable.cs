using System;
using System.Collections.Generic;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal static class InstructionTable
{
    private static readonly Instruction[] LookupTable = BuildTable();

    public static Instruction Decode(ushort opcode)
    {
        return LookupTable[opcode];
    }

    private static Instruction[] BuildTable()
    {
        var table = new Instruction[ushort.MaxValue + 1];
        for (int i = 0; i <= ushort.MaxValue; i++)
            table[i] = Instruction.Illegal((ushort)i);

        // Minimal bootstrap entries; full table port pending.
        table[0x4E71] = Instruction.NoOp();
        table[0x4E70] = Instruction.Reset();
        table[0x4E72] = Instruction.Stop();
        table[0x4E73] = Instruction.ReturnFromException();
        table[0x4E75] = Instruction.Return(restoreCcr: false);
        table[0x4E77] = Instruction.Return(restoreCcr: true);

        PopulateAll(table);
        return table;
    }

    private static void PopulateAll(Instruction[] table)
    {
        PopulateAbcd(table);
        PopulateAdd(table);
        PopulateAdda(table);
        PopulateAddi(table);
        PopulateAddq(table);
        PopulateAddx(table);
        PopulateAnd(table);
        PopulateAndi(table);
        PopulateAsd(table);
        PopulateBcc(table);
        PopulateBchg(table);
        PopulateBclr(table);
        PopulateBset(table);
        PopulateBtst(table);
        PopulateBsr(table);
        PopulateChk(table);
        PopulateClr(table);
        PopulateCmp(table);
        PopulateCmpa(table);
        PopulateCmpi(table);
        PopulateCmpm(table);
        PopulateDbcc(table);
        PopulateDivs(table);
        PopulateDivu(table);
        PopulateEor(table);
        PopulateEori(table);
        PopulateExg(table);
        PopulateExt(table);
        PopulateJmp(table);
        PopulateJsr(table);
        PopulateLea(table);
        PopulateLink(table);
        PopulateLsd(table);
        PopulateMove(table);
        PopulateMovea(table);
        PopulateMovem(table);
        PopulateMovep(table);
        PopulateMoveq(table);
        PopulateMoveCcrSrUsp(table);
        PopulateMuls(table);
        PopulateMulu(table);
        PopulateNbcd(table);
        PopulateNeg(table);
        PopulateNegx(table);
        PopulateNop(table);
        PopulateNot(table);
        PopulateOr(table);
        PopulateOri(table);
        PopulatePea(table);
        PopulateReset(table);
        PopulateRod(table);
        PopulateRoxd(table);
        PopulateRteRtrRts(table);
        PopulateSbcd(table);
        PopulateScc(table);
        PopulateStop(table);
        PopulateSub(table);
        PopulateSuba(table);
        PopulateSubi(table);
        PopulateSubq(table);
        PopulateSubx(table);
        PopulateSwap(table);
        PopulateTas(table);
        PopulateTrap(table);
        PopulateTst(table);
        PopulateUnlk(table);
    }

    private static IEnumerable<AddressingMode> AllAddressingModes()
    {
        foreach (var d in DataRegister.All)
            yield return AddressingMode.DataDirect(d);

        foreach (var a in AddressRegister.All)
        {
            yield return AddressingMode.AddressDirect(a);
            yield return AddressingMode.AddressIndirect(a);
            yield return AddressingMode.AddressIndirectPostincrement(a);
            yield return AddressingMode.AddressIndirectPredecrement(a);
            yield return AddressingMode.AddressIndirectDisplacement(a);
            yield return AddressingMode.AddressIndirectIndexed(a);
        }

        yield return AddressingMode.AbsoluteShort();
        yield return AddressingMode.AbsoluteLong();
        yield return AddressingMode.Immediate();
        yield return AddressingMode.PcRelativeDisplacement();
        yield return AddressingMode.PcRelativeIndexed();
    }

    private static IEnumerable<AddressingMode> AllAddressingModesNoAddressDirect()
    {
        foreach (var mode in AllAddressingModes())
        {
            if (mode.Kind == AddressingModeKind.AddressDirect)
                continue;
            yield return mode;
        }
    }

    private static IEnumerable<AddressingMode> JumpAddressingModes()
    {
        foreach (var mode in AllAddressingModes())
        {
            if (mode.Kind == AddressingModeKind.DataDirect
                || mode.Kind == AddressingModeKind.AddressDirect
                || mode.Kind == AddressingModeKind.AddressIndirectPostincrement
                || mode.Kind == AddressingModeKind.AddressIndirectPredecrement
                || mode.Kind == AddressingModeKind.Immediate)
                continue;
            yield return mode;
        }
    }

    private static IEnumerable<AddressingMode> DestAddressingModes()
    {
        foreach (var mode in AllAddressingModes())
        {
            if (mode.Kind == AddressingModeKind.Immediate
                || mode.Kind == AddressingModeKind.PcRelativeDisplacement
                || mode.Kind == AddressingModeKind.PcRelativeIndexed)
                continue;
            yield return mode;
        }
    }

    private static IEnumerable<AddressingMode> DestAddressingModesNoAddressDirect()
    {
        foreach (var mode in DestAddressingModes())
        {
            if (mode.Kind == AddressingModeKind.AddressDirect)
                continue;
            yield return mode;
        }
    }

    private static IEnumerable<AddressingMode> DestAddressingModesNoDirect()
    {
        foreach (var mode in DestAddressingModes())
        {
            if (mode.Kind == AddressingModeKind.DataDirect || mode.Kind == AddressingModeKind.AddressDirect)
                continue;
            yield return mode;
        }
    }

    public static ushort ToBits(this OpSize size)
    {
        return size switch
        {
            OpSize.Byte => 0x0000,
            OpSize.Word => 0x0040,
            OpSize.LongWord => 0x0080,
            _ => 0
        };
    }

    public static ushort ToMoveBits(this OpSize size)
    {
        return size switch
        {
            OpSize.Byte => 0x1000,
            OpSize.Word => 0x3000,
            OpSize.LongWord => 0x2000,
            _ => 0
        };
    }

    public static ushort ToBits(this AddressingMode mode)
    {
        return mode.Kind switch
        {
            AddressingModeKind.DataDirect => mode.DataReg.Index,
            AddressingModeKind.AddressDirect => (ushort)(0x0008 | mode.AddrReg.Index),
            AddressingModeKind.AddressIndirect => (ushort)(0x0010 | mode.AddrReg.Index),
            AddressingModeKind.AddressIndirectPostincrement => (ushort)(0x0018 | mode.AddrReg.Index),
            AddressingModeKind.AddressIndirectPredecrement => (ushort)(0x0020 | mode.AddrReg.Index),
            AddressingModeKind.AddressIndirectDisplacement => (ushort)(0x0028 | mode.AddrReg.Index),
            AddressingModeKind.AddressIndirectIndexed => (ushort)(0x0030 | mode.AddrReg.Index),
            AddressingModeKind.AbsoluteShort => 0x0038,
            AddressingModeKind.AbsoluteLong => 0x0039,
            AddressingModeKind.PcRelativeDisplacement => 0x003A,
            AddressingModeKind.PcRelativeIndexed => 0x003B,
            AddressingModeKind.Immediate => 0x003C,
            AddressingModeKind.Quick => throw new InvalidOperationException("Quick addressing mode does not have a standardized bit pattern."),
            _ => 0
        };
    }

    public static ushort ToBits(this BranchCondition condition)
    {
        return condition switch
        {
            BranchCondition.True => 0x0000,
            BranchCondition.False => 0x0100,
            BranchCondition.Higher => 0x0200,
            BranchCondition.LowerOrSame => 0x0300,
            BranchCondition.CarryClear => 0x0400,
            BranchCondition.CarrySet => 0x0500,
            BranchCondition.NotEqual => 0x0600,
            BranchCondition.Equal => 0x0700,
            BranchCondition.OverflowClear => 0x0800,
            BranchCondition.OverflowSet => 0x0900,
            BranchCondition.Plus => 0x0A00,
            BranchCondition.Minus => 0x0B00,
            BranchCondition.GreaterOrEqual => 0x0C00,
            BranchCondition.LessThan => 0x0D00,
            BranchCondition.GreaterThan => 0x0E00,
            BranchCondition.LessOrEqual => 0x0F00,
            _ => 0
        };
    }

    private static void PopulateAbcd(Instruction[] table)
    {
        for (byte rx = 0; rx < 8; rx++)
        {
            for (byte ry = 0; ry < 8; ry++)
            {
                ushort dataOpcode = (ushort)(0xC100 | ry | (rx << 9));
                table[dataOpcode] = Instruction.AddDecimal(
                    AddressingMode.DataDirect(new DataRegister(ry)),
                    AddressingMode.DataDirect(new DataRegister(rx)));

                ushort predecOpcode = (ushort)(dataOpcode | 0x0008);
                table[predecOpcode] = Instruction.AddDecimal(
                    AddressingMode.AddressIndirectPredecrement(new AddressRegister(ry)),
                    AddressingMode.AddressIndirectPredecrement(new AddressRegister(rx)));
            }
        }
    }

    private static void PopulateAdd(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in DataRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    if (size == OpSize.Byte && source.IsAddressDirect)
                        continue;
                    ushort opcode = (ushort)(0xD000 | size.ToBits() | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Add(size, source, AddressingMode.DataDirect(dest), withExtend: false);
                }
            }
        }

        foreach (var source in DataRegister.All)
        {
            foreach (var dest in DestAddressingModesNoDirect())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xD100 | size.ToBits() | dest.ToBits() | (source.Index << 9));
                    table[opcode] = Instruction.Add(size, AddressingMode.DataDirect(source), dest, withExtend: false);
                }
            }
        }
    }

    private static void PopulateAdda(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in AddressRegister.All)
            {
                foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
                {
                    ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 8);
                    ushort opcode = (ushort)(0xD0C0 | sizeBit | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Add(size, source, AddressingMode.AddressDirect(dest), withExtend: false);
                }
            }
        }
    }

    private static void PopulateAddi(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x0600 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Add(size, AddressingMode.Immediate(), dest, withExtend: false);
            }
        }
    }

    private static void PopulateAddq(Instruction[] table)
    {
        for (ushort q = 0; q < 8; q++)
        {
            foreach (var dest in DestAddressingModes())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    if (size == OpSize.Byte && dest.IsAddressDirect)
                        continue;
                    ushort opcode = (ushort)(0x5000 | size.ToBits() | dest.ToBits() | (q << 9));
                    var source = q == 0 ? AddressingMode.Quick(8) : AddressingMode.Quick((byte)q);
                    table[opcode] = Instruction.Add(size, source, dest, withExtend: false);
                }
            }
        }
    }

    private static void PopulateAddx(Instruction[] table)
    {
        foreach (var source in DataRegister.All)
        {
            foreach (var dest in DataRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xD100 | size.ToBits() | source.Index | (dest.Index << 9));
                    table[opcode] = Instruction.Add(size, AddressingMode.DataDirect(source), AddressingMode.DataDirect(dest), withExtend: true);
                }
            }
        }

        foreach (var source in AddressRegister.All)
        {
            foreach (var dest in AddressRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xD108 | size.ToBits() | source.Index | (dest.Index << 9));
                    table[opcode] = Instruction.Add(size,
                        AddressingMode.AddressIndirectPredecrement(source),
                        AddressingMode.AddressIndirectPredecrement(dest),
                        withExtend: true);
                }
            }
        }
    }

    private static void PopulateAnd(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            foreach (var dest in DataRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xC000 | size.ToBits() | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.And(size, source, AddressingMode.DataDirect(dest));
                }
            }
        }

        foreach (var source in DataRegister.All)
        {
            foreach (var dest in DestAddressingModesNoDirect())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xC100 | size.ToBits() | dest.ToBits() | (source.Index << 9));
                    table[opcode] = Instruction.And(size, AddressingMode.DataDirect(source), dest);
                }
            }
        }
    }

    private static void PopulateAndi(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x0200 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.And(size, AddressingMode.Immediate(), dest);
            }
        }

        table[0x023C] = Instruction.AndToCcr();
        table[0x027C] = Instruction.AndToSr();
    }

    private static void PopulateBitShift(
        Instruction[] table,
        ushort immOpcodeBase,
        ushort regOpcodeBase,
        ushort memOpcodeBase,
        Func<OpSize, ShiftDirection, DataRegister, ShiftCount, Instruction> regInstr,
        Func<ShiftDirection, AddressingMode, Instruction> memInstr)
    {
        foreach (var dest in DataRegister.All)
        {
            foreach (var size in OpSizeExtensions.All)
            {
                for (byte count = 0; count < 8; count++)
                {
                    ShiftCount shift = count == 0 ? ShiftCount.Const(8) : ShiftCount.Const(count);
                    ushort rOpcode = (ushort)(immOpcodeBase | size.ToBits() | dest.Index | (count << 9));
                    ushort lOpcode = (ushort)(rOpcode | 0x0100);
                    table[rOpcode] = regInstr(size, ShiftDirection.Right, dest, shift);
                    table[lOpcode] = regInstr(size, ShiftDirection.Left, dest, shift);

                    ShiftCount regShift = ShiftCount.Reg(new DataRegister(count));
                    ushort rOpcodeReg = (ushort)(regOpcodeBase | size.ToBits() | dest.Index | (count << 9));
                    ushort lOpcodeReg = (ushort)(rOpcodeReg | 0x0100);
                    table[rOpcodeReg] = regInstr(size, ShiftDirection.Right, dest, regShift);
                    table[lOpcodeReg] = regInstr(size, ShiftDirection.Left, dest, regShift);
                }
            }
        }

        foreach (var dest in DestAddressingModesNoDirect())
        {
            ushort rOpcode = (ushort)(memOpcodeBase | dest.ToBits());
            ushort lOpcode = (ushort)(rOpcode | 0x0100);
            table[rOpcode] = memInstr(ShiftDirection.Right, dest);
            table[lOpcode] = memInstr(ShiftDirection.Left, dest);
        }
    }

    private static void PopulateAsd(Instruction[] table)
    {
        PopulateBitShift(
            table,
            0xE000,
            0xE020,
            0xE0C0,
            (size, dir, reg, count) => Instruction.ArithmeticShiftRegister(size, dir, reg, count),
            (dir, dest) => Instruction.ArithmeticShiftMemory(dir, dest));
    }

    private static void PopulateLsd(Instruction[] table)
    {
        PopulateBitShift(
            table,
            0xE008,
            0xE028,
            0xE2C0,
            (size, dir, reg, count) => Instruction.LogicalShiftRegister(size, dir, reg, count),
            (dir, dest) => Instruction.LogicalShiftMemory(dir, dest));
    }

    private static void PopulateRod(Instruction[] table)
    {
        PopulateBitShift(
            table,
            0xE018,
            0xE038,
            0xE6C0,
            (size, dir, reg, count) => Instruction.RotateRegister(size, dir, reg, count),
            (dir, dest) => Instruction.RotateMemory(dir, dest));
    }

    private static void PopulateRoxd(Instruction[] table)
    {
        PopulateBitShift(
            table,
            0xE010,
            0xE030,
            0xE4C0,
            (size, dir, reg, count) => Instruction.RotateThruExtendRegister(size, dir, reg, count),
            (dir, dest) => Instruction.RotateThruExtendMemory(dir, dest));
    }

    private static void PopulateBcc(Instruction[] table)
    {
        foreach (var condition in BranchConditionExtensions.All)
        {
            for (ushort disp = 0; disp <= 0xFF; disp++)
            {
                ushort opcode = (ushort)(0x6000 | disp | condition.ToBits());
                table[opcode] = Instruction.Branch(condition, (sbyte)disp);
            }
        }
    }

    private static void PopulateBitTest(
        Instruction[] table,
        ushort immOpcodeBase,
        ushort regOpcodeBase,
        Func<AddressingMode, AddressingMode, Instruction> ctor)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            ushort immOpcode = (ushort)(immOpcodeBase | dest.ToBits());
            table[immOpcode] = ctor(AddressingMode.Immediate(), dest);

            foreach (var source in DataRegister.All)
            {
                ushort regOpcode = (ushort)(regOpcodeBase | dest.ToBits() | (source.Index << 9));
                table[regOpcode] = ctor(AddressingMode.DataDirect(source), dest);
            }
        }
    }

    private static void PopulateBchg(Instruction[] table)
        => PopulateBitTest(table, 0x0840, 0x0140, (s, d) => Instruction.BitTestAndChange(s, d));

    private static void PopulateBclr(Instruction[] table)
        => PopulateBitTest(table, 0x0880, 0x0180, (s, d) => Instruction.BitTestAndClear(s, d));

    private static void PopulateBset(Instruction[] table)
        => PopulateBitTest(table, 0x08C0, 0x01C0, (s, d) => Instruction.BitTestAndSet(s, d));

    private static void PopulateBtst(Instruction[] table)
    {
        foreach (var dest in AllAddressingModesNoAddressDirect())
        {
            if (dest.Kind == AddressingModeKind.Immediate)
                continue;
            ushort immOpcode = (ushort)(0x0800 | dest.ToBits());
            table[immOpcode] = Instruction.BitTest(AddressingMode.Immediate(), dest);
        }

        foreach (var dest in AllAddressingModesNoAddressDirect())
        {
            foreach (var source in DataRegister.All)
            {
                ushort regOpcode = (ushort)(0x0100 | dest.ToBits() | (source.Index << 9));
                table[regOpcode] = Instruction.BitTest(AddressingMode.DataDirect(source), dest);
            }
        }
    }

    private static void PopulateBsr(Instruction[] table)
    {
        for (ushort disp = 0; disp <= 0xFF; disp++)
        {
            ushort opcode = (ushort)(0x6100 | disp);
            table[opcode] = Instruction.BranchToSubroutine((sbyte)disp);
        }
    }

    private static void PopulateChk(Instruction[] table)
    {
        foreach (var mode in AllAddressingModesNoAddressDirect())
        {
            foreach (var reg in DataRegister.All)
            {
                ushort opcode = (ushort)(0x4180 | mode.ToBits() | (reg.Index << 9));
                table[opcode] = Instruction.CheckRegister(reg, mode);
            }
        }
    }

    private static void PopulateClr(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x4200 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Clear(size, dest);
            }
        }
    }

    private static void PopulateCmp(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in DataRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    if (size == OpSize.Byte && source.IsAddressDirect)
                        continue;
                    ushort opcode = (ushort)(0xB000 | size.ToBits() | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Compare(size, source, AddressingMode.DataDirect(dest));
                }
            }
        }
    }

    private static void PopulateCmpa(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in AddressRegister.All)
            {
                foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
                {
                    ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 8);
                    ushort opcode = (ushort)(0xB0C0 | sizeBit | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Compare(size, source, AddressingMode.AddressDirect(dest));
                }
            }
        }
    }

    private static void PopulateCmpi(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x0C00 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Compare(size, AddressingMode.Immediate(), dest);
            }
        }
    }

    private static void PopulateCmpm(Instruction[] table)
    {
        foreach (var source in AddressRegister.All)
        {
            foreach (var dest in AddressRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xB108 | size.ToBits() | source.Index | (dest.Index << 9));
                    table[opcode] = Instruction.Compare(size,
                        AddressingMode.AddressIndirectPostincrement(source),
                        AddressingMode.AddressIndirectPostincrement(dest));
                }
            }
        }
    }

    private static void PopulateDbcc(Instruction[] table)
    {
        foreach (var condition in BranchConditionExtensions.All)
        {
            foreach (var dest in DataRegister.All)
            {
                ushort opcode = (ushort)(0x50C8 | condition.ToBits() | dest.Index);
                table[opcode] = Instruction.BranchDecrement(condition, dest);
            }
        }
    }

    private static void PopulateDivs(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            foreach (var dest in DataRegister.All)
            {
                ushort opcode = (ushort)(0x81C0 | source.ToBits() | (dest.Index << 9));
                table[opcode] = Instruction.DivideSigned(dest, source);
            }
        }
    }

    private static void PopulateDivu(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            foreach (var dest in DataRegister.All)
            {
                ushort opcode = (ushort)(0x80C0 | source.ToBits() | (dest.Index << 9));
                table[opcode] = Instruction.DivideUnsigned(dest, source);
            }
        }
    }

    private static void PopulateEor(Instruction[] table)
    {
        foreach (var source in DataRegister.All)
        {
            foreach (var dest in DestAddressingModesNoAddressDirect())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0xB100 | size.ToBits() | dest.ToBits() | (source.Index << 9));
                    table[opcode] = Instruction.ExclusiveOr(size, AddressingMode.DataDirect(source), dest);
                }
            }
        }
    }

    private static void PopulateEori(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x0A00 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.ExclusiveOr(size, AddressingMode.Immediate(), dest);
            }
        }
        table[0x0A3C] = Instruction.ExclusiveOrToCcr();
        table[0x0A7C] = Instruction.ExclusiveOrToSr();
    }

    private static void PopulateExg(Instruction[] table)
    {
        for (byte rx = 0; rx < 8; rx++)
        {
            for (byte ry = 0; ry < 8; ry++)
            {
                ushort dataOpcode = (ushort)(0xC140 | ry | (rx << 9));
                table[dataOpcode] = Instruction.ExchangeData(new DataRegister(rx), new DataRegister(ry));

                ushort addressOpcode = (ushort)(dataOpcode | 0x0008);
                table[addressOpcode] = Instruction.ExchangeAddress(new AddressRegister(rx), new AddressRegister(ry));

                ushort mixedOpcode = (ushort)(0xC188 | ry | (rx << 9));
                table[mixedOpcode] = Instruction.ExchangeDataAddress(new DataRegister(rx), new AddressRegister(ry));
            }
        }
    }

    private static void PopulateExt(Instruction[] table)
    {
        foreach (var reg in DataRegister.All)
        {
            foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
            {
                ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 6);
                ushort opcode = (ushort)(0x4880 | sizeBit | reg.Index);
                table[opcode] = Instruction.Extend(size, reg);
            }
        }
    }

    private static void PopulateJmp(Instruction[] table)
    {
        foreach (var dest in JumpAddressingModes())
        {
            ushort opcode = (ushort)(0x4EC0 | dest.ToBits());
            table[opcode] = Instruction.Jump(dest);
        }
    }

    private static void PopulateJsr(Instruction[] table)
    {
        foreach (var dest in JumpAddressingModes())
        {
            ushort opcode = (ushort)(0x4E80 | dest.ToBits());
            table[opcode] = Instruction.JumpToSubroutine(dest);
        }
    }

    private static void PopulateLea(Instruction[] table)
    {
        foreach (var source in JumpAddressingModes())
        {
            foreach (var dest in AddressRegister.All)
            {
                ushort opcode = (ushort)(0x41C0 | source.ToBits() | (dest.Index << 9));
                table[opcode] = Instruction.LoadEffectiveAddress(source, dest);
            }
        }
    }

    private static void PopulateLink(Instruction[] table)
    {
        foreach (var source in AddressRegister.All)
        {
            ushort opcode = (ushort)(0x4E50 | source.Index);
            table[opcode] = Instruction.Link(source);
        }
    }

    private static void PopulateMove(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in DestAddressingModesNoAddressDirect())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    if (size == OpSize.Byte && source.IsAddressDirect)
                        continue;

                    ushort rawDestBits = dest.ToBits();
                    ushort destBits = (ushort)(((rawDestBits & 0x07) << 9) | ((rawDestBits & 0x38) << 3));
                    ushort opcode = (ushort)(size.ToMoveBits() | source.ToBits() | destBits);
                    table[opcode] = Instruction.Move(size, source, dest);
                }
            }
        }
    }

    private static void PopulateMovea(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in AddressRegister.All)
            {
                foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
                {
                    ushort opcode = (ushort)(0x0040 | size.ToMoveBits() | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Move(size, source, AddressingMode.AddressDirect(dest));
                }
            }
        }
    }

    private static void PopulateMovem(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoDirect())
        {
            if (dest.Kind == AddressingModeKind.AddressIndirectPostincrement)
                continue;
            foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
            {
                ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 6);
                ushort opcode = (ushort)(0x4880 | sizeBit | dest.ToBits());
                table[opcode] = Instruction.MoveMultiple(size, dest, Direction.RegisterToMemory);
            }
        }

        foreach (var source in AllAddressingModes())
        {
            if (source.Kind == AddressingModeKind.DataDirect
                || source.Kind == AddressingModeKind.AddressDirect
                || source.Kind == AddressingModeKind.AddressIndirectPredecrement
                || source.Kind == AddressingModeKind.Immediate)
                continue;

            foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
            {
                ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 6);
                ushort opcode = (ushort)(0x4C80 | sizeBit | source.ToBits());
                table[opcode] = Instruction.MoveMultiple(size, source, Direction.MemoryToRegister);
            }
        }
    }

    private static void PopulateMovep(Instruction[] table)
    {
        foreach (var d in DataRegister.All)
        {
            foreach (var a in AddressRegister.All)
            {
                foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
                {
                    ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 6);
                    ushort toReg = (ushort)(0x0108 | sizeBit | a.Index | (d.Index << 9));
                    ushort fromReg = (ushort)(toReg | 0x0080);

                    table[toReg] = Instruction.MovePeripheral(size, d, a, Direction.MemoryToRegister);
                    table[fromReg] = Instruction.MovePeripheral(size, d, a, Direction.RegisterToMemory);
                }
            }
        }
    }

    private static void PopulateMoveq(Instruction[] table)
    {
        foreach (var dest in DataRegister.All)
        {
            for (ushort value = 0; value <= 0xFF; value++)
            {
                ushort opcode = (ushort)(0x7000 | value | (dest.Index << 9));
                table[opcode] = Instruction.MoveQuick((sbyte)value, dest);
            }
        }
    }

    private static void PopulateMoveCcrSrUsp(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            ushort opcode = (ushort)(0x44C0 | source.ToBits());
            table[opcode] = Instruction.MoveToCcr(source);
        }

        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            ushort opcode = (ushort)(0x40C0 | dest.ToBits());
            table[opcode] = Instruction.MoveFromSr(dest);
        }

        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            ushort opcode = (ushort)(0x46C0 | source.ToBits());
            table[opcode] = Instruction.MoveToSr(source);
        }

        foreach (var reg in AddressRegister.All)
        {
            ushort toUsp = (ushort)(0x4E60 | reg.Index);
            ushort fromUsp = (ushort)(toUsp | 0x0008);
            table[toUsp] = Instruction.MoveUsp(UspDirection.RegisterToUsp, reg);
            table[fromUsp] = Instruction.MoveUsp(UspDirection.UspToRegister, reg);
        }
    }

    private static void PopulateMuls(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            foreach (var dest in DataRegister.All)
            {
                ushort opcode = (ushort)(0xC1C0 | source.ToBits() | (dest.Index << 9));
                table[opcode] = Instruction.MultiplySigned(dest, source);
            }
        }
    }

    private static void PopulateMulu(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            foreach (var dest in DataRegister.All)
            {
                ushort opcode = (ushort)(0xC0C0 | source.ToBits() | (dest.Index << 9));
                table[opcode] = Instruction.MultiplyUnsigned(dest, source);
            }
        }
    }

    private static void PopulateNbcd(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            ushort opcode = (ushort)(0x4800 | dest.ToBits());
            table[opcode] = Instruction.NegateDecimal(dest);
        }
    }

    private static void PopulateNeg(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x4400 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Negate(size, dest, withExtend: false);
            }
        }
    }

    private static void PopulateNegx(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x4000 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Negate(size, dest, withExtend: true);
            }
        }
    }

    private static void PopulateNop(Instruction[] table)
    {
        table[0x4E71] = Instruction.NoOp();
    }

    private static void PopulateNot(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x4600 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Not(size, dest);
            }
        }
    }

    private static void PopulateOr(Instruction[] table)
    {
        foreach (var source in AllAddressingModesNoAddressDirect())
        {
            foreach (var dest in DataRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0x8000 | size.ToBits() | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Or(size, source, AddressingMode.DataDirect(dest));
                }
            }
        }

        foreach (var source in DataRegister.All)
        {
            foreach (var dest in DestAddressingModesNoDirect())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0x8100 | size.ToBits() | dest.ToBits() | (source.Index << 9));
                    table[opcode] = Instruction.Or(size, AddressingMode.DataDirect(source), dest);
                }
            }
        }
    }

    private static void PopulateOri(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Or(size, AddressingMode.Immediate(), dest);
            }
        }
        table[0x003C] = Instruction.OrToCcr();
        table[0x007C] = Instruction.OrToSr();
    }

    private static void PopulatePea(Instruction[] table)
    {
        foreach (var source in JumpAddressingModes())
        {
            ushort opcode = (ushort)(0x4840 | source.ToBits());
            table[opcode] = Instruction.PushEffectiveAddress(source);
        }
    }

    private static void PopulateReset(Instruction[] table)
    {
        table[0x4E70] = Instruction.Reset();
    }

    private static void PopulateRteRtrRts(Instruction[] table)
    {
        table[0x4E73] = Instruction.ReturnFromException();
        table[0x4E77] = Instruction.Return(restoreCcr: true);
        table[0x4E75] = Instruction.Return(restoreCcr: false);
    }

    private static void PopulateSbcd(Instruction[] table)
    {
        for (byte rx = 0; rx < 8; rx++)
        {
            for (byte ry = 0; ry < 8; ry++)
            {
                ushort dataOpcode = (ushort)(0x8100 | ry | (rx << 9));
                table[dataOpcode] = Instruction.SubtractDecimal(
                    AddressingMode.DataDirect(new DataRegister(ry)),
                    AddressingMode.DataDirect(new DataRegister(rx)));

                ushort predec = (ushort)(dataOpcode | 0x0008);
                table[predec] = Instruction.SubtractDecimal(
                    AddressingMode.AddressIndirectPredecrement(new AddressRegister(ry)),
                    AddressingMode.AddressIndirectPredecrement(new AddressRegister(rx)));
            }
        }
    }

    private static void PopulateScc(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var condition in BranchConditionExtensions.All)
            {
                ushort opcode = (ushort)(0x50C0 | condition.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Set(condition, dest);
            }
        }
    }

    private static void PopulateStop(Instruction[] table)
    {
        table[0x4E72] = Instruction.Stop();
    }

    private static void PopulateSub(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in DataRegister.All)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    if (size == OpSize.Byte && source.IsAddressDirect)
                        continue;
                    ushort opcode = (ushort)(0x9000 | size.ToBits() | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Subtract(size, source, AddressingMode.DataDirect(dest), withExtend: false);
                }
            }
        }

        foreach (var source in DataRegister.All)
        {
            foreach (var dest in DestAddressingModesNoDirect())
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort opcode = (ushort)(0x9100 | size.ToBits() | dest.ToBits() | (source.Index << 9));
                    table[opcode] = Instruction.Subtract(size, AddressingMode.DataDirect(source), dest, withExtend: false);
                }
            }
        }
    }

    private static void PopulateSuba(Instruction[] table)
    {
        foreach (var source in AllAddressingModes())
        {
            foreach (var dest in AddressRegister.All)
            {
                foreach (var size in new[] { OpSize.Word, OpSize.LongWord })
                {
                    ushort sizeBit = (ushort)((size == OpSize.LongWord ? 1 : 0) << 8);
                    ushort opcode = (ushort)(0x90C0 | sizeBit | source.ToBits() | (dest.Index << 9));
                    table[opcode] = Instruction.Subtract(size, source, AddressingMode.AddressDirect(dest), withExtend: false);
                }
            }
        }
    }

    private static void PopulateSubi(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x0400 | size.ToBits() | dest.ToBits());
                table[opcode] = Instruction.Subtract(size, AddressingMode.Immediate(), dest, withExtend: false);
            }
        }
    }

    private static void PopulateSubq(Instruction[] table)
    {
        foreach (var dest in DestAddressingModes())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                if (size == OpSize.Byte && dest.IsAddressDirect)
                    continue;
                for (ushort q = 0; q < 8; q++)
                {
                    ushort opcode = (ushort)(0x5100 | size.ToBits() | dest.ToBits() | (q << 9));
                    var source = q == 0 ? AddressingMode.Quick(8) : AddressingMode.Quick((byte)q);
                    table[opcode] = Instruction.Subtract(size, source, dest, withExtend: false);
                }
            }
        }
    }

    private static void PopulateSubx(Instruction[] table)
    {
        for (byte rx = 0; rx < 8; rx++)
        {
            for (byte ry = 0; ry < 8; ry++)
            {
                foreach (var size in OpSizeExtensions.All)
                {
                    ushort dataOpcode = (ushort)(0x9100 | size.ToBits() | rx | (ry << 9));
                    table[dataOpcode] = Instruction.Subtract(size,
                        AddressingMode.DataDirect(new DataRegister(rx)),
                        AddressingMode.DataDirect(new DataRegister(ry)),
                        withExtend: true);

                    ushort predec = (ushort)(dataOpcode | 0x0008);
                    table[predec] = Instruction.Subtract(size,
                        AddressingMode.AddressIndirectPredecrement(new AddressRegister(rx)),
                        AddressingMode.AddressIndirectPredecrement(new AddressRegister(ry)),
                        withExtend: true);
                }
            }
        }
    }

    private static void PopulateSwap(Instruction[] table)
    {
        foreach (var dest in DataRegister.All)
        {
            ushort opcode = (ushort)(0x4840 | dest.Index);
            table[opcode] = Instruction.Swap(dest);
        }
    }

    private static void PopulateTas(Instruction[] table)
    {
        foreach (var dest in DestAddressingModesNoAddressDirect())
        {
            ushort opcode = (ushort)(0x4AC0 | dest.ToBits());
            table[opcode] = Instruction.TestAndSet(dest);
        }
    }

    private static void PopulateTrap(Instruction[] table)
    {
        for (ushort vector = 0; vector <= 0xF; vector++)
        {
            ushort opcode = (ushort)(0x4E40 | vector);
            table[opcode] = Instruction.Trap(vector);
        }
        table[0x4E76] = Instruction.TrapOnOverflow();
    }

    private static void PopulateTst(Instruction[] table)
    {
        foreach (var source in DestAddressingModesNoAddressDirect())
        {
            foreach (var size in OpSizeExtensions.All)
            {
                ushort opcode = (ushort)(0x4A00 | size.ToBits() | source.ToBits());
                table[opcode] = Instruction.Test(size, source);
            }
        }
    }

    private static void PopulateUnlk(Instruction[] table)
    {
        foreach (var reg in AddressRegister.All)
        {
            ushort opcode = (ushort)(0x4E58 | reg.Index);
            table[opcode] = Instruction.Unlink(reg);
        }
    }
}
