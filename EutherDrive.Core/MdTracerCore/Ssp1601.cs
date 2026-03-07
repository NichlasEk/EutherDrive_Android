using System;

namespace EutherDrive.Core.MdTracerCore;

internal static class Ssp1601
{
    private enum AluOp
    {
        Add,
        Subtract,
        Compare,
        And,
        Or,
        ExclusiveOr,
    }

    private enum AccumulateOp
    {
        Zero,
        Add,
        Subtract,
    }

    private enum Condition
    {
        True,
        Zero,
        NotZero,
        Negative,
        NotNegative,
    }

    private enum RamBank
    {
        Zero,
        One,
    }

    private enum AddressingModeKind
    {
        GeneralRegister,
        PointerRegister,
        Indirect,
        DoubleIndirect,
        Direct,
        Immediate,
        ShortImmediate,
        AccumulatorIndirect,
    }

    private readonly struct AddressingMode
    {
        public readonly AddressingModeKind Kind;
        public readonly ushort Register;
        public readonly RamBank Bank;
        public readonly ushort Pointer;
        public readonly ushort Modifier;
        public readonly byte Address;
        public readonly byte ShortImmediate;

        private AddressingMode(
            AddressingModeKind kind,
            ushort register,
            RamBank bank,
            ushort pointer,
            ushort modifier,
            byte address,
            byte shortImmediate)
        {
            Kind = kind;
            Register = register;
            Bank = bank;
            Pointer = pointer;
            Modifier = modifier;
            Address = address;
            ShortImmediate = shortImmediate;
        }

        public static AddressingMode GeneralRegister(ushort register)
            => new(AddressingModeKind.GeneralRegister, register, default, 0, 0, 0, 0);

        public static AddressingMode PointerRegister(RamBank bank, ushort pointer)
            => new(AddressingModeKind.PointerRegister, 0, bank, pointer, 0, 0, 0);

        public static AddressingMode Indirect(RamBank bank, ushort pointer, ushort modifier)
            => new(AddressingModeKind.Indirect, 0, bank, pointer, modifier, 0, 0);

        public static AddressingMode DoubleIndirect(RamBank bank, ushort pointer, ushort modifier)
            => new(AddressingModeKind.DoubleIndirect, 0, bank, pointer, modifier, 0, 0);

        public static AddressingMode Direct(RamBank bank, byte address)
            => new(AddressingModeKind.Direct, 0, bank, 0, 0, address, 0);

        public static AddressingMode Immediate()
            => new(AddressingModeKind.Immediate, 0, default, 0, 0, 0, 0);

        public static AddressingMode ShortImmediateValue(byte value)
            => new(AddressingModeKind.ShortImmediate, 0, default, 0, 0, 0, value);

        public static AddressingMode AccumulatorIndirect()
            => new(AddressingModeKind.AccumulatorIndirect, 0, default, 0, 0, 0, 0);
    }

    private static readonly AddressingMode AccumulatorRegister = AddressingMode.GeneralRegister(3);

    public static void ExecuteInstruction(SvpCore svp, ReadOnlySpan<byte> romBytes)
    {
        ushort opcode = FetchOperand(svp, romBytes);

        switch (opcode & 0xFF00)
        {
            case 0x0000:
                LdDS(svp, romBytes, opcode);
                break;
            case 0x0200:
            case 0x0300:
                LdDRiIndirect(svp, romBytes, opcode);
                break;
            case 0x0400:
            case 0x0500:
                LdRiSIndirect(svp, romBytes, opcode);
                break;
            case 0x0600:
            case 0x0700:
                LdAAddr(svp, romBytes, opcode);
                break;
            case 0x0800:
                LdiDImm(svp, romBytes, opcode);
                break;
            case 0x0A00:
            case 0x0B00:
                LdDRiDoubleIndirect(svp, romBytes, opcode);
                break;
            case 0x0C00:
            case 0x0D00:
                LdiRiImm(svp, romBytes, opcode);
                break;
            case 0x0E00:
            case 0x0F00:
                LdAddrA(svp, romBytes, opcode);
                break;
            case 0x1200:
            case 0x1300:
                LdDRi(svp, romBytes, opcode);
                break;
            case 0x1400:
            case 0x1500:
                LdRiS(svp, romBytes, opcode);
                break;
            case >= 0x1800 and <= 0x1F00:
                LdiRiSimm(svp, romBytes, opcode);
                break;
            case 0x3700:
                ExecuteMultiplyAccumulate(svp, opcode, AccumulateOp.Subtract);
                break;
            case 0x4800:
            case 0x4900:
                ExecuteCall(svp, romBytes, opcode);
                break;
            case 0x4A00:
                LdDAIndirect(svp, romBytes, opcode);
                break;
            case 0x4C00:
            case 0x4D00:
                ExecuteBra(svp, romBytes, opcode);
                break;
            case 0x9000:
            case 0x9100:
                ExecuteMod(svp, opcode);
                break;
            case 0x9700:
                ExecuteMultiplyAccumulate(svp, opcode, AccumulateOp.Add);
                break;
            case 0xB700:
                ExecuteMultiplyAccumulate(svp, opcode, AccumulateOp.Zero);
                break;
            case 0xFF00:
                break;
            default:
                ExecuteAlu(svp, romBytes, opcode);
                break;
        }
    }

