using System;
using System.Collections.Generic;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal sealed partial class InstructionExecutor
{
    private ExecuteResult<object> WriteLongWordForMove(AddressingMode dest, uint value)
    {
        if (dest.Kind == AddressingModeKind.AddressIndirectPredecrement)
        {
            ushort highWord = (ushort)(value >> 16);
            ushort lowWord = (ushort)value;

            uint address = dest.AddrReg.Read(_registers) - 2;
            dest.AddrReg.WriteLong(_registers, address);
            var r0 = WriteBusWord(address, lowWord);
            if (!r0.IsOk) return r0;

            address -= 2;
            dest.AddrReg.WriteLong(_registers, address);
            return WriteBusWord(address, highWord);
        }

        return WriteLongWord(dest, value);
    }

    private ExecuteResult<uint> MoveByte(AddressingMode source, AddressingMode dest)
    {
        var value = ReadByte(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = false;
            _registers.Ccr.Overflow = false;
            _registers.Ccr.Zero = value.Value == 0;
            _registers.Ccr.Negative = value.Value.SignBit();
        }

        var write = WriteByte(dest, value.Value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint baseCycles = dest.Kind == AddressingModeKind.AddressIndirectPredecrement ? 2u : 4u;
        return ExecuteResult<uint>.Ok(baseCycles + source.AddressCalculationCycles(OpSize.Byte) + dest.AddressCalculationCycles(OpSize.Byte));
    }

    private ExecuteResult<uint> MoveWord(AddressingMode source, AddressingMode dest)
    {
        var value = ReadWord(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = false;
            _registers.Ccr.Overflow = false;
            _registers.Ccr.Zero = value.Value == 0;
            _registers.Ccr.Negative = value.Value.SignBit();
        }

        var write = WriteWord(dest, value.Value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint baseCycles = dest.Kind == AddressingModeKind.AddressIndirectPredecrement ? 2u : 4u;
        return ExecuteResult<uint>.Ok(baseCycles + source.AddressCalculationCycles(OpSize.Word) + dest.AddressCalculationCycles(OpSize.Word));
    }

    private ExecuteResult<uint> MoveLongWord(AddressingMode source, AddressingMode dest)
    {
        var value = ReadLongWord(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

        if (!dest.IsAddressDirect)
        {
            _registers.Ccr.Carry = false;
            _registers.Ccr.Overflow = false;
            _registers.Ccr.Zero = value.Value == 0;
            _registers.Ccr.Negative = value.Value.SignBit();
        }

        var write = WriteLongWordForMove(dest, value.Value);
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        uint baseCycles = dest.Kind == AddressingModeKind.AddressIndirectPredecrement ? 2u : 4u;
        return ExecuteResult<uint>.Ok(baseCycles + source.AddressCalculationCycles(OpSize.LongWord) + dest.AddressCalculationCycles(OpSize.LongWord));
    }

    private ExecuteResult<uint> MoveFromSr(AddressingMode dest)
    {
        var destResolved = ResolveAddressWithPost(dest, OpSize.Word);
        if (!destResolved.IsOk) return ExecuteResult<uint>.Err(destResolved.Error!.Value);
        var write = WriteWordResolved(destResolved.Value, _registers.StatusRegister());
        if (!write.IsOk) return ExecuteResult<uint>.Err(write.Error!.Value);

        if (dest.IsDataDirect)
            return ExecuteResult<uint>.Ok(6);
        return ExecuteResult<uint>.Ok(8 + dest.AddressCalculationCycles(OpSize.Word));
    }

    private ExecuteResult<uint> MoveToCcr(AddressingMode source)
    {
        var value = ReadWord(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        _registers.Ccr = ConditionCodes.FromByte((byte)value.Value);
        return ExecuteResult<uint>.Ok(12 + source.AddressCalculationCycles(OpSize.Word));
    }

    private ExecuteResult<uint> MoveToSr(AddressingMode source)
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());
        var value = ReadWord(source);
        if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
        _registers.SetStatusRegister(value.Value);
        return ExecuteResult<uint>.Ok(12 + source.AddressCalculationCycles(OpSize.Word));
    }

    private uint Moveq(sbyte data, DataRegister register)
    {
        register.WriteLong(_registers, unchecked((uint)data));
        _registers.Ccr.Carry = false;
        _registers.Ccr.Overflow = false;
        _registers.Ccr.Zero = data == 0;
        _registers.Ccr.Negative = data < 0;
        return 4;
    }

    private ExecuteResult<uint> MoveUsp(UspDirection direction, AddressRegister register)
    {
        if (!_registers.SupervisorMode)
            return ExecuteResult<uint>.Err(M68kException.PrivilegeViolation());

        if (direction == UspDirection.RegisterToUsp)
        {
            _registers.Usp = register.Read(_registers);
        }
        else
        {
            register.WriteLong(_registers, _registers.Usp);
        }

        return ExecuteResult<uint>.Ok(4);
    }

    private ExecuteResult<uint> Movem(OpSize size, AddressingMode addressingMode, Direction direction)
    {
        var extension = FetchOperand();
        if (!extension.IsOk) return ExecuteResult<uint>.Err(extension.Error!.Value);
        ushort mask = extension.Value;

        if (addressingMode.Kind == AddressingModeKind.AddressIndirectPredecrement)
            return MovemPredecrement(size, addressingMode.AddrReg, mask);

        var resolved = ResolveAddress(addressingMode, size);
        if (!resolved.IsOk) return ExecuteResult<uint>.Err(resolved.Error!.Value);

        uint address;
        AddressRegister? postincRegister = null;
        if (resolved.Value.Kind == ResolvedAddressKind.Memory)
        {
            address = resolved.Value.Address;
        }
        else if (resolved.Value.Kind == ResolvedAddressKind.MemoryPostincrement)
        {
            address = resolved.Value.Address;
            postincRegister = resolved.Value.AddrReg;
        }
        else
        {
            throw new InvalidOperationException("MOVEM only supports addressing modes that resolve to a memory address");
        }

        int count = 0;
        if (direction == Direction.RegisterToMemory)
        {
            foreach (var reg in EnumerateMovemRegisters(mask, reverse: false))
            {
                uint value = reg.Read(_registers);
                if (size == OpSize.Word)
                {
                    var r0 = WriteBusWord(address, (ushort)value);
                    if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
                    address = unchecked(address + 2);
                }
                else if (size == OpSize.LongWord)
                {
                    var r0 = WriteBusLong(address, value);
                    if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
                    address = unchecked(address + 4);
                }
                else
                {
                    throw new InvalidOperationException("MOVEM does not support size byte");
                }

                count++;
            }
        }
        else
        {
            foreach (var reg in EnumerateMovemRegisters(mask, reverse: false))
            {
                if (size == OpSize.Word)
                {
                    if (postincRegister.HasValue)
                        postincRegister.Value.WriteLong(_registers, unchecked(address + 2));

                    var value = ReadBusWord(address);
                    if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);
                    uint signExtended = (uint)(short)value.Value;

                    if (!(reg.IsAddress && postincRegister.HasValue && reg.Address.Index == postincRegister.Value.Index))
                        reg.Write(_registers, signExtended);

                    address = unchecked(address + 2);
                }
                else if (size == OpSize.LongWord)
                {
                    if (postincRegister.HasValue)
                        postincRegister.Value.WriteLong(_registers, unchecked(address + 2));

                    var value = ReadBusLong(address);
                    if (!value.IsOk) return ExecuteResult<uint>.Err(value.Error!.Value);

                    if (!(reg.IsAddress && postincRegister.HasValue && reg.Address.Index == postincRegister.Value.Index))
                        reg.Write(_registers, value.Value);

                    address = unchecked(address + 4);
                }
                else
                {
                    throw new InvalidOperationException("MOVEM does not support size byte");
                }

                count++;
            }

            if (postincRegister.HasValue)
                postincRegister.Value.WriteLong(_registers, address);
        }

        uint countCycles = size == OpSize.Word ? (uint)(4 * count) : (uint)(8 * count);
        if (direction == Direction.MemoryToRegister)
            return ExecuteResult<uint>.Ok(8 + addressingMode.AddressCalculationCycles(OpSize.Word) + countCycles);
        return ExecuteResult<uint>.Ok(4 + addressingMode.AddressCalculationCycles(OpSize.Word) + countCycles);
    }

    private ExecuteResult<uint> MovemPredecrement(OpSize size, AddressRegister predecRegister, ushort mask)
    {
        uint address = predecRegister.Read(_registers);
        int count = 0;

        foreach (var reg in EnumerateMovemRegisters(mask, reverse: true))
        {
            if (size == OpSize.Word)
            {
                address = unchecked(address - 2);
                ushort value = (ushort)reg.Read(_registers);
                var r0 = WriteBusWord(address, value);
                if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);
            }
            else if (size == OpSize.LongWord)
            {
                uint value = reg.Read(_registers);
                ushort highWord = (ushort)(value >> 16);
                ushort lowWord = (ushort)value;

                address = unchecked(address - 2);
                var r0 = WriteBusWord(address, lowWord);
                if (!r0.IsOk) return ExecuteResult<uint>.Err(r0.Error!.Value);

                address = unchecked(address - 2);
                var r1 = WriteBusWord(address, highWord);
                if (!r1.IsOk) return ExecuteResult<uint>.Err(r1.Error!.Value);
            }
            else
            {
                throw new InvalidOperationException("MOVEM does not support size byte");
            }

            count++;
        }

        predecRegister.WriteLong(_registers, address);

        uint countCycles = size == OpSize.Word ? (uint)(4 * count) : (uint)(8 * count);
        return ExecuteResult<uint>.Ok(8 + countCycles);
    }

    private ExecuteResult<uint> Movep(OpSize size, DataRegister dRegister, AddressRegister aRegister, Direction direction)
    {
        var extension = FetchOperand();
        if (!extension.IsOk) return ExecuteResult<uint>.Err(extension.Error!.Value);
        short displacement = (short)extension.Value;
        uint address = unchecked(aRegister.Read(_registers) + (uint)displacement);

        if (size == OpSize.Word && direction == Direction.RegisterToMemory)
        {
            ushort value = (ushort)dRegister.Read(_registers);
            byte msb = (byte)(value >> 8);
            byte lsb = (byte)value;
            _bus.WriteByte(address, msb);
            _bus.WriteByte(unchecked(address + 2), lsb);
        }
        else if (size == OpSize.Word && direction == Direction.MemoryToRegister)
        {
            byte msb = _bus.ReadByte(address);
            byte lsb = _bus.ReadByte(unchecked(address + 2));
            ushort value = (ushort)((msb << 8) | lsb);
            dRegister.WriteWord(_registers, value);
        }
        else if (size == OpSize.LongWord && direction == Direction.RegisterToMemory)
        {
            uint value = dRegister.Read(_registers);
            _bus.WriteByte(address, (byte)(value >> 24));
            _bus.WriteByte(unchecked(address + 2), (byte)(value >> 16));
            _bus.WriteByte(unchecked(address + 4), (byte)(value >> 8));
            _bus.WriteByte(unchecked(address + 6), (byte)value);
        }
        else if (size == OpSize.LongWord && direction == Direction.MemoryToRegister)
        {
            byte b3 = _bus.ReadByte(address);
            byte b2 = _bus.ReadByte(unchecked(address + 2));
            byte b1 = _bus.ReadByte(unchecked(address + 4));
            byte b0 = _bus.ReadByte(unchecked(address + 6));
            uint value = ((uint)b3 << 24) | ((uint)b2 << 16) | ((uint)b1 << 8) | b0;
            dRegister.WriteLong(_registers, value);
        }
        else
        {
            throw new InvalidOperationException("MOVEP does not support size byte");
        }

        return ExecuteResult<uint>.Ok(size == OpSize.Word ? 16u : 24u);
    }

    private uint ExgData(DataRegister x, DataRegister y)
    {
        uint xVal = x.Read(_registers);
        uint yVal = y.Read(_registers);
        x.WriteLong(_registers, yVal);
        y.WriteLong(_registers, xVal);
        return 6;
    }

    private uint ExgAddress(AddressRegister x, AddressRegister y)
    {
        uint xVal = x.Read(_registers);
        uint yVal = y.Read(_registers);
        x.WriteLong(_registers, yVal);
        y.WriteLong(_registers, xVal);
        return 6;
    }

    private uint ExgDataAddress(DataRegister x, AddressRegister y)
    {
        uint xVal = x.Read(_registers);
        uint yVal = y.Read(_registers);
        x.WriteLong(_registers, yVal);
        y.WriteLong(_registers, xVal);
        return 6;
    }

    private IEnumerable<MovemRegister> EnumerateMovemRegisters(ushort mask, bool reverse)
    {
        for (int i = 0; i < 16; i++)
        {
            bool bit = ((mask >> i) & 1) != 0;
            if (!bit) continue;
            int index = reverse ? 15 - i : i;
            yield return MovemRegister.FromIndex(index);
        }
    }

    private readonly struct MovemRegister
    {
        public readonly bool IsAddress;
        public readonly DataRegister Data;
        public readonly AddressRegister Address;

        private MovemRegister(bool isAddress, DataRegister data, AddressRegister address)
        {
            IsAddress = isAddress;
            Data = data;
            Address = address;
        }

        public static MovemRegister FromIndex(int i)
        {
            if (i < 8)
                return new MovemRegister(false, new DataRegister((byte)i), default);
            return new MovemRegister(true, default, new AddressRegister((byte)(i - 8)));
        }

        public uint Read(Registers regs) => IsAddress ? Address.Read(regs) : Data.Read(regs);

        public void Write(Registers regs, uint value)
        {
            if (IsAddress)
                Address.WriteLong(regs, value);
            else
                Data.WriteLong(regs, value);
        }
    }
}
