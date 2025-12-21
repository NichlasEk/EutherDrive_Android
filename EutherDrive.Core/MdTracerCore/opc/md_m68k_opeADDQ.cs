using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ADDQ()
        {
            // size: 0=byte, 1=word, 2=long
            int w_size = g_op2 & 0x03;

            // Timing (din befintliga logik)
            if (w_size == 2)
                g_clock = (g_op3 == 0) ? 8 : 14;
            else
                g_clock = (g_op3 == 0) ? 4 : 9;

            g_reg_PC += 2;

            // Immediate: bit[11:9], 0 => 8
            g_work_val2.l = (uint)((g_opcode >> 9) & 0x07);
            if (g_work_val2.l == 0) g_work_val2.l = 8;

            // EA
            adressing_func_address(g_op3, g_op4, w_size);
            g_work_val1.l = adressing_func_read(g_op3, g_op4, w_size);

            // Result
            g_work_data.l = g_work_val1.l + g_work_val2.l;

            // Write-back
            adressing_func_write(g_op3, g_op4, w_size, g_work_data.l);

            // Flags: uppdateras inte för adressregister-direkt (mode 1)
            if (g_op3 != 1)
            {
                uint w_mask = MASKBIT[w_size];
                uint w_most = MOSTBIT[w_size];

                bool SMC = (g_work_val2.l & w_most) != 0; // source msb
                bool DMC = (g_work_val1.l & w_most) != 0; // dest   msb
                bool RMC = (g_work_data.l & w_most) != 0; // result msb

                g_status_N = RMC;
                g_status_Z = (g_work_data.l & w_mask) == 0;
                g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
                g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
                g_status_X = g_status_C;
            }
        }
    }
}
