using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceSonic3OuterLoop =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SONIC3_OUTER_LOOP"), "1", StringComparison.Ordinal);
        private static int _traceSonic3OuterLoopRemaining = 256;

        private void analyse_CMPA()
        {
            uint pcBefore = g_reg_PC;
            g_reg_PC += 2;
            int w_size = (g_op2 >> 2) + 1;
            g_clock = (w_size == 1) ? 8 : 6;
            g_work_val1.l = g_reg_addr[g_op1].l;
            adressing_func_address(g_op3, g_op4, w_size);
            g_work_val2.l = adressing_func_read(g_op3, g_op4, w_size);
            g_work_val2.l = get_int_cast(g_work_val2.l, w_size);
            g_work_data.l = g_work_val1.l  -  g_work_val2.l;
            uint w_mask = MASKBIT[2];
            uint w_most = MOSTBIT[2];
            bool SMC = ((g_work_val2.l & w_most)) == 0 ? false : true;
            bool DMC = ((g_work_val1.l & w_most)) == 0 ? false : true;
            bool RMC = ((g_work_data.l & w_most)) == 0 ? false : true;
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = ((SMC ^ DMC) & (DMC ^ RMC));
            g_status_C = ((SMC & !DMC) | (RMC & !DMC) | (SMC & RMC));

            if (TraceSonic3OuterLoop && _traceSonic3OuterLoopRemaining > 0 &&
                pcBefore >= 0x01946E && pcBefore <= 0x019474)
            {
                _traceSonic3OuterLoopRemaining--;
                Console.WriteLine(
                    $"[S3-OUTER-CMPA] pc=0x{pcBefore:X6} size={w_size} A{g_op1}=0x{g_work_val1.l:X8} src=0x{g_work_val2.l:X8} " +
                    $"res=0x{g_work_data.l:X8} N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
            }
        }
   }
}
