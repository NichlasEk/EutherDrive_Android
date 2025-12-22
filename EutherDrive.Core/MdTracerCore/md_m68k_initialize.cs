using System;
using System.Diagnostics;

namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        public void initialize()
        {
            // Register noll
            for (int i = 0; i < 8; i++)
            {
                g_reg_data[i].l = 0;
                g_reg_addr[i].l = 0;
            }

            // Minnesområde: 16 MiB (Mega Drive 24-bit space)
            g_memory ??= new byte[0x1000000];

            // Opcode-info-tabell: 0..65535
            g_opcode_info ??= new OPINFO[65536];

            // Flag-check tabellen (instansbunden)
            g_flag_chack = new Func<bool>[]
            {
                () => true,                         // T
                () => false,                        // F
                () => (!g_status_C && !g_status_Z), // HI
                () => (g_status_C || g_status_Z),   // LS
                () => (!g_status_C),                // CC
                () => (g_status_C),                 // CS
                () => (!g_status_Z),                // NE
                () => (g_status_Z),                 // EQ
                () => (!g_status_V),                // VC
                () => (g_status_V),                 // VS
                () => (!g_status_N),                // PL
                () => (g_status_N),                 // MI
                () => !(g_status_N ^ g_status_V),   // GE
                () =>  (g_status_N ^ g_status_V),   // LT
                () => !(g_status_N ^ g_status_V) && !g_status_Z, // GT
                () =>  (g_status_N ^ g_status_V) ||  g_status_Z  // LE
            };


            // (Valfritt) Om du vill kunna se att init körs i logg/debug:
            // Debug.WriteLine("md_m68k.initialize done");
        }

        public void reset()
        {
            // Säkerställ att initialize() körts minst en gång
            initialize();

            // Töm RAM
            Array.Clear(g_memory, 0, g_memory.Length);

            // Kopiera ROM till minnet från adress 0
            if (md_main.g_md_cartridge != null &&
                md_main.g_md_cartridge.g_file != null &&
                md_main.g_md_cartridge.g_file.Length > 0)
            {
                int copySize = Math.Min(md_main.g_md_cartridge.g_file.Length, g_memory.Length);
                Buffer.BlockCopy(md_main.g_md_cartridge.g_file, 0, g_memory, 0, copySize);
            }

            // Init PC/SP från vektor-tabellen (0=initial SP, 4=initial PC)
            uint initialSp = read32(0);
            uint initialPc = read32(4);

            g_initial_PC = initialPc;
            g_reg_PC = initialPc;

            g_stack_top = initialSp;

            // Nollställ Dn/An igen (för tydlighet)
            for (int i = 0; i < 8; i++)
            {
                g_reg_data[i].l = 0;
                g_reg_addr[i].l = 0;
            }

            // A7 = stack pointer efter reset
            g_reg_addr[7].l = g_stack_top;

            // USP (värdet i sig) – många implementationer nollar den vid reset
            g_reg_addr_usp.l = 0;

            // ---- SR/CCR flags ----
            g_status_T = false;
            g_status_B1 = false;
            g_status_S = true;                 // Supervisor efter reset
            g_status_B2 = false;
            g_status_B3 = false;

            // Interrupt mask = 7 (alla maskade) efter reset
            g_status_interrupt_mask = 7;

            // CCR bits
            g_status_B4 = false;
            g_status_B5 = false;
            g_status_B6 = false;
            g_status_X = false;
            g_status_N = false;
            g_status_Z = false;
            g_status_V = false;
            g_status_C = false;

            // ---- IRQ flags ----
            g_interrupt_V_req = false;
            g_interrupt_H_req = false;
            g_interrupt_EXT_req = false;

            g_interrupt_V_act = false;
            g_interrupt_H_act = false;
            g_interrupt_EXT_act = false;

            // CPU stop state
            g_68k_stop = false;

            // ---- Clocks ----
            g_clock_total = 0;
            g_clock_now = 0;
            g_clock = 0;

            _bootTraceEnabled = true;
            _bootTraceRemaining = 200;
            _bootTraceProbeRemaining = 16;
            _btstLogRemaining = 16;
            _bneLogRemaining = 32;
            _d1LogRemaining = 64;
            _d1LogLastPc = 0;
            _pc466LogRemaining = 32;
            MdLog.WriteLine("[md_m68k] Boot trace armed");
        }

        private void opcode_add(
            int in_opnum,
            Action in_func,
            string in_opname_org,
            string in_opname,
            string in_opname_out,
            string in_format,
            int in_opleng,
            int in_datasize,
            int in_memaccess)
        {
            g_opcode_info ??= new OPINFO[65536];

            var op = g_opcode_info[in_opnum] ??= new OPINFO();

            op.opcode     = in_func;
            op.opname_org = in_opname_org;
            op.opname     = in_opname;
            op.opname_out = in_opname_out.ToLowerInvariant();
            op.format     = in_format;
            op.opleng     = in_opleng;
            op.datasize   = in_datasize;
            op.memaccess  = in_memaccess;

            g_opcode_info[in_opnum] = op;
        }


    }
}
