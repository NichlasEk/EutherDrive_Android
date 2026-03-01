using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static int _s3InnerAddLogRemaining = 512;

        private void analyse_ADD()
        {
            uint pcBefore = g_reg_PC;
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

            if (TraceSonic3OuterLoop && _s3InnerAddLogRemaining > 0 && pcBefore >= 0x01940E && pcBefore <= 0x019412)
            {
                _s3InnerAddLogRemaining--;
                Console.WriteLine(
                    $"[S3-INNER-ADD] pc=0x{pcBefore:X6} op=0x{g_opcode:X4} size={w_size} src=0x{g_work_val2.l & w_mask:X8} " +
                    $"dst=0x{g_work_val1.l & w_mask:X8} res=0x{g_work_data.l & w_mask:X8} " +
                    $"D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} SR=0x{g_reg_SR:X4}");
            }
        }
    }
}
