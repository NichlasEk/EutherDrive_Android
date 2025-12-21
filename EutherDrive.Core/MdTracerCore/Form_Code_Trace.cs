namespace EutherDrive.Core.MdTracerCore;

public sealed class Form_Code_Trace
{
    // Matchar det opcode-filerna refererar till:
    public enum STACK_LIST_TYPE
    {
        HINT, VINT, EXT,
        TRAP, TRAPV,
        BSR, JSR,
        RTS, RTR, RTE
    }

    public void CPU_Trace(uint pc)
    {
        // headless: noop
    }

    public void CPU_Trace_push(STACK_LIST_TYPE type, uint vector, uint start, uint pc, uint sp)
    {
        // headless: noop
    }

    // Call-sites som skickar (pc, sp)
    public void CPU_Trace_pop(uint pc, uint sp)
    {
        // headless: noop
    }

    // Call-sites som skickar (newPc, oldPc, spAfter)
    public void CPU_Trace_pop(uint newPc, uint oldPc, uint spAfter)
    {
        // headless: noop
        // Long-term: du kan använda oldPc för callstack/trace, men nu räcker noop.
    }
}
