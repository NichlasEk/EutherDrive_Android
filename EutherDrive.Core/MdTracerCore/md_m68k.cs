using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // ... (alla dina fält osv som innan)

        // --- Headless trace helpers (ersätter Form_Code_Trace) ---
        [Conditional("DEBUG")]
        internal static void TraceCpu(uint pc)
        {
            Debug.WriteLine($"[m68k] PC={pc:X6}");
        }

        [Conditional("DEBUG")]
        internal static void TracePush(string kind, uint vector, uint start, uint pc, uint sp)
        {
            Debug.WriteLine($"[m68k] {kind} vec={vector:X4} start={start:X6} pc={pc:X6} sp={sp:X8}");
        }
        // ----------------------------------------------------------

        public md_m68k()
        {
            initialize();
            initialize2();
        }

        public void run(int in_clock)
        {
            g_clock_total += in_clock;
            while (g_clock_now < g_clock_total)
            {
                // md_main.g_form_code_trace.CPU_Trace(g_reg_PC);
                TraceCpu(g_reg_PC); // headless

                interrupt_chk();
                g_clock = md_main.g_md_vdp.dma_status_update();
                if (g_clock == 0)
                {
                    g_opcode = read16(g_reg_PC);
                    g_op  = (byte)(g_opcode >> 12);
                    g_op1 = (byte)((g_opcode >> 9) & 0x07);
                    g_op2 = (byte)((g_opcode >> 6) & 0x07);
                    g_op3 = (byte)((g_opcode >> 3) & 0x07);
                    g_op4 = (byte)(g_opcode & 0x07);

                    if (g_68k_stop) { g_clock_now = g_clock_total; break; }

                    g_opcode_info[g_opcode].opcode();
                }
                g_clock_now += g_clock;
            }
        }

        private void interrupt_chk()
        {
            if (g_interrupt_H_req && (g_status_interrupt_mask < 4)
                && (md_main.g_md_vdp.g_vdp_reg_0_4_hinterrupt == 1))
            {
                uint w_start_address = read32(0x0070);
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...HINT...);
                TracePush("HINT", 0x0070, w_start_address, g_reg_PC, g_reg_addr[7].l);

                ushort w_data = g_reg_SR;
                stack_push16(w_data);
                g_reg_PC = w_start_address;
                g_status_interrupt_mask = 4;
                g_interrupt_H_req = false;
                g_interrupt_H_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_V_req && (g_status_interrupt_mask < 6)
                && (md_main.g_md_vdp.g_vdp_reg_1_5_vinterrupt == 1)
                && !g_interrupt_H_act)
            {
                uint w_start_address = read32(0x0078);
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...VINT...);
                TracePush("VINT", 0x0078, w_start_address, g_reg_PC, g_reg_addr[7].l);

                ushort w_data = g_reg_SR;
                stack_push16(w_data);
                g_reg_PC = w_start_address;
                g_status_interrupt_mask = 6;
                g_interrupt_V_req = false;
                g_interrupt_V_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_EXT_req && (g_status_interrupt_mask < 2))
            {
                uint w_start_address = read32(0x0068);
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...EXT...);
                TracePush("EXT", 0x0068, w_start_address, g_reg_PC, g_reg_addr[7].l);

                ushort w_data = g_reg_SR;
                stack_push16(w_data);
                g_reg_PC = w_start_address;
                g_status_interrupt_mask = 2;
                g_interrupt_EXT_req = false;
                g_interrupt_EXT_act = true;
                g_68k_stop = false;
            }
        }

        // resten av filen (traceout/logout/logout2 etc) kan vara kvar oförändrat
    }
}
