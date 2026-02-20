using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed partial class InstructionExecutor
{
    private readonly Registers _registers;
    private readonly IBusInterface _bus;
    private readonly bool _allowTasWrites;
    private readonly string _name;

    private ushort _opcode;
    private Instruction? _instruction;
    private uint _tracePc;

    private static readonly bool TraceExceptions =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_EX"), "1", StringComparison.Ordinal);
    private static readonly uint? TracePcMin = ReadHexEnv("EUTHERDRIVE_M68K_TRACE_PC_MIN");
    private static readonly uint? TracePcMax = ReadHexEnv("EUTHERDRIVE_M68K_TRACE_PC_MAX");

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
        uint pcBefore = _registers.Pc;
        _tracePc = pcBefore;
        _opcode = _registers.Prefetch;
        _instruction = InstructionTable.Decode(_opcode);

        if (_instruction.Value.Kind == InstructionKind.Illegal)
            return ExecuteResult<uint>.Err(M68kException.IllegalInstruction(_opcode));

        if (ShouldTracePc(pcBefore))
        {
            Instruction traceInst = _instruction.Value;
            Console.WriteLine(
                $"[M68K-PC] cpu={_name} pc=0x{pcBefore:X8} op=0x{_opcode:X4} inst={traceInst.Kind} size={traceInst.Size} " +
                $"src={FormatMode(traceInst.Source)} dst={FormatMode(traceInst.Dest)} " +
                $"A0=0x{_registers.Address[0]:X8} A2=0x{_registers.Address[2]:X8} D0=0x{_registers.Data[0]:X8}");
        }

        var next = ReadBusWord(_registers.Pc + 2);
        if (!next.IsOk)
            return ExecuteResult<uint>.Err(next.Error!.Value);
        _registers.Prefetch = next.Value;
        _registers.Pc += 2;

        Instruction inst = _instruction.Value;
        return inst.Kind switch
        {
            InstructionKind.Add => inst.Size switch
            {
                OpSize.Byte => AddByte(inst.Source, inst.Dest, inst.WithExtend),
                OpSize.Word => AddWord(inst.Source, inst.Dest, inst.WithExtend),
                _ => AddLongWord(inst.Source, inst.Dest, inst.WithExtend),
            },
            InstructionKind.AddDecimal => Abcd(inst.Source, inst.Dest),
            InstructionKind.And => inst.Size switch
            {
                OpSize.Byte => AndByte(inst.Source, inst.Dest),
                OpSize.Word => AndWord(inst.Source, inst.Dest),
                _ => AndLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.AndToCcr => AndiToCcr(),
            InstructionKind.AndToSr => AndiToSr(),
            InstructionKind.ArithmeticShiftMemory => AsdMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.ArithmeticShiftRegister => AsdRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.BitTest => Btst(inst.Source, inst.Dest),
            InstructionKind.BitTestAndChange => Bchg(inst.Source, inst.Dest),
            InstructionKind.BitTestAndClear => Bclr(inst.Source, inst.Dest),
            InstructionKind.BitTestAndSet => Bset(inst.Source, inst.Dest),
            InstructionKind.Branch => Branch(inst.BranchCondition, inst.Displacement8),
            InstructionKind.BranchDecrement => Dbcc(inst.BranchCondition, inst.DataReg),
            InstructionKind.BranchToSubroutine => Bsr(inst.Displacement8),
            InstructionKind.CheckRegister => Chk(inst.DataReg, inst.Source),
            InstructionKind.Clear => inst.Size switch
            {
                OpSize.Byte => ClrByte(inst.Dest),
                OpSize.Word => ClrWord(inst.Dest),
                _ => ClrLongWord(inst.Dest),
            },
            InstructionKind.Compare => inst.Size switch
            {
                OpSize.Byte => CmpByte(inst.Source, inst.Dest),
                OpSize.Word => CmpWord(inst.Source, inst.Dest),
                _ => CmpLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.DivideSigned => Divs(inst.DataReg, inst.Source),
            InstructionKind.DivideUnsigned => Divu(inst.DataReg, inst.Source),
            InstructionKind.ExchangeAddress => ExecuteResult<uint>.Ok(ExgAddress(inst.AddrReg, inst.Dest.AddrReg)),
            InstructionKind.ExchangeData => ExecuteResult<uint>.Ok(ExgData(inst.DataReg, inst.Dest.DataReg)),
            InstructionKind.ExchangeDataAddress => ExecuteResult<uint>.Ok(ExgDataAddress(inst.DataReg, inst.AddrReg)),
            InstructionKind.ExclusiveOr => inst.Size switch
            {
                OpSize.Byte => EorByte(inst.Source, inst.Dest),
                OpSize.Word => EorWord(inst.Source, inst.Dest),
                _ => EorLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.ExclusiveOrToCcr => EoriToCcr(),
            InstructionKind.ExclusiveOrToSr => EoriToSr(),
            InstructionKind.Extend => ExecuteResult<uint>.Ok(Ext(inst.Size, inst.DataReg)),
            InstructionKind.Jump => Jmp(inst.Dest),
            InstructionKind.JumpToSubroutine => Jsr(inst.Dest),
            InstructionKind.Link => Link(inst.AddrReg),
            InstructionKind.LoadEffectiveAddress => Lea(inst.Source, inst.AddrReg),
            InstructionKind.LogicalShiftMemory => LsdMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.LogicalShiftRegister => LsdRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.Move => inst.Size switch
            {
                OpSize.Byte => MoveByte(inst.Source, inst.Dest),
                OpSize.Word => MoveWord(inst.Source, inst.Dest),
                _ => MoveLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.MoveFromSr => MoveFromSr(inst.Dest),
            InstructionKind.MoveMultiple => Movem(inst.Size, inst.Dest, inst.Direction),
            InstructionKind.MovePeripheral => Movep(inst.Size, inst.DataReg, inst.AddrReg, inst.Direction),
            InstructionKind.MoveQuick => ExecuteResult<uint>.Ok(Moveq(unchecked((sbyte)inst.QuickValue), inst.DataReg)),
            InstructionKind.MoveToCcr => MoveToCcr(inst.Source),
            InstructionKind.MoveToSr => MoveToSr(inst.Source),
            InstructionKind.MoveUsp => MoveUsp(inst.UspDirection, inst.AddrReg),
            InstructionKind.MultiplySigned => Muls(inst.DataReg, inst.Source),
            InstructionKind.MultiplyUnsigned => Mulu(inst.DataReg, inst.Source),
            InstructionKind.Negate => inst.Size switch
            {
                OpSize.Byte => NegByte(inst.Dest, inst.WithExtend),
                OpSize.Word => NegWord(inst.Dest, inst.WithExtend),
                _ => NegLongWord(inst.Dest, inst.WithExtend),
            },
            InstructionKind.NegateDecimal => Nbcd(inst.Dest),
            InstructionKind.NoOp => ExecuteResult<uint>.Ok(Nop()),
            InstructionKind.Not => inst.Size switch
            {
                OpSize.Byte => NotByte(inst.Dest),
                OpSize.Word => NotWord(inst.Dest),
                _ => NotLongWord(inst.Dest),
            },
            InstructionKind.Or => inst.Size switch
            {
                OpSize.Byte => OrByte(inst.Source, inst.Dest),
                OpSize.Word => OrWord(inst.Source, inst.Dest),
                _ => OrLongWord(inst.Source, inst.Dest),
            },
            InstructionKind.OrToCcr => OriToCcr(),
            InstructionKind.OrToSr => OriToSr(),
            InstructionKind.PushEffectiveAddress => Pea(inst.Source),
            InstructionKind.Reset => ExecuteResult<uint>.Ok(ResetInstruction()),
            InstructionKind.Return => Ret(inst.RestoreCcr),
            InstructionKind.ReturnFromException => Rte(),
            InstructionKind.RotateMemory => RodMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.RotateRegister => RodRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.RotateThruExtendMemory => RoxdMemory(inst.ShiftDirection, inst.Dest),
            InstructionKind.RotateThruExtendRegister => RoxdRegister(inst.Size, inst.ShiftDirection, inst.DataReg, inst.ShiftCount),
            InstructionKind.Set => Scc(inst.BranchCondition, inst.Dest),
            InstructionKind.Subtract => inst.Size switch
            {
                OpSize.Byte => SubByte(inst.Source, inst.Dest, inst.WithExtend),
                OpSize.Word => SubWord(inst.Source, inst.Dest, inst.WithExtend),
                _ => SubLongWord(inst.Source, inst.Dest, inst.WithExtend),
            },
            InstructionKind.SubtractDecimal => Sbcd(inst.Source, inst.Dest),
            InstructionKind.Swap => ExecuteResult<uint>.Ok(Swap(inst.DataReg)),
            InstructionKind.Stop => Stop(),
            InstructionKind.Test => inst.Size switch
            {
                OpSize.Byte => TstByte(inst.Source),
                OpSize.Word => TstWord(inst.Source),
                _ => TstLongWord(inst.Source),
            },
            InstructionKind.TestAndSet => Tas(inst.Dest),
            InstructionKind.Trap => Trap(inst.TrapVector),
            InstructionKind.TrapOnOverflow => Trapv(),
            InstructionKind.Unlink => Unlk(inst.AddrReg),
            _ => ExecuteResult<uint>.Err(M68kException.IllegalInstruction(_opcode)),
        };
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
                    uint addr = ext.Value;
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
                    if (ShouldTracePc(_tracePc))
                        Console.WriteLine($"[M68K-ABS] pc=0x{_tracePc:X8} hi=0x{hi.Value:X4} lo=0x{lo.Value:X4} addr=0x{addr:X8}");
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
        var result = resolved.Kind switch
        {
            ResolvedAddressKind.DataRegister => ExecuteResult<uint>.Ok(resolved.DataReg.Read(_registers)),
            ResolvedAddressKind.AddressRegister => ExecuteResult<uint>.Ok(resolved.AddrReg.Read(_registers)),
            ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement => ReadBusLong(resolved.Address),
            ResolvedAddressKind.Immediate => ExecuteResult<uint>.Ok(resolved.ImmediateValue),
            _ => ExecuteResult<uint>.Ok(0)
        };
        if (ShouldTracePc(_tracePc) && result.IsOk && resolved.Kind is ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement)
            Console.WriteLine($"[M68K-RL] pc=0x{_tracePc:X8} addr=0x{resolved.Address:X8} value=0x{result.Value:X8}");
        return result;
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
        if (TraceExceptions)
        {
            string detail = ex.Kind == M68kExceptionKind.AddressError
                ? $" addr=0x{ex.Address:X8} op={ex.BusOp} A0=0x{_registers.Address[0]:X8} SP=0x{_registers.StackPointer():X8}"
                : string.Empty;
            string instKind = _instruction.HasValue ? _instruction.Value.Kind.ToString() : "unknown";
            string modeInfo = string.Empty;
            if (_instruction.HasValue)
            {
                Instruction inst = _instruction.Value;
                modeInfo = $" size={inst.Size} src={FormatMode(inst.Source)} dst={FormatMode(inst.Dest)}";
            }
            Console.WriteLine($"[M68K-EX] cpu={_name} kind={ex.Kind} pc=0x{_registers.Pc:X8} op=0x{_opcode:X4} inst={instKind}{modeInfo}{detail}");
        }
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

    private static string FormatMode(AddressingMode mode)
    {
        return mode.Kind switch
        {
            AddressingModeKind.DataDirect => $"D{mode.DataReg.Index}",
            AddressingModeKind.AddressDirect => $"A{mode.AddrReg.Index}",
            AddressingModeKind.AddressIndirect => $"(A{mode.AddrReg.Index})",
            AddressingModeKind.AddressIndirectPostincrement => $"(A{mode.AddrReg.Index})+",
            AddressingModeKind.AddressIndirectPredecrement => $"- (A{mode.AddrReg.Index})",
            AddressingModeKind.AddressIndirectDisplacement => $"(d16,A{mode.AddrReg.Index})",
            AddressingModeKind.AddressIndirectIndexed => $"(d8,An,Xn)A{mode.AddrReg.Index}",
            AddressingModeKind.PcRelativeDisplacement => "(d16,PC)",
            AddressingModeKind.PcRelativeIndexed => "(d8,PC,Xn)",
            AddressingModeKind.AbsoluteShort => "(abs.w)",
            AddressingModeKind.AbsoluteLong => "(abs.l)",
            AddressingModeKind.Immediate => "#imm",
            AddressingModeKind.Quick => "#q",
            _ => mode.Kind.ToString()
        };
    }

    private static bool ShouldTracePc(uint pc)
    {
        if (!TracePcMin.HasValue || !TracePcMax.HasValue)
            return false;
        return pc >= TracePcMin.Value && pc <= TracePcMax.Value;
    }

    private static uint? ReadHexEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];
        if (uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value))
            return value;
        return null;
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