    private static ushort FetchOperand(SvpCore svp, ReadOnlySpan<byte> romBytes)
    {
        var r = svp.RegistersState;
        ushort operand = svp.ReadProgramMemory(r.Pc, romBytes);
        r.Pc = unchecked((ushort)(r.Pc + 1));
        return operand;
    }

    private static void ExecuteLoad(SvpCore svp, ReadOnlySpan<byte> romBytes, AddressingMode source, AddressingMode dest)
    {
        var r = svp.RegistersState;

        if (source.Kind == AddressingModeKind.GeneralRegister && source.Register == 7 &&
            dest.Kind == AddressingModeKind.GeneralRegister && dest.Register == 3)
        {
            r.Accumulator = r.Product();
            return;
        }

        if (source.Kind == AddressingModeKind.GeneralRegister && source.Register == 0 &&
            dest.Kind == AddressingModeKind.GeneralRegister && dest.Register is >= 8 and <= 15)
        {
            if (dest.Register <= 12)
            {
                int pmIndex = dest.Register - 8;
                r.PmWrite[pmIndex].Initialize(r.Pmc.Address, r.Pmc.Mode);
            }

            r.Pmc.WaitingFor = dest.Register != 14
                ? SvpCore.PmcWaitingFor.Address
                : Toggle(r.Pmc.WaitingFor);
            return;
        }

        if (source.Kind == AddressingModeKind.GeneralRegister && source.Register is >= 8 and <= 15 &&
            dest.Kind == AddressingModeKind.GeneralRegister && dest.Register == 0)
        {
            if (source.Register <= 12)
            {
                int pmIndex = source.Register - 8;
                r.PmRead[pmIndex].Initialize(r.Pmc.Address, r.Pmc.Mode);
            }

            r.Pmc.WaitingFor = source.Register != 14
                ? SvpCore.PmcWaitingFor.Address
                : Toggle(r.Pmc.WaitingFor);
            return;
        }

        ushort value = ReadAddressingMode(svp, romBytes, source);
        WriteAddressingMode(svp, dest, value);
    }

    private static void ExecuteAlu(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        if (!TryAluOpFromOpcode(opcode, out AluOp op))
        {
            throw new InvalidOperationException($"Invalid SSP1601 opcode: {opcode:X4}");
        }

        AddressingMode source = ParseAluAddressingMode(opcode);
        var r = svp.RegistersState;

        uint operand = source.Kind == AddressingModeKind.GeneralRegister && source.Register == 3
            ? r.Accumulator
            : source.Kind == AddressingModeKind.GeneralRegister && source.Register == 7
                ? r.Product()
                : (uint)ReadAddressingMode(svp, romBytes, source) << 16;

        uint accumulator = r.Accumulator;
        uint result = op switch
        {
            AluOp.Add => unchecked(accumulator + operand),
            AluOp.Subtract or AluOp.Compare => unchecked(accumulator - operand),
            AluOp.And => accumulator & operand,
            AluOp.Or => accumulator | operand,
            AluOp.ExclusiveOr => accumulator ^ operand,
            _ => throw new InvalidOperationException(),
        };

        UpdateFlags(svp, result);
        if (op != AluOp.Compare)
        {
            r.Accumulator = result;
        }
    }

    private static void UpdateFlags(SvpCore svp, uint accumulator)
    {
        var status = svp.RegistersState.Status;
        status.Zero = accumulator == 0;
        status.Negative = Bit(accumulator, 31);
    }

