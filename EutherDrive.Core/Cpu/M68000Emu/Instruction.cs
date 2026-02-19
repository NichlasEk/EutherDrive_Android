using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal enum Direction
{
    RegisterToMemory,
    MemoryToRegister,
}

internal enum UspDirection
{
    RegisterToUsp,
    UspToRegister,
}

internal enum ShiftDirection
{
    Left,
    Right,
}

internal readonly struct ShiftCount
{
    public readonly bool IsRegister;
    public readonly byte Constant;
    public readonly DataRegister Register;

    private ShiftCount(bool isRegister, byte constant, DataRegister register)
    {
        IsRegister = isRegister;
        Constant = constant;
        Register = register;
    }

    public static ShiftCount Const(byte value) => new(false, value, default);
    public static ShiftCount Reg(DataRegister reg) => new(true, 0, reg);

    public byte Get(Registers regs)
    {
        return IsRegister ? (byte)Register.Read(regs) : Constant;
    }
}

internal enum BranchCondition
{
    True,
    False,
    Higher,
    LowerOrSame,
    CarryClear,
    CarrySet,
    NotEqual,
    Equal,
    OverflowClear,
    OverflowSet,
    Plus,
    Minus,
    GreaterOrEqual,
    LessThan,
    GreaterThan,
    LessOrEqual,
}

internal static class BranchConditionExtensions
{
    public static bool Check(this BranchCondition condition, ConditionCodes ccr)
    {
        return condition switch
        {
            BranchCondition.True => true,
            BranchCondition.False => false,
            BranchCondition.Higher => !ccr.Carry && !ccr.Zero,
            BranchCondition.LowerOrSame => ccr.Carry || ccr.Zero,
            BranchCondition.CarryClear => !ccr.Carry,
            BranchCondition.CarrySet => ccr.Carry,
            BranchCondition.NotEqual => !ccr.Zero,
            BranchCondition.Equal => ccr.Zero,
            BranchCondition.OverflowClear => !ccr.Overflow,
            BranchCondition.OverflowSet => ccr.Overflow,
            BranchCondition.Plus => !ccr.Negative,
            BranchCondition.Minus => ccr.Negative,
            BranchCondition.GreaterOrEqual => ccr.Negative == ccr.Overflow,
            BranchCondition.LessThan => ccr.Negative != ccr.Overflow,
            BranchCondition.GreaterThan => !ccr.Zero && ccr.Negative == ccr.Overflow,
            BranchCondition.LessOrEqual => ccr.Zero || ccr.Negative != ccr.Overflow,
            _ => false,
        };
    }
}

internal enum InstructionKind
{
    Add,
    AddDecimal,
    And,
    AndToCcr,
    AndToSr,
    ArithmeticShiftMemory,
    ArithmeticShiftRegister,
    BitTest,
    BitTestAndChange,
    BitTestAndClear,
    BitTestAndSet,
    Branch,
    BranchDecrement,
    BranchToSubroutine,
    CheckRegister,
    Clear,
    Compare,
    DivideSigned,
    DivideUnsigned,
    ExchangeAddress,
    ExchangeData,
    ExchangeDataAddress,
    ExclusiveOr,
    ExclusiveOrToCcr,
    ExclusiveOrToSr,
    Extend,
    Illegal,
    Jump,
    JumpToSubroutine,
    Link,
    LoadEffectiveAddress,
    LogicalShiftMemory,
    LogicalShiftRegister,
    Move,
    MoveFromSr,
    MoveMultiple,
    MovePeripheral,
    MoveQuick,
    MoveToCcr,
    MoveToSr,
    MoveUsp,
    MultiplySigned,
    MultiplyUnsigned,
    Negate,
    NegateDecimal,
    NoOp,
    Not,
    Or,
    OrToCcr,
    OrToSr,
    PushEffectiveAddress,
    Reset,
    Return,
    ReturnFromException,
    RotateMemory,
    RotateRegister,
    RotateThruExtendMemory,
    RotateThruExtendRegister,
    Set,
    Subtract,
    SubtractDecimal,
    Swap,
    Stop,
    Test,
    TestAndSet,
    Trap,
    TrapOnOverflow,
    Unlink,
}

