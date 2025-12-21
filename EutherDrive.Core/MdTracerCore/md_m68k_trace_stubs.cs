namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // Headless port: trace UI saknas.
    internal static bool g_form_code_trace = false;

    // Headless port: gamla opcode-filer anropar den här med 3 args.
    // Vi gör no-op tills du vill implementera tracer på riktigt.
  //  internal static void CPU_Trace_pop(uint a, uint b, uint c) { }

    // Extra overload om någon fil råkar skicka ushort/short som tredje parameter.
  //  internal static void CPU_Trace_pop(uint a, uint b, short c) { }
}
