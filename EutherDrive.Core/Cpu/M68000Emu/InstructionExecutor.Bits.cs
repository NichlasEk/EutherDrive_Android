using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed partial class InstructionExecutor
{
    private ExecuteResult<uint> AndByte(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadByte(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadByteResolved(destResolved.Value);
        byte value = (byte)(operandL.Value & operandR);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteByteResolvedAsResult(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Byte, source, dest));
    }

    private ExecuteResult<uint> AndWord(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadWordResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        ushort value = (ushort)(operandL.Value & operandR.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Word, source, dest));
    }

    private ExecuteResult<uint> AndLongWord(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadLongWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadLongResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        uint value = operandL.Value & operandR.Value;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteLongResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.LongWord, source, dest));
    }

    private ExecuteResult<uint> OrByte(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadByte(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadByteResolved(destResolved.Value);
        byte value = (byte)(operandL.Value | operandR);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteByteResolvedAsResult(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Byte, source, dest));
    }

    private ExecuteResult<uint> OrWord(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadWordResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        ushort value = (ushort)(operandL.Value | operandR.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Word, source, dest));
    }

    private ExecuteResult<uint> OrLongWord(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadLongWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadLongResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        uint value = operandL.Value | operandR.Value;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteLongResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.LongWord, source, dest));
    }

    private ExecuteResult<uint> EorByte(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadByte(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadByteResolved(destResolved.Value);
        byte value = (byte)(operandL.Value ^ operandR);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteByteResolvedAsResult(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Byte, source, dest));
    }

    private ExecuteResult<uint> EorWord(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadWordResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        ushort value = (ushort)(operandL.Value ^ operandR.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.Word, source, dest));
    }

    private ExecuteResult<uint> EorLongWord(AddressingMode source, AddressingMode dest)
    {
        var operandL = ReadLongWord(source);
        if (!operandL.IsOk) return ExecuteResult<uint>.Err(operandL.Error!.Value);
        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var operandR = ReadLongResolved(destResolved.Value);
        if (!operandR.IsOk) return ExecuteResult<uint>.Err(operandR.Error!.Value);
        uint value = operandL.Value ^ operandR.Value;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteLongResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(BinaryOpCycles(OpSize.LongWord, source, dest));
    }

    private ExecuteResult<uint> AndiToCcr()
    {
        var value = ReadByte(AddressingMode.Immediate());
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        byte result = (byte)(value.Value & _registers.Ccr.ToByte());
        _registers.Ccr = ConditionCodes.FromByte(result);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> OriToCcr()
    {
        var value = ReadByte(AddressingMode.Immediate());
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        byte result = (byte)(value.Value | _registers.Ccr.ToByte());
        _registers.Ccr = ConditionCodes.FromByte(result);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> EoriToCcr()
    {
        var value = ReadByte(AddressingMode.Immediate());
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        byte result = (byte)(value.Value ^ _registers.Ccr.ToByte());
        _registers.Ccr = ConditionCodes.FromByte(result);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> AndiToSr()
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());
        var value = ReadWord(AddressingMode.Immediate());
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        ushort result = (ushort)(value.Value & _registers.StatusRegister());
        _registers.SetStatusRegister(result);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> OriToSr()
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());
        var value = ReadWord(AddressingMode.Immediate());
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        ushort result = (ushort)(value.Value | _registers.StatusRegister());
        _registers.SetStatusRegister(result);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> EoriToSr()
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());
        var value = ReadWord(AddressingMode.Immediate());
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        ushort result = (ushort)(value.Value ^ _registers.StatusRegister());
        _registers.SetStatusRegister(result);
        return ExecuteResult<uint>.Ok(20);
    }

    private ExecuteResult<uint> Btst(AddressingMode source, AddressingMode dest)
    {
        var bitIndex = ReadByte(source);
        if (!bitIndex.IsOk) return ExecuteResult<uint>.Err(bitIndex.Error!.Value);
        if (dest.IsDataDirect)
        {
            uint value = dest.DataReg.Read(_registers);
            int bit = bitIndex.Value % 32;
            _registers.Ccr.Zero = !value.Test(bit);
        }
        else
        {
            var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
            if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
            byte value = ReadByteResolved(destResolved.Value);
            int bit = bitIndex.Value % 8;
            _registers.Ccr.Zero = !value.Test(bit);
        }

        uint destCycles = dest.Kind switch
        {
            AddressingModeKind.DataDirect => 2u,
            AddressingModeKind.Immediate => 6u,
            _ => dest.AddressCalculationCycles(OpSize.Byte),
        };

        uint sourceCycles = source.Kind == AddressingModeKind.Immediate ? 4u : 0u;

        return ExecuteResult<uint>.Ok(4 + sourceCycles + destCycles);
    }

    private ExecuteResult<uint> Bclr(AddressingMode source, AddressingMode dest)
    {
        return BitTestModify(source, dest, (value, bit) => value & ~(1u << bit), extraDataWriteCycles: 2);
    }

    private ExecuteResult<uint> Bset(AddressingMode source, AddressingMode dest)
    {
        return BitTestModify(source, dest, (value, bit) => value | (1u << bit));
    }

    private ExecuteResult<uint> Bchg(AddressingMode source, AddressingMode dest)
    {
        return BitTestModify(source, dest, (value, bit) => value.Test(bit) ? value & ~(1u << bit) : value | (1u << bit));
    }

    private ExecuteResult<uint> BitTestModify(AddressingMode source, AddressingMode dest, Func<uint, int, uint> op, uint extraDataWriteCycles = 0)
    {
        var bitIndex = ReadByte(source);
        if (!bitIndex.IsOk) return ExecuteResult<uint>.Err(bitIndex.Error!.Value);

        if (dest.IsDataDirect)
        {
            uint value = dest.DataReg.Read(_registers);
            int bit = bitIndex.Value % 32;
            _registers.Ccr.Zero = !value.Test(bit);
            uint newValue = op(value, bit);
            dest.DataReg.WriteLong(_registers, newValue);
        }
        else
        {
            var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
            if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
            byte value = ReadByteResolved(destResolved.Value);
            int bit = bitIndex.Value % 8;
            _registers.Ccr.Zero = !value.Test(bit);
            byte newValue = (byte)op(value, bit);
            var write = WriteByteResolved(destResolved.Value, newValue);
            if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);
        }

        uint destCycles = dest.IsDataDirect
            ? 2u + extraDataWriteCycles + ((bitIndex.Value % 32) < 16 ? 0u : 2u)
            : 4u + dest.AddressCalculationCycles(OpSize.Byte);

        uint sourceCycles = source.Kind == AddressingModeKind.Immediate ? 4u : 0u;

        return ExecuteResult<uint>.Ok(4 + sourceCycles + destCycles);
    }

    private uint AslRegisterU8(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        sbyte value = (sbyte)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = ((byte)value).Test(7);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            if (value.SignBit() != ((sbyte)(value << 1)).SignBit())
                _registers.Ccr.Overflow = true;
            value = (sbyte)(value << 1);
        }
        reg.WriteByte(_registers, (byte)value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint AslRegisterU16(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        short value = (short)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = ((ushort)value).Test(15);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            if (value.SignBit() != ((short)(value << 1)).SignBit())
                _registers.Ccr.Overflow = true;
            value = (short)(value << 1);
        }
        reg.WriteWord(_registers, (ushort)value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint AslRegisterU32(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        int value = (int)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = ((uint)value).Test(31);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            if (value.SignBit() != (value << 1).SignBit())
                _registers.Ccr.Overflow = true;
            value <<= 1;
        }
        reg.WriteLong(_registers, (uint)value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint AsrRegisterU8(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        sbyte value = (sbyte)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = ((byte)value).Test(0);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            if (value.SignBit() != ((sbyte)(value >> 1)).SignBit())
                _registers.Ccr.Overflow = true;
            value = (sbyte)(value >> 1);
        }
        reg.WriteByte(_registers, (byte)value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint AsrRegisterU16(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        short value = (short)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = ((ushort)value).Test(0);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            if (value.SignBit() != ((short)(value >> 1)).SignBit())
                _registers.Ccr.Overflow = true;
            value = (short)(value >> 1);
        }
        reg.WriteWord(_registers, (ushort)value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint AsrRegisterU32(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        int value = (int)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = ((uint)value).Test(0);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            if (value.SignBit() != (value >> 1).SignBit())
                _registers.Ccr.Overflow = true;
            value >>= 1;
        }
        reg.WriteLong(_registers, (uint)value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint LslRegisterU8(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        byte value = (byte)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = value.Test(7);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            value = (byte)(value << 1);
        }
        reg.WriteByte(_registers, value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint LslRegisterU16(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        ushort value = (ushort)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = value.Test(15);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            value = (ushort)(value << 1);
        }
        reg.WriteWord(_registers, value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint LslRegisterU32(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        uint value = reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = value.Test(31);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            value <<= 1;
        }
        reg.WriteLong(_registers, value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint LsrRegisterU8(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        byte value = (byte)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = value.Test(0);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            value = (byte)(value >> 1);
        }
        reg.WriteByte(_registers, value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint LsrRegisterU16(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        ushort value = (ushort)reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = value.Test(0);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            value = (ushort)(value >> 1);
        }
        reg.WriteWord(_registers, value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint LsrRegisterU32(DataRegister reg, ShiftCount count)
    {
        uint shifts = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        uint value = reg.Read(_registers);
        for (uint i = 0; i < shifts; i++)
        {
            bool carry = value.Test(0);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
            value >>= 1;
        }
        reg.WriteLong(_registers, value);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        return shifts;
    }

    private uint RolRegisterU8(DataRegister reg, ShiftCount count)
    {
        return RotateLeftRegister(reg, count, 8);
    }

    private uint RolRegisterU16(DataRegister reg, ShiftCount count)
    {
        return RotateLeftRegister(reg, count, 16);
    }

    private uint RolRegisterU32(DataRegister reg, ShiftCount count)
    {
        return RotateLeftRegister(reg, count, 32);
    }

    private uint RorRegisterU8(DataRegister reg, ShiftCount count)
    {
        return RotateRightRegister(reg, count, 8);
    }

    private uint RorRegisterU16(DataRegister reg, ShiftCount count)
    {
        return RotateRightRegister(reg, count, 16);
    }

    private uint RorRegisterU32(DataRegister reg, ShiftCount count)
    {
        return RotateRightRegister(reg, count, 32);
    }

    private uint RoxlRegisterU8(DataRegister reg, ShiftCount count)
    {
        return RotateThroughExtendLeft(reg, count, 8);
    }

    private uint RoxlRegisterU16(DataRegister reg, ShiftCount count)
    {
        return RotateThroughExtendLeft(reg, count, 16);
    }

    private uint RoxlRegisterU32(DataRegister reg, ShiftCount count)
    {
        return RotateThroughExtendLeft(reg, count, 32);
    }

    private uint RoxrRegisterU8(DataRegister reg, ShiftCount count)
    {
        return RotateThroughExtendRight(reg, count, 8);
    }

    private uint RoxrRegisterU16(DataRegister reg, ShiftCount count)
    {
        return RotateThroughExtendRight(reg, count, 16);
    }

    private uint RoxrRegisterU32(DataRegister reg, ShiftCount count)
    {
        return RotateThroughExtendRight(reg, count, 32);
    }

    private uint RotateLeftRegister(DataRegister reg, ShiftCount count, int width)
    {
        uint rotates = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Carry = false;
        uint value = reg.Read(_registers) & MaskForWidth(width);
        for (uint i = 0; i < rotates; i++)
        {
            bool carry = ((value >> (width - 1)) & 1u) != 0;
            uint rotatingIn = carry ? 1u : 0u;
            value = ((value << 1) | rotatingIn) & MaskForWidth(width);
            _registers.Ccr.Carry = carry;
        }
        WriteRegisterByWidth(reg, value, width);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = ((value >> (width - 1)) & 1u) != 0;
        return rotates;
    }

    private uint RotateRightRegister(DataRegister reg, ShiftCount count, int width)
    {
        uint rotates = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Carry = false;
        uint value = reg.Read(_registers) & MaskForWidth(width);
        for (uint i = 0; i < rotates; i++)
        {
            bool carry = (value & 1u) != 0;
            uint rotatingIn = carry ? (1u << (width - 1)) : 0u;
            value = (value >> 1) | rotatingIn;
            value &= MaskForWidth(width);
            _registers.Ccr.Carry = carry;
        }
        WriteRegisterByWidth(reg, value, width);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = ((value >> (width - 1)) & 1u) != 0;
        return rotates;
    }

    private uint RotateThroughExtendLeft(DataRegister reg, ShiftCount count, int width)
    {
        uint rotates = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Carry = _registers.Ccr.Extend;
        uint value = reg.Read(_registers);
        for (uint i = 0; i < rotates; i++)
        {
            bool carry = ((value >> (width - 1)) & 1u) != 0;
            bool rotatingIn = _registers.Ccr.Extend;
            value = ((value << 1) | (rotatingIn ? 1u : 0u)) & MaskForWidth(width);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
        }
        WriteRegisterByWidth(reg, value, width);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = ((value >> (width - 1)) & 1u) != 0;
        return rotates;
    }

    private uint RotateThroughExtendRight(DataRegister reg, ShiftCount count, int width)
    {
        uint rotates = (uint)(count.Get(_registers) % 64);
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Carry = _registers.Ccr.Extend;
        uint value = reg.Read(_registers);
        for (uint i = 0; i < rotates; i++)
        {
            bool carry = (value & 1u) != 0;
            bool rotatingIn = _registers.Ccr.Extend;
            uint msb = rotatingIn ? (1u << (width - 1)) : 0u;
            value = (value >> 1) | msb;
            value &= MaskForWidth(width);
            _registers.Ccr.Carry = carry;
            _registers.Ccr.Extend = carry;
        }
        WriteRegisterByWidth(reg, value, width);
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = ((value >> (width - 1)) & 1u) != 0;
        return rotates;
    }

    private static uint MaskForWidth(int width)
    {
        return width switch
        {
            8 => 0xFFu,
            16 => 0xFFFFu,
            _ => 0xFFFF_FFFFu,
        };
    }

    private void WriteRegisterByWidth(DataRegister reg, uint value, int width)
    {
        switch (width)
        {
            case 8:
                reg.WriteByte(_registers, (byte)value);
                break;
            case 16:
                reg.WriteWord(_registers, (ushort)value);
                break;
            default:
                reg.WriteLong(_registers, value);
                break;
        }
    }

    private ExecuteResult<uint> NotByte(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var value = ReadByteResolved(destResolved.Value);
        byte negated = (byte)~value;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = negated == 0;
        _registers.Ccr.Negative = negated.SignBit();

        var write = WriteByteResolvedAsResult(destResolved.Value, negated);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.Byte, dest));
    }

    private ExecuteResult<uint> NotWord(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var value = ReadWordResolved(destResolved.Value);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        ushort negated = (ushort)~value.Value;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = negated == 0;
        _registers.Ccr.Negative = negated.SignBit();

        var write = WriteWordResolved(destResolved.Value, negated);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.Word, dest));
    }

    private ExecuteResult<uint> NotLongWord(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var value = ReadLongResolved(destResolved.Value);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        uint negated = ~value.Value;

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = negated == 0;
        _registers.Ccr.Negative = negated.SignBit();

        var write = WriteLongResolved(destResolved.Value, negated);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.LongWord, dest));
    }

    private ExecuteResult<uint> ClrByte(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        _ = ReadByteResolved(destResolved.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;

        var write = WriteByteResolvedAsResult(destResolved.Value, 0);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.Byte, dest));
    }

    private ExecuteResult<uint> ClrWord(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var _ = ReadWordResolved(destResolved.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;

        var write = WriteWordResolved(destResolved.Value, 0);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.Word, dest));
    }

    private ExecuteResult<uint> ClrLongWord(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.LongWord);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var _ = ReadLongResolved(destResolved.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = true;
        _registers.Ccr.Negative = false;

        var write = WriteLongResolved(destResolved.Value, 0);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(UnaryOpCycles(OpSize.LongWord, dest));
    }

    private uint Ext(OpSize size, DataRegister register)
    {
        bool zero;
        bool sign;
        switch (size)
        {
            case OpSize.Word:
                byte b = (byte)register.Read(_registers);
                ushort signExtended = (ushort)(sbyte)b;
                register.WriteWord(_registers, signExtended);
                zero = signExtended == 0;
                sign = signExtended.SignBit();
                break;
            case OpSize.LongWord:
                ushort w = (ushort)register.Read(_registers);
                uint signExtendedL = (uint)(short)w;
                register.WriteLong(_registers, signExtendedL);
                zero = signExtendedL == 0;
                sign = signExtendedL.SignBit();
                break;
            default:
                throw new InvalidOperationException("EXT does not support size byte");
        }

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = zero;
        _registers.Ccr.Negative = sign;

        return 4;
    }

    private uint Swap(DataRegister register)
    {
        uint value = register.Read(_registers);
        uint swapped = (value << 16) | (value >> 16);
        register.WriteLong(_registers, swapped);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = swapped == 0;
        _registers.Ccr.Negative = swapped.SignBit();

        return 4;
    }

    private ExecuteResult<uint> AsdRegister(OpSize size, ShiftDirection dir, DataRegister register, ShiftCount count)
    {
        uint shifts = size switch
        {
            OpSize.Byte => dir == ShiftDirection.Left ? AslRegisterU8(register, count) : AsrRegisterU8(register, count),
            OpSize.Word => dir == ShiftDirection.Left ? AslRegisterU16(register, count) : AsrRegisterU16(register, count),
            _ => dir == ShiftDirection.Left ? AslRegisterU32(register, count) : AsrRegisterU32(register, count),
        };

        return ExecuteResult<uint>.Ok(ShiftRegisterCycles(size, shifts));
    }

    private ExecuteResult<uint> AsdMemory(ShiftDirection dir, AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var original = ReadWordResolved(destResolved.Value);
        if (!original.IsOk) return ExecuteResult<uint>.Err(original.Error!.Value);

        ushort value;
        bool carry;
        if (dir == ShiftDirection.Left)
        {
            value = (ushort)(original.Value << 1);
            carry = original.Value.Test(15);
        }
        else
        {
            value = (ushort)((original.Value >> 1) | (original.Value & 0x8000));
            carry = original.Value.Test(0);
        }

        bool overflow = original.Value.SignBit() != value.SignBit();

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = overflow;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(ShiftMemoryCycles(dest));
    }

    private ExecuteResult<uint> LsdRegister(OpSize size, ShiftDirection dir, DataRegister register, ShiftCount count)
    {
        uint shifts = size switch
        {
            OpSize.Byte => dir == ShiftDirection.Left ? LslRegisterU8(register, count) : LsrRegisterU8(register, count),
            OpSize.Word => dir == ShiftDirection.Left ? LslRegisterU16(register, count) : LsrRegisterU16(register, count),
            _ => dir == ShiftDirection.Left ? LslRegisterU32(register, count) : LsrRegisterU32(register, count),
        };

        return ExecuteResult<uint>.Ok(ShiftRegisterCycles(size, shifts));
    }

    private ExecuteResult<uint> LsdMemory(ShiftDirection dir, AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var original = ReadWordResolved(destResolved.Value);
        if (!original.IsOk) return ExecuteResult<uint>.Err(original.Error!.Value);

        ushort value;
        bool carry;
        if (dir == ShiftDirection.Left)
        {
            value = (ushort)(original.Value << 1);
            carry = original.Value.Test(15);
        }
        else
        {
            value = (ushort)(original.Value >> 1);
            carry = original.Value.Test(0);
        }

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(ShiftMemoryCycles(dest));
    }

    private ExecuteResult<uint> RodRegister(OpSize size, ShiftDirection dir, DataRegister register, ShiftCount count)
    {
        uint rotates = size switch
        {
            OpSize.Byte => dir == ShiftDirection.Left ? RolRegisterU8(register, count) : RorRegisterU8(register, count),
            OpSize.Word => dir == ShiftDirection.Left ? RolRegisterU16(register, count) : RorRegisterU16(register, count),
            _ => dir == ShiftDirection.Left ? RolRegisterU32(register, count) : RorRegisterU32(register, count),
        };

        return ExecuteResult<uint>.Ok(ShiftRegisterCycles(size, rotates));
    }

    private ExecuteResult<uint> RodMemory(ShiftDirection dir, AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var original = ReadWordResolved(destResolved.Value);
        if (!original.IsOk) return ExecuteResult<uint>.Err(original.Error!.Value);

        ushort value;
        bool carry;
        if (dir == ShiftDirection.Left)
        {
            value = (ushort)((original.Value << 1) | (original.Value.SignBit() ? 1u : 0u));
            carry = original.Value.SignBit();
        }
        else
        {
            value = (ushort)((original.Value >> 1) | (original.Value.Test(0) ? 0x8000u : 0u));
            carry = original.Value.Test(0);
        }

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(ShiftMemoryCycles(dest));
    }

    private ExecuteResult<uint> RoxdRegister(OpSize size, ShiftDirection dir, DataRegister register, ShiftCount count)
    {
        uint rotates = size switch
        {
            OpSize.Byte => dir == ShiftDirection.Left ? RoxlRegisterU8(register, count) : RoxrRegisterU8(register, count),
            OpSize.Word => dir == ShiftDirection.Left ? RoxlRegisterU16(register, count) : RoxrRegisterU16(register, count),
            _ => dir == ShiftDirection.Left ? RoxlRegisterU32(register, count) : RoxrRegisterU32(register, count),
        };

        return ExecuteResult<uint>.Ok(ShiftRegisterCycles(size, rotates));
    }

    private ExecuteResult<uint> RoxdMemory(ShiftDirection dir, AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var original = ReadWordResolved(destResolved.Value);
        if (!original.IsOk) return ExecuteResult<uint>.Err(original.Error!.Value);

        bool extend = _registers.Ccr.Extend;
        ushort value;
        bool carry;
        if (dir == ShiftDirection.Left)
        {
            value = (ushort)((original.Value << 1) | (extend ? 1u : 0u));
            carry = original.Value.Test(15);
        }
        else
        {
            value = (ushort)((original.Value >> 1) | (extend ? 0x8000u : 0u));
            carry = original.Value.Test(0);
        }

        _registers.Ccr.Carry = carry;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();
        _registers.Ccr.Extend = carry;

        var write = WriteWordResolved(destResolved.Value, value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        return ExecuteResult<uint>.Ok(ShiftMemoryCycles(dest));
    }

    private ExecuteResult<uint> TstByte(AddressingMode source)
    {
        var value = ReadByte(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value.Value == 0;
        _registers.Ccr.Negative = value.Value.SignBit();

        return ExecuteResult<uint>.Ok(4 + source.AddressCalculationCycles(OpSize.Byte));
    }

    private ExecuteResult<uint> TstWord(AddressingMode source)
    {
        var value = ReadWord(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value.Value == 0;
        _registers.Ccr.Negative = value.Value.SignBit();

        return ExecuteResult<uint>.Ok(4 + source.AddressCalculationCycles(OpSize.Word));
    }

    private ExecuteResult<uint> TstLongWord(AddressingMode source)
    {
        var value = ReadLongWord(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value.Value == 0;
        _registers.Ccr.Negative = value.Value.SignBit();

        return ExecuteResult<uint>.Ok(4 + source.AddressCalculationCycles(OpSize.LongWord));
    }

    private ExecuteResult<uint> Tas(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Byte);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        byte value = ReadByteResolved(destResolved.Value);

        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = value == 0;
        _registers.Ccr.Negative = value.SignBit();

        if (dest.IsDataDirect || _allowTasWrites)
        {
            var write = WriteByteResolved(destResolved.Value, (byte)(value | 0x80));
            if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);
        }

        return ExecuteResult<uint>.Ok(dest.IsDataDirect ? 4u : 10u + dest.AddressCalculationCycles(OpSize.Byte));
    }
}
