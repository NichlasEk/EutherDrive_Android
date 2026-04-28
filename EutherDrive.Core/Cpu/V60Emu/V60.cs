using System;
using System.Globalization;

namespace EutherDrive.Core.Cpu.V60Emu;

// NEC V60 behavior is translated from MAME's BSD-3-Clause V60 core by
// Farfetch'd and R. Belmont.
public sealed class V60
{
    private const int SpIndex = 31;
    private const int PcIndex = 32;
    private const int PswIndex = 33;
    private const int SbrIndex = 41;
    private const int SycwIndex = 43;
    private const int TkcwIndex = 44;
    private const int Psw2Index = 51;
    private const uint StartPc = 0xfffffff0;

    private readonly uint[] _reg = new uint[68];
    private IV60Bus? _bus;
    private byte _cy;
    private byte _ov;
    private byte _s;
    private byte _z;

    public uint Pc => _reg[PcIndex];
    public uint PreviousPc { get; private set; }
    public ushort LastOpcode { get; private set; }
    public bool Halted { get; private set; }
    public string LastStopReason { get; private set; } = string.Empty;

    public uint DebugRegister(int index)
    {
        return (uint)index < _reg.Length ? _reg[index] : 0;
    }

    public void Reset(IV60Bus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Array.Clear(_reg);

        _reg[PswIndex] = 0x10000000;
        _reg[PcIndex] = StartPc;
        _reg[SbrIndex] = 0x00000000;
        _reg[SycwIndex] = 0x00000070;
        _reg[TkcwIndex] = 0x0000e000;
        _reg[Psw2Index] = 0x0000f002;
        _cy = 0;
        _ov = 0;
        _s = 0;
        _z = 0;
        PreviousPc = Pc;
        LastOpcode = 0;
        Halted = false;
        LastStopReason = string.Empty;
    }

    public int ExecuteInstruction()
    {
        IV60Bus bus = _bus ?? throw new InvalidOperationException("V60 has not been reset with a bus.");
        if (Halted)
            return 0;

        PreviousPc = Pc;
        byte opcode = bus.Read8(Pc);
        LastOpcode = bus.Read16(Pc);
        uint increment = ExecuteOpcode(bus, opcode);
        _reg[PcIndex] += increment;
        return 8;
    }

    public bool TryInterrupt(int vector)
    {
        IV60Bus bus = _bus ?? throw new InvalidOperationException("V60 has not been reset with a bus.");
        if (Halted || (ReadPsw() & (1u << 18)) == 0)
            return false;

        uint oldPsw = UpdatePswForException(isInterrupt: true, targetLevel: 0);
        _reg[SpIndex] -= 4;
        bus.Write32(_reg[SpIndex], oldPsw);
        _reg[SpIndex] -= 4;
        bus.Write32(_reg[SpIndex], Pc);
        _reg[PcIndex] = bus.Read32((_reg[SbrIndex] & 0xfffff000) + (uint)vector * 4);
        return true;
    }

