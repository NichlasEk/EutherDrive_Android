using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed partial class InstructionExecutor
{
    private const uint TrapVectorOffset = 32;
    private const uint OverflowVector = 7;

    private ExecuteResult<uint> ResolveToMemoryAddress(AddressingMode source)
    {
        var resolved = ResolveAddress(source, OpSize.LongWord);
        if (!resolved.IsOk) return ExecuteResult<uint>.Err(resolved.Error!.Value);
        if (resolved.Value.Kind != ResolvedAddressKind.Memory)
            throw new InvalidOperationException("Effective address operation resolved to non-memory addressing mode.");
        return ExecuteResult<uint>.Ok(resolved.Value.Address);
    }

    private ExecuteResult<uint> Lea(AddressingMode source, AddressRegister register)
    {
        var address = ResolveToMemoryAddress(source);
        if (!address.IsOk) return ExecuteResult<uint>.Err(address.Error!.Value);
        register.WriteLong(_registers, address.Value);
        return ExecuteResult<uint>.Ok(EffectiveAddressCycles(source));
    }

    private ExecuteResult<uint> Pea(AddressingMode source)
    {
        var address = ResolveToMemoryAddress(source);
        if (!address.IsOk) return ExecuteResult<uint>.Err(address.Error!.Value);
        var r0 = PushStackU32(address.Value);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
        return ExecuteResult<uint>.Ok(8 + EffectiveAddressCycles(source));
    }

    private ExecuteResult<uint> Jmp(AddressingMode source)
    {
        var address = ResolveToMemoryAddress(source);
        if (!address.IsOk) return ExecuteResult<uint>.Err(address.Error!.Value);
        var r0 = JumpToAddress(address.Value);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
        return ExecuteResult<uint>.Ok(JumpCycles(source));
    }

    private ExecuteResult<uint> Jsr(AddressingMode source)
    {
        var address = ResolveToMemoryAddress(source);
        if (!address.IsOk) return ExecuteResult<uint>.Err(address.Error!.Value);
        uint oldPc = _registers.Pc;
        var r0 = JumpToAddress(address.Value);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
        var r1 = PushStackU32(oldPc);
        if (!r1.IsOk) return ExecuteResult<uint>.Err(r1.Error!.Value);
        return ExecuteResult<uint>.Ok(8 + JumpCycles(source));
    }

    private ExecuteResult<uint> Link(AddressRegister register)
    {
        var extension = FetchOperand();
        if (!extension.IsOk) return ExecuteResult<uint>.Err(extension.Error!.Value);
        short displacement = (short)extension.Value;

        ExecuteResult<object> pushResult;
        if (register.IsStackPointer)
            pushResult = PushStackU32(_registers.StackPointer() - 4);
        else
            pushResult = PushStackU32(register.Read(_registers));
        if (!pushResult.IsOk) return ExecuteResult<uint>.Err(pushResult.Error!.Value);

        uint sp = _registers.StackPointer();
        register.WriteLong(_registers, sp);
        _registers.SetStackPointer(unchecked(sp + (uint)displacement));

        return ExecuteResult<uint>.Ok(16);
    }

    private ExecuteResult<uint> Unlk(AddressRegister register)
    {
        _registers.SetStackPointer(register.Read(_registers));
        var address = PopStackU32();
        if (!address.IsOk) return ExecuteResult<uint>.Err(address.Error!.Value);
        register.WriteLong(_registers, address.Value);
        return ExecuteResult<uint>.Ok(12);
    }

    private ExecuteResult<uint> Ret(bool restoreCcr)
    {
        if (restoreCcr)
        {
            var word = PopStackU16();
            if (!word.IsOk) return ExecuteResult<uint>.Err(word.Error!.Value);
            _registers.Ccr = ConditionCodes.FromByte((byte)word.Value);
        }

        var pc = PopStackU32();
        if (!pc.IsOk) return ExecuteResult<uint>.Err(pc.Error!.Value);
        var jump = JumpToAddress(pc.Value);
        if (!jump.IsOk) return ExecuteResult<uint>.Err(jump.Error!.Value);
        return ExecuteResult<uint>.Ok(restoreCcr ? 20u : 16u);
    }

    private ExecuteResult<uint> Rte()
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());

        var sr = PopStackU16();
        if (!sr.IsOk) return ExecuteResult<uint>.Err(sr.Error!.Value);
        var pc = PopStackU32();
        if (!pc.IsOk) return ExecuteResult<uint>.Err(pc.Error!.Value);

        _registers.SetStatusRegister(sr.Value);
        var r0 = JumpToAddress(pc.Value);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> Trapv()
    {
        if (_registers.Ccr.Overflow)
            return ExecuteResult<uint>.Err(M68kException.Trap(OverflowVector));
        return ExecuteResult<uint>.Ok(4);
    }

    private ExecuteResult<uint> Chk(DataRegister register, AddressingMode source)
    {
        var upper = ReadWord(source);
        if (!upper.IsOk) return ExecuteResult<uint>.Err(upper.Error!.Value);
        short upperBound = (short)upper.Value;

        short value = (short)register.Read(_registers);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = false;

        uint addressCycles = source.AddressCalculationCycles(OpSize.Word);

        if (value > upperBound)
        {
            _registers.Ccr.Negative = value < 0;
            return ExecuteResult<uint>.Err(M68kException.CheckRegister(addressCycles + 8));
        }
        if (value < 0)
        {
            _registers.Ccr.Negative = true;
            return ExecuteResult<uint>.Err(M68kException.CheckRegister(addressCycles + 10));
        }

        return ExecuteResult<uint>.Ok(addressCycles + 10);
    }

    private ExecuteResult<(short Displacement, bool FetchedExtension)> FetchBranchDisplacement(sbyte displacement)
    {
        if (displacement == 0)
        {
            var extension = FetchOperand();
            if (!extension.IsOk) return ExecuteResult<(short, bool)>.Err(extension.Error!.Value);
            return ExecuteResult<(short, bool)>.Ok(((short)extension.Value, true));
        }

        return ExecuteResult<(short, bool)>.Ok((displacement, false));
    }

    private ExecuteResult<uint> Branch(BranchCondition condition, sbyte displacement)
    {
        uint pc = _registers.Pc;
        var disp = FetchBranchDisplacement(displacement);
        if (!disp.IsOk) return ExecuteResult<uint>.Err(disp.Error!.Value);
        if (condition.Check(_registers.Ccr))
        {
            // Branch displacement is relative to PC after opcode fetch (extension word address)
            uint basePc = pc;
            uint address = unchecked(basePc + (uint)disp.Value.Displacement);
            var jump = JumpToAddress(address);
            if (!jump.IsOk) return ExecuteResult<uint>.Err(jump.Error!.Value);
            return ExecuteResult<uint>.Ok(10);
        }

        return ExecuteResult<uint>.Ok(disp.Value.FetchedExtension ? 12u : 8u);
    }

    private ExecuteResult<uint> Bsr(sbyte displacement)
    {
        uint pc = _registers.Pc;
        var disp = FetchBranchDisplacement(displacement);
        if (!disp.IsOk) return ExecuteResult<uint>.Err(disp.Error!.Value);

        var r0 = PushStackU32(_registers.Pc);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);

        // Branch displacement is relative to PC after opcode fetch (extension word address)
        uint basePc = pc;
        uint address = unchecked(basePc + (uint)disp.Value.Displacement);
        var jump = JumpToAddress(address);
        if (!jump.IsOk) return ExecuteResult<uint>.Err(jump.Error!.Value);
        return ExecuteResult<uint>.Ok(18);
    }

    private ExecuteResult<uint> Dbcc(BranchCondition condition, DataRegister register)
    {
        uint pcBefore = _registers.Pc;
        var displacement = FetchOperand();
        if (!displacement.IsOk) return ExecuteResult<uint>.Err(displacement.Error!.Value);
        short disp = (short)displacement.Value;

        if (!condition.Check(_registers.Ccr))
        {
            ushort value = (ushort)register.Read(_registers);
            register.WriteWord(_registers, (ushort)(value - 1));

            if (value != 0)
            {
                // DBcc displacement is relative to the extension word address (PC before FetchOperand)
                uint address = unchecked(pcBefore + (uint)disp);
                var jump = JumpToAddress(address);
                if (!jump.IsOk) return ExecuteResult<uint>.Err(jump.Error!.Value);
                return ExecuteResult<uint>.Ok(10);
            }
            return ExecuteResult<uint>.Ok(14);
        }

        return ExecuteResult<uint>.Ok(12);
    }

    private ExecuteResult<uint> Scc(BranchCondition condition, AddressingMode dest)
    {
        bool cc = condition.Check(_registers.Ccr);
        byte value = cc ? (byte)0xFF : (byte)0x00;

        var r0 = WriteByte(dest, value);
        if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);

        if (dest.IsDataDirect)
            return ExecuteResult<uint>.Ok(4u + (cc ? 2u : 0u));

        return ExecuteResult<uint>.Ok(8 + dest.AddressCalculationCycles(OpSize.Byte));
    }

    private ExecuteResult<uint> Stop()
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());

        var sr = FetchOperand();
        if (!sr.IsOk) return ExecuteResult<uint>.Err(sr.Error!.Value);
        _registers.SetStatusRegister(sr.Value);
        _registers.Stopped = true;
        return ExecuteResult<uint>.Ok(4);
    }

    private static uint JumpCycles(AddressingMode addressingMode)
    {
        return addressingMode.Kind switch
        {
            AddressingModeKind.AddressIndirect => 8u,
            AddressingModeKind.AddressIndirectDisplacement or AddressingModeKind.PcRelativeDisplacement or AddressingModeKind.AbsoluteShort => 10u,
            AddressingModeKind.AddressIndirectIndexed or AddressingModeKind.PcRelativeIndexed => 14u,
            AddressingModeKind.AbsoluteLong => 12u,
            _ => throw new InvalidOperationException($"Invalid jump addressing mode: {addressingMode.Kind}"),
        };
    }

    private static uint EffectiveAddressCycles(AddressingMode addressingMode)
    {
        return addressingMode.Kind switch
        {
            AddressingModeKind.AddressIndirectIndexed or AddressingModeKind.PcRelativeIndexed => 12u,
            _ => addressingMode.AddressCalculationCycles(OpSize.Byte),
        };
    }

    private static uint Nop() => 4;

    private static uint ResetInstruction() => 132;

    private static ExecuteResult<uint> Trap(uint vector)
    {
        return ExecuteResult<uint>.Err(M68kException.Trap(TrapVectorOffset + vector));
    }
}
