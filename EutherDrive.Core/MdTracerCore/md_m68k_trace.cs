namespace EutherDrive.Core.MdTracerCore
{
    internal interface IM68kTracer
    {
        void TracePop(uint newPc, uint oldPc, uint spAfter);
    }

    internal partial class md_m68k
    {
        // Koppla in en riktig tracer senare (valfritt).
        internal static IM68kTracer? Tracer;

        // ✅ Canonical: det är denna signatur opcode-filerna ska kunna använda.
        internal static void CPU_Trace_pop(uint newPc, uint oldPc, uint spAfter)
        => Tracer?.TracePop(newPc, oldPc, spAfter);

        // ✅ Convenience: om någon gammal kod bara har new/old PC.
        internal static void CPU_Trace_pop(uint newPc, uint oldPc)
        => CPU_Trace_pop(newPc, oldPc, g_reg_addr[7].l);

        // ✅ Om någon callsite råkar skicka sp som md_u32:
        internal static void CPU_Trace_pop(uint newPc, uint oldPc, md_u32 spAfter)
        => CPU_Trace_pop(newPc, oldPc, spAfter.l);
    }
}
