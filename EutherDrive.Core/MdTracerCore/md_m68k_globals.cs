using System;

namespace EutherDrive.Core.MdTracerCore;

internal partial class md_m68k
{
    // OPINFO måste vara class (inte struct) eftersom md_m68k_initialize.cs gör null-check
    internal sealed class OPINFO
    {
        public Action? opcode;

        // Dessa används av opcode_add(...) i din initialize/reset-port
        public string opname_org = "";
        public string opname     = "";
        public string opname_out = "";
        public string format     = "";

        public int opleng;
        public int datasize;
        public int memaccess;
    }

    // --- Fetch/decode state ---
    internal static ushort g_opcode;
    internal static byte g_op, g_op1, g_op2, g_op3, g_op4;

    // --- Core clocks ---
    internal static int g_clock;
    internal static int g_clock_total;
    internal static int g_clock_now;

    // --- 68k regs (måste vara md_u32 så .l/.w/.b finns) ---
    internal static uint g_reg_PC;

    internal static readonly md_u32[] g_reg_data = new md_u32[8];
    internal static readonly md_u32[] g_reg_addr = new md_u32[8];

    // OBS:
    // Definiera INTE g_reg_SR här som field.
    // Du har (eller bör ha) g_reg_SR som property i md_m68k_sub.cs som packar status-bitarna.

    internal static md_u32 g_reg_addr_usp; // USP måste kunna ha .l

    // --- Reset vectors ---
    internal static uint g_initial_PC;
    internal static uint g_stack_top;

    // --- Interrupt flags (VDP och RTE m.fl. behöver dessa) ---
    internal static bool g_interrupt_V_req, g_interrupt_H_req, g_interrupt_EXT_req;
    internal static bool g_interrupt_V_act, g_interrupt_H_act, g_interrupt_EXT_act;

    internal static bool g_68k_stop;

    // --- Status/CCR bits ---
    internal static bool g_status_T, g_status_S;
    internal static bool g_status_B1, g_status_B2, g_status_B3, g_status_B4, g_status_B5, g_status_B6;
    internal static bool g_status_X, g_status_N, g_status_Z, g_status_V, g_status_C;

    internal static byte g_status_interrupt_mask;

    // --- Flag check table (måste INTE vara readonly pga init i initialize()) ---
 //   internal static Func<bool>[]? g_flag_chack;

    // --- Opcode table (måste INTE vara readonly pga init i initialize()) ---
    internal static OPINFO[]? g_opcode_info;
}
