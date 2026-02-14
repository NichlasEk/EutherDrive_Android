using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_NBCD()
        {
            if(g_op3 <= 1) g_clock += 6; else g_clock += 9;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 0);
            g_work_data.b0 = (byte)adressing_func_read(g_op3, g_op4, 0);
            g_work_data.b0 = BcdSub(0, g_work_data.b0);
            adressing_func_write(g_op3, g_op4, 0, g_work_data.b0);
        }
   }
}
