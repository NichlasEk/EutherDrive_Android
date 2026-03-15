using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed partial class InstructionExecutor
{
    private byte ReadByteResolved(ResolvedAddress resolved)
    {
        return resolved.Kind switch
        {
            ResolvedAddressKind.DataRegister => (byte)resolved.DataReg.Read(_registers),
            ResolvedAddressKind.AddressRegister => (byte)resolved.AddrReg.Read(_registers),
            ResolvedAddressKind.Memory or ResolvedAddressKind.MemoryPostincrement => _bus.ReadByte(resolved.Address),
            ResolvedAddressKind.Immediate => (byte)resolved.ImmediateValue,
            _ => 0
        };
    }

    private ExecuteResult<byte> ReadByteResolvedAsResult(ResolvedAddress resolved)
    {
        return ExecuteResult<byte>.Ok(ReadByteResolved(resolved));
    }

    private ExecuteResult<byte> ReadByte(AddressingMode source)
    {
        var resolved = ResolveAddressWithPost(source, OpSize.Byte);
        if (!resolved.IsOk) return ExecuteResult<byte>.Err(resolved.Error!.Value);
        return ExecuteResult<byte>.Ok(ReadByteResolved(resolved.Value));
    }

    private ExecuteResult<ushort> ReadWord(AddressingMode source)
    {
        var resolved = ResolveAddressWithPost(source, OpSize.Word);
        if (!resolved.IsOk) return ExecuteResult<ushort>.Err(resolved.Error!.Value);
        return ReadWordResolved(resolved.Value);
    }

    private ExecuteResult<uint> ReadLongWord(AddressingMode source)
    {
        var resolved = ResolveAddressWithPost(source, OpSize.LongWord);
        if (!resolved.IsOk) return ExecuteResult<uint>.Err(resolved.Error!.Value);
        return ReadLongResolved(resolved.Value);
    }

    private ExecuteResult<object> WriteByteResolved(ResolvedAddress resolved, byte value)
    {
        switch (resolved.Kind)
        {
            case ResolvedAddressKind.DataRegister:
                resolved.DataReg.WriteByte(_registers, value);
                return ExecuteResult<object>.Ok(null!);
            case ResolvedAddressKind.AddressRegister:
                throw new InvalidOperationException("Cannot write a byte to an address register.");
            case ResolvedAddressKind.Memory:
            case ResolvedAddressKind.MemoryPostincrement:
                if (TraceWriteAddress.HasValue && resolved.Address == TraceWriteAddress.Value)
                {
                    string instKind = _instruction?.Kind.ToString() ?? "?";
                    string line =
                        $"[M68K-WB] cpu={_name} tracePc=0x{_tracePc:X8} curPc=0x{_registers.Pc:X8} op=0x{_opcode:X4} inst={instKind} " +
                        $"addr=0x{resolved.Address:X8} val=0x{value:X2}";
                    Console.WriteLine(line);
                    AppendTraceLine(TraceWriteFile, line);
                }
                _bus.WriteByte(resolved.Address, value);
                return ExecuteResult<object>.Ok(null!);
            default:
                throw new InvalidOperationException("Cannot write to immediate addressing mode.");
        }
    }

    private ExecuteResult<object> WriteByteResolvedAsResult(ResolvedAddress resolved, byte value)
    {
        return WriteByteResolved(resolved, value);
    }

    private ExecuteResult<object> WriteByte(AddressingMode dest, byte value)
    {
        var resolved = ResolveAddress(dest, OpSize.Byte);
        if (!resolved.IsOk) return ExecuteResult<object>.Err(resolved.Error!.Value);
        var result = WriteByteResolved(resolved.Value, value);
        if (!result.IsOk) return result;
        resolved.Value.ApplyPost(_registers);
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<object> WriteWord(AddressingMode dest, ushort value)
    {
        var resolved = ResolveAddress(dest, OpSize.Word);
        if (!resolved.IsOk) return ExecuteResult<object>.Err(resolved.Error!.Value);
        var result = WriteWordResolved(resolved.Value, value);
        if (!result.IsOk) return result;
        resolved.Value.ApplyPost(_registers);
        return ExecuteResult<object>.Ok(null!);
    }

    private ExecuteResult<object> WriteLongWord(AddressingMode dest, uint value)
    {
        var resolved = ResolveAddress(dest, OpSize.LongWord);
        if (!resolved.IsOk) return ExecuteResult<object>.Err(resolved.Error!.Value);
        var result = WriteLongResolved(resolved.Value, value);
        if (!result.IsOk) return result;
        resolved.Value.ApplyPost(_registers);
        return ExecuteResult<object>.Ok(null!);
    }

    private static uint BinaryOpCycles(OpSize size, AddressingMode source, AddressingMode dest)
    {
        uint cycles = size switch
        {
            OpSize.Byte or OpSize.Word => 4u,
            _ => 8u,
        };

        if (size == OpSize.Word && dest.IsAddressDirect)
            cycles += 4;

        cycles += source.AddressCalculationCycles(size);
        cycles += dest.AddressCalculationCycles(size);

        if (size == OpSize.LongWord && source.IsMemory && (dest.IsDataDirect || dest.IsAddressDirect))
            cycles -= 2;

        if (dest.IsMemory)
            cycles += 4;

        return cycles;
    }

    private static uint UnaryOpCycles(OpSize size, AddressingMode dest)
    {
        uint cycles = size switch
        {
            OpSize.Byte or OpSize.Word => 4u,
            _ => 8u,
        };

        if (size == OpSize.Word && dest.IsAddressDirect)
            cycles += 4;

        cycles += dest.AddressCalculationCycles(size);

        if (dest.IsMemory)
            cycles += 4;

        return cycles;
    }

    private static uint ShiftRegisterCycles(OpSize size, uint shifts)
    {
        uint baseCycles = size == OpSize.LongWord ? 8u : 6u;
        return baseCycles + 2u * shifts;
    }

    private static uint ShiftMemoryCycles(AddressingMode dest)
    {
        return 8u + dest.AddressCalculationCycles(OpSize.Word);
    }
}
