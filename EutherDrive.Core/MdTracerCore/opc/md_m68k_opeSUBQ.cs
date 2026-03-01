using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceSonic3Subq =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SONIC3_SUBQ"), "1", StringComparison.Ordinal);
        private static readonly int TraceSonic3SubqLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_SONIC3_SUBQ_LIMIT");
        private static int _traceSonic3SubqRemaining = TraceSonic3Subq ? TraceSonic3SubqLimit : 0;

        private void analyse_SUBQ()
        {
            uint opcodePc = g_reg_PC;
            int w_size = g_op2 & 0x03;
            if(w_size == 2)
            {
                if (g_op3 == 0) g_clock = 8; else g_clock = 14;
            }else{
                if (g_op3 == 0) g_clock = 4; else g_clock = 9;
            }
            g_reg_PC += 2;
            g_work_val2.l = (byte)((g_opcode >> 9) & 0x07);
            if(g_work_val2.l == 0) g_work_val2.l = 8;

            // Address register direct: size ignored, 32-bit subtract, no flags
            if (g_op3 == 1)
            {
                g_reg_addr[g_op4].l = g_reg_addr[g_op4].l - g_work_val2.l;
                return;
            }

            adressing_func_address(g_op3, g_op4, w_size);
            g_work_val1.l = adressing_func_read(g_op3, g_op4, w_size);
            g_work_data.l = g_work_val1.l - g_work_val2.l;
            adressing_func_write(g_op3, g_op4, w_size, g_work_data.l);
            if (TraceSonic3Subq && _traceSonic3SubqRemaining > 0 && opcodePc == 0x1944E && g_opcode == 0x5555)
            {
                _traceSonic3SubqRemaining--;
                Console.WriteLine(
                    $"[SONIC3-SUBQ] pc=0x{opcodePc:X6} before=0x{(g_work_val1.l & 0xFFFF):X4} " +
                    $"sub=0x{g_work_val2.l:X2} after=0x{(g_work_data.l & 0xFFFF):X4} A5=0x{g_reg_addr[5].l:X8}");
            }
            uint w_mask = MASKBIT[g_op2 & 0x03];
            uint w_most = MOSTBIT[g_op2 & 0x03];
            bool SMC = ((g_work_val2.l & w_most)) == 0 ? false : true;
            bool DMC = ((g_work_val1.l & w_most)) == 0 ? false : true;
            bool RMC = ((g_work_data.l & w_most)) == 0 ? false : true;
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = ((SMC ^ DMC) & (DMC ^ RMC));
            g_status_C = ((SMC & !DMC) | (RMC & !DMC) | (SMC & RMC));
            g_status_X = g_status_C;
        }
   }
}
