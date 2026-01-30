using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ANDI()
        {
            // Cycles (grovt enligt din befintliga tabell)
            if (g_op3 == 0)
                g_clock = (g_op2 == 2) ? 16 : 8;
            else
                g_clock = (g_op2 == 2) ? 22 : 13;

            g_reg_PC += 2;

            // Läs immediate beroende på storlek: 00=byte, 01=word, 10=long
            switch (g_op2)
            {
                case 0: // Byte
                    g_work_val2.l = (uint)(md_main.g_md_bus.read16(g_reg_PC) & 0x00FF);
                    g_reg_PC += 2;
                    break;

                case 1: // Word
                    g_work_val2.l = md_main.g_md_bus.read16(g_reg_PC);
                    g_reg_PC += 2;
                    break;

                default: // Long (2)
                    g_work_val2.l = md_main.g_md_bus.read32(g_reg_PC);
                    g_reg_PC += 4;
                    break;
            }

            // <ea> AND #imm → <ea>
            adressing_func_address(g_op3, g_op4, g_op2);
            g_work_val1.l = adressing_func_read(g_op3, g_op4, g_op2);
            g_work_data.l = g_work_val1.l & g_work_val2.l;
            
            // Debug logging for Madou
            if (g_reg_PC >= 0x013A00 && g_reg_PC <= 0x013B00)
            {
                Console.WriteLine($"[ANDI-DEBUG] PC=0x{g_reg_PC:X6} op2={g_op2} val1=0x{g_work_val1.l:X8} val2=0x{g_work_val2.l:X8} result=0x{g_work_data.l:X8} dest=({g_op3},{g_op4})");
            }
            
            adressing_func_write(g_op3, g_op4, g_op2, g_work_data.l);

            // Flaggar: N/Z set enligt resultat; V/C clear; X opåverkad
            uint w_mask = MASKBIT[g_op2];
            uint w_most = MOSTBIT[g_op2];

            g_status_N = (g_work_data.l & w_most) != 0;
            g_status_Z = (g_work_data.l & w_mask) == 0;
            g_status_V = false;
            g_status_C = false;
            // g_status_X unaffected
        }
    }
}
