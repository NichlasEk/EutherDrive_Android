using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_RTR()
        {
            g_clock += 20;
            uint w_pc = g_reg_PC;
            ushort ccr = stack_pop16();
            g_reg_SR = (ushort)((g_reg_SR & 0xFF00) | (ccr & 0x00FF));
            g_reg_PC = stack_pop32();
            md_main.g_form_code_trace.CPU_Trace_pop(g_reg_PC, w_pc, g_reg_addr[7].l);
        }
   }
}
