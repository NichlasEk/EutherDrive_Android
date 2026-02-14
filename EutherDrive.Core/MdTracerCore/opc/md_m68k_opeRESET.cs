using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_RESET()
        {
            g_reg_PC += 2;
            // RESET is privileged and intended to reset external devices.
            // On Mega Drive it's effectively a long NOP; jgenesis models it as 132 cycles.
            g_clock += 132;
        }
   }
}
