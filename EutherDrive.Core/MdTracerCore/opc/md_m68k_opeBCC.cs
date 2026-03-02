using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceS2Bcc =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_S2_BCC"), "1", StringComparison.Ordinal);
        private static int _s2BccLogRemaining = 256;
        private static int _s2VintBccLogRemaining = 128;
        private static int _s3OuterBccLogRemaining = 256;
        private static int _s3InnerBneLogRemaining = 512;

        private void analyse_Bcc_w()
        {
            uint pc = g_reg_PC;
            g_clock += 10;
            g_reg_PC += 2;
           short disp = (short)md_main.g_md_bus.read16(g_reg_PC);
           uint w_next_pc_work = (uint)(g_reg_PC + disp);
           g_reg_PC += 2;
           int cond = (g_opcode >> 8) & 0x0f;
           bool take = g_flag_chack[cond]();
           if (TraceSonic3OuterLoop && _s3OuterBccLogRemaining > 0 && pc >= 0x01946E && pc <= 0x019474)
           {
               _s3OuterBccLogRemaining--;
               Console.WriteLine(
                   $"[S3-OUTER-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(ushort)disp:X4} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
           }
           if (TraceSonic3OuterLoop && _s3InnerBneLogRemaining > 0 && pc == 0x019450)
           {
               _s3InnerBneLogRemaining--;
               uint a5 = g_reg_addr[5].l;
               ushort bucketCount = md_main.g_md_bus.read16(a5);
               Console.WriteLine(
                   $"[S3-INNER-BNE] pc=0x{pc:X6} disp=0x{(ushort)disp:X4} take={(take ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} C={(g_status_C ? 1 : 0)} " +
                   $"A5=0x{a5:X8} [A5]=0x{bucketCount:X4}");
           }
           if (take) g_reg_PC = w_next_pc_work;
        }
        private void analyse_Bcc_b()
        {
            uint pc = g_reg_PC;
            g_clock += 10;
            g_reg_PC += 2;
           uint w_next_pc_work = (uint)(g_reg_PC + (sbyte)(g_opcode & 0x00ff));
           int cond = (g_opcode >> 8) & 0x0f;
           bool take = g_flag_chack[cond]();
           if ((g_opcode & 0xFF00) == 0x6600)
           {
               if (_bneLogRemaining > 0)
               {
                   _bneLogRemaining--;
                   MdLog.WriteLine($"[m68k] BNE disp=0x{(byte)g_opcode:X2} Z={(g_status_Z ? 1 : 0)} take={(take ? 1 : 0)}");
               }
           }
           if (TraceS2Bcc && pc == 0x000B92)
           {
               Console.WriteLine($"[m68k] Bcc pc=0x{pc:X6} cond=0x{cond:X} Z={(g_status_Z ? 1 : 0)} take={(take ? 1 : 0)} disp=0x{(byte)g_opcode:X2}");
           }
           if (TraceS2Bcc && pc >= 0x0199C0 && pc <= 0x019A60 && _s2BccLogRemaining > 0)
           {
               _s2BccLogRemaining--;
               Console.WriteLine(
                   $"[S2-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(byte)g_opcode:X2} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
           }
           if (TraceS2Bcc && pc == 0x0003D6 && _s2VintBccLogRemaining > 0)
           {
               _s2VintBccLogRemaining--;
               Console.WriteLine(
                   $"[S2-VINT-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(byte)g_opcode:X2} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
           }
           if (TraceSonic3OuterLoop && _s3OuterBccLogRemaining > 0 && pc >= 0x01946E && pc <= 0x019474)
           {
               _s3OuterBccLogRemaining--;
               Console.WriteLine(
                   $"[S3-OUTER-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(byte)g_opcode:X2} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
           }
           if (TraceSonic3OuterLoop && _s3InnerBneLogRemaining > 0 && pc == 0x019450)
           {
               _s3InnerBneLogRemaining--;
               uint a5 = g_reg_addr[5].l;
               ushort bucketCount = md_main.g_md_bus.read16(a5);
               Console.WriteLine(
                   $"[S3-INNER-BNE] pc=0x{pc:X6} take={(take ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} C={(g_status_C ? 1 : 0)} " +
                   $"A5=0x{a5:X8} [A5]=0x{bucketCount:X4}");
           }
           if (TraceSonic3OuterLoop && _s3InnerBneLogRemaining > 0 && pc == 0x019418)
           {
               _s3InnerBneLogRemaining--;
               Console.WriteLine(
                   $"[S3-INNER-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(byte)g_opcode:X2} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)} " +
                   $"D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8}");
           }
           if (take) g_reg_PC = w_next_pc_work;
        }
   }
}
