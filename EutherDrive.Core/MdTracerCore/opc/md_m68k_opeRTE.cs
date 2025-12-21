namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // RTE: Return from Exception
    internal static void analyse_RTE()
    {
        g_clock += 20;

        uint oldPc = g_reg_PC;

        // Clear V-Interrupt pending flag in VDP status (om VDP finns)
        if (md_main.g_md_vdp != null)
            md_main.g_md_vdp.g_vdp_status_7_vinterrupt = 0;

        // Clear whichever interrupt was active
        if (g_interrupt_H_act) g_interrupt_H_act = false;
        else if (g_interrupt_V_act) g_interrupt_V_act = false;
        else if (g_interrupt_EXT_act) g_interrupt_EXT_act = false;

        // Restore SR and PC from stack
        g_reg_SR = stack_pop16();
        g_reg_PC = stack_pop32();

        // Trace (headless: bara om flaggan är på; stubben är no-op ändå)
        if (g_form_code_trace)
            CPU_Trace_pop(g_reg_PC, oldPc, g_reg_addr[7].l);
    }
}

