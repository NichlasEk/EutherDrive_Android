using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_STOP()
        {
            g_reg_PC += 2;
            g_work_data.w = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            g_reg_SR = g_work_data.w;
            g_68k_stop = true;
           g_clock += 4;
        }
   }
}
