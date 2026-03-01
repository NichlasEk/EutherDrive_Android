using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceSubChkCmp =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SUB_CHK"), "1", StringComparison.Ordinal);
        private static int _traceSubChkRemaining = 8;
        private static int _s2CmpLogRemaining = 128;
        private static int _s3InnerCmpLogRemaining = 512;

        private void analyse_CMP()
        {
            uint pcBefore = (uint)(g_reg_PC);
            uint a3Before = 0;
            uint a3After = 0;
            uint srcAddr = 0;
            uint d0Before = 0;
            bool traceB01b = false;
            g_reg_PC += 2;
            int w_size = g_op2 & 0x03;
                g_clock = (w_size == 2) ? 6 : 4;
                g_work_val1.l = read_g_reg_data(g_op1, w_size);
                if (g_opcode == 0xB01B)
                {
                    pcBefore = (uint)(g_reg_PC - 2);
                    a3Before = g_reg_addr[3].l;
                    d0Before = g_reg_data[0].l;
                }
                adressing_func_address(g_op3, g_op4, w_size);
                g_work_val2.l = adressing_func_read(g_op3, g_op4, w_size);
                if (g_opcode == 0xB01B)
                {
                    srcAddr = g_analyze_address;
                    a3After = g_reg_addr[3].l;
                    traceB01b = ShouldTraceOpcode(TraceOpcodeB01B, pcBefore);
                }
                g_work_data.l = g_work_val1.l  -  g_work_val2.l;
            if (traceB01b)
            {
                byte srcVal = (byte)(g_work_val2.l & 0xFF);
                byte d0Val = (byte)(d0Before & 0xFF);
                Console.WriteLine(
                    $"[OPB01B] pc=0x{pcBefore:X6} A3:0x{a3Before:X8}->0x{a3After:X8} src=0x{srcAddr:X6} val=0x{srcVal:X2} D0=0x{d0Val:X2}");
            }
            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];
            bool SMC = ((g_work_val2.l & w_most)) == 0 ? false : true;
            bool DMC = ((g_work_val1.l & w_most)) == 0 ? false : true;
            bool RMC = ((g_work_data.l & w_most)) == 0 ? false : true;
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = ((SMC ^ DMC) & (DMC ^ RMC));
            g_status_C = ((SMC & !DMC) | (RMC & !DMC) | (SMC & RMC));

            if (TraceSonic3OuterLoop && _s3InnerCmpLogRemaining > 0 && pcBefore >= 0x019414 && pcBefore <= 0x019418)
            {
                _s3InnerCmpLogRemaining--;
                Console.WriteLine(
                    $"[S3-INNER-CMP] pc=0x{pcBefore:X6} op=0x{g_opcode:X4} size={w_size} lhs=0x{g_work_val1.l & w_mask:X8} " +
                    $"rhs=0x{g_work_val2.l & w_mask:X8} res=0x{g_work_data.l & w_mask:X8} " +
                    $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
            }

            if (pcBefore >= 0x0199C0 && pcBefore <= 0x019A60 && _s2CmpLogRemaining > 0)
            {
                _s2CmpLogRemaining--;
                uint lhs = g_work_val1.l & w_mask;
                uint rhs = g_work_val2.l & w_mask;
                uint res = g_work_data.l & w_mask;
                Console.WriteLine(
                    $"[S2-CMP] pc=0x{pcBefore:X6} size={w_size} lhs=0x{lhs:X8} rhs=0x{rhs:X8} res=0x{res:X8} " +
                    $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
            }

            if (TraceSubChkCmp && _traceSubChkRemaining > 0 && g_opcode == 0xB041 && pcBefore == 0x0002F0)
            {
                _traceSubChkRemaining--;
                Console.WriteLine(
                    $"[SUB-CHK-CMP] pc=0x{pcBefore:X6} D0=0x{g_reg_data[0].w:X4} D1=0x{g_reg_data[1].w:X4} " +
                    $"src=0x{g_work_val2.l & w_mask:X4} dst=0x{g_work_val1.l & w_mask:X4} res=0x{g_work_data.l & w_mask:X4} " +
                    $"Z={(g_status_Z ? 1 : 0)} N={(g_status_N ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
            }
        }
   }
}
