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

        private static bool IsFpr64Mode()
        {
            const ulong cp0StatusFr = 0x04000000UL;
            return (Registers.COP0.Reg[Registers.COP0.STATUS_REG] & cp0StatusFr) != 0;
        }

        private static int FprDoubleIndex(int index) => IsFpr64Mode() ? index : (index & ~1);

        private static uint GetFprRaw32Mapped(int index)
        {
            if (IsFpr64Mode())
                return (uint)Registers.COP1.Reg[index];

            ulong raw = Registers.COP1.Reg[index & ~1];
            return (index & 1) == 0
                ? (uint)raw
                : (uint)(raw >> 32);
        }

        private static void SetFprRaw32Mapped(int index, uint value)
        {
            if (IsFpr64Mode())
            {
                Registers.COP1.Reg[index] = (Registers.COP1.Reg[index] & 0xFFFFFFFF00000000UL) | value;
                return;
            }

            int pairIndex = index & ~1;
            ulong raw = Registers.COP1.Reg[pairIndex];
            Registers.COP1.Reg[pairIndex] = (index & 1) == 0
                ? (raw & 0xFFFFFFFF00000000UL) | value
                : (raw & 0x00000000FFFFFFFFUL) | ((ulong)value << 32);
        }

        private static int RoundToWord(float value) => (int)Math.Round(value, MidpointRounding.ToEven);
        private static int RoundToWord(double value) => (int)Math.Round(value, MidpointRounding.ToEven);
        private static int TruncToWord(float value) => (int)Math.Truncate(value);
        private static int TruncToWord(double value) => (int)Math.Truncate(value);
        private static int CeilToWord(float value) => (int)Math.Ceiling(value);
        private static int CeilToWord(double value) => (int)Math.Ceiling(value);
        private static int FloorToWord(float value) => (int)Math.Floor(value);
        private static int FloorToWord(double value) => (int)Math.Floor(value);

        private static long RoundToLong(float value) => (long)Math.Round(value, MidpointRounding.ToEven);
        private static long RoundToLong(double value) => (long)Math.Round(value, MidpointRounding.ToEven);
        private static long TruncToLong(float value) => (long)Math.Truncate(value);
        private static long TruncToLong(double value) => (long)Math.Truncate(value);
        private static long CeilToLong(float value) => (long)Math.Ceiling(value);
        private static long CeilToLong(double value) => (long)Math.Ceiling(value);
        private static long FloorToLong(float value) => (long)Math.Floor(value);
        private static long FloorToLong(double value) => (long)Math.Floor(value);

        private static uint BranchAdjust(ushort imm)
        {
            return unchecked((uint)((((int)(short)imm) << 2) - 4));
        }

        private static void SetCop1CompareSingle(OpcodeTable.OpcodeDesc Desc, bool ordered, bool equalAllowed, bool lessAllowed, bool falseIfOrdered)
        {
            float a = GetFprSingle(Desc.op3);
            float b = GetFprSingle(Desc.op2);
            bool unordered = float.IsNaN(a) || float.IsNaN(b);
            bool result;

            if (unordered)
            {
                result = !ordered;
            }
            else if (falseIfOrdered)
            {
                result = false;
            }
            else
            {
                result = (equalAllowed && a == b) || (lessAllowed && a < b);
            }

            WriteCop1Condition(result);
            Registers.R4300.PC += 4;
        }

        private static void SetCop1CompareDouble(OpcodeTable.OpcodeDesc Desc, bool ordered, bool equalAllowed, bool lessAllowed, bool falseIfOrdered)
        {
            double a = GetFprDouble(Desc.op3);
            double b = GetFprDouble(Desc.op2);
            bool unordered = double.IsNaN(a) || double.IsNaN(b);
            bool result;

            if (unordered)
            {
                result = !ordered;
            }
            else if (falseIfOrdered)
            {
                result = false;
            }
            else
            {
                result = (equalAllowed && a == b) || (lessAllowed && a < b);
            }

            WriteCop1Condition(result);
            Registers.R4300.PC += 4;
        }

        internal static uint GetFprRaw32ForLoadStore(int index) => GetFprRaw32Mapped(index);
        internal static void SetFprRaw32ForLoadStore(int index, uint value) => SetFprRaw32Mapped(index, value);
        internal static ulong GetFprRaw64ForLoadStore(int index) => Registers.COP1.Reg[FprDoubleIndex(index)];
        internal static void SetFprRaw64ForLoadStore(int index, ulong value) => Registers.COP1.Reg[FprDoubleIndex(index)] = value;

        private static float GetFprSingle(int index) => Common.Util.UInt32ToFloat(GetFprRaw32Mapped(index));
        private static void SetFprSingle(int index, float value) => SetFprRaw32(index, Common.Util.FloatToUInt32(value));
        private static double GetFprDouble(int index) => Common.Util.UInt64ToDouble(GetFprRaw64(index));
        private static void SetFprDouble(int index, double value) => SetFprRaw64(index, Common.Util.DoubleToUInt64(value));
        private static uint GetFprRaw32(int index) => GetFprRaw32Mapped(index);
        private static void SetFprRaw32(int index, uint value) => SetFprRaw32Mapped(index, value);
        private static ulong GetFprRaw64(int index) => GetFprRaw64ForLoadStore(index);
        private static void SetFprRaw64(int index, ulong value) => SetFprRaw64ForLoadStore(index, value);

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
            R4300.ExecuteDelaySlot();
            if (!ReadCop1Condition())
                Registers.R4300.PC += BranchAdjust(Desc.Imm);
        }

        public static void BC1T(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4;
            R4300.ExecuteDelaySlot();
            if (ReadCop1Condition())
                Registers.R4300.PC += BranchAdjust(Desc.Imm);
        }

        public static void BC1FL(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4;
            if (!ReadCop1Condition())
            {
                R4300.ExecuteDelaySlot();
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
                R4300.ExecuteDelaySlot();
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

        public static void SQRT_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, (float)Math.Sqrt(GetFprSingle(Desc.op3)));
            Registers.R4300.PC += 4;
        }

        public static void ABS_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprSingle(Desc.op4, Math.Abs(GetFprSingle(Desc.op3)));
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

        public static void SQRT_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, Math.Sqrt(GetFprDouble(Desc.op3)));
            Registers.R4300.PC += 4;
        }

        public static void ABS_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprDouble(Desc.op4, Math.Abs(GetFprDouble(Desc.op3)));
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

        public static void ROUND_L_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)RoundToLong(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void TRUNC_L_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)TruncToLong(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void CEIL_L_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)CeilToLong(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void FLOOR_L_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)FloorToLong(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void ROUND_W_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)RoundToWord(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void TRUNC_W_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)TruncToWord(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void CEIL_W_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)CeilToWord(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void FLOOR_W_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)FloorToWord(GetFprSingle(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void ROUND_L_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)RoundToLong(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void TRUNC_L_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)TruncToLong(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void CEIL_L_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)CeilToLong(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void FLOOR_L_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw64(Desc.op4, unchecked((ulong)FloorToLong(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void ROUND_W_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)RoundToWord(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void TRUNC_W_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)TruncToWord(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void CEIL_W_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)CeilToWord(GetFprDouble(Desc.op3))));
            Registers.R4300.PC += 4;
        }

        public static void FLOOR_W_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetFprRaw32(Desc.op4, unchecked((uint)FloorToWord(GetFprDouble(Desc.op3))));
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
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_F_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: false, lessAllowed: false, falseIfOrdered: true);
        }

        public static void C_UN_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: false, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_UEQ_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_OLT_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_ULT_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_OLE_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_ULE_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_SF_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: false, lessAllowed: false, falseIfOrdered: true);
        }

        public static void C_NGLE_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: false, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_SEQ_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_NGL_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_LT_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_NGE_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_LE_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: true, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_NGT_S(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareSingle(Desc, ordered: false, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_EQ_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_F_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: false, lessAllowed: false, falseIfOrdered: true);
        }

        public static void C_UN_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: false, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_UEQ_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_OLT_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_ULT_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_OLE_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_ULE_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_SF_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: false, lessAllowed: false, falseIfOrdered: true);
        }

        public static void C_NGLE_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: false, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_SEQ_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_NGL_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: true, lessAllowed: false, falseIfOrdered: false);
        }

        public static void C_LT_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_NGE_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: false, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_LE_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: true, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }

        public static void C_NGT_D(OpcodeTable.OpcodeDesc Desc)
        {
            SetCop1CompareDouble(Desc, ordered: false, equalAllowed: true, lessAllowed: true, falseIfOrdered: false);
        }
    }
}
