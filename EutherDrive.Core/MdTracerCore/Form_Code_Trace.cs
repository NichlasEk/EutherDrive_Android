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

    public void CPU_Trace(uint pc) { /* headless: noop */ }

    public void CPU_Trace_push(STACK_LIST_TYPE type, uint vector, uint start, uint pc, uint sp)
    {
        /* headless: noop */
    }

    public void CPU_Trace_pop(uint pc, uint sp)
    {
        /* headless: noop */
    }
}
