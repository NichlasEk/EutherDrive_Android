using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_ROL_reg()
        {
            // ROL Dx, Dy or ROL #<data>, Dy
            // Format: 1110 0sss 1mm0 rrrr
            // sss = shift count (0=8, 1-7=1-7) or register
            // mm = size (00=byte, 01=word, 10=long)
            // rrrr = data register
            
            int w_size = g_op2 & 0x03; // mm
            int w_ir = g_op3 & 0x04;   // immediate/register flag
            int w_dr = g_op2 & 0x04;   // direction (0=right, 1=left) - but ROL is always left?
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
            
            // ROL - Rotate Left (bits rotate within register, MSB→LSB)
            // For ROL, bits rotate: MSB → C → LSB
            uint mask = MASKBIT[w_size];
            uint mostbit = MOSTBIT[w_size];
            
            for (int i = 0; i < wcnt; i++)
            {
                g_clock += 2;
                bool msb = (g_work_data.l & mostbit) != 0;
                g_work_data.l = (g_work_data.l << 1) & mask;
                if (msb) g_work_data.l |= 0x01; // MSB goes to LSB
                g_status_C = msb;
            }
            
            if(w_ir != 0) g_clock += 2; // Extra cycles for register shift count
            
            // Write result
            write_g_reg_data(g_op4, w_size, g_work_data.l);
            
            // Debug logging for Madou
            if (g_reg_PC >= 0x013A00 && g_reg_PC <= 0x013B00)
            {
                Console.WriteLine($"[ROL-DEBUG] PC=0x{g_reg_PC:X6} size={w_size} count={wcnt} src=0x{read_g_reg_data(g_op4, w_size):X8} result=0x{g_work_data.l:X8} reg={g_op4}");
            }
            
            // Set flags
            uint w_mask = MASKBIT[g_op2 & 0x03];
            uint w_most = MOSTBIT[g_op2 & 0x03];
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
                
            // V flag: always cleared for ROL
            g_status_V = false;
            
            // X flag: set equal to C flag
            g_status_X = g_status_C;
        }
    }
}