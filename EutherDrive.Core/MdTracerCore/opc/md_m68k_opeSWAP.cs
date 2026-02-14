using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_SWAP()
        {
           g_reg_PC += 2;
           g_work_data.l = g_reg_data[g_op4].l;
           g_reg_data[g_op4].w =  g_work_data.wup;
           g_reg_data[g_op4].wup =  g_work_data.w;
            uint result = g_reg_data[g_op4].l;
            uint w_mask = MASKBIT[1];
            uint w_most = MOSTBIT[1];
            g_status_N = ((result & w_most) == w_most) ? true: false;
            g_status_Z = ((result & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
           g_clock += 4;
        }
   }
}
