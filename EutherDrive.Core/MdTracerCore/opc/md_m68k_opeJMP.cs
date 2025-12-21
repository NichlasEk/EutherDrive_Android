using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_JMP()
        {
            g_clock += 4;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 2);
            g_reg_PC = g_analyze_address;
        }
   }
}
