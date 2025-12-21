// Gör att opcode-filerna i denna assembly kan använda g_clock, g_reg_PC, g_status_* osv
// utan att behöva skriva md_m68k.g_clock överallt.

global using static EutherDrive.Core.MdTracerCore.md_m68k;


