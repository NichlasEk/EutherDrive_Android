using System;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static readonly bool TraceCop0 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_COP0"), "1", StringComparison.Ordinal);

        private static ulong NormalizeCop0WriteValue(int reg, ulong rawValue)
        {
            uint value = (uint)rawValue;
            switch (reg)
            {
                case Registers.COP0.INDEX_REG:
                    // INDEX: P bit + low index bits are meaningful.
                    return value & 0x8000003Fu;
                case Registers.COP0.RANDOM_REG:
                    // RANDOM is hardware-managed.
                    return Registers.COP0.Reg[Registers.COP0.RANDOM_REG];
                case Registers.COP0.ENTRYLO0_REG:
                case Registers.COP0.ENTRYLO1_REG:
                    return value & 0x3FFFFFFFu;
                case Registers.COP0.PAGEMASK_REG:
                    return value & 0x01FFE000u;
                case Registers.COP0.WIRED_REG:
                    return value & 0x3Fu;
                case Registers.COP0.ENTRYHI_REG:
                    // Keep VPN2 + ASID fields.
                    return value & 0xFFFFE0FFu;
                default:
                    return value;
            }
        }

        private static void WriteCop0Register(int reg, ulong rawValue)
        {
            ulong value = NormalizeCop0WriteValue(reg, rawValue);
            Registers.COP0.Reg[reg] = value;

            if (reg == Registers.COP0.WIRED_REG)
            {
                // VR4300: RANDOM is reset when WIRED changes.
                Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = 0x1Fu;
            }
        }

        private static ulong SignExtend32To64(uint value)
        {
            return unchecked((ulong)(long)(int)value);
        }

        private static bool IsTrackedCop0Register(int reg)
        {
            return reg == Registers.COP0.STATUS_REG
                || reg == Registers.COP0.CAUSE_REG
                || reg == Registers.COP0.EPC_REG
                || reg == Registers.COP0.ERROREPC_REG;
        }

        public static void MFC0(OpcodeTable.OpcodeDesc Desc)
        {
            uint value = (uint)Registers.COP0.Reg[Desc.op3];
            Registers.R4300.Reg[Desc.op2] = SignExtend32To64(value);
            Registers.R4300.PC += 4;
        }

        public static void MTC0(OpcodeTable.OpcodeDesc Desc)
        {
            ulong value = (uint)Registers.R4300.Reg[Desc.op2];
            WriteCop0Register(Desc.op3, value);
            if (TraceCop0 && IsTrackedCop0Register(Desc.op3))
            {
                Common.Logger.PrintInfoLine(
                    $"[COP0] MTC0 reg={Desc.op3} value=0x{value:x16} pc=0x{Registers.R4300.PC:x8}");
            }
            Registers.R4300.PC += 4;
        }

        public static void DMFC0(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.Reg[Desc.op2] = Registers.COP0.Reg[Desc.op3];
            Registers.R4300.PC += 4;
        }

        public static void DMTC0(OpcodeTable.OpcodeDesc Desc)
        {
            // VR4300 CP0 register interface is effectively 32-bit for architectural fields used here.
            // Keep upper bits clear to avoid corrupting exception/status state with guest garbage.
            ulong value = (uint)Registers.R4300.Reg[Desc.op2];
            WriteCop0Register(Desc.op3, value);
            if (TraceCop0 && IsTrackedCop0Register(Desc.op3))
            {
                Common.Logger.PrintInfoLine(
                    $"[COP0] DMTC0 reg={Desc.op3} value=0x{value:x16} pc=0x{Registers.R4300.PC:x8}");
            }
            Registers.R4300.PC += 4;
        }

        public static void CFC0(OpcodeTable.OpcodeDesc Desc)
        {
            uint value = (uint)Registers.COP0.Reg[Desc.op3];
            Registers.R4300.Reg[Desc.op2] = SignExtend32To64(value);
            Registers.R4300.PC += 4;
        }

        public static void CTC0(OpcodeTable.OpcodeDesc Desc)
        {
            ulong value = (uint)Registers.R4300.Reg[Desc.op2];
            WriteCop0Register(Desc.op3, value);
            if (TraceCop0 && IsTrackedCop0Register(Desc.op3))
            {
                Common.Logger.PrintInfoLine(
                    $"[COP0] CTC0 reg={Desc.op3} value=0x{value:x16} pc=0x{Registers.R4300.PC:x8}");
            }
            Registers.R4300.PC += 4;
        }

        public static void CACHE(OpcodeTable.OpcodeDesc Desc)
        {
            Registers.R4300.PC += 4; // Stubbed.
        }

        public static void ERET(OpcodeTable.OpcodeDesc Desc)
        {
            _ = Desc;

            const ulong StatusExlBit = 1UL << 1;
            const ulong StatusErlBit = 1UL << 2;

            ulong status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
            ulong epc = Registers.COP0.Reg[Registers.COP0.EPC_REG];
            ulong errorEpc = Registers.COP0.Reg[Registers.COP0.ERROREPC_REG];

            if (TraceCop0)
            {
                Common.Logger.PrintInfoLine(
                    $"[COP0] ERET pc=0x{Registers.R4300.PC:x8} status=0x{status:x16} epc=0x{epc:x16} errorEpc=0x{errorEpc:x16}");
            }

            // VR4300 ERET semantics:
            // - If ERL is set, return to ErrorEPC and clear ERL.
            // - Otherwise, return to EPC and clear EXL.
            if ((status & StatusErlBit) != 0)
            {
                Registers.R4300.PC = (uint)errorEpc & 0xFFFFFFFCu;
                Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status & ~StatusErlBit;
            }
            else
            {
                Registers.R4300.PC = (uint)epc & 0xFFFFFFFCu;
                Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status & ~StatusExlBit;
            }
        }
    }
}
