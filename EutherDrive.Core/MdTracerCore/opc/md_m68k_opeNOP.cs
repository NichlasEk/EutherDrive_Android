using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_NOP()
        {
            g_reg_PC += 2;
            g_clock += 4;
        }
   }
}
