namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // --- Timing / cycle accounting ---
    internal static int g_clock;
    internal static int g_clock_now;
    internal static int g_clock_total;

    // --- Current opcode decode fields ---
    internal static ushort g_opcode;
    internal static byte g_op, g_op1, g_op2, g_op3, g_op4;

    // --- Registers (68k) ---
    internal static uint g_reg_PC;

    // IMPORTANT: must be md_u32 (union) so .b/.w/.l works in opcode files
    internal static readonly md_u32[] g_reg_data = new md_u32[8];
    internal static readonly md_u32[] g_reg_addr = new md_u32[8];

   // internal static ushort g_reg_SR;

    // --- Condition codes ---
    internal static bool g_status_X;
    internal static bool g_status_N;
    internal static bool g_status_Z;
    internal static bool g_status_V;
    internal static bool g_status_C;

    // (om din kod använder CCR-mask separat)
  //  internal static ushort g_status_CCR;

    // --- Interrupt mask (om den används så i din port) ---
    internal static int g_status_interrupt_mask;

    // SR-high bits (trace/supervisor + reserved/unused i denna port)
    internal bool g_status_T;
    internal bool g_status_S;

    // Dessa “B”-bitar är nästan säkert reserverade/unused i SR
    // men porten vill kunna bära runt dem.
    internal bool g_status_B1;
    internal bool g_status_B2;
    internal bool g_status_B3;
    internal bool g_status_B4;
    internal bool g_status_B5;
    internal bool g_status_B6;

    // User Stack Pointer (USP)
    internal uint g_reg_addr_usp;
}
