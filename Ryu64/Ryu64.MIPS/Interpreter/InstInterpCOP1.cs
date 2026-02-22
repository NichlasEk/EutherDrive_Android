using System;
using System.Collections.Generic;
using System.Text;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private const uint Fcr31ConditionBit = 1u << 23;

        private static ulong SignExtend32To64Cop1(uint value)
        {
            return unchecked((ulong)(long)(int)value);
        }

        private static uint ReadFcr31() => Registers.COP1.Control[31];
        private static void WriteFcr31(uint value) => Registers.COP1.Control[31] = value;
        private static bool ReadCop1Condition() => (ReadFcr31() & Fcr31ConditionBit) != 0;
        private static void WriteCop1Condition(bool value)
        {
            uint fcr31 = ReadFcr31();
            if (value) fcr31 |= Fcr31ConditionBit;
            else fcr31 &= ~Fcr31ConditionBit;
            WriteFcr31(fcr31);
        }

        private static uint BranchAdjust(ushort imm)
        {
            return unchecked((uint)((((int)(short)imm) << 2) - 4));
        }

        private static float GetFprSingle(int index) => (float)Registers.COP1.Reg[index];
        private static void SetFprSingle(int index, float value) => Registers.COP1.Reg[index] = value;
        private static double GetFprDouble(int index) => Registers.COP1.Reg[index];
        private static void SetFprDouble(int index, double value) => Registers.COP1.Reg[index] = value;
        private static uint GetFprRaw32(int index) => Common.Util.FloatToUInt32((float)Registers.COP1.Reg[index]);
        private static void SetFprRaw32(int index, uint value) => Registers.COP1.Reg[index] = Common.Util.UInt32ToFloat(value);
        private static ulong GetFprRaw64(int index) => Common.Util.DoubleToUInt64(Registers.COP1.Reg[index]);
        private static void SetFprRaw64(int index, ulong value) => Registers.COP1.Reg[index] = Common.Util.UInt64ToDouble(value);

        public static void CFC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint value = Desc.op3 == 31 ? ReadFcr31() : Registers.COP1.Control[Desc.op3];
            Registers.R4300.Reg[Desc.op2] = SignExtend32To64Cop1(value);

            Registers.R4300.PC += 4;
        }

        public static void CTC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint value = (uint)Registers.R4300.Reg[Desc.op2];
            if (Desc.op3 == 31) WriteFcr31(value);
            else Registers.COP1.Control[Desc.op3] = value;

            Registers.R4300.PC += 4;
        }

        public static void MFC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint bits = GetFprRaw32(Desc.op3);
            Registers.R4300.Reg[Desc.op2] = SignExtend32To64Cop1(bits);
            Registers.R4300.PC += 4;
        }

        public static void MTC1(OpcodeTable.OpcodeDesc Desc)
        {
            uint bits = (uint)Registers.R4300.Reg[Desc.op2];
            SetFprRaw32(Desc.op3, bits);

            Registers.R4300.PC += 4;
        }

        public static void DMFC1(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op2] = GetFprRaw64(Desc.op3);
            Registers.R4300.PC += 4;
        }

        public static void DMTC1(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op3, Registers.R4300.Reg[Desc.op2]);
            Registers.R4300.PC += 4;
        }

        public static void BC1F(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4;
            R4300.InterpretOpcode(R4300.memory.ReadUInt32(Registers.R4300.PC));
            if (!ReadCop1Condition())
                Registers.R4300.PC += BranchAdjust(Desc.Imm);
        }

        public static void BC1T(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4;
            R4300.InterpretOpcode(R4300.memory.ReadUInt32(Registers.R4300.PC));
            if (ReadCop1Condition())
                Registers.R4300.PC += BranchAdjust(Desc.Imm);
        }

        public static void BC1FL(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4;
            if (!ReadCop1Condition())
            {
                R4300.InterpretOpcode(R4300.memory.ReadUInt32(Registers.R4300.PC));
                Registers.R4300.PC += BranchAdjust(Desc.Imm);
            }
            else
            {
                Registers.R4300.PC += 4;
            }
        }

        public static void BC1TL(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4;
            if (ReadCop1Condition())
            {
                R4300.InterpretOpcode(R4300.memory.ReadUInt32(Registers.R4300.PC));
                Registers.R4300.PC += BranchAdjust(Desc.Imm);
            }
            else
            {
                Registers.R4300.PC += 4;
            }
        }

        public static void ADD_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, GetFprSingle(Desc.op3) + GetFprSingle(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void SUB_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, GetFprSingle(Desc.op3) - GetFprSingle(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void MUL_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, GetFprSingle(Desc.op3) * GetFprSingle(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void DIV_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, GetFprSingle(Desc.op3) / GetFprSingle(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void MOV_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, GetFprSingle(Desc.op3));
            Registers.R4300.PC += 4;
        }

        public static void NEG_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, -GetFprSingle(Desc.op3));
            Registers.R4300.PC += 4;
        }

        public static void ADD_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, GetFprDouble(Desc.op3) + GetFprDouble(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void SUB_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, GetFprDouble(Desc.op3) - GetFprDouble(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void MUL_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, GetFprDouble(Desc.op3) * GetFprDouble(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void DIV_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, GetFprDouble(Desc.op3) / GetFprDouble(Desc.op2));
            Registers.R4300.PC += 4;
        }

        public static void MOV_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, GetFprDouble(Desc.op3));
            Registers.R4300.PC += 4;
        }

        public static void NEG_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, -GetFprDouble(Desc.op3));
            Registers.R4300.PC += 4;
        }

        public static void CVT_S_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, (float)GetFprDouble(Desc.op3));
            Registers.R4300.PC += 4;
        }

        public static void CVT_D_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, GetFprSingle(Desc.op3));
            Registers.R4300.PC += 4;
        }

        public static void CVT_W_S(OpcodeTable.OpcodeDesc Desc)
        {
            int value = (int)Math.Round(GetFprSingle(Desc.op3));
            SetFprRaw32(Desc.op4, unchecked((uint)value));
            Registers.R4300.PC += 4;
        }

        public static void CVT_W_D(OpcodeTable.OpcodeDesc Desc)
        {
            int value = (int)Math.Round(GetFprDouble(Desc.op3));
            SetFprRaw32(Desc.op4, unchecked((uint)value));
            Registers.R4300.PC += 4;
        }

        public static void CVT_L_S(OpcodeTable.OpcodeDesc Desc)
        {
            long value = (long)Math.Round(GetFprSingle(Desc.op3));
            SetFprRaw64(Desc.op4, unchecked((ulong)value));
            Registers.R4300.PC += 4;
        }

        public static void CVT_L_D(OpcodeTable.OpcodeDesc Desc)
        {
            long value = (long)Math.Round(GetFprDouble(Desc.op3));
            SetFprRaw64(Desc.op4, unchecked((ulong)value));
            Registers.R4300.PC += 4;
        }

        public static void CVT_S_W(OpcodeTable.OpcodeDesc Desc)
        {
            int value = unchecked((int)GetFprRaw32(Desc.op3));
            SetFprSingle(Desc.op4, value);
            Registers.R4300.PC += 4;
        }

        public static void CVT_D_W(OpcodeTable.OpcodeDesc Desc)
        {
            int value = unchecked((int)GetFprRaw32(Desc.op3));
            SetFprDouble(Desc.op4, value);
            Registers.R4300.PC += 4;
        }

        public static void CVT_S_L(OpcodeTable.OpcodeDesc Desc)
        {
            long value = unchecked((long)GetFprRaw64(Desc.op3));
            SetFprSingle(Desc.op4, value);
            Registers.R4300.PC += 4;
        }

        public static void CVT_D_L(OpcodeTable.OpcodeDesc Desc)
        {
            long value = unchecked((long)GetFprRaw64(Desc.op3));
            SetFprDouble(Desc.op4, value);
            Registers.R4300.PC += 4;
        }

        public static void C_EQ_S(OpcodeTable.OpcodeDesc Desc)
        {
            float a = GetFprSingle(Desc.op3);
            float b = GetFprSingle(Desc.op2);
            WriteCop1Condition(!float.IsNaN(a) && !float.IsNaN(b) && a == b);
            Registers.R4300.PC += 4;
        }

        public static void C_LT_S(OpcodeTable.OpcodeDesc Desc)
        {
            float a = GetFprSingle(Desc.op3);
            float b = GetFprSingle(Desc.op2);
            WriteCop1Condition(!float.IsNaN(a) && !float.IsNaN(b) && a < b);
            Registers.R4300.PC += 4;
        }

        public static void C_LE_S(OpcodeTable.OpcodeDesc Desc)
        {
            float a = GetFprSingle(Desc.op3);
            float b = GetFprSingle(Desc.op2);
            WriteCop1Condition(!float.IsNaN(a) && !float.IsNaN(b) && a <= b);
            Registers.R4300.PC += 4;
        }

        public static void C_EQ_D(OpcodeTable.OpcodeDesc Desc)
        {
            double a = GetFprDouble(Desc.op3);
            double b = GetFprDouble(Desc.op2);
            WriteCop1Condition(!double.IsNaN(a) && !double.IsNaN(b) && a == b);
            Registers.R4300.PC += 4;
        }

        public static void C_LT_D(OpcodeTable.OpcodeDesc Desc)
        {
            double a = GetFprDouble(Desc.op3);
            double b = GetFprDouble(Desc.op2);
            WriteCop1Condition(!double.IsNaN(a) && !double.IsNaN(b) && a < b);
            Registers.R4300.PC += 4;
        }

        public static void C_LE_D(OpcodeTable.OpcodeDesc Desc)
        {
            double a = GetFprDouble(Desc.op3);
            double b = GetFprDouble(Desc.op2);
            WriteCop1Condition(!double.IsNaN(a) && !double.IsNaN(b) && a <= b);
            Registers.R4300.PC += 4;
        }
    }
}
