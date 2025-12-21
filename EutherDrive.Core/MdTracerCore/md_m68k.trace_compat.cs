namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        // Om du redan har CPU_Trace_pop med 1-2 args någonstans,
        // kan du forwarda. Annars gör den no-op för att få build.

        public static void CPU_Trace_pop(object a, object b, object c)
        {
            // no-op (trace optional)
            // eller: CPU_Trace_pop(a, b); om en sådan overload finns
        }
    }
}
