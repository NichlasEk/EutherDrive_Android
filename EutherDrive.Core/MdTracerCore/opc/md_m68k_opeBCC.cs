using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_Bcc_w()
        {
            g_clock += 10;
            g_reg_PC += 2;
           if (FixBranchBaseAfterExtension)
           {
               short disp = (short)md_main.g_md_bus.read16(g_reg_PC);
               g_reg_PC += 2;
               uint w_next_pc_work = (uint)(g_reg_PC + disp);
               if (g_flag_chack[(g_opcode >> 8) & 0x0f]()) g_reg_PC = w_next_pc_work;
           }
           else
           {
               uint w_next_pc_work = (uint)(g_reg_PC + (short)md_main.g_md_bus.read16(g_reg_PC));
               g_reg_PC += 2;
               if (g_flag_chack[(g_opcode >> 8) & 0x0f]()) g_reg_PC = w_next_pc_work;
           }
        }
        private void analyse_Bcc_b()
        {
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
           if (take) g_reg_PC = w_next_pc_work;
        }
   }
}