    private static void ExecuteMod(SvpCore svp, ushort opcode)
    {
        Condition condition = ConditionFromOpcode(opcode);
        if (!CheckCondition(condition, svp.RegistersState.Status))
        {
            return;
        }

        var r = svp.RegistersState;
        switch (opcode & 0x0007)
        {
            case 0x0002:
                r.Accumulator = (uint)((int)r.Accumulator >> 1);
                break;
            case 0x0003:
                r.Accumulator <<= 1;
                break;
            case 0x0006:
                r.Accumulator = unchecked(~r.Accumulator + 1);
                break;
            case 0x0007:
                if (Bit(r.Accumulator, 31))
                {
                    r.Accumulator = unchecked(~r.Accumulator + 1);
                }

                break;
            default:
                throw new InvalidOperationException($"Invalid SVP opcode: {opcode:X4}");
        }

        UpdateFlags(svp, r.Accumulator);
    }

    private static void ExecuteCall(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ushort address = FetchOperand(svp, romBytes);
        Condition condition = ConditionFromOpcode(opcode);

        if (CheckCondition(condition, svp.RegistersState.Status))
        {
            var r = svp.RegistersState;
            r.Stack.Push(r.Pc);
            r.Pc = address;
        }
    }

    private static void ExecuteBra(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ushort address = FetchOperand(svp, romBytes);
        Condition condition = ConditionFromOpcode(opcode);

        if (CheckCondition(condition, svp.RegistersState.Status))
        {
            svp.RegistersState.Pc = address;
        }
    }

    private static void ExecuteMultiplyAccumulate(SvpCore svp, ushort opcode, AccumulateOp op)
    {
        var r = svp.RegistersState;

        r.Accumulator = op switch
        {
            AccumulateOp.Zero => 0,
            AccumulateOp.Add => unchecked(r.Accumulator + r.Product()),
            AccumulateOp.Subtract => unchecked(r.Accumulator - r.Product()),
            _ => throw new InvalidOperationException(),
        };

        UpdateFlags(svp, r.Accumulator);

        ushort xPointer = (ushort)(opcode & 0x03);
        ushort xModifier = (ushort)((opcode >> 2) & 0x03);
        byte ram0Addr = ReadPointer(svp, RamBank.Zero, xPointer, xModifier);
        r.X = svp.Ram0[ram0Addr];

        ushort yPointer = (ushort)((opcode >> 4) & 0x03);
        ushort yModifier = (ushort)((opcode >> 6) & 0x03);
        byte ram1Addr = ReadPointer(svp, RamBank.One, yPointer, yModifier);
        r.Y = svp.Ram1[ram1Addr];
    }