    private uint ExecuteOpcode(IV60Bus bus, byte opcode)
    {
        switch (opcode)
        {
            case 0x00:
                return 1; // HALT waits for an interrupt on hardware; MAME's V60 core skips it.
            case 0x05:
                Halted = true;
                LastStopReason = "BRK";
                return 1;
            case 0x10:
                return 1; // CLRTLBA; TLB is not modeled yet, same skip behavior as MAME.
            case 0x12:
                return ExecuteLdpr(bus);
            case 0x13:
                return ExecuteUpdatePswWord(bus);
            case 0x09:
                return ExecuteMoveValue(bus, dimension: 0);
            case 0x0a:
                return ExecuteMoveSignExtend(bus, sourceDimension: 0, destDimension: 1);
            case 0x0b:
                return ExecuteMoveZeroExtend(bus, sourceDimension: 0, destDimension: 1);
            case 0x0c:
                return ExecuteMoveSignExtend(bus, sourceDimension: 0, destDimension: 2);
            case 0x0d:
                return ExecuteMoveZeroExtend(bus, sourceDimension: 0, destDimension: 2);
            case 0x1b:
                return ExecuteMoveValue(bus, dimension: 1);
            case 0x1c:
                return ExecuteMoveSignExtend(bus, sourceDimension: 1, destDimension: 2);
            case 0x1d:
                return ExecuteMoveZeroExtend(bus, sourceDimension: 1, destDimension: 2);
            case 0x19:
                return ExecuteMoveTruncate(bus, sourceDimension: 1, destDimension: 0);
            case 0x2c:
                return ExecuteReverseBytes(bus);
            case 0x29:
                return ExecuteMoveTruncate(bus, sourceDimension: 2, destDimension: 0);
            case 0x2b:
                return ExecuteMoveTruncate(bus, sourceDimension: 2, destDimension: 1);
            case 0x2d:
                return ExecuteMoveValue(bus, dimension: 2);
            case 0x38:
                return ExecuteNot(bus, dimension: 0);
            case 0x39:
                return ExecuteNegate(bus, dimension: 0);
            case 0x3a:
                return ExecuteNot(bus, dimension: 1);
            case 0x3b:
                return ExecuteNegate(bus, dimension: 1);
            case 0x3c:
                return ExecuteNot(bus, dimension: 2);
            case 0x3d:
                return ExecuteNegate(bus, dimension: 2);
            case 0x40:
                return ExecuteMoveAddress(bus, dimension: 0);
            case 0x41:
                return ExecuteExchange(bus, dimension: 0);
            case 0x42:
                return ExecuteMoveAddress(bus, dimension: 1);
            case 0x43:
                return ExecuteExchange(bus, dimension: 1);
            case 0x44:
                return ExecuteMoveAddress(bus, dimension: 2);
            case 0x45:
                return ExecuteExchange(bus, dimension: 2);
            case 0x47:
                return ExecuteSetFlag(bus);
            case 0x48:
                return ExecuteBranchSubroutine(bus);
            case 0x58:
                return ExecuteExtended58(bus);
            case 0x5a:
                return ExecuteExtended5A(bus);
            case 0x80:
                return ExecuteAdd(bus, dimension: 0);
            case 0x81:
                return ExecuteMultiply(bus, dimension: 0, unsigned: false);
            case 0x82:
                return ExecuteAdd(bus, dimension: 1);
            case 0x83:
                return ExecuteMultiply(bus, dimension: 1, unsigned: false);
            case 0x84:
                return ExecuteAdd(bus, dimension: 2);
            case 0x85:
                return ExecuteMultiply(bus, dimension: 2, unsigned: false);
            case 0x87:
                return ExecuteBitOperation(bus, BitOperation.Test);
            case 0x88:
                return ExecuteLogicalOperation(bus, dimension: 0, LogicalOperation.Or);
            case 0x89:
                return ExecuteRotate(bus, dimension: 0);
            case 0x99:
                return ExecuteRotateCarry(bus, dimension: 0);
            case 0x91:
                return ExecuteMultiply(bus, dimension: 0, unsigned: true);
            case 0x8a:
                return ExecuteLogicalOperation(bus, dimension: 1, LogicalOperation.Or);
            case 0x8b:
                return ExecuteRotate(bus, dimension: 1);
            case 0x9b:
                return ExecuteRotateCarry(bus, dimension: 1);
            case 0x93:
                return ExecuteMultiply(bus, dimension: 1, unsigned: true);
            case 0x8c:
                return ExecuteLogicalOperation(bus, dimension: 2, LogicalOperation.Or);
            case 0x8d:
                return ExecuteRotate(bus, dimension: 2);
            case 0x9d:
                return ExecuteRotateCarry(bus, dimension: 2);
            case 0x95:
                return ExecuteMultiply(bus, dimension: 2, unsigned: true);
            case 0x97:
                return ExecuteBitOperation(bus, BitOperation.Set);
            case 0xa0:
                return ExecuteLogicalOperation(bus, dimension: 0, LogicalOperation.And);
            case 0xa1:
                return ExecuteDivide(bus, dimension: 0, unsigned: false);
            case 0xa2:
                return ExecuteLogicalOperation(bus, dimension: 1, LogicalOperation.And);
            case 0xa3:
                return ExecuteDivide(bus, dimension: 1, unsigned: false);
            case 0xa4:
                return ExecuteLogicalOperation(bus, dimension: 2, LogicalOperation.And);
            case 0xa5:
                return ExecuteDivide(bus, dimension: 2, unsigned: false);
            case 0xa7:
                return ExecuteBitOperation(bus, BitOperation.Clear);
            case 0xa9:
                return ExecuteShiftLogical(bus, dimension: 0);
            case 0xab:
                return ExecuteShiftLogical(bus, dimension: 1);
            case 0xad:
                return ExecuteShiftLogical(bus, dimension: 2);
            case 0xa8:
                return ExecuteSubtract(bus, dimension: 0);
            case 0xaa:
                return ExecuteSubtract(bus, dimension: 1);
            case 0xac:
                return ExecuteSubtract(bus, dimension: 2);
            case 0xb0:
                return ExecuteLogicalOperation(bus, dimension: 0, LogicalOperation.Xor);
            case 0xb1:
                return ExecuteDivide(bus, dimension: 0, unsigned: true);
            case 0xb2:
                return ExecuteLogicalOperation(bus, dimension: 1, LogicalOperation.Xor);
            case 0xb3:
                return ExecuteDivide(bus, dimension: 1, unsigned: true);
            case 0xb4:
                return ExecuteLogicalOperation(bus, dimension: 2, LogicalOperation.Xor);
            case 0xb5:
                return ExecuteDivide(bus, dimension: 2, unsigned: true);
            case 0xb6:
                return ExecuteDivideExtendedUnsigned(bus);
            case 0xb7:
                return ExecuteBitOperation(bus, BitOperation.Not);
            case 0xb8:
                return ExecuteCompare(bus, dimension: 0);
            case 0xba:
                return ExecuteCompare(bus, dimension: 1);
            case 0xbc:
                return ExecuteCompare(bus, dimension: 2);
            case 0xca:
                return ExecuteReturnSubroutine(bus);
            case 0xcd:
                return 1; // NOP
            case 0xe4:
                return ExecutePopMultiple(bus, modm: 0);
            case 0xe5:
                return ExecutePopMultiple(bus, modm: 1);
            case 0xd0:
                return ExecuteIncrementDecrement(bus, dimension: 0, modm: 0, increment: false);
            case 0xd1:
                return ExecuteIncrementDecrement(bus, dimension: 0, modm: 1, increment: false);
            case 0xd2:
                return ExecuteIncrementDecrement(bus, dimension: 1, modm: 0, increment: false);
            case 0xd3:
                return ExecuteIncrementDecrement(bus, dimension: 1, modm: 1, increment: false);
            case 0xd4:
                return ExecuteIncrementDecrement(bus, dimension: 2, modm: 0, increment: false);
            case 0xd5:
                return ExecuteIncrementDecrement(bus, dimension: 2, modm: 1, increment: false);
            case >= 0x60 and <= 0x7f:
                return ExecuteBranch(bus, opcode);
            case 0xc6:
                return ExecuteDecrementBranch(bus, table: 0);
            case 0xc7:
                return ExecuteDecrementBranch(bus, table: 1);
            case 0xd6:
                return ExecuteJump(bus, modm: 0);
            case 0xd7:
                return ExecuteJump(bus, modm: 1);
            case 0xd8:
                return ExecuteIncrementDecrement(bus, dimension: 0, modm: 0, increment: true);
            case 0xd9:
                return ExecuteIncrementDecrement(bus, dimension: 0, modm: 1, increment: true);
            case 0xda:
                return ExecuteIncrementDecrement(bus, dimension: 1, modm: 0, increment: true);
            case 0xdb:
                return ExecuteIncrementDecrement(bus, dimension: 1, modm: 1, increment: true);
            case 0xdc:
                return ExecuteIncrementDecrement(bus, dimension: 2, modm: 0, increment: true);
            case 0xdd:
                return ExecuteIncrementDecrement(bus, dimension: 2, modm: 1, increment: true);
            case 0xe8:
                return ExecuteJumpSubroutine(bus, modm: 0);
            case 0xe9:
                return ExecuteJumpSubroutine(bus, modm: 1);
            case 0xec:
                return ExecutePushMultiple(bus, modm: 0);
            case 0xed:
                return ExecutePushMultiple(bus, modm: 1);
            case 0xee:
                return ExecutePush(bus, modm: 0);
            case 0xef:
                return ExecutePush(bus, modm: 1);
            case 0xf0:
                return ExecuteTestSingle(bus, dimension: 0, modm: 0);
            case 0xf1:
                return ExecuteTestSingle(bus, dimension: 0, modm: 1);
            case 0xf2:
                return ExecuteTestSingle(bus, dimension: 1, modm: 0);
            case 0xf3:
                return ExecuteTestSingle(bus, dimension: 1, modm: 1);
            case 0xf4:
                return ExecuteTestSingle(bus, dimension: 2, modm: 0);
            case 0xf5:
                return ExecuteTestSingle(bus, dimension: 2, modm: 1);
            case 0xfa:
                return ExecuteReturnFromSystem(bus, modm: 0);
            case 0xfb:
                return ExecuteReturnFromSystem(bus, modm: 1);
            default:
                Halted = true;
                LastStopReason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"unimplemented opcode 0x{opcode:X2} word=0x{LastOpcode:X4} pc=0x{PreviousPc:X8}");
                return 0;
        }
    }

    private uint ExecuteBranch(IV60Bus bus, byte opcode)
    {
        bool wide = (opcode & 0x10) != 0;
        bool taken = BranchCondition(opcode);
        if (taken)
        {
            int displacement = wide
                ? (short)bus.Read16(Pc + 1)
                : (sbyte)bus.Read8(Pc + 1);
            _reg[PcIndex] = unchecked(Pc + (uint)displacement);
            return 0;
        }

        return wide ? 3u : 2u;
    }

    private uint ExecuteBranchSubroutine(IV60Bus bus)
    {
        _reg[SpIndex] -= 4;
        bus.Write32(_reg[SpIndex], Pc + 3);
        _reg[PcIndex] = unchecked(Pc + (uint)(short)bus.Read16(Pc + 1));
        return 0;
    }

    private uint ExecuteIncrementDecrement(IV60Bus bus, int dimension, int modm, bool increment)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeReference(bus, modm, mod, Pc + 1, dimension, out OperandReference operand, out uint length, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        uint oldValue = ReadOperandReference(bus, operand, dimension) & mask;
        uint result;
        if (increment)
        {
            ulong sum = (ulong)oldValue + 1;
            result = (uint)sum & mask;
            _cy = (byte)(sum > mask ? 1 : 0);
            _ov = (byte)((((~(oldValue ^ 1u)) & (oldValue ^ result) & sign) != 0) ? 1 : 0);
        }
        else
        {
            result = (oldValue - 1) & mask;
            _cy = (byte)(oldValue == 0 ? 1 : 0);
            _ov = (byte)((((oldValue ^ 1u) & (oldValue ^ result) & sign) != 0) ? 1 : 0);
        }

        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        WriteOperandReference(bus, operand, result, dimension);
        return length + 1;
    }

    private uint ExecuteExtended58(IV60Bus bus)
    {
        byte subop = bus.Read8(Pc + 1);
        return (subop & 0x1f) switch
        {
            0x08 => ExecuteMoveStringUp(bus, subop, dimension: 0),
            0x09 => ExecuteMoveStringDown(bus, subop, dimension: 0),
            0x18 => ExecuteSearchString(bus, subop, dimension: 0, down: false, searchForEqual: true),
            0x19 => ExecuteSearchString(bus, subop, dimension: 0, down: true, searchForEqual: true),
            0x1a => ExecuteSearchString(bus, subop, dimension: 0, down: false, searchForEqual: false),
            0x1b => ExecuteSearchString(bus, subop, dimension: 0, down: true, searchForEqual: false),
            _ => StopUnimplementedExtended("58", subop)
        };
    }

    private uint ExecuteExtended5A(IV60Bus bus)
    {
        byte subop = bus.Read8(Pc + 1);
        return (subop & 0x1f) switch
        {
            0x08 => ExecuteMoveStringUp(bus, subop, dimension: 1),
            0x09 => ExecuteMoveStringDown(bus, subop, dimension: 1),
            0x18 => ExecuteSearchString(bus, subop, dimension: 1, down: false, searchForEqual: true),
            0x19 => ExecuteSearchString(bus, subop, dimension: 1, down: true, searchForEqual: true),
            0x1a => ExecuteSearchString(bus, subop, dimension: 1, down: false, searchForEqual: false),
            0x1b => ExecuteSearchString(bus, subop, dimension: 1, down: true, searchForEqual: false),
            _ => StopUnimplementedExtended("5A", subop)
        };
    }

    private uint ExecuteMoveStringUp(IV60Bus bus, byte subop, int dimension)
    {
        uint sourceModAddress = Pc + 2;
        byte sourceMod = bus.Read8(sourceModAddress);
        if (!TryReadAddressModeAddress(bus, (subop & 0x40) != 0 ? 1 : 0, sourceMod, sourceModAddress, dimension, out uint sourceAddress, out uint sourceLength, out string sourceError))
        {
            Halted = true;
            LastStopReason = sourceError;
            return 0;
        }

        byte sourceLengthToken = bus.Read8(Pc + 2 + sourceLength);
        uint sourceCount = (sourceLengthToken & 0x80) != 0
            ? _reg[sourceLengthToken & 0x1f]
            : sourceLengthToken;

        uint destModAddress = Pc + 3 + sourceLength;
        byte destMod = bus.Read8(destModAddress);
        if (!TryReadAddressModeAddress(bus, (subop & 0x20) != 0 ? 1 : 0, destMod, destModAddress, dimension, out uint destAddress, out uint destLength, out string destError))
        {
            Halted = true;
            LastStopReason = destError;
            return 0;
        }

        byte destLengthToken = bus.Read8(Pc + 3 + sourceLength + destLength);
        uint destCount = (destLengthToken & 0x80) != 0
            ? _reg[destLengthToken & 0x1f]
            : destLengthToken;

        uint count = Math.Min(sourceCount, destCount);
        uint stride = (uint)SizeForDimension(dimension);
        uint i;
        for (i = 0; i < count; i++)
            WriteMemoryByDimension(bus, destAddress + i * stride, ReadMemoryByDimension(bus, sourceAddress + i * stride, dimension), dimension);

        _reg[28] = sourceAddress + i * stride;
        _reg[27] = destAddress + i * stride;
        return sourceLength + destLength + 4;
    }

    private uint ExecuteMoveStringDown(IV60Bus bus, byte subop, int dimension)
    {
        uint sourceModAddress = Pc + 2;
        byte sourceMod = bus.Read8(sourceModAddress);
        if (!TryReadAddressModeAddress(bus, (subop & 0x40) != 0 ? 1 : 0, sourceMod, sourceModAddress, dimension, out uint sourceAddress, out uint sourceLength, out string sourceError))
        {
            Halted = true;
            LastStopReason = sourceError;
            return 0;
        }

        byte sourceLengthToken = bus.Read8(Pc + 2 + sourceLength);
        uint sourceCount = (sourceLengthToken & 0x80) != 0
            ? _reg[sourceLengthToken & 0x1f]
            : sourceLengthToken;

        uint destModAddress = Pc + 3 + sourceLength;
        byte destMod = bus.Read8(destModAddress);
        if (!TryReadAddressModeAddress(bus, (subop & 0x20) != 0 ? 1 : 0, destMod, destModAddress, dimension, out uint destAddress, out uint destLength, out string destError))
        {
            Halted = true;
            LastStopReason = destError;
            return 0;
        }

        byte destLengthToken = bus.Read8(Pc + 3 + sourceLength + destLength);
        uint destCount = (destLengthToken & 0x80) != 0
            ? _reg[destLengthToken & 0x1f]
            : destLengthToken;

        uint count = Math.Min(sourceCount, destCount);
        uint stride = (uint)SizeForDimension(dimension);
        uint i;
        for (i = 0; i < count; i++)
        {
            uint index = count - i - 1;
            WriteMemoryByDimension(bus, destAddress + index * stride, ReadMemoryByDimension(bus, sourceAddress + index * stride, dimension), dimension);
        }

        _reg[28] = unchecked(sourceAddress + (sourceCount - i - 1) * stride);
        _reg[27] = unchecked(destAddress + (destCount - i - 1) * stride);
        return sourceLength + destLength + 4;
    }

    private uint ExecuteSearchString(IV60Bus bus, byte subop, int dimension, bool down, bool searchForEqual)
    {
        uint sourceModAddress = Pc + 2;
        byte sourceMod = bus.Read8(sourceModAddress);
        if (!TryReadAddressModeAddress(bus, (subop & 0x40) != 0 ? 1 : 0, sourceMod, sourceModAddress, dimension, out uint sourceAddress, out uint sourceLength, out string sourceError))
        {
            Halted = true;
            LastStopReason = sourceError;
            return 0;
        }

        byte lengthToken = bus.Read8(Pc + 2 + sourceLength);
        uint count = (lengthToken & 0x80) != 0
            ? _reg[lengthToken & 0x1f]
            : lengthToken;

        uint valueModAddress = Pc + 3 + sourceLength;
        byte valueMod = bus.Read8(valueModAddress);
        if (!TryReadAddressModeValue(bus, (subop & 0x20) != 0 ? 1 : 0, valueMod, valueModAddress, dimension, out uint target, out uint valueLength, out string valueError))
        {
            Halted = true;
            LastStopReason = valueError;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint stride = (uint)SizeForDimension(dimension);
        target &= mask;

        bool found = false;
        int index = down ? (int)count - 1 : 0;
        if (down)
        {
            for (; index >= 0; index--)
            {
                bool equal = (ReadMemoryByDimension(bus, sourceAddress + (uint)index * stride, dimension) & mask) == target;
                if (equal == searchForEqual)
                {
                    found = true;
                    break;
                }
            }
        }
        else
        {
            for (; (uint)index < count; index++)
            {
                bool equal = (ReadMemoryByDimension(bus, sourceAddress + (uint)index * stride, dimension) & mask) == target;
                if (equal == searchForEqual)
                {
                    found = true;
                    break;
                }
            }
        }

        _reg[28] = unchecked(sourceAddress + (uint)index * stride);
        _reg[27] = (uint)index;
        _z = (byte)(found ? 0 : 1);
        return sourceLength + valueLength + 3;
    }

    private uint StopUnimplementedExtended(string group, byte subop)
    {
        Halted = true;
        LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented {group} subop 0x{subop:X2} pc=0x{PreviousPc:X8}");
        return 0;
    }

    private uint ExecuteReturnSubroutine(IV60Bus bus)
    {
        _reg[PcIndex] = bus.Read32(_reg[SpIndex]);
        _reg[SpIndex] += 4;
        return 0;
    }

    private uint ExecuteDecrementBranch(IV60Bus bus, int table)
    {
        byte operand = bus.Read8(Pc + 1);
        int condition = operand >> 5;
        int registerIndex = operand & 0x1f;
        bool taken;

        if (table == 1 && condition == 5)
        {
            taken = _reg[registerIndex] == 0;
        }
        else
        {
            _reg[registerIndex]--;
            taken = _reg[registerIndex] != 0 && DecrementBranchCondition(table, condition);
        }

        if (taken)
        {
            _reg[PcIndex] = unchecked(Pc + (uint)(short)bus.Read16(Pc + 2));
            return 0;
        }

        return 4;
    }

    private bool DecrementBranchCondition(int table, int condition)
    {
        return table == 0
            ? condition switch
            {
                0 => _ov != 0,                       // DBV
                1 => _cy != 0,                       // DBL
                2 => _z != 0,                        // DBE
                3 => (_cy | _z) != 0,                // DBNH
                4 => _s != 0,                        // DBN
                5 => true,                           // DBR
                6 => (_s ^ _ov) != 0,                // DBLT
                7 => ((_s ^ _ov) | _z) != 0,         // DBLE
                _ => false
            }
            : condition switch
            {
                0 => _ov == 0,                       // DBNV
                1 => _cy == 0,                       // DBNL
                2 => _z == 0,                        // DBNE
                3 => (_cy | _z) == 0,                // DBH
                4 => _s == 0,                        // DBP
                6 => (_s ^ _ov) == 0,                // DBGE
                7 => ((_s ^ _ov) | _z) == 0,         // DBGT
                _ => false
            };
    }

    private uint ExecuteJump(IV60Bus bus, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeAddress(bus, modm, mod, Pc + 1, dimension: 0, out uint target, out _, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        _reg[PcIndex] = target;
        return 0;
    }

    private uint ExecuteJumpSubroutine(IV60Bus bus, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeAddress(bus, modm, mod, Pc + 1, dimension: 0, out uint target, out uint length, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        _reg[SpIndex] -= 4;
        bus.Write32(_reg[SpIndex], Pc + length + 1);
        _reg[PcIndex] = target;
        return 0;
    }

    private uint ExecutePush(IV60Bus bus, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeValue(bus, modm, mod, Pc + 1, dimension: 2, out uint value, out uint length, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        _reg[SpIndex] -= 4;
        bus.Write32(_reg[SpIndex], value);
        return length + 1;
    }

    private uint ExecutePushMultiple(IV60Bus bus, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeValue(bus, modm, mod, Pc + 1, dimension: 2, out uint registerMask, out uint length, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        if ((registerMask & (1u << 31)) != 0)
        {
            _reg[SpIndex] -= 4;
            bus.Write32(_reg[SpIndex], ReadPsw());
        }

        for (int i = 0; i < 31; i++)
        {
            int register = 30 - i;
            if ((registerMask & (1u << register)) == 0)
                continue;

            _reg[SpIndex] -= 4;
            bus.Write32(_reg[SpIndex], _reg[register]);
        }

        return length + 1;
    }

    private uint ExecutePopMultiple(IV60Bus bus, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeValue(bus, modm, mod, Pc + 1, dimension: 2, out uint registerMask, out uint length, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        for (int register = 0; register < 31; register++)
        {
            if ((registerMask & (1u << register)) == 0)
                continue;

            _reg[register] = bus.Read32(_reg[SpIndex]);
            _reg[SpIndex] += 4;
        }

        if ((registerMask & (1u << 31)) != 0)
        {
            WritePsw((ReadPsw() & 0xffff0000) | bus.Read16(_reg[SpIndex]));
            _reg[SpIndex] += 4;
        }

        return length + 1;
    }

    private uint ExecuteTestSingle(IV60Bus bus, int dimension, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeValue(bus, modm, mod, Pc + 1, dimension, out uint value, out uint length, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        value &= MaskForDimension(dimension);
        _z = (byte)(value == 0 ? 1 : 0);
        _s = (byte)((value & SignBitForDimension(dimension)) != 0 ? 1 : 0);
        _cy = 0;
        _ov = 0;
        return length + 1;
    }

    private uint ExecuteReturnFromSystem(IV60Bus bus, int modm)
    {
        byte mod = bus.Read8(Pc + 1);
        if (!TryReadAddressModeValue(bus, modm, mod, Pc + 1, dimension: 1, out uint stackFrameBytes, out _, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint returnPc = bus.Read32(_reg[SpIndex]);
        _reg[SpIndex] += 4;
        uint newPsw = bus.Read32(_reg[SpIndex]);
        _reg[SpIndex] += 4 + stackFrameBytes;
        WritePsw(newPsw);
        _reg[PcIndex] = returnPc;
        return 0;
    }

    private uint ExecuteLdpr(IV60Bus bus)
    {
        byte instFlags = bus.Read8(Pc + 1);
        if ((instFlags & 0x80) == 0)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented LDPR flags=0x{instFlags:X2} pc=0x{PreviousPc:X8}");
            return 0;
        }

        if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, bus.Read8(Pc + 2), Pc + 2, 2, out uint source, out uint sourceLength, out string sourceError))
        {
            Halted = true;
            LastStopReason = sourceError;
            return 0;
        }

        uint secondAddress = Pc + 2 + sourceLength;
        byte secondMod = bus.Read8(secondAddress);
        if (!TryReadAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, secondMod, secondAddress, 2, out uint systemRegister, out uint destLength, out string destError))
        {
            Halted = true;
            LastStopReason = destError;
            return 0;
        }

        if (systemRegister > 28)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"invalid LDPR system register {systemRegister} pc=0x{PreviousPc:X8}");
            return 0;
        }

        _reg[systemRegister + 36] = source;
        return sourceLength + destLength + 2;
    }

    private uint ExecuteMoveValue(IV60Bus bus, int dimension)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, dimension, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, dimension);
            sourceLength = 0;
        }

        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, dimension, source, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, source, dimension);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, dimension, source, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteMoveZeroExtend(IV60Bus bus, int sourceDimension, int destDimension)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, sourceDimension, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, sourceDimension);
            sourceLength = 0;
        }

        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, destDimension, source, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, source, destDimension);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, destDimension, source, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteMoveSignExtend(IV60Bus bus, int sourceDimension, int destDimension)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, sourceDimension, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, sourceDimension);
            sourceLength = 0;
        }

        source = sourceDimension switch
        {
            0 => unchecked((uint)(int)(sbyte)(byte)source),
            1 => unchecked((uint)(int)(short)(ushort)source),
            _ => source
        };

        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, destDimension, source, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, source, destDimension);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, destDimension, source, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteMoveTruncate(IV60Bus bus, int sourceDimension, int destDimension)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, sourceDimension, destDimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint result = source & MaskForDimension(destDimension);
        uint truncated = source & MaskForDimension(sourceDimension);
        uint sign = SignBitForDimension(destDimension);
        uint discardedMask = MaskForDimension(sourceDimension) & ~MaskForDimension(destDimension);
        uint expectedDiscarded = (result & sign) != 0 ? discardedMask : 0u;
        _ov = (byte)((truncated & discardedMask) == expectedDiscarded ? 0 : 1);
        WriteOperandReference(bus, dest, result, destDimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteReverseBytes(IV60Bus bus)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, 2, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, 2);
            sourceLength = 0;
        }

        uint reversed = ((source & 0x000000ff) << 24)
            | ((source & 0x0000ff00) << 8)
            | ((source & 0x00ff0000) >> 8)
            | ((source & 0xff000000) >> 24);

        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, 2, reversed, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, reversed, 2);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, 2, reversed, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteSetFlag(IV60Bus bus)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, 0, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, 0);
            sourceLength = 0;
        }

        uint result = SetFlagCondition((int)(source & 0x0f)) ? 1u : 0u;
        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, 0, result, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, result, 0);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, 0, result, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteNot(IV60Bus bus, int dimension)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, dimension, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, dimension);
            sourceLength = 0;
        }

        uint result = (~source) & MaskForDimension(dimension);
        _ov = 0;
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & SignBitForDimension(dimension)) != 0 ? 1 : 0);

        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, dimension, result, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, result, dimension);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, dimension, result, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteNegate(IV60Bus bus, int dimension)
    {
        byte instFlags = bus.Read8(Pc + 1);

        uint source;
        uint sourceLength;
        if ((instFlags & 0x80) != 0 || (instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, dimension, out source, out sourceLength, out string sourceError))
            {
                Halted = true;
                LastStopReason = sourceError;
                return 0;
            }
        }
        else
        {
            source = ReadRegisterByDimension(instFlags & 0x1f, dimension);
            sourceLength = 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        source &= mask;
        uint result = unchecked(0u - source) & mask;
        _cy = (byte)(result != 0 ? 1 : 0);
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        _ov = (byte)(source == sign ? 1 : 0);

        if ((instFlags & 0x80) != 0)
        {
            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, dimension, result, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return sourceLength + destLength + 2;
        }

        if ((instFlags & 0x20) != 0)
        {
            WriteRegisterByDimension(instFlags & 0x1f, result, dimension);
            return sourceLength + 2;
        }

        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        if (!TryWriteAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, dimension, result, out uint writeLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        return writeLength + 2;
    }

    private uint ExecuteMoveAddress(IV60Bus bus, int dimension)
    {
        byte instFlags = bus.Read8(Pc + 1);
        if ((instFlags & 0x80) != 0)
        {
            uint firstAddress = Pc + 2;
            byte firstMod = bus.Read8(firstAddress);
            if (!TryReadAddressModeAddress(bus, (instFlags & 0x40) != 0 ? 1 : 0, firstMod, firstAddress, dimension, out uint source, out uint firstLength, out string firstError))
            {
                Halted = true;
                LastStopReason = firstError;
                return 0;
            }

            uint destAddress = Pc + 2 + firstLength;
            byte destMod = bus.Read8(destAddress);
            if (!TryWriteAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, 2, source, out uint destLength, out string destError))
            {
                Halted = true;
                LastStopReason = destError;
                return 0;
            }

            return firstLength + destLength + 2;
        }

        uint registerSourceAddress = Pc + 2;
        byte registerSourceMod = bus.Read8(registerSourceAddress);
        if (!TryReadAddressModeAddress(bus, (instFlags & 0x40) != 0 ? 1 : 0, registerSourceMod, registerSourceAddress, dimension, out uint registerSource, out uint registerSourceLength, out string registerSourceError))
        {
            Halted = true;
            LastStopReason = registerSourceError;
            return 0;
        }

        if ((instFlags & 0x20) == 0)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented MOVEA memory destination flags=0x{instFlags:X2} pc=0x{PreviousPc:X8}");
            return 0;
        }

        _reg[instFlags & 0x1f] = registerSource;
        return registerSourceLength + 2;
    }

    private uint ExecuteExchange(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12References(bus, dimension, out OperandReference first, out OperandReference second, out uint firstLength, out uint secondLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint firstValue = ReadOperandReference(bus, first, dimension);
        uint secondValue = ReadOperandReference(bus, second, dimension);
        WriteOperandReference(bus, first, secondValue, dimension);
        WriteOperandReference(bus, second, firstValue, dimension);
        return firstLength + secondLength + 2;
    }

    private uint ExecuteLogicalOperation(IV60Bus bus, int dimension, LogicalOperation operation)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, dimension, dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint destValue = ReadOperandReference(bus, dest, dimension);
        uint result = operation switch
        {
            LogicalOperation.Or => destValue | source,
            LogicalOperation.And => destValue & source,
            LogicalOperation.Xor => destValue ^ source,
            _ => destValue
        };

        result &= MaskForDimension(dimension);
        _ov = 0;
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & SignBitForDimension(dimension)) != 0 ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteBitOperation(IV60Bus bus, BitOperation operation)
    {
        if (operation == BitOperation.Test)
        {
            if (!TryDecodeFormat12Values(bus, dimension: 2, out uint bitIndex, out uint value, out uint sourceLength, out uint destLength, out string error))
            {
                Halted = true;
                LastStopReason = error;
                return 0;
            }

            _cy = (byte)((value & (1u << (int)(bitIndex & 31))) != 0 ? 1 : 0);
            _z = (byte)(_cy == 0 ? 1 : 0);
            return sourceLength + destLength + 2;
        }

        if (!TryDecodeFormat12ValueAndAddress(bus, sourceDimension: 2, destDimension: 2, out uint source, out OperandReference dest, out uint firstLength, out uint secondLength, out string writeError))
        {
            Halted = true;
            LastStopReason = writeError;
            return 0;
        }

        uint bit = 1u << (int)(source & 31);
        uint valueToWrite = ReadOperandReference(bus, dest, 2);
        _cy = (byte)((valueToWrite & bit) != 0 ? 1 : 0);
        _z = (byte)(_cy == 0 ? 1 : 0);

        valueToWrite = operation switch
        {
            BitOperation.Set => valueToWrite | bit,
            BitOperation.Clear => valueToWrite & ~bit,
            BitOperation.Not => (valueToWrite & bit) != 0 ? valueToWrite & ~bit : valueToWrite | bit,
            _ => valueToWrite
        };

        WriteOperandReference(bus, dest, valueToWrite, 2);
        return firstLength + secondLength + 2;
    }

    private uint ExecuteAdd(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, dimension, dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        uint destValue = ReadOperandReference(bus, dest, dimension) & mask;
        source &= mask;
        ulong sum = (ulong)destValue + source;
        uint result = (uint)sum & mask;
        _cy = (byte)(sum > mask ? 1 : 0);
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        _ov = (byte)((((~(destValue ^ source)) & (destValue ^ result) & sign) != 0) ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteMultiply(IV60Bus bus, int dimension, bool unsigned)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, dimension, dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        uint destValue = ReadOperandReference(bus, dest, dimension) & mask;
        source &= mask;

        ulong product = unsigned
            ? (ulong)destValue * source
            : dimension switch
            {
                0 => (ulong)((long)(sbyte)(byte)destValue * (sbyte)(byte)source),
                1 => (ulong)((long)(short)(ushort)destValue * (short)(ushort)source),
                _ => (ulong)((long)(int)destValue * (int)source)
            };

        uint result = (uint)product & mask;
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        _ov = (byte)((product >> (SizeForDimension(dimension) * 8)) != 0 ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteDivideExtendedUnsigned(IV60Bus bus)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, sourceDimension: 2, destDimension: 3, out uint divisor, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        if (divisor == 0)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"DIVUX divide by zero pc=0x{PreviousPc:X8}");
            return 0;
        }

        ulong dividend = dest.IsRegister
            ? ((ulong)_reg[(dest.RegisterIndex + 1) & 0x1f] << 32) | _reg[dest.RegisterIndex & 0x1f]
            : ((ulong)bus.Read32(dest.Address + 4) << 32) | bus.Read32(dest.Address);

        ulong quotient64 = dividend / divisor;
        uint quotient = (uint)quotient64;
        uint remainder = (uint)(dividend % divisor);
        _z = (byte)(quotient == 0 ? 1 : 0);
        _s = (byte)((quotient & 0x80000000) != 0 ? 1 : 0);
        _ov = (byte)(quotient64 > uint.MaxValue ? 1 : 0);

        if (dest.IsRegister)
        {
            _reg[dest.RegisterIndex & 0x1f] = quotient;
            _reg[(dest.RegisterIndex + 1) & 0x1f] = remainder;
        }
        else
        {
            bus.Write32(dest.Address, quotient);
            bus.Write32(dest.Address + 4, remainder);
        }

        return sourceLength + destLength + 2;
    }

    private uint ExecuteDivide(IV60Bus bus, int dimension, bool unsigned)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, dimension, dimension, out uint divisor, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        divisor &= mask;
        uint result = ReadOperandReference(bus, dest, dimension) & mask;

        if (unsigned)
        {
            _ov = 0;
            if (divisor != 0)
                result = (result / divisor) & mask;
        }
        else
        {
            _ov = (byte)(result == sign && divisor == mask ? 1 : 0);
            if (divisor != 0 && _ov == 0)
            {
                long left = SignedValue(result, dimension);
                long right = SignedValue(divisor, dimension);
                result = unchecked((uint)(left / right)) & mask;
            }
        }

        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteSubtract(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, dimension, dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        uint destValue = ReadOperandReference(bus, dest, dimension) & mask;
        source &= mask;
        uint result = (destValue - source) & mask;
        _cy = (byte)(destValue < source ? 1 : 0);
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        _ov = (byte)((((destValue ^ source) & (destValue ^ result) & sign) != 0) ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteShiftLogical(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, sourceDimension: 0, destDimension: dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint destValue = ReadOperandReference(bus, dest, dimension) & MaskForDimension(dimension);
        int count = unchecked((sbyte)(byte)source);
        int bits = SizeForDimension(dimension) * 8;
        uint result = destValue;

        if (count > 0)
        {
            _ov = 0;
            _cy = (byte)(count <= bits ? (destValue >> (bits - count)) & 1 : 0);
            result = count >= bits ? 0 : (destValue << count) & MaskForDimension(dimension);
        }
        else if (count < 0)
        {
            int shift = -count;
            _cy = (byte)(shift <= bits ? (destValue >> (shift - 1)) & 1 : 0);
            _ov = 0;
            result = shift >= bits ? 0 : destValue >> shift;
        }
        else
        {
            _cy = 0;
            _ov = 0;
        }

        result &= MaskForDimension(dimension);
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & SignBitForDimension(dimension)) != 0 ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteRotate(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, sourceDimension: 0, destDimension: dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        int bits = SizeForDimension(dimension) * 8;
        uint mask = MaskForDimension(dimension);
        uint result = ReadOperandReference(bus, dest, dimension) & mask;
        int count = unchecked((sbyte)(byte)source);
        int rotations = Math.Abs(count) % bits;
        if (count > 0)
        {
            for (int i = 0; i < rotations; i++)
                result = ((result << 1) | ((result & SignBitForDimension(dimension)) >> (bits - 1))) & mask;
            _cy = (byte)(rotations == 0 ? 0 : result & 1);
        }
        else if (count < 0)
        {
            for (int i = 0; i < rotations; i++)
                result = (result >> 1) | ((result & 1) << (bits - 1));
            _cy = (byte)(rotations == 0 ? 0 : (result & SignBitForDimension(dimension)) != 0 ? 1 : 0);
        }
        else
        {
            _cy = 0;
        }

        _ov = 0;
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & SignBitForDimension(dimension)) != 0 ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteRotateCarry(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12ValueAndAddress(bus, sourceDimension: 0, destDimension: dimension, out uint source, out OperandReference dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        int bits = SizeForDimension(dimension) * 8;
        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        uint result = ReadOperandReference(bus, dest, dimension) & mask;
        int count = unchecked((sbyte)(byte)source);
        if (count > 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte oldCarry = _cy;
                _cy = (byte)((result & sign) != 0 ? 1 : 0);
                result = ((result << 1) | oldCarry) & mask;
            }
        }
        else if (count < 0)
        {
            count = -count;
            for (int i = 0; i < count; i++)
            {
                byte oldCarry = _cy;
                _cy = (byte)(result & 1);
                result = (result >> 1) | ((uint)oldCarry << (bits - 1));
            }
        }
        else
        {
            _cy = 0;
        }

        _ov = 0;
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        WriteOperandReference(bus, dest, result, dimension);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteCompare(IV60Bus bus, int dimension)
    {
        if (!TryDecodeFormat12Values(bus, dimension, out uint source, out uint dest, out uint sourceLength, out uint destLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        uint mask = MaskForDimension(dimension);
        uint sign = SignBitForDimension(dimension);
        source &= mask;
        dest &= mask;
        uint result = (dest - source) & mask;
        _cy = (byte)(dest < source ? 1 : 0);
        _z = (byte)(result == 0 ? 1 : 0);
        _s = (byte)((result & sign) != 0 ? 1 : 0);
        _ov = (byte)((((dest ^ source) & (dest ^ result) & sign) != 0) ? 1 : 0);
        return sourceLength + destLength + 2;
    }

    private uint ExecuteUpdatePswWord(IV60Bus bus)
    {
        if (!TryDecodeFormat12Values(bus, dimension: 2, out uint value, out uint mask, out uint valueLength, out uint maskLength, out string error))
        {
            Halted = true;
            LastStopReason = error;
            return 0;
        }

        value &= 0x00ff_ffff;
        mask &= 0x00ff_ffff;
        WritePsw((ReadPsw() & ~mask) | (value & mask));
        return valueLength + maskLength + 2;
    }

    private uint UpdatePswForException(bool isInterrupt, int targetLevel)
    {
        uint oldPsw = ReadPsw();
        uint newPsw = oldPsw;
        newPsw &= ~(3u << 24);
        newPsw |= ((uint)targetLevel & 3u) << 24;
        newPsw &= ~(1u << 18);
        newPsw &= ~(1u << 16);
        newPsw &= ~(1u << 27);
        newPsw &= ~(1u << 17);
        newPsw &= ~(1u << 29);
        if (isInterrupt)
            newPsw |= 1u << 28;
        newPsw |= 1u << 31;
        WritePsw(newPsw);
        return oldPsw;
    }

    private bool TryDecodeFormat12Values(
        IV60Bus bus,
        int dimension,
        out uint source,
        out uint dest,
        out uint sourceLength,
        out uint destLength,
        out string error)
    {
        source = 0;
        dest = 0;
        sourceLength = 0;
        destLength = 0;
        error = string.Empty;

        byte instFlags = bus.Read8(Pc + 1);
        if ((instFlags & 0x80) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, dimension, out source, out sourceLength, out error))
                return false;

            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            return TryReadAddressModeValue(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, dimension, out dest, out destLength, out error);
        }

        if ((instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, dimension, out source, out sourceLength, out error))
                return false;

            dest = ReadRegisterByDimension(instFlags & 0x1f, dimension);
            return true;
        }

        source = ReadRegisterByDimension(instFlags & 0x1f, dimension);
        uint destAddress2 = Pc + 2;
        byte destMod2 = bus.Read8(destAddress2);
        return TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, destMod2, destAddress2, dimension, out dest, out destLength, out error);
    }

    private bool TryDecodeFormat12ValueAndAddress(
        IV60Bus bus,
        int sourceDimension,
        int destDimension,
        out uint source,
        out OperandReference dest,
        out uint sourceLength,
        out uint destLength,
        out string error)
    {
        source = 0;
        dest = default;
        sourceLength = 0;
        destLength = 0;
        error = string.Empty;

        byte instFlags = bus.Read8(Pc + 1);
        if ((instFlags & 0x80) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, sourceDimension, out source, out sourceLength, out error))
                return false;

            uint destAddress = Pc + 2 + sourceLength;
            byte destMod = bus.Read8(destAddress);
            return TryReadAddressModeReference(bus, (instFlags & 0x20) != 0 ? 1 : 0, destMod, destAddress, destDimension, out dest, out destLength, out error);
        }

        if ((instFlags & 0x20) != 0)
        {
            uint sourceAddress = Pc + 2;
            byte sourceMod = bus.Read8(sourceAddress);
            if (!TryReadAddressModeValue(bus, (instFlags & 0x40) != 0 ? 1 : 0, sourceMod, sourceAddress, sourceDimension, out source, out sourceLength, out error))
                return false;

            dest = OperandReference.ForRegister(instFlags & 0x1f);
            destLength = 0;
            return true;
        }

        source = ReadRegisterByDimension(instFlags & 0x1f, sourceDimension);
        uint writeAddress = Pc + 2;
        byte writeMod = bus.Read8(writeAddress);
        return TryReadAddressModeReference(bus, (instFlags & 0x40) != 0 ? 1 : 0, writeMod, writeAddress, destDimension, out dest, out destLength, out error);
    }

    private bool TryDecodeFormat12References(
        IV60Bus bus,
        int dimension,
        out OperandReference first,
        out OperandReference second,
        out uint firstLength,
        out uint secondLength,
        out string error)
    {
        first = default;
        second = default;
        firstLength = 0;
        secondLength = 0;
        error = string.Empty;

        byte instFlags = bus.Read8(Pc + 1);
        if ((instFlags & 0x80) != 0)
        {
            uint firstAddress = Pc + 2;
            byte firstMod = bus.Read8(firstAddress);
            if (!TryReadAddressModeReference(bus, (instFlags & 0x40) != 0 ? 1 : 0, firstMod, firstAddress, dimension, out first, out firstLength, out error))
                return false;

            uint secondAddress = Pc + 2 + firstLength;
            byte secondMod = bus.Read8(secondAddress);
            return TryReadAddressModeReference(bus, (instFlags & 0x20) != 0 ? 1 : 0, secondMod, secondAddress, dimension, out second, out secondLength, out error);
        }

        if ((instFlags & 0x20) != 0)
        {
            second = OperandReference.ForRegister(instFlags & 0x1f);
            uint firstAddress = Pc + 2;
            byte firstMod = bus.Read8(firstAddress);
            return TryReadAddressModeReference(bus, (instFlags & 0x40) != 0 ? 1 : 0, firstMod, firstAddress, dimension, out first, out firstLength, out error);
        }

        first = OperandReference.ForRegister(instFlags & 0x1f);
        uint secondAddress2 = Pc + 2;
        byte secondMod2 = bus.Read8(secondAddress2);
        return TryReadAddressModeReference(bus, (instFlags & 0x40) != 0 ? 1 : 0, secondMod2, secondAddress2, dimension, out second, out secondLength, out error);
    }

    private bool TryReadAddressModeAddress(
        IV60Bus bus,
        int modm,
        byte mod,
        uint modAddress,
        int dimension,
        out uint address,
        out uint length,
        out string error)
    {
        address = 0;
        length = 0;
        error = string.Empty;

        int group = mod >> 5;
        int registerIndex = mod & 0x1f;
        if (modm == 0)
        {
            switch (group)
            {
                case 0:
                    address = unchecked(_reg[registerIndex] + (uint)(sbyte)bus.Read8(modAddress + 1));
                    length = 2;
                    return true;
                case 1:
                    address = unchecked(_reg[registerIndex] + (uint)(short)bus.Read16(modAddress + 1));
                    length = 3;
                    return true;
                case 2:
                    address = unchecked(_reg[registerIndex] + bus.Read32(modAddress + 1));
                    length = 5;
                    return true;
                case 3:
                    address = _reg[registerIndex];
                    length = 1;
                    return true;
                case 4:
                    address = bus.Read32(unchecked(_reg[registerIndex] + (uint)(sbyte)bus.Read8(modAddress + 1)));
                    length = 2;
                    return true;
                case 5:
                    address = bus.Read32(unchecked(_reg[registerIndex] + (uint)(short)bus.Read16(modAddress + 1)));
                    length = 3;
                    return true;
                case 6:
                    address = bus.Read32(unchecked(_reg[registerIndex] + bus.Read32(modAddress + 1)));
                    length = 5;
                    return true;
            }
        }

        if (modm == 1)
        {
            int size = SizeForDimension(dimension);
            switch (group)
            {
                case 0:
                    address = unchecked(bus.Read32(_reg[registerIndex] + (uint)(sbyte)bus.Read8(modAddress + 1)) + (uint)(sbyte)bus.Read8(modAddress + 2));
                    length = 3;
                    return true;
                case 1:
                    address = unchecked(bus.Read32(_reg[registerIndex] + (uint)(short)bus.Read16(modAddress + 1)) + (uint)(short)bus.Read16(modAddress + 3));
                    length = 5;
                    return true;
                case 2:
                    address = unchecked(bus.Read32(_reg[registerIndex] + bus.Read32(modAddress + 1)) + bus.Read32(modAddress + 5));
                    length = 9;
                    return true;
                case 3:
                    address = _reg[registerIndex];
                    length = 1;
                    return true;
                case 4:
                    address = _reg[registerIndex];
                    _reg[registerIndex] = unchecked(_reg[registerIndex] + (uint)size);
                    length = 1;
                    return true;
                case 5:
                    _reg[registerIndex] = unchecked(_reg[registerIndex] - (uint)size);
                    address = _reg[registerIndex];
                    length = 1;
                    return true;
                case 6:
                    return TryReadIndexedAddressMode(bus, mod, modAddress, dimension, out address, out length);
            }
        }

        if (modm == 0 && group == 7)
        {
            int submode = mod & 0x1f;
            switch (submode)
            {
                case 0x10:
                    address = unchecked(Pc + (uint)(sbyte)bus.Read8(modAddress + 1));
                    length = 2;
                    return true;
                case 0x11:
                    address = unchecked(Pc + (uint)(short)bus.Read16(modAddress + 1));
                    length = 3;
                    return true;
                case 0x12:
                    address = unchecked(Pc + bus.Read32(modAddress + 1));
                    length = 5;
                    return true;
                case 0x13:
                    address = bus.Read32(modAddress + 1);
                    length = 5;
                    return true;
                case 0x15:
                    address = bus.Read32(bus.Read32(modAddress + 1));
                    length = 5;
                    return true;
            }
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"unimplemented address mode modm={modm} mod=0x{mod:X2} for opcode 0x{bus.Read8(Pc):X2} pc=0x{PreviousPc:X8}");
        return false;
    }

    private bool TryReadIndexedAddressMode(IV60Bus bus, byte mod, uint modAddress, int dimension, out uint address, out uint length)
    {
        address = 0;
        length = 0;

        byte second = bus.Read8(modAddress + 1);
        int mode = second >> 5;
        int baseRegister = second & 0x1f;
        int indexRegister = mod & 0x1f;
        uint scaledIndex = unchecked(_reg[indexRegister] * (uint)SizeForDimension(dimension));

        switch (mode)
        {
            case 0:
                address = unchecked(_reg[baseRegister] + (uint)(sbyte)bus.Read8(modAddress + 2) + scaledIndex);
                length = 3;
                return true;
            case 1:
                address = unchecked(_reg[baseRegister] + (uint)(short)bus.Read16(modAddress + 2) + scaledIndex);
                length = 4;
                return true;
            case 2:
                address = unchecked(_reg[baseRegister] + bus.Read32(modAddress + 2) + scaledIndex);
                length = 6;
                return true;
            case 3:
                address = unchecked(_reg[baseRegister] + scaledIndex);
                length = 2;
                return true;
            case 4:
                address = unchecked(bus.Read32(_reg[baseRegister] + (uint)(sbyte)bus.Read8(modAddress + 2)) + scaledIndex);
                length = 3;
                return true;
            case 5:
                address = unchecked(bus.Read32(_reg[baseRegister] + (uint)(short)bus.Read16(modAddress + 2)) + scaledIndex);
                length = 4;
                return true;
            case 6:
                address = unchecked(bus.Read32(_reg[baseRegister] + bus.Read32(modAddress + 2)) + scaledIndex);
                length = 6;
                return true;
            case 7:
                if ((second & 0x10) == 0)
                    return false;

                switch (second & 0x0f)
                {
                    case 0:
                        address = unchecked(Pc + (uint)(sbyte)bus.Read8(modAddress + 2) + scaledIndex);
                        length = 3;
                        return true;
                    case 1:
                        address = unchecked(Pc + (uint)(short)bus.Read16(modAddress + 2) + scaledIndex);
                        length = 4;
                        return true;
                    case 2:
                        address = unchecked(Pc + bus.Read32(modAddress + 2) + scaledIndex);
                        length = 6;
                        return true;
                    case 3:
                        address = unchecked(bus.Read32(modAddress + 2) + scaledIndex);
                        length = 6;
                        return true;
                    default:
                        return false;
                }
            default:
                return false;
        }
    }

    private bool TryReadAddressModeReference(
        IV60Bus bus,
        int modm,
        byte mod,
        uint modAddress,
        int dimension,
        out OperandReference reference,
        out uint length,
        out string error)
    {
        reference = default;
        length = 0;
        error = string.Empty;

        int group = mod >> 5;
        int registerIndex = mod & 0x1f;
        int size = SizeForDimension(dimension);

        if (modm == 1 && group == 3)
        {
            reference = OperandReference.ForRegister(registerIndex);
            length = 1;
            return true;
        }

        if (modm == 0)
        {
            switch (group)
            {
                case 0:
                    reference = OperandReference.ForMemory(unchecked(_reg[registerIndex] + (uint)(sbyte)bus.Read8(modAddress + 1)));
                    length = 2;
                    return true;
                case 1:
                    reference = OperandReference.ForMemory(unchecked(_reg[registerIndex] + (uint)(short)bus.Read16(modAddress + 1)));
                    length = 3;
                    return true;
                case 2:
                    reference = OperandReference.ForMemory(unchecked(_reg[registerIndex] + bus.Read32(modAddress + 1)));
                    length = 5;
                    return true;
                case 3:
                    reference = OperandReference.ForMemory(_reg[registerIndex]);
                    length = 1;
                    return true;
            }
        }

        if (modm == 1)
        {
            switch (group)
            {
                case 4:
                    reference = OperandReference.ForMemory(_reg[registerIndex]);
                    _reg[registerIndex] = unchecked(_reg[registerIndex] + (uint)size);
                    length = 1;
                    return true;
                case 5:
                    _reg[registerIndex] = unchecked(_reg[registerIndex] - (uint)size);
                    reference = OperandReference.ForMemory(_reg[registerIndex]);
                    length = 1;
                    return true;
            }
        }

        if (TryReadAddressModeAddress(bus, modm, mod, modAddress, dimension, out uint address, out length, out error))
        {
            reference = OperandReference.ForMemory(address);
            return true;
        }

        return false;
    }

    private bool TryReadAddressModeValue(
        IV60Bus bus,
        int modm,
        byte mod,
        uint modAddress,
        int dimension,
        out uint value,
        out uint length,
        out string error)
    {
        value = 0;
        length = 0;
        error = string.Empty;

        int group = mod >> 5;
        int registerIndex = mod & 0x1f;
        int size = SizeForDimension(dimension);
        if (modm == 0 && group == 3)
        {
            value = ReadMemoryByDimension(bus, _reg[registerIndex], dimension);
            length = 1;
            return true;
        }

        if (modm == 1 && group == 3)
        {
            value = ReadRegisterByDimension(registerIndex, dimension);
            length = 1;
            return true;
        }

        if (modm == 1 && group == 4)
        {
            uint operandAddress = _reg[registerIndex];
            value = ReadMemoryByDimension(bus, operandAddress, dimension);
            _reg[registerIndex] = unchecked(operandAddress + (uint)size);
            length = 1;
            return true;
        }

        if (modm == 1 && group == 5)
        {
            uint operandAddress = unchecked(_reg[registerIndex] - (uint)size);
            _reg[registerIndex] = operandAddress;
            value = ReadMemoryByDimension(bus, operandAddress, dimension);
            length = 1;
            return true;
        }

        if (modm == 0 && group == 7)
        {
            int submode = mod & 0x1f;
            if (submode <= 0x0f)
            {
                value = (uint)(mod & 0x0f);
                length = 1;
                return true;
            }

            if (submode == 0x14)
            {
                value = dimension switch
                {
                    0 => bus.Read8(modAddress + 1),
                    1 => bus.Read16(modAddress + 1),
                    2 => bus.Read32(modAddress + 1),
                    _ => 0
                };
                length = dimension switch
                {
                    0 => 2u,
                    1 => 3u,
                    2 => 5u,
                    _ => 1u
                };
                return true;
            }
        }

        if (TryReadAddressModeAddress(bus, modm, mod, modAddress, dimension, out uint address, out length, out error))
        {
            value = ReadMemoryByDimension(bus, address, dimension);
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"unimplemented value address mode modm={modm} mod=0x{mod:X2} dim={dimension} for opcode 0x{bus.Read8(Pc):X2} pc=0x{PreviousPc:X8}");
        return false;
    }

    private bool TryWriteAddressModeValue(
        IV60Bus bus,
        int modm,
        byte mod,
        uint modAddress,
        int dimension,
        uint value,
        out uint length,
        out string error)
    {
        length = 0;
        error = string.Empty;

        int group = mod >> 5;
        int registerIndex = mod & 0x1f;
        int size = SizeForDimension(dimension);
        if (TryReadAddressModeReference(bus, modm, mod, modAddress, dimension, out OperandReference reference, out length, out error))
        {
            WriteOperandReference(bus, reference, value, dimension);
            return true;
        }

        if (modm == 0 && group == 3)
        {
            WriteMemoryByDimension(bus, _reg[registerIndex], value, dimension);
            length = 1;
            return true;
        }

        if (modm == 1 && group == 4)
        {
            uint address = _reg[registerIndex];
            WriteMemoryByDimension(bus, address, value, dimension);
            _reg[registerIndex] = unchecked(address + (uint)size);
            length = 1;
            return true;
        }

        if (modm == 1 && group == 5)
        {
            uint address = unchecked(_reg[registerIndex] - (uint)size);
            _reg[registerIndex] = address;
            WriteMemoryByDimension(bus, address, value, dimension);
            length = 1;
            return true;
        }

        if (modm == 0 && group == 7 && (mod & 0x1f) == 0x13)
        {
            WriteMemoryByDimension(bus, bus.Read32(modAddress + 1), value, dimension);
            length = 5;
            return true;
        }

        error = string.Create(
            CultureInfo.InvariantCulture,
            $"unimplemented write address mode modm={modm} mod=0x{mod:X2} dim={dimension} for opcode 0x{bus.Read8(Pc):X2} pc=0x{PreviousPc:X8}");
        return false;
    }

    private uint ReadRegisterByDimension(int index, int dimension)
    {
        uint value = _reg[index & 0x1f];
        return dimension switch
        {
            0 => value & 0xff,
            1 => value & 0xffff,
            2 => value,
            _ => value
        };
    }

    private static int SizeForDimension(int dimension)
    {
        return dimension switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 1
        };
    }

    private static uint ReadMemoryByDimension(IV60Bus bus, uint address, int dimension)
    {
        return dimension switch
        {
            0 => bus.Read8(address),
            1 => bus.Read16(address),
            2 => bus.Read32(address),
            _ => bus.Read8(address)
        };
    }

    private static void WriteMemoryByDimension(IV60Bus bus, uint address, uint value, int dimension)
    {
        switch (dimension)
        {
            case 0:
                bus.Write8(address, (byte)value);
                break;
            case 1:
                bus.Write16(address, (ushort)value);
                break;
            case 2:
                bus.Write32(address, value);
                break;
        }
    }

    private uint ReadOperandReference(IV60Bus bus, OperandReference reference, int dimension)
    {
        return reference.IsRegister
            ? ReadRegisterByDimension(reference.RegisterIndex, dimension)
            : ReadMemoryByDimension(bus, reference.Address, dimension);
    }

    private void WriteOperandReference(IV60Bus bus, OperandReference reference, uint value, int dimension)
    {
        if (reference.IsRegister)
            WriteRegisterByDimension(reference.RegisterIndex, value, dimension);
        else
            WriteMemoryByDimension(bus, reference.Address, value, dimension);
    }

    private static uint MaskForDimension(int dimension)
    {
        return dimension switch
        {
            0 => 0xff,
            1 => 0xffff,
            2 => 0xffffffff,
            _ => 0xffffffff
        };
    }

    private static uint SignBitForDimension(int dimension)
    {
        return dimension switch
        {
            0 => 0x80,
            1 => 0x8000,
            2 => 0x80000000,
            _ => 0x80000000
        };
    }

    private static long SignedValue(uint value, int dimension)
    {
        return dimension switch
        {
            0 => (sbyte)(byte)value,
            1 => (short)(ushort)value,
            _ => (int)value
        };
    }

    private void WriteRegisterByDimension(int index, uint value, int dimension)
    {
        ref uint register = ref _reg[index & 0x1f];
        switch (dimension)
        {
            case 0:
                register = (register & 0xffffff00) | (value & 0xff);
                break;
            case 1:
                register = (register & 0xffff0000) | (value & 0xffff);
                break;
            case 2:
                register = value;
                break;
        }
    }

    private void WritePsw(uint value)
    {
        _reg[PswIndex] = value;
        _z = (byte)(value & 1);
        _s = (byte)(value & 2);
        _ov = (byte)(value & 4);
        _cy = (byte)(value & 8);
    }

    private uint ReadPsw()
    {
        _reg[PswIndex] &= 0xfffffff0;
        _reg[PswIndex] |= (uint)((_z != 0 ? 1 : 0) | (_s != 0 ? 2 : 0) | (_ov != 0 ? 4 : 0) | (_cy != 0 ? 8 : 0));
        return _reg[PswIndex];
    }

    private bool BranchCondition(byte opcode)
    {
        return (opcode & 0x0f) switch
        {
            0x0 => _ov != 0,                       // BV
            0x1 => _ov == 0,                       // BNV
            0x2 => _cy != 0,                       // BL
            0x3 => _cy == 0,                       // BNL
            0x4 => _z != 0,                        // BE
            0x5 => _z == 0,                        // BNE
            0x6 => (_cy | _z) != 0,                // BNH
            0x7 => (_cy | _z) == 0,                // BH
            0x8 => _s != 0,                        // BN
            0x9 => _s == 0,                        // BP
            0xa => true,                           // BR
            0xc => (_s ^ _ov) != 0,                // BLT
            0xd => (_s ^ _ov) == 0,                // BGE
            0xe => ((_s ^ _ov) | _z) != 0,         // BLE
            0xf => ((_s ^ _ov) | _z) == 0,         // BGT
            _ => UnsupportedBranch(opcode)
        };
    }

    private bool SetFlagCondition(int condition)
    {
        return condition switch
        {
            0x0 => _ov != 0,
            0x1 => _ov == 0,
            0x2 => _cy != 0,
            0x3 => _cy == 0,
            0x4 => _z != 0,
            0x5 => _z == 0,
            0x6 => (_cy | _z) != 0,
            0x7 => (_cy | _z) == 0,
            0x8 => _s != 0,
            0x9 => _s == 0,
            0xa => true,
            0xb => false,
            0xc => (_s ^ _ov) != 0,
            0xd => (_s ^ _ov) == 0,
            0xe => ((_s ^ _ov) | _z) != 0,
            0xf => ((_s ^ _ov) | _z) == 0,
            _ => false
        };
    }

    private bool UnsupportedBranch(byte opcode)
    {
        Halted = true;
        LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented branch opcode 0x{opcode:X2} pc=0x{PreviousPc:X8}");
        return false;
    }

    private enum LogicalOperation
    {
        Or,
        And,
        Xor
    }

    private enum BitOperation
    {
        Test,
        Set,
        Clear,
        Not
    }

    private readonly struct OperandReference
    {
        private OperandReference(bool isRegister, int registerIndex, uint address)
        {
            IsRegister = isRegister;
            RegisterIndex = registerIndex;
            Address = address;
        }

        public bool IsRegister { get; }
        public int RegisterIndex { get; }
        public uint Address { get; }

        public static OperandReference ForRegister(int index) => new(true, index & 0x1f, 0);
        public static OperandReference ForMemory(uint address) => new(false, 0, address);
    }
}
