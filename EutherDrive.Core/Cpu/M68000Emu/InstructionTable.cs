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

        // TODO: Port populate_* functions from jgenesis m68000-emu table.rs
        return table;
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

    public static IEnumerable<AddressingMode> AllAddressingModes()
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

    public static IEnumerable<AddressingMode> AllAddressingModesNoAddressDirect()
    {
        foreach (var mode in AllAddressingModes())
        {
            if (mode.Kind == AddressingModeKind.AddressDirect)
                continue;
            yield return mode;
        }
    }

    public static IEnumerable<AddressingMode> JumpAddressingModes()
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
}
