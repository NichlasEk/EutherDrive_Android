using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static int _s2BccLogRemaining = 256;
        private static int _s2VintBccLogRemaining = 128;

        private void analyse_Bcc_w()
        {
            g_clock += 10;
            g_reg_PC += 2;
           uint w_next_pc_work = (uint)(g_reg_PC + (short)md_main.g_md_bus.read16(g_reg_PC));
           g_reg_PC += 2;
           if (g_flag_chack[(g_opcode >> 8) & 0x0f]()) g_reg_PC = w_next_pc_work;
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
           if (pc == 0x000B92)
           {
               Console.WriteLine($"[m68k] Bcc pc=0x{pc:X6} cond=0x{cond:X} Z={(g_status_Z ? 1 : 0)} take={(take ? 1 : 0)} disp=0x{(byte)g_opcode:X2}");
           }
           if (pc >= 0x0199C0 && pc <= 0x019A60 && _s2BccLogRemaining > 0)
           {
               _s2BccLogRemaining--;
               Console.WriteLine(
                   $"[S2-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(byte)g_opcode:X2} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
           }
           if (pc == 0x0003D6 && _s2VintBccLogRemaining > 0)
           {
               _s2VintBccLogRemaining--;
               Console.WriteLine(
                   $"[S2-VINT-BCC] pc=0x{pc:X6} cond=0x{cond:X} disp=0x{(byte)g_opcode:X2} take={(take ? 1 : 0)} " +
                   $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} V={(g_status_V ? 1 : 0)} C={(g_status_C ? 1 : 0)}");
           }
           if (take) g_reg_PC = w_next_pc_work;
        }
   }
}
