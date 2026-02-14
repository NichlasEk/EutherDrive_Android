using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_CHK()
        {
            g_clock += 43;
            g_reg_PC += 2;
            g_work_val1.w = g_reg_data[g_op1].w;
            adressing_func_address(g_op3, g_op4, 1);
            g_work_val2.w = (ushort)adressing_func_read(g_op3, g_op4, 1);

            // CHK uses signed comparison; C/V/Z cleared, N set based on value
            short value = (short)g_work_val1.w;
            short upper = (short)g_work_val2.w;
            g_status_C = false;
            g_status_V = false;
            g_status_Z = false;

            if (value < 0)
            {
                g_status_N = true;
                RaiseException("CHK", 0x0018);
            }
            else if (value > upper)
            {
                g_status_N = false;
                RaiseException("CHK", 0x0018);
            }
        }
   }
}
