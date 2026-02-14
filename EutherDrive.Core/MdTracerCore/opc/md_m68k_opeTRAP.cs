using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_TRAP()
        {
            g_clock += 37;
            g_reg_PC += 2;
            RaiseException("TRAP", (uint)(0x0080 + ((g_opcode & 0x0f) << 2)));
        }
   }
}
