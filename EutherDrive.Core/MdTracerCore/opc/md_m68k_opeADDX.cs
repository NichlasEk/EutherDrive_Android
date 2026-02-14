using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // ---------------------------------------
        // ADDX (byte), register -> register (mode 0)
        // ---------------------------------------
        private void analyse_ADDX_b_mode_0()
        {
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;

            g_work_val1.l = g_reg_data[g_op1].b0;
            g_work_val2.l = g_reg_data[g_op4].b0;

            g_clock += 4;

            g_work_data.l = g_work_val1.l + g_work_val2.l;
            if (g_status_X) g_work_data.l += 1;

            write_g_reg_data(g_op1, w_size, g_work_data.l);

            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool SMC = (g_work_val2.l & w_most) != 0;
            bool DMC = (g_work_val1.l & w_most) != 0;
            bool RMC = (g_work_data.l & w_most) != 0;

            g_status_N = RMC;
            if ((g_work_data.l & w_mask) != 0) g_status_Z = false;   // cumulative Z
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }

        // ---------------------------------------
        // ADDX (byte), predecrement memory -> memory (mode 1)
        // ---------------------------------------
        private void analyse_ADDX_b_mode_1()
        {
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;

            g_reg_addr[g_op1].l -= (uint)(g_op1 == 7 ? 2 : 1);
            g_reg_addr[g_op4].l -= (uint)(g_op4 == 7 ? 2 : 1);

            g_work_val1.l = md_main.g_md_bus.read8(g_reg_addr[g_op1].l);
            g_work_val2.l = md_main.g_md_bus.read8(g_reg_addr[g_op4].l);

            g_clock += 19;

            g_work_data.l = g_work_val1.l + g_work_val2.l;
            if (g_status_X) g_work_data.l += 1;

            md_main.g_md_bus.write8(g_reg_addr[g_op1].l, g_work_data.b0);

            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool SMC = (g_work_val2.l & w_most) != 0;
            bool DMC = (g_work_val1.l & w_most) != 0;
            bool RMC = (g_work_data.l & w_most) != 0;

            g_status_N = RMC;
            if ((g_work_data.l & w_mask) != 0) g_status_Z = false;   // cumulative Z
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }

        // ---------------------------------------
        // ADDX (word), register -> register (mode 0)
        // ---------------------------------------
        private void analyse_ADDX_w_mode_0()
        {
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;

            g_work_val1.l = g_reg_data[g_op1].w;
            g_work_val2.l = g_reg_data[g_op4].w;

            g_clock += 4;

            g_work_data.l = g_work_val1.l + g_work_val2.l;
            if (g_status_X) g_work_data.l += 1;

            write_g_reg_data(g_op1, w_size, g_work_data.l);

            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool SMC = (g_work_val2.l & w_most) != 0;
            bool DMC = (g_work_val1.l & w_most) != 0;
            bool RMC = (g_work_data.l & w_most) != 0;

            g_status_N = RMC;
            if ((g_work_data.l & w_mask) != 0) g_status_Z = false;   // cumulative Z
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }

        // ---------------------------------------
        // ADDX (word), predecrement memory -> memory (mode 1)
        // ---------------------------------------
        private void analyse_ADDX_w_mode_1()
        {
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;

            g_reg_addr[g_op1].l -= 2;
            g_reg_addr[g_op4].l -= 2;

            g_work_val1.l = md_main.g_md_bus.read16(g_reg_addr[g_op1].l);
            g_work_val2.l = md_main.g_md_bus.read16(g_reg_addr[g_op4].l);

            g_clock += 19;

            g_work_data.l = g_work_val1.l + g_work_val2.l;
            if (g_status_X) g_work_data.l += 1;

            md_main.g_md_bus.write16(g_reg_addr[g_op1].l, g_work_data.w);

            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool SMC = (g_work_val2.l & w_most) != 0;
            bool DMC = (g_work_val1.l & w_most) != 0;
            bool RMC = (g_work_data.l & w_most) != 0;

            g_status_N = RMC;
            if ((g_work_data.l & w_mask) != 0) g_status_Z = false;   // cumulative Z
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }

        // ---------------------------------------
        // ADDX (long), register -> register (mode 0)
        // ---------------------------------------
        private void analyse_ADDX_l_mode_0()
        {
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;

            g_work_val1.l = g_reg_data[g_op1].l;
            g_work_val2.l = g_reg_data[g_op4].l;

            g_clock += 8;

            g_work_data.l = g_work_val1.l + g_work_val2.l;
            if (g_status_X) g_work_data.l += 1;

            write_g_reg_data(g_op1, w_size, g_work_data.l);

            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool SMC = (g_work_val2.l & w_most) != 0;
            bool DMC = (g_work_val1.l & w_most) != 0;
            bool RMC = (g_work_data.l & w_most) != 0;

            g_status_N = RMC;
            if ((g_work_data.l & w_mask) != 0) g_status_Z = false;   // cumulative Z
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }

        // ---------------------------------------
        // ADDX (long), predecrement memory -> memory (mode 1)
        // ---------------------------------------
        private void analyse_ADDX_l_mode_1()
        {
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;

            g_reg_addr[g_op1].l -= 4;
            g_reg_addr[g_op4].l -= 4;

            g_work_val1.l = md_main.g_md_bus.read32(g_reg_addr[g_op1].l);
            g_work_val2.l = md_main.g_md_bus.read32(g_reg_addr[g_op4].l);

            g_clock += 32;

            g_work_data.l = g_work_val1.l + g_work_val2.l;
            if (g_status_X) g_work_data.l += 1;

            md_main.g_md_bus.write32(g_reg_addr[g_op1].l, g_work_data.l);

            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool SMC = (g_work_val2.l & w_most) != 0;
            bool DMC = (g_work_val1.l & w_most) != 0;
            bool RMC = (g_work_data.l & w_most) != 0;

            g_status_N = RMC;
            if ((g_work_data.l & w_mask) != 0) g_status_Z = false;   // cumulative Z
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }
    }
}
