using System;
using static EutherDrive.Core.MdTracerCore.md_m68k;
namespace EutherDrive.Core.MdTracerCore
{
    internal partial class md_m68k
    {
        private static readonly bool TraceDbfSub =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DBF_SUB"), "1", StringComparison.Ordinal);
        private static int _traceDbfSubRemaining = 64;
        private static readonly bool TraceSonic3Dbmi =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SONIC3_DBMI"), "1", StringComparison.Ordinal);
        private static readonly int TraceSonic3DbmiLimit =
            ParseWatchLimit("EUTHERDRIVE_TRACE_SONIC3_DBMI_LIMIT");
        private static int _traceSonic3DbmiRemaining = TraceSonic3Dbmi ? TraceSonic3DbmiLimit : 0;

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
            if (TraceDbfSub && _traceDbfSubRemaining > 0 && startPc >= 0x0002E0 && startPc <= 0x0002E2)
            {
                _traceDbfSubRemaining--;
                Console.WriteLine(
                    $"[DBF-SUB] pc=0x{startPc:X6} op=0x{g_opcode:X4} D{g_op4} pre=0x{before:X4} post=0x{after:X4} branch={(branch ? 1 : 0)} " +
                    $"D0=0x{g_reg_data[0].l:X8} D1=0x{g_reg_data[1].l:X8} D2=0x{g_reg_data[2].l:X8} A0=0x{g_reg_addr[0].l:X8}");
            }
            if (TraceSonic3Dbmi && _traceSonic3DbmiRemaining > 0 && (startPc == 0x195C6 || startPc == 0x195FA))
            {
                _traceSonic3DbmiRemaining--;
                Console.WriteLine(
                    $"[SONIC3-DBMI] pc=0x{startPc:X6} D{g_op4} pre=0x{before:X4} post=0x{after:X4} branch={(branch ? 1 : 0)} " +
                    $"N={(g_status_N ? 1 : 0)} Z={(g_status_Z ? 1 : 0)} disp=0x{displacement:X4} D7=0x{g_reg_data[7].l:X8} D4=0x{g_reg_data[4].l:X8}");
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
