using System;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Cpu.Z80Emu
{
    internal struct Flags
    {
        public bool Sign;
        public bool Zero;
        public bool Y;
        public bool HalfCarry;
        public bool X;
        public bool Overflow;
        public bool Subtract;
        public bool Carry;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ToByte()
        {
            return (byte)((Sign ? 0x80 : 0)
                | (Zero ? 0x40 : 0)
                | (Y ? 0x20 : 0)
                | (HalfCarry ? 0x10 : 0)
                | (X ? 0x08 : 0)
                | (Overflow ? 0x04 : 0)
                | (Subtract ? 0x02 : 0)
                | (Carry ? 0x01 : 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Flags FromByte(byte value)
        {
            return new Flags
            {
                Sign = value.Bit(7),
                Zero = value.Bit(6),
                Y = value.Bit(5),
                HalfCarry = value.Bit(4),
                X = value.Bit(3),
                Overflow = value.Bit(2),
                Subtract = value.Bit(1),
                Carry = value.Bit(0),
            };
        }
    }

    public enum InterruptMode : byte
    {
        Mode0 = 0,
        Mode1 = 1,
        Mode2 = 2,
    }

    internal sealed class Registers
    {
        public byte A;
        public Flags F;
        public byte B;
        public byte C;
        public byte D;
        public byte E;
        public byte H;
        public byte L;
        public byte Ap;
        public Flags Fp;
        public byte Bp;
        public byte Cp;
        public byte Dp;
        public byte Ep;
        public byte Hp;
        public byte Lp;
        public byte I;
        public byte R;
        public ushort Ix;
        public ushort Iy;
        public ushort Sp;
        public ushort Pc;
        public bool Iff1;
        public bool Iff2;
        public InterruptMode InterruptMode;
        public bool InterruptDelay;
        public InterruptLine LastNmi;
        public bool Halted;

        public Registers()
        {
            A = 0xFF;
            F = Flags.FromByte(0xFF);
            B = 0xFF;
            C = 0xFF;
            D = 0xFF;
            E = 0xFF;
            H = 0xFF;
            L = 0xFF;
            Ap = 0xFF;
            Fp = Flags.FromByte(0xFF);
            Bp = 0xFF;
            Cp = 0xFF;
            Dp = 0xFF;
            Ep = 0xFF;
            Hp = 0xFF;
            Lp = 0xFF;
            I = 0xFF;
            R = 0xFF;
            Ix = 0xFFFF;
            Iy = 0xFFFF;
            Sp = 0xFFFF;
            Pc = 0x0000;
            Iff1 = false;
            Iff2 = false;
            InterruptMode = InterruptMode.Mode0;
            InterruptDelay = false;
            LastNmi = InterruptLine.High;
            Halted = false;
        }
    }

    internal enum Register8
    {
        A,
        B,
        C,
        D,
        E,
        H,
        L,
        I,
        R,
        IXHigh,
        IXLow,
        IYHigh,
        IYLow,
    }

    internal static class Register8Extensions
    {
        public static byte ReadFrom(this Register8 reg, Registers registers)
        {
            return reg switch
            {
                Register8.A => registers.A,
                Register8.B => registers.B,
                Register8.C => registers.C,
                Register8.D => registers.D,
                Register8.E => registers.E,
                Register8.H => registers.H,
                Register8.L => registers.L,
                Register8.I => registers.I,
                Register8.R => registers.R,
                Register8.IXHigh => registers.Ix.Msb(),
                Register8.IXLow => registers.Ix.Lsb(),
                Register8.IYHigh => registers.Iy.Msb(),
                Register8.IYLow => registers.Iy.Lsb(),
                _ => 0,
            };
        }

        public static void WriteTo(this Register8 reg, byte value, Registers registers)
        {
            switch (reg)
            {
                case Register8.A:
                    registers.A = value;
                    break;
                case Register8.B:
                    registers.B = value;
                    break;
                case Register8.C:
                    registers.C = value;
                    break;
                case Register8.D:
                    registers.D = value;
                    break;
                case Register8.E:
                    registers.E = value;
                    break;
                case Register8.H:
                    registers.H = value;
                    break;
                case Register8.L:
                    registers.L = value;
                    break;
                case Register8.I:
                    registers.I = value;
                    break;
                case Register8.R:
                    registers.R = value;
                    break;
                case Register8.IXHigh:
                    registers.Ix = registers.Ix.WithMsb(value);
                    break;
                case Register8.IXLow:
                    registers.Ix = registers.Ix.WithLsb(value);
                    break;
                case Register8.IYHigh:
                    registers.Iy = registers.Iy.WithMsb(value);
                    break;
                case Register8.IYLow:
                    registers.Iy = registers.Iy.WithLsb(value);
                    break;
            }
        }
    }

    internal enum Register16
    {
        AF,
        BC,
        DE,
        HL,
        IX,
        IY,
        SP,
    }

    internal static class Register16Extensions
    {
        public static ushort ReadFrom(this Register16 reg, Registers registers)
        {
            return reg switch
            {
                Register16.AF => (ushort)((registers.A << 8) | registers.F.ToByte()),
                Register16.BC => (ushort)((registers.B << 8) | registers.C),
                Register16.DE => (ushort)((registers.D << 8) | registers.E),
                Register16.HL => (ushort)((registers.H << 8) | registers.L),
                Register16.IX => registers.Ix,
                Register16.IY => registers.Iy,
                Register16.SP => registers.Sp,
                _ => 0,
            };
        }

        public static void WriteTo(this Register16 reg, ushort value, Registers registers)
        {
            switch (reg)
            {
                case Register16.AF:
                    registers.A = (byte)(value >> 8);
                    registers.F = Flags.FromByte((byte)value);
                    break;
                case Register16.BC:
                    registers.B = (byte)(value >> 8);
                    registers.C = (byte)value;
                    break;
                case Register16.DE:
                    registers.D = (byte)(value >> 8);
                    registers.E = (byte)value;
                    break;
                case Register16.HL:
                    registers.H = (byte)(value >> 8);
                    registers.L = (byte)value;
                    break;
                case Register16.IX:
                    registers.Ix = value;
                    break;
                case Register16.IY:
                    registers.Iy = value;
                    break;
                case Register16.SP:
                    registers.Sp = value;
                    break;
            }
        }
    }

    internal enum IndexRegister
    {
        IX,
        IY,
    }

    internal static class IndexRegisterExtensions
    {
        public static ushort ReadFrom(this IndexRegister index, Registers registers)
        {
            return index == IndexRegister.IX ? registers.Ix : registers.Iy;
        }

        public static Register8 HighByte(this IndexRegister index) => index == IndexRegister.IX ? Register8.IXHigh : Register8.IYHigh;
        public static Register8 LowByte(this IndexRegister index) => index == IndexRegister.IX ? Register8.IXLow : Register8.IYLow;
        public static Register16 ToRegister16(this IndexRegister index) => index == IndexRegister.IX ? Register16.IX : Register16.IY;
    }

    public sealed class Z80
    {
        private readonly Registers _registers;
        private bool _stalled;
        private uint _tCyclesWait;

        public Z80()
        {
            _registers = new Registers();
            _stalled = false;
            _tCyclesWait = 0;
        }

        public ushort Pc => _registers.Pc;

        public void SetPc(ushort pc) => _registers.Pc = pc;
        public void SetSp(ushort sp) => _registers.Sp = sp;
        public void SetInterruptMode(InterruptMode mode) => _registers.InterruptMode = mode;

        public bool Stalled => _stalled;

        private const uint MinimumTCycles = 4;

        public uint ExecuteInstruction(IBusInterface bus)
        {
            if (bus.Reset())
            {
                _registers.I = 0;
                _registers.R = 0;
                _registers.Pc = 0;
                _registers.Iff1 = false;
                _registers.Iff2 = false;
                _registers.InterruptMode = InterruptMode.Mode0;
                _registers.InterruptDelay = false;
                _registers.LastNmi = InterruptLine.High;
                _registers.Halted = false;
                _stalled = false;
                return MinimumTCycles;
            }

            if (bus.BusReq())
            {
                _stalled = true;
                return MinimumTCycles;
            }

            _stalled = false;
            return Instructions.Execute(_registers, bus);
        }

        public void Tick(IBusInterface bus)
        {
            if (_tCyclesWait > 0)
            {
                _tCyclesWait -= 1;
            }
            else
            {
                _tCyclesWait = ExecuteInstruction(bus) - 1;
            }
        }

        internal Registers Registers => _registers;
    }
}
