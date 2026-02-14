using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private void analyse_DBcc()
        {
            g_clock += 12;
            uint startPc = g_reg_PC;
            g_reg_PC += 2;
            uint basePc = g_reg_PC;
            short displacement = (short)md_main.g_md_bus.read16(g_reg_PC);
            g_reg_PC += 2;

            bool conditionFalse = !g_flag_chack[(g_opcode >> 8) & 0x0f]();
            ushort before = g_reg_data[g_op4].w;
            ushort after = before;
            bool branch = false;
            if (conditionFalse)
            {
                after = (ushort)(before - 1);
                g_reg_data[g_op4].w = after;
                branch = after != 0xFFFF;
            }
            if (branch)
                g_reg_PC = (uint)(basePc + displacement);

            MaybeLogDbra(startPc, before, after, branch, displacement, g_op4);
            if (g_opcode == 0x51C9 && ShouldTraceOpcode(TraceOpcode51C9, startPc))
            {
                Console.WriteLine(
                    $"[OP51C9] pc=0x{startPc:X6} D{g_op4} pre=0x{before:X4} post=0x{after:X4} branch={(branch ? 1 : 0)} disp=0x{displacement:X4}");
            }
        }

        private void MaybeLogDbra(uint startPc, ushort before, ushort after, bool branch, short displacement, int regIndex)
        {
            if (!TraceDbra)
                return;

            if (startPc != 0x0EC03C)
                return;

            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            if (frame == _lastDbraLogFrame)
                return;

            _lastDbraLogFrame = frame;
            Console.WriteLine($"[DBRA] PC=0x{startPc:X6} D{regIndex} pre=0x{before:X4} post=0x{after:X4} branch={(branch ? 1 : 0)} disp=0x{displacement:X4}");
        }
   }
}
