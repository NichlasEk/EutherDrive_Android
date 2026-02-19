using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed class InstructionExecutor
{
    private readonly Registers _registers;
    private readonly IBusInterface _bus;
    private readonly bool _allowTasWrites;
    private readonly string _name;

    private ushort _opcode;
    private Instruction? _instruction;

    private const uint AddressErrorVector = 3;
    private const uint IllegalOpcodeVector = 4;
    private const uint DivideByZeroVector = 5;
    private const uint CheckRegisterVector = 6;
    private const uint PrivilegeViolationVector = 8;
    private const uint Line1010Vector = 10;
    private const uint Line1111Vector = 11;
    private const uint AutoVectoredInterruptBase = 0x60;

    public InstructionExecutor(Registers registers, IBusInterface bus, bool allowTasWrites, string name)
    {
        _registers = registers;
        _bus = bus;
        _allowTasWrites = allowTasWrites;
        _name = name;
    }

    public uint Execute()
    {
        _registers.AddressError = false;
        _registers.LastInstructionWasMulDiv = false;

        if (_registers.PendingInterruptLevel.HasValue)
        {
            byte level = _registers.PendingInterruptLevel.Value;
            _registers.PendingInterruptLevel = null;
            _bus.AcknowledgeInterrupt(level);
            _registers.Stopped = false;
            return HandleAutoVectoredInterrupt(level).IsOk ? 44u : 50u;
        }

        byte interruptLevel = (byte)(_bus.InterruptLevel() & 0x07);
        if (interruptLevel > _registers.InterruptPriorityMask)
        {
            _registers.PendingInterruptLevel = interruptLevel;
            return 10;
        }

        if (_registers.Stopped)
            return 4;

        var result = DoExecute();
        return result.IsOk ? result.Value : HandleException(result.Error!.Value);
    }

    private ExecuteResult<uint> DoExecute()
    {
        _opcode = _registers.Prefetch;
        _instruction = InstructionTable.Decode(_opcode);

        if (_instruction.Value.Kind == InstructionKind.Illegal)
            return ExecuteResult<uint>.Err(M68kException.IllegalInstruction(_opcode));

        throw new NotImplementedException("Instruction execution not yet implemented.");
    }

    private ExecuteResult<ushort> ReadBusWord(uint address)
    {
        if ((address & 1) != 0)
            return ExecuteResult<ushort>.Err(M68kException.AddressError(address, BusOpType.Read));
        return ExecuteResult<ushort>.Ok(_bus.ReadWord(address));
    }

    private ExecuteResult<uint> ReadBusLong(uint address)
    {
        if ((address & 1) != 0)
            return ExecuteResult<uint>.Err(M68kException.AddressError(address, BusOpType.Read));
        return ExecuteResult<uint>.Ok(_bus.ReadLong(address));
    }

    private ExecuteResult<object> WriteBusWord(uint address, ushort value)
    {
        if ((address & 1) != 0)
            return ExecuteResult<object>.Err(M68kException.AddressError(address, BusOpType.Write));
        _bus.WriteWord(address, value);
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<object> WriteBusLong(uint address, uint value)
    {
        if ((address & 1) != 0)
            return ExecuteResult<object>.Err(M68kException.AddressError(address, BusOpType.Write));
        _bus.WriteLong(address, value);
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<ushort> FetchOperand()
    {
        ushort operand = _registers.Prefetch;
        var next = ReadBusWord(_registers.Pc + 2);
        if (!next.IsOk) return ExecuteResult<ushort>.Err(next.Error!.Value);
        _registers.Prefetch = next.Value;
        _registers.Pc += 2;
        return ExecuteResult<ushort>.Ok(operand);
    }

    private ExecuteResult<ResolvedAddress> ResolveAddress(AddressingMode mode, OpSize size)
    {
        ResolvedAddress resolved;
        switch (mode.Kind)
        {
            case AddressingModeKind.DataDirect:
                resolved = ResolvedAddress.DataRegister(mode.DataReg);
                break;
            case AddressingModeKind.AddressDirect:
                resolved = ResolvedAddress.AddressRegister(mode.AddrReg);
                break;
            case AddressingModeKind.AddressIndirect:
                resolved = ResolvedAddress.Memory(mode.AddrReg.Read(_registers));
                break;
            case AddressingModeKind.AddressIndirectPredecrement:
                {
                    uint inc = size.IncrementStepFor(mode.AddrReg);
                    uint addr = mode.AddrReg.Read(_registers) - inc;
                    mode.AddrReg.WriteLong(_registers, addr);
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AddressIndirectPostincrement:
                {
                    uint inc = size.IncrementStepFor(mode.AddrReg);
                    uint addr = mode.AddrReg.Read(_registers);
                    resolved = ResolvedAddress.MemoryPostincrement(addr, mode.AddrReg, inc);
                    break;
                }
            case AddressingModeKind.AddressIndirectDisplacement:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    short disp = (short)ext.Value;
                    uint addr = mode.AddrReg.Read(_registers) + (uint)disp;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AddressIndirectIndexed:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    var (idxReg, idxSize) = Indexing.ParseIndex(ext.Value);
                    uint index = idxReg.Read(_registers, idxSize);
                    sbyte disp = (sbyte)ext.Value;
                    uint addr = mode.AddrReg.Read(_registers) + index + (uint)disp;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.PcRelativeDisplacement:
                {
                    uint pc = _registers.Pc;
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    short disp = (short)ext.Value;
                    uint addr = pc + (uint)disp;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.PcRelativeIndexed:
                {
                    uint pc = _registers.Pc;
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    var (idxReg, idxSize) = Indexing.ParseIndex(ext.Value);
                    uint index = idxReg.Read(_registers, idxSize);
                    sbyte disp = (sbyte)ext.Value;
                    uint addr = pc + index + (uint)disp;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AbsoluteShort:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    uint addr = (uint)(short)ext.Value;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.AbsoluteLong:
                {
                    var hi = FetchOperand();
                    if (!hi.IsOk) return ExecuteResult<ResolvedAddress>.Err(hi.Error!.Value);
                    var lo = FetchOperand();
                    if (!lo.IsOk) return ExecuteResult<ResolvedAddress>.Err(lo.Error!.Value);
                    uint addr = ((uint)hi.Value << 16) | lo.Value;
                    resolved = ResolvedAddress.Memory(addr);
                    break;
                }
            case AddressingModeKind.Immediate:
                {
                    var ext = FetchOperand();
                    if (!ext.IsOk) return ExecuteResult<ResolvedAddress>.Err(ext.Error!.Value);
                    if (size == OpSize.Byte)
                        resolved = ResolvedAddress.Immediate((byte)ext.Value);
                    else if (size == OpSize.Word)
                        resolved = ResolvedAddress.Immediate(ext.Value);
                    else
                    {
                        var lo = FetchOperand();
                        if (!lo.IsOk) return ExecuteResult<ResolvedAddress>.Err(lo.Error!.Value);
                        uint value = ((uint)ext.Value << 16) | lo.Value;
                        resolved = ResolvedAddress.Immediate(value);
                    }
                    break;
                }
            case AddressingModeKind.Quick:
                resolved = ResolvedAddress.Immediate(mode.QuickValue);
                break;
            default:
                resolved = ResolvedAddress.Immediate(0);
                break;
        }

        return ExecuteResult<ResolvedAddress>.Ok(resolved);
    }

    private ExecuteResult<ResolvedAddress> ResolveAddressWithPost(AddressingMode mode, OpSize size)
    {
        var resolved = ResolveAddress(mode, size);
        if (!resolved.IsOk) return resolved;
        resolved.Value.ApplyPost(_registers);
        return resolved;
    }

    private ExecuteResult<ushort> ReadWordResolved(ResolvedAddress resolved)
    {
        return resolved.Kind switch
        {
            ResolvedAddressKind.DataRegister => ExecuteResult<ushort>.Ok((ushort)resolved.DataReg.Read(_registers)),
            ResolvedAddressKind.AddressRegister => ExecuteResult<ushort>.Ok((ushort)resolved.AddrReg.Read(_registers)),
            ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement => ReadBusWord(resolved.Address),
            ResolvedAddressKind.Immediate => ExecuteResult<ushort>.Ok((ushort)resolved.ImmediateValue),
            _ => ExecuteResult<ushort>.Ok(0)
        };
    }

    private ExecuteResult<uint> ReadLongResolved(ResolvedAddress resolved)
    {
        return resolved.Kind switch
        {
            ResolvedAddressKind.DataRegister => ExecuteResult<uint>.Ok(resolved.DataReg.Read(_registers)),
            ResolvedAddressKind.AddressRegister => ExecuteResult<uint>.Ok(resolved.AddrReg.Read(_registers)),
            ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement => ReadBusLong(resolved.Address),
            ResolvedAddressKind.Immediate => ExecuteResult<uint>.Ok(resolved.ImmediateValue),
            _ => ExecuteResult<uint>.Ok(0)
        };
    }

    private ExecuteResult<object> WriteWordResolved(ResolvedAddress resolved, ushort value)
    {
        switch (resolved.Kind)
        {
            case ResolvedAddressKind.DataRegister:
                resolved.DataReg.WriteWord(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.AddressRegister:
                resolved.AddrReg.WriteWord(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.Memory:
            case ResolvedAddressKind.MemoryPostincrement:
                return WriteBusWord(resolved.Address, value);
            default:
                throw new InvalidOperationException("Cannot write to immediate addressing mode.");
        }
    }

    private ExecuteResult<object> WriteLongResolved(ResolvedAddress resolved, uint value)
    {
        switch (resolved.Kind)
        {
            case ResolvedAddressKind.DataRegister:
                resolved.DataReg.WriteLong(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.AddressRegister:
                resolved.AddrReg.WriteLong(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.Memory:
            case ResolvedAddressKind.MemoryPostincrement:
                return WriteBusLong(resolved.Address, value);
            default:
                throw new InvalidOperationException("Cannot write to immediate addressing mode.");
        }
    }

    private ExecuteResult<object> PushStackU16(ushort value)
    {
        uint sp = _registers.StackPointer() - 2;
        _registers.SetStackPointer(sp);
        return WriteBusWord(sp, value);
    }

    private ExecuteResult<object> PushStackU32(uint value)
    {
        ushort hi = (ushort)(value >> 16);
        ushort lo = (ushort)(value & 0xFFFF);
        uint sp = _registers.StackPointer() - 4;
        _registers.SetStackPointer(sp);
        var r0 = WriteBusWord(sp, hi);
        if (!r0.IsOk) return r0;
        return WriteBusWord(sp + 2, lo);
    }

    private ExecuteResult<ushort> PopStackU16()
    {
        uint sp = _registers.StackPointer();
        var value = ReadBusWord(sp);
        if (!value.IsOk) return ExecuteResult<ushort>.Err(value.Error!.Value);
        _registers.SetStackPointer(sp + 2);
        return ExecuteResult<ushort>.Ok(value.Value);
    }

    private ExecuteResult<uint> PopStackU32()
    {
        uint sp = _registers.StackPointer();
        var value = ReadBusLong(sp);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        _registers.SetStackPointer(sp + 4);
        return ExecuteResult<uint>.Ok(value.Value);
    }

    private ExecuteResult<object> JumpToAddress(uint address)
    {
        _registers.Pc = address - 2;
        if ((address & 1) != 0)
            return ExecuteResult<object>.Err(M68kException.AddressError(address, BusOpType.Jump));
        var _ = FetchOperand();
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<uint> HandleAutoVectoredInterrupt(byte interruptLevel)
    {
        ushort sr = _registers.StatusRegister();
        _registers.TraceEnabled = false;
        _registers.SupervisorMode = true;
        _registers.InterruptPriorityMask = interruptLevel;

        var r0 = PushStackU32(_registers.Pc);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
        var r1 = PushStackU16(sr);
        if (!r1.IsOk) return ExecuteResult<uint>.Err(r1.Error!.Value);

        uint vectorAddr = AutoVectoredInterruptBase + 4u * interruptLevel;
        uint newPc = _bus.ReadLong(vectorAddr);
        var r2 = JumpToAddress(newPc);
        if (!r2.IsOk) return ExecuteResult<uint>.Err(r2.Error!.Value);

        return ExecuteResult<uint>.Ok(44);
    }

    private uint HandleException(M68kException ex)
    {
        switch (ex.Kind)
        {
            case M68kExceptionKind.AddressError:
                _registers.AddressError = true;
                if (!HandleAddressError(ex.Address, ex.BusOp).IsOk)
                {
                    _registers.Frozen = true;
                }
                return 50;
            case M68kExceptionKind.PrivilegeViolation:
                HandleTrap(PrivilegeViolationVector, _registers.Pc - 2);
                return 34;
            case M68kExceptionKind.IllegalInstruction:
                uint vector = (_opcode >> 12) switch
                {
                    0b1010 => Line1010Vector,
                    0b1111 => Line1111Vector,
                    _ => IllegalOpcodeVector
                };
                HandleTrap(vector, _registers.Pc - 2);
                return 34;
            case M68kExceptionKind.DivisionByZero:
                HandleTrap(DivideByZeroVector, _registers.Pc);
                return 38 + ex.Cycles;
            case M68kExceptionKind.Trap:
                HandleTrap(ex.Vector, _registers.Pc);
                return 34;
            case M68kExceptionKind.CheckRegister:
                HandleTrap(CheckRegisterVector, _registers.Pc);
                return 30 + ex.Cycles;
            default:
                return 50;
        }
    }

    private ExecuteResult<object> HandleTrap(uint vector, uint pc)
    {
        ushort sr = _registers.StatusRegister();
        _registers.TraceEnabled = false;
        _registers.SupervisorMode = true;
        var r0 = PushStackU32(pc);
        if (!r0.IsOk) return r0;
        var r1 = PushStackU16(sr);
        if (!r1.IsOk) return r1;
        uint newPc = _bus.ReadLong(vector * 4);
        return JumpToAddress(newPc);
    }

    private ExecuteResult<object> HandleAddressError(uint address, BusOpType opType)
    {
        ushort sr = _registers.StatusRegister();
        bool supervisorMode = _registers.SupervisorMode;

        _registers.TraceEnabled = false;
        _registers.SupervisorMode = true;

        AddressingMode? dest = _instruction.HasValue ? _instruction.Value.DestAddressingMode() : null;
        AddressingMode? source = _instruction.HasValue ? _instruction.Value.SourceAddressingMode() : null;

        uint pc;
        if (opType == BusOpType.Write
            && dest.HasValue
            && dest.Value.Kind == AddressingModeKind.AddressIndirectPredecrement)
        {
            pc = _registers.Pc;
        }
        else if (opType == BusOpType.Write
                 && dest.HasValue
                 && dest.Value.Kind == AddressingModeKind.AbsoluteLong
                 && source.HasValue)
        {
            var srcKind = source.Value.Kind;
            if (srcKind == AddressingModeKind.AddressIndirect
                || srcKind == AddressingModeKind.AddressIndirectPostincrement
                || srcKind == AddressingModeKind.AddressIndirectPredecrement
                || srcKind == AddressingModeKind.AddressIndirectDisplacement
                || srcKind == AddressingModeKind.AddressIndirectIndexed
                || srcKind == AddressingModeKind.PcRelativeDisplacement
                || srcKind == AddressingModeKind.PcRelativeIndexed
                || srcKind == AddressingModeKind.AbsoluteShort
                || srcKind == AddressingModeKind.AbsoluteLong)
            {
                pc = _registers.Pc - 4;
            }
            else
            {
                pc = _registers.Pc - 2;
            }
        }
        else
        {
            pc = _registers.Pc - 2;
        }

        var r0 = PushStackU32(pc);
        if (!r0.IsOk) return r0;
        var r1 = PushStackU16(sr);
        if (!r1.IsOk) return r1;
        var r2 = PushStackU16(_opcode);
        if (!r2.IsOk) return r2;
        var r3 = PushStackU32(address);
        if (!r3.IsOk) return r3;

        bool rwBit = (opType == BusOpType.Read || opType == BusOpType.Jump)
            ^ (_instruction.HasValue && _instruction.Value.Kind == InstructionKind.MoveFromSr);
        ushort statusCode = opType == BusOpType.Jump ? (ushort)(supervisorMode ? 0x0E : 0x0A) : (ushort)0x05;
        ushort statusWord = (ushort)((_opcode & 0xFFE0) | ((rwBit ? 1 : 0) << 4) | statusCode);
        var r4 = PushStackU16(statusWord);
        if (!r4.IsOk) return r4;

        uint vector = _bus.ReadLong(AddressErrorVector * 4);
        return JumpToAddress(vector);
    }
}

internal readonly struct ResolvedAddress
{
    public readonly ResolvedAddressKind Kind;
    public readonly DataRegister DataReg;
    public readonly AddressRegister AddrReg;
    public readonly uint Address;
    public readonly uint ImmediateValue;
    public readonly uint PostIncrement;

    private ResolvedAddress(
        ResolvedAddressKind kind,
        DataRegister dataReg,
        AddressRegister addrReg,
        uint address,
        uint immediateValue,
        uint postIncrement)
    {
        Kind = kind;
        DataReg = dataReg;
        AddrReg = addrReg;
        Address = address;
        ImmediateValue = immediateValue;
        PostIncrement = postIncrement;
    }

    public static ResolvedAddress DataRegister(DataRegister reg) =>
        new(ResolvedAddressKind.DataRegister, reg, default, 0, 0, 0);

    public static ResolvedAddress AddressRegister(AddressRegister reg) =>
        new(ResolvedAddressKind.AddressRegister, default, reg, 0, 0, 0);

    public static ResolvedAddress Memory(uint address) =>
        new(ResolvedAddressKind.Memory, default, default, address, 0, 0);

    public static ResolvedAddress MemoryPostincrement(uint address, AddressRegister reg, uint increment) =>
        new(ResolvedAddressKind.MemoryPostincrement, default, reg, address, 0, increment);

    public static ResolvedAddress Immediate(uint value) =>
        new(ResolvedAddressKind.Immediate, default, default, 0, value, 0);

    public void ApplyPost(Registers regs)
    {
        if (Kind == ResolvedAddressKind.MemoryPostincrement)
        {
            AddrReg.WriteLong(regs, Address + PostIncrement);
        }
    }
}

internal enum ResolvedAddressKind
{
    DataRegister,
    AddressRegister,
    Memory,
    MemoryPostincrement,
    Immediate,
}
