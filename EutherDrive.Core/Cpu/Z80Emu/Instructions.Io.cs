using System;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal static partial class Instructions
    {
        internal sealed partial class InstructionExecutor
        {
            public uint InAN()
            {
                byte operand = FetchOperand();
                ushort ioAddress = (ushort)((Registers.A << 8) | operand);
                Registers.A = Bus.ReadIo(ioAddress);
                return 11;
            }

            public uint InRC(byte opcode)
            {
                Register8? register = ParseRegisterFromOpcode((byte)(opcode >> 3), null);
                ushort ioAddress = (ushort)((Registers.B << 8) | Registers.C);
                byte value = Bus.ReadIo(ioAddress);

                if (register.HasValue)
                    register.Value.WriteTo(value, Registers);

                Flags f = Registers.F;
                f.Sign = value.Bit(7);
                f.Zero = value == 0;
                f.HalfCarry = false;
                f.Overflow = (value.BitCount() & 1) == 0;
                f.Subtract = false;
                Registers.F = f;

                return 12;
            }

            public uint InBlock(BlockMode mode, bool repeat)
            {
                byte b = Registers.B;
                ushort ioAddress = (ushort)((b << 8) | Registers.C);
                byte value = Bus.ReadIo(ioAddress);

                ushort hl = Register16.HL.ReadFrom(Registers);
                Bus.WriteMemory(hl, value);

                Registers.B = (byte)(b - 1);
                Register16.HL.WriteTo(mode.Apply(hl), Registers);

                bool shouldRepeat = repeat && b != 1;
                if (shouldRepeat)
                    Registers.Pc -= 2;

                Flags f = Registers.F;
                f.Zero = repeat || b == 1;
                f.Subtract = true;
                Registers.F = f;

                return shouldRepeat ? 21u : 16u;
            }

            public uint OutNA()
            {
                byte operand = FetchOperand();
                ushort ioAddress = (ushort)((Registers.A << 8) | operand);
                Bus.WriteIo(ioAddress, Registers.A);
                return 11;
            }

            public uint OutCR(byte opcode)
            {
                Register8? register = ParseRegisterFromOpcode((byte)(opcode >> 3), null);
                ushort ioAddress = (ushort)((Registers.B << 8) | Registers.C);
                byte value = register.HasValue ? register.Value.ReadFrom(Registers) : (byte)0;
                Bus.WriteIo(ioAddress, value);
                return 12;
            }

            public uint OutBlock(BlockMode mode, bool repeat)
            {
                ushort hl = Register16.HL.ReadFrom(Registers);
                byte value = Bus.ReadMemory(hl);

                byte b = Registers.B;
                Registers.B = (byte)(b - 1);

                ushort ioAddress = (ushort)((Registers.B << 8) | Registers.C);
                Bus.WriteIo(ioAddress, value);

                Register16.HL.WriteTo(mode.Apply(hl), Registers);

                bool shouldRepeat = repeat && b != 1;
                if (shouldRepeat)
                    Registers.Pc -= 2;

                Flags f = Registers.F;
                f.Zero = repeat || b == 1;
                f.Subtract = true;
                Registers.F = f;

                return shouldRepeat ? 21u : 16u;
            }
        }
    }
}
