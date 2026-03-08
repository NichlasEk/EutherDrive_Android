namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // RTE: Return from Exception
    internal void analyse_RTE()
    {
        g_clock += 20;

        uint oldPc = g_reg_PC;

        if (!g_status_S)
        {
            RaiseException("PRIV", 0x0020);
            return;
        }

        // Clear active flag by current interrupt mask level (pre-RTE SR).
        // This keeps nested IRQ bookkeeping correct (e.g. VINT preempting HINT).
        int currentMask = (g_reg_SR >> 8) & 0x07;
        if (currentMask >= 6)
            g_interrupt_V_act = false;
        else if (currentMask == 4)
            g_interrupt_H_act = false;
        else if (g_interrupt_EXT_act)
            g_interrupt_EXT_act = false;

        // Restore SR and PC from stack (SR update handles S-bit swap)
        ushort newSr = stack_pop16();
        uint newPc = stack_pop32();
        WriteSR(newSr);
        g_reg_PC = newPc;

        if (_intLogRemaining > 0)
        {
            _intLogRemaining--;
            MdLog.WriteLine($"[m68k int] RTE pc=0x{g_reg_PC:X6} sr=0x{newSr:X4} sp=0x{g_reg_addr[7].l:X8}");
        }

        // Trace (headless: bara om flaggan är på; stubben är no-op ändå)
        if (md_main.g_form_code_trace != null)
            md_main.g_form_code_trace.CPU_Trace_pop(g_reg_PC, oldPc, g_reg_addr[7].l);
    }
}
