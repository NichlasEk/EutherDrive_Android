using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ROR_reg()
        {
            // ROR Dx, Dy or ROR #<data>, Dy
            // Format: 1110 0sss 1mm1 rrrr
            // sss = shift count (0=8, 1-7=1-7) or register
            // mm = size (00=byte, 01=word, 10=long)
            // rrrr = data register
            
            int w_size = g_op2 & 0x03; // mm
            int w_ir = g_op3 & 0x04;   // immediate/register flag
            if (w_size == 2) g_clock = 8; else g_clock = 6;
            g_reg_PC += 2;
            uint wcnt = 0;
            
            // Get shift count
            if(w_ir == 0)
            {
                // Immediate count (0=8, 1-7=1-7)
                wcnt = (uint)(g_op1 & 0x07);
                if(wcnt == 0) wcnt = 8;
            }
            else
            {
                // Register count (modulo 64)
                wcnt = g_reg_data[g_op1].l & 0x3f;
            }
            
            // Read source data
            g_work_data.l = read_g_reg_data(g_op4, w_size);
            g_status_V = false;
            g_status_C = false;
            
            // ROR - Rotate Right (bits rotate through C flag)
            uint mask = MASKBIT[w_size];
            uint msb_bit = (w_size == 2 ? 0x80000000u : (w_size == 1 ? 0x8000u : 0x80u));
            for (int i = 0; i < wcnt; i++)
            {
                g_clock += 2;
                bool lsb = (g_work_data.l & 0x01) != 0;
                g_work_data.l = (g_work_data.l >> 1);
                if (lsb) g_work_data.l |= msb_bit; // LSB goes to MSB
                g_status_C = lsb;
            }
            
            if(w_ir != 0) g_clock += 2; // Extra cycles for register shift count
            
            // Write result
            write_g_reg_data(g_op4, w_size, g_work_data.l);
            
            // Set flags
            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];
            g_work_data.l &= w_mask;
            
            // N flag: most significant bit of result
            if ((g_work_data.l & w_most) != 0) 
                g_status_N = true; 
            else 
                g_status_N = false;
                
            // Z flag: result is zero
            if (g_work_data.l == 0) 
                g_status_Z = true; 
            else 
                g_status_Z = false;
                
            // V flag: always cleared for ROR
            g_status_V = false;
            
            // X flag: set equal to C flag
            g_status_X = g_status_C;
        }
    }
}