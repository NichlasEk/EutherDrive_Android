using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceSonic3Movea =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SONIC3_MOVEA"), "1", StringComparison.Ordinal);
        private static readonly int TraceSonic3MoveaLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_SONIC3_MOVEA_LIMIT");
        private static int _traceSonic3MoveaRemaining = TraceSonic3Movea ? TraceSonic3MoveaLimit : 0;

        private void analyse_MOVEA_w()
        {
            if((g_op2 <= 1)&&(g_op3 <=1)) g_clock += 4; else g_clock += 5;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 1);
            g_work_data.l = adressing_func_read(g_op3, g_op4, 1);
            g_reg_addr[g_op1].l = get_int_cast(g_work_data.w, 1);
            if (TraceSonic3Movea && _traceSonic3MoveaRemaining > 0 && g_opcode == 0x305C && g_reg_PC >= 0x193C2 && g_reg_PC <= 0x193C6)
            {
                _traceSonic3MoveaRemaining--;
                Console.WriteLine(
                    $"[SONIC3-MOVEA] pc=0x{g_reg_PC:X6} srcW=0x{g_work_data.w:X4} dstA{g_op1}=0x{g_reg_addr[g_op1].l:X8}");
            }
        }
        private void analyse_MOVEA_l()
        {
            if((g_op2 <= 1)&&(g_op3 <=1)) g_clock += 4; else g_clock += 5;
            g_reg_PC += 2;
            adressing_func_address(g_op3, g_op4, 2);
            g_work_data.l = adressing_func_read(g_op3, g_op4, 2);
            g_reg_addr[g_op1].l = g_work_data.l;
        }
   }
}
