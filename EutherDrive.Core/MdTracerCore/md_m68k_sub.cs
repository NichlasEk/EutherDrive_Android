using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // Gör tabellerna statiska och readonly
        private static readonly uint[] MASKBIT    = { 0xffu, 0xffffu, 0xffffffffu };
        private static readonly uint[] MOSTBIT    = { 0x80u, 0x8000u, 0x80000000u };
        private static readonly uint[] MASKNOTBIT = { 0xffff_ff00u, 0xffff_0000u, 0x0000_0000u };
        private static uint _lastD1Value;

        private static readonly uint[] BITHIT =
        {
            0x00000001, 0x00000002, 0x00000004, 0x00000008,
            0x00000010, 0x00000020, 0x00000040, 0x00000080,
            0x00000100, 0x00000200, 0x00000400, 0x00000800,
            0x00001000, 0x00002000, 0x00004000, 0x00008000,
            0x00010000, 0x00020000, 0x00040000, 0x00080000,
            0x00100000, 0x00200000, 0x00400000, 0x00800000,
            0x01000000, 0x02000000, 0x04000000, 0x08000000,
            0x10000000, 0x20000000, 0x40000000, 0x80000000
        };

        private static readonly int[,] MOVE_CLOCK =
        {
            {  4,  4,  9,  9,  9, 13, 15, 13, 17 },
            {  4,  4,  9,  9,  9, 13, 15, 13, 17 },
            {  8,  8, 13, 13, 13, 17, 19, 17, 21 },
            {  8,  8, 13, 13, 13, 17, 19, 17, 21 },
            { 10, 10, 15, 15, 15, 19, 21, 19, 23 },
            { 12, 12, 17, 17, 17, 21, 23, 21, 15 },
            { 14, 14, 19, 19, 19, 23, 25, 23, 27 },
            { 12, 12, 17, 17, 17, 21, 23, 21, 25 },
            { 16, 16, 21, 21, 21, 25, 27, 25, 29 },
            { 12, 12, 17, 17, 17, 21, 23, 21, 25 },
            { 14, 14, 19, 19, 19, 23, 25, 23, 27 },
            {  8,  8, 13, 13, 13, 17, 19, 17, 21 }
        };

        private static readonly int[,] MOVE_CLOCK_L =
        {
            {  4,  4, 14, 14, 16, 18, 20, 18, 22 },
            {  4,  4, 14, 14, 16, 18, 20, 18, 22 },
            { 12, 12, 22, 22, 22, 26, 28, 26, 30 },
            { 12, 12, 22, 22, 22, 26, 28, 26, 30 },
            { 14, 14, 24, 24, 24, 28, 30, 28, 32 },
            { 16, 16, 26, 26, 26, 30, 35, 30, 34 },
            { 18, 18, 28, 28, 28, 32, 34, 32, 36 },
            { 16, 16, 26, 26, 26, 30, 32, 30, 34 },
            { 20, 20, 30, 30, 30, 34, 36, 34, 38 },
            { 16, 16, 26, 26, 26, 30, 32, 30, 34 },
            { 18, 18, 28, 28, 28, 32, 34, 32, 36 },
            { 12, 12, 22, 22, 22, 26, 28, 26, 30 }
        };

        // ==========================================================
        // SR/CCR pack/unpack (ANVÄNDER globals: g_reg_SR, g_status_CCR)
        // ==========================================================

        private static ushort PackSR()
        {
            ushort value = 0;

            if (g_status_T)  value |= 0x8000;
            if (g_status_B1) value |= 0x4000;
            if (g_status_S)  value |= 0x2000;
            if (g_status_B2) value |= 0x1000;
            if (g_status_B3) value |= 0x0800;

            value |= (ushort)((g_status_interrupt_mask & 0x07) << 8);

            if (g_status_B4) value |= 0x0080;
            if (g_status_B5) value |= 0x0040;
            if (g_status_B6) value |= 0x0020;
            if (g_status_X)  value |= 0x0010;
            if (g_status_N)  value |= 0x0008;
            if (g_status_Z)  value |= 0x0004;
            if (g_status_V)  value |= 0x0002;
            if (g_status_C)  value |= 0x0001;

            return value;
        }

        private static void UnpackSR(ushort value)
        {
            g_status_T  = (value & 0x8000) != 0;
            g_status_B1 = (value & 0x4000) != 0;
            g_status_S  = (value & 0x2000) != 0;
            g_status_B2 = (value & 0x1000) != 0;
            g_status_B3 = (value & 0x0800) != 0;

            g_status_interrupt_mask = (byte)((value >> 8) & 0x07);

            g_status_B4 = (value & 0x0080) != 0;
            g_status_B5 = (value & 0x0040) != 0;
            g_status_B6 = (value & 0x0020) != 0;
            g_status_X  = (value & 0x0010) != 0;
            g_status_N  = (value & 0x0008) != 0;
            g_status_Z  = (value & 0x0004) != 0;
            g_status_V  = (value & 0x0002) != 0;
            g_status_C  = (value & 0x0001) != 0;
        }

        private static ushort PackCCR()
        {
            // CCR är low byte i SR (vi håller som ushort för kompatibilitet)
            ushort value = 0;
            if (g_status_B4) value |= 0x80;
            if (g_status_B5) value |= 0x40;
            if (g_status_B6) value |= 0x20;
            if (g_status_X)  value |= 0x10;
            if (g_status_N)  value |= 0x08;
            if (g_status_Z)  value |= 0x04;
            if (g_status_V)  value |= 0x02;
            if (g_status_C)  value |= 0x01;
            return value;
        }

        private static void UnpackCCR(ushort value)
        {
            byte b = (byte)(value & 0xFF);

            g_status_B4 = (b & 0x80) != 0;
            g_status_B5 = (b & 0x40) != 0;
            g_status_B6 = (b & 0x20) != 0;
            g_status_X  = (b & 0x10) != 0;
            g_status_N  = (b & 0x08) != 0;
            g_status_Z  = (b & 0x04) != 0;
            g_status_V  = (b & 0x02) != 0;
            g_status_C  = (b & 0x01) != 0;
        }

        // Anropa dessa när din CPU-kod vill “skriva SR/CCR”
        private static void WriteSR(ushort value)
        {
            g_reg_SR = value;
            UnpackSR(value);
            g_status_CCR = (byte)PackCCR();
        }

        private static void WriteCCR(ushort value)
        {
            g_status_CCR = (byte)(value & 0x00FF);
            UnpackCCR(g_status_CCR);

            // uppdatera SR så att SR och flaggor matchar
            g_reg_SR = PackSR();
        }

        private static void SyncSRFromFlags()  => g_reg_SR     = PackSR();
        private static void SyncCCRFromFlags() => g_status_CCR = (byte)PackCCR();

        // ==========================================================
        // Flag check table (g_flag_chack) — gör det till static
        // ==========================================================

        private static Func<bool>[]? g_flag_chack;

        private static bool flag_t()  => true;
        private static bool flag_f()  => false;
        private static bool flag_hi() => (!g_status_C && !g_status_Z);
        private static bool flag_ls() => (g_status_C || g_status_Z);
        private static bool flag_cc() => (!g_status_C);
        private static bool flag_cs() => (g_status_C);
        private static bool flag_ne() => (!g_status_Z);
        private static bool flag_eq() => (g_status_Z);
        private static bool flag_vc() => (!g_status_V);
        private static bool flag_vs() => (g_status_V);
        private static bool flag_pl() => (!g_status_N);
        private static bool flag_mi() => (g_status_N);

        // signed compares
        private static bool flag_ge() => !(g_status_N ^ g_status_V);
        private static bool flag_lt() =>  (g_status_N ^ g_status_V);
        private static bool flag_gt() => !(g_status_N ^ g_status_V) && !g_status_Z;
        private static bool flag_le() =>  (g_status_N ^ g_status_V) ||  g_status_Z;

        // Kör detta en gång i init/reset för att slippa null
        private static void EnsureFlagTable()
        {
            if (g_flag_chack != null) return;

            g_flag_chack = new Func<bool>[16]
            {
                flag_t,  // 0  T
                flag_f,  // 1  F
                flag_hi, // 2  HI
                flag_ls, // 3  LS
                flag_cc, // 4  CC
                flag_cs, // 5  CS
                flag_ne, // 6  NE
                flag_eq, // 7  EQ
                flag_vc, // 8  VC
                flag_vs, // 9  VS
                flag_pl, // 10 PL
                flag_mi, // 11 MI
                flag_ge, // 12 GE
                flag_lt, // 13 LT
                flag_gt, // 14 GT
                flag_le  // 15 LE
            };
        }

        // ==========================================================
        // helpers (oförändrade i sak)
        // ==========================================================

        private static uint get_int_cast(uint in_data, int in_size)
        {
            uint w_mostbit    = MOSTBIT[in_size];
            uint w_mostnotbit = MASKNOTBIT[in_size];
            uint w_out = in_data;

            if (in_size != 2)
            {
                if ((w_out & w_mostbit) == w_mostbit)
                    w_out |= w_mostnotbit;
                else
                    w_out &= MASKBIT[in_size];
            }

            return w_out;
        }

        private static uint read_g_reg_data(int in_num, int in_size)
        {
            return in_size switch
            {
                0 => g_reg_data[in_num].b0,
                1 => g_reg_data[in_num].w,
                _ => g_reg_data[in_num].l
            };
        }

        private static void write_g_reg_data(int in_num, int in_size, uint in_val)
        {
            switch (in_size)
            {
                case 0: g_reg_data[in_num].b0 = (byte)in_val;   break;
                case 1: g_reg_data[in_num].w  = (ushort)in_val; break;
                default: g_reg_data[in_num].l = in_val;         break;
            }

            if (in_num == 1)
            {
                uint newVal = g_reg_data[1].l;
                if (newVal != _lastD1Value)
                {
                    _lastD1Value = newVal;
                    if (_d1LogRemaining > 0)
                    {
                        _d1LogRemaining--;
                        if (_d1LogLastPc != g_reg_PC)
                            _d1LogLastPc = g_reg_PC;
                        MdLog.WriteLine($"[md_m68k] D1=0x{newVal:X8} PC=0x{g_reg_PC:X6}");
                    }
                }
            }
        }

        // ==========================================================
        // stack helpers (S-bit swap + korrekt USP-adress)
        // ==========================================================

        private static void SwapStacks()
        {
            if (_swapLogRemaining > 0)
            {
                _swapLogRemaining--;
                Console.WriteLine($"[m68k] SwapStacks S={(g_status_S ? 1 : 0)} A7=0x{g_reg_addr[7].l:X8} USP=0x{g_reg_addr_usp.l:X8}");
            }
            uint temp = g_reg_addr_usp.l;
            g_reg_addr_usp.l = g_reg_addr[7].l;
            g_reg_addr[7].l = temp;
        }

        private static void stack_push32(uint in_val)
        {
            g_reg_addr[7].l -= 4;
            write32(g_reg_addr[7].l, in_val);
        }

        private static uint stack_pop32()
        {
            uint w_val;

            uint sp = g_reg_addr[7].l;
            w_val = read32(sp);
            write32(sp, 0);
            g_reg_addr[7].l = sp + 4;

            return w_val;
        }

        private static void stack_push16(ushort in_val)
        {
            g_reg_addr[7].l -= 2;
            write16(g_reg_addr[7].l, in_val);
        }

        private static ushort stack_pop16()
        {
            ushort w_val;

            uint sp = g_reg_addr[7].l;
            w_val = read16(sp);
            write16(sp, 0);
            g_reg_addr[7].l = sp + 2;

            return w_val;
        }
    }
}
