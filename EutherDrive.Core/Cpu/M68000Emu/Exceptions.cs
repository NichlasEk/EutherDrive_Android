namespace EutherDrive.Core.Cpu.M68000Emu;

internal enum BusOpType
{
    Read,
    Write,
    Jump,
}

internal enum M68kExceptionKind
{
    AddressError,
    PrivilegeViolation,
    IllegalInstruction,
    DivisionByZero,
    Trap,
    CheckRegister,
}

internal readonly struct M68kException
{
    public readonly M68kExceptionKind Kind;
    public readonly uint Address;
    public readonly BusOpType BusOp;
    public readonly ushort Opcode;
    public readonly uint Vector;
    public readonly uint Cycles;

    private M68kException(
        M68kExceptionKind kind,
        uint address,
        BusOpType busOp,
        ushort opcode,
        uint vector,
        uint cycles)
    {
        Kind = kind;
        Address = address;
        BusOp = busOp;
        Opcode = opcode;
        Vector = vector;
        Cycles = cycles;
    }

    public static M68kException AddressError(uint address, BusOpType op)
        => new(M68kExceptionKind.AddressError, address, op, 0, 0, 0);

    public static M68kException PrivilegeViolation()
        => new(M68kExceptionKind.PrivilegeViolation, 0, BusOpType.Read, 0, 0, 0);

    public static M68kException IllegalInstruction(ushort opcode)
        => new(M68kExceptionKind.IllegalInstruction, 0, BusOpType.Read, opcode, 0, 0);

    public static M68kException DivisionByZero(uint cycles)
        => new(M68kExceptionKind.DivisionByZero, 0, BusOpType.Read, 0, 0, cycles);

    public static M68kException Trap(uint vector)
        => new(M68kExceptionKind.Trap, 0, BusOpType.Read, 0, vector, 0);

    public static M68kException CheckRegister(uint cycles)
        => new(M68kExceptionKind.CheckRegister, 0, BusOpType.Read, 0, 0, cycles);
}

internal readonly struct ExecuteResult<T>
{
    public readonly T Value;
    public readonly M68kException? Error;

    private ExecuteResult(T value, M68kException? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsOk => Error is null;

    public static ExecuteResult<T> Ok(T value) => new(value, null);
    public static ExecuteResult<T> Err(M68kException error) => new(default!, error);
}
