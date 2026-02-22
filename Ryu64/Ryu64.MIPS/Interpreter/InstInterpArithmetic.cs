namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static ulong SignExtend32(uint value)
        {
            return unchecked((ulong)(long)(int)value);
        }

        private static void SetReg32(int reg, uint value)
        {
            Registers.R4300.Reg[reg] = SignExtend32(value);
        }

        private static uint Reg32(int reg)
        {
            return (uint)Registers.R4300.Reg[reg];
        }

        private static void SetLoHiFrom32(uint lo, uint hi)
        {
            Registers.R4300.LO = SignExtend32(lo);
            Registers.R4300.HI = SignExtend32(hi);
        }

        private static void MultiplyUnsigned64(ulong a, ulong b, out ulong hi, out ulong lo)
        {
            ulong aLo = (uint)a;
            ulong aHi = a >> 32;
            ulong bLo = (uint)b;
            ulong bHi = b >> 32;

            ulong p0 = aLo * bLo;
            ulong p1 = aHi * bLo;
            ulong p2 = aLo * bHi;
            ulong p3 = aHi * bHi;

            lo = p0;
            hi = p3 + (p1 >> 32) + (p2 >> 32);

            ulong add = p1 << 32;
            ulong prev = lo;
            lo += add;
            if (lo < prev)
                hi++;

            add = p2 << 32;
            prev = lo;
            lo += add;
            if (lo < prev)
                hi++;
        }

        public static void ADD(OpcodeTable.OpcodeDesc Desc)
        {
            // TODO: Correctly check for Overflow and Underflow
            ADDU(Desc);
        }

        public static void ADDI(OpcodeTable.OpcodeDesc Desc)
        {
            // TODO: Correctly check for Overflow and Underflow
            ADDIU(Desc);
        }

        public static void ADDIU(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op2, unchecked(Reg32(Desc.op1) + (uint)(int)(short)Desc.Imm));
            Registers.R4300.PC += 4;
        }

        public static void DADDI(OpcodeTable.OpcodeDesc Desc)
        {
            // TODO: Correctly check for overflow.
            DADDIU(Desc);
        }

        public static void DADDIU(OpcodeTable.OpcodeDesc Desc)
        {
            long lhs = (long)Registers.R4300.Reg[Desc.op1];
            long rhs = (short)Desc.Imm;
            Registers.R4300.Reg[Desc.op2] = (ulong)(lhs + rhs);
            Registers.R4300.PC += 4;
        }

        public static void ADDU(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, unchecked(Reg32(Desc.op1) + Reg32(Desc.op2)));
            Registers.R4300.PC += 4;
        }

        public static void DADD(OpcodeTable.OpcodeDesc Desc)
        {
            // TODO: Correctly check for Overflow and Underflow.
            DADDU(Desc);
        }

        public static void DADDU(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = (ulong)((long)Registers.R4300.Reg[Desc.op1] + (long)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void AND(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op1] & Registers.R4300.Reg[Desc.op2];
            Registers.R4300.PC += 4;
        }

        public static void NOR(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = ~(Registers.R4300.Reg[Desc.op1] | Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void ANDI(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op2] = Registers.R4300.Reg[Desc.op1] & Desc.Imm;
            Registers.R4300.PC += 4;
        }

        public static void SUB(OpcodeTable.OpcodeDesc Desc)
        {
            // TODO: Correctly check for underflow
            SUBU(Desc);
        }

        public static void SUBU(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, unchecked(Reg32(Desc.op1) - Reg32(Desc.op2)));
            Registers.R4300.PC += 4;
        }

        public static void DSUB(OpcodeTable.OpcodeDesc Desc)
        {
            // TODO: Correctly check for underflow.
            DSUBU(Desc);
        }

        public static void DSUBU(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = (ulong)((long)Registers.R4300.Reg[Desc.op1] - (long)Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void XOR(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op1] ^ Registers.R4300.Reg[Desc.op2];
            Registers.R4300.PC += 4;
        }

        public static void XORI(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op2] = Registers.R4300.Reg[Desc.op1] ^ Desc.Imm;
            Registers.R4300.PC += 4;
        }

        public static void LUI(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op2, (uint)(Desc.Imm << 16));
            Registers.R4300.PC += 4;
        }

        public static void MFHI(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.HI;
            Registers.R4300.PC += 4;
        }

        public static void MFLO(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.LO;
            Registers.R4300.PC += 4;
        }

        public static void OR(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op1] | Registers.R4300.Reg[Desc.op2];
            Registers.R4300.PC += 4;
        }

        public static void ORI(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op2] = Registers.R4300.Reg[Desc.op1] | Desc.Imm;
            Registers.R4300.PC += 4;
        }

        public static void MTLO(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.LO = Registers.R4300.Reg[Desc.op1];
            Registers.R4300.PC += 4;
        }

        public static void MTHI(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.HI = Registers.R4300.Reg[Desc.op1];
            Registers.R4300.PC += 4;
        }

        public static void MULT(OpcodeTable.OpcodeDesc Desc)
        {
            ulong Res = (ulong)((int)Registers.R4300.Reg[Desc.op1] * (int)Registers.R4300.Reg[Desc.op2]);
            SetLoHiFrom32((uint)(Res & 0x00000000FFFFFFFF), (uint)(Res >> 32));
            Registers.R4300.PC += 4;
        }

        public static void MULTU(OpcodeTable.OpcodeDesc Desc)
        {
            ulong Res = (uint)Registers.R4300.Reg[Desc.op1] * (uint)Registers.R4300.Reg[Desc.op2];
            SetLoHiFrom32((uint)(Res & 0x00000000FFFFFFFF), (uint)(Res >> 32));
            Registers.R4300.PC += 4;
        }

        public static void DMULTU(OpcodeTable.OpcodeDesc Desc)
        {
            MultiplyUnsigned64(Registers.R4300.Reg[Desc.op1], Registers.R4300.Reg[Desc.op2], out ulong hi, out ulong lo);
            Registers.R4300.HI = hi;
            Registers.R4300.LO = lo;
            Registers.R4300.PC += 4;
        }

        public static void DIV(OpcodeTable.OpcodeDesc Desc)
        {
            int divisor = (int)(uint)Registers.R4300.Reg[Desc.op2];
            if (divisor != 0)
            {
                int dividend = (int)(uint)Registers.R4300.Reg[Desc.op1];
                SetLoHiFrom32((uint)(dividend / divisor), (uint)(dividend % divisor));
            }

            Registers.R4300.PC += 4;
        }

        public static void DIVU(OpcodeTable.OpcodeDesc Desc)
        {
            uint divisor = (uint)Registers.R4300.Reg[Desc.op2];
            if (divisor != 0)
            {
                uint dividend = (uint)Registers.R4300.Reg[Desc.op1];
                SetLoHiFrom32(dividend / divisor, dividend % divisor);
            }

            Registers.R4300.PC += 4;
        }

        public static void DDIV(OpcodeTable.OpcodeDesc Desc)
        {
            long divisor = (long)Registers.R4300.Reg[Desc.op2];
            if (divisor != 0)
            {
                long dividend = (long)Registers.R4300.Reg[Desc.op1];
                Registers.R4300.LO = (ulong)(dividend / divisor);
                Registers.R4300.HI = (ulong)(dividend % divisor);
            }

            Registers.R4300.PC += 4;
        }

        public static void DDIVU(OpcodeTable.OpcodeDesc Desc)
        {
            ulong divisor = Registers.R4300.Reg[Desc.op2];
            if (divisor != 0)
            {
                ulong dividend = Registers.R4300.Reg[Desc.op1];
                Registers.R4300.LO = dividend / divisor;
                Registers.R4300.HI = dividend % divisor;
            }

            Registers.R4300.PC += 4;
        }

        public static void SLL(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, Reg32(Desc.op2) << Desc.op4);
            Registers.R4300.PC += 4;
        }

        public static void SLLV(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, Reg32(Desc.op2) << (byte)(Registers.R4300.Reg[Desc.op1] & 0x0000001F));
            Registers.R4300.PC += 4;
        }

        public static void DSLLV(OpcodeTable.OpcodeDesc Desc)
        {
            int shift = (int)(Registers.R4300.Reg[Desc.op1] & 0x3F);
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op2] << shift;
            Registers.R4300.PC += 4;
        }

        public static void SRA(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, (uint)((int)Reg32(Desc.op2) >> Desc.op4));
            Registers.R4300.PC += 4;
        }

        public static void SRL(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, Reg32(Desc.op2) >> Desc.op4);
            Registers.R4300.PC += 4;
        }

        public static void SRLV(OpcodeTable.OpcodeDesc Desc)
        {
            SetReg32(Desc.op3, Reg32(Desc.op2) >> (byte)(Registers.R4300.Reg[Desc.op1] & 0x0000001F));
            Registers.R4300.PC += 4;
        }

        public static void DSLL(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op2] << Desc.op4;
            Registers.R4300.PC += 4;
        }

        public static void DSRL(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op2] >> Desc.op4;
            Registers.R4300.PC += 4;
        }

        public static void DSRA(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op3] = (ulong)((long)Registers.R4300.Reg[Desc.op2] >> Desc.op4);
            Registers.R4300.PC += 4;
        }

        public static void DSLL32(OpcodeTable.OpcodeDesc Desc)
        {
            int shift = Desc.op4 + 32;
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op2] << shift;
            Registers.R4300.PC += 4;
        }

        public static void DSRL32(OpcodeTable.OpcodeDesc Desc)
        {
            int shift = Desc.op4 + 32;
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op2] >> shift;
            Registers.R4300.PC += 4;
        }

        public static void DSRA32(OpcodeTable.OpcodeDesc Desc)
        {
            int shift = Desc.op4 + 32;
            Registers.R4300.Reg[Desc.op3] = (ulong)((long)Registers.R4300.Reg[Desc.op2] >> shift);
            Registers.R4300.PC += 4;
        }

        public static void DSRLV(OpcodeTable.OpcodeDesc Desc)
        {
            int shift = (int)(Registers.R4300.Reg[Desc.op1] & 0x3F);
            Registers.R4300.Reg[Desc.op3] = Registers.R4300.Reg[Desc.op2] >> shift;
            Registers.R4300.PC += 4;
        }

        public static void DSRAV(OpcodeTable.OpcodeDesc Desc)
        {
            int shift = (int)(Registers.R4300.Reg[Desc.op1] & 0x3F);
            Registers.R4300.Reg[Desc.op3] = (ulong)((long)Registers.R4300.Reg[Desc.op2] >> shift);
            Registers.R4300.PC += 4;
        }

        public static void SLTI(OpcodeTable.OpcodeDesc Desc)
        {
            if ((long)Registers.R4300.Reg[Desc.op1] < (short)Desc.Imm)
            {
                SetReg32(Desc.op2, 1);
            }
            else
            {
                SetReg32(Desc.op2, 0);
            }

            Registers.R4300.PC += 4;
        }

        public static void SLTIU(OpcodeTable.OpcodeDesc Desc)
        {
            ulong imm = unchecked((ulong)(long)(short)Desc.Imm);
            if (Registers.R4300.Reg[Desc.op1] < imm)
            {
                SetReg32(Desc.op2, 1);
            }
            else
            {
                SetReg32(Desc.op2, 0);
            }

            Registers.R4300.PC += 4;
        }

        public static void SLT(OpcodeTable.OpcodeDesc Desc)
        {
            if ((long)Registers.R4300.Reg[Desc.op1] < (long)Registers.R4300.Reg[Desc.op2])
            {
                SetReg32(Desc.op3, 1);
            }
            else
            {
                SetReg32(Desc.op3, 0);
            }

            Registers.R4300.PC += 4;
        }

        public static void SLTU(OpcodeTable.OpcodeDesc Desc)
        {
            if (Registers.R4300.Reg[Desc.op1] < Registers.R4300.Reg[Desc.op2])
            {
                SetReg32(Desc.op3, 1);
            }
            else
            {
                SetReg32(Desc.op3, 0);
            }

            Registers.R4300.PC += 4;
        }
    }
}
