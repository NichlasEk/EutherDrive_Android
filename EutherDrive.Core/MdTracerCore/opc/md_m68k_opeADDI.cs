using System;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ADDI()
        {
            // ADDI #imm,<ea>   (size i g_op2: 0=byte, 1=word, 2=long)

            // Klockor: dina ursprungliga regler, bara uppställda tydligare
            if (g_op3 == 0)            // (troligen register/enkelt EA)
                g_clock = (g_op2 == 2) ? 16 : 8;
            else
                g_clock = (g_op2 == 2) ? 22 : 13;

            g_reg_PC += 2; // hoppa över op-word

            // Läs omedelbart värde beroende på storlek
            switch (g_op2)
            {
                case 0: // .B – immediate kommer som 16-bit, använd lågbyte
                    g_work_val2.l = (uint)(md_main.g_md_bus.read16(g_reg_PC) & 0x00FF);
                    g_reg_PC += 2;
                    break;

                case 1: // .W
                    g_work_val2.l = md_main.g_md_bus.read16(g_reg_PC);
                    g_reg_PC += 2;
                    break;

                default: // 2 => .L
                    g_work_val2.l = md_main.g_md_bus.read32(g_reg_PC);
                    g_reg_PC += 4;
                    break;
            }

            // Adressera destination, läs operand
            adressing_func_address(g_op3, g_op4, g_op2);
            g_work_val1.l = adressing_func_read(g_op3, g_op4, g_op2);

            // Utför addition (maskning/flaggar hanteras nedan)
            g_work_data.l = g_work_val1.l + g_work_val2.l;

            // Skriv tillbaka med vald storlek
            adressing_func_write(g_op3, g_op4, g_op2, g_work_data.l);

            // Flagguppdatering enligt 68000 ADD (samma som din ADD-impl)
            uint w_mask = MASKBIT[g_op2];
            uint w_most = MOSTBIT[g_op2];

            bool SMC = (g_work_val2.l & w_most) != 0;  // Source Most-significant bit
            bool DMC = (g_work_val1.l & w_most) != 0;  // Dest   Most-significant bit
            bool RMC = (g_work_data.l & w_most) != 0;  // Result Most-significant bit

            g_status_N = RMC;
            g_status_Z = (g_work_data.l & w_mask) == 0;
            g_status_V = (SMC ^ RMC) & (DMC ^ RMC);
            g_status_C = (SMC && DMC) || (!RMC && DMC) || (SMC && !RMC);
            g_status_X = g_status_C;
        }
    }
}
