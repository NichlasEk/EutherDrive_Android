using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceCmpiSubReady =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SUB_READY"), "1", StringComparison.Ordinal);
        private static int _traceCmpiSubReadyRemaining = 8;

        private void analyse_CMPI()
        {
            uint pcBefore = g_reg_PC;
           if(g_op3 == 0) if(g_op2 == 2)g_clock = 14; else g_clock = 8;
                     else if(g_op2 == 2)g_clock = 12; else g_clock = 8;
            g_reg_PC += 2;
            switch (g_op2)
            {
                case 0: g_work_val2.l = (uint)(md_main.g_md_bus.read16(g_reg_PC) & 0x00ff); g_reg_PC += 2; break;
                case 1: g_work_val2.l = md_main.g_md_bus.read16(g_reg_PC); g_reg_PC += 2; break;
                default: g_work_val2.l = md_main.g_md_bus.read32(g_reg_PC); g_reg_PC += 4; break;
            }
            adressing_func_address(g_op3, g_op4, g_op2);
            g_work_val1.l = adressing_func_read(g_op3, g_op4, g_op2);
            g_work_data.l = g_work_val1.l  -  g_work_val2.l;
            uint w_mask = MASKBIT[g_op2];
            uint w_most = MOSTBIT[g_op2];
            bool SMC = ((g_work_val2.l & w_most)) == 0 ? false : true;
            bool DMC = ((g_work_val1.l & w_most)) == 0 ? false : true;
            bool RMC = ((g_work_data.l & w_most)) == 0 ? false : true;
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = ((SMC ^ DMC) & (DMC ^ RMC));
            g_status_C = ((SMC & !DMC) | (RMC & !DMC) | (SMC & RMC));

            if (TraceCmpiSubReady && _traceCmpiSubReadyRemaining > 0 && pcBefore == 0x000572A)
            {
                _traceCmpiSubReadyRemaining--;
                Console.WriteLine(
                    $"[SUB-READY-CMPI] pc=0x{pcBefore:X6} imm=0x{g_work_val2.l & w_mask:X8} addr=0x{g_analyze_address:X6} " +
                    $"val=0x{g_work_val1.l & w_mask:X8} Z={(g_status_Z ? 1 : 0)} N={(g_status_N ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
            }
        }
   }
}
