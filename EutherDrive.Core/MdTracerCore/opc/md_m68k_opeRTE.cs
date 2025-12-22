namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // RTE: Return from Exception
    internal static void analyse_RTE()
    {
        g_clock += 20;

        uint oldPc = g_reg_PC;

        // Clear whichever interrupt was active
        if (g_interrupt_H_act) g_interrupt_H_act = false;
        else if (g_interrupt_V_act) g_interrupt_V_act = false;
        else if (g_interrupt_EXT_act) g_interrupt_EXT_act = false;

        // Restore SR and PC from stack (SR update handles S-bit swap)
        ushort newSr = stack_pop16();
        uint newPc = stack_pop32();
        g_reg_SR = newSr;
        g_reg_PC = newPc;

        if (_intLogRemaining > 0)
        {
            _intLogRemaining--;
            MdLog.WriteLine($"[m68k int] RTE pc=0x{g_reg_PC:X6} sr=0x{newSr:X4} sp=0x{g_reg_addr[7].l:X8}");
        }

        // Trace (headless: bara om flaggan är på; stubben är no-op ändå)
        if (g_form_code_trace)
            CPU_Trace_pop(g_reg_PC, oldPc, g_reg_addr[7].l);
    }
}
