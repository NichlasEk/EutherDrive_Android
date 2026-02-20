using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static partial class Instructions
    {
        internal sealed partial class InstructionExecutor
        {
            public uint LdRR(byte opcode, IndexRegister? index)
            {
                Register8 writeTarget = ParseRegisterFromOpcode((byte)(opcode >> 3), index)!.Value;
                Register8 readTarget = ParseRegisterFromOpcode(opcode, index)!.Value;
                byte value = readTarget.ReadFrom(Registers);
                writeTarget.WriteTo(value, Registers);
                return 4;
            }

            public uint LdRImmediate(byte opcode, IndexRegister? index)
            {
                Register8 writeTarget = ParseRegisterFromOpcode((byte)(opcode >> 3), index)!.Value;
                byte value = FetchOperand();
                writeTarget.WriteTo(value, Registers);
                return 7;
            }

            public uint LdRHl(byte opcode, IndexRegister? index)
            {
                Register8 writeTarget = ParseRegisterFromOpcode((byte)(opcode >> 3), null)!.Value;
                ushort address = FetchIndirectHlAddress(index);
                byte value = Bus.ReadMemory(address);
                writeTarget.WriteTo(value, Registers);
                return index.HasValue ? 15u : 7u;
            }

            public uint LdHlR(byte opcode, IndexRegister? index)
            {
                Register8 readTarget = ParseRegisterFromOpcode(opcode, null)!.Value;
                byte value = readTarget.ReadFrom(Registers);
                ushort address = FetchIndirectHlAddress(index);
                Bus.WriteMemory(address, value);
                return index.HasValue ? 15u : 7u;
            }

            public uint LdHlImmediate(IndexRegister? index)
            {
                ushort address = FetchIndirectHlAddress(index);
                byte value = FetchOperand();
                Bus.WriteMemory(address, value);
                return index.HasValue ? 15u : 10u;
            }

            public uint LdAIndirect(Register16 register)
            {
                ushort address = register.ReadFrom(Registers);
                byte value = Bus.ReadMemory(address);
                Registers.A = value;
                return 7;
            }

            public uint LdADirect()
            {
                ushort address = FetchOperandU16();
                byte value = Bus.ReadMemory(address);
                Registers.A = value;
                return 13;
            }

            public uint LdIndirectA(Register16 register)
            {
                ushort address = register.ReadFrom(Registers);
                Bus.WriteMemory(address, Registers.A);
                return 7;
            }

            public uint LdDirectA()
            {
                ushort address = FetchOperandU16();
                Bus.WriteMemory(address, Registers.A);
                return 13;
            }

            public uint LdAIr(Register8 register)
            {
                byte value = register.ReadFrom(Registers);
                Registers.A = value;

                Flags f = Registers.F;
                f.Sign = value.Bit(7);
                f.Zero = value == 0;
                f.HalfCarry = false;
                f.Overflow = Registers.Iff2;
                f.Subtract = false;
                Registers.F = f;

                return 9;
            }

            public uint LdIrA(Register8 register)
            {
                register.WriteTo(Registers.A, Registers);
                return 9;
            }

            public uint LdDdImmediate(byte opcode, IndexRegister? index)
            {
                Register16 register = ParseDdRegister(opcode, index);
                ushort value = FetchOperandU16();
                register.WriteTo(value, Registers);
                return 10;
            }

            public uint LdHlDirect(IndexRegister? index)
            {
                ushort address = FetchOperandU16();
                ushort value = ReadMemoryU16(address);
                Register16 register = index.HasValue ? index.Value.ToRegister16() : Register16.HL;
                register.WriteTo(value, Registers);
                return 16;
            }

            public uint LdDdDirect(byte opcode)
            {
                Register16 register = ParseDdRegister(opcode, null);
                ushort address = FetchOperandU16();
                ushort value = ReadMemoryU16(address);
                register.WriteTo(value, Registers);
                return 20;
            }

            public uint LdDirectHl(IndexRegister? index)
            {
                Register16 register = index.HasValue ? index.Value.ToRegister16() : Register16.HL;
                ushort value = register.ReadFrom(Registers);
                ushort address = FetchOperandU16();
                WriteMemoryU16(address, value);
                return 16;
            }

            public uint LdDirectDd(byte opcode)
            {
                Register16 register = ParseDdRegister(opcode, null);
                ushort value = register.ReadFrom(Registers);
                ushort address = FetchOperandU16();
                WriteMemoryU16(address, value);
                return 20;
            }

            public uint LdSpHl(IndexRegister? index)
            {
                Register16 register = index.HasValue ? index.Value.ToRegister16() : Register16.HL;
                ushort value = register.ReadFrom(Registers);
                Registers.Sp = value;
                return 6;
            }

            public uint PushQq(byte opcode, IndexRegister? index)
            {
                Register16 register = ParseQqRegister(opcode, index);
                ushort value = register.ReadFrom(Registers);
                PushStack(value);
                return 11;
            }

            public uint PopQq(byte opcode, IndexRegister? index)
            {
                Register16 register = ParseQqRegister(opcode, index);
                ushort value = PopStack();
                register.WriteTo(value, Registers);
                return 10;
            }

            public uint ExchangeDeHl()
            {
                (Registers.D, Registers.H) = (Registers.H, Registers.D);
                (Registers.E, Registers.L) = (Registers.L, Registers.E);
                return 4;
            }

            public uint ExchangeAf()
            {
                (Registers.A, Registers.Ap) = (Registers.Ap, Registers.A);
                (Registers.F, Registers.Fp) = (Registers.Fp, Registers.F);
                return 4;
            }

            public uint ExchangeBcdehl()
            {
                (Registers.B, Registers.Bp) = (Registers.Bp, Registers.B);
                (Registers.C, Registers.Cp) = (Registers.Cp, Registers.C);
                (Registers.D, Registers.Dp) = (Registers.Dp, Registers.D);
                (Registers.E, Registers.Ep) = (Registers.Ep, Registers.E);
                (Registers.H, Registers.Hp) = (Registers.Hp, Registers.H);
                (Registers.L, Registers.Lp) = (Registers.Lp, Registers.L);
                return 4;
            }

            public uint ExchangeStackHl(IndexRegister? index)
            {
                Register16 register = index.HasValue ? index.Value.ToRegister16() : Register16.HL;
                ushort registerValue = register.ReadFrom(Registers);
                ushort stackValue = PopStack();
                register.WriteTo(stackValue, Registers);
                PushStack(registerValue);
                return 19;
            }

            public uint BlockTransfer(BlockMode mode, bool repeat)
            {
                ushort hl = Register16.HL.ReadFrom(Registers);
                ushort de = Register16.DE.ReadFrom(Registers);

                byte value = Bus.ReadMemory(hl);
                Bus.WriteMemory(de, value);

                ushort bc = Register16.BC.ReadFrom(Registers);
                Register16.BC.WriteTo((ushort)(bc - 1), Registers);

                Register16.HL.WriteTo(mode.Apply(hl), Registers);
                Register16.DE.WriteTo(mode.Apply(de), Registers);

                bool shouldRepeat = repeat && bc != 1;
                if (shouldRepeat)
                    Registers.Pc = (ushort)(Registers.Pc - 2);

                Flags f = Registers.F;
                f.HalfCarry = false;
                f.Overflow = bc != 1;
                f.Subtract = false;
                Registers.F = f;

                return shouldRepeat ? 21u : 16u;
            }
        }
    }
}
