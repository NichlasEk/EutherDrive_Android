using System;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static int ParseTlbTraceLimit(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (int.TryParse(raw, out int parsed) && parsed > 0)
                return parsed;

            return fallback;
        }

        private static readonly bool TraceTlbOps =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_TLB_OPS"), "1", StringComparison.Ordinal);
        private static readonly int TraceTlbOpsLimit = ParseTlbTraceLimit("EUTHERDRIVE_TRACE_N64_TLB_OPS_LIMIT", 256);
        private static int _traceTlbOpsCount = 0;

        private static void TraceTlb(string opName)
        {
            if (!TraceTlbOps || _traceTlbOpsCount >= TraceTlbOpsLimit)
                return;

            _traceTlbOpsCount++;
            Common.Logger.PrintWarningLine(
                $"[TLB] #{_traceTlbOpsCount} {opName} pc=0x{Registers.R4300.PC:x8} " +
                $"idx=0x{Registers.COP0.Reg[Registers.COP0.INDEX_REG]:x8} " +
                $"entryHi=0x{Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG]:x8} " +
                $"entryLo0=0x{Registers.COP0.Reg[Registers.COP0.ENTRYLO0_REG]:x8} " +
                $"entryLo1=0x{Registers.COP0.Reg[Registers.COP0.ENTRYLO1_REG]:x8} " +
                $"pageMask=0x{Registers.COP0.Reg[Registers.COP0.PAGEMASK_REG]:x8}");
        }

        public static void TLBR(OpcodeTable.OpcodeDesc Desc)
        {
            TraceTlb("TLBR(before)");
            TLB.ReadTLBEntry();
            TraceTlb("TLBR(after)");
            Registers.R4300.PC += 4;
        }

        public static void TLBWI(OpcodeTable.OpcodeDesc Desc)
        {
            TraceTlb("TLBWI(before)");
            TLB.WriteTLBEntryIndexed();
            TraceTlb("TLBWI(after)");
            Registers.R4300.PC += 4;
        }

        public static void TLBWR(OpcodeTable.OpcodeDesc Desc)
        {
            TraceTlb("TLBWR(before)");
            TLB.WriteTLBEntryRandom();
            TraceTlb("TLBWR(after)");
            Registers.R4300.PC += 4;
        }

        public static void TLBP(OpcodeTable.OpcodeDesc Desc)
        {
            TraceTlb("TLBP(before)");
            TLB.ProbeTLB();
            TraceTlb("TLBP(after)");
            Registers.R4300.PC += 4;
        }
    }
}
