using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_MOVE_b()
        {
            int w_size = 0;
            int w_src = (g_op3 < 7) ? g_op3 : 7 + g_op4;
            int w_dest= (g_op2 < 7) ? g_op2 : 7 + g_op1;
            int w_clock = 0;
            uint pcBefore = (uint)(g_reg_PC);
            uint a2Before = 0;
            uint d0Before = 0;
            uint srcAddr = 0;
            bool trace102a = false;
            switch(g_op)
            {
                case 1:
                    w_size = 0;
                    w_clock = MOVE_CLOCK[w_src, w_dest];
                    break;
                case 3: 
                    w_size = 1; 
                    w_clock = MOVE_CLOCK[w_src, w_dest];
                    break; 
                default: 
                    w_size = 2; 
                    w_clock = MOVE_CLOCK_L[w_src, w_dest];
                    break; 
            } 
            g_reg_PC += 2;
            if (g_opcode == 0x102A)
            {
                pcBefore = (uint)(g_reg_PC - 2);
                a2Before = g_reg_addr[2].l;
                d0Before = g_reg_data[0].l;
            }
            adressing_func_address(g_op3, g_op4, w_size);
            g_work_data.l = (uint)adressing_func_read(g_op3, g_op4, w_size);
            if (g_opcode == 0x102A)
            {
                srcAddr = g_analyze_address;
                trace102a = ShouldTraceOpcode(TraceOpcode102A, pcBefore);
            }
            adressing_func_address(g_op2, g_op1, w_size); 
            adressing_func_write(g_op2, g_op1, w_size, g_work_data.l);
            if (trace102a)
            {
                uint d0After = g_reg_data[0].l;
                byte srcVal = (byte)(g_work_data.l & 0xFF);
                Console.WriteLine(
                    $"[OP102A] pc=0x{pcBefore:X6} A2=0x{a2Before:X8} src=0x{srcAddr:X6} val=0x{srcVal:X2} D0:0x{d0Before:X8}->0x{d0After:X8}");
            }
            g_clock = w_clock;
            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
        }
        private void analyse_MOVE_w()
        {
            int w_size = 0;
            int w_src = (g_op3 < 7) ? g_op3 : 7 + g_op4;
            int w_dest= (g_op2 < 7) ? g_op2 : 7 + g_op1;
            int w_clock = 0;
            switch(g_op)
            {
                case 1:
                    w_size = 0;
                    w_clock = MOVE_CLOCK[w_src, w_dest];
                    break;
                case 3: 
                    w_size = 1; 
                    w_clock = MOVE_CLOCK[w_src, w_dest];
                    break; 
                default: 
                    w_size = 2; 
                    w_clock = MOVE_CLOCK_L[w_src, w_dest];
                    break; 
            } 
            g_reg_PC += 2; 
            adressing_func_address(g_op3, g_op4, w_size); 
            g_work_data.l = (uint)adressing_func_read(g_op3, g_op4, w_size); 
            adressing_func_address(g_op2, g_op1, w_size); 
            adressing_func_write(g_op2, g_op1, w_size, g_work_data.l); 
            g_clock = w_clock;
            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
        }
        private void analyse_MOVE_l()
        {
            int w_size = 0;
            int w_src = (g_op3 < 7) ? g_op3 : 7 + g_op4;
            int w_dest= (g_op2 < 7) ? g_op2 : 7 + g_op1;
            int w_clock = 0;
            switch(g_op)
            {
                case 1:
                    w_size = 0;
                    w_clock = MOVE_CLOCK[w_src, w_dest];
                    break;
                case 3: 
                    w_size = 1; 
                    w_clock = MOVE_CLOCK[w_src, w_dest];
                    break; 
                default: 
                    w_size = 2; 
                    w_clock = MOVE_CLOCK_L[w_src, w_dest];
                    break; 
            } 
            g_reg_PC += 2; 
            adressing_func_address(g_op3, g_op4, w_size); 
            g_work_data.l = (uint)adressing_func_read(g_op3, g_op4, w_size); 
            adressing_func_address(g_op2, g_op1, w_size); 
            adressing_func_write(g_op2, g_op1, w_size, g_work_data.l); 
            g_clock = w_clock;
            uint w_mask = MASKBIT[w_size];
            uint w_most = MOSTBIT[w_size];
            g_status_N = ((g_work_data.l & w_most) == w_most) ? true: false;
            g_status_Z = ((g_work_data.l & w_mask) == 0) ? true: false;
            g_status_V = false;
            g_status_C = false;
        }
   }
}
