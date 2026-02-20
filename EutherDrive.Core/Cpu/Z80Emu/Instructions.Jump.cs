using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static partial class Instructions
    {
        private enum JumpCondition
        {
            NonZero,
            Zero,
            NoCarry,
            Carry,
            ParityOdd,
            ParityEven,
            Positive,
            Negative,
        }

        private static JumpCondition JumpConditionFromOpcode(byte opcode)
        {
            return (opcode & 0x38) switch
            {
                0x00 => JumpCondition.NonZero,
                0x08 => JumpCondition.Zero,
                0x10 => JumpCondition.NoCarry,
                0x18 => JumpCondition.Carry,
                0x20 => JumpCondition.ParityOdd,
                0x28 => JumpCondition.ParityEven,
                0x30 => JumpCondition.Positive,
                0x38 => JumpCondition.Negative,
                _ => JumpCondition.NonZero,
            };
        }

        private static bool CheckJumpCondition(JumpCondition condition, Registers registers)
        {
            return condition switch
            {
                JumpCondition.NonZero => !registers.F.Zero,
                JumpCondition.Zero => registers.F.Zero,
                JumpCondition.NoCarry => !registers.F.Carry,
                JumpCondition.Carry => registers.F.Carry,
                JumpCondition.ParityOdd => !registers.F.Overflow,
                JumpCondition.ParityEven => registers.F.Overflow,
                JumpCondition.Positive => !registers.F.Sign,
                JumpCondition.Negative => registers.F.Sign,
                _ => false,
            };
        }

        internal sealed partial class InstructionExecutor
        {
            public uint JrE()
            {
                sbyte offset = unchecked((sbyte)FetchOperand());
                Registers.Pc = (ushort)(Registers.Pc + offset);
                return 12;
            }

            public uint JrCE()
            {
                sbyte offset = unchecked((sbyte)FetchOperand());
                if (Registers.F.Carry)
                {
                    Registers.Pc = (ushort)(Registers.Pc + offset);
                    return 12;
                }
                return 7;
            }

            public uint JrNcE()
            {
                sbyte offset = unchecked((sbyte)FetchOperand());
                if (!Registers.F.Carry)
                {
                    Registers.Pc = (ushort)(Registers.Pc + offset);
                    return 12;
                }
                return 7;
            }

            public uint JrZE()
            {
                sbyte offset = unchecked((sbyte)FetchOperand());
                if (Registers.F.Zero)
                {
                    Registers.Pc = (ushort)(Registers.Pc + offset);
                    return 12;
                }
                return 7;
            }

            public uint JrNzE()
            {
                sbyte offset = unchecked((sbyte)FetchOperand());
                if (!Registers.F.Zero)
                {
                    Registers.Pc = (ushort)(Registers.Pc + offset);
                    return 12;
                }
                return 7;
            }

            public uint JpNn()
            {
                ushort address = FetchOperandU16();
                Registers.Pc = address;
                return 10;
            }

            public uint JpCcNn(byte opcode)
            {
                JumpCondition condition = JumpConditionFromOpcode(opcode);
                ushort address = FetchOperandU16();
                if (CheckJumpCondition(condition, Registers))
                    Registers.Pc = address;
                return 10;
            }

            public uint JpHl(IndexRegister? index)
            {
                Register16 register = index.HasValue ? index.Value.ToRegister16() : Register16.HL;
                ushort address = register.ReadFrom(Registers);
                Registers.Pc = address;
                return 4;
            }

            public uint DjnzE()
            {
                sbyte offset = unchecked((sbyte)FetchOperand());
                byte b = Registers.B;
                Registers.B = (byte)(b - 1);
                if (b != 1)
                {
                    Registers.Pc = (ushort)(Registers.Pc + offset);
                    return 13;
                }
                return 8;
            }

            public uint CallNn()
            {
                ushort address = FetchOperandU16();
                PushStack(Registers.Pc);
                Registers.Pc = address;
                return 17;
            }

            public uint CallCcNn(byte opcode)
            {
                JumpCondition condition = JumpConditionFromOpcode(opcode);
                ushort address = FetchOperandU16();
                if (CheckJumpCondition(condition, Registers))
                {
                    PushStack(Registers.Pc);
                    Registers.Pc = address;
                    return 17;
                }
                return 10;
            }

            public uint Ret()
            {
                Registers.Pc = PopStack();
                return 10;
            }

            public uint RetCc(byte opcode)
            {
                JumpCondition condition = JumpConditionFromOpcode(opcode);
                if (CheckJumpCondition(condition, Registers))
                {
                    Registers.Pc = PopStack();
                    return 11;
                }
                return 5;
            }

            public uint RetiRetn()
            {
                Registers.Pc = PopStack();
                Registers.Iff1 = Registers.Iff2;
                return 14;
            }

            public uint RstP(byte opcode)
            {
                byte address = (byte)(opcode & 0x38);
                PushStack(Registers.Pc);
                Registers.Pc = address;
                return 11;
            }
        }
    }
}
