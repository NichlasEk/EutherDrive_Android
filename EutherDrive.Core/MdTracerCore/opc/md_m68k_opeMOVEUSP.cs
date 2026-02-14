using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_MOVEUSP_1()
        {
            if (!g_status_S)
            {
                RaiseException("PRIV", 0x0020);
                return;
            }
            g_clock += 4;
            g_reg_PC += 2;
            g_reg_addr_usp.l = g_reg_addr[g_op4].l;
        }
        private void analyse_MOVEUSP_2()
        {
            if (!g_status_S)
            {
                RaiseException("PRIV", 0x0020);
                return;
            }
            g_clock += 4;
            g_reg_PC += 2;
            g_reg_addr[g_op4].l = g_reg_addr_usp.l;
        }
   }
}