    private static void LdDS(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.GeneralRegister((ushort)(opcode & 0xF)),
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)));
    }

    private static void LdDRi(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = RamBankFromOpcode(opcode);
        ushort pointer = (ushort)(opcode & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.PointerRegister(bank, pointer),
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)));
    }

    private static void LdRiS(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = RamBankFromOpcode(opcode);
        ushort pointer = (ushort)(opcode & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)),
            AddressingMode.PointerRegister(bank, pointer));
    }

    private static void LdDRiIndirect(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = RamBankFromOpcode(opcode);
        ushort pointer = (ushort)(opcode & 0x03);
        ushort modifier = (ushort)((opcode >> 2) & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.Indirect(bank, pointer, modifier),
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)));
    }

    private static void LdRiSIndirect(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = RamBankFromOpcode(opcode);
        ushort pointer = (ushort)(opcode & 0x03);
        ushort modifier = (ushort)((opcode >> 2) & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)),
            AddressingMode.Indirect(bank, pointer, modifier));
    }

    private static void LdDRiDoubleIndirect(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = RamBankFromOpcode(opcode);
        ushort pointer = (ushort)(opcode & 0x03);
        ushort modifier = (ushort)((opcode >> 2) & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.DoubleIndirect(bank, pointer, modifier),
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)));
    }

    private static void LdAAddr(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.Direct(RamBankFromOpcode(opcode), (byte)opcode),
            AccumulatorRegister);
    }

    private static void LdAddrA(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ExecuteLoad(
            svp,
            romBytes,
            AccumulatorRegister,
            AddressingMode.Direct(RamBankFromOpcode(opcode), (byte)opcode));
    }

    private static void LdiDImm(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.Immediate(),
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)));
    }

    private static void LdiRiImm(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = RamBankFromOpcode(opcode);
        ushort pointer = (ushort)(opcode & 0x03);
        ushort modifier = (ushort)((opcode >> 2) & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.Immediate(),
            AddressingMode.Indirect(bank, pointer, modifier));
    }

    private static void LdiRiSimm(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        RamBank bank = Bit(opcode, 10) ? RamBank.One : RamBank.Zero;
        ushort pointer = (ushort)((opcode >> 8) & 0x03);

        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.ShortImmediateValue((byte)opcode),
            AddressingMode.PointerRegister(bank, pointer));
    }

    private static void LdDAIndirect(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort opcode)
    {
        ExecuteLoad(
            svp,
            romBytes,
            AddressingMode.AccumulatorIndirect(),
            AddressingMode.GeneralRegister((ushort)((opcode >> 4) & 0xF)));
    }

    private static AddressingMode ParseAluAddressingMode(ushort opcode)
    {
        return (opcode & 0x1F00) switch
        {
            0x0000 => AddressingMode.GeneralRegister((ushort)(opcode & 0xF)),
            0x0200 or 0x0300 => AddressingMode.Indirect(
                RamBankFromOpcode(opcode),
                (ushort)(opcode & 0x03),
                (ushort)((opcode >> 2) & 0x03)),
            0x0600 or 0x0700 => AddressingMode.Direct(RamBankFromOpcode(opcode), (byte)opcode),
            0x0800 => AddressingMode.Immediate(),
            0x0A00 or 0x0B00 => AddressingMode.DoubleIndirect(
                RamBankFromOpcode(opcode),
                (ushort)(opcode & 0x03),
                (ushort)((opcode >> 2) & 0x03)),
            0x1200 or 0x1300 => AddressingMode.PointerRegister(
                RamBankFromOpcode(opcode),
                (ushort)(opcode & 0x03)),
            0x1800 => AddressingMode.ShortImmediateValue((byte)opcode),
            _ => throw new InvalidOperationException($"Invalid SVP opcode: {opcode:X4}"),
        };
    }

    private static ushort ReadAddressingMode(SvpCore svp, ReadOnlySpan<byte> romBytes, AddressingMode source)
    {
        var r = svp.RegistersState;

        return source.Kind switch
        {
            AddressingModeKind.GeneralRegister => ReadRegister(svp, romBytes, source.Register),
            AddressingModeKind.PointerRegister => (source.Bank, source.Pointer) switch
            {
                (RamBank.Zero, <= 2) => r.Ram0Pointers[source.Pointer],
                (RamBank.One, <= 2) => r.Ram1Pointers[source.Pointer],
                (_, 3) => 0,
                _ => throw new InvalidOperationException($"Invalid pointer register: {source.Pointer}"),
            },
            AddressingModeKind.Indirect => ReadIndirect(svp, source.Bank, source.Pointer, source.Modifier),
            AddressingModeKind.DoubleIndirect => ReadDoubleIndirect(svp, romBytes, source.Bank, source.Pointer, source.Modifier),
            AddressingModeKind.Direct => source.Bank == RamBank.Zero ? svp.Ram0[source.Address] : svp.Ram1[source.Address],
            AddressingModeKind.Immediate => FetchOperand(svp, romBytes),
            AddressingModeKind.ShortImmediate => source.ShortImmediate,
            AddressingModeKind.AccumulatorIndirect => svp.ReadProgramMemory((ushort)(r.Accumulator >> 16), romBytes),
            _ => throw new InvalidOperationException(),
        };
    }

    private static ushort ReadIndirect(SvpCore svp, RamBank bank, ushort pointer, ushort modifier)
    {
        byte ramAddress = ReadPointer(svp, bank, pointer, modifier);
        return bank == RamBank.Zero ? svp.Ram0[ramAddress] : svp.Ram1[ramAddress];
    }

    private static ushort ReadDoubleIndirect(
        SvpCore svp,
        ReadOnlySpan<byte> romBytes,
        RamBank bank,
        ushort pointer,
        ushort modifier)
    {
        byte ramAddress = ReadPointer(svp, bank, pointer, modifier);
        ushort[] ram = bank == RamBank.Zero ? svp.Ram0 : svp.Ram1;

        ushort indirectAddress = ram[ramAddress];
        ram[ramAddress] = unchecked((ushort)(indirectAddress + 1));

        return svp.ReadProgramMemory(indirectAddress, romBytes);
    }

    private static void WriteAddressingMode(SvpCore svp, AddressingMode dest, ushort value)
    {
        var r = svp.RegistersState;

        switch (dest.Kind)
        {
            case AddressingModeKind.GeneralRegister:
                WriteRegister(svp, dest.Register, value);
                return;
            case AddressingModeKind.PointerRegister:
                if (dest.Pointer < 3)
                {
                    if (dest.Bank == RamBank.Zero)
                    {
                        r.Ram0Pointers[dest.Pointer] = (byte)value;
                    }
                    else
                    {
                        r.Ram1Pointers[dest.Pointer] = (byte)value;
                    }
                }

                return;
            case AddressingModeKind.Indirect:
            {
                byte ramAddress = ReadPointer(svp, dest.Bank, dest.Pointer, dest.Modifier);
                if (dest.Bank == RamBank.Zero)
                {
                    svp.Ram0[ramAddress] = value;
                }
                else
                {
                    svp.Ram1[ramAddress] = value;
                }

                return;
            }
            case AddressingModeKind.Direct:
                if (dest.Bank == RamBank.Zero)
                {
                    svp.Ram0[dest.Address] = value;
                }
                else
                {
                    svp.Ram1[dest.Address] = value;
                }

                return;
            default:
                throw new InvalidOperationException($"Invalid write addressing mode: {dest.Kind}");
        }
    }

    private static ushort ReadRegister(SvpCore svp, ReadOnlySpan<byte> romBytes, ushort register)
    {
        var r = svp.RegistersState;

        return register switch
        {
            0 => 0xFFFF,
            1 => r.X,
            2 => r.Y,
            3 => (ushort)(r.Accumulator >> 16),
            4 => r.Status.ToWord(),
            5 => r.Stack.Pop(),
            6 => r.Pc,
            7 => (ushort)(r.Product() >> 16),
            8 => r.Status.StBitsSet ? PmRead(svp, romBytes, 0) : r.Xst.SspReadStatus(),
            9 => PmRead(svp, romBytes, 1),
            10 => PmRead(svp, romBytes, 2),
            11 => r.Status.StBitsSet ? PmRead(svp, romBytes, 3) : r.Xst.Value,
            12 => PmRead(svp, romBytes, 4),
            13 => 0xFFFF,
            14 => r.Pmc.Read(),
            15 => (ushort)r.Accumulator,
            _ => throw new InvalidOperationException($"Invalid SVP register number: {register}"),
        };
    }

    private static void WriteRegister(SvpCore svp, ushort register, ushort value)
    {
        var r = svp.RegistersState;

        switch (register)
        {
            case 0:
                return;
            case 1:
                r.X = value;
                return;
            case 2:
                r.Y = value;
                return;
            case 3:
                r.Accumulator = (r.Accumulator & 0x0000_FFFF) | ((uint)value << 16);
                return;
            case 4:
                r.Status.Write(value);
                return;
            case 5:
                r.Stack.Push(value);
                return;
            case 6:
                r.Pc = value;
                return;
            case 7:
                return;
            case 8:
                if (r.Status.StBitsSet)
                {
                    PmWrite(svp, 0, value);
                }
                else
                {
                    r.Xst.M68kWritten = Bit(value, 1);
                    r.Xst.SspWritten = Bit(value, 0);
                }

                return;
            case 9:
                PmWrite(svp, 1, value);
                return;
            case 10:
                PmWrite(svp, 2, value);
                return;
            case 11:
                if (r.Status.StBitsSet)
                {
                    PmWrite(svp, 3, value);
                }
                else
                {
                    r.Xst.SspWrite(value);
                }

                return;
            case 12:
                PmWrite(svp, 4, value);
                return;
            case 13:
                return;
            case 14:
                r.Pmc.Write(value);
                return;
            case 15:
                r.Accumulator = (r.Accumulator & 0xFFFF_0000) | value;
                return;
            default:
                throw new InvalidOperationException($"Invalid SVP register number: {register}");
        }
    }

    private static ushort PmRead(SvpCore svp, ReadOnlySpan<byte> romBytes, int pmIndex)
    {
        var r = svp.RegistersState;

        SvpCore.ProgrammableMemoryRegister pmRegister = r.PmRead[pmIndex];
        uint address = pmRegister.GetAndIncrementAddress();
        r.Pmc.UpdateFrom(pmRegister);

        return svp.ReadExternalMemory(address, romBytes);
    }

    private static void PmWrite(SvpCore svp, int pmIndex, ushort value)
    {
        var r = svp.RegistersState;

        SvpCore.ProgrammableMemoryRegister pmRegister = r.PmWrite[pmIndex];
        uint address = pmRegister.GetAndIncrementAddress();
        bool overwriteMode = pmRegister.OverwriteMode;

        r.Pmc.UpdateFrom(pmRegister);

        if (overwriteMode)
        {
            if (address is > 0x0F_FFFF and <= 0x1F_FFFF)
            {
                ushort existingValue = svp.ReadExternalMemory(address, ReadOnlySpan<byte>.Empty);
                ushort newValue = 0;

                ushort mask0 = 0x000F;
                ushort mask1 = 0x00F0;
                ushort mask2 = 0x0F00;
                ushort mask3 = 0xF000;

                newValue |= (ushort)((value & mask0) != 0 ? value & mask0 : existingValue & mask0);
                newValue |= (ushort)((value & mask1) != 0 ? value & mask1 : existingValue & mask1);
                newValue |= (ushort)((value & mask2) != 0 ? value & mask2 : existingValue & mask2);
                newValue |= (ushort)((value & mask3) != 0 ? value & mask3 : existingValue & mask3);

                svp.WriteExternalMemory(address, newValue);
            }

            return;
        }

        svp.WriteExternalMemory(address, value);
    }

    private static byte ReadPointer(SvpCore svp, RamBank bank, ushort pointer, ushort modifier)
    {
        if (pointer < 3)
        {
            byte[] registers = bank == RamBank.Zero
                ? svp.RegistersState.Ram0Pointers
                : svp.RegistersState.Ram1Pointers;

            byte ramAddress = registers[pointer];
            IncrementPointerRegister(ref registers[pointer], modifier, svp.RegistersState.Status.LoopModulo);
            return ramAddress;
        }

        return (byte)modifier;
    }

    private static void IncrementPointerRegister(ref byte register, ushort modifier, byte loopModulo)
    {
        switch (modifier)
        {
            case 0:
                return;
            case 1:
                register = unchecked((byte)(register + 1));
                return;
            case 2:
                register = ModuloDecrement(register, loopModulo);
                return;
            case 3:
                register = ModuloIncrement(register, loopModulo);
                return;
            default:
                throw new InvalidOperationException($"Invalid pointer register modifier: {modifier}");
        }
    }

    private static byte ModuloIncrement(byte value, byte modulo)
    {
        byte mask = unchecked((byte)(modulo - 1));
        return unchecked((byte)((value & ~mask) | ((value + 1) & mask)));
    }

    private static byte ModuloDecrement(byte value, byte modulo)
    {
        byte mask = unchecked((byte)(modulo - 1));
        return unchecked((byte)((value & ~mask) | ((value - 1) & mask)));
    }

    private static bool TryAluOpFromOpcode(ushort opcode, out AluOp aluOp)
    {
        switch (opcode & 0xE000)
        {
            case 0x2000:
                aluOp = AluOp.Subtract;
                return true;
            case 0x6000:
                aluOp = AluOp.Compare;
                return true;
            case 0x8000:
                aluOp = AluOp.Add;
                return true;
            case 0xA000:
                aluOp = AluOp.And;
                return true;
            case 0xC000:
                aluOp = AluOp.Or;
                return true;
            case 0xE000:
                aluOp = AluOp.ExclusiveOr;
                return true;
            default:
                aluOp = default;
                return false;
        }
    }

    private static Condition ConditionFromOpcode(ushort opcode)
    {
        return (opcode & 0x01F0) switch
        {
            0x0000 => Condition.True,
            0x0050 => Condition.NotZero,
            0x0150 => Condition.Zero,
            0x0070 => Condition.NotNegative,
            0x0170 => Condition.Negative,
            _ => throw new InvalidOperationException($"Invalid SVP opcode (invalid condition): {opcode:X4}"),
        };
    }

    private static bool CheckCondition(Condition condition, SvpCore.StatusRegister status)
    {
        return condition switch
        {
            Condition.True => true,
            Condition.Zero => status.Zero,
            Condition.NotZero => !status.Zero,
            Condition.Negative => status.Negative,
            Condition.NotNegative => !status.Negative,
            _ => throw new InvalidOperationException(),
        };
    }

    private static RamBank RamBankFromOpcode(ushort opcode)
    {
        return Bit(opcode, 8) ? RamBank.One : RamBank.Zero;
    }

    private static SvpCore.PmcWaitingFor Toggle(SvpCore.PmcWaitingFor value)
    {
        return value == SvpCore.PmcWaitingFor.Address
            ? SvpCore.PmcWaitingFor.Mode
            : SvpCore.PmcWaitingFor.Address;
    }

    private static bool Bit(uint value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }

    private static bool Bit(ushort value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }
}
