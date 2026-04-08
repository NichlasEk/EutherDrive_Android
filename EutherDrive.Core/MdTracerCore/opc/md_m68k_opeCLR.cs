using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceClrTerminator =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CLR_TERM"), "1", StringComparison.Ordinal);
        private static int _traceClrTerminatorRemaining = 64;

        private void analyse_CLR_b()
        {
            uint pcBefore = g_reg_PC;
            if(g_op3 <= 1){
                g_clock += 4;
            }else{
                g_clock += 9;
            }
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 0);
            if (TraceClrTerminator && _traceClrTerminatorRemaining > 0 && pcBefore == 0x00545C)
            {
                _traceClrTerminatorRemaining--;
                Console.WriteLine(
                    $"[CLR-TERM] pc=0x{pcBefore:X6} mode={g_op3} reg={g_op4} addr=0x{g_analyze_address:X6} " +
                    $"sp=0x{g_reg_addr[7].l:X8} sr=0x{g_reg_SR:X4}");
            }
            adressing_func_write(g_op3, g_op4, 0, 0);
            g_status_N = false;
            g_status_Z = true;
            g_status_V = false;
            g_status_C = false;
        }
        private void analyse_CLR_w()
        {
            if(g_op3 <= 1){
                g_clock += 4;
            }else{
                g_clock += 9;
            }
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 1);
            adressing_func_write(g_op3, g_op4, 1, 0);
            g_status_N = false;
            g_status_Z = true;
            g_status_V = false;
            g_status_C = false;
        }
        private void analyse_CLR_l()
        {
            if(g_op3 <= 1){
                g_clock += 6;
            }else{
                g_clock += 14;
            }
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 2);
            adressing_func_write(g_op3, g_op4, 2, 0);
            g_status_N = false;
            g_status_Z = true;
            g_status_V = false;
            g_status_C = false;
        }
   }
}
