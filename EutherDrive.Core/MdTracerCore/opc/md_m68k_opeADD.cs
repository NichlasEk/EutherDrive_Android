using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ADD()
        {
            g_reg_PC += 2;

            int w_size = g_op2 & 0x03;

            if ((g_op2 & 0x04) == 0)
            {
                // Dn = Dn + <src>
                g_clock = (w_size == 2) ? 6 : 4;

                g_work_val1.l = read_g_reg_data(g_op1, w_size);
                adressing_func_address(g_op3, g_op4, w_size);
                g_work_val2.l = adressing_func_read(g_op3, g_op4, w_size);

                g_work_data.l = g_work_val1.l + g_work_val2.l;

                write_g_reg_data(g_op1, w_size, g_work_data.l);
            }
            else
            {
                // <dst EA> = <dst EA> + Dn
                g_clock = (w_size == 2) ? 14 : 9;

                adressing_func_address(g_op3, g_op4, w_size);
                g_work_val1.l = adressing_func_read(g_op3, g_op4, w_size);
                g_work_val2.l = read_g_reg_data(g_op1, w_size);

                g_work_data.l = g_work_val1.l + g_work_val2.l;

                adressing_func_write(g_op3, g_op4, w_size, g_work_data.l);
            }

            // Flaggar
            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];

            bool Sm = (g_work_val2.l & w_most) != 0;  // source MSB
            bool Dm = (g_work_val1.l & w_most) != 0;  // dest   MSB
            bool Rm = (g_work_data.l & w_most) != 0;  // result MSB

            g_status_N = Rm;
            g_status_Z = (g_work_data.l & w_mask) == 0;

            // Overflow: (Sm ^ Rm) & (Dm ^ Rm)
            g_status_V = ((Sm ^ Rm) && (Dm ^ Rm));

            // Carry: (Sm & Dm) | (!Rm & Dm) | (Sm & !Rm)
            g_status_C = ((Sm && Dm) || (!Rm && Dm) || (Sm && !Rm));

            g_status_X = g_status_C;
        }
    }
}
