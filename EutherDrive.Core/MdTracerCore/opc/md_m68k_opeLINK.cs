using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_LINK()
        {
            g_clock += 18;
            g_reg_PC += 2;
            short w_ext_disp = (short)md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            stack_push32(g_reg_addr[g_op4].l);
            g_reg_addr[g_op4].l = g_reg_addr[7].l;
            g_reg_addr[7].l = (uint)(g_reg_addr[7].l + w_ext_disp);
            if ((g_reg_addr[7].l & 1) != 0)
            {
                Console.WriteLine(
                    $"[m68k] LINK odd SP pc=0x{(g_reg_PC - 4):X6} A{g_op4} sp=0x{g_reg_addr[7].l:X8} disp=0x{(ushort)w_ext_disp:X4}");
            }
        }
   }
}
