using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_RTS()
        {
            g_clock += 16;
            uint w_pc = g_reg_PC;
            uint spBefore = g_reg_addr[7].l;
            uint stack0 = md_main.g_md_bus.read32(spBefore);
            uint stack1 = md_main.g_md_bus.read32(spBefore + 4);
            uint ret = stack_pop32();
            if (_rtsBadLogRemaining > 0 && (ret == 0 || spBefore >= 0xFFFF0000))
            {
                _rtsBadLogRemaining--;
                Console.WriteLine($"[m68k] RTS pc=0x{w_pc:X6} sp=0x{spBefore:X8} ret=0x{ret:X8} stack0=0x{stack0:X8} stack1=0x{stack1:X8}");
            }
            g_reg_PC = ret;
            md_main.g_form_code_trace.CPU_Trace_pop(g_reg_PC, w_pc, g_reg_addr[7].l);
        }
   }
}
