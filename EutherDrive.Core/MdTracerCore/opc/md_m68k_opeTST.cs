using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static int _tstF62aLogRemaining = 128;

        private void analyse_TST_b()
        {
            uint pcBefore = g_reg_PC;
            g_clock += 4;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 0);
            g_work_data.l = adressing_func_read(g_op3, g_op4, 0);
            uint w_mask = MASKBIT[g_op2];
            uint w_most = MOSTBIT[g_op2];
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
            if (pcBefore == 0x0003D2 && _tstF62aLogRemaining > 0)
            {
                _tstF62aLogRemaining--;
                Console.WriteLine(
                    $"[S2-TST] pc=0x{pcBefore:X6} addr=0x{g_analyze_address:X6} val=0x{(g_work_data.l & 0xFF):X2} " +
                    $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)}");
            }
        }
        private void analyse_TST_w()
        {
            g_clock += 4;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 1);
            g_work_data.l = adressing_func_read(g_op3, g_op4, 1);
            uint w_mask = MASKBIT[g_op2];
            uint w_most = MOSTBIT[g_op2];
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
        }
        private void analyse_TST_l()
        {
            g_clock += 4;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 2);
            g_work_data.l = adressing_func_read(g_op3, g_op4, 2);
            uint w_mask = MASKBIT[g_op2];
            uint w_most = MOSTBIT[g_op2];
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
        }
   }
}