internal readonly struct Instruction
{
    public readonly InstructionKind Kind;
    public readonly OpSize Size;
    public readonly AddressingMode Source;
    public readonly AddressingMode Dest;
    public readonly DataRegister DataReg;
    public readonly AddressRegister AddrReg;
    public readonly BranchCondition BranchCondition;
    public readonly ShiftDirection ShiftDirection;
    public readonly ShiftCount ShiftCount;
    public readonly Direction Direction;
    public readonly UspDirection UspDirection;
    public readonly bool WithExtend;
    public readonly bool RestoreCcr;
    public readonly sbyte Displacement8;
    public readonly byte QuickValue;
    public readonly ushort IllegalOpcode;
    public readonly uint TrapVector;

    private Instruction(
        InstructionKind kind,
        OpSize size,
        AddressingMode source,
        AddressingMode dest,
        DataRegister dataReg,
        AddressRegister addrReg,
        BranchCondition branchCondition,
        ShiftDirection shiftDirection,
        ShiftCount shiftCount,
        Direction direction,
        UspDirection uspDirection,
        bool withExtend,
        bool restoreCcr,
        sbyte displacement8,
        byte quickValue,
        ushort illegalOpcode,
        uint trapVector)
    {
        Kind = kind;
        Size = size;
        Source = source;
        Dest = dest;
        DataReg = dataReg;
        AddrReg = addrReg;
        BranchCondition = branchCondition;
        ShiftDirection = shiftDirection;
        ShiftCount = shiftCount;
        Direction = direction;
        UspDirection = uspDirection;
        WithExtend = withExtend;
        RestoreCcr = restoreCcr;
        Displacement8 = displacement8;
        QuickValue = quickValue;
        IllegalOpcode = illegalOpcode;
        TrapVector = trapVector;
    }

    public static Instruction Add(OpSize size, AddressingMode source, AddressingMode dest, bool withExtend)
        => new(InstructionKind.Add, size, source, dest, default, default, default, default, default, default, default, withExtend, false, 0, 0, 0, 0);

    public static Instruction AddDecimal(AddressingMode source, AddressingMode dest)
        => new(InstructionKind.AddDecimal, OpSize.Byte, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction And(OpSize size, AddressingMode source, AddressingMode dest)
        => new(InstructionKind.And, size, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction AndToCcr() => new(InstructionKind.AndToCcr, OpSize.Byte, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);
    public static Instruction AndToSr() => new(InstructionKind.AndToSr, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ArithmeticShiftMemory(ShiftDirection dir, AddressingMode dest)
        => new(InstructionKind.ArithmeticShiftMemory, OpSize.Word, default, dest, default, default, default, dir, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ArithmeticShiftRegister(OpSize size, ShiftDirection dir, DataRegister reg, ShiftCount count)
        => new(InstructionKind.ArithmeticShiftRegister, size, default, default, reg, default, default, dir, count, default, default, false, false, 0, 0, 0, 0);

    public static Instruction BitTest(AddressingMode source, AddressingMode dest)
        => new(InstructionKind.BitTest, OpSize.Byte, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction BitTestAndChange(AddressingMode source, AddressingMode dest)
        => new(InstructionKind.BitTestAndChange, OpSize.Byte, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction BitTestAndClear(AddressingMode source, AddressingMode dest)
        => new(InstructionKind.BitTestAndClear, OpSize.Byte, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction BitTestAndSet(AddressingMode source, AddressingMode dest)
        => new(InstructionKind.BitTestAndSet, OpSize.Byte, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Branch(BranchCondition condition, sbyte displacement)
        => new(InstructionKind.Branch, OpSize.Byte, default, default, default, default, condition, default, default, default, default, false, false, displacement, 0, 0, 0);

    public static Instruction BranchDecrement(BranchCondition condition, DataRegister reg)
        => new(InstructionKind.BranchDecrement, OpSize.Word, default, default, reg, default, condition, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction BranchToSubroutine(sbyte displacement)
        => new(InstructionKind.BranchToSubroutine, OpSize.Byte, default, default, default, default, default, default, default, default, default, false, false, displacement, 0, 0, 0);

    public static Instruction CheckRegister(DataRegister reg, AddressingMode source)
        => new(InstructionKind.CheckRegister, OpSize.Word, source, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Clear(OpSize size, AddressingMode dest)
        => new(InstructionKind.Clear, size, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Compare(OpSize size, AddressingMode source, AddressingMode dest)
        => new(InstructionKind.Compare, size, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction DivideSigned(DataRegister reg, AddressingMode source)
        => new(InstructionKind.DivideSigned, OpSize.Word, source, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction DivideUnsigned(DataRegister reg, AddressingMode source)
        => new(InstructionKind.DivideUnsigned, OpSize.Word, source, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ExchangeAddress(AddressRegister a1, AddressRegister a2)
        => new(InstructionKind.ExchangeAddress, OpSize.LongWord, default, default, default, a1, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ExchangeData(DataRegister d1, DataRegister d2)
        => new(InstructionKind.ExchangeData, OpSize.LongWord, default, default, d1, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ExchangeDataAddress(DataRegister d, AddressRegister a)
        => new(InstructionKind.ExchangeDataAddress, OpSize.LongWord, default, default, d, a, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ExclusiveOr(OpSize size, AddressingMode source, AddressingMode dest)
        => new(InstructionKind.ExclusiveOr, size, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction ExclusiveOrToCcr() => new(InstructionKind.ExclusiveOrToCcr, OpSize.Byte, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);
    public static Instruction ExclusiveOrToSr() => new(InstructionKind.ExclusiveOrToSr, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Extend(OpSize size, DataRegister reg)
        => new(InstructionKind.Extend, size, default, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Illegal(ushort opcode)
        => new(InstructionKind.Illegal, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, opcode, 0);

    public static Instruction Jump(AddressingMode dest)
        => new(InstructionKind.Jump, OpSize.LongWord, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction JumpToSubroutine(AddressingMode dest)
        => new(InstructionKind.JumpToSubroutine, OpSize.LongWord, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Link(AddressRegister reg)
        => new(InstructionKind.Link, OpSize.Word, default, default, default, reg, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction LoadEffectiveAddress(AddressingMode source, AddressRegister dest)
        => new(InstructionKind.LoadEffectiveAddress, OpSize.LongWord, source, default, default, dest, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction LogicalShiftMemory(ShiftDirection dir, AddressingMode dest)
        => new(InstructionKind.LogicalShiftMemory, OpSize.Word, default, dest, default, default, default, dir, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction LogicalShiftRegister(OpSize size, ShiftDirection dir, DataRegister reg, ShiftCount count)
        => new(InstructionKind.LogicalShiftRegister, size, default, default, reg, default, default, dir, count, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Move(OpSize size, AddressingMode source, AddressingMode dest)
        => new(InstructionKind.Move, size, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction MoveFromSr(AddressingMode dest)
        => new(InstructionKind.MoveFromSr, OpSize.Word, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction MoveMultiple(OpSize size, AddressingMode dest, Direction direction)
        => new(InstructionKind.MoveMultiple, size, default, dest, default, default, default, default, default, direction, default, false, false, 0, 0, 0, 0);

    public static Instruction MovePeripheral(OpSize size, DataRegister d, AddressRegister a, Direction direction)
        => new(InstructionKind.MovePeripheral, size, default, default, d, a, default, default, default, direction, default, false, false, 0, 0, 0, 0);

    public static Instruction MoveQuick(sbyte value, DataRegister dest)
        => new(InstructionKind.MoveQuick, OpSize.LongWord, default, default, dest, default, default, default, default, default, default, false, false, 0, (byte)value, 0, 0);

    public static Instruction MoveToCcr(AddressingMode source)
        => new(InstructionKind.MoveToCcr, OpSize.Byte, source, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction MoveToSr(AddressingMode source)
        => new(InstructionKind.MoveToSr, OpSize.Word, source, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction MoveUsp(UspDirection dir, AddressRegister reg)
        => new(InstructionKind.MoveUsp, OpSize.LongWord, default, default, default, reg, default, default, default, default, dir, false, false, 0, 0, 0, 0);

    public static Instruction MultiplySigned(DataRegister reg, AddressingMode source)
        => new(InstructionKind.MultiplySigned, OpSize.Word, source, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction MultiplyUnsigned(DataRegister reg, AddressingMode source)
        => new(InstructionKind.MultiplyUnsigned, OpSize.Word, source, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Negate(OpSize size, AddressingMode dest, bool withExtend)
        => new(InstructionKind.Negate, size, default, dest, default, default, default, default, default, default, default, withExtend, false, 0, 0, 0, 0);

    public static Instruction NegateDecimal(AddressingMode dest)
        => new(InstructionKind.NegateDecimal, OpSize.Byte, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction NoOp() => new(InstructionKind.NoOp, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Not(OpSize size, AddressingMode dest)
        => new(InstructionKind.Not, size, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Or(OpSize size, AddressingMode source, AddressingMode dest)
        => new(InstructionKind.Or, size, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction OrToCcr() => new(InstructionKind.OrToCcr, OpSize.Byte, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);
    public static Instruction OrToSr() => new(InstructionKind.OrToSr, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction PushEffectiveAddress(AddressingMode source)
        => new(InstructionKind.PushEffectiveAddress, OpSize.LongWord, source, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Reset() => new(InstructionKind.Reset, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Return(bool restoreCcr)
        => new(InstructionKind.Return, OpSize.Word, default, default, default, default, default, default, default, default, default, false, restoreCcr, 0, 0, 0, 0);

    public static Instruction ReturnFromException()
        => new(InstructionKind.ReturnFromException, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction RotateMemory(ShiftDirection dir, AddressingMode dest)
        => new(InstructionKind.RotateMemory, OpSize.Word, default, dest, default, default, default, dir, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction RotateRegister(OpSize size, ShiftDirection dir, DataRegister reg, ShiftCount count)
        => new(InstructionKind.RotateRegister, size, default, default, reg, default, default, dir, count, default, default, false, false, 0, 0, 0, 0);

    public static Instruction RotateThruExtendMemory(ShiftDirection dir, AddressingMode dest)
        => new(InstructionKind.RotateThruExtendMemory, OpSize.Word, default, dest, default, default, default, dir, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction RotateThruExtendRegister(OpSize size, ShiftDirection dir, DataRegister reg, ShiftCount count)
        => new(InstructionKind.RotateThruExtendRegister, size, default, default, reg, default, default, dir, count, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Set(BranchCondition condition, AddressingMode dest)
        => new(InstructionKind.Set, OpSize.Byte, default, dest, default, default, condition, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Subtract(OpSize size, AddressingMode source, AddressingMode dest, bool withExtend)
        => new(InstructionKind.Subtract, size, source, dest, default, default, default, default, default, default, default, withExtend, false, 0, 0, 0, 0);

    public static Instruction SubtractDecimal(AddressingMode source, AddressingMode dest)
        => new(InstructionKind.SubtractDecimal, OpSize.Byte, source, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Swap(DataRegister reg)
        => new(InstructionKind.Swap, OpSize.Word, default, default, reg, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Stop()
        => new(InstructionKind.Stop, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Test(OpSize size, AddressingMode source)
        => new(InstructionKind.Test, size, source, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction TestAndSet(AddressingMode dest)
        => new(InstructionKind.TestAndSet, OpSize.Byte, default, dest, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Trap(uint vector)
        => new(InstructionKind.Trap, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, vector);

    public static Instruction TrapOnOverflow()
        => new(InstructionKind.TrapOnOverflow, OpSize.Word, default, default, default, default, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public static Instruction Unlink(AddressRegister reg)
        => new(InstructionKind.Unlink, OpSize.Word, default, default, default, reg, default, default, default, default, default, false, false, 0, 0, 0, 0);

    public AddressingMode? SourceAddressingMode()
    {
        return Kind switch
        {
            InstructionKind.Add or InstructionKind.AddDecimal or InstructionKind.And or InstructionKind.ArithmeticShiftMemory
                or InstructionKind.BitTest or InstructionKind.BitTestAndChange or InstructionKind.BitTestAndClear or InstructionKind.BitTestAndSet
                or InstructionKind.CheckRegister or InstructionKind.Compare or InstructionKind.DivideSigned or InstructionKind.DivideUnsigned
                or InstructionKind.ExclusiveOr or InstructionKind.LoadEffectiveAddress or InstructionKind.LogicalShiftMemory
                or InstructionKind.Jump or InstructionKind.JumpToSubroutine or InstructionKind.Move or InstructionKind.MoveToCcr
                or InstructionKind.MoveToSr or InstructionKind.MultiplySigned or InstructionKind.MultiplyUnsigned or InstructionKind.Or
                or InstructionKind.PushEffectiveAddress or InstructionKind.RotateMemory or InstructionKind.RotateThruExtendMemory
                or InstructionKind.Subtract or InstructionKind.SubtractDecimal or InstructionKind.Test
                => Source,
            _ => null,
        };
    }

    public AddressingMode? DestAddressingMode()
    {
        return Kind switch
        {
            InstructionKind.Add or InstructionKind.AddDecimal or InstructionKind.And or InstructionKind.ArithmeticShiftMemory
                or InstructionKind.BitTest or InstructionKind.BitTestAndChange or InstructionKind.BitTestAndClear or InstructionKind.BitTestAndSet
                or InstructionKind.Clear or InstructionKind.Compare or InstructionKind.ExclusiveOr or InstructionKind.Jump
                or InstructionKind.JumpToSubroutine or InstructionKind.LogicalShiftMemory or InstructionKind.Move
                or InstructionKind.MoveFromSr or InstructionKind.Negate or InstructionKind.NegateDecimal or InstructionKind.Not
                or InstructionKind.Or or InstructionKind.RotateMemory or InstructionKind.RotateThruExtendMemory
                or InstructionKind.Set or InstructionKind.Subtract or InstructionKind.SubtractDecimal or InstructionKind.Test
                or InstructionKind.TestAndSet
                => Dest,
            _ => null,
        };
    }
}
