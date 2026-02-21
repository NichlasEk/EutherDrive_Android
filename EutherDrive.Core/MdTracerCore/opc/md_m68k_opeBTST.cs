using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static int _s2MonitorBtstLogRemaining = 200;

        private void analyse_BTST_dynamic_long()
        {
            uint pc = g_reg_PC;
            g_clock += 6;
            g_reg_PC += 2;
            int w_bit = (int)g_reg_data[g_op1].l;
            w_bit = w_bit & 0x1f;
            g_work_data.l = adressing_func_read(0, g_op4, 2);
            g_status_Z = ((g_work_data.l & BITHIT[w_bit]) == 0);
            if ((pc == 0x01E9F8 || pc == 0x01E818) && _s2MonitorBtstLogRemaining > 0)
            {
                _s2MonitorBtstLogRemaining--;
                Console.WriteLine(
                    $"[S2-BTST] pc=0x{pc:X6} op1=D{g_op1} op4=D{g_op4} " +
                    $"src=0x{g_reg_data[g_op1].l:X8} dst=0x{g_work_data.l:X8} bit={w_bit} Z={(g_status_Z ? 1 : 0)}");
            }
        }
        private void analyse_BTST_dynamic_byte()
        {
            g_clock += 4;
            g_reg_PC += 2;
            int w_bit = (int)g_reg_data[g_op1].l;
            w_bit = w_bit & 0x07;
            adressing_func_address(g_op3, g_op4, 0);
            g_work_data.b0 = (byte)adressing_func_read(g_op3, g_op4, 0);
            g_status_Z = ((g_work_data.b0 & BITHIT[w_bit]) == 0);
            if (g_analyze_address == 0x00A11100 && _btstLogRemaining > 0)
            {
                _btstLogRemaining--;
                MdLog.WriteLine($"[m68k] BTST A11100 val=0x{g_work_data.b0:X2} bit={w_bit} Z={(g_status_Z ? 1 : 0)}");
            }
        }
        private void analyse_BTST_static_long()
        {
            uint pc = g_reg_PC;
            g_clock += 10;
            g_reg_PC += 2;
            int w_bit = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            w_bit = w_bit & 0x1f;
            g_work_data.l = adressing_func_read(0, g_op4, 2);
            g_status_Z = ((g_work_data.l & BITHIT[w_bit]) == 0);
            if (pc == 0x000B8E)
            {
                Console.WriteLine($"[m68k] BTST pc=0x{pc:X6} bit={w_bit} val=0x{g_work_data.l:X8} Z={(g_status_Z ? 1 : 0)}");
            }
        }
        private void analyse_BTST_static_byte()
        {
            uint pc = g_reg_PC;
            g_clock += 8;
            g_reg_PC += 2;
            int w_bit = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            w_bit = w_bit & 0x07;
            adressing_func_address(g_op3, g_op4, 0);
            g_work_data.b0 = (byte)adressing_func_read(g_op3, g_op4, 0);
            g_status_Z = ((g_work_data.b0 & BITHIT[w_bit]) == 0);
            if (pc == 0x001072)
            {
                Console.WriteLine($"[m68k] BTST.B pc=0x{pc:X6} addr=0x{g_analyze_address:X6} bit={w_bit} val=0x{g_work_data.b0:X2} Z={(g_status_Z ? 1 : 0)}");
            }
            if (pc == 0x0007A4)
            {
                Console.WriteLine($"[m68k] BTST.B pc=0x{pc:X6} addr=0x{g_analyze_address:X6} bit={w_bit} val=0x{g_work_data.b0:X2} Z={(g_status_Z ? 1 : 0)}");
            }
            if ((g_analyze_address == 0x00A11100 || g_analyze_address == 0x00A01FFD) && _btstLogRemaining > 0)
            {
                _btstLogRemaining--;
                MdLog.WriteLine($"[m68k] BTST pc=0x{pc:X6} addr=0x{g_analyze_address:X6} val=0x{g_work_data.b0:X2} bit={w_bit} Z={(g_status_Z ? 1 : 0)}");
            }
        }
   }
}
