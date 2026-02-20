using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed partial class InstructionExecutor
{
    private ExecuteResult<(ResolvedAddress Address, byte Value)> ReadByteForExtend(AddressingMode source)
    {
        var address = ResolveAddressWithPost(source, OpSize.Byte);
        if (!address.IsOk) return ExecuteResult<(ResolvedAddress, byte)>.Err(address.Error!.Value);
        byte value = ReadByteResolved(address.Value);
        return ExecuteResult<(ResolvedAddress, byte)>.Ok((address.Value, value));
    }

    private ExecuteResult<(ResolvedAddress Address, ushort Value)> ReadWordForExtend(AddressingMode source)
    {
        var address = ResolveAddressWithPost(source, OpSize.Word);
        if (!address.IsOk) return ExecuteResult<(ResolvedAddress, ushort)>.Err(address.Error!.Value);
        var value = ReadWordResolved(address.Value);
        if (!value.IsOk) return ExecuteResult<(ResolvedAddress, ushort)>.Err(value.Error!.Value);
        return ExecuteResult<(ResolvedAddress, ushort)>.Ok((address.Value, value.Value));
    }

    private ExecuteResult<(ResolvedAddress Address, uint Value)> ReadLongWordForExtend(AddressingMode source)
    {
        if (source.Kind == AddressingModeKind.AddressIndirectPredecrement)
        {
            uint address = source.AddrReg.Read(_registers) - 2;
            source.AddrReg.WriteLong(_registers, address);
            var low = ReadBusWord(address);
            if (!low.IsOk) return ExecuteResult<(ResolvedAddress, uint)>.Err(low.Error!.Value);

            address -= 2;
            source.AddrReg.WriteLong(_registers, address);
            var high = ReadBusWord(address);
            if (!high.IsOk) return ExecuteResult<(ResolvedAddress, uint)>.Err(high.Error!.Value);

            uint value = ((uint)high.Value << 16) | low.Value;
            return ExecuteResult<(ResolvedAddress, uint)>.Ok((ResolvedAddress.Memory(address), value));
        }

        var resolved = ResolveAddressWithPost(source, OpSize.LongWord);
        if (!resolved.IsOk) return ExecuteResult<(ResolvedAddress, uint)>.Err(resolved.Error!.Value);
        var valueLong = ReadLongResolved(resolved.Value);
        if (!valueLong.IsOk) return ExecuteResult<(ResolvedAddress, uint)>.Err(valueLong.Error!.Value);
        return ExecuteResult<(ResolvedAddress, uint)>.Ok((resolved.Value, valueLong.Value));
    }

    private ExecuteResult<uint> ReadAddressOperand(OpSize size, AddressingMode source)
    {
        if (size == OpSize.Word)
        {
            var v = ReadWord(source);
            if (!v.IsOk) return ExecuteResult<uint>.Err(v.Error!.Value);
            return ExecuteResult<uint>.Ok((uint)(short)v.Value);
        }
        if (size == OpSize.LongWord)
        {
            var v = ReadLongWord(source);
            if (!v.IsOk) return ExecuteResult<uint>.Err(v.Error!.Value);
            return ExecuteResult<uint>.Ok(v.Value);
        }

        throw new InvalidOperationException("ADDA does not support bytes");
    }

    private ExecuteResult<uint> Adda(OpSize size, AddressingMode source, AddressRegister dest)
    {
        var operandR = ReadAddressOperand(size, source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        uint operandL = dest.Read(_registers);
        uint sum = unchecked(operandL + operandR.Value);
        dest.WriteLong(_registers, sum);
        return ExecuteResult<uint>.Ok(BinaryOpCycles(size, source, AddressingMode.AddressDirect(dest)));
    }

    private ExecuteResult<uint> AddByte(AddressingMode source, AddressingMode dest, bool withExtend)
    {
        if (withExtend)
            return AddxByte(source, dest);

        if (dest.IsAddressDirect)
            return Adda(OpSize.Byte, source, dest.AddrReg);

        var operandR = ReadByte(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte operandL = ReadByteResolved(destResolved.Value);

        var (value, carry, overflow) = AddBytes(operandL, operandR.Value, false);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Overflow = overflow;
            _registers.Ccr.Zero = value == 0;
            _registers.Ccr.Negative = value.SignBit();
            _registers.Ccr.Extend = carry;
        }

        var write = WriteByteResolvedAsResult(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Byte, source, dest));
    }

    private ExecuteResult<uint> AddWord(AddressingMode source, AddressingMode dest, bool withExtend)
    {
        if (withExtend)
            return AddxWord(source, dest);

        if (dest.IsAddressDirect)
            return Adda(OpSize.Word, source, dest.AddrReg);

        var operandR = ReadWord(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandL = ReadWordResolved(destResolved.Value);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);

        var (value, carry, overflow) = AddWords(operandL.Value, operandR.Value, false);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Overflow = overflow;
            _registers.Ccr.Zero = value == 0;
            _registers.Ccr.Negative = value.SignBit();
            _registers.Ccr.Extend = carry;
        }

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Word, source, dest));
    }

    private ExecuteResult<uint> AddLongWord(AddressingMode source, AddressingMode dest, bool withExtend)
    {
        if (withExtend)
            return AddxLongWord(source, dest);

        if (dest.IsAddressDirect)
            return Adda(OpSize.LongWord, source, dest.AddrReg);

        var operandR = ReadLongWord(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandL = ReadLongResolved(destResolved.Value);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);

        var (value, carry, overflow) = AddLongWords(operandL.Value, operandR.Value, false);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Overflow = overflow;
            _registers.Ccr.Zero = value == 0;
            _registers.Ccr.Negative = value.SignBit();
            _registers.Ccr.Extend = carry;
        }

        var write = WriteLongResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.LongWord, source, dest));
    }

    private ExecuteResult<uint> AddxByte(AddressingMode source, AddressingMode dest)
    {
        var sourceRead = ReadByteForExtend(source);
        if (!sourceRead.IsOk) return ExecuteResult<uint>.Err(sourceRead.Error!.Value);
        var destRead = ReadByteForExtend(dest);
        if (!destRead.IsOk) return ExecuteResult<uint>.Err(destRead.Error!.Value);

        var (value, carry, overflow) = AddBytes(destRead.Value.Value, sourceRead.Value.Value, _registers.Ccr.Extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteByteResolvedAsResult(destRead.Value.Address, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 4u : 18u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> AddxWord(AddressingMode source, AddressingMode dest)
    {
        var sourceRead = ReadWordForExtend(source);
        if (!sourceRead.IsOk) return ExecuteResult<uint>.Err(sourceRead.Error!.Value);
        var destRead = ReadWordForExtend(dest);
        if (!destRead.IsOk) return ExecuteResult<uint>.Err(destRead.Error!.Value);

        var (value, carry, overflow) = AddWords(destRead.Value.Value, sourceRead.Value.Value, _registers.Ccr.Extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteWordResolved(destRead.Value.Address, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 4u : 18u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> AddxLongWord(AddressingMode source, AddressingMode dest)
    {
        var sourceRead = ReadLongWordForExtend(source);
        if (!sourceRead.IsOk) return ExecuteResult<uint>.Err(sourceRead.Error!.Value);
        var destRead = ReadLongWordForExtend(dest);
        if (!destRead.IsOk) return ExecuteResult<uint>.Err(destRead.Error!.Value);

        var (value, carry, overflow) = AddLongWords(destRead.Value.Value, sourceRead.Value.Value, _registers.Ccr.Extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteLongResolved(destRead.Value.Address, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 8u : 30u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> Suba(OpSize size, AddressingMode source, AddressRegister dest)
    {
        var operandR = ReadAddressOperand(size, source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        uint operandL = dest.Read(_registers);
        uint diff = unchecked(operandL - operandR.Value);
        dest.WriteLong(_registers, diff);
        return ExecuteResult<uint>.Ok(BinaryOpCycles(size, source, AddressingMode.AddressDirect(dest)));
    }

    private ExecuteResult<uint> SubByte(AddressingMode source, AddressingMode dest, bool withExtend)
    {
        if (withExtend)
            return SubxByte(source, dest);

        if (dest.IsAddressDirect)
            return Suba(OpSize.Byte, source, dest.AddrReg);

        var operandR = ReadByte(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte operandL = ReadByteResolved(destResolved.Value);

        var (value, carry, overflow) = SubBytes(operandL, operandR.Value, false);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Overflow = overflow;
            _registers.Ccr.Zero = value == 0;
            _registers.Ccr.Negative = value.SignBit();
            _registers.Ccr.Extend = carry;
        }

        var write = WriteByteResolvedAsResult(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Byte, source, dest));
    }

    private ExecuteResult<uint> SubWord(AddressingMode source, AddressingMode dest, bool withExtend)
    {
        if (withExtend)
            return SubxWord(source, dest);

        if (dest.IsAddressDirect)
            return Suba(OpSize.Word, source, dest.AddrReg);

        var operandR = ReadWord(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandL = ReadWordResolved(destResolved.Value);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);

        var (value, carry, overflow) = SubWords(operandL.Value, operandR.Value, false);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Overflow = overflow;
            _registers.Ccr.Zero = value == 0;
            _registers.Ccr.Negative = value.SignBit();
            _registers.Ccr.Extend = carry;
        }

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Word, source, dest));
    }

    private ExecuteResult<uint> SubLongWord(AddressingMode source, AddressingMode dest, bool withExtend)
    {
        if (withExtend)
            return SubxLongWord(source, dest);

        if (dest.IsAddressDirect)
            return Suba(OpSize.LongWord, source, dest.AddrReg);

        var operandR = ReadLongWord(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandL = ReadLongResolved(destResolved.Value);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);

        var (value, carry, overflow) = SubLongWords(operandL.Value, operandR.Value, false);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Overflow = overflow;
            _registers.Ccr.Zero = value == 0;
            _registers.Ccr.Negative = value.SignBit();
            _registers.Ccr.Extend = carry;
        }

        var write = WriteLongResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.LongWord, source, dest));
    }

    private ExecuteResult<uint> SubxByte(AddressingMode source, AddressingMode dest)
    {
        var sourceRead = ReadByteForExtend(source);
        if (!sourceRead.IsOk) return ExecuteResult<uint>.Err(sourceRead.Error!.Value);
        var destRead = ReadByteForExtend(dest);
        if (!destRead.IsOk) return ExecuteResult<uint>.Err(destRead.Error!.Value);

        var (value, carry, overflow) = SubBytes(destRead.Value.Value, sourceRead.Value.Value, _registers.Ccr.Extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteByteResolvedAsResult(destRead.Value.Address, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 4u : 18u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> SubxWord(AddressingMode source, AddressingMode dest)
    {
        var sourceRead = ReadWordForExtend(source);
        if (!sourceRead.IsOk) return ExecuteResult<uint>.Err(sourceRead.Error!.Value);
        var destRead = ReadWordForExtend(dest);
        if (!destRead.IsOk) return ExecuteResult<uint>.Err(destRead.Error!.Value);

        var (value, carry, overflow) = SubWords(destRead.Value.Value, sourceRead.Value.Value, _registers.Ccr.Extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteWordResolved(destRead.Value.Address, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 4u : 18u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> SubxLongWord(AddressingMode source, AddressingMode dest)
    {
        var sourceRead = ReadLongWordForExtend(source);
        if (!sourceRead.IsOk) return ExecuteResult<uint>.Err(sourceRead.Error!.Value);
        var destRead = ReadLongWordForExtend(dest);
        if (!destRead.IsOk) return ExecuteResult<uint>.Err(destRead.Error!.Value);

        var (value, carry, overflow) = SubLongWords(destRead.Value.Value, sourceRead.Value.Value, _registers.Ccr.Extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteLongResolved(destRead.Value.Address, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 8u : 30u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> NegByte(AddressingMode dest, bool withExtend)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte operandR = ReadByteResolved(destResolved.Value);
        bool extend = withExtend && _registers.Ccr.Extend;
        var (difference, carry, overflow) = SubBytes(0, operandR, extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = (!withExtend || _registers.Ccr.Zero) && difference == 0;
        _registers.Ccr.Negative = difference.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteByteResolvedAsResult(destResolved.Value, difference);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.Byte, dest));
    }

    private ExecuteResult<uint> NegWord(AddressingMode dest, bool withExtend)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadWordResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        bool extend = withExtend && _registers.Ccr.Extend;
        var (difference, carry, overflow) = SubWords(0, operandR.Value, extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = (!withExtend || _registers.Ccr.Zero) && difference == 0;
        _registers.Ccr.Negative = difference.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteWordResolved(destResolved.Value, difference);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.Word, dest));
    }

    private ExecuteResult<uint> NegLongWord(AddressingMode dest, bool withExtend)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadLongResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        bool extend = withExtend && _registers.Ccr.Extend;
        var (difference, carry, overflow) = SubLongWords(0, operandR.Value, extend);

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = (!withExtend || _registers.Ccr.Zero) && difference == 0;
        _registers.Ccr.Negative = difference.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteLongResolved(destResolved.Value, difference);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.LongWord, dest));
    }

    private ExecuteResult<uint> CmpByte(AddressingMode source, AddressingMode dest)
    {
        if (dest.IsAddressDirect)
            return Cmpa(OpSize.Byte, source, dest.AddrReg);

        var sourceOperand = ReadByte(source);
        if (!sourceOperand.IsOk) return ExecuteResult<uint>.Err(sourceOperand.Error!.Value);
        var destOperand = ReadByte(dest);
        if (!destOperand.IsOk) return ExecuteResult<uint>.Err(destOperand.Error!.Value);

        CompareBytes(sourceOperand.Value, destOperand.Value, ref _registers.Ccr);
        return ExecuteResult<uint>.Ok(CmpCyclesByte(source, dest));
    }

    private ExecuteResult<uint> CmpWord(AddressingMode source, AddressingMode dest)
    {
        if (dest.IsAddressDirect)
            return Cmpa(OpSize.Word, source, dest.AddrReg);

        var sourceOperand = ReadWord(source);
        if (!sourceOperand.IsOk) return ExecuteResult<uint>.Err(sourceOperand.Error!.Value);
        var destOperand = ReadWord(dest);
        if (!destOperand.IsOk) return ExecuteResult<uint>.Err(destOperand.Error!.Value);

        CompareWords(sourceOperand.Value, destOperand.Value, ref _registers.Ccr);
        return ExecuteResult<uint>.Ok(CmpCyclesWord(source, dest));
    }

    private ExecuteResult<uint> CmpLongWord(AddressingMode source, AddressingMode dest)
    {
        if (dest.IsAddressDirect)
            return Cmpa(OpSize.LongWord, source, dest.AddrReg);

        var sourceOperand = ReadLongWord(source);
        if (!sourceOperand.IsOk) return ExecuteResult<uint>.Err(sourceOperand.Error!.Value);
        var destOperand = ReadLongWord(dest);
        if (!destOperand.IsOk) return ExecuteResult<uint>.Err(destOperand.Error!.Value);

        CompareLongWords(sourceOperand.Value, destOperand.Value, ref _registers.Ccr);
        return ExecuteResult<uint>.Ok(CmpCyclesLong(source, dest));
    }

    private ExecuteResult<uint> Cmpa(OpSize size, AddressingMode source, AddressRegister dest)
    {
        var sourceOperand = ReadAddressOperand(size, source);
        if (!sourceOperand.IsOk) return ExecuteResult<uint>.Err(sourceOperand.Error!.Value);
        uint destOperand = dest.Read(_registers);
        CompareLongWords(sourceOperand.Value, destOperand, ref _registers.Ccr);
        return ExecuteResult<uint>.Ok(6 + source.AddressCalculationCycles(size));
    }

    private ExecuteResult<uint> Muls(DataRegister register, AddressingMode source)
    {
        var operandL = ReadWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        short left = (short)operandL.Value;
        short right = (short)register.Read(_registers);

        uint value = (uint)(left * right);
        register.WriteLong(_registers, value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        bool lastBit = false;
        int alternatingBits = 0;
        for (int i = 0; i < 16; i++)
        {
            bool bit = ((ushort)left).Test(i);
            if (bit != lastBit) alternatingBits++;
            lastBit = bit;
        }

        return ExecuteResult<uint>.Ok(38u + (uint)(2 * alternatingBits) + source.AddressCalculationCycles(OpSize.Word));
    }

    private ExecuteResult<uint> Mulu(DataRegister register, AddressingMode source)
    {
        var operandL = ReadWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        ushort left = operandL.Value;
        ushort right = (ushort)register.Read(_registers);

        uint value = (uint)(left * right);
        register.WriteLong(_registers, value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        int ones = 0;
        for (int i = 0; i < 16; i++)
            if (left.Test(i)) ones++;

        return ExecuteResult<uint>.Ok(38u + (uint)(2 * ones) + source.AddressCalculationCycles(OpSize.Word));
    }

    private M68kException DivideByZeroError(AddressingMode source)
    {
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = false;
        _registers.Ccr.Negative = false;
        return M68kException.DivisionByZero(source.AddressCalculationCycles(OpSize.Word));
    }

    private ExecuteResult<uint> Divs(DataRegister register, AddressingMode source)
    {
        int dividend = (int)register.Read(_registers);
        var divisorRead = ReadWord(source);
        if (!divisorRead.IsOk) return ExecuteResult<uint>.Err(divisorRead.Error!.Value);
        int divisor = (short)divisorRead.Value;

        if (divisor == 0)
            return ExecuteResult<uint>.Err(DivideByZeroError(source));

        int quotient = dividend / divisor;
        int remainder = dividend % divisor;

        if (quotient > short.MaxValue || quotient < short.MinValue)
        {
            _registers.Ccr.Carry = false;
            _registers.Ccr.Overflow = true;

            uint baseCycles;
            if ((Math.Abs(dividend) >> 16) >= Math.Abs(divisor))
            {
                baseCycles = (uint)(16 + (dividend < 0 ? 2 : 0));
                return ExecuteResult<uint>.Ok(baseCycles + source.AddressCalculationCycles(OpSize.Word));
            }

            return ExecuteResult<uint>.Ok(DivsCycleCount(dividend, divisor, quotient, source));
        }

        uint value = ((uint)quotient & 0x0000_FFFF) | ((uint)remainder << 16);
        register.WriteLong(_registers, value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = quotient == 0;
        _registers.Ccr.Negative = quotient < 0;

        return ExecuteResult<uint>.Ok(DivsCycleCount(dividend, divisor, quotient, source));
    }

    private ExecuteResult<uint> Divu(DataRegister register, AddressingMode source)
    {
        uint dividend = register.Read(_registers);
        var divisorRead = ReadWord(source);
        if (!divisorRead.IsOk) return ExecuteResult<uint>.Err(divisorRead.Error!.Value);
        uint divisor = divisorRead.Value;

        if (divisor == 0)
            return ExecuteResult<uint>.Err(DivideByZeroError(source));

        uint quotient = dividend / divisor;
        uint remainder = dividend % divisor;

        if (quotient > ushort.MaxValue)
        {
            _registers.Ccr.Carry = false;
            _registers.Ccr.Overflow = true;
            return ExecuteResult<uint>.Ok(10 + source.AddressCalculationCycles(OpSize.Word));
        }

        uint value = (quotient & 0x0000_FFFF) | (remainder << 16);
        register.WriteLong(_registers, value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = quotient == 0;
        _registers.Ccr.Negative = (quotient & 0x8000) != 0;

        return ExecuteResult<uint>.Ok(DivuCycleCount(dividend, divisor, source));
    }

    private ExecuteResult<uint> Abcd(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadByte(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);

        var destResolved = ResolveAddress(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte operandR = ReadByteResolved(destResolved.Value);

        byte extend = _registers.Ccr.Extend ? (byte)1 : (byte)0;

        byte sum;
        bool carry;
        unchecked
        {
            int raw = operandL.Value + operandR;
            sum = (byte)raw;
            carry = raw > 0xFF;
            if (carry)
            {
                sum = (byte)(sum + extend);
            }
            else
            {
                int raw2 = sum + extend;
                sum = (byte)raw2;
                carry = raw2 > 0xFF;
            }
        }

        int diff = 0;
        if ((operandL.Value & 0x0F) + (operandR & 0x0F) + extend >= 0x10 || (sum & 0x0F) > 0x09)
            diff += 0x06;
        if (sum > 0x99 || carry)
            diff += 0x60;

        int correctedSumRaw = sum + diff;
        byte correctedSum = (byte)correctedSumRaw;
        bool correctedCarry = correctedSumRaw > 0xFF;

        bool bit6Carry = ((sum & 0x7F) + (diff & 0x7F)) >= 0x80;
        bool overflow = bit6Carry != correctedCarry;

        bool finalCarry = carry || correctedCarry;
        _registers.Ccr.Carry = finalCarry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && correctedSum == 0;
        _registers.Ccr.Negative = correctedSum.SignBit();
        _registers.Ccr.Extend = finalCarry;

        var write = WriteByteResolved(destResolved.Value, correctedSum);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 6u : 18u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> Sbcd(AddressingMode source, AddressingMode dest)
    {
        var operandR = ReadByte(source);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);

        var destResolved = ResolveAddress(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte operandL = ReadByteResolved(destResolved.Value);

        byte difference = DecimalSubtract(operandL, operandR.Value);
        var write = WriteByteResolved(destResolved.Value, difference);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = source.IsDataDirect ? 6u : 18u;
        return ExecuteResult<uint>.Ok(cycles);
    }

    private ExecuteResult<uint> Nbcd(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte operandR = ReadByteResolved(destResolved.Value);

        byte difference = DecimalSubtract(0, operandR);

        var write = WriteByteResolved(destResolved.Value, difference);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint cycles = dest.IsDataDirect ? 6u : UnaryOpCycles(OpSize.Byte, dest);
        return ExecuteResult<uint>.Ok(cycles);
    }

    private byte DecimalSubtract(byte operandL, byte operandR)
    {
        byte extend = _registers.Ccr.Extend ? (byte)1 : (byte)0;

        byte difference;
        bool borrow;
        unchecked
        {
            int raw = operandL - operandR;
            difference = (byte)raw;
            borrow = raw < 0;
            if (borrow)
            {
                difference = (byte)(difference - extend);
            }
            else
            {
                int raw2 = difference - extend;
                difference = (byte)raw2;
                borrow = raw2 < 0;
            }
        }

        int diff = 0;
        if ((operandL & 0x0F) < ((operandR & 0x0F) + extend))
            diff += 0x06;
        if (borrow)
            diff += 0x60;

        int correctedRaw = difference - diff;
        byte corrected = (byte)correctedRaw;
        bool correctedBorrow = correctedRaw < 0;

        bool bit6Borrow = (difference & 0x7F) < (diff & 0x7F);
        bool overflow = bit6Borrow != correctedBorrow;

        bool finalBorrow = borrow || correctedBorrow;
        _registers.Ccr.Carry = finalBorrow;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = _registers.Ccr.Zero && corrected == 0;
        _registers.Ccr.Negative = corrected.SignBit();
        _registers.Ccr.Extend = finalBorrow;

        return corrected;
    }

    private static (byte Value, bool Carry, bool Overflow) AddBytes(byte l, byte r, bool extend)
    {
        byte extendOperand = extend ? (byte)1 : (byte)0;
        int sum = l + r + extendOperand;
        byte value = (byte)sum;
        bool carry = sum > 0xFF;
        bool bitM1Carry = ((l & 0x7F) + (r & 0x7F) + extendOperand) > 0x7F;
        bool overflow = bitM1Carry != carry;
        return (value, carry, overflow);
    }

    private static (ushort Value, bool Carry, bool Overflow) AddWords(ushort l, ushort r, bool extend)
    {
        ushort extendOperand = extend ? (ushort)1 : (ushort)0;
        uint sum = (uint)l + r + extendOperand;
        ushort value = (ushort)sum;
        bool carry = sum > 0xFFFF;
        bool bitM1Carry = ((l & 0x7FFF) + (r & 0x7FFF) + extendOperand) > 0x7FFF;
        bool overflow = bitM1Carry != carry;
        return (value, carry, overflow);
    }

    private static (uint Value, bool Carry, bool Overflow) AddLongWords(uint l, uint r, bool extend)
    {
        uint extendOperand = extend ? 1u : 0u;
        ulong sum = (ulong)l + r + extendOperand;
        uint value = (uint)sum;
        bool carry = sum > uint.MaxValue;
        bool bitM1Carry = ((l & 0x7FFF_FFFF) + (r & 0x7FFF_FFFF) + extendOperand) > 0x7FFF_FFFF;
        bool overflow = bitM1Carry != carry;
        return (value, carry, overflow);
    }

    private static (byte Value, bool Carry, bool Overflow) SubBytes(byte l, byte r, bool extend)
    {
        byte extendOperand = extend ? (byte)1 : (byte)0;
        int diff = l - r - extendOperand;
        byte value = (byte)diff;
        bool borrow = diff < 0;
        bool bitM1Borrow = (l & 0x7F) < ((r & 0x7F) + extendOperand);
        bool overflow = bitM1Borrow != borrow;
        return (value, borrow, overflow);
    }

    private static (ushort Value, bool Carry, bool Overflow) SubWords(ushort l, ushort r, bool extend)
    {
        ushort extendOperand = extend ? (ushort)1 : (ushort)0;
        int diff = l - r - extendOperand;
        ushort value = (ushort)diff;
        bool borrow = diff < 0;
        bool bitM1Borrow = (l & 0x7FFF) < ((r & 0x7FFF) + extendOperand);
        bool overflow = bitM1Borrow != borrow;
        return (value, borrow, overflow);
    }

    private static (uint Value, bool Carry, bool Overflow) SubLongWords(uint l, uint r, bool extend)
    {
        uint extendOperand = extend ? 1u : 0u;
        long diff = (long)l - r - extendOperand;
        uint value = (uint)diff;
        bool borrow = diff < 0;
        bool bitM1Borrow = (l & 0x7FFF_FFFF) < ((r & 0x7FFF_FFFF) + extendOperand);
        bool overflow = bitM1Borrow != borrow;
        return (value, borrow, overflow);
    }

    private static void CompareBytes(byte source, byte dest, ref ConditionCodes ccr)
    {
        int diff = dest - source;
        byte value = (byte)diff;
        bool borrow = diff < 0;
        bool bitM1Borrow = (dest & 0x7F) < (source & 0x7F);
        bool overflow = bitM1Borrow != borrow;

        ccr.Carry = borrow;
        ccr.Overflow = overflow;
        ccr.Zero = value == 0;
        ccr.Negative = value.SignBit();
    }

    private static void CompareWords(ushort source, ushort dest, ref ConditionCodes ccr)
    {
        int diff = dest - source;
        ushort value = (ushort)diff;
        bool borrow = diff < 0;
        bool bitM1Borrow = (dest & 0x7FFF) < (source & 0x7FFF);
        bool overflow = bitM1Borrow != borrow;

        ccr.Carry = borrow;
        ccr.Overflow = overflow;
        ccr.Zero = value == 0;
        ccr.Negative = value.SignBit();
    }

    private static void CompareLongWords(uint source, uint dest, ref ConditionCodes ccr)
    {
        long diff = (long)dest - source;
        uint value = (uint)diff;
        bool borrow = diff < 0;
        bool bitM1Borrow = (dest & 0x7FFF_FFFF) < (source & 0x7FFF_FFFF);
        bool overflow = bitM1Borrow != borrow;

        ccr.Carry = borrow;
        ccr.Overflow = overflow;
        ccr.Zero = value == 0;
        ccr.Negative = value.SignBit();
    }

    private static uint DivuCycleCount(uint dividend, uint divisor, AddressingMode source)
    {
        uint shiftedDivisor = divisor << 16;
        uint localDividend = dividend;
        uint addedCycles = 0;

        for (int i = 0; i < 15; i++)
        {
            bool prevMsb = localDividend.Test(31);
            localDividend <<= 1;

            bool negative = localDividend < shiftedDivisor;
            if (prevMsb || !negative)
                localDividend = unchecked(localDividend - shiftedDivisor);

            addedCycles += (uint)(!prevMsb ? 2 : 0);
            addedCycles += (uint)(!prevMsb && negative ? 2 : 0);
        }

        return 76 + addedCycles + source.AddressCalculationCycles(OpSize.Word);
    }

    private static uint DivsCycleCount(int dividend, int divisor, int quotient, AddressingMode source)
    {
        uint baseCycles = (uint)(dividend < 0 ? 122 : 120);
        uint addedCycles = 0;

        short absoluteQuotient = (short)Math.Abs(quotient);
        for (int i = 0; i < 15; i++)
        {
            addedCycles += (uint)(absoluteQuotient >= 0 ? 2 : 0);
            absoluteQuotient <<= 1;
        }

        if (divisor < 0)
            addedCycles += 2;
        else if (dividend < 0)
            addedCycles += 4;

        return baseCycles + addedCycles + source.AddressCalculationCycles(OpSize.Word);
    }

    private static uint CmpCyclesByte(AddressingMode source, AddressingMode dest)
    {
        if (source.Kind == AddressingModeKind.AddressIndirectPostincrement && dest.Kind == AddressingModeKind.AddressIndirectPostincrement)
            return 12;
        if (source.Kind == AddressingModeKind.Immediate && dest.IsDataDirect)
            return 8;
        if (source.Kind == AddressingModeKind.Immediate)
            return BinaryOpCycles(OpSize.Byte, source, dest) - 4;
        return BinaryOpCycles(OpSize.Byte, source, dest);
    }

    private static uint CmpCyclesWord(AddressingMode source, AddressingMode dest)
    {
        if (source.Kind == AddressingModeKind.AddressIndirectPostincrement && dest.Kind == AddressingModeKind.AddressIndirectPostincrement)
            return 12;
        if (source.Kind == AddressingModeKind.Immediate && dest.IsDataDirect)
            return 8;
        if (source.Kind == AddressingModeKind.Immediate)
            return BinaryOpCycles(OpSize.Word, source, dest) - 4;
        return BinaryOpCycles(OpSize.Word, source, dest);
    }

    private static uint CmpCyclesLong(AddressingMode source, AddressingMode dest)
    {
        if (source.Kind == AddressingModeKind.AddressIndirectPostincrement && dest.Kind == AddressingModeKind.AddressIndirectPostincrement)
            return 20;
        if (source.Kind == AddressingModeKind.Immediate && dest.IsDataDirect)
            return 14;
        if (source.Kind == AddressingModeKind.Immediate)
            return BinaryOpCycles(OpSize.LongWord, source, dest) - 8;
        if ((source.IsDataDirect || source.IsAddressDirect) && dest.IsDataDirect)
            return 6;
        return BinaryOpCycles(OpSize.LongWord, source, dest);
    }
}
