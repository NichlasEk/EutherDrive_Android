using System;

namespace Ryu64.MIPS
{
    public partial class InstInterp
    {
        private static readonly bool TraceCop0 =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_COP0"), "1", StringComparison.Ordinal);
        private static readonly bool CanonicalizeLowEretTargets =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_CANONICALIZE_ERET"), "1", StringComparison.Ordinal);

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
                case Registers.COP0.STATUS_REG:
                    // Bit 19 is not writable on VR4300.
                    return value & ~0x00080000u;
                default:
                    return value;
            }
        }

        private static void WriteCop0Register(int reg, ulong rawValue)
        {
            const ulong CauseIp7Bit = 1UL << 15;
            ulong value = NormalizeCop0WriteValue(reg, rawValue);

            switch (reg)
            {
                case Registers.COP0.RANDOM_REG:
                case Registers.COP0.BADVADDR_REG:
                case Registers.COP0.XCONTEXT_REG:
                case Registers.COP0.CACHERR_REG:
                    return;
                case Registers.COP0.CONTEXT_REG:
                    Registers.COP0.Reg[reg] =
                        (value & 0xFF800000u) |
                        (Registers.COP0.Reg[reg] & 0x007FFFF0u);
                    return;
                case Registers.COP0.CAUSE_REG:
                    // Only software interrupt pending bits are writable.
                    Registers.COP0.Reg[reg] =
                        (Registers.COP0.Reg[reg] & ~0x00000300u) |
                        (value & 0x00000300u);
                    return;
                case Registers.COP0.CONFIG_REG:
                    // Match mupen's limited writable subset.
                    Registers.COP0.Reg[reg] =
                        (value & 0x0000000Fu) |
                        (Registers.COP0.Reg[reg] & 0x00008000u) |
                        (Registers.COP0.Reg[reg] & 0x7FFFFFF0u);
                    return;
                case Registers.COP0.PERR_REG:
                    Registers.COP0.Reg[reg] = value & 0xFFu;
                    return;
                case Registers.COP0.TAGLO_REG:
                    Registers.COP0.Reg[reg] = value & 0x0FFFFFC0u;
                    return;
                case Registers.COP0.TAGHI_REG:
                    Registers.COP0.Reg[reg] = 0;
                    return;
                default:
                    Registers.COP0.Reg[reg] = value;
                    break;
            }

            if (reg == Registers.COP0.WIRED_REG)
            {
                // VR4300: RANDOM is reset when WIRED changes.
                Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = 0x1Fu;
            }
            else if (reg == Registers.COP0.COMPARE_REG)
            {
                // Writing COMPARE acknowledges/clears the CP0 timer pending bit (IP7).
                Registers.COP0.Reg[Registers.COP0.CAUSE_REG] &= ~CauseIp7Bit;
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

        private static bool AffectsInterruptDelivery(int reg)
        {
            return reg == Registers.COP0.STATUS_REG
                || reg == Registers.COP0.CAUSE_REG
                || reg == Registers.COP0.COMPARE_REG
                || reg == Registers.COP0.COUNT_REG;
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
            if (AffectsInterruptDelivery(Desc.op3))
                R4300.CheckPendingInterruptsNow(Registers.R4300.PC);
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
            if (AffectsInterruptDelivery(Desc.op3))
                R4300.CheckPendingInterruptsNow(Registers.R4300.PC);
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
            if (AffectsInterruptDelivery(Desc.op3))
                R4300.CheckPendingInterruptsNow(Registers.R4300.PC);
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
            const ulong SentinelErrorEpc = 0xFFFFFFFFUL;

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
            uint targetPc;
            string eretPath;
            if ((status & StatusErlBit) != 0)
            {
                // Match mupen/N64 bring-up behavior more closely when software restores
                // ERL without ever seeding ErrorEPC: prefer EPC over the power-on sentinel.
                if ((uint)errorEpc == (uint)SentinelErrorEpc && (uint)epc != (uint)SentinelErrorEpc)
                {
                    targetPc = (uint)epc & 0xFFFFFFFCu;
                    Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status & ~StatusErlBit;
                    eretPath = "ERL->EPC-fallback";
                }
                else
                {
                    targetPc = (uint)errorEpc & 0xFFFFFFFCu;
                    Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status & ~StatusErlBit;
                    eretPath = "ERL->ErrorEPC";
                }
            }
            else
            {
                targetPc = (uint)epc & 0xFFFFFFFCu;
                Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status & ~StatusExlBit;
                eretPath = "EXL->EPC";
            }

            // This is a bring-up-only escape hatch.
            // Real VR4300 semantics return to EPC directly.
            if (CanonicalizeLowEretTargets && targetPc < 0x20000000u)
                targetPc |= 0x80000000u;

            if (TraceCop0)
            {
                Common.Logger.PrintInfoLine(
                    $"[COP0] ERET target path={eretPath} target=0x{targetPc:x8} newStatus=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x16}");
            }

            Registers.R4300.PC = targetPc;
            R4300.ClearLoadLinkedReservation();

            // Mupen rechecks pending interrupts immediately after ERET once EXL/ERL has been
            // cleared. Without that, guest scheduler code can run past a point where hardware
            // should have vectored straight back into the general exception handler.
            R4300.CheckPendingInterruptsNow(targetPc);
        }
    }
}
