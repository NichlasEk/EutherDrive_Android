using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static partial class Instructions
    {
        internal sealed partial class InstructionExecutor
        {
            public uint AddAR(byte opcode, IndexRegister? index, bool withCarry)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte operand = readTarget.ReadFrom(Registers);
                Registers.A = Add(Registers.A, operand, withCarry, ref Registers.F);
                return 4;
            }

            public uint AddAImmediate(bool withCarry)
            {
                byte operand = FetchOperand();
                Registers.A = Add(Registers.A, operand, withCarry, ref Registers.F);
                return 7;
            }

            public uint AddAHl(IndexRegister? index, bool withCarry)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte operand = Bus.ReadMemory(address);
                Registers.A = Add(Registers.A, operand, withCarry, ref Registers.F);
                return index.HasValue ? 15u : 7u;
            }

            public uint SubAR(byte opcode, IndexRegister? index, bool withCarry)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte operand = readTarget.ReadFrom(Registers);
                Registers.A = Subtract(Registers.A, operand, withCarry, ref Registers.F);
                return 4;
            }

            public uint SubAImmediate(bool withCarry)
            {
                byte operand = FetchOperand();
                Registers.A = Subtract(Registers.A, operand, withCarry, ref Registers.F);
                return 7;
            }

            public uint SubAHl(IndexRegister? index, bool withCarry)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte operand = Bus.ReadMemory(address);
                Registers.A = Subtract(Registers.A, operand, withCarry, ref Registers.F);
                return index.HasValue ? 15u : 7u;
            }

            public uint AndAR(byte opcode, IndexRegister? index)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte operand = readTarget.ReadFrom(Registers);
                Registers.A = And(Registers.A, operand, ref Registers.F);
                return 4;
            }

            public uint AndAImmediate()
            {
                byte operand = FetchOperand();
                Registers.A = And(Registers.A, operand, ref Registers.F);
                return 7;
            }

            public uint AndAHl(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte operand = Bus.ReadMemory(address);
                Registers.A = And(Registers.A, operand, ref Registers.F);
                return index.HasValue ? 15u : 7u;
            }

            public uint OrAR(byte opcode, IndexRegister? index)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte operand = readTarget.ReadFrom(Registers);
                Registers.A = Or(Registers.A, operand, ref Registers.F);
                return 4;
            }

            public uint OrAImmediate()
            {
                byte operand = FetchOperand();
                Registers.A = Or(Registers.A, operand, ref Registers.F);
                return 7;
            }

            public uint OrAHl(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte operand = Bus.ReadMemory(address);
                Registers.A = Or(Registers.A, operand, ref Registers.F);
                return index.HasValue ? 15u : 7u;
            }

            public uint XorAR(byte opcode, IndexRegister? index)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte operand = readTarget.ReadFrom(Registers);
                Registers.A = Xor(Registers.A, operand, ref Registers.F);
                return 4;
            }

            public uint XorAImmediate()
            {
                byte operand = FetchOperand();
                Registers.A = Xor(Registers.A, operand, ref Registers.F);
                return 7;
            }

            public uint XorAHl(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte operand = Bus.ReadMemory(address);
                Registers.A = Xor(Registers.A, operand, ref Registers.F);
                return index.HasValue ? 15u : 7u;
            }

            public uint CpAR(byte opcode, IndexRegister? index)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte operand = readTarget.ReadFrom(Registers);
                Registers.A = Compare(Registers.A, operand, ref Registers.F);
                return 4;
            }

            public uint CpAImmediate()
            {
                byte operand = FetchOperand();
                Registers.A = Compare(Registers.A, operand, ref Registers.F);
                return 7;
            }

            public uint CpAHl(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte operand = Bus.ReadMemory(address);
                Registers.A = Compare(Registers.A, operand, ref Registers.F);
                return index.HasValue ? 15u : 7u;
            }

            public uint IncR(byte opcode, IndexRegister? index)
            {
                Register8 register = ParseRegisterFromOpcode((byte)(opcode >> 3), index)!.Value;
                byte original = register.ReadFrom(Registers);
                byte modified = Increment(original, ref Registers.F);
                register.WriteTo(modified, Registers);
                return 4;
            }

            public uint IncHl(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte original = Bus.ReadMemory(address);
                byte modified = Increment(original, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 11u;
            }

            public uint DecR(byte opcode, IndexRegister? index)
            {
                Register8 register = ParseRegisterFromOpcode((byte)(opcode >> 3), index)!.Value;
                byte original = register.ReadFrom(Registers);
                byte modified = Decrement(original, ref Registers.F);
                register.WriteTo(modified, Registers);
                return 4;
            }

            public uint DecHl(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte original = Bus.ReadMemory(address);
                byte modified = Decrement(original, ref Registers.F);
                Bus.WriteMemory(address, modified);
                return index.HasValue ? 19u : 11u;
            }

            public uint IncSs(byte opcode, IndexRegister? index)
            {
                Register16 register = ParseDdRegister(opcode, index);
                ushort original = register.ReadFrom(Registers);
                ushort modified = (ushort)(original + 1);
                register.WriteTo(modified, Registers);
                return 6;
            }

            public uint DecSs(byte opcode, IndexRegister? index)
            {
                Register16 register = ParseDdRegister(opcode, index);
                ushort original = register.ReadFrom(Registers);
                ushort modified = (ushort)(original - 1);
                register.WriteTo(modified, Registers);
                return 6;
            }

            public uint AddHlSs(byte opcode, IndexRegister? index)
            {
                Register16 lRegister = index.HasValue ? index.Value.ToRegister16() : Register16.HL;
                Register16 rRegister = ParseDdRegister(opcode, index);

                ushort lValue = lRegister.ReadFrom(Registers);
                ushort rValue = rRegister.ReadFrom(Registers);

                ushort sum = AddU16(lValue, rValue, false, ref Registers.F);
                lRegister.WriteTo(sum, Registers);

                return 11;
            }

            public uint AdcHlSs(byte opcode)
            {
                Register16 register = ParseDdRegister(opcode, null);

                ushort lValue = Register16.HL.ReadFrom(Registers);
                ushort rValue = register.ReadFrom(Registers);

                ushort sum = AddU16(lValue, rValue, true, ref Registers.F);
                Register16.HL.WriteTo(sum, Registers);

                return 15;
            }

            public uint SbcHlSs(byte opcode)
            {
                Register16 register = ParseDdRegister(opcode, null);

                ushort lValue = Register16.HL.ReadFrom(Registers);
                ushort rValue = register.ReadFrom(Registers);

                ushort difference = SbcU16(lValue, rValue, ref Registers.F);
                Register16.HL.WriteTo(difference, Registers);

                return 15;
            }

            public uint Daa()
            {
                byte a = Registers.A;
                Flags flags = Registers.F;

                int diff = 0;
                if (flags.HalfCarry || (a & 0x0F) > 0x09)
                {
                    diff |= 0x06;
                }

                bool carry = flags.Carry || a > 0x99;
                if (carry)
                {
                    diff |= 0x60;
                }

                byte value = flags.Subtract ? (byte)(a - diff) : (byte)(a + diff);

                bool halfCarry = a.Bit(4) != value.Bit(4);

                Registers.A = value;
                Registers.F = new Flags
                {
                    Sign = SignFlag(value),
                    Zero = ZeroFlag(value),
                    HalfCarry = halfCarry,
                    Overflow = ParityFlag(value),
                    Carry = carry,
                    Y = flags.Y,
                    X = flags.X,
                    Subtract = flags.Subtract,
                };

                return 4;
            }

            public uint Cpl()
            {
                Registers.A = (byte)~Registers.A;
                Flags f = Registers.F;
                f.HalfCarry = true;
                f.Subtract = true;
                Registers.F = f;
                return 4;
            }

            public uint Neg()
            {
                Registers.A = Subtract(0, Registers.A, false, ref Registers.F);
                return 8;
            }

            public uint Ccf()
            {
                bool prevCarry = Registers.F.Carry;
                Flags f = Registers.F;
                f.HalfCarry = prevCarry;
                f.Subtract = false;
                f.Carry = !prevCarry;
                Registers.F = f;
                return 4;
            }

            public uint Scf()
            {
                Flags f = Registers.F;
                f.HalfCarry = false;
                f.Subtract = false;
                f.Carry = true;
                Registers.F = f;
                return 4;
            }

            public uint CompareBlock(BlockMode mode, bool repeat)
            {
                byte a = Registers.A;
                ushort bc = Register16.BC.ReadFrom(Registers);
                ushort hl = Register16.HL.ReadFrom(Registers);
                byte operand = Bus.ReadMemory(hl);

                byte difference = (byte)(a - operand);
                bool halfCarry = (a & 0x0F) < (operand & 0x0F);

                Register16.HL.WriteTo(mode.Apply(hl), Registers);
                Register16.BC.WriteTo((ushort)(bc - 1), Registers);

                Flags f = Registers.F;
                f.Sign = SignFlag(difference);
                f.Zero = ZeroFlag(difference);
                f.HalfCarry = halfCarry;
                f.Overflow = bc != 1;
                f.Subtract = true;
                Registers.F = f;

                bool shouldRepeat = repeat && difference != 0 && bc != 1;
                if (shouldRepeat)
                {
                    Registers.Pc -= 2;
                    return 21;
                }

                return 16;
            }
        }

        private static bool SignFlag(byte value) => value.Bit(7);
        private static bool ZeroFlag(byte value) => value == 0;
        private static bool ParityFlag(byte value) => (value.BitCount() & 1) == 0;

        private static byte Add(byte l, byte r, bool withCarry, ref Flags flags)
        {
            byte carryOperand = withCarry && flags.Carry ? (byte)1 : (byte)0;

            byte sum;
            bool carry;
            byte temp = (byte)(l + r);
            if (temp < l)
            {
                sum = (byte)(temp + carryOperand);
                carry = true;
            }
            else
            {
                sum = (byte)(temp + carryOperand);
                carry = (carryOperand == 1) && sum == 0;
            }

            bool halfCarry = (l & 0x0F) + (r & 0x0F) + carryOperand >= 0x10;
            bool bit6Carry = (l & 0x7F) + (r & 0x7F) + carryOperand >= 0x80;
            bool overflow = bit6Carry != carry;

            flags = new Flags
            {
                Sign = SignFlag(sum),
                Zero = ZeroFlag(sum),
                HalfCarry = halfCarry,
                Overflow = overflow,
                Subtract = false,
                Carry = carry,
                X = flags.X,
                Y = flags.Y,
            };

            return sum;
        }

        private static ushort AddU16(ushort l, ushort r, bool withCarry, ref Flags flags)
        {
            ushort carryOperand = withCarry && flags.Carry ? (ushort)1 : (ushort)0;

            ushort sum;
            bool carry;
            ushort temp = (ushort)(l + r);
            if (temp < l)
            {
                sum = (ushort)(temp + carryOperand);
                carry = true;
            }
            else
            {
                sum = (ushort)(temp + carryOperand);
                carry = (carryOperand == 1) && sum == 0;
            }

            bool halfCarry = (l & 0x0FFF) + (r & 0x0FFF) + carryOperand >= 0x1000;
            Flags next = flags;
            next.HalfCarry = halfCarry;
            next.Subtract = false;
            next.Carry = carry;

            if (withCarry)
            {
                bool bit14Carry = (l & 0x7FFF) + (r & 0x7FFF) + carryOperand >= 0x8000;
                bool overflow = bit14Carry != carry;
                next.Sign = sum.Bit(15);
                next.Zero = sum == 0;
                next.Overflow = overflow;
            }

            flags = next;
            return sum;
        }

        private static byte Subtract(byte l, byte r, bool withCarry, ref Flags flags)
        {
            byte carryOperand = withCarry && flags.Carry ? (byte)1 : (byte)0;

            byte diff;
            bool carry;
            byte temp = (byte)(l - r);
            if (l < r)
            {
                diff = (byte)(temp - carryOperand);
                carry = true;
            }
            else
            {
                diff = (byte)(temp - carryOperand);
                carry = (carryOperand == 1) && diff == 0xFF;
            }

            bool halfCarry = (l & 0x0F) < ((r & 0x0F) + carryOperand);
            bool bit6Borrow = (l & 0x7F) < ((r & 0x7F) + carryOperand);
            bool overflow = bit6Borrow != carry;

            flags = new Flags
            {
                Sign = SignFlag(diff),
                Zero = ZeroFlag(diff),
                HalfCarry = halfCarry,
                Overflow = overflow,
                Subtract = true,
                Carry = carry,
                X = flags.X,
                Y = flags.Y,
            };

            return diff;
        }

        private static ushort SbcU16(ushort l, ushort r, ref Flags flags)
        {
            ushort carryOperand = flags.Carry ? (ushort)1 : (ushort)0;

            ushort diff;
            bool carry;
            ushort temp = (ushort)(l - r);
            if (l < r)
            {
                diff = (ushort)(temp - carryOperand);
                carry = true;
            }
            else
            {
                diff = (ushort)(temp - carryOperand);
                carry = (carryOperand == 1) && diff == 0xFFFF;
            }

            bool halfCarry = (l & 0x0FFF) < ((r & 0x0FFF) + carryOperand);
            bool bit14Borrow = (l & 0x7FFF) < ((r & 0x7FFF) + carryOperand);
            bool overflow = bit14Borrow != carry;

            flags = new Flags
            {
                Sign = diff.Bit(15),
                Zero = diff == 0,
                HalfCarry = halfCarry,
                Overflow = overflow,
                Subtract = true,
                Carry = carry,
                X = flags.X,
                Y = flags.Y,
            };

            return diff;
        }

        private static byte And(byte l, byte r, ref Flags flags)
        {
            byte value = (byte)(l & r);
            flags = new Flags
            {
                Sign = SignFlag(value),
                Zero = ZeroFlag(value),
                HalfCarry = true,
                Overflow = ParityFlag(value),
                Subtract = false,
                Carry = false,
                X = flags.X,
                Y = flags.Y,
            };
            return value;
        }

        private static byte Or(byte l, byte r, ref Flags flags)
        {
            byte value = (byte)(l | r);
            flags = new Flags
            {
                Sign = SignFlag(value),
                Zero = ZeroFlag(value),
                HalfCarry = false,
                Overflow = ParityFlag(value),
                Subtract = false,
                Carry = false,
                X = flags.X,
                Y = flags.Y,
            };
            return value;
        }

        private static byte Xor(byte l, byte r, ref Flags flags)
        {
            byte value = (byte)(l ^ r);
            flags = new Flags
            {
                Sign = SignFlag(value),
                Zero = ZeroFlag(value),
                HalfCarry = false,
                Overflow = ParityFlag(value),
                Subtract = false,
                Carry = false,
                X = flags.X,
                Y = flags.Y,
            };
            return value;
        }

        private static byte Compare(byte l, byte r, ref Flags flags)
        {
            byte diff = (byte)(l - r);
            bool carry = l < r;
            bool halfCarry = (l & 0x0F) < (r & 0x0F);
            bool bit6Borrow = (l & 0x7F) < (r & 0x7F);
            bool overflow = bit6Borrow != carry;

            flags = new Flags
            {
                Sign = SignFlag(diff),
                Zero = ZeroFlag(diff),
                HalfCarry = halfCarry,
                Overflow = overflow,
                Subtract = true,
                Carry = carry,
                X = flags.X,
                Y = flags.Y,
            };

            return l;
        }

        private static byte Increment(byte value, ref Flags flags)
        {
            bool halfCarry = (value & 0x0F) == 0x0F;
            bool overflow = value == 0x7F;
            byte incremented = (byte)(value + 1);
            flags = new Flags
            {
                Sign = SignFlag(incremented),
                Zero = ZeroFlag(incremented),
                HalfCarry = halfCarry,
                Overflow = overflow,
                Subtract = false,
                Carry = flags.Carry,
                X = flags.X,
                Y = flags.Y,
            };
            return incremented;
        }

        private static byte Decrement(byte value, ref Flags flags)
        {
            bool halfCarry = (value & 0x0F) == 0x00;
            bool overflow = value == 0x80;
            byte decremented = (byte)(value - 1);
            flags = new Flags
            {
                Sign = SignFlag(decremented),
                Zero = ZeroFlag(decremented),
                HalfCarry = halfCarry,
                Overflow = overflow,
                Subtract = true,
                Carry = flags.Carry,
                X = flags.X,
                Y = flags.Y,
            };
            return decremented;
        }

        internal static Register8? ParseRegisterFromOpcode(byte opcode, IndexRegister? index)
        {
            return (opcode & 0x07) switch
            {
                0x00 => Register8.B,
                0x01 => Register8.C,
                0x02 => Register8.D,
                0x03 => Register8.E,
                0x04 => index.HasValue ? index.Value.HighByte() : Register8.H,
                0x05 => index.HasValue ? index.Value.LowByte() : Register8.L,
                0x06 => null,
                0x07 => Register8.A,
                _ => null,
            };
        }

        internal static Register16 ParseDdRegister(byte opcode, IndexRegister? index)
        {
            return (opcode & 0x30) switch
            {
                0x00 => Register16.BC,
                0x10 => Register16.DE,
                0x20 => index.HasValue ? index.Value.ToRegister16() : Register16.HL,
                0x30 => Register16.SP,
                _ => Register16.BC,
            };
        }

        internal static Register16 ParseQqRegister(byte opcode, IndexRegister? index)
        {
            return (opcode & 0x30) switch
            {
                0x00 => Register16.BC,
                0x10 => Register16.DE,
                0x20 => index.HasValue ? index.Value.ToRegister16() : Register16.HL,
                0x30 => Register16.AF,
                _ => Register16.BC,
            };
        }
    }
}
