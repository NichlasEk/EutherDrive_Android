using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_BTST_dynamic_long()
        {
            g_clock += 6;
            g_reg_PC += 2;
            int w_bit = g_reg_data[g_op1].b0;
            w_bit = w_bit & 0x1f;
            g_work_data.l = adressing_func_read(0, g_op4, 2);
            g_status_Z = ((g_work_data.l & BITHIT[w_bit]) == 0);
        }
        private void analyse_BTST_dynamic_byte()
        {
            g_clock += 4;
            g_reg_PC += 2;
            int w_bit = g_reg_data[g_op1].b0;
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
            g_clock += 10;
            g_reg_PC += 2;
            int w_bit = md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;
            w_bit = w_bit & 0x1f;
            g_work_data.l = adressing_func_read(0, g_op4, 2);
            g_status_Z = ((g_work_data.l & BITHIT[w_bit]) == 0);
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
            if ((g_analyze_address == 0x00A11100 || g_analyze_address == 0x00A01FFD) && _btstLogRemaining > 0)
            {
                _btstLogRemaining--;
                MdLog.WriteLine($"[m68k] BTST pc=0x{pc:X6} addr=0x{g_analyze_address:X6} val=0x{g_work_data.b0:X2} bit={w_bit} Z={(g_status_Z ? 1 : 0)}");
            }
        }
   }
}
