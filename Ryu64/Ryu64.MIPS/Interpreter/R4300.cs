using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ryu64.MIPS
{
    public class R4300
    {
        public static bool R4300_ON = false;
        private static Thread CpuThread;

        public static Memory memory;
        public static ulong CycleCounter = 0;
        private static ulong Count = 0;
        private static long UnknownOpcodeCount = 0;
        private static readonly Dictionary<uint, long> UnknownOpcodeByPc = new Dictionary<uint, long>();
        private static readonly Dictionary<uint, long> UnknownOpcodeByValue = new Dictionary<uint, long>();
        private static readonly object UnknownOpcodeLock = new object();
        private struct RecentInst
        {
            public uint Pc;
            public uint Op;
        }
        private const int RecentInstHistorySize = 512;
        private const int RecentInstHistoryMask = RecentInstHistorySize - 1;
        private static readonly RecentInst[] _recentInst = new RecentInst[RecentInstHistorySize];
        private static int _recentInstPos = 0;
        private static ulong _stuckPcLogCount = 0;
        private static readonly bool TraceBootWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_BOOT_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceBootWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_BOOT_WINDOW_LIMIT", 4000);
        private static int _traceBootWindowCount = 0;
        private static readonly bool TraceEarlyLoopWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_EARLY_LOOP"), "1", StringComparison.Ordinal);
        private static readonly int TraceEarlyLoopWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_EARLY_LOOP_LIMIT", 6000);
        private static int _traceEarlyLoopWindowCount = 0;
        private static readonly bool TraceEretWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_ERET_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceEretWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_ERET_WINDOW_LIMIT", 200);
        private static int _traceEretWindowCount = 0;
        private static readonly bool TraceRefillWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_REFILL_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceRefillWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_REFILL_WINDOW_LIMIT", 400);
        private static int _traceRefillWindowCount = 0;
        private static readonly bool TraceSm64WalkWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_SM64_WALK_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceSm64WalkWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_SM64_WALK_WINDOW_LIMIT", 2000);
        private static int _traceSm64WalkWindowCount = 0;
        private static readonly bool TraceViInitWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_INIT_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceViInitWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_VI_INIT_WINDOW_LIMIT", 600);
        private static int _traceViInitWindowCount = 0;
        private static readonly bool TraceViPrepWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_PREP_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceViPrepWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_VI_PREP_WINDOW_LIMIT", 400);
        private static int _traceViPrepWindowCount = 0;
        private static readonly bool TraceViCalcWindow =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_VI_CALC_WINDOW"), "1", StringComparison.Ordinal);
        private static readonly int TraceViCalcWindowLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_N64_VI_CALC_WINDOW_LIMIT", 200);
        private static int _traceViCalcWindowCount = 0;
        private static readonly bool TraceStuckPcDetails =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_N64_STUCK_PC"), "1", StringComparison.Ordinal);
        private static readonly bool EnableSm64AddressErrorPatch =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_DISABLE_SM64_ADDRERR_PATCH"), "1", StringComparison.Ordinal);
        private const ulong StatusExlBit = 1UL << 1;
        private const ulong StatusErlBit = 1UL << 2;
        private const ulong StatusIeBit = 1UL << 0;
        private const ulong StatusBevBit = 1UL << 22;
        private const ulong StatusImMask = 0x0000FF00UL;
        private const ulong CauseBdBit = 1UL << 31;
        private const ulong CauseExcCodeMask = 0x7CUL;
        private const ulong CauseIpMask = 0x0000FF00UL;
        private const ulong CauseIp2Bit = 1UL << 10;
        private const ulong CauseIp7Bit = 1UL << 15;
        private const ulong CauseExcCodeTlbLoad = 2UL << 2;
        private const ulong CauseExcCodeTlbStore = 3UL << 2;
        private const ulong CauseExcCodeAddressErrorLoad = 4UL << 2;
        private const ulong CauseExcCodeAddressErrorStore = 5UL << 2;
        private const ulong CauseExcCodeInterrupt = 0UL << 2;
        private const ulong CauseExcCodeRi = 10UL << 2;
        private static readonly bool UnknownOpcodeAsNop =
            string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_UNKNOWN_AS_NOP"), "1", StringComparison.Ordinal);
        private static readonly bool AllowInstructionLowPhysicalFallbackOnTlbMiss =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_STRICT_ITLB"), "1", StringComparison.Ordinal);
        private static readonly bool AlignMisalignedPcDuringBringup =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_ALIGN_MISALIGNED_PC"), "0", StringComparison.Ordinal);
        // Bring-up default: prefer RAM vectors when BEV is set because PIF/boot ROM exception
        // vectors are not fully emulated yet. Set EUTHERDRIVE_N64_STRICT_BEV_VECTORS=1 to force
        // strict VR4300 ROM-vector behavior.
        private static readonly bool UseRamVectorsWhenBevSet =
            !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_N64_STRICT_BEV_VECTORS"), "1", StringComparison.Ordinal);
        private static bool _executingDelaySlot;
        private static uint _delaySlotBranchPc;
        private static bool _delaySlotExceptionPending;
        private static uint _delaySlotExceptionBranchPc;
        private static bool _loggedPifTailEntry;
        private static bool _loggedFirstBfcEntry;
        private static ulong _tlbRefillLogCount;
        private static ulong _addressErrorLogCount;
        private static bool _sm64ThunkPatched;

        private static int ParseTraceLimit(string name, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (int.TryParse(raw, out int parsed) && parsed > 0)
                return parsed;

            return fallback;
        }

        private static void TrackUnknownOpcode(uint pc, uint opcode)
        {
            lock (UnknownOpcodeLock)
            {
                if (!UnknownOpcodeByPc.TryGetValue(pc, out long pcCount))
                    pcCount = 0;
                UnknownOpcodeByPc[pc] = pcCount + 1;

                if (!UnknownOpcodeByValue.TryGetValue(opcode, out long opCount))
                    opCount = 0;
                UnknownOpcodeByValue[opcode] = opCount + 1;
            }
        }

        private static void RaiseTlbRefillException(uint badAddress, uint faultingPc, bool isStore)
        {
            _tlbRefillLogCount++;
            ulong status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
            ulong cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
            ulong entryHi = Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG];
            ulong context = Registers.COP0.Reg[Registers.COP0.CONTEXT_REG];
            bool exlAlreadySet = (status & StatusExlBit) != 0;
            bool bevSet = (status & StatusBevBit) != 0;
            bool inDelaySlot = _executingDelaySlot;
            uint branchPc = _delaySlotBranchPc;
            ConsumeDelaySlotExceptionContext(ref inDelaySlot, ref branchPc);

            if (_tlbRefillLogCount <= 32 || (_tlbRefillLogCount % 256) == 0)
            {
                Common.Logger.PrintWarningLine(
                    $"TLB refill exception (count={_tlbRefillLogCount}) " +
                    $"faultPc=0x{faultingPc:x8} badv=0x{badAddress:x8} " +
                    $"status=0x{status:x8} cause=0x{cause:x8} exl={exlAlreadySet} bev={bevSet} delay={inDelaySlot} store={isStore}");
                if (_tlbRefillLogCount <= 4)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Recent PCs before TLB refill:");
                    for (int i = 0; i < 20; i++)
                    {
                        int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                        RecentInst rec = _recentInst[idx];
                        sb.Append($" [{i}]pc=0x{rec.Pc:x8}/op=0x{rec.Op:x8}");
                    }
                    Common.Logger.PrintWarningLine(sb.ToString());

                    try
                    {
                        uint v0 = memory.ReadUInt32(0x803359B0u);
                        uint v1 = memory.ReadUInt32(0x803359B4u);
                        uint v2 = memory.ReadUInt32(0x803359B8u);
                        uint v3 = memory.ReadUInt32(0x803359BCu);
                        Common.Logger.PrintWarningLine(
                            $"Refill globals @803359b0: [0]=0x{v0:x8} [1]=0x{v1:x8} [2]=0x{v2:x8} [3]=0x{v3:x8}");
                    }
                    catch
                    {
                        // Best-effort diagnostics only.
                    }
                }
            }

            Registers.COP0.Reg[Registers.COP0.BADVADDR_REG] = badAddress;
            Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG] = (badAddress & 0xFFFFE000u) | (entryHi & 0xFFu);
            Registers.COP0.Reg[Registers.COP0.CONTEXT_REG] = (context & 0xFF80000FUL) | (((ulong)badAddress >> 9) & 0x007FFFF0UL);
            if (!exlAlreadySet)
                Registers.COP0.Reg[Registers.COP0.EPC_REG] = (inDelaySlot ? branchPc : faultingPc) & 0xFFFFFFFCu;
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] =
                (cause & ~(CauseExcCodeMask | CauseBdBit))
                | (isStore ? CauseExcCodeTlbStore : CauseExcCodeTlbLoad)
                | (inDelaySlot ? CauseBdBit : 0);
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status | StatusExlBit;

            if (bevSet)
            {
                if (UseRamVectorsWhenBevSet)
                {
                    Registers.COP0.Reg[Registers.COP0.STATUS_REG] &= ~StatusBevBit;
                    Registers.R4300.PC = exlAlreadySet ? 0x80000180u : 0x80000000u;
                }
                else
                {
                    Registers.R4300.PC = exlAlreadySet ? 0xBFC00380u : 0xBFC00200u;
                }
            }
            else
            {
                Registers.R4300.PC = exlAlreadySet ? 0x80000180u : 0x80000000u;
            }
        }

        private static void RaiseCpuException(ulong exceptionCode, uint faultingPc)
        {
            ulong status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
            ulong cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
            bool exlAlreadySet = (status & StatusExlBit) != 0;
            bool bevSet = (status & StatusBevBit) != 0;
            bool inDelaySlot = _executingDelaySlot;
            uint branchPc = _delaySlotBranchPc;
            ConsumeDelaySlotExceptionContext(ref inDelaySlot, ref branchPc);

            if (!exlAlreadySet)
                Registers.COP0.Reg[Registers.COP0.EPC_REG] = (inDelaySlot ? branchPc : faultingPc) & 0xFFFFFFFCu;
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] =
                (cause & ~(CauseExcCodeMask | CauseBdBit))
                | exceptionCode
                | (inDelaySlot ? CauseBdBit : 0);
            Registers.COP0.Reg[Registers.COP0.STATUS_REG] = status | StatusExlBit;
            if (bevSet && UseRamVectorsWhenBevSet)
            {
                Registers.COP0.Reg[Registers.COP0.STATUS_REG] &= ~StatusBevBit;
                Registers.R4300.PC = 0x80000180u;
            }
            else
            {
                Registers.R4300.PC = bevSet ? 0xBFC00380u : 0x80000180u;
            }
        }

        private static void RaiseAddressErrorException(uint badAddress, bool isStore, uint faultingPc)
        {
            if (EnableSm64AddressErrorPatch
                && !_sm64ThunkPatched
                && !isStore
                && badAddress == 0x3C1A8036u
                && (uint)Registers.R4300.Reg[4] == 0x803359A8u)
            {
                try
                {
                    uint slot0 = memory.ReadUInt32(0x803359A8u);
                    uint slot1 = memory.ReadUInt32(0x803359ACu);
                    if (slot0 == 0 && (slot1 & 0xE0000000u) == 0x80000000u)
                    {
                        memory.WriteUInt32(0x803359A8u, slot1);
                        _sm64ThunkPatched = true;
                        Common.Logger.PrintWarningLine(
                            $"Applied SM64 thunk patch: [803359a8]=0x{slot1:x8} after AddressError at pc=0x{faultingPc:x8}");
                        return;
                    }
                }
                catch
                {
                    // Best-effort recovery only.
                }
            }

            _addressErrorLogCount++;
            if (_addressErrorLogCount <= 64 || (_addressErrorLogCount % 256) == 0)
            {
                ulong a0 = Registers.R4300.Reg[4];
                ulong v0 = Registers.R4300.Reg[2];
                uint a0w = 0;
                uint a0wm8 = 0;
                uint a0wm4 = 0;
                uint a0w4 = 0;
                uint a0w8 = 0;
                uint a0wc = 0;
                uint a0w10 = 0;
                uint v0w = 0;
                uint v0w4 = 0;
                uint v0w8 = 0;
                uint v0wc = 0;
                uint v0w10 = 0;
                uint v0wm4 = 0;
                try { a0w = memory.ReadUInt32((uint)a0); } catch { }
                try { a0wm8 = memory.ReadUInt32((uint)a0 - 8u); } catch { }
                try { a0wm4 = memory.ReadUInt32((uint)a0 - 4u); } catch { }
                try { a0w4 = memory.ReadUInt32((uint)a0 + 4u); } catch { }
                try { a0w8 = memory.ReadUInt32((uint)a0 + 8u); } catch { }
                try { a0wc = memory.ReadUInt32((uint)a0 + 12u); } catch { }
                try { a0w10 = memory.ReadUInt32((uint)a0 + 16u); } catch { }
                try { v0w = memory.ReadUInt32((uint)v0); } catch { }
                try { v0w4 = memory.ReadUInt32((uint)v0 + 4u); } catch { }
                try { v0w8 = memory.ReadUInt32((uint)v0 + 8u); } catch { }
                try { v0wc = memory.ReadUInt32((uint)v0 + 12u); } catch { }
                try { v0w10 = memory.ReadUInt32((uint)v0 + 16u); } catch { }
                try { v0wm4 = memory.ReadUInt32((uint)v0 - 4u); } catch { }

                Common.Logger.PrintWarningLine(
                    $"Address error exception (count={_addressErrorLogCount}) " +
                    $"pc=0x{faultingPc:x8} badv=0x{badAddress:x8} store={isStore} " +
                    $"epc=0x{Registers.COP0.Reg[Registers.COP0.EPC_REG]:x8} " +
                    $"cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8} " +
                    $"status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} " +
                    $"a0=0x{a0:x16} v0=0x{v0:x16} " +
                    $"[a0-8]=0x{a0wm8:x8} [a0-4]=0x{a0wm4:x8} [a0]=0x{a0w:x8} [a0+4]=0x{a0w4:x8} [a0+8]=0x{a0w8:x8} [a0+c]=0x{a0wc:x8} [a0+10]=0x{a0w10:x8} " +
                    $"[v0-4]=0x{v0wm4:x8} [v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} [v0+8]=0x{v0w8:x8} [v0+c]=0x{v0wc:x8} [v0+10]=0x{v0w10:x8}");

                if (_addressErrorLogCount <= 16)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Recent PCs before AddressError:");
                    for (int i = 0; i < 24; i++)
                    {
                        int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                        RecentInst rec = _recentInst[idx];
                        sb.Append($" [{i}]pc=0x{rec.Pc:x8}/op=0x{rec.Op:x8}");
                    }
                    Common.Logger.PrintWarningLine(sb.ToString());
                }
            }

            Registers.COP0.Reg[Registers.COP0.BADVADDR_REG] = badAddress;
            RaiseCpuException(isStore ? CauseExcCodeAddressErrorStore : CauseExcCodeAddressErrorLoad, faultingPc);
        }

        private static void ConsumeDelaySlotExceptionContext(ref bool inDelaySlot, ref uint branchPc)
        {
            if (!_delaySlotExceptionPending)
                return;

            inDelaySlot = true;
            branchPc = _delaySlotExceptionBranchPc;
            _delaySlotExceptionPending = false;
        }

        private static bool ServiceInterrupts(uint pc)
        {
            // N64 RCP interrupts are routed through MI and appear on CP0 IP2.
            ulong cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
            uint miIntr = memory.ReadUInt32(0xA4300008u);
            uint miMask = memory.ReadUInt32(0xA430000Cu);
            bool rcpPending = (miIntr & miMask & 0x3Fu) != 0;

            // Only control IP2 from MI; preserve all other pending IP bits (timer/SW/etc).
            cause = rcpPending ? (cause | CauseIp2Bit) : (cause & ~CauseIp2Bit);
            Registers.COP0.Reg[Registers.COP0.CAUSE_REG] = cause;
            ulong pendingIp = cause & CauseIpMask;

            ulong status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
            bool canTake = (status & (StatusExlBit | StatusErlBit)) == 0
                && (status & StatusIeBit) != 0
                && ((status & StatusImMask & pendingIp) != 0);

            if (!canTake)
                return false;

            RaiseCpuException(CauseExcCodeInterrupt, pc);
            return true;
        }

        public static void ExecuteDelaySlot()
        {
            uint delayPc = Registers.R4300.PC;
            bool prevInDelay = _executingDelaySlot;
            uint prevBranchPc = _delaySlotBranchPc;
            _executingDelaySlot = true;
            _delaySlotBranchPc = delayPc - 4;
            try
            {
                InterpretOpcode(memory.ReadUInt32(delayPc));
            }
            catch
            {
                // Preserve delay-slot metadata for outer catch handlers.
                _delaySlotExceptionPending = true;
                _delaySlotExceptionBranchPc = _delaySlotBranchPc;
                throw;
            }
            finally
            {
                _executingDelaySlot = prevInDelay;
                _delaySlotBranchPc = prevBranchPc;
            }
        }

        private static string FormatTopUnknownSummary(Dictionary<uint, long> source, string prefix, int topN)
        {
            List<KeyValuePair<uint, long>> list = new List<KeyValuePair<uint, long>>(source);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            if (list.Count > topN)
                list.RemoveRange(topN, list.Count - topN);

            string[] chunks = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                chunks[i] = $"{prefix}=0x{list[i].Key:x8}:{list[i].Value}";

            return string.Join(", ", chunks);
        }

        private static bool CountCompareReached(uint previousCount, uint newCount, uint compare)
        {
            // compare match between previousCount(exclusive) -> newCount(inclusive), wrapping at 32-bit.
            if (previousCount <= newCount)
                return compare > previousCount && compare <= newCount;
            return compare > previousCount || compare <= newCount;
        }

        private static uint CRC32(uint StartAddress, uint Length)
        {
            uint[] Table = new uint[256];
            ulong n, k;
            uint c;

            for (n = 0; n < 256; ++n)
            {
                c = (uint)n;

                for (k = 0; k < 8; ++k)
                {
                    if ((c & 1) == 1)
                        c = 0xEDB88320 ^ (c >> 1);
                    else
                        c >>= 1;
                }

                Table[n] = c;
            }

            c = 0 ^ 0xFFFFFFFF;

            for (n = 0; n < Length; ++n)
            {
                c = Table[(c ^ memory.ReadUInt8(StartAddress + (uint)n)) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFF;
        }

        // All values from Cen64: https://github.com/tj90241/cen64/blob/master/si/cic.c
        private const uint CIC_SEED_NUS_5101 = 0x0000AC00;
        private const uint CIC_SEED_NUS_6101 = 0x00043F3F;
        private const uint CIC_SEED_NUS_6102 = 0x00003F3F;
        private const uint CIC_SEED_NUS_6103 = 0x0000783F;
        private const uint CIC_SEED_NUS_6105 = 0x0000913F;
        private const uint CIC_SEED_NUS_6106 = 0x0000853F;
        private const uint CIC_SEED_NUS_8303 = 0x0000DD00;

        private const uint CRC_NUS_5101 = 0x587BD543;
        private const uint CRC_NUS_6101 = 0x6170A4A1;
        private const uint CRC_NUS_7102 = 0x009E9EA3;
        private const uint CRC_NUS_6102 = 0x90BB6CB5;
        private const uint CRC_NUS_6103 = 0x0B050EE0;
        private const uint CRC_NUS_6105 = 0x98BC2C86;
        private const uint CRC_NUS_6106 = 0xACC8580A;
        private const uint CRC_NUS_8303 = 0x0E018159;
        private const uint CRC_iQue_1   = 0xCD19FEF1;
        private const uint CRC_iQue_2   = 0xB98CED9A;
        private const uint CRC_iQue_3   = 0xE71C2766;


        private static uint GetCICSeed()
        {
            // Use kseg1 alias for cart ROM reads during early boot.
            // Data-side TLB may not be initialized yet.
            const uint cartBootBaseKseg1 = 0xB0000040u;
            uint CRC        = CRC32(cartBootBaseKseg1, 0xFC0);
            uint Aleck64CRC = CRC32(cartBootBaseKseg1, 0xBC0);

            if (Aleck64CRC == CRC_NUS_5101) return CIC_SEED_NUS_5101;
            switch (CRC)
            {
                default:
                    Common.Logger.PrintWarningLine("Unknown CIC, defaulting to seed CIC-6101.");
                    return CIC_SEED_NUS_6101;

                case CRC_NUS_6101:
                case CRC_NUS_7102:
                case CRC_iQue_1:
                case CRC_iQue_2:
                case CRC_iQue_3:
                    return CIC_SEED_NUS_6101;

                case CRC_NUS_6102:
                    return CIC_SEED_NUS_6102;

                case CRC_NUS_6103:
                    return CIC_SEED_NUS_6103;

                case CRC_NUS_6105:
                    return CIC_SEED_NUS_6105;

                case CRC_NUS_6106:
                    return CIC_SEED_NUS_6106;

                case CRC_NUS_8303:
                    return CIC_SEED_NUS_8303;
            }
        }

        private static byte GetInitialPifControlByte(uint cicSeed)
        {
            // Boot ROM/IPL paths are sensitive to the initial PIF control byte.
            // Keep a pragmatic mapping for commonly used CICs during bring-up.
            switch (cicSeed)
            {
                case CIC_SEED_NUS_6102:
                    return 0x3D;
                case CIC_SEED_NUS_6105:
                case CIC_SEED_NUS_6106:
                    return 0xD2;
                default:
                    return 0x3D;
            }
        }

        public static void InterpretOpcode(uint Opcode)
        {
            if (Registers.R4300.Reg[0] != 0) Registers.R4300.Reg[0] = 0;

            if (Registers.COP0.Reg[Registers.COP0.COUNT_REG] >= 0xFFFFFFFF)
            {
                Registers.COP0.Reg[Registers.COP0.COUNT_REG] = 0x0;
                Count = 0x0;
            }

            OpcodeTable.OpcodeDesc Desc = new OpcodeTable.OpcodeDesc(Opcode);
            OpcodeTable.InstInfo   Info = OpcodeTable.GetOpcodeInfo(Opcode);

            if (Common.Variables.Debug)
            {
                string ASM = string.Format(
                    Info.FormattedASM,
                    Desc.op1, Desc.op2, Desc.op3, Desc.op4,
                    Desc.Imm, Desc.Target);
                Common.Logger.PrintInfoLine($"0x{Registers.R4300.PC:x}: {Convert.ToString(Opcode, 2).PadLeft(32, '0')}: {ASM}");
            }

            Info.Interpret(Desc);
            CycleCounter += Info.Cycles;
            Count        += Info.Cycles;
            memory?.Tick(Info.Cycles);
            uint previousCount = (uint)Registers.COP0.Reg[Registers.COP0.COUNT_REG];
            uint newCount = (uint)(Count >> 1);
            Registers.COP0.Reg[Registers.COP0.COUNT_REG] = newCount;
            uint compare = (uint)Registers.COP0.Reg[Registers.COP0.COMPARE_REG];
            if (CountCompareReached(previousCount, newCount, compare))
                Registers.COP0.Reg[Registers.COP0.CAUSE_REG] |= CauseIp7Bit;
            uint random = (uint)Registers.COP0.Reg[Registers.COP0.RANDOM_REG] & 0x1Fu;
            uint wired = (uint)Registers.COP0.Reg[Registers.COP0.WIRED_REG] & 0x1Fu;
            if (random <= wired)
                random = 0x1Fu;
            else
                random--;
            Registers.COP0.Reg[Registers.COP0.RANDOM_REG] = random;

            Common.Measure.InstructionCount += 1;
            Common.Measure.CycleCounter = CycleCounter;
        }

        public static void PowerOnR4300()
        {
            StopR4300();

            for (int i = 0; i < Registers.R4300.Reg.Length; ++i)
                Registers.R4300.Reg[i] = 0; // Clear Registers.

            uint RomType   = 0; // 0 = Cart, 1 = DD
            uint ResetType = 0; // 0 = Cold Reset, 1 = NMI, 2 = Reset to boot disk
            uint osVersion = 0; // 00 = 1.0, 15 = 2.5, etc.
            uint TVType    = 1; // 0 = PAL, 1 = NTSC, 2 = MPAL

            Registers.R4300.Reg[1]  = 0x0000000000000001;
            Registers.R4300.Reg[2]  = 0x000000000EBDA536;
            Registers.R4300.Reg[3]  = 0x000000000EBDA536;
            Registers.R4300.Reg[4]  = 0x000000000000A536;
            Registers.R4300.Reg[5]  = 0xFFFFFFFFC0F1D859;
            Registers.R4300.Reg[6]  = 0xFFFFFFFFA4001F0C;
            Registers.R4300.Reg[7]  = 0xFFFFFFFFA4001F08;
            Registers.R4300.Reg[8]  = 0x00000000000000C0;
            Registers.R4300.Reg[10] = 0x0000000000000040;
            Registers.R4300.Reg[11] = 0xFFFFFFFFA4000040;
            Registers.R4300.Reg[12] = 0xFFFFFFFFED10D0B3;
            Registers.R4300.Reg[13] = 0x000000001402A4CC;
            Registers.R4300.Reg[14] = 0x000000002DE108EA;
            Registers.R4300.Reg[15] = 0x000000003103E121;
            Registers.R4300.Reg[19] = RomType;
            Registers.R4300.Reg[20] = TVType;
            Registers.R4300.Reg[21] = ResetType;
            uint cicSeed = GetCICSeed();
            Registers.R4300.Reg[22] = (cicSeed >> 8) & 0xFF;
            Registers.R4300.Reg[23] = osVersion;
            Registers.R4300.Reg[25] = 0xFFFFFFFF9DEBB54F;
            Registers.R4300.Reg[29] = 0xFFFFFFFFA4001FF0;
            Registers.R4300.Reg[31] = 0xFFFFFFFFA4001550;
            Registers.R4300.HI      = 0x000000003FC18657;
            Registers.R4300.LO      = 0x000000003103E121;
            Registers.R4300.PC      = 0xA4000040;

            memory.FastMemoryCopy(0xA4000000, 0xB0000000, 0x1000); // Load the 4 KiB IPL3 boot code into SP memory.
            memory.WriteUInt8(0xBFC007FF, GetInitialPifControlByte(cicSeed)); // kseg1 alias of PIF RAM control byte.

            TLB.Reset();
            COP0.PowerOnCOP0();
            COP1.PowerOnCOP1();

            R4300_ON = true;
            UnknownOpcodeCount = 0;
            lock (UnknownOpcodeLock)
            {
                UnknownOpcodeByPc.Clear();
                UnknownOpcodeByValue.Clear();
            }
            _stuckPcLogCount = 0;
            _loggedPifTailEntry = false;
            _loggedFirstBfcEntry = false;
            _tlbRefillLogCount = 0;
            _traceEretWindowCount = 0;
            _traceRefillWindowCount = 0;
            _traceEarlyLoopWindowCount = 0;
            _traceSm64WalkWindowCount = 0;

            OpcodeTable.Init();

            CpuThread =
            new Thread(() =>
            {
                Common.Measure.MeasureTime.Start();
                try
                {
                    uint lastPc = Registers.R4300.PC;
                    ulong samePcIterations = 0;
                    while (R4300_ON)
                    {
                        uint pc = Registers.R4300.PC;
                        if (ServiceInterrupts(pc))
                            continue;

                        if ((pc & 0x3) != 0)
                        {
                            if (AlignMisalignedPcDuringBringup)
                            {
                                uint alignedPc = pc & 0xFFFFFFFCu;
                                if (_stuckPcLogCount < 64)
                                {
                                    _stuckPcLogCount++;
                                    Common.Logger.PrintWarningLine(
                                        $"R4300 bring-up: aligning misaligned PC from 0x{pc:x8} to 0x{alignedPc:x8} " +
                                        $"(count={_stuckPcLogCount}).");
                                }
                                Registers.R4300.PC = alignedPc;
                                continue;
                            }

                            RaiseAddressErrorException(pc, isStore: false, pc);
                            continue;
                        }
                        if (pc == lastPc)
                        {
                            samePcIterations++;
                            if (samePcIterations == 5_000_000 || (samePcIterations % 20_000_000) == 0)
                            {
                                _stuckPcLogCount++;
                                Common.Logger.PrintWarningLine(
                                    $"R4300 watchdog: PC appears stuck at 0x{pc:x8} for {samePcIterations} iterations " +
                                    $"(report #{_stuckPcLogCount}).");
                                if (TraceStuckPcDetails)
                                {
                                    uint op = 0;
                                    uint miIntr = 0, miMask = 0, viStatus = 0, viCurrent = 0;
                                    ulong cop0Status = 0, cop0Cause = 0, cop0Epc = 0;
                                    try { op = memory.ReadUInt32(pc); } catch { }
                                    try { miIntr = memory.ReadUInt32(0x04300008u); } catch { }
                                    try { miMask = memory.ReadUInt32(0x0430000Cu); } catch { }
                                    try { viStatus = memory.ReadUInt32(0x04400000u); } catch { }
                                    try { viCurrent = memory.ReadUInt32(0x04400010u); } catch { }
                                    cop0Status = Registers.COP0.Reg[Registers.COP0.STATUS_REG];
                                    cop0Cause = Registers.COP0.Reg[Registers.COP0.CAUSE_REG];
                                    cop0Epc = Registers.COP0.Reg[Registers.COP0.EPC_REG];
                                    Common.Logger.PrintWarningLine(
                                        $"R4300 stuck details: pc=0x{pc:x8} op=0x{op:x8} " +
                                        $"cop0Status=0x{cop0Status:x8} cop0Cause=0x{cop0Cause:x8} cop0Epc=0x{cop0Epc:x8} " +
                                        $"miIntr=0x{miIntr:x8} miMask=0x{miMask:x8} viStatus=0x{viStatus:x8} viCurrent=0x{viCurrent:x8} " +
                                        $"t0=0x{Registers.R4300.Reg[8]:x16} t1=0x{Registers.R4300.Reg[9]:x16} " +
                                        $"a0=0x{Registers.R4300.Reg[4]:x16} v0=0x{Registers.R4300.Reg[2]:x16}");
                                }
                            }
                        }
                        else
                        {
                            samePcIterations = 0;
                            lastPc = pc;
                        }

                        try
                        {
                            if (!_loggedFirstBfcEntry && pc >= 0xBFC00000u && pc <= 0xBFC00810u)
                            {
                                _loggedFirstBfcEntry = true;
                                StringBuilder firstBfc = new StringBuilder();
                                firstBfc.Append($"First entry into BFC region at pc=0x{pc:x8}. Recent PCs:");
                                for (int i = 0; i < 48; i++)
                                {
                                    int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                                    RecentInst rec = _recentInst[idx];
                                    firstBfc.Append($" [{i}]pc=0x{rec.Pc:x8}/op=0x{rec.Op:x8}");
                                }
                                Common.Logger.PrintWarningLine(firstBfc.ToString());
                            }

                            if (!_loggedPifTailEntry && pc >= 0xBFC007B0u && pc <= 0xBFC00810u)
                            {
                                _loggedPifTailEntry = true;
                                const int pifEntryHistoryCount = 24;
                                StringBuilder pifEntry = new StringBuilder();
                                pifEntry.Append($"Entered PIF tail execution window at pc=0x{pc:x8}. Recent PCs:");
                                for (int i = 0; i < pifEntryHistoryCount; i++)
                                {
                                    int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                                    RecentInst rec = _recentInst[idx];
                                    pifEntry.Append($" [{i}]pc=0x{rec.Pc:x8}/op=0x{rec.Op:x8}");
                                }

                                int discontinuities = 0;
                                uint prevPcInHistory = 0;
                                bool havePrevPc = false;
                                for (int i = 0; i < RecentInstHistorySize - 1; i++)
                                {
                                    int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                                    RecentInst rec = _recentInst[idx];
                                    if (havePrevPc && rec.Pc != (prevPcInHistory - 4u))
                                    {
                                        discontinuities++;
                                        pifEntry.Append(
                                            $" | jump#{discontinuities}: newer=0x{prevPcInHistory:x8} older=0x{rec.Pc:x8}");
                                        if (discontinuities >= 8)
                                            break;
                                    }

                                    prevPcInHistory = rec.Pc;
                                    havePrevPc = true;
                                }
                                Common.Logger.PrintWarningLine(pifEntry.ToString());
                            }

                            uint fetchAddress = pc;
                            uint segment = pc & 0xE0000000u;
                            // TLB-translate all virtual segments except direct-mapped kseg0/kseg1.
                            if (segment != 0x80000000u && segment != 0xA0000000u)
                            {
                                uint translated;
                                try
                                {
                                    translated = TLB.TranslateAddress(pc, throwOnMiss: true) & 0x1FFFFFFFu;
                                }
                                catch (Common.Exceptions.TLBMissException)
                                {
                                    // Bring-up fallback: allow low virtual instruction fetches to
                                    // behave as direct physical when ITLB state is incomplete.
                                    // Set EUTHERDRIVE_N64_STRICT_ITLB=1 to enforce strict behavior.
                                    if (AllowInstructionLowPhysicalFallbackOnTlbMiss && pc < 0x05000000u)
                                    {
                                        translated = pc & 0x1FFFFFFFu;
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                                fetchAddress = 0xA0000000u | translated;
                            }

                            uint Opcode = memory.ReadUInt32(fetchAddress);
                            _recentInst[_recentInstPos] = new RecentInst { Pc = pc, Op = Opcode };
                            _recentInstPos = (_recentInstPos + 1) & RecentInstHistoryMask;
                            if (TraceBootWindow
                                && _traceBootWindowCount < TraceBootWindowLimit
                                && pc >= 0x80000000
                                && pc <= 0x80000200)
                            {
                                _traceBootWindowCount++;
                                Console.WriteLine(
                                    $"[N64BOOT] #{_traceBootWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8} piWrLen=0x{memory.ReadUInt32(0x0460000C):x8} " +
                                    $"t0=0x{Registers.R4300.Reg[8]:x16} t1=0x{Registers.R4300.Reg[9]:x16} " +
                                    $"t8=0x{Registers.R4300.Reg[24]:x16} t9=0x{Registers.R4300.Reg[25]:x16} " +
                                    $"ra=0x{Registers.R4300.Reg[31]:x16}");
                            }

                            if (TraceEarlyLoopWindow
                                && _traceEarlyLoopWindowCount < TraceEarlyLoopWindowLimit
                                && pc >= 0x80000120
                                && pc <= 0x800001A0)
                            {
                                _traceEarlyLoopWindowCount++;
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong t6 = Registers.R4300.Reg[14];
                                ulong t7 = Registers.R4300.Reg[15];
                                ulong t8 = Registers.R4300.Reg[24];
                                ulong t9 = Registers.R4300.Reg[25];
                                uint t0w = 0;
                                uint t1w = 0;
                                uint t1w4 = 0;
                                try { t0w = memory.ReadUInt32((uint)t0); } catch { }
                                try { t1w = memory.ReadUInt32((uint)t1); } catch { }
                                try { t1w4 = memory.ReadUInt32((uint)t1 + 4u); } catch { }

                                Console.WriteLine(
                                    $"[N64EARLY] #{_traceEarlyLoopWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} " +
                                    $"t6=0x{t6:x16} t7=0x{t7:x16} t8=0x{t8:x16} t9=0x{t9:x16} " +
                                    $"[t0]=0x{t0w:x8} [t1]=0x{t1w:x8} [t1+4]=0x{t1w4:x8} " +
                                    $"miIntr=0x{memory.ReadUInt32(0x04300008):x8} miMask=0x{memory.ReadUInt32(0x0430000C):x8} " +
                                    $"piStatus=0x{memory.ReadUInt32(0x04600010):x8}");
                            }

                            if (TraceEretWindow
                                && _traceEretWindowCount < TraceEretWindowLimit
                                && pc >= 0x80327de0
                                && pc <= 0x80327ec0)
                            {
                                _traceEretWindowCount++;
                                ulong k0 = Registers.R4300.Reg[26];
                                ulong k1 = Registers.R4300.Reg[27];
                                uint k0a = (uint)k0;
                                uint m118 = 0;
                                uint m11c = 0;
                                try
                                {
                                    m118 = memory.ReadUInt32(k0a + 0x118u);
                                    m11c = memory.ReadUInt32(k0a + 0x11Cu);
                                }
                                catch
                                {
                                    // Best-effort trace; ignore side read failures.
                                }

                                Console.WriteLine(
                                    $"[N64ERET] #{_traceEretWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"k0=0x{k0:x16} k1=0x{k1:x16} m118=0x{m118:x8} m11c=0x{m11c:x8} " +
                                    $"cop0Status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} " +
                                    $"cop0Epc=0x{Registers.COP0.Reg[Registers.COP0.EPC_REG]:x8}");
                            }

                            if (TraceRefillWindow
                                && _traceRefillWindowCount < TraceRefillWindowLimit
                                && pc >= 0x80327660
                                && pc <= 0x80327720)
                            {
                                _traceRefillWindowCount++;
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong t4 = Registers.R4300.Reg[12];
                                ulong k0 = Registers.R4300.Reg[26];
                                ulong k1 = Registers.R4300.Reg[27];
                                Console.WriteLine(
                                    $"[N64REFILL] #{_traceRefillWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} t4=0x{t4:x16} " +
                                    $"k0=0x{k0:x16} k1=0x{k1:x16} " +
                                    $"status=0x{Registers.COP0.Reg[Registers.COP0.STATUS_REG]:x8} " +
                                    $"cause=0x{Registers.COP0.Reg[Registers.COP0.CAUSE_REG]:x8} " +
                                    $"epc=0x{Registers.COP0.Reg[Registers.COP0.EPC_REG]:x8} " +
                                    $"badv=0x{Registers.COP0.Reg[Registers.COP0.BADVADDR_REG]:x8} " +
                                    $"entryHi=0x{Registers.COP0.Reg[Registers.COP0.ENTRYHI_REG]:x8} " +
                                    $"context=0x{Registers.COP0.Reg[Registers.COP0.CONTEXT_REG]:x8}");
                            }

                            if (TraceSm64WalkWindow
                                && _traceSm64WalkWindowCount < TraceSm64WalkWindowLimit
                                && pc >= 0x80327D10
                                && pc <= 0x80327D70)
                            {
                                _traceSm64WalkWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong t8 = Registers.R4300.Reg[24];
                                ulong t6 = Registers.R4300.Reg[14];
                                uint a0w = 0, a0w4 = 0, a0w8 = 0, a0wc = 0;
                                uint t8w = 0, t8w4 = 0, t8w8 = 0, t8wc = 0;
                                uint v0w = 0, v0w4 = 0;
                                try { a0w = memory.ReadUInt32((uint)a0); } catch { }
                                try { a0w4 = memory.ReadUInt32((uint)a0 + 4u); } catch { }
                                try { a0w8 = memory.ReadUInt32((uint)a0 + 8u); } catch { }
                                try { a0wc = memory.ReadUInt32((uint)a0 + 12u); } catch { }
                                try { t8w = memory.ReadUInt32((uint)t8); } catch { }
                                try { t8w4 = memory.ReadUInt32((uint)t8 + 4u); } catch { }
                                try { t8w8 = memory.ReadUInt32((uint)t8 + 8u); } catch { }
                                try { t8wc = memory.ReadUInt32((uint)t8 + 12u); } catch { }
                                try { v0w = memory.ReadUInt32((uint)v0); } catch { }
                                try { v0w4 = memory.ReadUInt32((uint)v0 + 4u); } catch { }
                                uint opD64 = 0;
                                try { opD64 = memory.ReadUInt32(0x80327D64u); } catch { }

                                Console.WriteLine(
                                    $"[N64SM64WALK] #{_traceSm64WalkWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} t8=0x{t8:x16} t6=0x{t6:x16} v0=0x{v0:x16} " +
                                    $"[a0]=0x{a0w:x8} [a0+4]=0x{a0w4:x8} [a0+8]=0x{a0w8:x8} [a0+c]=0x{a0wc:x8} " +
                                    $"[t8]=0x{t8w:x8} [t8+4]=0x{t8w4:x8} [t8+8]=0x{t8w8:x8} [t8+c]=0x{t8wc:x8} " +
                                    $"[v0]=0x{v0w:x8} [v0+4]=0x{v0w4:x8} op@80327d64=0x{opD64:x8}");
                            }

                            if (TraceViInitWindow
                                && _traceViInitWindowCount < TraceViInitWindowLimit
                                && pc >= 0x80328290
                                && pc <= 0x803283A0)
                            {
                                _traceViInitWindowCount++;
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong t4 = Registers.R4300.Reg[12];
                                ulong t5 = Registers.R4300.Reg[13];
                                ulong t6 = Registers.R4300.Reg[14];
                                ulong t7 = Registers.R4300.Reg[15];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong v1 = Registers.R4300.Reg[3];
                                ulong sp = Registers.R4300.Reg[29];
                                uint sp3c = 0;
                                uint sp38 = 0;
                                uint sp40 = 0;
                                try { sp38 = memory.ReadUInt32((uint)sp + 0x38u); } catch { }
                                try { sp3c = memory.ReadUInt32((uint)sp + 0x3Cu); } catch { }
                                try { sp40 = memory.ReadUInt32((uint)sp + 0x40u); } catch { }
                                Console.WriteLine(
                                    $"[N64VIINIT] #{_traceViInitWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} v0=0x{v0:x16} v1=0x{v1:x16} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} " +
                                    $"t4=0x{t4:x16} t5=0x{t5:x16} t6=0x{t6:x16} t7=0x{t7:x16} " +
                                    $"sp=0x{sp:x16} [sp+38]=0x{sp38:x8} [sp+3c]=0x{sp3c:x8} [sp+40]=0x{sp40:x8}");
                            }

                            if (TraceViPrepWindow
                                && _traceViPrepWindowCount < TraceViPrepWindowLimit
                                && pc >= 0x803280A0
                                && pc <= 0x80328120)
                            {
                                _traceViPrepWindowCount++;
                                ulong sp = Registers.R4300.Reg[29];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                ulong s2 = Registers.R4300.Reg[18];
                                ulong s3 = Registers.R4300.Reg[19];
                                ulong s4 = Registers.R4300.Reg[20];
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong v0 = Registers.R4300.Reg[2];
                                uint sp3c = 0;
                                try { sp3c = memory.ReadUInt32((uint)sp + 0x3Cu); } catch { }
                                Console.WriteLine(
                                    $"[N64VIPREP] #{_traceViPrepWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} v0=0x{v0:x16} " +
                                    $"s0=0x{s0:x16} s1=0x{s1:x16} s2=0x{s2:x16} s3=0x{s3:x16} s4=0x{s4:x16} " +
                                    $"sp=0x{sp:x16} [sp+3c]=0x{sp3c:x8}");
                            }

                            if (TraceViCalcWindow
                                && _traceViCalcWindowCount < TraceViCalcWindowLimit
                                && pc >= 0x80327E80
                                && pc <= 0x80327F20)
                            {
                                _traceViCalcWindowCount++;
                                ulong a0 = Registers.R4300.Reg[4];
                                ulong a1 = Registers.R4300.Reg[5];
                                ulong a2 = Registers.R4300.Reg[6];
                                ulong v0 = Registers.R4300.Reg[2];
                                ulong t0 = Registers.R4300.Reg[8];
                                ulong t1 = Registers.R4300.Reg[9];
                                ulong t2 = Registers.R4300.Reg[10];
                                ulong t3 = Registers.R4300.Reg[11];
                                ulong s0 = Registers.R4300.Reg[16];
                                ulong s1 = Registers.R4300.Reg[17];
                                Console.WriteLine(
                                    $"[N64VICALC] #{_traceViCalcWindowCount} pc=0x{pc:x8} op=0x{Opcode:x8} " +
                                    $"a0=0x{a0:x16} a1=0x{a1:x16} a2=0x{a2:x16} v0=0x{v0:x16} " +
                                    $"t0=0x{t0:x16} t1=0x{t1:x16} t2=0x{t2:x16} t3=0x{t3:x16} " +
                                    $"s0=0x{s0:x16} s1=0x{s1:x16}");
                            }

                            InterpretOpcode(Opcode);
                        }
                        catch (NotImplementedException ex)
                        {
                            uint opcode = _recentInst[(_recentInstPos - 1) & RecentInstHistoryMask].Op;
                            UnknownOpcodeCount++;
                            TrackUnknownOpcode(pc, opcode);
                            if (UnknownOpcodeCount <= 32 || (UnknownOpcodeCount % 256) == 0)
                            {
                                Common.Logger.PrintWarningLine(
                                    $"Unknown opcode encountered (count={UnknownOpcodeCount}) " +
                                    $"pc=0x{pc:x8} op=0x{opcode:x8}: {ex.Message}");
                                if (UnknownOpcodeCount <= 16)
                                {
                                    StringBuilder sb = new StringBuilder();
                                    sb.Append("Recent PCs before unknown:");
                                    for (int i = 0; i < 20; i++)
                                    {
                                        int idx = (_recentInstPos - 1 - i) & RecentInstHistoryMask;
                                        RecentInst rec = _recentInst[idx];
                                        sb.Append($" [{i}]pc=0x{rec.Pc:x8}/op=0x{rec.Op:x8}");
                                    }
                                    Common.Logger.PrintWarningLine(sb.ToString());
                                }
                            }
                            if ((UnknownOpcodeCount % 1024) == 0)
                            {
                                string topPc;
                                string topOp;
                                lock (UnknownOpcodeLock)
                                {
                                    topPc = FormatTopUnknownSummary(UnknownOpcodeByPc, "pc", 5);
                                    topOp = FormatTopUnknownSummary(UnknownOpcodeByValue, "op", 5);
                                }

                                Common.Logger.PrintWarningLine($"Unknown opcode hot PCs: {topPc}");
                                Common.Logger.PrintWarningLine($"Unknown opcode hot ops: {topOp}");
                            }

                            if (UnknownOpcodeAsNop)
                            {
                                Registers.R4300.PC = pc + 4;
                                CycleCounter += 1;
                                Count += 1;
                                Registers.COP0.Reg[Registers.COP0.COUNT_REG] = Count >> 1;
                            }
                            else
                            {
                                RaiseCpuException(CauseExcCodeRi, pc);
                            }
                            continue;
                        }
                        catch (Common.Exceptions.TLBMissException tlbMiss)
                        {
                            RaiseTlbRefillException(tlbMiss.Address, pc, tlbMiss.IsStore);
                            continue;
                        }
                        catch (Common.Exceptions.AddressErrorException addrErr)
                        {
                            RaiseAddressErrorException(addrErr.Address, addrErr.IsStore, pc);
                            continue;
                        }

                        while (Common.Settings.STEP_MODE && !Common.Variables.Step);
                        if (Common.Settings.STEP_MODE)
                        {
                            Registers.R4300.PrintRegisterInfo();
                            Registers.COP0.PrintRegisterInfo();
                            Registers.COP1.PrintRegisterInfo();
                            Thread.Sleep(250);
                            Common.Variables.Step = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Common.Logger.PrintErrorLine($"R4300 halted due to exception at PC=0x{Registers.R4300.PC:x8}: {ex.Message}");
                    Common.Logger.PrintErrorLine(ex.ToString());
                    R4300_ON = false;
                }
                Common.Measure.MeasureTime.Stop();
            });
            CpuThread.Name = "R4300";
            CpuThread.Start();
        }

        public static void StopR4300()
        {
            R4300_ON = false;
            Thread thread = CpuThread;
            if (thread != null && thread.IsAlive)
            {
                if (!thread.Join(200))
                    thread.Interrupt();
            }
            CpuThread = null;
        }

        public static uint GetCurrentPc()
        {
            return Registers.R4300.PC;
        }

        public static ulong GetCycleCounter()
        {
            return CycleCounter;
        }

        public static long GetUnknownOpcodeCount()
        {
            return UnknownOpcodeCount;
        }
    }
}
