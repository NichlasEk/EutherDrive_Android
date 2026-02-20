using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static partial class Instructions
    {
        internal sealed partial class InstructionExecutor
        {
            public uint RlcR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = RotateLeft(original, false, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = RotateLeft(originalReg, false, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint RlcHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = RotateLeft(original, false, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint RlR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = RotateLeft(original, true, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = RotateLeft(originalReg, true, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint RlHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = RotateLeft(original, true, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint RrcR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = RotateRight(original, false, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = RotateRight(originalReg, false, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint RrcHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = RotateRight(original, false, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint RrR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = RotateRight(original, true, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = RotateRight(originalReg, true, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint RrHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = RotateRight(original, true, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint SlaR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = ShiftLeftArithmetic(original, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = ShiftLeftArithmetic(originalReg, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint SlaHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = ShiftLeftArithmetic(original, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint SllR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = ShiftLeftLogical(original, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = ShiftLeftLogical(originalReg, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint SllHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = ShiftLeftLogical(original, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint SraR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = ShiftRightArithmetic(original, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = ShiftRightArithmetic(originalReg, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint SraHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = ShiftRightArithmetic(original, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint SrlR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = ShiftRightLogical(original, ref Registers.F);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = ShiftRightLogical(originalReg, ref Registers.F);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint SrlHl((IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);

                byte original = Bus.ReadMemory(address);
                byte modified = ShiftRightLogical(original, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint Rld()
            {
                byte a = Registers.A;
                ushort address = Register16.HL.ReadFrom(Registers);
                byte memoryValue = Bus.ReadMemory(address);
                (byte newA, byte newMem) = RotateLeftDecimal(a, memoryValue, ref Registers.F);
                Registers.A = newA;
                Bus.WriteMemory(address, newMem);
                return 18;
            }

            public uint Rrd()
            {
                byte a = Registers.A;
                ushort address = Register16.HL.ReadFrom(Registers);
                byte memoryValue = Bus.ReadMemory(address);
                (byte newA, byte newMem) = RotateRightDecimal(a, memoryValue, ref Registers.F);
                Registers.A = newA;
                Bus.WriteMemory(address, newMem);
                return 18;
            }

            public uint SetBR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                byte bit = (byte)((opcode >> 3) & 0x07);

                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = SetBit(original, bit);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = SetBit(originalReg, bit);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint SetBHl(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);
                byte bit = (byte)((opcode >> 3) & 0x07);

                byte original = Bus.ReadMemory(address);
                byte modified = SetBit(original, bit);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint ResBR(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                byte bit = (byte)((opcode >> 3) & 0x07);

                if (index.HasValue)
                {
                    ushort address = ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset);
                    byte original = Bus.ReadMemory(address);
                    byte modified = ResetBit(original, bit);
                    Bus.WriteMemory(address, modified);
                    register.WriteTo(modified, Registers);
                    return 19;
                }

                byte originalReg = register.ReadFrom(Registers);
                byte modifiedReg = ResetBit(originalReg, bit);
                register.WriteTo(modifiedReg, Registers);
                return 8;
            }

            public uint ResBHl(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);
                byte bit = (byte)((opcode >> 3) & 0x07);

                byte original = Bus.ReadMemory(address);
                byte modified = ResetBit(original, bit);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 15u;
            }

            public uint Rlca()
            {
                byte a = Registers.A;
                Registers.A = (byte)((a << 1) | (a >> 7));
                Flags f = Registers.F;
                f.HalfCarry = false;
                f.Subtract = false;
                f.Carry = a.Bit(7);
                Registers.F = f;
                return 4;
            }

            public uint Rla()
            {
                byte a = Registers.A;
                Registers.A = (byte)((a << 1) | (Registers.F.Carry ? 1 : 0));
                Flags f = Registers.F;
                f.HalfCarry = false;
                f.Subtract = false;
                f.Carry = a.Bit(7);
                Registers.F = f;
                return 4;
            }

            public uint Rrca()
            {
                byte a = Registers.A;
                Registers.A = (byte)((a >> 1) | (a << 7));
                Flags f = Registers.F;
                f.HalfCarry = false;
                f.Subtract = false;
                f.Carry = a.Bit(0);
                Registers.F = f;
                return 4;
            }

            public uint Rra()
            {
                byte a = Registers.A;
                Registers.A = (byte)((a >> 1) | ((Registers.F.Carry ? 1 : 0) << 7));
                Flags f = Registers.F;
                f.HalfCarry = false;
                f.Subtract = false;
                f.Carry = a.Bit(0);
                Registers.F = f;
                return 4;
            }

            public uint BitBR(byte opcode)
            {
                Register8 register = ParseRegisterFromOpcode(opcode, null)!.Value;
                byte bit = (byte)((opcode >> 3) & 0x07);
                BitTest(register.ReadFrom(Registers), bit, ref Registers.F);
                return 8;
            }

            public uint BitBHl(byte opcode, (IndexRegister Index, sbyte Offset)? index)
            {
                ushort address = index.HasValue
                    ? ComputeIndexAddress(Registers, index.Value.Index, index.Value.Offset)
                    : Register16.HL.ReadFrom(Registers);
                byte value = Bus.ReadMemory(address);
                byte bit = (byte)((opcode >> 3) & 0x07);
                BitTest(value, bit, ref Registers.F);
                return index.HasValue ? 16u : 12u;
            }

            private static ushort ComputeIndexAddress(Registers registers, IndexRegister index, sbyte offset)
            {
                ushort indexValue = index.ReadFrom(registers);
                return (ushort)(indexValue + offset);
            }
        }

        private static void SetShiftFlags(ref Flags flags, byte value, bool carry)
        {
            flags = new Flags
            {
                Sign = value.Bit(7),
                Zero = value == 0,
                HalfCarry = false,
                Overflow = (value.BitCount() & 1) == 0,
                Subtract = false,
                Carry = carry,
                X = flags.X,
                Y = flags.Y,
            };
        }

        private static byte RotateLeft(byte value, bool thruCarry, ref Flags flags)
        {
            bool bit0 = thruCarry ? flags.Carry : value.Bit(7);
            byte rotated = (byte)((value << 1) | (bit0 ? 1 : 0));
            SetShiftFlags(ref flags, rotated, value.Bit(7));
            return rotated;
        }

        private static byte RotateRight(byte value, bool thruCarry, ref Flags flags)
        {
            bool bit7 = thruCarry ? flags.Carry : value.Bit(0);
            byte rotated = (byte)((value >> 1) | ((bit7 ? 1 : 0) << 7));
            SetShiftFlags(ref flags, rotated, value.Bit(0));
            return rotated;
        }

        private static byte ShiftLeftArithmetic(byte value, ref Flags flags)
        {
            byte shifted = (byte)(value << 1);
            SetShiftFlags(ref flags, shifted, value.Bit(7));
            return shifted;
        }

        private static byte ShiftLeftLogical(byte value, ref Flags flags)
        {
            byte shifted = (byte)((value << 1) | 0x01);
            SetShiftFlags(ref flags, shifted, value.Bit(7));
            return shifted;
        }

        private static byte ShiftRightArithmetic(byte value, ref Flags flags)
        {
            byte shifted = (byte)((value >> 1) | (value & 0x80));
            SetShiftFlags(ref flags, shifted, value.Bit(0));
            return shifted;
        }

        private static byte ShiftRightLogical(byte value, ref Flags flags)
        {
            byte shifted = (byte)(value >> 1);
            SetShiftFlags(ref flags, shifted, value.Bit(0));
            return shifted;
        }

        private static (byte, byte) RotateLeftDecimal(byte a, byte memoryValue, ref Flags flags)
        {
            byte newA = (byte)((a & 0xF0) | (memoryValue >> 4));
            byte newMem = (byte)((memoryValue << 4) | (a & 0x0F));
            SetShiftFlags(ref flags, newA, flags.Carry);
            return (newA, newMem);
        }

        private static (byte, byte) RotateRightDecimal(byte a, byte memoryValue, ref Flags flags)
        {
            byte newA = (byte)((a & 0xF0) | (memoryValue & 0x0F));
            byte newMem = (byte)((memoryValue >> 4) | (a << 4));
            SetShiftFlags(ref flags, newA, flags.Carry);
            return (newA, newMem);
        }

        private static void BitTest(byte value, byte bit, ref Flags flags)
        {
            bool zero = (value & (1 << bit)) == 0;
            flags = new Flags
            {
                Zero = zero,
                HalfCarry = true,
                Subtract = false,
                Overflow = zero,
                Sign = bit == 7 && !zero,
                Carry = flags.Carry,
                X = flags.X,
                Y = flags.Y,
            };
        }

        private static byte SetBit(byte value, byte bit) => (byte)(value | (1 << bit));
        private static byte ResetBit(byte value, byte bit) => (byte)(value & ~(1 << bit));
    }
}
