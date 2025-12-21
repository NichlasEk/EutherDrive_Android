namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // Endast wrappers för blandade typer → ropar på den primära uint/uint/uint-varianten
        internal static void CPU_Trace_pop(uint newPc, ushort oldPc, uint spAfter)
        => CPU_Trace_pop(newPc, (uint)oldPc, spAfter);

        internal static void CPU_Trace_pop(uint newPc, uint oldPc, ushort spAfter)
        => CPU_Trace_pop(newPc, oldPc, (uint)spAfter);

        internal static void CPU_Trace_pop(uint newPc, ushort oldPc, ushort spAfter)
        => CPU_Trace_pop(newPc, (uint)oldPc, (uint)spAfter);
    }
}
