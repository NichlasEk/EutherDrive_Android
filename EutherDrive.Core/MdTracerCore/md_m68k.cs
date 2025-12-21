using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static bool _bootTraceEnabled = true;
        private static int _bootTraceRemaining = 200;
        private static int _bootTraceProbeRemaining = 16;
        private static int _btstLogRemaining = 16;
        private static int _bneLogRemaining = 32;
        private static int _d1LogRemaining = 64;
        private static uint _d1LogLastPc;
        private static int _pc466LogRemaining = 32;
        private static int _intLogRemaining = 32;
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

                    if (_bootTraceEnabled && _bootTraceRemaining == 200)
                        Console.WriteLine("[m68k boot] trace start");

                    if (_bootTraceEnabled && _bootTraceRemaining > 0)
                    {
                        Console.WriteLine($"[m68k boot] PC=0x{g_reg_PC:X6} OP=0x{g_opcode:X4} D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} A0=0x{g_reg_addr[0].l:X8} A1=0x{g_reg_addr[1].l:X8}");
                        if (g_opcode == 0x0111 && _bootTraceProbeRemaining > 0)
                        {
                            uint a1 = g_reg_addr[1].l;
                            byte val = md_main.g_md_bus != null ? md_main.g_md_bus.read8(a1) : read8(a1);
                            Console.WriteLine($"[m68k boot] OP=0x0111 probe A1=0x{a1:X6} -> 0x{val:X2}");
                            _bootTraceProbeRemaining--;
                        }
                        _bootTraceRemaining--;
                        if (_bootTraceRemaining == 0)
                            _bootTraceEnabled = false;
                    }

                    if ((g_reg_PC == 0x000466 || g_reg_PC == 0x000464 || g_reg_PC == 0x000468) && _pc466LogRemaining > 0)
                    {
                        _pc466LogRemaining--;
                        ushort op0 = g_opcode;
                        ushort op1 = read16(g_reg_PC + 2);
                        ushort op2 = read16(g_reg_PC + 4);
                        Console.WriteLine($"[m68k] PC=0x{g_reg_PC:X6} OP=0x{op0:X4} N1=0x{op1:X4} N2=0x{op2:X4} SR=0x{g_reg_SR:X4} SP=0x{g_reg_addr[7].l:X8}");
                    }

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
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(0x0070);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    Console.WriteLine($"[m68k int] HINT vec=0x0070 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...HINT...);
                TracePush("HINT", 0x0070, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (4 << 8));
                g_interrupt_H_req = false;
                g_interrupt_H_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_V_req && (g_status_interrupt_mask < 6)
                && (md_main.g_md_vdp.g_vdp_reg_1_5_vinterrupt == 1)
                && !g_interrupt_H_act)
            {
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(0x0078);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    Console.WriteLine($"[m68k int] VINT vec=0x0078 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...VINT...);
                TracePush("VINT", 0x0078, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (6 << 8));
                g_interrupt_V_req = false;
                g_interrupt_V_act = true;
                g_68k_stop = false;
            }
            else if (g_interrupt_EXT_req && (g_status_interrupt_mask < 2))
            {
                ushort oldSr = g_reg_SR;
                if (!g_status_S)
                {
                    SwapStacks();
                    g_status_S = true;
                }

                uint w_start_address = read32(0x0068);
                if (_intLogRemaining > 0)
                {
                    _intLogRemaining--;
                    Console.WriteLine($"[m68k int] EXT vec=0x0068 start=0x{w_start_address:X6} pc=0x{g_reg_PC:X6} sr=0x{oldSr:X4} sp=0x{g_reg_addr[7].l:X8}");
                }
                stack_push32(g_reg_PC);
                // md_main.g_form_code_trace.CPU_Trace_push(...EXT...);
                TracePush("EXT", 0x0068, w_start_address, g_reg_PC, g_reg_addr[7].l);

                stack_push16(oldSr);
                g_reg_PC = w_start_address;
                g_reg_SR = (ushort)((oldSr & 0xF8FF) | 0x2000 | (2 << 8));
                g_interrupt_EXT_req = false;
                g_interrupt_EXT_act = true;
                g_68k_stop = false;
            }
        }

        // resten av filen (traceout/logout/logout2 etc) kan vara kvar oförändrat
    }
}
