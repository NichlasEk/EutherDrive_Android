using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_TRAPV()
        {
            g_reg_PC += 2;
            if (g_status_V == true)
            {
                g_clock += 37;
                RaiseException("TRAPV", 0x001C);
            }
            else
            {
                g_clock += 4;
            }
        }
   }
}
